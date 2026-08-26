// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#518 — height-field detail shaping for the Relief 3D pre-pass. Two pure,
/// in-place operators that raise the fractal filament structure RELATIVE to the base
/// "slab" it sits on, where the global Height scale (a uniform multiplier) cannot:
///   • <see cref="Unsharp"/> — local high-pass gain: push each cell away from a
///     blurred base by a gain, so filament ridges grow/sharpen without lifting the
///     slab.
///   • <see cref="Gamma"/>   — top-end contrast on the normalised height, so the
///     high filament band separates from the base.
/// Both are identity at their default (gain 1 / gamma 1) → byte-identical. Run in
/// <c>BuildPrepass</c> so they apply to the CPU trace and the GPU relief kernel alike
/// (both consume the same compressed field).</summary>
public static class ReliefHeightDetail
{
    /// <summary>Auto blur radius (cells) for the detail base when the caller passes
    /// <paramref name="radius"/> ≤ 0: ~1.5% of the field short axis, min 1.</summary>
    public static int AutoRadius(int w, int h)
        => Math.Max(1, (int)Math.Round(0.015 * Math.Min(w, h)));

    /// <summary>Local high-pass detail gain (unsharp mask). Blurs a copy of
    /// <paramref name="field"/> (separable box blur, <paramref name="radius"/> cells;
    /// ≤ 0 → <see cref="AutoRadius"/>) to get the low-frequency base, then sets
    /// <c>h' = max(0, base + gain·(h − base))</c>. <paramref name="gain"/> ≤ 1 with the
    /// default 1.0 is an exact no-op (the array is untouched). Clamped to ≥ 0 so the
    /// base plane never goes negative.</summary>
    public static void Unsharp(float[] field, int w, int h, double gain, int radius)
    {
        if (field == null || w <= 0 || h <= 0 || field.Length < w * h) return;
        if (gain == 1.0) return;                 // identity — byte-identical
        if (gain < 0.0) gain = 0.0;
        int r = radius > 0 ? radius : AutoRadius(w, h);

        var blur = new float[w * h];
        BoxBlur(field, blur, w, h, r);
        for (int i = 0; i < w * h; i++)
        {
            double bse = blur[i];
            double hv = bse + gain * (field[i] - bse);
            field[i] = hv > 0.0 ? (float)hv : 0f;
        }
    }

    /// <summary>Top-end contrast via a power curve on the normalised height:
    /// <c>h' = hmax · (h / hmax)^gamma</c>. <paramref name="gamma"/> &gt; 1 expands the
    /// high (filament) band and compresses the base; &lt; 1 does the reverse. Default
    /// 1.0 (and any gamma ≤ 0) is an exact no-op. The peak (hmax) is preserved.</summary>
    public static void Gamma(float[] field, int w, int h, double gamma)
    {
        if (field == null || w <= 0 || h <= 0 || field.Length < w * h) return;
        if (gamma == 1.0 || gamma <= 0.0) return;   // identity

        float hmax = 0f;
        int n = w * h;
        for (int i = 0; i < n; i++) { float v = field[i]; if (v > hmax) hmax = v; }
        if (hmax <= 1e-9f) return;
        double inv = 1.0 / hmax;
        for (int i = 0; i < n; i++)
        {
            float v = field[i];
            if (v <= 0f) continue;
            field[i] = (float)(hmax * Math.Pow(v * inv, gamma));
        }
    }

    // Separable box blur (radius r each axis) via a running sum, edge-clamped. Two
    // scratch passes; leaves `dst` = blurred `src`.
    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r)
    {
        var tmp = new float[w * h];
        double norm = 1.0 / (2 * r + 1);

        // Horizontal.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double sum = src[row] * (r + 1);
            for (int x = 1; x <= r; x++) sum += src[row + Math.Min(x, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (float)(sum * norm);
                int add = Math.Min(x + r + 1, w - 1);
                int sub = Math.Max(x - r, 0);
                sum += src[row + add] - src[row + sub];
            }
        }

        // Vertical.
        for (int x = 0; x < w; x++)
        {
            double sum = tmp[x] * (r + 1);
            for (int y = 1; y <= r; y++) sum += tmp[Math.Min(y, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = (float)(sum * norm);
                int add = Math.Min(y + r + 1, h - 1);
                int sub = Math.Max(y - r, 0);
                sum += tmp[add * w + x] - tmp[sub * w + x];
            }
        }
    }
}
