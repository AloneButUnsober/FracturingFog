// Models/ColorSchemes/PolarNight3D.cs
//
// CORRECTED version — two bugs fixed from the original generation:
//
// BUG 1 — Wrong interface method routing (cause of flat image with no 3D)
// ────────────────────────────────────────────────────────────────────────
// The 5-parameter Map was declared as a plain class method:
//     public int Map(float smooth, float distance, int iterations, float nx, float ny)
//
// The calculator calls through the IColorMap interface:
//     colorMap.Map(smooth, distance, iter, nx, ny)   // typed as IColorMap
//
// C# default interface methods work like this: when the caller holds an
// IColorMap reference and calls the 5-param overload, it resolves to the
// DEFAULT INTERFACE IMPLEMENTATION (which just calls the 3-param version
// with the real nx/ny discarded).  The class-level 5-param method is
// invisible to the interface call unless it is declared as an EXPLICIT
// INTERFACE IMPLEMENTATION.
//
// The 3-param override then called Map(s,d,i, 0f, 0f), so every pixel was
// computed with nx=0, ny=0 → normal always (0,0,1) → constant diffuse for
// every pixel → no 3D shading variation at all.
//
// Fix: declare the 5-param method as an explicit interface implementation:
//     int IColorMap.Map(float smooth, float distance, int iterations, float nx, float ny)
// This makes the interface call route directly to our Phong implementation.
//
// BUG 2 — gradient t mapping produced wrong colour distribution
// ────────────────────────────────────────────────────────────────────────
// The original used t = smooth / maxIterations (GradientColorMap's linear
// mapping).  At low zoom most pixels have small smooth values, keeping t
// near 0 and producing near-black navy across most of the image.
//
// The flat PolarNight is a CyclingGradientColorMap with CycleSpeed=0.02,
// which uses t = (smooth * 0.02) % 1.0 — cycling the full gradient
// repeatedly regardless of maxIterations.  Using the same formula here
// makes the 3D version's colour distribution match the flat original.

using FracturingFog.Interefaces;

