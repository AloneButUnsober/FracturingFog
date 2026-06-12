// Models/ColorSchemes/LunarSurface.cs
//
// Simulates the appearance of the lunar surface under direct unfiltered
// sunlight: a single very bright directional key light with no fill (no
// atmosphere to scatter light), deep opaque shadows, and a subtle grey
// colour variation from crater albedo differences.  The high contrast
// between lit and shadowed surfaces emphasises every surface normal
// change, making the 3D structure extremely pronounced.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Lunar regolith under unfiltered sunlight — harsh single key light,
    /// no fill, deep black shadows.  Maximises apparent 3D depth.
    /// </summary>
    public class LunarSurfaceMap : IColorMap
    {
        public static string Name        => "Lunar Surface";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category    => "3D Relief";
        public static string Description => "Harsh single sunlight, deep black vacuum shadows — extreme 3D contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Single directional sun: 40° above horizontal, slightly to the left.
        private static readonly LightSource Sun = new(
            lx: -0.6f, ly: 0.3f, lz: 0.75f,
            diffR: 1.00f, diffG: 0.97f, diffB: 0.90f,   // slightly warm sunlight
            specR: 1.00f, specG: 0.99f, specB: 0.95f,
            shininess: 25f);

        public int Map(float smooth, float distance, int iterations)
            => Map(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // Deep carving: low steepness = more extreme normals = more contrast.
            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 0.8f);

            // Regolith base: mid-grey with subtle albedo variation.
            float albedoVar = 0.5f + 0.5f * MathF.Sin(smooth * 0.009f * MathF.PI * 2f);
            float baseGrey  = 0.40f + 0.18f * albedoVar;

            // Almost zero ambient: vacuum — no scattered light.
            float r = baseGrey * 0.02f;
            float g = baseGrey * 0.02f;
            float b = baseGrey * 0.02f;

            // Diffuse.
            float diff = MathF.Max(0f, Nx*Sun.Lx + Ny*Sun.Ly + Nz*Sun.Lz);
            r += diff * Sun.DiffR * baseGrey;
            g += diff * Sun.DiffG * baseGrey;
            b += diff * Sun.DiffB * baseGrey;

            // Specular: low shininess → broad highlight consistent with rough regolith.
            float hx = Sun.Lx, hy = Sun.Ly, hz = Sun.Lz + 1.0f;
            float hl = MathF.Sqrt(hx*hx + hy*hy + hz*hz);
            hx/=hl; hy/=hl; hz/=hl;
            float spec = MathF.Pow(MathF.Max(0f, Nx*hx + Ny*hy + Nz*hz), Sun.Shininess) * 0.30f;
            r += spec * Sun.SpecR;
            g += spec * Sun.SpecG;
            b += spec * Sun.SpecB;

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
