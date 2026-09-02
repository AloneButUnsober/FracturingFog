// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Standalone Lighting & FX panel. Bound to the same
/// <see cref="FractalParamsViewModel"/> as <see cref="FractalParamsView"/> so
/// every knob still routes through the existing LightingFxData partial — this
/// view only hosts the controls. Hybrid-shell: a UserControl opened modeless
/// from FractalParamsView (wrapped in a PanelHostWindow) so the Params panel
/// stays compact.
/// </summary>
public sealed partial class LightingFxDialog : UserControl
{
    // Yellow per user-colorblindness memory (#FFCC00). See FractalParamsView
    // code-behind for the same brushes — duplicated here so the dialog can
    // be opened independently of that view.
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
    private static readonly IBrush OkBrush    = Brushes.LightGreen;
    private static readonly IBrush MutedBrush = Brushes.Gray;

    public LightingFxDialog()
    {
        AvaloniaXamlLoader.Load(this);
        // #580 — populate the "My FX preset" recall list whenever this view binds
        // to a params VM (dialog open / region apply refresh).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FractalParamsViewModel vm) vm.RefreshUserFxPresets();
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    // ── #580 — user Lighting & FX preset lifecycle (recall / save / delete /
    // import / export). The VM owns the library work; this code-behind owns the
    // name prompt + file pickers, matching the HDRI browse handler above. ─────

    private void OnRecallFxPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FractalParamsViewModel vm) vm.RecallUserFxPreset();
    }

    private async void OnSaveFxPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        string suggested = vm.SelectedUserFxPreset ?? $"FX preset {vm.UserFxPresets.Count + 1}";
        string? name = await PromptTextAsync("Save Lighting & FX Preset", "Preset name:", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        vm.SaveUserFxPreset(name!);
    }

    private async void OnDeleteFxPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        string? name = vm.SelectedUserFxPreset;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!await ConfirmAsync("Delete Preset", $"Delete saved preset \"{name}\"?")) return;
        vm.DeleteUserFxPreset(name!);
    }

    private async void OnImportFxPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Lighting & FX preset",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("JSON") { Patterns = new[] { "*.json" } },
                new("All files") { Patterns = new[] { "*" } },
            },
        });
        if (picked is not { Count: > 0 }) return;
        string path = picked[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        vm.ImportUserFxPresets(path);
    }

    private async void OnExportFxPresetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        string? name = vm.SelectedUserFxPreset;
        if (string.IsNullOrWhiteSpace(name)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Lighting & FX preset",
            SuggestedFileName = name + ".json",
            DefaultExtension = "json",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("JSON") { Patterns = new[] { "*.json" } },
            },
        });
        if (file == null) return;
        string path = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        vm.ExportUserFxPreset(name!, path);
    }

    // Minimal modal text prompt (Avalonia has no built-in). Returns the entered
    // text, or null on cancel / empty.
    private async Task<string?> PromptTextAsync(string title, string label, string initial)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return null;

        var box = new TextBox { Text = initial, Watermark = label };
        string? result = null;

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };

        var dlg = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };
        box.AttachedToVisualTree += (_, _) => { box.SelectAll(); box.Focus(); };

        await dlg.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result!.Trim();
    }

    // Minimal modal yes/no confirm.
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return false;

        bool confirmed = false;
        var yes = new Button { Content = "Delete", MinWidth = 72 };
        var no = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 72 };

        var dlg = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes },
                    },
                },
            },
        };

        yes.Click += (_, _) => { confirmed = true; dlg.Close(); };
        no.Click += (_, _) => { confirmed = false; dlg.Close(); };

        await dlg.ShowDialog(owner);
        return confirmed;
    }

    /// <summary>Browse… handler for the HDRI preset / file row. Same flow as
    /// the original FractalParamsView handler — kept here so the dialog
    /// owns its file-picker UX end-to-end.</summary>
    private async void OnBrowseHdriClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FractalParamsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var status = this.FindControl<TextBlock>("HdriStatus");

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick HDRI environment map",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("HDRI files")
                {
                    Patterns = new[] { "*.hdr", "*.pic", "*.exr" },
                    AppleUniformTypeIdentifiers = new[] { "public.image" },
                    MimeTypes = new[] { "image/vnd.radiance", "image/x-exr" },
                },
                new("Radiance HDR") { Patterns = new[] { "*.hdr", "*.pic" } },
                new("OpenEXR")      { Patterns = new[] { "*.exr" } },
                new("All files")    { Patterns = new[] { "*" } },
            },
        });
        if (picked is not { Count: > 0 }) return;

        string path = picked[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus(status, "Picker returned no usable path.", ErrorBrush);
            return;
        }

        var probe = HdriProbe.TryLoad;
        bool? loaded = probe == null ? (bool?)null : await Task.Run(() => probe(path));

        if (loaded == false)
            vm.EnvironmentName = path;
        else
            vm.ApplyHdriPick(path);

        if (loaded == true)
        {
            string fileName = Path.GetFileName(path);
            SetStatus(status, $"Loaded {fileName} — SkyMode = HDRI, IBL armed.", OkBrush);
        }
        else if (loaded == false)
        {
            SetStatus(status,
                "Failed to load HDRI (unsupported format / compression?). Falling back to gradient sky.",
                ErrorBrush);
        }
        else
        {
            SetStatus(status, "Path set; engine will load on next render.", MutedBrush);
        }
    }

    private static void SetStatus(TextBlock? status, string text, IBrush brush)
    {
        if (status == null) return;
        status.Text = text;
        status.Foreground = brush;
    }
}
