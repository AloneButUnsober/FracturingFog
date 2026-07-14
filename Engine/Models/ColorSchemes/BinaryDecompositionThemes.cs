// Models/ColorSchemes/BinaryDecompositionThemes.cs
//
// Binary decomposition — at escape, the sign of Im(z_n) splits the exterior
// into two regions.  Discontinuities of the colouring trace out the external
// rays of the Mandelbrot set.  Pairs beautifully with field-line themes to
// reveal the Böttcher coordinate structure.
//
// All three themes consume the 9-parameter Map overload to read finalZi.
//
// Three sample themes:
//   • BinaryDecompClassicMap  — pure 2-tone (black / white)
//   • BinaryDecompGoldMap     — gold / navy modulated by smooth iteration
//   • BinaryDecompContourMap  — binary decomp overlaid on iteration rings

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Pure two-tone binary decomposition: white where Im(z_n) ≥ 0, black where
    /// Im(z_n) &lt; 0 at the escape iteration.  Reveals the external-ray
    /// landing structure as the boundary between the two regions.
    /// </summary>
    public sealed class BinaryDecompClassicMap : IColorMap
    {
        public static string Name => "Binary Decomp - Classic";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Pure two-tone binary decomposition by sign of Im(z) at escape.  " +
            "Discontinuities trace the external rays of the Mandelbrot set.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            return finalZi >= 0f
                ? unchecked((int)0xFFF5F5F5u)
                : unchecked((int)0xFF101010u);
        }
    }

    /// <summary>
    /// Binary decomposition modulated by smooth iteration brightness.  Gold
    /// for Im(z) ≥ 0, navy for Im(z) &lt; 0; both fade toward dark deeper in
    /// the exterior.
    /// </summary>
    public sealed class BinaryDecompGoldMap : GradientColorMap
    {
        public static string Name => "Binary Decomp - Gold / Navy";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Binary decomp keyed gold (Im(z) ≥ 0) vs navy (Im(z) < 0), with " +
            "smooth-iteration brightness fade.  Field-line rays visible against fade.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        private static readonly Color GoldHi = Color.FromArgb(250, 215, 100);
        private static readonly Color GoldLo = Color.FromArgb( 60,  40,  10);
        private static readonly Color NavyHi = Color.FromArgb(120, 160, 220);
        private static readonly Color NavyLo = Color.FromArgb( 10,  15,  40);

        public BinaryDecompGoldMap()
        {
            // Stops unused — kept so JSON export sees something.
            Stops.Add(new ColorStop(0f, GoldHi));
            Stops.Add(new ColorStop(1f, NavyLo));
        }

        public override int Map(float smooth, float distance, int maxIterations) => 0;

        public override int Map(float smooth, float distance, int iterations,
                                float nx, float ny,
                                float finalZr, float finalZi,
                                float dzdcR, float dzdcI)
        {
            float t = iterations > 0 ? smooth / iterations : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            // Boundary bright; deep exterior dim.
            float fade = MathF.Exp(-3f * t);
            Color hi = finalZi >= 0f ? GoldHi : NavyHi;
            Color lo = finalZi >= 0f ? GoldLo : NavyLo;
            byte r = (byte)(lo.R + (hi.R - lo.R) * fade);
            byte g = (byte)(lo.G + (hi.G - lo.G) * fade);
            byte b = (byte)(lo.B + (hi.B - lo.B) * fade);
            return ColorUtils.PackArgb(r, g, b);
        }
    }

    /// <summary>
    /// Binary decomposition combined with iteration-ring contours.  Inverts
    /// the upper/lower tone every integer iteration, producing a chequerboard
    /// of equipotential rings × external rays.
    /// </summary>
    public sealed class BinaryDecompContourMap : IColorMap
    {
        public static string Name => "Binary Decomp - Contour Grid";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Binary decomp × iteration rings.  Cell boundaries form the " +
            "external-ray / equipotential grid of the Böttcher coordinate.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            int iter = (int)smooth;
            bool ring = (iter & 1) == 0;
            bool upper = finalZi >= 0f;
            // Four-cell chequerboard.
            if (ring && upper)  return unchecked((int)0xFFE8D080u);  // warm light
            if (ring && !upper) return unchecked((int)0xFF202848u);  // cool dark
            if (!ring && upper) return unchecked((int)0xFF402008u);  // warm dark
            return unchecked((int)0xFFC0D0E8u);                       // cool light
        }
    }
}
