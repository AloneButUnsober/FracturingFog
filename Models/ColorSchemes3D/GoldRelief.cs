// Models/ColorSchemes/GoldRelief.cs
//
// Two complementary light sources — a warm golden key light and a cooler
// fill light — illuminate a gold metallic surface.  The base colour cycles
// between dark burnished gold and bright polished gold.  The combination of
// key highlight, fill-side diffuse and tight specular gleam gives the
// characteristic dimensional depth of hammered gold leaf.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Hammered gold relief — warm key light + cool fill, cycling from dark
    /// burnished to bright polished gold.  Rich 3D depth.
    /// </summary>
    public class GoldReliefMap : IColorMap
    {
        public static string Name        => "Gold Relief";
        public static string Category    => "3D Relief";
        public static string Description => "Hammered gold — warm key + cool fill, cycling burnished-to-polished gold tones.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Warm golden key light: upper-right.
        private static readonly LightSource Key = new(
            lx:  0.7f, ly: 0.4f, lz: 0.8f,
            diffR: 1.00f, diffG: 0.85f, diffB: 0.30f,   // golden diffuse
            specR: 1.00f, specG: 0.95f, specB: 0.60f,   // warm specular
            shininess: 80f);

        // Cool blue fill: from lower-left, softer.
        private static readonly LightSource Fill = new(
            lx: -0.8f, ly: -0.5f, lz: 0.5f,
            diffR: 0.20f, diffG: 0.25f, diffB: 0.50f,   // cool blue fill
            specR: 0.40f, specG: 0.50f, specB: 0.80f,
            shininess: 30f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.4f);

            // Base colour: cycles between dark/burnished and bright/polished gold.
            float cycle = 0.5f + 0.5f * MathF.Sin(smooth * 0.025f * MathF.PI * 2f);
            float baseR = 0.60f + 0.38f * cycle;
            float baseG = 0.45f + 0.30f * cycle;
            float baseB = 0.05f + 0.12f * cycle;

            // Warm ambient (slightly golden shadow).
            float r = baseR * 0.12f;
            float g = baseG * 0.10f;
            float b = baseB * 0.08f;

            // Key light diffuse.
            float diffKey = MathF.Max(0f, Nx*Key.Lx + Ny*Key.Ly + Nz*Key.Lz);
            r += diffKey * Key.DiffR * baseR;
            g += diffKey * Key.DiffG * baseG;
            b += diffKey * Key.DiffB * baseB;

            // Fill light diffuse.
            float diffFill = MathF.Max(0f, Nx*Fill.Lx + Ny*Fill.Ly + Nz*Fill.Lz);
            r += diffFill * Fill.DiffR * baseR * 0.6f;
            g += diffFill * Fill.DiffG * baseG * 0.6f;
            b += diffFill * Fill.DiffB * baseB * 0.6f;

            // Key specular (tight golden gleam).
            r += SpecContrib(Nx,Ny,Nz, Key,  1.0f) * Key.SpecR;
            g += SpecContrib(Nx,Ny,Nz, Key,  1.0f) * Key.SpecG;
            b += SpecContrib(Nx,Ny,Nz, Key,  1.0f) * Key.SpecB;

            // Fill specular (softer blue-tinted backlight gleam).
            r += SpecContrib(Nx,Ny,Nz, Fill, 0.4f) * Fill.SpecR;
            g += SpecContrib(Nx,Ny,Nz, Fill, 0.4f) * Fill.SpecG;
            b += SpecContrib(Nx,Ny,Nz, Fill, 0.4f) * Fill.SpecB;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        private static float SpecContrib(float Nx, float Ny, float Nz,
                                          in LightSource light, float scale)
        {
            float hx = light.Lx, hy = light.Ly, hz = light.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            if (hl < 1e-8f) return 0f;
            hx/=hl; hy/=hl; hz/=hl;
            float s = MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz);
            return MathF.Pow(s, light.Shininess) * scale;
        }
    }
}
