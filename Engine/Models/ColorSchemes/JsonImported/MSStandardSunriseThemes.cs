// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/JsonImported/MSStandardSunriseThemes.cs
//
// Four "MS Standard Sunrise" variants — gold/yellow background, deep blue
// spirals, white sparkle peaks (Mandelbrot Standard rotated to sunrise phase)
// across Gradient / Cycling / Phong3D / Pbr3D.
// Generated from Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    internal static class MSStandardSunrisePalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 170,   0)));
            stops.Add(new ColorStop(0.22f, Color.FromArgb( 40,   5,  25)));
            stops.Add(new ColorStop(0.36f, Color.FromArgb(  0,   7, 100)));
            stops.Add(new ColorStop(0.52f, Color.FromArgb( 32, 107, 203)));
            stops.Add(new ColorStop(0.78f, Color.FromArgb(237, 255, 255)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 170,   0)));
        }
    }

    /// <summary>"MS Standard Sunrise (GradientLinear)" — linear stretch.</summary>
    public sealed class MSStandardSunriseLinearMap : GradientColorMap
    {
        public static string Name => "MS Standard Sunrise (GradientLinear)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette rotated - gold/yellow background, deep blue spirals, white sparkle peaks. Linear stretch.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public MSStandardSunriseLinearMap()
        {
            MSStandardSunrisePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Sunrise (Gradient)" — cycling.</summary>
    public sealed class MSStandardSunriseCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "MS Standard Sunrise (Gradient)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette rotated - gold/yellow background, deep blue spirals, white sparkle peaks. Cycling.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public MSStandardSunriseCyclingMap()
        {
            MSStandardSunrisePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Sunrise (Phong3D)" — Phong relief with warm key.</summary>
    public sealed class MSStandardSunrisePhong3DMap : GradientPhong3DBase
    {
        public static string Name => "MS Standard Sunrise (Phong3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette - sunrise phase. Phong relief with warm key.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.02f;
        protected override float Steepness     => 1.5f;
        protected override float Ambient       => 0.18f;
        protected override float KeySpecScale  => 0.85f;
        protected override float FillSpecScale => 0.25f;
        protected override float FillDiffScale => 0.35f;

        public MSStandardSunrisePhong3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.6f,
                diffR: 1.05f, diffG: 0.95f, diffB: 0.70f,
                specR: 1.00f, specG: 0.85f, specB: 0.55f,
                shininess: 60f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.30f, diffG: 0.35f, diffB: 0.55f,
                specR: 0.20f, specG: 0.25f, specB: 0.40f,
                shininess: 28f);

            MSStandardSunrisePalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Sunrise (Pbr3D)" — PBR relief, glowing yellow/orange dominant.</summary>
    public sealed class MSStandardSunrisePbr3DMap : PbrGradient3DBase
    {
        public static string Name => "MS Standard Sunrise (Pbr3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette - sunrise phase. PBR relief, glowing yellow/orange dominant.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.02f;
        protected override float Steepness  => 1.5f;
        protected override float Ambient    => 0.15f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public MSStandardSunrisePbr3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.7f,
                diffR: 1.30f, diffG: 1.15f, diffB: 0.90f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.30f, diffG: 0.40f, diffB: 0.60f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            MSStandardSunrisePalette.AddStops(Stops);
        }

        protected override float GlowBoost(float t) => 0.3f * MathF.Pow(t, 6f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.22f) return new PbrMaterial(r, g, b, metalness: 0.50f, roughness: 0.40f);
            if (t < 0.52f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.70f);
            if (t < 0.78f) return new PbrMaterial(r, g, b, metalness: 0.10f, roughness: 0.50f);
            return new PbrMaterial(r, g, b, metalness: 0.40f, roughness: 0.35f);
        }
    }
}
