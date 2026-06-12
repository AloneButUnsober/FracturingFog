// RendererFactory.cs
// Creates the best available renderer for the current machine.
// DirectX 12 is preferred when the GPU supports Feature Level 12.0+.
// Falls back to DirectX 11 automatically on older hardware or when D3D12
// initialisation fails for any reason.

using System;
using System.Runtime.Versioning;
using FracturingFog.Abstractions;

namespace FracturingFog;

/// <summary>
/// Factory that creates the highest-capability renderer available.
/// Usage: var renderer = RendererFactory.Create(hwnd, w, h);
///
/// Phase X.3 / Slice 3.3: the IntPtr overload + <see cref="ProbeDescription"/>
/// directly construct DX12/DX11 renderers so they are annotated
/// [SupportedOSPlatform("windows")]. The surface-aware <see
/// cref="Create(IGpuSurface, bool)"/> overload stays cross-platform — it
/// dispatches to <see cref="NonWin32Backend"/> on non-Win32 surface kinds and
/// is the canonical entry point from <c>FracturingFog.Hosting</c>.
/// </summary>
public static class RendererFactory
{
    /// <summary>
    /// Creates a DirectX 12 renderer if the GPU supports FL 12.0+, otherwise
    /// creates a DirectX 11 renderer.  Never throws — falls back silently.
    /// </summary>
    [SupportedOSPlatform("windows")]
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
    /// Optional host-supplied fallback for non-<see cref="GpuSurfaceKind.Win32Hwnd"/>
    /// surfaces. The Avalonia bootstrap on Linux/macOS plants the Silk.NET
    /// OpenGL backend here (see <c>FracturingFog.Rendering.Silk</c>); the
    /// WinForms shell leaves it null. When null and a non-HWND surface arrives
    /// the factory throws — same behaviour as before Phase 2.4.
    /// </summary>
    public static Func<IGpuSurface, IFractalRenderer?>? NonWin32Backend { get; set; }

    /// <summary>
    /// Phase 2 surface-aware overload. Accepts an <see cref="IGpuSurface"/> from
    /// whichever shell hosts the renderer (WinForms control wrapper today,
    /// Avalonia <c>NativeControlHost</c> in the new shell) and subscribes the
    /// renderer to the surface's Resized / HandleLost events so the swap chain
    /// follows DPI and window-size changes automatically.
    ///
    /// Non-Win32 surface kinds route through <see cref="NonWin32Backend"/> when
    /// the host has registered one (Phase 2.4 cross-platform path).
    /// </summary>
    public static IFractalRenderer Create(IGpuSurface surface, bool force_D3D11 = false)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.Kind != GpuSurfaceKind.Win32Hwnd)
        {
            IFractalRenderer? alt = NonWin32Backend?.Invoke(surface);
            if (alt is not null)
            {
                surface.Resized += (_, _) =>
                    alt.Resize(System.Math.Max(1, surface.PixelWidth),
                               System.Math.Max(1, surface.PixelHeight));
                surface.HandleLost += (_, _) => alt.Dispose();
                return alt;
            }

            throw new PlatformNotSupportedException(
                $"DirectX renderer requires a Win32 HWND surface; got {surface.Kind}. " +
                "Register RendererFactory.NonWin32Backend with a Silk/Skia/Metal " +
                "factory (Phase 2.4) before constructing the surface, or run on Windows.");
        }

        if (surface.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "IGpuSurface.Handle is null — the native control has not been created yet. " +
                "Subscribe to GpuSurfaceControl.SurfaceReady before calling Create.");

        // Phase X.3 / Slice 3.3: explicit OS gate so the CA1416 analyzer accepts
        // the IntPtr Create overload (annotated [SupportedOSPlatform("windows")]).
        // A Win32Hwnd surface kind implies Windows, but the analyzer cannot infer
        // that from surface.Kind alone — make the OS check explicit.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Win32Hwnd surface received on a non-Windows host. " +
                "Register RendererFactory.NonWin32Backend so non-HWND surfaces " +
                "are dispatched before reaching the DX path.");

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
    [SupportedOSPlatform("windows")]
    public static string ProbeDescription()
        => DirectX12Renderer.IsAvailable() ? "DirectX 12" : "DirectX 11";
}