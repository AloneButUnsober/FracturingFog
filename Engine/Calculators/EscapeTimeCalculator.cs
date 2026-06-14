// EscapeTimeCalculator.cs
//
// Generic escape-time fractal calculator. Routes non-Mandelbrot fractal types
// (Julia, Burning Ship, Tricorn, Multibrot, Phoenix) through a struct-generic
// kernel dispatch so the inner loop is JIT-specialized per fractal.
//
// SP only — no DD/QD/PT/SA/BLA. Zoom cap ≈ 1e15.
// Surface intentionally mirrors MandelbrotCalculator so MainForm can route to
// either engine based on FractalType without major refactor.

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Models.FractalKernels;

namespace FracturingFog;

public sealed class EscapeTimeCalculator : Interefaces.IFractalCalculator
{
    public bool SupportsZoomPan => true;

    // ── Public state (mirrors MandelbrotCalculator) ──────────────────────────

    public int Width { get; private set; }
    public int Height { get; private set; }

    public double CenterX { get; set; } = 0.0;
    public double CenterXLo { get; set; } = 0.0;
    public double CenterX2 { get; set; } = 0.0;
    public double CenterX3 { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double CenterYLo { get; set; } = 0.0;
    public double CenterY2 { get; set; } = 0.0;
    public double CenterY3 { get; set; } = 0.0;

    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 512;

    /// <summary>T3.1 phase 3: GPU compute toggle for the SIMD escape-time
    /// kernels (Mandelbrot, Julia, BurningShip, Tricorn). When true and a
    /// kernel is attached, <see cref="CalculateCoreSimd"/> dispatches to
    /// the shared <see cref="MandelbrotGpuKernel"/> via the FractalKind
    /// switch. Set by the host alongside MandelbrotCalculator.UseGpuCompute.</summary>
    public bool UseGpuCompute { get; set; }

    /// <summary>T3.1 phase 3: shared GPU kernel. Same instance used by
    /// the Mandelbrot path (set by the host).</summary>
    public FracturingFog.Rendering.IGpuKernel? GpuKernel { get; set; }

    /// <summary>Phase 2.1 per-row maxIter cap. See
    /// <see cref="MandelbrotCalculator.PerRowMaxIter"/> for the policy.
    /// Honoured by the SIMD + scalar core paths; bulb-skip / in-set
    /// auxiliary paths fall back to <see cref="MaxIterations"/>.</summary>
    public int[]? PerRowMaxIter { get; set; }
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;

    /// <summary>Always false — this engine is SP only.</summary>
    public bool IsHighPrecisionActive => false;

    public bool DisableAcceleration { get; set; } = false;
    public bool DisableSeriesApproximation { get; set; } = false;

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public FractalType FractalType { get; set; } = FractalType.Mandelbrot;
    public FractalParameters FractalParameters { get; set; } = new();

    // ── Output buffers ───────────────────────────────────────────────────────

    public int[] IterationBuffer { get; private set; } = Array.Empty<int>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDiBuffer { get; private set; } = Array.Empty<float>();

    public static double LastPixelScale { get; private set; } = 1.0;

    // Cached ParallelOptions — see MandelbrotCalculator._po notes.
    private readonly ParallelOptions _po = new();

    // T2.5: chunked row partitioner. Single dispatch per worker chunk
    // instead of one per row — see MandelbrotCalculator.ParallelForRows.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RowChunk(int count)
    {
        int chunk = count / (Environment.ProcessorCount * 4);
        return chunk < 1 ? 1 : chunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParallelForRows(int from, int to, ParallelOptions po, Action<int> body)
    {
        int count = to - from;
        if (count <= 0) return;
        Parallel.ForEach(Partitioner.Create(from, to, RowChunk(count)), po, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
                body(y);
        });
    }

    // ── Constructor / resize ─────────────────────────────────────────────────

    public EscapeTimeCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");
        Width = width;
        Height = height;
        int n = width * height;
        // Pinned LOH (see MandelbrotCalculator.Resize notes).
        IterationBuffer = GC.AllocateUninitializedArray<int>(n, pinned: true);
        SmoothBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        DistanceBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalXBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalYBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        ColorBuffer = GC.AllocateUninitializedArray<uint>(n, pinned: true);
        FinalZrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalZiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = MaxIterations;
        LastPixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;

        // T3.1 phase 3: GPU dispatch for the SIMD-capable kinds. Skipped
        // when the kernel isn't attached, when the toggle is off, when
        // zoom exceeds MaxGpuZoom (FP32 precision band), or when the
        // active fractal type isn't shader-supported (Multibrot needs
        // pow, Phoenix has prev-z carry — both stay CPU).
        if (UseGpuCompute && GpuKernel != null
            && Zoom <= MandelbrotCalculator.MaxGpuZoom
            && TryDispatchGpu(ct))
            return;

        switch (FractalType)
        {
            // SIMD-capable kernels (pure polynomial in zr/zi).
            case FractalType.Mandelbrot:
                DispatchByColorMapSimd(new MandelbrotKernel(), ct);
                break;
            case FractalType.Julia:
                DispatchByColorMapSimd(new JuliaKernel(FractalParameters.JuliaC.Real, FractalParameters.JuliaC.Imaginary), ct);
                break;
            case FractalType.BurningShip:
                DispatchByColorMapSimd(new BurningShipKernel(), ct);
                break;
            case FractalType.Tricorn:
                DispatchByColorMapSimd(new TricornKernel(), ct);
                break;
            // Transcendental / two-step memory — stay scalar.
            case FractalType.Multibrot:
                DispatchByColorMap(new MultibrotKernel(FractalParameters.MultibrotExponent), ct);
                break;
            case FractalType.Phoenix:
                CalculatePhoenix(new PhoenixKernel(FractalParameters.PhoenixP.Real, FractalParameters.PhoenixP.Imaginary), ct);
                break;
            case FractalType.Magnet1:
                DispatchByColorMap(new MagnetOneKernel(), ct);
                break;
            case FractalType.Magnet2:
                DispatchByColorMap(new MagnetTwoKernel(), ct);
                break;
            case FractalType.Glynn:
                DispatchByColorMap(new GlynnKernel(FractalParameters.GlynnC.Real, FractalParameters.GlynnC.Imaginary), ct);
                break;
            case FractalType.Spider:
                CalculateSpider(new SpiderKernel(FractalParameters.SpiderCDecay), ct);
                break;
            default:
                throw new NotSupportedException($"EscapeTimeCalculator does not handle {FractalType}");
        }
    }

