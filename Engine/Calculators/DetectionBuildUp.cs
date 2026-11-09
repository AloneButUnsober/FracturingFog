// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DetectionBuildUp.cs (#1599) — the particle-by-particle build-up view of the wave
// fractals: Slit Cascade far field, Talbot carpet and Julibrot Pair path
// interference. Each detection lands on a pixel with probability proportional to
// that pixel's intensity |A|², and the detections accumulate into the fringes, as
// in the single-electron double-slit experiment of Tonomura, Endo, Matsuda,
// Kawasaki & Ezawa (Am. J. Phys. 57(2), 117–120, 1989, doi:10.1119/1.16104;
// Docs/Resources-Bibliography.md#tonomura-buildup). The image IS the probability,
// revealed by sampling.
//
// FF's sampler (not taken from the paper): an inverse CDF over the screen's pixels
// (the screen = the detector), drawn in chunks of ChunkSize samples, chunk k using
// its own SplitMix64 stream seeded from (seed, k). So the result is deterministic
// whatever the thread count (poster == live), and sample j is the same whatever the
// total: raising the count only ADDS dots — an animation of the count shows the
// pattern building up, never reshuffling. Counts are accumulated with atomic
// increments, as a histogram (Melinda Green's Buddhabrot idea, credited in the bibliography).
//
// Painting: each pixel sums the detections within DotRadius pixels (a disk), and
// shows exposure v = 1 − exp(−gain·sum) as achromatic OkLab lightness — no hue, so
// it reads the same for every colour vision. Background (no detections) is black.

using System;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>Detection build-up sampling and painting (#1599); see the file header.</summary>
public static class DetectionBuildUp
{
    /// <summary>Samples per independently seeded chunk.</summary>
    public const int ChunkSize = 4096;
    /// <summary>Largest detection count honoured.</summary>
    public const int MaxCount = 50_000_000;
    /// <summary>Largest dot radius in pixels.</summary>
    public const int MaxRadius = 8;

    public static int ClampCount(int n) => Math.Clamp(n, 0, MaxCount);
    public static int ClampRadius(int r) => Math.Clamp(r, 0, MaxRadius);

    /// <summary>Draw <paramref name="count"/> detections over the pixels with
    /// probability ∝ <paramref name="weights"/> (negative / NaN = 0) and add them to
    /// <paramref name="counts"/> (cleared first). Returns false (and leaves every
    /// count 0) when the total weight is 0.</summary>
    public static bool Sample(double[] weights, int count, int seed, int[] counts, CancellationToken ct = default)
    {
        int n = weights.Length;
        Array.Clear(counts, 0, counts.Length);
        var cdf = new double[n];
        double total = 0.0;
        for (int i = 0; i < n; i++)
        {
            double w = weights[i];
            if (w > 0.0 && double.IsFinite(w)) total += w;
            cdf[i] = total;
        }
        count = ClampCount(count);
        if (!(total > 0.0) || count == 0) return total > 0.0;
        int chunks = (count + ChunkSize - 1) / ChunkSize;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, k =>
        {
            ulong state = ChunkSeed(seed, k);
            int m = Math.Min(ChunkSize, count - k * ChunkSize);
            for (int j = 0; j < m; j++)
            {
                double u = NextDouble(ref state) * total;
                // First pixel whose cumulative weight exceeds u (zero-weight pixels never win).
                int lo = 0, hi = n - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cdf[mid] > u) hi = mid; else lo = mid + 1;
                }
                Interlocked.Increment(ref counts[lo]);
            }
        });
        return true;
    }

    /// <summary>Paint the build-up: each pixel's exposure from the detections within
    /// <paramref name="radius"/> px. Writes <paramref name="color"/>; when
    /// <paramref name="smooth"/> is given, also the height v·maxIt (kept below maxIt so
    /// themes never read it as "in the set"). With <paramref name="only"/>, pixels whose
    /// entry is negative (off the detector, e.g. the set's interior) are left as they are.</summary>
    public static void Paint(int[] counts, int width, int height, int radius, double gain,
        uint[] color, float[]? smooth, int maxIt, CancellationToken ct = default, double[]? only = null)
    {
        int r = ClampRadius(radius), r2 = r * r;
        double g = double.IsFinite(gain) && gain > 0.0 ? gain : 0.5;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (only != null && only[row + x] < 0.0) continue;
                long sum = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= height) continue;
                    int rr = yy * width;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy > r2) continue;
                        int xx = x + dx;
                        if (xx < 0 || xx >= width) continue;
                        sum += counts[rr + xx];
                    }
                }
                double v = sum > 0 ? 1.0 - Math.Exp(-g * sum) : 0.0;
                color[row + x] = Grey(v);
                if (smooth != null) smooth[row + x] = (float)Math.Min(v * maxIt, maxIt * (1.0 - 1e-6));
            }
        });
    }

    /// <summary>Achromatic colour with OkLab lightness <paramref name="v"/> (0 = black).</summary>
    public static uint Grey(double v)
    {
        if (!(v > 0.0)) return 0xFF000000u;
        GradientColorSpaces.OkLabToRgb((float)Math.Min(1.0, v), 0f, 0f, out float r, out float g, out float b);
        static uint C(float c) => (uint)Math.Clamp((int)(c + 0.5f), 0, 255);
        return 0xFF000000u | (C(r) << 16) | (C(g) << 8) | C(b);
    }

    // SplitMix64 (Steele, Lea & Flood, OOPSLA 2014 —
    // Docs/Resources-Bibliography.md#splitmix64).
    private static ulong ChunkSeed(int seed, int chunk)
    {
        ulong s = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)chunk + 1UL) * 0xD1B54A32D192ED03UL);
        Next(ref s);
        return s;
    }

    private static ulong Next(ref ulong state)
    {
        ulong z = unchecked(state += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    private static double NextDouble(ref ulong state) => (Next(ref state) >> 11) * (1.0 / (1UL << 53));
}
