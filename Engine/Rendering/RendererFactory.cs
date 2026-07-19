// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/RendererFactory.cs
//
// S-X1b (2026-06-23) — cross-platform surface-aware renderer factory carved
// out of Rendering.D3D\RenderFactory.cs. Lives in the Engine assembly so the
// cross-platform Hosting bootstrap can dispatch surfaces to whichever backend
// is registered (DX on Windows via WindowsDxRendererFactory, Silk.NET GL on
// Linux/macOS, optional Skia CPU on all hosts) without dragging the Win-only
// Rendering.D3D ProjectReference into Hosting.dll.
//
// The Win-only IntPtr Create overload + ProbeDescription stayed behind in
// Rendering.D3D under the new type FracturingFog.WindowsDxRendererFactory.
// FracturingFog.Win.WindowsBootstrap.Install wires WindowsDxRendererFactory
// into the Win32HwndBackend / Win32ProbeBackend hooks before AvaloniaShell.Run
// so the cross-plat Create(IGpuSurface) call below picks up the DX path on
// Windows hosts.

using System;

using FracturingFog.Abstractions;

namespace FracturingFog;

/// <summary>
/// Phase X.4 / Slice 4.1 — renderer backend override. Mirrors the previous
/// enum in Rendering.D3D so the --renderer CLI flag wires to the same names.
/// </summary>
public enum RendererBackend
{
    /// <summary>Pick the best backend for the host: DX on Windows with a
    /// Win32 HWND surface, <see cref="RendererFactory.NonWin32Backend"/>
    /// (Silk.NET OpenGL) elsewhere.</summary>
    Auto,
    /// <summary>Force the DirectX 11 / 12 backend. Throws on non-Win32 surfaces
    /// or when the Win32HwndBackend hook has not been wired.</summary>
    Dx,
    /// <summary>Force the Silk.NET OpenGL backend. Routes through
    /// <see cref="RendererFactory.NonWin32Backend"/> even for Win32 HWND
    /// surfaces.</summary>
    Silk,
    /// <summary>Force the SkiaSharp CPU backend. Routes through
    /// <see cref="RendererFactory.SkiaBackend"/>.</summary>
    Skia,

    /// <summary>V3 (#42) — Vulkan compute for the fractal maths, OpenGL for
    /// present (Vulkan does compute only, no swapchain). Present routes through
    /// <see cref="RendererFactory.NonWin32Backend"/> (the same Silk GL blit);
    /// the Vulkan compute kernel is attached to the calculator by the host
    /// bootstrap, not by this factory. The CLI <c>--renderer vulkan</c> parse +
    /// bootstrap kernel-injection land in the V3 GUI follow-up.</summary>
    Vulkan,
}

/// <summary>
/// Cross-platform surface-aware renderer factory. Hosts dispatch an
/// <see cref="IGpuSurface"/> here and the factory routes to whichever backend
/// is registered for the surface kind:
///
/// <list type="bullet">
///   <item><see cref="Win32HwndBackend"/> — Windows, registered by
///   FracturingFog.Win.WindowsBootstrap (DirectX 11/12 via
///   WindowsDxRendererFactory).</item>
///   <item><see cref="NonWin32Backend"/> — Linux/macOS, registered by the
///   Avalonia bootstrap (Silk.NET OpenGL).</item>
///   <item><see cref="SkiaBackend"/> — optional CPU fallback wired by the
///   bootstrap when the SkiaPresent callback is available.</item>
/// </list>
///
/// All Win-only IntPtr-flavoured Create overloads live on
/// <c>FracturingFog.WindowsDxRendererFactory</c> in Rendering.D3D.
/// </summary>
public static class RendererFactory
{
    /// <summary>
    /// Phase X.4 / Slice 4.1 — caller-supplied backend override. Program.cs
    /// sets this from the <c>--renderer</c> CLI flag before the Avalonia shell
    /// boots so <see cref="Create(IGpuSurface, bool)"/> sees the request the
    /// first time a surface arrives.
    /// </summary>
    public static RendererBackend PreferredBackend { get; set; } = RendererBackend.Auto;

