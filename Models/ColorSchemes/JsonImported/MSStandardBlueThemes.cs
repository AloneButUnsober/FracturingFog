// Models/ColorSchemes/JsonImported/MSStandardBlueThemes.cs
//
// Four "MS Standard Blue" variants — navy/royal/white/gold/burgundy palette
// (classic Mandelbrot Standard) across Gradient / Cycling / Phong3D / Pbr3D.
// Generated from Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    internal static class MSStandardBluePalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,   7, 100)));
            stops.Add(new ColorStop(0.16f, Color.FromArgb( 32, 107, 203)));
            stops.Add(new ColorStop(0.42f, Color.FromArgb(237, 255, 255)));
            stops.Add(new ColorStop(0.64f, Color.FromArgb(255, 170,   0)));
            stops.Add(new ColorStop(0.86f, Color.FromArgb( 40,   5,  25)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   7, 100)));
        }
    }

    /// <summary>"MS Standard Blue (GradientLinear)" — linear stretch.</summary>
    public sealed class MSStandardBlueLinearMap : GradientColorMap
    {
        public static string Name => "MS Standard Blue (GradientLinear)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Classic Mandelbrot Standard palette - navy/royal/white/gold/burgundy, blue dominant phase. Linear stretch.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public MSStandardBlueLinearMap()
        {
            MSStandardBluePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Blue (Gradient)" — cycling.</summary>
    public sealed class MSStandardBlueCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "MS Standard Blue (Gradient)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Classic Mandelbrot Standard palette - navy/royal/white/gold/burgundy, blue dominant phase. Cycling.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public MSStandardBlueCyclingMap()
        {
            MSStandardBluePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Blue (Phong3D)" — Phong relief, blue phase.</summary>
    public sealed class MSStandardBluePhong3DMap : GradientPhong3DBase
    {
        public static string Name => "MS Standard Blue (Phong3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Classic Mandelbrot Standard palette - blue phase. Phong relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.02f;
        protected override float Steepness     => 1.5f;
        protected override float Ambient       => 0.15f;
        protected override float KeySpecScale  => 0.85f;
        protected override float FillSpecScale => 0.25f;
        protected override float FillDiffScale => 0.35f;

        public MSStandardBluePhong3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.6f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 0.95f, specB: 0.85f,
                shininess: 64f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.35f, diffG: 0.40f, diffB: 0.55f,
                specR: 0.25f, specG: 0.30f, specB: 0.45f,
                shininess: 32f);

            MSStandardBluePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Blue (Pbr3D)" — PBR relief, dielectric with subtle metallic peaks.</summary>
    public sealed class MSStandardBluePbr3DMap : PbrGradient3DBase
    {
        public static string Name => "MS Standard Blue (Pbr3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Classic Mandelbrot Standard palette - blue phase. PBR relief, mostly dielectric with subtle metallic peaks.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.02f;
        protected override float Steepness  => 1.5f;
        protected override float Ambient    => 0.12f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public MSStandardBluePbr3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.7f,
                diffR: 1.20f, diffG: 1.20f, diffB: 1.30f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.30f, diffG: 0.40f, diffB: 0.60f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            MSStandardBluePalette.AddStops(Stops);
        }

        protected override float GlowBoost(float t) => 0.15f * MathF.Pow(t, 8f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.42f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.75f);
            if (t < 0.65f) return new PbrMaterial(r, g, b, metalness: 0.15f, roughness: 0.45f);
            return new PbrMaterial(r, g, b, metalness: 0.05f, roughness: 0.60f);
        }
    }
}
