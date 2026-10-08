// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbrotGpuKernel.cs — T3.1 phase 1+2+4
//
// HLSL compute shader for the SP (double-precision) Mandelbrot escape-time
// inner loop. Writes back per-pixel buffers — iter, smooth, finalZD, and
// (T3.1 phase 4) packed BGRA color when a GPU palette is active.
//
// Two compiled shader variants kept:
//   • _csBase   — iter + smooth + finalZD only (palette done on CPU).
//   • _csColor  — same plus emitted EvalPalette and a gColor UAV write.
//                 Compiled on demand and cached per-theme by PaletteId
//                 (IGpuHlslPalette opt-in).
//
// Phase 1 scope (matches Performance-DevelopmentPlan.md):
//   • SP path only (zoom < ~1e15). HP DD/QD stays CPU.
//
// Phase 2 (this revision):
//   • IColorMap impls that also implement IGpuHlslPalette ship a HLSL Map
//     body. SetPalette splices it into the compute shader and caches the
//     compiled CS by PaletteId. Run(... colorDst) fills colorDst direct
//     from the GPU, letting the calculator skip its CPU palette pass.
//
// Phase 4 (this revision):
//   • New RWStructuredBuffer<uint> gColor : register(u3) — packed BGRA
//     output. Allocated only when a palette is active. Calculator's
//     ColorBuffer is filled in-place via Map+memcpy from a staging buffer.
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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
[SupportedOSPlatform("windows")]
public sealed class MandelbrotGpuKernel : IGpuKernel
{
    // ── HLSL builder ──────────────────────────────────────────────────────
    //
    // Per-pixel kernel: classic z² + c escape-time with a cheap whole-
    // cardioid/period-2 bulb early-out (same predicate the CPU SIMD path
    // uses) and a smooth-iter log-log writeback for palette continuity.
    //
    // Centre passed split: cxHi + cxLo, cyHi + cyLo. Reconstruction:
    //     cx = cxHi + (px - 0.5*W) * scale + cxLo
    // keeps a few extra digits past the FP32 mantissa relative to a single
    // float centre. Not full DD — just enough to lift the FP32 zoom floor.
    //
    // Two emit modes:
    //   • emitColor = false → base shader, writes iter/smooth/finalZD only.
    //   • emitColor = true  → also invokes EvalPalette(…) with the
    //     IGpuHlslPalette body spliced in + helpers prepended.
    //     gColor : register(u3) gets packed BGRA.
    //
    // The palette body assumes the canonical 15-input EvalPalette signature
    // (see GpuPaletteInputOrder in IGpuHlslPalette.cs) — the kernel composes
    // the function head/tail so the IGpuHlslPalette implementation only
    // ships the body.
    private static string BuildHlsl(string? paletteBody, string? paletteHelpers, bool emitColor)
    {
        // Both variants now come from the shared, dependency-free
        // MandelbrotKernelSource so the exact same HLSL feeds FXC (here) and DXC
        // (the V2 Vulkan colour probe). The gColor UAV + ordered-dither pack +
        // EvalPalette signature + branch splices live there; this class only
        // supplies the theme's IGpuHlslPalette body + helpers.
        return emitColor
            ? MandelbrotKernelSource.BuildColor(paletteHelpers, paletteBody)
            : MandelbrotKernelSource.BuildBase();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public int Width;
        public int Height;
        public int MaxIter;
        public float Bailout2;
        public float CXHi, CXLo, CYHi, CYLo;
        public float ScaleHi, ScaleLo;
        public int UsePerRow;
        public int FractalKind;
        public float Param0;
        public float Param1;
        // F11b: GPU ordered-dither amp (0 = off). Was _pad0.
        public float DitherStrength;
        // #1173-I: domain warp (Strength 0 = off). 72 bytes; the cbuffer is 80.
        public float WarpStrength, WarpK, WarpHalfSpan;
    }

    // V6 (#82): deep-zoom perturbation params. Mirrors the HLSL PerturbParams
    // cbuffer (register b0) in MandelbrotKernelSource.BuildPerturb and the
    // Vulkan PerturbParamsBlob: 4 ints (16 B) then 4 doubles (32 B) = 48 B, a
    // multiple of 16 with no scalar straddling a 16-byte cbuffer row.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerturbParams
    {
        public double Scale, EscapeR2, OffX0, OffY0;
        public int Width, Height, MaxIter, RefLen, RowBase, Pad0, Pad1, Pad2;
    }

