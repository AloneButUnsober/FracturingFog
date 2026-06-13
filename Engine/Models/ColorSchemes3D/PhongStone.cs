// Models/ColorSchemes/PhongStone.cs  — v2 (BUG FIX)
//
// ROOT CAUSE OF BLACK DISPLAY (now fixed):
//
// PhongStoneMap previously inherited GradientPhong3DBase but:
//   1. Never populated Stops[] — the base's gradient list was empty
//   2. Never assigned KeyLight / FillLight on the base struct fields
//   3. Declared Map(s,d,i,nx,ny) as a plain CLASS method, not an explicit
//      interface implementation
//
// The calculator calls  colorMap.Map(s,d,i,nx,ny)  through an IColorMap
// reference.  C# therefore routes to GradientPhong3DBase's sealed explicit
// interface implementation:
//
//     sealed int IColorMap.Map(...,nx,ny)  →  LitMap(s,d,i,nx,ny)
//
// LitMap calls MapNormalized() on the EMPTY Stops[] list which returns
// 0xFF000000 (solid black) for every pixel.  The hand-written lighting
// code in the subclass was never reached.
//
// FIX: Implement IColorMap directly (identical pattern to GoldReliefMap,
// LunarSurfaceMap, MoltenMetalMap, etc.) and declare the 5-param overload
// as an EXPLICIT INTERFACE IMPLEMENTATION so the calculator's interface-
// typed call routes here instead of to the default method.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Classic Phong shading on a grey granite surface with a warm key light.
    /// Produces the look of carved stone mathematical relief art.
    /// </summary>
    public class PhongStoneMap : IColorMap   // ← IColorMap directly, NOT GradientPhong3DBase
    {
        public static string Name => "Phong Stone";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category => "3D Relief";
        public static string Description => "Carved grey granite with warm key light — classic mathematical relief art.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Single warm key light: upper-right, slightly toward the viewer.
        private static readonly LightSource Key = new(
            lx: 0.6f, ly: 0.5f, lz: 0.9f,
            diffR: 1.00f, diffG: 0.90f, diffB: 0.75f,   // warm diffuse
            specR: 1.00f, specG: 0.95f, specB: 0.85f,   // warm specular
            shininess: 40f);

        // ── 3-param fallback — called when no normal data is available ─────────
        public int Map(float smooth, float distance, int iterations)
            => LitMap(smooth, distance, iterations, 0f, 0f);

        // ── 5-param EXPLICIT INTERFACE IMPLEMENTATION ─────────────────────────
        // Declared as 'int IColorMap.Map(...)' so the calculator's interface-
        // typed call (colorMap.Map(s, d, i, nx, ny)) routes directly here,
        // bypassing the default interface method which would discard nx/ny.
        int IColorMap.Map(float smooth, float distance, int iterations,
                          float nx, float ny)
            => LitMap(smooth, distance, iterations, nx, ny);

        // ── Core Phong implementation ─────────────────────────────────────────
        private static int LitMap(float smooth, float distance, int iterations,
                                   float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.8f);

            // Surface colour: slow cool-grey cycle with a slight blue tint.
            float t = (smooth * 0.012f) % 1f;
            float band = 0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f);
            float baseR = 0.35f + 0.20f * band;
            float baseG = 0.37f + 0.20f * band;
            float baseB = 0.42f + 0.22f * band;

            // Ambient: dim cool fill.
            const float ka = 0.18f;
            float r = baseR * ka * 0.7f;
            float g = baseG * ka * 0.8f;
            float b = baseB * ka * 1.0f;

            // Diffuse — surface albedo modulated.
            float diff = MathF.Max(0f, Nx * Key.Lx + Ny * Key.Ly + Nz * Key.Lz);
            r += diff * Key.DiffR * baseR * 0.80f;
            g += diff * Key.DiffG * baseG * 0.80f;
            b += diff * Key.DiffB * baseB * 0.80f;

            // Blinn-Phong specular (not surface-coloured — gives stone sheen).
            float hx = Key.Lx, hy = Key.Ly, hz = Key.Lz + 1.0f;
            float hl = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hl > 1e-8f)
            {
                hx /= hl; hy /= hl; hz /= hl;
                float spec = MathF.Pow(
                    MathF.Max(0f, Nx * hx + Ny * hy + Nz * hz),
                    Key.Shininess) * 0.55f;
                r += spec * Key.SpecR;
                g += spec * Key.SpecG;
                b += spec * Key.SpecB;
            }

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}