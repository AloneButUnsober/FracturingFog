// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/CrystalCave.cs
//
// Two opposing rim lights (left and right) plus a cold overhead fill give a
// crystal/ice facet appearance.  Very high shininess (300) produces pinpoint
// specular highlights suggesting crystalline facets.  The base colour is
// pale blue-grey cycling slowly to near-white at high iteration counts.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Crystalline ice facets — two opposing rim lights, pinpoint specular
    /// highlights, pale blue-white palette cycling to near-white at depth.
    /// </summary>
    public class CrystalCaveMap : IColorMap
    {
        public static string Name        => "Crystal Cave";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category    => "3D Relief";
        public static string Description => "Ice-crystal facets — two opposing rim lights, high-frequency pinpoint specular.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect;

        public int MaxIterations { get; set; } = 1000;

        // Left rim light: cold blue.
        private static readonly LightSource LeftRim = new(
            lx: -1.0f, ly:  0.2f, lz: 0.8f,
            diffR: 0.35f, diffG: 0.55f, diffB: 0.90f,
            specR: 0.80f, specG: 0.90f, specB: 1.00f,
            shininess: 300f);

        // Right rim light: slightly warmer blue-white.
        private static readonly LightSource RightRim = new(
            lx:  1.0f, ly: -0.1f, lz: 0.6f,
            diffR: 0.50f, diffG: 0.65f, diffB: 0.85f,
            specR: 1.00f, specG: 1.00f, specB: 1.00f,
            shininess: 300f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // Shallow steepness → very faceted carving.
            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 0.9f);

            // Base colour: pale blue-grey → icy white.
            float t     = Math.Clamp(smooth / iterations, 0f, 1f);
            float cycle = 0.5f + 0.5f * MathF.Sin(smooth * 0.015f * MathF.PI * 2f);
            float baseR = 0.55f + 0.30f * cycle * t;
            float baseG = 0.62f + 0.28f * cycle * t;
            float baseB = 0.80f + 0.18f * cycle * t;

            // Cold ambient (near-black, very dark blue cave).
            float r = baseR * 0.06f;
            float g = baseG * 0.08f;
            float b = baseB * 0.15f;

            // Left rim.
            float diffL = MathF.Max(0f, Nx * LeftRim.Lx  + Ny * LeftRim.Ly  + Nz * LeftRim.Lz);
            r += diffL * LeftRim.DiffR  * baseR;
            g += diffL * LeftRim.DiffG  * baseG;
            b += diffL * LeftRim.DiffB  * baseB;

            // Right rim.
            float diffR2 = MathF.Max(0f, Nx * RightRim.Lx + Ny * RightRim.Ly + Nz * RightRim.Lz);
            r += diffR2 * RightRim.DiffR * baseR;
            g += diffR2 * RightRim.DiffG * baseG;
            b += diffR2 * RightRim.DiffB * baseB;

            // Left specular.
            AddSpec(Nx, Ny, Nz, LeftRim,  ref r, ref g, ref b);
            AddSpec(Nx, Ny, Nz, RightRim, ref r, ref g, ref b);

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        private static void AddSpec(float Nx, float Ny, float Nz,
                                     in LightSource light,
                                     ref float r, ref float g, ref float b)
        {
            float hx = light.Lx, hy = light.Ly, hz = light.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            if (hl < 1e-8f) return;
            hx/=hl; hy/=hl; hz/=hl;
            float s = MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz);
            s = MathF.Pow(s, light.Shininess) * 1.2f;
            r += s * light.SpecR;
            g += s * light.SpecG;
            b += s * light.SpecB;
        }
    }
}
