// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/BilliardThemes.cs (#627 / A2)
//
// Chaotic-billiard colour themes. Each implements IBilliardColorMap so
// ChaoticBilliardCalculator routes per-pixel colour through MapBilliard(),
// receiving the escape-gate sector (categorical), bounce count, and normalised
// path length. This mirrors the Newton-basin theme family (INewtonColorMap):
// the billiard outcome is categorical and does not fit the escape-time IColorMap
// inputs, so it gets its own contract and its own theme family.
//
// Kinds represented:
//   A. Gate categorical      — one flat colour per escape gate, no shading
//   B. Gate + bounce shade   — hue per gate, brightness fades with bounce count
//   C. Path-length gradient  — continuous cool→warm ramp by total path length
//   D. Bounce cyclic         — bounce count cycles a closed rainbow
//   E. Trapped-set reveal    — trapped trajectories bright, escapes dim; isolates
//                              the fractal trapped (Cantor) set
//
// The categorical palette is Okabe–Ito — the standard colour-blind-safe
// qualitative set — so distinct gates never rely on a red/green distinction.
//
// Each theme also implements the 3-parameter IColorMap.Map so it produces
// something defensible if accidentally selected for a non-billiard fractal;
// that path is not the intended use.

using System;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    internal static class BilliardColorHelper
    {
        // Okabe–Ito colour-blind-safe qualitative palette (8 hues). Index 0 is
        // reserved as the "trapped" colour (near-black) so escaped gates start at 1.
        public static readonly int[] OkabeIto =
        {
            unchecked((int)0xFF000000), // trapped — black
            unchecked((int)0xFFE69F00), // orange
            unchecked((int)0xFF56B4E9), // sky blue
            unchecked((int)0xFF009E73), // bluish green
            unchecked((int)0xFFF0E442), // yellow
            unchecked((int)0xFF0072B2), // blue
            unchecked((int)0xFFD55E00), // vermillion
            unchecked((int)0xFFCC79A7), // reddish purple
        };

        // Categorical colour for a gate. gate < 0 == trapped. Wraps the 7 escaped
        // hues (indices 1..7) for gate counts beyond the palette.
        public static int GateColor(int gate)
        {
            if (gate < 0) return OkabeIto[0];
            return OkabeIto[1 + (gate % 7)];
        }

        public static int Rgb(byte r, byte g, byte b)
            => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);

        public static int Shade(int rgb, float shade)
        {
            shade = Math.Clamp(shade, 0f, 1f);
            int r = (int)(((rgb >> 16) & 0xFF) * shade);
            int g = (int)(((rgb >> 8) & 0xFF) * shade);
            int b = (int)((rgb & 0xFF) * shade);
            return Rgb((byte)r, (byte)g, (byte)b);
        }

        public static int HsvArgb(float h, float s, float v)
        {
            h = (h % 1f + 1f) % 1f * 6f;
            int i = (int)Math.Floor(h);
            float f = h - i;
            float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            float rF, gF, bF;
            switch (i % 6)
            {
                case 0: rF = v; gF = t; bF = p; break;
                case 1: rF = q; gF = v; bF = p; break;
                case 2: rF = p; gF = v; bF = t; break;
                case 3: rF = p; gF = q; bF = v; break;
                case 4: rF = t; gF = p; bF = v; break;
                default: rF = v; gF = p; bF = q; break;
            }
            return Rgb((byte)(Math.Clamp(rF, 0f, 1f) * 255),
                       (byte)(Math.Clamp(gF, 0f, 1f) * 255),
                       (byte)(Math.Clamp(bF, 0f, 1f) * 255));
        }

        public static int Lerp(int a, int b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            int ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
            int br = (b >> 16) & 0xFF, bg = (b >> 8) & 0xFF, bb = b & 0xFF;
            return Rgb((byte)(ar + (br - ar) * t),
                       (byte)(ag + (bg - ag) * t),
                       (byte)(ab + (bb - ab) * t));
        }
    }

    // ── A. Gate categorical ─────────────────────────────────────────────────
    /// <summary>Flat colour-blind-safe colour per escape gate. Trapped
    /// trajectories are black. Pure basin visualisation — the fractal basin
    /// boundaries read as the interfaces between colour regions.</summary>
    public sealed class BilliardGatesMap : IBilliardColorMap
    {
        public static string Name => "Billiard - Gates (Okabe-Ito)";
        public static string Category => "Billiard / Scatter";
        public static string Description =>
            "Flat colour-blind-safe colour per escape gate (Okabe-Ito); trapped " +
            "trajectories black. Basin boundaries are the fractal.";
        public static ColorMapFeatures Features => ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 256;

        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
            => BilliardColorHelper.GateColor(gateId);
    }

    // ── B. Gate + bounce shade ──────────────────────────────────────────────
    /// <summary>Hue per escape gate, brightness fading with bounce count so the
    /// chaotic (many-bounce) filaments near basin boundaries darken toward the
    /// set. Trapped trajectories black.</summary>
    public sealed class BilliardGatesShadedMap : IBilliardColorMap
    {
        public static string Name => "Billiard - Gates Shaded";
        public static string Category => "Billiard / Scatter";
        public static string Description =>
            "Colour-blind-safe hue per escape gate, brightness fading with bounce " +
            "count — chaotic boundary filaments darken toward the trapped set.";
        public static ColorMapFeatures Features => ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 256;

        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            if (gateId < 0) return BilliardColorHelper.OkabeIto[0];
            int baseCol = BilliardColorHelper.GateColor(gateId);
            // More bounces -> darker (log-ish so a couple of bounces stays bright).
            float shade = 1f - MathF.Min(MathF.Log2(bounces + 1) / 8f, 0.8f);
            return BilliardColorHelper.Shade(baseCol, shade);
        }
    }

    // ── C. Path-length gradient ─────────────────────────────────────────────
    /// <summary>Continuous cool→warm ramp keyed to total path length (normalised).
    /// Ignores the gate — reveals how long trajectories wander before escaping,
    /// which spikes near the fractal set.</summary>
    public sealed class BilliardPathLengthMap : IBilliardColorMap
    {
        public static string Name => "Billiard - Path Length";
        public static string Category => "Billiard / Scatter";
        public static string Description =>
            "Continuous cool-to-warm ramp by total path length; long-wandering " +
            "trajectories near the fractal set glow hot.";
        public static ColorMapFeatures Features => ColorMapFeatures.GradientBased;

        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; } = 256;

        private static readonly int Cool = unchecked((int)0xFF0B0B3B); // deep indigo
        private static readonly int Mid  = unchecked((int)0xFF2E8BC0); // teal-blue
        private static readonly int Warm = unchecked((int)0xFFFFD166); // warm gold

        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            float t = Math.Clamp(pathLength, 0f, 1f);
            return t < 0.5f
                ? BilliardColorHelper.Lerp(Cool, Mid, t * 2f)
                : BilliardColorHelper.Lerp(Mid, Warm, (t - 0.5f) * 2f);
        }
    }

    // ── D. Bounce cyclic ────────────────────────────────────────────────────
    /// <summary>Bounce count cycles a closed rainbow — successive reflections
    /// step the hue, so basins with equal escape gate but different bounce parity
    /// separate into concentric bands. Trapped trajectories black.</summary>
    public sealed class BilliardBounceCyclicMap : IBilliardColorMap
    {
        public static string Name => "Billiard - Bounce Cyclic";
        public static string Category => "Billiard / Scatter";
        public static string Description =>
            "Bounce count cycles a closed rainbow — equal-gate basins with " +
            "different bounce counts split into concentric bands.";
        public static ColorMapFeatures Features => ColorMapFeatures.Cyclic;

        public ColorPaletteType Type => ColorPaletteType.GradientCyclic;
        public int MaxIterations { get; set; } = 256;

        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            if (gateId < 0) return unchecked((int)0xFF000000);
            float hue = (bounces % 12) / 12f;
            return BilliardColorHelper.HsvArgb(hue, 0.8f, 0.95f);
        }
    }

    // ── E. Trapped-set reveal ───────────────────────────────────────────────
    /// <summary>Isolates the fractal trapped set: trajectories that never escape
    /// within the bounce cap glow bright, everything that escapes is dimmed by
    /// how quickly it left. The bright residue is the (Cantor-like) invariant set
    /// of the scatterer.</summary>
    public sealed class BilliardTrappedSetMap : IBilliardColorMap
    {
        public static string Name => "Billiard - Trapped Set";
        public static string Category => "Billiard / Scatter";
        public static string Description =>
            "Trapped trajectories glow, escapes dim by exit speed — isolates the " +
            "fractal (Cantor-like) invariant set of the scatterer.";
        public static ColorMapFeatures Features => ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 256;

        private static readonly int Hot = unchecked((int)0xFFFFF2A6); // pale gold glow

        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            if (gateId < 0) return Hot;                     // trapped -> full glow
            // Escaped: brightness by how long it lingered (near-set escapes stay lit).
            float lit = MathF.Min(bounces / (float)Math.Max(1, maxBounces) * 6f, 0.55f);
            return BilliardColorHelper.Shade(Hot, lit);
        }
    }
}
