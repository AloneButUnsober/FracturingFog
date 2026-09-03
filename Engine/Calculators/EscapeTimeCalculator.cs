// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

public sealed class EscapeTimeCalculator : Interefaces.IFractalCalculator, Interefaces.IHeightFieldSource, Interefaces.ISupportsHistogramEq
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

    // ── #253 / IDEA-3 cross-fractal domain warp ──────────────────────────────
    //
    // A pre-sample coordinate warp: each pixel's c is displaced by a smooth
    // interference field before the fractal iterates (FractalDomainWarp). It is
    // an artful, shallow-zoom effect — gated below MaxWarpZoom so the deep-zoom
    // Phoenix perturbation tier (Zoom ≥ 1e10) never warps — and it forces the
    // scalar cores (SIMD builds one cy per row, which a per-pixel warp breaks;
    // GPU is skipped for the same reason). Off / strength 0 → byte-identical.

    /// <summary>Upper zoom bound for the domain warp. Above this the warp is
    /// inactive (the field is defined in normalised view space and would just
    /// freeze into a constant displacement at extreme zoom anyway).</summary>
    public const double MaxWarpZoom = 1.0e6;

    private bool WarpActive =>
        FractalParameters.DomainWarpEnabled
        && FractalParameters.DomainWarpStrength != 0.0
        && Zoom <= MaxWarpZoom;

    private readonly struct WarpCtx
    {
        public readonly bool Active;
        public readonly double Strength, Frequency, HalfSpan;
        public WarpCtx(bool active, double strength, double frequency, double halfSpan)
        { Active = active; Strength = strength; Frequency = frequency; HalfSpan = halfSpan; }
    }

    private WarpCtx MakeWarp(double scale, int width, int height)
        => new(WarpActive,
               FractalParameters.DomainWarpStrength,
               FractalParameters.DomainWarpFrequency,
               0.5 * Math.Max(width, height) * scale);

    // ── Entry point ──────────────────────────────────────────────────────────

    public void Calculate(CancellationToken ct = default)
    {
        // #615 Phase 1 — render, then a single path-agnostic post-pass paints the
        // beyond-escape-radius surround (opt-in via IColorMap.OutOfBoundsColor).
        CalculateInternal(ct);
        ApplyOutOfBoundsSurround(ct);
    }

    // #615 — the escape radius² the GPU shader uses (radius 2). The GPU dispatch
    // (TryDispatchGpu) hardcodes this, independent of the kernel's CPU
    // BailoutRadius2; the out-of-bounds post-pass reads _lastBailout2 to match.
    private const double GpuBailout2 = 4.0;

    // #615 — escape radius² of the most recent render (GpuBailout2 when the GPU
    // path ran, else the kernel's BailoutRadius2). Consumed by the out-of-bounds
    // post-pass so the surround disk is sized for the frame actually produced.
    private double _lastBailout2 = 512.0 * 512.0;

    // #615 Phase 1 — bailout radius² for the active fractal type, mirroring the
    // kernel construction in CalculateInternal. Used only by the out-of-bounds
    // post-pass; cheap to instantiate.
    private double CurrentEscapeRadius2()
    {
        FracturingFog.Interefaces.IFractalKernel k = FractalType switch
        {
            FractalType.Mandelbrot  => new MandelbrotKernel(),
            FractalType.Julia       => new JuliaKernel(FractalParameters.JuliaC.Real, FractalParameters.JuliaC.Imaginary),
            FractalType.BurningShip => new BurningShipKernel(),
            FractalType.Tricorn     => new TricornKernel(),
            FractalType.Multibrot   => new MultibrotKernel(FractalParameters.MultibrotExponent),
            FractalType.Phoenix     => new PhoenixKernel(FractalParameters.PhoenixP.Real, FractalParameters.PhoenixP.Imaginary),
            FractalType.Magnet1     => new MagnetOneKernel(),
            FractalType.Magnet2     => new MagnetTwoKernel(),
            FractalType.Glynn       => new GlynnKernel(FractalParameters.GlynnC.Real, FractalParameters.GlynnC.Imaginary),
            FractalType.Spider      => new SpiderKernel(FractalParameters.SpiderCDecay),
            _                       => new MandelbrotKernel(),
        };
        return k.BailoutRadius2;
    }

    // #615 Phase 1 — paint the flat out-of-bounds surround for the escape-time
    // families. The pixel's varying plane coordinate is the escape variable
    // (c for Mandelbrot-like, z0 for Julia), so |coord| ≥ escapeRadius is the
    // visible disk boundary either way. null OutOfBoundsColor ⇒ no-op (byte-
    // identical). No view rotation here, so the simple mapping matches the
    // render (an active domain warp distorts the edge slightly — acceptable).
    private void ApplyOutOfBoundsSurround(CancellationToken ct)
    {
        if (ColorMap.OutOfBoundsColor is not uint oob) return;
        int w = Width, h = Height;
        if (w <= 0 || h <= 0 || ColorBuffer.Length < w * h) return;
        bool haveNormals = NormalXBuffer.Length >= w * h && NormalYBuffer.Length >= w * h;

        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double r2 = _lastBailout2;   // #615 — radius the frame actually rendered with (GPU vs CPU)
        double cx0 = CenterX, cy0 = CenterY;

        _po.CancellationToken = ct;
        ParallelForRows(0, h, _po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = cy0 + (y - h * 0.5) * scale;
            int rb = y * w;
            for (int x = 0; x < w; x++)
            {
                double cx = cx0 + (x - w * 0.5) * scale;
                if (cx * cx + cy * cy >= r2)
                {
                    ColorBuffer[rb + x] = oob;
                    if (haveNormals)
                    {
                        NormalXBuffer[rb + x] = 0f;
                        NormalYBuffer[rb + x] = 0f;
                    }
                }
            }
        });
    }

    private void CalculateInternal(CancellationToken ct)
    {
        ColorMap.MaxIterations = MaxIterations;
        LastPixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;
        // Push the per-frame pixel span to distance-estimation themes so they
        // normalise by *this* fractal's scale, not a stale MandelbrotCalculator
        // static (matches MandelbrotCalculator.Calculate).
        if (ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = LastPixelScale;

        // T3.1 phase 3: GPU dispatch for the SIMD-capable kinds. Skipped
        // when the kernel isn't attached, when the toggle is off, when
        // zoom exceeds MaxGpuZoom (FP32 precision band), or when the
        // active fractal type isn't shader-supported (Multibrot needs
        // pow, Phoenix has prev-z carry — both stay CPU).
        // Domain warp (#253) needs the scalar cores (per-pixel c) — skip GPU
        // and route the SIMD kinds through the scalar dispatch when it's active.
        bool warp = WarpActive;

        if (UseGpuCompute && GpuKernel != null
            && Zoom <= MandelbrotCalculator.MaxGpuZoom
            && !warp
            && TryDispatchGpu(ct))
        {
            // #615 — the GPU shader escapes at |z|² ≥ GpuBailout2 (radius 2),
            // NOT the kernel's CPU BailoutRadius2 (512²). The out-of-bounds
            // post-pass must use the radius the frame actually rendered with,
            // or the surround disk would be sized for the wrong bailout.
            _lastBailout2 = GpuBailout2;
            return;
        }

        // #615 — CPU path escapes at the kernel's own bailout; record it so the
        // out-of-bounds post-pass paints the disk at the matching radius.
        _lastBailout2 = CurrentEscapeRadius2();

        switch (FractalType)
        {
            // SIMD-capable kernels (pure polynomial in zr/zi). Forced scalar
            // under an active domain warp.
            case FractalType.Mandelbrot:
                if (warp) DispatchByColorMap(new MandelbrotKernel(), ct);
                else      DispatchByColorMapSimd(new MandelbrotKernel(), ct);
                break;
            case FractalType.Julia:
            {
                var jk = new JuliaKernel(FractalParameters.JuliaC.Real, FractalParameters.JuliaC.Imaginary);
                if (warp) DispatchByColorMap(jk, ct);
                else      DispatchByColorMapSimd(jk, ct);
                break;
            }
            case FractalType.BurningShip:
                if (warp) DispatchByColorMap(new BurningShipKernel(), ct);
                else      DispatchByColorMapSimd(new BurningShipKernel(), ct);
                break;
            case FractalType.Tricorn:
                if (warp) DispatchByColorMap(new TricornKernel(), ct);
                else      DispatchByColorMapSimd(new TricornKernel(), ct);
                break;
            // Multibrot d ∈ {3,4,5}: SIMD via direct complex multiplication.
            // d ≥ 6: polar fallback (atan2 + pow + cos + sin), scalar.
            case FractalType.Multibrot:
            {
                var mk = new MultibrotKernel(FractalParameters.MultibrotExponent);
                if (mk.SimdSupported && !warp)
                    DispatchByColorMapSimd(mk, ct);
                else
                    DispatchByColorMap(mk, ct);
                break;
            }
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
                MaxIterations, GpuBailout2,
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
                // F11a: seed the ordered-dither offset for this pixel.
                if (GradientColorMap.DitherEnabled) GradientColorMap.SetDitherForPixel(x, y);
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
        var warp = MakeWarp(scale, width, height);

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cyRow = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double ox = (x - width * 0.5) * scale;
                double cx = centerX + ox;
                double cy = cyRow;
                if (warp.Active)
                {
                    double oy = (y - height * 0.5) * scale;
                    FractalDomainWarp.Apply(ref ox, ref oy, warp.HalfSpan, warp.Strength, warp.Frequency);
                    cx = centerX + ox;
                    cy = centerY + oy;
                }
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

    /// <summary>Phoenix-specific deep-zoom perturbation threshold.
    /// EscapeTimeCalculator is otherwise SP-only; Phoenix is the
    /// exception because D-3.16 ships a scalar perturbation tier here.
    /// Below this zoom the plain SP path runs (cheaper, no ref-orbit
    /// build). Above it, a double-precision reference orbit is computed
    /// once and per-pixel δ + δ_prev recurrence runs against it.
    /// 1e10 is conservative — pixel offsets ε ~ scale ~ 3.5e-13 / 1e10
    /// have ~3 decimal digits of headroom in double, plenty for the
    /// linear δ-step to be pixel-distinct.</summary>
    private const double PhoenixPerturbZoomThreshold = 1.0e10;

    private void CalculatePhoenix<TMap>(PhoenixKernel kernel, TMap colorMap, CancellationToken ct = default)
        where TMap : IColorMap
    {
        if (Zoom >= PhoenixPerturbZoomThreshold)
        {
            CalculatePhoenixPerturb(kernel, colorMap, ct);
            return;
        }

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter;
        bool useTileCap = perRow != null && perRow.Length >= Height;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;
        var warp = MakeWarp(scale, width, height);

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cyRow = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double ox = (x - width * 0.5) * scale;
                double cx = centerX + ox;
                double cy = cyRow;
                if (warp.Active)
                {
                    double oy = (y - height * 0.5) * scale;
                    FractalDomainWarp.Apply(ref ox, ref oy, warp.HalfSpan, warp.Strength, warp.Frequency);
                    cx = centerX + ox;
                    cy = centerY + oy;
                }
                int idx = rowBase + x;

                double zr = 0, zi = 0, prevZr = 0, prevZi = 0;
                // D-3.16 — Phoenix proper DE. dz/dc + dprev/dc carried alongside
                // (z, prev_z). Recurrence: D_{n+1} = 2·z·D + 1 + p·Dp.
                double dr = 0, di = 0, dprev_r = 0, dprev_i = 0;
                int iter;
                for (iter = 0; iter < rowMaxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.StepWithPrevDeriv(
                        ref zr, ref zi, ref prevZr, ref prevZi,
                        ref dr, ref di, ref dprev_r, ref dprev_i,
                        cx, cy);
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

    private void CalculatePhoenix(PhoenixKernel kernel, CancellationToken ct)
        => CalculatePhoenix(kernel, ColorMap, ct);

    // ── Phoenix perturbation tier (D-3.16, scalar) ──
    //
    // Reference orbit at view centre (plain double precision). Per-pixel
    // δ + δ_prev recurrence built from the symbolic expansion:
    //
    //   z = Z + δ,  c = C + ε,  p constant.
    //   z_{n+1} = z² + c + p·z_{n-1}
    //   δ_{n+1} = (Z+δ)² + (C+ε) + p·(Zp + δp) − Z² − C − p·Zp
    //           = 2·Z·δ + δ² + ε + p · δ_prev
    //   δ_prev_new ← δ_old
    //
    // Glitch fallback: when |Z+δ|² exceeds bailout we stop. When the
    // reference orbit escapes at iter k, every per-pixel iter past k
    // falls back to direct iteration (rare for non-trivial views).
    //
    // Derivative: tracked per-pixel via StepWithPrevDeriv on the
    // reconstructed (z, prev_z) = (Z+δ, Zp+δp) state — works at SP-only
    // depth (≲1e15) without DD/QD chains.
    private void CalculatePhoenixPerturb<TMap>(PhoenixKernel kernel, TMap colorMap, CancellationToken ct = default)
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
        double pR = kernel.PR;
        double pI = kernel.PI;

        // Build reference orbit at frame centre. Two arrays for Z, two more
        // for Zprev to feed the p·Zprev term. We carry refZr/refZi only
        // and read refZr[n-1] for Zprev (n≥1; iter 0 prev = 0).
        double[] refZr = new double[maxIt + 1];
        double[] refZi = new double[maxIt + 1];
        int refLen = maxIt;
        {
            double Zr = 0, Zi = 0, Pr = 0, Pi = 0;
            for (int n = 0; n < maxIt; n++)
            {
                refZr[n] = Zr;
                refZi[n] = Zi;
                if (Zr * Zr + Zi * Zi >= bailout2) { refLen = n; break; }
                double pPR = pR * Pr - pI * Pi;
                double pPI = pR * Pi + pI * Pr;
                double newZr = Zr * Zr - Zi * Zi + centerX + pPR;
                double newZi = 2.0 * Zr * Zi + centerY + pPI;
                Pr = Zr; Pi = Zi;
                Zr = newZr; Zi = newZi;
            }
            if (refLen == maxIt)
            {
                refZr[maxIt] = Zr;
                refZi[maxIt] = Zi;
            }
        }

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double epsI = (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double epsR = (x - width * 0.5) * scale;
                int idx = rowBase + x;

                // δ, δ_prev start at 0; per-pixel ε = pixel offset in c-space.
                double dR = 0, dI = 0, dPrevR = 0, dPrevI = 0;
                // True (z, prev_z) reconstructed each step as (Z + δ, Zp + δp);
                // carry derivative against true state for DE coloring.
                double dr = 0, di = 0, dprev_r = 0, dprev_i = 0;
                int iter;
                int cap = Math.Min(rowMaxIt, refLen);
                for (iter = 0; iter < cap; iter++)
                {
                    double Zr = refZr[iter];
                    double Zi = refZi[iter];
                    double zr = Zr + dR;
                    double zi = Zi + dI;
                    if (zr * zr + zi * zi >= bailout2) break;

                    // Derivative recurrence on true z. Z_prev = refZr[iter-1] + dPrevR
                    // (for iter=0: Z_prev=0, δ_prev=0).
                    double prevZr = iter == 0 ? 0.0 : refZr[iter - 1] + dPrevR;
                    double prevZi = iter == 0 ? 0.0 : refZi[iter - 1] + dPrevI;
                    kernel.StepWithPrevDeriv(
                        ref zr, ref zi, ref prevZr, ref prevZi,
                        ref dr, ref di, ref dprev_r, ref dprev_i,
                        centerX + epsR, centerY + epsI);
                    // StepWithPrevDeriv now holds z_{n+1} = (Z+δ)_{n+1}.
                    // Reconstruct next δ from next true z and next ref:
                    double nextZr = iter + 1 <= refLen ? refZr[iter + 1] : zr;
                    double nextZi = iter + 1 <= refLen ? refZi[iter + 1] : zi;
                    // Rotate δ_prev ← δ (after using current δ to step).
                    dPrevR = dR;
                    dPrevI = dI;
                    dR = zr - nextZr;
                    dI = zi - nextZi;
                }

                // Past-ref-orbit-end fallback: iterate true z directly.
                if (iter == cap && cap < rowMaxIt)
                {
                    double zr = refLen <= maxIt ? refZr[refLen] + dR : dR;
                    double zi = refLen <= maxIt ? refZi[refLen] + dI : dI;
                    double prevZr = refLen >= 1 ? refZr[refLen - 1] + dPrevR : dPrevR;
                    double prevZi = refLen >= 1 ? refZi[refLen - 1] + dPrevI : dPrevI;
                    for (; iter < rowMaxIt; iter++)
                    {
                        if (zr * zr + zi * zi >= bailout2) break;
                        kernel.StepWithPrevDeriv(
                            ref zr, ref zi, ref prevZr, ref prevZi,
                            ref dr, ref di, ref dprev_r, ref dprev_i,
                            centerX + epsR, centerY + epsI);
                    }
                    IterationBuffer[idx] = iter;
                    FillAuxAndColor(idx, iter, maxIt, zr, zi, dr, di, colorMap);
                }
                else
                {
                    double finalZr = iter <= refLen ? refZr[iter] + dR : dR;
                    double finalZi = iter <= refLen ? refZi[iter] + dI : dI;
                    IterationBuffer[idx] = iter;
                    FillAuxAndColor(idx, iter, maxIt, finalZr, finalZi, dr, di, colorMap);
                }
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
        var warp = MakeWarp(scale, width, height);

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowMaxIt = useTileCap ? perRow![y] : maxIt;
            if (rowMaxIt <= 0) rowMaxIt = maxIt;
            double cyRow = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double ox = (x - width * 0.5) * scale;
                double cx0 = centerX + ox;
                double cy0 = cyRow;
                if (warp.Active)
                {
                    double oy = (y - height * 0.5) * scale;
                    FractalDomainWarp.Apply(ref ox, ref oy, warp.HalfSpan, warp.Strength, warp.Frequency);
                    cx0 = centerX + ox;
                    cy0 = centerY + oy;
                }
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
        // F11a: seed the ordered-dither offset for this pixel (no-op when off).
        // Callers pass a linear idx; recover x/y at the render stride (== Width).
        if (GradientColorMap.DitherEnabled)
        {
            int dy = idx / Width;
            GradientColorMap.SetDitherForPixel(idx - dy * Width, dy);
        }
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

    // ── Histogram equalization (#145 — shared core in HistogramEqualizer) ────
    //
    // Same rank-order equalization as MandelbrotCalculator: this family carries
    // the identical IterationBuffer / SmoothBuffer / aux-buffer set, so it
    // delegates to the shared HistogramEqualizer. Covers Julia, BurningShip,
    // Tricorn, Multibrot, Phoenix, Magnet1/2, Glynn, Spider.

    private EscapeTimeColorState ColorState() => new(
        Width, Height, MaxIterations, LastPixelScale, ColorMap,
        IterationBuffer, SmoothBuffer, DistanceBuffer, NormalXBuffer, NormalYBuffer,
        FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer, ColorBuffer, _po);

    /// <inheritdoc/>
    public bool BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter)
    {
        sourceMaxIter = MaxIterations;
        return HistogramEqualizer.BuildCdf(
            Width, Height, MaxIterations, IterationBuffer, SmoothBuffer, out cdf, out bins);
    }

    /// <inheritdoc/>
    public void ApplyHistogramEqualization(double strength)
    {
        // No escaped pixels → leave the Calculate-coloured buffer untouched
        // (unlike MandelbrotCalculator there is no interior-alpha recolor to
        // re-run here).
        if (!BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter))
            return;
        ApplyHistogramEqualizationWithCdf(cdf!, bins, sourceMaxIter, strength);
    }

    /// <inheritdoc/>
    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, 0.0, out _, out _);

    /// <inheritdoc/>
    public void ApplyHistogramEqualizationWithCdf(
        double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength,
        out long escapedCount, out long saturatedCount)
    {
        var st = ColorState();
        HistogramEqualizer.ApplyWithCdf(
            in st, cdf, bins, sourceMaxIter, strength, ditherIterStrength,
            out escapedCount, out saturatedCount);
    }
}
