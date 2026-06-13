// Models/ColorSchemes/JsonImported/EyeTrickThemes.cs
//
// Six "Eye Trick" palettes designed to drive vivid visual effects:
// simultaneous-contrast pop, chromostereopsis, afterimage, etc.
// Generated from Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>"Acid Carnival Neon" — super-saturated cycling with black voids.</summary>
    public sealed class AcidCarnivalNeonMap : CyclingGradientColorMap
    {
        public static string Name => "Acid Carnival Neon";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Super-saturated cartoon neon. Complementary-pair clashes with black voids drive vivid afterimage and simultaneous-contrast pop.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.06f;

        public AcidCarnivalNeonMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0, 200)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0, 255, 255)));
            Stops.Add(new ColorStop(0.24f, Color.FromArgb(255, 255,   0)));
            Stops.Add(new ColorStop(0.36f, Color.FromArgb(  0, 255,  60)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(  8,   0,  12)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(255,   0,  30)));
            Stops.Add(new ColorStop(0.68f, Color.FromArgb(  0,  90, 255)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(255, 110,   0)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  8,   0,  12)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0, 200)));
        }
    }

    /// <summary>"Bubblegum Riot" — saturated bubblegum/mint/sky/banana/coral/lavender ring.</summary>
    public sealed class BubblegumRiotMap : CyclingGradientColorMap
    {
        public static string Name => "Bubblegum Riot";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Cartoon-saturated bubblegum/mint/sky/banana/coral/lavender ring. Adjacent complementaries vibrate at the seam.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.035f;

        public BubblegumRiotMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,  30, 180)));
            Stops.Add(new ColorStop(0.16f, Color.FromArgb( 60, 255, 200)));
            Stops.Add(new ColorStop(0.33f, Color.FromArgb( 80, 180, 255)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(255, 250,  60)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(255,  90, 110)));
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(200, 100, 255)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,  30, 180)));
        }
    }

    /// <summary>"Saturday Morning Cartoon" — pure-primary cycle with complementary 3D lights.</summary>
    public sealed class SaturdayMorningCartoonMap : GradientPhong3DBase
    {
        public static string Name => "Saturday Morning Cartoon";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Pure-primary cartoon palette: cherry, tangerine, lemon, grass, ocean, grape. Complementary key/fill lights amplify chroma into 3D pop.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.025f;
        protected override float Steepness     => 1.8f;
        protected override float Ambient       => 0.18f;
        protected override float KeySpecScale  => 1.10f;
        protected override float FillSpecScale => 0.45f;
        protected override float FillDiffScale => 0.55f;

        public SaturdayMorningCartoonMap()
        {
            KeyLight = new LightSource(
                lx: -0.60f, ly: 0.50f, lz: 0.62f,
                diffR: 1.00f, diffG: 0.25f, diffB: 0.95f,
                specR: 1.00f, specG: 0.40f, specB: 1.00f,
                shininess: 45f);
            FillLight = new LightSource(
                lx: 0.55f, ly: -0.45f, lz: 0.70f,
                diffR: 0.25f, diffG: 1.00f, diffB: 0.35f,
                specR: 0.40f, specG: 1.00f, specB: 0.50f,
                shininess: 25f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,  60)));
            Stops.Add(new ColorStop(0.17f, Color.FromArgb(255, 130,   0)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(255, 240,   0)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(  0, 220,  60)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(  0, 150, 255)));
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(160,   0, 255)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,  60)));
        }
    }

    /// <summary>"Vermillion Stab" — grayscale ramp pierced by a single vermillion peak.</summary>
    public sealed class VermillionStabMap : GradientColorMap
    {
        public static string Name => "Vermillion Stab";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Charcoal-to-paper grayscale ramp pierced by a single vermillion peak. Isolated chroma reads brighter than it is by contrast against the achromatic field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        public VermillionStabMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb( 40,  40,  42)));
            Stops.Add(new ColorStop(0.35f, Color.FromArgb(110, 110, 112)));
            Stops.Add(new ColorStop(0.48f, Color.FromArgb(180, 180, 182)));
            Stops.Add(new ColorStop(0.53f, Color.FromArgb(255,  30,   0)));
            Stops.Add(new ColorStop(0.58f, Color.FromArgb(220, 220, 222)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(130, 130, 132)));
            Stops.Add(new ColorStop(0.95f, Color.FromArgb(250, 250, 250)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }
    }

    /// <summary>"Oilslick Iridescence" — thin-film spectral cycle, mostly metallic.</summary>
    public sealed class OilslickIridescenceMap : PbrGradient3DBase
    {
        public static string Name => "Oilslick Iridescence";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Thin-film oilslick: full spectral wheel cycling rapidly with PBR metal/roughness bands so it shimmers and shifts like petroleum on water.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.055f;
        protected override float Steepness  => 2.1f;
        protected override float Ambient    => 0.08f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public OilslickIridescenceMap()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.45f, lz: 0.70f,
                diffR: 1.00f, diffG: 0.95f, diffB: 1.05f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 80f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.55f, diffG: 0.60f, diffB: 0.75f,
                specR: 0.65f, specG: 0.70f, specB: 0.90f,
                shininess: 35f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 80,   0, 140)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,  30, 220)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(  0, 200, 230)));
            Stops.Add(new ColorStop(0.38f, Color.FromArgb(  0, 230,  90)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(240, 240,   0)));
            Stops.Add(new ColorStop(0.62f, Color.FromArgb(255, 130,   0)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(230,   0,  60)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(200,   0, 220)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 80,   0, 140)));
        }

        protected override float GlowBoost(float t) => 0.4f * MathF.Pow(t, 5f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.18f) return new PbrMaterial(r, g, b, metalness: 0.90f, roughness: 0.15f);
            if (t < 0.36f) return new PbrMaterial(r, g, b, metalness: 0.85f, roughness: 0.22f);
            if (t < 0.55f) return new PbrMaterial(r, g, b, metalness: 0.95f, roughness: 0.12f);
            if (t < 0.72f) return new PbrMaterial(r, g, b, metalness: 0.80f, roughness: 0.28f);
            if (t < 0.90f) return new PbrMaterial(r, g, b, metalness: 0.92f, roughness: 0.18f);
            return new PbrMaterial(r, g, b, metalness: 0.88f, roughness: 0.20f);
        }
    }

    /// <summary>"Chromostereopsis" — red/blue/black voids drive chromatic depth illusion.</summary>
    public sealed class ChromostereopsisMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Pure red against pure blue on black voids drives chromostereopsis: chromatic aberration in the eye forces red to float forward and blue to recede. Green spacers cap the depth illusion.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.045f;

        public ChromostereopsisMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.38f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.48f, Color.FromArgb(  0, 255,   0)));
            Stops.Add(new ColorStop(0.58f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,   0)));
        }
    }
}
