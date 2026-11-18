// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/DeepMisiurewiczFinder.cs
//
// #1868 (epic #1428, C7b) — MisiurewiczFinder past octuple-double: the same method
// (design: Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.4; Misiurewicz
// points and deflated Newton credited in Docs/Resources-Bibliography.md: Munafo's Mu-Ency,
// Claude Heiland-Allen, Tan Lei), carried in BigFloat and floatexp:
//
//   • Reference orbit Zₙ at the exact click point in BigFloat (kept, for the EXACT
//     differences Z_a − Z_b that cancel to ~0 near a Misiurewicz point), with double
//     copies and dZ/dc in CFx (∏2Z overflows double at depth).
//   • Candidates (k, p): first-order distance |Z_{k+p} − Z_k| / |dZ_{k+p} − dZ_k| within
//     reach, the smallest k per p, simplest first. A double difference that already
//     rules a pair out skips the exact one.
//   • Deflated Newton (lower preperiods and divisor periods divided out), perturbed about
//     the reference in CFx; then polished in BigFloat with the step Δc = 1 / (G′/G) in CFx.
//   • Accepted only within reach of the click and at minimal (k, p) there; the multiplier
//     λ = Π 2zᵢ over the cycle in CFx.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>#1868 — <see cref="MisiurewiczResult"/> past OD: the point as an exact centre.
/// <see cref="Multiplier"/> λ = (fᵖ)′ on the repelling cycle (valid when Found).</summary>
public readonly record struct DeepMisiurewiczResult(
    MisiurewiczStatus Status, ExactCentre Point, int Preperiod, int Period, Complex Multiplier, int Attempts)
{
    public bool Found => Status == MisiurewiczStatus.Found;
}

/// <summary>#1868 — the Misiurewicz-point finder in BigFloat / floatexp. See the file header.</summary>
public static class DeepMisiurewiczFinder
{
    private const int MaxSteps = 64;
    private const double ReferenceEscape = 1e6;
    private const double Log2Of10 = 3.3219280948873622;

    /// <summary>BigFloat bits for a search of reach 10^<paramref name="acceptRadiusLog10"/>:
    /// 30 decades past it, so the snapped point still holds when the view zooms on into it.</summary>
    public static int PrecisionFor(double acceptRadiusLog10) => BigFloat.PrecisionForZoom(-acceptRadiusLog10 + 30);

    /// <summary>As <see cref="MisiurewiczFinder.FindNear"/>: the simplest Misiurewicz point
    /// within 10^<paramref name="acceptRadiusLog10"/> of <paramref name="click"/>.</summary>
    public static DeepMisiurewiczResult FindNear(
        ExactCentre click, double acceptRadiusLog10,
        int maxOrbit = MisiurewiczFinder.DefaultMaxOrbit, int maxPeriod = MisiurewiczFinder.DefaultMaxPeriod,
        int maxAttempts = MisiurewiczFinder.DefaultMaxAttempts, CancellationToken ct = default)
    {
        if (!double.IsFinite(acceptRadiusLog10))
            throw new ArgumentOutOfRangeException(nameof(acceptRadiusLog10), acceptRadiusLog10, "Accept radius must be finite.");
        int prec = PrecisionFor(acceptRadiusLog10);
        var c = click.WithPrecision(prec);
        var r = ReferenceOrbit(c, maxOrbit, prec, ct);
        int n = r.Z.Length;
        double acceptLog2 = acceptRadiusLog10 * Log2Of10;
        double reachLog2 = acceptLog2 + 2;   // 4 accept radii

        var candidates = new List<(int K, int P, double Est)>();
        for (int p = 1; p <= SMath.Min(maxPeriod, n - 2); p++)
        {
            if ((p & 0x3F) == 0) ct.ThrowIfCancellationRequested();
            for (int k = 1; k + p < n; k++)
            {
                var den = r.Dz[k + p] - r.Dz[k];
                if (den.IsZero) continue;
                double denLog2 = den.Log2Abs();
                // The double difference is only ~1e-16·|Z| accurate: when even its lower
                // bound puts the root out of reach, skip the exact one.
                double rough = (r.Z[k + p] - r.Z[k]).Magnitude;
                double err = 4e-16 * (r.Z[k + p].Magnitude + r.Z[k].Magnitude);
                if (rough - err > 0 && SMath.Log2(rough - err) - denLog2 > reachLog2) continue;
                double est = r.Diff(k + p, k, prec).Log2Abs() - denLog2;
                if (est <= reachLog2) { candidates.Add((k, p, est)); break; }
            }
        }
        candidates.Sort((a, b) => a.K + a.P != b.K + b.P ? (a.K + a.P).CompareTo(b.K + b.P) : a.Est.CompareTo(b.Est));

        int attempts = 0;
        foreach (var (k, p, _) in candidates)
        {
            if (attempts >= maxAttempts) break;
            ct.ThrowIfCancellationRequested();
            attempts++;
            if (!PerturbedNewton(r, k, p, acceptLog2, prec, out CFx delta)) continue;
            var seed = new ExactCentre(
                BigFloat.Add(c.Re, BigFloat.FromFloatExp(delta.Re, prec), prec),
                BigFloat.Add(c.Im, BigFloat.FromFloatExp(delta.Im, prec), prec));
            var res = Refine(seed, k, p, c, acceptRadiusLog10, ct);
            if (res.Found) return res with { Attempts = attempts };
        }
        return new DeepMisiurewiczResult(MisiurewiczStatus.NotFound, click, 0, 0, Complex.Zero, attempts);
    }

