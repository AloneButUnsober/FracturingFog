// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/CosmicWeb/SoneiraPeebles.cs (#2021, Cosmic Web epic #2020)
//
// The hierarchical clustering model of R. M. Soneira & P. J. E. Peebles, "A computer model
// universe - Simulation of the nature of the galaxy distribution in the Lick catalog",
// Astron. J. 83, 845-860 (1978), doi:10.1086/112268. Read from the scanned original
// (pp. 845-848, 853-856, 859-860); section and equation numbers below are the paper's.
//
// A clump (Sec. IIa, Eq. 1): within a sphere of radius R place eta spheres of radius R/lambda,
// within each of those eta spheres of radius R/lambda^2, and so on through L levels to eta^L
// positions in eta^(L-1) spheres of radius r0 = R/lambda^(L-1). A galaxy sits at each position.
// Clumps are distributed uniformly at random in space.
//
// Two ways of placing the sub-centres, both from the paper:
//  - UniformSphere: "the eta subsphere centers are distributed uniformly at random in the
//    sphere" (their earlier m < 14 model, SP 1977, described in Secs. IIa and IVa). FF keeps
//    eta fixed; the paper's random-eta distribution P_eta (Eq. 16) is not used (Sec. IId shows
//    it breaks the constancy of Q).
//  - Dumbbell (the final model, Sec. IVc i): eta = 2, the two sub-centres at a fixed separation
//    d_n in a random orientation, centred on the parent centre. d_1 = 11.35 h^-1 Mpc (Eq. 17),
//    d_1/d_2 = 1.10 (Eq. 18), d_n/d_{n+1} = lambda = 1.76 thereafter (Eq. 19).
// Number of levels per clump (Sec. IIe, IVc ii): either fixed, or drawn from the paper's
// Table I distribution P_L over L = 7..12 (column 4; it sums to 0.993 as printed, so FF
// normalises it).
//
// Scaling (Eq. 4): the density inside clusters of size r runs as r^-gamma with
// gamma = 3 - log eta / log lambda, so the point set's fractal dimension over r0 << r << R is
//   D = 3 - gamma = log eta / log lambda   (1.226 for the final model's eta = 2, lambda = 1.76;
//   the paper quotes gamma = 1.77).
//
// FF's own choices (not the paper's): a periodic cubic box (the paper used a survey cone and
// projected to the sky); FirstRatio generalises Eq. 18 to the UniformSphere placement too; the
// sizes are in h^-1 Mpc; a point budget caps the set by dropping whole clumps from the end of
// the list (so every clump that is kept is complete); seed-deterministic per clump, so the
// result does not depend on the thread count.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2021 — everything that changes the generated points (the cache key).</summary>
public sealed record SoneiraPeeblesSettings(
    int Seed, double BoxSize, int Clumps, CosmicWebPlacement Placement, int Eta,
    double Lambda, double FirstRatio, double TopSize, CosmicWebLevelMode LevelMode, int Levels, int PointCap);

/// <summary>#2021 — a generated point set in a periodic box [0, BoxSize)^3, xyz interleaved.</summary>
public sealed class CosmicWebPoints
{
    public float[] Xyz { get; init; } = Array.Empty<float>();
    public int Count { get; init; }
    public double BoxSize { get; init; }
    public int ClumpsRequested { get; init; }
    public int ClumpsUsed { get; init; }
    /// <summary>True when the point budget dropped clumps.</summary>
    public bool Capped { get; init; }
}

public static class SoneiraPeebles
{
    /// <summary>Table I of the paper: levels L and their probability P_L (column 4), as printed.</summary>
    public static readonly (int L, double P)[] TableI =
    {
        (7, 0.68), (8, 0.23), (9, 0.061), (10, 0.015), (11, 0.0059), (12, 0.0011),
    };

    /// <summary>Table I column 3, "minimum clustering diameter" (kpc, h^-1): d_L for L = 7..12.</summary>
    public static readonly (int L, double Kpc)[] TableIMinimumDiameter =
    {
        (7, 612), (8, 348), (9, 198), (10, 113), (11, 64), (12, 37),
    };

    /// <summary>Eq. 17: separation of the sub-centres at the largest level, h^-1 Mpc.</summary>
    public const double PaperD1 = 11.35;
    /// <summary>Eq. 18: d_1 / d_2.</summary>
    public const double PaperFirstRatio = 1.10;
    /// <summary>Eq. 19: d_n / d_{n+1} for n &gt;= 2.</summary>
    public const double PaperLambda = 1.76;
    /// <summary>Sec. IVc iii: mean space density of clumps, 3.1e-4 h^3 Mpc^-3.</summary>
    public const double PaperClumpDensity = 3.1e-4;

