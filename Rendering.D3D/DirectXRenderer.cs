// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using System.Runtime.Versioning;
using System.Threading;
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
[SupportedOSPlatform("windows")]
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

    /// <summary>T3.1: expose the device + immediate context to callers
    /// that want to build a compute-shader kernel sharing the same D3D11
    /// device the swap chain is bound to. Sharing the device avoids a
    /// second adapter selection + DXGI factory creation, and means the
    /// kernel's UAV outputs can later be promoted to GPU-resident SRVs
    /// the display pixel shader samples directly (T3.1 phase 4).
    /// Returns false on non-Windows / non-D3D11 backends — caller falls
    /// back to the CPU path.</summary>
    public bool TryGetD3D11(out ID3D11Device device, out ID3D11DeviceContext context)
    {
        device = _device;
        context = _context;
        return device != null && context != null;
    }

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
    private readonly IntPtr _hwnd;

    // ── Device loss (#1045) ───────────────────────────────────────────────────
    //
    // A GPU driver reset (TDR, another app, a driver hiccup) removes the device:
    // Present returns DXGI_ERROR_DEVICE_REMOVED and Map / ResizeBuffers throw it.
    // The GPU compute kernels share this device, so before #1045 any reset ended
    // presentation (Present's result was ignored, so the window froze) or crashed
    // the app (Map / ResizeBuffers threw). Now a lost device is detected at every
    // entry point, the device, swap chain and pipeline objects are rebuilt on the
    // same window, the last frame is re-uploaded, and DeviceGeneration moves on so
    // the host rebuilds the kernels that held the old device. Recovery is capped
    // per session: a kernel that trips the watchdog on every dispatch must not
    // loop the screen through resets. A recreate that fails (driver still
    // resetting) is retried after a pause, then given up with a status.
    private const int MaxRecoveries = 3;
    private const int MaxRecreateAttempts = 10;
    private const int RecreateRetryMs = 1000;
    private bool _deviceLost;
    private int _recoveries;
    private int _recreateFailures;
    private long _nextRecreateTicks;
    private int _deviceGeneration;
    private uint[]? _lastUpload;
    private int _lastUploadW, _lastUploadH;
    private DeviceLossSite _simulateSite;
    private int _simulateRecreateFailures;

    /// <summary>#1045 test hook — where a simulated device loss surfaces.</summary>
    public enum DeviceLossSite { None, Present, Upload, Resize }

    /// <inheritdoc/>
    public int DeviceGeneration => Volatile.Read(ref _deviceGeneration);

    /// <inheritdoc/>
    public string? DeviceStatus { get; private set; }

    /// <summary>#1045 — true while the device is lost and not yet rebuilt.</summary>
    public bool DeviceLost => _deviceLost;

    /// <summary>#1045 test hook — the next call at <paramref name="site"/> sees
    /// DXGI_ERROR_DEVICE_REMOVED as a real loss surfaces there: Present returns
    /// it, Map (upload) and ResizeBuffers throw it.</summary>
    public void SimulateDeviceLoss(DeviceLossSite site = DeviceLossSite.Present) => _simulateSite = site;

    private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);

    private bool TakeSimulated(DeviceLossSite site)
    {
        if (_simulateSite != site) return false;
        _simulateSite = DeviceLossSite.None;
        return true;
    }

    /// <summary>#1045 test hook — the next <paramref name="count"/> device
    /// recreations fail, as while a driver is still resetting.</summary>
    public void SimulateRecreateFailures(int count) => _simulateRecreateFailures = Math.Max(0, count);

    /// <summary>#1045 test hook — skip the retry pause after a failed recreate.</summary>
    public void ExpireRecreateDelay() => _nextRecreateTicks = 0;

    /// <inheritdoc/>
    public bool VSync { get; set; } = true;

    // ── IFractalRenderer ──────────────────────────────────────────────────────
    public string RendererDescription
    {
        get
        {
            if (_device == null) return "DirectX 11";
            return $"DirectX 11"; // (Feature Level {_device.FeatureLevel})";
        }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public DirectXRenderer(IntPtr hwnd, int width, int height)
    {
        _width  = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);
        _hwnd   = hwnd;

        CreateDeviceObjects();
    }

    private void CreateDeviceObjects()
    {
        CreateDeviceAndSwapChain(_hwnd);
        CreateRenderTarget();
        CreateShaders();
        CreateSamplerAndStates();
    }

    // Every object on the device; null-safe, so a half-built recreate can be
    // torn down too. A window takes one flip-model swap chain at a time, and
    // D3D11 destroys released objects only when the immediate context flushes:
    // without the ClearState + Flush the old swap chain outlives its last
    // reference and the new one on the same window fails with E_ACCESSDENIED
    // (the GPU kernels still hold the old device and context).
    private void ReleaseDeviceObjects()
    {
        try { _context?.ClearState(); } catch { /* removed device */ }
        _srv?.Dispose();        _srv = null;
        _tex?.Dispose();        _tex = null;
        _blendState?.Dispose(); _blendState = null!;
        _rasterizer?.Dispose(); _rasterizer = null!;
        _sampler?.Dispose();    _sampler = null!;
        _ps?.Dispose();         _ps = null!;
        _vs?.Dispose();         _vs = null!;
        _rtv?.Dispose();        _rtv = null!;
        _swapChain?.Dispose();  _swapChain = null!;
        try { _context?.Flush(); } catch { /* removed device */ }
        _context?.Dispose();    _context = null!;
        _device?.Dispose();     _device = null!;
    }

    // DXGI_ERROR_DEVICE_REMOVED / _HUNG / _RESET / DRIVER_INTERNAL_ERROR.
    private static bool IsDeviceLossCode(int hr) =>
        hr is unchecked((int)0x887A0005) or unchecked((int)0x887A0006)
           or unchecked((int)0x887A0007) or unchecked((int)0x887A0020);

    /// <summary>A failure is a lost device when its HRESULT says so or the
    /// device reports a removed reason (the authoritative signal: a call can
    /// surface the loss with another code).</summary>
    private bool IsDeviceLoss(int hr)
    {
        if (IsDeviceLossCode(hr)) return true;
        try { return _device == null || _device.DeviceRemovedReason.Failure; }
        catch { return true; }   // the device object itself is unusable
    }

    private void OnDeviceLost(int hr, string where)
    {
        if (_deviceLost) return;
        _deviceLost = true;
        int reason = 0;
        try { reason = _device?.DeviceRemovedReason.Code ?? 0; } catch { }
        string msg = $"D3D11 device lost in {where} (HRESULT 0x{hr:X8}, reason 0x{reason:X8}); rebuilding it.";
        System.Diagnostics.Debug.WriteLine("[DirectXRenderer] " + msg);
        try { Console.Error.WriteLine("[DirectXRenderer] " + msg); } catch { }
        DeviceStatus = "GPU reset: restoring the display...";
        TryRecover();
    }

    /// <summary>Rebuild the device after a loss. True when the renderer is usable
    /// (not lost, or rebuilt now); false while it is still lost (retry later) or
    /// recovery was given up (<see cref="DeviceStatus"/> says so).</summary>
    private bool TryRecover()
    {
        if (!_deviceLost) return true;
        if (_recoveries >= MaxRecoveries || _recreateFailures >= MaxRecreateAttempts)
        {
            DeviceStatus = "GPU reset: the display could not be restored - restart the app";
            return false;
        }
        long now = Environment.TickCount64;
        if (now < _nextRecreateTicks) return false;
        try
        {
            ReleaseDeviceObjects();
            if (_simulateRecreateFailures > 0)
            {
                _simulateRecreateFailures--;
                throw new InvalidOperationException("simulated recreate failure");
            }
            CreateDeviceObjects();
        }
        catch (Exception ex)
        {
            ReleaseDeviceObjects();
            _recreateFailures++;
            _nextRecreateTicks = now + RecreateRetryMs;
            DeviceStatus = _recreateFailures >= MaxRecreateAttempts
                ? "GPU reset: the display could not be restored - restart the app"
                : "GPU reset: restoring the display...";
            try { Console.Error.WriteLine($"[DirectXRenderer] device recreate failed ({_recreateFailures}/{MaxRecreateAttempts}): {ex.Message}"); } catch { }
            return false;
        }

        _deviceLost = false;
        _recoveries++;
        _recreateFailures = 0;
        Interlocked.Increment(ref _deviceGeneration);
        DeviceStatus = _recoveries >= MaxRecoveries
            ? "GPU reset: display restored (a further reset this session will not be recovered)"
            : "GPU reset: display restored";
        try { Console.Error.WriteLine($"[DirectXRenderer] device rebuilt (generation {_deviceGeneration})."); } catch { }

        // The new device starts with no texture: re-upload the last frame so the
        // window shows it rather than black until the next one arrives.
        if (_lastUpload is { } last)
        {
            try { UploadCore(last, _lastUploadW, _lastUploadH); }
            catch (SharpGenException) { /* the next frame uploads */ }
        }
        return true;
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
            defines:     null!,
            include:    null!,
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
            defines:     null!,
            include:    null!,
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
    public void UpdateTexture(uint[] colorBuffer, int width, int height)
    {
        if (_disposed) return;
        _lastUpload = colorBuffer;
        _lastUploadW = width;
        _lastUploadH = height;
        if (_deviceLost && !TryRecover()) return;
        try { UploadCore(colorBuffer, width, height); }
        catch (SharpGenException ex) when (IsDeviceLoss(ex.HResult))
        {
            OnDeviceLost(ex.HResult, "UpdateTexture");
        }
    }

    private unsafe void UploadCore(uint[] colorBuffer, int width, int height)
    {
        EnsureTexture(width, height);
        if (TakeSimulated(DeviceLossSite.Upload)) throw new SharpGenException(new Result(DXGI_ERROR_DEVICE_REMOVED));

        // MappedSubresource.DataPointer is IntPtr in Vortice 3.8.x.
        // MappedSubresource.RowPitch    is int.
        MappedSubresource mapped = _context.Map(_tex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* dst = (byte*)mapped.DataPointer;
            fixed (uint* srcPtr = colorBuffer)
            {
                byte* src = (byte*)srcPtr;
                long rowBytes = (long)width * 4;
                if (mapped.RowPitch == rowBytes)
                {
                    // Tight pack — one whole-buffer copy. Saves height-many
                    // call overheads + per-row branch. Common case at 1080p+
                    // on modern drivers where RowPitch matches width*4.
                    long total = rowBytes * height;
                    Buffer.MemoryCopy(src, dst, total, total);
                }
                else
                {
                    for (int row = 0; row < height; row++)
                    {
                        // RowPitch is larger than width*4 due to GPU alignment.
                        Buffer.MemoryCopy(
                            source:                 src + (long)row * rowBytes,
                            destination:            dst + (long)row * mapped.RowPitch,
                            destinationSizeInBytes: rowBytes,
                            sourceBytesToCopy:      rowBytes);
                    }
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
    public void Render()
    {
        if (_disposed) return;
        if (_deviceLost && !TryRecover()) return;
        try
        {
            Result hr = RenderCore();
            if (hr.Failure && IsDeviceLoss(hr.Code)) OnDeviceLost(hr.Code, "Present");
        }
        catch (SharpGenException ex) when (IsDeviceLoss(ex.HResult))
        {
            OnDeviceLost(ex.HResult, "Render");
        }
    }

    private unsafe Result RenderCore()
    {
        if (_tex == null) return Result.Ok;

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
            return _swapChain.Present(VSync ? 1u : 0u, PresentFlags.None);
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
        // VSync=false → SyncInterval=0 → uncapped (video record / blocking
        // single-image render path).
        Result presented = _swapChain.Present(VSync ? 1u : 0u, PresentFlags.None);
        return TakeSimulated(DeviceLossSite.Present) ? new Result(DXGI_ERROR_DEVICE_REMOVED) : presented;
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

        // A lost device is rebuilt at the new size (the swap chain takes _width /
        // _height), so there is nothing to resize.
        if (_deviceLost) { TryRecover(); return; }
        try { ResizeCore(width, height); }
        catch (SharpGenException ex) when (IsDeviceLoss(ex.HResult))
        {
            OnDeviceLost(ex.HResult, "Resize");
        }
    }

    private void ResizeCore(int width, int height)
    {
        if (TakeSimulated(DeviceLossSite.Resize)) throw new SharpGenException(new Result(DXGI_ERROR_DEVICE_REMOVED));

        // Unbind the RTV before resize; D3D11 will refuse if it is still bound.
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
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

        ReleaseDeviceObjects();
    }
}
