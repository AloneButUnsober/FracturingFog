// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Shared math for the faithful (Lavaurs-limit) parabolic implosion
/// (#920). The parameter <c>c</c> is placed on the main cardioid boundary as a
/// function of the internal angle <c>θ</c>; the boundary point at a rational
/// <c>θ = p/q</c> is exactly the <b>root of the period-q bulb</b> (a parabolic
/// parameter, fixed-point multiplier <c>e^{2πip/q}</c>), so sweeping <c>θ → p/q</c>
/// renders the faithful implosion at that root. Used by
/// <see cref="ProceduralAnimator"/> (the <c>CardioidApproach</c> mode) and by the
/// built-in implosion regions/animations.</summary>
public static class ParabolicImplosionMath
{
    /// <summary>The main-cardioid boundary point at internal angle <paramref name="theta"/>:
    /// <c>c(θ) = λ/2 − λ²/4</c> with <c>λ = e^{2πiθ}</c> the fixed-point multiplier.
    /// <c>θ = 0</c> gives the cusp <c>c = 1/4</c>.</summary>
    public static Complex CardioidPoint(double theta)
    {
        double a = 2.0 * global::System.Math.PI * theta;
        var lam = new Complex(global::System.Math.Cos(a), global::System.Math.Sin(a));
        return lam / 2.0 - lam * lam / 4.0;
    }

    /// <summary>The parabolic root of the <c>p/q</c> bulb = <c>CardioidPoint(p/q)</c>
    /// (fixed-point multiplier a primitive <c>q</c>-th root of unity <c>e^{2πip/q}</c>).</summary>
    public static Complex ParabolicRoot(int p, int q) => CardioidPoint((double)p / q);

    /// <summary>The near-parabolic Julia parameter for the faithful implosion at the
    /// <c>p/q</c> root, offset from the root by internal-angle distance
    /// <paramref name="approach"/> (a positive θ-gap): as <c>approach → 0</c> the set
    /// converges to the imploded limit <c>J(g_α)</c>. The cusp (<c>p/q = 0</c>) is
    /// approached from above (θ = approach); every other root from below
    /// (θ = p/q − approach), so θ stays on the main cardioid between neighbouring
    /// roots. <paramref name="approach"/> is clamped to <c>[1e-4, 0.3]</c>.</summary>
    public static Complex ImplosionC(int p, int q, double approach)
        => ImplosionC(p, q, approach, satellite: false);

    // ── general bulb-boundary solver (period-n bulbs, no closed form) ──────────
    // f_c^n(z) and the period-n cycle multiplier (f_c^n)'(z) = Π 2 z_k.
    static (Complex fn, Complex mult) IterN(Complex z, Complex c, int n)
    {
        Complex zk = z, m = Complex.One;
        for (int k = 0; k < n; k++) { m *= 2.0 * zk; zk = zk * zk + c; }
        return (zk, m);
    }
    // refine a cycle point at c onto the period-n branch (Newton on f^n(z)−z, d/dz = m−1).
    static Complex CycleRefine(Complex c, int n, Complex z)
    {
        for (int i = 0; i < 40; i++)
        {
            var (fn, m) = IterN(z, c, n);
            Complex res = fn - z;
            if (res.Magnitude < 1e-14) break;
            z -= res / (m - 1.0);
        }
        return z;
    }
    static Complex Mult(Complex c, int n, ref Complex z) { z = CycleRefine(c, n, z); return IterN(z, c, n).mult; }
    // an EXACT-period-n attracting cycle point at c (settle the critical orbit + verify).
    static bool ExactPeriodN(Complex c, int n, out Complex z0, out Complex mult)
    {
        z0 = default; mult = default;
        Complex z = 0;
        for (int i = 0; i < 6000; i++) z = z * z + c;
        if (!double.IsFinite(z.Real) || z.Magnitude > 10) return false;
        var pts = new Complex[n]; Complex zk = z;
        for (int k = 0; k < n; k++) { pts[k] = zk; zk = zk * zk + c; }
        if ((zk - z).Magnitude > 1e-8) return false;
        for (int k = 1; k < n; k++) if ((pts[k] - pts[0]).Magnitude < 1e-6) return false; // sub-period
        z0 = z; mult = IterN(z, c, n).mult;
        return mult.Magnitude < 1.0;
    }

    /// <summary>The boundary point of the <b>period-<paramref name="n"/> bulb</b> rooted on
    /// the main cardioid at internal angle <paramref name="rootAngle"/> (= p/q, n = q), at
    /// internal angle <paramref name="phi"/> — i.e. where the period-n cycle multiplier is
    /// <c>e^{2πiφ}</c>. No closed form for n ≥ 3: seeds an interior attracting cycle (root ±
    /// outward normal · ~1/n²), then Newton-continues the multiplier out to the target,
    /// tracking the cycle by local Newton. Validated against the period-2 closed form
    /// <see cref="Period2BulbPoint"/> to ~1e-14. Falls back to the root if no interior cycle
    /// is found.</summary>
    public static Complex BulbBoundaryPoint(double rootAngle, int n, double phi)
    {
        if (n < 2) return CardioidPoint(phi);
        Complex root = CardioidPoint(rootAngle);
        double a = 2.0 * global::System.Math.PI * rootAngle;
        Complex lam = new Complex(global::System.Math.Cos(a), global::System.Math.Sin(a));
        Complex tangent = Complex.ImaginaryOne * global::System.Math.PI * lam * (1 - lam);
        Complex normal = Complex.ImaginaryOne * tangent; normal /= normal.Magnitude;
        double bulbSize = 0.4 / (n * n);
        Complex cIn = default, z0 = default, m0 = default; bool found = false;
        foreach (double sgn in new[] { -1.0, 1.0 })
        {
            foreach (double frac in new[] { 1.0, 0.6, 1.4 })
            {
                Complex ci = root + normal * (sgn * bulbSize * frac);
                if (ExactPeriodN(ci, n, out var zz, out var mm)) { cIn = ci; z0 = zz; m0 = mm; found = true; break; }
            }
            if (found) break;
        }
        if (!found) return root;
        Complex target = Complex.Exp(new Complex(0, 2.0 * global::System.Math.PI * phi));
        Complex c = cIn, zg = z0;
        int steps = 22;
        for (int s = 1; s <= steps; s++)
        {
            Complex lamS = m0 + (target - m0) * ((double)s / steps);
            for (int i = 0; i < 30; i++)
            {
                Complex m = Mult(c, n, ref zg);
                Complex res = m - lamS;
                if (res.Magnitude < 1e-13) break;
                Complex hc = 1e-7; Complex zc = zg;
                Complex mp = Mult(c + hc, n, ref zc);
                Complex dmdc = (mp - m) / hc;
                if (dmdc.Magnitude < 1e-30) break;
                c -= res / dmdc;
            }
        }
        return double.IsFinite(c.Real) ? c : root;
    }

