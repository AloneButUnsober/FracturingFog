// Models/ColorSchemes/CopperSheen.cs
// Reproduces the characteristic copper colour ramp used in many classic fractal
// renderers.  Red rises fastest (power 0.6), green at a slower curve (power 0.8),
// blue stays dark until high iteration counts — mimicking polished/annealed copper.
// Distance modulates specularity: close to the boundary the surface gleams.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Metallic copper sheen — nonlinear power curves on R/G/B channels with
    /// a distance-based specular highlight near the set boundary.
    /// </summary>
    public class CopperSheenMap : IColorMap
    {
        public static string Name        => "Copper Sheen";
        public static string Category    => "Metallic";
        public static string Description => "Polished copper — power-curve R/G with distance specular.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            float t = System.Math.Clamp(smooth / maxIterations, 0f, 1f);

            // Copper channel mapping.
            float r = System.Math.Clamp(MathF.Pow(t * 1.25f, 0.60f), 0f, 1f);
            float g = System.Math.Clamp(MathF.Pow(t * 0.78f, 0.80f), 0f, 1f);
            float b = System.Math.Clamp(MathF.Pow(t * 0.40f, 1.20f), 0f, 1f);

            // Specular highlight: near the boundary (low distance) add a bright
            // warm flare — the characteristic copper gleam.
            float spec = 0.6f * MathF.Exp(-distance * 0.25f);
            r = System.Math.Clamp(r + spec,          0f, 1f);
            g = System.Math.Clamp(g + spec * 0.55f,  0f, 1f);
            b = System.Math.Clamp(b + spec * 0.10f,  0f, 1f);

            return ColorUtils.PackArgbF(r, g, b);
        }
    }
}
