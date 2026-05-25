// RendererFactory.cs
// Creates the best available renderer for the current machine.
// DirectX 12 is preferred when the GPU supports Feature Level 12.0+.
// Falls back to DirectX 11 automatically on older hardware or when D3D12
// initialisation fails for any reason.

using System;
using FracturingFog.Abstractions;

namespace FracturingFog;

/// <summary>
/// Factory that creates the highest-capability renderer available.
/// Usage: var renderer = RendererFactory.Create(hwnd, w, h);
/// </summary>
public static class RendererFactory
{
    /// <summary>
    /// Creates a DirectX 12 renderer if the GPU supports FL 12.0+, otherwise
    /// creates a DirectX 11 renderer.  Never throws — falls back silently.
    /// </summary>
    public static IFractalRenderer Create(IntPtr hwnd, int width, int height, bool force_D3D11 = false)
    {
        if (!force_D3D11 && DirectX12Renderer.IsAvailable())
        {
            try
            {
                return new DirectX12Renderer(hwnd, width, height);
            }
            catch
            {
                // D3D12 init failed for a non-capability reason (driver bug,
                // SDK mismatch, etc.).  Fall through to D3D11.
            }
        }

        return new DirectXRenderer(hwnd, width, height);
    }

    /// <summary>
    /// Phase 2 surface-aware overload. Accepts an <see cref="IGpuSurface"/> from
    /// whichever shell hosts the renderer (WinForms control wrapper today,
    /// Avalonia <c>NativeControlHost</c> in the new shell) and subscribes the
    /// renderer to the surface's Resized / HandleLost events so the swap chain
    /// follows DPI and window-size changes automatically.
    ///
    /// Only <see cref="GpuSurfaceKind.Win32Hwnd"/> is supported today — the
    /// DirectX 11/12 backends require an HWND. Non-Windows surface kinds will
    /// be served by future Skia / Vulkan / Metal backends once those projects
    /// land (see Phase 2.4 in PHASE2_AVALONIA_MIGRATION.md).
    /// </summary>
    public static IFractalRenderer Create(IGpuSurface surface, bool force_D3D11 = false)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.Kind != GpuSurfaceKind.Win32Hwnd)
            throw new PlatformNotSupportedException(
                $"DirectX renderer requires a Win32 HWND surface; got {surface.Kind}. " +
                "Run on Windows or wait for the Skia/Vulkan backend (Phase 2.4).");

        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "IGpuSurface.Handle is null — the native control has not been created yet. " +
                "Subscribe to GpuSurfaceControl.SurfaceReady before calling Create.");

        // Surfaces start out at the control's logical size before the first
        // layout pass. Clamp to >=1 so swap chain creation does not fail with
        // an invalid description; the first Resized event will correct the size.
        int w = System.Math.Max(1, surface.PixelWidth);
        int h = System.Math.Max(1, surface.PixelHeight);

        IFractalRenderer renderer = Create(surface.Handle, w, h, force_D3D11);

        // Wire the surface's lifecycle to the renderer. The surface owns the
        // native handle so it is responsible for telling the renderer when the
        // backing window resizes or disappears.
        surface.Resized += (_, _) =>
            renderer.Resize(System.Math.Max(1, surface.PixelWidth),
                            System.Math.Max(1, surface.PixelHeight));

        surface.HandleLost += (_, _) => renderer.Dispose();

        return renderer;
    }

    /// <summary>
    /// Returns a short description of which API will be used on this machine,
    /// useful for the title bar or System Info dialog before a renderer is created.
    /// </summary>
    public static string ProbeDescription()
        => DirectX12Renderer.IsAvailable() ? "DirectX 12" : "DirectX 11";
}