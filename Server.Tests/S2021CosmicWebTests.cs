// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2021 — Cosmic Web S1: Soneira & Peebles (AJ 83, 845, 1978) hierarchical clustering and the point
// splat. Independent checks:
//   • box-counting dimension of generated clumps matches the model's analytic D = log eta / log lambda
//     (their Eq. 4) over the scales between the smallest and largest clump sizes, for the paper's
//     dumbbell model (D = 1.226) and two uniform-sphere hierarchies (D = 2 and 1.5);
//   • the size recurrence (Eqs. 17-19) reproduces Table I's minimum clustering diameters;
//     Eq. 4's gamma for the final model is the paper's 1.77;
//   • dumbbell geometry: each pair is exactly d_n apart and centred on its parent;
//   • Table I level sampling matches P_L; the clump density default is the paper's;
//   • generation is seed-deterministic, per-clump (a clump rebuilt alone equals its slice), and the
//     point cap drops whole clumps only;
//   • splat conservation: with one-pixel splats, no fade and the whole box in view, the density sums
//     to (points) x (one weight); renders are deterministic;
//   • settings survive the snapshot and the command line; presets render the same live, from the
//     command line and as a poster; help names the sources and the scale caveat.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.CosmicWeb;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2021CosmicWebTests
{
    private static readonly string[] Presets =
    {
        "Cosmic Web - Soneira-Peebles Final Model", "Cosmic Web - Rich Clumps", "Cosmic Web - Uniform Sphere Clumps (D = 2)",
    };

    // ── The model ──

    [Fact]
    public void Eq4_Gives_The_Papers_Gamma_For_The_Final_Model()
    {
        double d = SoneiraPeebles.AnalyticDimension(2, SoneiraPeebles.PaperLambda);
        Assert.Equal(1.77, 3 - d, 2);   // "gamma = 1.77" (Sec. IVa); 3 - log 2 / log 1.76 = 1.774
    }

    [Fact]
    public void The_Size_Recurrence_Reproduces_Table_I_Minimum_Diameters()
    {
        // Table I column 3 is d_L in kpc/h; the recurrence d1 = 11.35, d1/d2 = 1.10, d_n/d_(n+1) = 1.76
        // gives each within 1.2 kpc (the printed values are rounded, and so are lambda and d1).
        foreach (var (l, kpc) in SoneiraPeebles.TableIMinimumDiameter)
        {
            double d = 1000 * SoneiraPeebles.SizeAt(l, SoneiraPeebles.PaperD1, SoneiraPeebles.PaperFirstRatio, SoneiraPeebles.PaperLambda);
            Assert.True(Math.Abs(d - kpc) <= 1.2, $"L = {l}: {d:0.0} kpc vs Table I {kpc}");
        }
        // Table I column 2: N_L = 2^L.
        foreach (var (l, _) in SoneiraPeebles.TableI) Assert.Equal(1L << l, SoneiraPeebles.ClumpPoints(2, l));
    }

    [Fact]
    public void Dumbbell_Pairs_Are_Exactly_Their_Separation_Apart_And_Centred_On_The_Parent()
    {
        const int levels = 5;
        var rng = new SplitMix64(99);
        var pts = new double[3 << levels];
        SoneiraPeebles.BuildClump(ref rng, CosmicWebPlacement.Dumbbell, 2, levels, 11.35, 1.10, 1.76, pts);
        // Breadth-first order: points 2k and 2k+1 are the last level's pair.
        double dl = SoneiraPeebles.SizeAt(levels, 11.35, 1.10, 1.76);
        for (int k = 0; k < (1 << levels); k += 2)
            Assert.Equal(dl, Dist(pts, k, k + 1), 9);
        // The clump's centroid is the origin (every split is symmetric about its parent).
        for (int a = 0; a < 3; a++)
        {
            double s = 0;
            for (int k = 0; k < (1 << levels); k++) s += pts[3 * k + a];
            Assert.Equal(0.0, s / (1 << levels), 9);
        }
        // One level: the top pair is d1 apart.
        var one = new double[6];
        rng = new SplitMix64(5);
        SoneiraPeebles.BuildClump(ref rng, CosmicWebPlacement.Dumbbell, 2, 1, 11.35, 1.10, 1.76, one);
        Assert.Equal(11.35, Dist(one, 0, 1), 9);
    }

    private static double Dist(double[] p, int i, int j)
    {
        double dx = p[3 * i] - p[3 * j], dy = p[3 * i + 1] - p[3 * j + 1], dz = p[3 * i + 2] - p[3 * j + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    [Theory]
    // placement, eta, lambda, levels, fine levels left out of the fit, expected D, tolerance.
    // The fit runs from level (levels - skip) up to level 2. The bottom levels are left out because a
    // finite hierarchy runs out of points there (each box holds a handful, so N(eps) flattens). Measured
    // (numpy prototype, 4 seeds): dumbbell 1.212, eta 4 / lambda 2 1.897, eta 8 / lambda 4 1.476. The
    // uniform-sphere eta 4 case reads about 5% low at this depth: its sub-spheres are placed at random
    // and overlap, which hides boxes at a finite level count.
    [InlineData(CosmicWebPlacement.Dumbbell, 2, 1.76, 17, 4, 1.2262, 0.10)]
    [InlineData(CosmicWebPlacement.UniformSphere, 4, 2.0, 9, 3, 2.0, 0.15)]
    [InlineData(CosmicWebPlacement.UniformSphere, 8, 4.0, 6, 2, 1.5, 0.10)]
    public void Box_Counting_Dimension_Matches_Eq4(CosmicWebPlacement placement, int eta, double lambda, int levels, int skip,
        double expected, double tol)
    {
        Assert.Equal(expected, SoneiraPeebles.AnalyticDimension(eta, lambda), 3);
        double first = lambda;   // a plain hierarchy: the same ratio at every level
        const double top = 10.0;
        int n = (int)SoneiraPeebles.ClumpPoints(eta, levels);
        double small = SoneiraPeebles.SizeAt(levels - skip, top, first, lambda);
        double large = SoneiraPeebles.SizeAt(2, top, first, lambda);
        var slopes = new List<double>();
        foreach (ulong seed in new ulong[] { 11, 22, 33, 44 })
        {
            var rng = new SplitMix64(seed);
            var pts = new double[3 * n];
            SoneiraPeebles.BuildClump(ref rng, placement, eta, levels, top, first, lambda, pts);
            slopes.Add(BoxCountingSlope(pts, n, small, large));
        }
        double mean = slopes.Average();
        Assert.True(Math.Abs(mean - expected) <= tol,
            $"{placement} eta {eta} lambda {lambda}: box-counting D {mean:0.###} (seeds {string.Join(", ", slopes.Select(s => s.ToString("0.###")))}) vs {expected}");
    }

    /// <summary>Least-squares slope of log N(eps) against log(1/eps), eps geometric between the bounds;
    /// N = occupied cubes of side eps (grid offset averaged over four shifts).</summary>
    private static double BoxCountingSlope(double[] pts, int n, double small, double large)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        const int steps = 10;
        for (int k = 0; k <= steps; k++)
        {
            double eps = small * Math.Pow(large / small, k / (double)steps);
            double count = 0;
            for (int shift = 0; shift < 4; shift++)
            {
                double off = 0.25 * shift * eps;
                var cells = new HashSet<(long, long, long)>();
                for (int i = 0; i < n; i++)
                    cells.Add(((long)Math.Floor((pts[3 * i] + off) / eps), (long)Math.Floor((pts[3 * i + 1] + off) / eps), (long)Math.Floor((pts[3 * i + 2] + off) / eps)));
                count += cells.Count;
            }
            xs.Add(Math.Log(1 / eps));
            ys.Add(Math.Log(count / 4));
        }
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0;
        for (int i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); }
        return sxy / sxx;
    }

    [Fact]
    public void Table_I_Levels_Are_Drawn_With_Its_Probabilities()
    {
        double total = SoneiraPeebles.TableI.Sum(t => t.P);
        var counts = new Dictionary<int, int>();
        var rng = new SplitMix64(2021);
        const int draws = 400_000;
        for (int i = 0; i < draws; i++)
        {
            int l = SoneiraPeebles.SampleTableI(rng.NextDouble());
            counts[l] = counts.GetValueOrDefault(l) + 1;
        }
        foreach (var (l, p) in SoneiraPeebles.TableI)
        {
            double expect = p / total, got = counts.GetValueOrDefault(l) / (double)draws;
            double sigma = Math.Sqrt(expect * (1 - expect) / draws);
            Assert.True(Math.Abs(got - expect) < 5 * sigma, $"L = {l}: {got:0.#####} vs {expect:0.#####}");
        }
    }

    [Fact]
    public void Default_Clump_Count_Is_The_Papers_Clump_Density()
    {
        var p = new FractalParameters();
        Assert.Equal(SoneiraPeebles.PaperClumpDensity * Math.Pow(p.CosmicWebBoxSize, 3), p.CosmicWebClumps, 0);
        Assert.Equal(SoneiraPeebles.PaperD1, p.CosmicWebTopSize);
        Assert.Equal(SoneiraPeebles.PaperFirstRatio, p.CosmicWebFirstRatio);
        Assert.Equal(SoneiraPeebles.PaperLambda, p.CosmicWebLambda);
    }

    // ── Generation ──

    private static SoneiraPeeblesSettings Small(int cap = 1_000_000) => new(
        Seed: 3, BoxSize: 50, Clumps: 30, Placement: CosmicWebPlacement.Dumbbell, Eta: 2, Lambda: 1.76, FirstRatio: 1.10,
        TopSize: 11.35, LevelMode: CosmicWebLevelMode.TableI, Levels: 9, PointCap: cap);

    [Fact]
    public void Generation_Is_Deterministic_And_Per_Clump()
    {
        var a = SoneiraPeebles.Generate(Small());
        var b = SoneiraPeebles.Generate(Small());
        Assert.Equal(a.Xyz, b.Xyz);
        Assert.All(a.Xyz, v => Assert.InRange(v, 0f, 50f));
        Assert.NotEqual(a.Xyz, SoneiraPeebles.Generate(Small() with { Seed = 4 }).Xyz);

        // Clump 0 rebuilt on its own stream (independent of the parallel loop) equals the first slice,
        // up to the periodic wrap and the float store.
        var c = SoneiraPeebles.Generate(Small() with { Clumps = 1, LevelMode = CosmicWebLevelMode.Fixed, Levels = 6 });
        var rng = new SplitMix64(SplitMix64.Mix(3, 0));
        var local = new double[3 * 64];
        SoneiraPeebles.BuildClump(ref rng, CosmicWebPlacement.Dumbbell, 2, 6, 11.35, 1.10, 1.76, local);
        var head = new SplitMix64(unchecked((ulong)(uint)3 * 0x9E3779B97F4A7C15UL) ^ 0x2021UL);
        double cx = head.NextDouble() * 50, cy = head.NextDouble() * 50, cz = head.NextDouble() * 50;
        for (int k = 0; k < 64; k++)
        {
            Assert.Equal((float)SoneiraPeebles.Wrap(cx + local[3 * k], 50), c.Xyz[3 * k]);
            Assert.Equal((float)SoneiraPeebles.Wrap(cy + local[3 * k + 1], 50), c.Xyz[3 * k + 1]);
            Assert.Equal((float)SoneiraPeebles.Wrap(cz + local[3 * k + 2], 50), c.Xyz[3 * k + 2]);
        }
    }

    [Fact]
    public void The_Point_Cap_Drops_Whole_Clumps()
    {
        var full = SoneiraPeebles.Generate(Small());
        Assert.False(full.Capped);
        int cap = full.Count / 2;
        var capped = SoneiraPeebles.Generate(Small(cap));
        Assert.True(capped.Capped);
        Assert.True(capped.Count <= cap);
        Assert.True(capped.ClumpsUsed < full.ClumpsUsed);
        // The kept clumps are the same clumps: a prefix of the full set.
        Assert.Equal(full.Xyz.Take(capped.Xyz.Length), capped.Xyz);
    }

    // ── Rendering ──

    private static CosmicWebCalculator Render(FractalParameters p, int w, int h, double zoom = 1.0)
    {
        var c = new CosmicWebCalculator(w, h) { ColorMap = new HsvPalette(), Zoom = zoom, FractalParameters = p };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Splat_Conserves_Points_When_The_Box_Is_In_View()
    {
        var p = new FractalParameters
        {
            CosmicWebBoxSize = 50, CosmicWebClumps = 30, CosmicWebSeed = 3, CosmicWebPointSize = 0.0, CosmicWebDepthFade = 0.0,
            CosmicWebCameraDistance = 6.0,
        };
        var c = Render(p, 160, 120);
        Assert.NotNull(c.LastPoints);
        long sum = c.DensityBuffer.Sum();
        Assert.Equal(c.LastPoints!.Count * CosmicWebCalculator.WeightOne, sum);
        // An independent recount: project every point with the same camera and count the in-frame ones.
        var cam = CosmicWebCalculator.CameraFor(p, 1.0, 160, 120);
        int inFrame = 0;
        for (int i = 0; i < c.LastPoints.Count; i++)
        {
            var q = cam.Project(c.LastPoints.Xyz[3 * i] / 50.0 - 0.5, c.LastPoints.Xyz[3 * i + 1] / 50.0 - 0.5, c.LastPoints.Xyz[3 * i + 2] / 50.0 - 0.5);
            if (q is { } v && v.X >= 0 && v.X < 160 && v.Y >= 0 && v.Y < 120) inFrame++;
        }
        Assert.Equal(c.LastPoints.Count, inFrame);
    }

    [Fact]
    public void Renders_Are_Deterministic_And_Not_Flat()
    {
        var p = new FractalParameters { CosmicWebClumps = 200, CosmicWebPointSize = 1.5 };
        var a = Render(p, 64, 40).ColorBuffer.ToArray();
        var b = Render(p, 64, 40).ColorBuffer.ToArray();
        Assert.Equal(a, b);
        Assert.True(a.Distinct().Count() > 5);
        p.CosmicWebCameraTheta += 0.4;
        Assert.NotEqual(a, Render(p, 64, 40).ColorBuffer);
    }

    [Fact]
    public void Depth_Colouring_Is_Front_To_Back_And_Density_Mode_Differs()
    {
        // Clumps fill the box, so the frame's weighted mean depth sits well inside (0, 1) (front to back);
        // the Density mode colours the same splat differently.
        var p = new FractalParameters { CosmicWebClumps = 120, CosmicWebPointSize = 0.0 };
        var depth = Render(p, 64, 40);
        long v = 0, dv = 0;
        for (int i = 0; i < depth.DensityBuffer.Length; i++) { v += depth.DensityBuffer[i]; dv += depth.DepthSumBuffer[i]; }
        double meanT = (double)dv / ((double)v * CosmicWebCalculator.DepthOne);
        Assert.InRange(meanT, 0.15, 0.85);   // points spread through the box
        p.CosmicWebColour = CosmicWebColour.Density;
        Assert.NotEqual(depth.ColorBuffer, Render(p, 64, 40).ColorBuffer);
    }

    [Fact]
    public void The_Status_Label_Reports_The_Cap_And_The_Model_D()
    {
        var p = new FractalParameters { CosmicWebClumps = 50, CosmicWebPointCap = 500 };
        var c = Render(p, 32, 20);
        Assert.Contains("point cap 500 reached", c.LastPrecisionLabel);
        Assert.Contains("D = log 2 / log 1.76 = 1.226", c.LastPrecisionLabel);
    }

    // ── Batch, presets, help ──

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.CosmicWeb, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
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
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            CosmicWebSeed = 42, CosmicWebBoxSize = 123.5, CosmicWebClumps = 77, CosmicWebPlacement = CosmicWebPlacement.UniformSphere,
            CosmicWebEta = 5, CosmicWebLambda = 2.25, CosmicWebFirstRatio = 1.5, CosmicWebTopSize = 7.75,
            CosmicWebLevelMode = CosmicWebLevelMode.Fixed, CosmicWebLevels = 6, CosmicWebPointCap = 123_456,
            CosmicWebPointSize = 2.5, CosmicWebDepthFade = 0.3, CosmicWebColour = CosmicWebColour.Density, CosmicWebGamma = 1.7,
            CosmicWebCameraDistance = 3.25, CosmicWebCameraTheta = -1.1, CosmicWebCameraPhi = 0.7,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("CosmicWeb", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            var live = Render(p, 48, 30, region.Zoom);
            var cli = Render(ViaCommandLine(p), 48, 30, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.CosmicWeb, Width = 48, Height = 30,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.ColorBuffer.Distinct().Count() > 5, $"{name}: a flat frame");
        }
    }

    [Fact]
    public void Animatable_Params_Exist()
    {
        var names = typeof(FractalParameters).GetProperties().Select(x => x.Name).ToHashSet();
        var animatable = FractalAnimatableParamsMap.For(FractalType.CosmicWeb).Select(d => d.ParamName).ToArray();
        Assert.NotEmpty(animatable);
        Assert.All(animatable, n => Assert.Contains(n, names));
    }

    [Fact]
    public void Help_Names_Presets_Sources_And_The_Scale_Caveat()
    {
        string help = HelpTextBundle.MathCosmicWebText.Replace("\r\n", " ").Replace("\n", " ");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.Contains("Soneira & Peebles", help);
        Assert.Contains("Einasto 2026", help);
        Assert.Contains("not one fractal of a single dimension", help);
    }
}