    /// <summary>Phase 3 fractal selector. Matches the shader's
    /// <c>gFractalKind</c> switch order. Mandelbrot is the default; other
    /// kinds pass appropriate per-pixel <c>cIter</c> + <c>z_0</c> init.</summary>
    // FractalKind enum moved to FracturingFog.Rendering.IGpuKernel (top-level)
    // in Phase X.0 / Slice 0.1b so the interface boundary can name it without
    // referencing this D3D-bound class. Existing in-file references compile
    // unchanged because the enum is in the same namespace.

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    // Shared D3D gate — same lock the FractalRenderHost takes around every
    // renderer.Render / renderer.UpdateTexture so kernel.Run never overlaps
    // the immediate-context's swap-chain present path. ID3D11DeviceContext
    // (immediate) is not thread-safe; the calc thread (which calls Run)
    // and the threadpool upload (which calls Render) must serialise.
    private readonly object _d3dGate;
    // Phase 4: shader cache. _csBase = no-color variant (palette on CPU).
    // _csByPaletteId[paletteId] = color-emitting variants (one per theme).
    private ID3D11ComputeShader _csBase = null!;
    private readonly Dictionary<string, ID3D11ComputeShader> _csByPaletteId = new(StringComparer.Ordinal);
    private ID3D11Buffer _paramsBuf = null!;
    private ID3D11Buffer _iterBuf = null!;
    private ID3D11Buffer _smoothBuf = null!;
    private ID3D11Buffer _finalZDBuf = null!;
    private ID3D11Buffer _iterStaging = null!;
    private ID3D11Buffer _smoothStaging = null!;
    private ID3D11Buffer _finalZDStaging = null!;
    private ID3D11UnorderedAccessView _iterUav = null!;
    private ID3D11UnorderedAccessView _smoothUav = null!;
    private ID3D11UnorderedAccessView _finalZDUav = null!;
    private int _allocPixels;
    // Phase 1.b: per-row maxIter SRV. Sized to Height; re-alloc on Height
    // change. Null until first PerTile run.
    private ID3D11Buffer? _perRowBuf;
    private ID3D11ShaderResourceView? _perRowSrv;
    private int _perRowAllocRows;
    // Phase 4: GPU-resident color buffer + staging. Allocated only when a
    // palette is active. Output is packed BGRA, matching the CPU
    // ColorBuffer layout (alpha = 0xFF, then RGB).
    private ID3D11Buffer? _colorBuf;
    private ID3D11Buffer? _colorStaging;
    private ID3D11UnorderedAccessView? _colorUav;
    private int _colorAllocPixels;
    // #1173-J: orbit-trap output (u4) of the F16 orbit variant + staging. Allocated
    // on the first orbit Run that asks for it. _orbitPaletteIds = the cached
    // palette ids built with BuildColorOrbit (the only shaders that declare u4).
    private ID3D11Buffer? _trapBuf;
    private ID3D11Buffer? _trapStaging;
    private ID3D11UnorderedAccessView? _trapUav;
    private int _trapAllocPixels;
    private readonly HashSet<string> _orbitPaletteIds = new(StringComparer.Ordinal);
    // Phase 2: currently active palette state. When non-null, Run() with a
    // colorDst argument uses the color-emitting variant.
    private string? _activePaletteId;
    // V6 (#82): deep-zoom perturbation shader + its dedicated buffers. The
    // reference-orbit Hi-limb doubles (t0/t1) + 48-byte double param cbuffer
    // (b0); iter/smooth/finalZD outputs reuse the shared EnsureOutputBuffers
    // UAVs (u0/u1/u2). Compiled lazily on first RunPerturb so devices that
    // never deep-zoom on GPU pay nothing. Null until then.
    private ID3D11ComputeShader? _csPerturb;
    private ID3D11Buffer? _perturbParamsBuf;
    private ID3D11Buffer? _refZrBuf;
    private ID3D11Buffer? _refZiBuf;
    private ID3D11ShaderResourceView? _refZrSrv;
    private ID3D11ShaderResourceView? _refZiSrv;
    private int _refAllocLen;
    // FeatureDataDoubles cached once — DoublePrecisionFloatShaderOps gates
    // SupportsPerturbation. -1 = unqueried, 0 = no, 1 = yes.
    private int _fp64 = -1;
    private bool _disposed;

    public MandelbrotGpuKernel(ID3D11Device device, ID3D11DeviceContext context, object d3dGate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _d3dGate = d3dGate ?? throw new ArgumentNullException(nameof(d3dGate));
        _csBase = CompileShader(BuildHlsl(null, null, emitColor: false), label: "base");
        AllocParamsBuffer();
    }

    /// <summary>Compile a CS variant from a fully composed HLSL string.
    /// Caller is responsible for caching the returned shader.</summary>
    private ID3D11ComputeShader CompileShader(string hlsl, string label, string entryPoint = "CSMain")
        => D3DShaderCache.CompileOrLoad(       // #456 — machine-cached FXC bytecode
            _device,
            hlsl,
            entryPoint: entryPoint,
            profile: "cs_5_0",
            sourceName: $"MandelbrotGpuKernel.{label}.hlsl",
            errorLabel: $"MandelbrotGpuKernel ({label})");

