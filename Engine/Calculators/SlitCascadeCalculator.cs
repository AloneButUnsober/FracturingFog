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
// Angular momentum sets the filament (#1572, epic S5; design §6). Light with
// phase e^{iℓφ} carries orbital angular momentum and has a dark phase
// singularity on its axis (Allen, Beijersbergen, Spreeuw & Woerdman, Phys.
// Rev. A 45, 1992). On the Ring layout each level-n child at angle φᵢ = 2πi/N
// gets the constant phase e^{iℓₙφᵢ}, so the level's array factor becomes
//
//     F(u) = Σᵢ √pᵢ · e^{iℓₙφᵢ} · e^{−2πi·bᵢ·u}
//
// and the exact product of §4.2 still holds. With ℓₙ = SlitCascadeCharge and
// SlitCascadeChargePattern: Constant ℓₙ = ℓ, Alternating ℓₙ = (−1)ⁿℓ, Ramp
// ℓₙ = (n + 1)ℓ. Near u = 0 the Jacobi–Anger expansion of F keeps only the
// Bessel orders m ≡ −ℓₙ (mod N), so with equal shares each level has a vortex
// of charge ℓₙ reduced into (−N/2, N/2] at the centre (none when the reduced
// charge is exactly N/2: two orders tie), and the windings add over levels.
// Unequal shares move the vortices off centre but keep their charges. A slit
// pair cannot carry a vortex (e^{iℓφ} = ±1), and the Line layout has no angle,
// so the charge applies to Ring only. Closest prior art: the devil's vortex
// lens (Furlan, Giménez, Calatayud & Monsoriu, Opt. Express 17, 2009), a
// Cantor-function phase plus a spiral phase making a self-similar chain of
// vortices along the axis; the cascade nests them in the transverse plane.
//
// Probability sets the dimension (#1571, epic S4; design §5). The aperture
// measure is self-similar with equal ratios r, so its generalized (Rényi)
// dimensions (Hentschel & Procaccia, Physica D 8, 1983) and f(α) spectrum
// (Halsey, Jensen, Kadanoff, Procaccia & Shraiman, Phys. Rev. A 33, 1986) are
// closed form:
//
//     D_q = log Σ pᵢ^q / ((q − 1)·log r),   D₁ = Σ pᵢ log pᵢ / log r,
//     D₀ = log N_eff / log(1/r)             (N_eff = children with pᵢ > 0)
//
// so λ, d and w set the fringes, the fringes set pᵢ, and pᵢ sets D_q. The
// status bar shows D₀, D₁, D₂ and D_q (q = SlitCascadeQ). The formulas assume
// the children don't overlap (the open set condition); when they do, the
// label says so, since the numbers are then only similarity dimensions.
//
// Two more aperture views colour each point by its address i₁…i_D:
//   HolderAlpha: α = log(p_{i₁}⋯p_{i_D}) / log(r^D), mapped from [α_min, α_max]
//                (α_min = log p_max / log r, α_max = log p_min / log r);
//   Spectrum:    f(α) = log(D! / Π kᵢ!) / (D·log(1/r)), kᵢ = how often child i
//                occurs in the address — the exact depth-D count of leaves that
//                share this point's α, as a dimension; mapped over [0, D₀].
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
    public readonly record struct Cascade(double Ratio, int Depth, double[] Bx, double[] By, double[] P,
        double[] Phi, int[] Charges);

    /// <summary>The per-level charges ℓₙ for a charge ℓ and pattern (#1572).</summary>
    public static int[] LevelCharges(int ell, SlitCascadeChargePattern pattern, int depth)
    {
        var q = new int[depth];
        for (int n = 0; n < depth; n++)
            q[n] = pattern switch
            {
                SlitCascadeChargePattern.Alternating => (n % 2 == 0) ? ell : -ell,
                SlitCascadeChargePattern.Ramp => (n + 1) * ell,
                _ => ell,
            };
        return q;
    }

    /// <summary>A charge ℓ reduced into (−N/2, N/2]: the vortex an N-child ring
    /// with phases e^{iℓφᵢ} actually carries (#1572).</summary>
    public static int ReducedCharge(int ell, int n)
    {
        int m = ((ell % n) + n) % n;
        return 2 * m > n ? m - n : m;
    }

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
        // A share below 1e-12 of the brightest is a dark fringe: cos(π/2) evaluates
        // to ~6e-17, not 0, and would otherwise keep a dead branch in N_eff (#1571).
        double pPeak = 0;
        foreach (double v in pw) pPeak = Math.Max(pPeak, v);
        for (int i = 0; i < n; i++) if (pw[i] < 1e-12 * pPeak) { sum -= pw[i]; pw[i] = 0.0; }
        // Every child on a dark fringe: no light gets through; fall back to equal
        // shares so the image stays defined (a plain Cantor set).
        for (int i = 0; i < n; i++) pw[i] = sum > 1e-300 ? pw[i] / sum : 1.0 / n;
        // #1572 — vortex phases need an angle per child: Ring only.
        bool ring = p.SlitCascadeLayout == SlitCascadeLayout.Ring;
        var phi = new double[n];
        if (ring) for (int i = 0; i < n; i++) phi[i] = 2.0 * Math.PI * i / n;
        int ell = Math.Clamp(p.SlitCascadeCharge, -64, 64);
        var charges = ring ? LevelCharges(ell, p.SlitCascadeChargePattern, depth) : new int[depth];
        return new Cascade(r, depth, bx, by, pw, phi, charges);
    }

    /// <summary>Far-field amplitude A(u) (header), unnormalised: A(0) = (Σ√pᵢ)^D.</summary>
    public static Complex Amplitude(in Cascade c, double ux, double uy)
    {
        double re = 1.0, im = 0.0, scale = 1.0;
        int n = c.P.Length;
        for (int level = 0; level < c.Depth; level++)
        {
            double fr = 0.0, fi = 0.0;
            int charge = c.Charges[level];
            for (int i = 0; i < n; i++)
            {
                double a = c.P[i];
                if (a <= 0.0) continue;
                double amp = Math.Sqrt(a);
                double ph = -2.0 * Math.PI * scale * (c.Bx[i] * ux + c.By[i] * uy);
                if (charge != 0) ph += charge * c.Phi[i];   // #1572 vortex phase
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
        var counts = new int[c.P.Length];
        if (!Address(c, x, y, counts)) return 0.0;
        double mass = 1.0;
        for (int i = 0; i < counts.Length; i++)
            for (int k = 0; k < counts[i]; k++) mass *= c.P[i] / pMax;
        return mass;
    }

    /// <summary>Walks the levels from (x, y): true when the point lies in a leaf,
    /// with <paramref name="counts"/>[i] = how many levels chose child i (#1571).</summary>
    public static bool Address(in Cascade c, double x, double y, int[] counts)
    {
        Array.Clear(counts);
        for (int level = 0; level < c.Depth; level++)
        {
            // Child squares have half-size r in the parent's frame [−1, 1]².
            int hit = -1;
            for (int i = 0; i < c.P.Length; i++)
                if (Math.Abs(x - c.Bx[i]) <= c.Ratio && Math.Abs(y - c.By[i]) <= c.Ratio && c.P[i] > 0.0) { hit = i; break; }
            if (hit < 0) return false;
            counts[hit]++;
            x = (x - c.Bx[hit]) / c.Ratio; y = (y - c.By[hit]) / c.Ratio;
        }
        return true;
    }

    /// <summary>Generalized dimension D_q of the cascade measure (header); q = 1
    /// is the information dimension, q = 0 the box dimension of the support.</summary>
    public static double GeneralizedDimension(in Cascade c, double q)
    {
        double logR = Math.Log(c.Ratio);
        if (Math.Abs(q - 1.0) < 1e-12)
        {
            double s = 0.0;
            foreach (double p in c.P) if (p > 0.0) s += p * Math.Log(p);
            return s / logR;
        }
        double sum = 0.0;
        foreach (double p in c.P) if (p > 0.0) sum += Math.Pow(p, q);
        return Math.Log(sum) / ((q - 1.0) * logR);
    }

    /// <summary>Coarse Hölder exponent α of an address (header).</summary>
    public static double Alpha(in Cascade c, int[] counts)
    {
        double logMass = 0.0;
        for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) logMass += counts[i] * Math.Log(c.P[i]);
        return logMass / (c.Depth * Math.Log(c.Ratio));
    }

    /// <summary>Depth-D spectrum f(α) of an address: log of the number of leaves
    /// with the same child counts, as a dimension (header).</summary>
    public static double Spectrum(in Cascade c, int[] counts)
    {
        double logMultinomial = LogFactorial(c.Depth);
        foreach (int k in counts) logMultinomial -= LogFactorial(k);
        return logMultinomial / (c.Depth * Math.Log(1.0 / c.Ratio));
    }

    /// <summary>True when two children's squares overlap (the D_q formulas then
    /// give only similarity dimensions).</summary>
    public static bool ChildrenOverlap(in Cascade c)
    {
        for (int i = 0; i < c.P.Length; i++)
            for (int j = i + 1; j < c.P.Length; j++)
                if (c.P[i] > 0 && c.P[j] > 0 && Math.Abs(c.Bx[i] - c.Bx[j]) < 2 * c.Ratio && Math.Abs(c.By[i] - c.By[j]) < 2 * c.Ratio)
                    return true;
        return false;
    }

    /// <summary>Status-bar text: D₀, D₁, D₂ and D_q for the current cascade (#1571).</summary>
    public string? LastPrecisionLabel { get; private set; }

    private static double LogFactorial(int n)
    {
        double s = 0.0;
        for (int k = 2; k <= n; k++) s += Math.Log(k);
        return s;
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

        // #1571 — the dimension readout, and the α / f(α) colour ranges.
        double qOrder = Finite(fp.SlitCascadeQ, 3.0);
        double d0 = GeneralizedDimension(cascade, 0), d1 = GeneralizedDimension(cascade, 1), d2 = GeneralizedDimension(cascade, 2);
        LastPrecisionLabel = FormattableString.Invariant(
            $"D0 {d0:F3}  D1 {d1:F3}  D2 {d2:F3}  Dq(q={qOrder:G3}) {GeneralizedDimension(cascade, qOrder):F3}")
            + (ChildrenOverlap(cascade) ? "  (children overlap: similarity dimensions)" : "");
        double pMaxAll = 0.0, pMinAll = double.MaxValue;
        foreach (double p in cascade.P) if (p > 0.0) { pMaxAll = Math.Max(pMaxAll, p); pMinAll = Math.Min(pMinAll, p); }
        double alphaMin = Math.Log(pMaxAll) / Math.Log(cascade.Ratio), alphaMax = Math.Log(pMinAll) / Math.Log(cascade.Ratio);
        double alphaSpan = alphaMax - alphaMin;

        double pitch = (4.0 / Math.Max(1, width)) / Math.Max(1e-300, Zoom);
        double cx = CenterX, cy = CenterY;
        int maxIt = Math.Max(16, MaxIterations);
        var cm = ColorMap;
        cm.MaxIterations = maxIt;
        uint inSet = cm.InSetColor;
        float[] smooth = SmoothBuffer; uint[] color = ColorBuffer;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            var counts = new int[cascade.P.Length];
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
                else if (view is SlitCascadeView.HolderAlpha or SlitCascadeView.Spectrum)
                {
                    if (!Address(cascade, px, py, counts)) t = 0.0;
                    else if (view == SlitCascadeView.HolderAlpha)
                        t = Math.Max(1e-9, alphaSpan > 1e-12 ? (Alpha(cascade, counts) - alphaMin) / alphaSpan : 0.5);
                    else
                        t = Math.Max(1e-9, d0 > 1e-12 ? Spectrum(cascade, counts) / d0 : 1.0);
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
                // #1591 — a phase a hair below 1 (≡ 0) rounds up to maxIt, which themes
                // read as "in the set" (black): wrap it to the bottom of the wheel.
                if (view == SlitCascadeView.Phase && s >= maxIt) s = (float)(1e-9 * maxIt);
                smooth[idx] = s;
                color[idx] = (uint)cm.Map(s, 0f, maxIt);
            }
        });
    }

    private static double Sinc(double t) => Math.Abs(t) < 1e-8 ? 1.0 - t * t / 6.0 : Math.Sin(t) / t;
    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
}
