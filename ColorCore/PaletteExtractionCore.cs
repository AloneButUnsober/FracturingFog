// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtractionCore.cs
//
// Roadmap slice S10.5 (PaletteBuilder-Design.md, #392) — EXTRACTION UPGRADES.
// Pull a palette out of a bag of colours the perceptual way:
//
//   * PERCEPTUAL K-MEANS  — Lloyd's k-means clustering in OkLab (not raw sRGB),
//                           k-means++ seeded, so distances are perceptual and the
//                           clusters don't collapse on a degenerate sample set.
//   * ORDERED RAMP        — return the clusters as an ORDERED ramp (ascending OkLab
//                           lightness), not just an unordered swatch bag: a ramp is
//                           a curve, and the render wants it lightness-structured
//                           (design §2 — luminance is load-bearing twice).
//   * DOMINANT + ACCENT   — the largest cluster is the DOMINANT; the ACCENT is the
//                           most chromatic of the rest — the pop colour to pair with
//                           the workhorse.
//
// Why here (Engine), not the PaletteBuilder extraction lib: the lib's extractors
// (`Imaging/PaletteExtraction/*`) compile ONLY into PaletteBuilder.Lib — the render
// and the headless tests can't reach them. An extracted ramp FLOWS INTO THE RENDER,
// so the perceptual core lives in Engine alongside its S10 siblings (PerceptualRamp,
// CvdAnalysis, …), reusing PerceptualRamp's OkLab primitives. Pure + deterministic
// (fixed seed) → asserted in tests.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>One extracted colour plus the sample weight (pixel count) it represents
/// (roadmap S10.5, #392).</summary>
public readonly record struct PaletteCluster(byte R, byte G, byte B, int Weight);

/// <summary>The result of a perceptual extraction (roadmap S10.5, #392): an ordered
/// ramp plus the dominant and accent picks.</summary>
public sealed class ExtractedPalette
{
    /// <summary>The clusters as an ordered ramp — ascending OkLab lightness.</summary>
    public IReadOnlyList<PaletteCluster> Ramp { get; }

    /// <summary>The workhorse colour — the heaviest (most pixels) cluster.</summary>
    public PaletteCluster Dominant { get; }

    /// <summary>The pop colour — the most chromatic cluster that isn't the dominant
    /// (falls back to the dominant when there is only one cluster).</summary>
    public PaletteCluster Accent { get; }

    public ExtractedPalette(IReadOnlyList<PaletteCluster> ramp, PaletteCluster dominant, PaletteCluster accent)
    {
        Ramp = ramp;
        Dominant = dominant;
        Accent = accent;
    }
}

/// <summary>Perceptual palette extraction (roadmap S10.5, #392): OkLab k-means →
/// lightness-ordered ramp + dominant / accent detection. Reuses
/// <see cref="PerceptualRamp"/> for the OkLab primitives.</summary>
public static class PaletteExtractionCore
{
    private const int MaxIterations = 32;
    private const float ConvergenceEpsilon = 1e-5f;   // OkLab feature units (L,a,b ≈ [0,1])

    /// <summary>Extract a perceptual palette from <paramref name="samples"/> (an sRGB
    /// colour bag — one entry per pixel, or per weighted bin). Clusters in OkLab, orders
    /// the survivors by lightness, and tags the dominant + accent. Deterministic for a
    /// given <paramref name="seed"/>.</summary>
    /// <param name="samples">The colour bag; empty → an empty palette.</param>
    /// <param name="k">Requested cluster count (clamped to ≥ 1 and ≤ sample count).</param>
    /// <param name="seed">k-means++ RNG seed (fixed default keeps runs reproducible).</param>
    public static ExtractedPalette Extract(
        IReadOnlyList<(byte r, byte g, byte b)> samples, int k, int seed = 1337)
        => Classify(KMeansOkLab(samples, k, seed));

    /// <summary>Turn a set of weighted colour clusters into an <see cref="ExtractedPalette"/>:
    /// the lightness-ordered ramp plus the dominant (heaviest) and accent (most chromatic
    /// non-dominant) picks. Split out from <see cref="Extract"/> so callers that ALREADY
    /// hold weighted clusters — e.g. a palette's extracted swatches carrying their pixel
    /// counts — can reuse the exact same dominant / accent / ordering rules without
    /// re-running k-means.</summary>
    public static ExtractedPalette Classify(IReadOnlyList<PaletteCluster> clusters)
    {
        if (clusters == null || clusters.Count == 0)
        {
            var empty = new PaletteCluster(0, 0, 0, 0);
            return new ExtractedPalette(Array.Empty<PaletteCluster>(), empty, empty);
        }

        // Dominant = heaviest cluster (ties → the one seen first).
        var dominant = clusters[0];
        foreach (var c in clusters)
            if (c.Weight > dominant.Weight) dominant = c;

        // Accent = most chromatic non-dominant cluster (ties → heavier). Falls back to
        // the dominant when it's the only cluster.
        var accent = dominant;
        float bestChroma = -1f;
        foreach (var c in clusters)
        {
            if (c.Equals(dominant)) continue;
            float chroma = Chroma(c.R, c.G, c.B);
            if (chroma > bestChroma || (chroma == bestChroma && c.Weight > accent.Weight))
            {
                bestChroma = chroma;
                accent = c;
            }
        }

        // Ordered ramp: ascending OkLab lightness (a ramp is a curve, not a bag).
        var ramp = new List<PaletteCluster>(clusters);
        ramp.Sort((a, b) =>
        {
            float la = PerceptualRamp.RgbToOkLab(a.R, a.G, a.B).L;
            float lb = PerceptualRamp.RgbToOkLab(b.R, b.G, b.B).L;
            int cmp = la.CompareTo(lb);
            return cmp != 0 ? cmp : b.Weight.CompareTo(a.Weight);   // tie → heavier first
        });

        return new ExtractedPalette(ramp, dominant, accent);
    }

