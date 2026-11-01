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
// By default the lattice is fixed to the frame, so the pattern stays put when the
// fractal pans or zooms; MoireAnchor (#1633) can fix it to the fractal (Layers).
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
        /// A no-op when <see cref="Active"/> is false. Without the view (this overload)
        /// every anchor renders as <see cref="MoireAnchor.Screen"/>.</summary>
        public static void Apply(uint[] buf, int w, int h, in LightingFxData fx)
            => Apply(buf, w, h, in fx, double.NaN, double.NaN, double.NaN);

        /// <summary>#1633 — as above, with the view (centre and zoom, FF's escape-time
        /// mapping: plane = centre + (pixel − size/2)·3.5/max(w,h)/zoom), so the
        /// <see cref="MoireAnchor.Pan"/> / <see cref="MoireAnchor.Zoom"/> anchors can fix
        /// the lattice to the fractal. The centre's double value is used, so anchoring
        /// holds to about 1e12 zoom; past that the lattice jitters. A non-finite view
        /// falls back to Screen.</summary>
        public static void Apply(uint[] buf, int w, int h, in LightingFxData fx,
                                 double centerX, double centerY, double zoom)
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
            double inv = 1.0 / (s * s);
            var layers = Layers(fx.MoireAnchor, w, h, centerX, centerY, zoom);
            double ofx = fx.MoireOffsetX, ofy = fx.MoireOffsetY;
            double oxs = w * 0.5 + ofx, oys = h * 0.5 + ofy;   // Screen: the original expression, kept byte-identical

            Parallel.For(0, h, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double c = 0;
                    foreach (var L in layers)
                    {
                        double lit = 0;
                        for (int sy = 0; sy < s; sy++)
                        {
                            double v = L.Screen ? y + (sy + 0.5) / s - oys
                                                : L.OffY + (y + (sy + 0.5) / s - h * 0.5) * L.Mul - ofy;
                            for (int sx = 0; sx < s; sx++)
                            {
                                double u = L.Screen ? x + (sx + 0.5) / s - oxs
                                                    : L.OffX + (x + (sx + 0.5) / s - w * 0.5) * L.Mul - ofx;
                                long i = (long)Math.Floor((cs * u + sn * v) / cell);
                                long j = (long)Math.Floor((-sn * u + cs * v) / cellY);
                                lit += Lit(Gcd(i, j), cond, k);
                            }
                        }
                        c += L.Weight * lit * inv;
                    }
                    if (invert) c = 1.0 - c;

                    uint p = buf[row + x];
                    uint b = Channel(p & 0xFF, c, blend, strength);
                    uint g = Channel((p >> 8) & 0xFF, c, blend, strength);
                    uint r = Channel((p >> 16) & 0xFF, c, blend, strength);
                    buf[row + x] = (p & 0xFF000000u) | (r << 16) | (g << 8) | b;
                }
            });
        }

        /// <summary>One lattice placement: a pixel's lattice coordinates (before the
        /// user offset, rotation and cell) are Off + (pixel − size/2)·Mul.</summary>
        public readonly record struct Layer(double OffX, double OffY, double Mul, double Weight, bool Screen = false);

        /// <summary>#1633 — the placements for an anchor (measured on a zoom sequence;
        /// the PR records the numbers):
        /// <list type="bullet">
        /// <item>Screen: offset 0, Mul 1 — the lattice is fixed to the frame (the
        /// original behaviour, byte-identical).</item>
        /// <item>Pan: lattice coordinates = plane / pixel scale. It moves with the fractal
        /// when panning; cells stay in pixels, so zooming reshuffles it.</item>
        /// <item>Zoom: anchored in the plane at the octave's scale (zoom 2^⌊log₂ zoom⌋),
        /// so it moves and grows with the fractal; the neighbouring octave is cross-faded
        /// in by the fractional log₂ zoom, so there is no jump at each doubling. (A pure
        /// plane anchor was rejected: its cells balloon, and the moiré is gone by about
        /// zoom 4–16.)</item>
        /// </list></summary>
        public static Layer[] Layers(MoireAnchor anchor, int w, int h, double centerX, double centerY, double zoom)
        {
            bool view = double.IsFinite(centerX) && double.IsFinite(centerY) && double.IsFinite(zoom) && zoom > 0;
            if (anchor == MoireAnchor.Screen || !view) return new[] { new Layer(0, 0, 1, 1, Screen: true) };
            double unit = 3.5 / Math.Max(w, h);            // plane width of a pixel at zoom 1
            if (anchor == MoireAnchor.Pan)
            {
                double sv = unit / zoom;
                return new[] { new Layer(centerX / sv, centerY / sv, 1, 1) };
            }
            double lg = Math.Log2(zoom), oct = Math.Floor(lg), a = lg - oct;
            Layer At(double o, double wt)
            {
                double sref = unit / Math.Pow(2, o);         // pixel scale at the octave's zoom
                return new Layer(centerX / sref, centerY / sref, Math.Pow(2, o) / zoom, wt);
            }
            return a <= 0 ? new[] { At(oct, 1) } : new[] { At(oct, 1 - a), At(oct + 1, a) };
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
