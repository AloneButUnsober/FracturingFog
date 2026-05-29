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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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
                var widthTx = new TextBox { Text = "24", MinWidth = 70, PlaceholderText = "inches" };
                var heightTx = new TextBox { Text = "36", MinWidth = 70, PlaceholderText = "inches" };

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
