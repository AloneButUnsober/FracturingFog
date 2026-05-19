// Models/ColorSchemes/RadioInterferenceThemes.cs
//
// Four colour-map themes inspired by RadioInterference.cs (originally RedAndBlack).
//
// The original RadioInterference palette uses 8 hue cycles of HSV across the
// iteration range, producing vivid rainbow banding on a dark background.  The
// themes here faithfully reproduce that look as:
//
//   RadioInterferenceGradient   — linear gradient (t = smooth / maxIter)
//   RadioInterferenceCycling    — cycling gradient, same 8-band repeat rate.
//   RadioInterferencePhong3D    — cycling gradient + dual Phong lights (3D relief).
//   RadioInterferencePbr3D      — cycling gradient + Cook-Torrance GGX PBR lighting.
//
// Colour philosophy
//   The "radio interference" hue sweep runs through:
//     red → orange → yellow → green → cyan → blue → violet → back to red
//   with high saturation and a brightness envelope that dims near the set
//   boundary (low smooth values) and brightens toward the escape fringe.
//   16 representative stops are sampled from the original HSV formula so
//   the gradient version is perceptually identical to the algorithmic original.
//
// PBR lighting design (RadioInterferencePbr3D)
//   The material varies smoothly around the hue wheel:
//     Reds / oranges  — warm dielectric (paint-like, rough)
//     Yellows         — transitioning to semi-metal, moderate roughness
//     Greens / cyans  — semi-metallic to fully metallic, polished (chrome peak)
//     Blues           — high metalness, smooth — anodised aluminium feel
//     Violets         — back toward dielectric, moderate roughness
//   All metalness/roughness transitions use SmoothLerp (Hermite cubic) to
//   eliminate any visible material-boundary banding.
//
//   Key light  — golden-white from upper-left, like afternoon sun on metal.
//               Warm colour temperature so reds/yellows glow appropriately;
//               cool hues gain a warm-contrast shimmer.
//   Fill light — cool lavender from lower-right, complementing the warm key
//               and lifting violet/blue shadows with a secondary cool sheen.
//
//   GlowBoost — mild warm-white emission on the cyan/green peaks (t ≈ 0.46),
//               simulating the self-luminous look of a CRT or neon discharge.
//               Applied post-tone-map, albedo-tinted — no channel blowout.

