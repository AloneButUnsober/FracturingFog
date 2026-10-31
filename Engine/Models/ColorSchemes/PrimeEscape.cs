// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/PrimeEscape.cs
//
// Prime Escape (Gold-Cyan) (#1486, tracker #1485): lights the pixels whose escape
// iteration count n is prime and dims the rest. Odd primes are split by n mod 4:
// n ≡ 1 → gold (#FFCC00), n ≡ 3 → cyan, n = 2 → white. Gold vs cyan is the
// blue-yellow axis, so the split survives red-green colour blindness.
//
// The test is plain trial division on both CPU and GPU, so it is exact for any n
// (no lookup table to outgrow, the bug the #1485 mock hit). It costs O(√n), less
// than the n iterations the pixel already took to escape.
//
// n is the true escape count: the theme is IColorMapHandlesInSet, so the Mandelbrot
// and escape-time calculators pass the escape iteration rather than maxIter, and
// the GPU kernel passes it as in_iter. Calculators that only hand themes maxIter
// fall back to n = floor(smooth), which can sit a few bands off (smooth lags n by
// log2 log2 of the bailout radius).
//
// Lit bands are flat. A fade across each band would need smooth − n, whose offset
// depends on the bailout radius the theme never sees (at FF's 512 it pins the fade
// at one end), and the flat split was the clearest panel in the #1485 mock.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    public class PrimeEscapeGoldCyanMap : IColorMap, IColorMapHandlesInSet, IGpuHlslPalette
    {
        public static string Name        => "Prime Escape (Gold-Cyan)";
        public static string Category    => "Algorithmic";
        public static string Description => "Lights escape counts that are prime: gold for n = 1 mod 4, cyan for n = 3 mod 4, white for 2; other counts dimmed.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public static readonly (float R, float G, float B) Gold  = (1.0f, 0.8f, 0.0f);   // #FFCC00
        public static readonly (float R, float G, float B) Cyan  = (0.1f, 0.6f, 1.0f);
        public static readonly (float R, float G, float B) White = (1.0f, 1.0f, 1.0f);
        public static readonly (float R, float G, float B) Slate = (0.16f, 0.18f, 0.26f);

        /// <summary>Trial division; exact for every non-negative n.</summary>
        public static bool IsPrime(uint n)
        {
            if (n < 2u) return false;
            if (n < 4u) return true;
            if ((n & 1u) == 0u) return false;
            for (uint d = 3u; d * d <= n; d += 2u)
                if (n % d == 0u) return false;
            return true;
        }

        public int Map(float smooth, float distance, int iterations)
        {
            int n;
            if (iterations >= MaxIterations)
            {
                // In-set sentinel is smooth = 0; a positive smooth means the calculator
                // passed maxIter for an escaped pixel, so estimate n from smooth.
                if (!(smooth > 0f)) return unchecked((int)0xFF000000);
                n = (int)MathF.Floor(smooth);
            }
            else n = iterations;
            return unchecked((int)ColorFor(n, smooth));
        }

        /// <summary>Colour of an escaped pixel with escape count <paramref name="n"/>.</summary>
        public static uint ColorFor(int n, float smooth)
        {
            if (n < 0 || !IsPrime((uint)n))
            {
                float k = 0.75f + 0.25f * MathF.Cos(smooth * 0.25f);
                return Pack(Slate.R * k, Slate.G * k, Slate.B * k);
            }
            var c = n == 2 ? White : (n & 3) == 1 ? Gold : Cyan;
            return Pack(c.R, c.G, c.B);
        }

        // Rounds like the GPU's cg_pack_bgra (without its dither term).
        private static uint Pack(float r, float g, float b)
        {
            static uint Ch(float v) => (uint)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
            return 0xFF000000u | (Ch(r) << 16) | (Ch(g) << 8) | Ch(b);
        }

        public string HlslPrelude => "";

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    uint n = (uint)max(in_iter, 0.0);
    bool prime = n >= 2u;
    if (n >= 4u)
    {
        if ((n & 1u) == 0u) prime = false;
        else
        {
            [loop] for (uint d = 3u; d * d <= n; d += 2u)
                if (n % d == 0u) { prime = false; break; }
        }
    }
    if (!prime)
        return float3(0.16, 0.18, 0.26) * (0.75 + 0.25 * cos(in_smooth * 0.25));
    return n == 2u ? float3(1.0, 1.0, 1.0)
         : (n & 3u) == 1u ? float3(1.0, 0.8, 0.0)
         : float3(0.1, 0.6, 1.0);
";

        public string PaletteId => "PrimeEscapeGoldCyanMap/v1";
    }
}
