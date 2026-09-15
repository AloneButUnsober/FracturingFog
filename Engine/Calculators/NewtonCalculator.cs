// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// NewtonCalculator.cs
//
// Renders Newton fractal for f(z) = z^d - 1. Iterates z := z - R·f(z)/f'(z)
// until convergence to a root. Color is basin (root index) hue blended with
// iteration count for shading.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
// INewtonColorMap lives in FracturingFog.Interefaces

namespace FracturingFog;

public sealed class NewtonCalculator : IFractalCalculator, IHeightFieldSource, ISupportsHistogramEq
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

    public FractalParameters FractalParameters { get; set; } = new();

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
            converged:
                int idx = rowBase + x;
                // Relief height / HE scalar = iterations to convergence.
                SmoothBuffer[idx] = iter;
                BasinBuffer[idx] = basin;               // #146 — HE + recolor source
                FinalZrBuffer[idx] = (float)zr;
                FinalZiBuffer[idx] = (float)zi;
                if (basin >= 0)
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
        });
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
            out escapedCount, out saturatedCount);
    }
}
