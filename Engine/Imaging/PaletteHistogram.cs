// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteHistogram.cs
//
// Roadmap slice S10.3 (PaletteBuilder-Design.md, #392) — FRACTAL-AWARE preview core.
// A palette does not live on a gradient bar; it lives on THIS view's iteration
// density. Two deterministic analyses the fractal-aware preview + advisor surface:
//
//   * HISTOGRAM-AWARE stop mapping — where do the palette's stops land on the density
//     of the parameter (normalised smooth-t) the render actually feeds the ramp? Flag
//     wasted range ("this view never hits 40% of your palette") and offer a
//     histogram-equalised redistribution (the CDF-inverse idiom of the render's own
//     HistogramEqualizer, #145) so stops track where the pixels are.
//   * SEAMLESS-CYCLE guarantee — for palette cycling (CyclingGradientColorMap) the
//     ramp's end must meet its start in PERCEPTUAL space or the cycle shows a seam;
//     measured as OkLab ΔE across the join (reuses S10.1 PerceptualRamp).
//
// Pure + deterministic → asserted in tests (the colour parity twin). Self-contained
// (the render's HistogramEqualizer is internal); this mirrors its equalisation idiom
// on the t-domain the caller supplies.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>Fractal-aware palette analysis (roadmap S10.3, #392): how a palette's range
/// maps onto a view's density, a histogram-equalised stop redistribution, and the
/// seamless-cycle check.</summary>
public static class PaletteHistogram
{
    /// <summary>Histogram over the palette parameter <c>t∈[0,1]</c> — the normalised
    /// value the render feeds the ramp per pixel (e.g. smooth-iteration / maxIter, or a
    /// CDF-equalised t). Values are clamped to [0,1]; NaN (in-set / no colour) skipped.
    /// <paramref name="bins"/> ≥ 1.</summary>
    public static int[] Build(IReadOnlyList<float> tValues, int bins)
    {
        if (bins < 1) bins = 1;
        var h = new int[bins];
        if (tValues == null) return h;
        for (int i = 0; i < tValues.Count; i++)
        {
            float t = tValues[i];
            if (float.IsNaN(t)) continue;
            t = Math.Clamp(t, 0f, 1f);
            int b = (int)(t * bins);
            if (b >= bins) b = bins - 1;
            h[b]++;
        }
        return h;
    }

    /// <summary>Total counted pixels in a histogram.</summary>
    public static long Total(int[] hist)
    {
        long n = 0;
        if (hist != null) foreach (var c in hist) n += c;
        return n;
    }

    /// <summary>Fraction of the palette's t-range (as histogram bins) that receives less
    /// than <paramref name="minShare"/> of the pixels — the "wasted" range this view
    /// barely touches. 0 = the whole ramp is used; 0.4 = 40% of the ramp is near-unused.
    /// An empty histogram counts as fully wasted (1.0).</summary>
    public static double WastedFraction(int[] hist, double minShare = 0.001)
    {
        if (hist == null || hist.Length == 0) return 1.0;
        long total = Total(hist);
        if (total <= 0) return 1.0;
        double floor = minShare * total;
        int wasted = 0;
        foreach (var c in hist) if (c < floor) wasted++;
        return (double)wasted / hist.Length;
    }

    /// <summary>Redistribute <paramref name="count"/> stop positions so equal pixel-mass
    /// falls between consecutive stops — the histogram-equalisation (CDF-inverse) remap
    /// that packs colour resolution where THIS view's pixels actually are. Positions are
    /// returned ascending in [0,1] with the endpoints pinned to 0 and 1. A flat / empty
    /// histogram returns evenly-spaced positions (a no-op redistribution).</summary>
    public static float[] EqualizeStopPositions(int[] hist, int count)
    {
        if (count < 2) count = 2;
        var pos = new float[count];
        pos[0] = 0f;
        pos[count - 1] = 1f;
        long total = Total(hist);
        if (hist == null || hist.Length == 0 || total <= 0)
        {
            for (int i = 0; i < count; i++) pos[i] = (float)i / (count - 1);
            return pos;
        }
        int bins = hist.Length;
        // Cumulative distribution at each bin EDGE (cdf[0]=0 … cdf[bins]=1).
        var cdf = new double[bins + 1];
        double run = 0;
        for (int b = 0; b < bins; b++) { run += hist[b]; cdf[b + 1] = run / total; }
        // Invert: for each interior stop, target mass q = i/(count-1), find t where the
        // CDF reaches q (linear within the bracketing bin).
        for (int i = 1; i < count - 1; i++)
        {
            double q = (double)i / (count - 1);
            int b = 0;
            while (b < bins && cdf[b + 1] < q) b++;
            if (b >= bins) { pos[i] = 1f; continue; }
            double lo = cdf[b], hi = cdf[b + 1];
            double frac = hi > lo ? (q - lo) / (hi - lo) : 0.0;
            pos[i] = (float)((b + frac) / bins);
        }
        // Guard strict monotonicity against float ties.
        for (int i = 1; i < count; i++)
            if (pos[i] <= pos[i - 1]) pos[i] = Math.Min(1f, pos[i - 1] + 1e-6f);
        return pos;
    }

    // ── seamless cycle ────────────────────────────────────────────────────────

    /// <summary>Perceptual gap across a cycling ramp's join: OkLab ΔE between the last
    /// and first stop. Large = a visible seam when the palette cycles.</summary>
    public static float CycleSeamDeltaE(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1)
        => PerceptualRamp.DeltaEOk(r0, g0, b0, r1, g1, b1);

    /// <summary>True when a cycling ramp's ends meet within <paramref name="tol"/> OkLab
    /// ΔE — the cycle reads seamlessly. Default tol ≈ a just-noticeable difference.</summary>
    public static bool IsSeamlessCycle(byte r0, byte g0, byte b0, byte r1, byte g1, byte b1, float tol = 0.02f)
        => CycleSeamDeltaE(r0, g0, b0, r1, g1, b1) <= tol;
}
