// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes3D/AlgorithmicPhong3DThemes.cs
//
// Phong 3D variants of the 21 algorithmic flat themes (those that derive
// colour from a formula rather than a gradient stop list).
//
// Each subclass:
//   • Inherits AlgorithmicPhong3DBase (handles the Blinn-Phong maths and the
//     IColorMap routing).
//   • Replicates the original 2D theme's colour formula in ComputeAlbedo,
//     so the lit version has the same colour personality as its flat
//     counterpart.
//   • Picks key/fill light directions and tints to match the theme's mood
//     (e.g. cool key for icy plasma themes, warm key for fire/copper, etc.).
//
// Source-of-truth for the albedo formulas: Models/ColorSchemes/<Name>.cs.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    // =========================================================================
    // Bernstein — Íñigo Quílez cosine palette (purple/cyan/orange)
    // =========================================================================
    public sealed class BernsteinPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Bernstein 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Bernstein cosine palette under balanced 3D Phong lighting — smooth purple/cyan/orange relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.Perceptual |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.6f;
        protected override float Ambient => 0.14f;

        private const float TwoPi = MathF.PI * 2f;
        private static readonly float[] A = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] B = { 0.500f, 0.500f, 0.500f };
        private static readonly float[] C = { 1.000f, 0.700f, 0.400f };
        private static readonly float[] D = { 0.000f, 0.150f, 0.200f };

        public BernsteinPhong3D()
        {
            // Neutral white key, soft warm fill — lets the gradient supply the colour.
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.85f,
                diffR: 0.95f, diffG: 0.95f, diffB: 1.00f,
                specR: 1.00f, specG: 0.98f, specB: 0.95f,
                shininess: 60f);
            FillLight = new LightSource(
                lx: 0.70f, ly: -0.40f, lz: 0.55f,
                diffR: 0.55f, diffG: 0.45f, diffB: 0.40f,
                specR: 0.30f, specG: 0.25f, specB: 0.20f,
                shininess: 18f);
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
    }

    // =========================================================================
    // CopperSheen — polished cycling copper with distance-driven gleam
    // =========================================================================
    public sealed class CopperSheenPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Copper Sheen 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Polished cycling copper relief — warm key, distance-driven specular gleam.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.10f;
        protected override float KeySpecScale => 1.05f;   // metallic gleam

        public CopperSheenPhong3D()
        {
            // Warm copper-tinted key from upper-right; cool fill picks up shadow side.
            KeyLight = new LightSource(
                lx: 0.65f, ly: 0.55f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.78f, diffB: 0.40f,
                specR: 1.00f, specG: 0.85f, specB: 0.55f,
                shininess: 95f);
            FillLight = new LightSource(
                lx: -0.75f, ly: -0.35f, lz: 0.55f,
                diffR: 0.20f, diffG: 0.30f, diffB: 0.45f,
                specR: 0.15f, specG: 0.25f, specB: 0.45f,
                shininess: 18f);
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

            float spec = 0.50f * MathF.Exp(-distance * 0.20f);
            aR += spec;
            aG += spec * 0.55f;
            aB += spec * 0.10f;
        }
    }

    // =========================================================================
    // DigitalMatrix — phosphor green CRT with scan-line interference
    // =========================================================================
    public sealed class DigitalMatrixPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Digital Matrix 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Phosphor-green CRT relief — scan-line bands lit by a cool green key, deep black recesses.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.2f;   // sharp embossing
        protected override float Ambient => 0.06f;  // dark recesses
        protected override float KeySpecScale => 0.40f;  // not too shiny — phosphor
        protected override float FillDiffScale => 0.20f;

        public DigitalMatrixPhong3D()
        {
            // Cold green key from straight above — like a CRT illuminating itself.
            KeyLight = new LightSource(
                lx: 0.20f, ly: 0.85f, lz: 0.50f,
                diffR: 0.30f, diffG: 1.00f, diffB: 0.40f,
                specR: 0.50f, specG: 1.00f, specB: 0.60f,
                shininess: 40f);
            FillLight = new LightSource(
                lx: -0.70f, ly: -0.40f, lz: 0.40f,
                diffR: 0.05f, diffG: 0.30f, diffB: 0.25f,
                specR: 0.05f, specG: 0.20f, specB: 0.20f,
                shininess: 8f);
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
    }

    // =========================================================================
    // DistanceEnhancedGlow — distance-driven HSV glow
    // =========================================================================
    public sealed class DistanceGlowPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Distance Glow 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Distance-modulated HSV glow with 3D Phong relief — colours fade with distance from the set.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.10f;

        public DistanceGlowPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.70f, lz: 0.80f,
                diffR: 1.00f, diffG: 1.00f, diffB: 0.95f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 55f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.45f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.35f, diffB: 0.50f,
                specR: 0.25f, specG: 0.30f, specB: 0.45f,
                shininess: 16f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * 0.02f) % 1f;
            float v = MathF.Exp(-distance * 0.1f);
            var c = ColorUtils.Hsv(h, 1f, v);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }
    }

    // =========================================================================
    // Fire — black → red → orange → yellow → white
    // =========================================================================
    public sealed class FirePhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Fire 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Classic fire ramp under fierce orange key light — molten relief with black recesses.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.2f;   // dramatic carving
        protected override float Ambient => 0.08f;
        protected override float KeySpecScale => 0.95f;

        public FirePhong3D()
        {
            // Orange key from below — fire lights from beneath.
            KeyLight = new LightSource(
                lx: 0.20f, ly: -0.65f, lz: 0.75f,
                diffR: 1.00f, diffG: 0.55f, diffB: 0.20f,
                specR: 1.00f, specG: 0.80f, specB: 0.40f,
                shininess: 50f);
            // Cool blue back-fill — the cold air around a flame.
            FillLight = new LightSource(
                lx: -0.55f, ly: 0.60f, lz: 0.45f,
                diffR: 0.10f, diffG: 0.20f, diffB: 0.45f,
                specR: 0.10f, specG: 0.20f, specB: 0.50f,
                shininess: 14f);
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
    }

    // =========================================================================
    // GoldenRatio — phi-based hue cycling (each iteration shifts hue by phi)
    // =========================================================================
    public sealed class GoldenRatioPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Golden Ratio 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Golden-ratio hue spiral under warm gold key light — chromatic relief with metallic highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.12f;
        protected override float KeySpecScale => 1.00f;

        private const float Phi = 0.61803398875f;

        public GoldenRatioPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.55f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.90f, diffB: 0.60f,
                specR: 1.00f, specG: 0.92f, specB: 0.65f,
                shininess: 75f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.35f, diffB: 0.55f,
                specR: 0.25f, specG: 0.30f, specB: 0.50f,
                shininess: 18f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * Phi) % 1f;
            var c = ColorUtils.Hsv(h, 0.8f, 1f);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }
    }

    // =========================================================================
    // Greyscale — cycling grey ramp with secondary banding
    // =========================================================================
    public sealed class GrayscalePhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Greyscale 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Cycling grey relief — cool stone-grey under neutral white key, classic chiaroscuro.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.18f;
        protected override float KeySpecScale => 0.55f;

        public GrayscalePhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.60f, lz: 0.85f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 35f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.45f, diffG: 0.45f, diffB: 0.50f,
                specR: 0.30f, specG: 0.30f, specB: 0.30f,
                shininess: 14f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;
            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.12f);
            float v = Math.Clamp(t * 0.75f + band * 0.25f, 0f, 1f);
            aR = v; aG = v; aB = v;
        }
    }

    // =========================================================================
    // HSV — classic hue cycling with distance-darkening
    // =========================================================================
    public sealed class HsvPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "HSV 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Classic HSV hue cycle under crisp white key light — full-spectrum 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.14f;

        public HsvPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.85f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 60f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.40f, diffB: 0.50f,
                specR: 0.25f, specG: 0.25f, specB: 0.30f,
                shininess: 18f);
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
    }

    // =========================================================================
    // MonochromeBands — sinusoidal grey bands
    // =========================================================================
    public sealed class MonoBandPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Monochrome Bands 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Sine-wave grey bands embossed in 3D — sharp ridges under a hard white key.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.0f;   // sharp carving
        protected override float Ambient => 0.10f;
        protected override float KeySpecScale => 0.75f;
        protected override float FillDiffScale => 0.25f;

        public MonoBandPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.65f, ly: 0.55f, lz: 0.75f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 70f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.50f, lz: 0.45f,
                diffR: 0.30f, diffG: 0.30f, diffB: 0.35f,
                specR: 0.20f, specG: 0.20f, specB: 0.25f,
                shininess: 12f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float v = 0.5f + 0.5f * MathF.Sin(smooth * 0.1f);
            aR = v; aG = v; aB = v;
        }
    }

    // =========================================================================
    // NebulaDust — HSV with distance-driven brightness halos
    // =========================================================================
    public sealed class NebulaDustPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Nebula Dust 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Cosmic-fog hue cycle with distance halo, lit by soft violet key — dreamy 3D dust clouds.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.8f;   // soft relief
        protected override float Ambient => 0.16f;  // glowing
        protected override float KeySpecScale => 0.30f;
        protected override float FillDiffScale => 0.45f;

        public NebulaDustPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.55f, lz: 0.85f,
                diffR: 0.85f, diffG: 0.70f, diffB: 1.00f,   // violet
                specR: 0.80f, specG: 0.70f, specB: 1.00f,
                shininess: 22f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.55f, diffG: 0.40f, diffB: 0.65f,
                specR: 0.30f, specG: 0.25f, specB: 0.45f,
                shininess: 10f);
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
    }

    // =========================================================================
    // Painted — fast HSV cycle with lightness boost
    // =========================================================================
    public sealed class PaintedPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Painted 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Vivid jewel-tone cycling under cool white key — painted-relief look with rich saturation.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.5f;
        protected override float Ambient => 0.14f;

        public PaintedPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.60f, ly: 0.65f, lz: 0.80f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 50f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.35f, diffG: 0.40f, diffB: 0.50f,
                specR: 0.25f, specG: 0.30f, specB: 0.40f,
                shininess: 14f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 0.05f) % 1.1f;
            hue -= MathF.Floor(hue);
            float baseValue = 1.0f;
            float lightness = 1.35f - MathF.Min(distance * 0.04f, 1.0f);
            int packed = Fractals.HsvToRgb(hue, 0.9f, baseValue * lightness);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }
    }

    // =========================================================================
    // PaintedReversed — clean 20-unit HSV cycle, dark moody background
    // =========================================================================
    public sealed class PaintedReversedPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Painted Reversed 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Smooth jewel-tone cycle on a moody dark backdrop — 3D relief with deep ambient shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.6f;
        protected override float Ambient => 0.10f;   // moodier than Painted

        public PaintedReversedPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.60f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.95f, diffB: 0.85f,
                specR: 1.00f, specG: 0.95f, specB: 0.85f,
                shininess: 55f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.45f, lz: 0.45f,
                diffR: 0.20f, diffG: 0.25f, diffB: 0.35f,
                specR: 0.15f, specG: 0.20f, specB: 0.30f,
                shininess: 12f);
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
    }

    // =========================================================================
    // Pastelly — soft pastel with desaturation curve
    // =========================================================================
    public sealed class PastellyPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Pastelly 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Soft pastel relief — gentle hue cycle, low specular, washed-out shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 2.0f;   // very gentle relief
        protected override float Ambient => 0.22f;  // bright ambient — pastel feel
        protected override float KeySpecScale => 0.30f;  // matte
        protected override float FillDiffScale => 0.45f;

        public PastellyPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.55f, lz: 0.90f,
                diffR: 0.95f, diffG: 0.95f, diffB: 1.00f,
                specR: 0.85f, specG: 0.90f, specB: 1.00f,
                shininess: 18f);
            FillLight = new LightSource(
                lx: 0.55f, ly: -0.35f, lz: 0.65f,
                diffR: 0.65f, diffG: 0.65f, diffB: 0.75f,
                specR: 0.30f, specG: 0.35f, specB: 0.45f,
                shininess: 8f);
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
    }

    // =========================================================================
    // Psychedelic — ultra-fast hue cycling with interference pattern
    // =========================================================================
    public sealed class PsychedelicPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Psychedelic 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Hyper-cycling rainbow under sharp white key — kaleidoscopic 3D relief with hard specular bands.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.1f;
        protected override float Ambient => 0.10f;
        protected override float KeySpecScale => 1.10f;

        public PsychedelicPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 90f);
            FillLight = new LightSource(
                lx: 0.70f, ly: -0.45f, lz: 0.50f,
                diffR: 0.30f, diffG: 0.40f, diffB: 0.55f,
                specR: 0.30f, specG: 0.40f, specB: 0.55f,
                shininess: 20f);
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
    }

    // =========================================================================
    // RadioInterference (RedAndBlack) — 8-cycle hue at low value
    // =========================================================================
    public sealed class RadioInterferenceOriginalPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Radio Interference Red Phong 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Eight-cycle red/black spiral relief — harsh red key over deep ambient shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.06f;   // very dark recesses

        public RadioInterferenceOriginalPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.60f, ly: 0.60f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.20f, diffB: 0.20f,
                specR: 1.00f, specG: 0.40f, specB: 0.30f,
                shininess: 60f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.45f,
                diffR: 0.20f, diffG: 0.05f, diffB: 0.05f,
                specR: 0.15f, specG: 0.05f, specB: 0.05f,
                shininess: 10f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 8.0f) % 360.0f;
            float saturation = 0.85f;
            // Use smooth-relative value so deep iterations darken naturally.
            float value = Math.Clamp(1.0f - MathF.Pow((float)smooth / MathF.Max(1, maxIter), 0.2f), 0f, 1f);
            int packed = Fractals.HsvToRgb(hue, saturation, value);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }
    }

    // =========================================================================
    // RadioInterference (RedAndBlack) — 8-cycle hue at low value
    // =========================================================================
    public sealed class RadioInterferenceOriginalBluePhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Radio Interference Not Red Phong 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Eight-cycle red/black spiral relief — harsh red key over deep ambient shadows.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.HighContrast |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.08f;   // very dark recesses

        public RadioInterferenceOriginalBluePhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.60f, ly: 0.60f, lz: 0.80f,
                diffR: 0.40f, diffG: 0.30f, diffB: 1.00f,
                specR: 0.30f, specG: 0.40f, specB: 1.00f,
                shininess: 60f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.45f,
                diffR: 0.09f, diffG: 0.05f, diffB: 0.20f,
                specR: 0.09f, specG: 0.05f, specB: 0.15f,
                shininess: 10f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float hue = (smooth * 8.0f) % 360.0f;
            float saturation = 0.85f;
            // Use smooth-relative value so deep iterations darken naturally.
            float value = Math.Clamp(1.0f - MathF.Pow((float)smooth / MathF.Max(1, maxIter), 0.2f), 0f, 1f);
            int packed = Fractals.HsvToRgb(hue, saturation, value);
            aR = ((packed >> 16) & 0xFF) / 255f;
            aG = ((packed >> 8) & 0xFF) / 255f;
            aB = (packed & 0xFF) / 255f;
        }
    }

    // =========================================================================
    // Rainbow — straight HSV hue cycle
    // =========================================================================
    public sealed class RainbowPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Rainbow 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Saturated rainbow cycle under crisp white key — glossy plastic 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.Cyclic | ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.13f;
        protected override float KeySpecScale => 0.95f;

        public RainbowPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.65f, lz: 0.80f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 65f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.40f, diffB: 0.50f,
                specR: 0.25f, specG: 0.25f, specB: 0.30f,
                shininess: 16f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float h = (smooth * 0.015f) % 1f;
            var c = ColorUtils.Hsv(h, 1f, 1f);
            aR = c.R / 255f; aG = c.G / 255f; aB = c.B / 255f;
        }
    }

    // =========================================================================
    // SolarWind — coronal plasma (purple → cyan → corona-white)
    // =========================================================================
    public sealed class SolarWindPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Solar Wind 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Coronal plasma 3D relief — cool blue-violet key, white corona flare on raised ridges.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.10f;
        protected override float KeySpecScale => 1.10f;   // hot corona gleam

        public SolarWindPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 0.80f, diffG: 0.90f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 110f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.20f, diffG: 0.15f, diffB: 0.55f,
                specR: 0.30f, specG: 0.30f, specB: 0.70f,
                shininess: 22f);
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
    }

    // =========================================================================
    // SolarWind-MOD — wider hue sweep, extended saturation
    // =========================================================================
    public sealed class SolarWindModPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Solar Wind MOD 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Wide-spectrum coronal plasma 3D relief — extended hue sweep, dramatic specular flare.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.3f;
        protected override float Ambient => 0.09f;
        protected override float KeySpecScale => 1.20f;

        public SolarWindModPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.55f, ly: 0.70f, lz: 0.75f,
                diffR: 0.85f, diffG: 0.92f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 130f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.30f, diffG: 0.10f, diffB: 0.50f,
                specR: 0.40f, specG: 0.20f, specB: 0.70f,
                shininess: 20f);
        }

        protected override void ComputeAlbedo(float smooth, float distance, int maxIter,
                                              out float aR, out float aG, out float aB)
        {
            float t = smooth * 0.023f;
            float hue = 0.95f - 0.58f * ((t % 1f + 1.02f) % 1f);
            float sat = Math.Clamp(0.88f + 0.20f * MathF.Sin(smooth * 0.08f), 0f, 2f);
            float val = Math.Clamp(0.25f + 0.75f * ((t * 1.7f) % 1f), 0f, 1f);
            float corona = MathF.Exp(-distance * 0.22f);
            var c = ColorUtils.Hsv(hue, Math.Clamp(sat, 0f, 1f), val);
            aR = c.R / 255f + corona * 0.70f;
            aG = c.G / 255f + corona * 0.80f;
            aB = c.B / 255f + corona * 1.00f;
        }
    }

    // =========================================================================
    // TwilightCyclic — three offset sine waves (blue→purple→indigo)
    // =========================================================================
    public sealed class TwilightCyclicPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Twilight Cyclic 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Beating-sine blue/purple/indigo bands under cool dusk lighting — soft 3D atmosphere.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.7f;
        protected override float Ambient => 0.20f;
        protected override float KeySpecScale => 0.40f;
        protected override float FillDiffScale => 0.50f;

        private const float FreqR = 0.0190f;
        private const float FreqG = 0.0110f;
        private const float FreqB = 0.0260f;
        private const float PhaseR = 0.0f;
        private const float PhaseG = 1.0472f;
        private const float PhaseB = 2.0944f;

        public TwilightCyclicPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.50f, ly: 0.55f, lz: 0.85f,
                diffR: 0.70f, diffG: 0.75f, diffB: 1.00f,
                specR: 0.80f, specG: 0.85f, specB: 1.00f,
                shininess: 28f);
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.40f, lz: 0.55f,
                diffR: 0.55f, diffG: 0.40f, diffB: 0.65f,
                specR: 0.30f, specG: 0.20f, specB: 0.45f,
                shininess: 10f);
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
    }

    // =========================================================================
    // VintageSepia — cycling sepia ramp with vignette
    // =========================================================================
    public sealed class VintageSepiaPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Vintage Sepia 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "Aged sepia 3D relief — warm tungsten key, paper-textured surface, deep vignette.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.8f;
        protected override float Ambient => 0.16f;
        protected override float KeySpecScale => 0.35f;   // matte paper
        protected override float FillDiffScale => 0.40f;

        public VintageSepiaPhong3D()
        {
            KeyLight = new LightSource(
                lx: 0.55f, ly: 0.55f, lz: 0.85f,
                diffR: 1.00f, diffG: 0.78f, diffB: 0.50f,   // tungsten warm
                specR: 1.00f, specG: 0.85f, specB: 0.55f,
                shininess: 22f);
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.50f,
                diffR: 0.45f, diffG: 0.30f, diffB: 0.20f,
                specR: 0.30f, specG: 0.20f, specB: 0.10f,
                shininess: 9f);
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
    }

    // =========================================================================
    // WarpedHSV — HSV with non-linear saturation/value and edge glow
    // =========================================================================
    public sealed class WarpedHsvPhong3D : AlgorithmicPhong3DBase
    {
        public static string Name => "Warped HSV 3D";
        public static string Category => "3D Relief";
        public static string Description =>
            "HSV with warped saturation and edge glow — glossy 3D relief with bright boundary highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        protected override float Steepness => 1.4f;
        protected override float Ambient => 0.12f;
        protected override float KeySpecScale => 1.00f;

        public WarpedHsvPhong3D()
        {
            KeyLight = new LightSource(
                lx: -0.50f, ly: 0.65f, lz: 0.85f,
                diffR: 1.00f, diffG: 0.98f, diffB: 0.95f,
                specR: 1.00f, specG: 0.98f, specB: 0.95f,
                shininess: 80f);
            FillLight = new LightSource(
                lx: 0.65f, ly: -0.40f, lz: 0.55f,
                diffR: 0.35f, diffG: 0.40f, diffB: 0.55f,
                specR: 0.25f, specG: 0.30f, specB: 0.45f,
                shininess: 18f);
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
    }
}
