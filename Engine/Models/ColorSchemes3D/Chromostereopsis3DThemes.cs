// Models/ColorSchemes3D/Chromostereopsis3DThemes.cs
//
// 3D variants of the "Chromostereopsis" Eye Trick palette.  The pure
// R / G / B primaries on black voids drive chromatic aberration in the
// eye — red appears to float forward, blue to recede.  These variants
// preserve that perceptual effect by using neutral-white lighting
// (no colour cast) and keeping the primaries fully saturated.
//
// Four variants, one per 3D theme type:
//   * Phong3D     — neutral white key + dim grey fill, sharp specular
//   * Pbr3D       — high metalness on primaries, dielectric voids
//   * Lambert     — single-light diffuse, broad top-front key
//   * Slope       — distance-driven topo shading + light Lambert

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // Shared palette across all four variants.  Pure primaries with black
    // spacers — identical to the original Chromostereopsis Eye Trick theme.
    internal static class ChromostereopsisPalette
    {
        public static void AddStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,   0)));
            stops.Add(new ColorStop(0.10f, Color.FromArgb(  0,   0,   0)));
            stops.Add(new ColorStop(0.18f, Color.FromArgb(  0,   0, 255)));
            stops.Add(new ColorStop(0.30f, Color.FromArgb(  0,   0,   0)));
            stops.Add(new ColorStop(0.38f, Color.FromArgb(255,   0,   0)));
            stops.Add(new ColorStop(0.48f, Color.FromArgb(  0, 255,   0)));
            stops.Add(new ColorStop(0.58f, Color.FromArgb(  0,   0, 255)));
            stops.Add(new ColorStop(0.70f, Color.FromArgb(  0,   0,   0)));
            stops.Add(new ColorStop(0.78f, Color.FromArgb(255,   0,   0)));
            stops.Add(new ColorStop(0.88f, Color.FromArgb(  0,   0, 255)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,   0)));
        }
    }

    // =========================================================================
    // Phong3D
    // =========================================================================

    /// <summary>
    /// Chromostereopsis with Blinn-Phong 3D relief.  Neutral white key light
    /// preserves the pure RGB primary identity (no warm/cool tint); sharp
    /// specular gives a glassy/lacquered finish that amplifies the red-forward,
    /// blue-back depth illusion.
    /// </summary>
    public sealed class Chromostereopsis3DPhongMap : GradientPhong3DBase
    {
        public static string Name => "Chromostereopsis 3D";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Chromostereopsis with Phong 3D relief.  Neutral white key preserves " +
            "pure RGB primary identity; sharp specular gives a lacquered finish " +
            "that amplifies the red-forward / blue-back depth illusion.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.Cyclic;

        protected override float CycleSpeed    => 0.045f;
        protected override float Steepness     => 1.8f;   // moderate carving — keep primaries readable
        protected override float Ambient       => 0.05f;  // very dark voids
        protected override float KeySpecScale  => 1.10f;  // bright glassy gleam
        protected override float FillSpecScale => 0.20f;
        protected override float FillDiffScale => 0.25f;

        public Chromostereopsis3DPhongMap()
        {
            // Pure neutral white key — does not bias R, G, or B.
            KeyLight = new LightSource(
                lx: 0.50f, ly: 0.55f, lz: 0.85f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 90f);   // tight highlight — glass / lacquer

            // Neutral mid-grey fill, dim — no colour cast.
            FillLight = new LightSource(
                lx: -0.55f, ly: -0.40f, lz: 0.45f,
                diffR: 0.35f, diffG: 0.35f, diffB: 0.35f,
                specR: 0.15f, specG: 0.15f, specB: 0.15f,
                shininess: 30f);

            ChromostereopsisPalette.AddStops(Stops);
        }
    }

    // =========================================================================
    // Pbr3D
    // =========================================================================

    /// <summary>
    /// Chromostereopsis with Cook-Torrance PBR.  High metalness on the
    /// primaries delivers chromatic Fresnel rim glow that strengthens the
    /// red/blue depth split; the black bands stay matte dielectric.
    /// </summary>
    public sealed class Chromostereopsis3DPbrMap : PbrGradient3DBase
    {
        public static string Name => "Chromostereopsis 3D (PBR)";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Chromostereopsis with PBR.  High metalness on the pure primaries gives " +
            "chromatic Fresnel rim glow that intensifies the red-forward / blue-back " +
            "depth split; black bands stay matte dielectric.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.Cyclic;

        protected override float CycleSpeed => 0.045f;
        protected override float Steepness  => 1.7f;
        protected override float Ambient    => 0.04f;
        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;

        public Chromostereopsis3DPbrMap()
        {
            // Bright neutral white key — preserves chroma identity.
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.55f, lz: 0.85f,
                diffR: 1.40f, diffG: 1.40f, diffB: 1.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            // Cool-neutral fill, slight blue lift to push blue further back.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.45f,
                diffR: 0.35f, diffG: 0.40f, diffB: 0.55f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);

            ChromostereopsisPalette.AddStops(Stops);
        }

        // Subtle glow on the brightest primaries — boosts chromatic intensity
        // without washing the blacks.
        protected override float GlowBoost(float t) => 0.20f * MathF.Pow(t, 6f);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            // Albedo brightness — proxy for "is this a primary or a black void?"
            float lum = 0.299f * r + 0.587f * g + 0.114f * b;

            if (lum < 0.05f)
            {
                // Black voids — matte dielectric so the eye reads them as deep cavities.
                return new PbrMaterial(r, g, b, metalness: 0.00f, roughness: 0.85f);
            }
            // Pure primaries — high metal + low roughness gives chromatic Fresnel.
            return new PbrMaterial(r, g, b, metalness: 0.85f, roughness: 0.18f);
        }
    }

    // =========================================================================
    // Lambert (single-light, no specular)
    // =========================================================================

    /// <summary>
    /// Chromostereopsis with pure Lambert diffuse shading.  Single overhead
    /// white light; no specular.  Cheaper than the Phong / PBR variants while
    /// still giving directional relief that preserves the depth illusion.
    /// </summary>
    public sealed class Chromostereopsis3DLambertMap : GradientColorMap, IColorMap
    {
        public static string Name => "Chromostereopsis 3D (Lambert)";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Chromostereopsis with pure Lambert diffuse shading.  Single neutral " +
            "overhead light preserves chroma; no specular keeps the primaries flat-saturated.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.Cyclic;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Top-front white light.
        private const float Lx = 0.30f;
        private const float Ly = -0.25f;
        private const float Lz = 0.918f;

        private const float Steepness = 1.5f;
        private const float Ambient   = 0.12f;
        // Cycle the gradient so deep zoom doesn't lock onto one primary.
        private const float CycleSpeed = 0.045f;

        public Chromostereopsis3DLambertMap()
        {
            ChromostereopsisPalette.AddStops(Stops);
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations)
                return unchecked((int)0xFF000000);

            // Cycling parameter (mirrors CyclingGradientColorMap behaviour).
            float t = ((smooth * CycleSpeed) % 1.0f + 1.0f) % 1.0f;
            int albedo = MapNormalized(t, distance);
            float aR = ((albedo >> 16) & 0xFF) / 255f;
            float aG = ((albedo >>  8) & 0xFF) / 255f;
            float aB = ( albedo        & 0xFF) / 255f;

            // 3D normal with steepness (same convention as the 3D bases).
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else             { Nx = 0f;       Ny = 0f;       Nz = 1f; }

            float diff = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);
            float k = Ambient + (1f - Ambient) * diff;

            byte R = (byte)(System.Math.Clamp(aR * k, 0f, 1f) * 255f);
            byte G = (byte)(System.Math.Clamp(aG * k, 0f, 1f) * 255f);
            byte B = (byte)(System.Math.Clamp(aB * k, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }

    // =========================================================================
    // Slope (distance-driven topographic shading)
    // =========================================================================

    /// <summary>
    /// Chromostereopsis with topographic slope shading.  Distance estimator
    /// drives darkening near the boundary; Lambert from a top-front light
    /// gives subtle 3D pop without specular highlights washing the primaries.
    /// </summary>
    public sealed class Chromostereopsis3DSlopeMap : GradientColorMap, IColorMap
    {
        public static string Name => "Chromostereopsis 3D (Slope)";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Chromostereopsis with topographic slope shading.  Distance estimator " +
            "darkens steep regions near the boundary; subtle Lambert relief adds " +
            "3D pop while preserving the pure-primary chromatic depth split.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect |
            ColorMapFeatures.Cyclic;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        private const float Lx = 0.30f;
        private const float Ly = -0.25f;
        private const float Lz = 0.918f;

        private const float Steepness       = 1.4f;
        private const float DistanceFalloff = 5.0f;
        private const float LambertWeight   = 0.45f;
        private const float Ambient         = 0.15f;
        private const float CycleSpeed      = 0.045f;

        public Chromostereopsis3DSlopeMap()
        {
            ChromostereopsisPalette.AddStops(Stops);
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations)
                return unchecked((int)0xFF000000);

            float t = ((smooth * CycleSpeed) % 1.0f + 1.0f) % 1.0f;
            int albedo = MapNormalized(t, distance);
            float aR = ((albedo >> 16) & 0xFF) / 255f;
            float aG = ((albedo >>  8) & 0xFF) / 255f;
            float aB = ( albedo        & 0xFF) / 255f;

            // Topo magnitude from distance estimator.
            float flatness = 1f - MathF.Exp(-distance * DistanceFalloff);

            // Build 3D normal for Lambert direction.
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else             { Nx = 0f;       Ny = 0f;       Nz = 1f; }

            float NL = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);
            float shade = (1f - LambertWeight) * flatness + LambertWeight * NL;
            float k = Ambient + (1f - Ambient) * shade;

            byte R = (byte)(System.Math.Clamp(aR * k, 0f, 1f) * 255f);
            byte G = (byte)(System.Math.Clamp(aG * k, 0f, 1f) * 255f);
            byte B = (byte)(System.Math.Clamp(aB * k, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
