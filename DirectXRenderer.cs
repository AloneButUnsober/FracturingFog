// DirectXRenderer.cs  — Vortice.DirectX 3.8.3 (now implements IFractalRenderer)
//
// API conventions verified against Vortice 3.8.x source and official samples:
//
//   • D3D11CreateDevice / CreateDXGIFactory1 are static methods accessed via
//     "using static Vortice.Direct3D11.D3D11" and "using static Vortice.DXGI.DXGI".
//
//   • Texture ResourceUsage  → Vortice.Direct3D11.ResourceUsage  (was "Usage" pre-3.x)
//   • DXGI BufferUsage       → Vortice.DXGI.Usage                (unchanged DXGI enum)
//
//   • IDXGIDevice.GetAdapter() is a method (not a property) and returns a manually-
//     disposable IDXGIAdapter — per Vortice 3.x changelog.
//
//   • Compiler.Compile now accepts ShaderFlags (added per issue #230).
//     Overload used:  Compile(string, string, string, string,
//                             ShaderFlags, EffectFlags,
//                             out Blob?, out Blob?)
//
//   • ID3D11Device.CreateVertexShader / CreatePixelShader accept ReadOnlySpan<byte>
//     obtained from Blob.AsSpan() (Vortice 3.8 "Create shaders with Blob directly"
//     improvement; .GetBytes() also works as a byte[] fallback).
//
//   • MappedSubresource.DataPointer is IntPtr; RowPitch is int.

using System;
using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;

// Pull D3D11CreateDevice and SdkLayersAvailable into scope as bare static calls.
using static Vortice.Direct3D11.D3D11;
// Pull CreateDXGIFactory1 / CreateDXGIFactory2 into scope.
using static Vortice.DXGI.DXGI;

using SharpGen.Runtime;

namespace FracturingFog;

/// <summary>
/// Owns a D3D11 device, DXGI swap chain bound to a WinForms HWND, and a
/// dynamic CPU-writable texture that is blitted to the screen each frame.
/// </summary>
public sealed class DirectXRenderer : IFractalRenderer
{
    // ── Embedded HLSL ─────────────────────────────────────────────────────────
    //
    // SV_VertexID full-screen triangle — no vertex buffer required.
    // Draw(3, 0) with TriangleList covers the entire [-1,1]×[-1,1] NDC space.
    //
    //   vid=0 → UV=(0,0) → NDC=(-1, 1)   top-left
    //   vid=1 → UV=(2,0) → NDC=( 3, 1)   far right  (clipped)
    //   vid=2 → UV=(0,2) → NDC=(-1,-3)   far below  (clipped)
    //
    // The triangle's intersection with the viewport is exactly the screen quad,
    // with UVs interpolating cleanly from (0,0) to (1,1).

    private const string ShaderSource = @"
struct VSOut
{
    float4 Pos : SV_Position;
    float2 UV  : TEXCOORD0;
};

VSOut VS(uint vid : SV_VertexID)
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    VSOut o;
    o.Pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
    o.UV  = uv;
    return o;
}

Texture2D<float4>  g_Tex  : register(t0);
SamplerState       g_Samp : register(s0);

