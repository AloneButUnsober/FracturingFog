// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/MoltenMetal.cs
//
// A directional forge-light from lower-left illuminates a glowing metal
// surface.  The base colour transitions from black (cool) through deep red
// and orange to bright yellow-white (incandescent) as smooth iterations
// increase.  A tight specular highlight adds the sharp liquid-metal gleam.
// The normal gives deep, dimensional furrows to the fractal structure.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Incandescent molten metal — base colour from black → red → orange → white,
    /// with a tight specular highlight and dramatic shadowing from a forge light.
    /// </summary>
    public class MoltenMetalMap : IColorMap
    {
        public static string Name        => "Molten Metal";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category    => "3D Relief";
        public static string Description => "Incandescent metal surface — cool/hot colour gradient with sharp forge-light highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Forge light: from the lower-left, strong orange-white.
        private static readonly LightSource Forge = new(
            lx: -0.5f, ly: -0.7f, lz: 1.0f,
            diffR: 1.0f, diffG: 0.75f, diffB: 0.40f,   // warm orange diffuse
            specR: 1.0f, specG: 0.98f, specB: 0.90f,   // near-white specular
            shininess: 120f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.2f);

            // Incandescent colour ramp: cool (black) → red → orange → white.
            float t = MathF.Min(smooth * 4f / iterations, 1f);
            float t2 = t * t;

            float baseR = Math.Clamp(t  * 1.8f, 0f, 1f);
            float baseG = Math.Clamp(t2 * 2.2f, 0f, 1f);
            float baseB = Math.Clamp((t - 0.7f) * 3.5f, 0f, 1f);

            // Ambient: very dim, keeps the deep shadow areas just visible.
            const float ka = 0.05f;
            float r = baseR * ka;
            float g = baseG * ka;
            float b = baseB * ka;

            // Diffuse.
            float diff = MathF.Max(0f, Nx * Forge.Lx + Ny * Forge.Ly + Nz * Forge.Lz);
            r += diff * Forge.DiffR * baseR;
            g += diff * Forge.DiffG * baseG;
            b += diff * Forge.DiffB * baseB;

            // Tight specular — liquid metal gleam.
            float hx = Forge.Lx, hy = Forge.Ly, hz = Forge.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            hx/=hl; hy/=hl; hz/=hl;
            float spec = MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz);
            spec = MathF.Pow(spec, Forge.Shininess) * 1.4f;
            r += spec * Forge.SpecR;
            g += spec * Forge.SpecG;
            b += spec * Forge.SpecB;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
