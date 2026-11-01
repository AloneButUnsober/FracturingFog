// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/CoprimeMoire.cs
//
// Coprime moiré (#1488, tracker #1485): an artistic screen-space post layer that
// lights the "visible" lattice points — pairs (i, j) with gcd(|i|, |j|) = 1 — and
// blends that mask over the finished display frame. The fine moiré comes from
// aliasing between the coprime pattern and the pixel grid, so every knob that moves
// the lattice against the grid (cell size, rotation, offset, aspect) moves the
// moiré, and supersampling fades it. The visible-lattice-point set is classical
// number theory (Docs/Resources-Bibliography.md).
//
// Pixel (x, y), subsample (sx, sy) of S × S:
//   u = x + (sx + ½)/S − w/2 − OffsetX,   v = y + (sy + ½)/S − h/2 − OffsetY
//   (u, v) rotated by MoireRotation, then i = ⌊u′ / cell⌋, j = ⌊v′ / (cell·aspect)⌋
//   lit(i, j) per MoireCondition on g = gcd(|i|, |j|); coverage = mean over subsamples
// then each channel v ∈ [0, 1] becomes v·(1 − strength) + blend(v, coverage)·strength.
//
// Screen-space by design: the pattern stays put when the fractal pans or zooms.
// Shared by the live upload (FractalRenderHost), the offscreen poster path
// (PosterRenderer) and the batch video / slideshow frames, so they cannot drift.

using System;
using System.Threading.Tasks;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering
{
    public static class CoprimeMoire
    {
        /// <summary>True when the layer would change the frame (strength above 0).</summary>
        public static bool Active(in LightingFxData fx) => fx.MoireStrength > 0;

        /// <summary>Applies the layer to <paramref name="buf"/> (BGRA, row-major
        /// <paramref name="w"/> × <paramref name="h"/>) in place. Alpha is kept.
        /// A no-op when <see cref="Active"/> is false.</summary>
        public static void Apply(uint[] buf, int w, int h, in LightingFxData fx)
        {
            if (!Active(fx) || w <= 0 || h <= 0 || buf.Length < (long)w * h) return;

            double strength = Math.Clamp(fx.MoireStrength, 0.0, 1.0);
            double cell = Math.Clamp(fx.MoireCellSize, 0.25, 64.0);
            double cellY = cell * Math.Clamp(fx.MoireAspect, 0.25, 4.0);
            int s = Math.Clamp(fx.MoireSamples, 1, 4);
            int k = Math.Clamp(fx.MoireConditionK, 1, 64);
            var cond = fx.MoireCondition;
            var blend = fx.MoireBlend;
            bool invert = fx.MoireInvert;
            double rad = fx.MoireRotation * (Math.PI / 180.0);
            double cs = Math.Cos(rad), sn = Math.Sin(rad);
            double ox = w * 0.5 + fx.MoireOffsetX, oy = h * 0.5 + fx.MoireOffsetY;
            double inv = 1.0 / (s * s);

            Parallel.For(0, h, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double lit = 0;
                    for (int sy = 0; sy < s; sy++)
                    {
                        double v = y + (sy + 0.5) / s - oy;
                        for (int sx = 0; sx < s; sx++)
                        {
                            double u = x + (sx + 0.5) / s - ox;
                            long i = (long)Math.Floor((cs * u + sn * v) / cell);
                            long j = (long)Math.Floor((-sn * u + cs * v) / cellY);
                            lit += Lit(Gcd(i, j), cond, k);
                        }
                    }
                    double c = lit * inv;
                    if (invert) c = 1.0 - c;

                    uint p = buf[row + x];
                    uint b = Channel(p & 0xFF, c, blend, strength);
                    uint g = Channel((p >> 8) & 0xFF, c, blend, strength);
                    uint r = Channel((p >> 16) & 0xFF, c, blend, strength);
                    buf[row + x] = (p & 0xFF000000u) | (r << 16) | (g << 8) | b;
                }
            });
        }

        /// <summary>Brightness of a lattice point with gcd <paramref name="g"/> (0 at the origin).</summary>
        public static double Lit(long g, MoireCondition cond, int k) => cond switch
        {
            MoireCondition.Coprime => g == 1 ? 1.0 : 0.0,
            MoireCondition.GcdEquals => g == k ? 1.0 : 0.0,
            MoireCondition.GcdAtMost => g >= 1 && g <= k ? 1.0 : 0.0,
            MoireCondition.GcdIntensity => g >= 1 ? 1.0 / g : 0.0,
            _ => 0.0,
        };

        private static long Gcd(long a, long b)
        {
            a = Math.Abs(a); b = Math.Abs(b);
            while (b != 0) { long t = a % b; a = b; b = t; }
            return a;
        }

        private static uint Channel(uint byteValue, double c, MoireBlendMode mode, double strength)
        {
            double v = byteValue / 255.0;
            double blended = mode switch
            {
                MoireBlendMode.Screen => 1.0 - (1.0 - v) * (1.0 - c),
                MoireBlendMode.Overlay => v < 0.5 ? 2.0 * v * c : 1.0 - 2.0 * (1.0 - v) * (1.0 - c),
                _ => v * c,
            };
            double o = v * (1.0 - strength) + blended * strength;   // exact at strength 0 and 1
            return (uint)Math.Clamp((int)(o * 255.0 + 0.5), 0, 255);
        }
    }
}
