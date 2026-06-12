// Models/ColorSchemes/ArgumentDecompositionThemes.cs
//
// Argument (phase) decomposition — splits the exterior into N angular sectors
// keyed to arg(z_n) at the escape iteration.  Generalises binary decomposition
// (which is the N = 2 special case where the sectors are upper / lower half).
//
// All three themes consume the 9-parameter Map overload to read finalZr / finalZi.
//
// Three sample themes:
//   • ArgDecompQuadrantsMap — 4 sectors (quadrants), 4 distinct colours
//   • ArgDecompPinwheelMap  — 8 sectors, alternating warm / cool
//   • ArgDecompSpectralMap  — continuous hue from arg(z) (full HSV pinwheel)

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Four-way argument decomposition — one colour per quadrant of arg(z_n).
    /// Cell boundaries trace external rays at the rational angles
    /// {0, 1/4, 1/2, 3/4}.
    /// </summary>
    public sealed class ArgDecompQuadrantsMap : IColorMap
    {
        public static string Name => "Arg Decomp — Quadrants";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Four-way argument decomposition: one colour per quadrant of arg(z) " +
            "at escape.  Cell boundaries trace external rays at {0, 1/4, 1/2, 3/4}.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private static readonly int[] Quadrants = new[]
        {
            unchecked((int)0xFFE85020u), // Q1  red-orange
            unchecked((int)0xFF20A050u), // Q2  green
            unchecked((int)0xFF2050C8u), // Q3  blue
            unchecked((int)0xFFC830A8u), // Q4  magenta
        };

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            int q = (finalZr >= 0f ? 0 : 1) + (finalZi >= 0f ? 0 : 2);
            return Quadrants[q];
        }
    }

    /// <summary>
    /// Eight-way pinwheel decomposition — arg(z_n) split into 8 equal sectors,
    /// alternating warm / cool tones.
    /// </summary>
    public sealed class ArgDecompPinwheelMap : IColorMap
    {
        public static string Name => "Arg Decomp — Pinwheel (8)";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Eight-way pinwheel: arg(z) split into 8 equal sectors, " +
            "alternating warm and cool tones.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int Sectors = 8;
        private static readonly int[] Wedges = new[]
        {
            unchecked((int)0xFFE0A040u), unchecked((int)0xFF2060A0u),
            unchecked((int)0xFFE07040u), unchecked((int)0xFF208090u),
            unchecked((int)0xFFD05030u), unchecked((int)0xFF30A080u),
            unchecked((int)0xFFC04060u), unchecked((int)0xFF50B080u),
        };

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a = Math.Atan2(finalZi, finalZr);
            double t = (a / (2.0 * Math.PI)) + 0.5;
            int s = (int)(t * Sectors);
            if (s < 0) s = 0; else if (s >= Sectors) s = Sectors - 1;
            return Wedges[s];
        }
    }

    /// <summary>
    /// Continuous spectral colouring of arg(z_n) — the full HSV wheel mapped
    /// onto the exterior.  Reveals the external-ray field as a smooth
    /// rainbow pinwheel.
    /// </summary>
    public sealed class ArgDecompSpectralMap : IColorMap
    {
        public static string Name => "Arg Decomp — Spectral Pinwheel";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Continuous HSV pinwheel keyed to arg(z) at escape.  Smooth rainbow " +
            "rendering of the external-ray field.";
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
            double a = Math.Atan2(finalZi, finalZr);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            var c = ColorUtils.Hsv(h, 0.80f, 0.95f);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
