// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/HistogramThemes.cs
//
// Themes designed to pair with the histogram-equalisation slider already wired
// into the renderer (MandelbrotCalculator.ApplyHistogramEqualization).  They
// render acceptably at strength = 0 but transform into perceptually-uniform
// rank-order displays at strength = 1.
//
// Marked with ColorMapFeatures.UsesHistogram so future UI surfacing can
// auto-engage the slider when one of these themes is selected.
//
// Three sample themes:
//   • HistogramViridisMap   — Matplotlib-style viridis gradient
//   • HistogramTwilightMap  — cyclic twilight, dense bands flattened by EQ
//   • HistogramSpectralMap  — full spectral rainbow optimised for rank-order

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Viridis-style perceptual gradient designed for histogram equalisation.
    /// At EQ strength 1.0 every band carries equal pixel area, giving a
    /// scientific-publication look.
    /// </summary>
    public sealed class HistogramViridisMap : GradientColorMap
    {
        public static string Name => "Histogram - Viridis";
        public static string Category => "Histogram / Rank-Order";
        public static string Description =>
            "Matplotlib-style viridis gradient tuned for histogram equalisation. " +
            "Push the EQ slider toward 1.0 for evenly-distributed iteration bands.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesHistogram |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        public HistogramViridisMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 68,   1,  84)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 59,  82, 139)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb( 33, 145, 140)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb( 94, 201,  98)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(253, 231,  37)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(254, 255, 200)));
        }
    }

    /// <summary>
    /// Cyclic twilight gradient.  Dense at the boundary; histogram equalisation
    /// stretches the boundary detail across the full gradient.
    /// </summary>
    public sealed class HistogramTwilightMap : GradientColorMap
    {
        public static string Name => "Histogram - Twilight";
        public static string Category => "Histogram / Rank-Order";
        public static string Description =>
            "Cyclic twilight palette designed to flatten under histogram EQ.  At " +
            "strength 1.0 reveals filament structure usually compressed at the boundary.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesHistogram |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;

        public HistogramTwilightMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 25,  10,  60)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 90,  40, 130)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(190, 120, 180)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(245, 230, 220)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(200, 170, 100)));
            Stops.Add(new ColorStop(0.85f, Color.FromArgb(100,  90,  60)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 25,  10,  60)));
        }
    }

    /// <summary>
    /// Wide spectral rainbow with deliberately uneven hue spacing.  Histogram
    /// equalisation flattens the perceptual width of each hue band.
    /// </summary>
    public sealed class HistogramSpectralMap : GradientColorMap
    {
        public static string Name => "Histogram - Spectral";
        public static string Category => "Histogram / Rank-Order";
        public static string Description =>
            "Wide spectral rainbow optimised for histogram pairing.  Each hue band " +
            "covers equal pixel area at EQ strength 1.0.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesHistogram |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        public HistogramSpectralMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 94,  79, 162)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb( 50, 136, 189)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(102, 194, 165)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb(171, 221, 164)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(230, 245, 152)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(254, 224, 139)));
            Stops.Add(new ColorStop(0.82f, Color.FromArgb(253, 174,  97)));
            Stops.Add(new ColorStop(0.92f, Color.FromArgb(244, 109,  67)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(213,  62,  79)));
        }
    }
}
