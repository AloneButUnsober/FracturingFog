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
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views;

namespace FracturingFog.Hosting
{
    internal static class AvaloniaDialogs
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

        /// <summary>
        /// Opens the Avalonia <see cref="SlideshowSettingsView"/> bound to a
        /// new <see cref="SlideshowSettingsViewModel"/> seeded from
        /// <paramref name="current"/> / <paramref name="audioReactive"/>.
        /// Returns the chosen settings + audio-reactive flag on OK, null on
        /// Cancel.
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
                var win = new SlideshowSettingsView { DataContext = vm };
                // Audio… button — open the audio-reactive settings dialog as a
                // nested modal owned by this window. Without this wiring the
                // button raised its event into the void (the dialog never showed).
                vm.ShowAudioDialogRequested += (_, _) => _ = ShowAudioSettingsAsync(win);
                win.Closed += (_, _) =>
                {
                    if (tcs.Task.IsCompleted) return;
                    if (vm.Result != null)
                        tcs.TrySetResult((vm.Result, vm.AudioReactiveResult));
                    else
                        tcs.TrySetResult(null);
                };
                var owner = ActiveMainWindow;
                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
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
        public static Task ShowAudioSettingsAsync(Window? owner)
        {
            var tcs = new TaskCompletionSource<bool>();

            void Run()
            {
                var current = AudioSettingsStore.Load();
                var vm = new AudioSettingsViewModel(current, liveSource: null);
                var win = new AudioSettingsView { DataContext = vm };

                // Browse… → Avalonia open-file picker; push the chosen path back.
                vm.BrowseFileRequested += async (_, _) =>
                {
                    var path = await PickOpenFileAsync(
                        "Choose Audio File",
                        "Audio (*.mp3;*.wav;*.flac;*.ogg)|*.mp3;*.wav;*.flac;*.ogg|All files (*.*)|*.*");
                    if (!string.IsNullOrEmpty(path)) vm.FilePath = path!;
                };

                // OK commits vm.Result; persist it. Cancel raises false → no save.
                vm.CloseRequested += (_, ok) =>
                {
                    if (ok)
                    {
                        try { AudioSettingsStore.Save(vm.Result); } catch { }
                    }
                };

                win.Closed += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(true); };

                if (owner != null) _ = win.ShowDialog(owner);
                else if (ActiveMainWindow != null) _ = win.ShowDialog(ActiveMainWindow);
                else win.Show();
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
                    Watermark = prompt,
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
                void Close(string? r)
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(r);
                    win.Close();
                }
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
                win.Closing += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(null); };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
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
        /// Portrait) on OK, null on cancel. Portrait also drives the 90° rotate.
        /// </summary>
        public static Task<(int Width, int Height, bool Portrait)?> ShowPosterAsync()
        {
            var owner = ActiveMainWindow;
            var tcs = new TaskCompletionSource<(int, int, bool)?>();

            void Run()
            {
                var widthTx = new TextBox { Text = "24", MinWidth = 70, Watermark = "inches" };
                var heightTx = new TextBox { Text = "36", MinWidth = 70, Watermark = "inches" };

                var portrait = new CheckBox { Content = "Portrait orientation", IsChecked = true, Foreground = Brushes.White };

                var lowDpi = new RadioButton { Content = "Low (150 DPI)", GroupName = "dpi", Foreground = Brushes.White };
                var medDpi = new RadioButton { Content = "Med (300 DPI)", GroupName = "dpi", IsChecked = true, Foreground = Brushes.White };
                var highDpi = new RadioButton { Content = "High (600 DPI)", GroupName = "dpi", Foreground = Brushes.White };

                var pixelLabel = new TextBlock
                {
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };

                int Dpi() => lowDpi.IsChecked == true ? 150 : highDpi.IsChecked == true ? 600 : 300;
                (int w, int h) Pixels()
                {
                    int.TryParse(widthTx.Text, out int wi);
                    int.TryParse(heightTx.Text, out int hi);
                    if (wi < 0) wi = 0;
                    if (hi < 0) hi = 0;
                    int dpi = Dpi();
                    return (wi * dpi, hi * dpi);
                }
                void Refresh()
                {
                    var (pw, ph) = Pixels();
                    pixelLabel.Text = $"Output: {pw:N0} × {ph:N0} px  ({(long)pw * ph / 1_000_000:N0} MP)";
                }
                widthTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
                heightTx.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refresh(); };
                lowDpi.IsCheckedChanged += (_, _) => Refresh();
                medDpi.IsCheckedChanged += (_, _) => Refresh();
                highDpi.IsCheckedChanged += (_, _) => Refresh();
                Refresh();

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

                static TextBlock Lbl(string t) => new()
                {
                    Text = t,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var grid = new Grid
                {
                    Margin = new Thickness(16),
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
                };
                void Place(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }

                var wLbl = Lbl("Poster width (in):");
                var hLbl = Lbl("Poster height (in):");
                wLbl.Margin = new Thickness(0, 0, 8, 6);
                hLbl.Margin = new Thickness(0, 0, 8, 6);
                widthTx.Margin = new Thickness(0, 0, 0, 6);
                heightTx.Margin = new Thickness(0, 0, 0, 6);
                Place(wLbl, 0, 0); Place(widthTx, 0, 1);
                Place(hLbl, 1, 0); Place(heightTx, 1, 1);

                var dpiRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 4) };
                dpiRow.Children.Add(lowDpi);
                dpiRow.Children.Add(medDpi);
                dpiRow.Children.Add(highDpi);
                Grid.SetRow(dpiRow, 2); Grid.SetColumn(dpiRow, 0); Grid.SetColumnSpan(dpiRow, 2);
                grid.Children.Add(dpiRow);

                Grid.SetRow(portrait, 3); Grid.SetColumn(portrait, 0); Grid.SetColumnSpan(portrait, 2);
                grid.Children.Add(portrait);

                Grid.SetRow(pixelLabel, 4); Grid.SetColumn(pixelLabel, 0); Grid.SetColumnSpan(pixelLabel, 2);
                grid.Children.Add(pixelLabel);

                var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
                var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
                void Close((int, int, bool)? r) { if (!tcs.Task.IsCompleted) tcs.TrySetResult(r); win.Close(); }
                ok.Click += (_, _) =>
                {
                    var (pw, ph) = Pixels();
                    if (pw <= 0 || ph <= 0)
                    {
                        pixelLabel.Foreground = Brushes.OrangeRed;
                        pixelLabel.Text = "Enter positive width and height in inches.";
                        return;
                    }
                    Close((pw, ph, portrait.IsChecked == true));
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
                Grid.SetRow(buttonRow, 5); Grid.SetColumn(buttonRow, 0); Grid.SetColumnSpan(buttonRow, 2);
                grid.Children.Add(buttonRow);

                win.Content = grid;
                win.Closing += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(null); };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
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
                bool ffmpegHere = global::FracturingFog.FfmpegEncoder.IsAvailable();

                // Cached QD limbs for the picked region (folded into the parsed
                // textbox value when the textbox carries only the Hi limb).
                double targetCXLo = 0, targetCX2 = 0, targetCX3 = 0;
                double targetCYLo = 0, targetCY2 = 0, targetCY3 = 0;
                int targetIterations = 0;
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
                    Text = global::FracturingFog.Views.FormHelpers.FormatCoordSingle(currentCX, 0, 0, 0),
                    FontFamily = new FontFamily("Consolas"),
                };
                var txCY = new TextBox
                {
                    Text = global::FracturingFog.Views.FormHelpers.FormatCoordSingle(currentCY, 0, 0, 0),
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

                    txCX.Text = global::FracturingFog.Views.FormHelpers.FormatCoordSingle(
                        region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    txCY.Text = global::FracturingFog.Views.FormHelpers.FormatCoordSingle(
                        region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);

                    double z = region.Zoom;
                    if (z > ultraCap) z = ultraCap;
                    txZoom.Text = z.ToString("G6", ic);
                    targetIterations = region.Iterations;
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
                    bool okCX = global::FracturingFog.Views.FormHelpers.TryParseCoordAny(
                        txCX.Text ?? "", out cxHi, out cxLo, out cx2, out cx3);
                    bool okCY = global::FracturingFog.Views.FormHelpers.TryParseCoordAny(
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

                void Close(global::FracturingFog.Render.VideoZoomRequest? r)
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(r);
                    win.Close();
                }

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
                        Seconds = seconds,
                        IsSlideshow = false,
                        IsReverse = chkReverse.IsChecked == true,
                        IsSaveVideo = chkSaveVideo.IsChecked == true,
                        IsSaveLossless = chkSaveLossless.IsChecked == true,
                        LosslessEncode = MapEncode(),
                        TaaSmoothing = (int)Math.Round(taaSlider.Value),
                        BandDither = chkBandDither.IsChecked == true,
                        BandDitherStrength = (int)Math.Round(ditherSlider.Value),
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
                root.Children.Add(smoothBox);
                root.Children.Add(errLabel);
                root.Children.Add(buttonRow);

                win.Content = root;
                win.Closing += (_, _) => { if (!tcs.Task.IsCompleted) tcs.TrySetResult(null); };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
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
                if (owner != null)
                    _ = win.ShowDialog(owner);
                else
                    win.Show();
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

            void Close(MessageResult r)
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(r);
                win.Close();
            }

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

            win.Closing += (_, _) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(MessageResult.Cancelled);
            };

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(bodyText, 0);
            Grid.SetRow(buttonRow, 1);
            grid.Children.Add(bodyText);
            grid.Children.Add(buttonRow);

            win.Content = grid;
            return win;
        }

        // ── Image-palette picker ─────────────────────────────────────────────

        /// <summary>
        /// Opens the Avalonia <see cref="ImagePaletteView"/> bound to a
        /// <see cref="ImagePaletteViewModel"/> backed by the supplied service.
        /// Wires Browse / Drop / Apply / Cancel / Message events. Resolves
        /// to the chosen stops on Apply, or null on Cancel / close.
        /// </summary>
        public static async Task<IReadOnlyList<PaletteStop>?> ShowImagePalettePickerAsync(
            IPaletteExtractionService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            var vm = new ImagePaletteViewModel(service);
            var win = new ImagePaletteView { DataContext = vm };

            IReadOnlyList<PaletteStop>? accepted = null;
            var tcs = new TaskCompletionSource<bool>();

            vm.BrowseRequested += async (_, _) =>
            {
                try
                {
                    string? picked = await PickImageFileAsync(win);
                    if (!string.IsNullOrEmpty(picked))
                        TryLoadIntoVm(vm, picked);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaDialogs] Browse failed: {ex.Message}");
                }
            };

            vm.MessageRequested += async (_, msg) =>
            {
                try { await ShowMessageAsync("Palette", msg, expectsConfirmation: false); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaDialogs] Palette message failed: {ex.Message}");
                }
            };

            vm.ResultAccepted += (_, stops) =>
            {
                accepted = stops;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            };

            vm.Cancelled += (_, _) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            };

            win.FileDropped += (_, path) => TryLoadIntoVm(vm, path);

            win.Closing += (_, _) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            };

            var owner = ActiveMainWindow;
            if (owner != null) _ = win.ShowDialog(owner);
            else win.Show();

            await tcs.Task;
            return accepted;
        }

        private static async Task<string?> PickImageFileAsync(Window owner)
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top == null) return null;
            var opts = new FilePickerOpenOptions
            {
                Title = "Choose Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" },
                    },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                },
            };
            var files = await top.StorageProvider.OpenFilePickerAsync(opts);
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        private static void TryLoadIntoVm(ImagePaletteViewModel vm, string path)
        {
            Bitmap? preview = null;
            try { preview = new Bitmap(path); }
            catch { /* fall through — service will surface the real error */ }
            vm.SetImage(path, preview);
        }
    }
}
