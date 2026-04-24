// Models/ColorSchemes/CesiumPalettes.cs
//
// Three colour map themes inspired by the emission spectrum of Cesium (Cs, Z=55).
//
// Cesium's characteristic spectral lines:
//   • 455.5 nm  — deep blue-violet (primary, intense)
//   • 459.3 nm  — blue-violet
//   • 461.4 nm  — blue
//   • 469.9 nm  — blue
//   • 852.1 nm  — near-IR (rendered as dim deep-red glow at shadow edges)
//   • 894.3 nm  — near-IR
//
// Visual philosophy
//   The dominant 455–470 nm band gives Cesium its vivid blue-violet "glow".
//   Dark regions are near-black with a barely-visible deep indigo, evoking the
//   near-IR emission picked up as cool shadow detail.  Bright escape bands
//   pulse through electric blue → cyan → white-blue, simulating the brilliant
//   discharge glow of a Cs flame.  All three themes share the same underlying
//   stop set so they look like siblings.
//
// Themes provided:
//   CesiumSpectrumGradient        — linear gradient (t = smooth / maxIter)
//   CesiumSpectrumCycling         — cycling gradient (repeats across zoom depths)
//   CesiumSpectrumPhong3D         — cycling gradient + dual-light Phong (3D relief)

using System;
using System.Drawing;