    /// <summary>Hard ceiling on the point budget (memory: 12 bytes a point).</summary>
    public const int MaxPointCap = 16_000_000;
    /// <summary>Most points one clump may have (eta^L).</summary>
    public const long MaxClumpPoints = 1L << 24;

    /// <summary>Eq. 4: D = 3 - gamma = log eta / log lambda.</summary>
    public static double AnalyticDimension(int eta, double lambda) => Math.Log(eta) / Math.Log(lambda);

    /// <summary>The size at level <paramref name="level"/> (1-based): the dumbbell separation d_level, or the
    /// sphere radius R_level. s_1 = top, s_2 = top / firstRatio, s_(n+1) = s_n / lambda.</summary>
    public static double SizeAt(int level, double top, double firstRatio, double lambda)
    {
        if (level <= 1) return top;
        return top / firstRatio / Math.Pow(lambda, level - 2);
    }

    /// <summary>Points in a clump of <paramref name="levels"/> levels: eta^L.</summary>
    public static long ClumpPoints(int eta, int levels)
    {
        long n = 1;
        for (int i = 0; i < levels; i++) { n *= eta; if (n > MaxClumpPoints) return n; }
        return n;
    }

    /// <summary>Table I's P_L, normalised (the printed column sums to 0.993).</summary>
    public static int SampleTableI(double u)
    {
        double total = 0;
        foreach (var (_, p) in TableI) total += p;
        double acc = 0;
        foreach (var (l, p) in TableI)
        {
            acc += p / total;
            if (u < acc) return l;
        }
        return TableI[^1].L;
    }

    /// <summary>Clamped copy of the settings (the same limits the UI and CLI use).</summary>
    public static SoneiraPeeblesSettings Sanitise(SoneiraPeeblesSettings s) => s with
    {
        BoxSize = double.IsFinite(s.BoxSize) ? Math.Clamp(s.BoxSize, 1.0, 1e5) : 200.0,
        Clumps = Math.Clamp(s.Clumps, 1, 10_000_000),
        Eta = s.Placement == CosmicWebPlacement.Dumbbell ? 2 : Math.Clamp(s.Eta, 2, 64),
        Lambda = double.IsFinite(s.Lambda) ? Math.Clamp(s.Lambda, 1.01, 100.0) : PaperLambda,
        FirstRatio = double.IsFinite(s.FirstRatio) ? Math.Clamp(s.FirstRatio, 0.5, 100.0) : PaperFirstRatio,
        TopSize = double.IsFinite(s.TopSize) ? Math.Clamp(s.TopSize, 1e-3, 1e5) : PaperD1,
        Levels = Math.Clamp(s.Levels, 1, 24),
        PointCap = Math.Clamp(s.PointCap, 1, MaxPointCap),
    };

    /// <summary>Lays one clump down about the origin into <paramref name="xyz"/> (3 eta^L doubles).</summary>
    public static void BuildClump(ref SplitMix64 rng, CosmicWebPlacement placement, int eta, int levels,
        double top, double firstRatio, double lambda, Span<double> xyz)
    {
        // Breadth-first: after level k there are eta^k centres; each splits into eta at level k+1.
        int count = 1;
        xyz[0] = xyz[1] = xyz[2] = 0;
        var tmp = new double[xyz.Length];
        for (int level = 1; level <= levels; level++)
        {
            double s = SizeAt(level, top, firstRatio, lambda);
            int next = 0;
            for (int c = 0; c < count; c++)
            {
                double cx = xyz[3 * c], cy = xyz[3 * c + 1], cz = xyz[3 * c + 2];
                if (placement == CosmicWebPlacement.Dumbbell)
                {
                    // Two centres at fixed separation s, random orientation, centred on the parent.
                    var (ux, uy, uz) = rng.UnitVector();
                    double h = 0.5 * s;
                    tmp[3 * next] = cx + h * ux; tmp[3 * next + 1] = cy + h * uy; tmp[3 * next + 2] = cz + h * uz; next++;
                    tmp[3 * next] = cx - h * ux; tmp[3 * next + 1] = cy - h * uy; tmp[3 * next + 2] = cz - h * uz; next++;
                }
                else
                {
                    // eta centres uniformly at random in the sphere of radius s.
                    for (int k = 0; k < eta; k++)
                    {
                        var (ux, uy, uz) = rng.UnitVector();
                        double r = s * Math.Cbrt(rng.NextDouble());
                        tmp[3 * next] = cx + r * ux; tmp[3 * next + 1] = cy + r * uy; tmp[3 * next + 2] = cz + r * uz; next++;
                    }
                }
            }
            tmp.AsSpan(0, 3 * next).CopyTo(xyz);
            count = next;
        }
    }

