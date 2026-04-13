// Models/ColorSchemes/Psychedelic.cs
// Very rapid hue cycling combined with oscillating saturation and value
// produces a garish, high-energy kaleidoscope of colour bands.
// Two independent sin waves create a Lissajous-like interference pattern
// that changes character as you zoom in.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Ultra-rapid hue cycling with interference-pattern saturation ripple.
    /// Produces dense, psychedelic colour banding.
    /// </summary>
    public class PsychedelicMap : IColorMap
    {
        public static string Name        => "Psychedelic";
        public static string Category    => "Artistic";
        public static string Description => "Ultra-fast rainbow cycling with interference-pattern ripple.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Fast hue cycling — ~8 full rotations across the iteration range.
            float hue = (smooth * 0.055f) % 1f;

            // Two-frequency saturation ripple gives a swirling intensity pattern.
            float ripple1 = 0.5f + 0.5f * MathF.Sin(smooth * 0.31f);
            float ripple2 = 0.5f + 0.5f * MathF.Sin(smooth * 0.11f);
            float sat     = System.Math.Clamp(0.6f + 0.4f * ripple1 * ripple2, 0f, 1f);

            // Value oscillates gently — never goes dark, never fully saturates.
            float val = 0.65f + 0.35f * MathF.Sin(smooth * 0.05f + 1.2f);
            val = System.Math.Clamp(val, 0f, 1f);

            var c = ColorUtils.Hsv(hue, sat, val);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
