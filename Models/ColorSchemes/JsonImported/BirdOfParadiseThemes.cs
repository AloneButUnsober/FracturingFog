// Models/ColorSchemes/JsonImported/BirdOfParadiseThemes.cs
//
// Four "Bird of Paradise" variants — tropical indigo→magenta→orange→gold→cyan
// palette across Gradient / Cycling / Phong3D / Pbr3D, generated from
// Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    internal static class BirdOfParadisePalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb( 45,   0, 100)));
            stops.Add(new ColorStop(0.10f, Color.FromArgb( 75,  10, 180)));
            stops.Add(new ColorStop(0.22f, Color.FromArgb(170,   0, 210)));
            stops.Add(new ColorStop(0.33f, Color.FromArgb(240,   0, 190)));
            stops.Add(new ColorStop(0.45f, Color.FromArgb(255,  20,  80)));
            stops.Add(new ColorStop(0.55f, Color.FromArgb(255,  80,  10)));
            stops.Add(new ColorStop(0.65f, Color.FromArgb(255, 155,   0)));
            stops.Add(new ColorStop(0.75f, Color.FromArgb(255, 225,  20)));
            stops.Add(new ColorStop(0.85f, Color.FromArgb(255, 255, 180)));
            stops.Add(new ColorStop(0.92f, Color.FromArgb(130, 248, 255)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb(  0, 210, 255)));
        }
    }

    /// <summary>"Bird of Paradise" — linear tropical gradient.</summary>
    public sealed class BirdOfParadiseMap : GradientColorMap
    {
        public static string Name => "Bird of Paradise";
        public static string Category => "Nature";
        public static string Description =>
            "Tropical gradient: deep indigo through hot magenta, orange, gold, and cyan. Derived from birdofparidise.png.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public BirdOfParadiseMap()
        {
            BirdOfParadisePalette.AddStops(Stops);
        }
    }

    /// <summary>"Bird of Paradise Cycling" — repeating tropical cycle.</summary>
    public sealed class BirdOfParadiseCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "Bird of Paradise Cycling";
        public static string Category => "Nature";
        public static string Description =>
            "Repeating tropical cycle: indigo-magenta-orange-gold-cyan. Derived from birdofparidise.png.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.016f;

        public BirdOfParadiseCyclingMap()
        {
            BirdOfParadisePalette.AddStops(Stops);
        }
    }

    /// <summary>"Bird of Paradise Phong 3D" — Phong relief with warm gold key and cool cyan fill.</summary>
    public sealed class BirdOfParadisePhong3DMap : GradientPhong3DBase
    {
        public static string Name => "Bird of Paradise Phong 3D";
        public static string Category => "Nature";
        public static string Description =>
            "Tropical gradient with warm golden key light and cool cyan fill. Derived from birdofparidise.png.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.016f;
        protected override float Steepness     => 1.55f;
        protected override float Ambient       => 0.10f;
        protected override float KeySpecScale  => 0.90f;
        protected override float FillSpecScale => 0.30f;
        protected override float FillDiffScale => 0.40f;

        public BirdOfParadisePhong3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5208651f, ly: 0.7102706f, lz: 0.47351372f,
                diffR: 1.20f, diffG: 0.85f, diffB: 0.40f,
                specR: 1.00f, specG: 0.90f, specB: 0.70f,
                shininess: 52f);
            FillLight = new LightSource(
                lx: 0.7060631f, ly: -0.3801878f, lz: 0.59743804f,
                diffR: 0.20f, diffG: 0.55f, diffB: 0.80f,
                specR: 0.30f, specG: 0.65f, specB: 0.85f,
                shininess: 28f);

            BirdOfParadisePalette.AddStops(Stops);
        }
    }

    /// <summary>"Bird of Paradise PBR" — Cook-Torrance relief with banded metalness and glow boost.</summary>
    public sealed class BirdOfParadisePbr3DMap : PbrGradient3DBase
    {
        public static string Name => "Bird of Paradise PBR";
        public static string Category => "Nature";
        public static string Description =>
            "PBR tropical: vivid golden key, electric cyan fill, gold-metallic highlights. Derived from birdofparidise.png.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.016f;
        protected override float Steepness  => 1.4f;
        protected override float Ambient    => 0.12f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        public BirdOfParadisePbr3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5208651f, ly: 0.7102706f, lz: 0.47351372f,
                diffR: 1.40f, diffG: 1.05f, diffB: 0.55f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.7060631f, ly: -0.3801878f, lz: 0.59743804f,
                diffR: 0.25f, diffG: 0.70f, diffB: 0.95f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            BirdOfParadisePalette.AddStops(Stops);
        }

        protected override float GlowBoost(float t) => 0.5f * MathF.Pow(t, 8f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.28f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.78f);
            if (t < 0.58f) return new PbrMaterial(r, g, b, metalness: 0.15f, roughness: 0.52f);
            if (t < 0.80f) return new PbrMaterial(r, g, b, metalness: 0.55f, roughness: 0.28f);
            if (t < 0.90f) return new PbrMaterial(r, g, b, metalness: 0.82f, roughness: 0.12f);
            return new PbrMaterial(r, g, b, metalness: 0.30f, roughness: 0.08f);
        }
    }
}
