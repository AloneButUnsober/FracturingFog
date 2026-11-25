// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2023 — Cosmic Web S3: the fractal dimension function D(r) = 3 + d log(1 + xi) / d log r
// (Einasto 2026, arXiv:2608.04694, Eqs. 2-4). Independent checks:
//   • (a) Poisson: uniform random points give D(r) = 3 within the noise the pair counts predict;
//   • (b) Soneira-Peebles: the hierarchical points give D(r) near the model's log eta / log lambda where
//     the clump pairs dominate (xi >> 1, so g = 1 + xi is close to xi), for two models of different D;
//   • (c) the cell-list pair counter equals a brute-force O(N^2) count (written separately here);
//   • the slope fit recovers an exact power law;
//   • the measurement does not change the render; the inset does; settings survive the command line.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.CosmicWeb;
using FracturingFog.Help;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2023FractalDimensionTests
{
    private readonly ITestOutputHelper _out;
    public S2023FractalDimensionTests(ITestOutputHelper output) => _out = output;

    private static CosmicWebPoints Uniform(int n, double box, ulong seed)
    {
        var rng = new SplitMix64(seed);
        var xyz = new float[3 * n];
        for (int i = 0; i < xyz.Length; i++) xyz[i] = (float)(rng.NextDouble() * box);
        return new CosmicWebPoints { Xyz = xyz, Count = n, BoxSize = box };
    }

    private void Dump(string name, CosmicWebDimension d)
    {
        for (int k = 0; k < d.R.Length; k++)
            _out.WriteLine($"{name} r={d.R[k]:0.####} DD={d.Pairs[k]} xi={d.Xi[k]:0.####} D={d.D[k]:0.###}");
    }

    // ── (a) Poisson ──

    [Fact]
    public void Poisson_Points_Have_D_Three_Within_The_Counting_Noise()
    {
        var pts = Uniform(20_000, 100, 7);
        var s = CosmicWebDimensionSettings.Default(20_000);
        var d = FractalDimension.Measure(pts, s, TestContext.Current.CancellationToken);
        Dump("poisson", d);
        int checkedBins = 0;
        double sum = 0;
        for (int k = 0; k < d.R.Length; k++)
        {
            if (!double.IsFinite(d.D[k])) continue;
            // Predicted standard error of the slope: each bin's log g has sigma 1 / sqrt(DD) (Poisson counts),
            // propagated through the least-squares slope over the window.
            double xm = 0;
            for (int j = k - s.Window; j <= k + s.Window; j++) xm += Math.Log(d.R[j]);
            xm /= 2 * s.Window + 1;
            double sxx = 0, var = 0;
            for (int j = k - s.Window; j <= k + s.Window; j++)
            {
                double dx = Math.Log(d.R[j]) - xm;
                sxx += dx * dx;
                var += dx * dx / d.Pairs[j];
            }
            double sigma = Math.Sqrt(var) / sxx;
            Assert.True(Math.Abs(d.D[k] - 3) < 4 * sigma + 1e-9, $"r={d.R[k]:0.###}: D={d.D[k]:0.###}, sigma={sigma:0.###}");
            if (d.R[k] >= 1) { sum += d.D[k]; checkedBins++; }
        }
        Assert.True(checkedBins >= 12, $"only {checkedBins} bins above r = 1");
        Assert.InRange(sum / checkedBins, 2.97, 3.03);
        // The large scales are measured tightly.
        Assert.InRange(d.DAt(10), 2.95, 3.05);
    }

    // ── (b) Soneira-Peebles ──

    [Theory]
    [InlineData(CosmicWebPlacement.Dumbbell, 2, 1.76, 1.10, 11.35, 12)]
    [InlineData(CosmicWebPlacement.UniformSphere, 4, 2.0, 2.0, 12.0, 8)]
    public void Soneira_Peebles_Points_Have_The_Model_Dimension_Over_The_Scaling_Range(
        CosmicWebPlacement placement, int eta, double lambda, double first, double top, int levels)
    {
        var p = new FractalParameters
        {
            CosmicWebPlacement = placement, CosmicWebEta = eta, CosmicWebLambda = lambda, CosmicWebFirstRatio = first,
            CosmicWebTopSize = top, CosmicWebLevelMode = CosmicWebLevelMode.Fixed, CosmicWebLevels = levels,
            CosmicWebClumps = 310, CosmicWebBoxSize = 100, CosmicWebSeed = 1978,
        };
        var pts = CosmicWebCalculator.PointsFor(p, TestContext.Current.CancellationToken);
        var d = FractalDimension.Measure(pts, CosmicWebDimensionSettings.Default(30_000), TestContext.Current.CancellationToken);
        Dump(placement.ToString(), d);
        double model = SoneiraPeebles.AnalyticDimension(eta, lambda);
        // The model's D is the slope of xi; D(r) = 3 + d log(1 + xi) / d log r follows it only while xi >> 1.
        // Where xi falls towards 1 the uncorrelated pairs between clumps take over and D(r) climbs to 3.
        var inRange = Enumerable.Range(0, d.R.Length).Where(k => d.Xi[k] > 20 && double.IsFinite(d.D[k])).ToArray();
        Assert.True(inRange.Length >= 5, $"{inRange.Length} bins in range");
        double mean = inRange.Average(k => d.D[k]);
        _out.WriteLine($"model D {model:0.###}, mean measured {mean:0.###}");
        Assert.InRange(mean, model - 0.1, model + 0.1);
        Assert.All(inRange, k => Assert.InRange(d.D[k], model - 0.25, model + 0.25));
        // Past the top clump size the pairs between clumps (uncorrelated) take over and D(r) climbs towards 3.
        int last = Array.FindLastIndex(d.D, double.IsFinite);
        Assert.True(d.D[last] > model + 0.3, $"r={d.R[last]:0.###}: D={d.D[last]:0.###}");
    }

    // ── (c) Estimator ──

    [Fact]
    public void The_Cell_List_Counts_Equal_A_Brute_Force_Count()
    {
        var p = new FractalParameters { CosmicWebClumps = 20, CosmicWebBoxSize = 60, CosmicWebSeed = 5 };
        var pts = CosmicWebCalculator.PointsFor(p, TestContext.Current.CancellationToken);
        foreach (int sample in new[] { 1500, 2500 })
        {
            var idx = FractalDimension.SampleIndices(pts.Count, sample, 3);
            Assert.Equal(Math.Min(sample, pts.Count), idx.Length);
            Assert.Equal(idx.Length, idx.Distinct().Count());
            foreach (double frac in new[] { 0.25, 0.1, 0.33 })
            {
                double rMin = 0.06, rMax = 60 * frac;
                var cell = FractalDimension.CountPairs(pts.Xyz, idx, 60, rMin, rMax, 30, TestContext.Current.CancellationToken);
                var brute = FractalDimension.CountPairsBrute(pts.Xyz, idx, 60, rMin, rMax, 30);
                Assert.Equal(brute, cell);
                Assert.True(brute.Sum() > 1000);
            }
        }
        // xi from the counts is the natural estimator: zero when DD equals the analytic RR.
        var e = FractalDimension.Edges(1, 10, 4);
        int m = 1000;
        var dd = new long[4];
        double rr0 = 0.5 * m * (m - 1.0) * 4.0 / 3.0 * Math.PI * (Math.Pow(e[1], 3) - 1) / 1e6;
        dd[0] = (long)Math.Round(rr0 * 3);
        Assert.Equal(dd[0] / rr0 - 1, FractalDimension.Xi(dd, m, 100, e)[0], 12);
    }

    [Fact]
    public void The_Slope_Fit_Recovers_An_Exact_Power_Law()
    {
        var e = FractalDimension.Edges(0.1, 10, 20);
        var r = Enumerable.Range(0, 20).Select(k => Math.Sqrt(e[k] * e[k + 1])).ToArray();
        var g = r.Select(x => 4.0 * Math.Pow(x, -1.7)).ToArray();
        var dd = Enumerable.Repeat(1000L, 20).ToArray();
        var gamma = FractalDimension.Gamma(r, g, dd, 2, 10);
        for (int k = 0; k < 20; k++)
            if (k < 2 || k > 17) Assert.True(double.IsNaN(gamma[k]));
            else Assert.Equal(-1.7, gamma[k], 10);
        dd[10] = 3;   // too few pairs: every window touching bin 10 has no value
        gamma = FractalDimension.Gamma(r, g, dd, 2, 10);
        for (int k = 8; k <= 12; k++) Assert.True(double.IsNaN(gamma[k]));
        Assert.False(double.IsNaN(gamma[7]));
    }

    // ── Calculator, status, overlay, batch, help ──

    private static CosmicWebCalculator Render(FractalParameters p)
    {
        var c = new CosmicWebCalculator(540, 340) { ColorMap = new HsvPalette(), FractalParameters = p };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Measuring_Reports_D_And_Only_The_Inset_Changes_The_Frame()
    {
        var p = new FractalParameters { CosmicWebClumps = 60, CosmicWebSeed = 4, CosmicWebMeasureSample = 5000 };
        var off = Render(p);
        Assert.Null(off.LastDimension);
        Assert.DoesNotContain("D(r)", off.LastPrecisionLabel);

        p.CosmicWebMeasure = true;
        p.CosmicWebDimensionOverlay = false;
        var bare = Render(p);
        Assert.NotNull(bare.LastDimension);
        Assert.Contains("; D(r) from 5,000 of ", bare.LastPrecisionLabel);
        Assert.Contains(" 1 Mpc/h ", bare.LastPrecisionLabel);
        Assert.True(off.ColorBuffer.SequenceEqual(bare.ColorBuffer), "the measurement alone changed the render");

        p.CosmicWebDimensionOverlay = true;
        var inset = Render(p);
        Assert.False(off.ColorBuffer.SequenceEqual(inset.ColorBuffer), "no inset drawn");
        // Cached per point set (#2025: by value, since a test running in parallel may evict the shared one-entry
        // point cache and regenerate an equal point set).
        if (ReferenceEquals(bare.LastPoints, inset.LastPoints)) Assert.Same(bare.LastDimension, inset.LastDimension);
        Assert.Equal(bare.LastDimension!.D, inset.LastDimension!.D);
        Assert.Contains(inset.ColorBuffer, c => (c & 0xFFFFFF) == (CosmicWebCalculator.DimensionCurveColor & 0xFFFFFF));
    }

    [Fact]
    public void Zeldovich_Web_Is_Below_Three_And_Climbs_On_Large_Scales()
    {
        var p = new FractalParameters
        {
            CosmicWebGenerator = CosmicWebGenerator.Zeldovich, CosmicWebGrid = 64, CosmicWebSpectralIndex = -2,
            CosmicWebSmoothing = 2, CosmicWebGrowth = 1.5, CosmicWebSeed = 1970, CosmicWebBoxSize = 100,
        };
        var pts = CosmicWebCalculator.PointsFor(p, TestContext.Current.CancellationToken);
        var d = FractalDimension.Measure(pts, CosmicWebDimensionSettings.Default(30_000), TestContext.Current.CancellationToken);
        Dump("zeldovich", d);
        Assert.True(d.DAt(1) < 2.8, $"D(1) = {d.DAt(1):0.###}");
        // With n = -2 much of the power is on the largest scales, so a 100 Mpc/h box does not reach 3 by
        // r = box / 4; D(r) still climbs past its minimum.
        Assert.True(d.DAt(15) > d.DAt(6) + 0.1, $"D(6) = {d.DAt(6):0.###}, D(15) = {d.DAt(15):0.###}");
        Assert.All(d.D.Where(double.IsFinite), v => Assert.InRange(v, 1.5, 3.4));
    }

    [Fact]
    public void Measurement_Settings_Survive_The_Command_Line()
    {
        var p = new FractalParameters { CosmicWebMeasure = true, CosmicWebMeasureSample = 12_345, CosmicWebDimensionOverlay = false };
        var snap = RegionFractalParams.Snapshot(FractalType.CosmicWeb, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.CosmicWeb, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("CosmicWeb", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.CosmicWeb, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.True(q.CosmicWebMeasure);
        Assert.Equal(12_345, q.CosmicWebMeasureSample);
        Assert.False(q.CosmicWebDimensionOverlay);
    }

    [Fact]
    public void Help_Explains_D_Of_r_And_Names_The_Source()
    {
        string help = HelpTextBundle.MathCosmicWebText.Replace("\r\n", " ").Replace("\n", " ");
        Assert.Contains("D(r) = 3 + gamma(r)", help);
        Assert.Contains("Einasto", help);
        Assert.Contains("natural estimator", help);
        Assert.Contains("3 Mpc/h", help);
    }
}
