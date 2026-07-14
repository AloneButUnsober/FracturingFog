// Models/ColorSchemes/LemniscateThemes.cs
//
// Lemniscate / level-curve themes — edge detection at integer iteration
// boundaries.  The lemniscate of order n is the locus { c : |F_c^n(0)| = R }
// for bailout radius R, equivalent to the boundary of the dwell-n region.
//
// Implemented purely from smooth iteration count via its fractional part:
// near 0 or 1, we are within one iteration of an integer boundary → on or
// near a lemniscate.
//
// Three sample themes:
//   • LemniscateEdgeMap     — bright thin lines at each lemniscate
//   • LemniscateFilledMap   — alternating filled bands between lemniscates
//   • LemniscateContourMap  — coloured contour lines on dark ground

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Bright thin lines at every integer iteration boundary.  Pure edge
    /// detection — interior of each band reads black.
    /// </summary>
    public sealed class LemniscateEdgeMap : IColorMap
    {
        public static string Name => "Lemniscate - Bright Edges";
        public static string Category => "Lemniscates / Level Curves";
        public static string Description =>
            "Bright thin lines at every integer iteration boundary.  Edge-only " +
            "rendering of the lemniscate field; band interiors read black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        // Edge thickness in iteration-units.
        private const float EdgeWidth = 0.08f;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float frac = smooth - MathF.Floor(smooth);
            float d = Math.Min(frac, 1f - frac);
            if (d > EdgeWidth) return unchecked((int)0xFF080808u);
            float w = 1f - d / EdgeWidth;
            byte v = (byte)(w * 255f);
            return ColorUtils.PackArgb(v, v, v);
        }
    }

    /// <summary>
    /// Alternating filled bands between lemniscates.  Each band is one
    /// iteration wide; bands alternate two themed colours.
    /// </summary>
    public sealed class LemniscateFilledMap : IColorMap
    {
        public static string Name => "Lemniscate - Filled Bands";
        public static string Category => "Lemniscates / Level Curves";
        public static string Description =>
            "Alternating filled bands between consecutive lemniscates.  Each " +
            "band one iteration wide, two-tone teal / amber alternation.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            int iter = (int)smooth;
            bool a = (iter & 1) == 0;
            return a
                ? unchecked((int)0xFF30B0A8u)   // teal
                : unchecked((int)0xFFD09038u);  // amber
        }
    }

    /// <summary>
    /// Coloured contour lines on a dark ground.  Each contour's hue keys to
    /// its iteration count, giving a stratigraphic look.
    /// </summary>
    public sealed class LemniscateContourMap : IColorMap
    {
        public static string Name => "Lemniscate - Coloured Contours";
        public static string Category => "Lemniscates / Level Curves";
        public static string Description =>
            "Coloured contour lines on dark ground.  Hue keys to iteration " +
            "count, giving a stratigraphic / topographic-map appearance.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const float EdgeWidth = 0.10f;
        private const int   HueWrap   = 24;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float frac = smooth - MathF.Floor(smooth);
            float d = Math.Min(frac, 1f - frac);
            if (d > EdgeWidth) return unchecked((int)0xFF0A0A12u);
            int iter = (int)smooth;
            float h = ((iter % HueWrap) + HueWrap) % HueWrap / (float)HueWrap;
            float intensity = 1f - d / EdgeWidth;
            var c = ColorUtils.Hsv(h, 0.7f, intensity);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
