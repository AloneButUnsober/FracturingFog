// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Abstractions.Animation;

/// <summary>The data-shape of an animatable parameter on
/// <see cref="FracturingFog.Models.FractalParameters"/>. Drives how the bus
/// reads / writes it and what UI control the editor renders.</summary>
public enum AnimatableParamKind
{
    /// <summary>Single <c>double</c> field. Bounds are absolute.</summary>
    ScalarDouble,
    /// <summary>Single <c>int</c> field. Bounds are absolute; the animator
    /// integrates in <c>double</c> and rounds at apply time.</summary>
    ScalarInt,
    /// <summary>System.Numerics.Complex field. <see cref="AnimatableParamDescriptor.Min"/>
    /// / <see cref="AnimatableParamDescriptor.Max"/> bound the modulus |c|; the
    /// animator handles polar angle / radius separately.</summary>
    Complex,
    /// <summary>An <c>enum</c> field animated as a discrete ladder. The animator
    /// integrates in <c>double</c>, rounds to the nearest ladder index, and
    /// writes the enum member at that position (see #632 precision-tier sweep).
    /// <see cref="AnimatableParamDescriptor.Min"/> / <see cref="AnimatableParamDescriptor.Max"/>
    /// are ladder indices — <c>0</c>..<c>N-1</c> for an N-member enum. Low
    /// Min/Max spans produce visible "step" frames per member (a coarse sweep).</summary>
    Enum,
}

/// <summary>Rough cost class of integrating this parameter once per frame.
/// Used by the animated-param ceiling (Animation Roadmap Phase 6) to drop the
/// most expensive tracks first when the ceiling is hit.</summary>
public enum AnimatableParamCost
{
    /// <summary>Hot-path scalar field — no extra invalidation. Free to animate
    /// at any rate.</summary>
    Cheap,
    /// <summary>3D raymarched parameter — frame is expensive but the
    /// parameter change itself is cheap. Animation rate is bounded by
    /// per-frame render cost, not the parameter.</summary>
    Moderate,
    /// <summary>Animating this parameter invalidates an accumulator,
    /// re-runs a particle simulation, or scales an iteration cap. Visible
    /// stutter / flashing risk at high tick rates.</summary>
    Expensive,
}

/// <summary>One animatable parameter on
/// <see cref="FracturingFog.Models.FractalParameters"/>. Lookup is via
/// <see cref="FractalAnimatableParamsMap.For(FracturingFog.FractalType)"/>.
/// </summary>
/// <param name="ParamName">Canonical public property name on
/// <see cref="FracturingFog.Models.FractalParameters"/>. Used as the registry
/// key and the reflection target.</param>
/// <param name="Kind">Field shape.</param>
/// <param name="Min">Lower bound for procedural motion. For
/// <see cref="AnimatableParamKind.Complex"/>, this is the lower bound on
/// |c|.</param>
/// <param name="Max">Upper bound for procedural motion. For
/// <see cref="AnimatableParamKind.Complex"/>, this is the upper bound on
/// |c|.</param>
/// <param name="Cost">Cost class for the per-frame ceiling.</param>
/// <param name="Notes">Optional human-readable note shown in the animation
/// editor — typically a caveat ("accumulator reset on change", "discrete
/// step only").</param>
public sealed record AnimatableParamDescriptor(
    string ParamName,
    AnimatableParamKind Kind,
    double Min,
    double Max,
    AnimatableParamCost Cost = AnimatableParamCost.Cheap,
    string? Notes = null);
