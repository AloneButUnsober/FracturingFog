// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes3D/AlgorithmicPbr3DThemes.cs
//
// PBR (Cook-Torrance GGX) 3D variants of the 21 algorithmic flat themes.
//
// Each subclass:
//   • Inherits AlgorithmicPbr3DBase (handles the GGX maths, F0 lookup,
//     Reinhard tone-map and IColorMap routing).
//   • Replicates the original 2D theme's colour formula in ComputeAlbedo,
//     so the PBR variant carries the same colour personality.
//   • Picks LightingMode (Realistic vs Bright) and BuildMaterial
//     (metalness/roughness curves) per theme — copper is metallic, paper
//     is matte dielectric, plasma uses a glow boost, etc.
//
// Source-of-truth for albedo: Models/ColorSchemes/<Name>.cs.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    // =========================================================================
    // Bernstein PBR — Íñigo Quílez cosine palette
    // =========================================================================
    public sealed class BernsteinPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Bernstein 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Bernstein cosine palette with PBR shading — soft dielectric finish, gently varying roughness.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.Perceptual |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.6f;
        protected override float Ambient => 0.10f;

        private const float TwoPi = MathF.PI * 2f;
        private static readonly float[] A = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] B = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] C = { 1.000f, 0.700f, 0.400f };
        private static readonly float[] D = { 0.000f, 0.150f, 0.200f };

        public BernsteinPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.85f,
                diffR: 1.10f, diffG: 1.10f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.70f, ly: -0.40f, lz: 0.55f,
                diffR: 0.55f, diffG: 0.45f, diffB: 0.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = smooth * 0.020f;
            aR = A[0] + B[0] * MathF.Cos(TwoPi * (C[0] * t + D[0]));
            aG = A[1] + B[1] * MathF.Cos(TwoPi * (C[1] * t + D[1]));
            aB = A[2] + B[2] * MathF.Cos(TwoPi * (C[2] * t + D[2]));
            float edge = 1.0f - 0.25f * MathF.Exp(-distance * 0.2f);
            aR *= edge; aG *= edge; aB *= edge;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Smoothly varying roughness — peaks (warm orange) get glossy, valleys stay matte.
            float t = (smooth * 0.020f) % 1f;
            float rough = PbrMath.SmoothLerp(t, 0f, 1f, 0.85f, 0.55f);
            return new PbrMaterial(r, g, b, metalness: 0.05f, roughness: rough);
        }
    }

    // =========================================================================
    // CopperSheen PBR — full metal
    // =========================================================================
    public sealed class CopperSheenPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Copper Sheen 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Polished copper PBR — high metalness, varying roughness for hammered-to-mirror finish.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.08f;

        public CopperSheenPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.65f, ly: 0.55f, lz: 0.80f,
                diffR: 1.30f, diffG: 1.10f, diffB: 0.80f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.75f, ly: -0.35f, lz: 0.55f,
                diffR: 0.35f, diffG: 0.45f, diffB: 0.70f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            aR = MathF.Pow(t * 1.25f, 0.60f);
            aG = MathF.Pow(t * 0.78f, 0.80f);
            aB = MathF.Pow(t * 0.40f, 1.20f);

            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.09f + 0.5f);
            aR *= 0.72f + 0.28f * band;
            aG *= 0.68f + 0.32f * band;
            aB *= 0.80f + 0.20f * band;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Distance modulates polish: pixels close to the boundary are mirror-polished;
            // far pixels are matte — the 2D version's distance specular, in PBR form.
            float polish = MathF.Exp(-distance * 0.2f);
            float rough = Math.Clamp(0.55f - 0.35f * polish, 0.10f, 0.85f);
            return new PbrMaterial(r, g, b, metalness: 0.95f, roughness: rough);
        }
    }

    // =========================================================================
    // DigitalMatrix PBR — emissive phosphor green
    // =========================================================================
    public sealed class DigitalMatrixPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Digital Matrix 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Phosphor-green PBR — emissive bands glow off matte black dielectric, scan-line interference.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.2f;
        protected override float Ambient => 0.05f;

        public DigitalMatrixPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.20f, ly: 0.85f, lz: 0.50f,
                diffR: 0.40f, diffG: 1.30f, diffB: 0.50f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.70f, ly: -0.40f, lz: 0.40f,
                diffR: 0.10f, diffG: 0.50f, diffB: 0.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float band1 = 0.5f + 0.5f * MathF.Sin(smooth * 0.25f);
            float band2 = 0.5f + 0.5f * MathF.Sin(smooth * 0.07f);
            float combined = band1 * band2;
            float glow = MathF.Exp(-distance * 0.12f);
            float v = Math.Clamp(combined * (0.3f + 0.7f * glow), 0f, 1f);
            aR = 0f;
            aG = v;
            aB = v * v * (80f / 255f);
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Matte plastic for the phosphor screen — no metal, mid roughness.
            return new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.55f);
        }

        protected override float GlowBoost(float smooth, float distance, int maxIter)
        {
            // Phosphor self-illuminates near the boundary — pure green emission.
            return 0.30f * MathF.Exp(-distance * 0.10f);
        }
    }

    // =========================================================================
    // DistanceEnhancedGlow PBR — distance-driven HSV with emissive halo
    // =========================================================================
    public sealed class DistanceGlowPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Distance Glow 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Distance-driven HSV glow PBR — emissive boundary halo on a matte body.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.08f;

        public DistanceGlowPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.70f, lz: 0.80f,
                diffR: 1.20f, diffG: 1.20f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.45f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.45f, diffB: 0.60f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * 0.02f) % 1f;
            float v = MathF.Exp(-distance * 0.1f);
            var c = ColorUtils.Hsv(h, 1f, v);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            float polish = MathF.Exp(-distance * 0.15f);
            float rough = Math.Clamp(0.70f - 0.30f * polish, 0.20f, 0.85f);
            return new PbrMaterial(r, g, b, metalness: 0.10f, roughness: rough);
        }

        protected override float GlowBoost(float smooth, float distance, int maxIter)
            => 0.25f * MathF.Exp(-distance * 0.08f);
    }

    // =========================================================================
    // Fire PBR — hot emissive
    // =========================================================================
    public sealed class FirePbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Fire 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Fire ramp PBR — hot emissive cores at the bright end, blackbody dielectric in shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.2f;
        protected override float Ambient => 0.08f;

        public FirePbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.20f, ly: -0.65f, lz: 0.75f,
                diffR: 1.50f, diffG: 0.85f, diffB: 0.35f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.55f, ly: 0.60f, lz: 0.45f,
                diffR: 0.20f, diffG: 0.30f, diffB: 0.65f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            aR = Math.Clamp(t * 3.0f, 0f, 1f);
            aG = Math.Clamp((t - 0.33f) * 3.0f, 0f, 1f);
            aB = Math.Clamp((t - 0.67f) * 3.0f, 0f, 1f);
            float ripple = 0.85f + 0.15f * MathF.Sin(smooth * 0.11f);
            aR *= ripple; aG *= ripple; aB *= ripple;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Hotter (whiter/yellower) bands act metallic — like glowing iron.
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            float metal = PbrMath.SmoothLerp(t, 0.30f, 0.85f, 0.0f, 0.55f);
            float rough = PbrMath.SmoothLerp(t, 0.0f, 1.0f, 0.85f, 0.30f);
            return new PbrMaterial(r, g, b, metal, rough);
        }

        protected override float GlowBoost(float smooth, float distance, int maxIter)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            // Strong glow on bright (hot) bands.
            return MathF.Pow(t, 5f) * 0.55f;
        }
    }

    // =========================================================================
    // GoldenRatio PBR — phi-cycling hue with subtle metal
    // =========================================================================
    public sealed class GoldenRatioPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Golden Ratio 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Phi-spaced hue spiral PBR — semi-metallic surface with warm gold lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.10f;

        private const float Phi = 0.61803398875f;

        public GoldenRatioPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.55f, lz: 0.80f,
                diffR: 1.25f, diffG: 1.10f, diffB: 0.85f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.40f, diffB: 0.60f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * Phi) % 1f;
            var c = ColorUtils.Hsv(h, 0.8f, 1f);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.30f, roughness: 0.40f);
    }

    // =========================================================================
    // Greyscale PBR — matte stone
    // =========================================================================
    public sealed class GrayscalePbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Greyscale 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Cycling grey ramp PBR — dielectric stone surface, balanced ambient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.14f;

        public GrayscalePbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.60f, lz: 0.85f,
                diffR: 1.10f, diffG: 1.10f, diffB: 1.10f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.45f, diffG: 0.45f, diffB: 0.50f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.12f);
            float v = Math.Clamp(t * 0.75f + band * 0.25f, 0f, 1f);
            aR = v; aG = v; aB = v;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.75f);
    }

    // =========================================================================
    // HSV PBR — full-spectrum dielectric
    // =========================================================================
    public sealed class HsvPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "HSV 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Classic HSV PBR — saturated dielectric spectrum with even gloss.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.12f;

        public HsvPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.85f,
                diffR: 1.20f, diffG: 1.20f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.40f, diffB: 0.50f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 0.02f) % 1.0f;
            hue -= MathF.Floor(hue);
            float lightness = 1.0f - MathF.Min(distance * 0.08f, 1.0f);
            int packed = Fractals.HsvToRgb(hue, 1.0f, lightness);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.45f);
    }

    // =========================================================================
    // MonochromeBands PBR — sharp embossed dielectric
    // =========================================================================
    public sealed class MonoBandPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Monochrome Bands 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Sine-band monochrome PBR — sharp embossed dielectric with crisp highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.0f;
        protected override float Ambient => 0.10f;

        public MonoBandPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.65f, ly: 0.55f, lz: 0.75f,
                diffR: 1.15f, diffG: 1.15f, diffB: 1.15f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.50f, lz: 0.45f,
                diffR: 0.30f, diffG: 0.30f, diffB: 0.35f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float v = 0.5f + 0.5f * MathF.Sin(smooth * 0.1f);
            aR = v; aG = v; aB = v;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Bright bands (white peaks) read as polished, dark bands as matte.
            float v = 0.5f + 0.5f * MathF.Sin(smooth * 0.1f);
            float rough = PbrMath.SmoothLerp(v, 0f, 1f, 0.80f, 0.30f);
            return new PbrMaterial(r, g, b, metalness: 0.05f, roughness: rough);
        }
    }

    // =========================================================================
    // NebulaDust PBR — emissive cloud
    // =========================================================================
    public sealed class NebulaDustPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Nebula Dust 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Cosmic-fog PBR — soft dielectric core, distance-driven emissive halo.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.8f;
        protected override float Ambient => 0.14f;

        public NebulaDustPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.55f, lz: 0.85f,
                diffR: 1.05f, diffG: 0.85f, diffB: 1.30f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.65f, diffG: 0.45f, diffB: 0.85f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = ((smooth * 0.018f) % 1f + 1f) % 1f;
            float saturation = 0.75f + 0.25f * MathF.Exp(-distance * 0.3f);
            float glow = MathF.Exp(-distance * 0.08f);
            float value = Math.Clamp(0.15f + 0.85f * glow, 0f, 1f);
            var c = ColorUtils.Hsv(hue, saturation, value);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.85f);

        protected override float GlowBoost(float smooth, float distance, int maxIter)
            => 0.35f * MathF.Exp(-distance * 0.10f);
    }

    // =========================================================================
    // Painted PBR — glossy ceramic
    // =========================================================================
    public sealed class PaintedPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Painted 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Painted-jewel PBR — vivid ceramic with controlled gloss, distance brightening.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.12f;

        public PaintedPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.60f, ly: 0.65f, lz: 0.80f,
                diffR: 1.20f, diffG: 1.20f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.35f, diffG: 0.40f, diffB: 0.55f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 0.05f) % 1.1f;
            hue -= MathF.Floor(hue);
            float lightness = 1.35f - MathF.Min(distance * 0.04f, 1.0f);
            int packed = Fractals.HsvToRgb(hue, 0.9f, lightness);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.05f, roughness: 0.35f);
    }

    // =========================================================================
    // PaintedReversed PBR — moody glazed ceramic
    // =========================================================================
    public sealed class PaintedReversedPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Painted Reversed 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Painted-jewel PBR with a moody dark backdrop — glazed ceramic with deep shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.6f;
        protected override float Ambient => 0.08f;

        public PaintedReversedPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.60f, lz: 0.80f,
                diffR: 1.20f, diffG: 1.10f, diffB: 0.95f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.45f, lz: 0.45f,
                diffR: 0.20f, diffG: 0.25f, diffB: 0.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 0.05f) % 1.0f;
            float lightness = 1.35f - MathF.Min(distance * 0.04f, 1.0f);
            int packed = Fractals.HsvToRgb(hue, 0.9f, lightness);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.05f, roughness: 0.40f);
    }

    // =========================================================================
    // Pastelly PBR — matte chalk
    // =========================================================================
    public sealed class PastellyPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Pastelly 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Pastel PBR — chalky matte dielectric, very high roughness, soft ambient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 2.0f;
        protected override float Ambient => 0.20f;

        public PastellyPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.55f, lz: 0.90f,
                diffR: 1.05f, diffG: 1.05f, diffB: 1.15f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.55f, ly: -0.35f, lz: 0.65f,
                diffR: 0.65f, diffG: 0.65f, diffB: 0.75f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = smooth * 0.05f;
            float c = distance % maxIter * t;
            float hue = t % 1.0f;
            float saturation = c == 0f ? 0f : (c * (t / c)) % 1.0f;
            float lightness = 1.35f - MathF.Min(t * 0.04f, 1.0f);
            float value = Math.Clamp(lightness + (0.3f * MathF.Exp(-distance * 0.2f)), 0f, 1f);
            int packed = Fractals.HsvToRgb(hue, saturation, value);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.95f);
    }

    // =========================================================================
    // Psychedelic PBR — vivid metallic-glossy
    // =========================================================================
    public sealed class PsychedelicPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Psychedelic 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Hyper-cycling rainbow PBR — semi-metallic, glossy, with HDR-boosted lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.1f;
        protected override float Ambient => 0.10f;

        public PsychedelicPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.40f, diffG: 1.40f, diffB: 1.50f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.70f, ly: -0.45f, lz: 0.50f,
                diffR: 0.45f, diffG: 0.55f, diffB: 0.80f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 0.055f) % 1f;
            float ripple1 = 0.5f + 0.5f * MathF.Sin(smooth * 0.31f);
            float ripple2 = 0.5f + 0.5f * MathF.Sin(smooth * 0.11f);
            float sat = Math.Clamp(0.6f + 0.4f * ripple1 * ripple2, 0f, 1f);
            float val = Math.Clamp(0.65f + 0.35f * MathF.Sin(smooth * 0.05f + 1.2f), 0f, 1f);
            var c = ColorUtils.Hsv(hue, sat, val);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Saturation ripple drives metalness — dense ripple peaks gleam metallic.
            float ripple1 = 0.5f + 0.5f * MathF.Sin(smooth * 0.31f);
            float ripple2 = 0.5f + 0.5f * MathF.Sin(smooth * 0.11f);
            float metal = 0.20f + 0.50f * ripple1 * ripple2;
            return new PbrMaterial(r, g, b, metal, roughness: 0.30f);
        }
    }

    // =========================================================================
    // RadioInterference (RedAndBlack) PBR — glowing red plasma
    // =========================================================================
    public sealed class RadioInterferenceOriginalPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Radio Interference Original 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Eight-cycle red/black PBR — emissive crimson plasma with glowing crests.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.05f;

        public RadioInterferenceOriginalPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.60f, ly: 0.60f, lz: 0.80f,
                diffR: 1.50f, diffG: 0.30f, diffB: 0.30f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.45f,
                diffR: 0.40f, diffG: 0.10f, diffB: 0.10f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 8.0f) % 360.0f;
            float saturation = 0.85f;
            float value = Math.Clamp(1.0f - MathF.Pow((float)smooth / MathF.Max(1, maxIter), 0.2f), 0f, 1f);
            int packed = Fractals.HsvToRgb(hue, saturation, value);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.20f, roughness: 0.45f);

        protected override float GlowBoost(float smooth, float distance, int maxIter)
        {
            float v = 1.0f - MathF.Pow((float)smooth / MathF.Max(1, maxIter), 0.2f);
            return Math.Clamp(v, 0f, 1f) * 0.30f;
        }
    }

    // =========================================================================
    // Rainbow PBR — glossy plastic
    // =========================================================================
    public sealed class RainbowPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Rainbow 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Rainbow PBR — glossy plastic dielectric, even hue cycling, crisp specular highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.11f;

        public RainbowPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.80f,
                diffR: 1.20f, diffG: 1.20f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.40f, diffB: 0.50f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * 0.015f) % 1f;
            var c = ColorUtils.Hsv(h, 1f, 1f);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.30f);
    }

    // =========================================================================
    // SolarWind PBR — coronal plasma
    // =========================================================================
    public sealed class SolarWindPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Solar Wind 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Coronal plasma PBR — emissive white-blue corona on a polished metallic body.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.08f;

        public SolarWindPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.10f, diffG: 1.20f, diffB: 1.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.20f, diffB: 0.80f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = smooth * 0.023f;
            float hue = 0.75f - 0.28f * ((t % 1f + 1f) % 1f);
            float sat = Math.Clamp(0.80f + 0.20f * MathF.Sin(smooth * 0.06f), 0f, 1f);
            float val = Math.Clamp(0.25f + 0.75f * ((t * 1.7f) % 1f), 0f, 1f);
            float corona = MathF.Exp(-distance * 0.22f);
            var c = ColorUtils.Hsv(hue, sat, val);
            aR = c.R / 255f + corona * 0.70f;
            aG = c.G / 255f + corona * 0.80f;
            aB = c.B / 255f + corona * 1.00f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            // Brighter (corona) bands ⇒ lower roughness / higher metalness.
            float t = smooth * 0.023f;
            float val = ((t * 1.7f) % 1f);
            float metal = PbrMath.SmoothLerp(val, 0.40f, 1.0f, 0.10f, 0.55f);
            float rough = PbrMath.SmoothLerp(val, 0f, 1f, 0.70f, 0.20f);
            return new PbrMaterial(r, g, b, metal, rough);
        }

        protected override float GlowBoost(float smooth, float distance, int maxIter)
            => 0.45f * MathF.Exp(-distance * 0.18f);
    }

    // =========================================================================
    // SolarWind-MOD PBR — wider hue, more flare
    // =========================================================================
    public sealed class SolarWindModPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Solar Wind MOD 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Wide-spectrum coronal plasma PBR — extended hue sweep, dramatic emissive flare.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.08f;

        public SolarWindModPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.20f, diffG: 1.25f, diffB: 1.40f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.45f, diffG: 0.15f, diffB: 0.75f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = smooth * 0.023f;
            float hue = 0.95f - 0.58f * ((t % 1f + 1.02f) % 1f);
            float sat = Math.Clamp(0.88f + 0.20f * MathF.Sin(smooth * 0.08f), 0f, 1f);
            float val = Math.Clamp(0.25f + 0.75f * ((t * 1.7f) % 1f), 0f, 1f);
            float corona = MathF.Exp(-distance * 0.22f);
            var c = ColorUtils.Hsv(hue, sat, val);
            aR = c.R / 255f + corona * 0.70f;
            aG = c.G / 255f + corona * 0.80f;
            aB = c.B / 255f + corona * 1.00f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
        {
            float t = smooth * 0.023f;
            float val = ((t * 1.7f) % 1f);
            float metal = PbrMath.SmoothLerp(val, 0.40f, 1.0f, 0.15f, 0.60f);
            float rough = PbrMath.SmoothLerp(val, 0f, 1f, 0.70f, 0.18f);
            return new PbrMaterial(r, g, b, metal, rough);
        }

        protected override float GlowBoost(float smooth, float distance, int maxIter)
            => 0.55f * MathF.Exp(-distance * 0.18f);
    }

    // =========================================================================
    // TwilightCyclic PBR — matte cloth at dusk
    // =========================================================================
    public sealed class TwilightCyclicPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Twilight Cyclic 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Beating-sine twilight PBR — matte cloth dielectric, soft dusk lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.7f;
        protected override float Ambient => 0.18f;

        private const float FreqR = 0.0190f;
        private const float FreqG = 0.0110f;
        private const float FreqB = 0.0260f;
        private const float PhaseR = 0.0f;
        private const float PhaseG = 1.0472f;
        private const float PhaseB = 2.0944f;

        public TwilightCyclicPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.50f, ly: 0.55f, lz: 0.85f,
                diffR: 0.85f, diffG: 0.90f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.40f, lz: 0.55f,
                diffR: 0.65f, diffG: 0.45f, diffB: 0.80f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float s = smooth;
            aR = 0.10f + 0.25f * (0.5f + 0.5f * MathF.Sin(s * FreqR + PhaseR));
            aG = 0.05f + 0.30f * (0.5f + 0.5f * MathF.Sin(s * FreqG + PhaseG));
            aB = 0.40f + 0.60f * (0.5f + 0.5f * MathF.Sin(s * FreqB + PhaseB));
            float glow = 1.0f + 0.3f * MathF.Exp(-distance * 0.15f);
            aR *= glow; aG *= glow; aB *= glow;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.85f);
    }

    // =========================================================================
    // VintageSepia PBR — paper / photographic emulsion
    // =========================================================================
    public sealed class VintageSepiaPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Vintage Sepia 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Aged sepia PBR — fibrous paper dielectric, warm tungsten lighting, deep vignette.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected override float Steepness => 1.8f;
        protected override float Ambient => 0.14f;

        public VintageSepiaPbr3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.55f, lz: 0.85f,
                diffR: 1.20f, diffG: 0.85f, diffB: 0.55f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.45f, diffG: 0.30f, diffB: 0.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            float tone = t * t * (3f - 2f * t);
            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.08f + 0.3f);
            tone = tone * 0.80f + band * 0.20f;
            aR = 0.05f + 0.87f * tone;
            aG = 0.02f + 0.64f * tone;
            aB = 0.00f + 0.40f * tone;
            float vignette = Math.Clamp(distance * 0.18f, 0f, 1f);
            float vigScale = 0.35f + 0.65f * vignette;
            aR *= vigScale; aG *= vigScale; aB *= vigScale;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.90f);
    }

    // =========================================================================
    // WarpedHSV PBR — glossy with edge emission
    // =========================================================================
    public sealed class WarpedHsvPbr3D : AlgorithmicPbr3DBase
    {
        public static string Name => "Warped HSV 3D (PBR)";
        public static string Category => "3D Relief";
        public static string Description =>
            "Warped HSV PBR — glossy ceramic, distance-driven edge emission, depth-faded interiors.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override PbrLightingMode LightingMode => PbrLightingMode.PBRBright;
        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.10f;

        public WarpedHsvPbr3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.65f, lz: 0.85f,
                diffR: 1.30f, diffG: 1.25f, diffB: 1.20f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.45f, diffB: 0.65f,
                specR: 0f, specG: 0f, specB: 0f,
                shininess: 1f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = ((smooth * 0.021f) % 1f + 1f) % 1f;
            float satRipple = 0.5f + 0.5f * MathF.Sin(smooth * 0.08f + 0.7f);
            float sat = Math.Clamp(0.55f + 0.45f * satRipple, 0f, 1f);
            float depthDim = 1.0f - 0.4f * MathF.Pow((float)smooth / MathF.Max(1, maxIter), 0.5f);
            float edgeGlow = 0.5f * MathF.Exp(-distance * 0.15f);
            float val = Math.Clamp(depthDim + edgeGlow, 0f, 1f);
            var c = ColorUtils.Hsv(hue, sat, val);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }

        protected override PbrMaterial BuildMaterial(float smooth, float distance, int maxIter,
                                                     float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.10f, roughness: 0.30f);

        protected override float GlowBoost(float smooth, float distance, int maxIter)
            => 0.30f * MathF.Exp(-distance * 0.12f);
    }
}