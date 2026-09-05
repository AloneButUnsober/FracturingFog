// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/Cividis.cs
// Cividis — CVD-optimised, luminance-monotonic sequential colour map (roadmap
// S10.1, #392). Nuñez, Anderton & Renslow (2018): tuned so deuteranopes and
// protanopes see a near-identical, monotonic-lightness sweep — the colourblind-
// first companion to Viridis. Key stops sampled from the matplotlib cividis LUT
// (CC0). The perceptual core (Imaging/PerceptualRamp.Cividis) samples the same
// anchors in OkLab; this render theme is the selectable built-in.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// CVD-optimised dark-blue → blue-grey → khaki → yellow gradient with strictly
    /// monotonic lightness. Reads near-identically under deuteranopia / protanopia
    /// and survives greyscale — the colourblind-first sibling of Viridis.
    /// </summary>
    public class CividisColorMap : CyclingGradientColorMap
    {
        public static string Name        => "Cividis";
        public static string Category    => "Scientific";
        public static string Description => "CVD-optimised blue→grey→yellow, monotonic lightness. Colourblind-first.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual | ColorMapFeatures.GradientBased;

        public CividisColorMap()
        {
            // 11 key stops sampled from the 256-entry matplotlib cividis LUT.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,  32,  76)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb(  0,  42, 102)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 47,  62, 101)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb( 74,  78,  98)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb( 99,  93,  95)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(120, 109,  95)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(143, 126,  91)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(167, 144,  84)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(192, 163,  74)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(219, 183,  60)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 233,  69)));
        }
    }
}
