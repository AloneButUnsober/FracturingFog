// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/JuliaMorph.cs
//
// Interesting-location finder S5 (#1189, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.6.
//
// Julia morphing / shape stacking. Near a minibrot M there is an embedded
// Julia set (a copy of J_c for c at M) at roughly the LOG-SCALE MIDPOINT
// between the depth M was chosen from and M's own size. Zooming toward a
// deeper minibrot through an off-centre target T doubles the structure around
// T; repeating with new targets stacks the doublings into trees, X-forms and
// layered spirals.
//
// One morph step from the current view (zoom z₀):
//   1. find a minibrot Q near the clicked target T (S2's verified search, in a
//      small disk around T);
//   2. new centre = Q's nucleus;
//   3. new zoom  = z₀^(1−α) · z_Q^α, where z_Q = 1/|size_Q| frames Q itself and
//      α (default ½, the log midpoint) sets how deep toward Q the step goes.
// The view lands where Q's embedded Julia set is, so the next click picks the
// next layer. α is a tunable: α → 1 lands on Q itself (Zoom to minibrot),
// α → 0 barely moves.
//
// Batch: a morph path is just navigation ending at one view, and a plain zoom
// video into that final view passes through every stacked layer in turn — so
// the live Video zoom and `--batch --video-motion zoom --x --y --zoom` already
// render a morph-path video; no new flag.

using System;

using FracturingFog.FFMath;
using FracturingFog.Models;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Why a morph step was refused.</summary>
public enum JuliaMorphRefusal
{
    None,
    /// <summary>The minibrot near the target is not deeper than the current view.</summary>
    NotDeeper,
    /// <summary>The step would pass the deepest quality tier (1e100).</summary>
    TooDeep,
}

/// <summary>A planned morph step: the jump plan plus the minibrot it heads for.</summary>
public readonly record struct JuliaMorphStep(
    MinibrotJumpPlan Plan, int Period, double MinibrotZoom, JuliaMorphRefusal Refusal = JuliaMorphRefusal.None)
{
    public bool Ok => Refusal == JuliaMorphRefusal.None;
}

public static class JuliaMorph
{
    /// <summary>Default depth α: the log-scale midpoint, where the embedded
    /// Julia set of the target minibrot appears.</summary>
    public const double DefaultDepth = 0.5;

    /// <summary>Plan one morph step toward <paramref name="target"/> (a found,
    /// verified minibrot near the clicked point) from the current zoom.</summary>
    public static JuliaMorphStep Plan(
        in NucleusResult target, double currentZoom, QualityPreset currentQuality,
        bool iterLocked, int lockedIterations, double depth = DefaultDepth)
    {
        if (!target.Found) throw new ArgumentException("The morph target must be a found minibrot.", nameof(target));
        if (!(currentZoom > 0)) throw new ArgumentOutOfRangeException(nameof(currentZoom));
        if (!(depth > 0 && depth <= 1)) throw new ArgumentOutOfRangeException(nameof(depth), depth, "Depth α must be in (0, 1].");

        double minibrotZoom = 1.0 / target.Size.Magnitude;
        if (!(minibrotZoom > currentZoom))
            return new JuliaMorphStep(default, target.Period, minibrotZoom, JuliaMorphRefusal.NotDeeper);

        double zoom = SMath.Exp((1 - depth) * SMath.Log(currentZoom) + depth * SMath.Log(minibrotZoom));
        var plan = MinibrotJump.At(target.Nucleus, zoom, target.Period, currentQuality, iterLocked, lockedIterations);
        return plan is MinibrotJumpPlan p
            ? new JuliaMorphStep(p, target.Period, minibrotZoom)
            : new JuliaMorphStep(default, target.Period, minibrotZoom, JuliaMorphRefusal.TooDeep);
    }
}
