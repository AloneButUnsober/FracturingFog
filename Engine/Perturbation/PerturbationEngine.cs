// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — the generic perturbation engine, double-δ tier. One reference orbit in
// BigFloat at the view's precision (stored as doubles: |Z| stays below the bailout), a per-pixel δ
// in double, the plan's bilinear approximation, and the glitch handling the plan allows:
//   • parameter plane (critical point z₀ = 0): Zhuoran's rebase — when |Z_m + δ| < |δ| or the
//     reference runs out, δ ← Z_m + δ and m ← 0;
//   • otherwise (Julia: z₀ is the pixel; or a formula whose critical point is not z₀ = 0, #1908):
//     the plan's glitch test (Pauldelbrot's |Z_m + δ|² < τ·|Z_m|², generalised to |f′(z)|² <
//     τ·|f′(Z)|²) and the reference running out mark the pixel glitched; up to MaxReferences secondary
//     references, each at the glitched pixel that lasted longest, re-render the glitched pixels; any
//     left are iterated directly in BigFloat.
// Sources (Docs/Resources-Bibliography.md): K. I. Martin (perturbation), Pauldelbrot (glitch test),
// Zhuoran (rebasing, BLA). Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §3, §4.1.
// The double δ holds to a pixel scale of ~1e-300 (zoom ~1e290); the float-exp tier and a log zoom
// past double come later (epic #1253).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

/// <summary>What one frame did (diagnostics, tests).</summary>
public readonly record struct PerturbationStats(
    int ReferenceLength, int References, long Rebases, long BlaSkips, int GlitchedPixels, int DirectPixels);

public static class PerturbationEngine
{
    /// <summary>Pauldelbrot's glitch tolerance τ on |z|² / |Z|² (dynamic plane).</summary>
    public const double GlitchTolerance2 = 1e-6;

    /// <summary>The BLA tolerance ε the engine uses by default. Every pixel here resumes after a rebase
    /// or a glitch with BLA, the case #1613 measured for Mandelbrot's rows: at the default 1e-6 a
    /// period-448 minibrot was ~2.5 % off no-BLA rebasing, at 1e-10 ~0.2 % (MandelbrotCalculator.
    /// QdBandBlaTolerance). Here too: at 1e-6 z² + c at p448 1e30 was 2.8 % off MandelbrotCalculator and
    /// Julia's derivative off on ~10 % of pixels; with BLA off both were exact.</summary>
    public const double DefaultBlaEpsilon = 1e-10;

    /// <summary>Secondary references tried before the remaining glitched pixels are iterated directly.</summary>
    public const int MaxReferences = 8;

    /// <summary>Receives one finished pixel: index, iterations, final z, final derivative.</summary>
    public delegate void PixelSink(int idx, int iter, double zr, double zi, double dvr, double dvi);

    private sealed class Reference
    {
        public double[] Zr = Array.Empty<double>(), Zi = Array.Empty<double>();
        public int Len;           // valid Z_0 .. Z_{Len−1}
        public bool HasEsc;       // the reference escaped: EscR / EscI is Z_{Len} (outside the bailout)
        public double EscR, EscI;
        public double Cr, Ci;     // the reference's c, rounded to double (a coefficient in the δ step)
        public BlaTable? Bla;
        public Bla2Table? Bla2;   // #1909 — a real (non-conformal) plan's matrix BLA
        public double OffX, OffY; // the reference's offset from the view centre, in complex units
    }

