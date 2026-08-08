// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/AcidWarpSpectrum.cs
//
// "Acid Warp Spectrum" — a saturated full-spectrum rainbow tuned for the Acid
// Warp mode (#250). Seamless under rotation (first colour == last) so the live
// palette cycle never shows a seam, giving the continuously-flowing classic
// Acid Warp look. Clean-room homage; not a copy of the original's palettes.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>Saturated seamless rainbow for the Acid Warp mode. A plain
    /// (non-cycling) gradient so the procedural field maps 1:1 across one full
    /// spectrum sweep — the live palette-cycle clock rotates it for motion. The
    /// seamless flag closes the loop so that rotation never shows a seam.</summary>
    public sealed class AcidWarpSpectrumMap : GradientColorMap
    {
        public static string Name => "Acid Warp Spectrum";
        public static string Category => "Psychedelic";
        public static string Description =>
            "Saturated seamless rainbow for Acid Warp — one full spectrum, flows under palette cycling.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.HighContrast |
            ColorMapFeatures.GradientBased;

        public AcidWarpSpectrumMap()
        {
            // Full hue wheel, ending back on the opening colour for a seamless loop.
            Stops.Add(new ColorStop(0.000f, Color.FromArgb(255,   0,   0))); // red
            Stops.Add(new ColorStop(0.143f, Color.FromArgb(255, 140,   0))); // orange
            Stops.Add(new ColorStop(0.286f, Color.FromArgb(255, 255,   0))); // yellow
            Stops.Add(new ColorStop(0.429f, Color.FromArgb(  0, 220,  60))); // green
            Stops.Add(new ColorStop(0.571f, Color.FromArgb(  0, 190, 255))); // cyan
            Stops.Add(new ColorStop(0.714f, Color.FromArgb( 40,  60, 255))); // blue
            Stops.Add(new ColorStop(0.857f, Color.FromArgb(170,   0, 255))); // violet
            Stops.Add(new ColorStop(1.000f, Color.FromArgb(255,   0,   0))); // back to red

            // Guarantee the loop closes even if a downstream edit nudges an end.
            SeamlessCycle = true;
        }
    }
}
