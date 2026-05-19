// Models/ColorSchemes/CesiumColorThemes.cs  — v2
//
// Updated to use PbrGradient3DBase v2.  Changes per theme:
//
//   CesiumSpectrumPbr3D            — fill Lz raised 0.45 → 0.60 so fill light
//                                    actually illuminates faces the key misses.
//                                    BuildMaterial() now uses SmoothLerp() instead
//                                    of hard if/else so banding disappears.
//
//   CesiumSpectrumPbr3D_Realistic  — same fill Lz fix + same SmoothLerp material.
//
//   CesiumSpectrumPbr3D_UltraGlow  — same fixes + GlowBoost reduced from 1.2→0.55
//                                    (post-tone-map glow no longer blows out channels).
//
// The non-PBR themes (CesiumSpectrumGradient, CesiumSpectrumCycling,
// CesiumSpectrumPhong3D) are unchanged from v1.

using System;
using System.Drawing;

using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    // ── Shared stop factory ───────────────────────────────────────────────────

    internal static class CesiumStops
    {
        /// <summary>
        /// Canonical Cesium emission-spectrum gradient stops (unchanged from v1).
        /// </summary>
        internal static System.Collections.Generic.List<ColorStop> Build()
        {
            return new System.Collections.Generic.List<ColorStop>
            {
                new(0.00f, Color.FromArgb(  4,   2,  18)),   // near-black indigo
                new(0.08f, Color.FromArgb( 10,   6,  60)),   // deep indigo
                new(0.18f, Color.FromArgb( 35,  12, 140)),   // blue-violet  ~455 nm
                new(0.30f, Color.FromArgb( 25,  60, 210)),   // royal blue   ~461 nm
                new(0.44f, Color.FromArgb(  8, 110, 235)),   // cerulean     ~470 nm
                new(0.57f, Color.FromArgb( 40, 170, 255)),   // sky-blue glow
                new(0.68f, Color.FromArgb(120, 210, 255)),   // ice-blue
                new(0.80f, Color.FromArgb(190, 230, 255)),   // white-blue bloom
                new(0.90f, Color.FromArgb(210, 240, 255)),   // icy cyan highlight
                new(1.00f, Color.FromArgb( 12,  10,  72)),   // wrap back to indigo
            };
        }
    }

    // =========================================================================
    // 1. Linear gradient — CesiumSpectrumGradient   (unchanged)
    // =========================================================================

    public sealed class CesiumSpectrumGradient : GradientColorMap
    {
        public static string Name => "Cesium Spectrum";
        public static string Category => "Spectral";
        public static string Description =>
            "Linear gradient across the Cesium emission spectrum — " +
            "deep indigo shadows to electric blue-white glow.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;
        public new ColorPaletteType Type => ColorPaletteType.GradientLinear;

        public CesiumSpectrumGradient() { Stops.AddRange(CesiumStops.Build()); }
    }

    // =========================================================================
    // 2. Cycling gradient — CesiumSpectrumCycling   (unchanged)
    // =========================================================================

    public sealed class CesiumSpectrumCycling : CyclingGradientColorMap
    {
        public static string Name => "Cesium Spectrum Cycling";
        public static string Category => "Spectral";
        public static string Description =>
            "Cycling Cesium spectrum — repeating blue-glow rings at all zoom depths.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;
        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;
        protected override float CycleSpeed => 0.018f;

        public CesiumSpectrumCycling() { Stops.AddRange(CesiumStops.Build()); }
    }

    // =========================================================================
    // 3. Phong 3D — CesiumSpectrumPhong3D   (unchanged)
    // =========================================================================

    public sealed class CesiumSpectrumPhong3D : GradientPhong3DBase
    {
        public static string Name => "Cesium Spectrum 3D";
        public static string Category => "Spectral";
        public static string Description =>
            "Cesium emission spectrum with 3D Phong lighting — " +
            "electric blue-white sparks on raised ridges, deep indigo shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;
        protected override float CycleSpeed  => 0.018f;
        protected override float Steepness   => 1.1f;
        protected override float Ambient     => 0.08f;
        protected override float KeySpecScale  => 1.10f;
        protected override float FillSpecScale => 0.18f;
        protected override float FillDiffScale => 0.28f;

        public CesiumSpectrumPhong3D()
        {
            Stops.AddRange(CesiumStops.Build());
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 0.80f, diffG: 0.88f, diffB: 1.00f,
                specR: 0.90f, specG: 0.95f, specB: 1.00f,
                shininess: 220f);
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.60f,  // Lz raised 0.45→0.60
                diffR: 0.08f, diffG: 0.15f, diffB: 0.60f,
                specR: 0.20f, specG: 0.25f, specB: 0.80f,
                shininess: 32f);
        }
    }

    // =========================================================================
    // 4. PBR Bright — CesiumSpectrumPbr3D   (FIXED)
    // =========================================================================

    /// <summary>
    /// HDR-boosted PBR Cesium — glowing metallic blues, icy highlights, deep
    /// indigo shadows.  Uses PbrGradient3DBase v2 (smooth material ramps,
    /// corrected tone-mapping and glow).
    /// </summary>
    public sealed class CesiumSpectrumPbr3D : PbrGradient3DBase
    {
        public static string Name => "Cesium 3D (PBR Bright)";
        public static string Category => "Spectral";
        public static string Description =>
            "HDR-boosted PBR Cesium emission spectrum — glowing metallic blues, " +
            "icy highlights, and deep indigo shadows.";
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float CycleSpeed => 0.018f;
        protected override float Steepness  => 1.1f;
        protected override float Ambient    => 0.10f;   // raised from 0.06

        public CesiumSpectrumPbr3D()
        {
            Stops.AddRange(CesiumStops.Build());

            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.0f, diffG: 1.1f, diffB: 1.2f,
                specR: 0f,   specG: 0f,   specB: 0f,   // spec handled via F0 in PBR
                shininess: 1f);

            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.60f,     // Lz raised 0.45→0.60
                diffR: 0.3f, diffG: 0.5f, diffB: 1.6f,
                specR: 0f,   specG: 0f,   specB: 0f,
                shininess: 1f);
        }

        // FIX: SmoothLerp replaces hard breakpoints — no more banding.
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            // Metalness ramps: dark indigo (0) → dielectric → semi-metal → full metal → back
            float metal =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.00f, 0.10f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f, 0.40f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f, 0.35f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.15f);
            metal = Math.Clamp(metal, 0f, 1f);

            // Roughness ramps from matte (shadow) to polished (bright band) and back.
            float rough =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.85f, 0.65f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f,-0.20f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f,-0.25f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.00f);
            rough = Math.Clamp(rough, 0.08f, 0.92f);

            return new PbrMaterial(r, g, b, metal, rough);
        }

        // FIX: glow is post-tone-map and albedo-tinted — no channel blowout.
        protected override float GlowBoost(float t)
            => MathF.Pow(t, 6f) * 0.45f;
    }

    // =========================================================================
    // 5. PBR Realistic — CesiumSpectrumPbr3D_Realistic   (FIXED)
    // =========================================================================

    /// <summary>
    /// Physically grounded PBR Cesium — realistic contrast, subtle glow,
    /// correct tone-mapping.
    /// </summary>
    public sealed class CesiumSpectrumPbr3D_Realistic : PbrGradient3DBase
    {
        public static string Name => "Cesium 3D (PBR Realistic)";
        public static string Category => "Spectral";
        public static string Description =>
            "Physically-based Cesium emission spectrum — realistic metallic blues, " +
            "controlled highlights, and deep but readable shadows.";
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float CycleSpeed => 0.018f;
        protected override float Steepness  => 1.1f;
        protected override float Ambient    => 0.12f;   // raised from 0.08 — shadows now readable

        public CesiumSpectrumPbr3D_Realistic()
        {
            Stops.AddRange(CesiumStops.Build());

            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.2f, diffG: 1.25f, diffB: 1.3f,
                specR: 0f,   specG: 0f,    specB: 0f,
                shininess: 1f);

            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.60f,     // Lz raised 0.45→0.60
                diffR: 0.25f, diffG: 0.40f, diffB: 1.1f,
                specR: 0f,    specG: 0f,    specB: 0f,
                shininess: 1f);
        }

        // FIX: smooth material ramp — no banding.
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float metal =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.00f, 0.08f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f, 0.28f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f, 0.35f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.19f);
            metal = Math.Clamp(metal, 0f, 1f);

            float rough =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.90f, 0.70f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f,-0.15f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f,-0.22f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.05f);
            rough = Math.Clamp(rough, 0.08f, 0.95f);

            return new PbrMaterial(r, g, b, metal, rough);
        }

        // Minimal emission — mostly reflective.
        protected override float GlowBoost(float t)
            => MathF.Pow(t, 8f) * 0.20f;
    }

    // =========================================================================
    // 6. PBR UltraGlow — CesiumSpectrumPbr3D_UltraGlow   (FIXED)
    // =========================================================================

    /// <summary>
    /// Stylized over-the-top glowing PBR Cesium — strong emission,
    /// aggressive HDR, very bright spectral cores.  Glow is now
    /// applied post-tone-map and albedo-tinted so no channel blows out.
    /// </summary>
    public sealed class CesiumSpectrumPbr3D_UltraGlow : PbrGradient3DBase
    {
        public static string Name => "Cesium 3D (PBR UltraGlow)";
        public static string Category => "Spectral";
        public static string Description =>
            "Ultra-glowing PBR Cesium — extreme metallic cores, strong emission, " +
            "HDR-friendly highlights.";
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float CycleSpeed => 0.018f;
        protected override float Steepness  => 1.0f;
        protected override float Ambient    => 0.08f;

        public CesiumSpectrumPbr3D_UltraGlow()
        {
            Stops.AddRange(CesiumStops.Build());

            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.4f, diffG: 1.6f, diffB: 1.9f,
                specR: 0f,   specG: 0f,   specB: 0f,
                shininess: 1f);

            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.60f,     // Lz raised 0.45→0.60
                diffR: 0.4f, diffG: 0.7f, diffB: 2.2f,
                specR: 0f,   specG: 0f,   specB: 0f,
                shininess: 1f);
        }

        // FIX: smooth material ramp — no banding.
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float metal =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.00f, 0.10f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f, 0.40f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f, 0.40f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.10f);
            metal = Math.Clamp(metal, 0f, 1f);

            float rough =
                PbrMath.SmoothLerp(t, 0.00f, 0.18f, 0.95f, 0.65f) +
                PbrMath.SmoothLerp(t, 0.18f, 0.44f, 0.00f,-0.15f) +
                PbrMath.SmoothLerp(t, 0.44f, 0.68f, 0.00f,-0.28f) +
                PbrMath.SmoothLerp(t, 0.68f, 0.90f, 0.00f, 0.05f);
            rough = Math.Clamp(rough, 0.05f, 0.95f);

            return new PbrMaterial(r, g, b, metal, rough);
        }

        // FIX: reduced and albedo-tinted — was adding raw unbalanced RGB offsets.
        protected override float GlowBoost(float t)
            => MathF.Pow(t, 8f) * 0.55f;
    }
}
