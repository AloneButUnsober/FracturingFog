// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/PhaseWheel.cs
//
// Phase Wheel (Blue-Amber) (#1591): a colour-blind-safe cyclic theme for values
// that are phases. It spans exactly one turn over 0..MaxIterations (the range the
// phase views map a phase in turns onto: Julibrot Pair PathInterferencePhase,
// Slit Cascade Phase, Talbot Carpet Phase), so phase 0 and phase 1 meet with no
// seam.
//
// The wheel is a closed path in OkLab (Ottosson 2020; Docs/Resources-Bibliography.md)
// with a = 0: only lightness and the blue↔yellow axis vary,
//
//     L = 0.64 + 0.18·cos φ,   a = 0,   b = 0.13·sin φ,   φ = 2π·turns,
//
// so nothing rides on red↔green. It is FF's own construction, first used by the
// dual-orbit Böttcher domain view (#1120), which now calls ColorAt here. Measured
// with the Machado–Oliveira–Fernandes CVD simulation (2009; CvdAnalysis.cs): the
// minimum OkLab ΔE between colours a quarter-turn or more apart is 0.189 for normal
// vision, 0.186 deutan, 0.178 protan, 0.127 tritan. A six-stop Okabe–Ito blend
// (the Math Plane phase wheel) measured 0.147 / 0.096 / 0.059 / 0.065, so this
// wheel is the one shipped as a theme.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    public class PhaseWheelBlueAmberMap : IColorMap
    {
        public static string Name        => "Phase Wheel (Blue-Amber)";
        public static string Category    => "Scientific";
        public static string Description => "One colour-blind-safe turn (lightness and blue-amber only) over the whole range — for phase views.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic;

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        /// <summary>OkLab path of the wheel: L = LMid + LAmp·cos φ, a = 0, b = BAmp·sin φ.</summary>
        public const float LMid = 0.64f, LAmp = 0.18f, BAmp = 0.13f;

        /// <summary>The wheel colour at <paramref name="turns"/> (any real; one turn = 1),
        /// with lightness scaled by <paramref name="k"/> (1 = full; chroma scales by √k
        /// so dimmed colours stay in gamut).</summary>
        public static uint ColorAt(double turns, double k = 1.0)
        {
            double phi = 2.0 * Math.PI * turns;
            float L = (float)((LMid + LAmp * Math.Cos(phi)) * k);
            float B = (float)(BAmp * Math.Sin(phi) * Math.Sqrt(k));
            GradientColorSpaces.OkLabToRgb(L, 0f, B, out float r, out float g, out float b);
            return 0xFF000000u | ((uint)(r + 0.5f) << 16) | ((uint)(g + 0.5f) << 8) | (uint)(b + 0.5f);
        }

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (maxIterations <= 0 || smooth >= maxIterations) return unchecked((int)0xFF000000);
            return unchecked((int)ColorAt(smooth / (double)maxIterations));
        }
    }
}
