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
    ///
    /// <paramref name="varyStart"/> (#801) begins each leg at a small bounded
    /// random offset from the authored constant (≤ the drift amplitude) instead
    /// of on it — the caller must render the leg pre-render at the animator's
    /// <see cref="ConstantPathLegAnimator.StartValue"/> to avoid a first-frame
    /// jump. <paramref name="varySpeed"/> (#801) randomises the traversal speed
    /// within a tasteful band. Both require <paramref name="rng"/> (no RNG ⇒ the
    /// deterministic single-orbit-on-the-constant P2 default).
    /// </summary>
    public static ConstantPathLegAnimator? TryBuild(
        FractalType type, FractalParameters? p, double legSeconds, Random? rng = null,
        bool varyStart = false, bool varySpeed = false)
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
        Complex startOffset = default;
        double speed = 1.0;
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

            // #801 — per-leg start-position variance: a bounded random offset
            // (30–100% of amplitude, random direction) so successive legs of the
            // same region begin at a different c. Stays ≤ amplitude, so the set
            // keeps its shape.
            if (varyStart)
            {
                double mag = amp * (0.3 + 0.7 * rng.NextDouble());
                double ang = rng.NextDouble() * 2.0 * System.Math.PI;
                startOffset = new Complex(mag * System.Math.Cos(ang), mag * System.Math.Sin(ang));
            }

            // #801 — per-leg speed variance within a tasteful band (0.75×–1.75×
            // one traversal) so leg-to-leg constant motion differs without
            // strobing.
            if (varySpeed)
                speed = 0.75 + rng.NextDouble();
        }

        return new ConstantPathLegAnimator(
            name, baseValue, amp, shape, legSeconds, setter,
            startAngle: startAngle, lineAngle: lineAngle,
            startOffset: startOffset, speed: speed);
    }
}
