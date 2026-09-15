// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// NewtonBasinColoring.cs
//
// Shared coloring + histogram-equalization core for the root-finding basin
// families (Newton / Halley / Secant — #146, slice 2 of the HE roadmap #144).
//
// These fractals do NOT color by smooth escape-time through ColorMap.Map, so
// they cannot reuse HistogramEqualizer / EscapeTimeColorState. A converged
// pixel's color is a pure function of (basin, iter, maxIter, zr, zi), dispatched
// either through an INewtonColorMap theme (MapNewton) or a built-in HSV fallback
// when the active ColorMap is not Newton-aware. ConvergedColor centralizes that
// dispatch so the three calculators' write-sites stay byte-identical and the
// equalizer can reproduce them exactly.
//
// HE design (resolves the #146 design questions):
//   * The scalar is the per-pixel convergence step count (iterations to reach a
//     root). Non-converged "interior" pixels (basin < 0) are excluded from the
//     histogram and never recolored — HE only touches the escaped equivalent.
//   * Equalization is GLOBAL (one CDF across all basins). Because basin index
//     drives HUE and iter drives only SHADE/GRADIENT, remapping iter through the
//     CDF redistributes the shading term WITHOUT touching which-root hue — so HE
//     does not fight basin separation (the issue's central worry). Per-basin
//     CDFs are a possible future knob; global is the low-risk slice-1 choice.
//   * strength blends raw↔equalized iter (0 = byte-identical to Calculate, an
//     idempotent recolor; 1 = full equalization). Themes that ignore iter
//     (categorical basin-only) are unaffected — correct.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;

namespace FracturingFog;

