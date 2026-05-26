// Hosting/AvaloniaShellBootstrap.cs
//
// Replaces the proof-of-life AvaloniaBootstrap from Phase 2.1. Wires the
// full Phase 2.3 stack:
//
//   GpuSurface → RendererFactory → FractalRenderHost
//                                    + FractalInputController
//                                    + HostColorThemeService
//                                    + HostHelpContentProvider
//                                    + (optional) IPaletteExtractionService
//
//   ShellViewModel(renderHost, input, theme, help)  →  MainWindow.DataContext
//
// Host-handled events (file dialogs, palette extraction, system browser)
// are wired here so the Avalonia VM tree stays free of System.Drawing /
// System.Diagnostics.Process / Windows-only APIs.
//
// Lives in the main FracturingFog WinExe so RendererFactory + the
// IColorMap + UserColorThemeLibrary stack are reachable without dragging
// them into UI.Avalonia.

using System;
using System.Diagnostics;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using FracturingFog.Abstractions;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views;
using FracturingFog.ViewState;

namespace FracturingFog.Hosting
{
    /// <summary>
    /// Static entry point passed to AvaloniaShell.Run as the
    /// <c>onSurfaceReady</c> callback. The Avalonia MainWindow invokes
    /// <see cref="OnSurfaceReady"/> the first time its native GPU surface
    /// is available; everything else flows from there.
    /// </summary>
    public static class AvaloniaShellBootstrap
    {
        private static IFractalRenderer? s_renderer;
        private static FractalRenderHost? s_renderHost;
        private static FractalInputController? s_input;
        private static ShellViewModel? s_shell;
        private static Timer? s_presentTimer;
        private static IGpuSurface? s_surface;
        private static readonly object s_gate = new();

        /// <summary>Optional palette-extraction service injection. Bootstrap
        /// supplies null today; F.3+ task wires the real implementation.</summary>
        public static IPaletteExtractionService? PaletteService { get; set; }

        public static void OnSurfaceReady(IGpuSurface surface)
        {
            try
            {
                s_surface = surface ?? throw new ArgumentNullException(nameof(surface));
                s_renderer = RendererFactory.Create(surface);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvaloniaShellBootstrap] Renderer init failed: {ex}");
                return;
            }

            int w = Math.Max(1, surface.PixelWidth);
            int h = Math.Max(1, surface.PixelHeight);

            // ── Engines ──────────────────────────────────────────────────
            var viewState = new FractalViewState();
            var initialMap = ColorPalette.GetPaletteByName("HSV");
            s_renderHost = new FractalRenderHost(s_renderer, viewState, w, h, initialMap);
            s_input = new FractalInputController(viewState);

            // ── Services ─────────────────────────────────────────────────
            var themeService = new HostColorThemeService();
            var helpProvider = new HostHelpContentProvider();

            // ── View model tree ──────────────────────────────────────────
            s_shell = new ShellViewModel(s_renderHost, s_input, themeService, helpProvider, PaletteService);

            WireShellHostEvents(s_shell);

            // ── Surface lifetime ─────────────────────────────────────────
            surface.Resized += OnSurfaceResized;
            surface.HandleLost += (_, _) => Shutdown();

            // ── Assign DataContext on the UI thread ──────────────────────
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is MainWindow mw)
                {
                    mw.DataContext = s_shell;
                }

                // Kick the first calculation now that the VM tree is bound.
                s_renderHost?.Trigger();
            });

            // ── ~60 Hz present loop ──────────────────────────────────────
            // Calculator drives UpdateTexture; the present timer pushes the
            // most-recent texture through the swap chain at vsync rate so
            // resize + DPI change re-presents are visible immediately.
            s_presentTimer = new Timer(_ => Present(), null, 0, 16);
        }

        private static void WireShellHostEvents(ShellViewModel shell)
        {
            // Color theme preview: editor produced a fresh ColorThemeDef. Build
            // an IColorMap from it and push onto the render host so the user
            // sees the change without saving to the library first.
            shell.ColorThemePreviewRequested += (_, def) =>
            {
                var map = HostColorThemeService.BuildColorMap(def);
                if (map != null && s_renderHost != null)
                {
                    s_renderHost.ColorMap = map;
                    s_renderHost.RepaintWithPostFx();
                }
            };

            // Open URLs in the user's default browser.
            shell.LinkRequested += (_, url) =>
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Failed to open URL: {ex.Message}");
                }
            };

            // File save (theme JSON or generated C#). For F.3 this is a stub
            // that writes to a fixed temp path so the editor's Save button
            // doesn't no-op; the proper SaveFileDialog hookup lands once the
            // Avalonia.Controls.ApplicationLifetimes storage provider is
            // wired into the shell.
            shell.SaveFileRequested += (_, args) =>
            {
                try
                {
                    string path = string.IsNullOrEmpty(args.SuggestedName)
                        ? System.IO.Path.GetTempFileName()
                        : System.IO.Path.Combine(System.IO.Path.GetTempPath(), args.SuggestedName);
                    System.IO.File.WriteAllText(path, args.Content ?? "");
                    args.Saved = true;
                }
                catch (Exception ex)
                {
                    args.Saved = false;
                    args.ErrorMessage = ex.Message;
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Save failed: {ex.Message}");
                }
            };

            // From-image flow: editor wants the host to extract a palette from
            // a chosen image. F.3 leaves this unwired (PaletteService null)
            // so the editor's "From Image…" button surfaces a message; the
            // real wiring lands when the palette-extraction service ships.
            shell.FromImageRequested += (_, args) =>
            {
                if (PaletteService == null) return;
                // Placeholder: when the real service is wired, replace with
                // a real Avalonia OpenFileDialog → request → args.Stops.
            };

            shell.MessageRequested += (_, args) =>
            {
                // Console for now; F.3+ task swaps in a proper Avalonia MessageBox.
                Console.WriteLine($"[{args.Title}] {args.Body}");
            };
        }

        private static void OnSurfaceResized(object? sender, EventArgs e)
        {
            var surf = s_surface;
            var host = s_renderHost;
            if (surf == null || host == null) return;

            int w = Math.Max(1, surf.PixelWidth);
            int h = Math.Max(1, surf.PixelHeight);
            Dispatcher.UIThread.Post(() => host.Resize(w, h));
        }

        private static void Present()
        {
            var renderer = s_renderer;
            if (renderer == null) return;
            try
            {
                renderer.Render();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvaloniaShellBootstrap] Present failed: {ex.Message}");
                Shutdown();
            }
        }

        public static void Shutdown()
        {
            lock (s_gate)
            {
                s_presentTimer?.Dispose();
                s_presentTimer = null;
                try { s_shell?.Dispose(); } catch { /* ignore */ }
                s_shell = null;
                try { s_renderHost?.Dispose(); } catch { /* renderer disposed via host */ }
                s_renderHost = null;
                s_renderer = null;
                s_input = null;
                s_surface = null;
            }
        }
    }
}
