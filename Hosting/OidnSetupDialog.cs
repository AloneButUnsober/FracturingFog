// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/OidnSetupDialog.cs
//
// #1338 — the Intel Open Image Denoise setup dialog (Control Center → Advanced →
// "Denoiser (Intel OIDN)…"): shows whether the runtime is installed, and installs
// it on request through OidnInstaller (the official RenderKit release, pinned and
// SHA-256 checked, into the user data folder). Modelled on AudioRuntimeSetupDialog.

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

namespace FracturingFog.Hosting
{
    public static class OidnSetupDialog
    {
        public static Task ShowAsync(Window? owner)
        {
            var tcs = new TaskCompletionSource();

            void Run()
            {
                var statusBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                UpdateStatus(statusBlock);

                var asset = OidnInstaller.AssetForThisPlatform();
                var explainBlock = new TextBlock
                {
                    Text =
                        "Intel Open Image Denoise is a learned denoiser (the one Blender uses). With " +
                        "Lighting & FX → Denoise (Intel OIDN) on, it cleans the finished 3D and relief " +
                        "frames — best on sample noise such as thin-lens depth of field with few lens " +
                        "samples; it also softens fine fractal colour detail. It runs on the CPU " +
                        "(about a second for a 1080p frame).\n\n" +
                        "It is not bundled: Install downloads the official release " +
                        $"(Open Image Denoise {OidnDenoiser.Version}, Apache-2.0, " +
                        (asset != null ? $"{asset.FileName}, about 50-65 MB" : "no build for this platform") +
                        ") from github.com/RenderKit/oidn, checks its SHA-256, and keeps only the CPU " +
                        "runtime and its licence files in your data folder.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                var progressBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.LightGray,
                    Margin = new Thickness(0, 0, 0, 8),
                    IsVisible = false,
                };

                var btnInstall = new Button
                {
                    Content = OidnDenoiser.IsAvailable ? "Reinstall" : "Install",
                    MinWidth = 220,
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsEnabled = asset != null,
                };
                var btnRescan = new Button
                {
                    Content = "Rescan",
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

                var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
                body.Children.Add(statusBlock);
                body.Children.Add(explainBlock);
                body.Children.Add(progressBlock);
                body.Children.Add(btnInstall);
                body.Children.Add(btnRescan);
                body.Children.Add(btnClose);

                var win = new Window
                {
                    Title = "Denoiser (Intel Open Image Denoise)",
                    Width = 540,
                    MinWidth = 440,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    ShowInTaskbar = false,
                    Background = Brushes.Black,
                    Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body },
                };

                // Escape closes on KeyUp (Avalonia swallows Escape KeyDown; see EscapeCloseBehavior).
                win.AddHandler(
                    global::Avalonia.Input.InputElement.KeyUpEvent,
                    (_, ke) =>
                    {
                        if (ke.Handled || ke.Key != global::Avalonia.Input.Key.Escape
                            || ke.KeyModifiers != global::Avalonia.Input.KeyModifiers.None) return;
                        ke.Handled = true;
                        win.Close();
                    },
                    global::Avalonia.Interactivity.RoutingStrategies.Tunnel,
                    handledEventsToo: false);

                var cts = new CancellationTokenSource();
                btnInstall.Click += async (_, _) =>
                {
                    btnInstall.IsEnabled = false;
                    btnRescan.IsEnabled = false;
                    progressBlock.IsVisible = true;
                    progressBlock.Text = "Starting…";
                    var progress = new Progress<InstallProgress>(p => progressBlock.Text = p.Phase switch
                    {
                        InstallPhase.Downloading => p.Fraction >= 0 ? $"Downloading… {p.Fraction:P0}" : "Downloading…",
                        InstallPhase.Verifying => "Checking SHA-256…",
                        InstallPhase.Extracting => "Unpacking…",
                        InstallPhase.Done => "Installed.",
                        _ => progressBlock.Text,
                    });
                    var result = await OidnInstaller.InstallAsync(progress, cts.Token);
                    progressBlock.Text = result.Success
                        ? "Installed."
                        : $"Install failed ({result.Outcome}): {result.ErrorDetail}";
                    UpdateStatus(statusBlock);
                    btnInstall.Content = OidnDenoiser.IsAvailable ? "Reinstall" : "Install";
                    btnInstall.IsEnabled = asset != null;
                    btnRescan.IsEnabled = true;
                };
                btnRescan.Click += (_, _) => { OidnDenoiser.Refresh(); UpdateStatus(statusBlock); };
                btnClose.Click += (_, _) => win.Close();
                win.Closed += (_, _) => { cts.Cancel(); tcs.TrySetResult(); };

                if (owner != null) _ = win.ShowDialog(owner);
                else win.Show();
            }

            if (Dispatcher.UIThread.CheckAccess()) Run();
            else Dispatcher.UIThread.Post(Run);
            return tcs.Task;
        }

        private static void UpdateStatus(TextBlock block)
        {
            if (OidnDenoiser.IsAvailable)
            {
                block.Text = $"Current status: installed ({OidnDenoiser.LoadedFrom})";
                block.Foreground = Brushes.White;
            }
            else
            {
                block.Text = "Current status: not installed — " + OidnDenoiser.LoadError;
                // Yellow, not red — red/green is indistinguishable for some users.
                block.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            }
        }
    }
}
