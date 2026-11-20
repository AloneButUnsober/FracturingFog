// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/DeepGeneralFinder.cs
//
// #1916 (epic #1253, D11) — GeneralFinder (S7, #1191) past double, for the families that now render
// to 1e290 on the perturbation engine (Multibrot, Tricorn, Burning Ship). The same method and the same
// acceptance invariants, with the orbit in BigFloat:
//   • Newton in R² on G(c) = z_{k+p}(c) − z_k(c) (k = 0: a nucleus). The orbit runs in BigFloat at the
//     view's precision (IDeepOrbitMap.StepBig); the 2×2 Jacobian ∂z/∂c is carried in double from the
//     map's own Step at the rounded orbit points, renormalised by powers of two so long orbits do not
//     overflow. The Newton step J⁻¹·G is applied in BigFloat, so each step gains ~16 digits (a Newton
//     with a double-precision Jacobian), as many steps as the depth needs.
//   • Periods: a first-order ball test along the centre's orbit — the disk of radius R around the
//     centre maps to a disk of radius ≈ ‖∂z_n/∂c‖·R around z_n, and the first n at which that disk holds
//     0 (z₀ = 0 for these maps) is a lower bound for the period. This is the linear form of the ball
//     method (Munafo's box period, Heiland-Allen's ball period; Docs/Resources-Bibliography.md) that
//     PeriodDetector uses for z² + c. For z² + c it lands on the period; for the non-conformal maps it
//     can fall short (Burning Ship at 1e-30: the ball says 171, the nucleus is 205), so periods are
//     scanned upward from it, lowest first, within a work budget, as DeepNucleusFinder scans.
//   • Precision: a nucleus is found at the view's precision, then polished at the precision of the
//     minibrot's own size (its framing zooms far past the view), as DeepNucleusFinder polishes.
//   • Acceptance: inside the view disk, the minimal period (no proper divisor q is also a root, by the
//     length of q's Newton step), an attracting cycle for a nucleus (a repelling one for a Misiurewicz
//     point), as GeneralFinder.
//   • Size, for framing: the atom-domain size of z² + c, 1/(b·l²) = 1/((∂z_p/∂c)·Λ), Λ = ∏_{k=1}^{p−1} f′(z_k)
//     (NucleusFinder.AtomSize; credited to Heiland-Allen in the bibliography), generalised as
//     1/(‖∂z_p/∂c‖·‖Λ‖^{1/(d−1)}) for a critical point of degree d, with operator-norm-like sizes for
//     the non-conformal maps. That generalisation is FF's heuristic, used only to choose a zoom; the
//     calculator renders whatever is there.
// Not covered:
//   • User Equation: its DSL has no BigFloat evaluator in Abstractions (#1952, with a perturbed
//     MultibrotRay and the general Julia morph);
//   • Burning Ship's minibrots: its folds break both the linear ball bound and the size estimate. At
//     1e-5 around a Misiurewicz point the double finder finds period 79, the deep scan period 241 with
//     no size. IDeepOrbitMap.DeepNuclei is false for it and FindMinibrot refuses (#1951; the
//     published generalisation, Heiland-Allen's EVA 2019 paper, could not be read for this slice).
//     Its Misiurewicz points are found deep as for the others.

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>#1916 — an orbit map that can also step in BigFloat (z₀ = 0). Its double
/// <see cref="IOrbitMap.Step"/> still gives the Jacobians, at the rounded orbit points.</summary>
public interface IDeepOrbitMap : IOrbitMap
{
    /// <summary>z ← f(z, c) at <paramref name="prec"/> bits.</summary>
    void StepBig(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int prec);

    /// <summary>The local degree of the critical point z₀ = 0 (2 for Tricorn / Burning Ship, d for z^d + c).</summary>
    int CriticalDegree { get; }

    /// <summary>False when the deep minibrot search is not trusted for this map (Burning Ship; see the
    /// file header). Misiurewicz points are found regardless.</summary>
    bool DeepNuclei { get; }
}

/// <summary>A minibrot found past double: the nucleus as an exact centre, log10 of its size
/// relative to the whole set, and the zoom that frames it.</summary>
public readonly record struct DeepGeneralNucleus(GeneralStatus Status, ExactCentre Point, int Period, double SizeLog10, int Attempts)
{
    public bool Found => Status == GeneralStatus.Found;

    /// <summary>log10 of the zoom that frames the minibrot as the home view frames the set.</summary>
    public double ZoomLog10 => SMath.Log10(GeneralFinder.FrameFit) - SizeLog10;
}

