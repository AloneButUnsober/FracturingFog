// Models/ColorSchemes/VintageSepia.cs
// Recreates the look of an aged silver-gelatin photographic print: deep rich
// blacks, warm brown midtones, and bright cream highlights.
// The exterior distance estimate is used as a vignette signal — pixels near
// the set boundary are darkened as if the lens aperture were closing in.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Aged sepia photograph — warm brown tones with a distance-based vignette
    /// darkening the region near the set boundary.
    /// </summary>
    public class VintageSepiaMap : IColorMap
    {
        public static string Name        => "Vintage Sepia";
        public static string Category    => "Monochrome";
        public static string Description => "Aged sepia photograph with distance vignette darkening.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            float t = System.Math.Clamp(smooth / maxIterations, 0f, 1f);

            // Tone-map: gentle S-curve lifts shadows, avoids blown highlights.
            float tone = t * t * (3f - 2f * t);   // smoothstep

            // Classic sepia RGB ratios (warm brown-to-cream).
            float r = System.Math.Clamp(0.05f + 0.87f * tone, 0f, 1f);
            float g = System.Math.Clamp(0.02f + 0.64f * tone, 0f, 1f);
            float b = System.Math.Clamp(0.00f + 0.40f * tone, 0f, 1f);

            // Vignette: close to the set (small distance) darken the pixel.
            float vignette = System.Math.Clamp(distance * 0.18f, 0f, 1f);
            float vigScale = 0.35f + 0.65f * vignette;
            r *= vigScale;
            g *= vigScale;
            b *= vigScale;

            return ColorUtils.PackArgbF(r, g, b);
        }
    }
}
