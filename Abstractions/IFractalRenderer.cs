// IFractalRenderer.cs
// Shared abstraction for renderer implementations. Lives in Abstractions so
// Phase 2.4 cross-platform backends (Rendering.Silk OpenGL/Vulkan, Skia
// future) can implement it without referencing the main WinExe.
//
// Namespace deliberately kept at FracturingFog (not FracturingFog.Abstractions)
// so the legacy WinForms shell and the existing Vortice DX renderers continue
// to compile against the same fully-qualified name they always did.

using System;

namespace FracturingFog;

/// <summary>
/// Common interface implemented by DirectXRenderer (D3D11), DirectX12Renderer
/// (D3D12), and SilkGLRenderer (OpenGL 3.3 — Linux/Mac). Exposes only what
/// MainForm / FractalRenderHost need.
/// </summary>
public interface IFractalRenderer : IDisposable
{
    /// <summary>Uploads a new BGRA pixel buffer and displays it on the next Render() call.</summary>
    void UpdateTexture(uint[] colorBuffer, int width, int height);

    /// <summary>Presents the current texture to the screen. Call from the UI idle loop.</summary>
    void Render();

    /// <summary>Resizes the swap chain and render target to match the new client area.</summary>
    void Resize(int width, int height);

    /// <summary>Human-readable renderer name shown in the System Info dialog.</summary>
    string RendererDescription { get; }

    /// <summary>
    /// When true (default), Present blocks for vertical blank — caps to
    /// monitor refresh. Flip false during video recording or a single-image
    /// blocking render so Present returns immediately and the calc loop is
    /// not paced by the display.
    /// </summary>
    bool VSync { get; set; }
}
