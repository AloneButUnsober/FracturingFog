// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/EscapeAngleDemoThemes.cs
//
// Renderer B (#626 / #629) — packaged "escape-angle" demo themes.
//
// Renderer B's coloring already ships in FF (ArgumentDecompositionThemes,
// FieldLinesThemes, BinaryDecompositionThemes, DomainColoringThemes all key on
// arg(z_n) at escape via the nine-parameter IColorMap.Map overload). This file
// is packaging, per scoping doc §3.2: a clearly-named demo pair that shows the
// escape-angle idea on its own, then combined with the iteration count. No new
// coloring engine — both consume finalZr / finalZi (and, for the shaded map,
// the smooth iteration count) that already reach the nine-arg overload on every
// escape-time path at every zoom depth.
//
//   • EscapeAngleDemoMap        — pure escape angle: hue = arg(z), flat value.
//                                 The continuous-hue sibling of
//                                 ArgDecompSpectralMap, dark interior.
//   • EscapeAngleIterShadedMap  — hue = arg(z), brightness = iteration depth.
//                                 The combined "angle × iter" view.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Pure escape-angle demo — continuous HSV hue keyed to <c>arg(z_n)</c> at
    /// the escape iteration, at flat saturation / value. The exterior reads as a
    /// smooth rainbow pinwheel tracing the external-ray field; the interior is
    /// the theme's in-set colour. Sibling of <see cref="ArgDecompSpectralMap"/>,
    /// surfaced under a demo name for the Renderer B packaging (#629).
    /// </summary>
    public sealed class EscapeAngleDemoMap : IColorMap
    {
        public static string Name => "Escape Angle (demo)";
        public static string Category => "Escape Angle Demo";
        public static string Description =>
            "Renderer B demo: continuous hue keyed to the escape angle arg(z) — a " +
            "smooth rainbow pinwheel over the external-ray field, dark interior.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.GradientBased;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            // Interior sentinel: the calculator zeroes finalZ for in-set pixels.
            if (finalZr == 0f && finalZi == 0f)
                return unchecked((int)((IColorMap)this).InSetColor);

            double a = Math.Atan2(finalZi, finalZr);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            var c = ColorUtils.Hsv(h, 0.85f, 0.95f);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Combined escape-angle demo — hue keyed to <c>arg(z_n)</c> while the
    /// brightness carries the (smooth) iteration depth, so a single image shows
    /// both channels at once: the angle field as colour, the escape-time bands as
    /// shading. The third panel of the Renderer B compare poster (#629).
    /// </summary>
    public sealed class EscapeAngleIterShadedMap : IColorMap
    {
        public static string Name => "Escape Angle x Iter (demo)";
        public static string Category => "Escape Angle Demo";
        public static string Description =>
            "Renderer B demo: hue = escape angle arg(z), brightness = iteration " +
            "depth. Combines the angle field (colour) with the escape-time bands " +
            "(shading) in one view.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            if (finalZr == 0f && finalZi == 0f)
                return unchecked((int)((IColorMap)this).InSetColor);

            double a = Math.Atan2(finalZi, finalZr);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);

            // Iteration depth → brightness. Normalise the smooth count over the
            // escape range so shallow escapers are dim and deep (near-boundary)
            // escapers are bright; clamp defends against smooth > MaxIterations.
            double period = MaxIterations > 0 ? MaxIterations : 256.0;
            double depth = smooth / period;
            if (depth < 0.0) depth = 0.0; else if (depth > 1.0) depth = 1.0;
            float v = 0.20f + 0.80f * (float)depth;

            var c = ColorUtils.Hsv(h, 0.85f, v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
