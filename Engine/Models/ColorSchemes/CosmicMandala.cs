// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/CosmicMandala.cs
// Algorithmic palette inspired by a jewelled fractal mandala image:
// black void → deep violet → magenta → copper/amber → gold → turquoise glass.
//
// Built on the Íñigo Quílez cosine palette formula
//     colour(t) = a + b * cos(2π * (c*t + d))
// with a secondary band modulation that punches in a hot amber core and
// a cool cyan rim, then a distance-driven edge darkening that keeps the
// boundary filigree visible at deep zooms.
//
// Reference: https://iquilezles.org/articles/palettes/

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    public class CosmicMandalaMap : IColorMap
    {
        public static string Name => "Cosmic Mandala";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public static string Category => "Algorithmic";
        public static string Description =>
            "Jewelled mandala palette — violet voids, copper filigree, amber cores, turquoise glass. Cosine palette with band modulation.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.Cyclic | ColorMapFeatures.Perceptual;

        public int MaxIterations { get; set; } = 1000;

        // Primary cosine palette — tuned for the violet/copper/amber/cyan run.
        // colour(t) = a + b * cos(TWO_PI * (c*t + d))
        private static readonly float[] A = { 0.520f, 0.450f, 0.500f };
        private static readonly float[] B = { 0.500f, 0.500f, 0.520f };
        private static readonly float[] C = { 1.000f, 1.000f, 0.900f };
        private static readonly float[] D = { 0.620f, 0.450f, 0.250f };

        private const float TwoPi = MathF.PI * 2f;

        public int Map(float smooth, float distance, int maxIterations)
        {
            if (smooth >= maxIterations) return unchecked((int)0xFF000000);

            // Slow cycle (~ once every 55 smooth-units) — keeps stripes wide.
            float t = smooth * 0.018f;

            float r = A[0] + B[0] * MathF.Cos(TwoPi * (C[0] * t + D[0]));
            float g = A[1] + B[1] * MathF.Cos(TwoPi * (C[1] * t + D[1]));
            float b = A[2] + B[2] * MathF.Cos(TwoPi * (C[2] * t + D[2]));

            // Secondary high-frequency amber pulse — gives the hot mandala core.
            float pulse = 0.5f + 0.5f * MathF.Cos(TwoPi * (3.0f * t + 0.18f));
            float hot = MathF.Pow(pulse, 6f);
            r += hot * 0.35f;
            g += hot * 0.22f;
            b -= hot * 0.10f;

            // Cool cyan rim modulation, offset half-cycle from the amber pulse.
            float rim = 0.5f + 0.5f * MathF.Cos(TwoPi * (3.0f * t + 0.68f));
            float cool = MathF.Pow(rim, 8f);
            r -= cool * 0.18f;
            g += cool * 0.20f;
            b += cool * 0.35f;

            // Distance-driven edge darkening keeps the copper filigree visible
            // against the bright bulbs at deep zooms.
            float edge = 1.0f - 0.30f * MathF.Exp(-distance * 0.25f);
            r *= edge; g *= edge; b *= edge;

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);

            return ColorUtils.PackArgbF(r, g, b);
        }
    }
}