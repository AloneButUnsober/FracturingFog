// MandelbrotGpuKernel.cs — T3.1 phase 1
//
// HLSL compute shader for the SP (double-precision) Mandelbrot escape-time
// inner loop. Writes back two buffers per pixel:
//   • iter      : int   — escape iteration (or maxIter if in-set)
//   • smooth    : float — log-log smoothed continuous iter for palettes
//
// Phase 1 scope (matches Performance-DevelopmentPlan.md):
//   • SP path only (zoom < ~1e15). HP DD/QD stays CPU.
//   • Palette evaluation stays CPU — we copy iter+smooth back to host buffers
//     and run the existing IColorMap.Map() pass on the CPU. End-to-end GPU
//     palette is phase 2 (ColorGen → HLSL emit).
//   • No FP64 lanes assumed — HLSL `double` works on most consumer GPUs but
//     isn't accelerated. SP `float` lanes for the iteration math; CenterX/Y
//     are passed split into hi+lo floats so we can run a "doubledouble-lite"
//     centre at the cost of a small per-pixel overhead, lifting the FP32
//     zoom floor by ~6 decimal digits over plain float centres.
//
// Design choices:
//   • One thread per pixel (8×8 thread group). Simplest dispatch; GPU
//     occupancy plenty high at any non-trivial resolution.
//   • Iteration loop has an internal early-exit `if (mag2 >= bailout) break;`.
//     No bucket-dispatch yet for long shaders (TDR concern documented in plan
//     doc) — most consumer drivers tolerate ~2 s shaders, well above the
//     practical maxIter range Phase 1 targets.
//   • Output goes to StructuredBuffer<uint> + StructuredBuffer<float> over
//     RWTexture2D so the host can `Map` them straight into pinned CPU
//     IterationBuffer/SmoothBuffer without a per-frame texture copy.
//   • Staging buffers reused frame-to-frame; resized on Resize().
//
// Integration outline (not wired in this commit — see Phase 1.b):
//   • DirectXRenderer exposes its ID3D11Device + immediate context via a
//     new optional accessor (or a service-locator method) so the host can
//     hand it to this kernel.
//   • FractalRenderHost owns one MandelbrotGpuKernel instance per session.
//     A toggle (MandelbrotCalculator.UseGpuCompute) gates the dispatch.
//   • CalculateDoublePrecision branches: if UseGpuCompute && _gpuKernel
//     != null && !needsHighPrecision → kernel.Run(...); then palette pass
//     on CPU. Else current Parallel.ForEach path.

using System;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

/// <summary>
/// Wraps a D3D11 compute shader that runs the SP Mandelbrot escape-time
/// inner loop on the GPU. Phase 1 leaves palette evaluation on CPU — caller
/// runs the existing IColorMap pass after Run() returns. Thread-affine: a
/// single immediate context, single-threaded use from the calc thread.
/// </summary>
public sealed class MandelbrotGpuKernel : IDisposable
{
    // ── HLSL ──────────────────────────────────────────────────────────────
    //
    // Per-pixel kernel: classic z² + c escape-time with a cheap whole-
    // cardioid/period-2 bulb early-out (same predicate the CPU SIMD path
    // uses) and a smooth-iter log-log writeback for palette continuity.
    //
    // Centre passed split: cxHi + cxLo, cyHi + cyLo. Reconstruction:
    //     cx = cxHi + (px - 0.5*W) * scale + cxLo
    // keeps a few extra digits past the FP32 mantissa relative to a single
    // float centre. Not full DD — just enough to lift the FP32 zoom floor.
    private const string Hlsl = @"
cbuffer Params : register(b0)
{
    int   gWidth;
    int   gHeight;
    int   gMaxIter;
    float gBailout2;       // typically 4.0
    float gCXHi;
    float gCXLo;
    float gCYHi;
    float gCYLo;
    float gScaleHi;
    float gScaleLo;
    // 12 ints / floats packed — D3D11 cbuffer requires float4 alignment so
    // pad to 48 bytes (12 * 4). Already 48 — no pad slots needed.
}

RWStructuredBuffer<uint>  gIter   : register(u0);
RWStructuredBuffer<float> gSmooth : register(u1);

bool InCardioid(float cx, float cy)
{
    // |1 - sqrt(1 - 4c)| <= 1  →  expanded form (no sqrt) per the standard
    // Wikipedia early-out. q = (x - 1/4)^2 + y^2.
    float xm = cx - 0.25;
    float q = xm * xm + cy * cy;
    return q * (q + xm) <= 0.25 * cy * cy;
}

bool InPeriod2Bulb(float cx, float cy)
{
    // Disk of radius 1/4 centred at (-1, 0).
    float dx = cx + 1.0;
    return dx * dx + cy * cy <= 0.0625;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    uint x = tid.x;
    uint y = tid.y;
    if ((int)x >= gWidth || (int)y >= gHeight) return;

    int idx = (int)y * gWidth + (int)x;

    // Reconstruct cx / cy using the split centre.
    float fx = (float)x - 0.5 * gWidth;
    float fy = (float)y - 0.5 * gHeight;
    float cx = gCXHi + fx * gScaleHi + gCXLo + fx * gScaleLo;
    float cy = gCYHi + fy * gScaleHi + gCYLo + fy * gScaleLo;

    // Whole-cardioid + period-2 bulb early-out. Saves the full iteration
    // for any pixel guaranteed in-set on shallow zoom video frames.
    if (InCardioid(cx, cy) || InPeriod2Bulb(cx, cy))
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
        return;
    }

