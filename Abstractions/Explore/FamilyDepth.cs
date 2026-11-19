// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1906 (epic #1253, D1) — how deep each family's renderer resolves pixels, in one place. The
// explorer (live and --batch --explore) stops at it; later slices raise a family's entry as its
// calculator gains deeper tiers (Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §2, §9).
//
// The numbers are the calculators' own precision ceilings, not measured limits:
//   • Mandelbrot — every tier up to the exact centre (#1428): no limit.
//   • TearDrop and Generated Z2–Z5 — a QD reference orbit with double δ (TearDropCalculator's
//     QD-PT; the CalcGen template's QD-PT, whose comment gives its ceiling as ~1e50). They take
//     the full centre on every path since #1906 (IDeepCentre); the probe (the poster path)
//     already gave them QD limbs.
//   • Everything else renders in double: 1e13 (AutoExplorer.DoubleMaxZoom). Generated Tricorn /
//     Burning Ship have QD-direct paths but no perturbation (CalcGen turns it off for Conj /
//     Folded), too slow to explore; they stay at double until D4 / D5.

using FracturingFog.Models;

namespace FracturingFog.Abstractions.Explore;

public static class FamilyDepth
{
    /// <summary>The QD perturbation tiers' ceiling (TearDrop, the CalcGen template).</summary>
    public const double QdPerturbationMaxZoom = 1e50;

    /// <summary>The deepest zoom at which <paramref name="t"/>'s renderer resolves a pixel.</summary>
    public static double MaxZoomFor(FractalType t) => t switch
    {
        FractalType.Mandelbrot => QualityPreset.Deepest.ZoomMax,
        FractalType.TearDrop
            or FractalType.GeneratedMandelbrotZ2 or FractalType.GeneratedMandelbrotZ3
            or FractalType.GeneratedMandelbrotZ4 or FractalType.GeneratedMandelbrotZ5 => QdPerturbationMaxZoom,
        _ => AutoExplorer.DoubleMaxZoom,
    };
}
