// Models/ColorSchemes/MarbleRelief.cs
//
// An overhead white light illuminates a Carrara marble surface.  The base
// colour is a very soft warm grey-white that shifts slightly toward cream at
// low iteration counts (deep relief) and near-white at high counts (surface
// edges).  The result resembles a fine marble bas-relief sculpture.
// A very mild distance-based vein effect adds the subtle grey veining
// characteristic of Carrara marble.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// White Carrara marble bas-relief — overhead light, subtle grey veining,
    /// soft warm shadows in recessed areas.
    /// </summary>
    public class MarbleReliefMap : IColorMap
    {
        public static string Name        => "Marble Relief";
        public static string Category    => "3D Relief";
        public static string Description => "Carved white Carrara marble — overhead light with soft warm shadows and grey veining.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesDistance |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.ThreeDEffect;

        public int MaxIterations { get; set; } = 1000;

        // Overhead white key light.
        private static readonly LightSource Overhead = new(
            lx: 0.2f, ly: 0.8f, lz: 1.0f,
            diffR: 1.00f, diffG: 0.98f, diffB: 0.95f,   // near-white diffuse
            specR: 1.00f, specG: 1.00f, specB: 1.00f,   // pure white specular
            shininess: 60f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 2.2f);

            // Marble vein: slight grey darkening from distance-modulated sin wave.
            float vein  = 0.5f + 0.5f * MathF.Sin(distance * 0.6f + smooth * 0.03f);
            float baseR = 0.90f - vein * 0.12f;
            float baseG = 0.88f - vein * 0.12f;
            float baseB = 0.86f - vein * 0.10f;

            // Warm ambient — recessed areas glow with a faint candlelight warmth.
            float r = baseR * 0.22f;
            float g = baseG * 0.20f;
            float b = baseB * 0.16f;

            // Diffuse.
            float diff = MathF.Max(0f, Nx*Overhead.Lx + Ny*Overhead.Ly + Nz*Overhead.Lz);
            r += diff * Overhead.DiffR * baseR;
            g += diff * Overhead.DiffG * baseG;
            b += diff * Overhead.DiffB * baseB;

            // Specular — soft stone polish.
            float hx = Overhead.Lx, hy = Overhead.Ly, hz = Overhead.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            hx/=hl; hy/=hl; hz/=hl;
            float spec = MathF.Pow(MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz), Overhead.Shininess) * 0.50f;
            r += spec;
            g += spec;
            b += spec;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
