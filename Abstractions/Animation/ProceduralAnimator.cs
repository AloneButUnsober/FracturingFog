// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Base for procedural animators driven by an
/// <see cref="AnimationTrack"/>. Phase is integrated in radians at
/// <c>2π × FrequencyHz</c> per second of <see cref="Tick(double)"/> dt.
/// Concrete subclasses translate the procedural <c>[0,1]</c> shape (or, for
/// <see cref="ComplexProceduralAnimator"/>, the polar (r, θ) trajectory)
/// into the target field's CLR type and apply it via a captured setter.
/// </summary>
public abstract class ProceduralAnimator : IParameterAnimator
{
    protected readonly AnimationTrack Track;

    /// <summary>Accumulated phase in radians. Initialised to the track's
    /// <see cref="AnimationTrack.PhaseOffsetRadians"/> so two animators
    /// constructed at the same instant can start out of phase.</summary>
    protected double Phase;

    protected ProceduralAnimator(AnimationTrack track, AnimatableParamCost cost = AnimatableParamCost.Cheap)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));
        Phase = track.PhaseOffsetRadians;
        IsEnabled = track.Enabled;
        Cost = cost;
    }

    /// <summary>Display name = the param this animator drives.</summary>
    public string Name => Track.ParamName;

    /// <summary>Per-frame cost class, resolved from
    /// <see cref="FractalAnimatableParamsMap"/> at construction. Drives the
    /// animated-param ceiling.</summary>
    public AnimatableParamCost Cost { get; }

    /// <summary>Live-toggle from outside the bus. Independent of
    /// <see cref="AnimationTrack.Enabled"/> (which is the as-saved default);
    /// the editor and the per-track UI toggle this at runtime.</summary>
    public bool IsEnabled { get; set; }

    public virtual void Tick(double dt)
    {
        if (!IsEnabled || !Track.Enabled) return;
        Phase += 2.0 * global::System.Math.PI * Track.FrequencyHz * dt;
        Apply(ComputeScalar());
    }

    /// <summary>Compute the current value for scalar modes mapped into
    /// <c>[Min, Max]</c>. Modes that don't reduce to a single scalar
    /// (Lissajous on Complex) override <see cref="Tick(double)"/>
    /// entirely.</summary>
    protected double ComputeScalar()
    {
        double t = Track.Mode switch
        {
            AnimationMode.Hold => 0.0,
            AnimationMode.Sine => 0.5 + 0.5 * global::System.Math.Sin(Phase),
            AnimationMode.Triangle => TriangleUnit(Phase),
            AnimationMode.Linear => SawtoothUnit(Phase),
            AnimationMode.Lissajous => 0.5 + 0.5 * global::System.Math.Sin(Phase),
            _ => 0.0,
        };
        return Track.Min + (Track.Max - Track.Min) * t;
    }

    /// <summary>Triangle wave in <c>[0,1]</c> with period 2π.
    /// <c>0</c> at phase = 0, <c>1</c> at phase = π, back to <c>0</c> at 2π.</summary>
    protected static double TriangleUnit(double phase)
    {
        double normalized = (phase / (2.0 * global::System.Math.PI));
        double frac = normalized - global::System.Math.Floor(normalized);
        return 1.0 - global::System.Math.Abs(2.0 * frac - 1.0);
    }

    /// <summary>Sawtooth in <c>[0,1)</c> with period 2π. <c>0</c> at phase = 0,
    /// approaches <c>1</c> at phase → 2π, wraps back to <c>0</c>.</summary>
    protected static double SawtoothUnit(double phase)
    {
        double normalized = (phase / (2.0 * global::System.Math.PI));
        double frac = normalized - global::System.Math.Floor(normalized);
        return frac;
    }

    protected abstract void Apply(double scalar);
}

/// <summary>Drives a <see cref="double"/> property on
/// <see cref="FracturingFog.Models.FractalParameters"/> via a captured setter.</summary>
public sealed class DoubleProceduralAnimator : ProceduralAnimator
{
    private readonly Action<double> _setter;

    public DoubleProceduralAnimator(AnimationTrack track, Action<double> setter,
        AnimatableParamCost cost = AnimatableParamCost.Cheap) : base(track, cost)
    {
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    protected override void Apply(double scalar) => _setter(scalar);
}

/// <summary>Drives an <see cref="int"/> property via a captured setter.
/// Procedural motion runs in <c>double</c>; rounded to nearest integer at
/// apply time. Per-tick re-rounding produces visible "step" frames at low
/// Min/Max spans — intended behaviour for discrete params like
/// <c>MultibrotExponent</c>.</summary>
public sealed class IntProceduralAnimator : ProceduralAnimator
{
    private readonly Action<int> _setter;

    public IntProceduralAnimator(AnimationTrack track, Action<int> setter,
        AnimatableParamCost cost = AnimatableParamCost.Cheap) : base(track, cost)
    {
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    protected override void Apply(double scalar)
        => _setter((int)global::System.Math.Round(scalar));
}

/// <summary>Drives a <see cref="Complex"/> property. For
/// <see cref="AnimationMode.Lissajous"/> performs a polar orbit at modulus
/// <c>r</c> rotating at <c>FrequencyHz × 2π rad/s</c>; if Min == Max, r is
/// fixed (true circle), otherwise r oscillates between Min and Max in sync
/// with angle (eccentric circle). Other modes scale the produced scalar
/// onto the *real* axis only — that's a degenerate use of a Complex
/// param, but kept so authors who pick the wrong mode get a visible result
/// rather than a no-op.</summary>
public sealed class ComplexProceduralAnimator : ProceduralAnimator
{
    private readonly Action<Complex> _setter;

    public ComplexProceduralAnimator(AnimationTrack track, Action<Complex> setter,
        AnimatableParamCost cost = AnimatableParamCost.Cheap) : base(track, cost)
    {
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    public override void Tick(double dt)
    {
        if (!IsEnabled || !Track.Enabled) return;
        Phase += 2.0 * global::System.Math.PI * Track.FrequencyHz * dt;

        if (Track.Mode == AnimationMode.Lissajous)
        {
            double r = (Track.Min == Track.Max)
                ? Track.Min
                : Track.Min + (Track.Max - Track.Min) * (0.5 + 0.5 * global::System.Math.Sin(Phase * 0.25));
            _setter(new Complex(r * global::System.Math.Cos(Phase), r * global::System.Math.Sin(Phase)));
            return;
        }

        Apply(ComputeScalar());
    }

    protected override void Apply(double scalar)
        => _setter(new Complex(scalar, 0.0));
}
