// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>
/// The shape of a video-slideshow constant-path leg (#92 / P2). Each shape
/// starts at zero offset (so the leg's pre-rendered start frame matches the
/// authored constant exactly) and moves the constant through the parameter
/// plane over the leg's duration.
/// </summary>
public enum ConstantPathShape
{
    /// <summary>Out-and-back along a fixed direction: 0 → peak at mid-leg → 0.
    /// Ends on the authored constant.</summary>
    Line,

    /// <summary>One full circle around the authored constant, returning to it
    /// at the end of the leg.</summary>
    Orbit,

    /// <summary>A partial circular sweep (default half-turn). Ends off the
    /// authored constant — the leg cross-fade covers the discontinuity.</summary>
    Arc,
}

/// <summary>
/// Sweeps a <see cref="Complex"/> constant (Julia <c>c</c>, Phoenix <c>p</c>,
/// Glynn <c>c</c>) along a bounded, eased path around its authored value over a
/// single video-slideshow leg (#92 / P2). Unlike
/// <see cref="ComplexProceduralAnimator"/> — which orbits the plane origin
/// forever at a fixed frequency — this animator has a finite, leg-length
/// timeline, stays near the authored constant (so the Julia set keeps its
/// recognisable shape while its filaments breathe), and is offset-relative so
/// <c>Tick</c>-progress <c>u = 0</c> reproduces the authored constant exactly.
/// It integrates its own elapsed time from the per-frame <c>dt</c> the video
/// leg feeds it, so it needs no external clock.
/// </summary>
public sealed class ConstantPathLegAnimator : IParameterAnimator
{
    private readonly Complex _base;
    private readonly double _amplitude;
    private readonly ConstantPathShape _shape;
    private readonly double _legSeconds;
    private readonly double _startAngle;   // radians — orbit/arc entry angle
    private readonly double _lineAngle;    // radians — Line direction
    private readonly double _arcSpan;      // radians — Arc sweep (Orbit uses 2π)
    private readonly Action<Complex> _setter;

    private double _elapsed;

    /// <param name="paramName">The animated field's name (e.g. "JuliaC") — used
    /// only for display and for the leg's authored-animation-wins de-dup.</param>
    /// <param name="baseValue">The authored constant the path centres on.</param>
    /// <param name="amplitude">Peak excursion from <paramref name="baseValue"/>
    /// in the parameter plane.</param>
    /// <param name="shape">Path geometry.</param>
    /// <param name="legSeconds">Leg duration the path spans.</param>
    /// <param name="setter">Writes the animated constant into the live params.</param>
    /// <param name="startAngle">Orbit/arc entry angle in radians.</param>
    /// <param name="lineAngle">Line direction in radians.</param>
    /// <param name="arcSpan">Arc sweep in radians (ignored for other shapes).</param>
    public ConstantPathLegAnimator(
        string paramName,
        Complex baseValue,
        double amplitude,
        ConstantPathShape shape,
        double legSeconds,
        Action<Complex> setter,
        double startAngle = 0.0,
        double lineAngle = 0.0,
        double arcSpan = System.Math.PI)
    {
        Name = paramName ?? throw new ArgumentNullException(nameof(paramName));
        _base = baseValue;
        _amplitude = System.Math.Max(0.0, amplitude);
        _shape = shape;
        _legSeconds = legSeconds > 0.0 ? legSeconds : 1.0;
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        _startAngle = startAngle;
        _lineAngle = lineAngle;
        _arcSpan = arcSpan;
    }

    public string Name { get; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>2D escape-time constant — the cheapest animated-param class.</summary>
    public AnimatableParamCost Cost => AnimatableParamCost.Cheap;

    public void Tick(double dt)
    {
        if (!IsEnabled || dt <= 0.0) return;
        _elapsed += dt;
        double u = _elapsed / _legSeconds;
        if (u < 0.0) u = 0.0;
        else if (u > 1.0) u = 1.0;
        _setter(_base + Offset(u));
    }

    /// <summary>The path offset at normalised leg progress <paramref name="u"/>
    /// in <c>[0,1]</c>. Zero at <c>u = 0</c> for every shape.</summary>
    public Complex Offset(double u)
    {
        double s = Smoother(u);
        switch (_shape)
        {
            case ConstantPathShape.Line:
            {
                // Out-and-back: 0 → amplitude at mid → 0. sin(π·s) is already
                // velocity-zero at both ends after the smootherstep on u.
                double mag = _amplitude * System.Math.Sin(System.Math.PI * s);
                return new Complex(mag * System.Math.Cos(_lineAngle), mag * System.Math.Sin(_lineAngle));
            }
            case ConstantPathShape.Orbit:
            {
                double delta = 2.0 * System.Math.PI * s;
                return CirclePoint(_startAngle + delta) - CirclePoint(_startAngle);
            }
            case ConstantPathShape.Arc:
            {
                double delta = _arcSpan * s;
                return CirclePoint(_startAngle + delta) - CirclePoint(_startAngle);
            }
            default:
                return Complex.Zero;
        }
    }

    private Complex CirclePoint(double angle)
        => new Complex(_amplitude * System.Math.Cos(angle), _amplitude * System.Math.Sin(angle));

    /// <summary>Smootherstep (Ken Perlin) — velocity AND acceleration are zero
    /// at both ends, so the constant eases in and out with no visible jerk.</summary>
    private static double Smoother(double u)
        => u * u * u * (u * (u * 6.0 - 15.0) + 10.0);
}