    /// <summary>Lloyd's k-means over the sample bag, clustering in OkLab (perceptual)
    /// with k-means++ seeding. Each returned cluster's colour is the mean sRGB of its
    /// assigned samples (more faithful than inverting an averaged OkLab centroid) and
    /// its weight is the sample count. Empty clusters are dropped, so the result may
    /// hold fewer than <paramref name="k"/> entries.</summary>
    public static List<PaletteCluster> KMeansOkLab(
        IReadOnlyList<(byte r, byte g, byte b)> samples, int k, int seed = 1337)
    {
        var outp = new List<PaletteCluster>();
        if (samples == null) return outp;
        int n = samples.Count;
        if (n == 0) return outp;
        k = Math.Clamp(k, 1, n);

        // Feature vectors in OkLab.
        var fL = new float[n];
        var fa = new float[n];
        var fb = new float[n];
        for (int i = 0; i < n; i++)
        {
            var (r, g, b) = samples[i];
            var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
            fL[i] = L; fa[i] = a; fb[i] = bb;
        }

        // k-means++ seeding.
        var rng = new Random(seed);
        var cL = new float[k];
        var ca = new float[k];
        var cb = new float[k];
        int[] seeds = KMeansPlusPlusSeed(fL, fa, fb, n, k, rng);
        for (int c = 0; c < k; c++)
        {
            int s = seeds[c];
            cL[c] = fL[s]; ca[c] = fa[s]; cb[c] = fb[s];
        }

        var assign = new int[n];
        var count = new int[k];
        var sL = new double[k];
        var sa = new double[k];
        var sb = new double[k];

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            // Assign each sample to its nearest centroid (OkLab Euclidean).
            for (int i = 0; i < n; i++)
            {
                float best = float.MaxValue;
                int bestC = 0;
                for (int c = 0; c < k; c++)
                {
                    float dL = fL[i] - cL[c], da = fa[i] - ca[c], db = fb[i] - cb[c];
                    float d = dL * dL + da * da + db * db;
                    if (d < best) { best = d; bestC = c; }
                }
                assign[i] = bestC;
            }

            // Recompute centroids in OkLab.
            Array.Clear(count); Array.Clear(sL); Array.Clear(sa); Array.Clear(sb);
            for (int i = 0; i < n; i++)
            {
                int c = assign[i];
                sL[c] += fL[i]; sa[c] += fa[i]; sb[c] += fb[i]; count[c]++;
            }

            float shift = 0f;
            for (int c = 0; c < k; c++)
            {
                if (count[c] == 0) continue;
                float nL = (float)(sL[c] / count[c]);
                float na = (float)(sa[c] / count[c]);
                float nb = (float)(sb[c] / count[c]);
                float dL = nL - cL[c], da = na - ca[c], db = nb - cb[c];
                shift += dL * dL + da * da + db * db;
                cL[c] = nL; ca[c] = na; cb[c] = nb;
            }

            if (shift < ConvergenceEpsilon) break;
        }

        // Each cluster's swatch = mean sRGB of its assigned samples.
        var rSum = new long[k];
        var gSum = new long[k];
        var bSum = new long[k];
        var cCount = new int[k];
        for (int i = 0; i < n; i++)
        {
            int c = assign[i];
            var (r, g, b) = samples[i];
            rSum[c] += r; gSum[c] += g; bSum[c] += b; cCount[c]++;
        }
        for (int c = 0; c < k; c++)
        {
            if (cCount[c] == 0) continue;   // drop empty clusters
            byte r = (byte)Math.Clamp(rSum[c] / cCount[c], 0, 255);
            byte g = (byte)Math.Clamp(gSum[c] / cCount[c], 0, 255);
            byte b = (byte)Math.Clamp(bSum[c] / cCount[c], 0, 255);
            outp.Add(new PaletteCluster(r, g, b, cCount[c]));
        }
        return outp;
    }

    /// <summary>OKLCH chroma (hypot of the OkLab a/b) of an sRGB colour — how colourful
    /// it is, independent of lightness. Drives accent selection.</summary>
    private static float Chroma(byte r, byte g, byte b)
    {
        var (_, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
        return MathF.Sqrt(a * a + bb * bb);
    }

    // k-means++ seeding in the OkLab feature space: spread the initial centroids by
    // sampling each next seed proportional to its squared distance from the nearest
    // already-chosen seed. Keeps degenerate bags (few distinct colours) from collapsing.
    private static int[] KMeansPlusPlusSeed(
        float[] fL, float[] fa, float[] fb, int n, int k, Random rng)
    {
        var seeds = new int[k];
        seeds[0] = rng.Next(n);
        var minDist = new double[n];
        for (int i = 0; i < n; i++) minDist[i] = double.MaxValue;

        for (int s = 1; s < k; s++)
        {
            int prev = seeds[s - 1];
            float pL = fL[prev], pa = fa[prev], pb = fb[prev];
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                float dL = fL[i] - pL, da = fa[i] - pa, db = fb[i] - pb;
                double d = dL * dL + da * da + db * db;
                if (d < minDist[i]) minDist[i] = d;
                total += minDist[i];
            }

            double target = rng.NextDouble() * total;
            double acc = 0;
            int pick = n - 1;
            for (int i = 0; i < n; i++)
            {
                acc += minDist[i];
                if (acc >= target) { pick = i; break; }
            }
            seeds[s] = pick;
        }
        return seeds;
    }
}
