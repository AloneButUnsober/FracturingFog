// Models/ColorSchemes/Bernstein.cs
// Uses the cubic Bernstein polynomial (Bézier basis) to evaluate smooth,
// mathematically defined colour curves for each of the R, G and B channels
// independently.  This approach, popularised by Íñigo Quílez, produces
// gradient-free, artefact-free colour transitions that remain distinct across
// the full iteration range without any explicit stop interpolation.
//
// Reference: https://iquilezles.org/articles/palettes/
// The formula: colour(t) = a + b * cos(2π * (c*t + d))
// where a, b, c, d are per-channel float4 constants.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Bézier/cosine-basis colour curves — ultra-smooth, mathematically
    /// defined gradients without interpolation artefacts.
    /// Based on Íñigo Quílez's cosine palette formula.
    /// </summary>
    public class BernsteinMap : IColorMap, IGpuHlslPalette
    {
        public static string Name        => "Bernstein";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public static string Category    => "Algorithmic";
        public static string Description => "Cosine-basis palette by Íñigo Quílez — mathematically smooth, band-free.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.Perceptual;

        public int MaxIterations { get; set; } = 1000;

        // Cosine palette constants tuned for a rich blue/purple/cyan/orange range.
        // colour(t) = a + b * cos(TWO_PI * (c*t + d))
        private static readonly float[] A = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] B = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] C = { 1.000f, 0.700f, 0.400f };
        private static readonly float[] D = { 0.000f, 0.150f, 0.200f };

        private const float TwoPi = MathF.PI * 2f;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // t: cycles once every ~50 smooth-units.
            float t = smooth * 0.020f;

            float r = A[0] + B[0] * MathF.Cos(TwoPi * (C[0] * t + D[0]));
            float g = A[1] + B[1] * MathF.Cos(TwoPi * (C[1] * t + D[1]));
            float b = A[2] + B[2] * MathF.Cos(TwoPi * (C[2] * t + D[2]));

            // Light distance modulation — keeps the boundary region visible.
            float edge = 1.0f - 0.25f * MathF.Exp(-distance * 0.2f);
            r = System.Math.Clamp(r * edge, 0f, 1f);
            g = System.Math.Clamp(g * edge, 0f, 1f);
            b = System.Math.Clamp(b * edge, 0f, 1f);

            return ColorUtils.PackArgbF(r, g, b);
        }

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    const float TWO_PI = 6.2831853071795864769;
    float t = in_smooth * 0.020;
    float r = 0.5 + 0.5 * cos(TWO_PI * (1.000 * t + 0.000));
    float g = 0.5 + 0.5 * cos(TWO_PI * (0.700 * t + 0.150));
    float b = 0.5 + 0.5 * cos(TWO_PI * (0.400 * t + 0.200));
    float edge = 1.0 - 0.25 * exp(-in_dist * 0.2);
    return saturate(float3(r, g, b) * edge);
";

        public string PaletteId => "BernsteinMap/v1";
    }
}
