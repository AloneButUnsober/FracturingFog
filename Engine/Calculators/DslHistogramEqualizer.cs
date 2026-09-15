// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslHistogramEqualizer.cs
//
// Histogram-equalization recolor for the escape-time DSL calculators
// (UserEquationCalculator, SandboxCalculator — #845, slice 3 of the HE roadmap
// #144). The CDF math is identical to the shared escape-time core
// (HistogramEqualizer.BuildCdf is reused verbatim), but the RECOLOR is not:
//
//   * These calculators color more pixel classes than the clean two-class
//     escape-time model (EscapeTimeCalculator) the shared HistogramEqualizer.
//     ApplyWithCdf assumes. A DSL frame can carry, besides plain smooth-escaped
//     pixels: in-set pixels (interior-alpha scaled), #544 converged pixels
//     (colored by raw convergence speed), #615 out-of-bounds surround, and
//     #583 interior-orbit pixels. The shared apply would overwrite the
//     non-escaped classes with InSetColor. So HE here recolors ONLY the plain
//     smooth-escaped pixels (IterationBuffer[idx] < maxIter) and leaves every
//     other pixel exactly as Calculate wrote it.
//
//   * The caller marks every non-plain-escaped pixel with IterationBuffer =
//     maxIter, so this apply skips them and the reused BuildCdf excludes them
//     from the histogram.
//
//   * HE is meaningful only in the smooth/escape color regime. Under an
//     orbit-trap theme (ColorMap is IOrbitAwareColorMap) the escaped pixels are
//     colored through MapWithOrbit, which the CDF remap cannot reproduce, so
//     the caller makes HE inert there (BuildHistogramCdf returns false) rather
//     than recolor them wrong. That gate lives in the calculators.
//
// strength 0 reproduces the plain linear mapping (an idempotent recolor from
// the smooth buffer), matching the shared core's convention; strength 1 is full
// equalization.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;

namespace FracturingFog;

/// <summary>
/// Escaped-pixel-only histogram-equalization recolor for the DSL escape-time
/// calculators. Pairs with <see cref="HistogramEqualizer.BuildCdf"/> for the
/// CDF. See the file header for why the shared apply cannot be reused directly.
/// </summary>
internal static class DslHistogramEqualizer
{
    /// <summary>
    /// Recolors every plain smooth-escaped pixel (<paramref name="iterBuf"/>
    /// &lt; <paramref name="maxIter"/>) from its equalized smooth value through
    /// the nine-parameter <c>ColorMap.Map</c> overload — the same call the DSL
    /// calculators make for escaped pixels (the three final-Z / derivative
    /// buffers are optional; pass null when the calculator only feeds the
    /// five-parameter overload, and they read as 0). Non-escaped pixels are left
    /// untouched, preserving in-set alpha, converged / OOB / interior-orbit
    /// coloring. <paramref name="escapedCount"/> / <paramref name="saturatedCount"/>
    /// let the video path detect a locked CDF that has drifted out of range.
    /// </summary>
    public static void ApplyWithCdf(
        uint[] color, int width, int height, int maxIter,
        IColorMap colorMap,
        int[] iterBuf, float[] smoothBuf, float[] nxBuf, float[] nyBuf,
        float[]? zrBuf, float[]? ziBuf, float[]? drBuf, float[]? diBuf,
        double[] cdf, int bins, int sourceMaxIter,
        double strength, double ditherIterStrength,
        out long escapedCount, out long saturatedCount)
    {
        escapedCount = 0;
        saturatedCount = 0;
        if (cdf == null || bins <= 0 || width <= 0 || height <= 0) return;
        if (maxIter <= 0) return;
        if (sourceMaxIter <= 0) sourceMaxIter = maxIter;
        if (strength < 0.0) strength = 0.0; else if (strength > 1.0) strength = 1.0;
        if (ditherIterStrength < 0.0) ditherIterStrength = 0.0;

        colorMap.MaxIterations = maxIter;

        float invMax = 1.0f / maxIter;             // current-frame linear position
        float invMaxSrc = 1.0f / sourceMaxIter;    // CDF bin lookup (locked source)
        int lastBin = bins - 1;
        float ditherIter = (float)ditherIterStrength;

        long[] rowEscaped = new long[height];
        long[] rowSaturated = new long[height];

        Parallel.For(0, height, y =>
        {
            int rowBase = y * width;
            long esc = 0, sat = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                if (iterBuf[idx] >= maxIter) continue;   // non-escaped: leave as Calculate wrote it
                esc++;

                float s = smoothBuf[idx];
                float tLin = s * invMax;
                float tLinC = tLin < 0f ? 0f : (tLin > 0.9999999f ? 0.9999999f : tLin);

                float tLookupRaw = s * invMaxSrc;
                float tLookup = tLookupRaw < 0f ? 0f : (tLookupRaw > 0.9999999f ? 0.9999999f : tLookupRaw);
                int b = (int)(tLookup * bins);
                if (b > lastBin) b = lastBin;
                if (tLookupRaw >= 0.9999999f) sat++;

                double tEq = cdf[b];
                double tBlend = tLinC + (tEq - tLinC) * strength;
                float smoothEq = (float)(tBlend * maxIter);
                if (ditherIter > 0f)
                    smoothEq += HistogramEqualizer.SpatialDither(x, y) * ditherIter;

                float zr = zrBuf != null ? zrBuf[idx] : 0f;
                float zi = ziBuf != null ? ziBuf[idx] : 0f;
                float dr = drBuf != null ? drBuf[idx] : 0f;
                float di = diBuf != null ? diBuf[idx] : 0f;

                color[idx] = unchecked((uint)colorMap.Map(
                    smoothEq, 0f, maxIter, nxBuf[idx], nyBuf[idx], zr, zi, dr, di));
            }
            rowEscaped[y] = esc;
            rowSaturated[y] = sat;
        });

        long totalEsc = 0, totalSat = 0;
        for (int i = 0; i < height; i++) { totalEsc += rowEscaped[i]; totalSat += rowSaturated[i]; }
        escapedCount = totalEsc;
        saturatedCount = totalSat;
    }
}
