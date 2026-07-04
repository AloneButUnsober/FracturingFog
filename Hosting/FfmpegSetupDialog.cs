// Hosting/FfmpegSetupDialog.cs
//
// Avalonia modal for the first-run FFmpeg install offer + the
// FloatingMenu "FFmpeg…" Update flow. Three primary actions:
//
//   A. Download FFmpeg now    — runs FfmpegInstaller against the BtbN
//                               GPL build, verifies SHA-256, drops the
//                               binary into Tools\ffmpeg.exe.
//   B. Install manually       — shows brief instructions and dismisses;
//                               persists FfmpegUserElection.Manual so the
//                               startup prompt is suppressed next launch.
//   C. Continue without video — persists FfmpegUserElection.Skip; callers
//                               then gate the Save Lossless UI.
//
// All three close the modal. The startup hook treats Installed +
// SkipChosen + ManualChosen as "do not re-prompt"; Cancelled / closed-X
// re-prompts next launch (mirrors the spec: only A/B/C explicit picks
// suppress).

using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using FracturingFog.Imaging;
using FracturingFog.Models;

namespace FracturingFog.Hosting
{
    // Wave 1.C1 — flipped internal → public when the file moved into the
    // cross-platform FracturingFog.Hosting assembly; both shell hosts
    // (FracturingFog WinExe + FracturingFog.App) consume it across the
    // assembly boundary.
    public static class FfmpegSetupDialog
    {
        public enum Result
        {
            Installed,
            ManualChosen,
            SkipChosen,
            Cancelled,
        }

        public static Task<Result> ShowAsync(Window? owner)
        {
            var tcs = new TaskCompletionSource<Result>();

            void Run()
            {
                Result pending = Result.Cancelled;

                // Phase X.2 / Slice 2.5 — auto-download targets a Windows
                // `ffmpeg.exe` from BtbN/FFmpeg-Builds, so the Win flow keeps
                // its existing UX. Linux/macOS hosts install via apt / brew
                // and only need a rescan button to re-detect a freshly
                // installed binary on PATH; both copy blocks branch on this.
                bool isWindows = OperatingSystem.IsWindows();

                bool isInstalled = FfmpegInstaller.IsInstalled();
                string? installedVersion = FfmpegInstaller.TryReadInstalledVersion();

                var statusBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                UpdateStatus(statusBlock, isInstalled, installedVersion);

                var explainBlock = new TextBlock
                {
                    Text = isWindows
                        ? "ffmpeg.exe is used to encode video (Lossless H.264 / FFV1 / " +
                          "visually-lossless MP4) from the rendered PNG frame sequence. " +
                          "It is GPL-licensed and not bundled with this app. You can let " +
                          "FracturingFog download a current GPL build from BtbN's GitHub " +
                          "releases, install one yourself, or skip video saving entirely."
                        : "ffmpeg is used to encode video (Lossless H.264 / FFV1 / " +
                          "visually-lossless MP4) from the rendered PNG frame sequence. " +
                          "On Linux / macOS install it via your package manager and " +
                          "FracturingFog will pick it up off PATH automatically.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 0, 0, 10),
                };

                var noticeBlock = new TextBlock
                {
                    Text = isWindows
                        ? "Download source: github.com/BtbN/FFmpeg-Builds (GPL build). " +
                          "The download is verified against the SHA-256 digest published " +
                          "by GitHub for the release asset before it is extracted."
                        : "Suggested install commands:\n" +
                          "  Ubuntu / Debian:  sudo apt install ffmpeg\n" +
                          "  Fedora:           sudo dnf install ffmpeg\n" +
                          "  Arch:             sudo pacman -S ffmpeg\n" +
                          "  macOS (Homebrew): brew install ffmpeg",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 110)),
                    FontStyle = FontStyle.Italic,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 12),
                };

