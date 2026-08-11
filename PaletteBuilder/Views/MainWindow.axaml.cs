// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/MainWindow.axaml.cs
//
// Standalone Palette Builder main window. Hosts a PaletteBuilderViewModel
// (extraction VM + Export + presets + recent + auto-extract), wires:
//   • File > Open / Recent → vm.SetImage
//   • Presets > Save / Load / Delete → preset store
//   • Tools > Auto-extract → vm.AutoExtract
//   • Drag-drop image files onto the window → vm.SetImage
//   • Cancel ("Close") → window.Close()
//   • Export (any registered IPaletteExporter) → save dialog → exporter.Export

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PaletteBuilder.Services;
using PaletteBuilder.ViewModels;

namespace PaletteBuilder.Views;

public sealed partial class MainWindow : Window
{
    // _service is only allocated when the standalone PaletteBuilder.exe
    // wrapper opens the window. When FracturingFog's host opens the
    // window as a picker, the host injects its own IPaletteExtractionService
    // (HostPaletteExtractionService) and this field stays null.
    private readonly PaletteExtractionService? _service;
    private readonly PaletteBuilderViewModel _vm;

    private MenuItem? _recentMenu;
    private MenuItem? _loadPresetMenu;
    private MenuItem? _deletePresetMenu;

    /// <summary>
    /// Standalone ctor — owns a freshly constructed
    /// <see cref="PaletteExtractionService"/>. Used by Program.cs and the
    /// PaletteBuilder.exe wrapper.
    /// </summary>
    public MainWindow()
        : this(new PaletteExtractionService(), pickerMode: false, ownsService: true)
    {
    }

    /// <summary>
    /// Picker-mode ctor — host supplies its own extraction service so the
    /// dialog returns palettes that match the host's pixel cache and
    /// extractor instances. Set <paramref name="pickerMode"/> true to hide
    /// the standalone app-shell (File/Presets/Tools menus + Export +
    /// status bar) and surface an Apply/Cancel bottom row instead.
    /// </summary>
    public MainWindow(global::FracturingFog.Imaging.IPaletteExtractionService service, bool pickerMode = false)
        : this(service, pickerMode, ownsService: false)
    {
    }