    /// <summary>Generates the point set: clumps at uniform random centres in the periodic box.</summary>
    public static CosmicWebPoints Generate(SoneiraPeeblesSettings settings, CancellationToken ct = default)
    {
        var s = Sanitise(settings);
        int eta = s.Eta;
        double box = s.BoxSize;

        // Pass 1 (sequential, one stream): each clump's centre and level count, then the prefix
        // that fits the budget.
        var head = new SplitMix64((ulong)(uint)s.Seed * 0x9E3779B97F4A7C15UL ^ 0x2021UL);
        var centres = new double[3 * s.Clumps];
        var levels = new int[s.Clumps];
        var offsets = new long[s.Clumps + 1];
        int used = 0;
        bool capped = false;
        for (int i = 0; i < s.Clumps; i++)
        {
            centres[3 * i] = head.NextDouble() * box;
            centres[3 * i + 1] = head.NextDouble() * box;
            centres[3 * i + 2] = head.NextDouble() * box;
            int l = s.LevelMode == CosmicWebLevelMode.Fixed ? s.Levels : SampleTableI(head.NextDouble());
            long n = ClumpPoints(eta, l);
            if (n > MaxClumpPoints || offsets[used] + n > s.PointCap) { capped = true; break; }
            levels[i] = l;
            offsets[used + 1] = offsets[used] + n;
            used++;
        }
        int total = (int)offsets[used];
        var xyz = new float[3 * total];

        // Pass 2 (parallel): each clump from its own stream, so the result is thread-count independent.
        Parallel.For(0, used, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var rng = new SplitMix64(SplitMix64.Mix((ulong)(uint)s.Seed, (ulong)i));
            int l = levels[i];
            int n = (int)(offsets[i + 1] - offsets[i]);
            var local = new double[3 * n];
            BuildClump(ref rng, s.Placement, eta, l, s.TopSize, s.FirstRatio, s.Lambda, local);
            long o = 3 * offsets[i];
            double cx = centres[3 * i], cy = centres[3 * i + 1], cz = centres[3 * i + 2];
            for (int k = 0; k < n; k++)
            {
                xyz[o + 3 * k] = (float)Wrap(cx + local[3 * k], box);
                xyz[o + 3 * k + 1] = (float)Wrap(cy + local[3 * k + 1], box);
                xyz[o + 3 * k + 2] = (float)Wrap(cz + local[3 * k + 2], box);
            }
        });

        return new CosmicWebPoints
        {
            Xyz = xyz, Count = total, BoxSize = box,
            ClumpsRequested = s.Clumps, ClumpsUsed = used, Capped = capped,
        };
    }

    /// <summary>Periodic wrap into [0, box).</summary>
    public static double Wrap(double v, double box)
    {
        double r = v % box;
        if (r < 0) r += box;
        return r >= box ? 0 : r;
    }
}

/// <summary>#2021 — SplitMix64 (Steele, Lea &amp; Flood, "Fast splittable pseudorandom number generators",
/// OOPSLA 2014): a small, fast, seedable generator; one stream per clump keeps generation deterministic.</summary>
public struct SplitMix64
{
    private ulong _state;
    public SplitMix64(ulong seed) => _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A uniformly distributed unit vector: z uniform in [-1, 1] and the azimuth uniform (the sphere's
    /// area between two heights is proportional to their difference).</summary>
    public (double X, double Y, double Z) UnitVector()
    {
        double z = 2.0 * NextDouble() - 1.0;
        double a = 2.0 * Math.PI * NextDouble();
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
        return (r * Math.Cos(a), r * Math.Sin(a), z);
    }

    /// <summary>A seed for stream <paramref name="b"/> of seed <paramref name="a"/>.</summary>
    public static ulong Mix(ulong a, ulong b)
    {
        var m = new SplitMix64(a * 0xD1B54A32D192ED03UL + b * 0x9E3779B97F4A7C15UL + 0x5EED);
        m.Next();
        return m.Next();
    }
}