    /// <summary>
    /// Windows-only HWND renderer hook. <c>FracturingFog.Win.WindowsBootstrap</c>
    /// registers <c>WindowsDxRendererFactory.Create</c> here on Windows hosts.
    /// Left null on Linux/macOS so a Win32Hwnd surface arriving on a non-Win
    /// host throws with a clear message.
    /// </summary>
    public static Func<IntPtr, int, int, bool, IFractalRenderer?>? Win32HwndBackend { get; set; }

    /// <summary>
    /// Windows-only probe description for the title bar / System Info dialog.
    /// Returns "DirectX 12" or "DirectX 11" on Windows; null on other hosts so
    /// the shell can substitute a Silk / Skia label.
    /// </summary>
    public static Func<string>? Win32ProbeBackend { get; set; }

    /// <summary>
    /// Optional host-supplied fallback for non-<see cref="GpuSurfaceKind.Win32Hwnd"/>
    /// surfaces. The Avalonia bootstrap on Linux/macOS plants the Silk.NET
    /// OpenGL backend here; the legacy WinForms shell leaves it null. When
    /// null and a non-HWND surface arrives the factory throws — same behaviour
    /// as before Phase 2.4.
    ///
    /// Phase X.4 / Slice 4.1 — this hook is also used when
    /// <see cref="PreferredBackend"/> is <see cref="RendererBackend.Silk"/>
    /// regardless of the surface kind, so the user can force GL on a Windows
    /// host for parity testing.
    /// </summary>
    public static Func<IGpuSurface, IFractalRenderer?>? NonWin32Backend { get; set; }

    /// <summary>
    /// Phase X.4 / Slice 4.1 — host-supplied SkiaSharp CPU backend. Bootstrap
    /// registers a callback that constructs a <c>SkiaCpuRenderer</c> with a
    /// host-owned present delegate; left null here so the Skia override is a
    /// no-op on hosts that have not wired the SkiaPresent callback.
    /// </summary>
    public static Func<IGpuSurface, IFractalRenderer?>? SkiaBackend { get; set; }

    /// <summary>
    /// V3 (#42) — optional probe-description override for the Vulkan-compute
    /// backend. The host bootstrap can set this to a live string (device name);
    /// left null, <see cref="ProbeDescription"/> falls back to the static
    /// "Vulkan (compute) + OpenGL (present)" label when
    /// <see cref="PreferredBackend"/> is <see cref="RendererBackend.Vulkan"/>.
    /// </summary>
    public static Func<string>? VulkanProbeBackend { get; set; }