    private MainWindow(global::FracturingFog.Imaging.IPaletteExtractionService service, bool pickerMode, bool ownsService)
    {
        AvaloniaXamlLoader.Load(this);

        if (ownsService) _service = (PaletteExtractionService)service;

        _vm = new PaletteBuilderViewModel(service) { PickerMode = pickerMode };
        DataContext = _vm;

        // Host owns the dialog lifetime in picker mode — Apply closes the
        // window via the same path Cancel does. ResultAccepted carries the
        // chosen stops; the host's AvaloniaDialogs subscribes and resolves
        // its TaskCompletionSource.
        if (pickerMode)
        {
            _vm.ResultAccepted += (_, _) => Dispatcher.UIThread.Post(Close);
        }

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.F1) ShowHelp(); };

        _recentMenu = this.FindControl<MenuItem>("RecentMenu");
        _loadPresetMenu = this.FindControl<MenuItem>("LoadPresetMenu");
        _deletePresetMenu = this.FindControl<MenuItem>("DeletePresetMenu");

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        _vm.BrowseRequested += async (_, _) =>
        {
            try
            {
                var picked = await PickImageFileAsync();
                if (!string.IsNullOrEmpty(picked)) TryLoadIntoVm(picked);
            }
            catch (Exception ex)
            {
                await ShowInfoAsync("Browse failed: " + ex.Message);
            }
        };

        _vm.MessageRequested += async (_, msg) =>
        {
            try { await ShowInfoAsync(msg); } catch { /* best-effort */ }
        };

        _vm.Cancelled += (_, _) => Dispatcher.UIThread.Post(Close);

        _vm.ExportRequested += async (_, args) =>
        {
            try { await HandleExportAsync(args); }
            catch (Exception ex) { await ShowInfoAsync("Export failed: " + ex.Message); }
        };

        // Time extracts so the status bar can show duration + swatch count.
        var sw = new System.Diagnostics.Stopwatch();
        _vm.ExtractCommand.Subscribe(_ => { });
        _vm.ExtractCommand.IsExecuting.Subscribe(running =>
        {
            if (running) { sw.Restart(); }
            else if (sw.IsRunning)
            {
                sw.Stop();
                int n = _vm.SelectedResult?.Palette.Count ?? 0;
                _vm.NotifyExtractCompleted(sw.ElapsedMilliseconds, n, _vm.SelectedResult?.Name);
            }
        });
        _vm.CompareAllCommand.IsExecuting.Subscribe(running =>
        {
            if (running) { sw.Restart(); }
            else if (sw.IsRunning)
            {
                sw.Stop();
                _vm.NotifyExtractCompleted(sw.ElapsedMilliseconds, _vm.Results.Count, "Compare-All");
            }
        });

        _vm.RecentFilePaths.CollectionChanged += (_, _) => RebuildRecentMenu();
        _vm.PresetNames.CollectionChanged += (_, _) => RebuildPresetMenus();
        RebuildRecentMenu();
        RebuildPresetMenus();
    }

    // ── Menu handlers ──────────────────────────────────────────────────

    private async void OnMenuOpen(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picks = await PickImageFilesAsync(allowMultiple: true);
            if (picks.Count > 0) TryLoadIntoVm(picks);
        }
        catch (Exception ex) { await ShowInfoAsync("Open failed: " + ex.Message); }
    }

    private async void OnMenuOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickFolderAsync();
            if (string.IsNullOrEmpty(folder)) return;
            var paths = EnumerateImagesInFolder(folder).ToList();
            if (paths.Count == 0) { await ShowInfoAsync("No images found in folder."); return; }
            TryLoadIntoVm(paths);
        }
        catch (Exception ex) { await ShowInfoAsync("Open folder failed: " + ex.Message); }
    }

    private async void OnMenuFromUrl(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new UrlFetchDialog();
            var ok = await dlg.ShowDialog<bool>(this);
            if (ok && !string.IsNullOrEmpty(dlg.ResolvedTempPath))
                TryLoadIntoVm(dlg.ResolvedTempPath);
        }
        catch (Exception ex) { await ShowInfoAsync("URL load failed: " + ex.Message); }
    }

    private void OnMenuClearRoi(object? sender, RoutedEventArgs e) => _vm.ClearRoi();

    private void OnMenuExit(object? sender, RoutedEventArgs e) => Close();

    private void OnMenuHelp(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        var dlg = new HelpDialog();
        _ = dlg.ShowDialog(this);
    }

    private void OnMenuRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string path && File.Exists(path))
            TryLoadIntoVm(path);
    }

    private async void OnMenuSavePreset(object? sender, RoutedEventArgs e)
    {
        var name = await PromptForNameAsync("Save Preset", "Preset name:", "Untitled");
        if (string.IsNullOrWhiteSpace(name)) return;
        _vm.SavePresetCommand.Execute(name).Subscribe(_ => { }, _ => { });
    }

    private void OnMenuLoadPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string name)
            _vm.LoadPresetCommand.Execute(name).Subscribe(_ => { }, _ => { });
    }

    private void OnMenuDeletePreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string name)
            _vm.DeletePresetCommand.Execute(name).Subscribe(_ => { }, _ => { });
    }

    // ── Dynamic menu rebuild ───────────────────────────────────────────

    private void RebuildRecentMenu()
    {
        if (_recentMenu is null) return;
        var items = new List<object>();
        if (_vm.RecentFilePaths.Count == 0)
        {
            items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        }
        else
        {
            foreach (var p in _vm.RecentFilePaths)
            {
                var mi = new MenuItem { Header = p, Tag = p };
                mi.Click += OnMenuRecentClick;
                items.Add(mi);
            }
        }
        _recentMenu.ItemsSource = items;
    }

    private void RebuildPresetMenus()
    {
        if (_loadPresetMenu is not null)
        {
            var loadItems = new List<object>();
            if (_vm.PresetNames.Count == 0)
                loadItems.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            else
                foreach (var n in _vm.PresetNames)
                {
                    var mi = new MenuItem { Header = n, Tag = n };
                    mi.Click += OnMenuLoadPreset;
                    loadItems.Add(mi);
                }
            _loadPresetMenu.ItemsSource = loadItems;
        }

        if (_deletePresetMenu is not null)
        {
            var delItems = new List<object>();
            if (_vm.PresetNames.Count == 0)
                delItems.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            else
                foreach (var n in _vm.PresetNames)
                {
                    var mi = new MenuItem { Header = n, Tag = n };
                    mi.Click += OnMenuDeletePreset;
                    delItems.Add(mi);
                }
            _deletePresetMenu.ItemsSource = delItems;
        }
    }

    // ── Drag-drop / browse ─────────────────────────────────────────────

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0) return;

        // Expand: a single dropped folder counts as a batch of every image
        // inside it. Multiple dropped files counts as a batch directly.
        var paths = new List<string>();
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            if (Directory.Exists(p)) paths.AddRange(EnumerateImagesInFolder(p));
            else paths.Add(p);
        }
        if (paths.Count == 0) return;
        TryLoadIntoVm(paths);
    }

    private static IEnumerable<string> EnumerateImagesInFolder(string folder)
    {
        string[] exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif" };
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            if (Array.IndexOf(exts, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                yield return f;
    }

    private void TryLoadIntoVm(string path) => TryLoadIntoVm(new[] { path });

    private void TryLoadIntoVm(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count > 1)
        {
            string? err = null;
            if (_service is null || !_service.TryLoadImages(paths, out err))
            {
                _ = ShowInfoAsync(err ?? "Failed to load images.");
                return;
            }
            // Preview shows first image; VM SetImage no-ops the service call
            // because the path is already loaded as part of the batch.
            Bitmap? preview = null;
            try { preview = new Bitmap(paths[0]); } catch { }
            _vm.SetImage(paths[0], preview);
            foreach (var p in paths) _vm.NotifyImageLoaded(p);
            return;
        }

        var single = paths[0];
        Bitmap? singlePreview = null;
        try { singlePreview = new Bitmap(single); } catch { /* service will surface */ }
        _vm.SetImage(single, singlePreview);
        _vm.NotifyImageLoaded(single);
    }

    private async System.Threading.Tasks.Task<string?> PickImageFileAsync()
    {
        var picks = await PickImageFilesAsync(allowMultiple: false);
        return picks.Count > 0 ? picks[0] : null;
    }

    private async System.Threading.Tasks.Task<IReadOnlyList<string>> PickImageFilesAsync(bool allowMultiple)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return Array.Empty<string>();
        var opts = new FilePickerOpenOptions
        {
            Title = allowMultiple ? "Choose Images" : "Choose Image",
            AllowMultiple = allowMultiple,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp", "*.heic", "*.heif" },
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var paths = new List<string>(files.Count);
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        return paths;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Folder of Images",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // ── Export ─────────────────────────────────────────────────────────

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var fmt = _vm.SelectedExportFormat ?? _vm.ExportFormats.FirstOrDefault();
        if (fmt is null) return;
        _vm.ExportCommand.Execute(fmt.Id).Subscribe(_ => { }, _ => { });
    }

    private void OnInspectClick(object? sender, RoutedEventArgs e)
    {
        var swatches = _vm.GetAdjustedSwatches();
        if (swatches.Count == 0) return;
        var inspector = new InspectorWindow();
        inspector.Populate(swatches);
        _ = inspector.ShowDialog(this);
    }

    private void OnResetTemperature(object? sender, RoutedEventArgs e) => _vm.Temperature = 0;
    private void OnResetTint(object? sender, RoutedEventArgs e) => _vm.Tint = 0;

    // ── Phase 7.3 hex paste ─────────────────────────────────────────────

    private async void OnPasteHexClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        string? clip = null;
        try { clip = top?.Clipboard is null ? null : await top.Clipboard.TryGetTextAsync(); }
        catch { }
        clip = await PromptForNameAsync("Paste Hex List", "Hex colors (#aabbcc, comma/space separated):", clip ?? "");
        if (string.IsNullOrWhiteSpace(clip)) return;
        _vm.SeedFromHexList(clip);
    }

    // ── Phase 7.5 theme toggle ──────────────────────────────────────────

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? Avalonia.Styling.ThemeVariant.Light
            : Avalonia.Styling.ThemeVariant.Dark;
    }

    // ── Phase 7.2 undo/redo button handlers ─────────────────────────────

    private void OnUndoClick(object? sender, RoutedEventArgs e)
        => _vm.UndoCommand?.Execute().Subscribe(_ => { }, _ => { });

    private void OnRedoClick(object? sender, RoutedEventArgs e)
        => _vm.RedoCommand?.Execute().Subscribe(_ => { }, _ => { });

    private async System.Threading.Tasks.Task HandleExportAsync(ExportRequestedEventArgs args)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        // PDF gets a settings dialog up-front so the user can pick page size,
        // columns, cover page, etc. Other formats jump straight to save.
        PdfExportOptions? pdfOpts = null;
        if (args.Exporter.Id == "pdf")
        {
            var dlg = new PdfSettingsDialog();
            pdfOpts = await dlg.ShowDialogAsync(this);
            if (pdfOpts is null) return;
            pdfOpts.SourceImagePath = args.SourceImagePath;
            pdfOpts.SettingsDump = BuildSettingsDump(args);

            // Comparison page needs every extractor's output on the same source.
            if (pdfOpts.IncludeComparisonPage)
            {
                _vm.StatusBarText = "Running all extractors for comparison page…";
                var rows = _vm.RunAllForExport();
                var pdfRows = new List<PdfComparisonRow>(rows.Count);
                foreach (var (method, sw, stops) in rows)
                    pdfRows.Add(new PdfComparisonRow { MethodName = method, Swatches = sw, Stops = stops });
                pdfOpts.ComparisonRows = pdfRows;
            }
        }

        string suggested = "palette." + args.Exporter.Extension;
        if (!string.IsNullOrEmpty(args.SourceImagePath))
            suggested = Path.GetFileNameWithoutExtension(args.SourceImagePath) + "-palette." + args.Exporter.Extension;

        var save = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save " + args.Exporter.DisplayName,
            SuggestedFileName = suggested,
            DefaultExtension = args.Exporter.Extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(args.Exporter.DisplayName)
                {
                    Patterns = new[] { "*." + args.Exporter.Extension },
                },
            },
        });
        if (save == null) return;
        var path = save.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var ctx = new PaletteExportContext
        {
            SourceImagePath = args.SourceImagePath,
            PaletteName = string.IsNullOrEmpty(args.SourceImagePath)
                ? "Palette"
                : Path.GetFileNameWithoutExtension(args.SourceImagePath),
            MethodName = args.MethodName,
            Extra = pdfOpts,
        };

        PaletteBuilderViewModel.PerformExport(args.Exporter, path, args.Swatches, args.Stops, ctx);
    }

    private string BuildSettingsDump(ExportRequestedEventArgs args)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Method: {args.MethodName}");
        sb.AppendLine($"Colors: {_vm.ColorCount}");
        string space = _vm.SpaceIndex switch { 0 => "RGB", 1 => "Lab", 2 => "HSL", 3 => "OkLab", _ => "Lab" };
        sb.AppendLine($"Space: {space}");
        string sort = _vm.SortIndex switch { 1 => "Hue", 2 => "Luminance", 3 => "ClusterSize", _ => "NN-Chain" };
        sb.AppendLine($"Sort: {sort}");
        sb.AppendLine($"Dedup ΔE: {_vm.DedupDeltaE:0.0} ({(_vm.DedupMetricIndex == 1 ? "ΔE2000" : "ΔE76")})");
        sb.AppendLine($"Downsample max: {_vm.DownsampleMax}");
        if (_vm.GammaCorrect) sb.AppendLine("Gamma-correct: on");
        if (_vm.WeightedPositions) sb.AppendLine("Weighted positions: on");
        if (_vm.ExcludeNearBlack) sb.AppendLine("Exclude near-black: on");
        if (_vm.ExcludeNearWhite) sb.AppendLine("Exclude near-white: on");
        if (_vm.ExcludeTransparent) sb.AppendLine("Exclude transparent: on");
        if (_vm.MinSaturation > 0 || _vm.MaxSaturation < 1)
            sb.AppendLine($"Saturation band: {_vm.MinSaturation:0.00}–{_vm.MaxSaturation:0.00}");
        if (_vm.MinLightness > 0 || _vm.MaxLightness < 1)
            sb.AppendLine($"Lightness band: {_vm.MinLightness:0.00}–{_vm.MaxLightness:0.00}");
        if (_vm.RoiWidth > 0 && _vm.RoiHeight > 0)
            sb.AppendLine($"ROI: x={_vm.RoiX:0.00} y={_vm.RoiY:0.00} w={_vm.RoiWidth:0.00} h={_vm.RoiHeight:0.00}");
        if (_vm.Temperature != 0 || _vm.Tint != 0)
            sb.AppendLine($"Adjust: temp={_vm.Temperature:0.00} tint={_vm.Tint:0.00}");
        return sb.ToString();
    }

    // ── Tiny dialogs ───────────────────────────────────────────────────

    private async System.Threading.Tasks.Task ShowInfoAsync(string message)
    {
        var dlg = new Window
        {
            Title = "Palette Builder",
            Width = 380,
            Height = 160,
            Background = Avalonia.Media.Brush.Parse("#1E1E1E"),
            Foreground = Avalonia.Media.Brush.Parse("#DCDCDC"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Padding = new Thickness(16, 4) };
        ok.Click += (_, _) => dlg.Close();
        panel.Children.Add(ok);
        dlg.Content = panel;
        await dlg.ShowDialog(this);
    }

    private async System.Threading.Tasks.Task<string?> PromptForNameAsync(string title, string prompt, string initial)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
        var dlg = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            Background = Avalonia.Media.Brush.Parse("#1E1E1E"),
            Foreground = Avalonia.Media.Brush.Parse("#DCDCDC"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = prompt });
        var tb = new TextBox { Text = initial };
        panel.Children.Add(tb);
        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        var ok = new Button { Content = "OK", Padding = new Thickness(16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4), IsCancel = true };
        ok.Click += (_, _) => { tcs.TrySetResult(tb.Text); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        panel.Children.Add(btnRow);
        dlg.Content = panel;
        dlg.Closed += (_, _) => tcs.TrySetResult(null);
        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Only dispose the extraction service the window allocated itself.
        // In picker mode the host owns its IPaletteExtractionService and
        // disposes it on its own schedule.
        _service?.Dispose();
        base.OnClosed(e);
    }
}
