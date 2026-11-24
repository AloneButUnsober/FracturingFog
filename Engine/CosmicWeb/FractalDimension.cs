// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalDimension.cs (#2023, Cosmic Web epic #2020)
//
// The fractal dimension function D(r) of a point set in a periodic box, by the measurement chain of
// J. Einasto, "Fractal properties of the cosmic web" (arXiv:2608.04694, 2026):
//   Eq. 2  xi(r) = DD(r) / RR(r) - 1          (the natural estimator, Sec. III)
//          g(r)  = 1 + xi(r)                   (the structure function, unnumbered, Sec. III / IV)
//   Eq. 3  gamma(r) = d log g(r) / d log r     (Sec. IV)
//   Eq. 4  D(r) = 3 + gamma(r)                 (Sec. IV)
// The paper states the derivative exactly and gives no binning or smoothing scheme; the ones below are
// FF's own choices:
//   - DD: unordered pairs counted in logarithmic bins of r on a periodic cell list (the standard
//     linked-cell neighbour search of particle simulation, not FF's; minimum-image separations, exact
//     integer counts, so the result is independent of the thread count).
//   - RR: analytic for a periodic box, M (M - 1) / 2 * V_shell / V_box, instead of a random catalogue
//     (valid for r below half the box side, which the range keeps to).
//   - A random subsample of M points (deterministic, seeded; a partial Fisher-Yates shuffle in
//     Durstenfeld's in-place form) when the set is larger; the estimator's expectation does not change,
//     only its noise.
//   - gamma: the least-squares slope of log g against log r over a window of 2w + 1 neighbouring bins
//     (a smoothed finite difference); a bin with too few pairs or g <= 0 has no value.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2023 — the measured chain, per logarithmic bin of separation (bin centres in <see cref="R"/>).</summary>
public sealed record CosmicWebDimension(
    double[] R, long[] Pairs, double[] Xi, double[] G, double[] Gamma, double[] D,
    int SampleSize, int TotalPoints, double BoxSize, double RMin, double RMax)
{
    /// <summary>D at separation <paramref name="r"/>, linear in log r between bin centres (NaN outside or in a gap).</summary>
    public double DAt(double r)
    {
        if (!(r > 0) || R.Length == 0) return double.NaN;
        double lr = Math.Log(r);
        for (int k = 0; k + 1 < R.Length; k++)
        {
            double a = Math.Log(R[k]), b = Math.Log(R[k + 1]);
            if (lr < a || lr > b) continue;
            if (!double.IsFinite(D[k]) || !double.IsFinite(D[k + 1])) return double.NaN;
            double t = (lr - a) / (b - a);
            return D[k] + t * (D[k + 1] - D[k]);
        }
        return double.NaN;
    }
}

/// <summary>#2023 — settings of a measurement (the cache key alongside the point set).</summary>
public sealed record CosmicWebDimensionSettings(int Sample, int Bins, double RMinFraction, double RMaxFraction, int Window, int MinPairs, int Seed)
{
    /// <summary>FF's defaults: r from box/1000 to box/4 in 30 bins, a 5-bin slope window, 10 pairs per bin.</summary>
    public static CosmicWebDimensionSettings Default(int sample, int seed = 2023) => new(sample, 30, 1e-3, 0.25, 2, 10, seed);
}

public static class FractalDimension
{
    public const int MinSample = 1000;
    public const int MaxSample = 100_000;

