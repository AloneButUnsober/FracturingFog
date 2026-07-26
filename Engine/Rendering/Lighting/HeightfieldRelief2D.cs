// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/HeightfieldRelief2D.cs
//
// #102 Phase 1 — real raised relief + cast shadows for escape-time 2D fractals.
//
// The per-pixel Phong "3D" themes only emboss the escape-potential slope; the
// surface has no height, so it can't cast shadows or read as raised. This
// post-pass treats the smooth iteration count as a HEIGHT FIELD and modulates
// the already-themed colour buffer by:
//
//   1. Hillshade  — normal from the height gradient, Lambert against the light.
//   2. Cast shadow — a horizon walk toward the light across the height field
//      (sub-pixel DDA), tracking the max elevation angle; occluded ⇒ shadowed.
//
// It multiplies the existing colour (so any theme keeps its palette; relief is
// pure lighting on top). Interior / in-set pixels (height <= 0) are the base
// plane: left unlit but usable as shadow receivers. Prototyped headless in
// Diagnostics/HeightfieldReliefProbe; this is the productionised, colour-
// modulating, buffer-driven version. See Docs/Technical/Heightfield-Relief-Spike.md.

using System;
using System.Threading.Tasks;

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

public static class HeightfieldRelief2D
{
    /// <summary>
    /// Applies heightfield relief lighting, reading <paramref name="src"/> +
    /// <paramref name="height"/> and writing lit colour to <paramref name="dst"/>.
    /// <paramref name="src"/> and <paramref name="dst"/> may be the same array.
    /// No-op copy when disabled or the buffers don't line up.
    /// </summary>
    /// <param name="src">Flat themed ARGB colour, length ≥ w*h.</param>
    /// <param name="dst">Destination ARGB colour, length ≥ w*h.</param>
    /// <param name="height">Height field (smooth iteration count); in-set = 0.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <param name="p">Relief parameters (from <see cref="FractalParameters"/>).</param>
    public static void Apply(uint[] src, uint[] dst, float[] height, int w, int h,
                             FractalParameters p)
    {
        int n = w * h;
        if (w <= 2 || h <= 2 || src.Length < n || dst.Length < n || height.Length < n)
        {
            if (!ReferenceEquals(src, dst)) Array.Copy(src, dst, n);
            return;
        }

        double strength = Math.Clamp(p.Relief2DStrength, 0.0, 1.0);
        if (!p.Relief2DEnabled || strength <= 0.0)
        {
            if (!ReferenceEquals(src, dst)) Array.Copy(src, dst, n);
            return;
        }

        // Normalise the height field to world units via the height-scale knob.
        float maxH = 0f;
        for (int i = 0; i < n; i++) { float hv = height[i]; if (hv > maxH) maxH = hv; }
        double heightScale = Math.Max(0.0, p.Relief2DHeightScale);
        double invMax = maxH > 1e-9f ? heightScale / maxH : 0.0;

        // Precompute world-unit heights once (shared by hillshade + shadow walk).
        // pxScale: one screen pixel = 1 world-unit-of-travel so the light slope
        // and the gradient share a consistent horizontal metric.
        double az = p.Relief2DLightAzimuthDeg * Math.PI / 180.0;
        double el = Math.Clamp(p.Relief2DLightElevationDeg, 1.0, 89.0) * Math.PI / 180.0;
        double lx = Math.Cos(az), ly = Math.Sin(az);
        double lightSlope = Math.Tan(el);                 // rise per unit horizontal
        double lvx = lx * Math.Cos(el), lvy = ly * Math.Cos(el), lvz = Math.Sin(el);
        double shadowStrength = Math.Clamp(p.Relief2DShadowStrength, 0.0, 1.0);

        float[] wh = s_heightScratch is { } sh && sh.Length >= n
            ? sh : (s_heightScratch = new float[n]);
        for (int i = 0; i < n; i++) wh[i] = (float)(height[i] * invMax);

        int maxShadowSteps = Math.Max(w, h);
        const double ambient = 0.18;

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                uint c = src[idx];
                float srcH = wh[idx];
                if (srcH <= 0f) { dst[idx] = c; continue; }   // interior base plane

                // Hillshade — central-difference gradient → surface normal.
                float hL = wh[idx - (x > 0 ? 1 : 0)];
                float hR = wh[idx + (x < w - 1 ? 1 : 0)];
                float hD = wh[idx - (y > 0 ? w : 0)];
                float hU = wh[idx + (y < h - 1 ? w : 0)];
                double dhx = (hR - hL) * 0.5;
                double dhy = (hU - hD) * 0.5;
                double nlen = Math.Sqrt(dhx * dhx + dhy * dhy + 1.0);
                double nx = -dhx / nlen, ny = -dhy / nlen, nz = 1.0 / nlen;
                double lambert = Math.Max(0.0, nx * lvx + ny * lvy + nz * lvz);

                // Cast shadow — sub-pixel horizon walk toward the light.
                double shadow = 1.0;
                if (shadowStrength > 0.0)
                {
                    double px = x, py = y;
                    for (int s = 1; s <= maxShadowSteps; s++)
                    {
                        px += lx; py += ly;
                        int sx = (int)(px + 0.5), sy = (int)(py + 0.5);
                        if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) break;
                        float occ = wh[sy * w + sx];
                        if (occ <= 0f) continue;
                        double rayHeight = srcH + lightSlope * s;
                        if (occ > rayHeight) { shadow = 1.0 - shadowStrength; break; }
                    }
                }

                double lit = (ambient + (1.0 - ambient) * lambert) * shadow;
                // Blend relief lighting against the flat colour by strength.
                double m = (1.0 - strength) + strength * lit;
                int r = (int)((c >> 16) & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)(c & 0xFF);
                r = (int)(r * m); g = (int)(g * m); b = (int)(b * m);
                if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
                dst[idx] = (c & 0xFF000000u) | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
        });
    }

    // Reused across frames on the render thread that calls Apply (serialised by
    // the host upload gate). The Parallel.For inside only reads it.
    private static float[]? s_heightScratch;
}
