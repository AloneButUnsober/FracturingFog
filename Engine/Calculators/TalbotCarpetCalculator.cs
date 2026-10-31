// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// TalbotCarpetCalculator.cs
//
// Fractal Talbot carpet (#1570, double-slit epic #1565 S6; design:
// Docs/Technical/DoubleSlit-Fractal-RnD.md §7). A periodic grating lit by a
// plane wave images itself at multiples of the Talbot distance z_T = 2d²/λ;
// in between, the (transverse, propagation) intensity is a "quantum carpet",
// and at irrational z/z_T the transverse intensity is a fractal (Berry &
// Klein, J. Mod. Opt. 43, 1996; Berry, Marzoli & Schleich, Physics World 14,
// 2001). Its quantum twin is a uniform state in a box (Berry, J. Phys. A 29,
// 1996).
//
// Paraxially, a grating with Fourier coefficients cₙ propagated a distance z is
//
//     ψ(x, z) = Σₙ cₙ · e^{2πi·n·x/d} · e^{−iπ·λ·z·n²/d²}.
//
// In grating periods ξ = x/d and Talbot distances ζ = z/z_T the propagation
// phase is exactly 2π·n²·ζ, so the carpet has no free scale:
//
//     ψ(ξ, ζ) = Σ_{|n|≤M} cₙ · e^{2πi·n·ξ} · e^{−2πi·n²·ζ}.
//
// ζ = 1 gives the grating back (n² is an integer); ζ = ½ gives it shifted by
// half a period (n² ≡ n mod 2); rational ζ gives finite superpositions of
// shifted copies (fractional Talbot). The view's x axis is ξ, its y axis ζ
// (propagation runs down the screen).
//
// Profiles (cₙ): Ronchi — a square wave of duty a, cₙ = a·sinc(π·n·a);
// δ-comb — cₙ = 1 (Berry & Klein's idealisation, the sharpest carpet); and
// SlitCascade — one period of the #1569 cascade aperture on the Line layout
// (period 2, the level-0 square [−1, 1]), cₙ = r^D·A(n/2) with A the
// cascade's far-field amplitude. M (TalbotModes) truncates the sum: it is
// how sharp the grating edges are, and so how fine the carpet's fractal
// detail can get.
//
// Cost: each row has one ζ, so bₙ = cₙ·e^{−2πi·n²·ζ} is built once per row and
// the pixel sum steps e^{2πi·n·ξ} by complex multiplication — O(M) per pixel
// with no trig in the inner loop.
//
// Domain view (#1592): hue = arg ψ on the colour-blind-safe Phase Wheel,
// brightness = the log-intensity scale below (Wegert-style phase portrait; the
// theme is not used). The SmoothBuffer keeps the intensity for Relief.
//
// Scale: by Parseval the mean intensity over a period is Σ|cₙ|² at every ζ
// (propagation conserves energy), and |ψ|² ≤ (Σ|cₙ|)². The log of I/mean is
// mapped from −TalbotDynamicRange decades (dark) up to that bound (white), so
// the scale is the same everywhere in the carpet and under pan / animation.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class TalbotCarpetCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>Log intensity (or phase) mapped onto [0, MaxIterations]; 0 = dark.</summary>
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; }
    public double CenterY { get; set; } = 0.5;
    public double Zoom { get; set; } = 1.9;
    public int MaxIterations { get; set; } = 400;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public const int MaxModes = 4096;

    public TalbotCarpetCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    /// <summary>The grating's Fourier coefficients c₋M..c_M (index n + M).</summary>
    public static Complex[] Coefficients(FractalParameters p)
    {
        int m = Math.Clamp(p.TalbotModes, 1, MaxModes);
        var c = new Complex[2 * m + 1];
        switch (p.TalbotProfile)
        {
            case TalbotProfile.DeltaComb:
                for (int n = -m; n <= m; n++) c[n + m] = Complex.One;
                break;
            case TalbotProfile.SlitCascade:
            {
                var lineParams = p.Clone();
                lineParams.SlitCascadeLayout = SlitCascadeLayout.Line;   // one period of a 1D grating
                var cascade = SlitCascadeCalculator.Build(lineParams);
                double leaf = Math.Pow(cascade.Ratio, cascade.Depth);
                for (int n = -m; n <= m; n++) c[n + m] = leaf * SlitCascadeCalculator.Amplitude(cascade, n / 2.0, 0.0);
                break;
            }
            default:
            {
                double a = Math.Clamp(double.IsFinite(p.TalbotDuty) ? p.TalbotDuty : 0.3, 1e-6, 1.0);
                for (int n = -m; n <= m; n++)
                {
                    double t = Math.PI * n * a;
                    c[n + m] = a * (n == 0 ? 1.0 : Math.Sin(t) / t);
                }
                break;
            }
        }
        return c;
    }

    /// <summary>ψ(ξ, ζ) by the direct sum (header) — the reference the row
    /// recurrence must match.</summary>
    public static Complex Field(Complex[] c, double xi, double zeta)
    {
        int m = (c.Length - 1) / 2;
        Complex sum = Complex.Zero;
        for (int n = -m; n <= m; n++)
        {
            double ph = 2.0 * Math.PI * (n * xi - (double)n * n * zeta);
            sum += c[n + m] * new Complex(Math.Cos(ph), Math.Sin(ph));
        }
        return sum;
    }

    public void Calculate(CancellationToken ct)
    {
        int width = Width, height = Height;
        if (width < 1 || height < 1) return;
        var fp = FractalParameters;
        var c = Coefficients(fp);
        int m = (c.Length - 1) / 2;
        double sumAbs = 0.0, mean = 0.0;
        foreach (var v in c) { sumAbs += v.Magnitude; mean += v.Real * v.Real + v.Imaginary * v.Imaginary; }
        double logMean = Math.Log10(Math.Max(mean, 1e-300));
        double top = Math.Max(1e-6, 2.0 * Math.Log10(Math.Max(sumAbs, 1e-300)) - logMean);   // bound over mean, decades
        bool phase = fp.TalbotView == TalbotView.Phase;
        bool domain = fp.TalbotView == TalbotView.Domain;   // #1592
        double decades = Math.Clamp(double.IsFinite(fp.TalbotDynamicRange) ? fp.TalbotDynamicRange : 2.0, 0.25, 30.0);

        double pitch = (4.0 / Math.Max(1, width)) / Math.Max(1e-300, Zoom);
        double cx = CenterX, cy = CenterY;
        int maxIt = Math.Max(16, MaxIterations);
        var cm = ColorMap;
        cm.MaxIterations = maxIt;
        uint inSet = cm.InSetColor;
        float[] smooth = SmoothBuffer; uint[] color = ColorBuffer;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            double zeta = cy + (y - height * 0.5) * pitch;
            // bₙ = cₙ·e^{−2πi·n²·ζ}; n² mod a period keeps the angle small at large ζ·n².
            var b = new Complex[c.Length];
            double zf = zeta - Math.Floor(zeta);
            for (int n = -m; n <= m; n++)
            {
                double ang = (double)n * n * zf;
                ang -= Math.Floor(ang);
                double ph = -2.0 * Math.PI * ang;
                b[n + m] = c[n + m] * new Complex(Math.Cos(ph), Math.Sin(ph));
            }
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double xi = cx + (x - width * 0.5) * pitch;
                double xf = xi - Math.Floor(xi);
                double a0 = 2.0 * Math.PI * xf;
                var w = new Complex(Math.Cos(a0), Math.Sin(a0));
                // Σ bₙ wⁿ from n = −M: start at w^{−M} and multiply up.
                double am = -2.0 * Math.PI * ((m * xf) - Math.Floor(m * xf));
                var wn = new Complex(Math.Cos(am), Math.Sin(am));
                double re = 0.0, im = 0.0;
                for (int k = 0; k < b.Length; k++)
                {
                    var bk = b[k];
                    re += bk.Real * wn.Real - bk.Imaginary * wn.Imaginary;
                    im += bk.Real * wn.Imaginary + bk.Imaginary * wn.Real;
                    wn *= w;
                }
                if (domain)
                {
                    double m2 = re * re + im * im;
                    double ti = m2 > 0.0 ? Math.Clamp((Math.Log10(m2) - logMean + decades) / (decades + top), 0.0, 1.0) : 0.0;
                    int di = row + x;
                    smooth[di] = (float)(ti * maxIt);
                    color[di] = PhaseWheelBlueAmberMap.ColorAt(Math.Atan2(im, re) / (2.0 * Math.PI), ti);
                    continue;
                }
                double t;
                if (phase)
                {
                    t = Math.Atan2(im, re) / (2.0 * Math.PI);
                    t -= Math.Floor(t);
                    t = Math.Max(t, 1e-9);   // phase 0 is a colour, not "dark"
                }
                else
                {
                    double mag2 = re * re + im * im;
                    t = mag2 > 0.0 ? (Math.Log10(mag2) - logMean + decades) / (decades + top) : 0.0;
                }
                int idx = row + x;
                if (!(t > 0.0)) { smooth[idx] = 0f; color[idx] = inSet; continue; }
                float s = (float)(Math.Min(1.0, t) * maxIt);
                // #1591 — phase 1 ≡ 0: a value rounding up to maxIt would read as "in the set".
                if (phase && s >= maxIt) s = (float)(1e-9 * maxIt);
                smooth[idx] = s;
                color[idx] = (uint)cm.Map(s, 0f, maxIt);
            }
        });
    }
}