/// <summary>A Misiurewicz point M(Preperiod, Period) found past double, with log2 of its cycle
/// multiplier's size.</summary>
public readonly record struct DeepGeneralMisiurewicz(GeneralStatus Status, ExactCentre Point, int Preperiod, int Period, double MultiplierLog2, int Attempts)
{
    public bool Found => Status == GeneralStatus.Found;
}

public static class DeepGeneralFinder
{
    /// <summary>Candidate periods recorded by the ball test (the first is the scan's start).</summary>
    public const int MaxCandidates = 8;
    public const int MaxNewtonSteps = 80;
    /// <summary>Periods scanned upward from the ball test's first candidate.</summary>
    public const int DefaultPeriodScan = 1024;
    /// <summary>Work cap of one minibrot search, in BigFloat orbit steps.</summary>
    public const long DefaultMaxWork = 4_000_000;

    private sealed class Work { public long N; }

    /// <summary>BigFloat bits for a view of radius 10^<paramref name="radiusLog10"/> (four decades of headroom).</summary>
    public static int PrecisionFor(double radiusLog10) => BigFloat.PrecisionForZoom(-radiusLog10 + 4);

    private static long Ilog2(BigFloat x) => x.IsZero ? long.MinValue / 4 : x.Exponent + (long)x.Mantissa.GetBitLength() - 1;

    private static double Log2Abs(BigFloat re, BigFloat im)
    {
        if (re.IsZero && im.IsZero) return double.NegativeInfinity;
        long e = SMath.Max(Ilog2(re), Ilog2(im));
        double a = re.ScaleB(-e).ToDouble(), b = im.ScaleB(-e).ToDouble();
        return e + 0.5 * SMath.Log2(a * a + b * b);
    }

    // A 2×2 matrix with a power-of-two scale: the value is M·2^E.
    private struct Scaled
    {
        public M2 M; public long E;
        public void Normalise()
        {
            double n = SMath.Max(SMath.Max(SMath.Abs(M.A), SMath.Abs(M.B)), SMath.Max(SMath.Abs(M.C), SMath.Abs(M.D)));
            if (n == 0 || !double.IsFinite(n)) return;
            int k = SMath.ILogB(n);
            if (k > 256 || k < -256) { M = new M2(SMath.ScaleB(M.A, -k), SMath.ScaleB(M.B, -k), SMath.ScaleB(M.C, -k), SMath.ScaleB(M.D, -k)); E += k; }
        }
        public double Log2Norm => M.Norm > 0 ? SMath.Log2(M.Norm / SMath.Sqrt(2)) + E : double.NegativeInfinity;
    }

    // The orbit to step n in BigFloat with ∂z/∂c (scaled); zs / js receive every step when given.
    private static bool Orbit(IDeepOrbitMap map, BigFloat cr, BigFloat ci, int n, int prec,
                              out BigFloat zr, out BigFloat zi, out Scaled j,
                              List<(BigFloat, BigFloat, Scaled)>? keep, CancellationToken ct, Work? work = null)
    {
        if (work != null) work.N += n;
        zr = BigFloat.Zero(prec); zi = BigFloat.Zero(prec);
        j = new Scaled { M = M2.Zero, E = 0 };
        var c = new V2(cr.ToDouble(), ci.ToDouble());
        keep?.Add((zr, zi, j));
        for (int i = 1; i <= n; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            var z = new V2(zr.ToDouble(), zi.ToDouble());
            if (!(z.X * z.X + z.Y * z.Y <= GeneralFinder.OverflowRadius2)) return false;
            map.Step(z, c, out _, out var a, out var b);
            // J′ = A·J + B, with J = M·2^E: M′ = A·M + B·2^−E.
            double s = SMath.ScaleB(1.0, (int)SMath.Clamp(-j.E, -1074, 1023));
            j.M = a * j.M + new M2(b.A * s, b.B * s, b.C * s, b.D * s);
            j.Normalise();
            map.StepBig(ref zr, ref zi, cr, ci, prec);
            keep?.Add((zr, zi, j));
        }
        return true;
    }

