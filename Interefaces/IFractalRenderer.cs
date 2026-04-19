// IFractalRenderer.cs
// Shared abstraction for DirectX 11 and DirectX 12 renderer implementations.
// MainForm holds an IFractalRenderer so it is decoupled from the concrete type.

using System;

namespace FracturingFog;

/// <summary>
/// Common interface implemented by both DirectXRenderer (D3D11) and
/// DirectX12Renderer (D3D12).  Exposes only what MainForm needs.
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
}