    /// <summary>
    /// Surface-aware Create. Accepts an <see cref="IGpuSurface"/> from
    /// whichever shell hosts the renderer and subscribes the renderer to the
    /// surface's Resized / HandleLost events so the swap chain follows DPI and
    /// window-size changes automatically.
    /// </summary>
    public static IFractalRenderer Create(IGpuSurface surface, bool force_D3D11 = false)
    {
        ArgumentNullException.ThrowIfNull(surface);

        // Honour --renderer override before the default surface-kind dispatch.
        // Silk / Skia bypass the DX path even when the surface is a Win32 HWND
        // so the user can parity-test the cross-platform backends on Windows.
        switch (PreferredBackend)
        {
            case RendererBackend.Silk:
            {
                IFractalRenderer? silk = NonWin32Backend?.Invoke(surface);
                if (silk is not null) return WireLifecycle(surface, silk);
                throw new PlatformNotSupportedException(
                    "--renderer silk requested but no Silk.NET backend is registered. " +
                    "AvaloniaShellBootstrap populates RendererFactory.NonWin32Backend " +
                    "in its static ctor; ensure the bootstrap has loaded.");
            }
            case RendererBackend.Skia:
            {
                IFractalRenderer? skia = SkiaBackend?.Invoke(surface);
                if (skia is not null) return WireLifecycle(surface, skia);
                throw new PlatformNotSupportedException(
                    "--renderer skia requested but no Skia backend is registered. " +
                    "Wire RendererFactory.SkiaBackend before requesting this backend.");
            }
            case RendererBackend.Vulkan:
            {
                // V3 (#42): Vulkan is compute-only (no swapchain); present is the
                // same Silk GL blit. Route present through NonWin32Backend; the
                // Vulkan compute kernel is attached to the calculator by the host
                // bootstrap, independent of the present backend chosen here.
                IFractalRenderer? gl = NonWin32Backend?.Invoke(surface);
                if (gl is not null) return WireLifecycle(surface, gl);
                throw new PlatformNotSupportedException(
                    "--renderer vulkan requested but no GL present backend is registered. " +
                    "AvaloniaShellBootstrap populates RendererFactory.NonWin32Backend; " +
                    "Vulkan present rides the same Silk GL blit.");
            }
        }

        if (surface.Kind != GpuSurfaceKind.Win32Hwnd)
        {
            IFractalRenderer? alt = NonWin32Backend?.Invoke(surface);
            if (alt is not null) return WireLifecycle(surface, alt);

            throw new PlatformNotSupportedException(
                $"DirectX renderer requires a Win32 HWND surface; got {surface.Kind}. " +
                "Register RendererFactory.NonWin32Backend with a Silk/Skia/Metal " +
                "factory (Phase 2.4) before constructing the surface, or run on Windows.");
        }

        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "IGpuSurface.Handle is null — the native control has not been created yet. " +
                "Subscribe to GpuSurfaceControl.SurfaceReady before calling Create.");

        if (Win32HwndBackend is null)
            throw new PlatformNotSupportedException(
                "Win32Hwnd surface received but no Win32 renderer backend is registered. " +
                "FracturingFog.Win.WindowsBootstrap.Install must run before AvaloniaShell.Run " +
                "on Windows hosts.");

        // Surfaces start out at the control's logical size before the first
        // layout pass. Clamp to >=1 so swap chain creation does not fail with
        // an invalid description; the first Resized event will correct the size.
        int w = System.Math.Max(1, surface.PixelWidth);
        int h = System.Math.Max(1, surface.PixelHeight);

        IFractalRenderer? renderer = Win32HwndBackend(surface.Handle, w, h, force_D3D11)
            ?? throw new InvalidOperationException(
                "Win32HwndBackend returned null. WindowsDxRendererFactory.Create never " +
                "returns null today — investigate the hook registration.");

        surface.Resized += (_, _) =>
            renderer.Resize(System.Math.Max(1, surface.PixelWidth),
                            System.Math.Max(1, surface.PixelHeight));
        surface.HandleLost += (_, _) => renderer.Dispose();

        return renderer;
    }

    /// <summary>
    /// Cross-platform probe description. Returns the Win32 probe string when
    /// the hook is wired (Windows host), or a Silk / Skia label on other
    /// hosts. Used by the title bar + System Info dialog.
    /// </summary>
    public static string ProbeDescription()
    {
        if (PreferredBackend == RendererBackend.Vulkan)
            return VulkanProbeBackend?.Invoke() ?? "Vulkan (compute) + OpenGL (present)";
        return Win32ProbeBackend?.Invoke() ?? "Silk.NET OpenGL";
    }

    private static IFractalRenderer WireLifecycle(IGpuSurface surface, IFractalRenderer renderer)
    {
        surface.Resized += (_, _) =>
            renderer.Resize(System.Math.Max(1, surface.PixelWidth),
                            System.Math.Max(1, surface.PixelHeight));
        surface.HandleLost += (_, _) => renderer.Dispose();
        return renderer;
    }
}
