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
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Silk;
using FracturingFog.Rendering.Silk.Platform;
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
        private static FractalParamsView? s_paramsWin;

        // Dedicated source-compiled editors (one window each, modeless).
        private static UserEquationView? s_userEqWin;
        private static SandboxView? s_sandboxWin;
        private static UserBulbView? s_userBulbWin;
        private static ColorGenEditorView? s_colorGenWin;
        private static DispatcherTimer? s_userBulbAnimTimer;

        private static readonly object s_gate = new();

        // ── Span-mode (borderless multi-monitor fullscreen) saved state ──────
        private static bool s_spanning;
        private static WindowState s_preSpanState;
        private static SystemDecorations s_preSpanDecorations;
        private static PixelPoint s_preSpanPosition;
        private static double s_preSpanWidth;
        private static double s_preSpanHeight;
        private static bool s_preSpanTopmost;

        /// <summary>Palette-extraction service. Defaulted to the
        /// System.Drawing-backed <see cref="HostPaletteExtractionService"/>;
        /// callers may swap before <see cref="OnSurfaceReady"/> for tests.</summary>
        public static IPaletteExtractionService? PaletteService { get; set; }
            = new HostPaletteExtractionService();

        // Phase 2.4 cross-platform: registers the Silk.NET OpenGL backend as
        // RendererFactory.NonWin32Backend so X11 / CAMetalLayer / Wayland
        // surfaces can be served when the DX path is unavailable. Kept in a
        // static ctor (rather than at first OnSurfaceReady call) because the
        // factory hook must be live before any IGpuSurface arrives — Avalonia
        // can raise the SurfaceReady event on a worker thread.
        static AvaloniaShellBootstrap()
        {
            RendererFactory.NonWin32Backend = TryCreateSilkRenderer;
        }

        private static IFractalRenderer? TryCreateSilkRenderer(IGpuSurface surface)
        {
            try
            {
                switch (surface.Kind)
                {
                    case GpuSurfaceKind.X11Window:
                    {
                        var ctx = SilkGLXContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers);
                    }
                    case GpuSurfaceKind.Win32Hwnd:
                    {
                        // Only reached when the DX path declined the surface
                        // (force-fallback path for parity testing). Normal
                        // Windows runs short-circuit before this hook fires.
                        var ctx = SilkWin32ContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers);
                    }
                    case GpuSurfaceKind.CoreAnimationMetalLayer:
                    {
                        // Avalonia hands NSView* on macOS via NativeControlHost
                        // even when the enum label says CoreAnimationMetalLayer.
                        // SilkCglContextAdapter binds NSOpenGLContext.setView:
                        // to that NSView and produces a 3.2 core context that
                        // SilkGLRenderer's 3.3 GLSL shaders compile against.
                        var ctx = SilkCglContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers);
                    }
                    case GpuSurfaceKind.WaylandSurface:
                    {
                        // Wayland native: EGL bound to GL (not GLES), 3.3 core
                        // forward-compatible. The adapter opens its own
                        // wl_display_connect so it does not require Avalonia
                        // to surface its internal display pointer.
                        var ctx = SilkEglContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers);
                    }
                    default:
                        Console.Error.WriteLine(
                            $"[AvaloniaShellBootstrap] No Silk adapter for surface kind {surface.Kind}.");
                        return null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[AvaloniaShellBootstrap] Silk renderer init failed for {surface.Kind}: {ex.Message}");
                return null;
            }
        }

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

            // The swap-chain HWND composites on top of all Avalonia content, so
            // the XAML InputSponge never receives a pointer event. Subclass the
            // native window and forward its mouse messages into the controller.
            // (Runs on the UI thread — OnSurfaceReady fires from the native
            // control's CreateNativeControlCore.)
            NativeMouseForwarder.Attach(surface.Handle, s_input);
            // Bridge native HWND right-click release to the Avalonia shell so
            // MainWindow can open its context menu (Avalonia's own
            // ContextRequested never fires — WM_RBUTTONUP is swallowed by the
            // subclass above so Windows never raises WM_CONTEXTMENU).
            NativeMouseForwarder.ContextMenuRequested = wasDrag =>
            {
                try { FracturingFog.UI.Avalonia.AvaloniaShell.ContextMenuRequested?.Invoke(wasDrag); }
                catch { /* swallow — must not crash the native subclass */ }
            };
            // Bridge "mouse-down on render surface" to the shell so it can
            // pull keyboard focus back onto the InputSponge. Otherwise a
            // toolbar ComboBox keeps logical focus after the click and
            // swallows R/M/T/V via its type-ahead. Posted onto the UI
            // dispatcher so the Focus() call doesn't run inside the Win32
            // message handler.
            NativeMouseForwarder.FocusRequested = () =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { FracturingFog.UI.Avalonia.AvaloniaShell.RenderSurfaceFocusRequested?.Invoke(); }
                        catch { /* swallow */ }
                    });
                }
                catch { /* swallow — must not crash the native subclass */ }
            };

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

            // Stamp program name + version onto the render host so the watermark
            // overlay (FractalOverlayCompositor) renders "Fracturing Fog v0.6.1
            // 2026" instead of "Fracturing Fog v? 2026". Source: assembly
            // version via HostHelpContentProvider — same value FloatingHelp uses.
            s_renderHost.ProgramName = helpProvider.ProgramName;
            s_renderHost.ProgramVersion = helpProvider.ProgramVersion;

            // Hand the render host its theme service so the video slideshow
            // engine can cycle regions/themes per leg (legacy VideoZoom parity).
            s_renderHost.AttachThemeService(themeService);

            // ── Persisted libraries ──────────────────────────────────────
            // Mirror MainForm startup (MainForm.cs ~873): load user regions +
            // equation stores from disk. Without this the region combos only
            // surface built-ins (UserRegions stays empty), and saved
            // equations don't appear in their editors' Saved lists.
            try { FractalRegionLibrary.Instance.Load(); } catch { }
            try { UserEquationStore.Instance.Load(); }    catch { }
            try { SandboxEquationStore.Instance.Load(); }  catch { }
            try { UserBulbStore.Instance.Load(); }         catch { }

            // ── View model tree ──────────────────────────────────────────
            s_shell = new ShellViewModel(s_renderHost, s_input, themeService, helpProvider, PaletteService);

            // Window title: "{ProgramName} v{Version}  —  {renderer description}"
            // (legacy MainForm parity, MainForm.cs:917). RebuildWindowTitle()
            // fires after each setter assignment.
            s_shell.ProgramName = helpProvider.ProgramName;
            s_shell.ProgramVersion = helpProvider.ProgramVersion;
            s_shell.RendererDescription = s_renderer?.RendererDescription ?? "";

            // Dimensions combo: feed the resolution table + handle resize.
            // Lives here (not in ShellViewModel) because the main-project
            // ResolutionDimensions table isn't referenced from UI.Avalonia.
            s_shell.FloatingMenu.SetResolutions(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(
                        FracturingFog.Models.ResolutionDimensions.Resolutions,
                        r => !string.IsNullOrEmpty(r.Name)),
                    r => r.Name!));
            s_shell.FloatingMenu.ResolutionChanged += (_, name) =>
            {
                var res = System.Linq.Enumerable.FirstOrDefault(
                    FracturingFog.Models.ResolutionDimensions.Resolutions,
                    r => string.Equals(r.Name, name, StringComparison.Ordinal));
                if (res == null || res.Width <= 0 || res.Height <= 0) return;
                Dispatcher.UIThread.Post(() =>
                {
                    var win = AvaloniaDialogs.ActiveMainWindow;
                    if (win == null) return;
                    win.Width = res.Width;
                    win.Height = res.Height;
                });
            };

            // Palette samplers for MiniDepth (#11 — theme-styled gradient).
            // Uses the host's MandelbrotCalculator.ColorMap so the strip
            // colours stay in lock-step with whatever theme is active.
            s_shell.SamplePaletteColor = smoothIter =>
            {
                var map = s_renderHost?.Mandelbrot.ColorMap;
                if (map == null) return 0xFF808080u;
                int maxIter = Math.Max(1, s_renderHost?.Mandelbrot.MaxIterations ?? 256);
                try
                {
                    int packed = map.Map((float)smoothIter, 0f, maxIter);
                    return unchecked((uint)packed) | 0xFF000000u;
                }
                catch
                {
                    return 0xFF808080u;
                }
            };
            s_shell.GetCurrentSwatchArgb = () =>
            {
                var map = s_renderHost?.Mandelbrot.ColorMap;
                if (map == null) return 0xFF808080u;
                try
                {
                    int packed = map.SwatchSample;
                    return unchecked((uint)packed) | 0xFF000000u;
                }
                catch
                {
                    return 0xFF808080u;
                }
            };

            // MiniMap thumbnail render (UI-gap #10). Each time the user
            // toggles MiniMap on, regenerate the thumbnail for the active
            // fractal type / theme. Indicator state is driven separately by
            // FrameCompleted in ShellViewModel — no re-render needed for
            // pan/zoom.
            s_shell.MiniMapVisibilityChanged += (_, _) => RenderMiniMapAsync(s_shell);
            // Also regenerate when the user picks a new fractal type or
            // colour theme — both invalidate the thumbnail.
            s_shell.Main.PropertyChanged += (_, e) =>
            {
                if (s_shell == null || !s_shell.IsMiniMapVisible) return;
                if (e.PropertyName == nameof(MainViewModel.SelectedFractalType)
                 || e.PropertyName == nameof(MainViewModel.SelectedFractalEntry)
                 || e.PropertyName == nameof(MainViewModel.SelectedTheme))
                {
                    RenderMiniMapAsync(s_shell);
                }
            };

            WireShellHostEvents(s_shell);

            // Phase 3: start the 5-second probe that drives the status-bar
            // "● Server: running / off" indicator. Uses the default server
            // port (47823) unless a server-config.json under %APPDATA% overrides.
            s_shell.StartServerPing(FracturingFog.Server.ServerConfig.LoadOrDefault().Port);

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
                    // ApplyColorMap recolours the current frame in place
                    // (Mandelbrot) or recomputes (alt calculators). The old
                    // "ColorMap = map; RepaintWithPostFx()" path re-uploaded the
                    // stale, old-map buffer, so editor edits only showed after
                    // the next pan/zoom.
                    s_renderHost.ApplyColorMap(map);
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

            // ColorGen editor: shell raises this when the "ColorGen Editor"
            // menu/toolbar entry is invoked. Open the dialog (single instance).
            shell.OpenColorGenEditorRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(OpenColorGenEditor);
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
                        "Save Region", "Region name:", suggested: BuildRegionNameDefault(shell));
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

            // ── #64 — Video zoom ─────────────────────────────────────────
            //
            // Pop the (programmatic, main-project) video dialog seeded from the
            // current view, then hand the request back to the shell on the UI
            // thread. The shell owns the button label + VCR visibility and the
            // IVideoZoomController start call; the engine itself runs on a
            // background Task and marshals its events via Dispatcher.
            shell.VideoRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;
                    var vs = s_renderHost.ViewState;
                    var req = await AvaloniaDialogs.ShowVideoAsync(vs.CenterX, vs.CenterY, vs.Zoom);
                    if (req == null) return;
                    Dispatcher.UIThread.Post(() => s_shell?.StartVideoFromRequest(req));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Video failed: {ex.Message}");
                }
            };

            // ── Fractal-type parameters ──────────────────────────────────
            //
            // Pop the per-type parameter editor seeded from the shared
            // ViewState. The VM mutates ViewState.FractalParameters in place
            // and fires ParamChanged on every control edit, so we re-render
            // live. Shown modeless (legacy parity) and tracked so a second
            // click re-focuses the existing window instead of stacking copies.
            shell.FractalParamsRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_renderHost == null) return;
                    var vs = s_renderHost.ViewState;

                    // UserEquation / Sandbox / UserBulb carry their own
                    // source-compiled editors (source textbox + knobs), not the
                    // generic FractalParamsView. Route each to its dedicated
                    // window — mirrors legacy MainForm.ShowUserEquationDialog /
                    // ShowSandboxDialog / ShowUserBulbDialog.
                    switch (vs.FractalType)
                    {
                        case global::FracturingFog.FractalType.UserEquation:
                            OpenUserEquationEditor(vs.FractalParameters);
                            return;
                        case global::FracturingFog.FractalType.Sandbox:
                            OpenSandboxEditor(vs.FractalParameters);
                            return;
                        case global::FracturingFog.FractalType.UserBulb:
                            OpenUserBulbEditor(vs.FractalParameters);
                            return;
                    }

                    // Already open → bring to front rather than duplicate.
                    if (s_paramsWin != null)
                    {
                        s_paramsWin.Activate();
                        return;
                    }

                    var vm = new FractalParamsViewModel(
                        vs.FractalType,
                        vs.FractalParameters,
                        ifsPresets: new List<string>(IFSPresets.All.Keys),
                        lsystemPresets: new List<string>(LSystemPresets.All.Keys),
                        attractorPresets: null,
                        attractorDefaults: global::FracturingFog.AttractorCalculator.DefaultParams);
                    vm.ParamChanged += () => s_renderHost?.Trigger();

                    var win = new FractalParamsView { DataContext = vm };
                    win.Closed += (_, _) => s_paramsWin = null;
                    s_paramsWin = win;

                    var owner = AvaloniaDialogs.ActiveMainWindow;
                    if (owner != null) win.Show(owner);
                    else win.Show();
                });
            };

            // Recording finished — the engine has finalised the temp MP4 and/or
            // PNG sequence. On success, prompt for save destinations; on cancel
            // or fault, discard the temp artefacts. Fires on a background thread
            // → marshal the prompts onto the UI thread.
            ((IVideoZoomController)s_renderHost!).RecordingFinished += (_, result) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try { await HandleRecordingFinished(result); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaShellBootstrap] RecordingFinished failed: {ex.Message}");
                    }
                });
            };
        }

        // ── Source-compiled editors (UserEquation / Sandbox / UserBulb) ──────
        //
        // These three fractal types carry a dedicated editor window (source
        // textbox + per-type knobs) rather than the generic FractalParamsView.
        // The VMs are UI-agnostic: they raise CompileRequested / RenderRequested
        // / PromotionChanged plus synchronous prompt callbacks the host fills
        // here. Compile runs through FractalRenderHost's CompileXxx wrappers so
        // the calculator types stay inside the main project. Mirrors legacy
        // MainForm.ShowUserEquationDialog / ShowSandboxDialog / ShowUserBulbDialog.
        //
        // PromotionChanged is intentionally not wired: the Avalonia fractal-type
        // combo is bound to the fixed FractalType enum (MainViewModel.FractalTypes)
        // and does not surface promoted named equations as extra entries the way
        // the legacy WinForms combo did.

        private static void OpenUserEquationEditor(global::FracturingFog.Models.FractalParameters p)
        {
            if (s_renderHost == null) return;
            if (s_userEqWin != null) { s_userEqWin.Activate(); return; }

            var vm = new UserEquationViewModel(p);
            vm.CompileRequested += () =>
            {
                var (ok, error) = s_renderHost!.CompileUserEquation(p.UserEquationSource ?? "return z*z + c;");
                vm.ShowError(error);
                if (ok) s_renderHost.Trigger();
            };
            vm.RenderRequested += () => s_renderHost!.Trigger();
            vm.NamePromptRequested += def => PromptName("Save Equation", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNo($"Delete saved equation \"{name}\"?", "Delete Equation");
            vm.ConfirmOverwriteRequested += name => ConfirmYesNo(
                $"A saved equation named \"{name}\" already exists.\n\nOverwrite it?",
                "Overwrite Equation");
            vm.HotLoadRequested += (eq, baseName) =>
            {
                try
                {
                    var result = FracturingFog.CalculatorGen.CalculatorGenHotLoad
                        .TryCompileAndLoad(eq, baseName);
                    if (!result.Ok) return result.Error;
                    int w = s_renderHost!.Mandelbrot.Width;
                    int h = s_renderHost.Mandelbrot.Height;
                    var calc = (FracturingFog.Interefaces.IFractalCalculator?)
                        Activator.CreateInstance(result.CalculatorType!, w, h);
                    if (calc == null) return "Activator returned null.";
                    s_renderHost.SetDynamicAltCalculator(calc);
                    return null;
                }
                catch (Exception ex)
                {
                    return $"Hot-load failed: {ex.GetType().Name}: {ex.Message}";
                }
            };

            var win = new UserEquationView { DataContext = vm };
            win.Closed += (_, _) => s_userEqWin = null;
            s_userEqWin = win;

            ShowEditor(win);
            vm.TriggerCompile();
        }

        private static void OpenSandboxEditor(global::FracturingFog.Models.FractalParameters p)
        {
            if (s_renderHost == null) return;
            if (s_sandboxWin != null) { s_sandboxWin.Activate(); return; }

            var vm = new SandboxViewModel(p);
            vm.CompileRequested += () =>
            {
                var (ok, error) = s_renderHost!.CompileSandbox(p.SandboxSource ?? "z*z + c");
                vm.ShowError(error);
                if (ok) s_renderHost.Trigger();
            };
            vm.NamePromptRequested += def => PromptName("Save Sandbox Equation", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNo($"Delete saved sandbox equation \"{name}\"?", "Delete");
            vm.ConfirmOverwriteRequested += name => ConfirmYesNo(
                $"A saved sandbox equation named \"{name}\" already exists.\n\nOverwrite it?",
                "Overwrite Sandbox Equation");
            vm.SaveFilePromptRequested += defName =>
                PickSaveSync("Export Sandbox Equations", "JSON (*.json)|*.json|All files (*.*)|*.*", defName);
            vm.OpenFilePromptRequested += () =>
                PickOpenSync("Import Sandbox Equations", "JSON (*.json)|*.json|All files (*.*)|*.*");
            vm.MessageRequested += (title, body, isErr) => ShowInfo(title, body, isErr);

            var win = new SandboxView { DataContext = vm };
            win.Closed += (_, _) => s_sandboxWin = null;
            s_sandboxWin = win;

            ShowEditor(win);
            vm.TriggerCompile();
        }

        private static void OpenUserBulbEditor(global::FracturingFog.Models.FractalParameters p)
        {
            if (s_renderHost == null) return;
            if (s_userBulbWin != null) { s_userBulbWin.Activate(); return; }

            var vm = new UserBulbViewModel(p);
            vm.CompileRequested += (_, _) =>
            {
                var (ok, error) = s_renderHost!.CompileUserBulb(p.UserBulbSource ?? string.Empty);
                vm.ShowError(error ?? string.Empty);
                if (ok) s_renderHost.Trigger();
            };
            vm.RenderRequested += (_, _) => s_renderHost!.Trigger();
            vm.NamePromptRequested += (_, e) => e.Result = PromptName(e.Caption, "Enter a name:", e.DefaultValue);
            vm.ConfirmDeleteRequested += (_, e) => e.Result = ConfirmYesNo(e.Message, "Confirm");
            vm.ConfirmOverwriteRequested += (_, e) => e.Result = ConfirmYesNo(e.Message, "Overwrite Bulb Equation");
            vm.OpenFilePromptRequested += (_, e) => e.Path = PickOpenSync(e.Title, e.Filter);
            vm.SaveFilePromptRequested += (_, e) => e.Path = PickSaveSync(e.Title, e.Filter, e.DefaultName);
            vm.MessageRequested += (_, msg) => ShowInfo("UserBulb", msg, false);
            vm.ExportMeshRequested += (_, e) =>
            {
                if (s_renderHost == null) return;
                try
                {
                    int tris = global::FracturingFog.Export.UserBulbMeshExporter.ExportObjVoxelSurface(
                        e.Path,
                        (x, y, z) => s_renderHost!.SampleUserBulbDE(x, y, z),
                        s_renderHost.UserBulbCenterX, -s_renderHost.UserBulbCenterY, 0,
                        e.Range, e.GridN);
                    ShowInfo("Mesh export", $"Exported {tris} triangles to {e.Path}", false);
                }
                catch (Exception ex)
                {
                    ShowInfo("Mesh export error", $"Export failed: {ex.Message}", true);
                }
            };

            // ~30 Hz animation pump. The VM advances t and raises RenderRequested
            // only when no frame is in flight; NotifyRenderDone re-opens the gate
            // off the host's AnimationFrameUploaded so timer ticks don't pile up.
            void OnFrameUploaded(object? _, EventArgs __) => vm.NotifyRenderDone();
            s_renderHost.AnimationFrameUploaded += OnFrameUploaded;

            var lastTick = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                double dt = (now - lastTick).TotalSeconds;
                lastTick = now;
                vm.AnimationTick(dt);
            };
            timer.Start();
            s_userBulbAnimTimer = timer;

            var win = new UserBulbView { DataContext = vm };
            win.Closed += (_, _) =>
            {
                timer.Stop();
                if (s_renderHost != null) s_renderHost.AnimationFrameUploaded -= OnFrameUploaded;
                s_userBulbAnimTimer = null;
                s_userBulbWin = null;
            };
            s_userBulbWin = win;

            ShowEditor(win);
            vm.TriggerCompile();
        }

        // ── ColorGen editor ─────────────────────────────────────────────────
        //
        // Roslyn-compile an algorithmic colour theme from the DSL the user
        // types into the editor, then either:
        //   • HotLoad: register the IColorMap into ColorPalette.HotLoadedPalettes
        //     and push it onto the active calculator immediately, no rebuild.
        //   • Generate: write the rendered C# under
        //     Models/ColorSchemes/Generated/{Name}.cs so it ships with the
        //     next build (and the user keeps a long-lived diffable file).
        // Single-instance window; second click re-focuses the existing one.
        private static void OpenColorGenEditor()
        {
            if (s_renderHost == null) return;
            if (s_colorGenWin != null) { s_colorGenWin.Activate(); return; }

            var vm = new ColorGenEditorViewModel();
            vm.NamePromptRequested += def => PromptName("Save ColorGen Theme", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNo($"Delete saved theme \"{name}\"?", "Delete Theme");
            vm.MessageRequested += (title, body, isErr) => ShowInfo(title, body, isErr);

            vm.HotLoadRequested += (source, className, themeName, description) =>
            {
                try
                {
                    var opts = new FracturingFog.ColorGen.GenerateOptions
                    {
                        ThemeName = themeName,
                        Category = "User",
                        Description = description ?? "",
                    };
                    var result = FracturingFog.ColorGen.ColorGenHotLoad
                        .TryCompileAndLoad(source, className, opts);
                    if (!result.Ok) return result.Error;
                    var map = (FracturingFog.Interefaces.IColorMap?)
                        Activator.CreateInstance(result.ColorMapType!);
                    if (map == null) return "Activator returned null.";
                    FracturingFog.Models.ColorPalette.RegisterHotLoaded(map);
                    s_renderHost!.ApplyColorMap(map);
                    // Refresh the FloatingMenu theme combo so the freshly
                    // registered entry appears in the dropdown without
                    // requiring the user to close and reopen the picker.
                    s_shell?.RefreshThemeListsFromService();
                    return null;
                }
                catch (Exception ex)
                {
                    return $"Hot-load failed: {ex.GetType().Name}: {ex.Message}";
                }
            };

            vm.GenerateRequested += (source, className, themeName, description) =>
            {
                try
                {
                    var opts = new FracturingFog.ColorGen.GenerateOptions
                    {
                        ThemeName = themeName,
                        Category = "User",
                        Description = description ?? "",
                    };
                    var gen = FracturingFog.ColorGen.ColorGenApi.Generate(source, className, opts);
                    if (!gen.Ok) return gen.Error;

                    string outDir = System.IO.Path.Combine(
                        AppContext.BaseDirectory, "..", "..", "..", "Models", "ColorSchemes", "Generated");
                    outDir = System.IO.Path.GetFullPath(outDir);
                    System.IO.Directory.CreateDirectory(outDir);
                    string outPath = System.IO.Path.Combine(outDir, $"{gen.ClassName}.cs");
                    System.IO.File.WriteAllText(outPath, gen.Source, new System.Text.UTF8Encoding(false));
                    return null;
                }
                catch (Exception ex)
                {
                    return $"Generate failed: {ex.GetType().Name}: {ex.Message}";
                }
            };

            var win = new ColorGenEditorView { DataContext = vm };
            win.Closed += (_, _) => s_colorGenWin = null;
            s_colorGenWin = win;

            ShowEditor(win);
        }

        private static void ShowEditor(Window win)
        {
            var owner = AvaloniaDialogs.ActiveMainWindow;
            if (owner != null) win.Show(owner);
            else win.Show();
        }

        // ── Region save-default name ─────────────────────────────────────────
        // Spec: prefill the Save-Region prompt with "{FractalType} - " by
        // default. When the current fractal type is UserEquation, Sandbox, or
        // UserBulb AND the active parameters carry a named equation, prefill
        // "{FractalType} - {EquationName} - " instead so the user just appends
        // a region-specific suffix. Promoted (RegisteredFractal) entries
        // already carry the equation name as their toolbar label, so the
        // params-side name is dropped to avoid duplicating it.
        private static string BuildRegionNameDefault(ShellViewModel shell)
        {
            string typeLabel = shell.Main.SelectedFractalEntry?.Label
                ?? shell.Main.SelectedFractalType.ToString();

            string? eqName = null;
            if (shell.Main.SelectedFractalEntry?.Promoted == null && s_renderHost != null)
            {
                var p = s_renderHost.ViewState.FractalParameters;
                eqName = shell.Main.SelectedFractalType switch
                {
                    FractalType.UserEquation => p.UserEquationName,
                    FractalType.Sandbox      => p.SandboxName,
                    FractalType.UserBulb     => p.UserBulbName,
                    _                        => null,
                };
            }

            return !string.IsNullOrWhiteSpace(eqName)
                ? $"{typeLabel} - {eqName} - "
                : $"{typeLabel} - ";
        }

        // ── Synchronous host prompts ─────────────────────────────────────────
        //
        // The source-editor VMs expect synchronous-return prompt callbacks
        // (Func<…>/EventArgs.Result), but Avalonia's dialog stack is async-only.
        // Rather than pump a nested dispatcher frame, lean on the Win32 common
        // dialogs already available here (this is a WinExe with UseWindowsForms):
        // they run their own modal message loop and return synchronously.

        private static string? PromptName(string title, string prompt, string defaultValue)
        {
            // Sync prompt for the source-editor VMs (Func<string,string?>).
            // Use a Windows Forms modal — visual styles are enabled at startup
            // so it renders with the system theme (no VB6 InputBox legacy
            // look), and its own message loop runs without recursing the
            // Avalonia dispatcher (the earlier RunJobs-spin approach
            // crashed on Cancel/X). Centred on the active Avalonia window
            // so the dialog appears over the editor that requested it.
            using var dlg = new System.Windows.Forms.Form
            {
                Text = string.IsNullOrEmpty(title) ? "Enter Name" : title,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(420, 124),
                AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi,
            };

            var lbl = new System.Windows.Forms.Label
            {
                Text = prompt ?? "Enter a name:",
                Location = new System.Drawing.Point(12, 12),
                Size = new System.Drawing.Size(396, 24),
                AutoEllipsis = true,
            };
            var box = new System.Windows.Forms.TextBox
            {
                Text = defaultValue ?? string.Empty,
                Location = new System.Drawing.Point(12, 40),
                Size = new System.Drawing.Size(396, 24),
                Anchor = System.Windows.Forms.AnchorStyles.Left
                       | System.Windows.Forms.AnchorStyles.Right
                       | System.Windows.Forms.AnchorStyles.Top,
            };
            var ok = new System.Windows.Forms.Button
            {
                Text = "OK",
                DialogResult = System.Windows.Forms.DialogResult.OK,
                Location = new System.Drawing.Point(252, 80),
                Size = new System.Drawing.Size(76, 28),
            };
            var cancel = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                DialogResult = System.Windows.Forms.DialogResult.Cancel,
                Location = new System.Drawing.Point(332, 80),
                Size = new System.Drawing.Size(76, 28),
            };

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(box);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            dlg.Shown += (_, _) => { box.SelectAll(); box.Focus(); };

            var result = dlg.ShowDialog();
            if (result != System.Windows.Forms.DialogResult.OK) return null;
            string r = box.Text;
            return string.IsNullOrWhiteSpace(r) ? null : r;
        }

        private static bool ConfirmYesNo(string message, string title)
            => System.Windows.Forms.MessageBox.Show(
                   message, title,
                   System.Windows.Forms.MessageBoxButtons.YesNo,
                   System.Windows.Forms.MessageBoxIcon.Question)
               == System.Windows.Forms.DialogResult.Yes;

        private static void ShowInfo(string title, string body, bool isError)
            => System.Windows.Forms.MessageBox.Show(
                   body, title,
                   System.Windows.Forms.MessageBoxButtons.OK,
                   isError ? System.Windows.Forms.MessageBoxIcon.Error
                           : System.Windows.Forms.MessageBoxIcon.Information);

        private static string? PickOpenSync(string title, string filter)
        {
            using var d = new System.Windows.Forms.OpenFileDialog
            {
                Title = string.IsNullOrEmpty(title) ? "Open" : title,
                Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
                CheckFileExists = true,
            };
            return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
        }

        private static string? PickSaveSync(string title, string filter, string defaultName)
        {
            using var d = new System.Windows.Forms.SaveFileDialog
            {
                Title = string.IsNullOrEmpty(title) ? "Save" : title,
                Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter,
                FileName = defaultName ?? string.Empty,
                OverwritePrompt = true,
            };
            return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
        }

        // ── #64 — Video recording save prompts ───────────────────────────────

        private static async Task HandleRecordingFinished(VideoRecordingResult result)
        {
            // Cancelled / faulted: nothing to keep — delete temp artefacts.
            if (result.Cancelled)
            {
                if (!string.IsNullOrEmpty(result.Mp4TempPath) && System.IO.File.Exists(result.Mp4TempPath))
                    try { System.IO.File.Delete(result.Mp4TempPath); } catch { }
                if (!string.IsNullOrEmpty(result.PngFolder) && System.IO.Directory.Exists(result.PngFolder))
                    try { System.IO.Directory.Delete(result.PngFolder, recursive: true); } catch { }
                return;
            }

            // 1. MP4 — SaveFileDialog then move the temp file into place.
            if (!string.IsNullOrEmpty(result.Mp4TempPath) && System.IO.File.Exists(result.Mp4TempPath))
                await PromptSaveMp4(result.Mp4TempPath!);

            // 2. PNG sequence — pick a destination folder, move the frames, then
            //    optionally encode with ffmpeg.
            if (!string.IsNullOrEmpty(result.PngFolder) && System.IO.Directory.Exists(result.PngFolder))
                await PromptSaveLossless(result.PngFolder!, result.Encode);
        }

        private static async Task PromptSaveMp4(string tempPath)
        {
            string? path = await AvaloniaDialogs.PickSaveFileAsync(
                "Save Video Zoom",
                suggestedName: $"FracturingFog_Zoom_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
                filter: "MP4 video (*.mp4)|*.mp4");

            if (string.IsNullOrEmpty(path))
            {
                try { System.IO.File.Delete(tempPath); } catch { }
                SetStatus("Recorded video discarded.");
                return;
            }

            try
            {
                System.IO.File.Move(tempPath, path, overwrite: true);
                SetStatus($"Video saved: {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                try { System.IO.File.Delete(tempPath); } catch { }
                await AvaloniaDialogs.ShowMessageAsync(
                    "Save Video", $"Failed to save video:\n{ex.Message}", expectsConfirmation: false);
            }
        }

        private static async Task PromptSaveLossless(string pngFolder, VideoLosslessEncode encode)
        {
            // 1. Pick destination folder for the PNG sequence.
            string? destFolder = await AvaloniaDialogs.PickFolderAsync(
                "Choose a folder to keep the lossless PNG sequence" +
                (encode != VideoLosslessEncode.None
                    ? " (an encoded video will also be written next to it)" : ""));

            if (string.IsNullOrEmpty(destFolder))
            {
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                SetStatus("Lossless PNG sequence discarded.");
                return;
            }

            // 2. Move temp folder contents into a uniquely-named subfolder.
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string finalFolder = System.IO.Path.Combine(destFolder, $"FracturingFog_Zoom_{stamp}");
            try
            {
                System.IO.Directory.CreateDirectory(finalFolder);
                foreach (string src in System.IO.Directory.EnumerateFiles(pngFolder))
                {
                    string dst = System.IO.Path.Combine(finalFolder, System.IO.Path.GetFileName(src));
                    System.IO.File.Move(src, dst, overwrite: true);
                }
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
            }
            catch (Exception ex)
            {
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                await AvaloniaDialogs.ShowMessageAsync(
                    "Save Lossless", $"Failed to move PNG sequence:\n{ex.Message}", expectsConfirmation: false);
                return;
            }

            SetStatus($"Lossless PNG sequence saved: {finalFolder}");

            if (encode == VideoLosslessEncode.None) return;
            if (!FfmpegEncoder.IsAvailable())
            {
                await AvaloniaDialogs.ShowMessageAsync(
                    "Save Lossless",
                    "ffmpeg.exe is no longer available — keeping PNG sequence only.",
                    expectsConfirmation: false);
                return;
            }

            // 3. Encode with ffmpeg next to the PNG folder.
            var preset = encode switch
            {
                VideoLosslessEncode.LosslessH264Mp4 => FfmpegEncoder.Preset.LosslessH264Mp4,
                VideoLosslessEncode.Ffv1Mkv => FfmpegEncoder.Preset.Ffv1Mkv,
                VideoLosslessEncode.HighQualityH264Mp4 => FfmpegEncoder.Preset.HighQualityH264Mp4,
                _ => FfmpegEncoder.Preset.LosslessH264Mp4,
            };
            string ext = FfmpegEncoder.DefaultExtensionFor(preset);
            string outPath = System.IO.Path.Combine(destFolder, $"FracturingFog_Zoom_{stamp}.{ext}");

            SetStatus($"Encoding lossless video → {System.IO.Path.GetFileName(outPath)} (ffmpeg)…");
            try
            {
                var (ok, log) = await FfmpegEncoder.EncodeAsync(
                    finalFolder, outPath, preset,
                    onProgressLine: line =>
                    {
                        if (line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase))
                            Dispatcher.UIThread.Post(() => SetStatus($"ffmpeg: {line.Trim()}"));
                    });
                if (ok)
                    SetStatus($"Encoded: {System.IO.Path.GetFileName(outPath)}");
                else
                    await AvaloniaDialogs.ShowMessageAsync(
                        "Save Lossless", "ffmpeg encode failed.\n\n" + log, expectsConfirmation: false);
            }
            catch (Exception ex)
            {
                await AvaloniaDialogs.ShowMessageAsync(
                    "Save Lossless", $"ffmpeg encode exception:\n{ex.Message}", expectsConfirmation: false);
            }
        }

        // ── MiniMap thumbnail ────────────────────────────────────────────────
        //
        // Renders a 220×180 thumbnail of the active fractal at its
        // MiniMapDefaults framing on a background task, then pushes the
        // resulting Avalonia Bitmap into the ShellViewModel's MiniMap VM.
        // Mandelbrot only for now; other types render a placeholder via the
        // MiniMapViewModel.IsSupported path.
        private static void RenderMiniMapAsync(ShellViewModel shell)
        {
            if (shell == null) return;
            var type = shell.Main.ViewState.FractalType;
            if (!FracturingFog.Models.MiniMapDefaults.IsSupported(type)) return;
            if (s_renderHost == null) return;

            var bounds = FracturingFog.Models.MiniMapDefaults.For(type);
            var map = s_renderHost.Mandelbrot.ColorMap;
            int iters = FracturingFog.Models.MiniMapDefaults.IterationsFor(type);
            const int W = 220, H = 180;
            var fractalParams = shell.Main.ViewState.FractalParameters;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                uint[]? bgra = null;
                try
                {
                    // Use PosterRenderer's capture-calculator factory so the
                    // thumbnail reflects the active fractal type (Burning Ship,
                    // Julia, etc.) instead of always showing Mandelbrot.
                    var altReq = new FracturingFog.Imaging.PosterRequest
                    {
                        FractalType = type,
                        Width = W,
                        Height = H,
                        CenterX = bounds.CenterX,
                        CenterY = bounds.CenterY,
                        Zoom = bounds.Zoom,
                        MaxIterations = iters,
                        ColorMap = map,
                        Quality = QualityPreset.Standard,
                        FractalParameters = fractalParams ?? new FractalParameters(),
                    };

                    var alt = FracturingFog.Imaging.PosterRenderer.BuildCaptureCalculator(altReq);
                    if (alt != null)
                    {
                        alt.Calculate(System.Threading.CancellationToken.None);
                        bgra = alt.ColorBuffer;
                    }
                    else
                    {
                        // Mandelbrot path.
                        var calc = new MandelbrotCalculator(W, H)
                        {
                            CenterX = bounds.CenterX,
                            CenterY = bounds.CenterY,
                            Zoom = bounds.Zoom,
                            MaxIterations = iters,
                            ColorMap = map,
                            Quality = QualityPreset.Standard,
                        };
                        calc.Calculate(System.Threading.CancellationToken.None);
                        bgra = calc.ColorBuffer;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] MiniMap render failed: {ex.Message}");
                    return;
                }

                if (bgra == null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var bmp = BgraToBitmap(bgra, W, H);
                        shell.MiniMap.SetThumbnail(bmp);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaShellBootstrap] MiniMap upload failed: {ex.Message}");
                    }
                });
            });
        }

        private static global::Avalonia.Media.Imaging.Bitmap BgraToBitmap(uint[] bgra, int w, int h)
        {
            // WriteableBitmap fully owns its pixel storage — no dangling-
            // pointer risk that arises with the IntPtr Bitmap ctor when the
            // backend defers the copy.
            var bmp = new global::Avalonia.Media.Imaging.WriteableBitmap(
                new global::Avalonia.PixelSize(w, h),
                new global::Avalonia.Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                global::Avalonia.Platform.AlphaFormat.Premul);
            byte[] bytes = new byte[w * h * 4];
            Buffer.BlockCopy(bgra, 0, bytes, 0, bytes.Length);
            using (var fb = bmp.Lock())
            {
                if (fb.RowBytes == w * 4)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, bytes.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            bytes, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
                    }
                }
            }
            return bmp;
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
            s_preSpanDecorations = win.SystemDecorations;
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
            win.SystemDecorations = SystemDecorations.None;
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
            win.SystemDecorations = s_preSpanDecorations;
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

        // Status helper — null-conditional can't sit on an assignment LHS, so
        // route status updates through a guarded setter. Callers are already on
        // the UI thread (recording prompts run inside a Dispatcher.Post).
        private static void SetStatus(string text)
        {
            var sh = s_shell;
            if (sh != null) sh.Main.SetStatus(text);
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
                try { NativeMouseForwarder.Detach(); } catch { /* ignore */ }
                try { s_userBulbAnimTimer?.Stop(); } catch { /* ignore */ }
                s_userBulbAnimTimer = null;
                try { s_userEqWin?.Close(); }   catch { /* ignore */ } s_userEqWin = null;
                try { s_sandboxWin?.Close(); }  catch { /* ignore */ } s_sandboxWin = null;
                try { s_userBulbWin?.Close(); } catch { /* ignore */ } s_userBulbWin = null;
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