    /// <summary>As <see cref="MisiurewiczFinder.Refine"/>: polish <paramref name="seed"/> to
    /// M(<paramref name="preperiod"/>, <paramref name="period"/>) in BigFloat and verify
    /// (k, p) is minimal there, within 10^<paramref name="acceptRadiusLog10"/> of
    /// <paramref name="anchor"/>.</summary>
    public static DeepMisiurewiczResult Refine(
        ExactCentre seed, int preperiod, int period, ExactCentre anchor, double acceptRadiusLog10,
        CancellationToken ct = default)
    {
        if (preperiod < 1) throw new ArgumentOutOfRangeException(nameof(preperiod));
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        int k = preperiod, p = period;
        int prec = PrecisionFor(acceptRadiusLog10);
        double acceptLog2 = acceptRadiusLog10 * Log2Of10;
        BigFloat cx = seed.Re.WithPrecision(prec), cy = seed.Im.WithPrecision(prec);
        var ax = anchor.Re.WithPrecision(prec); var ay = anchor.Im.WithPrecision(prec);
        double lastLog2 = double.PositiveInfinity;
        bool converged = false;
        for (int s = 0; s < MaxSteps; s++)
        {
            ct.ThrowIfCancellationRequested();
            var o = Orbit(cx, cy, k + p, prec, ct);
            if (o == null) break;
            CFx? step = DeflatedStep(o, k, p, prec);
            if (step is not CFx d || !d.IsFinite) break;
            cx = BigFloat.Sub(cx, BigFloat.FromFloatExp(d.Re, prec), prec);
            cy = BigFloat.Sub(cy, BigFloat.FromFloatExp(d.Im, prec), prec);
            double ms = d.IsZero ? double.NegativeInfinity : d.Log2Abs();
            if (DistLog2(cx, cy, ax, ay, prec) > acceptLog2 + 3) break;   // 8 accept radii
            double cLog2 = SMath.Max(new CFx(cx.ToFloatExp(), cy.ToFloatExp()).Log2Abs(), acceptLog2);
            if (ms <= cLog2 - prec + 24 || (ms >= lastLog2 && ms <= cLog2 - prec + 64))
            {
                lastLog2 = SMath.Min(lastLog2, ms);
                converged = true;
                break;
            }
            lastLog2 = ms;
        }
        var point = new ExactCentre(cx, cy);
        if (!converged || DistLog2(cx, cy, ax, ay, prec) > acceptLog2)
            return new DeepMisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);

