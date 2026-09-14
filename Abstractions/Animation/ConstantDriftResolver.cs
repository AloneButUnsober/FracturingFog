// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;
using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation;

/// <summary>
/// Builds the default constant-path leg animator (#92 / P2) for the 2D
/// escape-time families that carry a natural complex constant — Julia
/// (<c>JuliaC</c>), Phoenix (<c>PhoenixP</c>) and Glynn (<c>GlynnC</c>). When
/// the video slideshow admits one of these regions and no authored animation
/// already drives its constant, this synthesises a gentle, bounded, eased
/// drift centred on the region's authored value so the leg does more than a
/// point-zoom: its filaments visibly breathe while the set keeps its shape.
///
/// Every other family (and Mandelbrot) resolves to <c>null</c> — the leg falls
/// back to the proven static point-zoom.
/// </summary>
public static class ConstantDriftResolver
{
    /// <summary>The parameter name a default drift would target for
    /// <paramref name="type"/>, or <c>null</c> if the family carries no
    /// drift-able constant. Kept separate from <see cref="TryBuild"/> so the
    /// caller can cheaply check "is this constant already authored?" before
    /// building.</summary>
    public static string? ConstantParamName(FractalType type) => type switch
    {
        FractalType.Julia => "JuliaC",
        FractalType.Phoenix => "PhoenixP",
        FractalType.Glynn => "GlynnC",
        _ => null,
    };

    /// <summary>
    /// Build a default constant-path animator for <paramref name="type"/> over
    /// a <paramref name="legSeconds"/>-long leg, centred on the constant already
    /// present in <paramref name="p"/>. Returns <c>null</c> when the family has
    /// no drift-able constant. <paramref name="rng"/> adds per-leg variety
    /// (shape, entry angle, direction); pass <c>null</c> for a deterministic
    /// orbit (used by tests).
    /// </summary>
    public static ConstantPathLegAnimator? TryBuild(
        FractalType type, FractalParameters? p, double legSeconds, Random? rng = null)
    {
        if (p == null) return null;
        string? name = ConstantParamName(type);
        if (name == null) return null;

        Complex baseValue;
        Action<Complex> setter;
        switch (type)
        {
            case FractalType.Julia:
                baseValue = p.JuliaC;   setter = c => p.JuliaC = c;   break;
            case FractalType.Phoenix:
                baseValue = p.PhoenixP; setter = c => p.PhoenixP = c; break;
            case FractalType.Glynn:
                baseValue = p.GlynnC;   setter = c => p.GlynnC = c;   break;
            default:
                return null;
        }

        // Amplitude scales gently with the constant's magnitude so a constant
        // near the origin doesn't get thrown across the plane, and one far out
        // still visibly moves. Clamped to a tasteful band.
        double amp = System.Math.Clamp(0.045 * (0.5 + baseValue.Magnitude), 0.02, 0.12);

        // Shape / angles. Orbit is the classic look and returns to the authored
        // constant, so it's favoured; Arc and Line add variety. Deterministic
        // (orbit, zero angles) when no RNG is supplied.
        ConstantPathShape shape = ConstantPathShape.Orbit;
        double startAngle = 0.0, lineAngle = 0.0;
        if (rng != null)
        {
            int roll = rng.Next(4); // 0,1 = Orbit, 2 = Arc, 3 = Line
            shape = roll switch
            {
                2 => ConstantPathShape.Arc,
                3 => ConstantPathShape.Line,
                _ => ConstantPathShape.Orbit,
            };
            startAngle = rng.NextDouble() * 2.0 * System.Math.PI;
            lineAngle = rng.NextDouble() * 2.0 * System.Math.PI;
        }

        return new ConstantPathLegAnimator(
            name, baseValue, amp, shape, legSeconds, setter,
            startAngle: startAngle, lineAngle: lineAngle);
    }
}
