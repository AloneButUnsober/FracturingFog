// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

using FracturingFog.UI.Avalonia.Services;

using FracturingFog.Abstractions;
using FracturingFog.Audio;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Silk;
using FracturingFog.Rendering.Vulkan;   // V3-GUI (#57): VulkanComputeKernel
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
    // S-X1 (2026-06-23) — IBootstrapHooks.NativeInputBridge + IBootstrapHooks.ColorSampleBridge contracts
    // live in Hosting/BootstrapHookContracts.cs so FracturingFog.Win can
    // implement them against a Hosting ProjectReference.

    public static class AvaloniaShellBootstrap
    {
        // Win-only service hooks live on BootstrapHooks (Hosting.dll) so
        // FracturingFog.Win can write them through a Hosting ProjectReference.

        // Read-only accessors for the Win-only installer + diagnostics.
        public static IGpuSurface? CurrentSurface => s_surface;
        public static ShellViewModel? CurrentShell => s_shell;

        private static IFractalRenderer? s_renderer;
        private static FractalRenderHost? s_renderHost;
        private static FractalInputController? s_input;
        private static ShellViewModel? s_shell;
        // #189 feature 4 — the in-flight poster/high-res render's cancellation
        // source, or null when no poster is rendering. Clicking Poster again while
        // this is non-null cancels the render instead of starting a new one.
        private static CancellationTokenSource? s_posterCts;
        private static IGpuSurface? s_surface;
        private static HostColorThemeService? s_themeService;
        // Hybrid-shell: the feature views are UserControls wrapped in a generic
        // PanelHostWindow (chrome + close). These modeless editors are
        // close-and-destroy (Closed => field null; reopen builds fresh), unlike
        // the hide-on-close MainWindow Sync* windows.
        private static PanelHostWindow? s_paramsWin;

        // S2 — the standalone Volumetric Lighting & FX window is owned by
        // WindowService (single app-wide instance, always owned by the main
        // window so no calling panel can cascade it shut). The menu toggles it;
        // the Params / Relief 3D panels open-or-refocus it.

        // #147 — standalone Relief 3D panel. Its own
        // FractalParamsViewModel over the shared ViewState, independent of the
        // Fractal Params window so closing Params leaves Relief 3D open, and
        // launchable straight from the Control Center. Re-focus if already open.
        private static PanelHostWindow? s_relief3DWin;

        // Standalone Big Buttons kid dialog (Color / Place / Show). Large,
        // resizable, host-owned — bound to a BigButtonsViewModel over the shell.
        // Re-focus if already open. Nulled on close.
        private static PanelHostWindow? s_bigButtonsWin;

        // Dedicated source-compiled editors (one window each, modeless).
        private static PanelHostWindow? s_userEqWin;
        private static PanelHostWindow? s_sandboxWin;
        private static PanelHostWindow? s_userBulbWin;
        private static PanelHostWindow? s_colorGenWin;
        private static DispatcherTimer? s_userBulbAnimTimer;

        // Audio-reactive backend — lazily created on first audio-reactive
        // slideshow start, reused across toggles. Stopped (not disposed) when
        // the slideshow ends so the meter timer in any open Audio Settings
        // dialog still shows live BPM until the user explicitly closes it.
        // Phase X.B / Slice B.4: swapped from AudioEngine to the
        // IAudioCaptureBackend + AudioCaptureDriver split. The Win-only
        // WindowsNAudioBackend keeps WASAPI loopback + WaveOutEvent in play
        // here; future cross-platform App bootstrap will pick NoopAudioBackend
        // (analyzer-only) on Linux/macOS.
        private static IAudioCaptureBackend? s_audioBackend;
        private static AudioCaptureDriver? s_audioDriver;
        // #277 — audio-reactive slideshow demand flag, one input to the capture
        // reconcile predicate (the toggle consumers are read live off the VMs).
        private static bool s_slideshowAudioDemand;

        private static readonly object s_gate = new();

        // Cached latest RenderFrameInfo — populated from FractalRenderHost
        // FrameCompleted. Used to build the suggested save-file name with
        // current CX/CY/Zoom/Iter/W/H without reaching back into the host.
        private static RenderFrameInfo? s_lastFrame;

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

        // Phase 2.4 cross-platform: registers the Silk.NET OpenGL backend as
        // RendererFactory.NonWin32Backend so X11 / CAMetalLayer / Wayland
        // surfaces can be served when the DX path is unavailable. Kept in a
        // static ctor (rather than at first OnSurfaceReady call) because the
        // factory hook must be live before any IGpuSurface arrives — Avalonia
        // can raise the SurfaceReady event on a worker thread.
        static AvaloniaShellBootstrap()
        {
            RendererFactory.NonWin32Backend = TryCreateSilkRenderer;

            // S-X7.2 (2026-06-23) — Linux native input bridge. Avalonia's
            // NativeControlHost child X11 subwindow swallows pointer events
            // before the XAML InputSponge can see them (same cause as the
            // Win+DX swap-chain HWND case). X11InputBridge XSelectInputs on
            // the foreign XID and forwards to IFractalInputController.
            // Windows installs its own NativeMouseForwarder via
            // FracturingFog.Win.WindowsBootstrap; on Linux we wire the X11
            // bridge here so the bootstrap installs whichever is present.
            if (OperatingSystem.IsLinux() && BootstrapHooks.NativeInputBridge == null)
                BootstrapHooks.NativeInputBridge = new X11InputBridge();

            // S-X8 (2026-06-27) / S-X11 (2026-07-28, #123) — Linux desktop pixel
            // sampler. Was unwired (BootstrapHooks.ColorSampleBridge null on
            // Linux) so the Color Theme Editor's per-stop Sample button silently
            // no-op'd. Windows hosts install WindowsColorSampleBridge ahead of
            // this in WindowsBootstrap.Install.
            //
            // Selector, gated on session type (the two bridges are mutually
            // exclusive — never run together):
            //   * Wayland (incl. XWayland-hosted FF) → PortalColorSampleBridge:
            //     org.freedesktop.portal.Screenshot.PickColor, the only sanctioned
            //     desktop-wide read there. Raw XGrabPointer + XGetImage cannot work
            //     under Wayland by design, so routing a Wayland session to X11 (the
            //     pre-#123 behaviour when WAYLAND_DISPLAY was set) just fails.
            //   * Xorg → X11ColorSampleBridge: XGrabPointer root with a crosshair
            //     cursor, sample the next button-press via XGetImage.
            if (OperatingSystem.IsLinux() && BootstrapHooks.ColorSampleBridge == null)
            {
                // One-time notice, shared by both bridges: the first out-of-FF
                // sample that fails for a reason other than the user cancelling
                // (X11 grab rejected; or a portal-less compositor like wlroots/sway
                // with no PickColor) tells the user once that desktop sampling
                // won't work this session. Each bridge raises its event at most
                // once per process; do not message again.
                Action showUnavailableNotice = () =>
                    _ = AvaloniaDialogs.ShowMessageAsync(
                        "Colour sampler",
                        "Screen colour sampling outside Fracturing Fog isn't available in "
                        + "this session (a Wayland/portal limitation). Colour sampling will "
                        + "only work within the Fracturing Fog window for the rest of this "
                        + "session.\n\nThis notice won't appear again.",
                        expectsConfirmation: false);
                X11ColorSampleBridge.ExternalSampleUnavailable += showUnavailableNotice;
                PortalColorSampleBridge.ExternalSampleUnavailable += showUnavailableNotice;

                bool wayland =
                    string.Equals(
                        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                        "wayland", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(
                        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

                BootstrapHooks.ColorSampleBridge = wayland
                    ? new PortalColorSampleBridge()
                    : new X11ColorSampleBridge();
            }

            // Phase X.5 / Slice 5.2 — register Help → Hardware tab probes.
            // The callables read live state each time the user opens the
            // help window so they reflect the audio backend / ILGPU device
            // list at that moment, not at boot.
            HostHelpContentProvider.IlgpuDeviceProbe = ProbeIlgpuDevices;
            HostHelpContentProvider.AudioBackendProbe = ProbeAudioBackend;

            // Phase 16b / EXR — wire the UI-layer HDRI probe to the Engine's
            // HdriRegistry. Lets the Avalonia file picker eagerly pre-warm a
            // pick and surface load failures (unsupported EXR compression,
            // missing file, etc.) without UI taking a project reference on
            // the Engine. The TryLoadFromFile out-param is discarded — we
            // only need the success bool for status reporting; the registry
            // caches the image internally.
            FracturingFog.Rendering.Lighting.HdriProbe.TryLoad =
                path => FracturingFog.Rendering.Lighting.HdriRegistry.TryLoadFromFile(path, out _);
        }

        /// <summary>True when an env var is set to an affirmative value
        /// (1/true/yes/on, case-insensitive). Used for opt-in feature gates
        /// like FF_GPU_PERTURB (V6 #82 D3D deep-zoom perturbation).</summary>
        private static bool IsTruthyEnv(string name)
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v)) return false;
            v = v.Trim();
            return v.Equals("1", StringComparison.Ordinal)
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ProbeIlgpuDevices()
        {
            try
            {
                using var ctx = ILGPU.Context.Create(b => b.Default());
                var devices = ctx.Devices.ToList();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"  Devices ({devices.Count}):");
                foreach (var d in devices)
                    sb.AppendLine($"    {d.AcceleratorType,-12}  {d.Name}");
                var preferred = devices.FirstOrDefault(
                                    d => d.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU)
                                ?? ctx.GetPreferredDevice(preferCPU: true);
                sb.Append($"  Preferred: {preferred.AcceleratorType}  {preferred.Name}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"  (ILGPU probe failed: {ex.Message})";
            }
        }

        private static string? ProbeAudioBackend()
        {
            var be = s_audioBackend;
            if (be == null) return null;
            return $"  Backend: {be.GetType().Name}\n  Capabilities: {be.Capabilities}";
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
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers, ctx.ReleaseCurrent);
                    }
                    case GpuSurfaceKind.Win32Hwnd:
                    {
                        // Only reached when the DX path declined the surface
                        // (force-fallback path for parity testing). Normal
                        // Windows runs short-circuit before this hook fires.
                        var ctx = SilkWin32ContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers, ctx.ReleaseCurrent);
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
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers, ctx.ReleaseCurrent);
                    }
                    case GpuSurfaceKind.WaylandSurface:
                    {
                        // Wayland native: EGL bound to GL (not GLES), 3.3 core
                        // forward-compatible. The adapter opens its own
                        // wl_display_connect so it does not require Avalonia
                        // to surface its internal display pointer.
                        var ctx = SilkEglContextAdapter.CreateFor(surface);
                        return SilkRendererFactory.Create(
                            ctx.Gl, surface, ctx.MakeCurrent, ctx.SwapBuffers, ctx.ReleaseCurrent);
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

            // Wave 2.3 — warm-load any user-persisted calculators
            // (%LOCALAPPDATA%/FracturingFog/UserCalculators/*.cs) before the
            // first UserEquation editor open so Compile & Load is a cache
            // hit on the equations the user previously saved.
            try
            {
                var persisted = FracturingFog.CalculatorGen.CalculatorGenHotLoad.LoadAllPersisted();
                foreach (var entry in persisted)
                {
                    if (entry.CalculatorType != null)
                        Console.WriteLine($"[Persist] warm-loaded {entry.ClassName} ← {entry.SourcePath}");
                    else
                        Console.Error.WriteLine($"[Persist] skip {entry.ClassName}: {entry.Error}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Persist] scan failed: {ex.Message}");
            }

            // ── Engines ──────────────────────────────────────────────────
            var viewState = new FractalViewState();
            var initialMap = ColorPalette.GetPaletteByName("HSV");
            s_renderHost = new FractalRenderHost(s_renderer, viewState, w, h, initialMap);
            // Phase X.0 / Slice 0.1c: install the D3D11-backed IGpuKernel
            // factory. Engine cannot construct the kernel itself because
            // MandelbrotGpuKernel lives in Rendering.D3D and owns Vortice
            // handles; the host knows the live renderer and can downcast.
            // Non-D3D11 renderers (Silk GL, Skia CPU) return null so
            // UseGpuCompute stays off silently.
            //
            // S-X1 carve: WindowsBootstrap.Install populates BootstrapHooks.GpuKernelFactoryHook
            // with the DirectXRenderer downcast + MandelbrotGpuKernel construct.
            // On Linux/macOS the hook is null so UseGpuCompute stays off.
            if (BootstrapHooks.GpuKernelFactoryHook != null)
                s_renderHost.GpuKernelFactory = BootstrapHooks.GpuKernelFactoryHook;

            // #162 (Slice 3d): install the D3D11 relief-raymarch kernel factory the
            // same way — WindowsBootstrap populates the hook with the DirectXRenderer
            // downcast + ReliefRaymarchGpuKernel construct. Null on Linux/macOS, where
            // the Vulkan branch below wires its own relief factory. The GPU relief
            // path stays opt-in (FractalParameters.Relief2DGpuRaymarch); the CPU
            // raymarch runs whenever no kernel is attached or the flag is off.
            if (BootstrapHooks.ReliefKernelFactoryHook != null)
                s_renderHost.ReliefKernelFactory = BootstrapHooks.ReliefKernelFactoryHook;

            // V3-GUI (#57): --renderer vulkan attaches the cross-platform Vulkan
            // compute kernel. It is independent of the present renderer (the Silk
            // GL blit) — the kernel owns its own VulkanContext — so we install it
            // here rather than downcasting the renderer like the D3D hook does.
            // Only when Vulkan is explicitly selected AND a device exists; with no
            // device we log and leave the factory unset, so UseGpuCompute stays
            // off and the CPU path handles compute while GL still presents.
            if (RendererFactory.PreferredBackend == RendererBackend.Vulkan
                && s_renderHost.GpuKernelFactory == null)
            {
                string? vkDev = VulkanComputeKernel.ProbeDeviceName();
                if (vkDev != null)
                {
                    s_renderHost.GpuKernelFactory = (_, _) => VulkanComputeKernel.TryCreateWithOwnContext();
                    // #162 (Slice 3d): the relief raymarch kernel also owns its own
                    // VulkanContext, so wire it independently of the present renderer
                    // just like the compute kernel above. Opt-in per frame.
                    s_renderHost.ReliefKernelFactory = (_, _) => ReliefRaymarchVulkanKernel.TryCreateWithOwnContext();
                    RendererFactory.VulkanProbeBackend = () => $"Vulkan compute ({vkDev}) + OpenGL (present)";
                    // Default GPU compute ON for an explicit --renderer vulkan
                    // session — the setter constructs the kernel now via the
                    // factory. If construction returns null the host leaves it
                    // off silently and the CPU path takes over.
                    s_renderHost.UseGpuCompute = true;
                    Console.Error.WriteLine($"[Vulkan] compute backend selected: {vkDev}");

                    // V6 (#82) — deep-zoom GPU perturbation. Enable only when the
                    // device advertises shaderFloat64 (the double δ kernel needs
                    // it); otherwise deep zoom stays on the CPU. The per-frame
                    // gate in MandelbrotCalculator also checks the live kernel's
                    // SupportsPerturbation, so this is belt-and-braces. Off by
                    // default everywhere else — only an explicit Vulkan session
                    // opts in.
                    bool fp64 = VulkanComputeKernel.ProbeSupportsFloat64();
                    FracturingFog.MandelbrotCalculator.UseGpuPerturbation = fp64;
                    Console.Error.WriteLine(fp64
                        ? "[Vulkan] deep-zoom GPU perturbation ENABLED (shaderFloat64 present)."
                        : "[Vulkan] deep-zoom GPU perturbation disabled (no shaderFloat64); deep zoom stays CPU.");
                }
                else
                {
                    Console.Error.WriteLine(
                        "[Vulkan] --renderer vulkan requested but no Vulkan device was found; " +
                        "falling back to CPU compute (OpenGL present unaffected).");
                }
            }
            // V6 (#82) — deep-zoom GPU perturbation on the D3D (default Windows)
            // backend. Held behind an explicit opt-in (env FF_GPU_PERTURB=1) —
            // NOT default-on — because it changes user-facing deep-zoom
            // rendering and wants on-device parity sign-off, the same posture as
            // the Vulkan enable (which is gated behind explicit --renderer
            // vulkan). When opted in AND a D3D kernel factory is present, force
            // GPU compute on so the kernel attaches now, then flip the master
            // perturbation toggle; the per-frame gate still checks the live
            // kernel's SupportsPerturbation, so a device without
            // DoublePrecisionFloatShaderOps self-disables and deep zoom stays
            // CPU. Left off entirely when the env is unset.
            else if (BootstrapHooks.GpuKernelFactoryHook != null
                     && IsTruthyEnv("FF_GPU_PERTURB"))
            {
                // DEEP-ONLY opt-in. Toggle UseGpuCompute on then off: the `true`
                // assignment lazily constructs + attaches the D3D kernel via the
                // factory; the `false` leaves the kernel attached but disables
                // the SHALLOW (zoom ≤ 1e4) GPU dispatch. We deliberately do NOT
                // leave the shallow path on — it is a separate, user-toggled
                // feature, and forcing it from startup put a GPU dispatch on the
                // very first (shallow) frames, which raced window resize. Deep
                // perturbation gates only on UseGpuPerturbation + GpuKernel +
                // SupportsPerturbation (not UseGpuCompute), so this is all it
                // needs. Per-frame gate still checks the device's FP64 support,
                // so a non-FP64 D3D device self-disables and deep zoom stays CPU.
                s_renderHost.UseGpuCompute = true;    // constructs + attaches the kernel
                s_renderHost.UseGpuCompute = false;   // ...then disable the shallow GPU path
                FracturingFog.MandelbrotCalculator.UseGpuPerturbation = true;
                Console.Error.WriteLine(
                    "[D3D] FF_GPU_PERTURB set — deep-zoom GPU perturbation opted in " +
                    "(deep-only; shallow stays CPU). The per-frame gate checks " +
                    "DoublePrecisionFloatShaderOps, so deep zoom stays CPU if the " +
                    "device lacks FP64 shader ops.");
            }
            // Phase X.2 / Slice 2.6 — per-OS video-writer selection.
            //   * Windows: WindowsBootstrap supplies a Media Foundation Mp4Writer
            //     via BootstrapHooks.NativeVideoWriterFactoryHook. Returns null when MF init
            //     fails (driver edge case, locked-down Server SKU).
            //   * Linux/macOS: hook is null. Falls through to ffmpeg.
            //   * Either path: ffmpeg fallback when the native writer rejects.
            // null return propagates to the UI which surfaces "ffmpeg required"
            // via the existing IsEnabledForUser gating + FfmpegSetupDialog rescan
            // flow (Slice 2.5).
            s_renderHost.VideoWriterFactory = (path, w, h) =>
            {
                var native = BootstrapHooks.NativeVideoWriterFactoryHook?.Invoke(path, w, h);
                if (native != null) return native;
                if (FracturingFog.FfmpegEncoder.IsAvailable())
                {
                    try
                    {
                        return new FracturingFog.Imaging.FfmpegVideoWriter(
                            path, w, h,
                            fps: 30,
                            preset: FracturingFog.FfmpegEncoder.Preset.HighQualityH264Mp4);
                    }
                    catch { return null; }
                }
                return null;
            };
            s_renderHost.FrameCompleted += (_, info) => s_lastFrame = info;
            s_input = new FractalInputController(viewState);

            // The swap-chain HWND composites on top of all Avalonia content, so
            // the XAML InputSponge never receives a pointer event. The native
            // bridge (Windows: subclass via NativeMouseForwarder) forwards its
            // mouse messages into the controller.
            // (Runs on the UI thread — OnSurfaceReady fires from the native
            // control's CreateNativeControlCore.)
            //
            // S-X1 carve: on Linux/macOS BootstrapHooks.NativeInputBridge is null. Avalonia
            // PointerPressed events already bubble through MainWindow's
            // InputSponge because the GL/Skia render path doesn't composite a
            // separate native HWND on top of the XAML tree.
            if (BootstrapHooks.NativeInputBridge != null)
            {
                var bridge = BootstrapHooks.NativeInputBridge;
                bridge.Attach(surface.Handle, s_input);
                // Bridge native right-click release to the Avalonia shell so
                // MainWindow can open its context menu (Avalonia's own
                // ContextRequested never fires — WM_RBUTTONUP is swallowed by
                // the subclass so Windows never raises WM_CONTEXTMENU).
                bridge.ContextMenuRequested = wasDrag =>
                {
                    try { FracturingFog.UI.Avalonia.AvaloniaShell.ContextMenuRequested?.Invoke(wasDrag); }
                    catch { /* swallow — must not crash the native subclass */ }
                };
                // Bridge "mouse-down on render surface" to the shell so it can
                // pull keyboard focus back onto the InputSponge.
                bridge.FocusRequested = () =>
                {
                    try
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { FracturingFog.UI.Avalonia.AvaloniaShell.RenderSurfaceFocusRequested?.Invoke(); }
                            catch { /* swallow */ }
                        });
                    }
                    catch { /* swallow */ }
                };
                // Bridge "left-button down on render surface" to the Toy-Mode
                // window-drag hook.
                bridge.LeftDragWindowHook = () =>
                {
                    try { return FracturingFog.UI.Avalonia.AvaloniaShell.LeftDragWindowHook?.Invoke() ?? false; }
                    catch { return false; }
                };
            }

            // ── Services ─────────────────────────────────────────────────
            // Theme service holds a reference to the render host so its
            // ApplyTheme(name) path can push a freshly-built IColorMap
            // directly onto the renderer without UI.Avalonia having to see
            // the main-project IColorMap type. Stored statically so
            // WireShellHostEvents can reach it for the SaveRegion / Delete
            // / ReloadThemes flows.
            s_themeService = new HostColorThemeService(s_renderHost);
            var themeService = s_themeService;
            // Phase X.0 / Slice 0.3b — Hardware tab probe. Windows installs
            // WindowsD3D11BootstrapHooks.HardwareInfoProvider via the bootstrap hook so the
            // tab enumerates DXGI adapters + reports the D3D11 feature level.
            // Linux/macOS leave BootstrapHooks.HardwareInfoProvider null; the help content
            // provider falls back to platform-neutral text.
            var helpProvider = new HostHelpContentProvider(BootstrapHooks.HardwareInfoProvider);

            // Animation Roadmap Phase 6 — feed the real discrete-GPU signal into
            // the animated-param ceiling. Windows installs the DXGI-backed probe;
            // elsewhere the hook stays null and Detect() assumes an iGPU.
            if (BootstrapHooks.HardwareInfoProvider is { } hwInfo)
                FracturingFog.Abstractions.Animation.HardwareProfile.DiscreteGpuProbe
                    = hwInfo.HasDiscreteGpu;

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
            // #27 Phase 5a — convert saved C# equations to the safe DSL once the
            // store is loaded (backup-guarded, idempotent). After Phase 3 the
            // live path is DSL-only, so translatable saved equations are
            // persisted as DSL; untranslatable ones are left editable.
            try { FracturingFog.UserEquationDslMigration.Run(UserEquationStore.Instance); } catch { }
            try { SandboxEquationStore.Instance.Load(); }  catch { }
            try { UserBulbStore.Instance.Load(); }         catch { }
            // #27 / #211 — convert saved C# Vec3/Quat bulbs to the safe DSL once
            // the store is loaded (backup-guarded, idempotent). Mirrors the
            // equation migration above: after Phase 3 the live bulb path is
            // DSL-only, so translatable saved bulbs are persisted as DSL;
            // untranslatable ones are left editable.
            try { FracturingFog.UserBulbDslMigration.Run(UserBulbStore.Instance); } catch { }
            try { UserWatermarkStore.Instance.Load(); }    catch { }
            // Animation Roadmap P2/P4 — without this the library stays empty at
            // runtime, so the editor's Load dropdown, the Save-Region animation
            // combo, and the Animation slideshow's picker all see zero
            // animations (built-in seed included).
            try { AnimationLibrary.Instance.Load(); }      catch { }
            // Scene Engine Roadmap S5 — warm the scene library so the Asset
            // Manager's Scenes node and the Scene Editor's Load combo see the
            // built-in demos + any saved scenes on first open.
            try { SceneLibrary.Instance.Load(); }          catch { }

            // ── View model tree ──────────────────────────────────────────
            s_shell = new ShellViewModel(s_renderHost, s_input, themeService, helpProvider, PaletteService,
                assetSources: FracturingFog.Assets.AssetSourceRegistry.All());

            // Wire the slideshow-record sink factory. ShellViewModel asks for
            // one when the active SlideshowConfig has RecordSlideshow=true;
            // factory wraps PngSequenceWriter so UI.Avalonia stays free of
            // System.Drawing.
            s_shell.SlideshowRecorderFactory = (folder, w, h) =>
                new PngSlideshowFrameRecorder(folder, w, h);

            // Audio-reactive lifecycle. ShellViewModel calls these when a
            // slideshow with AudioReactive=true starts / stops. The host owns
            // the AudioEngine instance because UI.Avalonia only references the
            // IBeatSource abstraction (the NAudio capture stack lives in the
            // main WinExe). EnsureAudioEngineStarted is reconfig-safe so the
            // user can edit AudioSettings mid-session.
            s_shell.StartAudioReactive = () =>
            {
                s_slideshowAudioDemand = true;
                ReconcileAudioCapture();
                return s_audioDriver?.BeatSource;
            };
            // #277 — the slideshow no longer hard-stops shared capture; it drops
            // its demand and reconciles, so a still-active toggle (Pulse, Beat FX)
            // keeps audio alive, and nothing else keeps a File source playing.
            s_shell.StopAudioReactive = () =>
            {
                s_slideshowAudioDemand = false;
                ReconcileAudioCapture();
            };
            s_shell.GetAudioBeatCadence = () =>
            {
                var s = AudioSettingsStore.Load();
                return (Math.Max(1, s.BeatsPerTheme), Math.Max(1, s.BeatsPerRegion));
            };
            // #259 audio-reactive expansion — pure getter for the modulation
            // source (no side effects) plus an ensure-started hook the shell
            // fires when an audio-reactive consumer (ASCII FX, params) turns on
            // outside a slideshow. Left warm on toggle-off; slideshow stop still
            // owns the eventual capture stop.
            s_shell.GetAudioModulationSource = () => s_audioDriver?.ModulationSource;
            s_shell.EnsureAudioModulationStarted = EnsureAudioCaptureStarted;
            // #277 — reconcile hook: consumers call this on toggle on AND off so a
            // File source stops when the last consumer turns off (was left warm).
            s_shell.ReconcileAudioCapture = ReconcileAudioCapture;
            // #262 — Acid Fog beat-lock lives on MainViewModel's ambient loop.
            s_shell.Main.GetAudioModulationSource = () => s_audioDriver?.ModulationSource;
            s_shell.Main.EnsureAudioModulationStarted = EnsureAudioCaptureStarted;
            s_shell.Main.ReconcileAudioCapture = ReconcileAudioCapture;
            // #263 — audio→param modulation matrix manager (app-scoped), driving
            // fractal params through the shared render-gated animation bus.
            s_shell.AudioModulation = new FracturingFog.UI.Avalonia.ViewModels.Animation.AudioModulationManager(
                () => s_audioDriver?.ModulationSource,
                ReconcileAudioCapture,
                () => s_shell.Main.ViewState.FractalParameters,
                () => s_shell.Main.ViewState.FractalType,
                () => FracturingFog.UI.Avalonia.ViewModels.Animation.AnimationBusHost.Bus);

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
            // Watermark library combo — seeded from the user store loaded above.
            s_shell.FloatingMenu.SetWatermarks(UserWatermarkStore.Instance.EnumerateNames());
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

            // Theme change → regenerate thumbnail with the new ColorMap.
            // Hook the render host's ColorMapChanged (not MainViewModel's
            // SelectedTheme PropertyChanged) so the render fires AFTER the
            // new IColorMap has been pushed across every calculator. The
            // SelectedTheme PropertyChanged is raised by RaiseAndSetIfChanged
            // before the ShellViewModel.ColorThemeChanged handler calls
            // _themeService.ApplyTheme(...), so reading ColorMap at that
            // point yielded the previous theme — the thumbnail rendered
            // with stale colours across every fractal type. ColorMapChanged
            // fires once the new map is propagated.
            s_renderHost.ColorMapChanged += (_, _) =>
            {
                if (s_shell == null) return;
                if (s_shell.IsMiniMapVisible) RenderMiniMapAsync(s_shell);
            };

            // Also regenerate when the user picks a new fractal type — that
            // path doesn't change the ColorMap so ColorMapChanged won't fire.
            s_shell.Main.PropertyChanged += (_, e) =>
            {
                if (s_shell == null) return;
                if (e.PropertyName == nameof(MainViewModel.SelectedFractalType)
                 || e.PropertyName == nameof(MainViewModel.SelectedFractalEntry))
                {
                    if (s_shell.IsMiniMapVisible) RenderMiniMapAsync(s_shell);

                    // Re-route any open params editor to the new fractal type's
                    // editor so the modal tracks the toolbar selection instead
                    // of stranding the user on the old type's knobs. Close the
                    // generic FractalParamsView and the source-compiled
                    // editors (UserEquation / Sandbox / UserBulb), then
                    // re-fire the request so the bootstrap picks the right
                    // window for the active type.
                    if (e.PropertyName == nameof(MainViewModel.SelectedFractalType)
                     || e.PropertyName == nameof(MainViewModel.SelectedFractalEntry))
                    {
                        bool wasOpen = s_paramsWin != null || s_userEqWin != null
                                    || s_sandboxWin != null || s_userBulbWin != null;
                        if (wasOpen)
                        {
                            try { s_paramsWin?.Close(); }   catch { /* ignore */ } s_paramsWin = null;
                            try { s_userEqWin?.Close(); }   catch { /* ignore */ } s_userEqWin = null;
                            try { s_sandboxWin?.Close(); }  catch { /* ignore */ } s_sandboxWin = null;
                            try { s_userBulbWin?.Close(); } catch { /* ignore */ } s_userBulbWin = null;
                            Dispatcher.UIThread.Post(() =>
                                s_shell?.ShowFractalParamsCommand.Execute().Subscribe());
                        }
                    }
                }
            };

            WireShellHostEvents(s_shell);

            // FFmpeg install / update launcher in the FloatingMenu. UI.Avalonia
            // can't see FfmpegSetupDialog (it lives in the WinExe alongside
            // FfmpegInstaller / FfmpegEncoder), so the routing happens here.
            s_shell.FloatingMenu.FfmpegSetupClick += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                    _ = FfmpegSetupDialog.ShowAsync(AvaloniaDialogs.ActiveMainWindow));

            // User-data location picker. Lets the user move every FracturingFog
            // settings file (presets, themes, equations, etc.) to a folder of
            // their choice. Honoured via AppDataPaths; effective on next launch
            // since the singletons cache their paths at first access.
            s_shell.FloatingMenu.AppDataLocationClick += (_, _) =>
                Dispatcher.UIThread.Post(async () => await ShowAppDataLocationDialogAsync());

            // Phase 3: start the 5-second probe that drives the status-bar
            // "● Server: running / off" indicator. Uses the default server
            // port (47823) unless a server-config.json under %APPDATA% overrides.
            s_shell.StartServerPing(FracturingFog.Server.ServerConfig.LoadOrDefault().Port);

            // First-run FFmpeg setup prompt. Spec: show the install offer if
            // ffmpeg.exe is missing AND the user has not previously elected
            // Manual or Skip (FfmpegPreferences.SuppressStartupPrompt). Posted
            // through the dispatcher so it appears after the main window is
            // shown rather than blocking surface init.
            Dispatcher.UIThread.Post(MaybeShowFfmpegStartupPrompt,
                DispatcherPriority.Background);

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

            // ── Export Scene… (Scene Engine S8 polish) ───────────────────
            //
            // The Scene Editor can't touch the Engine's SceneVideoRenderer
            // (UI.Avalonia stays Engine-free), so it hands the built SceneData +
            // export knobs here. Pick an output path, run the frame-locked
            // offline render on a background thread (one calculator live at a
            // time -> inside the ~90% cap), then report the outcome. ffmpeg
            // missing -> the render keeps a recoverable PNG sequence.
            // Phase 7 (#266) — Scene Editor "Browse…" for the export audio file.
            shell.SceneAudioFileBrowseRequested += async e =>
                e.Path = await AvaloniaDialogs.PickOpenFileAsync(e.Title, e.Filter);

            shell.ExportSceneRequested += async (_, args) =>
            {
                try
                {
                    var s = args.Settings;
                    var preset = s.Encode switch
                    {
                        SceneExportEncode.LosslessH264 => FracturingFog.FfmpegEncoder.Preset.LosslessH264Mp4,
                        SceneExportEncode.Ffv1         => FracturingFog.FfmpegEncoder.Preset.Ffv1Mkv,
                        _                              => FracturingFog.FfmpegEncoder.Preset.HighQualityH264Mp4,
                    };
                    string ext = FracturingFog.FfmpegEncoder.DefaultExtensionFor(preset);
                    string filter = ext == "mkv"
                        ? "Matroska Video (*.mkv)|*.mkv"
                        : "MP4 Video (*.mp4)|*.mp4";
                    string suggested = SanitizeFileStem(args.Scene.Name) + "." + ext;

                    string? path = await AvaloniaDialogs.PickSaveFileAsync("Export Scene", suggested, filter);
                    if (string.IsNullOrEmpty(path)) return; // cancelled

                    if (!FracturingFog.FfmpegEncoder.IsAvailable())
                        await AvaloniaDialogs.ShowMessageAsync("Export Scene",
                            "ffmpeg was not found, so the video can't be encoded. The rendered PNG frame " +
                            "sequence will be kept instead — you can encode it later.", false);

                    var opts = new FracturingFog.Export.SceneVideoOptions
                    {
                        Width = s.Width,
                        Height = s.Height,
                        Encode = preset,
                        OutputPath = path,
                        Settings = new FracturingFog.Abstractions.Animation.SceneRenderSettings
                        {
                            Fps = s.Fps,
                            MotionBlurSubframes = s.MotionBlurSubframes,
                            ShutterFraction = s.ShutterFraction,
                        },
                    };

                    // Status-bar "Rendering…" chip (with Cancel) while the job runs.
                    using var sceneCts = new CancellationTokenSource();
                    var busy = shell.BeginRenderBusy("Rendering scene…", () => { try { sceneCts.Cancel(); } catch { } });
                    FracturingFog.Export.SceneVideoResult result;
                    try
                    {
                        result = await Task.Run(() =>
                    {
                        // Phase 7 (#266) — deterministic audio-reactive export: bake
                        // the scene's audio file into a seekable modulation source
                        // and mux it into the encoded video. No file / no ffmpeg =
                        // audio-silent, exactly as before.
                        var scene = args.Scene;
                        if (scene.AudioTracks is { Count: > 0 }
                            && !string.IsNullOrWhiteSpace(scene.AudioFilePath))
                        {
                            shell.UpdateRenderBusy("Analysing audio…");
                            string? ff = FracturingFog.FfmpegEncoder.FindFfmpeg();
                            if (ff != null)
                            {
                                var aset = s_audioDriver?.Settings;
                                var baked = FracturingFog.Audio.OfflineAudioAnalysis.AnalyzeFile(
                                    scene.AudioFilePath, ff,
                                    aset?.Sensitivity ?? 0.5f, aset?.BandWeights);
                                if (baked != null)
                                {
                                    opts.AudioSource = baked;
                                    opts.AudioMuxPath = scene.AudioFilePath;
                                }
                            }
                        }
                        return FracturingFog.Export.SceneVideoRenderer.Render(scene, opts,
                            (frac, line) => shell.UpdateRenderBusy($"Rendering scene… {frac * 100:0}%"),
                            sceneCts.Token);
                        });
                    }
                    finally { busy.Dispose(); }

                    string msg = result.Ok
                        ? (!string.IsNullOrEmpty(result.VideoPath)
                            ? $"Scene exported ({result.FramesWritten} frames):\n{result.VideoPath}"
                            : $"Frames rendered ({result.FramesWritten}):\n{result.FrameFolder}")
                        : (result.Message ?? "Scene export failed.");
                    await AvaloniaDialogs.ShowMessageAsync("Export Scene", msg, false);
                }
                catch (OperationCanceledException)
                {
                    try { await AvaloniaDialogs.ShowMessageAsync("Export Scene", "Scene export cancelled.", false); }
                    catch { /* dialog itself failed */ }
                }
                catch (Exception ex)
                {
                    try { await AvaloniaDialogs.ShowMessageAsync("Export Scene", "Export failed: " + ex.Message, false); }
                    catch { /* dialog itself failed — logged below */ }
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Scene export failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            // ── Palette import / export / eyedropper ─────────────────────
            //
            // Editor sends ThemeImportPaletteEventArgs. Host pops an
            // OpenFilePicker filtered for PaletteBuilder JSON, GIMP .gpl,
            // CSS, hex/.txt. PaletteFileIO parses the file (kept in the
            // WinExe so UI.Avalonia stays free of palette-format code), then
            // a 3-button Add/Replace/Cancel dialog seals the operation.
            shell.ImportPaletteRequested += async (_, args) =>
            {
                try
                {
                    string? path = await AvaloniaDialogs.PickOpenFileAsync(
                        "Import Palette",
                        FracturingFog.Views.Editors.PaletteFileIO.ImportFilter);
                    if (string.IsNullOrEmpty(path)) return;

                    List<FracturingFog.Views.Editors.PaletteFileIO.Rgb> parsed;
                    try { parsed = FracturingFog.Views.Editors.PaletteFileIO.Load(path); }
                    catch (Exception ex)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Palette",
                            "Failed to read palette file:\n" + ex.Message,
                            expectsConfirmation: false);
                        return;
                    }

                    if (parsed.Count == 0)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Palette",
                            "No colors found in the file. Verify it is a PaletteBuilder JSON, GIMP .gpl, CSS, or hex list.",
                            expectsConfirmation: false);
                        return;
                    }

                    var choice = await AvaloniaDialogs.ShowAddOrReplaceAsync(
                        parsed.Count, args.CurrentCount, System.IO.Path.GetFileName(path));
                    if (choice == AvaloniaDialogs.AddOrReplaceResult.Cancel) return;

                    args.Colors = new List<(byte R, byte G, byte B)>(parsed.Count);
                    foreach (var c in parsed) args.Colors.Add((c.R, c.G, c.B));
                    args.Result = choice switch
                    {
                        AvaloniaDialogs.AddOrReplaceResult.Add     => ThemeImportPaletteEventArgs.Choice.Add,
                        AvaloniaDialogs.AddOrReplaceResult.Replace => ThemeImportPaletteEventArgs.Choice.Replace,
                        _                                          => ThemeImportPaletteEventArgs.Choice.Cancel,
                    };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ImportPalette failed: {ex.Message}");
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            shell.ExportPaletteRequested += async (_, args) =>
            {
                try
                {
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Export Palette",
                        args.SuggestedName,
                        FracturingFog.Views.Editors.PaletteFileIO.ExportFilter);
                    if (string.IsNullOrEmpty(path)) return;

                    // PaletteFileIO eats ColorStopData (WinExe DTO); convert
                    // from the abstraction-layer ColorStopDef the VM holds.
                    var native = new List<FracturingFog.Models.ColorStopData>(args.Stops.Count);
                    foreach (var s in args.Stops)
                        native.Add(new FracturingFog.Models.ColorStopData
                        {
                            Position = s.Position, R = s.R, G = s.G, B = s.B,
                        });

                    FracturingFog.Views.Editors.PaletteFileIO.Save(path, native, args.PaletteName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ExportPalette failed: {ex.Message}");
                    await AvaloniaDialogs.ShowMessageAsync(
                        "Export Palette",
                        "Failed to write palette file:\n" + ex.Message,
                        expectsConfirmation: false);
                }
                finally
                {
                    args.Completion.TrySetResult(true);
                }
            };

            shell.SampleColorRequested += (_, args) =>
            {
                // S-X1 carve: desktop pixel sampler is Win-only (low-level
                // mouse hook + GDI+ CopyFromScreen). When the bridge is null
                // (Linux/macOS) the request completes immediately without
                // picking — UI shows the prior swatch unchanged.
                var bridge = BootstrapHooks.ColorSampleBridge;
                Console.Error.WriteLine($"[AvaloniaShellBootstrap] SampleColorRequested fired. bridge={(bridge?.GetType().Name ?? "null")} IsActive={(bridge?.IsActive.ToString() ?? "n/a")}");
                Console.Error.Flush();
                if (bridge == null || bridge.IsActive)
                {
                    Console.Error.WriteLine("[AvaloniaShellBootstrap] Sample short-circuit: bridge null or already active.");
                    Console.Error.Flush();
                    args.Completion.TrySetResult(true);
                    return;
                }
                try
                {
                    Console.Error.WriteLine("[AvaloniaShellBootstrap] Calling bridge.Begin().");
                    Console.Error.Flush();
                    bridge.Begin(
                        picked =>
                        {
                            Console.Error.WriteLine($"[AvaloniaShellBootstrap] bridge picked RGB=({picked.R},{picked.G},{picked.B})");
                            Console.Error.Flush();
                            args.PickedR = picked.R;
                            args.PickedG = picked.G;
                            args.PickedB = picked.B;
                            args.Completion.TrySetResult(true);
                        },
                        () =>
                        {
                            Console.Error.WriteLine("[AvaloniaShellBootstrap] bridge cancelled.");
                            Console.Error.Flush();
                            args.Completion.TrySetResult(true);
                        });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Sample failed: {ex.Message}");
                    Console.Error.Flush();
                    args.Completion.TrySetResult(true);
                }
            };

            // Inspect hook: while the editor's Inspect checkbox is on every
            // left-click on the rendered fractal samples the pixel under the
            // cursor and routes the colour into the editor instead of
            // starting a pan. Hook stays installed for the program lifetime
            // and is a no-op when no editor is open or Inspect is unchecked.
            //
            // S-X1 carve: pixel sampling (Win32 ClientToScreen + screen GetPixel)
            // lives behind BootstrapHooks.NativeInputBridge.TrySampleClient. The shell-state
            // routing logic stays here.
            if (BootstrapHooks.NativeInputBridge != null)
            {
                var bridge = BootstrapHooks.NativeInputBridge;
                bridge.InspectClickHook = (clientX, clientY) =>
                {
                    var editor = s_shell?.ColorThemeEditor;
                    if (editor == null || !editor.AnyInspectActive) return false;
                    if (s_surface == null) return false;
                    if (!bridge.TrySampleClient(s_surface.Handle, clientX, clientY,
                                                 out byte r, out byte g, out byte b))
                        return false;
                    bool routeTo3D = editor.Inspect3DActive;
                    bool routeToBand = editor.InspectBandActive;
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (routeToBand) editor.HandleInspectBandColor(r, g, b);
                            else if (routeTo3D) editor.HandleInspect3DColor(r, g, b);
                            else editor.HandleInspectColor(r, g, b);
                        }
                        catch { }
                    });
                    return true;
                };
            }

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

            // Unsaved-changes prompt: the Color Theme Editor raises this
            // when dirty and the user switches theme / closes the window.
            // Host shows the three-button Save / Discard / Cancel modal and
            // writes the pick back into args.Result before signalling.
            shell.UnsavedChangesPromptRequested += async (_, args) =>
            {
                try
                {
                    var result = await AvaloniaDialogs.ShowSaveDiscardAsync(
                        "Unsaved Changes",
                        "You have unsaved changes to the current color theme.\n\n" +
                        "• Save — keep the editor open and focus the Name field so you can save manually.\n" +
                        "• Discard — drop your edits and continue.\n" +
                        "• Cancel — back out and stay on the current theme.");
                    args.Result = result switch
                    {
                        AvaloniaDialogs.MessageResult.Yes => UnsavedChangesChoice.Save,
                        AvaloniaDialogs.MessageResult.No  => UnsavedChangesChoice.Discard,
                        _                                 => UnsavedChangesChoice.Cancel,
                    };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] UnsavedChanges prompt failed: {ex.Message}");
                    args.Result = UnsavedChangesChoice.Cancel;
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
                    bool customWatermarkAvailable =
                        shell.Main.UseCustomWatermark
                        && shell.Main.ActiveCustomWatermark != null;

                    var animationNames = ((IColorThemeService)s_themeService!).EnumerateAnimationNames();
                    var prompt = await AvaloniaDialogs.PromptForSaveRegionAsync(
                        "Save Region", "Region name:", BuildRegionNameDefault(shell),
                        customWatermarkAvailable,
                        animationNames);

                    if (prompt is { } picked && !string.IsNullOrWhiteSpace(picked.Name) && s_renderHost != null)
                    {
                        var embedded = (customWatermarkAvailable && picked.IncludeWatermark)
                            ? shell.Main.ActiveCustomWatermark
                            : null;
                        bool ok = ((IColorThemeService)s_themeService!)
                            .SaveCurrentAsRegion(picked.Name, s_renderHost.ViewState, embedded, picked.AnimationName,
                                shell.AudioModulation?.ExportBindings());
                        if (ok)
                        {
                            // RefreshRegions honours the menu's active sort +
                            // fractal-type filter (unlike SetRegions with the
                            // unfiltered enumeration), so the just-saved name
                            // lands in the same bucket the user is browsing.
                            shell.FloatingMenu.RefreshRegions();
                            shell.FloatingMenu.SetRegionSilent(picked.Name);
                            shell.Main.SetRegionName(picked.Name);
                        }
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
                        suggestedName: BuildSuggestedFileName("png", isSpanning: s_spanning),
                        filter: "PNG image (*.png)|*.png");
                    if (string.IsNullOrEmpty(path)) return;
                    s_renderHost.SaveLastFrameToPng(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Screenshot failed: {ex.Message}");
                }
            };

            // ASCII / text-art export (#226) — save the current frame as
            // character art. The chosen file extension selects the format; the
            // exporter consumes the real IColorMap-coloured buffer + smooth field
            // off the render host.
            shell.AsciiArtRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Text Art",
                        suggestedName: BuildSuggestedFileName("html", isSpanning: s_spanning),
                        filter:
                            "HTML (coloured) (*.html)|*.html|" +
                            "SVG (vector, coloured) (*.svg)|*.svg|" +
                            "ANSI half-block (*.ans)|*.ans|" +
                            "ANSI per-character (*.ansi)|*.ansi|" +
                            "Plain text (*.txt)|*.txt|" +
                            "Braille dots (*.brl)|*.brl");
                    if (string.IsNullOrEmpty(path)) return;

                    var fmt = AsciiFormatFromExtension(path);
                    var opts = new FracturingFog.Imaging.AsciiArtOptions
                    {
                        Format = fmt,
                        Columns = 200,
                    };
                    // Monochrome-density formats read best on the fine ramp.
                    if (fmt == FracturingFog.Imaging.AsciiArtFormat.PlainText ||
                        fmt == FracturingFog.Imaging.AsciiArtFormat.Braille)
                        opts.WithFineRamp();

                    s_renderHost.SaveLastFrameAsAsciiArt(path, opts);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Text-art export failed: {ex.Message}");
                }
            };

            // Record the current view's ASCII FX animation (#230) — bakes the
            // active FX (preset / panel) over a short loop into a shareable file.
            shell.RecordAsciiRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;
                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Record ASCII Animation",
                        suggestedName: BuildSuggestedFileName("cast", isSpanning: s_spanning),
                        filter:
                            "asciinema cast (*.cast)|*.cast|" +
                            "Animated SVG (*.svg)|*.svg|" +
                            "ANSI frame sequence (*.ans)|*.ans|" +
                            "MP4 video (*.mp4)|*.mp4");
                    if (string.IsNullOrEmpty(path)) return;

                    string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

                    // Bake the active FX (or a static clip if none) over a ~5s,
                    // 12fps loop at export resolution.
                    var fx = shell.BuildAsciiFxSettings(0.0) ?? new FracturingFog.Imaging.AsciiFxSettings();
                    const double fps = 12.0;
                    const int frames = 60; // ~5 seconds
                    const int cols = 160;

                    if (ext == "mp4")
                    {
                        await ExportAsciiMp4Async(path, fx, cols, frames, fps, shell.AsciiRampFromColor);
                    }
                    else
                    {
                        string format = ext switch { "svg" => "svg", "ans" => "ans", _ => "cast" };
                        string? text = s_renderHost.RecordAsciiAnimation(
                            columns: cols, cellAspect: 2.0, invert: false, fineRamp: false,
                            rampFromColor: shell.AsciiRampFromColor, fx: fx,
                            frames: frames, fps: fps, format: format);
                        if (string.IsNullOrEmpty(text)) return;
                        await System.IO.File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] ASCII animation record failed: {ex.Message}");
                }
            };

            // Live ASCII recording (#230) — capture whatever is animating (zoom
            // video / Scene / slideshow / interactive) as it plays. Start on true;
            // on false freeze the capture, then pop a save dialog + serialise/encode.
            shell.AsciiRecordingToggleRequested += async (_, recording) =>
            {
                try
                {
                    if (s_renderHost == null) return;
                    if (recording) { s_renderHost.BeginLiveAsciiRecording(); return; }

                    int count = s_renderHost.StopLiveAsciiRecording();
                    if (count == 0) { s_renderHost.ClearPendingRecording(); return; }

                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Recorded ASCII",
                        suggestedName: BuildSuggestedFileName("cast", isSpanning: s_spanning),
                        filter:
                            "asciinema cast (*.cast)|*.cast|" +
                            "Animated SVG (*.svg)|*.svg|" +
                            "ANSI frame sequence (*.ans)|*.ans|" +
                            "MP4 video (*.mp4)|*.mp4");
                    if (string.IsNullOrEmpty(path)) { s_renderHost.ClearPendingRecording(); return; }

                    string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if (ext == "mp4")
                    {
                        var grids = s_renderHost.PendingRecordingFrames();
                        if (grids != null) await ExportFramesToMp4Async(path, grids, 20.0);
                    }
                    else
                    {
                        string format = ext switch { "svg" => "svg", "ans" => "ans", _ => "cast" };
                        string? text = s_renderHost.SerializePendingRecording(format);
                        if (!string.IsNullOrEmpty(text))
                            await System.IO.File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
                    }
                    s_renderHost.ClearPendingRecording();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Live ASCII recording failed: {ex.Message}");
                    try { s_renderHost?.ClearPendingRecording(); } catch { }
                }
            };

            // Wallpaper screenshot — render an offscreen image sized to the
            // union of every connected monitor's pixel bounds, regardless of
            // the current window state. Sidesteps the GNOME/Wayland limitation
            // where Span mode (borderless Topmost) cannot overlay the shell's
            // top bar + dock across multiple monitors: by going through
            // PosterRenderer we never touch the window chrome at all and the
            // output matches what Span+Screenshot produces on Windows.
            shell.WallpaperScreenshotRequested += async (_, _) =>
            {
                try
                {
                    if (s_renderHost == null) return;

                    var win = AvaloniaDialogs.ActiveMainWindow;
                    var screens = win?.Screens;
                    if (screens == null || screens.All.Count == 0)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Wallpaper",
                            "Could not enumerate monitors for the wallpaper render.",
                            expectsConfirmation: false);
                        return;
                    }

                    // Virtual-screen union (mirrors EnterSpanMode math). Screen
                    // bounds are in physical pixels — exactly what we want for
                    // the wallpaper render dimensions.
                    int minX = int.MaxValue, minY = int.MaxValue;
                    int maxX = int.MinValue, maxY = int.MinValue;
                    foreach (var s in screens.All)
                    {
                        var b = s.Bounds;
                        if (b.X < minX) minX = b.X;
                        if (b.Y < minY) minY = b.Y;
                        if (b.X + b.Width  > maxX) maxX = b.X + b.Width;
                        if (b.Y + b.Height > maxY) maxY = b.Y + b.Height;
                    }
                    int wpW = maxX - minX;
                    int wpH = maxY - minY;
                    if (wpW <= 0 || wpH <= 0)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Wallpaper",
                            "Computed wallpaper dimensions are invalid.",
                            expectsConfirmation: false);
                        return;
                    }

                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Wallpaper Screenshot",
                        suggestedName: BuildSuggestedFileName(
                            "png", imageWidth: wpW, imageHeight: wpH, isSpanning: true),
                        filter: "PNG image (*.png)|*.png|TIFF image (*.tiff;*.tif)|*.tiff;*.tif|BMP image (*.bmp)|*.bmp");
                    if (string.IsNullOrEmpty(path)) return;

                    string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".bmp" => FracturingFog.Imaging.ImageFileFormat.Bmp,
                        ".tif" or ".tiff" => FracturingFog.Imaging.ImageFileFormat.Tiff,
                        _ => FracturingFog.Imaging.ImageFileFormat.Png,
                    };

                    // Re-use the Poster watermark plumbing so wallpaper output
                    // honours the current region/theme watermark + the custom
                    // override toggle, matching what Poster does. The top-line
                    // and program/version sub-line are composed by
                    // CreatePosterRequest off the render host's own state — the
                    // shell does not get to spell them out.
                    var customWm = shell.Main.UseCustomWatermark
                        ? UserWatermarkStore.Instance.GetByName(shell.Main.SelectedCustomWatermarkName)
                        : null;

                    var req = s_renderHost.CreatePosterRequest(
                        wpW, wpH, rotate: false, path, format, customWm);

                    try
                    {
                        var busy = shell.BeginRenderBusy("Rendering wallpaper…");
                        FracturingFog.Imaging.PosterResult result;
                        try { result = await Task.Run(() => PosterRenderer.RenderToFile(req, CancellationToken.None)); }
                        finally { busy.Dispose(); }
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Wallpaper Saved",
                            $"Saved {result.SavedWidth}×{result.SavedHeight} px to:\n{path}\n({result.ElapsedMs} ms)",
                            expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Wallpaper", $"Render failed:\n{ex.Message}", expectsConfirmation: false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] Wallpaper failed: {ex.Message}");
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

            // Recorded slideshow stopped — pop Convert / Save / Cancel.
            // ShellViewModel owns the temp PNG folder lifetime and the
            // RecordEncodePreset string; we own the ffmpeg call + folder copy.
            shell.SlideshowRecordingReady += async (_, args) =>
            {
                await HandleSlideshowRecordingReadyAsync(args);
            };

            // General application settings — pops the Avalonia AppSettings
            // dialog (animated-param ceiling override today). Persists on OK
            // and invalidates the animation bus's cached ceiling.
            shell.AppSettingsRequested += async (_, _) =>
            {
                try { await AvaloniaDialogs.ShowAppSettingsAsync(null); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] AppSettings failed: {ex.Message}");
                }
            };

            // Audio-reactive settings — standalone entry (regression fix: was
            // only reachable from the Slideshow Settings dialog). Opens bound to
            // the persisted store; on close, reconfigure a running driver so any
            // edits (source / sensitivity / band weights) apply to live audio-
            // reactive consumers (Beat FX, Acid Fog beat-lock) without a restart.
            shell.AudioSettingsRequested += async (_, _) =>
            {
                try
                {
                    // Build one matrix row per animatable scalar of the current
                    // fractal type, bound to the app-scoped manager (in-session).
                    var mgr = s_shell?.AudioModulation;
                    var rows = mgr?.DescriptorsForCurrentType()
                        .Select(d => new FracturingFog.UI.Avalonia.ViewModels.AudioBindingRowViewModel(d, mgr))
                        .ToList();
                    var before = AudioSettingsStore.Load();
                    await AvaloniaDialogs.ShowAudioSettingsAsync(
                        owner: null, liveSource: s_audioDriver?.BeatSource, bindingRows: rows);
                    // #277 — only reconfigure a *running* driver, and only when the
                    // capture-relevant settings actually changed. The old code
                    // reconfigured unconditionally after every close (incl. Cancel),
                    // which restarted a File source from the top even on no-op opens.
                    var after = AudioSettingsStore.Load();
                    if (s_audioDriver is { IsRunning: true }
                        && AudioCaptureSettingsChanged(before, after))
                        s_audioDriver.Reconfigure(after);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaloniaShellBootstrap] AudioSettings failed: {ex.Message}");
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
                    var file = SlideshowConfigLibrary.Load();
                    var themeNames = s_themeService?.EnumerateThemeNames();
                    var animationNames = s_themeService?.EnumerateAnimationNames();
                    var regionNames = FractalRegionLibrary.Instance
                        .AllSlideshowRegions
                        .Select(r => r.Name)
                        .ToList();
                    Action<Action<double, double, double>>? captureCallback = capture =>
                    {
                        var fm = shell.FloatingMenu;
                        if (fm == null) return;
                        capture(fm.Brightness, fm.Contrast, fm.Adaptive);
                    };
                    // Seed the dialog's AudioReactive checkbox from the active
                    // preset (each SlideshowConfig carries its own flag) so
                    // toggling it in the dialog round-trips through the saved
                    // preset, not a separate per-session bit.
                    bool initialAudioReactive = SlideshowConfigLibrary.GetActive(file).AudioReactive;
                    var chosen = await AvaloniaDialogs.ShowSlideshowSettingsAsync(
                        file,
                        audioReactive: initialAudioReactive,
                        regionNames: regionNames,
                        themeNames: themeNames,
                        capturePostFxCallback: captureCallback,
                        animationNames: animationNames);
                    if (chosen != null)
                    {
                        // No persistence on dialog close — the Save button is the
                        // only persistence path. OK / Start drive the engine with
                        // the in-memory working copy so unsaved edits don't get
                        // silently written back to the selected preset.

                        // Start button — route to the active type's engine,
                        // passing the working config in-memory.
                        if (chosen.Value.StartRequested)
                        {
                            // Image AND Animation both run on the CPU cross-fade
                            // cycler (SlideshowEngine) — Animation just drives the
                            // shared ParameterAnimationBus during each leg. Only
                            // Video routes to the zoom engine.
                            if (chosen.Value.Config.Type != SlideshowType.Video)
                            {
                                // Stop any running video slideshow before kicking
                                // the image engine — the two share the render host.
                                if (shell.IsVideoRunning) shell.StopVideo();
                                shell.StartSlideshowFromConfig(chosen.Value.Config);
                            }
                            else
                            {
                                // Same the other way — stop a running image
                                // slideshow before starting the video engine.
                                if (shell.IsSlideshowRunning) shell.ToggleSlideshowCommand?.Execute().Subscribe();
                                shell.StartVideoSlideshowFromConfig(chosen.Value.Config);
                            }
                        }
                    }
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

                    // #189 feature 4 — a second click while a render is in flight
                    // is a CANCEL. The render checks the token at each stage
                    // (alt/Calculate + ThrowIfCancellationRequested), so it stops
                    // at the next checkpoint; the awaiting call below surfaces the
                    // OperationCanceledException and resets the button.
                    if (s_posterCts != null)
                    {
                        try { s_posterCts.Cancel(); }
                        catch (Exception cex)
                        {
                            await AvaloniaDialogs.ShowMessageAsync(
                                "Poster",
                                "Could not cancel the poster render:\n" + cex.Message,
                                expectsConfirmation: false);
                        }
                        return;
                    }

                    var dims = await AvaloniaDialogs.ShowPosterAsync(
                        watermarkNames: UserWatermarkStore.Instance.EnumerateNames(),
                        customWatermarkDefault: shell.Main.UseCustomWatermark,
                        watermarkNameDefault: shell.Main.SelectedCustomWatermarkName,
                        onEditWatermark: () => Dispatcher.UIThread.Post(() => shell.ShowWatermarkEditor()));
                    if (dims == null) return;

                    // The dialog's Width/Height are the labelled OUTPUT size (e.g.
                    // 24"×36" portrait → 7200×10800 px). PosterRenderer's rotate
                    // contract is "render LANDSCAPE (w×h), rotate 90° CW → portrait
                    // (h×w)", so for a portrait poster we must feed it the
                    // TRANSPOSED (landscape, wide) render dimensions and let the
                    // rotate produce the portrait output. Passing the tall dims
                    // directly (#190) rendered a portrait buffer whose calculator
                    // scale (3.5 / max(W,H)) anchored the view span to the vertical
                    // axis — cropping the sides into a zoomed-in centre — and then
                    // rotated that into a landscape file. Rendering landscape first
                    // keeps the on-screen framing and yields a true portrait file.
                    int renderW = dims.Value.Portrait ? dims.Value.Height : dims.Value.Width;
                    int renderH = dims.Value.Portrait ? dims.Value.Width  : dims.Value.Height;
                    // Output (post-rotation) is always the labelled Width × Height.
                    int savedW = dims.Value.Width;
                    int savedH = dims.Value.Height;

                    string? path = await AvaloniaDialogs.PickSaveFileAsync(
                        "Save Poster Image",
                        suggestedName: BuildSuggestedFileName(
                            "png",
                            imageWidth: savedW,
                            imageHeight: savedH,
                            isPoster: true,
                            isPortrait: dims.Value.Portrait),
                        filter: "PNG image (*.png)|*.png|TIFF image (*.tiff;*.tif)|*.tiff;*.tif|BMP image (*.bmp)|*.bmp");
                    if (string.IsNullOrEmpty(path)) return;

                    string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".bmp" => FracturingFog.Imaging.ImageFileFormat.Bmp,
                        ".tif" or ".tiff" => FracturingFog.Imaging.ImageFileFormat.Tiff,
                        _ => FracturingFog.Imaging.ImageFileFormat.Png,
                    };

                    var customWm = dims.Value.UseCustomWatermark
                        ? UserWatermarkStore.Instance.GetByName(dims.Value.WatermarkName)
                        : null;
                    var req = s_renderHost.CreatePosterRequest(
                        renderW, renderH, rotate: dims.Value.Portrait,
                        path, format, customWm);

                    // #189 feature 5 — memory pre-flight. A print-resolution
                    // poster can need gigabytes; warn (and let the user back out)
                    // before a render that would exceed a safe share of RAM, so it
                    // fails a confirmation instead of the process OOM-ing.
                    bool relief = req.FractalParameters?.Relief2DEnabled == true;
                    long estBytes = PosterRenderer.EstimatePeakBytes(renderW, renderH, relief, dims.Value.Portrait);
                    long availBytes = PosterRenderer.AvailableMemoryBytes();
                    if (availBytes > 0 && estBytes > 0.85 * availBytes)
                    {
                        var proceed = await AvaloniaDialogs.ShowMessageAsync(
                            "Poster — large render",
                            $"This {savedW:N0} × {savedH:N0} px poster may use about "
                            + $"{estBytes / (1024.0 * 1024.0 * 1024.0):N1} GB of memory, out of "
                            + $"~{availBytes / (1024.0 * 1024.0 * 1024.0):N1} GB available.\n\n"
                            + "Rendering it could exhaust memory and fail. Continue anyway?",
                            expectsConfirmation: true);
                        if (proceed != AvaloniaDialogs.MessageResult.Yes) return;
                    }

                    var cts = new CancellationTokenSource();
                    s_posterCts = cts;
                    shell.FloatingMenu.PosterButtonText = "Cancel Poster";
                    // #189 feature 5 — cap CPU near 90% for the duration of this
                    // heavy offscreen render so it can't peg every core and starve
                    // the UI. Restored in the finally; batch/server renders (which
                    // never set this) still use the whole machine.
                    int prevDop = FracturingFog.Rendering.RenderThrottle.MaxDegreeOfParallelism;
                    FracturingFog.Rendering.RenderThrottle.MaxDegreeOfParallelism =
                        FracturingFog.Rendering.RenderThrottle.Cpu90();
                    var posterBusy = shell.BeginRenderBusy("Rendering poster…", () => { try { cts.Cancel(); } catch { } });
                    try
                    {
                        var result = await Task.Run(() => PosterRenderer.RenderToFile(req, cts.Token));
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster Saved",
                            $"Saved {result.SavedWidth}×{result.SavedHeight} px to:\n{path}\n({result.ElapsedMs} ms)",
                            expectsConfirmation: false);
                    }
                    catch (OperationCanceledException)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster", "Poster render cancelled.", expectsConfirmation: false);
                    }
                    catch (OutOfMemoryException)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster",
                            "Ran out of memory rendering this poster. Try a smaller size or a lower DPI.",
                            expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Poster", $"Render failed:\n{ex.Message}", expectsConfirmation: false);
                    }
                    finally
                    {
                        posterBusy.Dispose();
                        FracturingFog.Rendering.RenderThrottle.MaxDegreeOfParallelism = prevDop;
                        s_posterCts = null;
                        cts.Dispose();
                        shell.FloatingMenu.PosterButtonText = "Poster";
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

                    // Toggle close — repeated invocation (e.g. P key, or a
                    // second right-click "Params") closes the editor instead
                    // of stacking duplicates or refusing to act.
                    if (s_userEqWin   != null) { try { s_userEqWin.Close();   } catch { } s_userEqWin   = null; return; }
                    if (s_sandboxWin  != null) { try { s_sandboxWin.Close();  } catch { } s_sandboxWin  = null; return; }
                    if (s_userBulbWin != null) { try { s_userBulbWin.Close(); } catch { } s_userBulbWin = null; return; }
                    if (s_paramsWin   != null) { try { s_paramsWin.Close();   } catch { } s_paramsWin   = null; return; }

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

                    var vm = new FractalParamsViewModel(
                        vs.FractalType,
                        vs.FractalParameters,
                        ifsPresets: new List<string>(IFSPresets.All.Keys),
                        lsystemPresets: new List<string>(LSystemPresets.All.Keys),
                        attractorPresets: null,
                        attractorDefaults: global::FracturingFog.AttractorCalculator.DefaultParams,
                        flamePresets: new List<string>(FlamePresets.All.Keys),
                        audioModulation: s_shell?.AudioModulation);
                    vm.ParamChanged += () => s_renderHost?.Trigger();

                    // #135 — drop-colour eyedropper: route the params VM's sample
                    // request through the same platform colour-sample bridge the
                    // colour-theme editor uses.
                    vm.SampleColorRequested += (_, args) =>
                    {
                        var bridge = BootstrapHooks.ColorSampleBridge;
                        if (bridge == null || bridge.IsActive)
                        {
                            args.Completion.TrySetResult(true);
                            return;
                        }
                        try
                        {
                            bridge.Begin(
                                picked =>
                                {
                                    args.PickedR = picked.R;
                                    args.PickedG = picked.G;
                                    args.PickedB = picked.B;
                                    args.Completion.TrySetResult(true);
                                },
                                () => args.Completion.TrySetResult(true));
                        }
                        catch
                        {
                            args.Completion.TrySetResult(true);
                        }
                    };

                    // #101 — mesh export for the current DE raymarcher. Sampler
                    // builds straight off the live FractalType + params via the
                    // shared factory; marching cubes runs off-thread so the pick
                    // dialog + UI stay responsive.
                    vm.ExportMeshRequested += async () =>
                    {
                        var vsx = s_renderHost?.ViewState;
                        if (vsx == null) return;
                        var de = global::FracturingFog.Export.RaymarchMeshSampler.For(
                            vsx.FractalType, vsx.FractalParameters);
                        if (de == null)
                        {
                            ShowInfo("Mesh export",
                                "This fractal has no distance-estimated surface to export.", true);
                            return;
                        }
                        string? path = await PickSaveAsync("Export Mesh",
                            "OBJ (*.obj)|*.obj|glTF binary (colour + PBR, *.glb)|*.glb|glTF (*.gltf)|*.gltf|PLY (vertex colour, *.ply)|*.ply|3MF (colour + print units, *.3mf)|*.3mf|STL (*.stl)|*.stl|All files (*.*)|*.*",
                            vsx.FractalType.ToString());
                        if (string.IsNullOrEmpty(path)) return;
                        double range = global::FracturingFog.Export.RaymarchMeshSampler.SuggestedRange(
                            vsx.FractalType, vsx.FractalParameters);
                        var colorFn = MakeMeshColorSource(s_renderHost?.ColorMap, range);
                        _ = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                global::FracturingFog.Export.MeshReport? rep = null;
                                int tris = global::FracturingFog.Export.UserBulbMeshExporter.ExportMarchingCubes(
                                    path, de, 0, 0, 0, range, 96, sampleColor: colorFn, onReport: r => rep = r);
                                Dispatcher.UIThread.Post(() =>
                                    ShowInfo("Mesh export",
                                        $"Exported {tris} triangles to {path}\n\n{rep?.PrintReadiness()}", false));
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.UIThread.Post(() =>
                                    ShowInfo("Mesh export error", $"Export failed: {ex.Message}", true));
                            }
                        });
                    };

                    // #138 — export the Oblique 3D heightfield object as a mesh.
                    // #147 fix — shared with the standalone Relief 3D window
                    // (Relief3DRequested) so the button works from either host;
                    // the standalone launcher forgot to wire this event.
                    AttachReliefMeshExport(vm);

                    // Render-completion gate for the Julia animation. Without
                    // this the timer-driven c-orbit fires Trigger every tick
                    // and floods the render pipe — the UI thread loses ground
                    // until the dialog appears to freeze. Forwarding
                    // FrameCompleted lets the VM emit the next render only
                    // after the previous one finishes.
                    EventHandler<RenderFrameInfo> onFrame = (_, _) => vm.NotifyRenderCompleted();
                    var host = s_renderHost!;
                    host.FrameCompleted += onFrame;

                    var win = new PanelHostWindow(
                        new FractalParamsView(),
                        new PanelHostOptions(
                            string.IsNullOrEmpty(vm.Title) ? "Fractal Params" : vm.Title,
                            Width: 400, MinWidth: 320,
                            SizeToContentHeight: true, CanResize: false, ShowInTaskbar: true,
                            StartupLocation: WindowStartupLocation.CenterOwner,
                            Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
                    {
                        DataContext = vm,
                    };
                    win.Closed += (_, _) =>
                    {
                        host.FrameCompleted -= onFrame;
                        s_paramsWin = null;
                    };
                    s_paramsWin = win;

                    var owner = AvaloniaDialogs.ActiveMainWindow;
                    if (owner != null) win.Show(owner);
                    else win.Show();
                });
            };

            // ── Standalone Volumetric Lighting & FX (S2) ─────────────────
            //
            // Surfaces the Lighting/FX block on its own so it isn't buried
            // inside Fractal Params. Its own FractalParamsViewModel over the
            // shared ViewState.FractalParameters — the LightingFxData partial
            // reads/writes _p.Lighting, which every fractal type shares, so the
            // panel is type-independent and needs no close/reopen on type
            // change (the type-change handler above leaves s_lightingFxWin
            // untouched). Toggle-close on repeated invocation.
            shell.LightingFxRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_renderHost == null) return;

                    // Menu toggles the single WindowService-owned window. The VM
                    // is built lazily (only when opening) over the shared
                    // ViewState so every Lighting/FX edit fires a re-render.
                    WindowService.ToggleLightingFx(() =>
                    {
                        var vs = s_renderHost.ViewState;
                        var vm = new FractalParamsViewModel(vs.FractalType, vs.FractalParameters,
                            audioModulation: s_shell?.AudioModulation);
                        vm.ParamChanged += () => s_renderHost?.Trigger();
                        return vm;
                    }, "Volumetric Lighting & FX");
                });
            };

            // ── Standalone Relief 3D (#147) ──────────────────────────────────
            //
            // Mirrors the Lighting & FX launcher above but for the Relief 3D
            // (2D heightfield) panel: its own FractalParamsViewModel over the
            // shared ViewState.FractalParameters, so every Relief2D* edit fires
            // ParamChanged → re-render. Independent of the Fractal Params window
            // (closing Params no longer closes Relief 3D) and reachable from the
            // Control Center. Re-focus if already open rather than toggle-close,
            // since it is launched from persistent buttons in two panels.
            //
            // Topmost is matched to the owner at show time so the panel is not
            // hidden behind the render window in Span mode (borderless Topmost).
            shell.Relief3DRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_renderHost == null) return;

                    if (s_relief3DWin is { IsVisible: true })
                    {
                        s_relief3DWin.Activate();
                        return;
                    }

                    var vs = s_renderHost.ViewState;
                    var vm = new FractalParamsViewModel(vs.FractalType, vs.FractalParameters,
                        audioModulation: s_shell?.AudioModulation);
                    vm.ParamChanged += () => s_renderHost?.Trigger();
                    // #147 fix — the mesh-export button lives in this standalone
                    // dialog; wire its handler here too (it was only wired on the
                    // Fractal Params window's VM, so export was a dead no-op when
                    // launched from the independent Relief 3D window).
                    AttachReliefMeshExport(vm);

                    var win = new PanelHostWindow(
                        new Relief3DDialog(),
                        new PanelHostOptions(
                            "Relief 3D",
                            Width: 480, Height: 720, MinWidth: 420, MinHeight: 400,
                            SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                            StartupLocation: WindowStartupLocation.CenterOwner,
                            Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
                    {
                        DataContext = vm,
                    };
                    win.Closed += (_, _) => s_relief3DWin = null;
                    s_relief3DWin = win;

                    var owner = AvaloniaDialogs.ActiveMainWindow;
                    if (owner != null)
                    {
                        // Match Span-mode Topmost so the panel floats above the
                        // borderless fullscreen render window instead of behind it.
                        win.Topmost = owner.Topmost;
                        win.Show(owner);
                    }
                    else win.Show();
                });
            };

            // ── Standalone Big Buttons (kid mode) ────────────────────────────
            //
            // Large, resizable dialog with three oversized buttons (Color /
            // Place / Show) for young explorers. Launched from the Control Center
            // View section (which auto-closes on launch). Bound to a
            // BigButtonsViewModel over the shell so every press drives the same
            // machinery the grown-up UI uses. Re-focus if already open.
            shell.BigButtonsRequested += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_bigButtonsWin is { IsVisible: true })
                    {
                        s_bigButtonsWin.Activate();
                        return;
                    }

                    var win = new PanelHostWindow(
                        new BigButtonsView(),
                        new PanelHostOptions(
                            "Big Buttons",
                            Width: 560, Height: 720, MinWidth: 360, MinHeight: 420,
                            SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                            StartupLocation: WindowStartupLocation.CenterOwner,
                            Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
                    {
                        DataContext = new BigButtonsViewModel(shell),
                    };
                    win.Closed += (_, _) =>
                    {
                        (win.DataContext as BigButtonsViewModel)?.Dispose();
                        s_bigButtonsWin = null;
                    };
                    s_bigButtonsWin = win;

                    var owner = AvaloniaDialogs.ActiveMainWindow;
                    if (owner != null)
                    {
                        // Match Span-mode Topmost so the panel floats above the
                        // borderless fullscreen render window instead of behind it.
                        win.Topmost = owner.Topmost;
                        win.Show(owner);
                    }
                    else win.Show();
                });
            };

            // Asset Manager (Sub-goal A / A2) — the shell routes the three
            // source-editor asset types here because their editor windows are
            // host-owned and edit live params. Open the matching editor, then
            // select the saved entry so the picked asset loads into it.
            shell.AssetHostEditorRequested += (_, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_renderHost == null) return;
                    var p = s_renderHost.ViewState.FractalParameters;
                    switch (e.Kind)
                    {
                        case global::FracturingFog.Abstractions.Assets.AssetKind.UserEquation:
                            OpenUserEquationEditor(p);
                            if (s_userEqWin?.DataContext is UserEquationViewModel ueVm)
                                ueVm.SelectedSavedName = e.Name;
                            break;
                        case global::FracturingFog.Abstractions.Assets.AssetKind.SandboxEquation:
                            OpenSandboxEditor(p);
                            if (s_sandboxWin?.DataContext is SandboxViewModel sbVm)
                                sbVm.SelectedSavedName = e.Name;
                            break;
                        case global::FracturingFog.Abstractions.Assets.AssetKind.UserBulb:
                            OpenUserBulbEditor(p);
                            if (s_userBulbWin?.DataContext is UserBulbViewModel ubVm)
                                ubVm.SelectedSavedName = e.Name;
                            break;
                    }
                });
            };

            // Asset Manager bulk export (A3) — the VM assembled the zip in
            // memory; the host owns the save picker + file write.
            shell.AssetBundleExportRequested += (_, e) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        string? path = await AvaloniaDialogs.PickSaveFileAsync(
                            "Export Asset Bundle",
                            e.SuggestedName,
                            "Zip archive (*.zip)|*.zip|All files (*.*)|*");
                        if (string.IsNullOrEmpty(path)) return;

                        await System.IO.File.WriteAllBytesAsync(path, e.ZipBytes);
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Export Asset Bundle",
                            $"Exported {e.Count} asset{(e.Count == 1 ? "" : "s")} to:\n{path}",
                            expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaShellBootstrap] Asset bundle export failed: {ex.Message}");
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Export Asset Bundle",
                            "Export failed:\n" + ex.Message,
                            expectsConfirmation: false);
                    }
                });
            };

            // Asset Manager bulk import (A3 import) — the host owns the open
            // picker + overwrite prompt + file read; the VM owns the zip parse and
            // per-source routing (shell.ImportAssetBundle forwards to it).
            shell.AssetBundleImportRequested += (_, __) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        string? path = await AvaloniaDialogs.PickOpenFileAsync(
                            "Import Asset Bundle",
                            "Zip archive (*.zip)|*.zip|All files (*.*)|*");
                        if (string.IsNullOrEmpty(path)) return;

                        // Ask once, up front, how same-name collisions resolve.
                        var choice = await AvaloniaDialogs.ShowMessageAsync(
                            "Import Asset Bundle",
                            "Overwrite assets that already exist?\n\n" +
                            "Yes — replace matching saved assets with the bundle's.\n" +
                            "No — keep your existing assets and skip those names.",
                            expectsConfirmation: true);
                        bool overwrite = choice == AvaloniaDialogs.MessageResult.Yes;

                        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
                        var summary = shell.ImportAssetBundle(bytes, overwrite);

                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Asset Bundle", summary.Describe(), expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaShellBootstrap] Asset bundle import failed: {ex.Message}");
                        await AvaloniaDialogs.ShowMessageAsync(
                            "Import Asset Bundle",
                            "Import failed:\n" + ex.Message,
                            expectsConfirmation: false);
                    }
                });
            };

            // Per-editor JSON import (Scenes / Animations / Watermarks). Same
            // shape as the bundle import above — picker, one up-front overwrite
            // prompt, then the shell routes every entry through the kind's own
            // asset source — but scoped to the editor's single kind.
            shell.AssetJsonImportRequested += (_, args) =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        string? path = await AvaloniaDialogs.PickOpenFileAsync(
                            args.Title,
                            "JSON File (*.json)|*.json|All files (*.*)|*");
                        if (string.IsNullOrEmpty(path)) return;

                        var choice = await AvaloniaDialogs.ShowMessageAsync(
                            args.Title,
                            "Overwrite assets that already exist?\n\n" +
                            "Yes — replace matching saved assets with the file's.\n" +
                            "No — keep your existing assets and skip those names.",
                            expectsConfirmation: true);
                        bool overwrite = choice == AvaloniaDialogs.MessageResult.Yes;

                        string json = await System.IO.File.ReadAllTextAsync(path);
                        var summary = shell.ImportAssetsFromJson(args.Kind, json, overwrite);

                        await AvaloniaDialogs.ShowMessageAsync(
                            args.Title, summary.Describe(), expectsConfirmation: false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[AvaloniaShellBootstrap] Asset JSON import failed: {ex.Message}");
                        await AvaloniaDialogs.ShowMessageAsync(
                            args.Title, "Import failed:\n" + ex.Message, expectsConfirmation: false);
                    }
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
                // Reclaim the live Roslyn path. A prior "Compile & Load" installs
                // a _dynamicAltCalculator that permanently overrides the
                // UserEquation slot, so live edits would recompile
                // _userEquationCalculator into a calculator the host no longer
                // selects — the editor looks dead (no render change) until app
                // close. Typing raw C# means the user wants the live path back;
                // drop the hot-load override so this compile is what renders.
                s_renderHost!.SetDynamicAltCalculator(null);
                var (ok, error) = s_renderHost.CompileUserEquation(p.UserEquationSource ?? "return z*z + c;");
                vm.ShowError(error);
                if (ok) s_renderHost.Trigger();
            };
            vm.RenderRequested += () => s_renderHost!.Trigger();
            vm.NamePromptRequested += def => PromptNameAsync("Save Equation", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNoAsync($"Delete saved equation \"{name}\"?", "Delete Equation");
            vm.ConfirmOverwriteRequested += name => ConfirmYesNoAsync(
                $"A saved equation named \"{name}\" already exists.\n\nOverwrite it?",
                "Overwrite Equation");
            vm.OpenFilePromptRequested += () =>
                PickOpenAsync("Import User Equations", "JSON (*.json)|*.json|All files (*.*)|*.*");
            vm.MessageRequested += (title, body, isErr) => ShowInfo(title, body, isErr);
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

            // Wave 2.8 — Cookbook picker. Opens the picker as a modeless child
            // of the editor (matches the rest of the editor surface — modeless
            // so the equation editor stays interactive). The VM applies the
            // entry from its Accepted callback; cancel is a no-op.
            vm.CookbookRequested += () =>
            {
                var cookbookVm = new CookbookViewModel();
                cookbookVm.Accepted += entry => vm.ApplyCookbookEntry(entry);
                var cookbookWin = new PanelHostWindow(
                    new CookbookView(),
                    new PanelHostOptions(
                        "Equation Cookbook",
                        Width: 720, Height: 520, MinWidth: 520, MinHeight: 380,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))))
                {
                    DataContext = cookbookVm,
                };
                cookbookVm.CloseRequested += () => cookbookWin.Close();
                if (s_userEqWin != null) cookbookWin.Show(s_userEqWin);
                else ShowEditor(cookbookWin);
            };
            vm.CookbookCentreRequested += (cx, cy, zoom) =>
            {
                if (s_renderHost == null) return;
                s_renderHost.ViewState.CenterX = cx;
                s_renderHost.ViewState.CenterY = cy;
                s_renderHost.ViewState.Zoom = zoom;
                s_renderHost.Trigger();
            };

            // Wave 2.9 — Equation morph. Opens the morph dialog modeless. The
            // dialog's per-frame loop calls RenderAndSaveRequested which:
            //   (a) hot-compiles the synth DSL via CalcGenHotLoad
            //   (b) installs it as the dynamic alt calculator
            //   (c) Trigger()s a render and awaits AnimationFrameUploaded
            //   (d) saves the resulting BGRA buffer to PNG.
            // Per-frame Roslyn compile is slow but acceptable for offline use.
            vm.MorphRequested += () =>
            {
                var morphVm = new EquationMorphViewModel();
                morphVm.RenderAndSaveRequested += async (synth, outPath, ct) =>
                {
                    if (s_renderHost == null) return "Render host not initialised.";
                    try
                    {
                        var compiled = FracturingFog.CalculatorGen.CalculatorGenHotLoad
                            .TryCompileAndLoad(synth, "Morph");
                        if (!compiled.Ok) return compiled.Error ?? "Compile failed.";
                        int w = s_renderHost.Mandelbrot.Width;
                        int h = s_renderHost.Mandelbrot.Height;
                        var calc = (FracturingFog.Interefaces.IFractalCalculator?)
                            Activator.CreateInstance(compiled.CalculatorType!, w, h);
                        if (calc == null) return "Activator returned null.";

                        var tcs = new TaskCompletionSource();
                        EventHandler? handler = null;
                        handler = (_, _) =>
                        {
                            s_renderHost!.AnimationFrameUploaded -= handler;
                            tcs.TrySetResult();
                        };
                        s_renderHost.AnimationFrameUploaded += handler;
                        s_renderHost.SetDynamicAltCalculator(calc);
                        // SetDynamicAltCalculator already fires Trigger; no extra call needed.

                        var timeout = Task.Delay(30_000, ct);
                        // Stay on UI sync ctx — VM resumes after await and
                        // mutates reactive props which Avalonia bindings
                        // require to be touched on the dispatcher.
                        var done = await Task.WhenAny(tcs.Task, timeout);
                        if (done == timeout)
                        {
                            s_renderHost.AnimationFrameUploaded -= handler;
                            return "Frame timed out (30 s).";
                        }
                        if (ct.IsCancellationRequested) return null;

                        s_renderHost.SaveLastFrameToPng(outPath);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }
                };
                morphVm.BrowseFolderRequested += current =>
                {
                    string? picked = null;
                    var task = AvaloniaDialogs.PickFolderAsync("Choose morph output directory");
                    // PickFolderAsync runs the picker on the UI thread; this lambda
                    // runs from a Reactive command on the UI thread too. .Wait is
                    // safe here because AvaloniaDialogs marshals onto the dispatcher
                    // continuation pool, not the calling sync ctx.
                    try { picked = task.GetAwaiter().GetResult(); }
                    catch { picked = null; }
                    return picked;
                };

                // Defer ALC unloads while morph is active — per-frame compile
                // would otherwise unload the previous context while the host
                // still references the calc instance via _dynamicAltCalculator,
                // racing with AnimationTick / queued render jobs and crashing
                // the process. Flush on close once we've cleared the alt slot.
                FracturingFog.CalculatorGen.CalculatorGenHotLoad.KeepContexts = true;

                var morphWin = new PanelHostWindow(
                    new EquationMorphView(),
                    new PanelHostOptions(
                        "Equation Morph",
                        Width: 760, Height: 600, MinWidth: 560, MinHeight: 460,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))))
                {
                    DataContext = morphVm,
                };
                morphVm.CloseRequested += () => morphWin.Close();
                morphWin.Closed += (_, _) =>
                {
                    try { s_renderHost?.SetDynamicAltCalculator(null); }
                    catch { }
                    FracturingFog.CalculatorGen.CalculatorGenHotLoad.KeepContexts = false;
                    // Defer flush a beat so any in-flight render finishes
                    // reading the just-cleared alt slot before we unload
                    // its assembly.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { FracturingFog.CalculatorGen.CalculatorGenHotLoad.FlushKeptContexts(); }
                        catch { }
                    }, DispatcherPriority.Background);
                };
                if (s_userEqWin != null) morphWin.Show(s_userEqWin);
                else ShowEditor(morphWin);
            };

            // Wave 2.3 — Persist + Hot-Load.
            vm.HotLoadAndPersistRequested += (eq, baseName) =>
            {
                try
                {
                    var result = FracturingFog.CalculatorGen.CalculatorGenHotLoad
                        .PersistAndLoad(eq, baseName);
                    if (!result.Ok) return (result.Error, result.SourcePath);
                    int w = s_renderHost!.Mandelbrot.Width;
                    int h = s_renderHost.Mandelbrot.Height;
                    var calc = (FracturingFog.Interefaces.IFractalCalculator?)
                        Activator.CreateInstance(result.CalculatorType!, w, h);
                    if (calc == null) return ("Activator returned null.", result.SourcePath);
                    s_renderHost.SetDynamicAltCalculator(calc);
                    return (null, result.SourcePath);
                }
                catch (Exception ex)
                {
                    return ($"Persist + Hot-load failed: {ex.GetType().Name}: {ex.Message}", (string?)null);
                }
            };

            var win = new PanelHostWindow(
                new UserEquationView(),
                new PanelHostOptions(
                    "User Equation",
                    Width: 700, Height: 680, MinWidth: 460, MinHeight: 480,
                    SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                    StartupLocation: WindowStartupLocation.CenterOwner,
                    Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
            {
                DataContext = vm,
            };
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
            vm.NamePromptRequested += def => PromptNameAsync("Save Sandbox Equation", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNoAsync($"Delete saved sandbox equation \"{name}\"?", "Delete");
            vm.ConfirmOverwriteRequested += name => ConfirmYesNoAsync(
                $"A saved sandbox equation named \"{name}\" already exists.\n\nOverwrite it?",
                "Overwrite Sandbox Equation");
            vm.SaveFilePromptRequested += defName =>
                PickSaveAsync("Export Sandbox Equations", "JSON (*.json)|*.json|All files (*.*)|*.*", defName);
            vm.OpenFilePromptRequested += () =>
                PickOpenAsync("Import Sandbox Equations", "JSON (*.json)|*.json|All files (*.*)|*.*");
            vm.MessageRequested += (title, body, isErr) => ShowInfo(title, body, isErr);

            var win = new PanelHostWindow(
                new SandboxView(),
                new PanelHostOptions(
                    "Sandbox Equation",
                    Width: 760, Height: 520, MinWidth: 520, MinHeight: 400,
                    SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                    StartupLocation: WindowStartupLocation.CenterOwner,
                    Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
            {
                DataContext = vm,
            };
            win.Closed += (_, _) => s_sandboxWin = null;
            s_sandboxWin = win;

            ShowEditor(win);
            vm.TriggerCompile();
        }

        private static string? FormatAnalyticBadge(global::FracturingFog.Calculators.AnalyticDEKind kind, double power)
            => kind switch
            {
                global::FracturingFog.Calculators.AnalyticDEKind.None        => null,
                global::FracturingFog.Calculators.AnalyticDEKind.Square      => "Analytic engaged · Square",
                global::FracturingFog.Calculators.AnalyticDEKind.PowerN      => $"Analytic engaged · Pow N={power:0.##}",
                global::FracturingFog.Calculators.AnalyticDEKind.MandelbulbN => $"Analytic engaged · Triplex N={power:0.##}",
                _                                                           => "Analytic engaged",
            };

        private static void OpenUserBulbEditor(global::FracturingFog.Models.FractalParameters p)
        {
            if (s_renderHost == null) return;
            if (s_userBulbWin != null) { s_userBulbWin.Activate(); return; }

            var vm = new UserBulbViewModel(p);
            vm.CompileRequested += (_, _) =>
            {
                var (ok, error) = s_renderHost!.CompileUserBulb(p.UserBulbSource ?? string.Empty);
                vm.ShowError(error ?? string.Empty);
                var pat = s_renderHost.UserBulbAnalyticPattern;
                vm.SetAnalyticBadge(FormatAnalyticBadge(pat.Kind, pat.Power));
                vm.SetErrorSpan(
                    ok ? -1 : s_renderHost.UserBulbLastErrorPosition,
                    ok ? 0  : s_renderHost.UserBulbLastErrorLength);
                if (ok) s_renderHost.Trigger();
            };
            vm.RenderRequested += (_, _) => s_renderHost!.Trigger();
            vm.NamePromptRequested += async e => e.Result = await PromptNameAsync(e.Caption, "Enter a name:", e.DefaultValue);
            vm.ConfirmDeleteRequested += async e => e.Result = await ConfirmYesNoAsync(e.Message, "Confirm");
            vm.ConfirmOverwriteRequested += async e => e.Result = await ConfirmYesNoAsync(e.Message, "Overwrite Bulb Equation");
            vm.OpenFilePromptRequested += async e => e.Path = await PickOpenAsync(e.Title, e.Filter);
            vm.SaveFilePromptRequested += async e => e.Path = await PickSaveAsync(e.Title, e.Filter, e.DefaultName);
            vm.MessageRequested += (_, msg) => ShowInfo("UserBulb", msg, false);
            vm.AutoRangeRequested += (_, e) =>
            {
                if (s_renderHost == null) return;
                try
                {
                    var sampler = s_renderHost.MakeUserBulbExportSampler(e.Iterations, e.JacobianH);
                    global::FracturingFog.Export.SampleDistance de = sampler != null
                        ? (x, y, z) => sampler(x, y, z)
                        : (x, y, z) => s_renderHost!.SampleUserBulbDE(x, y, z);
                    e.Result = global::FracturingFog.Export.UserBulbMeshExporter.ProbeBoundingRange(
                        de, s_renderHost.UserBulbCenterX, -s_renderHost.UserBulbCenterY, 0);
                }
                catch
                {
                    e.Result = 0.0; // VM warns on no-surface
                }
            };

            vm.ExportMeshRequested += (_, e) =>
            {
                if (s_renderHost == null) { vm.NotifyExportDone(); return; }
                // Build the snapshot sampler on the UI thread (cheap) so it reads a
                // consistent kernel + params, then run the heavy marching-cubes off
                // the UI thread. Running it inline froze the app on high Grid/SS
                // (the DE field is millions of numerical-Jacobian evals); Task.Run
                // + a busy flag keeps the UI alive and the button disabled.
                var sampler = s_renderHost.MakeUserBulbExportSampler(e.Iterations, e.JacobianH);
                global::FracturingFog.Export.SampleDistance de = sampler != null
                    ? (x, y, z) => sampler(x, y, z)
                    : (x, y, z) => s_renderHost!.SampleUserBulbDE(x, y, z);
                double cx0 = s_renderHost.UserBulbCenterX, cy0 = -s_renderHost.UserBulbCenterY;
                var colorFn = MakeMeshColorSource(s_renderHost.ColorMap, Math.Max(1e-6, e.Range));
                // #269 busy chip + cancellation. The chip's Cancel button trips the
                // token; ExportMarchingCubes' Parallel.For + cell loop honour it and
                // return without writing, so a long high-Grid/SS export is abortable.
                var cts = new System.Threading.CancellationTokenSource();
                IDisposable? busy = s_shell?.BeginRenderBusy("Exporting mesh…", cts.Cancel);
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        global::FracturingFog.Export.MeshReport? rep = null;
                        int tris = e.MeshingMode == global::FracturingFog.Models.MeshingMode.DualContouring
                            ? global::FracturingFog.Export.UserBulbMeshExporter.ExportDualContouring(
                                e.Path, de, cx0, cy0, 0,
                                e.Range, e.GridN, e.IsoScale, e.IsoAbsolute,
                                capBoundary: e.CapBoundary, repair: e.Repair,
                                sampleColor: colorFn, onReport: r => rep = r, ct: cts.Token)
                            : global::FracturingFog.Export.UserBulbMeshExporter.ExportMarchingCubes(
                                e.Path, de, cx0, cy0, 0,
                                e.Range, e.GridN, e.IsoScale, e.IsoAbsolute, e.SuperSamples, e.CreaseDegrees,
                                capBoundary: e.CapBoundary, repair: e.Repair,
                                sampleColor: colorFn, onReport: r => rep = r, ct: cts.Token);
                        bool cancelled = cts.IsCancellationRequested;
                        Dispatcher.UIThread.Post(() =>
                        {
                            busy?.Dispose();
                            vm.NotifyExportDone();
                            cts.Dispose();
                            if (cancelled)
                                ShowInfo("Mesh export", "Export cancelled.", false);
                            else if (tris == 0)
                                // #113 — a fold/IFS map under the numerical DE crosses
                                // no iso surface. Point the user at the scalar-KIFS knob.
                                ShowInfo("Mesh export",
                                    "Exported 0 triangles — the distance field never crossed the surface. " +
                                    "For fold / IFS maps (Menger, Sierpinski, Mandelbox, kaleidoscopic) set " +
                                    "KIFS Scale to the fold's per-iteration scale (e.g. 3 for Menger), then re-export. " +
                                    "Also check Range encloses the fractal.", true);
                            else
                                ShowInfo("Mesh export",
                                    $"Exported {tris} triangles to {e.Path}\n\n{rep?.PrintReadiness()}", false);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            busy?.Dispose();
                            vm.NotifyExportDone();
                            cts.Dispose();
                            ShowInfo("Mesh export error", $"Export failed: {ex.Message}", true);
                        });
                    }
                });
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

            var win = new PanelHostWindow(
                new UserBulbView(),
                new PanelHostOptions(
                    "User Bulb (3D)",
                    Width: 1280, Height: 940, MinWidth: 980, MinHeight: 700,
                    SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                    StartupLocation: WindowStartupLocation.CenterOwner,
                    Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
            {
                DataContext = vm,
            };
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
            vm.NamePromptRequested += def => PromptNameAsync("Save ColorGen Theme", "Enter a name:", def);
            vm.ConfirmDeleteRequested += name => ConfirmYesNoAsync($"Delete saved theme \"{name}\"?", "Delete Theme");
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
                    // #27 Phase 4 — the theme runs on the safe DSL INTERPRETER
                    // (InterpretedColorMap), not Roslyn codegen + AssemblyLoadContext.
                    // No compile, no assembly load: a custom theme is a data object.
                    var map = FracturingFog.Models.InterpretedColorMap
                        .TryCreate(source, opts, out string? cgError);
                    if (map == null) return cgError ?? "Theme failed to parse.";
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

            var win = new PanelHostWindow(
                new ColorGenEditorView(),
                new PanelHostOptions(
                    "ColorGen Editor",
                    Width: 720, Height: 600, MinWidth: 520, MinHeight: 420,
                    SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                    StartupLocation: WindowStartupLocation.CenterOwner,
                    Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
            {
                DataContext = vm,
            };
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

        // ── Async host prompts (#118) ────────────────────────────────────────
        //
        // The source-editor VMs now raise async prompt callbacks
        // (Func<…,Task<…>> / Func<Args,Task>), so the host satisfies them with
        // Avalonia's own async dialog stack — no WinForms, no nested dispatcher
        // frame. These work on every platform (Windows / Linux / macOS); the
        // former WinForms-backed BootstrapHooks.SyncDialogs bridge is retired.

        private static Task<string?> PromptNameAsync(string title, string prompt, string defaultValue)
            => AvaloniaDialogs.ShowPromptAsync(title, prompt, defaultValue);

        private static Task<bool> ConfirmYesNoAsync(string message, string title)
            => AvaloniaDialogs.ConfirmAsync(title, message);

        private static void ShowInfo(string title, string body, bool isError)
        {
            // Fire-and-forget info/error toast — callers don't await it.
            _ = AvaloniaDialogs.ShowMessageAsync(title, body, expectsConfirmation: false);
        }

        // #138 / #147 — export the active Oblique 3D heightfield object as a
        // watertight mesh (OBJ with vertex colour + smooth normals, or binary
        // STL). Pulls the active 2D calculator's height + flat albedo and
        // Per-vertex albedo source for the Marching-Cubes export (roadmap S9.4 MC
        // vertex colour, #391). The screen colour driver is view-dependent (raymarch
        // step count + view depth) so it can't be replayed at a bare surface point;
        // this drives the SAME active palette with a view-INDEPENDENT scalar (radial
        // distance from the object centre, normalised by the export range) plus the
        // vertex normal, so the exported solid carries the theme even though it is
        // not a pixel-exact match of any one camera. Returns null when there is no
        // colour map (mesh then falls back to a flat material / grey).
        private static global::FracturingFog.Export.SampleSurfaceColor? MakeMeshColorSource(
            IColorMap? map, double range)
        {
            if (map == null) return null;
            double inv = 1.0 / Math.Max(1e-9, range);
            return (x, y, z, nx, ny, nz) =>
            {
                double rad = Math.Sqrt(x * x + y * y + z * z) * inv;
                float value = (float)Math.Clamp(rad, 0.0, 1.0) * 256f;
                // Map returns a packed 0x00RRGGBB / 0xAARRGGBB albedo; force opaque.
                uint c = unchecked((uint)map.Map(value, 0f, 256, (float)nx, (float)ny));
                return c | 0xFF000000u;
            };
        }

        // tessellates a solid matching the on-screen cutout. Shared by the
        // Fractal Params window and the standalone Relief 3D window so the
        // export button works from either host (the standalone launcher used to
        // omit this wiring, leaving the button a dead no-op).
        private static void AttachReliefMeshExport(
            FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel vm)
        {
            vm.ExportReliefMeshRequested += async () =>
            {
                var host2 = s_renderHost;
                if (host2 == null) return;
                if (!host2.TryGetHeightFieldExport(out var alb, out var hgt, out int hw, out int hh))
                {
                    ShowInfo("Relief mesh export",
                        "This fractal has no height field to export. Enable Relief 3D on a supported 2D type first.", true);
                    return;
                }
                var pex = host2.ViewState.FractalParameters;
                string? path = await PickSaveAsync("Export Relief Mesh",
                    "OBJ (*.obj)|*.obj|glTF binary (colour + PBR, *.glb)|*.glb|glTF (*.gltf)|*.gltf|PLY (vertex colour, *.ply)|*.ply|3MF (colour + print units, *.3mf)|*.3mf|STL (*.stl)|*.stl|All files (*.*)|*.*",
                    host2.ViewState.FractalType + "-relief");
                if (string.IsNullOrEmpty(path)) return;
                // Copy the live buffers before handing to the worker (the render
                // thread may overwrite them on the next frame).
                var albCopy = (uint[])alb.Clone();
                var hgtCopy = (float[])hgt.Clone();
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        global::FracturingFog.Export.MeshReport? rep = null;
                        int tris = global::FracturingFog.Export.HeightfieldMeshExporter.Export(
                            albCopy, hgtCopy, hw, hh, pex, path, onReport: r => rep = r);
                        string readiness = tris > 0 && rep != null
                            ? rep.Value.PrintReadiness()
                            : "Nothing to export (height field is flat or fully culled).";
                        string body = tris > 0
                            ? $"Exported {tris} triangles to {path}\n\n{readiness}"
                            : readiness;
                        Dispatcher.UIThread.Post(() =>
                        {
                            vm.ReliefMeshPrintStatus = readiness;   // live status in the expander
                            ShowInfo("Relief mesh export", body, tris == 0);
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            vm.ReliefMeshPrintStatus = $"Export failed: {ex.Message}";
                            ShowInfo("Relief mesh export error", $"Export failed: {ex.Message}", true);
                        });
                    }
                });
            };
        }

        private static Task<string?> PickOpenAsync(string title, string filter)
            => AvaloniaDialogs.PickOpenFileAsync(title, filter);

        private static Task<string?> PickSaveAsync(string title, string filter, string defaultName)
            => AvaloniaDialogs.PickSaveFileAsync(title, defaultName, filter);

        // ── FFmpeg startup prompt ────────────────────────────────────────────
        //
        // First-launch (or freshly-deleted ffmpeg.exe) prompt: offer the
        // install dialog when the binary is missing AND the user hasn't
        // previously chosen "I'll install manually" or "Continue without
        // video". The dialog itself persists the election to
        // FfmpegPreferences so subsequent launches honour it.
        private static void MaybeShowFfmpegStartupPrompt()
        {
            try
            {
                if (FfmpegEncoder.IsAvailable()) return;
                if (FracturingFog.Models.FfmpegPreferences.Instance.SuppressStartupPrompt()) return;
                _ = FfmpegSetupDialog.ShowAsync(AvaloniaDialogs.ActiveMainWindow);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[AvaloniaShellBootstrap] FFmpeg startup prompt failed: {ex.Message}");
            }
        }

        // ── AppData location picker ─────────────────────────────────────────

        private static async Task ShowAppDataLocationDialogAsync()
        {
            string current = AppDataPaths.Root;
            string defaultPath = AppDataPaths.DefaultRoot;
            bool isOverridden = !string.Equals(current, defaultPath, StringComparison.OrdinalIgnoreCase);

            string body = isOverridden
                ? $"Current AppData folder:\n{current}\n\n(Default: {defaultPath})\n\nPick a new folder?"
                : $"Current AppData folder:\n{current}\n\nPick a new folder?";

            var pick = await AvaloniaDialogs.ShowMessageAsync(
                "AppData Location",
                body,
                expectsConfirmation: true);
            if (pick != AvaloniaDialogs.MessageResult.Yes) return;

            string? chosen = await AvaloniaDialogs.PickFolderAsync("Choose new AppData folder");
            if (string.IsNullOrWhiteSpace(chosen)) return;

            // Same folder selected — nothing to do.
            if (string.Equals(System.IO.Path.GetFullPath(chosen), System.IO.Path.GetFullPath(current),
                              StringComparison.OrdinalIgnoreCase))
                return;

            var migrate = await AvaloniaDialogs.ShowMessageAsync(
                "Copy Existing Data?",
                $"Copy existing settings from\n{current}\nto\n{chosen}?\n\n(No = leave old files in place; FracturingFog starts fresh in the new folder.)",
                expectsConfirmation: true);
            if (migrate == AvaloniaDialogs.MessageResult.Cancelled) return;

            try
            {
                AppDataPaths.SetRoot(chosen!, migrateFiles: migrate == AvaloniaDialogs.MessageResult.Yes);
            }
            catch (Exception ex)
            {
                await AvaloniaDialogs.ShowMessageAsync(
                    "AppData Location Failed",
                    $"Could not switch AppData folder:\n{ex.Message}",
                    expectsConfirmation: false);
                return;
            }

            await AvaloniaDialogs.ShowMessageAsync(
                "AppData Location Updated",
                $"AppData folder set to:\n{chosen}\n\nRestart FracturingFog so all settings reload from the new location.",
                expectsConfirmation: false);
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

        // Recorded image-slideshow stopped. The engine handed us a temp PNG
        // folder; pop Convert / Save Frames / Cancel and act accordingly.
        private static async Task HandleSlideshowRecordingReadyAsync(
            FracturingFog.UI.Avalonia.ViewModels.SlideshowRecordingReadyEventArgs args)
        {
            string pngFolder = args.FolderPath;
            if (string.IsNullOrEmpty(pngFolder) || !System.IO.Directory.Exists(pngFolder)) return;

            var choice = await AvaloniaDialogs.ShowSlideshowRecordingPromptAsync(
                args.FrameCount, args.Width, args.Height, args.EncodePreset);

            if (choice == AvaloniaDialogs.SlideshowRecordingChoice.Cancel)
            {
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                SetStatus("Slideshow recording discarded.");
                return;
            }

            if (choice == AvaloniaDialogs.SlideshowRecordingChoice.SaveFrames)
            {
                string? dest = await AvaloniaDialogs.PickFolderAsync(
                    "Choose a folder to keep the PNG sequence");
                if (string.IsNullOrEmpty(dest))
                {
                    try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                    SetStatus("Slideshow recording discarded.");
                    return;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string finalFolder = System.IO.Path.Combine(dest, $"FracturingFog_Slideshow_{stamp}");
                try
                {
                    System.IO.Directory.CreateDirectory(finalFolder);
                    foreach (string src in System.IO.Directory.EnumerateFiles(pngFolder))
                    {
                        string dst = System.IO.Path.Combine(finalFolder, System.IO.Path.GetFileName(src));
                        System.IO.File.Move(src, dst, overwrite: true);
                    }
                    try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                    SetStatus($"Slideshow PNG sequence saved: {finalFolder}");
                }
                catch (Exception ex)
                {
                    try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                    await AvaloniaDialogs.ShowMessageAsync(
                        "Save Frames", $"Failed to move PNG sequence:\n{ex.Message}", expectsConfirmation: false);
                }
                return;
            }

            // Convert — encode with ffmpeg, then drop the temp folder.
            if (!FfmpegEncoder.IsEnabledForUser())
            {
                string msg = FfmpegEncoder.IsAvailable()
                    ? "Video encoding is disabled (Continue Without Video selected). " +
                      "Open the FFmpeg setup dialog from the floating menu to re-enable it. " +
                      "Keeping PNG sequence in temp folder."
                    : "ffmpeg.exe is not available — install it from the floating menu's FFmpeg Setup. " +
                      "Keeping PNG sequence in temp folder.";
                await AvaloniaDialogs.ShowMessageAsync(
                    "Convert Slideshow", msg + "\n\n" + pngFolder, expectsConfirmation: false);
                return;
            }

            var preset = args.EncodePreset switch
            {
                "LosslessH264Mp4" => FfmpegEncoder.Preset.LosslessH264Mp4,
                "Ffv1Mkv" => FfmpegEncoder.Preset.Ffv1Mkv,
                _ => FfmpegEncoder.Preset.HighQualityH264Mp4,
            };
            string ext = FfmpegEncoder.DefaultExtensionFor(preset);
            string suggested = $"FracturingFog_Slideshow_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
            string? outPath = await AvaloniaDialogs.PickSaveFileAsync(
                "Save Slideshow Video", suggested, $"Video (*.{ext})|*.{ext}");
            if (string.IsNullOrEmpty(outPath))
            {
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                SetStatus("Slideshow recording discarded.");
                return;
            }

            SetStatus("Encoding slideshow video…");
            try
            {
                var (ok, log) = await FfmpegEncoder.EncodeAsync(
                    pngFolder, outPath, preset, fps: 30,
                    onProgressLine: line => SetStatus("ffmpeg: " + line));
                if (!ok)
                {
                    await AvaloniaDialogs.ShowMessageAsync(
                        "Convert Slideshow",
                        $"ffmpeg encode failed.\n\n{log}\n\nThe PNG sequence is still in:\n{pngFolder}",
                        expectsConfirmation: false);
                    return;
                }
                try { System.IO.Directory.Delete(pngFolder, recursive: true); } catch { }
                SetStatus($"Slideshow video saved: {System.IO.Path.GetFileName(outPath)}");
            }
            catch (Exception ex)
            {
                await AvaloniaDialogs.ShowMessageAsync(
                    "Convert Slideshow",
                    $"ffmpeg encode crashed:\n{ex.Message}\n\nThe PNG sequence is still in:\n{pngFolder}",
                    expectsConfirmation: false);
            }
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
            if (!FfmpegEncoder.IsEnabledForUser())
            {
                string msg = FfmpegEncoder.IsAvailable()
                    ? "Video encoding is disabled (Continue Without Video selected). " +
                      "Open the FFmpeg setup dialog from the floating menu to re-enable it. " +
                      "Keeping PNG sequence only."
                    : "ffmpeg.exe is no longer available — keeping PNG sequence only.";
                await AvaloniaDialogs.ShowMessageAsync(
                    "Save Lossless", msg, expectsConfirmation: false);
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
            // Read the committed combo selection, NOT ViewState.FractalType.
            // This handler is invoked synchronously from the
            // SelectedFractalType PropertyChanged, which RaiseAndSetIfChanged
            // raises BEFORE the setter assigns ViewState.FractalType — so
            // ViewState.FractalType is still the PREVIOUS type at this point
            // (the "minimap lags fractal-type change by one" bug, issue #29).
            // SelectedFractalType is already updated; the other call sites
            // (visibility toggle, ColorMapChanged) have both in sync anyway.
            var type = shell.Main.SelectedFractalType;
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

        // Build the default save-file name for Screenshot / Poster. Format:
        //   {ProgramName}[_{RegionName}][_{ColorThemeName}]_x{CX}_y{CY}_z{Zoom}
        //     _i{Iterations}_{W}x{H}[_wallpaper|_poster[_portrait]].{ext}
        // Spaces and characters invalid on Windows or Linux pathnames are
        // stripped. Region / theme tokens are omitted when empty.
        /// <summary>Strip filesystem-invalid characters (and spaces) from a name
        /// so it can seed a save-dialog filename. Falls back to a generic stem
        /// when nothing usable remains.</summary>
        private static string SanitizeFileStem(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Scene";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name!.Length);
            foreach (char c in name)
            {
                if (c == ' ') { sb.Append('_'); continue; }
                if (Array.IndexOf(invalid, c) >= 0 || c < 0x20) continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "Scene" : s;
        }

        // Map a chosen save-path extension to an ASCII text-art format (#226).
        // Two variants each share the natural .ans/.txt containers, so distinct
        // extensions (.ansi, .brl) disambiguate the per-character ANSI and
        // braille formats in the save dialog. Unknown → HTML (safe default).
        private static FracturingFog.Imaging.AsciiArtFormat AsciiFormatFromExtension(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".txt"          => FracturingFog.Imaging.AsciiArtFormat.PlainText,
                ".brl"          => FracturingFog.Imaging.AsciiArtFormat.Braille,
                ".ans"          => FracturingFog.Imaging.AsciiArtFormat.AnsiHalfBlock,
                ".ansi"         => FracturingFog.Imaging.AsciiArtFormat.Ansi,
                ".svg"          => FracturingFog.Imaging.AsciiArtFormat.Svg,
                ".html" or ".htm" => FracturingFog.Imaging.AsciiArtFormat.Html,
                _               => FracturingFog.Imaging.AsciiArtFormat.Html,
            };
        }

        // ASCII → MP4 (#230): pull the FX-animated grids from the host, rasterise
        // each on the UI thread (RenderTargetBitmap needs it), then encode via the
        // existing ffmpeg pipeline off-thread so the UI isn't blocked.
        private static async System.Threading.Tasks.Task ExportAsciiMp4Async(
            string path, FracturingFog.Imaging.AsciiFxSettings fx,
            int cols, int frames, double fps, bool rampFromColor)
        {
            if (s_renderHost == null) return;

            var raster = new FracturingFog.UI.Avalonia.Controls.AsciiFrameRasterizer();
            var grids = s_renderHost.RecordAsciiFrames(
                columns: cols, cellAspect: raster.CellAspect, invert: false,
                fineRamp: false, rampFromColor: rampFromColor, fx: fx, frames: frames, fps: fps);
            if (grids == null || grids.Count == 0) return;
            await ExportFramesToMp4Async(path, grids, fps, raster);
        }

        // Rasterise each AsciiFrame (UI thread) then encode via the ffmpeg pipeline
        // off-thread. Shared by the current-frame FX-loop MP4 and the live-record
        // MP4. A fresh rasteriser is made if none is supplied.
        private static async System.Threading.Tasks.Task ExportFramesToMp4Async(
            string path,
            System.Collections.Generic.IReadOnlyList<FracturingFog.Render.AsciiFrame> grids,
            double fps,
            FracturingFog.UI.Avalonia.Controls.AsciiFrameRasterizer? raster = null)
        {
            if (grids == null || grids.Count == 0) return;
            raster ??= new FracturingFog.UI.Avalonia.Controls.AsciiFrameRasterizer();

            var bufs = new System.Collections.Generic.List<uint[]>(grids.Count);
            int w = 0, h = 0;
            foreach (var g in grids)
            {
                var (bgra, gw, gh) = raster.Rasterize(g);
                w = gw; h = gh;
                bufs.Add(bgra);
            }
            if (w < 2 || h < 2) return;

            int encFps = (int)Math.Max(1, Math.Round(fps));
            await System.Threading.Tasks.Task.Run(() =>
            {
                using var vw = new FracturingFog.Imaging.FfmpegVideoWriter(
                    path, w, h, encFps, FracturingFog.FfmpegEncoder.Preset.HighQualityH264Mp4);
                long dt100ns = (long)(1e7 / encFps);
                long ts = 0;
                foreach (var b in bufs) { vw.WriteFrame(b, ts); ts += dt100ns; }
            });
        }

        private static string BuildSuggestedFileName(
            string defaultExt,
            int? imageWidth = null,
            int? imageHeight = null,
            bool isSpanning = false,
            bool isPoster = false,
            bool isPortrait = false)
        {
            static string Sanitize(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                var invalid = System.IO.Path.GetInvalidFileNameChars();
                var sb = new System.Text.StringBuilder(s!.Length);
                foreach (char c in s)
                {
                    if (c == ' ') continue;
                    if (Array.IndexOf(invalid, c) >= 0) continue;
                    if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?'
                        || c == '"' || c == '<' || c == '>' || c == '|' || c < 0x20) continue;
                    sb.Append(c);
                }
                return sb.ToString();
            }

            static string FmtNum(double v)
                => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    .Replace(".", string.Empty);

            var info = s_lastFrame;
            string program = Sanitize(s_renderHost?.ProgramName);
            if (string.IsNullOrEmpty(program)) program = "FracturingFog";
            string region = Sanitize(s_renderHost?.RegionName);
            string theme  = Sanitize(s_renderHost?.ThemeName);

            double cx   = info?.CenterX    ?? 0d;
            double cy   = info?.CenterY    ?? 0d;
            double zoom = info?.Zoom       ?? 0d;
            int    iter = info?.Iterations ?? 0;
            int    width  = imageWidth  ?? info?.Width  ?? 0;
            int    height = imageHeight ?? info?.Height ?? 0;

            var sb2 = new System.Text.StringBuilder();
            sb2.Append(program);
            if (!string.IsNullOrEmpty(region)) sb2.Append('_').Append(region);
            if (!string.IsNullOrEmpty(theme))  sb2.Append('_').Append(theme);
            sb2.Append("_x").Append(FmtNum(cx));
            sb2.Append("_y").Append(FmtNum(cy));
            sb2.Append("_z").Append(FmtNum(zoom));
            sb2.Append("_i").Append(iter.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb2.Append('_').Append(width).Append('x').Append(height);

            if (isSpanning) sb2.Append("_wallpaper");
            else if (isPoster)
            {
                sb2.Append("_poster");
                if (isPortrait) sb2.Append("_portrait");
            }

            string ext = (defaultExt ?? "png").TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "png";
            sb2.Append('.').Append(ext);
            return sb2.ToString();
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
                try { BootstrapHooks.NativeInputBridge?.Detach(); } catch { /* ignore */ }
                try { s_userBulbAnimTimer?.Stop(); } catch { /* ignore */ }
                s_userBulbAnimTimer = null;
                try { s_userEqWin?.Close(); }   catch { /* ignore */ } s_userEqWin = null;
                try { s_sandboxWin?.Close(); }  catch { /* ignore */ } s_sandboxWin = null;
                try { s_userBulbWin?.Close(); } catch { /* ignore */ } s_userBulbWin = null;
                try { s_audioDriver?.Dispose(); } catch { /* ignore */ }
                s_audioDriver = null;
                // Driver disposes the backend, but null the field for clarity.
                s_audioBackend = null;
                try { s_shell?.Dispose(); } catch { /* ignore */ }
                s_shell = null;
                try { s_renderHost?.Dispose(); } catch { /* renderer disposed via host */ }
                s_renderHost = null;
                s_renderer = null;
                s_input = null;
                s_surface = null;
            }
        }

        // ── AudioCaptureDriver lifecycle ──────────────────────────────────
        //
        // Created lazily on first audio-reactive slideshow. Reconfigure picks
        // up settings edits the user made via the Audio Settings dialog. Stop
        // (not Dispose) so the singleton stays warm across slideshow toggles.
        //
        // Backend selection (Phase X.B / Slice B.4): WindowsNAudioBackend on
        // Windows hosts (WASAPI loopback + mic + file + synth via NAudio),
        // NoopAudioBackend otherwise (file decode + analyzer-only synth).
        // Reflection-loaded so this assembly can stay net10.0-windows-free
        // when the Hosting csproj eventually flips. Today the file lives only
        // in FracturingFogCLD.csproj (net10.0-windows), so the Windows branch
        // is always taken — but write the gate so the cross-platform App
        // bootstrap reuses the same helper.
        private static void EnsureAudioCaptureStarted()
        {
            try
            {
                if (s_audioDriver == null)
                {
                    s_audioBackend = CreateAudioBackend();
                    s_audioDriver = new AudioCaptureDriver(s_audioBackend, AudioSettingsStore.Load());
                }
                // #277 — only (re)load settings + start when not already running.
                // Reconfiguring a live driver here restarts a File source from the
                // top, so a second consumer turning on would jog the music back to
                // zero. Mid-session edits go through the Audio Settings dialog's
                // explicit reconfigure instead.
                if (!s_audioDriver.IsRunning)
                {
                    s_audioDriver.Reconfigure(AudioSettingsStore.Load());
                    s_audioDriver.Start();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvaloniaShellBootstrap] Audio capture start failed: {ex.Message}");
            }
        }

        private static void StopAudioCapture()
        {
            try
            {
                if (s_audioDriver is { IsRunning: true }) s_audioDriver.Stop();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvaloniaShellBootstrap] Audio capture stop failed: {ex.Message}");
            }
        }

        /// <summary>#277 — true when any audio-reactive consumer currently wants
        /// live capture. Keyed on the consumers' explicit on/off state (not a
        /// reference count) so <see cref="ReconcileAudioCapture"/> is idempotent:
        /// no leaked "warm forever" capture, and no premature stop while another
        /// consumer is still active.</summary>
        private static bool AnyAudioConsumerActive()
            => s_shell != null &&
               (s_shell.AsciiFxAudioReactive
                || s_shell.Main.AudioViewBreathe
                || s_shell.Main.AcidFogAmbientBeatSync
                || (s_shell.AudioModulation?.HasEnabledBindings ?? false)
                || s_shell.SceneAudioActive
                || s_slideshowAudioDemand);

        /// <summary>#277 — start or stop shared audio capture to match current
        /// demand. Idempotent; consumers call it after any toggle on or off.</summary>
        private static void ReconcileAudioCapture()
        {
            if (AnyAudioConsumerActive()) EnsureAudioCaptureStarted();
            else StopAudioCapture();
        }

        /// <summary>#277 — compare the capture-relevant fields of two AudioSettings
        /// so the Audio Settings dialog only reconfigures a live driver on a real
        /// change (source / file / sensitivity / band weights).</summary>
        private static bool AudioCaptureSettingsChanged(AudioSettings a, AudioSettings b)
        {
            if (a is null || b is null) return a is not null || b is not null;
            if (a.Source != b.Source) return true;
            if (!string.Equals(a.FilePath, b.FilePath, StringComparison.Ordinal)) return true;
            if (a.Sensitivity != b.Sensitivity) return true;
            if (a.RouteSynthThroughAnalyzer != b.RouteSynthThroughAnalyzer) return true;
            if (a.PlaySynthOutput != b.PlaySynthOutput) return true;
            if (a.SynthBpm != b.SynthBpm) return true;
            var wa = a.BandWeights;
            var wb = b.BandWeights;
            if (wa is null || wb is null) return !ReferenceEquals(wa, wb);
            if (wa.Length != wb.Length) return true;
            for (int i = 0; i < wa.Length; i++)
                if (wa[i] != wb[i]) return true;
            return false;
        }

        /// <summary>
        /// Backend capability flags for the live driver, or
        /// <see cref="AudioBackendCapabilities.None"/> when the driver hasn't
        /// been constructed yet. AvaloniaDialogs queries this when opening the
        /// Audio Settings dialog so the source picker can grey unsupported
        /// options on Linux / macOS.
        /// </summary>
        public static AudioBackendCapabilities AudioCapabilities
            => s_audioDriver?.Capabilities ?? AudioCapabilityProbe.Detect();

        private static IAudioCaptureBackend CreateAudioBackend()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var asm = System.Reflection.Assembly.Load("FracturingFog.Audio.Win");
                    var t = asm.GetType("FracturingFog.Audio.Win.WindowsNAudioBackend");
                    if (t != null && Activator.CreateInstance(t) is IAudioCaptureBackend win)
                        return win;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[AvaloniaShellBootstrap] WindowsNAudioBackend load failed, falling back to noop: {ex.Message}");
                }
            }
            else if (OpenAlRuntime.IsAvailable())
            {
                // #271 (parent #58) — Linux/macOS live audio. OpenAlAudioBackend
                // adds mic everywhere + monitor loopback on Linux; file/synth
                // still work through it. Falls back to noop if construction fails
                // (lib vanished between probe and here, or no capture extension).
                try
                {
                    var oal = new OpenAlAudioBackend();
                    if ((oal.Capabilities & AudioBackendCapabilities.Microphone) != 0)
                        return oal;
                    oal.Dispose(); // capture extension unusable — degrade to noop
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[AvaloniaShellBootstrap] OpenAlAudioBackend load failed, falling back to noop: {ex.Message}");
                }
            }
            return new NoopAudioBackend();
        }
    }
}