    /// <summary>The period-2 bulb boundary point at internal angle <paramref name="phi"/>:
    /// <c>c(φ) = −1 + e^{2πiφ}/4</c> (the period-2 cycle multiplier is <c>e^{2πiφ}</c>;
    /// the bulb is the disc <c>|c + 1| = 1/4</c>). Its sub-roots at <c>φ = p/q</c> are the
    /// <b>satellite</b> parabolic parameters — the period-doubling cascade: <c>φ = 1/2</c>
    /// is <c>c = −5/4</c> (period 4), etc. <c>φ = 0</c> is the attachment root
    /// <c>c = −3/4</c>.</summary>
    public static Complex Period2BulbPoint(double phi)
    {
        double a = 2.0 * global::System.Math.PI * phi;
        return new Complex(-1.0 + global::System.Math.Cos(a) / 4.0, global::System.Math.Sin(a) / 4.0);
    }

    /// <summary>The near-parabolic Julia parameter for the faithful implosion at the
    /// <c>p/q</c> root, offset from the root by internal-angle distance
    /// <paramref name="approach"/>. When <paramref name="satellite"/> is false the root is
    /// on the <b>main cardioid</b> (cusp <c>p/q = 0</c> approached from above, others from
    /// below). When true the root is a <b>satellite</b> on the <b>period-2 bulb</b>
    /// (<see cref="Period2BulbPoint"/>) — the period-doubling family; the attachment
    /// <c>p/q = 0</c> is approached from above. <paramref name="approach"/> is clamped to
    /// <c>[1e-4, 0.3]</c>.</summary>
    public static Complex ImplosionC(int p, int q, double approach, bool satellite)
        => ImplosionC(p, q, approach, satellite ? 1 : 0, satellite ? 2 : 1);

    /// <summary>The near-parabolic Julia parameter for the faithful implosion at the
    /// <c>p/q</c> root on the boundary of the parent bulb <c>parentP/parentQ</c>, offset by
    /// internal-angle distance <paramref name="approach"/>. Parent <c>0/1</c> = the main
    /// cardioid (root = <see cref="CardioidPoint"/>); parent <c>1/2</c> = the period-2 bulb
    /// (fast closed form <see cref="Period2BulbPoint"/> — the period-doubling family); any
    /// other parent (period <c>parentQ ≥ 3</c>) uses the general numerical
    /// <see cref="BulbBoundaryPoint"/>. The attachment <c>p/q = 0</c> is approached from
    /// above; <paramref name="approach"/> is clamped to <c>[1e-4, 0.3]</c>.</summary>
    public static Complex ImplosionC(int p, int q, double approach, int parentP, int parentQ)
    {
        double a = global::System.Math.Clamp(approach, 1e-4, 0.3);
        if (q <= 0) q = 1;
        double target = (double)p / q;
        double angle = (target <= 0.0) ? a : target - a;   // attachment from above, others from below
        if (parentQ <= 1) return CardioidPoint(angle);                       // main cardioid
        if (parentQ == 2 && parentP == 1) return Period2BulbPoint(angle);    // period-2 fast path
        return BulbBoundaryPoint((double)parentP / parentQ, parentQ, angle); // general bulb
    }

    /// <summary>Recommended escape-time iteration budget for the faithful implosion at
    /// depth <paramref name="approach"/> toward a period-<paramref name="q"/> root.
    /// <para>Empirically measured (P99.95 of the exterior escape-time distribution, ×3
    /// headroom for crisp boundaries at high resolution):
    /// the <b>cusp</b> (q = 1, multiplier 1) is the iteration-hungry case — the classic
    /// parabolic 1/n crawl, whose parameter distance from the root scales as
    /// <c>approach²</c>, so the budget scales as <c>~10/approach</c>. Higher-q roots
    /// (multiplier a primitive root of unity ≠ 1) are milder — the rotation stirs orbits
    /// out faster, parameter distance scales linearly in <c>approach</c>, budget
    /// <c>~30/√approach</c>.</para>
    /// The render host takes this as a floor on the iteration cap.</summary>
    public static int RecommendedIterations(int q, double approach)
    {
        double a = global::System.Math.Clamp(approach, 1e-4, 1.0);
        if (q < 1) q = 1;
        double law = (q == 1)
            ? 10.0 / a                                    // cusp: ~10/approach
            : 30.0 / global::System.Math.Sqrt(a);         // q≥2: ~30/√approach
        return (int)global::System.Math.Clamp(3.0 * law, 1.0, 2_000_000.0);
    }
}