    /// <summary>Phase 2: switch active GPU palette. Pass null to clear; the
    /// next Run-with-color call will use the base shader (CPU palette path).
    /// Compiles + caches the per-theme shader on first set. PaletteId is
    /// the cache key — same id → same compiled shader reused.</summary>
    public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette)
    {
        if (palette == null) { _activePaletteId = null; return; }
        string id = palette.PaletteId ?? "";
        if (string.IsNullOrEmpty(id)) { _activePaletteId = null; return; }
        if (_csByPaletteId.ContainsKey(id))
        {
            _activePaletteId = id;
            return;
        }
        try
        {
            // F16 (#603) — an orbit palette with a non-None mask builds the
            // orbit-accumulating kernel; every other palette uses the plain
            // escape-only colour kernel.
            bool orbit = palette is FracturingFog.Interefaces.IGpuOrbitPalette o
                         && o.OrbitInputs != FracturingFog.Interefaces.GpuOrbitInputs.None;
            string hlsl = orbit
                ? MandelbrotKernelSource.BuildColorOrbit(palette.HlslPrelude, palette.HlslPaletteBody,
                    (int)((FracturingFog.Interefaces.IGpuOrbitPalette)palette).OrbitInputs)
                : BuildHlsl(palette.HlslPaletteBody, palette.HlslPrelude, emitColor: true);
            var cs = CompileShader(hlsl, label: id);
            _csByPaletteId[id] = cs;
            if (orbit) _orbitPaletteIds.Add(id);
            _activePaletteId = id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MandelbrotGpuKernel] palette '{id}' HLSL compile failed; staying on CPU palette: {ex.Message}");
            _activePaletteId = null;
        }
    }

    /// <summary>Whether the kernel currently has an active GPU palette
    /// loaded. Read by the calculator to decide between Run-with-color and
    /// Run-without-color.</summary>
    public bool HasGpuPalette => _activePaletteId != null && _csByPaletteId.ContainsKey(_activePaletteId);

    private void EnsureColorBuffers(int n)
    {
        if (_colorBuf != null && _colorAllocPixels == n) return;
        AllocColorBuffer(n);
    }

    private void AllocColorBuffer(int n)
    {
        _colorUav?.Dispose();
        _colorBuf?.Dispose();
        _colorStaging?.Dispose();

        var desc = new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        };
        _colorBuf = _device.CreateBuffer(desc);

        var stage = new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        };
        _colorStaging = _device.CreateBuffer(stage);

        var uavDesc = new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        };
        _colorUav = _device.CreateUnorderedAccessView(_colorBuf, uavDesc);
        _colorAllocPixels = n;
    }

    // #1173-J — the orbit variant's float trap UAV (u4) + its staging copy.
    private void EnsureTrapBuffers(int n)
    {
        if (_trapBuf != null && _trapAllocPixels == n) return;
        _trapUav?.Dispose();
        _trapBuf?.Dispose();
        _trapStaging?.Dispose();
        _trapBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(float)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _trapStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(float)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });
        _trapUav = _device.CreateUnorderedAccessView(_trapBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        });
        _trapAllocPixels = n;
    }

    private void AllocParamsBuffer()
    {
        var desc = new BufferDescription(
            byteWidth: 80,      // #1173-I: Params is 72 bytes (domain warp), float4-rounded
            bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic,
            cpuAccessFlags: CpuAccessFlags.Write);
        _paramsBuf = _device.CreateBuffer(desc);
    }

    private void EnsurePerRowBuffer(int height)
    {
        if (_perRowBuf != null && _perRowAllocRows == height) return;
        _perRowSrv?.Dispose();
        _perRowBuf?.Dispose();
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(height * sizeof(uint)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        };
        _perRowBuf = _device.CreateBuffer(desc);
        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)height },
        };
        _perRowSrv = _device.CreateShaderResourceView(_perRowBuf, srvDesc);
        _perRowAllocRows = height;
    }

    private void EnsureOutputBuffers(int width, int height)
    {
        int n = width * height;
        if (_iterBuf != null && _allocPixels == n) return;

        _iterUav?.Dispose();
        _smoothUav?.Dispose();
        _finalZDUav?.Dispose();
        _iterBuf?.Dispose();
        _smoothBuf?.Dispose();
        _finalZDBuf?.Dispose();
        _iterStaging?.Dispose();
        _smoothStaging?.Dispose();
        _finalZDStaging?.Dispose();

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

        // FinalZD: float4 per pixel (zr, zi, dr, di).
        var finalZDDesc = new BufferDescription
        {
            ByteWidth = (uint)(n * 4 * sizeof(float)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 4 * sizeof(float),
        };
        _finalZDBuf = _device.CreateBuffer(finalZDDesc);

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

        var stageFinalZD = stageIter with { ByteWidth = (uint)(n * 4 * sizeof(float)) };
        _finalZDStaging = _device.CreateBuffer(stageFinalZD);

        var uavDesc = new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        };
        _iterUav = _device.CreateUnorderedAccessView(_iterBuf, uavDesc);
        _smoothUav = _device.CreateUnorderedAccessView(_smoothBuf, uavDesc);
        _finalZDUav = _device.CreateUnorderedAccessView(_finalZDBuf, uavDesc);

        _allocPixels = n;
    }

    /// <summary>Run the kernel and read back per-pixel iter + smooth into the
    /// caller's pinned buffers. iterDst length must be at least width*height,
    /// likewise smoothDst. Phase 1: synchronous readback — caller blocks on
    /// the CPU mapping; total cost ~2-5 ms per Mp at 1080p on a modest IGP.</summary>
    /// <summary>Last dispatch's wall time in ms — measured from start of
    /// Run() up to the first Map(Read), so it covers cbuffer + per-row
    /// uploads, Dispatch submission, and the implicit GPU flush triggered
    /// by the first staging Map. Includes driver synchronisation, not just
    /// shader runtime.</summary>
    public double LastDispatchMs { get; private set; }

    /// <summary>Last dispatch's CPU readback cost in ms — Map+memcpy of
    /// all three staging buffers (iter, smooth, finalZD). Useful for
    /// diagnosing PCIe / unified-memory bandwidth bottlenecks on weak
    /// IGPs.</summary>
    public double LastReadbackMs { get; private set; }

    public void Run(int width, int height, double centerX, double centerY,
        double scale, int maxIter, double bailout2,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst,
        int[]? perRowMaxIter = null,
        FractalKind kind = FractalKind.Mandelbrot,
        float param0 = 0f, float param1 = 0f,
        uint[]? colorDst = null,
        float[]? trapDst = null,
        GpuDomainWarp warp = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        if (width <= 0 || height <= 0) return;

        // Phase 2/4: GPU palette path is only taken when a colorDst array is
        // supplied AND a palette is active. Mandelbrot-only — Julia and the
        // alt fractals come back through the CPU palette path for now.
        bool useColorPath = colorDst != null && HasGpuPalette;
        // #1173-J — only the orbit variant declares the u4 trap output.
        bool useTrap = useColorPath && trapDst != null && _orbitPaletteIds.Contains(_activePaletteId!);

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureOutputBuffers(width, height);
            if (useColorPath) EnsureColorBuffers(width * height);
            if (useTrap) EnsureTrapBuffers(width * height);

            bool usePerRow = perRowMaxIter != null && perRowMaxIter.Length >= height;
            if (usePerRow)
            {
                EnsurePerRowBuffer(height);
                // Upload per-row caps as uint[] via WriteDiscard. perRowMaxIter
                // is int[] from the calculator — we narrow per-element to uint
                // since negative caps don't make sense (defensive: shader
                // falls back to gMaxIter when cell is 0).
                var prMapped = _ctx.Map(_perRowBuf!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
                unsafe
                {
                    uint* dst = (uint*)prMapped.DataPointer;
                    for (int i = 0; i < height; i++)
                    {
                        int v = perRowMaxIter![i];
                        dst[i] = v > 0 ? (uint)v : 0u;
                    }
                }
                _ctx.Unmap(_perRowBuf!, 0);
            }

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
                UsePerRow = usePerRow ? 1 : 0,
                FractalKind = (int)kind,
                Param0 = param0,
                Param1 = param1,
                // F11b: same runtime knob as the CPU deband (F11a). Default-off
                // statics → 0 → the shader packs the plain round, unchanged.
                DitherStrength = FracturingFog.Models.GradientColorMap.DitherEnabled
                    ? FracturingFog.Models.GradientColorMap.DitherStrength
                    : 0f,
                WarpStrength = warp.Active ? warp.Strength : 0f,
                WarpK = warp.K,
                WarpHalfSpan = warp.HalfSpan,
            };
            var mapped = _ctx.Map(_paramsBuf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
            unsafe
            {
                *(Params*)mapped.DataPointer = p;
            }
            _ctx.Unmap(_paramsBuf, 0);

            // Pick the right CS variant. Color path uses the cached
            // per-palette shader; non-color path uses the base.
            var shader = useColorPath
                ? _csByPaletteId[_activePaletteId!]
                : _csBase;
            _ctx.CSSetShader(shader);
            _ctx.CSSetConstantBuffer(0, _paramsBuf);
            _ctx.CSSetUnorderedAccessView(0, _iterUav);
            _ctx.CSSetUnorderedAccessView(1, _smoothUav);
            _ctx.CSSetUnorderedAccessView(2, _finalZDUav);
            if (useColorPath) _ctx.CSSetUnorderedAccessView(3, _colorUav);
            if (useTrap) _ctx.CSSetUnorderedAccessView(4, _trapUav);
            if (usePerRow) _ctx.CSSetShaderResource(0, _perRowSrv);

            uint groupsX = (uint)((width + 7) / 8);
            uint groupsY = (uint)((height + 7) / 8);
            _ctx.Dispatch(groupsX, groupsY, 1);

            _ctx.CSUnsetUnorderedAccessView(0);
            _ctx.CSUnsetUnorderedAccessView(1);
            _ctx.CSUnsetUnorderedAccessView(2);
            if (useColorPath) _ctx.CSUnsetUnorderedAccessView(3);
            if (useTrap) _ctx.CSUnsetUnorderedAccessView(4);
            if (usePerRow) _ctx.CSUnsetShaderResource(0);

            // Copy default → staging then Map(Read) for CPU readback. Synchronous.
            _ctx.CopyResource(_iterStaging, _iterBuf);
            _ctx.CopyResource(_smoothStaging, _smoothBuf);
            _ctx.CopyResource(_finalZDStaging, _finalZDBuf);
            if (useColorPath) _ctx.CopyResource(_colorStaging!, _colorBuf!);
            if (useTrap) _ctx.CopyResource(_trapStaging!, _trapBuf!);

            // Dispatch + flush cost: the first Map(Read) below blocks until
            // GPU finishes, so dispatch_ms covers cbuffer upload, Dispatch
            // submission, and the implicit flush — everything but the
            // CPU-side memcpy.
            long tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();
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

            // Unpack the packed float4 into four CPU arrays. Could be SIMD'd
            // (Avx.GatherVector256) — left scalar for clarity since cost is
            // ~0.5 ms at 1080p, much less than the kernel + IColorMap pass.
            var fzdMap = _ctx.Map(_finalZDStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    float* src = (float*)fzdMap.DataPointer;
                    fixed (float* zr = finalZrDst)
                    fixed (float* zi = finalZiDst)
                    fixed (float* dr = finalDrDst)
                    fixed (float* di = finalDiDst)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int b = i * 4;
                            zr[i] = src[b + 0];
                            zi[i] = src[b + 1];
                            dr[i] = src[b + 2];
                            di[i] = src[b + 3];
                        }
                    }
                }
            }
            finally { _ctx.Unmap(_finalZDStaging, 0); }

            if (useColorPath)
            {
                var colMap = _ctx.Map(_colorStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
                try
                {
                    unsafe
                    {
                        uint* src = (uint*)colMap.DataPointer;
                        fixed (uint* dst = colorDst!)
                        {
                            // Plain memcpy — packed BGRA matches CPU
                            // ColorBuffer layout (0xAARRGGBB with A=0xFF).
                            Buffer.MemoryCopy(src, dst, (long)n * sizeof(uint), (long)n * sizeof(uint));
                        }
                    }
                }
                finally { _ctx.Unmap(_colorStaging!, 0); }
            }

            if (useTrap)
            {
                var trapMap = _ctx.Map(_trapStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
                try
                {
                    unsafe
                    {
                        fixed (float* dst = trapDst!)
                            Buffer.MemoryCopy((void*)trapMap.DataPointer, dst, (long)n * sizeof(float), (long)n * sizeof(float));
                    }
                }
                finally { _ctx.Unmap(_trapStaging!, 0); }
            }

            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
            LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
        }
    }

    // ── V6 (#82): deep-zoom GPU perturbation ─────────────────────────────────

    /// <summary>Whether this D3D device can run the double perturbation kernel
    /// — i.e. advertises <c>DoublePrecisionFloatShaderOps</c>. Queried once and
    /// cached. The calculator gates its GPU-perturbation dispatch on this so a
    /// device without FP64 shader ops falls back to the CPU deep path.</summary>
    public string BackendLabel => "D3D11";

    public bool SupportsPerturbation
    {
        get
        {
            if (_fp64 < 0)
            {
                try
                {
                    var d = _device.CheckFeatureSupport<FeatureDataDoubles>(Feature.Doubles);
                    _fp64 = d.DoublePrecisionFloatShaderOps ? 1 : 0;
                }
                catch { _fp64 = 0; }
            }
            return _fp64 == 1;
        }
    }

    /// <summary>Deep-zoom perturbation dispatch. Runs the double δ-rebased loop
    /// (MandelbrotKernelSource.BuildPerturb, entry CSPerturb — the same HLSL the
    /// Vulkan backend runs) over a CPU-built Hi-limb reference orbit, writing
    /// back iter/smooth/finalZD exactly like <see cref="Run"/>. Colour stays on
    /// the CPU (the calculator's FillAuxAndColorHP pass consumes finalZD).</summary>
    public void RunPerturb(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        if (!SupportsPerturbation)
            throw new NotSupportedException("D3D device has no DoublePrecisionFloatShaderOps — cannot run the perturbation kernel.");
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));
        if (refZr.Length < refLen || refZi.Length < refLen)
            throw new ArgumentException("reference-orbit arrays shorter than refLen");

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureOutputBuffers(width, height);      // shares iter/smooth/finalZD UAVs (u0/u1/u2)
            EnsurePerturbParamsBuffer();
            EnsureRefOrbitBuffers(refLen);
            _csPerturb ??= CompileShader(MandelbrotKernelSource.BuildPerturb(), label: "perturb",
                entryPoint: MandelbrotKernelSource.PerturbEntryPoint);

            // Upload the reference orbit (Hi-limb doubles) into t0/t1.
            UploadDoubles(_refZrBuf!, refZr, refLen);
            UploadDoubles(_refZiBuf!, refZi, refLen);

            var p = new PerturbParams
            {
                Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
                Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
                RowBase = 0,
            };

            _ctx.CSSetShader(_csPerturb);
            _ctx.CSSetShaderResource(0, _refZrSrv);   // t0
            _ctx.CSSetShaderResource(1, _refZiSrv);   // t1
            DispatchPerturbBands(width, height, maxIter, srvCount: 2, "", rowBase =>
            {
                p.RowBase = rowBase;
                var mapped = _ctx.Map(_perturbParamsBuf!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
                unsafe { *(PerturbParams*)mapped.DataPointer = p; }
                _ctx.Unmap(_perturbParamsBuf!, 0);
                _ctx.CSSetConstantBuffer(0, _perturbParamsBuf);   // re-bind after WriteDiscard rename
            });
            ReadPerturbOutputs(width * height, t0, iterDst, smoothDst, finalZrDst, finalZiDst, finalDrDst, finalDiDst);
        }
    }

    // ── #838 / G4.7 Buddhabrot sample pass ───────────────────────────────────────

    // 64 bytes: 11 ints then 5 floats, the HLSL BuddhaParams cbuffer byte-for-byte.
    [StructLayout(LayoutKind.Sequential)]
    private struct BuddhaParams
    {
        public int Width, Height, MaxOrbit, InSet, Low, Mid, Hd;
        public uint Seed, Batch, ThreadBase, ThreadCount;
        public float Scale, MidX, MidY, Pad0, Pad1;
    }

    private ID3D11ComputeShader? _csBuddha;
    private ID3D11Buffer? _buddhaParamsBuf;
    private ID3D11Buffer? _buddhaHitsBuf;
    private ID3D11Buffer? _buddhaHitsStaging;
    private ID3D11UnorderedAccessView? _buddhaHitsUav;
    private ID3D11Query? _buddhaDone;
    private int _buddhaHitsAlloc;

    /// <summary>#838 / G4.7 — the uniform Buddhabrot sample pass is plain float.</summary>
    public bool SupportsBuddhabrot => true;

    /// <summary>#838 / G4.7 — one Buddhabrot uniform sample batch
    /// (BuddhaKernelSource, the same HLSL Vulkan runs): clear the device histogram,
    /// run the samples in adaptive TDR-sized dispatches (BuddhaKernelSource.Plan;
    /// each Flushed and waited on through an event query, the shared D3D gate held
    /// per dispatch so presentation can run between them), then add the three bands
    /// into the caller's arrays.</summary>
    public void RunBuddhaBatch(in GpuBuddhaBatch b, uint[] hitsR, uint[] hitsG, uint[] hitsB)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        int n = b.Width * b.Height;
        if (n <= 0 || b.Samples <= 0) return;
        if (hitsR.Length < n || hitsG.Length < n || hitsB.Length < n)
            throw new ArgumentException("hit arrays shorter than width * height");

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_d3dGate)
        {
            _csBuddha ??= CompileShader(BuddhaKernelSource.Build(), label: "buddha", entryPoint: BuddhaKernelSource.EntryPoint);
            _buddhaParamsBuf ??= _device.CreateBuffer(new BufferDescription(
                byteWidth: 64, bindFlags: BindFlags.ConstantBuffer,
                usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
            _buddhaDone ??= _device.CreateQuery(new QueryDescription(QueryType.Event));
            EnsureBuddhaHits(n * 3);
            _ctx.ClearUnorderedAccessView(_buddhaHitsUav!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
        }

        var p = new BuddhaParams
        {
            Width = b.Width, Height = b.Height, MaxOrbit = b.MaxOrbit, InSet = b.InSet ? 1 : 0,
            Low = b.Low, Mid = b.Mid, Hd = b.HighDefinition ? 1 : 0,
            Seed = b.Seed, Batch = (uint)b.Batch, ThreadCount = (uint)b.Samples,
            Scale = (float)b.Scale, MidX = (float)b.MidX, MidY = (float)b.MidY,
        };
        BuddhaKernelSource.Plan(b.Samples, b.MaxOrbit, (baseT, count) =>
        {
            lock (_d3dGate)
            {
                long tChunk = System.Diagnostics.Stopwatch.GetTimestamp();
                var pl = p;
                pl.ThreadBase = (uint)baseT;
                var mapped = _ctx.Map(_buddhaParamsBuf!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
                unsafe { *(BuddhaParams*)mapped.DataPointer = pl; }
                _ctx.Unmap(_buddhaParamsBuf!, 0);
                _ctx.CSSetShader(_csBuddha);
                _ctx.CSSetConstantBuffer(0, _buddhaParamsBuf);
                _ctx.CSSetUnorderedAccessView(0, _buddhaHitsUav);
                _ctx.Dispatch((uint)((count + BuddhaKernelSource.GroupSize - 1) / BuddhaKernelSource.GroupSize), 1, 1);
                _ctx.CSUnsetUnorderedAccessView(0);
                // One GPU packet per dispatch (TDR); wait for it so the next is sized
                // from a real time.
                _ctx.End(_buddhaDone!);
                _ctx.Flush();
                while (_ctx.GetData(_buddhaDone!, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)   // S_FALSE until done
                    System.Threading.Thread.Yield();
                return (System.Diagnostics.Stopwatch.GetTimestamp() - tChunk) * 1000.0
                       / System.Diagnostics.Stopwatch.Frequency;
            }
        });

        long tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_d3dGate)
        {
            _ctx.CopyResource(_buddhaHitsStaging!, _buddhaHitsBuf!);
            var map = _ctx.Map(_buddhaHitsStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    uint* src = (uint*)map.DataPointer;
                    for (int i = 0; i < n; i++)
                    {
                        hitsR[i] += src[i];
                        hitsG[i] += src[n + i];
                        hitsB[i] += src[2 * n + i];
                    }
                }
            }
            finally { _ctx.Unmap(_buddhaHitsStaging!, 0); }
        }
        long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
        double freq = System.Diagnostics.Stopwatch.Frequency;
        LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
        LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
    }

    private void EnsureBuddhaHits(int uints)
    {
        if (_buddhaHitsBuf != null && _buddhaHitsAlloc == uints) return;
        _buddhaHitsUav?.Dispose();
        _buddhaHitsBuf?.Dispose();
        _buddhaHitsStaging?.Dispose();
        _buddhaHitsBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(uints * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess,
            Usage = ResourceUsage.Default,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _buddhaHitsStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(uints * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });
        _buddhaHitsUav = _device.CreateUnorderedAccessView(_buddhaHitsBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)uints },
        });
        _buddhaHitsAlloc = uints;
    }

    /// <summary>#607 / G4.6 — the orbit kernel's double work is the plain kernel's;
    /// its sampling is float.</summary>
    public bool SupportsPerturbationOrbit => SupportsPerturbation;

    /// <summary>#607 / G4.6 — deep-zoom perturbation with orbit accumulation
    /// (MandelbrotKernelSource.BuildPerturbOrbit, one shader per mask). Same band
    /// tiling, too-slow abort and readback as <see cref="RunPerturb"/>, plus the
    /// gOrbit means (u3).</summary>
    public void RunPerturbOrbit(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        int orbitMask, double centerRe, double centerIm,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst,
        float[] orbitDst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        if (!SupportsPerturbation)
            throw new NotSupportedException("D3D device has no DoublePrecisionFloatShaderOps — cannot run the orbit perturbation kernel.");
        orbitMask &= MandelbrotKernelSource.OrbAll;
        int stride = MandelbrotKernelSource.PerturbOrbitStride(orbitMask);
        if (stride == 0) throw new ArgumentException("no orbit inputs in the mask", nameof(orbitMask));
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));
        if (refZr.Length < refLen || refZi.Length < refLen)
            throw new ArgumentException("reference-orbit arrays shorter than refLen");
        int n = width * height;
        if (orbitDst.Length < n * stride) throw new ArgumentException("orbitDst shorter than width * height * stride", nameof(orbitDst));

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureOutputBuffers(width, height);
            EnsurePerturbParamsBuffer();
            EnsureRefOrbitBuffers(refLen);
            EnsureOrbitOutBuffers(n * stride);
            if (!_csPerturbOrbit.TryGetValue(orbitMask, out var cs))
            {
                cs = CompileShader(MandelbrotKernelSource.BuildPerturbOrbit(orbitMask), label: $"perturb-orbit-{orbitMask:X}",
                    entryPoint: MandelbrotKernelSource.PerturbOrbitEntryPoint);
                _csPerturbOrbit[orbitMask] = cs;
            }

            UploadDoubles(_refZrBuf!, refZr, refLen);
            UploadDoubles(_refZiBuf!, refZi, refLen);

            var p = new PerturbParams
            {
                Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
                Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
                RowBase = 0,
                Pad0 = BitConverter.SingleToInt32Bits((float)centerRe),   // gCRe
                Pad1 = BitConverter.SingleToInt32Bits((float)centerIm),   // gCIm
            };

            _ctx.CSSetShader(cs);
            _ctx.CSSetShaderResource(0, _refZrSrv);
            _ctx.CSSetShaderResource(1, _refZiSrv);
            _ctx.CSSetUnorderedAccessView(3, _orbitOutUav);   // u3 (unbound by DispatchPerturbBands)
            DispatchPerturbBands(width, height, maxIter, srvCount: 2, " (orbit)", rowBase =>
            {
                p.RowBase = rowBase;
                var mapped = _ctx.Map(_perturbParamsBuf!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
                unsafe { *(PerturbParams*)mapped.DataPointer = p; }
                _ctx.Unmap(_perturbParamsBuf!, 0);
                _ctx.CSSetConstantBuffer(0, _perturbParamsBuf);
            }, uavCount: 4);
            ReadPerturbOutputs(n, t0, iterDst, smoothDst, finalZrDst, finalZiDst, finalDrDst, finalDiDst);

            _ctx.CopyResource(_orbitOutStaging, _orbitOutBuf);
            var map = _ctx.Map(_orbitOutStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe { new ReadOnlySpan<float>((void*)map.DataPointer, n * stride).CopyTo(orbitDst); }
            }
            finally { _ctx.Unmap(_orbitOutStaging!, 0); }
        }
    }

    // #607 / G4.6 — orbit perturbation shaders (one per mask) + the gOrbit output.
    private readonly Dictionary<int, ID3D11ComputeShader> _csPerturbOrbit = new();
    private ID3D11Buffer? _orbitOutBuf;
    private ID3D11Buffer? _orbitOutStaging;
    private ID3D11UnorderedAccessView? _orbitOutUav;
    private int _orbitOutAllocFloats;

    private void EnsureOrbitOutBuffers(int floats)
    {
        if (_orbitOutBuf != null && _orbitOutAllocFloats >= floats) return;
        _orbitOutUav?.Dispose();
        _orbitOutBuf?.Dispose();
        _orbitOutStaging?.Dispose();
        _orbitOutBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(floats * sizeof(float)),
            BindFlags = BindFlags.UnorderedAccess,
            Usage = ResourceUsage.Default,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _orbitOutStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(floats * sizeof(float)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        });
        _orbitOutUav = _device.CreateUnorderedAccessView(_orbitOutBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)floats },
        });
        _orbitOutAllocFloats = floats;
    }

    /// <summary>#88 / G4.5 — the SA kernel needs only the double ops the plain one
    /// does (squared-magnitude FindSkip, no double sqrt / division).</summary>
    public bool SupportsPerturbationSA => SupportsPerturbation;

    /// <summary>#88 / G4.5 — deep-zoom perturbation with the Series-Approximation
    /// prelude (MandelbrotKernelSource.BuildPerturbSA, entry CSPerturbSA — the same
    /// HLSL the Vulkan backend runs): per pixel FindSkip → k, δ_k, dz_k from the
    /// uploaded coefficients, then the rebased δ loop from k. Same band tiling,
    /// too-slow abort and readback as <see cref="RunPerturb"/>.</summary>
    public void RunPerturbSA(
        int width, int height,
        double scale, int maxIter, double escapeRadius2,
        double offsetX0, double offsetY0,
        double[] refZr, double[] refZi, int refLen,
        double saTolerance, int safeMax,
        double[] aR, double[] aI, double[] bR, double[] bI,
        double[] cR, double[] cI, double[] dR, double[] dI,
        int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst,
        float[] finalDrDst, float[] finalDiDst,
        double[]? blaCoeffs = null, int blaLevels = 0)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MandelbrotGpuKernel));
        if (!SupportsPerturbation)
            throw new NotSupportedException("D3D device has no DoublePrecisionFloatShaderOps — cannot run the SA perturbation kernel.");
        if (blaCoeffs == null || blaCoeffs.Length < 5) blaLevels = 0;   // #88 / G4.5b
        if (width <= 0 || height <= 0) return;
        if (refLen < 1) throw new ArgumentException("reference orbit is empty", nameof(refLen));
        if (refZr.Length < refLen || refZi.Length < refLen)
            throw new ArgumentException("reference-orbit arrays shorter than refLen");
        int coeffLen = aR.Length;   // SA arrays are refLen + 1 long
        if (coeffLen < refLen + 1)
            throw new ArgumentException("SA coefficient arrays shorter than refLen + 1");

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureOutputBuffers(width, height);
            EnsureRefOrbitBuffers(refLen);
            EnsureSaBuffers(coeffLen);
            _csPerturbSa ??= CompileShader(MandelbrotKernelSource.BuildPerturbSA(), label: "perturb-sa",
                entryPoint: MandelbrotKernelSource.PerturbSaEntryPoint);

            UploadDoubles(_refZrBuf!, refZr, refLen);
            UploadDoubles(_refZiBuf!, refZi, refLen);
            var coeffs = new[] { aR, aI, bR, bI, cR, cI, dR, dI };
            for (int i = 0; i < 8; i++) UploadDoubles(_saBufs[i]!, coeffs[i], coeffLen);
            EnsureBlaBuffer(blaLevels > 0 ? blaCoeffs!.Length : 5);
            if (blaLevels > 0) UploadDoubles(_blaBuf!, blaCoeffs!, blaCoeffs!.Length);

            var p = new PerturbSaParams
            {
                Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
                Scale = scale, EscapeR2 = escapeRadius2, OffX0 = offsetX0, OffY0 = offsetY0,
                SaTol = saTolerance, SafeMax = safeMax, RowBase = 0,
                BlaLevels = blaLevels,
            };

            _ctx.CSSetShader(_csPerturbSa);
            _ctx.CSSetShaderResource(0, _refZrSrv);   // t0
            _ctx.CSSetShaderResource(1, _refZiSrv);   // t1
            for (int i = 0; i < 8; i++) _ctx.CSSetShaderResource((uint)(2 + i), _saSrvs[i]);   // t2..t9
            _ctx.CSSetShaderResource(10, _blaSrv);                                              // t10 (BLA)
            DispatchPerturbBands(width, height, maxIter, srvCount: 11, blaLevels > 0 ? " (SA+BLA)" : " (SA)", rowBase =>
            {
                p.RowBase = rowBase;
                var mapped = _ctx.Map(_saParamsBuf!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
                unsafe { *(PerturbSaParams*)mapped.DataPointer = p; }
                _ctx.Unmap(_saParamsBuf!, 0);
                _ctx.CSSetConstantBuffer(0, _saParamsBuf);
            });
            ReadPerturbOutputs(width * height, t0, iterDst, smoothDst, finalZrDst, finalZiDst, finalDrDst, finalDiDst);
        }
    }

    /// <summary>TDR row-band tiling for the perturbation kernels (the shader + its
    /// SRVs are bound by the caller; <paramref name="writeParams"/> uploads the
    /// band's cbuffer). A deep-zoom full-image dispatch runs long enough on a
    /// weak-FP64 GPU to trip the OS watchdog (DXGI_ERROR_DEVICE_REMOVED), which also
    /// kills the shared present device, so each band is Flushed as its own packet.
    /// Band 0 is synced and timed: if the extrapolated frame is too slow (weak FP64)
    /// it throws the too-slow marker so the caller drops to the CPU deep path.</summary>
    private void DispatchPerturbBands(int width, int height, int maxIter, int srvCount, string label,
        Action<int> writeParams, int uavCount = 3)
    {
        _ctx.CSSetUnorderedAccessView(0, _iterUav);
        _ctx.CSSetUnorderedAccessView(1, _smoothUav);
        _ctx.CSSetUnorderedAccessView(2, _finalZDUav);

        void Unbind()
        {
            for (int i = 0; i < uavCount; i++) _ctx.CSUnsetUnorderedAccessView((uint)i);   // u3: the orbit variant (#607)
            for (int i = 0; i < srvCount; i++) _ctx.CSSetShaderResource((uint)i, null);
        }

        int bandRows = MandelbrotKernelSource.PerturbBandRows(width, height, maxIter);
        int bandCount = (height + bandRows - 1) / bandRows;
        int bandIndex = 0;
        for (int rowBase = 0; rowBase < height; rowBase += bandRows, bandIndex++)
        {
            int rows = Math.Min(bandRows, height - rowBase);
            writeParams(rowBase);

            long tBand = System.Diagnostics.Stopwatch.GetTimestamp();
            _ctx.Dispatch((uint)((width + 7) / 8), (uint)((rows + 7) / 8), 1);

            // Perf-fallback: a CopyResource + Map(Read) on the iter staging blocks
            // until band 0's dispatch finishes -> a real GPU time; the remaining
            // bands just Flush (async).
            if (bandIndex == 0 && bandCount > 1)
            {
                _ctx.CopyResource(_iterStaging, _iterBuf);
                var sync = _ctx.Map(_iterStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
                _ctx.Unmap(_iterStaging, 0);
                _ = sync;
                double band0Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - tBand) * 1000.0
                                 / System.Diagnostics.Stopwatch.Frequency;
                if (MandelbrotKernelSource.PerturbTooSlow(band0Ms, bandCount))
                {
                    Unbind();   // leave the context clean before bailing to the CPU path
                    throw new TimeoutException(
                        $"{MandelbrotKernelSource.PerturbTooSlowMarker}: band0={band0Ms:F1}ms × {bandCount} bands " +
                        $"> {MandelbrotKernelSource.PerturbBudgetMs:F0}ms budget{label}");
                }
            }
            else
            {
                _ctx.Flush();   // submit this band as its own GPU packet (resets the TDR clock)
            }
        }
        Unbind();
    }

    /// <summary>Copy the perturbation outputs (iter / smooth / finalZD) back to the
    /// caller's arrays and record the dispatch / readback timings.</summary>
    private void ReadPerturbOutputs(int n, long t0, int[] iterDst, float[] smoothDst,
        float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst)
    {
        {
            _ctx.CopyResource(_iterStaging, _iterBuf);
            _ctx.CopyResource(_smoothStaging, _smoothBuf);
            _ctx.CopyResource(_finalZDStaging, _finalZDBuf);

            long tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();

            var iterMap = _ctx.Map(_iterStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    uint* src = (uint*)iterMap.DataPointer;
                    fixed (int* dst = iterDst) { for (int i = 0; i < n; i++) dst[i] = (int)src[i]; }
                }
            }
            finally { _ctx.Unmap(_iterStaging, 0); }

            var smoothMap = _ctx.Map(_smoothStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    float* src = (float*)smoothMap.DataPointer;
                    fixed (float* dst = smoothDst) { for (int i = 0; i < n; i++) dst[i] = src[i]; }
                }
            }
            finally { _ctx.Unmap(_smoothStaging, 0); }

            var fzdMap = _ctx.Map(_finalZDStaging, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    float* src = (float*)fzdMap.DataPointer;
                    fixed (float* zr = finalZrDst)
                    fixed (float* zi = finalZiDst)
                    fixed (float* dr = finalDrDst)
                    fixed (float* di = finalDiDst)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int b = i * 4;
                            zr[i] = src[b + 0]; zi[i] = src[b + 1];
                            dr[i] = src[b + 2]; di[i] = src[b + 3];
                        }
                    }
                }
            }
            finally { _ctx.Unmap(_finalZDStaging, 0); }

            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
            LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
        }
    }

    // #88 / G4.5 — SA cbuffer (b0 of CSPerturbSA): 5 doubles then 10 ints = 80 bytes,
    // byte-for-byte the HLSL PerturbParams in BuildPerturbSA (and the Vulkan blob).
    [StructLayout(LayoutKind.Sequential)]
    private struct PerturbSaParams
    {
        public double Scale, EscapeR2, OffX0, OffY0, SaTol;
        public int Width, Height, MaxIter, RefLen, RowBase, SafeMax, BlaLevels, Pad1, Pad2, Pad3;   // BlaLevels: G4.5b (was Pad0)
    }

    // SA coefficient SRVs t2..t9: A, B, C, D (re, im each), length refLen + 1.
    private readonly ID3D11Buffer?[] _saBufs = new ID3D11Buffer?[8];
    private readonly ID3D11ShaderResourceView?[] _saSrvs = new ID3D11ShaderResourceView?[8];
    private int _saAllocLen;
    private ID3D11Buffer? _saParamsBuf;
    private ID3D11ComputeShader? _csPerturbSa;

    // #88 / G4.5b — the flattened BLA table (t10); a 5-double placeholder without BLA.
    private ID3D11Buffer? _blaBuf;
    private ID3D11ShaderResourceView? _blaSrv;
    private int _blaAllocLen;

    private void EnsureBlaBuffer(int doubles)
    {
        if (_blaBuf != null && _blaAllocLen >= doubles) return;
        _blaSrv?.Dispose();
        _blaBuf?.Dispose();
        _blaBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(doubles * sizeof(double)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(double),
        });
        _blaSrv = _device.CreateShaderResourceView(_blaBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)doubles },
        });
        _blaAllocLen = doubles;
    }

    private void EnsureSaBuffers(int coeffLen)
    {
        _saParamsBuf ??= _device.CreateBuffer(new BufferDescription(
            byteWidth: 80, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
        if (_saBufs[0] != null && _saAllocLen >= coeffLen) return;
        for (int i = 0; i < 8; i++) { _saSrvs[i]?.Dispose(); _saBufs[i]?.Dispose(); }
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(coeffLen * sizeof(double)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(double),
        };
        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)coeffLen },
        };
        for (int i = 0; i < 8; i++)
        {
            _saBufs[i] = _device.CreateBuffer(desc);
            _saSrvs[i] = _device.CreateShaderResourceView(_saBufs[i]!, srvDesc);
        }
        _saAllocLen = coeffLen;
    }

    private void EnsurePerturbParamsBuffer()
    {
        if (_perturbParamsBuf != null) return;
        var desc = new BufferDescription(
            byteWidth: 64,      // multiple of 16, matches PerturbParams cbuffer
            bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic,
            cpuAccessFlags: CpuAccessFlags.Write);
        _perturbParamsBuf = _device.CreateBuffer(desc);
    }

    private void EnsureRefOrbitBuffers(int refLen)
    {
        if (_refZrBuf != null && _refAllocLen >= refLen) return;
        _refZrSrv?.Dispose();
        _refZiSrv?.Dispose();
        _refZrBuf?.Dispose();
        _refZiBuf?.Dispose();

        var desc = new BufferDescription
        {
            ByteWidth = (uint)(refLen * sizeof(double)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(double),
        };
        _refZrBuf = _device.CreateBuffer(desc);
        _refZiBuf = _device.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)refLen },
        };
        _refZrSrv = _device.CreateShaderResourceView(_refZrBuf, srvDesc);
        _refZiSrv = _device.CreateShaderResourceView(_refZiBuf, srvDesc);
        _refAllocLen = refLen;
    }

    private void UploadDoubles(ID3D11Buffer buf, double[] src, int count)
    {
        var m = _ctx.Map(buf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            double* dst = (double*)m.DataPointer;
            fixed (double* s = src) { for (int i = 0; i < count; i++) dst[i] = s[i]; }
        }
        _ctx.Unmap(buf, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _refZrSrv?.Dispose(); } catch { }
        try { _refZiSrv?.Dispose(); } catch { }
        try { _refZrBuf?.Dispose(); } catch { }
        try { _refZiBuf?.Dispose(); } catch { }
        try { _perturbParamsBuf?.Dispose(); } catch { }
        try { _csPerturb?.Dispose(); } catch { }
        try { _csPerturbSa?.Dispose(); } catch { }
        foreach (var cs in _csPerturbOrbit.Values) { try { cs.Dispose(); } catch { } }   // #607
        try { _csBuddha?.Dispose(); } catch { }   // #838
        try { _buddhaParamsBuf?.Dispose(); } catch { }
        try { _buddhaHitsUav?.Dispose(); } catch { }
        try { _buddhaHitsBuf?.Dispose(); } catch { }
        try { _buddhaHitsStaging?.Dispose(); } catch { }
        try { _buddhaDone?.Dispose(); } catch { }
        _csPerturbOrbit.Clear();
        try { _orbitOutUav?.Dispose(); } catch { }
        try { _orbitOutBuf?.Dispose(); } catch { }
        try { _orbitOutStaging?.Dispose(); } catch { }
        try { _saParamsBuf?.Dispose(); } catch { }
        try { _blaSrv?.Dispose(); } catch { }
        try { _blaBuf?.Dispose(); } catch { }
        for (int i = 0; i < 8; i++)
        {
            try { _saSrvs[i]?.Dispose(); } catch { }
            try { _saBufs[i]?.Dispose(); } catch { }
        }
        try { _iterUav?.Dispose(); } catch { }
        try { _smoothUav?.Dispose(); } catch { }
        try { _finalZDUav?.Dispose(); } catch { }
        try { _colorUav?.Dispose(); } catch { }
        try { _iterBuf?.Dispose(); } catch { }
        try { _smoothBuf?.Dispose(); } catch { }
        try { _finalZDBuf?.Dispose(); } catch { }
        try { _colorBuf?.Dispose(); } catch { }
        try { _iterStaging?.Dispose(); } catch { }
        try { _smoothStaging?.Dispose(); } catch { }
        try { _finalZDStaging?.Dispose(); } catch { }
        try { _colorStaging?.Dispose(); } catch { }
        try { _trapUav?.Dispose(); } catch { }
        try { _trapBuf?.Dispose(); } catch { }
        try { _trapStaging?.Dispose(); } catch { }
        try { _perRowSrv?.Dispose(); } catch { }
        try { _perRowBuf?.Dispose(); } catch { }
        try { _paramsBuf?.Dispose(); } catch { }
        try { _csBase?.Dispose(); } catch { }
        foreach (var cs in _csByPaletteId.Values)
        {
            try { cs.Dispose(); } catch { }
        }
        _csByPaletteId.Clear();
    }
}
