// Models/ColorSchemes/WarpedHSV.cs
// Enhances the classic HSV palette with non-linear saturation and value
// curves that respond to both the smooth iteration count and the distance
// estimate.  Deep iteration spirals gain more contrast; the set boundary
// glows with a near-white highlight.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// HSV with non-linear saturation/value warping and distance-based
    /// boundary highlighting.  Produces richer detail than plain HSV.
    /// </summary>
    public class WarpedHsvMap : IColorMap
    {
        public static string Name        => "Warped HSV";
        public static string Category    => "Classic";
        public static string Description => "HSV with nonlinear sat/val curves and distance boundary glow.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance | ColorMapFeatures.Cyclic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Hue — standard cycling.
            float hue = ((smooth * 0.021f) % 1f + 1f) % 1f;

            // Saturation warped: sin-based ripple across the iteration range.
            float satRipple = 0.5f + 0.5f * MathF.Sin(smooth * 0.08f + 0.7f);
            float sat        = System.Math.Clamp(0.55f + 0.45f * satRipple, 0f, 1f);

            // Value: base brightness from depth + nonlinear distance glow.
            float depthDim = 1.0f - 0.4f * MathF.Pow(smooth / maxIterations, 0.5f);
            float edgeGlow  = 0.5f * MathF.Exp(-distance * 0.15f);
            float val        = System.Math.Clamp(depthDim + edgeGlow, 0f, 1f);

            var c = ColorUtils.Hsv(hue, sat, val);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
