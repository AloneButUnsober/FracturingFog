// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/GoldenRatioThemes.cs
//
// Four colour-map themes that re-imagine the algorithmic Golden Ratio palette
// (Models/ColorSchemes/GoldenRatio.cs) as a gradient family with red-dominant
// base and phi-spaced rainbow accents.
//
//   GoldenRatioGradient       — linear gradient (t = smooth / maxIter)
//   GoldenRatioCycling        — cycling gradient (repeating phi-spiral rings)
//   GoldenRatioPhi3DPhong     — cycling gradient + dual-light Phong relief
//   GoldenRatioPhi3DPbr       — cycling gradient + Cook-Torrance GGX PBR
//
// Colour philosophy
//   The original phi-cycle (h_i = i*phi mod 1) lands hues at canonical
//   positions: 0.000, 0.618, 0.236, 0.854, 0.472, 0.090, 0.708, 0.326, 0.944,
//   0.562, 0.180, 0.798, 0.416, 0.034, 0.652, 0.270.  Plotted onto the iter
//   axis these form a quasi-periodic rainbow with no audible repetition —
//   the look the user described as "real".
//
//   This family keeps that DNA but biases the *base* hue toward red, so most
//   of the surface reads as crimson/ruby/maroon while the phi-spaced stops
//   punch through as rainbow accents (sapphire, gold, cyan, violet).  Red is
//   the heartwood; the spectrum decorates it like inlay.
//
// PBR design (GoldenRatioPhi3DPbr)
//   Reds        — warm dielectric (rough velvet/lacquer)
//   Gold/amber  — semi-metallic, brass-polished
//   Cyan/teal   — fully metallic, near-mirror chrome (peak shine)
//   Sapphire    — high-metal anodised aluminium
//   Violet      — semi-metallic iridescent
//   Key light is warm gold (~3000 K) from upper-right — fire on metal feel.
//   Fill is cool sapphire from lower-left — lifts shadow without washing red.
//   GlowBoost emits at the cyan/sapphire accents — gemstone fire.

