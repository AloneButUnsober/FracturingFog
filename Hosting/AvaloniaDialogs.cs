// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/AvaloniaDialogs.cs
//
// Small helpers used by AvaloniaShellBootstrap to satisfy SaveFileRequested
// and MessageRequested without dragging Avalonia.StorageProvider /
// Avalonia.Controls.Window usage into the VM layer.
//
// SaveFileAsync   — runs an Avalonia SaveFilePicker against the active main
//                   window, then writes the supplied content.
// ShowMessageAsync — opens a small modal Window with Title/Body + OK (or
//                   Yes/No when ExpectsConfirmation is true) and returns
//                   the user's choice.
//
// Both helpers must run on the UI thread and always await their result so
// the calling event handler can fill its EventArgs.Result / Saved / etc.
// fields before the editor reads them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using FracturingFog.Audio;
using FracturingFog.Imaging;
using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views;

namespace FracturingFog.Hosting
{
    public static class AvaloniaDialogs
    {
        public static Window? ActiveMainWindow
        {
            get
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
                    return desk.MainWindow;
                return null;
            }
        }

        // ── Save File ────────────────────────────────────────────────────────

        /// <summary>
        /// Runs an Avalonia SaveFilePicker, writes <paramref name="content"/> to
        /// the chosen file. Returns the chosen path or null on cancel.
        /// </summary>
        public static async Task<string?> SaveFileAsync(
            string title,
            string suggestedName,
            string filter,
            string content)
        {
            var owner = ActiveMainWindow;
            var top = owner != null ? TopLevel.GetTopLevel(owner) : null;
            if (top == null) return null;

            var opts = new FilePickerSaveOptions
            {
                Title = string.IsNullOrEmpty(title) ? "Save" : title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = ParseFilter(filter),
            };

            var file = await top.StorageProvider.SaveFilePickerAsync(opts);
            if (file == null) return null;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content ?? "");

