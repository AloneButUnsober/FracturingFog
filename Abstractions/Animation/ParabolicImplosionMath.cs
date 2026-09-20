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
    {
        double a = global::System.Math.Clamp(approach, 1e-4, 0.3);
        if (q <= 0) q = 1;
        double target = (double)p / q;
        double angle = (target <= 0.0) ? a : target - a;   // attachment from above, others from below
        return satellite ? Period2BulbPoint(angle) : CardioidPoint(angle);
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