    // G = z_{k+p} − z_k and its Jacobian, at c.
    private static bool Residual(IDeepOrbitMap map, BigFloat cr, BigFloat ci, int k, int p, int prec,
                                 out BigFloat gr, out BigFloat gi, out Scaled jg, CancellationToken ct, Work? work = null)
    {
        gr = gi = BigFloat.Zero(prec); jg = default;
        var keep = k > 0 ? new List<(BigFloat, BigFloat, Scaled)>(k + p + 1) : null;
        if (!Orbit(map, cr, ci, k + p, prec, out var zr, out var zi, out var j, keep, ct, work)) return false;
        if (k == 0) { gr = zr; gi = zi; jg = j; return true; }
        var (kr, ki, kj) = keep![k];
        gr = BigFloat.Sub(zr, kr, prec); gi = BigFloat.Sub(zi, ki, prec);
        // J_{k+p} − J_k on a common scale.
        long e = SMath.Max(j.E, kj.E);
        double sa = SMath.ScaleB(1.0, (int)SMath.Clamp(j.E - e, -1074, 0)), sb = SMath.ScaleB(1.0, (int)SMath.Clamp(kj.E - e, -1074, 0));
        jg = new Scaled { M = new M2(j.M.A * sa - kj.M.A * sb, j.M.B * sa - kj.M.B * sb, j.M.C * sa - kj.M.C * sb, j.M.D * sa - kj.M.D * sb), E = e };
        return true;
    }

    // One Newton step d = J⁻¹·G in BigFloat; false when J is singular.
    private static bool NewtonStep(BigFloat gr, BigFloat gi, Scaled jg, int prec, out BigFloat dr, out BigFloat di)
    {
        dr = di = BigFloat.Zero(prec);
        var m = jg.M;
        double det = m.Det;
        if (!(det != 0) || !double.IsFinite(det)) return false;
        // J⁻¹ = adj(M)/det · 2^−E
        double ia = m.D / det, ib = -m.B / det, ic = -m.C / det, id = m.A / det;
        BigFloat Mul(BigFloat x, double k) => BigFloat.Mul(x, BigFloat.FromDouble(k, prec), prec);
        dr = BigFloat.Add(Mul(gr, ia), Mul(gi, ib), prec).ScaleB(-jg.E);
        di = BigFloat.Add(Mul(gr, ic), Mul(gi, id), prec).ScaleB(-jg.E);
        return true;
    }

