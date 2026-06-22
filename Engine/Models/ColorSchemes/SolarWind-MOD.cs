// Models/ColorSchemes/SolarWind.cs
// Simulates the visual appearance of charged-particle streams in solar photography:
// deep near-ultraviolet purple through electric blue, cyan, and finally a bright
// near-white coronal fringe.  Distance amplifies the coronal glow near the set edge.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Solar wind / coronal plasma — purple→electric blue→cyan→white corona
    /// with a distance-driven edge flare.
    /// </summary>
    public class SolarWindMapMOD : IColorMap, IGpuHlslPalette
    {
        public static string Name        => "Solar Wind MOD";

        public ColorPaletteType Type { get; } = ColorPaletteType.Scientific;

        public static string Category    => "Scientific";
        public static string Description => "Coronal plasma: deep purple→electric blue→cyan→white edge flare.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance | ColorMapFeatures.Cyclic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            float t = smooth * 0.023f;

            // Hue sweeps from 0.75 (blue-violet) toward 0.50 (cyan) as t increases.
            float hue  = 0.95f - 0.58f * ((t % 1f + 1.02f) % 1f);
            //float sat = System.Math.Clamp(0.80f + 0.20f * MathF.Sin(smooth * 0.06f), 0f, 1f);
            float sat  = System.Math.Clamp(0.88f + 0.20f * MathF.Sin(smooth * 0.08f), 0f, 2f);
            float val  = System.Math.Clamp(0.25f + 0.75f * ((t * 1.7f) % 1f), 0f, 1f);

            // Coronal edge: bright white-blue flare very close to the boundary.
            float corona = MathF.Exp(-distance * 0.22f);
            float r = 0f, g = 0f, b = 0f;
            var c = ColorUtils.Hsv(hue, sat, val);
            r = c.R / 255f + corona * 0.70f;
            g = c.G / 255f + corona * 0.80f;
            b = c.B / 255f + corona * 1.00f;

            return ColorUtils.PackArgbF(
                System.Math.Clamp(r, 0f, 1f),
                System.Math.Clamp(g, 0f, 1f),
                System.Math.Clamp(b, 0f, 1f));
        }

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        // Note: CPU clamps sat at upper bound 2.0; our cg_hsv_to_rgb passes sat
        // through unchanged so the same algebra applies; values beyond 1 over-
        // saturate the chroma exactly as on CPU.
        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float t = in_smooth * 0.023;
    float h0 = t + 1.02;
    float h0f = h0 - floor(h0);
    float hue = 0.95 - 0.58 * h0f;
    float sat = clamp(0.88 + 0.20 * sin(in_smooth * 0.08), 0.0, 2.0);
    float t17 = t * 1.7;
    float val = saturate(0.25 + 0.75 * (t17 - floor(t17)));
    float3 base_rgb = cg_hsv_to_rgb(hue, sat, val);
    float corona = exp(-in_dist * 0.22);
    return saturate(base_rgb + corona * float3(0.70, 0.80, 1.00));
";

        public string PaletteId => "SolarWindMapMOD/v1";
    }
}
