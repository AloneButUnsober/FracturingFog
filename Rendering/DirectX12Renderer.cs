// DirectX12Renderer.cs  — Vortice.DirectX 3.8.3
//
// Full-screen textured quad renderer using D3D12.
// Mirrors the visual output of DirectXRenderer (D3D11) exactly.
//
// Design overview
// ───────────────
//   • Double-buffered FlipDiscard swap chain (FrameCount = 2).
//   • One committed DEFAULT-heap texture for the fractal image.
//   • One committed UPLOAD-heap buffer used to transfer CPU pixels to the
//     texture each time UpdateTexture() is called.
//   • A single root signature with one descriptor table (SRV slot 0) and
//     one static linear-clamp sampler.
//   • HLSL is the same full-screen-triangle shader as the D3D11 version.
//   • Synchronisation uses one ID3D12Fence per frame slot.
//
// Thread safety
//   UpdateTexture() is safe to call from any thread; it just stores the
//   buffer and marks a dirty flag.  The actual GPU upload happens at the
//   beginning of Render() on whatever thread calls it (the UI idle loop).

using SharpGen.Runtime;
using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace FracturingFog;

/// <summary>
/// DirectX 12 renderer implementing IFractalRenderer.
/// Preferred over DirectXRenderer when the GPU supports Feature Level 12.0+.
/// </summary>
public sealed class DirectX12Renderer : IFractalRenderer
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int FrameCount = 2;
    private const Format TexFormat = Format.B8G8R8A8_UNorm;
    private const int D3D12TexAlign = 512;  // D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT
    private const int D3D12RowAlign = 256;  // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT

    // ── Device / queue / swap chain ───────────────────────────────────────────
    private ID3D12Device2 _device = null!;
    private ID3D12CommandQueue _cmdQueue = null!;
    private IDXGISwapChain3 _swapChain = null!;

    // ── Per-frame command recording ───────────────────────────────────────────
    private ID3D12CommandAllocator[] _allocators = null!;
    private ID3D12GraphicsCommandList _cmdList = null!;

    // ── Render targets ────────────────────────────────────────────────────────
    private ID3D12DescriptorHeap _rtvHeap = null!;
    private ID3D12Resource[] _renderTargets = null!;
    private int _rtvDescSize;

    // ── Texture / SRV ─────────────────────────────────────────────────────────
    private ID3D12Resource? _texture;
    private ID3D12Resource? _uploadBuf;
    private ID3D12DescriptorHeap _srvHeap = null!;

    // ── Pipeline ──────────────────────────────────────────────────────────────
    private ID3D12RootSignature _rootSig = null!;
    private ID3D12PipelineState _pso = null!;

    // ── Synchronisation ───────────────────────────────────────────────────────
    private ID3D12Fence _fence = null!;
    private ulong[] _fenceValues = null!;
    private IntPtr _fenceEvent;

    // ── State ─────────────────────────────────────────────────────────────────
    private int _width, _height;
    private int _frameIndex;
    private bool _disposed;

    // Pending CPU→GPU upload (set by UpdateTexture, consumed by Render).
    private readonly object _pendingLock = new();
    private uint[]? _pendingPixels;
    private int _pendingW, _pendingH;
    private bool _pendingDirty;

    // ── IFractalRenderer ──────────────────────────────────────────────────────
    public string RendererDescription => "DirectX 12";

    /// <inheritdoc/>
    public bool VSync { get; set; } = true;

    // ── Availability probe ────────────────────────────────────────────────────

    /// <summary>Returns true when D3D12 Feature Level 12.0 is available.</summary>
    public static bool IsAvailable()
    {
        try
        {
            var hr = D3D12CreateDevice(null, FeatureLevel.Level_12_0,
                out ID3D12Device? d);
            d?.Dispose();
            return hr.Success;
        }
        catch { return false; }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public DirectX12Renderer(IntPtr hwnd, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        CreateDevice();
        CreateCommandInfrastructure();
        CreateSwapChain(hwnd);
        CreateRtvHeap();
        CreateRenderTargetViews();
        CreateSrvHeap();
        CreatePipelineState();
        CreateFence();
    }

    // ── Init helpers ──────────────────────────────────────────────────────────

    private void CreateDevice()
    {
#if DEBUG
        if (D3D12GetDebugInterface(out ID3D12Debug? dbg).Success && dbg != null)
        { dbg.EnableDebugLayer(); dbg.Dispose(); }
#endif
        D3D12CreateDevice(null, FeatureLevel.Level_12_0, out ID3D12Device2? dev).CheckError();
        _device = dev!;
    }

    private void CreateCommandInfrastructure()
    {
        _cmdQueue = _device.CreateCommandQueue(
            new CommandQueueDescription(CommandListType.Direct, flags: CommandQueueFlags.DisableGpuTimeout));

        _allocators = new ID3D12CommandAllocator[FrameCount];
        for (int i = 0; i < FrameCount; i++)
            _allocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);

        _cmdList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocators[0]);
        _cmdList.Close();
    }

    private void CreateSwapChain(IntPtr hwnd)
    {
        using var factory = CreateDXGIFactory2<IDXGIFactory4>(false);

        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = TexFormat,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)FrameCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
        };

        using var sc1 = factory.CreateSwapChainForHwnd(_cmdQueue, hwnd, desc);
        _swapChain = sc1.QueryInterface<IDXGISwapChain3>();
        _frameIndex = (int)_swapChain.CurrentBackBufferIndex;
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
    }

    private void CreateRtvHeap()
    {
        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = (uint)FrameCount,
        });
        _rtvDescSize = (int)_device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.RenderTargetView);
    }

    private void CreateRenderTargetViews()
    {
        _renderTargets = new ID3D12Resource[FrameCount];
        var handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>(i);
            _device.CreateRenderTargetView(_renderTargets[i], null, handle);
            handle.Ptr += (nuint)_rtvDescSize;
        }
    }

    private void CreateSrvHeap()
    {
        _srvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.ShaderVisible,
        });
    }

    private void CreatePipelineState()
    {
        // ── Root signature: one SRV descriptor table + one static sampler ─────
        var range = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0);
        var param = new RootParameter1(
            new RootDescriptorTable1(range),
            ShaderVisibility.Pixel);

        var staticSampler = new StaticSamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ShaderRegister = 0,
            RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        var rootDesc = new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { param },
                new[] { staticSampler }));

        //_device.SerializeRootSignature(rootDesc, out Blob? rsBlob, out Blob? errBlob).CheckError();
        D3D12.D3D12SerializeVersionedRootSignature(rootDesc, out Blob? rsBlob);
        _rootSig = _device.CreateRootSignature(rsBlob);
        rsBlob?.Dispose();

        // ── HLSL — identical full-screen triangle to the D3D11 version ─────────
        const string hlsl = @"
