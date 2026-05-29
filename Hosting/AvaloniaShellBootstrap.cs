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
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        private static HostColorThemeService? s_themeService;
        private static readonly object s_gate = new();

        // ── Span-mode (borderless multi-monitor fullscreen) saved state ──────
        private static bool s_spanning;
        private static WindowState s_preSpanState;
        private static WindowDecorations s_preSpanDecorations;
        private static PixelPoint s_preSpanPosition;
        private static double s_preSpanWidth;
        private static double s_preSpanHeight;
        private static bool s_preSpanTopmost;

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
            // the main-project IColorMap type. Stored statically so
            // WireShellHostEvents can reach it for the SaveRegion / Delete
            // / ReloadThemes flows.
            s_themeService = new HostColorThemeService(s_renderHost);
            var themeService = s_themeService;
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

            // ── New #53 wires ────────────────────────────────────────────

            // Close program — preferred path is the classic desktop lifetime
            // Shutdown(0); falls back to closing the main window for IDE-launch
            // scenarios where no lifetime exists yet.
            shell.CloseProgramRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
                        desk.Shutdown(0);
                    else
                        AvaloniaDialogs.ActiveMainWindow?.Close();
                });
            };

            // Clipboard copy — TopLevel.Clipboard is the cross-platform
            // accessor. Fire-and-forget; failures are logged but never
            // surface a modal because the user-perceived flow is "click → done".
            shell.CopyToClipboardRequested += async (_, text) =>
            {
                try
                {
                    var top = AvaloniaDialogs.ActiveMainWindow != null
                        ? TopLevel.GetTopLevel(AvaloniaDialogs.ActiveMainWindow) : null;
                    if (top?.Clipboard != null && text != null)
                        await top.Clipboard.SetValueAsync(DataFormat.Text, text);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Clipboard copy failed: {ex.Message}");
                }
            };

            // Save current view as region — prompt for a name via the
            // existing message dialog (re-used because we don't have a
            // proper input-prompt control yet), then ask the theme service
            // to persist. The args.Completion TCS is still signalled so the
            // caller flow is consistent with the other host-handled events.
            shell.SaveRegionRequested += async (_, args) =>
            {
                try
                {
                    string? name = await AvaloniaDialogs.PromptForTextAsync(
                        "Save Region", "Region name:", suggested: Main.SelectedTheme ?? "");
                    if (!string.IsNullOrWhiteSpace(name) && s_renderHost != null)
                    {
                        bool ok = ((IColorThemeService)s_themeService!)
                            .SaveCurrentAsRegion(name!, s_renderHost.ViewState);
                        if (ok)
                            shell.FloatingMenu.SetRegions(s_themeService!.EnumerateRegionNames());
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] SaveRegion failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            // Delete region — confirm then ask the service.
            shell.DeleteRegionRequested += async (_, tuple) =>
            {
                var (confirm, name) = tuple;
                try
                {
                    var result = await AvaloniaDialogs.ShowMessageAsync(
                        confirm.Title, confirm.Body, expectsConfirmation: true);
                    if (result == AvaloniaDialogs.MessageResult.Yes)
                    {
                        if (s_themeService!.DeleteRegion(name))
                            shell.FloatingMenu.SetRegions(s_themeService!.EnumerateRegionNames());
                        else
                            await AvaloniaDialogs.ShowMessageAsync(
                                "Delete Region",
                                "That region is built-in and cannot be deleted.",
                                expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] DeleteRegion failed: {ex.Message}");
                }
                finally
                {
                    confirm.Completion.TrySetResult(true);
                }
            };

            // Screenshot — encode the most-recent rendered frame to PNG via
            // System.Drawing and write through a SaveFilePicker.
            shell.ScreenshotRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Screenshot",
                        suggestedName: $"fracturing-fog-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                        filter: "PNG image (*.png)|*.png");
                    if (string.IsNullOrEmpty(path)) return;
                    s_renderHost.SaveLastFrameToPng(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Screenshot failed: {ex.Message}");
                }
            };

            // ── New #54 wires ────────────────────────────────────────────

            // Export user regions — pick a path, then serialize the bundle.
            shell.ExportRegionsRequested += async (_, _) =>
            {
                try
                {
                    if (s_themeService == null) return;
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Export Custom Regions",
                        suggestedName: "regions.json",
                        filter: "JSON File (*.json)|*.json");
                    if (string.IsNullOrEmpty(path)) return;

                    var result = ((IColorThemeService)s_themeService).ExportUserRegionsToFile(path);
                    if (!result.Success)
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Export Regions",
                            result.ErrorMessage ?? "Export failed.",
                            expectsConfirmation: false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ExportRegions failed: {ex.Message}");
                }
            };

            // Import regions — pick a path, merge, refresh the combo.
            shell.ImportRegionsRequested += async (_, _) =>
            {
                try
                {
                    if (s_themeService == null) return;
                    string? path = await AvaloniaDialogs.PickOpenFileAsync(
                        "Import Custom Regions",
                        filter: "JSON File (*.json)|*.json|All Files (*.*)|*.*");
                    if (string.IsNullOrEmpty(path)) return;

                    var result = ((IColorThemeService)s_themeService).ImportRegionsFromFile(path);
                    if (result.Success)
                    {
                        shell.RefreshRegionListsFromService();
                        if (result.Added == 0)
                            await AvaloniaDialogs.ShowMessageAsync(
                                "Import Regions",
                                "No new regions imported (all entries already exist).",
                                expectsConfirmation: false);
                    }
                    else
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Regions",
                            result.ErrorMessage ?? "Import failed.",
                            expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ImportRegions failed: {ex.Message}");
                }
            };

            // Slideshow settings — load persisted settings, pop the dialog,
            // write back on OK. The Avalonia shell doesn't run the slideshow
            // engine yet (legacy Slideshow.cs stays intact per scope), but the
            // settings round-trip so the values persist for when it lands.
            shell.SlideshowSettingsRequested += async (_, _) =>
            {
                try
                {
                    var current = SlideshowSettingsStore.Load();
                    var chosen = await AvaloniaDialogs.ShowSlideshowSettingsAsync(current, audioReactive: false);
                    if (chosen != null)
                        SlideshowSettingsStore.Save(chosen.Value.Settings);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] SlideshowSettings failed: {ex.Message}");
                }
            };

            // ── New #55 wires — colour-theme library IO ──────────────────

            // Export user themes — pick a path, then serialize the library.
            shell.ExportThemesRequested += async (_, _) =>
            {
                try
                {
                    if (s_themeService == null) return;
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Export Color Themes",
                        suggestedName: "colorthemes.json",
                        filter: "JSON File (*.json)|*.json");
                    if (string.IsNullOrEmpty(path)) return;

                    var result = ((IColorThemeService)s_themeService).ExportUserThemesToFile(path);
                    if (!result.Success)
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Export Themes",
                            result.ErrorMessage ?? "Export failed.",
                            expectsConfirmation: false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ExportThemes failed: {ex.Message}");
                }
            };

            // Import themes — pick a path, merge, refresh the combo.
            shell.ImportThemesRequested += async (_, _) =>
            {
                try
                {
                    if (s_themeService == null) return;
                    string? path = await AvaloniaDialogs.PickOpenFileAsync(
                        "Import Color Themes",
                        filter: "JSON File (*.json)|*.json|All Files (*.*)|*.*");
                    if (string.IsNullOrEmpty(path)) return;

                    var result = ((IColorThemeService)s_themeService).ImportThemesFromFile(path);
                    if (result.Success)
                    {
                        shell.RefreshThemeListsFromService();
                        if (result.Added == 0)
                            await AvaloniaDialogs.ShowMessageAsync(
                                "Import Themes",
                                "No new themes imported (all entries already exist).",
                                expectsConfirmation: false);
                    }
                    else
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Themes",
                            result.ErrorMessage ?? "Import failed.",
                            expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ImportThemes failed: {ex.Message}");
                }
            };

            // Delete theme — confirm then ask the service. Built-in themes
            // aren't in the user library, so DeleteTheme returns false for them.
            shell.DeleteThemeRequested += async (_, tuple) =>
            {
                var (confirm, name) = tuple;
                try
                {
                    var result = await AvaloniaDialogs.ShowMessageAsync(
                        confirm.Title, confirm.Body, expectsConfirmation: true);
                    if (result == AvaloniaDialogs.MessageResult.Yes)
                    {
                        if (s_themeService!.DeleteTheme(name))
                            shell.RefreshThemeListsFromService();
                        else
                            await AvaloniaDialogs.ShowMessageAsync(
                                "Delete Theme",
                                "That theme is built-in and cannot be deleted.",
                                expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] DeleteTheme failed: {ex.Message}");
                }
                finally
                {
                    confirm.Completion.TrySetResult(true);
                }
            };

            // Span — toggle borderless fullscreen across every monitor. The
            // ShellViewModel owns the intent (and the button label); we own the
            // Avalonia Window geometry and restore it verbatim on exit.
            shell.SpanToggleRequested += (_, enter) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var win = AvaloniaDialogs.ActiveMainWindow;
                    if (win == null) return;
                    if (enter) EnterSpanMode(win);
                    else ExitSpanMode(win);
                });
            };

            // Poster — pop the size dialog, pick a path, then render offscreen
            // at full resolution via the shared PosterRenderer (same engine the
            // legacy WinForms poster path uses) and save.
            shell.PosterRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;

                    var dims = await AvaloniaDialogs.ShowPosterAsync();
                    if (dims == null) return;

                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Poster Image",
                        suggestedName: $"fracturing-fog-poster-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                        filter: "PNG image (*.png)|*.png|TIFF image (*.tiff;*.tif)|*.tiff;*.tif|BMP image (*.bmp)|*.bmp");
                    if (string.IsNullOrEmpty(path)) return;

                    string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                        ".tif" or ".tiff" => System.Drawing.Imaging.ImageFormat.Tiff,
                        _ => System.Drawing.Imaging.ImageFormat.Png,
                    };

                    string watermark = !string.IsNullOrEmpty(s_renderHost.RegionName)
                        ? s_renderHost.RegionName!
                        : "Fracturing Fog";
                    if (!string.IsNullOrEmpty(s_renderHost.ThemeName))
                        watermark += " - " + s_renderHost.ThemeName;
                    string subText = $"Fracturing Fog {DateTime.Now.Year}";

                    var req = s_renderHost.CreatePosterRequest(
                        dims.Value.Width, dims.Value.Height, rotate: dims.Value.Portrait,
                        path, format, watermark, subText);

                    try
                    {
                        var result = await Task.Run(() => PosterRenderer.RenderToFile(req, CancellationToken.None));
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster Saved",
                            $"Saved {result.SavedWidth}×{result.SavedHeight} px to:\n{path}\n({result.ElapsedMs} ms)",
                            expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster", $"Render failed:\n{ex.Message}", expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Poster failed: {ex.Message}");
                }
            };
        }

        // ── Span-mode helpers ────────────────────────────────────────────────

        /// <summary>Stretch the window borderless across the union of all
        /// monitor bounds (legacy WinForms parity: Bounds = VirtualScreen).
        /// Saves the prior geometry so <see cref="ExitSpanMode"/> can restore it.</summary>
        private static void EnterSpanMode(Window win)
        {
            if (s_spanning) return;

            var screens = win.Screens;
            if (screens == null || screens.All.Count == 0) return;

            // Capture restore state before mutating anything.
            s_preSpanState = win.WindowState;
            s_preSpanDecorations = win.WindowDecorations;
            s_preSpanPosition = win.Position;
            s_preSpanWidth = double.IsNaN(win.Width) ? win.Bounds.Width : win.Width;
            s_preSpanHeight = double.IsNaN(win.Height) ? win.Bounds.Height : win.Height;
            s_preSpanTopmost = win.Topmost;

            // Union of every screen's pixel bounds.
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var s in screens.All)
            {
                var b = s.Bounds;
                if (b.X < minX) minX = b.X;
                if (b.Y < minY) minY = b.Y;
                if (b.X + b.Width > maxX) maxX = b.X + b.Width;
                if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
            }

            // Window.Width/Height are DIPs; screen bounds are physical pixels.
            double scaling = win.RenderScaling;
            if (scaling <= 0) scaling = 1.0;

            win.WindowState = WindowState.Normal;
            win.WindowDecorations = WindowDecorations.None;
            win.Topmost = true;
            win.Position = new PixelPoint(minX, minY);
            win.Width = (maxX - minX) / scaling;
            win.Height = (maxY - minY) / scaling;
            s_spanning = true;
        }

        /// <summary>Restore the window geometry captured by
        /// <see cref="EnterSpanMode"/>.</summary>
        private static void ExitSpanMode(Window win)
        {
            if (!s_spanning) return;
            win.WindowDecorations = s_preSpanDecorations;
            win.Topmost = s_preSpanTopmost;
            win.Position = s_preSpanPosition;
            win.Width = s_preSpanWidth;
            win.Height = s_preSpanHeight;
            win.WindowState = s_preSpanState;
            s_spanning = false;
        }

        // Convenience helper for the SaveRegion handler — pulls MainViewModel
        // through ShellViewModel so the prompt's "suggested name" can default
        // to the currently-selected theme (a common save pattern).
        private static MainViewModel Main => s_shell!.Main;

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
