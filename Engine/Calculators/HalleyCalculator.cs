// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// HalleyCalculator.cs
//
// Halley's method for the polynomial f(z) = z^d − 1. Cubic convergence vs
// Newton's quadratic; the basin geometry is similar but boundary detail is
// finer. Reuses FractalParameters.NewtonExponent (d) and NewtonRelaxation
// (R) so the Halley dialog shares wiring with Newton — picking Halley
// from the Type combo keeps the user's selected d / R.
//
// Iteration:
//     f   = z^d − 1
//     f'  = d · z^(d−1)
//     f'' = d (d − 1) · z^(d−2)
//     z  := z − R · 2 f f' / (2 f'² − f f'')
//
// Roots of z^d = 1 are the d-th unit roots; the calculator colours by
// basin index (which root the iteration converged to) and shades by
// iteration count, identical to NewtonCalculator. The INewtonColorMap
// path is honoured so themes authored for Newton render on Halley
// without modification.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class HalleyCalculator : IFractalCalculator, IHeightFieldSource, ISupportsHistogramEq
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // #139 — Relief 3D height = iterations to convergence (see NewtonCalculator).
    // Also the #146 HE scalar (per-pixel convergence step count).
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    // #146 — per-pixel fields for the basin histogram-equalizer (see
    // NewtonCalculator for the rationale).
    public int[] BasinBuffer { get; private set; } = Array.Empty<int>();
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();

    private int _basins = 2;   // polynomial degree d, cached for the HE recolor

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 64;

    /// <summary>Global interior-alpha knob (#830): basin non-convergence
    /// (basin &lt; 0) is the in-set; scaled inline at the write site. 255 =
    /// opaque (byte-identical). See <see cref="NewtonCalculator.InteriorAlpha"/>.</summary>
    public int InteriorAlpha { get; set; } = 255;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public HalleyCalculator(int width, int height) => Resize(width, height);

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

        var newtonMap = ColorMap as INewtonColorMap;
        ColorMap.MaxIterations = maxIter;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                double zr = cx, zi = cy;
                int iter;
                int basin = -1;
                for (iter = 0; iter < maxIter; iter++)
                {
                    double r2 = zr * zr + zi * zi;
                    if (r2 < 1e-30) { zr = 1e-10; zi = 0; r2 = 1e-20; }
                    double r = Math.Sqrt(r2);
                    double theta = Math.Atan2(zi, zr);

                    double rPowD = Math.Pow(r, d);
                    double rPowDm1 = rPowD / r;
                    double rPowDm2 = rPowDm1 / r;

                    // z^d, z^(d-1), z^(d-2) via polar.
                    double zdR    = rPowD   * Math.Cos(d * theta);
                    double zdI    = rPowD   * Math.Sin(d * theta);
                    double zdm1R  = rPowDm1 * Math.Cos((d - 1) * theta);
                    double zdm1I  = rPowDm1 * Math.Sin((d - 1) * theta);
                    double zdm2R  = rPowDm2 * Math.Cos((d - 2) * theta);
                    double zdm2I  = rPowDm2 * Math.Sin((d - 2) * theta);

                    // f, f', f''.
                    double fR = zdR - 1.0;
                    double fI = zdI;
                    double fpR = d * zdm1R;
                    double fpI = d * zdm1I;
                    double fppR = d * (d - 1) * zdm2R;
                    double fppI = d * (d - 1) * zdm2I;

                    // num = 2 f f'
                    double ffR = fR * fpR - fI * fpI;
                    double ffI = fR * fpI + fI * fpR;
                    double numR = 2.0 * ffR;
                    double numI = 2.0 * ffI;

                    // den = 2 f'² − f f''
                    double fp2R = fpR * fpR - fpI * fpI;
                    double fp2I = 2.0 * fpR * fpI;
                    double ffppR = fR * fppR - fI * fppI;
                    double ffppI = fR * fppI + fI * fppR;
                    double denR = 2.0 * fp2R - ffppR;
                    double denI = 2.0 * fp2I - ffppI;

                    double denMag2 = denR * denR + denI * denI;
                    if (denMag2 < 1e-30) break;

                    // quot = num / den.
                    double quotR = (numR * denR + numI * denI) / denMag2;
                    double quotI = (numI * denR - numR * denI) / denMag2;

                    zr -= R * quotR;
                    zi -= R * quotI;

                    // Root convergence test.
                    for (int k = 0; k < d; k++)
                    {
                        double dx = zr - rootsR[k];
                        double dy = zi - rootsI[k];
                        if (dx * dx + dy * dy < eps2) { basin = k; goto converged; }
                    }
                }
            converged:
                int idx = rowBase + x;
                SmoothBuffer[idx] = iter;   // #139 relief height / #146 HE scalar
                BasinBuffer[idx] = basin;
                FinalZrBuffer[idx] = (float)zr;
                FinalZiBuffer[idx] = (float)zi;
                if (basin >= 0)
                {
                    ColorBuffer[idx] = NewtonBasinColoring.ConvergedColor(
                        newtonMap, basin, d, iter, maxIter, zr, zi);
                }
                else if (newtonMap != null)
                {
                    uint c = unchecked((uint)newtonMap.MapNewton(basin, d, iter, maxIter, zr, zi));
                    ColorBuffer[idx] = InteriorAlphaStamp.ScaleArgbAlpha(c, InteriorAlpha);  // #830
                }
                else
                {
                    ColorBuffer[idx] = InteriorAlphaStamp.ScaleArgbAlpha(ColorMap.InSetColor, InteriorAlpha);  // #830
                }
            }
        });
    }

    // ── #146 histogram equalization (basin families) — see NewtonCalculator ────

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
            out escapedCount, out saturatedCount);
    }
}
