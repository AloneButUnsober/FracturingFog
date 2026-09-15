// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SecantCalculator.cs
//
// Secant-method basins for f(z) = z^d − 1. Two-point recurrence:
//     z_{n+1} = z_n − R · f(z_n) · (z_n − z_{n−1}) / (f(z_n) − f(z_{n−1}))
// Order of convergence ≈ φ ≈ 1.618 (superlinear, between Newton's 2 and
// linear). No derivative needed — the slope is approximated by the chord
// through the last two iterates, which is why the kernel must carry
// prev-z state (the roadmap notes this pattern; PhoenixKernel uses the
// same idea inside EscapeTimeCalculator.CalculatePhoenix).
//
// Reuses FractalParameters.NewtonExponent (d) + NewtonRelaxation (R) so
// the Params dialog shares wiring with Newton / Nova / Halley. A
// per-family SecantInitialOffset Complex param controls the initial
// prev_z displacement (default (0.5, 0)) — required because the secant
// recurrence is undefined if prev_z = z_0.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class SecantCalculator : IFractalCalculator, ISupportsHistogramEq
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // #146 — per-pixel fields for the basin histogram-equalizer. Secant is not
    // an IHeightFieldSource (no relief), so it carries its own convergence-count
    // buffer as the HE scalar rather than reusing a SmoothBuffer.
    public float[] IterBuffer { get; private set; } = Array.Empty<float>();
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

    public SecantCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        IterBuffer = new float[n];
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
        Complex off = FractalParameters.SecantInitialOffset;
        double offR = off.Real;
        double offI = off.Imaginary;
        // Guard against zero offset — degenerate first-step denominator.
        if (offR * offR + offI * offI < 1e-12) { offR = 0.5; offI = 0.0; }

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
                double pr = cx + offR, pi = cy + offI;

                // Cached f(prev) so each iteration only evaluates one
                // new polynomial (the other comes from the previous step).
                ComputeZd(pr, pi, d, out double fpR, out double fpI);
                fpR -= 1.0;

                int iter;
                int basin = -1;
                for (iter = 0; iter < maxIter; iter++)
                {
                    ComputeZd(zr, zi, d, out double fR, out double fI);
                    fR -= 1.0;

                    double diffR = fR - fpR;
                    double diffI = fI - fpI;
                    double diffMag2 = diffR * diffR + diffI * diffI;
                    if (diffMag2 < 1e-30) break;

                    // step = f(z) · (z - prev) / (f(z) - f(prev))
                    double dzR = zr - pr;
                    double dzI = zi - pi;
                    // numerator = f(z) · (z - prev)
                    double numR = fR * dzR - fI * dzI;
                    double numI = fR * dzI + fI * dzR;
                    // quot = num / diff
                    double quotR = (numR * diffR + numI * diffI) / diffMag2;
                    double quotI = (numI * diffR - numR * diffI) / diffMag2;

                    pr = zr; pi = zi;
                    fpR = fR; fpI = fI;
                    zr -= R * quotR;
                    zi -= R * quotI;

                    for (int k = 0; k < d; k++)
                    {
                        double dx = zr - rootsR[k];
                        double dy = zi - rootsI[k];
                        if (dx * dx + dy * dy < eps2) { basin = k; goto converged; }
                    }
                }
            converged:
                int idx = rowBase + x;
                IterBuffer[idx] = iter;            // #146 HE scalar
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
            Width * Height, MaxIterations, BasinBuffer, IterBuffer, out cdf, out bins);
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
            ColorBuffer, Width, Height, BasinBuffer, IterBuffer, FinalZrBuffer, FinalZiBuffer,
            _basins, MaxIterations, ColorMap as INewtonColorMap,
            cdf, bins, sourceMaxIter, strength, ditherIterStrength,
            out escapedCount, out saturatedCount);
    }

    private static void ComputeZd(double zr, double zi, int d, out double outR, out double outI)
    {
        double r2 = zr * zr + zi * zi;
        if (r2 < 1e-30) { outR = 0; outI = 0; return; }
        double r = Math.Sqrt(r2);
        double theta = Math.Atan2(zi, zr);
        double rPowD = Math.Pow(r, d);
        outR = rPowD * Math.Cos(d * theta);
        outI = rPowD * Math.Sin(d * theta);
    }
}