using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Arctic polar night with Phong 3D relief shading.
    /// Cold blue-white key light from upper-right; faint warm amber fill from
    /// lower-left.  Colour distribution matches the flat PolarNight theme.
    /// </summary>
    public class PolarNight3DMap : GradientColorMap, IColorMap
    {
        public static string Name => "Polar Night 3D";
        public static string Category => "3D Relief";
        public static string Description => "PolarNight gradient as a 3D Phong relief — cold key light, warm amber fill.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth |
            ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased |
            ColorMapFeatures.ThreeDEffect;

        // ── Gradient stops — identical to the flat PolarNight theme ───────────

        public PolarNight3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(2, 4, 15)));  // near-black navy
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(8, 20, 55)));  // midnight blue
            Stops.Add(new ColorStop(0.28f, Color.FromArgb(25, 40, 100)));  // deep blue
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(50, 60, 140)));  // blue-violet
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(80, 90, 170)));  // periwinkle
            Stops.Add(new ColorStop(0.74f, Color.FromArgb(130, 160, 210)));  // dusty blue
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(190, 220, 240)));  // pale aqua
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(225, 245, 255)));  // icy white-blue
        }

        // ── Light setup ───────────────────────────────────────────────────────

        // Cold blue-white key light: upper-right, ~40° above horizontal.
        private static readonly LightSource KeyLight = new LightSource(
            lx: 0.60f, ly: 0.45f, lz: 0.80f,
            diffR: 0.70f, diffG: 0.80f, diffB: 1.00f,
            specR: 0.80f, specG: 0.90f, specB: 1.00f,
            shininess: 55f);

        // Warm amber fill light: lower-left, dim.
        private static readonly LightSource FillLight = new LightSource(
            lx: -0.70f, ly: -0.40f, lz: 0.45f,
            diffR: 0.55f, diffG: 0.35f, diffB: 0.10f,
            specR: 0.30f, specG: 0.20f, specB: 0.05f,
            shininess: 12f);

        // Cycle speed matching the flat PolarNight (CyclingGradientColorMap default = 0.02).
        private const float CycleSpeed = 0.02f;

        // ── 3-param Map: fallback for callers that don't pass normals ─────────
        public override int Map(float smooth, float distance, int maxIterations)
            => LitMap(smooth, distance, maxIterations, 0f, 0f);

        // ── 5-param Map: EXPLICIT INTERFACE IMPLEMENTATION ────────────────────
        // Must be declared as 'int IColorMap.Map(...)' so that the calculator's
        // interface-typed call (colorMap.Map(s,d,i,nx,ny)) routes here directly,
        // bypassing the default interface implementation and receiving the real
        // nx/ny values from the normal buffers.
        int IColorMap.Map(float smooth, float distance, int maxIterations,
                          float nx, float ny)
            => LitMap(smooth, distance, maxIterations, nx, ny);

        // ── Core Phong implementation ─────────────────────────────────────────

        private int LitMap(float smooth, float distance, int maxIterations,
                           float nx, float ny)
        {
            // In-set pixels are always black.
            if (smooth >= maxIterations)
                return unchecked((int)0xFF000000);

            // ── Gradient albedo ───────────────────────────────────────────────
            // Cycling t matches the flat PolarNight colour distribution.
            // The gradient repeats every 1/CycleSpeed = 50 smooth-units,
            // ensuring all colour stops appear throughout the image.
            float t = (smooth * CycleSpeed) % 1.0f;
            int albedoI = MapNormalized(t, distance);

            float aR = ((albedoI >> 16) & 0xFF) / 255f;
            float aG = ((albedoI >> 8) & 0xFF) / 255f;
            float aB = (albedoI & 0xFF) / 255f;

            // ── Build the 3D surface normal ───────────────────────────────────
            // nx, ny come from the calculator's NormalXBuffer / NormalYBuffer.
            // ny is negated: complex-plane y points up, screen y points down.
            //
            // Steepness controls 3D depth drama:
            //   0.9  = deep ice-cliff carving (very dramatic shadows)
            //   1.6  = balanced depth (default)
            //   2.5  = gentle emboss (subtler, closer to flat original)
            const float Steepness = 1.6f;

            float ry = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else { Nx = 0f; Ny = 0f; Nz = 1f; }

            // ── Ambient ───────────────────────────────────────────────────────
            // Low ambient (0.12) keeps shadowed recesses dark, preserving the
            // moody polar-night atmosphere.  Raise to 0.25 to lift shadows.
            const float Ka = 0.12f;
            float r = aR * Ka;
            float g = aG * Ka;
            float b = aB * Ka;

            // ── Key light (cold blue-white) ───────────────────────────────────

            float diffKey = MathF.Max(0f,
                Nx * KeyLight.Lx + Ny * KeyLight.Ly + Nz * KeyLight.Lz);

            r += diffKey * KeyLight.DiffR * aR;
            g += diffKey * KeyLight.DiffG * aG;
            b += diffKey * KeyLight.DiffB * aB;

            // Blinn-Phong specular: H = normalize(L + V) where V = (0,0,1).
            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1.0f;
            float hkl = MathF.Sqrt(hkx * hkx + hky * hky + hkz * hkz);
            if (hkl > 1e-8f)
            {
                hkx /= hkl; hky /= hkl; hkz /= hkl;
                float spec = MathF.Pow(
                    MathF.Max(0f, Nx * hkx + Ny * hky + Nz * hkz),
                    KeyLight.Shininess) * 0.85f;
                r += spec * KeyLight.SpecR;
                g += spec * KeyLight.SpecG;
                b += spec * KeyLight.SpecB;
            }

            // ── Fill light (warm amber, dim) ──────────────────────────────────

            float diffFill = MathF.Max(0f,
                Nx * FillLight.Lx + Ny * FillLight.Ly + Nz * FillLight.Lz);

            r += diffFill * FillLight.DiffR * aR * 0.35f;
            g += diffFill * FillLight.DiffG * aG * 0.35f;
            b += diffFill * FillLight.DiffB * aB * 0.35f;

            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1.0f;
            float hfl = MathF.Sqrt(hfx * hfx + hfy * hfy + hfz * hfz);
            if (hfl > 1e-8f)
            {
                hfx /= hfl; hfy /= hfl; hfz /= hfl;
                float spec = MathF.Pow(
                    MathF.Max(0f, Nx * hfx + Ny * hfy + Nz * hfz),
                    FillLight.Shininess) * 0.25f;
                r += spec * FillLight.SpecR;
                g += spec * FillLight.SpecG;
                b += spec * FillLight.SpecB;
            }

            // ── Clamp and pack ────────────────────────────────────────────────

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}