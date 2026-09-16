// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// LyapunovCalculator.cs
//
// Markus–Lyapunov fractal (#851). NOT escape-time. A periodic string over
// {A, B} schedules the growth rate of the logistic map
//
//     x_{n+1} = r_n · x_n · (1 − x_n)
//
// where r_n cycles A→a, B→b through the string. Each pixel is a point (a, b)
// in the logistic-parameter plane (both axes in (0, 4]); the pixel value is the
// Lyapunov exponent of the resulting forced orbit:
//
//     λ = (1 / N) · Σ ln | r_n · (1 − 2·x_n) |.
//
// λ < 0 → the orbit is stable / periodic (the ridged "Zircon Zity" solids);
// λ > 0 → chaotic. This is a parameter-plane image, not dynamical z-space.
//
// View convention matches the escape-time families:
//     pixel (W/2, H/2) ↔ (CenterX, CenterY)
//     scale = (3.5 / max(W, H)) / Zoom
// CenterX = a-axis (horizontal), CenterY = b-axis (vertical). Default frame
// (CenterX=CenterY=3.5, Zoom=1) centres the classic a,b ∈ ~[2, 4] window.
//
// Colouring (R&D doc §2.1): the signed λ is mapped onto the SmoothBuffer —
// exactly as PrecisionField rides SmoothBuffer — so every existing 2D colour
// theme, ColorGen theme and Relief-3D height path works unchanged. λ is mapped
// linearly from [LambdaMin, LambdaMax] onto [0, MaxIterations]; pixels whose
// (a, b) leave the valid (0, 4] range are painted InSetColor.
//
// Reference: Markus & Hess, Computers & Graphics 13(4), 1989 (see
// Docs/Resources-Bibliography.md#markus-hess-lyapunov); Dewdney, Scientific
// American, Sept 1991.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class LyapunovCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>Signed Lyapunov exponent per pixel mapped onto [0, MaxIterations]
    /// (see class summary). Same length / layout as ColorBuffer; drives Relief-3D
    /// via <see cref="IHeightFieldSource"/> and every SmoothBuffer-based theme.</summary>
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 2.9;
    public double CenterY { get; set; } = 3.0;
    public double Zoom { get; set; } = 1.7;
    public int MaxIterations { get; set; } = 400;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // λ is unbounded below (period-doubling cascades dive toward −∞) but the
    // visually-interesting stable band sits in ≈[−1.5, 0]; chaos is small and
    // positive. Map [LambdaMin, LambdaMax] → [0, MaxIterations] for the palette.
    private const double LambdaMin = -1.5;
    private const double LambdaMax = 0.5;

    public LyapunovCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        SmoothBuffer = new float[n];
    }

    public void Calculate(CancellationToken ct)
    {
        int width = Width;
        int height = Height;
        if (width < 1 || height < 1) return;

        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double centerX = CenterX;
        double centerY = CenterY;
        int maxIt = Math.Max(16, MaxIterations);
        int warmup = Math.Clamp(FractalParameters.LyapunovWarmup, 0, maxIt - 1);
        int plot = Math.Max(1, maxIt - warmup);

        // Parse the {A, B} schedule once. Any non-'A'/'a' char reads as B, so
        // strings like "AABAB" or "BBBBBBAAAAAA" work; empty falls back to "AB".
        string seq = FractalParameters.LyapunovSequence;
        if (string.IsNullOrEmpty(seq)) seq = "AB";
        int seqLen = seq.Length;
        // Precompute per-position "is A" flags to avoid char work in the hot loop.
        bool[] isA = new bool[seqLen];
        for (int i = 0; i < seqLen; i++)
            isA[i] = seq[i] == 'A' || seq[i] == 'a';

        var cm = ColorMap;
        cm.MaxIterations = maxIt;
        uint inSetColor = cm.InSetColor;
        double invRange = 1.0 / (LambdaMax - LambdaMin);

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double b = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            bool bValid = b > 0.0 && b <= 4.0;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                double a = centerX + (x - width * 0.5) * scale;

                if (!bValid || a <= 0.0 || a > 4.0)
                {
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = inSetColor;
                    continue;
                }

                // Forced logistic orbit. Seed 0.5 (any non-fixed seed lands on
                // the same attractor after warm-up).
                double xv = 0.5;
                int si = 0;
                for (int i = 0; i < warmup; i++)
                {
                    double r = isA[si] ? a : b;
                    xv = r * xv * (1.0 - xv);
                    if (++si >= seqLen) si = 0;
                }

                double sumLog = 0.0;
                for (int i = 0; i < plot; i++)
                {
                    double r = isA[si] ? a : b;
                    xv = r * xv * (1.0 - xv);
                    if (++si >= seqLen) si = 0;
                    // |dx_{n+1}/dx_n| = |r · (1 − 2x)|. Floor the argument so a
                    // superstable hit (1 − 2x ≈ 0) contributes a large-negative,
                    // finite term instead of −∞.
                    double deriv = Math.Abs(r * (1.0 - 2.0 * xv));
                    sumLog += Math.Log(deriv < 1e-30 ? 1e-30 : deriv);
                }

                double lambda = sumLog / plot;

                // Map λ → [0, maxIt] for the palette + relief height.
                double t = (lambda - LambdaMin) * invRange;
                if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                float smooth = (float)(t * maxIt);
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = (uint)cm.Map(smooth, 0f, maxIt);
            }
        });
    }
}
