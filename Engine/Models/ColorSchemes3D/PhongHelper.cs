// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/PhongHelper.cs
//
// Shared Phong illumination utilities used by all 3D colour maps.
//
// The Blinn-Phong model used here
// ──────────────────────────────────────────────────────────────────────────────
//   colour = ambient
//           + kd * diffuse  * max(0, dot(N, L))
//           + ks * specular * max(0, dot(N, H))^shininess
//
//   where:
//     N = surface normal (unit vector, from fractal derivative)
//     L = light direction (unit vector, pointing toward light)
//     H = half vector = normalize(L + V) where V = (0,0,1) viewer direction
//
// Building the 3D normal from the calculator's (nx, ny)
// ──────────────────────────────────────────────────────────────────────────────
//   The calculator outputs a 2D normal (nx, ny) that lies in the tangent
//   plane to the escape potential.  To use it for Phong:
//
//     1.  Set Nz = Steepness  (a positive constant).  A large Steepness
//         makes the surface appear flatter; a small value makes it appear
//         more deeply carved.  Typical range: 0.5 (deep carving) – 3.0 (flat).
//
//     2.  Normalize:  len = sqrt(nx²+ny²+Nz²)
//                     N   = (nx/len, ny/len, Nz/len)
//
//   For in-set pixels (nx == ny == 0) the normal points straight at the
//   viewer (0, 0, 1) which produces a flat, unlit appearance — desirable
//   since the interior is always painted black.
//
// Multiple light sources
// ──────────────────────────────────────────────────────────────────────────────
//   Each LightSource carries its own diffuse colour, specular colour, and
//   direction.  The final illumination is the sum over all sources.
//
// Sign convention for ny
// ──────────────────────────────────────────────────────────────────────────────
//   The complex plane has the imaginary axis pointing upward, while WinForms
//   has y increasing downward.  The normal's ny component is in complex-plane
//   convention.  If a theme looks upside-down, negate ny in NormalFromRaw.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    // ── Data structures ───────────────────────────────────────────────────────

    /// <summary>
    /// A single directional point light for Phong shading.
    /// </summary>
    public readonly struct LightSource
    {
        /// <summary>Normalised light direction (pointing *toward* the light).</summary>
        public readonly float Lx, Ly, Lz;

        /// <summary>Diffuse colour contribution (R, G, B in [0,1]).</summary>
        public readonly float DiffR, DiffG, DiffB;

        /// <summary>Specular colour contribution (R, G, B in [0,1]).</summary>
        public readonly float SpecR, SpecG, SpecB;

        /// <summary>Specular shininess exponent (higher = tighter highlight).</summary>
        public readonly float Shininess;

        public LightSource(float lx, float ly, float lz,
                           float diffR, float diffG, float diffB,
                           float specR, float specG, float specB,
                           float shininess)
        {
            // Normalise the light direction on construction.
            float len = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
            if (len < 1e-6f) len = 1f;
            Lx = lx / len; Ly = ly / len; Lz = lz / len;

            DiffR = diffR; DiffG = diffG; DiffB = diffB;
            SpecR = specR; SpecG = specG; SpecB = specB;
            Shininess = shininess;
        }
    }

    /// <summary>
    /// Phong illumination helper.  Used by all 3D colour map implementations.
    /// </summary>
    public static class PhongHelper
    {
        // ── Normal construction ───────────────────────────────────────────────

        /// <summary>
        /// Builds a normalised 3D surface normal from the calculator's (nx, ny).
        /// </summary>
        /// <param name="nx">X component from NormalXBuffer (range [-1,1]).</param>
        /// <param name="ny">Y component from NormalYBuffer (range [-1,1]).</param>
        /// <param name="steepness">
        /// Z scale factor.  Smaller = deeper carving; larger = flatter surface.
        /// Typical values: 0.8 (dramatic) to 2.5 (subtle).
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (float Nx, float Ny, float Nz) NormalFromRaw(
            float nx, float ny, float steepness = 1.5f)
        {
            // Screen-space y is inverted relative to complex plane y, so negate ny.
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + steepness * steepness);
            if (len < 1e-8f) return (0f, 0f, 1f);
            return (nx / len, ry / len, steepness / len);
        }

        // ── Blinn-Phong per-light evaluation ─────────────────────────────────

        /// <summary>
        /// Evaluates one light source on the surface normal and accumulates the
        /// result into the running (r, g, b) totals.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AccumulateLight(
            float Nx, float Ny, float Nz,
            in LightSource light,
            ref float r, ref float g, ref float b)
        {
            // Diffuse term: Lambert cosine — dot(N, L).
            float diff = Nx * light.Lx + Ny * light.Ly + Nz * light.Lz;
            if (diff <= 0f) return;   // light behind surface — no contribution

            r += diff * light.DiffR;
            g += diff * light.DiffG;
            b += diff * light.DiffB;

            // Blinn-Phong specular: half-vector between L and viewer (0,0,1).
            // H = normalize(L + V) where V = (0,0,1).
            float hx  = light.Lx;
            float hy  = light.Ly;
            float hz  = light.Lz + 1.0f;
            float hlen = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hlen < 1e-8f) return;
            hx /= hlen; hy /= hlen; hz /= hlen;

            float spec = Nx * hx + Ny * hy + Nz * hz;
            if (spec <= 0f) return;
            spec = MathF.Pow(spec, light.Shininess);

            r += spec * light.SpecR;
            g += spec * light.SpecG;
            b += spec * light.SpecB;
        }

        // ── Full Phong evaluation (all lights + ambient) ──────────────────────

        /// <summary>
        /// Computes the final lit colour given a base surface colour, ambient
        /// contribution, and an array of light sources.
        /// </summary>
        /// <param name="baseR,baseG,baseB">Surface albedo in [0,1].</param>
        /// <param name="ambR,ambG,ambB">Ambient light colour (already scaled by ka).</param>
        /// <param name="lights">Array of directional lights to evaluate.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PhongColor(
            float Nx, float Ny, float Nz,
            float baseR, float baseG, float baseB,
            float ambR,  float ambG,  float ambB,
            ReadOnlySpan<LightSource> lights)
        {
            // Start from ambient.
            float r = ambR * baseR;
            float g = ambG * baseG;
            float b = ambB * baseB;

            // Accumulate each light source.
            foreach (ref readonly var light in lights)
                AccumulateLight(Nx, Ny, Nz, in light, ref r, ref g, ref b);

            // Multiply by base colour (diffuse/specular colours are pre-scaled in each light).
            // Already done above for ambient; we also weight diffuse by base.
            // (Separate pass to allow specular highlights to be colour-independent.)
            // Final clamp + pack.
            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        /// <summary>
        /// Simplified helper: one base colour + one light, returns an ARGB int.
        /// Useful for simple single-light 3D maps that don't need multi-light.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SimplePhong(
            float Nx, float Ny, float Nz,
            float baseR, float baseG, float baseB,
            float ambientScale,
            in LightSource light)
        {
            float r = baseR * ambientScale;
            float g = baseG * ambientScale;
            float b = baseB * ambientScale;

            // Diffuse.
            float diff = MathF.Max(0f, Nx * light.Lx + Ny * light.Ly + Nz * light.Lz);
            r += diff * light.DiffR * baseR;
            g += diff * light.DiffG * baseG;
            b += diff * light.DiffB * baseB;

            // Specular (highlight colour independent of base albedo).
            float hx  = light.Lx;
            float hy  = light.Ly;
            float hz  = light.Lz + 1.0f;
            float hlen = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            if (hlen > 1e-8f)
            {
                hx /= hlen; hy /= hlen; hz /= hlen;
                float spec = MathF.Max(0f, Nx * hx + Ny * hy + Nz * hz);
                spec = MathF.Pow(spec, light.Shininess);
                r += spec * light.SpecR;
                g += spec * light.SpecG;
                b += spec * light.SpecB;
            }

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
