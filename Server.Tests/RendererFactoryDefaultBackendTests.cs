// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #375: DX11 is now the default Windows backend and DX12 is opt-in
// (PreferD3D12 / --renderer dx12), because DX12 auto-selection could hang / hit
// DXGI_ERROR_DEVICE_REMOVED on some GPUs. These assert the selection contract at
// the cross-platform RendererFactory boundary: what force_D3D11 value reaches
// the Win32 HWND backend hook, and what the title-bar probe reports. The
// concrete DirectX12Renderer lives in the Windows-only Rendering.D3D assembly
// this net10.0 test project can't reference, so the policy — not the device — is
// exercised here via a recording stub hook.

using System;
using FracturingFog;
using FracturingFog.Abstractions;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("RendererFactoryStatics")]
public sealed class RendererFactoryDefaultBackendTests
{
    private sealed class StubSurface : IGpuSurface
    {
        public GpuSurfaceKind Kind => GpuSurfaceKind.Win32Hwnd;
        public IntPtr Handle => new(0x1234);
        public int PixelWidth => 640;
        public int PixelHeight => 480;
        public double DpiScale => 1.0;
        public event EventHandler? Resized { add { } remove { } }
        public event EventHandler? HandleLost { add { } remove { } }
    }

    private sealed class StubRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "stub";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    // Run one Create with a clean static slate, returning the force_D3D11 value
    // the Win32 backend hook received.
    private static bool CapturedForceD3D11(bool preferD3D12, bool forceD3D11)
    {
        var savedHook = RendererFactory.Win32HwndBackend;
        var savedBackend = RendererFactory.PreferredBackend;
        var savedForce = RendererFactory.ForceD3D11;
        var savedPrefer = RendererFactory.PreferD3D12;
        try
        {
            bool captured = false;
            RendererFactory.PreferredBackend = RendererBackend.Auto;
            RendererFactory.ForceD3D11 = forceD3D11;
            RendererFactory.PreferD3D12 = preferD3D12;
            RendererFactory.Win32HwndBackend = (_, _, _, force) =>
            {
                captured = force;
                return new StubRenderer();
            };
            RendererFactory.Create(new StubSurface());
            return captured;
        }
        finally
        {
            RendererFactory.Win32HwndBackend = savedHook;
            RendererFactory.PreferredBackend = savedBackend;
            RendererFactory.ForceD3D11 = savedForce;
            RendererFactory.PreferD3D12 = savedPrefer;
        }
    }

    [Fact]
    public void Default_Forces_DX11()
        => Assert.True(CapturedForceD3D11(preferD3D12: false, forceD3D11: false),
            "With no opt-in, the factory must force DX11 (the #375 default).");

    [Fact]
    public void PreferD3D12_Allows_DX12()
        => Assert.False(CapturedForceD3D11(preferD3D12: true, forceD3D11: false),
            "Opting into DX12 must let the DX12 path run (force_D3D11 == false).");

    [Fact]
    public void ForceD3D11_Wins_Over_PreferD3D12()
        => Assert.True(CapturedForceD3D11(preferD3D12: true, forceD3D11: true),
            "An explicit DX11 force must override a DX12 opt-in.");

    private static string Probe(bool preferD3D12, bool forceD3D11)
    {
        var savedProbe = RendererFactory.Win32ProbeBackend;
        var savedBackend = RendererFactory.PreferredBackend;
        var savedForce = RendererFactory.ForceD3D11;
        var savedPrefer = RendererFactory.PreferD3D12;
        try
        {
            RendererFactory.PreferredBackend = RendererBackend.Auto;
            RendererFactory.ForceD3D11 = forceD3D11;
            RendererFactory.PreferD3D12 = preferD3D12;
            // Emulate a FL12-capable GPU: the Win32 probe would report DX12.
            RendererFactory.Win32ProbeBackend = () => "DirectX 12";
            return RendererFactory.ProbeDescription();
        }
        finally
        {
            RendererFactory.Win32ProbeBackend = savedProbe;
            RendererFactory.PreferredBackend = savedBackend;
            RendererFactory.ForceD3D11 = savedForce;
            RendererFactory.PreferD3D12 = savedPrefer;
        }
    }

    [Fact]
    public void Probe_Default_Reports_DX11()
        => Assert.Equal("DirectX 11", Probe(preferD3D12: false, forceD3D11: false));

    [Fact]
    public void Probe_PreferD3D12_Reports_DX12()
        => Assert.Equal("DirectX 12", Probe(preferD3D12: true, forceD3D11: false));

    [Fact]
    public void Probe_ForceD3D11_Reports_DX11()
        => Assert.Equal("DirectX 11", Probe(preferD3D12: true, forceD3D11: true));
}
