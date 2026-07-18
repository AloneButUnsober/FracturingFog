// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/NeonRelief.cs
//
// Dark near-black surface with three bright neon rim lights (magenta, cyan,
// electric green) placed at shallow angles so they only catch the very tops
// of raised fractal features — producing vivid, glowing outlines on the
// structure against a dark background.  No fill light: the effect relies on
// the high contrast between lit rims and dark recesses.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Dark surface with neon rim lighting — magenta, cyan and green lights
    /// graze the raised fractal features, outlining structure with vivid colour.
    /// </summary>
    public class NeonReliefMap : IColorMap
    {
        public static string Name        => "Neon Relief";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category    => "3D Relief";
        public static string Description => "Dark surface, neon magenta/cyan/green rim lights — glowing outlined 3D structure.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Three neon rim lights at grazing angles (low Lz = shallow angle).
        private static readonly LightSource Magenta = new(
            lx:  0.85f, ly:  0.20f, lz: 0.35f,
            diffR: 0.90f, diffG: 0.00f, diffB: 0.90f,
            specR: 1.00f, specG: 0.20f, specB: 1.00f,
            shininess: 90f);

        private static readonly LightSource Cyan = new(
            lx: -0.75f, ly: -0.30f, lz: 0.30f,
            diffR: 0.00f, diffG: 0.90f, diffB: 0.90f,
            specR: 0.10f, specG: 1.00f, specB: 1.00f,
            shininess: 90f);

        private static readonly LightSource Green = new(
            lx:  0.10f, ly: -0.85f, lz: 0.28f,
            diffR: 0.10f, diffG: 1.00f, diffB: 0.20f,
            specR: 0.10f, specG: 1.00f, specB: 0.30f,
            shininess: 90f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // Very shallow steepness → grazing angles catch only the sharpest edges.
            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 0.6f);

            // Base: near-black with very slow hue hint (nearly invisible).
            float hueHint = (smooth * 0.008f) % 1f;
            var   hint    = ColorUtils.Hsv(hueHint, 0.30f, 0.04f);
            float r = hint.R / 255f;
            float g = hint.G / 255f;
            float b = hint.B / 255f;

            // Apply each neon rim — both diffuse and sharp specular.
            AddNeon(Nx,Ny,Nz, Magenta, ref r, ref g, ref b);
            AddNeon(Nx,Ny,Nz, Cyan,    ref r, ref g, ref b);
            AddNeon(Nx,Ny,Nz, Green,   ref r, ref g, ref b);

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        private static void AddNeon(float Nx, float Ny, float Nz,
                                     in LightSource light,
                                     ref float r, ref float g, ref float b)
        {
            float diff = MathF.Max(0f, Nx*light.Lx + Ny*light.Ly + Nz*light.Lz);
            r += diff * light.DiffR * 0.55f;
            g += diff * light.DiffG * 0.55f;
            b += diff * light.DiffB * 0.55f;

            float hx = light.Lx, hy = light.Ly, hz = light.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            if (hl < 1e-8f) return;
            hx/=hl; hy/=hl; hz/=hl;
            float spec = MathF.Pow(MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz), light.Shininess) * 1.5f;
            r += spec * light.SpecR;
            g += spec * light.SpecG;
            b += spec * light.SpecB;
        }
    }
}