Texture2D<float4>  g_Tex  : register(t0);
SamplerState       g_Samp : register(s0);
struct VSOut { float4 Pos : SV_Position; float2 UV : TEXCOORD0; };
VSOut VS(uint vid : SV_VertexID)
{
    float2 uv = float2((vid << 1) & 2, vid & 2);
    VSOut o;
    o.Pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
    o.UV  = uv;
    return o;
}
float4 PS(VSOut i) : SV_Target { return g_Tex.Sample(g_Samp, i.UV); }";

        Vortice.D3DCompiler.Compiler.CreateBlob(SharpGen.Runtime.PointerUSize.Zero, out Blob? vsBlob); //, out psBlob);
        Vortice.D3DCompiler.Compiler.CreateBlob(SharpGen.Runtime.PointerUSize.Zero, out Blob? vsErr);
        try
        {
            Vortice.D3DCompiler.Compiler.Compile(hlsl, "VS","", "vs_5_0", out vsBlob, out vsErr).CheckError();
        }
        catch (Exception)
        {
            if (vsErr != null)
            {
                throw new Exception(vsErr != null ? vsErr.AsString() : "Unknown error during vertex shader compilation.");
            }
        }
        finally
        {
            vsErr?.Dispose();
        }

        Vortice.D3DCompiler.Compiler.CreateBlob(SharpGen.Runtime.PointerUSize.Zero, out Blob? psBlob);
        Vortice.D3DCompiler.Compiler.CreateBlob(SharpGen.Runtime.PointerUSize.Zero, out Blob? psErr);
        try
        {
            Vortice.D3DCompiler.Compiler.Compile(hlsl, "PS", "", "ps_5_0", out psBlob, out psErr).CheckError();
        }
        catch (Exception)
        {
            if (psErr != null)
            {
                throw new Exception(psErr != null ? psErr.AsString() : "Unknown error during vertex shader compilation.");
            }
        }
        finally
        {
            psErr?.Dispose();
        }

        if (vsBlob == null || psBlob == null)
            throw new InvalidOperationException("D3D12 shader compilation failed.");

        ShaderBytecode vsBytecode = new ShaderBytecode(vsBlob.AsBytes());
        ReadOnlyMemory<byte> psBytes = psBlob.AsBytes();
        ShaderBytecode errBytecode = new ShaderBytecode(psBlob.AsBytes());
        ReadOnlyMemory<byte> vsBytes = vsBlob.AsBytes();

        _pso = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSig,
            VertexShader = vsBytes,
            PixelShader = psBytes,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { TexFormat },
            SampleDescription = new SampleDescription(1, 0),
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
        });
        vsBlob.Dispose();
        psBlob.Dispose();
    }

    private void CreateFence()
    {
        _fenceValues = new ulong[FrameCount];
        _fence = _device.CreateFence(0);
        _fenceEvent = CreateEventW(IntPtr.Zero, false, false, null);
    }

    // ── UpdateTexture (thread-safe) ───────────────────────────────────────────

    public void UpdateTexture(uint[] colorBuffer, int width, int height)
    {
        if (_disposed) return;
        lock (_pendingLock)
        {
            _pendingPixels = colorBuffer;
            _pendingW = width;
            _pendingH = height;
            _pendingDirty = true;
        }
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public void Render()
    {
        if (_disposed) return;

        // Upload any pending CPU texture before drawing.
        FlushPendingUpload();

        if (_texture == null) return;

        _frameIndex = (int)_swapChain.CurrentBackBufferIndex;
        WaitForFrame(_frameIndex);

        _allocators[_frameIndex].Reset();
        _cmdList.Reset(_allocators[_frameIndex], _pso);

        // Transition back buffer: Present → RenderTarget
        _cmdList.ResourceBarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.Present,
            ResourceStates.RenderTarget);

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += (nuint)(_frameIndex * _rtvDescSize);

        _cmdList.OMSetRenderTargets(rtvHandle);
        _cmdList.ClearRenderTargetView(rtvHandle,
            new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));

        _cmdList.SetGraphicsRootSignature(_rootSig);
        _cmdList.SetDescriptorHeaps(_srvHeap);
        _cmdList.SetGraphicsRootDescriptorTable(0,
            _srvHeap.GetGPUDescriptorHandleForHeapStart());

        _cmdList.RSSetViewport(new Viewport(0f, 0f, _width, _height, 0f, 1f));
        _cmdList.RSSetScissorRect(new RectI(0, 0, _width, _height));
        _cmdList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmdList.DrawInstanced(3, 1, 0, 0);

        // Transition back buffer: RenderTarget → Present
        _cmdList.ResourceBarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.RenderTarget,
            ResourceStates.Present);

        _cmdList.Close();
        _cmdQueue.ExecuteCommandList(_cmdList);
        _swapChain.Present(VSync ? 1u : 0u, PresentFlags.None);
        SignalFrame(_frameIndex);
    }

    // ── Pending upload ────────────────────────────────────────────────────────

    private unsafe void FlushPendingUpload()
    {
        uint[]? pixels;
        int pw, ph;
        lock (_pendingLock)
        {
            if (!_pendingDirty || _pendingPixels == null) return;
            pixels = _pendingPixels;
            pw = _pendingW;
            ph = _pendingH;
            _pendingDirty = false;
        }

        // (Re-)create texture and upload buffer when dimensions change.
        bool needNew = _texture == null
            || (int)_texture.Description.Width != pw
            || (int)_texture.Description.Height != ph;
        try
        {
            if (needNew)
            {
                _texture?.Dispose();
                _uploadBuf?.Dispose();

                var texDesc = ResourceDescription.Texture2D(
                    TexFormat, (uint)pw, (uint)ph, 1, 1);

                _texture = _device.CreateCommittedResource(
                    HeapProperties.DefaultHeapProperties,
                    HeapFlags.None,
                    texDesc,
                    ResourceStates.CopyDest);

                // Upload buffer size: one row per row, aligned to D3D12 pitch rules.
                int rowPitch = AlignUp(pw * 4, D3D12RowAlign);
                ulong uploadSz = (ulong)(rowPitch * ph);
                uploadSz = AlignUp64(uploadSz, D3D12TexAlign);

                _uploadBuf = _device.CreateCommittedResource(
                    HeapProperties.UploadHeapProperties,
                    HeapFlags.None,
                    ResourceDescription.Buffer(uploadSz),
                    ResourceStates.GenericRead);

                // Create SRV pointing at the new texture.
                _device.CreateShaderResourceView(
                    _texture,
                    new ShaderResourceViewDescription
                    {
                        Format = TexFormat,
                        ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                        Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
                        Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView { MipLevels = 1 },
                    },
                    _srvHeap.GetCPUDescriptorHandleForHeapStart());
            }
            else
            {
                // Texture already exists — wait for ALL in-flight frames before
                // touching it, then transition back to CopyDest.
                // We use a dedicated one-shot fence value so the wait is
                // guaranteed to complete (no dependency on Render's SignalFrame).
                WaitForAllFrames();

                int fi = _frameIndex;
                _allocators[fi].Reset();
                _cmdList.Reset(_allocators[fi], null);
                _cmdList.ResourceBarrierTransition(
                    _texture!,
                    ResourceStates.PixelShaderResource,
                    ResourceStates.CopyDest);
                _cmdList.Close();
                _cmdQueue.ExecuteCommandList(_cmdList);

                // Signal and wait inline — do NOT use WaitForCurrentFrame() here
                // because _fenceValues[fi] may not have been incremented by
                // Render() yet, making the wait return immediately on a stale value.
                ulong uploadFenceVal = _fenceValues[fi] + 1000;  // out-of-band value
                _cmdQueue.Signal(_fence, uploadFenceVal);
                if (_fence.CompletedValue < uploadFenceVal)
                {
                    _fence.SetEventOnCompletion(uploadFenceVal, _fenceEvent);
                    _ = WaitForSingleObject(_fenceEvent, uint.MaxValue);
                }
                // Do NOT update _fenceValues[fi] — Render() manages those.
            }

            // Map upload buffer and copy rows.
            int srcPitch = pw * 4;
            int dstPitch = AlignUp(srcPitch, D3D12RowAlign);

            void* mapped = null;
            _uploadBuf!.Map(0, null, &mapped).CheckError();
            fixed (uint* srcPtr = pixels)
            {
                byte* src = (byte*)srcPtr;
                byte* dst = (byte*)mapped;
                for (int row = 0; row < ph; row++)
                    Buffer.MemoryCopy(
                        src + (long)row * srcPitch,
                        dst + (long)row * dstPitch,
                        srcPitch, srcPitch);
            }
            _uploadBuf.Unmap(0, null);

            // Record CopyTextureRegion command.
            _allocators[_frameIndex].Reset();
            _cmdList.Reset(_allocators[_frameIndex], null);

            var foot = new PlacedSubresourceFootPrint
            {
                Footprint = new SubresourceFootPrint
                {
                    Format = TexFormat,
                    Width = (uint)pw,
                    Height = (uint)ph,
                    Depth = 1,
                    RowPitch = (uint)dstPitch,
                }
            };
            _cmdList.CopyTextureRegion(
                new TextureCopyLocation(_texture!, 0), 0, 0, 0,
                new TextureCopyLocation(_uploadBuf, foot), null);

            // Transition texture to PixelShaderResource for rendering.
            _cmdList.ResourceBarrierTransition(
                _texture!,
                ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource);
            _cmdList.Close();

            _cmdQueue.ExecuteCommandList(_cmdList);

            // Wait for the copy to finish using an out-of-band fence value.
            ulong copyDoneFenceVal = _fenceValues[_frameIndex] + 2000;
            _cmdQueue.Signal(_fence, copyDoneFenceVal);
            if (_fence.CompletedValue < copyDoneFenceVal)
            {
                _fence.SetEventOnCompletion(copyDoneFenceVal, _fenceEvent);
                _ = WaitForSingleObject(_fenceEvent, uint.MaxValue);

            }
        }
        catch (SharpGen.Runtime.SharpGenException sharpEX)
        {
            
            SharpGen.Runtime.Result sharpResult = _device.DeviceRemovedReason;
            ID3D12DeviceRemovedExtendedData drData = _device.QueryInterface<ID3D12DeviceRemovedExtendedData>();
            string errorMsg = $"D3D12 operation failed: {sharpEX.Message}\n" +
                $"Device Removed Reason: {sharpResult}\n"; // +
                //$"DRED Category: {drData.Category}\n" +
                //$"DRED ReasonCode: {drData.ReasonCode}\n" +
                //$"DRED Description: {drData.Description}";
            throw new Exception(errorMsg);
        }
    }

    // ── Resize ────────────────────────────────────────────────────────────────

    public void Resize(int width, int height)
    {
        if (_disposed || width < 1 || height < 1) return;
        if (width == _width && height == _height) return;

        WaitForAllFrames();

        foreach (var rt in _renderTargets) rt.Dispose();

        _swapChain.ResizeBuffers(
            0, (uint)width, (uint)height,
            Format.Unknown, SwapChainFlags.None).CheckError();

        _width = width;
        _height = height;
        _frameIndex = (int)_swapChain.CurrentBackBufferIndex;

        CreateRenderTargetViews();
    }

    // ── Fence helpers ─────────────────────────────────────────────────────────

    private void SignalFrame(int frame)
    {
        _fenceValues[frame]++;
        _cmdQueue.Signal(_fence, _fenceValues[frame]);
    }

    private void WaitForFrame(int frame)
    {
        if (_fence.CompletedValue < _fenceValues[frame])
        {
            _fence.SetEventOnCompletion(_fenceValues[frame], _fenceEvent);
            WaitForSingleObject(_fenceEvent, uint.MaxValue);
        }
    }

    private void WaitForCurrentFrame() => WaitForFrame(_frameIndex);

    private void WaitForAllFrames()
    {
        for (int i = 0; i < FrameCount; i++) WaitForFrame(i);
    }

    // ── Alignment helpers ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment)
        => (value + alignment - 1) & ~(alignment - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong AlignUp64(ulong value, int alignment)
        => ((value + (ulong)alignment - 1) / (ulong)alignment) * (ulong)alignment;

    // D3D12 constant for default shader component mapping (RGBA → RGBA).
    private const uint D3D12DefaultShader4ComponentMapping = 0x00001688u;

    // ── P/Invoke for Win32 event handles ──────────────────────────────────────
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset,
        bool bInitialState, string? lpName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        WaitForAllFrames();
        CloseHandle(_fenceEvent);

        _fence.Dispose();
        _pso.Dispose();
        _rootSig.Dispose();
        _srvHeap.Dispose();
        _texture?.Dispose();
        _uploadBuf?.Dispose();
        if (_renderTargets != null)
            foreach (var rt in _renderTargets) rt.Dispose();
        _rtvHeap.Dispose();
        if (_allocators != null)
            foreach (var a in _allocators) a.Dispose();
        _cmdList.Dispose();
        _swapChain.Dispose();
        _cmdQueue.Dispose();
        _device.Dispose();
    }
}
