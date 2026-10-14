// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Drive the popup text-scale resource from the global UI scale (#812).
        // Popups render outside the per-window LayoutTransform (S2), so their
        // text is scaled via the SizeFontPopup resource instead. Seed it before
        // any window/popup is shown.
        Services.UiScalePopupFont.Install();
        InstallDefaultWindowIcon("avares://FracturingFog.UI.Avalonia/Assets/FracturingFog.ico");   // #429

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new Views.MainWindow();
            desktop.MainWindow = main;

            // Startup splash. Shown before the main window's GPU surface comes
            // up and the render host / VM tree is built (that heavy work runs in
            // AvaloniaShellBootstrap.OnSurfaceReady, triggered while the main
            // window shows). Dismissed once the first frame lands, or after a
            // timeout fallback so a headless / surface-less run can't strand it.
            var splash = new Views.SplashWindow();
            splash.Show();
            DismissSplashWhenReady(splash, main);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>#429 — on Linux (and macOS) a window shows no icon unless one is
    /// set: the exe's ApplicationIcon is a Windows-only resource. An application
    /// style gives every window (and Window subclass) the app icon, so the main
    /// window and every dialog get it; a window that sets its own icon keeps it.
    /// (Window.IconProperty.OverrideDefaultValue throws in Avalonia 12 — Window
    /// already owns that metadata.) Windows keeps using the exe icon, as before.</summary>
    private void InstallDefaultWindowIcon(string assetUri)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            using var asset = AssetLoader.Open(new Uri(assetUri));
            var copy = new MemoryStream();
            asset.CopyTo(copy);
            copy.Position = 0;
            var icon = new WindowIcon(copy);
            Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Window icon not set: {ex.Message}");
        }
    }

    /// <summary>
    /// Close the splash once the shell is up. The bootstrap assigns
    /// MainWindow.DataContext (a <see cref="ShellViewModel"/>) at the end of
    /// OnSurfaceReady, then triggers the first render; we hook the first
    /// FrameCompleted off that and fade the splash out. A timeout fallback and
    /// an early main-window-close both dismiss it too, so it never lingers.
    /// </summary>
    private static void DismissSplashWhenReady(Views.SplashWindow splash, Views.MainWindow main)
    {
        bool dismissed = false;
        DispatcherTimer? fallback = null;
        FracturingFog.Render.IFractalRenderHost? host = null;

        void Dismiss()
        {
            if (dismissed) return;
            dismissed = true;
            fallback?.Stop();
            main.DataContextChanged -= OnDataContextChanged;
            main.Closed -= OnMainClosed;
            if (host != null) host.FrameCompleted -= OnFirstFrame;
            splash.FadeOutAndClose();
        }

        void OnFirstFrame(object? sender, FracturingFog.Render.RenderFrameInfo info)
        {
            // FrameCompleted can fire on a render thread — marshal to the UI thread.
            Dispatcher.UIThread.Post(Dismiss);
        }

        void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (main.DataContext is not ShellViewModel shell) return;
            main.DataContextChanged -= OnDataContextChanged;
            splash.SetStatus("Preparing the first render…");
            host = shell.Main.RenderHost;
            host.FrameCompleted += OnFirstFrame;
        }

        void OnMainClosed(object? sender, EventArgs e) => Dismiss();

        main.DataContextChanged += OnDataContextChanged;
        main.Closed += OnMainClosed;

        // Fallback: never let the splash outlive startup, even if the surface
        // never initializes (layout-only test, GPU init failure, headless leg).
        fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        fallback.Tick += (_, _) => Dismiss();
        fallback.Start();
    }
}