    private static bool Newton(IDeepOrbitMap map, ref BigFloat cr, ref BigFloat ci, int k, int p, int prec,
                               BigFloat anchorR, BigFloat anchorI, double reachLog2, CancellationToken ct, Work? work = null)
    {
        double last = double.PositiveInfinity;
        for (int it = 0; it < MaxNewtonSteps; it++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Residual(map, cr, ci, k, p, prec, out var gr, out var gi, out var jg, ct, work)) return false;
            if (!NewtonStep(gr, gi, jg, prec, out var dr, out var di)) return false;
            double step = Log2Abs(dr, di);
            if (step > reachLog2)   // clamp a wild step to the reach
            {
                double f = SMath.Pow(2, reachLog2 - step);
                dr = BigFloat.Mul(dr, BigFloat.FromDouble(f, prec), prec); di = BigFloat.Mul(di, BigFloat.FromDouble(f, prec), prec);
            }
            cr = BigFloat.Sub(cr, dr, prec); ci = BigFloat.Sub(ci, di, prec);
            if (Log2Abs(BigFloat.Sub(cr, anchorR, prec), BigFloat.Sub(ci, anchorI, prec)) > reachLog2 + 2) return false;
            double cMag = SMath.Max(0, Log2Abs(cr, ci));
            if (step <= cMag - prec + 8) return true;                      // the precision floor
            if (step <= reachLog2 - 64 && step >= last - 1) return true;   // tiny (64 bits under the reach) and no longer shrinking
            last = step;
        }
        return last <= reachLog2 - 64;
    }

    // log2 of the length of (k, p)'s Newton step at c: how far c is from being such a root.
    private static double RootDistanceLog2(IDeepOrbitMap map, BigFloat cr, BigFloat ci, int k, int p, int prec, CancellationToken ct)
    {
        if (!Residual(map, cr, ci, k, p, prec, out var gr, out var gi, out var jg, ct)) return double.PositiveInfinity;
        if (gr.IsZero && gi.IsZero) return double.NegativeInfinity;
        return NewtonStep(gr, gi, jg, prec, out var dr, out var di) ? Log2Abs(dr, di) : double.PositiveInfinity;
    }

    private static double SameRootLog2(double reachLog2) => reachLog2 + SMath.Log2(GeneralFinder.SameRootFraction);

    // log2 of the spectral radius of ∏_{i=from}^{to−1} A(z_i) (the cycle Jacobian), along the orbit of c.
    private static double CycleLog2(IDeepOrbitMap map, BigFloat cr, BigFloat ci, int from, int to, int prec, CancellationToken ct)
    {
        var keep = new List<(BigFloat, BigFloat, Scaled)>(to + 1);
        if (!Orbit(map, cr, ci, to, prec, out _, out _, out _, keep, ct)) return double.PositiveInfinity;
        var c = new V2(cr.ToDouble(), ci.ToDouble());
        var m = new Scaled { M = M2.Identity, E = 0 };
        for (int i = from; i < to; i++)
        {
            var (zr, zi, _) = keep[i];
            map.Step(new V2(zr.ToDouble(), zi.ToDouble()), c, out _, out var a, out _);
            m.M = a * m.M;
            m.Normalise();
        }
        double rho = m.M.SpectralRadius;
        return rho > 0 ? SMath.Log2(rho) + m.E : double.NegativeInfinity;
    }

    /// <summary>log10 of the minibrot's size (the header's generalised atom-domain size).</summary>
    public static double SizeLog10(IDeepOrbitMap map, ExactCentre nucleus, int period, CancellationToken ct = default)
    {
        int prec = SMath.Max(nucleus.Precision, 128);
        var keep = new List<(BigFloat, BigFloat, Scaled)>(period + 1);
        if (!Orbit(map, nucleus.Re, nucleus.Im, period, prec, out _, out _, out var jc, keep, ct)) return double.NaN;
        var c = new V2(nucleus.Re.ToDouble(), nucleus.Im.ToDouble());
        var lam = new Scaled { M = M2.Identity, E = 0 };
        for (int i = 1; i < period; i++)
        {
            var (zr, zi, _) = keep[i];
            map.Step(new V2(zr.ToDouble(), zi.ToDouble()), c, out _, out var a, out _);
            lam.M = a * lam.M;
            lam.Normalise();
        }
        double d = SMath.Max(2, map.CriticalDegree);
        double log2 = -(jc.Log2Norm + lam.Log2Norm / (d - 1));
        return log2 / 3.3219280948873622;
    }

    /// <summary>The candidate periods: the first steps n at which the view disk's image (radius
    /// ‖∂z_n/∂c‖·R around z_n) holds 0.</summary>
    public static List<int> CandidatePeriods(IDeepOrbitMap map, ExactCentre centre, double radiusLog10, int maxPeriod,
                                             CancellationToken ct = default)
    {
        int prec = PrecisionFor(radiusLog10);
        var cr = centre.Re.WithPrecision(prec); var ci = centre.Im.WithPrecision(prec);
        var found = new List<int>();
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        var j = new Scaled { M = M2.Zero, E = 0 };
        var c = new V2(cr.ToDouble(), ci.ToDouble());
        double rLog2 = radiusLog10 * 3.3219280948873622, esc2 = map.EscapeRadius * map.EscapeRadius;
        for (int n = 1; n <= maxPeriod && found.Count < MaxCandidates; n++)
        {
            if ((n & 255) == 0) ct.ThrowIfCancellationRequested();
            var z = new V2(zr.ToDouble(), zi.ToDouble());
            map.Step(z, c, out _, out var a, out var b);
            double s = SMath.ScaleB(1.0, (int)SMath.Clamp(-j.E, -1074, 1023));
            j.M = a * j.M + new M2(b.A * s, b.B * s, b.C * s, b.D * s);
            j.Normalise();
            map.StepBig(ref zr, ref zi, cr, ci, prec);
            double zx = zr.ToDouble(), zy = zi.ToDouble();
            if (!(zx * zx + zy * zy <= esc2)) break;
            if (Log2Abs(zr, zi) <= j.Log2Norm + rLog2) found.Add(n);
        }
        return found;
    }

    /// <summary>The lowest-period minibrot found from the ball test's candidates, inside the view disk
    /// of radius 10^<paramref name="radiusLog10"/> around <paramref name="centre"/>.</summary>
    public static DeepGeneralNucleus FindMinibrot(
        IDeepOrbitMap map, ExactCentre centre, double radiusLog10,
        int maxPeriod = GeneralFinder.DefaultMaxPeriod, int periodScan = DefaultPeriodScan, long maxWork = DefaultMaxWork,
        Action<int>? onPeriod = null, CancellationToken ct = default)
    {
        int attempts = 0;
        var none = new DeepGeneralNucleus(GeneralStatus.NotFound, centre, 0, double.NaN, attempts);
        if (!map.DeepNuclei) return none;
        var cands = CandidatePeriods(map, centre, radiusLog10, maxPeriod, ct);
        if (cands.Count == 0) return none;
        var work = new Work();
        int end = (int)SMath.Min(maxPeriod, (long)cands[0] + periodScan);
        for (int p = cands[0]; p <= end && work.N < maxWork; p++)
        {
            if (p % 8 == 0) onPeriod?.Invoke(p);
            attempts++;
            var r = FindNucleus(map, centre, p, radiusLog10, ct, work);
            if (r.Found) return r with { Attempts = attempts };
        }
        return none with { Attempts = attempts };
    }

    /// <summary>Newton from <paramref name="seed"/> for a period-<paramref name="period"/> nucleus within
    /// 10^<paramref name="reachLog10"/>, verified as GeneralFinder verifies (inside, minimal, attracting).</summary>
    public static DeepGeneralNucleus FindNucleus(IDeepOrbitMap map, ExactCentre seed, int period, double reachLog10, CancellationToken ct = default)
        => FindNucleus(map, seed, period, reachLog10, ct, null);

    private static DeepGeneralNucleus FindNucleus(IDeepOrbitMap map, ExactCentre seed, int period, double reachLog10, CancellationToken ct, Work? work)
    {
        int prec = PrecisionFor(reachLog10);
        double reachLog2 = reachLog10 * 3.3219280948873622;
        var ar = seed.Re.WithPrecision(prec); var ai = seed.Im.WithPrecision(prec);
        BigFloat cr = ar, ci = ai;
        var none = new DeepGeneralNucleus(GeneralStatus.NotFound, seed, period, double.NaN, 1);
        if (!Newton(map, ref cr, ref ci, 0, period, prec, ar, ai, reachLog2, ct, work)) return none;
        if (Log2Abs(BigFloat.Sub(cr, ar, prec), BigFloat.Sub(ci, ai, prec)) > reachLog2) return none;
        double same = SameRootLog2(reachLog2);
        for (int q = 1; q < period; q++)
            if (period % q == 0 && RootDistanceLog2(map, cr, ci, 0, q, prec, ct) <= same) return none;
        if (!(CycleLog2(map, cr, ci, 0, period, prec, ct) < 0)) return none;
        double size = SizeLog10(map, new ExactCentre(cr, ci), period, ct);
        // Polish at the precision of the minibrot itself (its framing zooms to ~1/size).
        int need = double.IsFinite(size) ? BigFloat.PrecisionForZoom(-size + 6) : prec;
        if (need > prec)
        {
            BigFloat pr = cr.WithPrecision(need), pi = ci.WithPrecision(need);
            if (Newton(map, ref pr, ref pi, 0, period, need, pr, pi, reachLog2, ct, work))
            {
                cr = pr; ci = pi;
                size = SizeLog10(map, new ExactCentre(cr, ci), period, ct);
            }
        }
        var point = new ExactCentre(cr, ci);
        return new DeepGeneralNucleus(GeneralStatus.Found, point, period, size, 1);
    }

    /// <summary>As GeneralFinder.FindNearMisiurewicz past double: the click's orbit in BigFloat, every
    /// (k, p) whose Newton step from the click is within the reach a candidate (lowest k + p first), each
    /// polished and classified by <see cref="FindMisiurewicz"/>.</summary>
    public static DeepGeneralMisiurewicz FindNearMisiurewicz(
        IDeepOrbitMap map, ExactCentre click, double reachLog10,
        int maxOrbit = 256, int maxPeriod = 64, int maxAttempts = 48, CancellationToken ct = default)
    {
        int prec = PrecisionFor(reachLog10);
        double reachLog2 = reachLog10 * 3.3219280948873622;
        var cr = click.Re.WithPrecision(prec); var ci = click.Im.WithPrecision(prec);
        var keep = new List<(BigFloat, BigFloat, Scaled)>(maxOrbit + 1);
        int n = maxOrbit;
        if (!Orbit(map, cr, ci, maxOrbit, prec, out _, out _, out _, keep, ct)) n = keep.Count - 1;
        var cands = new List<(int K, int P, double Est)>();
        for (int k = 1; k < n; k++)
            for (int per = 1; per <= maxPeriod && k + per <= n; per++)
            {
                var (ar, ai, aj) = keep[k + per]; var (br, bi, bj) = keep[k];
                var gr = BigFloat.Sub(ar, br, prec); var gi = BigFloat.Sub(ai, bi, prec);
                long e = SMath.Max(aj.E, bj.E);
                double sa = SMath.ScaleB(1.0, (int)SMath.Clamp(aj.E - e, -1074, 0)), sb = SMath.ScaleB(1.0, (int)SMath.Clamp(bj.E - e, -1074, 0));
                var jg = new Scaled { M = new M2(aj.M.A * sa - bj.M.A * sb, aj.M.B * sa - bj.M.B * sb, aj.M.C * sa - bj.M.C * sb, aj.M.D * sa - bj.M.D * sb), E = e };
                if (!NewtonStep(gr, gi, jg, prec, out var dr, out var di)) continue;
                double est = Log2Abs(dr, di);
                if (est <= reachLog2) cands.Add((k, per, est));
            }
        cands.Sort((x, y) => x.K + x.P != y.K + y.P ? (x.K + x.P).CompareTo(y.K + y.P) : x.Est.CompareTo(y.Est));
        int attempts = 0;
        foreach (var (k, per, _) in cands)
        {
            if (attempts >= maxAttempts) break;
            ct.ThrowIfCancellationRequested();
            attempts++;
            var m = FindMisiurewicz(map, click, k, per, reachLog10, ct);
            if (m.Found) return m with { Attempts = attempts };
        }
        return new DeepGeneralMisiurewicz(GeneralStatus.NotFound, click, 0, 0, double.NaN, attempts);
    }

    /// <summary>Newton from <paramref name="seed"/> for M(<paramref name="preperiod"/>, <paramref name="period"/>),
    /// re-classified to its minimal (k, p) and accepted with a repelling cycle, as GeneralFinder.FindMisiurewicz.</summary>
    public static DeepGeneralMisiurewicz FindMisiurewicz(IDeepOrbitMap map, ExactCentre seed, int preperiod, int period, double reachLog10,
                                                         CancellationToken ct = default)
    {
        int prec = PrecisionFor(reachLog10);
        double reachLog2 = reachLog10 * 3.3219280948873622;
        var ar = seed.Re.WithPrecision(prec); var ai = seed.Im.WithPrecision(prec);
        BigFloat cr = ar, ci = ai;
        var none = new DeepGeneralMisiurewicz(GeneralStatus.NotFound, seed, preperiod, period, double.NaN, 1);
        if (!Newton(map, ref cr, ref ci, preperiod, period, prec, ar, ai, reachLog2, ct)) return none;
        if (Log2Abs(BigFloat.Sub(cr, ar, prec), BigFloat.Sub(ci, ai, prec)) > reachLog2) return none;
        double same = SameRootLog2(reachLog2);
        int p = period;
        for (int q = 1; q < period; q++)
            if (period % q == 0 && RootDistanceLog2(map, cr, ci, preperiod, q, prec, ct) <= same) { p = q; break; }
        int k = preperiod;
        for (int kk = 1; kk < preperiod; kk++)
            if (RootDistanceLog2(map, cr, ci, kk, p, prec, ct) <= same) { k = kk; break; }
        // A root of z_p = z_0 (a nucleus) is not a Misiurewicz point.
        if (RootDistanceLog2(map, cr, ci, 0, p, prec, ct) <= same) return none;
        double mult = CycleLog2(map, cr, ci, k, k + p, prec, ct);
        if (!(mult > 0)) return none;
        return new DeepGeneralMisiurewicz(GeneralStatus.Found, new ExactCentre(cr, ci), k, p, mult, 1);
    }
}