float4 PS(VSOut i) : SV_Target
{
    return g_Tex.Sample(g_Samp, i.UV);
}
";

    // ── Feature level preference list ────────────────────────────────────────

    private static readonly FeatureLevel[] s_featureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    // ── D3D11 / DXGI objects ──────────────────────────────────────────────────

    private ID3D11Device         _device     = null!;
    private ID3D11DeviceContext  _context    = null!;
    private IDXGISwapChain1      _swapChain  = null!;

    private ID3D11RenderTargetView  _rtv        = null!;
    private ID3D11VertexShader      _vs         = null!;
    private ID3D11PixelShader       _ps         = null!;
    private ID3D11SamplerState      _sampler    = null!;
    private ID3D11RasterizerState   _rasterizer = null!;
    private ID3D11BlendState        _blendState = null!;

    // Dynamic GPU texture updated from the CPU colour buffer each frame.
    private ID3D11Texture2D?          _tex = null;
    private ID3D11ShaderResourceView? _srv = null;

    private int  _width;
    private int  _height;
    private bool _disposed;
    
    // ── IFractalRenderer ──────────────────────────────────────────────────────
    public string RendererDescription
    {
        get
        {
            if (_device == null) return "DirectX 11";
            return $"DirectX 11 (Feature Level {_device.FeatureLevel})";
        }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public DirectXRenderer(IntPtr hwnd, int width, int height)
    {
        _width  = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);

        CreateDeviceAndSwapChain(hwnd);
        CreateRenderTarget();
        CreateShaders();
        CreateSamplerAndStates();
    }

    // ── Device + swap chain ───────────────────────────────────────────────────

    private void CreateDeviceAndSwapChain(IntPtr hwnd)
    {
        // ── 1. Create D3D11 device ───────────────────────────────────────────
        //
        // In Vortice 3.8.x, D3D11CreateDevice is a static method imported via
        // "using static Vortice.Direct3D11.D3D11".
        //
        // Passing null adapter + DriverType.Hardware lets the runtime pick the
        // default hardware GPU without having to enumerate DXGI adapters first.
        // BgraSupport is mandatory for DXGI interop / WinForms child windows.

        DeviceCreationFlags creationFlags = DeviceCreationFlags.BgraSupport;

#if DEBUG
        // Enable the D3D11 debug layer when debugging (requires Windows SDK).
        if (SdkLayersAvailable())
            creationFlags |= DeviceCreationFlags.Debug;
#endif

        SharpGen.Runtime.Result deviceResult = D3D11CreateDevice(
            adapter:          null,           // null → use default hardware adapter
            driverType:       DriverType.Hardware,
            flags:            creationFlags,
            featureLevels:    s_featureLevels,
            device:           out _device,
            featureLevel:     out _,
            immediateContext: out _context
        );

        if (deviceResult.Failure)
            throw new InvalidOperationException(
                $"D3D11CreateDevice failed: HRESULT 0x{deviceResult.Code:X8}\n" +
                "Ensure the GPU supports Feature Level 10.0 or higher.");

        // ── 2. Obtain the DXGI factory through the device ────────────────────
        //
        // In Vortice 3.8.x, IDXGIDevice.GetAdapter() is a method (not a
        // property) and the returned IDXGIAdapter must be explicitly disposed.
        // GetParent<T>() walks the DXGI object chain up to the factory.

        IDXGIFactory2 dxgiFactory;
        using (var dxgiDevice  = _device.QueryInterface<IDXGIDevice1>())
        using (var dxgiAdapter = dxgiDevice.GetAdapter())          // must Dispose
        {
            dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>();  // AddRefs internally
        }

        // ── 3. Create DXGI swap chain ────────────────────────────────────────
        //
        // FlipDiscard swap chain for minimal latency and correct DWM integration.
        // B8G8R8A8_UNorm matches the colour buffer format packed in MandelbrotCalculator.

        var swapDesc = new SwapChainDescription1
        {
            Width             = (uint)_width,
            Height            = (uint)_height,
            Format            = Format.B8G8R8A8_UNorm,
            Stereo            = false,
            SampleDescription = new SampleDescription(1, 0),  // no MSAA on flip chain
            BufferUsage       = Usage.RenderTargetOutput,      // DXGI Usage (not ResourceUsage)
            BufferCount       = 2,
            Scaling           = Scaling.Stretch,
            SwapEffect        = SwapEffect.FlipDiscard,
            AlphaMode         = AlphaMode.Unspecified,
            Flags             = SwapChainFlags.None
        };

        _swapChain = dxgiFactory.CreateSwapChainForHwnd(
            device:             _device,
            wnd:               hwnd,
            desc:        swapDesc,
            fullscreenDesc: null,
            restrictToOutput:   null);

        // Prevent DXGI from capturing Alt+Enter (WinForms owns the window).
        dxgiFactory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        dxgiFactory.Dispose();
    }

    // ── Render target ─────────────────────────────────────────────────────────

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer, null);
    }

    // ── Shaders ───────────────────────────────────────────────────────────────

    private void CreateShaders()
    {
        // Vortice.D3DCompiler 3.8.x — Compiler.Compile now accepts ShaderFlags
        // (added in response to issue #230).  Use ShaderFlags.None for release
        // or ShaderFlags.Debug | ShaderFlags.SkipOptimization during development.

        // ── Vertex shader ──────────────────────────────────────────────────
        Result vsResult = Compiler.Compile(
            defines:     null,
            include:    null,
            shaderSource: ShaderSource,
            entryPoint:   "VS",
            sourceName:   "Mandelbrot.hlsl",
            profile:      "vs_5_0",
            shaderFlags:  ShaderFlags.OptimizationLevel3,
            effectFlags:  EffectFlags.None,
            blob:         out Blob? vsBlob,
            errorBlob:    out Blob? vsErrors);

        if (vsResult.Failure)
        {
            string msg = vsErrors?.ToString() ?? "(no error details)";
            vsErrors?.Dispose(); vsBlob?.Dispose();
            throw new InvalidOperationException($"Vertex shader compile error:\n{msg}");
        }
        vsErrors?.Dispose();

        // In 3.8.x, CreateVertexShader accepts ReadOnlySpan<byte> via Blob.AsSpan().
        _vs = _device.CreateVertexShader(vsBlob!.AsSpan());
        vsBlob.Dispose();

        // ── Pixel shader ───────────────────────────────────────────────────
        Result psResult = Compiler.Compile(
            defines:     null,
            include:    null,
            shaderSource: ShaderSource,
            entryPoint:   "PS",
            sourceName:   "Mandelbrot.hlsl",
            profile:      "ps_5_0",
            shaderFlags:  ShaderFlags.OptimizationLevel3,
            effectFlags:  EffectFlags.None,
            blob:         out Blob? psBlob,
            errorBlob:    out Blob? psErrors);

        if (psResult.Failure)
        {
            string msg = psErrors?.ToString() ?? "(no error details)";
            psErrors?.Dispose(); psBlob?.Dispose();
            throw new InvalidOperationException($"Pixel shader compile error:\n{msg}");
        }
        psErrors?.Dispose();

        _ps = _device.CreatePixelShader(psBlob!.AsSpan());
        psBlob.Dispose();
    }

    // ── Sampler + pipeline states ─────────────────────────────────────────────

    private void CreateSamplerAndStates()
    {
        // Bilinear sampler — smooth at zoom-out, pixel-perfect at 1:1.
        // SamplerDescription(Filter, AddressU, AddressV, AddressW) ctor available
        // in Vortice 3.x as a convenience overload.
        var samplerDesc = new SamplerDescription(
            filter:   Filter.MinMagMipLinear,
            addressU: TextureAddressMode.Clamp,
            addressV: TextureAddressMode.Clamp,
            addressW: TextureAddressMode.Clamp);
        _sampler = _device.CreateSamplerState(samplerDesc);

        // Rasterizer: disable backface culling for the winding-agnostic screen triangle.
        // RasterizerDescription(CullMode, FillMode) ctor available in Vortice 3.x.
        var rastDesc = new RasterizerDescription(CullMode.None, FillMode.Solid);
        _rasterizer = _device.CreateRasterizerState(rastDesc);

        // Opaque blend state (no blending — Mandelbrot output is fully opaque).
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable        = false,
            RenderTargetWriteMask = ColorWriteEnable.All   // write R, G, B, A
        };
        _blendState = _device.CreateBlendState(blendDesc);
    }

    // ── Texture management ────────────────────────────────────────────────────

    private void EnsureTexture(int width, int height)
    {
        var existing = _tex?.Description;
        if (existing.HasValue
            && existing.Value.Width  == width
            && existing.Value.Height == height)
            return;   // already the right size

        _srv?.Dispose(); _srv = null;
        _tex?.Dispose(); _tex = null;

        // Dynamic + CpuAccessFlags.Write → Map(WriteDiscard) each frame from CPU.
        // ResourceUsage.Dynamic  (Vortice 3.x name; was "Usage.Dynamic" pre-3.x)
        var texDesc = new Texture2DDescription
        {
            Width             = (uint)width,
            Height            = (uint)height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Dynamic,         // ← ResourceUsage in 3.8.x
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.Write
        };
        _tex = _device.CreateTexture2D(texDesc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format        = Format.B8G8R8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D     = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels       = 1
            }
        };
        _srv = _device.CreateShaderResourceView(_tex, srvDesc);
    }

    /// <summary>
    /// Uploads a new BGRA colour buffer from the CPU to the GPU texture.
    /// The array must contain exactly <paramref name="width"/> × <paramref name="height"/> elements.
    /// </summary>
    public unsafe void UpdateTexture(uint[] colorBuffer, int width, int height)
    {
        if (_disposed) return;
        EnsureTexture(width, height);

        // MappedSubresource.DataPointer is IntPtr in Vortice 3.8.x.
        // MappedSubresource.RowPitch    is int.
        MappedSubresource mapped = _context.Map(_tex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* dst = (byte*)mapped.DataPointer;
            fixed (uint* srcPtr = colorBuffer)
            {
                byte* src = (byte*)srcPtr;
                for (int row = 0; row < height; row++)
                {
                    // Copy one row; RowPitch may be larger than width*4 due to GPU alignment.
                    Buffer.MemoryCopy(
                        source:                 src + (long)row * width * 4,
                        destination:            dst + (long)row * mapped.RowPitch,
                        destinationSizeInBytes: (long)width * 4,
                        sourceBytesToCopy:      (long)width * 4);
                }
            }
        }
        finally
        {
            _context.Unmap(_tex!, 0);
        }
    }

    // ── Render ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Presents the Mandelbrot texture as a full-screen quad using the current GPU texture.
    /// </summary>
    public unsafe void Render()
    {
        if (_disposed || _tex == null) return;

        // Output-merger: bind RTV, opaque blend.
        _context.OMSetRenderTargets(_rtv);
        _context.OMSetBlendState(_blendState, null, 0xFFFFFFFF);

        // Rasterizer: full-window viewport, no culling.
        _context.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));
        _context.RSSetState(_rasterizer);

        // Only clear to black when there is no texture yet.  Once a texture
        // exists the full-screen triangle covers every pixel, so clearing
        // would cause a black flash between the old and new frames during
        // long recalculations (especially at High/Ultra quality with DD).
        if (_tex == null)
        {
            _context.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));
            _swapChain.Present(1, PresentFlags.None);
            return;
        }
        
        //_context.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));

        // Input assembler: no vertex buffer — SV_VertexID provides geometry.
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);

        // Shaders.
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);

        // Texture and sampler to pixel shader.
        ID3D11ShaderResourceView[] srvs = _srv != null ? new[] { _srv } : Array.Empty<ID3D11ShaderResourceView>();
        ID3D11SamplerState[] samplers = new[] { _sampler };
        _context.PSSetShaderResources(0, srvs);
        _context.PSSetSamplers(0, samplers);

        // Draw the full-screen triangle (3 vertices, no index buffer).
        _context.Draw(3, 0);

        // Present: SyncInterval=1 → wait for next VBlank (vsync on).
        // Set to 0 for uncapped frame rate.
        _swapChain.Present(1, PresentFlags.None);
    }

    // ── Resize ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resizes the swap chain and recreates the render-target view.
    /// Must be called on the UI thread before the next <see cref="Render"/> call.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (_disposed || width < 1 || height < 1) return;
        if (width == _width && height == _height) return;

        _width  = width;
        _height = height;

        // Unbind the RTV before resize; D3D11 will refuse if it is still bound.
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _rtv.Dispose();

        // Pass zero width/height to let DXGI inherit the new client area size;
        // pass Format.Unknown to preserve the existing format.
        _swapChain.ResizeBuffers(
            bufferCount: 0,
            width:       (uint)width,
            height:      (uint)height,
            newFormat:   Format.Unknown,
            swapChainFlags: SwapChainFlags.None
        ).CheckError();

        CreateRenderTarget();
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _srv?.Dispose();
        _tex?.Dispose();
        _blendState.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _rtv.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
