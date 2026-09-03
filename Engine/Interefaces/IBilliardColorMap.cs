// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/IBilliardColorMap.cs
//
// Chaotic-billiard (#627) colouring contract. ChaoticBilliardCalculator
// dispatches per-pixel colour through this interface when the active ColorMap
// implements it; otherwise it falls back to its built-in HSV-per-gate shading.
//
// The billiard outcome is categorical (which escape gate the trajectory left
// through) plus two continuous secondaries (bounce count, path length). None of
// these fit the escape-time IColorMap inputs (smooth / distance / normal /
// finalZ / dz-dc), so — exactly as Newton did with INewtonColorMap — the
// categorical outcome gets its own contract.
//
// Inputs unique to the billiard scatter:
//   gateId      Index of the angular escape-gate sector the trajectory exited
//               through (0 .. gateCount-1). -1 when the trajectory was trapped
//               (exceeded the bounce cap and never escaped); themes should paint
//               a sensible "trapped" colour (the interior of the scatter set).
//   gateCount   Total number of escape-gate sectors (BilliardGateCount).
//   bounces     Reflections consumed before escape (or the bounce cap if trapped).
//   maxBounces  Bounce cap for the frame.
//   pathLength  Total world-space path length travelled, normalised to ~[0, 1]
//               against a per-frame reference so themes need not know the scale.

namespace FracturingFog.Interefaces
{
    public interface IBilliardColorMap : IColorMap
    {
        int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength);
    }
}