        // Minimality at the polished point: (k, p) holds, no smaller preperiod or divisor period does.
        var z = Orbit(cx, cy, k + p, prec, ct);
        if (z == null)
            return new DeepMisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);
        double cMag = new CFx(cx.ToFloatExp(), cy.ToFloatExp()).Log2Abs();
        double tol = SMath.Max(lastLog2 + 10, cMag - prec + 64);   // max(lastStep·1e3, floor)
        if (!IsRoot(z, k, p, tol, prec))
            return new DeepMisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);
        for (int i = 1; i < k; i++)
            if (IsRoot(z, i, p, tol, prec))
                return new DeepMisiurewiczResult(MisiurewiczStatus.LowerPreperiod, point, i, p, Complex.Zero, 0);
        for (int q = 1; q < p; q++)
            if (p % q == 0 && IsRoot(z, k, q, tol, prec))
                return new DeepMisiurewiczResult(MisiurewiczStatus.LowerPeriod, point, k, q, Complex.Zero, 0);

        return new DeepMisiurewiczResult(MisiurewiczStatus.Found, point, k, p, Multiplier(z, k, p), 0);
    }

    // ── Orbits ──────────────────────────────────────────────────────────────

    private sealed class Reference
    {
        public required BigFloat[] Re, Im;
        public required Complex[] Z;
        public required CFx[] Dz;

        /// <summary>Z_a − Z_b, exact in BigFloat, as CFx.</summary>
        public CFx Diff(int a, int b, int prec)
            => new(BigFloat.Sub(Re[a], Re[b], prec).ToFloatExp(), BigFloat.Sub(Im[a], Im[b], prec).ToFloatExp());
    }

    // The reference at the click, until it escapes.
    private static Reference ReferenceOrbit(ExactCentre c, int maxN, int prec, CancellationToken ct)
    {
        var re = new List<BigFloat> { BigFloat.Zero(prec) };
        var im = new List<BigFloat> { BigFloat.Zero(prec) };
        var z = new List<Complex> { Complex.Zero };
        var dz = new List<CFx> { CFx.Zero };
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        CFx d = CFx.Zero;
        for (int n = 1; n <= maxN; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            d = (new CFx(zx.ToFloatExp(), zy.ToFloatExp()) * d).Twice().PlusOne();
            (zx, zy) = SquareAdd(zx, zy, c.Re, c.Im, prec);
            var v = new Complex(zx.ToDouble(), zy.ToDouble());
            if (!(v.Magnitude <= ReferenceEscape) || !d.IsFinite) break;
            re.Add(zx); im.Add(zy); z.Add(v); dz.Add(d);
        }
        return new Reference { Re = re.ToArray(), Im = im.ToArray(), Z = z.ToArray(), Dz = dz.ToArray() };
    }

    // The orbit z₀..z_len at c in BigFloat with dz/dc in CFx; null on escape.
    private static Reference? Orbit(BigFloat cx, BigFloat cy, int len, int prec, CancellationToken ct)
    {
        var re = new BigFloat[len + 1];
        var im = new BigFloat[len + 1];
        var dz = new CFx[len + 1];
        var z = new Complex[len + 1];
        re[0] = BigFloat.Zero(prec); im[0] = BigFloat.Zero(prec); dz[0] = CFx.Zero;
        for (int n = 0; n < len; n++)
        {
            if ((n & 0xFF) == 0xFF) ct.ThrowIfCancellationRequested();
            dz[n + 1] = (new CFx(re[n].ToFloatExp(), im[n].ToFloatExp()) * dz[n]).Twice().PlusOne();
            (re[n + 1], im[n + 1]) = SquareAdd(re[n], im[n], cx, cy, prec);
            z[n + 1] = new Complex(re[n + 1].ToDouble(), im[n + 1].ToDouble());
            if (!(z[n + 1].Magnitude <= ReferenceEscape)) return null;
        }
        return new Reference { Re = re, Im = im, Z = z, Dz = dz };
    }

    // ── Newton ──────────────────────────────────────────────────────────────

    // Δc = 1 / (G′/G) for the deflated G, from an exact orbit.
    private static CFx? DeflatedStep(Reference o, int k, int p, int prec)
    {
        CFx g = o.Diff(k + p, k, prec);
        if (g.IsZero) return CFx.Zero;   // exactly on the root
        CFx l = (o.Dz[k + p] - o.Dz[k]) / g;
        for (int i = 0; i < k; i++)
        {
            CFx f = o.Diff(i + p, i, prec);
            if (f.IsZero) return null;
            l -= (o.Dz[i + p] - o.Dz[i]) / f;
        }
        for (int q = 1; q < p; q++)
        {
            if (p % q != 0) continue;
            CFx f = o.Diff(k + q, k, prec);
            if (f.IsZero) return null;
            l -= (o.Dz[k + q] - o.Dz[k]) / f;
        }
        return l.IsZero || !l.IsFinite ? null : l.Reciprocal();
    }

    // Deflated Newton perturbed about the reference (c = c₀ + δ), in CFx: each difference
    // z_a − z_b is (Z_a − Z_b) exact plus (ε_a − ε_b), so its error scales with δ.
    private static bool PerturbedNewton(Reference r, int k, int p, double acceptLog2, int prec, out CFx delta)
    {
        delta = CFx.Zero;
        int len = k + p;
        var eps = new CFx[len + 1];
        var dz = new CFx[len + 1];
        var zc = new CFx[len + 1];
        for (int n = 0; n <= len; n++) zc[n] = CFx.Of(r.Z[n].Real, r.Z[n].Imaginary);

        CFx dTarget = r.Diff(k + p, k, prec);
        var dLower = new CFx[k];
        for (int i = 0; i < k; i++) dLower[i] = r.Diff(i + p, i, prec);
        var divisors = new List<int>();
        for (int q = 1; q < p; q++) if (p % q == 0) divisors.Add(q);
        var dDiv = new CFx[divisors.Count];
        for (int j = 0; j < divisors.Count; j++) dDiv[j] = r.Diff(k + divisors[j], k, prec);

        double prev = double.PositiveInfinity;
        for (int s = 0; s < MaxSteps; s++)
        {
            eps[0] = CFx.Zero; dz[0] = CFx.Zero;
            for (int n = 0; n < len; n++)
            {
                CFx zn = zc[n] + eps[n];
                dz[n + 1] = (zn * dz[n]).Twice().PlusOne();
                eps[n + 1] = (zc[n].Twice() + eps[n]) * eps[n] + delta;   // εₙ₊₁ = 2Zₙεₙ + εₙ² + δ
            }
            CFx g = dTarget + (eps[k + p] - eps[k]);
            if (!g.IsFinite) return false;
            if (g.IsZero) return delta.Log2Abs() <= acceptLog2;
            CFx l = (dz[k + p] - dz[k]) / g;
            for (int i = 0; i < k; i++) l -= (dz[i + p] - dz[i]) / (dLower[i] + (eps[i + p] - eps[i]));
            for (int j = 0; j < divisors.Count; j++)
            {
                int q = divisors[j];
                l -= (dz[k + q] - dz[k]) / (dDiv[j] + (eps[k + q] - eps[k]));
            }
            if (!l.IsFinite || l.IsZero) return false;
            CFx step = l.Reciprocal();
            delta -= step;
            if (!delta.IsFinite) return false;
            double ms = step.Log2Abs(), md = delta.Log2Abs();
            if (md > acceptLog2 + 3) return false;                       // 8 accept radii
            double scale = SMath.Max(md, acceptLog2 - 9.97);             // max(|δ|, 1e-3·reach)
            if (ms <= scale - 43.2 || (s >= 3 && ms >= prev - 1 && ms <= scale - 29.9))
                return md <= acceptLog2;
            prev = ms;
        }
        return false;
    }

    // The Newton step toward a root of z_{a+b} − z_a is within tol (log2): (a, b) holds.
    private static bool IsRoot(Reference z, int a, int b, double tolLog2, int prec)
    {
        var den = z.Dz[a + b] - z.Dz[a];
        if (den.IsZero) return false;
        var g = z.Diff(a + b, a, prec);
        return g.IsZero || g.Log2Abs() - den.Log2Abs() <= tolLog2;
    }

    // λ = Π 2zᵢ over the repelling cycle z_k … z_{k+p−1}.
    private static Complex Multiplier(Reference z, int k, int p)
    {
        CFx m = CFx.One;
        for (int i = k; i < k + p; i++) m = (new CFx(z.Re[i].ToFloatExp(), z.Im[i].ToFloatExp()) * m).Twice();
        return new Complex(m.Re.ToDouble(), m.Im.ToDouble());
    }

    private static (BigFloat, BigFloat) SquareAdd(BigFloat zx, BigFloat zy, BigFloat cx, BigFloat cy, int prec)
    {
        var x2 = BigFloat.Mul(zx, zx, prec);
        var y2 = BigFloat.Mul(zy, zy, prec);
        var xy = BigFloat.Mul(zx, zy, prec);
        return (BigFloat.Add(BigFloat.Sub(x2, y2, prec), cx, prec), BigFloat.Add(xy.ScaleB(1), cy, prec));
    }

    private static double DistLog2(BigFloat ax, BigFloat ay, BigFloat bx, BigFloat by, int prec)
        => new CFx(BigFloat.Sub(ax, bx, prec).ToFloatExp(), BigFloat.Sub(ay, by, prec).ToFloatExp()).Log2Abs();
}
