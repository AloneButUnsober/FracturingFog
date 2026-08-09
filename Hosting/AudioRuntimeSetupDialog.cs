// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/AudioRuntimeSetupDialog.cs
//
// #271 (parent #58) — Tier B fallback prompt for the OpenAL native runtime,
// modelled on the non-Windows branch of FfmpegSetupDialog. Shown when live
// audio (mic / system loopback) is requested on Linux/macOS but the OpenAL
// library cannot be loaded (bundled Silk.NET.OpenAL.Soft.Native missing AND no
// system package). No auto-install: installing a system library needs root, so
// we surface package-manager instructions + a rescan, exactly like the ffmpeg
// non-Win flow.
//
// Actions:
//   A. Rescan               — re-probe (OpenAlRuntime.Refresh). If the runtime
//                             is now present, persist Manual + resolve Installed.
//   B. Install manually…    — show the package-manager commands; persist Manual.
//   C. Continue without      — persist Skip; file + synth still work.
//
// Bundled runtime note: the app ships Silk.NET.OpenAL.Soft.Native per RID, so
// this dialog is only reached on hosts where even the bundled asset failed to
// load (missing sound server, unusual RID). The commands install a system
// OpenAL as a recovery path.

using System;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using FracturingFog.Audio;
using FracturingFog.Models;

namespace FracturingFog.Hosting
{
    public static class AudioRuntimeSetupDialog
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

                var statusBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                };
                UpdateStatus(statusBlock, OpenAlRuntime.IsAvailable());

                var explainBlock = new TextBlock
                {
                    Text =
                        "Live audio capture (microphone and, on Linux, system loopback) " +
                        "uses the OpenAL runtime. It ships bundled with the app, but could " +
                        "not be loaded on this host — usually a missing sound server or an " +
                        "unusual platform. Install a system OpenAL below, then rescan. " +
                        "File playback and the fractal synth work without it.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 0, 0, 10),
                };

                var noticeBlock = new TextBlock
                {
                    Text =
                        "Suggested install commands:\n" +
                        "  Ubuntu / Debian:  sudo apt install libopenal1\n" +
                        "  Fedora:           sudo dnf install openal-soft\n" +
                        "  Arch:             sudo pacman -S openal\n" +
                        "  macOS:            OpenAL ships with the OS (no install needed)",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 110)),
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 12),
                };

                var btnRescan = new Button
                {
                    Content = "Rescan",
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
                    Content = "Continue Without Live Audio",
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

                var manualPanel = new StackPanel { Orientation = Orientation.Vertical, IsVisible = false };
                manualPanel.Children.Add(new TextBlock
                {
                    Text =
                        "To enable live audio manually:\n" +
                        "  • Ubuntu / Debian:  sudo apt install libopenal1\n" +
                        "  • Fedora:           sudo dnf install openal-soft\n" +
                        "  • Arch:             sudo pacman -S openal\n" +
                        "\n" +
                        "After it installs, click 'Rescan' above (or restart the app) and " +
                        "the Microphone / System Loopback sources will enable automatically.",
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

                var actionPanel = new StackPanel { Orientation = Orientation.Vertical };
                actionPanel.Children.Add(btnRescan);
                actionPanel.Children.Add(btnManual);
                actionPanel.Children.Add(btnSkip);

                var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
                body.Children.Add(statusBlock);
                body.Children.Add(explainBlock);
                body.Children.Add(noticeBlock);
                body.Children.Add(actionPanel);
                body.Children.Add(manualPanel);
                body.Children.Add(btnClose);

                var win = new Window
                {
                    Title = "Live Audio Setup",
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

                // Escape closes on KeyUp — Avalonia 12.0.4 swallows Escape KeyDown
                // app-wide (see EscapeCloseBehavior); tunnel KeyUp is the reliable seam.
                win.AddHandler(
                    global::Avalonia.Input.InputElement.KeyUpEvent,
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

                btnRescan.Click += (_, _) =>
                {
                    bool nowAvailable = OpenAlRuntime.Refresh();
                    UpdateStatus(statusBlock, nowAvailable);
                    if (nowAvailable)
                    {
                        AudioRuntimePreferences.Instance.Election = AudioRuntimeElection.Manual;
                        AudioRuntimePreferences.Instance.Save();
                        pending = Result.Installed;
                        win.Close();
                    }
                };

                btnManual.Click += (_, _) =>
                {
                    actionPanel.IsVisible = false;
                    manualPanel.IsVisible = true;
                };

                btnManualOk.Click += (_, _) =>
                {
                    AudioRuntimePreferences.Instance.Election = AudioRuntimeElection.Manual;
                    AudioRuntimePreferences.Instance.Save();
                    pending = Result.ManualChosen;
                    win.Close();
                };

                btnSkip.Click += (_, _) =>
                {
                    AudioRuntimePreferences.Instance.Election = AudioRuntimeElection.Skip;
                    AudioRuntimePreferences.Instance.Save();
                    pending = Result.SkipChosen;
                    win.Close();
                };

                btnClose.Click += (_, _) => win.Close();

                win.Closed += (_, _) =>
                {
                    if (!tcs.Task.IsCompleted) tcs.TrySetResult(pending);
                };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);

            return tcs.Task;
        }

        private static void UpdateStatus(TextBlock block, bool available)
        {
            if (available)
            {
                block.Text = "Current status: Live audio runtime available";
                block.Foreground = new SolidColorBrush(Color.FromRgb(140, 200, 140));
            }
            else
            {
                block.Text = "Current status: Live audio runtime not available";
                // Yellow, not red — red/green is indistinguishable for some users.
                block.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            }
        }
    }
}
