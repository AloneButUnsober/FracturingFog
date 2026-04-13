// Models/ColorSchemes/AncientBronze.cs
//
// Three-point lighting on an aged bronze surface.  The base colour cycles
// between deep verdigris green (oxidised recesses) and bright copper-orange
// (polished raised surfaces).  Three lights cover key, fill, and a blue-sky
// back-fill so both lit and shadowed faces have interesting colour.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Ancient oxidised bronze — three-point lighting, verdigris green recesses
    /// cycling to copper highlights, classic patinated metal 3D effect.
    /// </summary>
    public class AncientBronzeMap : IColorMap
    {
        public static string Name        => "Ancient Bronze";
        public static string Category    => "3D Relief";
        public static string Description => "Three-point lit oxidised bronze — verdigris recesses, copper highlights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect;

        public int MaxIterations { get; set; } = 1000;

        // Key: warm directional light from upper-left.
        private static readonly LightSource Key = new(
            lx: -0.55f, ly: 0.50f, lz: 0.90f,
            diffR: 1.00f, diffG: 0.80f, diffB: 0.45f,
            specR: 1.00f, specG: 0.90f, specB: 0.60f,
            shininess: 70f);

        // Fill: soft neutral from the right.
        private static readonly LightSource Fill = new(
            lx:  0.80f, ly: 0.10f, lz: 0.55f,
            diffR: 0.45f, diffG: 0.55f, diffB: 0.50f,
            specR: 0.20f, specG: 0.30f, specB: 0.20f,
            shininess: 20f);

        // Back-fill: sky blue from behind/below.
        private static readonly LightSource BackFill = new(
            lx: 0.05f, ly: -0.70f, lz: 0.35f,
            diffR: 0.20f, diffG: 0.35f, diffB: 0.60f,
            specR: 0.10f, specG: 0.20f, specB: 0.40f,
            shininess: 10f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.6f);

            // Base colour: green verdigris at low t → copper at high t.
            float t = 0.5f + 0.5f * MathF.Sin(smooth * 0.020f * MathF.PI * 2f);
            float baseR = 0.12f + 0.65f * t;   // black → orange-copper
            float baseG = 0.28f + 0.20f * t;   // muted green → copper
            float baseB = 0.18f - 0.10f * t;   // slight green → near-zero

            // Ambient: dim warm oxidised green.
            float r = baseR * 0.10f;
            float g = baseG * 0.15f;
            float b = baseB * 0.12f;

            // Key, fill, back-fill diffuse.
            Apply(Nx, Ny, Nz, Key,      baseR, baseG, baseB, 1.00f, ref r, ref g, ref b);
            Apply(Nx, Ny, Nz, Fill,     baseR, baseG, baseB, 0.55f, ref r, ref g, ref b);
            Apply(Nx, Ny, Nz, BackFill, baseR, baseG, baseB, 0.40f, ref r, ref g, ref b);

            // Key specular only (the shiny raised surfaces catch the key).
            r += SpecC(Nx,Ny,Nz, Key, 0.80f) * Key.SpecR;
            g += SpecC(Nx,Ny,Nz, Key, 0.80f) * Key.SpecG;
            b += SpecC(Nx,Ny,Nz, Key, 0.80f) * Key.SpecB;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        private static void Apply(float Nx, float Ny, float Nz,
                                   in LightSource light,
                                   float baseR, float baseG, float baseB,
                                   float scale,
                                   ref float r, ref float g, ref float b)
        {
            float diff = MathF.Max(0f, Nx*light.Lx + Ny*light.Ly + Nz*light.Lz) * scale;
            r += diff * light.DiffR * baseR;
            g += diff * light.DiffG * baseG;
            b += diff * light.DiffB * baseB;
        }

        private static float SpecC(float Nx, float Ny, float Nz,
                                    in LightSource light, float scale)
        {
            float hx = light.Lx, hy = light.Ly, hz = light.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            if (hl < 1e-8f) return 0f;
            hx/=hl; hy/=hl; hz/=hl;
            return MathF.Pow(MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz), light.Shininess) * scale;
        }
    }
}
