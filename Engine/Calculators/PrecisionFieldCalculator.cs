// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// PrecisionFieldCalculator.cs (#628 / Renderer C)
//
// Precision-sensitivity field. Iterates Mandelbrot z² + c at TWO arithmetic
// tiers and colours each pixel by the divergence between the two outcomes. This
// is the scoping doc's Renderer C, reframed onto FF's fixed precision tiers
// (Float / Double / FFMath.DD / FFMath.QD) because FF has no MPFR.
//
// The outcome captured per tier is (smooth iteration count, escape angle
// arg(z)). The per-pixel divergence between the low and high tier is combined
// into one scalar (PrecisionDiffMetric) and written to SmoothBuffer, so every
// existing 2D colour theme / ColorGen theme / Relief-3D height path colours it
// with no new plumbing.
//
// Expected result: interior and deep-basin pixels agree between tiers and go
// dark; boundary filaments where the low tier loses precision light up — an
// empirical map of where the fractal is numerically fragile (conceptually a
// Lyapunov-adjacent image, computed by numerical divergence rather than
// analytically).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class PrecisionFieldCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // The divergence scalar per pixel (scaled to [0, maxIter]) doubles as the
    // Relief-3D height field: fragile boundary filaments rise, agreeing interior
    // stays flat.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = -0.5;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    // Bailout radius. 128 (not 2) gives a smoother continuous iteration count,
    // which sharpens the inter-tier difference near the boundary.
    private const double EscapeR = 128.0;
    private const double EscapeR2 = EscapeR * EscapeR;
    private static readonly double LogEscapeR = Math.Log(EscapeR);

    // A single-iteration disagreement of this many smooth counts saturates the
    // iteration-difference channel to full brightness.
    private const double IterDiffSaturation = 24.0;

    public PrecisionFieldCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    private readonly struct Outcome
    {
        public readonly bool Escaped;
        public readonly double SmoothN;   // continuous iteration count
        public readonly double ArgZ;      // arg(z) at escape, radians
        public Outcome(bool escaped, double smoothN, double argZ)
        { Escaped = escaped; SmoothN = smoothN; ArgZ = argZ; }
    }

    public void Calculate(CancellationToken ct = default)
    {
        int maxIter = Math.Max(16, MaxIterations);
        var lowTier = FractalParameters.PrecisionLowTier;
        var highTier = FractalParameters.PrecisionHighTier;
        var metric = FractalParameters.PrecisionDiffMetric;

        ColorMap.MaxIterations = maxIter;

        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        int width = Width, height = Height;
        double centerX = CenterX, centerY = CenterY;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            double cy = centerY + (y - height * 0.5) * pixelPitch;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * pixelPitch;

                Outcome lo = Iterate(lowTier, cx, cy, maxIter);
                Outcome hi = Iterate(highTier, cx, cy, maxIter);

                double diff = Diff(lo, hi, metric, maxIter);   // [0, 1]

                int idx = rowBase + x;
                float smooth = (float)(diff * maxIter);
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = unchecked((uint)ColorMap.Map(smooth, 0f, maxIter));
            }
        });
    }

    // Combine the two outcomes into a [0, 1] divergence scalar.
    private static double Diff(in Outcome a, in Outcome b, PrecisionDiffMetric metric, int maxIter)
    {
        // Disagreement on membership itself is maximal fragility.
        if (a.Escaped != b.Escaped) return 1.0;
        if (!a.Escaped) return 0.0;   // both in-set — tiers agree, interior dark

        double dIter = Math.Min(Math.Abs(a.SmoothN - b.SmoothN) / IterDiffSaturation, 1.0);
        double dArg = Math.Abs(a.ArgZ - b.ArgZ);
        if (dArg > Math.PI) dArg = 2.0 * Math.PI - dArg;   // shortest angular gap
        double dArgN = dArg / Math.PI;                      // [0, 1]

        return metric switch
        {
            PrecisionDiffMetric.IterationOnly => dIter,
            PrecisionDiffMetric.AngleOnly => dArgN,
            _ => Math.Sqrt(dIter * dIter + dArgN * dArgN) / Math.Sqrt(2.0),
        };
    }

    private static Outcome Iterate(PrecisionTier tier, double cx, double cy, int maxIter) => tier switch
    {
        PrecisionTier.Float => IterateFloat((float)cx, (float)cy, maxIter),
        PrecisionTier.DoubleDouble => IterateDD(cx, cy, maxIter),
        PrecisionTier.QuadDouble => IterateQD(cx, cy, maxIter),
        _ => IterateDouble(cx, cy, maxIter),
    };

    private static double Smooth(int n, double mag2)
    {
        // n + 1 - log2( log|z| / log R ) — continuous escape count.
        double logZ = 0.5 * Math.Log(mag2);
        return n + 1.0 - Math.Log(logZ / LogEscapeR, 2.0);
    }

    private static Outcome IterateFloat(float cx, float cy, int maxIter)
    {
        float zr = 0f, zi = 0f;
        for (int n = 0; n < maxIter; n++)
        {
            float zr2 = zr * zr, zi2 = zi * zi;
            float mag2 = zr2 + zi2;
            if (mag2 > (float)EscapeR2)
                return new Outcome(true, Smooth(n, mag2), Math.Atan2(zi, zr));
            zi = 2f * zr * zi + cy;
            zr = zr2 - zi2 + cx;
        }
        return new Outcome(false, maxIter, 0.0);
    }

    private static Outcome IterateDouble(double cx, double cy, int maxIter)
    {
        double zr = 0, zi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            double mag2 = zr2 + zi2;
            if (mag2 > EscapeR2)
                return new Outcome(true, Smooth(n, mag2), Math.Atan2(zi, zr));
            zi = 2.0 * zr * zi + cy;
            zr = zr2 - zi2 + cx;
        }
        return new Outcome(false, maxIter, 0.0);
    }

    private static Outcome IterateDD(double cx, double cy, int maxIter)
    {
        DD zr = new(0.0), zi = new(0.0);
        DD cr = new(cx), ci = new(cy);
        for (int n = 0; n < maxIter; n++)
        {
            DD zr2 = zr * zr, zi2 = zi * zi;
            DD mag2 = zr2 + zi2;
            if (mag2 > EscapeR2)
                return new Outcome(true, Smooth(n, mag2.Hi), Math.Atan2(zi.Hi, zr.Hi));
            zi = (zr * zi) * 2.0 + ci;
            zr = zr2 - zi2 + cr;
        }
        return new Outcome(false, maxIter, 0.0);
    }

    private static Outcome IterateQD(double cx, double cy, int maxIter)
    {
        QD zr = new(0.0), zi = new(0.0);
        QD cr = new(cx), ci = new(cy);
        for (int n = 0; n < maxIter; n++)
        {
            QD zr2 = zr * zr, zi2 = zi * zi;
            QD mag2 = zr2 + zi2;
            if (mag2 > EscapeR2)
                return new Outcome(true, Smooth(n, mag2.X0), Math.Atan2(zi.X0, zr.X0));
            zi = (zr * zi) * 2.0 + ci;
            zr = zr2 - zi2 + cr;
        }
        return new Outcome(false, maxIter, 0.0);
    }
}
