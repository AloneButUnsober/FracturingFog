// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1487 — Gaussian Prime Trap. The oracle rebuilds each escaped pixel's orbit on
// its own, picks the documented tail window, and measures to the nearest Gaussian
// prime by brute force over a list built from an independent primality test. The
// rendered TrapBuffer must agree pixel for pixel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class GaussianPrimeTrapThemeTests
{
    private const int W = 160, H = 120;

    private static bool IsPrime(long n)
    {
        if (n < 2) return false;
        for (long d = 2; d * d <= n; d++) if (n % d == 0) return false;
        return true;
    }

    // Gaussian primes in [-20, 20]², from the textbook characterisation.
    private static readonly (int a, int b)[] Primes = BuildPrimes();
    private static (int, int)[] BuildPrimes()
    {
        var list = new List<(int, int)>();
        for (int a = -20; a <= 20; a++)
            for (int b = -20; b <= 20; b++)
            {
                long aa = Math.Abs(a), ab = Math.Abs(b);
                bool p = aa != 0 && ab != 0 ? IsPrime(aa * aa + ab * ab)
                       : aa + ab != 0 && IsPrime(aa + ab) && (aa + ab) % 4 == 3;
                if (p) list.Add((a, b));
            }
        return list.ToArray();
    }

    private static double BruteNearest(double wr, double wi)
        => Primes.Min(p => Math.Sqrt((wr - p.a) * (wr - p.a) + (wi - p.b) * (wi - p.b)));

    [Fact]
    public void Grid_MatchesTheIndependentList()
    {
        var set = new HashSet<(int, int)>(Primes);
        for (int a = -16; a <= 16; a++)
            for (int b = -16; b <= 16; b++)
                Assert.True(GaussianPrimeTrapMap.IsGaussianPrime(a, b) == set.Contains((a, b)), $"{a}+{b}i");
        Assert.True(GaussianPrimeTrapMap.IsGaussianPrime(1, 1));    // norm 2
        Assert.True(GaussianPrimeTrapMap.IsGaussianPrime(0, 3));    // 3 ≡ 3 mod 4 on the axis
        Assert.False(GaussianPrimeTrapMap.IsGaussianPrime(5, 0));   // 5 = (2+i)(2−i)
        Assert.False(GaussianPrimeTrapMap.IsGaussianPrime(2, 0));   // 2 = −i(1+i)²
    }

    // The ±3 search is exact only if every point of the sampled box has a prime
    // within 2.5 (a ±2 search would need 1.5, which fails: the worst gap is √5);
    // check that precondition, then the search itself.
    [Fact]
    public void NearestDistance_IsExactOverTheSampledBox()
    {
        double worst = 0;
        var rng = new Random(1487);
        for (double wr = -12.5; wr <= 12.5; wr += 0.1)
            for (double wi = -12.5; wi <= 12.5; wi += 0.1)
            {
                double brute = BruteNearest(wr, wi);
                worst = Math.Max(worst, brute);
                Assert.Equal(brute, GaussianPrimeTrapMap.NearestDistance(wr, wi), 12);
            }
        Assert.True(worst <= 2.5, $"a point has no Gaussian prime within 2.5 (worst {worst:F3})");
        for (int i = 0; i < 20_000; i++)
        {
            double wr = rng.NextDouble() * 25 - 12.5, wi = rng.NextDouble() * 25 - 12.5;
            Assert.Equal(BruteNearest(wr, wi), GaussianPrimeTrapMap.NearestDistance(wr, wi), 12);
        }
    }

    // Window minimum of the samples the calculator hands Sample (z_k, k = 1..n−1).
    private static double OracleTrap(double cr, double ci, int n)
    {
        int last = n - 1, block = GaussianPrimeTrapMap.TailBlock;
        int from = Math.Max(1, (last / block - 1) * block);
        double zr = 0, zi = 0, best = double.MaxValue;
        for (int k = 1; k <= last; k++)
        {
            double t = zr * zr - zi * zi + cr; zi = 2 * zr * zi + ci; zr = t;
            if (k < from || Math.Abs(zr) > 2 || Math.Abs(zi) > 2) continue;
            best = Math.Min(best, BruteNearest(zr * 6, zi * 6));
        }
        return best;
    }

    private static MandelbrotCalculator Render(double cx, double cy, double zoom, int maxIter)
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter,
            ColorMap = new GaussianPrimeTrapMap(),
        };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Theory]
    [InlineData(-0.6, 0.0, 1.0, 300)]
    [InlineData(-0.7453, 0.1127, 267.0, 800)]
    public void TrapBuffer_MatchesTheOracle_ForEveryEscapedPixel(double cx, double cy, double zoom, int maxIter)
    {
        var calc = Render(cx, cy, zoom, maxIter);
        double scale = (3.5 / Math.Max(W, H)) / zoom;
        int checkedPx = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, n = calc.IterationBuffer[i];
                if (n >= maxIter) continue;
                double want = OracleTrap(cx + (x - W * 0.5) * scale, cy + (y - H * 0.5) * scale, n);
                float got = calc.TrapBuffer[i];
                if (want == double.MaxValue) Assert.Equal(0f, got);   // nothing trapped
                else Assert.True(Math.Abs(got - want) < 1e-5, $"({x},{y}) n={n}: trap {got} want {want}");
                checkedPx++;
            }
        Assert.True(checkedPx > W * H / 4, $"only {checkedPx} escaped pixels");
    }

    // The design choice (#1487): the tail window keeps contrast in seahorse valley,
    // where a whole-orbit minimum measured std 0.05.
    [Fact]
    public void SeahorseValley_IsNotFlat()
    {
        var calc = Render(-0.7453, 0.1127, 267.0, 800);
        var t = Enumerable.Range(0, W * H).Where(i => calc.IterationBuffer[i] < 800)
            .Select(i => (double)GaussianPrimeTrapMap.T(calc.TrapBuffer[i])).ToArray();
        double mean = t.Average(), std = Math.Sqrt(t.Average(v => (v - mean) * (v - mean)));
        Assert.True(std > 0.12, $"trap std {std:F3} over escaped pixels (whole-orbit minimum gave ~0.05)");
    }

    [Fact]
    public void Interior_IsColouredFromTheCycle()
    {
        var calc = Render(-0.6, 0.0, 1.0, 300);
        var inside = Enumerable.Range(0, W * H).Where(i => calc.IterationBuffer[i] >= 300).ToArray();
        Assert.True(inside.Length > 1000);
        var colours = new HashSet<uint>(inside.Select(i => calc.ColorBuffer[i]));
        // Not the flat in-set fill: many colours, and few pixels at the theme's InSetColor.
        Assert.True(colours.Count > 20, $"interior has {colours.Count} colours");
        uint flat = ((FracturingFog.Interefaces.IColorMap)calc.ColorMap).InSetColor;
        Assert.True(inside.Count(i => calc.ColorBuffer[i] == flat) < inside.Length / 10);
    }

    // Past 1e12 the orbit path switches to perturbation (#609); the trap still fills.
    [Fact]
    public void DeepPerturbationPath_StillTraps()
    {
        var calc = Render(-0.743643887037151, 0.131825904205330, 1e13, 3000);
        var esc = Enumerable.Range(0, W * H).Where(i => calc.IterationBuffer[i] < 3000).ToArray();
        Assert.True(esc.Length > W * H / 10, $"only {esc.Length} escaped");
        Assert.All(esc, i => Assert.True(float.IsFinite(calc.TrapBuffer[i]) && calc.TrapBuffer[i] >= 0f));
        Assert.True(esc.Select(i => calc.TrapBuffer[i]).Distinct().Count() > 50);
    }

    [Fact]
    public void IsABuiltInTheme()
    {
        Assert.Contains(ColorPalette.BuiltIns, m => m is GaussianPrimeTrapMap);
        Assert.IsType<GaussianPrimeTrapMap>(ColorPalette.GetPaletteByName("Gaussian Prime Trap"));
    }
}
