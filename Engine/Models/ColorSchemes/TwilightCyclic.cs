// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/TwilightCyclic.cs
// Three independent sine waves drive R, G, B channels with slightly different
// frequencies and phases, creating smoothly shifting bands reminiscent of the
// colour gradations in a twilight sky — blues, purples, magentas, and indigos.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Soft dusk palette — three offset sine waves across R/G/B create
    /// smooth, ever-shifting blue→purple→violet→indigo bands.
    /// </summary>
    public class TwilightCyclicMap : IColorMap, IGpuHlslPalette
    {
        public static string Name        => "Twilight Cyclic";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;
        public static string Category    => "Artistic";
        public static string Description => "Sinusoidal blue/purple/violet bands — soft dusk atmosphere.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic;

        public int MaxIterations { get; set; } = 1000;

        // Per-channel frequencies (in radians per iteration unit) and phase offsets.
        // Chosen so the three waves beat against each other, never truly repeating
        // within reasonable iteration ranges.
        private const float FreqR  = 0.0190f;
        private const float FreqG  = 0.0110f;
        private const float FreqB  = 0.0260f;
        private const float PhaseR = 0.0f;
        private const float PhaseG = 1.0472f;  // π/3
        private const float PhaseB = 2.0944f;  // 2π/3

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            float s = smooth;

            // Each channel: bias towards its own spectral range.
            // Red stays low (purple tones), Green mid (violet/mauve), Blue dominant.
            float r = 0.10f + 0.25f * (0.5f + 0.5f * MathF.Sin(s * FreqR + PhaseR));
            float g = 0.05f + 0.30f * (0.5f + 0.5f * MathF.Sin(s * FreqG + PhaseG));
            float b = 0.40f + 0.60f * (0.5f + 0.5f * MathF.Sin(s * FreqB + PhaseB));

            // Slight distance brightening near boundary.
            float glow = 1.0f + 0.3f * MathF.Exp(-distance * 0.15f);
            r = System.Math.Clamp(r * glow, 0f, 1f);
            g = System.Math.Clamp(g * glow, 0f, 1f);
            b = System.Math.Clamp(b * glow, 0f, 1f);

            return ColorUtils.PackArgbF(r, g, b);
        }

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float s = in_smooth;
    float r = 0.10 + 0.25 * (0.5 + 0.5 * sin(s * 0.0190 + 0.0));
    float g = 0.05 + 0.30 * (0.5 + 0.5 * sin(s * 0.0110 + 1.0472));
    float b = 0.40 + 0.60 * (0.5 + 0.5 * sin(s * 0.0260 + 2.0944));
    float glow = 1.0 + 0.3 * exp(-in_dist * 0.15);
    return saturate(float3(r, g, b) * glow);
";

        public string PaletteId => "TwilightCyclicMap/v1";
    }
}
