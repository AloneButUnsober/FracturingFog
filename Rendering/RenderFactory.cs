// RendererFactory.cs
// Creates the best available renderer for the current machine.
// DirectX 12 is preferred when the GPU supports Feature Level 12.0+.
// Falls back to DirectX 11 automatically on older hardware or when D3D12
// initialisation fails for any reason.

using System;

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
    /// Returns a short description of which API will be used on this machine,
    /// useful for the title bar or System Info dialog before a renderer is created.
    /// </summary>
    public static string ProbeDescription()
        => DirectX12Renderer.IsAvailable() ? "DirectX 12" : "DirectX 11";
}