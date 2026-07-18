// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/JsonImported/PastelSpiralBloomThemes.cs
//
// Four "Pastel Spiral Bloom" variants — cream-yellow voids and pastel
// mauve/olive/mint spirals across Gradient / Cycling / Phong3D / Pbr3D.
// Generated from Resources/ColorThemes/colorthemes.json.
//
// Note: Linear gradient and Pbr3D end on cream-yellow (250,245,175); Cycling
// and Phong3D end on the start colour (18,14,30) so the cycle joins seamlessly.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    internal static class PastelSpiralBloomPalette
    {
        // Stops 0..6 shared across all four variants.  Stop 7 (position 1.0)
        // varies per variant — see each constructor below.
        public static void AddCoreStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb( 18,  14,  30)));
            stops.Add(new ColorStop(0.15f, Color.FromArgb( 75,  55, 110)));
            stops.Add(new ColorStop(0.32f, Color.FromArgb(145, 115, 170)));
            stops.Add(new ColorStop(0.48f, Color.FromArgb(195, 130, 150)));
            stops.Add(new ColorStop(0.62f, Color.FromArgb(165, 150,  70)));
            stops.Add(new ColorStop(0.75f, Color.FromArgb(130, 175, 120)));
            stops.Add(new ColorStop(0.85f, Color.FromArgb( 75, 165,  60)));
        }
    }

    /// <summary>"Pastel Spiral Bloom (Gradient)" — linear stretch, cream-yellow tip.</summary>
    public sealed class PastelSpiralBloomLinearMap : GradientColorMap
    {
        public static string Name => "Pastel Spiral Bloom (Gradient)";
        public static string Category => "Pastel Spiral Bloom";
        public static string Description =>
            "Cream-yellow voids and pastel mauve/olive/mint spirals - linear gradient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public PastelSpiralBloomLinearMap()
        {
            PastelSpiralBloomPalette.AddCoreStops(Stops);
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(250, 245, 175)));
        }
    }

    /// <summary>"Pastel Spiral Bloom (Cycling)" — repeating cycle, joins on dark tip.</summary>
    public sealed class PastelSpiralBloomCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "Pastel Spiral Bloom (Cycling)";
        public static string Category => "Pastel Spiral Bloom";
        public static string Description =>
            "Pastel mauve/olive/mint spirals on cream - repeating cycling palette.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.035f;

        public PastelSpiralBloomCyclingMap()
        {
            PastelSpiralBloomPalette.AddCoreStops(Stops);
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 18,  14,  30)));
        }
    }

    /// <summary>"Pastel Spiral Bloom (Phong3D)" — Phong relief, warm cream key + cool lavender fill.</summary>
    public sealed class PastelSpiralBloomPhong3DMap : GradientPhong3DBase
    {
        public static string Name => "Pastel Spiral Bloom (Phong3D)";
        public static string Category => "Pastel Spiral Bloom";
        public static string Description =>
            "Pastel mauve/olive/mint cycling palette with Blinn-Phong relief lit by warm cream key + cool lavender fill.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed    => 0.035f;
        protected override float Steepness     => 1.4f;
        protected override float Ambient       => 0.18f;
        protected override float KeySpecScale  => 0.90f;
        protected override float FillSpecScale => 0.30f;
        protected override float FillDiffScale => 0.40f;

        public PastelSpiralBloomPhong3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.60f, lz: 0.58f,
                diffR: 1.00f, diffG: 0.95f, diffB: 0.70f,
                specR: 1.00f, specG: 0.95f, specB: 0.70f,
                shininess: 48f);
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.45f, lz: 0.66f,
                diffR: 0.55f, diffG: 0.45f, diffB: 0.85f,
                specR: 0.40f, specG: 0.30f, specB: 0.70f,
                shininess: 24f);

            PastelSpiralBloomPalette.AddCoreStops(Stops);
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 18,  14,  30)));
        }
    }

    /// <summary>"Pastel Spiral Bloom (Pbr3D)" — porcelain spirals, satin metals, matte shells.</summary>
    public sealed class PastelSpiralBloomPbr3DMap : PbrGradient3DBase
    {
        public static string Name => "Pastel Spiral Bloom (Pbr3D)";
        public static string Category => "Pastel Spiral Bloom";
        public static string Description =>
            "Pastel mauve/olive/mint cycling palette with Cook-Torrance PBR. Cream highlights on porcelain spirals, satin olive metals, matte mint shells.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.025f;
        protected override float Steepness  => 1.5f;
        protected override float Ambient    => 0.16f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public PastelSpiralBloomPbr3DMap()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.55f,
                diffR: 1.20f, diffG: 1.10f, diffB: 0.85f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.60f, ly: -0.40f, lz: 0.55f,
                diffR: 0.45f, diffG: 0.40f, diffB: 0.70f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            PastelSpiralBloomPalette.AddCoreStops(Stops);
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(250, 245, 175)));
        }

        protected override float GlowBoost(float t) => 0.25f * MathF.Pow(t, 6f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            if (t < 0.15f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.85f);
            if (t < 0.32f) return new PbrMaterial(r, g, b, metalness: 0.15f, roughness: 0.60f);
            if (t < 0.48f) return new PbrMaterial(r, g, b, metalness: 0.05f, roughness: 0.55f);
            if (t < 0.62f) return new PbrMaterial(r, g, b, metalness: 0.70f, roughness: 0.35f);
            if (t < 0.85f) return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.70f);
            return new PbrMaterial(r, g, b, metalness: 0.10f, roughness: 0.50f);
        }
    }
}
