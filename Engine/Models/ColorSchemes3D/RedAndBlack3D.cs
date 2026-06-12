// Models/ColorSchemes3D/RedAndBlack3D.cs
//
// Phong-lit 3D version of the original RedAndBlack / Radio Interference theme.
//
// The existing RadioInterferencePhong3D in RadioInterferenceThemes.cs samples
// the rainbow into a smoothly-interpolated 17-stop gradient, which produces
// the rainbow rings but loses the "interference" aliasing that defines the
// original look.  That aliasing comes from feeding hue values in [0, 360]
// into Fractals.HsvToRgb (which expects [0, 1]) — the integer sector index
// inside HsvToRgb wraps every 1/6 unit of hue, so smooth-coordinate steps
// of 1.33° land in different sectors and produce spotty banding at deep zoom.
//
// This map keeps that exact hue computation as the albedo and applies
// Blinn-Phong lighting on top, so the interference patterns gain 3D relief
// instead of being flattened by gradient interpolation.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// 3D Blinn-Phong relief built on the original RedAndBlack hue formula.
    /// Preserves the rainbow rings and interference banding that the gradient-
    /// based RadioInterferencePhong3D smooths away.
    /// </summary>
    public class RedAndBlackPhong3D : IColorMap
    {
        public static string Name => "RNB3D - Radio Interference Original 3D";

        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;

        public static string Category => "3D Relief";
        public static string Description =>
            "Original Radio Interference hue (with deliberate HSV-overflow banding) lit with warm key + cool fill — rainbow rings and interference spots in 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.HighContrast;

        public int MaxIterations { get; set; } = 1000;

        // Warm golden-white key from upper-left — matches the lighting of the
        // sibling RadioInterferencePhong3D so the two themes sit naturally
        // next to each other in the UI.
        private static readonly LightSource Key = new(
            lx: -0.60f, ly: 0.65f, lz: 0.80f,
            diffR: 0.5f, diffG: 0.92f, diffB: 0.70f,
            specR: 0.5f, specG: 0.98f, specB: 0.85f,
            shininess: 180f);

        // Cool blue-violet fill from the lower-right lifts shadow regions
        // without overpowering the rainbow albedo.
        private static readonly LightSource Fill = new(
            lx: 0.55f, ly: -0.55f, lz: 0.60f,
            diffR: 0.25f, diffG: 0.30f, diffB: 0.90f,
            specR: 0.30f, specG: 0.35f, specB: 0.95f,
            shininess: 28f);

        public int Map(float smooth, float distance, int iterations)
            => LitMap(smooth, iterations, 0f, 0f);

        int IColorMap.Map(float smooth, float distance, int iterations,
                          float nx, float ny)
            => LitMap(smooth, iterations, nx, ny);

        private static int LitMap(float smooth, int iterations,
                                   float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // ── Albedo: original RedAndBlack hue, full value ─────────────────
            // The lighting model handles all dimming — keeping value=1 here
            // preserves the saturated rainbow and the sector-wrap interference
            // aliasing that's the whole point of this theme.
            int packed = Fractals.HsvToRgb(smooth * 8.0f % 360.0f, 0.85f, 1.0f);
            float baseR = ((packed >> 16) & 0xFF) / 255f;
            float baseG = ((packed >> 8) & 0xFF) / 255f;
            float baseB = (packed & 0xFF) / 255f;

            var (Nx, Ny, Nz) = PhongHelper.NormalFromRaw(nx, ny, steepness: 1.3f);

            // Ambient floor — keeps shadows from going fully black so the
            // rainbow stays readable in deep recesses.
            const float ka = 0.14f;
            float r = baseR * ka;
            float g = baseG * ka;
            float b = baseB * ka;

            // Key: full diffuse + bright specular.
            AccumulatePhong(Nx, Ny, Nz, in Key,
                            baseR, baseG, baseB,
                            diffScale: 1.0f, specScale: 0.95f,
                            ref r, ref g, ref b);

            // Fill: softer diffuse + dim specular for cool counter-shading.
            AccumulatePhong(Nx, Ny, Nz, in Fill,
                            baseR, baseG, baseB,
                            diffScale: 0.32f, specScale: 0.20f,
                            ref r, ref g, ref b);

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        // Albedo-tinted diffuse + albedo-independent specular, mirroring the
        // pattern PhongStoneMap uses so the two themes feel consistent.
        private static void AccumulatePhong(
            float Nx, float Ny, float Nz, in LightSource light,
            float baseR, float baseG, float baseB,
            float diffScale, float specScale,
            ref float r, ref float g, ref float b)
        {
            float diff = MathF.Max(0f, Nx * light.Lx + Ny * light.Ly + Nz * light.Lz);
            r += diff * diffScale * light.DiffR * baseR;
            g += diff * diffScale * light.DiffG * baseG;
            b += diff * diffScale * light.DiffB * baseB;

            float hx = light.Lx;
            float hy = light.Ly;
            float hz = light.Lz + 1.0f;
            float hl = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hl < 1e-8f) return;
            hx /= hl; hy /= hl; hz /= hl;

            float spec = MathF.Max(0f, Nx * hx + Ny * hy + Nz * hz);
            spec = MathF.Pow(spec, light.Shininess) * specScale;
            r += spec * light.SpecR;
            g += spec * light.SpecG;
            b += spec * light.SpecB;
        }
    }
}