using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    // ── Shared stop factory ───────────────────────────────────────────────────

    internal static class CesiumStops
    {
        /// <summary>
        /// Builds the canonical Cesium emission-spectrum gradient stops.
        ///
        /// Stop map (position → colour description):
        ///   0.00  very dark indigo-black  (near-IR shadow edge)
        ///   0.08  deep indigo             (cool shadow)
        ///   0.18  rich blue-violet        (455 nm primary line)
        ///   0.30  electric royal-blue     (461–470 nm band)
        ///   0.44  intense cerulean-blue   (peak glow)
        ///   0.57  bright sky-blue         (excited outer shell)
        ///   0.68  ice-blue / cyan-white   (near saturation)
        ///   0.80  white-blue              (overloaded glow centre)
        ///   0.90  pale icy cyan           (bloom)
        ///   1.00  back to deep indigo     (cycle wrap — looks good in cycling mode)
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
    // 1. Linear gradient — CesiumSpectrumGradient
    // =========================================================================

    /// <summary>
    /// Linear gradient across the Cesium emission spectrum.
    /// Darker (low iteration) pixels are near-black indigo; higher-iteration
    /// pixels sweep through blue-violet → royal blue → cerulean → icy white-blue.
    /// The gradient stretches once across the full iteration range.
    /// </summary>
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

        public CesiumSpectrumGradient()
        {
            Stops.AddRange(CesiumStops.Build());
        }
    }

    // =========================================================================
    // 2. Cycling gradient — CesiumSpectrumCycling
    // =========================================================================

    /// <summary>
    /// Cycling variant of the Cesium spectrum gradient.
    /// The palette repeats every ~56 smooth-iteration units so deep-zoom images
    /// stay richly coloured rather than washing out to a single hue.
    /// Multiple "pulse rings" of blue glow appear at all zoom levels.
    /// </summary>
    public sealed class CesiumSpectrumCycling : CyclingGradientColorMap
    {
        public static string Name => "Cesium Spectrum Cycling";
        public static string Category => "Spectral";
        public static string Description =>
            "Cycling Cesium spectrum — repeating blue-glow rings at all zoom depths.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Cyclic;

        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        // One full cycle every ~56 smooth units  (1/0.018 ≈ 55.6)
        protected override float CycleSpeed => 0.018f;

        public CesiumSpectrumCycling()
        {
            Stops.AddRange(CesiumStops.Build());
        }
    }

    // =========================================================================
    // 3. 3D Phong — CesiumSpectrumPhong3D
    // =========================================================================

    /// <summary>
    /// 3D Phong relief version of the Cesium spectrum cycling theme.
    ///
    /// Lighting design — "radioactive discharge" look:
    ///
    ///   Key light  — positioned high-left, cool electric blue-white (6 500 K).
    ///               Strong specular to produce brilliant white-blue highlights
    ///               on raised fractal edges, evoking a discharge spark.
    ///
    ///   Fill light — low-right, deep violet-indigo.
    ///               Soft diffuse with a subtle blue-violet specular, so shadowed
    ///               faces read as deep indigo rather than flat black — the classic
    ///               Cs near-IR afterglow.
    ///
    ///   Steepness  — 1.1  (fairly dramatic carving to show the "radioactive"
    ///               surface relief clearly).
    ///   Ambient    — 0.08 (very dark shadows — the emission is the light source).
    /// </summary>
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

        // Cycle rate matches the flat cycling sibling.
        protected override float CycleSpeed => 0.018f;

        // Fairly tight carving — pronounced ridges catch the key light crisply.
        protected override float Steepness => 1.1f;

        // Very dark ambient: the glow IS the light, shadows are near-black.
        protected override float Ambient => 0.08f;

        // Strong key specular — brilliant spark highlights.
        protected override float KeySpecScale => 1.10f;

        // Subtle fill specular — faint indigo shimmer in shadow.
        protected override float FillSpecScale => 0.18f;

        // Fill diffuse kept soft so it doesn't compete with key.
        protected override float FillDiffScale => 0.28f;

        public CesiumSpectrumPhong3D()
        {
            Stops.AddRange(CesiumStops.Build());

            // ── Key light: high-left, cool electric blue-white ────────────────
            // Direction: mostly up-left and slightly toward the viewer.
            // Diffuse: slightly warm white to contrast the cool albedo.
            // Specular: near-pure white with a faint blue tint — discharge spark.
            // High shininess (220) gives a tight, brilliant highlight.
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,       // up-left, angled to viewer
                diffR: 0.80f, diffG: 0.88f, diffB: 1.00f, // cool white diffuse
                specR: 0.90f, specG: 0.95f, specB: 1.00f, // white-blue specular spark
                shininess: 220f);

            // ── Fill light: low-right, deep violet-indigo ─────────────────────
            // Direction: down-right, barely angled toward viewer.
            // Diffuse: deep blue-violet — indigo afterglow in shadow areas.
            // Specular: muted blue-violet shimmer.
            // Low shininess (32) gives a broad, soft secondary highlight.
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.45f,        // down-right
                diffR: 0.08f, diffG: 0.15f, diffB: 0.60f,  // indigo diffuse
                specR: 0.20f, specG: 0.25f, specB: 0.80f,  // blue-violet specular
                shininess: 32f);
        }
    }
    public sealed class CesiumSpectrumPbr3D : PbrGradient3DBase
    {
        public static string Name => "Cesium 3D (PBR Bright)";
        public static string Category => "Spectral";
        public static string Description =>
            "HDR-boosted PBR Cesium emission spectrum — glowing metallic blues, " +
            "icy highlights, and deep indigo shadows.";

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Use the bright PBR profile
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        protected override float CycleSpeed => 0.018f;
        protected override float Steepness => 1.1f;
        protected override float Ambient => 0.06f;

        public CesiumSpectrumPbr3D()
        {
            Stops.AddRange(CesiumStops.Build());

            // HDR key light
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.0f, diffG: 1.1f, diffB: 1.2f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            // HDR indigo fill
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.45f,
                diffR: 0.3f, diffG: 0.5f, diffB: 1.6f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        // Cesium glow boost
        protected override float GlowBoost(float t)
        {
            // Bright bands emit more light
            return MathF.Pow(t, 8f) * 0.6f;
        }

        // Metal/Roughness profile across Cesium spectral bands
        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float metal, rough;

            if (t < 0.18f) { metal = 0.0f; rough = 0.85f; }
            else if (t < 0.44f) { metal = 0.3f; rough = 0.55f; }
            else if (t < 0.68f) { metal = 0.7f; rough = 0.30f; }
            else if (t < 0.90f) { metal = 1.0f; rough = 0.12f; }
            else { metal = 0.0f; rough = 0.80f; }

            return new PbrMaterial(r, g, b, metal, rough);
        }
    }

    /// <summary>
    /// Physically grounded PBR Cesium — subtle glow, realistic contrast,
    /// suitable as a "reference" Cesium PBR look.
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
        protected override float Steepness => 1.1f;
        protected override float Ambient => 0.08f; // slightly higher for realism

        public CesiumSpectrumPbr3D_Realistic()
        {
            Stops.AddRange(CesiumStops.Build());

            // Daylight-like key light, moderate intensity
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.2f, diffG: 1.25f, diffB: 1.3f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            // Indigo fill, softer than bright variant
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.45f,
                diffR: 0.25f, diffG: 0.40f, diffB: 1.1f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        // Minimal glow boost — mostly reflective, not emissive
        protected override float GlowBoost(float t)
        {
            return MathF.Pow(t, 8f) * 0.25f;
        }

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float metal, rough;

            if (t < 0.18f) { metal = 0.0f; rough = 0.90f; }
            else if (t < 0.44f) { metal = 0.25f; rough = 0.60f; }
            else if (t < 0.68f) { metal = 0.6f; rough = 0.38f; }
            else if (t < 0.90f) { metal = 0.9f; rough = 0.18f; }
            else { metal = 0.0f; rough = 0.85f; }

            return new PbrMaterial(r, g, b, metal, rough);
        }
    }

    /// <summary>
    /// Stylized, over-the-top glowing PBR Cesium — strong emission,
    /// aggressive HDR, and very bright spectral cores.
    /// </summary>
    public sealed class CesiumSpectrumPbr3D_UltraGlow : PbrGradient3DBase
    {
        public static string Name => "Cesium 3D (PBR UltraGlow)";
        public static string Category => "Spectral";
        public static string Description =>
            "Ultra-glowing PBR Cesium — extreme metallic cores, strong emission, " +
            "and HDR-friendly highlights.";

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        protected override float CycleSpeed => 0.018f;
        protected override float Steepness => 1.0f;
        protected override float Ambient => 0.05f;

        public CesiumSpectrumPbr3D_UltraGlow()
        {
            Stops.AddRange(CesiumStops.Build());

            // Very strong key light
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.4f, diffG: 1.6f, diffB: 1.9f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            // Strong, saturated indigo fill
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.50f, lz: 0.45f,
                diffR: 0.4f, diffG: 0.7f, diffB: 2.2f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        // Aggressive emission for bright bands
        protected override float GlowBoost(float t)
        {
            // Very low t contributes almost nothing; high t explodes
            return MathF.Pow(t, 10f) * 1.2f;
        }

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            float metal, rough;

            if (t < 0.18f) { metal = 0.0f; rough = 0.95f; }
            else if (t < 0.44f) { metal = 0.4f; rough = 0.55f; }
            else if (t < 0.68f) { metal = 0.8f; rough = 0.28f; }
            else if (t < 0.90f) { metal = 1.0f; rough = 0.10f; }
            else { metal = 0.0f; rough = 0.90f; }

            return new PbrMaterial(r, g, b, metal, rough);
        }
    }

}