using System;
using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    // ── Shared stop factory ───────────────────────────────────────────────────

    internal static class GoldenRatioStops
    {
        /// <summary>
        /// 17 stops sampling the phi-cycle, with red bias on the dominant
        /// surface and saturated rainbow accents at phi-positioned peaks.
        /// Ends at 1.00 with the same red as 0.00 so cycling wraps cleanly.
        /// </summary>
        internal static System.Collections.Generic.List<ColorStop> Build()
        {
            return new System.Collections.Generic.List<ColorStop>
            {
                new(0.000f, Color.FromArgb(140,  18,  22)),  // deep blood red (heart)
                new(0.062f, Color.FromArgb(190,  35,  30)),  // ruby crimson
                new(0.125f, Color.FromArgb(230, 165,  55)),  // amber gold accent (phi#5)
                new(0.187f, Color.FromArgb(160,  28,  35)),  // crimson return
                new(0.250f, Color.FromArgb( 70,  55, 165)),  // sapphire-indigo accent (phi#1)
                new(0.312f, Color.FromArgb(120,  20,  25)),  // dark scarlet
                new(0.375f, Color.FromArgb( 60, 195, 175)),  // teal-cyan accent (phi#10)
                new(0.437f, Color.FromArgb(175,  30,  35)),  // ruby
                new(0.500f, Color.FromArgb(155,  62, 200)),  // violet accent (phi#11)
                new(0.562f, Color.FromArgb(135,  22,  28)),  // maroon
                new(0.625f, Color.FromArgb(225, 200,  85)),  // honey gold accent (phi#3)
                new(0.687f, Color.FromArgb(180,  35,  40)),  // carmine
                new(0.750f, Color.FromArgb( 50, 110, 220)),  // azure accent (phi#9)
                new(0.812f, Color.FromArgb(150,  25,  30)),  // ruby-maroon
                new(0.875f, Color.FromArgb(235, 110,  55)),  // orange-amber accent (phi#7)
                new(0.937f, Color.FromArgb(165,  28,  32)),  // deep ruby
                new(1.000f, Color.FromArgb(140,  18,  22)),  // wrap to deep blood red
            };
        }
    }

    // =========================================================================
    // 1. Linear gradient — GoldenRatioGradient
    // =========================================================================

    /// <summary>
    /// Linear gradient sweep across the red-dominant phi accent palette.
    /// Low-iter pixels read as deep blood-red heart; high-iter pixels reach
    /// orange-amber on the way back to red.
    /// </summary>
    public sealed class GoldenRatioGradient : GradientColorMap
    {
        public static string Name => "Golden Ratio Gradient";
        public static string Category => "Spectral";
        public static string Description =>
            "Linear gradient — red-dominant base with phi-spaced rainbow accents " +
            "(sapphire, teal, gold, violet) punched through the crimson field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Perceptual;

        public new ColorPaletteType Type => ColorPaletteType.GradientLinear;

        public GoldenRatioGradient()
        {
            Stops.AddRange(GoldenRatioStops.Build());
        }
    }

    // =========================================================================
    // 2. Cycling gradient — GoldenRatioCycling
    // =========================================================================

    /// <summary>
    /// Cycling variant — repeating phi-spiral rings.  CycleSpeed matches the
    /// original algorithmic GoldenRatioMap rhythm so the band frequency feels
    /// the same as the canonical theme at every zoom depth.
    /// </summary>
    public sealed class GoldenRatioCycling : CyclingGradientColorMap
    {
        public static string Name => "Golden Ratio Cycling";
        public static string Category => "Spectral";
        public static string Description =>
            "Cycling phi-spiral — crimson rings interleaved with sapphire/gold/teal " +
            "accent bands; never washes out at deep zoom.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast;

        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        // Original GoldenRatioMap uses h = (smooth * phi) % 1 — one full cycle
        // every 1/phi ≈ 1.618 smooth-units.  For visible band density at the
        // typical iteration scale we slow that to 0.04 (one cycle / 25 smooth).
        protected override float CycleSpeed => 0.04f;

        public GoldenRatioCycling()
        {
            Stops.AddRange(GoldenRatioStops.Build());
        }
    }

    // =========================================================================
    // 3. Phong 3D — GoldenRatioPhi3DPhong
    // =========================================================================

    /// <summary>
    /// 3D Blinn-Phong relief over the red-dominant phi palette.
    /// Warm gold key light (upper-right) carves the crimson surface like
    /// firelight on lacquer; cool sapphire fill (lower-left) keeps shadow
    /// from going flat black and amplifies the cool accent bands.
    /// </summary>
    public sealed class GoldenRatioPhi3DPhong : GradientPhong3DBase
    {
        public static string Name => "Golden Ratio 3D Phi";
        public static string Category => "3D Relief";
        public static string Description =>
            "Phi-spiral red lacquer with rainbow inlay under warm gold key + cool sapphire fill — " +
            "deeply carved relief, jewel-like accents.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override float CycleSpeed => 0.04f;
        protected override float Steepness => 1.35f;     // moderately deep carving
        protected override float Ambient => 0.11f;       // dark recesses
        protected override float KeySpecScale => 1.05f;  // hot metal gleam on accents
        protected override float FillSpecScale => 0.22f;
        protected override float FillDiffScale => 0.30f;

        public GoldenRatioPhi3DPhong()
        {
            Stops.AddRange(GoldenRatioStops.Build());

            // Warm gold key — like a candle on a ruby cabochon.
            KeyLight = new LightSource(
                lx: 0.58f, ly: 0.62f, lz: 0.78f,
                diffR: 1.10f, diffG: 0.88f, diffB: 0.55f,
                specR: 1.00f, specG: 0.92f, specB: 0.68f,
                shininess: 110f);

            // Cool sapphire fill — lifts violet/blue inlay, contrasts the gold key.
            FillLight = new LightSource(
                lx: -0.62f, ly: -0.42f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.42f, diffB: 0.95f,
                specR: 0.30f, specG: 0.40f, specB: 0.95f,
                shininess: 24f);
        }
    }

    // =========================================================================
    // 4. PBR 3D — GoldenRatioPhi3DPbr
    // =========================================================================

    /// <summary>
    /// Cook–Torrance GGX PBR relief over the red-dominant phi palette.
    ///
    /// Material design — accents become metal, base stays dielectric:
    ///   Red ground         (red.R high, low total chroma) — rough warm dielectric
    ///   Amber/gold accent  (R≈G high, B low)            — semi-metal, polished brass
    ///   Cyan/teal accent   (G high, B high, R low)      — full metal, mirror chrome
    ///   Sapphire accent    (B dominant)                  — high metal, smooth aluminium
    ///   Violet accent      (R+B high, G low)            — semi-metal, iridescent
    ///
    /// Material is selected from the *raw* albedo channels rather than the
    /// gradient parameter t, so the Hermite material curve smoothly tracks any
    /// blended in-between colour produced by stop interpolation — there are
    /// never visible material seams between stops.
    ///
    /// Lighting:
    ///   Key  — warm gold from upper-right (~3000 K).  Reds glow, golds flare,
    ///           cyan/sapphire accents catch warm-contrast specular highlights.
    ///   Fill — cool sapphire from lower-left.  Lifts blue/violet shadows;
    ///           gives reds a complementary cool sheen on shadowed faces.
    ///
    /// GlowBoost — gentle gemstone-fire emission at the sapphire/cyan stops
    /// (t ≈ 0.25 and t ≈ 0.375).  Albedo-tinted, post-tone-map, no clipping.
    /// </summary>
    public sealed class GoldenRatioPhi3DPbr : PbrGradient3DBase
    {
        public static string Name => "Golden Ratio 3D Phi (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Phi-spiral red lacquer with rainbow inlay under Cook–Torrance GGX PBR — " +
            "accents become metal (cyan = chrome, sapphire = aluminium), red stays warm dielectric.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Match cycling siblings exactly.
        protected override float CycleSpeed => 0.04f;

        // Slightly less steep than Phong — PBR ambient + rim already give depth.
        protected override float Steepness => 1.30f;

        // PBRBright: HDR boosted, makes the gemstone accents flare.
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        // Modest ambient — keeps red velvet feel without lifting shadows too much.
        protected override float Ambient => 0.10f;

        public GoldenRatioPhi3DPbr()
        {
            Stops.AddRange(GoldenRatioStops.Build());

            // Warm gold key — upper-right, ~3000 K.  Strong on warm hues; pulls
            // a warm-contrast shimmer from cool accents.
            KeyLight = new LightSource(
                lx: 0.58f, ly: 0.62f, lz: 0.78f,
                diffR: 1.20f, diffG: 0.95f, diffB: 0.55f,
                specR: 0f, specG: 0f, specB: 0f,    // PBR drives spec via F0
                shininess: 1f);

            // Cool sapphire fill — lower-left.  Carries the cool accent bands
            // and gives shadowed red faces a complementary indigo edge.
            FillLight = new LightSource(
                lx: -0.62f, ly: -0.42f, lz: 0.58f,
                diffR: 0.30f, diffG: 0.45f, diffB: 1.10f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        // ── Material: chroma-driven metalness ─────────────────────────────────
        //
        // Strategy: the red base has high R, low G, low B → low chroma-without-red.
        // Accent colours have appreciable G and/or B → high non-red chroma.
        // We measure `nonRed = max(g, b)` as the metallic indicator.
        //
        // SmoothLerp keeps transitions Hermite-cubic — no banding seams.
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float nonRed = MathF.Max(g, b);
            float redness = MathF.Max(0f, r - nonRed);  // how "purely red" this stop is

            // Metalness ramp:  red base → 0.05 dielectric;  accent peaks → up to 0.85 metal.
            float metal = PbrMath.SmoothLerp(nonRed, 0.18f, 0.55f, 0.05f, 0.85f);

            // Roughness ramp:  red base → 0.78 (matte lacquer);  accents → 0.18 (mirror).
            float rough = PbrMath.SmoothLerp(nonRed, 0.18f, 0.55f, 0.78f, 0.18f);

            // Pure-red regions stay slightly warmer/rougher (velvet bias).
            rough += PbrMath.SmoothLerp(redness, 0.20f, 0.45f, 0.00f, 0.06f);

            metal = Math.Clamp(metal, 0f, 1f);
            rough = Math.Clamp(rough, 0.10f, 0.90f);

            return new PbrMaterial(r, g, b, metal, rough);
        }

        // ── Emission: gemstone fire on sapphire/cyan accents ──────────────────
        //
        // Two soft Hermite tents: one centred on the sapphire stop (t≈0.25),
        // one on the teal/cyan stop (t≈0.375).  Combined peak ≈ 0.18 — strong
        // enough to feel like inlaid gem fire, weak enough not to wash colour.
        protected override float GlowBoost(float t)
        {
            float sap = HermiteTent(t, centre: 0.250f, halfWidth: 0.06f);
            float teal = HermiteTent(t, centre: 0.375f, halfWidth: 0.06f);
            return (sap + teal) * 0.18f;
        }

        private static float HermiteTent(float t, float centre, float halfWidth)
        {
            float dist = MathF.Abs(t - centre);
            float n = Math.Clamp(1f - dist / halfWidth, 0f, 1f);
            return n * n * (3f - 2f * n);  // smooth-step
        }
    }
}