// Models/ColorSchemes/DigitalMatrix.cs
// Phosphor-green-on-black rendering inspired by classic CRT terminal text.
// Two simultaneous sine waves of different frequencies add scan-line banding.
// Distance darkens the outer field so the set boundary glows most brightly.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Matrix digital rain — phosphor green on black with scan-line banding
    /// and distance-based edge glow.
    /// </summary>
    public class DigitalMatrixMap : IColorMap
    {
        public static string Name        => "Digital Matrix";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public static string Category    => "Artistic";
        public static string Description => "Phosphor-green-on-black with scan-line banding and edge glow.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Primary band oscillation — coarse spacing.
            float band1 = 0.5f + 0.5f * MathF.Sin(smooth * 0.25f);
            // Secondary — fine spacing, creates interference pattern.
            float band2 = 0.5f + 0.5f * MathF.Sin(smooth * 0.07f);
            float combined = band1 * band2;

            // Exponential distance falloff brightens the area near the set.
            float glow = MathF.Exp(-distance * 0.12f);
            float v    = System.Math.Clamp(combined * (0.3f + 0.7f * glow), 0f, 1f);

            // Pure phosphor green — only green channel carries brightness.
            byte g = (byte)(v * 255f);
            // Slight blue tint at high brightness for a cold-screen feel.
            byte b = (byte)(v * v * 80f);

            return unchecked((int)0xFF000000 | (g << 8) | b);
        }
    }
}