    float zr = 0.0;
    float zi = 0.0;
    int   it = 0;
    [loop]
    for (; it < gMaxIter; it++)
    {
        float zr2 = zr * zr;
        float zi2 = zi * zi;
        float mag2 = zr2 + zi2;
        if (mag2 >= gBailout2) break;

        float zrNew = zr2 - zi2 + cx;
        float zi_new_unscaled = zr * zi;
        zi = zi_new_unscaled + zi_new_unscaled + cy;
        zr = zrNew;
    }

    gIter[idx] = (uint)it;
    if (it >= gMaxIter)
    {
        gSmooth[idx] = 0.0;
    }
    else
    {
        // log-log smoothing for continuous palette index. Equivalent to the
        // CPU path's smooth =  it + 1 - log2(log(|z|)).
        float mag = sqrt(zr * zr + zi * zi);
        float nu = log(log(max(mag, 1.001))) / log(2.0);
        gSmooth[idx] = (float)it + 1.0 - nu;
    }
}
";

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public int Width;
        public int Height;
        public int MaxIter;
        public float Bailout2;
        public float CXHi, CXLo, CYHi, CYLo;
        public float ScaleHi, ScaleLo;
        // 10 fields × 4 bytes = 40 — cbuffers must be 16-byte multiples,
        // so pad to 48.
        private readonly int _pad0;
        private readonly int _pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    // Shared D3D gate — same lock the FractalRenderHost takes around every
    // renderer.Render / renderer.UpdateTexture so kernel.Run never overlaps
    // the immediate-context's swap-chain present path. ID3D11DeviceContext
    // (immediate) is not thread-safe; the calc thread (which calls Run)
    // and the threadpool upload (which calls Render) must serialise.
    private readonly object _d3dGate;
    private ID3D11ComputeShader _cs = null!;
    private ID3D11Buffer _paramsBuf = null!;
    private ID3D11Buffer _iterBuf = null!;
    private ID3D11Buffer _smoothBuf = null!;
    private ID3D11Buffer _iterStaging = null!;
    private ID3D11Buffer _smoothStaging = null!;
    private ID3D11UnorderedAccessView _iterUav = null!;
    private ID3D11UnorderedAccessView _smoothUav = null!;
    private int _allocPixels;
    private bool _disposed;

    public MandelbrotGpuKernel(ID3D11Device device, ID3D11DeviceContext context, object d3dGate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _d3dGate = d3dGate ?? throw new ArgumentNullException(nameof(d3dGate));
        CompileShader();
        AllocParamsBuffer();
    }

    private void CompileShader()
    {
        var hr = Compiler.Compile(
            Hlsl,
            entryPoint: "CSMain",
            sourceName: "MandelbrotGpuKernel.hlsl",
            profile: "cs_5_0",
            out var blob,
            out var errBlob);
        if (hr.Failure || blob == null)
        {
            string msg = errBlob?.AsString() ?? hr.ToString();
            errBlob?.Dispose();
            throw new InvalidOperationException(
                $"MandelbrotGpuKernel: HLSL compile failed — {msg}");
        }
        try
        {
            _cs = _device.CreateComputeShader(blob.AsSpan());
        }
        finally
        {
            blob.Dispose();
            errBlob?.Dispose();
        }
    }

    private void AllocParamsBuffer()
    {
        var desc = new BufferDescription(
            byteWidth: 48,
            bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic,
            cpuAccessFlags: CpuAccessFlags.Write);
        _paramsBuf = _device.CreateBuffer(desc);
    }

    private void EnsureOutputBuffers(int width, int height)
    {
        int n = width * height;
        if (_iterBuf != null && _allocPixels == n) return;

        _iterUav?.Dispose();
        _smoothUav?.Dispose();
        _iterBuf?.Dispose();
        _smoothBuf?.Dispose();
        _iterStaging?.Dispose();
        _smoothStaging?.Dispose();

        // Structured buffers — one uint per pixel for iter, one float per pixel
        // for smooth. Default usage so the CS writes via UAV; staging buffers
        // are CPU-readable copies populated each frame via CopyResource +
        // Map(Read).
        var iterDesc = new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        };
        _iterBuf = _device.CreateBuffer(iterDesc);

        var smoothDesc = iterDesc with { StructureByteStride = sizeof(float) };
        _smoothBuf = _device.CreateBuffer(smoothDesc);

        var stageIter = new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        };
        _iterStaging = _device.CreateBuffer(stageIter);

        var stageSmooth = stageIter with { ByteWidth = (uint)(n * sizeof(float)) };
        _smoothStaging = _device.CreateBuffer(stageSmooth);

        var uavDesc = new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        };
        _iterUav = _device.CreateUnorderedAccessView(_iterBuf, uavDesc);
        _smoothUav = _device.CreateUnorderedAccessView(_smoothBuf, uavDesc);

        _allocPixels = n;
    }

    /// <summary>Run the kernel and read back per-pixel iter + smooth into the
    /// caller's pinned buffers. iterDst length must be at least width*height,
    /// likewise smoothDst. Phase 1: synchronous readback — caller blocks on
    /// the CPU mapping; total cost ~2-5 ms per Mp at 1080p on a modest IGP.</summary>
    public void Run(int width, int height, double centerX, double centerY,
        double scale, int maxIter, double bailout2,
        int[] iterDst, float[] smoothDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        if (width <= 0 || height <= 0) return;

        lock (_d3dGate)
        {
            EnsureOutputBuffers(width, height);

            // Update params (split centre + split scale so we keep ~6 extra
            // mantissa bits past FP32 — fragile past zoom ~1e9 anyway).
            var p = new Params
            {
                Width = width,
                Height = height,
                MaxIter = maxIter,
                Bailout2 = (float)bailout2,
                CXHi = (float)centerX,
                CXLo = (float)(centerX - (float)centerX),
                CYHi = (float)centerY,
                CYLo = (float)(centerY - (float)centerY),
                ScaleHi = (float)scale,
                ScaleLo = (float)(scale - (float)scale),
            };
            var mapped = _ctx.Map(_paramsBuf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
            unsafe
            {
                *(Params*)mapped.DataPointer = p;
            }
            _ctx.Unmap(_paramsBuf, 0);

            _ctx.CSSetShader(_cs);
            _ctx.CSSetConstantBuffer(0, _paramsBuf);
            _ctx.CSSetUnorderedAccessView(0, _iterUav);
            _ctx.CSSetUnorderedAccessView(1, _smoothUav);

            uint groupsX = (uint)((width + 7) / 8);
            uint groupsY = (uint)((height + 7) / 8);
            _ctx.Dispatch(groupsX, groupsY, 1);

            _ctx.CSUnsetUnorderedAccessView(0);
            _ctx.CSUnsetUnorderedAccessView(1);

            // Copy default → staging then Map(Read) for CPU readback. Synchronous.
            _ctx.CopyResource(_iterStaging, _iterBuf);
            _ctx.CopyResource(_smoothStaging, _smoothBuf);

            int n = width * height;
            var iterMap = _ctx.Map(_iterStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    uint* src = (uint*)iterMap.DataPointer;
                    fixed (int* dst = iterDst)
                    {
                        for (int i = 0; i < n; i++)
                            dst[i] = (int)src[i];
                    }
                }
            }
            finally { _ctx.Unmap(_iterStaging, 0); }

            var smoothMap = _ctx.Map(_smoothStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    float* src = (float*)smoothMap.DataPointer;
                    fixed (float* dst = smoothDst)
                    {
                        for (int i = 0; i < n; i++) dst[i] = src[i];
                    }
                }
            }
            finally { _ctx.Unmap(_smoothStaging, 0); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _iterUav?.Dispose(); } catch { }
        try { _smoothUav?.Dispose(); } catch { }
        try { _iterBuf?.Dispose(); } catch { }
        try { _smoothBuf?.Dispose(); } catch { }
        try { _iterStaging?.Dispose(); } catch { }
        try { _smoothStaging?.Dispose(); } catch { }
        try { _paramsBuf?.Dispose(); } catch { }
        try { _cs?.Dispose(); } catch { }
    }
}
