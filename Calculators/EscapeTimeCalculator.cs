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

    // ── Constructor / resize ─────────────────────────────────────────────────

    public EscapeTimeCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");
        Width = width;
        Height = height;
        int n = width * height;
        IterationBuffer = new int[n];
        SmoothBuffer = new float[n];
        DistanceBuffer = new float[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
        ColorBuffer = new uint[n];
        FinalZrBuffer = new float[n];
        FinalZiBuffer = new float[n];
        FinalDrBuffer = new float[n];
        FinalDiBuffer = new float[n];
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = MaxIterations;
        LastPixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;

        switch (FractalType)
        {
            case FractalType.Mandelbrot:
                DispatchByColorMap(new MandelbrotKernel(), ct);
                break;
            case FractalType.Julia:
                DispatchByColorMap(new JuliaKernel(FractalParameters.JuliaC.Real, FractalParameters.JuliaC.Imaginary), ct);
                break;
            case FractalType.BurningShip:
                DispatchByColorMap(new BurningShipKernel(), ct);
                break;
            case FractalType.Tricorn:
                DispatchByColorMap(new TricornKernel(), ct);
                break;
            case FractalType.Multibrot:
                DispatchByColorMap(new MultibrotKernel(FractalParameters.MultibrotExponent), ct);
                break;
            case FractalType.Phoenix:
                CalculatePhoenix(new PhoenixKernel(FractalParameters.PhoenixP.Real, FractalParameters.PhoenixP.Imaginary), ct);
                break;
            default:
                throw new NotSupportedException($"EscapeTimeCalculator does not handle {FractalType}");
        }
    }

    // ── Color-map dispatch (devirtualize Map() via concrete generic) ────────
    //
    // The kernels here don't enumerate every IColorMap subclass like
    // MandelbrotCalculator does; that catalogue is brittle and would have to
    // mirror MandelbrotCalculator's switch. For Phase 1 the calculator goes
    // through one generic specialized on ColorMap's runtime type — virtual
    // dispatch on Map() costs a vtable lookup per pixel but is acceptable for
    // the SP path at the resolutions the new fractals will be used at.

    private void DispatchByColorMap<TKernel>(TKernel kernel, CancellationToken ct)
        where TKernel : struct, IFractalKernel
    {
        CalculateCore<TKernel, IColorMap>(kernel, ColorMap, ct);
    }

    // ── Generic core (Mandelbrot-family escape-time) ────────────────────────

    private void CalculateCore<TKernel, TMap>(TKernel kernel, TMap colorMap, CancellationToken ct)
        where TKernel : struct, IFractalKernel
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;
        bool hasCardioidSkip = kernel.HasCardioidSkip;

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
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
                for (iter = 0; iter < maxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.Step(ref zr, ref zi, ref dr, ref di, cx, cy);
                }
                IterationBuffer[idx] = iter;
                FillAuxAndColor(idx, iter, maxIt, zr, zi, dr, di, colorMap);
            }
        });
    }

    // ── Phoenix (separate path — two-step memory, no IFractalKernel.Step) ──

    private void CalculatePhoenix<TMap>(PhoenixKernel kernel, TMap colorMap, CancellationToken ct = default)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        double bailout2 = kernel.BailoutRadius2;

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                int idx = rowBase + x;

                double zr = 0, zi = 0, prevZr = 0, prevZi = 0;
                int iter;
                for (iter = 0; iter < maxIt; iter++)
                {
                    if (zr * zr + zi * zi >= bailout2) break;
                    kernel.StepWithPrev(ref zr, ref zi, ref prevZr, ref prevZi, cx, cy);
                }
                IterationBuffer[idx] = iter;
                // Phoenix doesn't carry a derivative; pass 1,0 as a stub.
                FillAuxAndColor(idx, iter, maxIt, zr, zi, 1, 0, colorMap);
            }
        });
    }

    private void CalculatePhoenix(PhoenixKernel kernel, CancellationToken ct)
        => CalculatePhoenix(kernel, ColorMap, ct);

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
