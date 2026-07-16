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

public sealed class SecantCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 64;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public SecantCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        int d = Math.Clamp(FractalParameters.NewtonExponent, 2, 8);
        double R = FractalParameters.NewtonRelaxation;
        int maxIter = MaxIterations;
        if (maxIter < 8) maxIter = 64;
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
                if (newtonMap != null)
                {
                    int rgb = newtonMap.MapNewton(basin, d, iter, maxIter, zr, zi);
                    ColorBuffer[idx] = unchecked((uint)rgb);
                }
                else if (basin < 0)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                }
                else
                {
                    float hue = (float)basin / d;
                    float shade = 1.0f - Math.Min(iter / (float)maxIter, 0.9f);
                    int rgb = HsvToArgb(hue, 1.0f, shade);
                    ColorBuffer[idx] = unchecked((uint)rgb);
                }
            }
        });
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

    private static int HsvToArgb(float h, float s, float v)
    {
        h = h * 6f;
        int i = (int)Math.Floor(h);
        float f = h - i;
        float p = v * (1 - s);
        float q = v * (1 - s * f);
        float t = v * (1 - s * (1 - f));
        float rF, gF, bF;
        switch (i % 6)
        {
            case 0: rF = v; gF = t; bF = p; break;
            case 1: rF = q; gF = v; bF = p; break;
            case 2: rF = p; gF = v; bF = t; break;
            case 3: rF = p; gF = q; bF = v; break;
            case 4: rF = t; gF = p; bF = v; break;
            case 5: rF = v; gF = p; bF = q; break;
            default: rF = gF = bF = 0; break;
        }
        int r = (int)(rF * 255);
        int g = (int)(gF * 255);
        int b = (int)(bF * 255);
        return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
    }
}