using System;
using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    // ── Shared stop factory ───────────────────────────────────────────────────

    internal static class RadioInterferenceStops
    {
        /// <summary>
        /// 17 stops sampled from the original RadioInterference HSV formula
        /// (s = 0.85, v = 0.96, one full hue revolution).
        /// The stops represent exactly one complete hue cycle [0, 1] so that
        /// the cycling variant reproduces the original 8-band banding rhythm
        /// at CycleSpeed = 1/45 (≈ 8 hue cycles per smooth-unit period).
        /// </summary>
        internal static System.Collections.Generic.List<ColorStop> Build()
        {
            const float s = 0.85f;
            const float v = 0.96f;

            var stops = new System.Collections.Generic.List<ColorStop>(17);
            for (int i = 0; i <= 16; i++)
            {
                float pos = i / 16f;
                var color = ColorUtils.Hsv(pos, s, v);   // h in [0,1)
                stops.Add(new ColorStop(pos, color));
            }
            return stops;
        }
    }

    // =========================================================================
    // 1. Linear gradient — RadioInterferenceGradient
    // =========================================================================

    /// <summary>
    /// Linear gradient that sweeps one full-hue rainbow across the complete
    /// iteration range (t = smooth / maxIterations).
    /// </summary>
    public sealed class RadioInterferenceGradient : GradientColorMap
    {
        public static string Name => "Radio Interference";
        public static string Category => "Spectral";
        public static string Description =>
            "Linear rainbow sweep — vivid HSV hues across the full iteration range.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        public new ColorPaletteType Type => ColorPaletteType.GradientLinear;

        public RadioInterferenceGradient()
        {
            Stops.AddRange(RadioInterferenceStops.Build());
        }
    }

    // =========================================================================
    // 2. Cycling gradient — RadioInterferenceCycling
    // =========================================================================

    /// <summary>
    /// Cycling variant that repeats the hue rainbow matching the original
    /// <c>smooth * 8.0f % 360.0f</c> formula — ~8 hue bands every 45 smooth units.
    /// </summary>
    public sealed class RadioInterferenceCycling : CyclingGradientColorMap
    {
        public static string Name => "Radio Interference Cycling";
        public static string Category => "Spectral";
        public static string Description =>
            "Cycling rainbow — 8 hue bands per iteration period, vivid at all zoom depths.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast;

        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        // 1 full hue cycle every 45 smooth units → CycleSpeed = 1/45
        protected override float CycleSpeed => 1f / 45f;

        public RadioInterferenceCycling()
        {
            Stops.AddRange(RadioInterferenceStops.Build());
        }
    }

    // =========================================================================
    // 3. Phong 3D — RadioInterferencePhong3D
    // =========================================================================

    /// <summary>
    /// 3D Blinn-Phong relief version of the cycling rainbow theme.
    /// Warm yellow-white key light + cool blue-violet fill.
    /// </summary>
    public sealed class RadioInterferencePhong3D : GradientPhong3DBase
    {
        public static string Name => "Radio Interference 3D";
        public static string Category => "Spectral";
        public static string Description =>
            "Rainbow cycling gradient with 3D Phong lighting — " +
            "warm key highlights and cool blue-violet shadow relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override float CycleSpeed => 1f / 45f;
        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.14f;
        protected override float KeySpecScale => 0.95f;
        protected override float FillSpecScale => 0.20f;
        protected override float FillDiffScale => 0.32f;

        public RadioInterferencePhong3D()
        {
            Stops.AddRange(RadioInterferenceStops.Build());

            KeyLight = new LightSource(
                lx: -0.60f, ly: 0.65f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.92f, diffB: 0.70f,
                specR: 1.00f, specG: 0.98f, specB: 0.85f,
                shininess: 180f);

            FillLight = new LightSource(
                lx: 0.55f, ly: -0.55f, lz: 0.60f,
                diffR: 0.25f, diffG: 0.30f, diffB: 0.90f,
                specR: 0.30f, specG: 0.35f, specB: 0.95f,
                shininess: 28f);
        }
    }

    // =========================================================================
    // 4. PBR 3D — RadioInterferencePbr3D
    // =========================================================================

    /// <summary>
    /// Cook–Torrance GGX PBR relief version of the Radio Interference cycling theme.
    ///
    /// Material design — hue-matched metalness / roughness across the rainbow wheel:
    ///
    ///   Red    (t ≈ 0.00–0.08)  dielectric, rough  — warm matte
    ///   Orange (t ≈ 0.08–0.17)  low metal,  rough  — slight warm sheen
    ///   Yellow (t ≈ 0.17–0.25)  semi-metal, medium — brass-like
    ///   Green  (t ≈ 0.25–0.42)  semi-metal, smooth — polished patina
    ///   Cyan   (t ≈ 0.42–0.54)  metallic,   mirror — chrome peak
    ///   Blue   (t ≈ 0.54–0.67)  metallic,   smooth — anodised aluminium
    ///   Violet (t ≈ 0.67–0.83)  semi-metal, medium — iridescent purple
    ///   Red    (t ≈ 0.83–1.00)  dielectric, rough  — cycle wrap, matte
    ///
    ///   All transitions are smooth (Hermite cubic) — no visible band edges.
    ///
    /// Lighting:
    ///   Key  — golden-white from upper-left (~3200 K).  Warm hues get warm
    ///          reflections; cool hues gain a warm-contrast shimmer.
    ///   Fill — cool lavender from lower-right.  Lifts blue/violet shadows
    ///          and gives the cool spectrum its own secondary sheen.
    ///
    /// GlowBoost:
    ///   Hermite-curve emission centred on cyan (t ≈ 0.46), width ±0.30.
    ///   Applied post-tone-map, albedo-tinted → no channel saturation.
    /// </summary>
    public sealed class RadioInterferencePbr3D : PbrGradient3DBase
    {
        public static string Name => "Radio Int 3D (PBR)";
        public static string Category => "Spectral";
        public static string Description =>
            "Rainbow cycling gradient with Cook–Torrance GGX PBR lighting — " +
            "hue-matched metalness across the spectrum, warm key and cool lavender fill.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Match the cycling siblings' repeat rate exactly.
        protected override float CycleSpeed => 1f / 45f;

        // Moderate relief so vivid colours are not swallowed by shadow.
        protected override float Steepness => 1.25f;

        // PBRBright: stronger radiance, gentler tone-map curve.
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        // Elevated ambient floor — no hue band goes black in shadow.
        protected override float Ambient => 0.13f;

        public RadioInterferencePbr3D()
        {
            Stops.AddRange(RadioInterferenceStops.Build());

            // ── Key light: upper-left, golden-white (~3200 K) ─────────────────
            // Warm colour temperature so red/orange/yellow hues receive
            // appropriately warm specular; blue/violet gain warm-contrast shimmer.
            KeyLight = new LightSource(
                lx: -0.58f, ly: 0.68f, lz: 0.78f,
                diffR: 1.15f, diffG: 1.00f, diffB: 0.72f,
                specR: 0f, specG: 0f, specB: 0f,   // specular handled via F0
                shininess: 1f);

            // ── Fill light: lower-right, cool lavender ────────────────────────
            // Lz = 0.62 ensures the fill reaches enough face orientations.
            // Blue-purple tint lifts violet/blue shadows; warm hue shadows gain
            // a subtle complementary cool tint.
            FillLight = new LightSource(
                lx: 0.52f, ly: -0.48f, lz: 0.62f,
                diffR: 0.55f, diffG: 0.45f, diffB: 1.10f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        // ── Material: hue-matched metalness / roughness ───────────────────────
        //
        // t travels around the hue wheel: 0=red, ~0.17=yellow, ~0.33=green,
        // ~0.46=cyan, ~0.58=blue, ~0.75=violet, 1=red (wrap).
        //
        // Metalness peaks at cyan (t≈0.46) and is lowest at reds (t≈0.00/1.00).
        // Roughness is inversely correlated: matte at red, mirror at cyan.
        // All transitions use SmoothLerp — no hard edges, no banding.
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            // ── Metalness ─────────────────────────────────────────────────────
            // Build as sum of shaped ramps, one per hue region.
            float metal = 0f;
            metal += PbrMath.SmoothLerp(t, 0.08f, 0.17f, 0.00f, 0.18f);   // orange rise
            metal += PbrMath.SmoothLerp(t, 0.17f, 0.25f, 0.00f, 0.24f);   // yellow rise
            metal += PbrMath.SmoothLerp(t, 0.25f, 0.42f, 0.00f, 0.18f);   // green rise
            metal += PbrMath.SmoothLerp(t, 0.42f, 0.54f, 0.00f, 0.30f);   // cyan peak rise
            metal -= PbrMath.SmoothLerp(t, 0.54f, 0.67f, 0.00f, 0.18f);   // blue fall start
            metal -= PbrMath.SmoothLerp(t, 0.67f, 0.83f, 0.00f, 0.42f);   // violet fall
            metal -= PbrMath.SmoothLerp(t, 0.83f, 1.00f, 0.00f, 0.30f);   // red-wrap fall
            metal = Math.Clamp(metal, 0f, 1f);

            // ── Roughness ─────────────────────────────────────────────────────
            // Starts high (matte red), dips to near-mirror at cyan, recovers.
            float rough = 0.82f;
            rough -= PbrMath.SmoothLerp(t, 0.08f, 0.25f, 0.00f, 0.22f);   // orange/yellow dip
            rough -= PbrMath.SmoothLerp(t, 0.25f, 0.54f, 0.00f, 0.52f);   // green→cyan (deepest)
            rough -= PbrMath.SmoothLerp(t, 0.54f, 0.67f, 0.00f, 0.10f);   // blue still smooth
            rough += PbrMath.SmoothLerp(t, 0.67f, 0.83f, 0.00f, 0.30f);   // violet recovering
            rough += PbrMath.SmoothLerp(t, 0.83f, 1.00f, 0.00f, 0.22f);   // red wrap → matte
            rough = Math.Clamp(rough, 0.06f, 0.92f);

            return new PbrMaterial(r, g, b, metal, rough);
        }

        // ── Emission: mild warm-white glow centred on cyan ────────────────────
        // Hermite tent curve; peak at t≈0.46, zero outside ±0.30.
        // Post-tone-map + albedo-tinted in base class — cannot blow out channels.
        protected override float GlowBoost(float t)
        {
            float dist = MathF.Abs(t - 0.46f);
            float n = Math.Clamp(1f - dist / 0.30f, 0f, 1f);
            float curve = n * n * (3f - 2f * n);   // smooth-step
            return curve * 0.28f;
        }
    }
}