    /// <summary>T3.1 phase 3 GPU dispatch. Returns true when the GPU
    /// kernel ran (CPU path can skip); false when the active fractal kind
    /// isn't shader-supported or when dispatch threw. On exception falls
    /// through with a Debug.WriteLine — the CPU SIMD path still produces
    /// a frame.</summary>
    private bool TryDispatchGpu(CancellationToken ct)
    {
        FracturingFog.Rendering.FractalKind kind;
        float p0 = 0f, p1 = 0f;
        switch (FractalType)
        {
            case FractalType.Mandelbrot:
                kind = FracturingFog.Rendering.FractalKind.Mandelbrot;
                break;
            case FractalType.Julia:
                kind = FracturingFog.Rendering.FractalKind.Julia;
                p0 = (float)FractalParameters.JuliaC.Real;
                p1 = (float)FractalParameters.JuliaC.Imaginary;
                break;
            case FractalType.BurningShip:
                kind = FracturingFog.Rendering.FractalKind.BurningShip;
                break;
            case FractalType.Tricorn:
                kind = FracturingFog.Rendering.FractalKind.Tricorn;
                break;
            default:
                return false;  // Multibrot / Phoenix etc. — CPU only.
        }

        bool gpuPalette;
        try
        {
            int[]? perRow = PerRowMaxIter;
            bool useTileCap = perRow != null && perRow.Length >= Height;
            double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
            // T3.1 phase 4 — share the GPU palette path with
            // MandelbrotCalculator. SetPalette caches per-PaletteId, so
            // switching back-and-forth between Mandelbrot + Julia themes
            // with the same colour map only compiles the HLSL once.
            var hlslPalette = ColorMap as FracturingFog.Interefaces.IGpuHlslPalette;
            if (hlslPalette != null) GpuKernel!.SetPalette(hlslPalette);
            else GpuKernel!.SetPalette(null);
            gpuPalette = hlslPalette != null && GpuKernel.HasGpuPalette;

            GpuKernel.Run(
                Width, Height,
                CenterX, CenterY, scale,
                MaxIterations, 4.0,
                IterationBuffer, SmoothBuffer,
                FinalZrBuffer, FinalZiBuffer,
                FinalDrBuffer, FinalDiBuffer,
                useTileCap ? perRow : null,
                kind, p0, p1,
                colorDst: gpuPalette ? ColorBuffer : null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EscapeTimeCalculator] GPU dispatch failed, falling back to CPU: {ex.Message}");
            return false;
        }

        if (gpuPalette)
        {
            // GPU emitted ColorBuffer end-to-end. Aux buffers stay at
            // whatever the previous frame left them — none of the
            // ColorGen-emitted themes consume them through the CPU
            // writeback in this path.
            return true;
        }

        // CPU writeback: aux + ColorBuffer from the GPU's iter + smooth +
        // final z+dz. Same shape as MandelbrotCalculator's GPU writeback.
        var colorMap = ColorMap;
        bool handlesInSet = colorMap is IColorMapHandlesInSet;
        uint inSetColor = colorMap.InSetColor;
        int maxIt = MaxIterations;
        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rb = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = rb + x;
                int iters = IterationBuffer[idx];
                if (iters < maxIt)
                {
                    float smooth = SmoothBuffer[idx];
                    float fzr = FinalZrBuffer[idx];
                    float fzi = FinalZiBuffer[idx];
                    float fdr = FinalDrBuffer[idx];
                    float fdi = FinalDiBuffer[idx];
                    double mag = Math.Sqrt(fzr * fzr + fzi * fzi);
                    double dMag = Math.Sqrt(fdr * fdr + fdi * fdi);
                    float dist = dMag > 1e-10
                        ? (float)(mag * Math.Log(mag) / dMag) : 0f;
                    DistanceBuffer[idx] = dist;
                    // Normal: rotate dz by 90° (perpendicular to escape
                    // direction) and normalize. Same shape as
                    // MandelbrotCalculator.FillNormal.
                    float u = fzr * fdr + fzi * fdi;
                    float v = fzi * fdr - fzr * fdi;
                    float m = u * u + v * v;
                    if (m > 1e-30f)
                    {
                        float invSqrt = 1.0f / MathF.Sqrt(m);
                        NormalXBuffer[idx] = u * invSqrt;
                        NormalYBuffer[idx] = v * invSqrt;
                    }
                    else
                    {
                        NormalXBuffer[idx] = 0f;
                        NormalYBuffer[idx] = 0f;
                    }
                    int iterArg = handlesInSet ? iters : maxIt;
                    ColorBuffer[idx] = (uint)colorMap.Map(
                        smooth, dist, iterArg,
                        NormalXBuffer[idx], NormalYBuffer[idx],
                        fzr, fzi, fdr, fdi);
                }
                else
                {
                    SmoothBuffer[idx] = 0f;
                    DistanceBuffer[idx] = 0f;
                    NormalXBuffer[idx] = 0f;
                    NormalYBuffer[idx] = 0f;
                    FinalZrBuffer[idx] = 0f;
                    FinalZiBuffer[idx] = 0f;
                    FinalDrBuffer[idx] = 0f;
                    FinalDiBuffer[idx] = 0f;
                    ColorBuffer[idx] = handlesInSet
                        ? (uint)colorMap.Map(0f, 0f, maxIt, 0f, 0f, 0f, 0f, 0f, 0f)
                        : inSetColor;
                }
            }
        });
        return true;
    }

    // ── SIMD dispatch (kernels implementing ISimdFractalKernel) ─────────────

    private void DispatchByColorMapSimd<TKernel>(TKernel kernel, CancellationToken ct)
        where TKernel : struct, ISimdFractalKernel
    {
        switch (ColorMap)
        {
            case HsvPalette m:        CalculateCoreSimd<TKernel, HsvPalette>(kernel, m, ct);        return;
            case GrayscalePalette m:  CalculateCoreSimd<TKernel, GrayscalePalette>(kernel, m, ct);  return;
            case RainbowColorMap m:   CalculateCoreSimd<TKernel, RainbowColorMap>(kernel, m, ct);   return;
            case FirePalette m:       CalculateCoreSimd<TKernel, FirePalette>(kernel, m, ct);       return;
            case Painted m:           CalculateCoreSimd<TKernel, Painted>(kernel, m, ct);           return;
            case PaintedReversed m:   CalculateCoreSimd<TKernel, PaintedReversed>(kernel, m, ct);   return;
            case Pastelly m:          CalculateCoreSimd<TKernel, Pastelly>(kernel, m, ct);          return;
            case WarpedHsvMap m:      CalculateCoreSimd<TKernel, WarpedHsvMap>(kernel, m, ct);      return;
            case GoldenRatioMap m:    CalculateCoreSimd<TKernel, GoldenRatioMap>(kernel, m, ct);    return;
            case MonoBandMap m:       CalculateCoreSimd<TKernel, MonoBandMap>(kernel, m, ct);       return;
            case BernsteinMap m:      CalculateCoreSimd<TKernel, BernsteinMap>(kernel, m, ct);      return;
            case RedAndBlack m:       CalculateCoreSimd<TKernel, RedAndBlack>(kernel, m, ct);       return;
            case NebulaDustMap m:     CalculateCoreSimd<TKernel, NebulaDustMap>(kernel, m, ct);     return;
            case DigitalMatrixMap m:  CalculateCoreSimd<TKernel, DigitalMatrixMap>(kernel, m, ct);  return;
            case PsychedelicMap m:    CalculateCoreSimd<TKernel, PsychedelicMap>(kernel, m, ct);    return;
            case TwilightCyclicMap m: CalculateCoreSimd<TKernel, TwilightCyclicMap>(kernel, m, ct); return;
            case SolarWindMap m:      CalculateCoreSimd<TKernel, SolarWindMap>(kernel, m, ct);      return;
            case SolarWindMapMOD m:   CalculateCoreSimd<TKernel, SolarWindMapMOD>(kernel, m, ct);   return;
            case CopperSheenMap m:    CalculateCoreSimd<TKernel, CopperSheenMap>(kernel, m, ct);    return;
            case VintageSepiaMap m:   CalculateCoreSimd<TKernel, VintageSepiaMap>(kernel, m, ct);   return;
            case DistanceGlowMap m:   CalculateCoreSimd<TKernel, DistanceGlowMap>(kernel, m, ct);   return;
            default:
                CalculateCoreSimd<TKernel, IColorMap>(kernel, ColorMap, ct);
                return;
        }
    }

    // ── SIMD inner loop ─────────────────────────────────────────────────────
    //
    // Vectorises the per-pixel iteration loop using Vector<double> (4 lanes
    // on AVX2, 8 on AVX-512). Per-lane derivative tracking + escape mask via
    // ConditionalSelect. FillAuxAndColor stays scalar per lane after exit —
    // the heavy cost is the iteration loop, which now runs at full SIMD
    // throughput.

    private void CalculateCoreSimd<TKernel, TMap>(TKernel kernel, TMap colorMap, CancellationToken ct)
        where TKernel : struct, ISimdFractalKernel
        where TMap : IColorMap
    {
        int vecLen = Vector<double>.Count;
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        // Phase 2.1: per-row cap snapshot. Same semantics as
        // MandelbrotCalculator — null or short array → fall back to global.
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;
        bool hasCardioidSkip = kernel.HasCardioidSkip;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;

            var bailoutV = new Vector<double>(bailout2);
            var oneV = Vector<double>.One;
            var zeroV = Vector<double>.Zero;
            var cyV = new Vector<double>(cy);
            Span<double> cxBuf = stackalloc double[vecLen];

            int x = 0;
            for (; x + vecLen <= width; x += vecLen)
            {
                // Build per-lane cx vector.
                for (int k = 0; k < vecLen; k++)
                    cxBuf[k] = centerX + ((x + k) - width * 0.5) * scale;
                var cxV = new Vector<double>(cxBuf);

                // Whole-block cardioid skip — fires often on shallow zooms.
                // Always writes maxIt (the global) so recolor's in-set gate
                // treats these pixels correctly regardless of per-row cap.
                if (hasCardioidSkip)
                {
                    int bulbBits = 0;
                    for (int k = 0; k < vecLen; k++)
                        if (kernel.IsInTrivialInSet(cxBuf[k], cy)) bulbBits |= 1 << k;
                    int allMask = (1 << vecLen) - 1;
                    if (bulbBits == allMask)
                    {
                        for (int k = 0; k < vecLen; k++)
                        {
                            int idx = rowBase + x + k;
                            IterationBuffer[idx] = maxIt;
                            FillAuxAndColor(idx, maxIt, maxIt, 0, 0, 1, 0, colorMap);
                        }
                        continue;
                    }
                }

                kernel.InitStateSimd(cxV, cyV,
                    out Vector<double> zr, out Vector<double> zi,
                    out Vector<double> dr, out Vector<double> di);
                var iterCountV = zeroV;

                for (int iter = 0; iter < rowMaxIt; iter++)
                {
                    var mag2 = zr * zr + zi * zi;
                    var notEscaped = Vector.LessThan(mag2, bailoutV);

                    iterCountV += Vector.ConditionalSelect(notEscaped, oneV, zeroV);

                    // Take a step on all lanes; freeze escaped lanes via select.
                    var prevZr = zr; var prevZi = zi;
                    var prevDr = dr; var prevDi = di;
                    kernel.StepSimd(ref zr, ref zi, ref dr, ref di, cxV, cyV);
                    zr = Vector.ConditionalSelect(notEscaped, zr, prevZr);
                    zi = Vector.ConditionalSelect(notEscaped, zi, prevZi);
                    dr = Vector.ConditionalSelect(notEscaped, dr, prevDr);
                    di = Vector.ConditionalSelect(notEscaped, di, prevDi);

                    if (!Vector.LessThanAny(mag2, bailoutV)) break;
                }

                // Write per-lane results scalarly.
                for (int k = 0; k < vecLen; k++)
                {
                    int idx = rowBase + x + k;
                    int iters = (int)iterCountV[k];
                    IterationBuffer[idx] = iters;
                    FillAuxAndColor(idx, iters, maxIt, zr[k], zi[k], dr[k], di[k], colorMap);
                }
            }

            // Scalar tail (when width is not a multiple of vecLen).
            for (; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                if (hasCardioidSkip && kernel.IsInTrivialInSet(cx, cy))
                {
                    IterationBuffer[idx] = maxIt;
                    FillAuxAndColor(idx, maxIt, maxIt, 0, 0, 1, 0, colorMap);
                    continue;
                }

                kernel.InitState(cx, cy, out double zrs, out double zis, out double drs, out double dis);

                int iter;
                for (iter = 0; iter < rowMaxIt; iter++)
                {
                    if (zrs * zrs + zis * zis >= bailout2) break;
                    kernel.Step(ref zrs, ref zis, ref drs, ref dis, cx, cy);
                }
                IterationBuffer[idx] = iter;
                FillAuxAndColor(idx, iter, maxIt, zrs, zis, drs, dis, colorMap);
            }

            // Phase 2.1 in-set rewrite (see MandelbrotCalculator).
            if (rowMaxIt < maxIt)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    if (IterationBuffer[rowBase + xx] >= rowMaxIt)
                        IterationBuffer[rowBase + xx] = maxIt;
                }
            }
        });
    }

    // ── Color-map dispatch (devirtualize Map() via concrete generic) ────────
    //
    // Mirrors the MandelbrotCalculator concrete-type switch. With a generic
    // constrained to the interface (TMap : IColorMap) the JIT cannot
    // devirtualize Map() — every pixel pays a vtable lookup. Switching on
    // the runtime type lets the JIT specialise CalculateCore<TKernel,
    // ConcreteMap> per palette so Map() inlines. Adds 1.5-2× on the SP
    // path for Julia / BurningShip / Tricorn / Multibrot / Phoenix.
    //
    // Catalogue covers the common 2D palettes selected with non-Mandelbrot
    // fractals. The default case falls back to the interface-generic path
    // for any theme not enumerated here — still correct, just not devirt.

    private void DispatchByColorMap<TKernel>(TKernel kernel, CancellationToken ct)
        where TKernel : struct, IFractalKernel
    {
        switch (ColorMap)
        {
            case HsvPalette m:        CalculateCore<TKernel, HsvPalette>(kernel, m, ct);        return;
            case GrayscalePalette m:  CalculateCore<TKernel, GrayscalePalette>(kernel, m, ct);  return;
            case RainbowColorMap m:   CalculateCore<TKernel, RainbowColorMap>(kernel, m, ct);   return;
            case FirePalette m:       CalculateCore<TKernel, FirePalette>(kernel, m, ct);       return;
            case Painted m:           CalculateCore<TKernel, Painted>(kernel, m, ct);           return;
            case PaintedReversed m:   CalculateCore<TKernel, PaintedReversed>(kernel, m, ct);   return;
            case Pastelly m:          CalculateCore<TKernel, Pastelly>(kernel, m, ct);          return;
            case WarpedHsvMap m:      CalculateCore<TKernel, WarpedHsvMap>(kernel, m, ct);      return;
            case GoldenRatioMap m:    CalculateCore<TKernel, GoldenRatioMap>(kernel, m, ct);    return;
            case MonoBandMap m:       CalculateCore<TKernel, MonoBandMap>(kernel, m, ct);       return;
            case BernsteinMap m:      CalculateCore<TKernel, BernsteinMap>(kernel, m, ct);      return;
            case RedAndBlack m:       CalculateCore<TKernel, RedAndBlack>(kernel, m, ct);       return;
            case NebulaDustMap m:     CalculateCore<TKernel, NebulaDustMap>(kernel, m, ct);     return;
            case DigitalMatrixMap m:  CalculateCore<TKernel, DigitalMatrixMap>(kernel, m, ct);  return;
            case PsychedelicMap m:    CalculateCore<TKernel, PsychedelicMap>(kernel, m, ct);    return;
            case TwilightCyclicMap m: CalculateCore<TKernel, TwilightCyclicMap>(kernel, m, ct); return;
            case SolarWindMap m:      CalculateCore<TKernel, SolarWindMap>(kernel, m, ct);      return;
            case SolarWindMapMOD m:   CalculateCore<TKernel, SolarWindMapMOD>(kernel, m, ct);   return;
            case CopperSheenMap m:    CalculateCore<TKernel, CopperSheenMap>(kernel, m, ct);    return;
            case VintageSepiaMap m:   CalculateCore<TKernel, VintageSepiaMap>(kernel, m, ct);   return;
            case DistanceGlowMap m:   CalculateCore<TKernel, DistanceGlowMap>(kernel, m, ct);   return;
            default:
                // Unknown / 3D / orbit-aware concrete type — fall back to
                // virtual dispatch. Correct, just not devirtualized.
                CalculateCore<TKernel, IColorMap>(kernel, ColorMap, ct);
                return;
        }
    }

    // ── Generic core (Mandelbrot-family escape-time) ────────────────────────

    private void CalculateCore<TKernel, TMap>(TKernel kernel, TMap colorMap, CancellationToken ct)
        where TKernel : struct, IFractalKernel
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;
        bool hasCardioidSkip = kernel.HasCardioidSkip;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                if (hasCardioidSkip && kernel.IsInTrivialInSet(cx, cy))
                {
                    IterationBuffer[idx] = maxIt;
                    FillAuxAndColor(idx, maxIt, maxIt, 0, 0, 1, 0, colorMap);
                    continue;
                }

                kernel.InitState(cx, cy, out double zr, out double zi, out double dr, out double di);

                int iter;
                for (iter = 0; iter < rowMaxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.Step(ref zr, ref zi, ref dr, ref di, cx, cy);
                }
                IterationBuffer[idx] = iter;
                FillAuxAndColor(idx, iter, maxIt, zr, zi, dr, di, colorMap);
            }
            // Phase 2.1 in-set rewrite.
            if (rowMaxIt < maxIt)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    if (IterationBuffer[rowBase + xx] >= rowMaxIt)
                        IterationBuffer[rowBase + xx] = maxIt;
                }
            }
        });
    }

    // ── Phoenix (separate path — two-step memory, no IFractalKernel.Step) ──

    private void CalculatePhoenix<TMap>(PhoenixKernel kernel, TMap colorMap, CancellationToken ct = default)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                double zr = 0, zi = 0, prevZr = 0, prevZi = 0;
                int iter;
                for (iter = 0; iter < rowMaxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.StepWithPrev(ref zr, ref zi, ref prevZr, ref prevZi, cx, cy);
                }
                IterationBuffer[idx] = iter;
                // Phoenix doesn't carry a derivative. Pass (0,0) so FillAuxAndColor's
                // dMag < 1e-10 branch zeros distance + normal — themes that scale by
                // distance (HSV value, WarpedHSV edge glow) would otherwise blacken
                // every escaped pixel because |dz/dc|=1 yields dist = mag·log(mag).
                FillAuxAndColor(idx, iter, maxIt, zr, zi, 0, 0, colorMap);
            }
            // Phase 2.1 in-set rewrite.
            if (rowMaxIt < maxIt)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    if (IterationBuffer[rowBase + xx] >= rowMaxIt)
                        IterationBuffer[rowBase + xx] = maxIt;
                }
            }
        });
    }

    private void CalculatePhoenix(PhoenixKernel kernel, CancellationToken ct)
        => CalculatePhoenix(kernel, ColorMap, ct);

    // ── Spider (separate path — c mutates per iteration) ────────────────────

    private void CalculateSpider<TMap>(SpiderKernel kernel, TMap colorMap, CancellationToken ct = default)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cy0 = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx0 = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                // Per-pixel c starts at the pixel coordinate and then drifts
                // each iteration via decay·c + z. Local copies are required
                // because the kernel's StepMutatingC writes back through
                // ref parameters.
                double zr = 0, zi = 0, cx = cx0, cy = cy0;
                int iter;
                for (iter = 0; iter < rowMaxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.StepMutatingC(ref zr, ref zi, ref cx, ref cy);
                }
                IterationBuffer[idx] = iter;
                // No closed-form dz/dc (c mutates) — same handling as Phoenix.
                FillAuxAndColor(idx, iter, maxIt, zr, zi, 0, 0, colorMap);
            }
            if (rowMaxIt < maxIt)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    if (IterationBuffer[rowBase + xx] >= rowMaxIt)
                        IterationBuffer[rowBase + xx] = maxIt;
                }
            }
        });
    }

    private void CalculateSpider(SpiderKernel kernel, CancellationToken ct)
        => CalculateSpider(kernel, ColorMap, ct);

    // ── Shared aux + color fill ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColor<TMap>(
        int idx, int iters, int maxIter,
        double zr, double zi, double dr, double di,
        TMap colorMap)
        where TMap : IColorMap
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            float smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            double dMag = Math.Sqrt(dr * dr + di * di);
            float dist = dMag > 1e-10
                ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            // Normal estimation — same formula as MandelbrotCalculator.FillNormal
            double u = zr * dr + zi * di;
            double v = zi * dr - zr * di;
            double m = Math.Sqrt(u * u + v * v);
            float nx, ny;
            if (m > 1e-10)
            {
                nx = (float)(u / m);
                ny = (float)(v / m);
            }
            else
            {
                nx = 0; ny = 0;
            }
            NormalXBuffer[idx] = nx;
            NormalYBuffer[idx] = ny;

            float fzr = (float)zr, fzi = (float)zi;
            float fdr = (float)dr, fdi = (float)di;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;

            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, dist, maxIter,
                nx, ny,
                fzr, fzi, fdr, fdi);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
        }
    }

    // ── Histogram equalization (stub for MainForm compatibility) ────────────

    public void ApplyHistogramEqualization(double strength)
    {
        // No-op for Phase 1. MainForm should gate histogram EQ to the
        // Mandelbrot engine until this path is implemented.
        _ = strength;
    }
}
