// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1906 (epic #1253, D1) — how deep each family's renderer resolves pixels, in one place. The
// explorer (live and --batch --explore) stops at it; later slices raise a family's entry as its
// calculator gains deeper tiers (Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §2, §9).
//
// The numbers are the calculators' own precision ceilings, not measured limits:
//   • Mandelbrot — every tier up to the exact centre (#1428): no limit.
//   • TearDrop — the engine since #1918 (R2Plan.TearDrop); its own QD-PT (~1e50) stays behind
//     UseEngine = false. It takes the full centre on every path since #1906 (IDeepCentre).
//   • Generated Z2–Z5, Tricorn and Burning Ship — the CalcGen template's deep tier is the engine
//     since #1917 (its own QD-PT, ~1e50, and QD-direct paths stay only for equations no plan
//     covers).
//   • Julia and Multibrot — the generic perturbation engine past 1e11 (#1907), double δ to ~1e290.
//     Tricorn too since #1909 (an R2Plan with the 2×2 matrix BLA), Burning Ship since #1910
//     (diffabs folds), Magnet 1 / 2 since #1911 (the quotient difference), Phoenix and Spider since
//     #1912 (a δ per state), Glynn since #1913 (elementary-function differences, BigFloatMath).
//   • #1921 (D2b): past ~1e286 the engine holds δ scaled (d·2^s) and the derivative as d·2^t, and
//     EscapeTimeCalculator takes a log zoom (IDeepZoom), so Julia, Multibrot, Tricorn, Burning Ship,
//     Magnet 1 / 2 and Glynn have no limit (Mandelbrot's). Phoenix and Spider (the two-state engine)
//     stay at 1e290: their δ is double. So does every other engine user (User Equation, the CalcGen
//     template, TearDrop): they pass a double zoom (#1957).
//   • Everything else renders in double: 1e13 (AutoExplorer.DoubleMaxZoom).

using FracturingFog.Models;

namespace FracturingFog.Abstractions.Explore;

public static class FamilyDepth
{
    /// <summary>The QD perturbation tiers' ceiling (TearDrop's and the CalcGen template's own QD-PT, now
    /// fallbacks behind the engine: #1917, #1918).</summary>
    public const double QdPerturbationMaxZoom = 1e50;

    /// <summary>#1907 — the generic perturbation engine's double-δ tier: a BigFloat reference and a
    /// per-pixel δ in double, which holds to a pixel scale of ~1e-300.</summary>
    public const double EngineDoubleDeltaMaxZoom = 1e290;

    /// <summary>The deepest zoom at which <paramref name="t"/>'s renderer resolves a pixel.</summary>
    public static double MaxZoomFor(FractalType t) => t switch
    {
        FractalType.Mandelbrot => QualityPreset.Deepest.ZoomMax,
        FractalType.TearDrop => EngineDoubleDeltaMaxZoom,                         // #1918
        FractalType.GeneratedMandelbrotZ2 or FractalType.GeneratedMandelbrotZ3
            or FractalType.GeneratedMandelbrotZ4 or FractalType.GeneratedMandelbrotZ5
            or FractalType.GeneratedTricorn or FractalType.GeneratedBurningShip => EngineDoubleDeltaMaxZoom,   // #1917
        // #1907, #1909-#1911, #1913; unlimited since #1921 (the scaled δ and derivative, the log zoom).
        FractalType.Julia or FractalType.Multibrot or FractalType.Tricorn or FractalType.BurningShip
            or FractalType.Magnet1 or FractalType.Magnet2 or FractalType.Glynn => QualityPreset.Deepest.ZoomMax,
        FractalType.Phoenix or FractalType.Spider => EngineDoubleDeltaMaxZoom,    // #1912
        _ => AutoExplorer.DoubleMaxZoom,
    };
}
