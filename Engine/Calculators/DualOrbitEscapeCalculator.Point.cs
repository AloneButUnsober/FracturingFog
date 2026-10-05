// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Point.cs (#1129, epic #1114 S15)
//
// Per-point evaluation of the pair-native (S2) and interior (S7) fields at one
// (c₀, s) — the same helpers, in the same order, as the 2D Iterate pass, so a
// consumer that samples single points (the Dual-Orbit VOLUME's surface colour)
// agrees with the 2D slice through that point by construction (the #971
// slice-agreement precedent; tested).

using System;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    /// <summary>#1129 — the 2D field scalar ([LiveFloor, maxIter], as the 2D pass
    /// stores it) of a pair-native or interior <paramref name="field"/> at c-seed
    /// (cx, cy) and parameter (sx, sy); false where the field has no value there.
    /// <paramref name="maxIter"/> and the other knobs mean what they mean on the
    /// 2D calculator (maxIter floored at 16, the bailout clamped to [2, 1e6]).</summary>
    public static bool TryFieldAt(DualOrbitField field, double cx, double cy, double sx, double sy,
        int maxIter, double bailout, double lyapunovSpan, double divergenceRatio, out double scalar,
        bool lyapunovSmooth = false)
    {
        maxIter = Math.Max(16, maxIter);
        var b = new Bailout(Math.Clamp(bailout, 2.0, 1e6));
        bool live;
        if (IsPairField(field))
        {
            var acc = PairAccum.Create(ChannelsFor(field, false), Math.Max(1.0 + 1e-9, divergenceRatio));
            RunPair(0.0, 0.0, cx, cy, sx, sy, maxIter, b, Span<double>.Empty, Span<double>.Empty, false,
                ref acc, out _, out _, out _, out _);
            scalar = PairScalar(field, acc, maxIter, Math.Max(1e-3, lyapunovSpan), quat: false, out live, lyapunovSmooth);
            return live;
        }
        if (IsInteriorField(field))
        {
            var oz = Run(0.0, 0.0, sx, sy, maxIter, b, Span<double>.Empty, out _);
            var oc = Run(cx, cy, sx, sy, maxIter, b, Span<double>.Empty, out _);
            scalar = InteriorScalar(field, oz, oc, sx, sy, maxIter, out live);
            return live;
        }
        scalar = 0.0;
        return false;
    }

    /// <summary>#1129 — the categorical class colour of PhaseLag / CyclePeriod for a
    /// stored scalar (the 2D default palette), for consumers outside the 2D pass.</summary>
    public static uint ClassColor(DualOrbitField field, double scalar)
        => CategoricalColor(field, (float)scalar) ?? 0xFF000000u;
}