/// <summary>
/// Shared basin coloring dispatch + histogram-equalization core for the
/// Newton / Halley / Secant calculators. See file header for the HE design.
/// </summary>
internal static class NewtonBasinColoring
{
    /// <summary>
    /// Color for a CONVERGED pixel (basin &gt;= 0). Reproduces the exact dispatch
    /// used inline by NewtonCalculator / HalleyCalculator / SecantCalculator:
    /// an <see cref="INewtonColorMap"/> theme when present, else the built-in
    /// HSV "hue per basin, shade fades with iteration count" fallback. Callers
    /// handle non-converged (basin &lt; 0) interior pixels themselves (interior
    /// alpha), so this method is only defined for basin &gt;= 0.
    /// </summary>
    public static uint ConvergedColor(
        INewtonColorMap? map, int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
    {
        if (map != null)
            return unchecked((uint)map.MapNewton(basin, totalBasins, iter, maxIter, zr, zi));

        // Built-in fallback — identical to the calculators' inline else-branch.
        float hue = (float)basin / totalBasins;
        float shade = 1.0f - Math.Min(iter / (float)maxIter, 0.9f);
        return unchecked((uint)HsvToArgb(hue, 1.0f, shade));
    }

    /// <summary>
    /// Builds a rank-order CDF over the convergence-step distribution of the
    /// converged pixels (basin &gt;= 0), binned on <c>iter / maxIter</c>. Returns
    /// false (identity case) when the view has no converged pixels — e.g. a
    /// region that is entirely non-convergent.
    /// </summary>
    public static bool BuildCdf(
        int count, int maxIter, int[] basin, float[] iter, out double[]? cdf, out int bins)
    {
        cdf = null;
        bins = 0;
        if (count <= 0 || maxIter <= 0) return false;

        bins = Math.Min(2048, Math.Max(256, maxIter));
        int[] hist = new int[bins];
        int total = 0;
        float invMax = 1.0f / maxIter;

        for (int i = 0; i < count; i++)
        {
            if (basin[i] < 0) continue;              // non-converged interior — excluded
            float t = iter[i] * invMax;
            if (t < 0f) t = 0f; else if (t > 0.9999999f) t = 0.9999999f;
            int b = (int)(t * bins);
            hist[b]++;
            total++;
        }

        if (total == 0) { bins = 0; return false; }

        cdf = new double[bins];
        long cum = 0;
        double invTotal = 1.0 / total;
        for (int i = 0; i < bins; i++)
        {
            cum += hist[i];
            cdf[i] = cum * invTotal;
        }
        return true;
    }

    /// <summary>
    /// Applies a previously-built CDF, recoloring every converged pixel from its
    /// equalized convergence count. Non-converged pixels (basin &lt; 0) are left
    /// untouched so their interior-alpha stamp survives. Always recolors (no
    /// strength-0 short-circuit): at <paramref name="strength"/> 0 the effective
    /// iter equals the raw iter, so the recolor is byte-identical to Calculate —
    /// which is what lets the live Adaptive slider return to the plain mapping.
    ///
    /// <paramref name="ditherIterStrength"/> adds a stable per-pixel spatial
    /// dither (in iteration units, a pure function of x/y) to blur the band edges
    /// MapNewton's integer <c>iter</c> contract would otherwise quantize.
    /// <paramref name="convergedCount"/> / <paramref name="saturatedCount"/> let
    /// the video path detect a locked CDF that has drifted out of range.
    /// </summary>
    public static void ApplyWithCdf(
        uint[] color, int width, int height,
        int[] basin, float[] iter, float[] finalZr, float[] finalZi,
        int totalBasins, int maxIter, INewtonColorMap? map,
        double[] cdf, int bins, int sourceMaxIter,
        double strength, double ditherIterStrength,
        out long convergedCount, out long saturatedCount)
    {
        convergedCount = 0;
        saturatedCount = 0;
        if (cdf == null || bins <= 0 || width <= 0 || height <= 0) return;
        if (sourceMaxIter <= 0) sourceMaxIter = maxIter;

        double s = strength < 0.0 ? 0.0 : (strength > 1.0 ? 1.0 : strength);
        float invSrc = 1.0f / sourceMaxIter;
        long conv = 0, sat = 0;

        Parallel.For(0, height, y =>
        {
            int rowBase = y * width;
            long localConv = 0, localSat = 0;
            for (int x = 0; x < width; x++)
            {
                int i = rowBase + x;
                int bsn = basin[i];
                if (bsn < 0) continue;               // leave interior pixels as-is
                localConv++;

                float rawIter = iter[i];
                float t = rawIter * invSrc;
                if (t < 0f) t = 0f;
                else if (t > 0.9999999f) { t = 0.9999999f; localSat++; }
                int b = (int)(t * bins);
                if (b >= bins) b = bins - 1;

                double eqIter = cdf[b] * maxIter;                 // CDF ∈ [0,1] → iter units
                double effF = (1.0 - s) * rawIter + s * eqIter;
                if (ditherIterStrength > 0.0)
                    effF += HistogramEqualizer.SpatialDither(x, y) * ditherIterStrength;

                int effIter = (int)Math.Round(effF);
                if (effIter < 0) effIter = 0;
                else if (effIter > maxIter) effIter = maxIter;

                color[i] = ConvergedColor(map, bsn, totalBasins, effIter, maxIter, finalZr[i], finalZi[i]);
            }
            Interlocked.Add(ref conv, localConv);
            Interlocked.Add(ref sat, localSat);
        });

        convergedCount = conv;
        saturatedCount = sat;
    }

    /// <summary>
    /// HSV → 0xAARRGGBB (opaque). Shared by the built-in basin fallback across
    /// Newton / Halley / Secant (previously duplicated per calculator).
    /// </summary>
    public static int HsvToArgb(float h, float s, float v)
    {
        h = h * 6f;
        int i = (int)Math.Floor(h);
        float f = h - i;
        float p = v * (1 - s);
        float q = v * (1 - s * f);
        float t = v * (1 - s * (1 - f));
        float rF, gF, bF;
        switch (i % 6)
        {
            case 0: rF = v; gF = t; bF = p; break;
            case 1: rF = q; gF = v; bF = p; break;
            case 2: rF = p; gF = v; bF = t; break;
            case 3: rF = p; gF = q; bF = v; break;
            case 4: rF = t; gF = p; bF = v; break;
            case 5: rF = v; gF = p; bF = q; break;
            default: rF = gF = bF = 0; break;
        }
        int r = (int)(rF * 255);
        int g = (int)(gF * 255);
        int b = (int)(bF * 255);
        return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
    }
}
