// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/Tropical.cs
// High-energy cycling palette modelled on tropical ocean and reef colouration.
// Turquoise, lime, hot pink, and coral cycle rapidly so deep-zoom images stay
// colourful even at very high iteration counts.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Vibrant turquoise → lime → hot-pink → coral cycling palette.
    /// Stays saturated at deep zoom thanks to gradient cycling.
    /// </summary>
    public class TropicalMap : CyclingGradientColorMap
    {
        public static string Name        => "Tropical";
        public static string Category    => "Artistic";
        public static string Description => "Vibrant turquoise/lime/hot-pink cycling — reef and ocean tones.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.022f;

        public TropicalMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,  20,  30)));  // deep ocean
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(  0, 180, 200)));  // turquoise
            Stops.Add(new ColorStop(0.35f, Color.FromArgb( 50, 240, 120)));  // lime
            Stops.Add(new ColorStop(0.52f, Color.FromArgb(255, 240,  50)));  // bright yellow
            Stops.Add(new ColorStop(0.68f, Color.FromArgb(255,  80, 160)));  // hot pink
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(255, 120,  60)));  // coral
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,  20,  30)));  // back to deep ocean
        }
    }
}
