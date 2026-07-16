// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/JsonImported/MSStandardAmberThemes.cs
//
// Four "MS Standard Amber" variants — burgundy/orange dominant, blue valleys,
// white peaks (Mandelbrot Standard rotated to amber phase) across
// Gradient / Cycling / Phong3D / Pbr3D.
// Generated from Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    internal static class MSStandardAmberPalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb( 40,   5,  25)));
            stops.Add(new ColorStop(0.14f, Color.FromArgb(  0,   7, 100)));
            stops.Add(new ColorStop(0.30f, Color.FromArgb( 32, 107, 203)));
            stops.Add(new ColorStop(0.56f, Color.FromArgb(237, 255, 255)));
            stops.Add(new ColorStop(0.78f, Color.FromArgb(255, 170,   0)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb( 40,   5,  25)));
        }
    }

    /// <summary>"MS Standard Amber (GradientLinear)" — linear stretch.</summary>
    public sealed class MSStandardAmberLinearMap : GradientColorMap
    {
        public static string Name => "MS Standard Amber (GradientLinear)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette rotated - burgundy/orange dominant, blue valleys, white peaks. Linear stretch.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public MSStandardAmberLinearMap()
        {
            MSStandardAmberPalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Amber (Gradient)" — cycling.</summary>
    public sealed class MSStandardAmberCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "MS Standard Amber (Gradient)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette rotated - burgundy/orange dominant, blue valleys, white peaks. Cycling.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public MSStandardAmberCyclingMap()
        {
            MSStandardAmberPalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Amber (Phong3D)" — Phong relief, amber phase.</summary>
    public sealed class MSStandardAmberPhong3DMap : GradientPhong3DBase
    {
        public static string Name => "MS Standard Amber (Phong3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette - amber phase. Phong relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.02f;
        protected override float Steepness     => 1.5f;
        protected override float Ambient       => 0.15f;
        protected override float KeySpecScale  => 0.85f;
        protected override float FillSpecScale => 0.25f;
        protected override float FillDiffScale => 0.35f;

        public MSStandardAmberPhong3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.6f,
                diffR: 1.00f, diffG: 0.95f, diffB: 0.85f,
                specR: 1.00f, specG: 0.90f, specB: 0.70f,
                shininess: 64f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.35f, diffG: 0.35f, diffB: 0.50f,
                specR: 0.25f, specG: 0.25f, specB: 0.40f,
                shininess: 32f);

            MSStandardAmberPalette.AddStops(Stops);
        }
    }

    /// <summary>"MS Standard Amber (Pbr3D)" — PBR relief, warm metallic gold highlights.</summary>
    public sealed class MSStandardAmberPbr3DMap : PbrGradient3DBase
    {
        public static string Name => "MS Standard Amber (Pbr3D)";
        public static string Category => "Mandelbrot Standard";
        public static string Description =>
            "Mandelbrot Standard palette - amber phase. PBR relief with warm metallic gold highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.02f;
        protected override float Steepness  => 1.5f;
        protected override float Ambient    => 0.12f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public MSStandardAmberPbr3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.5f, ly: 0.7f, lz: 0.7f,
                diffR: 1.25f, diffG: 1.15f, diffB: 1.00f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.6f, ly: -0.4f, lz: 0.5f,
                diffR: 0.30f, diffG: 0.40f, diffB: 0.60f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            MSStandardAmberPalette.AddStops(Stops);
        }

        protected override float GlowBoost(float t) => 0.2f * MathF.Pow(t, 8f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.30f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.75f);
            if (t < 0.56f) return new PbrMaterial(r, g, b, metalness: 0.10f, roughness: 0.50f);
            if (t < 0.85f) return new PbrMaterial(r, g, b, metalness: 0.60f, roughness: 0.35f);
            return new PbrMaterial(r, g, b, metalness: 0.10f, roughness: 0.65f);
        }
    }
}
