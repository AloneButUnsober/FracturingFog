// Models/ColorSchemes/JsonImported/New3DThemes.cs
//
// Concrete classes generated from Resources/ColorThemes/colorthemes.json
// for the "3D Relief" / "New 3D" family.  Six variants sharing the same
// green palette across Gradient / Cycling / Phong3D / Pbr3D base classes.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // Shared palette helper for all six New 3D variants.
    internal static class New3DPalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb(  2,  40,  12)));
            stops.Add(new ColorStop(0.15f, Color.FromArgb(  8,  74,  55)));
            stops.Add(new ColorStop(0.32f, Color.FromArgb( 13, 112,  89)));
            stops.Add(new ColorStop(0.50f, Color.FromArgb( 45, 155, 140)));
            stops.Add(new ColorStop(0.65f, Color.FromArgb( 12, 186,  50)));
            stops.Add(new ColorStop(0.80f, Color.FromArgb( 82, 212,  35)));
            stops.Add(new ColorStop(0.92f, Color.FromArgb( 82, 231,  30)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb( 19,  38,  13)));
        }

        public static LightSource KeyLight() => new LightSource(
            lx: -0.45f, ly: 0.55f, lz: 0.70f,
            diffR: 0.30f, diffG: 1.00f, diffB: 0.65f,
            specR: 0.50f, specG: 0.85f, specB: 0.10f,
            shininess: 90f);

        public static LightSource FillLight() => new LightSource(
            lx: 0.65f, ly: -0.50f, lz: 0.45f,
            diffR: 0.80f, diffG: 0.60f, diffB: 0.15f,
            specR: 0.15f, specG: 0.35f, specB: 0.80f,
            shininess: 20f);
    }

    /// <summary>"New 3D" — green Phong relief.</summary>
    public sealed class New3DPhongMap : GradientPhong3DBase
    {
        public static string Name => "New 3D";
        public static string Category => "3D Relief";
        public static string Description => "Inferno with volcanic forge light — magma 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed   => 0.02f;
        protected override float Steepness    => 1.3f;
        protected override float Ambient      => 0.40f;
        protected override float KeySpecScale => 0.95f;
        protected override float FillSpecScale => 0.25f;
        protected override float FillDiffScale => 0.35f;

        public New3DPhongMap()
        {
            // Custom KeyLight + FillLight (json's original "New 3D" variant).
            KeyLight = new LightSource(
                lx: -0.8578685f, ly: 0.3036554f, lz: 0.7325896f,
                diffR: 1.00f, diffG: 0.60f, diffB: 0.20f,
                specR: 1.00f, specG: 0.85f, specB: 0.55f,
                shininess: 30f);
            FillLight = new LightSource(
                lx: 0.20104754f, ly: -0.9842062f, lz: 0.40894437f,
                diffR: 0.30f, diffG: 0.05f, diffB: 1.00f,
                specR: 0.20f, specG: 0.02f, specB: 1.00f,
                shininess: 10f);

            Stops.Add(new ColorStop(0.0f, Color.FromArgb(  0,   0, 155)));
            Stops.Add(new ColorStop(0.2f, Color.FromArgb( 66,   0,  25)));
            Stops.Add(new ColorStop(0.4f, Color.FromArgb(147,  10,   0)));
            Stops.Add(new ColorStop(0.6f, Color.FromArgb(221,  60,   0)));
            Stops.Add(new ColorStop(0.8f, Color.FromArgb(252, 180,  85)));
            Stops.Add(new ColorStop(1.0f, Color.FromArgb(  0,   0, 155)));
        }
    }

    /// <summary>"New 3D MOD" — green Phong relief with reshaped lighting.</summary>
    public sealed class New3DModPhongMap : GradientPhong3DBase
    {
        public static string Name => "New 3D MOD";
        public static string Category => "3D Relief";
        public static string Description => "Inferno with volcanic forge light — magma 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed   => 0.02f;
        protected override float Steepness    => 1.3f;
        protected override float Ambient      => 0.09f;
        protected override float KeySpecScale => 0.95f;
        protected override float FillSpecScale => 0.30f;
        protected override float FillDiffScale => 0.35f;

        public New3DModPhongMap()
        {
            KeyLight  = New3DPalette.KeyLight();
            FillLight = New3DPalette.FillLight();
            New3DPalette.AddStops(Stops);
        }
    }

    /// <summary>"New 3D GRAD" — green linear gradient.</summary>
    public sealed class New3DGradMap : GradientColorMap
    {
        public static string Name => "New 3D GRAD";
        public static string Category => "3D Relief";
        public static string Description => "Green 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public New3DGradMap()
        {
            New3DPalette.AddStops(Stops);
        }
    }

    /// <summary>"New 3D CYC" — green cycling gradient.</summary>
    public sealed class New3DCycMap : CyclingGradientColorMap
    {
        public static string Name => "New 3D CYC";
        public static string Category => "3D Relief";
        public static string Description => "Green 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public New3DCycMap()
        {
            New3DPalette.AddStops(Stops);
        }
    }

    /// <summary>"New 3D PBR" — green Cook-Torrance relief.</summary>
    public sealed class New3DPbrMap : PbrGradient3DBase
    {
        public static string Name => "New 3D PBR";
        public static string Category => "3D Relief";
        public static string Description => "Green 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed => 0.02f;
        protected override float Steepness  => 1.3f;
        protected override float Ambient    => 0.09f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        public New3DPbrMap()
        {
            KeyLight  = New3DPalette.KeyLight();
            FillLight = New3DPalette.FillLight();
            New3DPalette.AddStops(Stops);
        }
    }

    /// <summary>"New 3D CYC MOD" — green cycling gradient, faster cycle.</summary>
    public sealed class New3DCycModMap : CyclingGradientColorMap
    {
        public static string Name => "New 3D CYC MOD";
        public static string Category => "3D Relief";
        public static string Description => "Green 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.015f;

        public New3DCycModMap()
        {
            New3DPalette.AddStops(Stops);
        }
    }
}
