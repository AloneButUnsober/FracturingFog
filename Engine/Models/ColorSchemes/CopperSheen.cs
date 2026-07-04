// Models/ColorSchemes/CopperSheen.cs
// Cycling copper palette — cycles through the copper gradient so deep-zoom
// structures remain vivid rather than saturating to a single flat tone.
// Distance modulates specularity: close to the boundary the surface gleams.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Metallic copper sheen — cycling power-curve RGB channels with a
    /// distance-based specular highlight.  Vivid at any zoom depth.
    /// </summary>
    public class CopperSheenMap : IColorMap, IGpuHlslPalette
    {
        public static string Name        => "Copper Sheen";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public static string Category    => "Metallic";
        public static string Description => "Polished cycling copper — power-curve R/G with distance specular.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance | ColorMapFeatures.Cyclic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Cycle so deep-zoom images stay vivid — one cycle every ~50 smooth-units.
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;

            // Copper channel mapping — power curves give the warm metallic look.
            float r = System.Math.Clamp(MathF.Pow(t * 1.25f, 0.60f), 0f, 1f);
            float g = System.Math.Clamp(MathF.Pow(t * 0.78f, 0.80f), 0f, 1f);
            float b = System.Math.Clamp(MathF.Pow(t * 0.40f, 1.20f), 0f, 1f);

            // Second harmonic adds banding variation visible at any depth.
            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.09f + 0.5f);
            r = System.Math.Clamp(r * (0.72f + 0.28f * band), 0f, 1f);
            g = System.Math.Clamp(g * (0.68f + 0.32f * band), 0f, 1f);
            b = System.Math.Clamp(b * (0.80f + 0.20f * band), 0f, 1f);

            // Specular highlight near the set boundary.
            float spec = 0.50f * MathF.Exp(-distance * 0.20f);
            r = System.Math.Clamp(r + spec,         0f, 1f);
            g = System.Math.Clamp(g + spec * 0.55f, 0f, 1f);
            b = System.Math.Clamp(b + spec * 0.10f, 0f, 1f);

            return ColorUtils.PackArgbF(r, g, b);
        }

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float traw = in_smooth * 0.020;
    float t = traw - floor(traw);
    float r = saturate(pow(t * 1.25, 0.60));
    float g = saturate(pow(t * 0.78, 0.80));
    float b = saturate(pow(t * 0.40, 1.20));
    float band = 0.5 + 0.5 * sin(in_smooth * 0.09 + 0.5);
    r = saturate(r * (0.72 + 0.28 * band));
    g = saturate(g * (0.68 + 0.32 * band));
    b = saturate(b * (0.80 + 0.20 * band));
    float spec = 0.50 * exp(-in_dist * 0.20);
    r = saturate(r + spec);
    g = saturate(g + spec * 0.55);
    b = saturate(b + spec * 0.10);
    return float3(r, g, b);
";

        public string PaletteId => "CopperSheenMap/v1";
    }
}