    /// <summary>The subsample indices: all points when the set is small, else M distinct points by a seeded
    /// partial Fisher-Yates shuffle (sorted, so the counting order is the point order).</summary>
    public static int[] SampleIndices(int count, int sample, int seed)
    {
        if (count <= sample)
        {
            var all = new int[count];
            for (int i = 0; i < count; i++) all[i] = i;
            return all;
        }
        var idx = new int[count];
        for (int i = 0; i < count; i++) idx[i] = i;
        var rng = new SplitMix64(SplitMix64.Mix((ulong)(uint)seed, 0x2023UL));
        for (int i = 0; i < sample; i++)
        {
            int j = i + (int)(rng.NextDouble() * (count - i));
            if (j >= count) j = count - 1;
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        var pick = idx.AsSpan(0, sample).ToArray();
        Array.Sort(pick);
        return pick;
    }

    /// <summary>Bin edges: <paramref name="bins"/> logarithmic bins from rMin to rMax (bins + 1 edges).</summary>
    public static double[] Edges(double rMin, double rMax, int bins)
    {
        var e = new double[bins + 1];
        double l0 = Math.Log(rMin), dl = (Math.Log(rMax) - l0) / bins;
        for (int k = 0; k <= bins; k++) e[k] = Math.Exp(l0 + k * dl);
        e[bins] = rMax;
        return e;
    }

    /// <summary>Unordered pair counts in logarithmic bins on a periodic cell list (minimum image).</summary>
    public static long[] CountPairs(float[] xyz, int[] idx, double box, double rMin, double rMax, int bins, CancellationToken ct = default)
    {
        int m = idx.Length;
        // Cells of side box/G with G = floor(k box / rMax): a pair closer than rMax is at most k cells
        // apart on each axis; 2k + 1 <= G keeps the neighbour cells distinct (no pair counted twice).
        int reach = 3;
        int g = (int)Math.Floor(reach * box / rMax);
        while (g < 2 * reach + 1 && reach > 1) { reach--; g = (int)Math.Floor(reach * box / rMax); }
        if (g < 2 * reach + 1) throw new ArgumentException("rMax must be at most a third of the box side.");
        g = Math.Min(g, 256);
        reach = Math.Max(reach, (int)Math.Ceiling(rMax * g / box));
        if (2 * reach + 1 > g) throw new ArgumentException("rMax too large for the cell list.");
        double cell = box / g;

        var px = new double[m]; var py = new double[m]; var pz = new double[m];
        var cellOf = new int[m];
        var head = new int[g * g * g];
        Array.Fill(head, -1);
        var next = new int[m];
        for (int i = 0; i < m; i++)
        {
            int s = idx[i];
            px[i] = xyz[3 * s]; py[i] = xyz[3 * s + 1]; pz[i] = xyz[3 * s + 2];
            int cx = Math.Clamp((int)(px[i] / cell), 0, g - 1), cy = Math.Clamp((int)(py[i] / cell), 0, g - 1), cz = Math.Clamp((int)(pz[i] / cell), 0, g - 1);
            cellOf[i] = (cz * g + cy) * g + cx;
        }
        // Lists built in reverse so each cell lists its points in increasing index order.
        for (int i = m - 1; i >= 0; i--) { next[i] = head[cellOf[i]]; head[cellOf[i]] = i; }

        double lMin = Math.Log(rMin), invDl = bins / (Math.Log(rMax) - lMin);
        double r2Min = rMin * rMin, r2Max = rMax * rMax;
        var total = new long[bins];
        object gate = new();
        Parallel.For(0, m, new ParallelOptions { CancellationToken = ct }, () => new long[bins], (i, _, local) =>
        {
            int c = cellOf[i];
            int cx = c % g, cy = c / g % g, cz = c / (g * g);
            double xi = px[i], yi = py[i], zi = pz[i];
            for (int dz = -reach; dz <= reach; dz++)
            {
                int nz = (cz + dz + g) % g;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    int ny = (cy + dy + g) % g;
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int nx = (cx + dx + g) % g;
                        for (int j = head[(nz * g + ny) * g + nx]; j >= 0; j = next[j])
                        {
                            if (j <= i) continue;
                            double ddx = px[j] - xi; ddx -= box * Math.Round(ddx / box);
                            double ddy = py[j] - yi; ddy -= box * Math.Round(ddy / box);
                            double ddz = pz[j] - zi; ddz -= box * Math.Round(ddz / box);
                            double r2 = ddx * ddx + ddy * ddy + ddz * ddz;
                            if (r2 < r2Min || r2 >= r2Max) continue;
                            int b = (int)((0.5 * Math.Log(r2) - lMin) * invDl);
                            if ((uint)b < (uint)bins) local[b]++;
                        }
                    }
                }
            }
            return local;
        }, local => { lock (gate) for (int k = 0; k < bins; k++) total[k] += local[k]; });
        return total;
    }

    /// <summary>The same counts by brute force over all pairs (minimum image): the independent check of
    /// the cell list.</summary>
    public static long[] CountPairsBrute(float[] xyz, int[] idx, double box, double rMin, double rMax, int bins)
    {
        var e = Edges(rMin, rMax, bins);
        var dd = new long[bins];
        for (int a = 0; a < idx.Length; a++)
            for (int b = a + 1; b < idx.Length; b++)
            {
                int i = idx[a], j = idx[b];
                double s = 0;
                for (int d = 0; d < 3; d++)
                {
                    double v = Math.Abs((double)xyz[3 * j + d] - xyz[3 * i + d]);
                    v = Math.Min(v, box - v);
                    s += v * v;
                }
                double r = Math.Sqrt(s);
                if (r < rMin || r >= rMax) continue;
                // Search the edges directly (no log arithmetic shared with the cell list).
                int k = Array.BinarySearch(e, r);
                if (k < 0) k = ~k - 1;
                if (k >= 0 && k < bins) dd[k]++;
            }
        return dd;
    }

    /// <summary>Natural estimator with analytic periodic RR: xi_k = DD_k / RR_k - 1.</summary>
    public static double[] Xi(long[] dd, int m, double box, double[] edges)
    {
        var xi = new double[dd.Length];
        double pairs = 0.5 * m * (m - 1.0), vol = box * box * box;
        for (int k = 0; k < dd.Length; k++)
        {
            double shell = 4.0 / 3.0 * Math.PI * (Math.Pow(edges[k + 1], 3) - Math.Pow(edges[k], 3));
            double rr = pairs * shell / vol;
            xi[k] = dd[k] / rr - 1.0;
        }
        return xi;
    }

    /// <summary>gamma_k: least-squares slope of log g on log r over bins k - w .. k + w (all of them valid), else NaN.</summary>
    public static double[] Gamma(double[] r, double[] g, long[] dd, int window, int minPairs)
    {
        int n = r.Length;
        var gamma = new double[n];
        for (int k = 0; k < n; k++)
        {
            gamma[k] = double.NaN;
            int lo = k - window, hi = k + window;
            if (lo < 0 || hi >= n) continue;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            bool ok = true;
            for (int j = lo; j <= hi; j++)
            {
                if (dd[j] < minPairs || !(g[j] > 0)) { ok = false; break; }
                double x = Math.Log(r[j]), y = Math.Log(g[j]);
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            if (!ok) continue;
            int c = hi - lo + 1;
            double den = c * sxx - sx * sx;
            gamma[k] = den > 0 ? (c * sxy - sx * sy) / den : double.NaN;
        }
        return gamma;
    }

    /// <summary>Runs the chain (Einasto 2026, Eqs. 2-4) on a point set.</summary>
    public static CosmicWebDimension Measure(CosmicWebPoints pts, CosmicWebDimensionSettings s, CancellationToken ct = default)
    {
        int sample = Math.Clamp(s.Sample, MinSample, MaxSample);
        double box = pts.BoxSize;
        double rMin = box * s.RMinFraction, rMax = box * s.RMaxFraction;
        var idx = SampleIndices(pts.Count, sample, s.Seed);
        var edges = Edges(rMin, rMax, s.Bins);
        var r = new double[s.Bins];
        for (int k = 0; k < s.Bins; k++) r[k] = Math.Sqrt(edges[k] * edges[k + 1]);
        long[] dd = idx.Length >= 2 ? CountPairs(pts.Xyz, idx, box, rMin, rMax, s.Bins, ct) : new long[s.Bins];
        var xi = Xi(dd, idx.Length, box, edges);
        var g = new double[s.Bins];
        for (int k = 0; k < s.Bins; k++) g[k] = 1.0 + xi[k];
        var gamma = Gamma(r, g, dd, s.Window, s.MinPairs);
        var d = new double[s.Bins];
        for (int k = 0; k < s.Bins; k++) d[k] = 3.0 + gamma[k];
        return new CosmicWebDimension(r, dd, xi, g, gamma, d, idx.Length, pts.Count, box, rMin, rMax);
    }
}
