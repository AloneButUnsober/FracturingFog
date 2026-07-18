// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes3D/LambertSlopeThemes.cs
//
// Lightweight standalone 3D-style themes that read the per-pixel surface
// normal (nx, ny) without paying the full Phong/PBR cost.
//
//   • LambertShadingMap — gradient albedo modulated by pure diffuse N·L.
//   • SlopeShadingMap   — gradient remapped by slope magnitude |N_xy|.
//
// Both inherit from GradientColorMap so the user can edit / export stops in
// the existing theme editor.  The 5-parameter Map overload is overridden to
// fold in the surface normal; the 3-parameter overload falls back to the
// plain gradient for swatches.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // =========================================================================
    // Lambert standalone shading
    // =========================================================================

    /// <summary>
    /// Pure Lambert (diffuse-only) shading: gradient colour at the smooth-iter
    /// position is multiplied by <c>max(0, N·L)</c> with a single configurable
    /// directional light.  No specular, no fill, no ambient term beyond
    /// <see cref="Ambient"/>.  Much cheaper than Phong / PBR while still
    /// conveying surface relief.
    /// </summary>
    public sealed class LambertShadingMap : GradientColorMap, IColorMap
    {
        public static string Name => "Lambert Relief";
        public static string Category => "3D Relief";
        public static string Description =>
            "Pure Lambert diffuse shading: gradient albedo × max(0, N·L) with a single " +
            "directional light.  Lightweight stand-in for Phong / PBR.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Light direction (normalized): up-and-right with elevation.
        private const float Lx = 0.40f;
        private const float Ly = -0.30f;
        private const float Lz = 0.866f;

        /// <summary>Surface steepness — larger = flatter, smaller = more relief.</summary>
        private const float Steepness = 1.4f;
        /// <summary>Constant ambient term added before tone clamp.</summary>
        private const float Ambient = 0.18f;

        public LambertShadingMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(15, 20, 35)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(70, 100, 145)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(180, 200, 210)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(240, 215, 160)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 235)));
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations)
                return unchecked((int)0xFF000000);

            float t = iterations > 0 ? smooth / iterations : 0f;
            int albedo = MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
            float aR = ((albedo >> 16) & 0xFF) / 255f;
            float aG = ((albedo >>  8) & 0xFF) / 255f;
            float aB = ( albedo        & 0xFF) / 255f;

            // Build 3D normal: (nx, -ny, Steepness) normalised.  ny is negated
            // to match the screen-space "y down" convention used by the 3D
            // bases (see AlgorithmicLighting3DBase.LitMap).
            float ry = -ny;
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
    // Slope shading
    // =========================================================================

    /// <summary>
    /// Slope shading: gradient albedo modulated by exterior distance estimate
    /// (small distance = steep gradient = darker) combined with Lambert relief
    /// from a top-front key light.  Bright on flats, darker on steep slopes —
    /// gives topographic-relief contour look.
    /// </summary>
    /// <remarks>
    /// IMPORTANT: the calculator's (nx, ny) are UNIT-NORMALISED partial
    /// derivatives — <c>nx² + ny² == 1</c> for every escaped pixel.  Magnitude
    /// information is lost.  The earlier <c>sqrt(nx²+ny²)</c> formula therefore
    /// produced <c>slope ≈ 1</c> everywhere → k ≈ Ambient → uniformly dark,
    /// no 3D effect.  The distance-estimator carries the magnitude signal we
    /// need; (nx, ny) is reused for directional Lambert shading.
    /// </remarks>
    public sealed class SlopeShadingMap : GradientColorMap, IColorMap
    {
        public static string Name => "Slope Relief";
        public static string Category => "3D Relief";
        public static string Description =>
            "Topographic slope shading: gradient albedo darkened near the boundary " +
            "(steep potential gradient) with Lambert relief from a top-front key " +
            "light.  Contour-map aesthetic.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Top-front key light — high elevation so flat areas read bright.
        private const float Lx = 0.30f;
        private const float Ly = -0.25f;
        private const float Lz = 0.918f;

        /// <summary>Surface steepness for the 3D normal build (smaller = more relief).</summary>
        private const float Steepness = 1.2f;
        /// <summary>How quickly distance maps to flatness; higher = sharper contours.</summary>
        private const float DistanceFalloff = 5.0f;
        /// <summary>Lambert weight blended into the topographic shading.</summary>
        private const float LambertWeight = 0.55f;
        private const float Ambient = 0.22f;

        public SlopeShadingMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(30, 25, 20)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(110, 85, 50)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(195, 175, 120)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(220, 210, 180)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(245, 240, 225)));
        }

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations)
                return unchecked((int)0xFF000000);

            float t = iterations > 0 ? smooth / iterations : 0f;
            int albedo = MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
            float aR = ((albedo >> 16) & 0xFF) / 255f;
            float aG = ((albedo >>  8) & 0xFF) / 255f;
            float aB = ( albedo        & 0xFF) / 255f;

            // Topographic magnitude from the exterior distance estimator.
            // distance → 0 near the boundary (steep), grows away from it (flat).
            float flatness = 1f - MathF.Exp(-distance * DistanceFalloff);

            // Build 3D normal with steepness (same convention as the 3D bases).
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else             { Nx = 0f;       Ny = 0f;       Nz = 1f; }

            float NL = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);

            // Blend topographic darkening (distance) with Lambert relief (normal).
            float shade = (1f - LambertWeight) * flatness + LambertWeight * NL;
            float k = Ambient + (1f - Ambient) * shade;

            byte R = (byte)(System.Math.Clamp(aR * k, 0f, 1f) * 255f);
            byte G = (byte)(System.Math.Clamp(aG * k, 0f, 1f) * 255f);
            byte B = (byte)(System.Math.Clamp(aB * k, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
