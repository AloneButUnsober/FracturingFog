// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SlitCascadeCalculator.cs
//
// Slit cascade (#1569, double-slit epic #1565 S3; design:
// Docs/Technical/DoubleSlit-Fractal-RnD.md §4). A recursive aperture: every
// slit is replaced by N smaller children at scale r, and each child transmits
// the share of power that the parent's two-slit fringe pattern puts at its
// position — "the fringe becomes the next slit". The image is the far-field
// (Fraunhofer) diffraction pattern of that aperture, or the aperture itself.
//
// Aperture (units: the level-0 aperture is the square [−1, 1]²). Level-n
// children of a slit centred at 0 sit at offsets rⁿ·bᵢ, with
//
//     bᵢ = (1 − r)·eᵢ,  eᵢ = Line: ((2i/(N−1)) − 1, 0)
//                            Ring: (cos 2πi/N, sin 2πi/N)
//
// and carry amplitude √pᵢ, where pᵢ is the parent's two-slit intensity at the
// child's x position (fringes run along x), normalised to Σ pᵢ = 1:
//
//     I₀(x) = sinc²(π·w·x/λ) · cos²(π·d·x/λ)       (sinc t = sin t / t)
//
// d = slit separation, w = slit width, λ = wavelength (the screen distance is
// folded into λ: only λ·L appears). A child on a dark fringe gets pᵢ = 0 and
// drops out — dark fringes delete branches. With a symmetric slit pair (N = 2)
// both weights are equal and the cascade is a plain Cantor set; N ≥ 3 is needed
// for the weights to differ (design §4.1).
//
// Far field. Because every level is a scaled copy, the Fourier transform
// factorises exactly into one array factor per level times the transform of
// the leaf square (half-size r^D) — the structure-factor result for Cantor
// gratings (Bak, Kim, Kim, Soh & Yee, arXiv:physics/9802007; Allain & Cloitre,
// Phys. Rev. B 33, 1986):
//
//     A(u) = S(r^D·u) · Π_{n=0}^{D−1} F(rⁿ·u),   F(u) = Σᵢ √pᵢ·e^{−2πi·bᵢ·u}
//     S(u) = sinc(2π·u_x) · sinc(2π·u_y)
//
// O(D·N) per pixel — no aperture sampling, no FFT. The image is normalised by
// I(0) = (Σ√pᵢ)^{2D}, so I(u)/I(0) ∈ [0, 1] (every coefficient is positive), and
// shown on a log scale over SlitCascadeDynamicRange decades.
//
// Views (SlitCascadeView): Intensity (log I/I₀), Phase (arg A in turns), and
// Aperture — the weighted measure itself: a point's address is found by
// walking the levels, and its brightness is Π pᵢ/max p over the levels (log
// scale), 0 outside the aperture.
//
// Pixel ↔ plane: the standard (4/W)/Zoom pitch round (CenterX, CenterY); the
// screen view's plane is the spatial frequency u, the aperture view's is x.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class SlitCascadeCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    /// <summary>The view's value mapped onto [0, MaxIterations] (log intensity,
    /// phase turns or log aperture mass); 0 = dark / outside. Drives every
    /// SmoothBuffer theme and the Relief-3D height path.</summary>
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Zoom { get; set; } = 0.25;
    public int MaxIterations { get; set; } = 400;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public const int MinChildren = 2, MaxChildren = 16, MinDepth = 1, MaxDepth = 12;

    public SlitCascadeCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    /// <summary>The resolved cascade: child offsets bᵢ and power shares pᵢ (Σ = 1).</summary>
    public readonly record struct Cascade(double Ratio, int Depth, double[] Bx, double[] By, double[] P);

    /// <summary>Two-slit fringe intensity I₀(x) = sinc²(πwx/λ)·cos²(πdx/λ) (header).</summary>
    public static double FringeIntensity(double x, double separation, double width, double wavelength)
    {
        double s = Sinc(Math.PI * width * x / wavelength);
        double c = Math.Cos(Math.PI * separation * x / wavelength);
        return s * s * c * c;
    }

    /// <summary>Builds the cascade from the parameters (clamped to their ranges).</summary>
    public static Cascade Build(FractalParameters p)
    {
        int n = Math.Clamp(p.SlitCascadeChildren, MinChildren, MaxChildren);
        int depth = Math.Clamp(p.SlitCascadeDepth, MinDepth, MaxDepth);
        double r = Math.Clamp(Finite(p.SlitCascadeRatio, 0.3), 0.01, 0.5);
        double lambda = Math.Max(1e-6, Finite(p.SlitCascadeWavelength, 0.6));
        double d = Finite(p.SlitCascadeSeparation, 1.0), w = Math.Max(0.0, Finite(p.SlitCascadeWidth, 0.2));
        var bx = new double[n]; var by = new double[n]; var pw = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double ex, ey;
            if (p.SlitCascadeLayout == SlitCascadeLayout.Ring) { double a = 2.0 * Math.PI * i / n; ex = Math.Cos(a); ey = Math.Sin(a); }
            else { ex = 2.0 * i / (n - 1) - 1.0; ey = 0.0; }
            bx[i] = (1.0 - r) * ex; by[i] = (1.0 - r) * ey;
            pw[i] = FringeIntensity(bx[i], d, w, lambda);
            sum += pw[i];
        }
        // Every child on a dark fringe: no light gets through; fall back to equal
        // shares so the image stays defined (a plain Cantor set).
        for (int i = 0; i < n; i++) pw[i] = sum > 1e-300 ? pw[i] / sum : 1.0 / n;
        return new Cascade(r, depth, bx, by, pw);
    }

    /// <summary>Far-field amplitude A(u) (header), unnormalised: A(0) = (Σ√pᵢ)^D.</summary>
    public static Complex Amplitude(in Cascade c, double ux, double uy)
    {
        double re = 1.0, im = 0.0, scale = 1.0;
        int n = c.P.Length;
        for (int level = 0; level < c.Depth; level++)
        {
            double fr = 0.0, fi = 0.0;
            for (int i = 0; i < n; i++)
            {
                double a = c.P[i];
                if (a <= 0.0) continue;
                double amp = Math.Sqrt(a);
                double ph = -2.0 * Math.PI * scale * (c.Bx[i] * ux + c.By[i] * uy);
                fr += amp * Math.Cos(ph); fi += amp * Math.Sin(ph);
            }
            double nr = re * fr - im * fi; im = re * fi + im * fr; re = nr;
            scale *= c.Ratio;
        }
        double leaf = Sinc(2.0 * Math.PI * scale * ux) * Sinc(2.0 * Math.PI * scale * uy);
        return new Complex(re * leaf, im * leaf);
    }

    /// <summary>Aperture mass at x relative to the brightest leaf, in (0, 1]; 0 outside.</summary>
    public static double ApertureMass(in Cascade c, double x, double y)
    {
        double pMax = 0.0;
        foreach (double q in c.P) pMax = Math.Max(pMax, q);
        if (pMax <= 0.0) return 0.0;
        double mass = 1.0;
        for (int level = 0; level < c.Depth; level++)
        {
            // Child squares have half-size r in the parent's frame [−1, 1]².
            int hit = -1;
            for (int i = 0; i < c.P.Length; i++)
                if (Math.Abs(x - c.Bx[i]) <= c.Ratio && Math.Abs(y - c.By[i]) <= c.Ratio && c.P[i] > 0.0) { hit = i; break; }
            if (hit < 0) return 0.0;
            mass *= c.P[hit] / pMax;
            x = (x - c.Bx[hit]) / c.Ratio; y = (y - c.By[hit]) / c.Ratio;
        }
        return mass;
    }

    public void Calculate(CancellationToken ct)
    {
        int width = Width, height = Height;
        if (width < 1 || height < 1) return;
        var fp = FractalParameters;
        var cascade = Build(fp);
        var view = fp.SlitCascadeView;
        double decades = Math.Clamp(Finite(fp.SlitCascadeDynamicRange, 6.0), 0.5, 30.0);
        double sumSqrt = 0.0;
        foreach (double q in cascade.P) sumSqrt += Math.Sqrt(q);
        double logI0 = 2.0 * cascade.Depth * Math.Log10(sumSqrt);

        double pitch = (4.0 / Math.Max(1, width)) / Math.Max(1e-300, Zoom);
        double cx = CenterX, cy = CenterY;
        int maxIt = Math.Max(16, MaxIterations);
        var cm = ColorMap;
        cm.MaxIterations = maxIt;
        uint inSet = cm.InSetColor;
        float[] smooth = SmoothBuffer; uint[] color = ColorBuffer;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            double py = cy + (y - height * 0.5) * pitch;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double px = cx + (x - width * 0.5) * pitch;
                double t;   // value in [0, 1]; ≤ 0 = dark
                if (view == SlitCascadeView.Aperture)
                {
                    double m = ApertureMass(cascade, px, py);
                    t = m > 0.0 ? 1.0 + Math.Log10(m) / decades : 0.0;
                }
                else
                {
                    var a = Amplitude(cascade, px, py);
                    if (view == SlitCascadeView.Phase)
                    {
                        t = a.Phase / (2.0 * Math.PI);
                        t -= Math.Floor(t);
                        t = Math.Max(t, 1e-9);   // phase 0 is a colour, not "dark"
                    }
                    else
                    {
                        double mag2 = a.Real * a.Real + a.Imaginary * a.Imaginary;
                        t = mag2 > 0.0 ? 1.0 + (Math.Log10(mag2) - logI0) / decades : 0.0;
                    }
                }
                int idx = row + x;
                if (!(t > 0.0)) { smooth[idx] = 0f; color[idx] = inSet; continue; }
                float s = (float)(Math.Min(1.0, t) * maxIt);
                smooth[idx] = s;
                color[idx] = (uint)cm.Map(s, 0f, maxIt);
            }
        });
    }

    private static double Sinc(double t) => Math.Abs(t) < 1e-8 ? 1.0 - t * t / 6.0 : Math.Sin(t) / t;
    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
}
