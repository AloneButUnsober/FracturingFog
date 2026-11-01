// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/GaussianPrimeTrap.cs
//
// Gaussian Prime Trap (#1487, tracker #1485): an orbit trap against the Gaussian
// primes, the integer points a+bi that are prime in Z[i]. It builds on the
// Gaussian-integer trap of Barrallo & Jones, "Coloring Algorithms for Dynamical
// Systems in the Complex Plane" (also Ultra Fractal's Gaussian Integer colouring;
// Docs/Resources-Bibliography.md), thinning that lattice to its primes. The orbit is scaled by
// LatticeScale before measuring, and the colour is exp(−3·d) of the closest
// approach d, so each near pass makes a bright dome (the #1485 mock's panel 5).
//
// a+bi is a Gaussian prime iff (a ≠ 0, b ≠ 0 and a² + b² is a rational prime) or
// (one of a, b is 0 and the other's |value| is a rational prime ≡ 3 mod 4).
//
// Tail window, chosen by measurement (#1487): a minimum over the WHOLE orbit goes
// flat past shallow zoom, because every pixel in a deep view shares the same early
// orbit, which then sets the minimum. Over the full set, seahorse valley (zoom ~270)
// and a zoom-1e6 view, the std of the trap value over escaped pixels was
// 0.22 / 0.05 / 0.08 for the whole orbit and 0.21 / 0.17 / 0.17 for the last 10
// iterations before escape. Scaling the lattice with zoom did as well, but a theme
// sees only pixel size, so its pattern would change with window resolution. The
// window is two blocks of TailBlock samples (the current and previous block), so it
// covers the last TailBlock..2·TailBlock−1 samples and fits in three accumulator
// floats. Past ~1e4 zoom on filament-heavy views the tail itself is chaotic from
// pixel to pixel, and the theme reads as speckle.
//
// Only samples with |Re z|, |Im z| ≤ 2 are trapped (the escape steps beyond are
// skipped), so the scaled point stays inside a fixed precomputed prime grid.
//
// In-set pixels are coloured from the last samples of their full iteration budget,
// i.e. how close the attracting cycle passes to a Gaussian prime.
//
// acc.TrapMin always holds the window minimum, so the orbit-trap relief height
// source (TrapBuffer, #592) gets the same domes.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    public sealed class GaussianPrimeTrapMap : GradientColorMap, IOrbitAwareColorMap
    {
        public static string Name => "Gaussian Prime Trap";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit trap against the Gaussian primes (lattice scale 6, last 10-19 iterations before escape): " +
            "bright domes where the orbit passes near one. Colours the interior too.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap | ColorMapFeatures.GradientBased;
        /// <summary>Past this the escape tail is chaotic from pixel to pixel (speckle).</summary>
        public static double MaxRecommendedZoom => 1e4;

        /// <summary>The orbit is scaled by this before measuring to the lattice.</summary>
        public const double LatticeScale = 6.0;
        /// <summary>Window block length; the window is the last TailBlock..2·TailBlock−1 samples.</summary>
        public const int TailBlock = 10;
        /// <summary>Only orbit points with |Re z|, |Im z| ≤ this are trapped.</summary>
        public const double SampleBox = 2.0;
        /// <summary>Brightness falloff: t = exp(−Falloff·d).</summary>
        public const double Falloff = 3.0;

        // Gaussian-prime grid over [−Half, Half]². Scaled samples round to at most
        // LatticeScale·SampleBox = 12 and the search reaches ±Reach beyond.
        private const int Half = 16, Reach = 3;
        private static readonly bool[] Grid = BuildGrid();

        private static bool[] BuildGrid()
        {
            int side = 2 * Half + 1, maxNorm = 2 * Half * Half;
            var prime = new bool[maxNorm + 1];
            for (int i = 2; i <= maxNorm; i++) prime[i] = true;
            for (int i = 2; i * i <= maxNorm; i++)
                if (prime[i]) for (int j = i * i; j <= maxNorm; j += i) prime[j] = false;
            var g = new bool[side * side];
            for (int a = -Half; a <= Half; a++)
                for (int b = -Half; b <= Half; b++)
                {
                    int aa = Math.Abs(a), ab = Math.Abs(b);
                    g[(a + Half) * side + (b + Half)] = aa != 0 && ab != 0
                        ? prime[aa * aa + ab * ab]
                        : (aa | ab) != 0 && prime[aa + ab] && (aa + ab) % 4 == 3;
                }
            return g;
        }

        /// <summary>True when a+bi is a Gaussian prime (|a|, |b| ≤ 16).</summary>
        public static bool IsGaussianPrime(int a, int b)
            => Math.Abs(a) <= Half && Math.Abs(b) <= Half && Grid[(a + Half) * (2 * Half + 1) + (b + Half)];

        // Per rounded cell (|a|, |b| ≤ Cells), the primes that can be nearest to some
        // point of the cell's unit square: of the primes within ±Reach, those whose
        // distance to the square is at most U, the smallest farthest-corner distance
        // of any of them (every point of the square has a prime within U). This
        // keeps the search exact while checking ~6 primes instead of 49 cells.
        private const int Cells = 13;
        private static readonly (int[] Start, int[] A, int[] B) Candidates = BuildCandidates();

        private static (int[], int[], int[]) BuildCandidates()
        {
            int side = 2 * Cells + 1;
            var start = new int[side * side + 1];
            var la = new System.Collections.Generic.List<int>();
            var lb = new System.Collections.Generic.List<int>();
            for (int ra = -Cells; ra <= Cells; ra++)
                for (int rb = -Cells; rb <= Cells; rb++)
                {
                    start[(ra + Cells) * side + (rb + Cells)] = la.Count;
                    double u = double.MaxValue;
                    for (int a = ra - Reach; a <= ra + Reach; a++)
                        for (int b = rb - Reach; b <= rb + Reach; b++)
                            if (IsGaussianPrime(a, b))
                                u = Math.Min(u, Math.Sqrt(Sq(Math.Abs(a - ra) + 0.5) + Sq(Math.Abs(b - rb) + 0.5)));
                    for (int a = ra - Reach; a <= ra + Reach; a++)
                        for (int b = rb - Reach; b <= rb + Reach; b++)
                            if (IsGaussianPrime(a, b)
                                && Math.Sqrt(Sq(Math.Max(0, Math.Abs(a - ra) - 0.5)) + Sq(Math.Max(0, Math.Abs(b - rb) - 0.5))) <= u)
                            { la.Add(a); lb.Add(b); }
                }
            start[side * side] = la.Count;
            return (start, la.ToArray(), lb.ToArray());
        }

        private static double Sq(double v) => v * v;

        /// <summary>Distance from (wr, wi) to the nearest Gaussian prime. Exact for
        /// |wr|, |wi| ≤ 12.5: every such point has a Gaussian prime within 2.5 (the
        /// worst gap is √5, checked by the tests), which the ±3 search around the
        /// rounded point reaches; the per-cell candidate lists prune that search
        /// without changing its result.</summary>
        public static double NearestDistance(double wr, double wi)
        {
            int ra = (int)Math.Round(wr), rb = (int)Math.Round(wi);
            double best2 = double.MaxValue;
            if (Math.Abs(ra) <= Cells && Math.Abs(rb) <= Cells)
            {
                var (start, ca, cb) = Candidates;
                int cell = (ra + Cells) * (2 * Cells + 1) + (rb + Cells);
                for (int k = start[cell], end = start[cell + 1]; k < end; k++)
                {
                    double da = wr - ca[k], db = wi - cb[k], d2 = da * da + db * db;
                    if (d2 < best2) best2 = d2;
                }
                return Math.Sqrt(best2);
            }
            for (int a = ra - Reach; a <= ra + Reach; a++)
                for (int b = rb - Reach; b <= rb + Reach; b++)
                {
                    if (!IsGaussianPrime(a, b)) continue;
                    double da = wr - a, db = wi - b, d2 = da * da + db * db;
                    if (d2 < best2) best2 = d2;
                }
            return Math.Sqrt(best2);
        }

        public GaussianPrimeTrapMap()
        {
            // Navy → blue → sky → gold → cream: lightness and blue-yellow only.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(10, 14, 30)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(40, 80, 160)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(90, 170, 220)));
            Stops.Add(new ColorStop(0.85f, Color.FromArgb(255, 204, 0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 230)));
        }

        public bool WantsInteriorColor => true;

        // TrapMin2 = current block min, TrapMin3 = previous block min,
        // TrapMin = min of the two (the window minimum, kept current every sample).
        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = acc.TrapMin2 = acc.TrapMin3 = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc, double zr, double zi, double cr, double ci, int iter)
        {
            if (iter > 0 && iter % TailBlock == 0)
            {
                acc.TrapMin3 = acc.TrapMin2;
                acc.TrapMin2 = float.MaxValue;
            }
            if (Math.Abs(zr) <= SampleBox && Math.Abs(zi) <= SampleBox)
            {
                float d = (float)NearestDistance(zr * LatticeScale, zi * LatticeScale);
                if (d < acc.TrapMin2) acc.TrapMin2 = d;
            }
            acc.TrapMin = Math.Min(acc.TrapMin2, acc.TrapMin3);
        }

        /// <summary>Gradient position for a window minimum (0 when nothing was trapped).</summary>
        public static float T(float trapMin)
            => trapMin == float.MaxValue ? 0f : (float)Math.Exp(-Falloff * trapMin);

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
            => MapNormalized(T(acc.TrapMin), distance);

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? smooth / maxIterations : 0f;
            return MapNormalized(Math.Clamp(t, 0f, 1f), distance);
        }
    }
}
