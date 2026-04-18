// Models/ColorSchemes/PhongStone.cs
//
// A single warm directional key light illuminates a grey granite surface.
// The surface colour cycles slowly through cool grey–blue tones based on the
// smooth iteration count — giving the carved-stone relief appearance of
// traditional mathematical art prints.
//
// Light placement: 45° above horizontal, 30° to the right.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Classic Phong shading on a grey granite surface with a warm key light.
    /// Produces the look of carved stone mathematical relief art.
    /// </summary>
    public class PhongStoneMap : GradientPhong3DBase
    {
        public static string Name        => "Phong Stone";
        public static string Category    => "3D Relief";
        public static string Description => "Carved grey granite with warm key light — classic mathematical relief art.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        //public int MaxIterations { get; set; } = 1000;

        // Single warm key light: upper-right, slightly toward the viewer.
        private static readonly LightSource Key = new(
            lx:  0.6f, ly: 0.5f, lz: 0.9f,
            diffR: 1.00f, diffG: 0.90f, diffB: 0.75f,   // warm diffuse
            specR: 1.00f, specG: 0.95f, specB: 0.85f,   // warm specular
            shininess: 40f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.8f);

            // Surface colour: slow cool-grey cycle with a slight blue tint.
            float t      = (smooth * 0.012f) % 1f;
            float band   = 0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f);
            float baseR  = 0.35f + 0.20f * band;
            float baseG  = 0.37f + 0.20f * band;
            float baseB  = 0.42f + 0.22f * band;

            // Ambient: dim cool fill light.
            const float ka = 0.18f;
            float r = baseR * ka * 0.7f;
            float g = baseG * ka * 0.8f;
            float b = baseB * ka * 1.0f;

            PhongHelper.AccumulateLight(Nx, Ny, Nz, in Key, ref r, ref g, ref b);

            // Modulate diffuse contribution by base colour (surface albedo).
            // The specular is already in r/g/b from AccumulateLight.
            // Re-apply to make diffuse surface-coloured.
            float diff = MathF.Max(0f, Nx * Key.Lx + Ny * Key.Ly + Nz * Key.Lz);
            r = (baseR * (ka + diff * 0.80f));
            g = (baseG * (ka + diff * 0.80f));
            b = (baseB * (ka + diff * 0.80f));

            // Add specular on top (non-coloured specular for stone sheen).
            float hx = Key.Lx, hy = Key.Ly, hz = Key.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            hx/=hl; hy/=hl; hz/=hl;
            float spec = MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz);
            spec = MathF.Pow(spec, Key.Shininess) * 0.55f;
            r += spec * 1.00f;
            g += spec * 0.95f;
            b += spec * 0.85f;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
