// Models/ColorSchemes/VolcanicRock.cs
//
// Two-light setup: a cool overhead key (simulating overcast sky) and a warm
// orange back-rim light (simulating lava glow beneath the rock).  The base
// colour is very dark grey-black (basalt).  Where the back-light catches the
// upper edge of elevated surface features, orange-red hues bleed in, giving
// the appearance of volcanic rock silhouetted against molten lava.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Dark basalt with orange lava back-glow — cool overhead fill + warm
    /// orange rim light from below simulating volcanic illumination.
    /// </summary>
    public class VolcanicRockMap : IColorMap
    {
        public static string Name        => "Volcanic Rock";
        public static string Category    => "3D Relief";
        public static string Description => "Dark basalt silhouetted against lava — cool overhead + warm orange back-rim light.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Overhead cool key (pale sky light).
        private static readonly LightSource Sky = new(
            lx:  0.1f, ly:  0.9f, lz: 0.8f,
            diffR: 0.55f, diffG: 0.65f, diffB: 0.80f,
            specR: 0.60f, specG: 0.70f, specB: 0.90f,
            shininess: 35f);

        // Lava back-rim: warm orange, from below and behind.
        private static readonly LightSource Lava = new(
            lx:  0.0f, ly: -0.8f, lz: 0.4f,
            diffR: 1.00f, diffG: 0.35f, diffB: 0.05f,
            specR: 1.00f, specG: 0.60f, specB: 0.20f,
            shininess: 20f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.0f);

            // Dark basalt base with very slight warm-cool variation.
            float t     = smooth * 0.018f % 1f;
            float baseR = 0.10f + 0.08f * t;
            float baseG = 0.09f + 0.06f * t;
            float baseB = 0.08f + 0.05f * t;

            // Near-zero ambient: deep shadows are almost completely black.
            float r = baseR * 0.04f;
            float g = baseG * 0.04f;
            float b = baseB * 0.04f;

            // Sky diffuse (soft, cool).
            float diffSky = MathF.Max(0f, Nx*Sky.Lx + Ny*Sky.Ly + Nz*Sky.Lz);
            r += diffSky * Sky.DiffR * baseR * 2.5f;
            g += diffSky * Sky.DiffG * baseG * 2.5f;
            b += diffSky * Sky.DiffB * baseB * 2.5f;

            // Lava rim diffuse (orange halo on back-facing surfaces).
            float diffLava = MathF.Max(0f, Nx*Lava.Lx + Ny*Lava.Ly + Nz*Lava.Lz);
            r += diffLava * Lava.DiffR * 0.60f;
            g += diffLava * Lava.DiffG * 0.60f;
            b += diffLava * Lava.DiffB * 0.60f;

            // Sky specular (subtle).
            r += SpecC(Nx,Ny,Nz, Sky,  0.45f) * Sky.SpecR;
            g += SpecC(Nx,Ny,Nz, Sky,  0.45f) * Sky.SpecG;
            b += SpecC(Nx,Ny,Nz, Sky,  0.45f) * Sky.SpecB;

            // Lava specular (orange gleam on rock edges).
            r += SpecC(Nx,Ny,Nz, Lava, 0.70f) * Lava.SpecR;
            g += SpecC(Nx,Ny,Nz, Lava, 0.70f) * Lava.SpecG;
            b += SpecC(Nx,Ny,Nz, Lava, 0.70f) * Lava.SpecB;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
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
