// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/InteriorThemes.cs
//
// Phase 4b — Interior colouring themes.  These themes paint the IN-SET region
// of the Mandelbrot set using attracting-cycle data captured by the
// MandelbrotCalculator's Phase 4a Brent cycle-detection pass:
//
//   • period         — length of attracting cycle (1, 2, 3, …)
//   • attractor (zr, zi) — a point on the cycle
//   • |multiplier|   — |∏_{k=0}^{p−1} 2 z_k|, hyperbolic if < 1
//
// Each theme implements IInteriorAwareColorMap.MapInterior() for in-set pixels
// and a regular Map() for the exterior (rendered by the main kernel as usual).
// All five inherit GradientColorMap so the exterior is a configurable gradient.
//
// Five themes:
//   • CyclePeriodMap      — interior coloured by detected period (categorical hue).
//   • MultiplierMap       — interior coloured by |λ| (gradient: super-attracting → bulb edge).
//   • AtomDomainsMap      — period-keyed bulb tiling; distinct palette per period.
//   • ArgumentMap         — interior coloured by arg(attractor) (hue wheel).
//   • FakeDistanceEstimateMap — interior coloured by 1 − |λ| (proxy for distance-to-edge).

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // =========================================================================
    // Shared helpers
    // =========================================================================

    internal static class InteriorHelpers
    {
        public static int Argb(int r, int g, int b)
            => unchecked((int)0xFF000000 | (Clamp255(r) << 16) | (Clamp255(g) << 8) | Clamp255(b));

        private static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        /// <summary>HSV→RGB. h ∈ [0,1), s,v ∈ [0,1].</summary>
        public static int HsvToArgb(float h, float s, float v)
        {
            h = ((h % 1f) + 1f) % 1f;
            float c = v * s;
            float hp = h * 6f;
            float x = c * (1f - MathF.Abs((hp % 2f) - 1f));
            float r1, g1, b1;
            if      (hp < 1f) { r1 = c; g1 = x; b1 = 0; }
            else if (hp < 2f) { r1 = x; g1 = c; b1 = 0; }
            else if (hp < 3f) { r1 = 0; g1 = c; b1 = x; }
            else if (hp < 4f) { r1 = 0; g1 = x; b1 = c; }
            else if (hp < 5f) { r1 = x; g1 = 0; b1 = c; }
            else              { r1 = c; g1 = 0; b1 = x; }
            float m = v - c;
            return Argb(
                (int)((r1 + m) * 255f),
                (int)((g1 + m) * 255f),
                (int)((b1 + m) * 255f));
        }

        /// <summary>Map period → hue via golden-ratio rotation for maximally
        /// distinct colours across consecutive periods.</summary>
        public static float PeriodHue(int period)
        {
            if (period <= 0) return 0f;
            const float golden = 0.61803398875f;
            return (period * golden) % 1f;
        }
    }

    // =========================================================================
    // 1. Cycle Period
    // =========================================================================

    /// <summary>
    /// Interior coloured by detected attracting-cycle period.  Each period gets
    /// a distinct hue (golden-ratio rotation).  Exterior uses a dark smooth
    /// gradient so the in-set hue structure dominates.
    /// </summary>
    public sealed class CyclePeriodMap : GradientColorMap, IInteriorAwareColorMap
    {
        public static string Name => "Cycle Period";
        public static string Category => "Interior";
        public static string Description =>
            "In-set pixels coloured by attracting-cycle period (golden-ratio hue rotation). " +
            "Reveals the bulb structure of the Mandelbrot set's interior.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesInterior |
            ColorMapFeatures.GradientBased;

        public new ColorPaletteType Type => ColorPaletteType.Algorithmic;

        public CyclePeriodMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 8,  8, 14)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(40, 40, 60)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(80, 80,100)));
        }

        public int MapInterior(int period, float attractorZr, float attractorZi,
                               float multiplierMag, double cx, double cy)
        {
            if (period <= 0)
                return unchecked((int)0xFF202028);   // undetected cycle: neutral grey

            // Brightness fades slightly with multiplier — super-attracting (|λ|→0)
            // is brightest, bulb edge (|λ|→1) is dimmer.
            float hue = InteriorHelpers.PeriodHue(period);
            float v = 0.55f + 0.45f * (1f - multiplierMag);
            return InteriorHelpers.HsvToArgb(hue, 0.85f, v);
        }
    }

    // =========================================================================
    // 2. Multiplier
    // =========================================================================

    /// <summary>
    /// Interior coloured by attracting-cycle multiplier magnitude |λ|.  Bulb
    /// centres (super-attracting, |λ|→0) are blue/violet; bulb edges
    /// (parabolic, |λ|→1) are red/orange.  Exterior is a muted grey gradient.
    /// </summary>
    public sealed class MultiplierMap : GradientColorMap, IInteriorAwareColorMap
    {
        public static string Name => "Multiplier |lambda|";
        public static string Category => "Interior";
        public static string Description =>
            "In-set pixels coloured by cycle multiplier magnitude |λ|. " +
            "Bulb centres (super-attracting) blue; bulb edges (parabolic) red.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesInterior |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        public new ColorPaletteType Type => ColorPaletteType.Algorithmic;

        // Interior gradient stops (|λ| in [0,1]).
        private static readonly (float t, int rgb)[] InteriorStops =
        {
            (0.00f, 0x202060), // deep blue   — super-attracting centre
            (0.30f, 0x4060B0),
            (0.55f, 0x80B0C0),
            (0.75f, 0xE0B070),
            (0.95f, 0xE05030),
            (1.00f, 0xA01010), // dark red    — parabolic boundary
        };

        public MultiplierMap()
        {
            // Exterior: dark neutral gradient.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 6,  6, 12)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb(30, 30, 40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(60, 60, 75)));
        }

        public int MapInterior(int period, float attractorZr, float attractorZi,
                               float multiplierMag, double cx, double cy)
        {
            if (period <= 0)
                return unchecked((int)0xFF1A1A22);

            float t = Math.Clamp(multiplierMag, 0f, 1f);
            // Find the bracketing stop pair.
            int i = 0;
            while (i < InteriorStops.Length - 1 && t > InteriorStops[i + 1].t) i++;
            var (ta, rgba) = InteriorStops[i];
            var (tb, rgbb) = InteriorStops[Math.Min(i + 1, InteriorStops.Length - 1)];
            float span = tb - ta;
            float u = span > 0 ? (t - ta) / span : 0f;
            int ra = (rgba >> 16) & 0xFF, ga = (rgba >> 8) & 0xFF, ba = rgba & 0xFF;
            int rb = (rgbb >> 16) & 0xFF, gb = (rgbb >> 8) & 0xFF, bb = rgbb & 0xFF;
            int r = (int)(ra + (rb - ra) * u);
            int g = (int)(ga + (gb - ga) * u);
            int b = (int)(ba + (bb - ba) * u);
            return InteriorHelpers.Argb(r, g, b);
        }
    }

    // =========================================================================
    // 3. Atom Domains
    // =========================================================================

    /// <summary>
    /// "Atom domain" colouring — each detected period gets a flat distinct
    /// colour from a categorical palette, so each Mandelbrot bulb tiles
    /// uniformly.  Period gradients within a bulb are suppressed; the focus is
    /// the bulb partition itself.
    /// </summary>
    public sealed class AtomDomainsMap : GradientColorMap, IInteriorAwareColorMap
    {
        public static string Name => "Atom Domains";
        public static string Category => "Interior";
        public static string Description =>
            "Each Mandelbrot bulb (set of c sharing the same attracting period) " +
            "is painted a single flat colour from a categorical palette.  " +
            "Reveals the global bulb partition without intra-bulb gradient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesInterior | ColorMapFeatures.HighContrast;

        public new ColorPaletteType Type => ColorPaletteType.Algorithmic;

        // 16-colour distinct categorical palette; periods cycle through.
        private static readonly int[] BulbPalette =
        {
            0xD64545, 0xE08E3C, 0xE6C229, 0x9CC83B,
            0x4FB17A, 0x3DA8A8, 0x4781C7, 0x6757C2,
            0x9C4FB1, 0xC74A8C, 0xB85C5C, 0xC58441,
            0xA8A640, 0x66A858, 0x4790B0, 0x7878C0,
        };

        public AtomDomainsMap()
        {
            // Exterior fade-to-black gradient.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 4,  4,  8)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(28, 28, 38)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(70, 70, 90)));
        }

        public int MapInterior(int period, float attractorZr, float attractorZi,
                               float multiplierMag, double cx, double cy)
        {
            if (period <= 0)
                return unchecked((int)0xFF181820);

            int rgb = BulbPalette[(period - 1) % BulbPalette.Length];
            return unchecked((int)0xFF000000 | rgb);
        }
    }

    // =========================================================================
    // 4. Argument
    // =========================================================================

    /// <summary>
    /// Interior coloured by arg(attractor) — the angle of a point on the
    /// detected attracting cycle, mapped through an HSV hue wheel.  Bulbs split
    /// into sectors that radiate around their centre.
    /// </summary>
    public sealed class InteriorArgumentMap : GradientColorMap, IInteriorAwareColorMap
    {
        public static string Name => "Interior Argument";
        public static string Category => "Interior";
        public static string Description =>
            "In-set pixels coloured by arg(attractor) through an HSV hue wheel.  " +
            "Each bulb shows radial sectors around its attractor.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesInterior |
            ColorMapFeatures.Cyclic;

        public new ColorPaletteType Type => ColorPaletteType.Algorithmic;

        public InteriorArgumentMap()
        {
            // Exterior: cool dark gradient.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 5,  8, 15)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(20, 35, 55)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(60, 90,120)));
        }

        public int MapInterior(int period, float attractorZr, float attractorZi,
                               float multiplierMag, double cx, double cy)
        {
            if (period <= 0)
                return unchecked((int)0xFF1A1F28);

            float ang = MathF.Atan2(attractorZi, attractorZr);  // [-π, π]
            float hue = (ang / (2f * MathF.PI)) + 0.5f;          // [0, 1]
            // Saturate strongly; brightness modulated lightly by |λ| so super-
            // attracting centres are punchy.
            float v = 0.60f + 0.35f * (1f - multiplierMag);
            return InteriorHelpers.HsvToArgb(hue, 0.90f, v);
        }
    }

    // =========================================================================
    // 5. Fake Distance Estimate (interior DE proxy)
    // =========================================================================

    /// <summary>
    /// "Fake distance estimate" — interior brightness driven by (1 − |λ|), a
    /// cheap proxy for distance from the bulb's attracting fixed point to its
    /// parabolic boundary.  Super-attracting centres are darkest, edges are
    /// brightest, producing a soft 3D depth illusion inside each bulb.
    /// </summary>
    public sealed class FakeDistanceEstimateMap : GradientColorMap, IInteriorAwareColorMap
    {
        public static string Name => "Fake DE (Interior)";
        public static string Category => "Interior";
        public static string Description =>
            "Interior brightness ∝ (1 − |λ|^period), a cheap distance-to-bulb-edge proxy.  " +
            "Centres dark, edges bright — gives bulbs a soft pseudo-3D shading.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesInterior |
            ColorMapFeatures.GradientBased;

        public new ColorPaletteType Type => ColorPaletteType.Algorithmic;

        // Interior gradient: dark cool → warm bright.
        private static readonly (float t, int rgb)[] InteriorStops =
        {
            (0.00f, 0x101020),
            (0.25f, 0x303060),
            (0.55f, 0x70709A),
            (0.80f, 0xC0A878),
            (1.00f, 0xFFE0A0),
        };

        public FakeDistanceEstimateMap()
        {
            // Exterior: muted cyan-blue gradient so the bright interior pops.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 4,  6, 10)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(15, 30, 45)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(45, 70, 95)));
        }

        public int MapInterior(int period, float attractorZr, float attractorZi,
                               float multiplierMag, double cx, double cy)
        {
            if (period <= 0)
                return unchecked((int)0xFF080810);

            // (1 − |λ|^period) maps super-attracting → 1, parabolic edge → 0.
            // We invert so edges are bright: t = 1 − that = |λ|^period.
            // Actually for "distance to edge", we want edges bright = high t,
            // and centres dark = low t.  Edge: |λ|=1 → |λ|^p = 1.
            // Centre:    |λ|=0 → |λ|^p = 0.   So t = |λ|^p directly.
            float lam = Math.Clamp(multiplierMag, 0f, 1f);
            // Pow with int period — clamp to avoid 0^0.
            int p = period > 0 ? period : 1;
            float t = MathF.Pow(lam, p);
            t = Math.Clamp(t, 0f, 1f);

            int i = 0;
            while (i < InteriorStops.Length - 1 && t > InteriorStops[i + 1].t) i++;
            var (ta, rgba) = InteriorStops[i];
            var (tb, rgbb) = InteriorStops[Math.Min(i + 1, InteriorStops.Length - 1)];
            float span = tb - ta;
            float u = span > 0 ? (t - ta) / span : 0f;
            int ra = (rgba >> 16) & 0xFF, ga = (rgba >> 8) & 0xFF, ba = rgba & 0xFF;
            int rb = (rgbb >> 16) & 0xFF, gb = (rgbb >> 8) & 0xFF, bb = rgbb & 0xFF;
            int r = (int)(ra + (rb - ra) * u);
            int g = (int)(ga + (gb - ga) * u);
            int b = (int)(ba + (bb - ba) * u);
            return InteriorHelpers.Argb(r, g, b);
        }
    }
}
