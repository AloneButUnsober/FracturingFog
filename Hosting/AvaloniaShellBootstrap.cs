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
using System.Collections.Generic;
using System.Diagnostics;

using System.Threading.Tasks;

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
        private static IGpuSurface? s_surface;
        private static readonly object s_gate = new();

        /// <summary>Palette-extraction service. Defaulted to the
        /// System.Drawing-backed <see cref="HostPaletteExtractionService"/>;
        /// callers may swap before <see cref="OnSurfaceReady"/> for tests.</summary>
        public static IPaletteExtractionService? PaletteService { get; set; }
            = new HostPaletteExtractionService();

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
            // Theme service holds a reference to the render host so its
            // ApplyTheme(name) path can push a freshly-built IColorMap
            // directly onto the renderer without UI.Avalonia having to see
            // the main-project IColorMap type.
            var themeService = new HostColorThemeService(s_renderHost);
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

            // No present loop. FractalRenderHost auto-presents after every
            // texture upload and after every resize, all under its own D3D
            // lock — so the swap chain never sees concurrent access. The
            // previous ~60 Hz background timer raced the UI-thread Resize
            // path on the D3D11 immediate context and locked the driver.
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

            // File save (theme JSON or generated C#). The editor awaits
            // args.Completion before reading args.Saved / args.ErrorMessage,
            // so we run the picker truly async without blocking the UI
            // thread — the prior `.GetAwaiter().GetResult()` pattern
            // deadlocked the dispatcher when raised from a UI-thread button.
            shell.SaveFileRequested += async (_, args) =>
            {
                try
                {
                    string? path = await AvaloniaDialogs.SaveFileAsync(
                        args.Title, args.SuggestedName, args.Filter, args.Content ?? "");
                    args.Saved = !string.IsNullOrEmpty(path);
                    if (!args.Saved) args.ErrorMessage = null;
                }
                catch (Exception ex)
                {
                    args.Saved = false;
                    args.ErrorMessage = ex.Message;
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Save failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            // From-image flow: editor wants the host to extract a palette
            // from a chosen image. Opens ImagePaletteView modally on the UI
            // thread; the editor's command awaits args.Completion (signalled
            // in the finally block) before reading args.Stops.
            shell.FromImageRequested += async (_, args) =>
            {
                var service = PaletteService;
                if (service == null)
                {
                    args.Completion.TrySetResult(true);
                    return;
                }
                try
                {
                    var stops = await AvaloniaDialogs.ShowImagePalettePickerAsync(service);
                    if (stops != null && stops.Count >= 2)
                    {
                        var defs = new List<ColorStopDef>(stops.Count);
                        foreach (var s in stops)
                            defs.Add(new ColorStopDef { Position = s.Position, R = s.R, G = s.G, B = s.B });
                        args.Stops = defs;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] FromImage failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            // MessageBox: editor awaits args.Completion before reading
            // args.Confirmed, so we never block the dispatcher.
            shell.MessageRequested += async (_, args) =>
            {
                try
                {
                    var result = await AvaloniaDialogs.ShowMessageAsync(
                        args.Title, args.Body, args.ExpectsConfirmation);
                    if (args.ExpectsConfirmation)
                        args.Confirmed = result == AvaloniaDialogs.MessageResult.Yes;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Message dialog failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
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

        public static void Shutdown()
        {
            lock (s_gate)
            {
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
