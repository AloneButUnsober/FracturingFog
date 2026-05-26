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
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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
    }
}