            return file.TryGetLocalPath();
        }

        /// <summary>
        /// Parses a WinForms-style filter string ("JSON (*.json)|*.json|All
        /// files (*.*)|*.*") into Avalonia FilePickerFileType entries. If the
        /// string is empty or malformed, returns a single "All files" entry.
        /// </summary>
        private static IReadOnlyList<FilePickerFileType> ParseFilter(string filter)
        {
            var fallback = new[] { new FilePickerFileType("All files") { Patterns = new[] { "*" } } };
            if (string.IsNullOrWhiteSpace(filter)) return fallback;

            var parts = filter.Split('|');
            if (parts.Length < 2) return fallback;

            var list = new List<FilePickerFileType>();
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string name = parts[i];
                var patterns = parts[i + 1]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .ToArray();
                if (patterns.Length == 0) continue;
                list.Add(new FilePickerFileType(name) { Patterns = patterns });
            }
            return list.Count == 0 ? fallback : list;
        }

        /// <summary>
        /// Runs an Avalonia SaveFilePicker and returns the chosen path (or null
        /// on cancel). Unlike <see cref="SaveFileAsync"/> this does NOT write
        /// any content — the caller writes through the returned path itself.
        /// Used by binary-output flows (e.g. PNG screenshots) where the
        /// content isn't a plain string.
        /// </summary>
        public static async Task<string?> PickSaveFileAsync(
            string title,
            string suggestedName,
            string filter)
        {
            var owner = ActiveMainWindow;
            var top = owner != null ? TopLevel.GetTopLevel(owner) : null;
            if (top == null) return null;

            var opts = new FilePickerSaveOptions
            {
                Title = string.IsNullOrEmpty(title) ? "Save" : title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = ParseFilter(filter),
            };
            var file = await top.StorageProvider.SaveFilePickerAsync(opts);
            return file?.TryGetLocalPath();
        }

        /// <summary>
        /// Runs an Avalonia OpenFilePicker (single-select) and returns the
        /// chosen local path, or null on cancel. Filter follows the same
        /// WinForms-style "Name (*.ext)|*.ext|..." grammar as
        /// <see cref="SaveFileAsync"/>.
        /// </summary>
        public static async Task<string?> PickOpenFileAsync(
            string title,
            string filter)
        {
            var owner = ActiveMainWindow;
            var top = owner != null ? TopLevel.GetTopLevel(owner) : null;
            if (top == null) return null;

            var opts = new FilePickerOpenOptions
            {
                Title = string.IsNullOrEmpty(title) ? "Open" : title,
                AllowMultiple = false,
                FileTypeFilter = ParseFilter(filter),
            };
            var files = await top.StorageProvider.OpenFilePickerAsync(opts);
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        /// <summary>
        /// Opens a folder picker and returns the chosen directory's local path,
        /// or null on cancel. Used by the video lossless-save flow to pick a
        /// destination for the PNG sequence.
        /// </summary>
        public static async Task<string?> PickFolderAsync(string title)
        {
            var owner = ActiveMainWindow;
            var top = owner != null ? TopLevel.GetTopLevel(owner) : null;
            if (top == null) return null;

            var opts = new FolderPickerOpenOptions
            {
                Title = string.IsNullOrEmpty(title) ? "Choose Folder" : title,
                AllowMultiple = false,
            };
            var folders = await top.StorageProvider.OpenFolderPickerAsync(opts);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        // ── Slideshow settings ───────────────────────────────────────────────

        /// <summary>Window chrome for the Slideshow Settings panel host — the
        /// title/size/background formerly on the view's <c>&lt;Window&gt;</c> root.
        /// Shared by both the legacy and library-mode launchers.</summary>
        private static PanelHostOptions SlideshowHostOptions() =>
            new PanelHostOptions(
                "Slideshow Settings",
                Width: 520, MinWidth: 460,
                Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));

        /// <summary>
        /// Legacy overload — opens the dialog bound to a single
        /// <see cref="global::FracturingFog.Models.SlideshowSettings"/>.
        /// Returns the chosen settings + audio flag on OK, null on Cancel.
        /// </summary>
        public static Task<(global::FracturingFog.Models.SlideshowSettings Settings, bool AudioReactive)?>
            ShowSlideshowSettingsAsync(
                global::FracturingFog.Models.SlideshowSettings current,
                bool audioReactive)
        {
            var tcs = new TaskCompletionSource<(global::FracturingFog.Models.SlideshowSettings, bool)?>();

            void Run()
            {
                var vm = new SlideshowSettingsViewModel(current, audioReactive);
                var panel = new SlideshowSettingsView { DataContext = vm };
                var host = new PanelHostWindow(panel, SlideshowHostOptions());
                vm.ShowAudioDialogRequested += (_, _) => _ = ShowAudioSettingsAsync(host);
                host.Closed += (_, _) =>
                {
                    if (tcs.Task.IsCompleted) return;
                    if (vm.ResultSettings != null)
                        tcs.TrySetResult((vm.ResultSettings, vm.AudioReactiveResult));
                    else
                        tcs.TrySetResult(null);
                };
                var owner = ActiveMainWindow;
                _ = WindowService.ShowDialogAsync(host, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        /// <summary>Result envelope from the unified Slideshow Settings dialog
        /// (library mode). <see cref="StartRequested"/> is true when the user
        /// clicked Start (rather than OK), so the shell can immediately route
        /// to the slideshow engine.</summary>
        public readonly record struct UnifiedSlideshowResult(
            global::FracturingFog.Models.SlideshowConfig Config,
            bool AudioReactive,
            bool StartRequested);

        /// <summary>
        /// Library-mode entry point. Binds the dialog to the live
        /// <see cref="global::FracturingFog.Models.SlideshowConfigFile"/>; the
        /// VM Save/Delete/Import buttons mutate the library directly. Returns
        /// the resolved Result on OK or Start, null on Cancel.
        /// </summary>
        public static Task<UnifiedSlideshowResult?>
            ShowSlideshowSettingsAsync(
                global::FracturingFog.Models.SlideshowConfigFile file,
                bool audioReactive,
                IReadOnlyList<string>? regionNames = null,
                IReadOnlyList<string>? themeNames = null,
                Action<Action<double, double, double>>? capturePostFxCallback = null,
                IReadOnlyList<string>? animationNames = null)
        {
            var tcs = new TaskCompletionSource<UnifiedSlideshowResult?>();

            void Run()
            {
                var vm = new SlideshowSettingsViewModel(file, audioReactive);
                vm.PopulateAvailableLists(regionNames, themeNames, animationNames);
                var panel = new SlideshowSettingsView { DataContext = vm };
                var win = new PanelHostWindow(panel, SlideshowHostOptions());
                vm.ShowAudioDialogRequested += (_, _) => _ = ShowAudioSettingsAsync(win);
                vm.CapturePostFxRequested += (_, _) =>
                {
                    capturePostFxCallback?.Invoke((b, c, a) => vm.ApplyCapturedPostFx(b, c, a));
                };

                vm.ImportRequested += async (_, _) =>
                {
                    try
                    {
                        var path = await PickOpenFileAsync(
                            "Import Slideshow Preset",
                            "JSON File (*.json)|*.json|All Files (*.*)|*.*");
                        if (string.IsNullOrEmpty(path)) return;
                        var names = global::FracturingFog.Models.SlideshowConfigLibrary.Import(file, path);
                        if (names.Count == 0)
                        {
                            await ShowMessageAsync(
                                "Import Slideshow Preset",
                                "The file contains no slideshow presets.",
                                expectsConfirmation: false);
                            return;
                        }
                        vm.ApplyImportedConfig(names[names.Count - 1]);
                        if (names.Count > 1)
                            await ShowMessageAsync(
                                "Import Slideshow Preset",
                                $"{names.Count} presets imported.",
                                expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaDialogs] Slideshow Import failed: {ex.Message}");
                    }
                };

                vm.ExportRequested += async (_, _) =>
                {
                    try
                    {
                        var path = await PickSaveFileAsync(
                            "Export Slideshow Preset",
                            SanitizeFileName(vm.ActiveName) + ".json",
                            "JSON File (*.json)|*.json");
                        if (string.IsNullOrEmpty(path)) return;
                        global::FracturingFog.Models.SlideshowConfigLibrary.Export(file, vm.ActiveName, path);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaDialogs] Slideshow Export failed: {ex.Message}");
                    }
                };

                vm.EditVideoSettingsRequested += async (_, _) =>
                {
                    try
                    {
                        var current = file.Configs.FirstOrDefault(c =>
                            string.Equals(c.Name, vm.ActiveName, StringComparison.OrdinalIgnoreCase))?.Video;
                        var edited = await ShowVideoSettingsAsync(current, owner: win);
                        if (edited != null) vm.ApplyEditedVideoSettings(edited);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaDialogs] Video Settings edit failed: {ex.Message}");
                    }
                };

                vm.UnsavedStartPrompt += async (_, _) =>
                {
                    try
                    {
                        // Yes = Start with current edits. No = Save first (focus the Name combo).
                        var result = await ShowMessageAsync(
                            "Unsaved Changes",
                            "You have unsaved changes.\n\nYes = Start the slideshow now with the current edits.\nNo  = Return to the dialog and save them under a name first.",
                            expectsConfirmation: true);
                        if (result == MessageResult.Yes) vm.ProceedToStart();
                        else vm.RequestNameFocus();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaDialogs] Unsaved-start prompt failed: {ex.Message}");
                    }
                };

                win.Closed += (_, _) =>
                {
                    if (tcs.Task.IsCompleted) return;
                    if (vm.Result != null)
                        tcs.TrySetResult(new UnifiedSlideshowResult(vm.Result, vm.AudioReactiveResult, vm.StartRequested));
                    else
                        tcs.TrySetResult(null);
                };

                var owner = ActiveMainWindow;
                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        /// <summary>Opens the embedded-mode Avalonia Video Settings dialog.
        /// Returns a populated <see cref="global::FracturingFog.Models.VideoSettingsConfig"/>
        /// on OK, null on Cancel.</summary>
        public static Task<global::FracturingFog.Models.VideoSettingsConfig?>
            ShowVideoSettingsAsync(global::FracturingFog.Models.VideoSettingsConfig? current, Window? owner = null)
        {
            var tcs = new TaskCompletionSource<global::FracturingFog.Models.VideoSettingsConfig?>();

            async void Run()
            {
                var vm = new VideoSettingsViewModel(current);
                var panel = new VideoSettingsView { DataContext = vm };
                await WindowService.ShowPanelDialogAsync(
                    panel,
                    new PanelHostOptions(
                        "Video Settings",
                        Width: 460, MinWidth: 380,
                        Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))),
                    owner ?? ActiveMainWindow);
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(vm.Result);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        private static string SanitizeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "slideshow";
            foreach (var bad in Path.GetInvalidFileNameChars())
                s = s.Replace(bad, '_');
            return s;
        }

        // ── Audio-reactive settings ────────────────────────────────────────────

        /// <summary>
        /// Opens the Avalonia <see cref="AudioSettingsView"/> bound to a fresh
        /// <see cref="AudioSettingsViewModel"/> seeded from the persisted
        /// <see cref="AudioSettingsStore"/>. Persists the edited settings on OK.
        /// Shown as a nested modal owned by <paramref name="owner"/> (the
        /// Slideshow-Settings dialog). No live meter pump is wired here — there
        /// is no active beat source in the settings context, so BPM/level read
        /// "—" (the VM degrades gracefully when liveSource is null).
        /// </summary>
        public static Task ShowAudioSettingsAsync(
            Window? owner,
            global::FracturingFog.Audio.IBeatSource? liveSource = null,
            System.Collections.Generic.IReadOnlyList<
                global::FracturingFog.UI.Avalonia.ViewModels.AudioBindingRowViewModel>? bindingRows = null)
        {
            var tcs = new TaskCompletionSource<bool>();

            async void Run()
            {
                // #271 (parent #58) — Tier B lazy prompt. On Linux/macOS where the
                // OpenAL runtime is missing, live mic/loopback are greyed in the
                // picker; offer the one-time install/skip dialog first so the user
                // learns why and can enable it. Suppressed after they elect
                // Manual/Skip; a successful rescan ungreys sources for this open.
                if (!OperatingSystem.IsWindows()
                    && !global::FracturingFog.Audio.OpenAlRuntime.IsAvailable()
                    && !global::FracturingFog.Models.AudioRuntimePreferences.Instance.SuppressPrompt())
                {
                    try { await AudioRuntimeSetupDialog.ShowAsync(owner); } catch { }
                }

                var current = AudioSettingsStore.Load();
                var vm = new AudioSettingsViewModel(current, liveSource,
                    capabilities: AudioCapabilityProbe.Detect(),
                    bindingRows: bindingRows);
                var panel = new AudioSettingsView { DataContext = vm };

                // Live meter pump — while a beat source is active, refresh the
                // BPM / band-level readout ~20 Hz. Stopped when the dialog closes.
                DispatcherTimer? meter = null;
                if (liveSource != null)
                {
                    meter = new DispatcherTimer(
                        TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                        (_, _) => vm.Tick());
                    meter.Start();
                }

                // Browse… → Avalonia open-file picker; push the chosen path back.
                vm.BrowseFileRequested += async (_, _) =>
                {
                    var path = await PickOpenFileAsync(
                        "Choose Audio File",
                        "Audio (*.mp3;*.wav;*.flac;*.ogg)|*.mp3;*.wav;*.flac;*.ogg|All files (*.*)|*.*");
                    if (!string.IsNullOrEmpty(path)) vm.FilePath = path!;
                };

                try
                {
                    var result = await WindowService.ShowPanelDialogAsync(
                        panel,
                        new PanelHostOptions(
                            "Audio-Reactive Settings",
                            Width: 520, MinWidth: 420,
                            Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))),
                        owner);

                    // OK (true) commits vm.Result; persist it. Cancel/dismiss → no save.
                    if (result == true)
                    {
                        try { AudioSettingsStore.Save(vm.Result); } catch { }
                    }
                }
                finally { meter?.Stop(); }

                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── General application settings ─────────────────────────────────────

        /// <summary>
        /// Opens the general <see cref="AppSettingsView"/> seeded from the
        /// persisted <see cref="FracturingFog.Models.AnimationSettings"/>. On
        /// OK, persists the edited settings and invalidates the animation
        /// bus's cached ceiling so the new value takes effect on the next
        /// region jump without a restart. Cancel discards.
        /// </summary>
        public static Task ShowAppSettingsAsync(Window? owner)
        {
            var tcs = new TaskCompletionSource<bool>();

            async void Run()
            {
                var current = FracturingFog.Models.AnimationSettingsStore.Load();
                var vm = new AppSettingsViewModel(current);
                var panel = new AppSettingsView { DataContext = vm };

                // PanelHostWindow owns the window chrome + closing; we await the
                // pop-out and persist on a committed result.
                await WindowService.ShowPanelDialogAsync(
                    panel,
                    new PanelHostOptions(
                        "Application Settings",
                        Width: 520, MinWidth: 440,
                        Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))),
                    owner);

                if (vm.Result != null)
                {
                    try { FracturingFog.Models.AnimationSettingsStore.Save(vm.Result); } catch { }
                    FracturingFog.UI.Avalonia.ViewModels.Animation
                        .AnimationBusHost.InvalidateCeilingCache();
                }

                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Add / Replace import prompt ──────────────────────────────────────

        public enum AddOrReplaceResult { Cancel, Add, Replace }

        /// <summary>
        /// 3-button modal used by the Color Theme Editor's palette-import
        /// flow. Asks whether to append the imported colors at position=1
        /// (Add) or rebuild the stops list with positions redistributed 0…1
        /// (Replace). Cancel discards the imported colors.
        /// </summary>
        public static Task<AddOrReplaceResult> ShowAddOrReplaceAsync(
            int importedCount,
            int currentCount,
            string fileLabel)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<AddOrReplaceResult>();

            void Run()
            {
                var win = new Window
                {
                    Title = "Import Palette",
                    Width = 460,
                    MinWidth = 380,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x1C, 0x1C, 0x1C)),
                };

                var title = new TextBlock
                {
                    Text = $"Loaded {importedCount} color" + (importedCount == 1 ? "" : "s")
                        + $" from {fileLabel}",
                    Foreground = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0xC8, 0xC8, 0x64)),
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(16, 16, 16, 4),
                };

                var body = new TextBlock
                {
                    Text = $"Current stops: {currentCount}.\n\n"
                        + "Add — append imported colors at position 1.0 (existing stops untouched).\n"
                        + "Replace — discard current stops and rebuild from imported colors (positions redistributed 0…1).",
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(16, 4, 16, 12),
                    TextWrapping = TextWrapping.Wrap,
                };

                var add = new Button
                {
                    Content = "Add",
                    MinWidth = 90,
                    IsDefault = true,
                    Background = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x28, 0x50, 0x28)),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                };
                var replace = new Button
                {
                    Content = "Replace",
                    MinWidth = 90,
                    Background = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x50, 0x32, 0x32)),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                };
                var cancel = new Button
                {
                    Content = "Cancel",
                    MinWidth = 90,
                    IsCancel = true,
                    Background = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(0x3C, 0x3C, 0x3C)),
                    Foreground = Brushes.White,
                };

                AddOrReplaceResult pending = AddOrReplaceResult.Cancel;
                add.Click     += (_, _) => { pending = AddOrReplaceResult.Add;     win.Close(); };
                replace.Click += (_, _) => { pending = AddOrReplaceResult.Replace; win.Close(); };
                cancel.Click  += (_, _) => { pending = AddOrReplaceResult.Cancel;  win.Close(); };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 4, 16, 16),
                    Spacing = 8,
                };
                row.Children.Add(cancel);
                row.Children.Add(replace);
                row.Children.Add(add);

                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
                Grid.SetRow(title, 0);
                Grid.SetRow(body, 1);
                Grid.SetRow(row, 2);
                grid.Children.Add(title);
                grid.Children.Add(body);
                grid.Children.Add(row);
                win.Content = grid;
                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending); };

                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Text prompt ──────────────────────────────────────────────────────

        /// <summary>
        /// Modal text-input prompt. Returns the entered string on OK, null on
        /// cancel. Used where the editor + menu need a simple "give me a name"
        /// flow without a dedicated dialog VM.
        /// </summary>
        public static Task<string?> PromptForTextAsync(
            string title,
            string prompt,
            string suggested = "")
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<string?>();

            void Run()
            {
                var box = new TextBox
                {
                    Text = suggested,
                    PlaceholderText = prompt,
                    Margin = new Thickness(16, 8, 16, 8),
                    MinWidth = 320,
                };
                var win = new Window
                {
                    Title = string.IsNullOrEmpty(title) ? "Prompt" : title,
                    Width = 420,
                    MinWidth = 320,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                };
                var promptText = new TextBlock
                {
                    Text = prompt,
                    Foreground = Brushes.White,
                    Margin = new Thickness(16, 16, 16, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
                var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
                string? pending = null;
                void Close(string? r) { pending = r; win.Close(); }
                ok.Click += (_, _) => Close(box.Text);
                cancel.Click += (_, _) => Close(null);

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 4, 16, 16),
                    Spacing = 8,
                };
                buttonRow.Children.Add(cancel);
                buttonRow.Children.Add(ok);

                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
                Grid.SetRow(promptText, 0);
                Grid.SetRow(box, 1);
                Grid.SetRow(buttonRow, 2);
                grid.Children.Add(promptText);
                grid.Children.Add(box);
                grid.Children.Add(buttonRow);
                win.Content = grid;
                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending); };

                _ = WindowService.ShowDialogAsync(win, owner);
                box.Focus();
                box.SelectAll();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        /// <summary>
        /// Save-Region prompt: like PromptForTextAsync but with an additional
        /// "Include watermark" checkbox shown only when a custom watermark is
        /// active and an optional Animation dropdown populated from the host
        /// animation library. Returns (Name, IncludeWatermark, AnimationName)
        /// on OK, null on cancel. <paramref name="animationNames"/> may be
        /// empty — the dropdown is hidden in that case. AnimationName is null
        /// when "(none)" is selected.
        /// </summary>
        public static Task<(string Name, bool IncludeWatermark, string? AnimationName)?> PromptForSaveRegionAsync(
            string title,
            string prompt,
            string suggested,
            bool customWatermarkAvailable,
            System.Collections.Generic.IReadOnlyList<string>? animationNames = null,
            string? animationDefault = null)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<(string, bool, string?)?>();

            void Run()
            {
                var box = new TextBox
                {
                    Text = suggested,
                    PlaceholderText = prompt,
                    Margin = new Thickness(16, 8, 16, 8),
                    MinWidth = 320,
                };
                var includeWatermark = new CheckBox
                {
                    Content = "Include custom watermark in this region",
                    Foreground = Brushes.White,
                    IsEnabled = customWatermarkAvailable,
                    IsChecked = false,
                    Margin = new Thickness(16, 0, 16, 8),
                };
                if (!customWatermarkAvailable)
                {
                    global::Avalonia.Controls.ToolTip.SetTip(includeWatermark,
                        "Enable \"Use custom watermark\" + pick a saved watermark first.");
                }

                // Animation dropdown — hidden when the library is empty.
                bool hasAnimations = animationNames != null && animationNames.Count > 0;
                const string NoneSentinel = "(none)";
                var animLabel = new TextBlock
                {
                    Text = "Attach animation:",
                    Foreground = Brushes.White,
                    Margin = new Thickness(16, 0, 16, 2),
                    IsVisible = hasAnimations,
                };
                var animCombo = new ComboBox
                {
                    MinWidth = 320,
                    Margin = new Thickness(16, 0, 16, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsVisible = hasAnimations,
                };
                animCombo.Items.Add(NoneSentinel);
                if (hasAnimations)
                {
                    foreach (var n in animationNames!) animCombo.Items.Add(n);
                    animCombo.SelectedItem = !string.IsNullOrEmpty(animationDefault)
                        && animCombo.Items.Contains(animationDefault)
                            ? animationDefault
                            : NoneSentinel;
                }

                var win = new Window
                {
                    Title = string.IsNullOrEmpty(title) ? "Save Region" : title,
                    Width = 460,
                    MinWidth = 360,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                };
                var promptText = new TextBlock
                {
                    Text = prompt,
                    Foreground = Brushes.White,
                    Margin = new Thickness(16, 16, 16, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
                var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
                (string, bool, string?)? pending = null;
                void Close((string, bool, string?)? r) { pending = r; win.Close(); }
                ok.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(box.Text)) { Close(null); return; }
                    string? animName = null;
                    if (hasAnimations
                        && animCombo.SelectedItem is string s
                        && !string.Equals(s, NoneSentinel, StringComparison.Ordinal))
                    {
                        animName = s;
                    }
                    Close((box.Text!, includeWatermark.IsChecked == true, animName));
                };
                cancel.Click += (_, _) => Close(null);

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 4, 16, 16),
                    Spacing = 8,
                };
                buttonRow.Children.Add(cancel);
                buttonRow.Children.Add(ok);

                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
                Grid.SetRow(promptText, 0);
                Grid.SetRow(box, 1);
                Grid.SetRow(includeWatermark, 2);
                Grid.SetRow(animLabel, 3);
                Grid.SetRow(animCombo, 4);
                Grid.SetRow(buttonRow, 5);
                grid.Children.Add(promptText);
                grid.Children.Add(box);
                grid.Children.Add(includeWatermark);
                grid.Children.Add(animLabel);
                grid.Children.Add(animCombo);
                grid.Children.Add(buttonRow);
                win.Content = grid;
                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending); };

                _ = WindowService.ShowDialogAsync(win, owner);
                box.Focus();
                box.SelectAll();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Poster print ──────────────────────────────────────────────────────

        /// <summary>
        /// Modal poster-size prompt mirroring the legacy WinForms PosterDialog:
        /// poster width/height in inches × a DPI preset (150 / 300 / 600) gives
        /// the output pixel dimensions. Returns (PixelWidth, PixelHeight,
        /// Portrait, UseCustomWatermark, WatermarkName) on OK, null on cancel.
        /// Portrait also drives the 90° rotate. UseCustomWatermark + WatermarkName
        /// let the caller swap in the matching <c>WatermarkDef</c> from
        /// <c>UserWatermarkStore</c> before submitting the poster render.
        /// </summary>
        public static Task<(int Width, int Height, bool Portrait, bool UseCustomWatermark, string? WatermarkName)?> ShowPosterAsync(
            System.Collections.Generic.IEnumerable<string> watermarkNames,
            bool customWatermarkDefault,
            string? watermarkNameDefault,
            Action? onEditWatermark)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<(int, int, bool, bool, string?)?>();

            void Run()
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;

                // #189 — physical (poster) size fields, in the currently selected
                // unit, plus their pixel-size twins. Either side may be edited; the
                // other is kept in sync through the DPI. The returned dims are
                // always PIXELS (parsed from the pixel fields on OK).
                var widthTx = new TextBox { Text = "24", MinWidth = 70 };
                var heightTx = new TextBox { Text = "36", MinWidth = 70 };
                var pxWidthTx = new TextBox { Text = "7200", MinWidth = 70 };
                var pxHeightTx = new TextBox { Text = "10800", MinWidth = 70 };

                var portrait = new CheckBox { Content = "Portrait orientation", IsChecked = true, Foreground = Brushes.White };

                // #189 feature 1 — Inches / Centimeters unit toggle.
                var inchesRb = new RadioButton { Content = "Inches", GroupName = "units", IsChecked = true, Foreground = Brushes.White };
                var cmRb = new RadioButton { Content = "Centimeters", GroupName = "units", Foreground = Brushes.White };

                var lowDpi = new RadioButton { Content = "Low (150 DPI)", GroupName = "dpi", Foreground = Brushes.White };
                var medDpi = new RadioButton { Content = "Med (300 DPI)", GroupName = "dpi", IsChecked = true, Foreground = Brushes.White };
                var highDpi = new RadioButton { Content = "High (600 DPI)", GroupName = "dpi", Foreground = Brushes.White };

                // #189 feature 3 — standard-definition presets. Blank first entry =
                // "no preset"; a manual size edit snaps back to it.
                var defCombo = new ComboBox { MinWidth = 160 };
                var definitions = new (string Name, int W, int H)[]
                {
                    ("— none —", 0, 0),
                    ("SD (640 × 480)", 640, 480),
                    ("HD (1280 × 720)", 1280, 720),
                    ("Full HD (1920 × 1080)", 1920, 1080),
                    ("QHD (2560 × 1440)", 2560, 1440),
                    ("4K UHD (3840 × 2160)", 3840, 2160),
                    ("5K (5120 × 2880)", 5120, 2880),
                    ("8K UHD (7680 × 4320)", 7680, 4320),
                };
                foreach (var d in definitions) defCombo.Items.Add(d.Name);
                defCombo.SelectedIndex = 0;   // "— none —" shown after items exist

                var pixelLabel = new TextBlock
                {
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                var wLbl = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
                var hLbl = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };

                bool sync = false;   // re-entrancy guard for programmatic edits

                int Dpi() => lowDpi.IsChecked == true ? 150 : highDpi.IsChecked == true ? 600 : 300;
                bool Cm() => cmRb.IsChecked == true;
                double ToInch(double v) => Cm() ? v / 2.54 : v;
                double FromInch(double inch) => Cm() ? inch * 2.54 : inch;
                double ParseD(TextBox t)
                {
                    double.TryParse(t.Text, System.Globalization.NumberStyles.Any, ci, out double v);
                    return v < 0 ? 0 : v;
                }
                void SetText(TextBox t, string s) { sync = true; t.Text = s; sync = false; }
                (int w, int h) PixelsNow()
                {
                    int.TryParse(pxWidthTx.Text, out int pw);
                    int.TryParse(pxHeightTx.Text, out int ph);
                    return (pw < 0 ? 0 : pw, ph < 0 ? 0 : ph);
                }
                void RefreshOut()
                {
                    var (pw, ph) = PixelsNow();
                    pixelLabel.Foreground = Brushes.LightGray;
                    pixelLabel.Text = $"Output: {pw:N0} × {ph:N0} px  ({(long)pw * ph / 1_000_000:N0} MP)";
                }
                // Physical → pixels (poster fields drive).
                void PosterToPixels()
                {
                    int dpi = Dpi();
                    SetText(pxWidthTx, ((int)Math.Round(ToInch(ParseD(widthTx)) * dpi)).ToString(ci));
                    SetText(pxHeightTx, ((int)Math.Round(ToInch(ParseD(heightTx)) * dpi)).ToString(ci));
                    RefreshOut();
                }
                // Pixels → physical (pixel fields drive).
                void PixelsToPoster()
                {
                    int dpi = Dpi();
                    var (pw, ph) = PixelsNow();
                    SetText(widthTx, FromInch(pw / (double)dpi).ToString("0.##", ci));
                    SetText(heightTx, FromInch(ph / (double)dpi).ToString("0.##", ci));
                    RefreshOut();
                }
                void UpdateUnitLabels()
                {
                    string u = Cm() ? "cm" : "in";
                    wLbl.Text = $"Poster width ({u}):";
                    hLbl.Text = $"Poster height ({u}):";
                }
                void ClearDefinition() { if (defCombo.SelectedIndex != 0) { sync = true; defCombo.SelectedIndex = 0; sync = false; } }

                widthTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !sync) { ClearDefinition(); PosterToPixels(); } };
                heightTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !sync) { ClearDefinition(); PosterToPixels(); } };
                pxWidthTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !sync) { ClearDefinition(); PixelsToPoster(); } };
                pxHeightTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !sync) { ClearDefinition(); PixelsToPoster(); } };
                // DPI change keeps the physical size, recomputes pixels.
                lowDpi.IsCheckedChanged += (_, _) => PosterToPixels();
                medDpi.IsCheckedChanged += (_, _) => PosterToPixels();
                highDpi.IsCheckedChanged += (_, _) => PosterToPixels();
                // Unit change keeps the pixels, re-expresses the physical size.
                inchesRb.IsCheckedChanged += (_, _) => { UpdateUnitLabels(); PixelsToPoster(); };
                cmRb.IsCheckedChanged += (_, _) => { UpdateUnitLabels(); PixelsToPoster(); };
                defCombo.SelectionChanged += (_, _) =>
                {
                    if (sync) return;
                    int i = defCombo.SelectedIndex;
                    if (i <= 0) return;
                    SetText(pxWidthTx, definitions[i].W.ToString(ci));
                    SetText(pxHeightTx, definitions[i].H.ToString(ci));
                    PixelsToPoster();
                };
                UpdateUnitLabels();
                RefreshOut();

                var win = new Window
                {
                    Title = "Poster Print",
                    Width = 380,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                };

                // Watermark sub-controls.
                var useWatermark = new CheckBox
                {
                    Content = "Use custom watermark",
                    IsChecked = customWatermarkDefault,
                    Foreground = Brushes.White,
                };
                var wmCombo = new ComboBox { MinWidth = 200, IsEnabled = customWatermarkDefault };
                foreach (var n in watermarkNames) wmCombo.Items.Add(n);
                if (!string.IsNullOrEmpty(watermarkNameDefault) && wmCombo.Items.Contains(watermarkNameDefault))
                    wmCombo.SelectedItem = watermarkNameDefault;
                useWatermark.IsCheckedChanged += (_, _) => wmCombo.IsEnabled = useWatermark.IsChecked == true;
                var editWmBtn = new Button { Content = "Edit Watermark…", MinWidth = 120 };
                editWmBtn.Click += (_, _) => onEditWatermark?.Invoke();

                var grid = new Grid
                {
                    Margin = new Thickness(16),
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
                };
                void Place(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
                void PlaceSpan(Control c, int row) { Grid.SetRow(c, row); Grid.SetColumn(c, 0); Grid.SetColumnSpan(c, 2); grid.Children.Add(c); }

                int row = 0;

                // Units row (#189 feature 1).
                var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6) };
                unitRow.Children.Add(inchesRb);
                unitRow.Children.Add(cmRb);
                PlaceSpan(unitRow, row++);

                // Definition preset row (#189 feature 3).
                var defRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
                defRow.Children.Add(new TextBlock { Text = "Definition:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
                defRow.Children.Add(defCombo);
                PlaceSpan(defRow, row++);

                widthTx.Margin = new Thickness(0, 0, 0, 6);
                heightTx.Margin = new Thickness(0, 0, 0, 6);
                Place(wLbl, row, 0); Place(widthTx, row, 1); row++;
                Place(hLbl, row, 0); Place(heightTx, row, 1); row++;

                var dpiRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 4) };
                dpiRow.Children.Add(lowDpi);
                dpiRow.Children.Add(medDpi);
                dpiRow.Children.Add(highDpi);
                PlaceSpan(dpiRow, row++);

                // Pixel-size fields (#189 feature 2).
                var pxWLbl = new TextBlock { Text = "Pixel width (px):", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
                var pxHLbl = new TextBlock { Text = "Pixel height (px):", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
                pxWidthTx.Margin = new Thickness(0, 0, 0, 6);
                pxHeightTx.Margin = new Thickness(0, 0, 0, 6);
                Place(pxWLbl, row, 0); Place(pxWidthTx, row, 1); row++;
                Place(pxHLbl, row, 0); Place(pxHeightTx, row, 1); row++;

                PlaceSpan(portrait, row++);

                // Replaces the old "Output" line; keeps the px + MP readout.
                PlaceSpan(pixelLabel, row++);

                // Watermark band — checkbox row, combo + edit-button row.
                PlaceSpan(useWatermark, row++);
                var wmRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                wmRow.Children.Add(wmCombo);
                wmRow.Children.Add(editWmBtn);
                PlaceSpan(wmRow, row++);

                var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
                (int, int, bool, bool, string?)? pending = null;
                void Close((int, int, bool, bool, string?)? r) { pending = r; win.Close(); }
                ok.Click += (_, _) =>
                {
                    var (pw, ph) = PixelsNow();
                    if (pw <= 0 || ph <= 0)
                    {
                        pixelLabel.Foreground = Brushes.OrangeRed;
                        pixelLabel.Text = "Enter a positive width and height.";
                        return;
                    }
                    Close((pw, ph, portrait.IsChecked == true,
                        useWatermark.IsChecked == true,
                        wmCombo.SelectedItem as string));
                };
                cancel.Click += (_, _) => Close(null);

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Spacing = 8,
                };
                buttonRow.Children.Add(cancel);
                buttonRow.Children.Add(ok);
                PlaceSpan(buttonRow, row++);

                win.Content = grid;
                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending); };

                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Video Zoom ─────────────────────────────────────────────────────

        /// <summary>
        /// Avalonia port of the legacy WinForms <c>VideoDialog</c>. Collects the
        /// full single-shot / slideshow configuration (target QD coordinates,
        /// zoom, duration, recording flags, post-encode choice, reverse, TAA
        /// blend, band dither) and returns a <see cref="global::FracturingFog.Render.VideoZoomRequest"/>.
        /// Returns null on Cancel. <see cref="global::FracturingFog.Render.VideoZoomRequest.IsSlideshow"/>
        /// distinguishes the Slideshow button from Start.
        ///
        /// Built programmatically here (rather than as an axaml view in
        /// UI.Avalonia) because the parsing/region lookup leans on main-project
        /// internals — FormHelpers' QD coordinate codec, FractalRegionLibrary,
        /// FfmpegEncoder availability — that the Avalonia assembly cannot see.
        /// </summary>
        public static Task<global::FracturingFog.Render.VideoZoomRequest?> ShowVideoAsync(
            double currentCX, double currentCY, double currentZoom)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<global::FracturingFog.Render.VideoZoomRequest?>();

            void Run()
            {
                const double CustomSecsMin = 0.5;
                const double CustomSecsMax = 300.0;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                double ultraCap = global::FracturingFog.Models.QualityPreset.Ultra.ZoomMax;
                // IsEnabledForUser collapses "binary on disk" + "user has not
                // chosen Continue Without Video" — Skip election keeps the
                // Lossless UI greyed even when ffmpeg.exe is present, matching
                // the spec that the user's opt-out persists until they reverse
                // it from the FloatingMenu FFmpeg setup dialog.
                bool ffmpegHere = global::FracturingFog.FfmpegEncoder.IsEnabledForUser();

                // Cached QD limbs for the picked region (folded into the parsed
                // textbox value when the textbox carries only the Hi limb).
                double targetCXLo = 0, targetCX2 = 0, targetCX3 = 0;
                double targetCYLo = 0, targetCY2 = 0, targetCY3 = 0;
                int targetIterations = 0;
                string? targetQualityPresetName = null;
                bool suppressRegionPick = false;

                // ── Controls ─────────────────────────────────────────────
                var regionCombo = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
                regionCombo.Items.Add("— select region —");
                foreach (var r in global::FracturingFog.Models.FractalRegionLibrary.Instance.All
                             .OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name))
                    regionCombo.Items.Add(r.Name);
                regionCombo.SelectedIndex = 0;

                var txCX = new TextBox
                {
                    Text = global::FracturingFog.Abstractions.Math.QdCoordCodec.FormatCoordSingle(currentCX, 0, 0, 0),
                    FontFamily = new FontFamily("Consolas"),
                };
                var txCY = new TextBox
                {
                    Text = global::FracturingFog.Abstractions.Math.QdCoordCodec.FormatCoordSingle(currentCY, 0, 0, 0),
                    FontFamily = new FontFamily("Consolas"),
                };
                var txZoom = new TextBox
                {
                    Text = Math.Max(currentZoom * 10.0, 100.0).ToString("G6", ic),
                    FontFamily = new FontFamily("Consolas"),
                };

                var capWarn = new TextBlock
                {
                    Text = $"Max target zoom: {ultraCap:G3} (Ultra). Deeper values are clamped.",
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 160, 100)),
                    FontStyle = FontStyle.Italic,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                };

                // ── Speed radios ─────────────────────────────────────────
                var rbSlow = new RadioButton { Content = "Slow (15 s)", GroupName = "vidspeed", Foreground = Brushes.LightGray };
                var rbMed = new RadioButton { Content = "Medium (8 s)", GroupName = "vidspeed", IsChecked = true, Foreground = Brushes.LightGray };
                var rbFast = new RadioButton { Content = "Fast (4 s)", GroupName = "vidspeed", Foreground = Brushes.LightGray };
                var rbCustom = new RadioButton { Content = "Custom:", GroupName = "vidspeed", Foreground = Brushes.LightGray };
                var txCustomSecs = new TextBox { Text = "30", Width = 70, FontFamily = new FontFamily("Consolas") };
                txCustomSecs.GotFocus += (_, _) => rbCustom.IsChecked = true;
                txCustomSecs.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty && txCustomSecs.IsFocused)
                        rbCustom.IsChecked = true;
                };
                var customHint = new TextBlock
                {
                    Text = $"seconds (0.5 – {CustomSecsMax:F0})",
                    Foreground = Brushes.LightGray,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var chkConstantRate = new CheckBox
                {
                    Content = "Constant Rate (slideshow): scale duration by depth",
                    Foreground = Brushes.LightGray,
                };
                var chkSaveVideo = new CheckBox
                {
                    Content = "Save video as MP4 (single-shot only — ignored for slideshow)",
                    Foreground = Brushes.LightGray,
                };
                var chkSaveLossless = new CheckBox
                {
                    Content = "Save lossless (PNG sequence — single-shot only)",
                    Foreground = Brushes.LightGray,
                };

                var encodeCombo = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
                encodeCombo.Items.Add("Keep PNG sequence only");
                if (ffmpegHere)
                {
                    encodeCombo.Items.Add("Lossless H.264 (CRF 0) → .mp4");
                    encodeCombo.Items.Add("FFV1 → .mkv");
                    encodeCombo.Items.Add("Visually-lossless H.264 (CRF 18) → .mp4");
                }
                else
                {
                    encodeCombo.Items.Add("(ffmpeg.exe not found — only PNG output available)");
                }
                encodeCombo.SelectedIndex = 0;
                chkSaveLossless.IsCheckedChanged += (_, _) =>
                    encodeCombo.IsEnabled = chkSaveLossless.IsChecked == true && ffmpegHere;

                var chkReverse = new CheckBox
                {
                    Content = "Reverse zoom (start at target, end at classic view)",
                    Foreground = Brushes.LightGray,
                };

                // Adaptive iter cap mode — Off / Global / PerTile.
                // Default Global preserves the prior auto-adaptive behaviour.
                // Off = full quality (recommended for strong HW).
                // PerTile = vertical row bands cap independently from prior
                // frame band dwell so interior bands shed cost while boundary
                // bands keep full detail.
                var iterCapCombo = new ComboBox
                {
                    MinWidth = 280,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                iterCapCombo.Items.Add("Off (full quality, strong-HW recommended)");
                iterCapCombo.Items.Add("Global (per-frame adaptive multiplier)");
                iterCapCombo.Items.Add("PerTile (per-band cap from prior frame stats)");
                iterCapCombo.SelectedIndex = 1;
                global::FracturingFog.Models.VideoIterCapMode PickIterCapMode() =>
                    iterCapCombo.SelectedIndex switch
                    {
                        0 => global::FracturingFog.Models.VideoIterCapMode.Off,
                        2 => global::FracturingFog.Models.VideoIterCapMode.PerTile,
                        _ => global::FracturingFog.Models.VideoIterCapMode.Global,
                    };

                // ── Smoothing (TAA blend + band dither) ──────────────────
                var taaSlider = new Slider { Minimum = 0, Maximum = 100, Value = 55, TickFrequency = 10, Width = 230 };
                var taaValue = new TextBlock { Text = "55%", Foreground = Brushes.LightGray, Width = 40, VerticalAlignment = VerticalAlignment.Center };
                taaSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == RangeBase.ValueProperty)
                        taaValue.Text = $"{(int)Math.Round(taaSlider.Value)}%";
                };

                var chkBandDither = new CheckBox { Content = "Band dither:", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
                var ditherSlider = new Slider { Minimum = 0, Maximum = 100, Value = 25, TickFrequency = 10, Width = 230, IsEnabled = false };
                var ditherValue = new TextBlock { Text = "25%", Foreground = Brushes.LightGray, Width = 40, VerticalAlignment = VerticalAlignment.Center };
                chkBandDither.IsCheckedChanged += (_, _) => ditherSlider.IsEnabled = chkBandDither.IsChecked == true;
                ditherSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property == RangeBase.ValueProperty)
                        ditherValue.Text = $"{(int)Math.Round(ditherSlider.Value)}%";
                };

                var chkUseRegionWatermark = new CheckBox
                {
                    Content = "Use each region's embedded watermark (slideshow only)",
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 4, 0, 0),
                };

                // Single-shot theme-fade controls: enable + count. The slideshow
                // path always cycles 3 themes/leg today, so these affect Start
                // only — Slideshow ignores them.
                var chkThemeFade = new CheckBox
                {
                    Content = "Cycle color themes during zoom (single-shot)",
                    Foreground = Brushes.LightGray,
                };
                var nudThemesPerLeg = new NumericUpDown
                {
                    Minimum = 2, Maximum = 12, Increment = 1, Value = 3,
                    Width = 90,
                    IsEnabled = false,
                };
                var themesLbl = new TextBlock
                {
                    Text = "Themes per zoom:",
                    Foreground = Brushes.LightGray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                chkThemeFade.IsCheckedChanged += (_, _) =>
                    nudThemesPerLeg.IsEnabled = chkThemeFade.IsChecked == true;
                var themeFadeRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Margin = new Thickness(20, 2, 0, 0),
                };
                themeFadeRow.Children.Add(themesLbl);
                themeFadeRow.Children.Add(nudThemesPerLeg);

                var errLabel = new TextBlock
                {
                    Foreground = Brushes.OrangeRed,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = false,
                    Margin = new Thickness(0, 4, 0, 0),
                };

                // ── Region pick: prefill targets from the chosen region ──
                regionCombo.SelectionChanged += (_, _) =>
                {
                    if (suppressRegionPick) return;
                    int idx = regionCombo.SelectedIndex;
                    if (idx <= 0) return;
                    string? name = regionCombo.SelectedItem as string;
                    if (string.IsNullOrEmpty(name)) return;
                    var region = global::FracturingFog.Models.FractalRegionLibrary.Instance.FindByName(name);
                    if (region == null) return;

                    // Note: FractalRegionLibrary.All excludes Extreme regions by
                    // default, so the legacy Extreme-confirmation prompt is
                    // unreachable here; deep targets are simply clamped to the
                    // Ultra cap (the cap-warn label states this).
                    targetCXLo = region.CenterXLo; targetCX2 = region.CenterX2; targetCX3 = region.CenterX3;
                    targetCYLo = region.CenterYLo; targetCY2 = region.CenterY2; targetCY3 = region.CenterY3;

                    txCX.Text = global::FracturingFog.Abstractions.Math.QdCoordCodec.FormatCoordSingle(
                        region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    txCY.Text = global::FracturingFog.Abstractions.Math.QdCoordCodec.FormatCoordSingle(
                        region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);

                    double z = region.Zoom;
                    if (z > ultraCap) z = ultraCap;
                    txZoom.Text = z.ToString("G6", ic);
                    targetIterations = region.Iterations;
                    targetQualityPresetName = region.QualityPreset?.Name;
                };

                // ── Parsing ──────────────────────────────────────────────
                bool TryGetSeconds(out double seconds)
                {
                    if (rbCustom.IsChecked == true)
                    {
                        if (!double.TryParse(txCustomSecs.Text?.Trim(), System.Globalization.NumberStyles.Float, ic, out seconds))
                            return false;
                        return seconds >= CustomSecsMin && seconds <= CustomSecsMax;
                    }
                    seconds = rbSlow.IsChecked == true ? 15.0 : rbFast.IsChecked == true ? 4.0 : 8.0;
                    return true;
                }

                bool TryGetTargetQD(
                    out double cxHi, out double cxLo, out double cx2, out double cx3,
                    out double cyHi, out double cyLo, out double cy2, out double cy3,
                    out double zoom, out double seconds)
                {
                    bool okCX = global::FracturingFog.Abstractions.Math.QdCoordCodec.TryParseCoordAny(
                        txCX.Text ?? "", out cxHi, out cxLo, out cx2, out cx3);
                    bool okCY = global::FracturingFog.Abstractions.Math.QdCoordCodec.TryParseCoordAny(
                        txCY.Text ?? "", out cyHi, out cyLo, out cy2, out cy3);

                    if (okCX && cxLo == 0 && cx2 == 0 && cx3 == 0)
                    { cxLo = targetCXLo; cx2 = targetCX2; cx3 = targetCX3; }
                    if (okCY && cyLo == 0 && cy2 == 0 && cy3 == 0)
                    { cyLo = targetCYLo; cy2 = targetCY2; cy3 = targetCY3; }

                    bool okZ = double.TryParse(txZoom.Text?.Trim(), System.Globalization.NumberStyles.Float, ic, out zoom);
                    bool okS = TryGetSeconds(out seconds);
                    if (!okCX || !okCY || !okZ || zoom <= 0 || !okS)
                    {
                        cxHi = cxLo = cx2 = cx3 = cyHi = cyLo = cy2 = cy3 = zoom = seconds = 0;
                        return false;
                    }
                    return true;
                }

                // ── Window + layout ──────────────────────────────────────
                var win = new Window
                {
                    Title = "Video Zoom",
                    Width = 440,
                    MinWidth = 420,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                };

                static TextBlock Lbl(string t) => new()
                {
                    Text = t,
                    Foreground = Brushes.LightGray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };

                Grid LabeledRow(string label, Control field)
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
                    var l = Lbl(label);
                    Grid.SetColumn(l, 0);
                    Grid.SetColumn(field, 1);
                    g.Children.Add(l);
                    g.Children.Add(field);
                    return g;
                }

                // Speed group
                var speedTop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                speedTop.Children.Add(rbSlow);
                speedTop.Children.Add(rbMed);
                speedTop.Children.Add(rbFast);
                var speedCustomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
                speedCustomRow.Children.Add(rbCustom);
                speedCustomRow.Children.Add(txCustomSecs);
                speedCustomRow.Children.Add(customHint);
                var speedBox = new StackPanel { Spacing = 2 };
                speedBox.Children.Add(new TextBlock { Text = "Zoom Speed", Foreground = Brushes.LightGray, FontWeight = FontWeight.Bold });
                speedBox.Children.Add(speedTop);
                speedBox.Children.Add(speedCustomRow);

                // Smoothing group
                var taaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                taaRow.Children.Add(new TextBlock { Text = "Temporal blend:", Foreground = Brushes.LightGray, Width = 100, VerticalAlignment = VerticalAlignment.Center });
                taaRow.Children.Add(taaSlider);
                taaRow.Children.Add(taaValue);
                var ditherRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
                chkBandDither.Width = 100;
                ditherRow.Children.Add(chkBandDither);
                ditherRow.Children.Add(ditherSlider);
                ditherRow.Children.Add(ditherValue);
                var smoothBox = new StackPanel { Spacing = 2 };
                smoothBox.Children.Add(new TextBlock { Text = "Smoothing (Video Only)", Foreground = Brushes.LightGray, FontWeight = FontWeight.Bold });
                smoothBox.Children.Add(taaRow);
                smoothBox.Children.Add(ditherRow);

                // Buttons
                var slideshowBtn = new Button { Content = "Slideshow", MinWidth = 96, Background = new SolidColorBrush(Color.FromRgb(55, 40, 70)), Foreground = Brushes.White };
                var startBtn = new Button { Content = "Start", MinWidth = 76, IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(60, 80, 60)), Foreground = Brushes.White };
                var cancelBtn = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true, Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), Foreground = Brushes.White };

                global::FracturingFog.Render.VideoZoomRequest? pending = null;
                void Close(global::FracturingFog.Render.VideoZoomRequest? r) { pending = r; win.Close(); }

                global::FracturingFog.Render.VideoLosslessEncode MapEncode()
                {
                    if (chkSaveLossless.IsChecked != true || !encodeCombo.IsEnabled)
                        return global::FracturingFog.Render.VideoLosslessEncode.None;
                    return encodeCombo.SelectedIndex switch
                    {
                        1 => global::FracturingFog.Render.VideoLosslessEncode.LosslessH264Mp4,
                        2 => global::FracturingFog.Render.VideoLosslessEncode.Ffv1Mkv,
                        3 => global::FracturingFog.Render.VideoLosslessEncode.HighQualityH264Mp4,
                        _ => global::FracturingFog.Render.VideoLosslessEncode.None,
                    };
                }

                startBtn.Click += (_, _) =>
                {
                    if (!TryGetTargetQD(out double cxHi, out double cxLo, out double cx2, out double cx3,
                                        out double cyHi, out double cyLo, out double cy2, out double cy3,
                                        out double zoom, out double seconds))
                    {
                        errLabel.Text = "Enter valid target CX / CY, a positive zoom, and " +
                                        $"(if Custom) a duration between {CustomSecsMin} and {CustomSecsMax:F0} s.";
                        errLabel.IsVisible = true;
                        return;
                    }
                    Close(new global::FracturingFog.Render.VideoZoomRequest
                    {
                        TargetCXHi = cxHi, TargetCXLo = cxLo, TargetCX2 = cx2, TargetCX3 = cx3,
                        TargetCYHi = cyHi, TargetCYLo = cyLo, TargetCY2 = cy2, TargetCY3 = cy3,
                        TargetZoom = zoom,
                        TargetIterations = targetIterations,
                        TargetQualityPresetName = targetQualityPresetName,
                        TargetRegionName = regionCombo.SelectedIndex > 0
                            ? regionCombo.SelectedItem as string
                            : null,
                        Seconds = seconds,
                        IsSlideshow = false,
                        IsReverse = chkReverse.IsChecked == true,
                        IsSaveVideo = chkSaveVideo.IsChecked == true,
                        IsSaveLossless = chkSaveLossless.IsChecked == true,
                        LosslessEncode = MapEncode(),
                        TaaSmoothing = (int)Math.Round(taaSlider.Value),
                        BandDither = chkBandDither.IsChecked == true,
                        BandDitherStrength = (int)Math.Round(ditherSlider.Value),
                        ThemeFadeEnabled = chkThemeFade.IsChecked == true,
                        ThemesPerLeg = (int)Math.Round(nudThemesPerLeg.Value ?? 3m),
                        IterCapMode = PickIterCapMode(),
                    });
                };

                slideshowBtn.Click += (_, _) =>
                {
                    double? secsOverride = null;
                    if (rbCustom.IsChecked == true)
                    {
                        if (!double.TryParse(txCustomSecs.Text?.Trim(), System.Globalization.NumberStyles.Float, ic, out double secs)
                            || secs < CustomSecsMin || secs > CustomSecsMax)
                        {
                            errLabel.Text = $"Custom duration must be between {CustomSecsMin} and {CustomSecsMax:F0} seconds.";
                            errLabel.IsVisible = true;
                            return;
                        }
                        secsOverride = secs;
                    }
                    Close(new global::FracturingFog.Render.VideoZoomRequest
                    {
                        IsSlideshow = true,
                        SlideshowSecondsOverride = secsOverride,
                        IsConstantRate = chkConstantRate.IsChecked == true,
                        IsReverse = chkReverse.IsChecked == true,
                        TaaSmoothing = (int)Math.Round(taaSlider.Value),
                        BandDither = chkBandDither.IsChecked == true,
                        BandDitherStrength = (int)Math.Round(ditherSlider.Value),
                        UseRegionWatermark = chkUseRegionWatermark.IsChecked == true,
                        ThemeFadeEnabled = chkThemeFade.IsChecked == true,
                        ThemesPerLeg = (int)Math.Round(nudThemesPerLeg.Value ?? 3m),
                        IterCapMode = PickIterCapMode(),
                    });
                };
                cancelBtn.Click += (_, _) => Close(null);

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                };
                buttonRow.Children.Add(slideshowBtn);
                buttonRow.Children.Add(startBtn);
                buttonRow.Children.Add(cancelBtn);

                var root = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
                root.Children.Add(LabeledRow("Region:", regionCombo));
                root.Children.Add(LabeledRow("Target CX:", txCX));
                root.Children.Add(LabeledRow("Target CY:", txCY));
                root.Children.Add(LabeledRow("Target Zoom:", txZoom));
                root.Children.Add(capWarn);
                root.Children.Add(speedBox);
                root.Children.Add(chkConstantRate);
                root.Children.Add(chkSaveVideo);
                root.Children.Add(chkSaveLossless);
                root.Children.Add(LabeledRow("Post-encode:", encodeCombo));
                root.Children.Add(chkReverse);
                root.Children.Add(LabeledRow("Adaptive iter cap:", iterCapCombo));
                root.Children.Add(smoothBox);
                root.Children.Add(chkUseRegionWatermark);
                root.Children.Add(chkThemeFade);
                root.Children.Add(themeFadeRow);
                root.Children.Add(errLabel);
                root.Children.Add(buttonRow);

                win.Content = root;
                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending); };

                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── MessageBox ───────────────────────────────────────────────────────

        public enum MessageResult { Ok, Yes, No, Cancelled }

        public static Task<MessageResult> ShowMessageAsync(
            string title,
            string body,
            bool expectsConfirmation)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<MessageResult>();

            // Helper that runs the dialog on the UI thread regardless of caller.
            void Run()
            {
                var win = BuildMessageWindow(title, body, expectsConfirmation, tcs);
                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        private static Window BuildMessageWindow(
            string title,
            string body,
            bool expectsConfirmation,
            TaskCompletionSource<MessageResult> tcs)
        {
            var win = new Window
            {
                Title = string.IsNullOrEmpty(title) ? "Message" : title,
                Width = 480,
                MinWidth = 320,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
                Background = Brushes.Black,
            };

            var bodyText = new TextBlock
            {
                Text = body ?? "",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 8),
            };

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 16, 16),
                Spacing = 8,
            };

            MessageResult pending = MessageResult.Cancelled;
            void Close(MessageResult r) { pending = r; win.Close(); }

            if (expectsConfirmation)
            {
                var yes = new Button { Content = "Yes", MinWidth = 80 };
                yes.Click += (_, _) => Close(MessageResult.Yes);
                var no = new Button { Content = "No", MinWidth = 80 };
                no.Click += (_, _) => Close(MessageResult.No);
                buttonRow.Children.Add(yes);
                buttonRow.Children.Add(no);
            }
            else
            {
                var ok = new Button { Content = "OK", MinWidth = 80 };
                ok.Click += (_, _) => Close(MessageResult.Ok);
                buttonRow.Children.Add(ok);
            }

            win.Closed += (_, _) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
            };

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(bodyText, 0);
            Grid.SetRow(buttonRow, 1);
            grid.Children.Add(bodyText);
            grid.Children.Add(buttonRow);

            win.Content = grid;
            return win;
        }

        // ── Text-name prompt ────────────────────────────────────────────────

        /// <summary>Async replacement for the retired WinForms PromptName. Opens
        /// a small modal Window with a single text box (OK / Cancel). Returns the
        /// trimmed text, or null when cancelled / left blank. Used by the
        /// source-editor VMs' Save-As flow (see #118).</summary>
        public static Task<string?> ShowPromptAsync(string title, string prompt, string defaultValue)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<string?>();

            void Run()
            {
                string? pending = null;

                var win = new Window
                {
                    Title = string.IsNullOrEmpty(title) ? "Enter Name" : title,
                    Width = 420,
                    MinWidth = 320,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                };

                var label = new TextBlock
                {
                    Text = string.IsNullOrEmpty(prompt) ? "Enter a name:" : prompt,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 16, 16, 4),
                };

                var box = new TextBox
                {
                    Text = defaultValue ?? string.Empty,
                    Margin = new Thickness(16, 4, 16, 8),
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 8, 16, 16),
                    Spacing = 8,
                };

                void Accept()
                {
                    var t = box.Text?.Trim();
                    pending = string.IsNullOrWhiteSpace(t) ? null : t;
                    win.Close();
                }

                var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                ok.Click += (_, _) => Accept();
                var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
                cancel.Click += (_, _) => { pending = null; win.Close(); };
                buttonRow.Children.Add(ok);
                buttonRow.Children.Add(cancel);

                // Enter in the text box accepts, Escape cancels (KeyUp — Avalonia
                // 12.0.4 swallows Escape KeyDown app-wide, see EscapeCloseBehavior).
                box.KeyUp += (_, e) =>
                {
                    if (e.Key == global::Avalonia.Input.Key.Enter) Accept();
                    else if (e.Key == global::Avalonia.Input.Key.Escape) { pending = null; win.Close(); }
                };

                win.Closed += (_, _) =>
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
                };
                win.Opened += (_, _) => { box.SelectAll(); box.Focus(); };

                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
                Grid.SetRow(label, 0);
                Grid.SetRow(box, 1);
                Grid.SetRow(buttonRow, 2);
                grid.Children.Add(label);
                grid.Children.Add(box);
                grid.Children.Add(buttonRow);

                win.Content = grid;
                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        /// <summary>Convenience yes/no confirm over <see cref="ShowMessageAsync"/>.
        /// Returns true only when the user picks Yes.</summary>
        public static async Task<bool> ConfirmAsync(string title, string message)
            => await ShowMessageAsync(title, message, expectsConfirmation: true) == MessageResult.Yes;

        // ── Save / Discard / Cancel prompt ──────────────────────────────────

        /// <summary>Three-button modal: Save / Discard / Cancel. Returns the
        /// picked button (or Cancelled if the user dismissed via the X).
        /// Used by the Color Theme Editor's unsaved-changes guard when the
        /// user picks a different theme or tries to close the window.</summary>
        public static Task<MessageResult> ShowSaveDiscardAsync(string title, string body)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<MessageResult>();

            void Run()
            {
                var win = new Window
                {
                    Title = string.IsNullOrEmpty(title) ? "Unsaved Changes" : title,
                    Width = 480,
                    MinWidth = 320,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                };

                var bodyText = new TextBlock
                {
                    Text = body ?? "",
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 16, 16, 8),
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 8, 16, 16),
                    Spacing = 8,
                };

                // "Save"  → Yes  (caller treats as: stay open, focus Name field
                //                  so the user can manually press the Save button)
                // "Discard" → No (caller treats as: drop edits, proceed)
                // "Cancel" → Cancelled (close prompt, no action)
                MessageResult pending = MessageResult.Cancelled;
                void Close(MessageResult r) { pending = r; win.Close(); }

                var save = new Button { Content = "Save", MinWidth = 80 };
                save.Click += (_, _) => Close(MessageResult.Yes);
                var discard = new Button { Content = "Discard", MinWidth = 80 };
                discard.Click += (_, _) => Close(MessageResult.No);
                var cancel = new Button { Content = "Cancel", MinWidth = 80 };
                cancel.Click += (_, _) => Close(MessageResult.Cancelled);
                buttonRow.Children.Add(save);
                buttonRow.Children.Add(discard);
                buttonRow.Children.Add(cancel);

                win.Closed += (_, _) =>
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
                };

                var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
                Grid.SetRow(bodyText, 0);
                Grid.SetRow(buttonRow, 1);
                grid.Children.Add(bodyText);
                grid.Children.Add(buttonRow);

                win.Content = grid;
                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Slideshow recording prompt ───────────────────────────────────────

        /// <summary>User decision after a recorded slideshow stops.</summary>
        public enum SlideshowRecordingChoice
        {
            /// <summary>Encode the PNG sequence to a video via ffmpeg.</summary>
            Convert,
            /// <summary>Keep the PNG sequence + move it under a user-picked folder.</summary>
            SaveFrames,
            /// <summary>Discard the temp folder.</summary>
            Cancel,
        }

        public static Task<SlideshowRecordingChoice> ShowSlideshowRecordingPromptAsync(
            int frameCount, int width, int height, string encodePreset)
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<SlideshowRecordingChoice>();

            void Run()
            {
                var win = new Window
                {
                    Title = "Slideshow Recorded",
                    Width = 520,
                    MinWidth = 360,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                    // FloatingMenu is a sibling child of MainWindow that
                    // typically sits in front of any modal opened against the
                    // owner; users reported the prompt was firing but
                    // invisible when stop came from the FloatingMenu Slideshow
                    // button or the VCR (only context-menu stop "worked"
                    // because the right-click menu closed before the prompt
                    // appeared, leaving the main window clear). Topmost +
                    // explicit Activate force the dialog above any non-modal
                    // child windows.
                    Topmost = true,
                };

                var bodyText = new TextBlock
                {
                    Text =
                        $"Captured {frameCount} frame{(frameCount == 1 ? "" : "s")} at {width}×{height}.\n\n" +
                        $"• Convert — run ffmpeg ({encodePreset}) and save a video file.\n" +
                        $"• Save Frames — pick a folder and keep the PNG sequence.\n" +
                        $"• Cancel — discard the captured frames.",
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 16, 16, 8),
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16, 8, 16, 16),
                    Spacing = 8,
                };

                SlideshowRecordingChoice pending = SlideshowRecordingChoice.Cancel;
                void Close(SlideshowRecordingChoice r) { pending = r; win.Close(); }

                var convert = new Button { Content = "Convert", MinWidth = 100 };
                convert.Click += (_, _) => Close(SlideshowRecordingChoice.Convert);
                var save = new Button { Content = "Save Frames", MinWidth = 110 };
                save.Click += (_, _) => Close(SlideshowRecordingChoice.SaveFrames);
                var cancel = new Button { Content = "Cancel", MinWidth = 90 };
                cancel.Click += (_, _) => Close(SlideshowRecordingChoice.Cancel);
                buttonRow.Children.Add(convert);
                buttonRow.Children.Add(save);
                buttonRow.Children.Add(cancel);

                win.Closed += (_, _) =>
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
                };

                var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
                Grid.SetRow(bodyText, 0);
                Grid.SetRow(buttonRow, 1);
                grid.Children.Add(bodyText);
                grid.Children.Add(buttonRow);

                win.Content = grid;
                // Foreground activation is handled centrally by WindowService.
                _ = WindowService.ShowDialogAsync(win, owner);
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        // ── Image-palette picker ─────────────────────────────────────────────

        /// <summary>
        /// Opens the standalone <c>PaletteBuilder.Views.MainWindow</c> in
        /// picker mode, bound to a <see cref="ImagePaletteViewModel"/>
        /// subclass backed by the supplied service. PaletteBuilder's
        /// MainWindow owns its own Browse / drag-drop / message dialogs,
        /// so this host wrapper only needs to bridge the two terminal
        /// events: <c>ResultAccepted</c> resolves the task with the chosen
        /// stops, anything else (Cancel, X-close) resolves with null.
        ///
        /// Replaces the previous <c>ImagePaletteView</c> dialog. Same
        /// public signature so call-sites in AvaloniaShellBootstrap stay
        /// untouched.
        /// </summary>
        public static async Task<IReadOnlyList<PaletteStop>?> ShowImagePalettePickerAsync(
            IPaletteExtractionService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            var win = new global::PaletteBuilder.Views.MainWindow(service, pickerMode: true);
            // MainWindow constructs its own PaletteBuilderViewModel (a
            // subclass of ImagePaletteViewModel) and assigns it as the
            // DataContext. The cast is safe by construction.
            var vm = (ImagePaletteViewModel)win.DataContext!;

            IReadOnlyList<PaletteStop>? accepted = null;
            var tcs = new TaskCompletionSource<bool>();

            vm.ResultAccepted += (_, stops) =>
            {
                accepted = stops;
                // MainWindow itself listens for ResultAccepted in picker
                // mode and closes the window; no Close() call needed here.
                // tcs resolves in win.Closed once the modal has fully torn
                // down so the caller cannot race the next dialog/picker.
            };

            // Cancel + X-close both resolve as "no palette" by leaving
            // accepted = null. The Closed handler reads accepted to decide
            // result so callers always resume after the modal has released.
            win.Closed += (_, _) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(accepted != null);
            };

            _ = WindowService.ShowDialogAsync(win, ActiveMainWindow);

            await tcs.Task;
            return accepted;
        }
    }
}
