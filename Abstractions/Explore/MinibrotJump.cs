// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/MinibrotJump.cs
//
// Interesting-location finder S2 (#1186): turn a found minibrot into a view
// change — where to centre, how far to zoom, which quality tier the depth
// needs, and how many iterations resolve the minibrot. UI-free so the rules
// are unit-tested; the Control Center applies the plan.

using FracturingFog.FFMath;
using FracturingFog.Models;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>A planned jump to a minibrot. <see cref="Quality"/> is the tier to
/// switch to (the current one unless the depth needs a deeper tier).
/// <see cref="PreferredIterations"/> is a region-style first-render hint, 0
/// when iterations are locked. <see cref="IterationsShort"/> means the
/// iteration count (locked or capped) is below what the period wants.</summary>
public readonly record struct MinibrotJumpPlan(
    DeepComplex Center, double Zoom, QualityPreset Quality,
    int PreferredIterations, int WantedIterations, bool IterationsShort);

public static class MinibrotJump
{
    /// <summary>Upper bound for the iteration hint: the Extreme tier's cap.</summary>
    public static readonly int MaxPreferredIterations = QualityPreset.Extreme.IterMax;

    /// <summary>The tier a jump to <paramref name="zoom"/> needs: the current
    /// one, or the first deeper tier whose ZoomMax reaches it. Promotion only
    /// (as the wheel does) — never demotes a tier the user chose.</summary>
    public static QualityPreset QualityFor(QualityPreset current, double zoom)
    {
        if (zoom <= current.ZoomMax) return current;
        foreach (var p in QualityPreset.All)
            if (p.ZoomMax >= zoom) return p;
        return QualityPreset.Extreme;
    }

    /// <summary>Plan the jump, or null when the minibrot is deeper than the
    /// deepest quality tier allows.</summary>
    public static MinibrotJumpPlan? Plan(
        in NucleusResult found, QualityPreset current, bool iterLocked, int lockedIterations,
        double framing = 1.0)
    {
        var (center, zoom) = NucleusFinder.Frame(found, framing);
        return At(center, zoom, found.Period, current, iterLocked, lockedIterations);
    }

    /// <summary>Plan a view change to (<paramref name="center"/>,
    /// <paramref name="zoom"/>) whose subject is a period-<paramref name="period"/>
    /// minibrot: tier promotion + iteration hint. Null past the deepest tier.
    /// Shared by the minibrot jump, angle landings and Julia-morph steps.</summary>
    public static MinibrotJumpPlan? At(
        DeepComplex center, double zoom, int period, QualityPreset current, bool iterLocked, int lockedIterations)
    {
        if (!(zoom <= QualityPreset.Extreme.ZoomMax)) return null;

        var quality = QualityFor(current, zoom);

        int wanted = NucleusFinder.SuggestedIterations(period);
        if (iterLocked)
            return new MinibrotJumpPlan(center, zoom, quality, 0, wanted, lockedIterations < wanted);

        // The hint is a first-render override (like a saved region's
        // iterations), not bound by the tier's own formula cap; the deepest
        // tier's cap keeps it sane.
        int byZoom = quality.ComputeIterations(zoom);
        int preferred = SMath.Max(byZoom, SMath.Min(wanted, MaxPreferredIterations));
        return new MinibrotJumpPlan(center, zoom, quality, preferred, wanted, preferred < wanted);
    }
}
