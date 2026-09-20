// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Word-tree escape for a <b>rational semigroup</b> ⟨g₀, g₁, …⟩ — the rendering
/// primitive for the faithful parabolic implosion's limit set <c>J(g_α)</c> (#918, epic #911).
/// <para>A point <c>z</c> escapes the semigroup iff <b>some</b> finite word in the generators
/// drives <c>|z| &gt; R</c>; the escape time is the length of the <b>shortest</b> escaping word.
/// This is fundamentally different from single-map escape-time: a single deterministic orbit
/// explores only one branch of a <c>bⁿ</c> word tree (b = #generators) and massively undercounts
/// escape — the S6 finding that a naïve "apply g_α in the basin, else f" combined map renders the
/// imploded set as all-bounded. The set must be found by exploring the tree.</para>
/// <para>The tree is explored breadth-first by word length (so the first escape found is the
/// shortest) with a <b>beam</b> that keeps only the widest-modulus nodes at each level — the ones
/// most likely to escape next — bounding the cost to <c>beamWidth × maxDepth</c> node expansions
/// instead of <c>bᵐ</c>. A generator with a restricted <see cref="Generator.Domain"/> (e.g. the
/// Lavaurs map <c>g_α</c>, defined only on the attracting basin) simply prunes that branch where it
/// is undefined, which also thins the tree. Validated (spike, #918): with a single generator this
/// reproduces ordinary escape-time exactly; with two it yields the union escape set (a strict
/// superset of either single orbit); the escape-depth field is locally continuous (colourable).</para></summary>
public static class SemigroupEscape
{
    /// <summary>A semigroup generator: the <paramref name="Map"/> to apply, and an optional
    /// <paramref name="Domain"/> predicate — when non-null, the generator is only applied where the
    /// predicate holds (elsewhere that branch is pruned). A null domain means "defined
    /// everywhere" (e.g. a polynomial <c>f</c>).</summary>
    public readonly record struct Generator(Func<Complex, Complex> Map, Func<Complex, bool>? Domain = null);

    /// <summary>The length of the <b>shortest word</b> in <paramref name="generators"/> that drives
    /// <paramref name="z0"/> to modulus &gt; <paramref name="escapeRadius"/>, or <c>-1</c> if no word
    /// up to <paramref name="maxDepth"/> escapes within the beam (the point is treated as in the
    /// filled semigroup set). <paramref name="beamWidth"/> caps the live frontier per level to the
    /// widest-modulus nodes — the escape-seeking heuristic — so the cost is
    /// <c>O(beamWidth · maxDepth)</c>. Non-finite images are dropped (a dead branch).</summary>
    public static int EscapeDepth(Complex z0, IReadOnlyList<Generator> generators,
                                  double escapeRadius, int maxDepth, int beamWidth)
    {
        if (generators == null || generators.Count == 0) return -1;
        if (beamWidth < 1) beamWidth = 1;
        double r2 = escapeRadius * escapeRadius;
        var frontier = new List<Complex>(1) { z0 };
        var next = new List<Complex>(beamWidth * generators.Count);
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            next.Clear();
            foreach (var z in frontier)
            {
                for (int gi = 0; gi < generators.Count; gi++)
                {
                    var g = generators[gi];
                    if (g.Domain != null && !g.Domain(z)) continue;   // undefined here → prune
                    Complex w = g.Map(z);
                    double m2 = w.Real * w.Real + w.Imaginary * w.Imaginary;
                    if (!double.IsFinite(m2)) continue;               // dead branch
                    if (m2 > r2) return depth;                        // shortest escaping word
                    next.Add(w);
                }
            }
            if (next.Count == 0) return -1;                           // no live branch → bounded
            if (next.Count > beamWidth)
            {
                // keep the widest-modulus beamWidth nodes (partial order is enough).
                next.Sort(static (a, b) =>
                    (b.Real * b.Real + b.Imaginary * b.Imaginary)
                    .CompareTo(a.Real * a.Real + a.Imaginary * a.Imaginary));
                next.RemoveRange(beamWidth, next.Count - beamWidth);
            }
            (frontier, next) = (next, frontier);
        }
        return -1;                                                    // bounded within budget
    }
}