                var btnDownload = new Button
                {
                    Content = isWindows
                        ? (isInstalled ? "Download Latest (Update)" : "Download FFmpeg Now")
                        : "Rescan PATH",
                    MinWidth = 220,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var btnManual = new Button
                {
                    Content = "Install Manually…",
                    MinWidth = 220,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var btnSkip = new Button
                {
                    Content = "Continue Without Video Save",
                    MinWidth = 220,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

                var btnClose = new Button
                {
                    Content = "Close",
                    MinWidth = 90,
                    IsCancel = true,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };

                // ── Progress band (hidden until Download is clicked) ─────
                var progressLabel = new TextBlock
                {
                    Foreground = Brushes.LightGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 4),
                };
                var progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1.0,
                    Height = 14,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                var btnCancel = new Button
                {
                    Content = "Cancel",
                    MinWidth = 90,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                var progressPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    IsVisible = false,
                };
                progressPanel.Children.Add(progressLabel);
                progressPanel.Children.Add(progressBar);
                progressPanel.Children.Add(btnCancel);

                // ── Manual instructions panel (hidden until clicked) ─────
                var manualPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    IsVisible = false,
                };
                manualPanel.Children.Add(new TextBlock
                {
                    Text = isWindows
                        ? "To install ffmpeg.exe manually:\n" +
                          "  1. Download a Windows GPL build (recommended:\n" +
                          "     https://github.com/BtbN/FFmpeg-Builds/releases ).\n" +
                          "  2. Extract bin\\ffmpeg.exe from the archive.\n" +
                          "  3. Copy it into the Tools folder next to FracturingFog.exe:\n" +
                          $"        {FfmpegInstaller.TargetPath}\n" +
                          "  4. Close and re-open FracturingFog. Video controls will\n" +
                          "     enable automatically once the binary is detected."
                        : "To install ffmpeg manually:\n" +
                          "  • Ubuntu / Debian:  sudo apt install ffmpeg\n" +
                          "  • Fedora:           sudo dnf install ffmpeg\n" +
                          "  • Arch:             sudo pacman -S ffmpeg\n" +
                          "  • macOS (Homebrew): brew install ffmpeg\n" +
                          "\n" +
                          "After the install completes, click 'Rescan PATH' in the\n" +
                          "main dialog (or restart FracturingFog) and video controls\n" +
                          "will enable automatically.",
                    Foreground = Brushes.LightGray,
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 8),
                });
                var btnManualOk = new Button
                {
                    Content = "Got it — Close",
                    MinWidth = 140,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                manualPanel.Children.Add(btnManualOk);

                // ── Container ────────────────────────────────────────────
                var actionPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                };
                actionPanel.Children.Add(btnDownload);
                actionPanel.Children.Add(btnManual);
                actionPanel.Children.Add(btnSkip);

