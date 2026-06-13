// Models/ColorSchemes/FieldLinesThemes.cs
//
// Field-line / Böttcher-angle colourings.  The Böttcher coordinate maps the
// exterior of the Mandelbrot set conformally onto the exterior of the unit
// disk.  Its argument — the external angle — is approximated at escape by
// arg(z_n).  Pairs naturally with binary decomposition (which uses sign of
// Im(z_n)) to render the full Böttcher coordinate grid.
//
// All three themes consume the 9-parameter Map overload to read finalZr / finalZi.
//
// Three sample themes:
//   • FieldLinesDiscreteMap     — N discrete external rays at rational angles
//   • FieldLinesBinaryComboMap  — field lines × binary decomp full grid
//   • FieldLinesContinuousMap   — continuous arg colouring, dim background

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// 16 discrete external rays at rational angles {k / 16, k = 0..15}.  Bright
    /// lines on a near-black background — pure ray visualisation.
    /// </summary>
    public sealed class FieldLinesDiscreteMap : IColorMap
    {
        public static string Name => "Field Lines — 16 External Rays";
        public static string Category => "Field Lines / Böttcher";
        public static string Description =>
            "Sixteen external rays at rational angles {k/16, k = 0..15}.  Bright " +
            "lines on near-black background — pure Böttcher-angle visualisation.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int RayCount = 16;
        // Angular thickness of each ray, in fractions of full circle.
        private const float RayWidth = 0.008f;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a = Math.Atan2(finalZi, finalZr);
            float frac = (float)((a / (2.0 * Math.PI)) + 0.5);
            float bin = frac * RayCount;
            float d = Math.Abs(bin - MathF.Round(bin)) / RayCount;
            if (d > RayWidth) return unchecked((int)0xFF050810u);
            byte v = (byte)(255f * (1f - d / RayWidth));
            return ColorUtils.PackArgb(v, v, (byte)(v * 0.9f));
        }
    }

    /// <summary>
    /// Combined visualisation — external rays (arg) × equipotential rings
    /// (binary decomp).  Renders the full Böttcher coordinate grid as a
    /// chequerboard on the exterior.
    /// </summary>
    public sealed class FieldLinesBinaryComboMap : IColorMap
    {
        public static string Name => "Field Lines — Böttcher Grid";
        public static string Category => "Field Lines / Böttcher";
        public static string Description =>
            "Full Böttcher coordinate grid: 8 external rays × binary decomposition. " +
            "Reads as a chequered conformal map of the exterior to the unit disk.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int Sectors = 8;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a = Math.Atan2(finalZi, finalZr);
            float frac = (float)((a / (2.0 * Math.PI)) + 0.5);
            int sec = (int)(frac * Sectors);
            if (sec < 0) sec = 0; else if (sec >= Sectors) sec = Sectors - 1;
            int iter = (int)smooth;
            bool ring = (iter & 1) == 0;
            bool warm = ((sec ^ (ring ? 0 : 1)) & 1) == 0;
            // Two-level chequerboard with hue variation per sector.
            float h = sec / (float)Sectors;
            float v = warm ? 0.95f : 0.35f;
            float s = warm ? 0.6f  : 0.7f;
            var c = ColorUtils.Hsv(h, s, v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Continuous Böttcher-angle colouring — full HSV pinwheel with a faint
    /// brightness fade by potential.  Reads as a smooth angular flow.
    /// </summary>
    public sealed class FieldLinesContinuousMap : IColorMap
    {
        public static string Name => "Field Lines — Continuous Flow";
        public static string Category => "Field Lines / Böttcher";
        public static string Description =>
            "Continuous Böttcher-angle pinwheel with potential-driven brightness fade.  " +
            "Smooth flow visualisation of the external angle field.";
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
            double a = Math.Atan2(finalZi, finalZr);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            float t = iterations > 0 ? smooth / iterations : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float v = 0.35f + 0.65f * MathF.Exp(-2f * t);
            var c = ColorUtils.Hsv(h, 0.75f, v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
