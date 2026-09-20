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
    {
        double a = global::System.Math.Clamp(approach, 1e-4, 0.3);
        if (q <= 0) q = 1;
        double target = (double)p / q;
        double theta = (target <= 0.0) ? a : target - a;   // cusp from above, others from below
        return CardioidPoint(theta);
    }
}