                var body = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(16),
                };
                body.Children.Add(statusBlock);
                body.Children.Add(explainBlock);
                body.Children.Add(noticeBlock);
                body.Children.Add(actionPanel);
                body.Children.Add(progressPanel);
                body.Children.Add(manualPanel);
                body.Children.Add(btnClose);

                var win = new Window
                {
                    Title = "FFmpeg Setup",
                    Width = 540,
                    MinWidth = 440,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                    Content = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = body,
                    },
                };
                win.AddHandler(
                    global::Avalonia.Input.InputElement.KeyDownEvent,
                    (_, ke) =>
                    {
                        if (ke.Handled) return;
                        if (ke.Key != global::Avalonia.Input.Key.Escape) return;
                        if (ke.KeyModifiers != global::Avalonia.Input.KeyModifiers.None) return;
                        ke.Handled = true;
                        win.Close();
                    },
                    global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
                    handledEventsToo: false);

                CancellationTokenSource? cts = null;

                btnDownload.Click += async (_, _) =>
                {
                    // Non-Win path: rescan PATH (and the per-RID Tools probe)
                    // for a freshly installed ffmpeg binary. No download
                    // pipeline; FfmpegInstaller is Windows-only by construction.
                    if (!isWindows)
                    {
                        bool nowInstalled = FfmpegInstaller.IsInstalled();
                        string? nowVersion = FfmpegInstaller.TryReadInstalledVersion();
                        UpdateStatus(statusBlock, nowInstalled, nowVersion);
                        if (nowInstalled)
                        {
                            FfmpegPreferences.Instance.Election = FfmpegUserElection.Manual;
                            FfmpegPreferences.Instance.LastInstalledVersion = nowVersion;
                            FfmpegPreferences.Instance.LastInstalledUtc = DateTime.UtcNow;
                            FfmpegPreferences.Instance.Save();
                            pending = Result.Installed;
                            win.Close();
                        }
                        return;
                    }

                    actionPanel.IsVisible = false;
                    manualPanel.IsVisible = false;
                    progressPanel.IsVisible = true;
                    btnClose.IsVisible = false;
                    progressLabel.Text = "Starting…";
                    progressBar.Value = 0;

                    cts = new CancellationTokenSource();
                    var progress = new Progress<InstallProgress>(p =>
                    {
                        if (p.Fraction >= 0)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = Math.Clamp(p.Fraction, 0, 1);
                        }
                        else
                        {
                            progressBar.IsIndeterminate = true;
                        }
                        progressLabel.Text = p.Message ?? p.Phase.ToString();
                    });

                    InstallResult result;
                    try
                    {
                        result = await FfmpegInstaller.InstallAsync(progress, cts.Token)
                            .ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        result = new InstallResult
                        {
                            Outcome = InstallOutcome.DownloadFailed,
                            ErrorDetail = $"Unexpected error: {ex.Message}",
                        };
                    }

                    btnCancel.IsVisible = false;
                    btnClose.IsVisible = true;
                    progressBar.IsIndeterminate = false;

                    HandleInstallResult(
                        result,
                        statusBlock,
                        progressLabel,
                        progressBar,
                        actionPanel,
                        out bool succeeded);

                    if (succeeded)
                    {
                        FfmpegPreferences.Instance.Election = FfmpegUserElection.AutoDownload;
                        FfmpegPreferences.Instance.LastInstalledVersion = result.NewVersion;
                        FfmpegPreferences.Instance.LastInstalledUtc = DateTime.UtcNow;
                        FfmpegPreferences.Instance.Save();
                        pending = Result.Installed;
                    }
                    // Failed install: leave the dialog up so the user can read
                    // the error and re-try or pick a different option. Don't
                    // change pending — Close still resolves as Cancelled.
                };

                btnCancel.Click += (_, _) =>
                {
                    try { cts?.Cancel(); } catch { }
                };

                btnManual.Click += (_, _) =>
                {
                    actionPanel.IsVisible = false;
                    progressPanel.IsVisible = false;
                    manualPanel.IsVisible = true;
                };

                btnManualOk.Click += (_, _) =>
                {
                    FfmpegPreferences.Instance.Election = FfmpegUserElection.Manual;
                    FfmpegPreferences.Instance.Save();
                    pending = Result.ManualChosen;
                    win.Close();
                };

                btnSkip.Click += (_, _) =>
                {
                    FfmpegPreferences.Instance.Election = FfmpegUserElection.Skip;
                    FfmpegPreferences.Instance.Save();
                    pending = Result.SkipChosen;
                    win.Close();
                };

                btnClose.Click += (_, _) => win.Close();

                win.Closed += (_, _) =>
                {
                    try { cts?.Cancel(); } catch { }
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
                };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        private static void UpdateStatus(TextBlock block, bool isInstalled, string? version)
        {
            if (isInstalled && !string.IsNullOrWhiteSpace(version))
            {
                block.Text = $"Current status: Installed\n  {version}";
                block.Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 140));
            }
            else if (isInstalled)
            {
                block.Text = "Current status: Installed (version unknown)";
                block.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 100));
            }
            else
            {
                block.Text = "Current status: Not installed";
                block.Foreground = new SolidColorBrush(Color.FromRgb(220, 130, 130));
            }
        }

        private static void HandleInstallResult(
            InstallResult result,
            TextBlock statusBlock,
            TextBlock progressLabel,
            ProgressBar progressBar,
            StackPanel actionPanel,
            out bool succeeded)
        {
            succeeded = false;

            switch (result.Outcome)
            {
                case InstallOutcome.Installed:
                    succeeded = true;
                    progressBar.Value = 1.0;
                    progressLabel.Text =
                        "FFmpeg installed successfully.\n" +
                        $"Version: {result.NewVersion ?? "(unknown)"}";
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 140));
                    UpdateStatus(statusBlock, true, result.NewVersion);
                    break;

                case InstallOutcome.UpdatedFromOlder:
                    succeeded = true;
                    progressBar.Value = 1.0;
                    progressLabel.Text =
                        "FFmpeg updated to a newer version.\n" +
                        $"Previous: {result.PreviousVersion ?? "(unknown)"}\n" +
                        $"New:      {result.NewVersion ?? "(unknown)"}";
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 140));
                    UpdateStatus(statusBlock, true, result.NewVersion);
                    break;

                case InstallOutcome.SkippedNotNewer:
                    progressBar.Value = 1.0;
                    progressLabel.Text =
                        "Downloaded build matches the currently installed version. " +
                        "Nothing to update.";
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 100));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.RejectedOlder:
                    progressBar.Value = 0;
                    progressLabel.Text =
                        "Refused to install: the downloaded build is older than the " +
                        "currently installed ffmpeg.exe.\n" +
                        $"Existing: {result.PreviousVersion}\n" +
                        $"Download: {result.NewVersion}";
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 130, 130));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.HashMismatch:
                    progressBar.Value = 0;
                    progressLabel.Text =
                        "SECURITY WARNING — SHA-256 hash mismatch. " +
                        "The downloaded archive does not match the digest published " +
                        "by GitHub. The download has been discarded and ffmpeg.exe " +
                        "was NOT modified.\n\n" + (result.ErrorDetail ?? "");
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 130, 130));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.HashUnavailable:
                    progressBar.Value = 0;
                    progressLabel.Text =
                        "WARNING — GitHub did not publish a SHA-256 digest for the " +
                        "latest release asset, so the download could not be " +
                        "cryptographically verified. The download has been discarded " +
                        "and ffmpeg.exe was NOT modified. Retry later, or install " +
                        "manually.\n\n" + (result.ErrorDetail ?? "");
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 170, 100));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.DownloadFailed:
                    progressBar.Value = 0;
                    progressLabel.Text =
                        "Download failed.\n" + (result.ErrorDetail ?? "(no detail)");
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 130, 130));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.ExtractFailed:
                    progressBar.Value = 0;
                    progressLabel.Text =
                        "Extraction failed.\n" + (result.ErrorDetail ?? "(no detail)");
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 130, 130));
                    actionPanel.IsVisible = true;
                    break;

                case InstallOutcome.Cancelled:
                    progressBar.Value = 0;
                    progressLabel.Text = "Cancelled.";
                    progressLabel.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 100));
                    actionPanel.IsVisible = true;
                    break;
            }
        }
    }
}