    /// <summary>Render a frame. Pixel (x, y) sits at the centre + ((x − w/2)·scale, (y − h/2)·scale)
    /// (EscapeTimeCalculator's convention), moved by <paramref name="warp"/> when given.</summary>
    public static PerturbationStats Render(
        IPerturbationPlan plan, ExactCentre centre, double scale, int width, int height, int maxIter,
        PixelSink sink, CancellationToken ct,
        int[]? rowMaxIter = null, Func<int, bool>? marked = null,
        bool useBla = true, double blaEpsilon = DefaultBlaEpsilon,
        Func<double, double, (double X, double Y)>? warp = null)
    {
        int prec = centre.Precision;
        var (jcr, jci) = plan is PowerPlan pp && !plan.ParameterPlane ? pp.JuliaC(prec) : (BigFloat.Zero(prec), BigFloat.Zero(prec));
        double dcMax = Math.Sqrt(width * width + height * height) * 0.5 * scale;

        Reference Build(double offX, double offY)
        {
            var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(offX, prec), prec);
            var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(offY, prec), prec);
            var r = BuildReference(plan, cr, ci, jcr, jci, prec, maxIter, ct);
            r.OffX = offX; r.OffY = offY;
            (r.Cr, r.Ci) = plan.ParameterPlane ? (cr.ToDouble(), ci.ToDouble()) : (jcr.ToDouble(), jci.ToDouble());
            if (useBla && r.Len > 2 && plan is IRealPerturbationPlan rp)
            {
                var level0 = new Bla2[r.Len];
                for (int n = 0; n < r.Len; n++) level0[n] = rp.Level0R2(r.Zr[n], r.Zi[n], r.Cr, r.Ci, blaEpsilon, dcMax);
                r.Bla2 = new Bla2Table(level0, r.Len, dcMax);
            }
            else if (useBla && r.Len > 2)
            {
                var level0 = new Bla[r.Len];
                for (int n = 0; n < r.Len; n++) level0[n] = plan.Level0(r.Zr[n], r.Zi[n], r.Cr, r.Ci, blaEpsilon);
                r.Bla = new BlaTable(level0, r.Len, dcMax, blaEpsilon);
            }
            return r;
        }

        (double X, double Y) Offset(int x, int y)
        {
            double ox = (x - width * 0.5) * scale, oy = (y - height * 0.5) * scale;
            return warp != null ? warp(ox, oy) : (ox, oy);
        }

        long rebases = 0, skips = 0;
        // Glitched pixels (dynamic plane): index → the iteration it glitched at.
        var glitched = new ConcurrentBag<(int Idx, int Iter)>();

        var main = Build(0, 0);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
            long rb = 0, sk = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (marked != null && !marked(idx)) continue;
                var (ox, oy) = Offset(x, y);
                int g = PixelAny(plan, main, ox, oy, rowMax, idx, sink, ref rb, ref sk);
                if (g >= 0) glitched.Add((idx, g));
            }
            Interlocked.Add(ref rebases, rb);
            Interlocked.Add(ref skips, sk);
        });

        int glitchedCount = glitched.Count, refs = 1, direct = 0;
        var pending = new List<(int Idx, int Iter)>(glitched);
        while (pending.Count > 0 && refs < MaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            // The next reference: the glitched pixel that lasted longest (its orbit covers the others'),
            // the lowest index on a tie, so a frame renders the same every time.
            var best = pending[0];
            foreach (var p in pending)
                if (p.Iter > best.Iter || (p.Iter == best.Iter && p.Idx < best.Idx)) best = p;
            var (bx, by) = Offset(best.Idx % width, best.Idx / width);
            var reference = Build(bx, by);
            refs++;
            var next = new ConcurrentBag<(int, int)>();
            Parallel.ForEach(pending, po, p =>
            {
                int y = p.Idx / width;
                int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
                var (ox, oy) = Offset(p.Idx % width, y);
                long rb = 0, sk = 0;
                int g = PixelAny(plan, reference, ox, oy, rowMax, p.Idx, sink, ref rb, ref sk);
                Interlocked.Add(ref rebases, rb);
                Interlocked.Add(ref skips, sk);
                if (g >= 0) next.Add((p.Idx, g));
            });
            pending = new List<(int Idx, int Iter)>(next);
        }

        // Whatever no reference could carry: direct iteration in BigFloat.
        if (pending.Count > 0)
        {
            Parallel.ForEach(pending, po, p =>
            {
                int y = p.Idx / width;
                int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
                var (ox, oy) = Offset(p.Idx % width, y);
                Direct(plan, centre, jcr, jci, prec, ox, oy, rowMax, p.Idx, sink, ct);
            });
            direct = pending.Count;
        }
        return new PerturbationStats(main.Len, refs, rebases, skips, glitchedCount, direct);
    }

    private static Reference BuildReference(IPerturbationPlan plan, BigFloat cr, BigFloat ci, BigFloat jcr, BigFloat jci,
                                            int prec, int maxIter, CancellationToken ct)
    {
        // Parameter plane: z₀ = 0, c = the reference point. Dynamic plane: z₀ = the reference point, c = Julia's.
        BigFloat zr, zi, kr, ki;
        if (plan.ParameterPlane) { zr = BigFloat.Zero(prec); zi = BigFloat.Zero(prec); kr = cr; ki = ci; }
        else { zr = cr; zi = ci; kr = jcr; ki = jci; }
        // Z_0 .. Z_{Len−1}, all inside the bailout. The escaped point is not kept: a pixel that
        // stepped onto it would carry |δ| ≈ |Z| ≥ the bailout while its own z may be small — the
        // large-minus-large loss perturbation must avoid. It rebases (or glitches) at Len − 1
        // instead, as MandelbrotCalculator's reference ends. (A dynamic-plane pixel may take the one
        // step onto it when that step escapes: see Pixel.)
        var r = new Reference { Zr = new double[maxIter + 1], Zi = new double[maxIter + 1] };
        int n = 0;
        for (; n <= maxIter; n++)
        {
            if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) { r.HasEsc = true; r.EscR = a; r.EscI = b; break; }
            r.Zr[n] = a; r.Zi[n] = b;
            if (n == maxIter) { n++; break; }
            plan.RefStep(ref zr, ref zi, kr, ki, prec);
        }
        r.Len = n;
        return r;
    }

    private static int PixelAny(IPerturbationPlan plan, Reference r, double ox, double oy, int maxIter, int idx,
                                PixelSink sink, ref long rebases, ref long skips)
        => plan is IRealPerturbationPlan rp
            ? PixelR2(rp, r, ox, oy, maxIter, idx, sink, ref rebases, ref skips)
            : Pixel(plan, r, ox, oy, maxIter, idx, sink, ref rebases, ref skips);

    // One pixel against a reference. Returns −1 when the pixel finished (sink called), else the
    // iteration it glitched at (dynamic plane only).
    private static int Pixel(IPerturbationPlan plan, Reference r, double ox, double oy, int maxIter, int idx,
                             PixelSink sink, ref long rebases, ref long skips)
    {
        bool pp = plan.ParameterPlane;
        double er, ei, dr, di;
        if (pp) { er = ox - r.OffX; ei = oy - r.OffY; dr = 0; di = 0; }
        else { er = 0; ei = 0; dr = ox - r.OffX; di = oy - r.OffY; }
        var (dvr, dvi) = plan.InitialDerivative;   // the derivative of the full orbit
        bool rebase = pp && plan.CanRebase;
        double pcr = pp ? r.Cr + er : r.Cr, pci = pp ? r.Ci + ei : r.Ci;   // the pixel's c, to double
        double zr = 0, zi = 0;
        double bail = plan.Bailout2;
        var bla = r.Bla;
        int m = 0, iter = 0;
        for (; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                int bi = bla.Lookup(m, dr * dr + di * di, Math.Min(r.Len - 1, m + (maxIter - iter)));
                if (bi >= 0)
                {
                    ref readonly var e = ref bla.Data[bi];
                    if (e.L >= 2)
                    {
                        double ar = e.ARe, ai = e.AIm, br = e.BRe, bim = e.BIm;
                        double ndr = ar * dr - ai * di + (br * er - bim * ei);
                        double ndi = ar * di + ai * dr + (br * ei + bim * er);
                        // d′ = A·d + B (B is ∂/∂c of the skipped steps; 0 in the dynamic plane) — #1782.
                        double nvr = ar * dvr - ai * dvi + br, nvi = ar * dvi + ai * dvr + bim;
                        dr = ndr; di = ndi; dvr = nvr; dvi = nvi;
                        m += e.L;
                        iter += e.L - 1;
                        skips++;
                        continue;
                    }
                }
            }

            double Zr = r.Zr[m], Zi = r.Zi[m];
            zr = Zr + dr; zi = Zi + di;
            double z2 = zr * zr + zi * zi;
            if (z2 >= bail) break;
            plan.DerivStep(zr, zi, pcr, pci, ref dvr, ref dvi);

            if (rebase)
            {
                if (z2 < dr * dr + di * di || m + 1 >= r.Len)
                {
                    dr = zr; di = zi; Zr = 0; Zi = 0; m = 0;   // rebase: Z₀ = 0 (the critical point)
                    rebases++;
                }
            }
            else if (m + 1 >= r.Len || plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
            {
                // The reference ends here. A pixel near it escapes with it: one δ step onto the
                // escaped point, kept only when it escapes (then |δ| ≪ |Z| and nothing cancelled).
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
                {
                    double tr = dr, ti = di;
                    plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref tr, ref ti, er, ei);
                    double ezr = r.EscR + tr, ezi = r.EscI + ti;
                    if (ezr * ezr + ezi * ezi >= bail)
                    {
                        sink(idx, iter + 1, ezr, ezi, dvr, dvi);
                        return -1;
                    }
                }
                return iter;   // glitched: another reference, or direct iteration, takes it
            }
            plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref dr, ref di, er, ei);
            m++;
        }
        // In set: z after the last step, as the direct loop leaves it.
        if (iter >= maxIter && m < r.Len) { zr = r.Zr[m] + dr; zi = r.Zi[m] + di; }
        sink(idx, iter, zr, zi, dvr, dvi);
        return -1;
    }

    // #1909 — Pixel for a real (non-conformal) plan: δ, ε as R² columns, the matrix BLA, and the
    // orbit's 2×2 Jacobian (the sink gets its first column, ∂z/∂Re(pixel)). The parameter plane only
    // (R2Plan's), with the same rebase / glitch / escape-step rules as Pixel.
    private static int PixelR2(IRealPerturbationPlan plan, Reference r, double ox, double oy, int maxIter, int idx,
                               PixelSink sink, ref long rebases, ref long skips)
    {
        double er = ox - r.OffX, ei = oy - r.OffY, dr = 0, di = 0;
        var (ivr, ivi) = plan.InitialDerivative;
        var jac = RealMat2.OfComplex(ivr, ivi);
        bool rebase = plan.CanRebase;
        double pcr = r.Cr + er, pci = r.Ci + ei;
        double zr = 0, zi = 0;
        double bail = plan.Bailout2;
        var bla = r.Bla2;
        int m = 0, iter = 0;
        for (; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                int bi = bla.Lookup(m, dr * dr + di * di, Math.Min(r.Len - 1, m + (maxIter - iter)));
                if (bi >= 0)
                {
                    ref readonly var e = ref bla.Data[bi];
                    if (e.L >= 2)
                    {
                        var A = e.A; var B = e.B;
                        double ndr = A.M11 * dr + A.M12 * di + (B.M11 * er + B.M12 * ei);
                        double ndi = A.M21 * dr + A.M22 * di + (B.M21 * er + B.M22 * ei);
                        jac = A * jac + B;   // J′ = A·J + B
                        dr = ndr; di = ndi;
                        m += e.L;
                        iter += e.L - 1;
                        skips++;
                        continue;
                    }
                }
            }

            double Zr = r.Zr[m], Zi = r.Zi[m];
            zr = Zr + dr; zi = Zi + di;
            double z2 = zr * zr + zi * zi;
            if (z2 >= bail) break;
            plan.JacobianStep(zr, zi, pcr, pci, ref jac);

            if (rebase)
            {
                if (z2 < dr * dr + di * di || m + 1 >= r.Len)
                {
                    dr = zr; di = zi; Zr = 0; Zi = 0; m = 0;
                    rebases++;
                }
            }
            else if (m + 1 >= r.Len || plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
            {
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
                {
                    double tr = dr, ti = di;
                    plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref tr, ref ti, er, ei);
                    double ezr = r.EscR + tr, ezi = r.EscI + ti;
                    if (ezr * ezr + ezi * ezi >= bail)
                    {
                        sink(idx, iter + 1, ezr, ezi, jac.M11, jac.M21);
                        return -1;
                    }
                }
                return iter;
            }
            plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref dr, ref di, er, ei);
            m++;
        }
        if (iter >= maxIter && m < r.Len) { zr = r.Zr[m] + dr; zi = r.Zi[m] + di; }
        sink(idx, iter, zr, zi, jac.M11, jac.M21);
        return -1;
    }

    // Direct iteration in BigFloat (the last resort for glitched pixels). The derivative runs in
    // double on the rounded z, as the perturbed path's does.
    private static void Direct(IPerturbationPlan plan, ExactCentre centre, BigFloat jcr, BigFloat jci, int prec,
                               double ox, double oy, int maxIter, int idx, PixelSink sink, CancellationToken ct)
    {
        var pr = BigFloat.Add(centre.Re, BigFloat.FromDouble(ox, prec), prec);
        var pi = BigFloat.Add(centre.Im, BigFloat.FromDouble(oy, prec), prec);
        BigFloat zr, zi, kr, ki;
        if (plan.ParameterPlane) { zr = BigFloat.Zero(prec); zi = BigFloat.Zero(prec); kr = pr; ki = pi; }
        else { zr = pr; zi = pi; kr = jcr; ki = jci; }
        var (dvr, dvi) = plan.InitialDerivative;
        var rp = plan as IRealPerturbationPlan;
        var jac = RealMat2.OfComplex(dvr, dvi);   // #1909 — a real plan's 2×2 Jacobian
        double a = 0, b = 0, pcr = kr.ToDouble(), pci = ki.ToDouble();
        int iter = 0;
        for (; iter < maxIter; iter++)
        {
            if ((iter & 1023) == 0) ct.ThrowIfCancellationRequested();
            a = zr.ToDouble(); b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) break;
            if (rp != null) rp.JacobianStep(a, b, pcr, pci, ref jac);
            else plan.DerivStep(a, b, pcr, pci, ref dvr, ref dvi);
            plan.RefStep(ref zr, ref zi, kr, ki, prec);
        }
        if (iter >= maxIter) { a = zr.ToDouble(); b = zi.ToDouble(); }
        if (rp != null) { dvr = jac.M11; dvi = jac.M21; }
        sink(idx, iter, a, b, dvr, dvi);
    }
}
