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
// Lighting is RELATIVE: a flat surface reads as neutral (no change) so the
// smooth exterior potential is left alone instead of being globally darkened;
// only real slopes shade (toward-light brighten, away darken), ridges catch a
// specular highlight, and occluders cast shadows. The result modulates the
// existing colour so any theme keeps its palette. Interior / in-set pixels
// (height <= 0) are the base plane: left unlit but usable as shadow receivers.
// Prototyped headless in
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

        // RELATIVE relief — a flat surface (normal (0,0,1)) reads as NEUTRAL
        // (multiplier 1, no highlight) so the smooth exterior potential is left
        // alone instead of being globally darkened. Only actual slopes shade:
        // faces toward the light brighten, faces away darken, ridges catch a
        // specular highlight, and occluders cast real shadows. This is the fix
        // for the "whole image just gets darker" behaviour of the first cut.
        //
        // Blinn-Phong half-vector against viewer V = (0,0,1). Baselines are the
        // flat-normal responses, subtracted per pixel so flats net to zero.
        double hx = lvx, hy = lvy, hz = lvz + 1.0;
        double hlen = Math.Sqrt(hx * hx + hy * hy + hz * hz);
        if (hlen < 1e-9) hlen = 1.0;
        hx /= hlen; hy /= hlen; hz /= hlen;
        const double shininess = 14.0;   // specular tightness
        const double gain = 2.6;         // diffuse slope amplification
        const double specStrength = 0.7; // additive highlight weight
        double flatLambert = lvz;                                   // (0,0,1)·L
        double flatSpec = hz > 0 ? Math.Pow(hz, shininess) : 0.0;   // (0,0,1)·H

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                uint c = src[idx];
                float srcH = wh[idx];
                if (srcH <= 0f) { dst[idx] = c; continue; }   // interior base plane

                // Surface normal from the central-difference height gradient.
                float hLv = wh[idx - (x > 0 ? 1 : 0)];
                float hRv = wh[idx + (x < w - 1 ? 1 : 0)];
                float hDv = wh[idx - (y > 0 ? w : 0)];
                float hUv = wh[idx + (y < h - 1 ? w : 0)];
                double dhx = (hRv - hLv) * 0.5;
                double dhy = (hUv - hDv) * 0.5;
                double nlen = Math.Sqrt(dhx * dhx + dhy * dhy + 1.0);
                double nx = -dhx / nlen, ny = -dhy / nlen, nz = 1.0 / nlen;

                // Diffuse RELATIVE to a flat surface — 0 on flats, ± on slopes.
                double diffuseDelta = (nx * lvx + ny * lvy + nz * lvz) - flatLambert;

                // Specular highlight, also relative so flats add nothing.
                double ndoth = nx * hx + ny * hy + nz * hz;
                double spec = ndoth > 0 ? Math.Pow(ndoth, shininess) : 0.0;
                double specDelta = spec - flatSpec;
                if (specDelta < 0.0) specDelta = 0.0;

                // Cast shadow — sub-pixel horizon walk toward the light.
                bool inShadow = false;
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
                        if (occ > rayHeight) { inShadow = true; break; }
                    }
                }

                double shadowMul = inShadow ? (1.0 - shadowStrength) : 1.0;
                double relMul = (1.0 + gain * diffuseDelta) * shadowMul;
                if (relMul < 0.15) relMul = 0.15;         // floor — never pure black
                double addSpec = inShadow ? 0.0 : specStrength * specDelta;

                // Blend deviation-from-neutral by overall strength.
                double finalMul = 1.0 + strength * (relMul - 1.0);
                double specAdd = 255.0 * strength * addSpec;

                double r = ((c >> 16) & 0xFF) * finalMul + specAdd;
                double g = ((c >> 8) & 0xFF) * finalMul + specAdd;
                double b = (c & 0xFF) * finalMul + specAdd;
                int ri = r > 255.0 ? 255 : (r < 0.0 ? 0 : (int)r);
                int gi = g > 255.0 ? 255 : (g < 0.0 ? 0 : (int)g);
                int bi = b > 255.0 ? 255 : (b < 0.0 ? 0 : (int)b);
                dst[idx] = (c & 0xFF000000u) | (uint)(ri << 16) | (uint)(gi << 8) | (uint)bi;
            }
        });
    }

    // Reused across frames on the render thread that calls Apply (serialised by
    // the host upload gate). The Parallel.For inside only reads it.
    private static float[]? s_heightScratch;
}
