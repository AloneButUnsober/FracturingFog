// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using FracturingFog;

namespace FracturingFog.Abstractions.Animation;

/// <summary>The procedural motion profile a track integrates over time. MVP
/// is procedural-only — keyframe authoring is deferred (see Animation Roadmap
/// §D.1).</summary>
public enum AnimationMode
{
    /// <summary>Hold the param at <see cref="AnimationTrack.Min"/>. Useful for
    /// disabling a single track without removing it from the asset.</summary>
    Hold,
    /// <summary>Linearly sweep Min → Max → Min at <see cref="AnimationTrack.FrequencyHz"/>
    /// (triangle wave, sharp turnaround).</summary>
    Triangle,
    /// <summary>Sinusoidal sweep between Min and Max at
    /// <see cref="AnimationTrack.FrequencyHz"/> (smooth turnaround).</summary>
    Sine,
    /// <summary>Monotonic ramp from Min to Max over <c>1 / FrequencyHz</c>
    /// seconds, then wraps back to Min instantly. Sawtooth.</summary>
    Linear,
    /// <summary>For Complex params: polar sweep at radius <c>Min</c> (radius =
    /// Min if Min == Max; otherwise radius oscillates between Min/Max in sync
    /// with angle). Angle advances at <c>FrequencyHz * 2π</c> rad/s. Phase
    /// offset moves the start angle.</summary>
    Lissajous,
}

/// <summary>One animated track inside an <see cref="AnimationData"/>. Names a
/// param on <see cref="FracturingFog.Models.FractalParameters"/> (validated
/// against <see cref="FractalAnimatableParamsMap"/>) and a procedural motion
/// profile that drives it.</summary>
public sealed class AnimationTrack
{
    /// <summary>Public property name on <see cref="FracturingFog.Models.FractalParameters"/>.
    /// Must appear in <see cref="FractalAnimatableParamsMap.For"/> for at least
    /// one fractal type in the parent's <see cref="AnimationData.TargetFractalTypes"/>.</summary>
    public string ParamName { get; set; } = string.Empty;

    /// <summary>Motion profile.</summary>
    public AnimationMode Mode { get; set; } = AnimationMode.Sine;

    /// <summary>Lower bound for procedural motion. For Complex params this is
    /// the lower modulus bound.</summary>
    public double Min { get; set; }

    /// <summary>Upper bound. For Complex params, upper modulus bound.</summary>
    public double Max { get; set; }

    /// <summary>Cycles per second. <c>0.1</c> = one full sweep every 10 s;
    /// <c>1.0</c> = one per second; <c>5.0</c> = strobing. Default <c>0.1</c>
    /// is a calm sweep visible without strobing artefacts.</summary>
    public double FrequencyHz { get; set; } = 0.1;

    /// <summary>Phase offset in radians. Lets tracks on the same animation
    /// start out of sync — e.g., two scalars at 90° offset produce a
    /// Lissajous-style trajectory in 2-space without needing a Complex
    /// track.</summary>
    public double PhaseOffsetRadians { get; set; }

    /// <summary>Per-track enable. Phase 6 wires this into the bus + UI; on
    /// disk today purely advisory. Default true.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Top-level animation asset. Persisted to
/// <c>%APPDATA%/FracturingFog/animations.json</c> via
/// <c>AnimationLibrary</c>. Bound to a region via
/// <c>FractalRegion.AnimationName</c> in Phase 3.</summary>
public sealed class AnimationData
{
    /// <summary>User-visible name. Library key (case-insensitive).</summary>
    public string Name { get; set; } = "Unnamed Animation";

    /// <summary>Optional human-readable description shown in the editor.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>"User" by default; built-in animations ship with "Built-in".
    /// Display-only.</summary>
    public string Category { get; set; } = "User";

    /// <summary>Fractal types this animation is *intended* to play on. Each
    /// type must support every <see cref="Tracks"/> entry's
    /// <see cref="AnimationTrack.ParamName"/> in
    /// <see cref="FractalAnimatableParamsMap"/>. Empty list = unconstrained
    /// (every type is acceptable, subject to per-track param resolution).</summary>
    public List<FractalType> TargetFractalTypes { get; set; } = new();

    /// <summary>The tracks driven by the animation bus. Order is meaningful
    /// only when two tracks touch the same param (later overrides earlier);
    /// in practice every track targets a unique param.</summary>
    public List<AnimationTrack> Tracks { get; set; } = new();

    /// <summary>Optional total length in seconds. <c>null</c> = loop
    /// forever. Used by the slideshow Animation leg to time cross-fades.</summary>
    public double? Duration { get; set; }

    /// <summary>Free-form tags for slideshow filter UI ("calm", "intense",
    /// "deep-zoom-safe", …). Case-sensitive.</summary>
    public List<string> Tags { get; set; } = new();
}
