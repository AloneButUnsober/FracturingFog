// Views/UrlFetchDialog.axaml.cs
//
// Two-step modal:
//   1. User enters URL + clicks Fetch. ImageUrlLoader runs the full
//      validation pipeline and returns either bytes + metadata or an
//      error.
//   2. On success the Load button activates and the host/content-type/
//      size triple is shown. User must click Load to accept — extra
//      confirmation step is the human-in-the-loop guard against
//      accidental loads from typoed or stale URLs.

using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PaletteBuilder.Services;

namespace PaletteBuilder.Views
{
    public partial class UrlFetchDialog : Window
    {
        private readonly ImageUrlLoader _loader = new();
        private CancellationTokenSource? _cts;
        private byte[]? _pendingBytes;
        private string? _pendingFilename;

        public string? ResolvedTempPath { get; private set; }

        public UrlFetchDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private async void OnFetchClick(object? sender, RoutedEventArgs e)
        {
            var url = (this.FindControl<TextBox>("UrlBox")?.Text ?? "").Trim();
            if (url.Length == 0)
            {
                SetStatus("Enter an HTTPS URL.", error: true);
                return;
            }

            var fetchButton = this.FindControl<Button>("FetchButton")!;
            var loadButton = this.FindControl<Button>("LoadButton")!;
            var confirmPanel = this.FindControl<Border>("ConfirmPanel")!;

            fetchButton.IsEnabled = false;
            loadButton.IsEnabled = false;
            confirmPanel.IsVisible = false;
            SetStatus("Fetching…", error: false);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            try
            {
                var result = await _loader.TryFetchAsync(url, _cts.Token);
                if (result.Error != null)
                {
                    SetStatus(result.Error, error: true);
                    return;
                }

                _pendingBytes = result.Bytes;
                _pendingFilename = result.Filename;

                this.FindControl<TextBlock>("HostText")!.Text = result.Host ?? "(unknown)";
                this.FindControl<TextBlock>("ContentTypeText")!.Text = result.ContentType ?? "(unknown)";
                this.FindControl<TextBlock>("SizeText")!.Text = FormatBytes(result.Size);
                confirmPanel.IsVisible = true;
                loadButton.IsEnabled = true;
                SetStatus("Review and click Load to use this image.", error: false);
            }
            catch (Exception ex)
            {
                SetStatus("Fetch failed: " + ex.Message, error: true);
            }
            finally
            {
                fetchButton.IsEnabled = true;
            }
        }

        private void OnLoadClick(object? sender, RoutedEventArgs e)
        {
            if (_pendingBytes == null || _pendingFilename == null)
            {
                SetStatus("No fetched image to load.", error: true);
                return;
            }
            try
            {
                var path = Path.Combine(Path.GetTempPath(), _pendingFilename);
                File.WriteAllBytes(path, _pendingBytes);
                ResolvedTempPath = path;
                Close(true);
            }
            catch (Exception ex)
            {
                SetStatus("Failed to write temp file: " + ex.Message, error: true);
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Close(false);
        }

        private void SetStatus(string text, bool error)
        {
            var st = this.FindControl<TextBlock>("StatusText");
            if (st == null) return;
            st.Text = text;
            st.Foreground = error
                ? Avalonia.Media.Brushes.IndianRed
                : Avalonia.Media.Brushes.LightGray;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }
    }
}
