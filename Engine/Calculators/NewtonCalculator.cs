// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// NewtonCalculator.cs
//
// Renders Newton fractal for f(z) = z^d - 1. Iterates z := z - R·f(z)/f'(z)
// until convergence to a root. Color is basin (root index) hue blended with
// iteration count for shading.
//
// #1294 — also renders Nova: z := z - R·f(z)/f'(z) + c. In the parameter plane
// (default) c is the pixel and z starts at NovaZ0; in Julia mode z starts at
// the pixel and c is NovaJuliaC. With +c the orbit no longer lands on a root
// of z^d - 1, so Nova converges when the step size falls below the tolerance
// and its "basin" is the root sector (angle) the final z lies in. At c = 0 the
// sectors are exactly Newton's root basins.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
// INewtonColorMap lives in FracturingFog.Interefaces

namespace FracturingFog;

public sealed partial class NewtonCalculator : IFractalCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator, IHeightFieldSource, ISupportsHistogramEq
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // #139 — height field for Relief 3D. Newton has no escape potential, so the
    // relief height is the iteration count to convergence: fast-converging basin
    // interiors are low, the fractal boundaries (slow/non-converging) rise into
    // ridges. Fed to HeightfieldRelief2D / HeightfieldRaymarch2D via the host.
    // Also the HE scalar (#146) — per-pixel convergence step count.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    // #146 — per-pixel fields the basin histogram-equalizer needs to recolor:
    // basin index (root converged to; -1 = non-convergent interior) and the
    // final iterate position (some Newton themes hue by root angle/offset).
    public int[] BasinBuffer { get; private set; } = Array.Empty<int>();
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();

    // Polynomial degree d = number of basins, cached from the last Calculate so
    // the ISupportsHistogramEq recolor can pass totalBasins to MapNewton.
    private int _basins = 2;

    // #1294 — whether the last Calculate rendered Nova, so the HE recolour uses
    // the same colouring rule as the render that filled the buffers.
    private bool _renderedNova;

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 64;

    /// <summary>Global interior-alpha knob (#830): 0..255, copied from
    /// <c>FractalParameters.InteriorAlpha</c> by the render host. The Newton
    /// "interior" is basin non-convergence (basin &lt; 0); with no
    /// <c>IterationBuffer</c> to key a post-pass on, the in-set colour's alpha
    /// is scaled inline at the write site so it composites over
    /// Interior2DBackground. 255 = opaque (byte-identical).</summary>
    public int InteriorAlpha { get; set; } = 255;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    /// <summary>#1382 — adaptive anti-aliasing (Supersampling.AccumulateAlt): with a
    /// mask (Width × Height bytes) Calculate computes only the pixels whose byte is
    /// non-zero; the others keep their buffer contents. Each pixel is computed from its
    /// own coordinate alone, so a marked pixel gets exactly a full frame's bytes.</summary>
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    public bool MaskExact => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>#1294 — <see cref="FractalType.Nova"/> adds the +c term; every
    /// other value renders plain Newton. Set by the host / poster / video
    /// paths alongside <see cref="FractalParameters"/>.</summary>
    public FractalType FractalType { get; set; } = FractalType.Newton;

    /// <summary>#1979 — Brent's periodicity check (Brent 1980; pauldelbrot, fractalforums.org topic 5693):
    /// an orbit that repeats a snapshot of z bit for bit cycles for good, so the pixel stops there and
    /// colours exactly as at the cap. On by default (frames are byte-identical); off for tests.</summary>
    public bool Periodicity { get; set; } = true;

    /// <summary>#1979 / #1980 — what the early-outs did in the last frame.</summary>
    public readonly record struct EarlyOutStats(long Cycles, long ChaosStops, long StopIterSum, long IterationsSaved);

    /// <summary>#1979 / #1980 — the last frame's early-outs (double loop and engine).</summary>
    public EarlyOutStats LastEarlyOuts => new(_statCycles, _statChaos, _statStopIter, _statSaved);

    private long _statCycles, _statChaos, _statStopIter, _statSaved;

    // #1980 — the threshold on log2 |Π f′|² for a threshold of 10^log10 on |Π f′|.
    internal static double ChaosLog2Squared(double log10) => 2 * log10 * Math.Log2(10);

    public NewtonCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        SmoothBuffer = new float[n];
        BasinBuffer = new int[n];
        FinalZrBuffer = new float[n];
        FinalZiBuffer = new float[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        int d = Math.Clamp(FractalParameters.NewtonExponent, 2, 8);
        double R = FractalParameters.NewtonRelaxation;
        int maxIter = MaxIterations;
        if (maxIter < 8) maxIter = 64;
        _basins = d;   // #146 — cache for the HE recolor path
        _renderedNova = FractalType == FractalType.Nova;   // #1294

        // Roots of z^d = 1 are unit roots e^(2π·k/d).
        var rootsR = new double[d];
        var rootsI = new double[d];
        for (int k = 0; k < d; k++)
        {
            rootsR[k] = Math.Cos(2 * Math.PI * k / d);
            rootsI[k] = Math.Sin(2 * Math.PI * k / d);
        }

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int width = Width;
        int height = Height;
        double centerX = CenterX;
        double centerY = CenterY;
        const double eps2 = 1e-12;
        double logEps = 0.5 * Math.Log(eps2);   // #1294 — Nova step tolerance, log scale

        var newtonMap = ColorMap as INewtonColorMap;
        ColorMap.MaxIterations = maxIter;

        _statCycles = _statChaos = _statStopIter = _statSaved = 0;   // #1979 / #1980

        // #1959 — Nova past DeepZoomThreshold: the perturbation engine (NewtonCalculator.Deep.cs).
        if (FractalType == FractalType.Nova && TryCalculateDeep(d, R, maxIter, newtonMap, ct)) return;
        LastPrecisionLabel = "SP";

        bool nova = FractalType == FractalType.Nova;
        bool periodicity = Periodicity;                       // #1979
        bool chaosOut = FractalParameters.ChaosEarlyOut;      // #1980
        double chaosLog2Sq = ChaosLog2Squared(FractalParameters.ChaosEarlyOutLog10);
        bool novaJulia = nova && FractalParameters.NovaJuliaMode;
        double novaZ0R = FractalParameters.NovaZ0.Real, novaZ0I = FractalParameters.NovaZ0.Imaginary;
        double novaCR = FractalParameters.NovaJuliaC.Real, novaCI = FractalParameters.NovaJuliaC.Imaginary;

        byte[]? aaMask = PixelMask is { } aaPm && aaPm.Length == width * height ? aaPm : null;   // #1382
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                if (aaMask != null && aaMask[rowBase + x] == 0) continue;   // #1382
                double cx = centerX + (x - width * 0.5) * scale;
                double zr = cx, zi = cy;
                // #1294 — Nova's added constant (unused by Newton).
                double addR = 0, addI = 0;
                if (nova)
                {
                    if (novaJulia) { addR = novaCR; addI = novaCI; }
                    else { zr = novaZ0R; zi = novaZ0I; addR = cx; addI = cy; }
                }
                int iter;
                int basin = -1;
                float smooth = 0f;              // #1294 — Nova smooth convergence count
                double stepPrev = double.MaxValue;
                // #1979 — Brent: z snapshotted at doubling step counts, compared bit for bit.
                int cap = maxIter, lam = 0, pow = 1;
                double snapR = double.NaN, snapI = double.NaN;
                bool brent = periodicity;
                // #1980 — the orbit's multiplier |Π N′(z_n)|², as mantissa · 2^mulE.
                double mul = 1; long mulE = 0;
                bool chaos = false;
                for (iter = 0; iter < cap; iter++)
                {
                    // f(z) = z^d - 1, f'(z) = d·z^(d-1)
                    // Compute z^(d-1) via polar to avoid repeated multiplication.
                    double r2 = zr * zr + zi * zi;
                    if (r2 < 1e-30) { zr = 1e-10; zi = 0; r2 = 1e-20; }
                    double r = Math.Sqrt(r2);
                    double theta = Math.Atan2(zi, zr);
                    double rPowD = Math.Pow(r, d);
                    double rPowDm1 = rPowD / r;
                    double zdR = rPowD * Math.Cos(d * theta);
                    double zdI = rPowD * Math.Sin(d * theta);
                    double zdm1R = rPowDm1 * Math.Cos((d - 1) * theta);
                    double zdm1I = rPowDm1 * Math.Sin((d - 1) * theta);

                    // f = z^d - 1
                    double fR = zdR - 1.0;
                    double fI = zdI;
                    // f' = d · z^(d-1)
                    double fpR = d * zdm1R;
                    double fpI = d * zdm1I;
                    // f / f' = (fR + i fI) * conj(fp) / |fp|²
                    double denom = fpR * fpR + fpI * fpI;
                    if (denom < 1e-30) break;
                    double quotR = (fR * fpR + fI * fpI) / denom;
                    double quotI = (fI * fpR - fR * fpI) / denom;

                    if (nova)
                    {
                        // z := z - R · quot + c; converged once the step is tiny.
                        double nzr = zr - R * quotR + addR;
                        double nzi = zi - R * quotI + addI;
                        double sx = nzr - zr, sy = nzi - zi;
                        double step2 = sx * sx + sy * sy;
                        zr = nzr; zi = nzi;
                        if (step2 < eps2)
                        {
                            basin = RootSector(zr, zi, d);
                            // Smooth count: log-interpolate where the step size
                            // crossed the tolerance (as the Magnet path does).
                            smooth = iter;
                            if (iter > 0 && stepPrev < double.MaxValue)
                            {
                                double logPrev = 0.5 * Math.Log(stepPrev);
                                double logCur = 0.5 * Math.Log(Math.Max(step2, 1e-300));
                                double span = logPrev - logCur;
                                double frac = span > 1e-12 ? (logPrev - logEps) / span : 0.0;
                                smooth = (float)(iter - 1 + Math.Clamp(frac, 0.0, 1.0));
                            }
                            goto converged;
                        }
                        stepPrev = step2;
                    }
                    else
                    {
                        // z := z - R · quot
                        zr -= R * quotR;
                        zi -= R * quotI;

                        // Check convergence to any root.
                        for (int k = 0; k < d; k++)
                        {
                            double dx = zr - rootsR[k];
                            double dy = zi - rootsI[k];
                            if (dx * dx + dy * dy < eps2) { basin = k; goto converged;
                            }
                        }
                    }

                    if (chaosOut && iter > 0)
                    {
                        // #1980 — N′(z) = 1 − R·(1 + (d−1)·z^−d)/d at the z this step started from
                        // (z^−d = conj(z^d)/|z^d|²), multiplied in; power-of-two renormalisation is exact.
                        // From the first iterate on: Nova's parameter plane starts at the critical point
                        // z₀ = 1 (N′(z₀) = 0), which would pin the product to 0.
                        double zd2 = zdR * zdR + zdI * zdI;
                        double wR = (d - 1) * zdR / zd2, wI = -(d - 1) * zdI / zd2;
                        double mR = 1 - R * (1 + wR) / d, mI = -R * wI / d;
                        FracturingFog.Perturbation.PerturbationEngine.MulIn(ref mul, ref mulE, mR * mR + mI * mI);
                        if (FracturingFog.Perturbation.PerturbationEngine.Chaotic(mul, mulE, chaosLog2Sq))
                        {
                            chaos = true;
                            Interlocked.Increment(ref _statChaos);
                            Interlocked.Add(ref _statStopIter, iter + 1);
                            Interlocked.Add(ref _statSaved, maxIter - (iter + 1));
                            break;
                        }
                    }

                    if (brent)
                    {
                        // #1979 — z_{iter+1} repeats a snapshot bit for bit: the step is a function of z alone, so
                        // the orbit cycles from here with period lam and never stops. Run on to the cap's phase
                        // (fewer than lam steps), so z, the buffers and the colour equal a run to the cap.
                        lam++;
                        if (zr == snapR && zi == snapI)
                        {
                            cap = iter + 1 + (maxIter - (iter + 1)) % lam;
                            brent = false;
                            Interlocked.Increment(ref _statCycles);
                            Interlocked.Add(ref _statStopIter, iter + 1);
                            Interlocked.Add(ref _statSaved, maxIter - cap);
                        }
                        else if (lam == pow) { snapR = zr; snapI = zi; pow <<= 1; lam = 0; }
                    }
                }
                if (iter == cap && cap < maxIter || chaos) iter = maxIter;   // #1979 / #1980 — as at the cap
            converged:
                FinishPixel(rowBase + x, x, y, basin, iter, smooth, zr, zi, d, maxIter, nova, newtonMap);
            }
        });
    }

    // One finished pixel's buffers and colour (shared by the double loop and, #1959, the deep path).
    private void FinishPixel(int idx, int x, int y, int basin, int iter, float smooth, double zr, double zi,
                             int d, int maxIter, bool nova, INewtonColorMap? newtonMap)
    {
        // Relief height / HE scalar = iterations to convergence.
        SmoothBuffer[idx] = nova && basin >= 0 ? smooth : iter;
        BasinBuffer[idx] = basin;               // #146 — HE + recolor source
        FinalZrBuffer[idx] = (float)zr;
        FinalZiBuffer[idx] = (float)zi;
        if (basin >= 0 && nova)
        {
            GradientColorMap.SetDitherForPixel(x, y);
            ColorBuffer[idx] = NewtonBasinColoring.NovaConvergedColor(
                newtonMap, ColorMap, basin, d, smooth, maxIter, zr, zi);
        }
        else if (basin >= 0)
        {
            // Converged: shared dispatch (MapNewton theme or HSV fallback).
            ColorBuffer[idx] = NewtonBasinColoring.ConvergedColor(
                newtonMap, basin, d, iter, maxIter, zr, zi);
        }
        else if (newtonMap != null)
        {
            // #830 — non-converged (in-set) pixels scale by the knob.
            uint c = unchecked((uint)newtonMap.MapNewton(basin, d, iter, maxIter, zr, zi));
            ColorBuffer[idx] = InteriorAlphaStamp.ScaleArgbAlpha(c, InteriorAlpha);
        }
        else
        {
            ColorBuffer[idx] = InteriorAlphaStamp.ScaleArgbAlpha(ColorMap.InSetColor, InteriorAlpha);  // #830
        }
    }

    // #1294 / #1959 — Nova's smooth convergence count: log-interpolated where the step size crossed the
    // tolerance between the step before (stepPrev²) and this one (as the Magnet path does).
    public static float NovaSmooth(int iter, double stepPrev2, double step2, double eps2)
    {
        float smooth = iter;
        if (iter > 0 && stepPrev2 < double.MaxValue)
        {
            double logEps = 0.5 * Math.Log(eps2);
            double logPrev = 0.5 * Math.Log(stepPrev2);
            double logCur = 0.5 * Math.Log(Math.Max(step2, 1e-300));
            double span = logPrev - logCur;
            double frac = span > 1e-12 ? (logPrev - logEps) / span : 0.0;
            smooth = (float)(iter - 1 + Math.Clamp(frac, 0.0, 1.0));
        }
        return smooth;
    }

    /// <summary>#1294 — index k of the root e^(2πik/d) whose angular sector holds
    /// z, i.e. the nearest root direction. For a z on a root it is that root's
    /// index, matching Newton's basin numbering.</summary>
    public static int RootSector(double zr, double zi, int d)
    {
        int k = (int)Math.Round(Math.Atan2(zi, zr) * d / (2 * Math.PI));
        return ((k % d) + d) % d;
    }

    // ── #146 histogram equalization (basin families) ───────────────────────────
    // Delegates to the shared NewtonBasinColoring core. The host / poster / batch
    // / video paths pick this up via `calc is ISupportsHistogramEq` (wired #145).

    public bool BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter)
    {
        sourceMaxIter = MaxIterations;
        return NewtonBasinColoring.BuildCdf(
            Width * Height, MaxIterations, BasinBuffer, SmoothBuffer, out cdf, out bins);
    }

    public void ApplyHistogramEqualization(double strength)
    {
        if (!BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter)) return;
        ApplyHistogramEqualizationWithCdf(cdf!, bins, sourceMaxIter, strength);
    }

    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, 0.0, out _, out _);

    public void ApplyHistogramEqualizationWithCdf(
        double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength,
        out long escapedCount, out long saturatedCount)
    {
        NewtonBasinColoring.ApplyWithCdf(
            ColorBuffer, Width, Height, BasinBuffer, SmoothBuffer, FinalZrBuffer, FinalZiBuffer,
            _basins, MaxIterations, ColorMap as INewtonColorMap,
            cdf, bins, sourceMaxIter, strength, ditherIterStrength,
            out escapedCount, out saturatedCount,
            novaPalette: _renderedNova ? ColorMap : null);   // #1294
    }
}
