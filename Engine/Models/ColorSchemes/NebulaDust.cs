// Models/ColorSchemes/NebulaDust.cs
// Hue cycles from smooth count; brightness and saturation are modulated
// by the exterior distance estimate, creating a glowing-fog effect around
// the set boundary and wispy tendrils further out.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Cosmic nebula effect — colours derived from iteration count, with
    /// distance-based brightness halos giving a dust-cloud appearance.
    /// </summary>
    public class NebulaDustMap : IColorMap
    {
        public static string Name        => "Nebula Dust";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;
        public static string Category    => "Artistic";
        public static string Description => "Cosmic fog — hue from iteration, brightness halo from distance.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.Cyclic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Hue cycles through spectrum — purple→blue→cyan→magenta
            float hue = ((smooth * 0.018f) % 1f + 1f) % 1f;

            // Saturation dips near the set edge for a desaturated halo look.
            float saturation = 0.75f + 0.25f * MathF.Exp(-distance * 0.3f);

            // Glow: bright rim close to the set, fades outward.
            float glow  = MathF.Exp(-distance * 0.08f);
            float value  = 0.15f + 0.85f * glow;
            value = System.Math.Clamp(value, 0f, 1f);

            var c = ColorUtils.Hsv(hue, saturation, value);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
