// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2025 — Cosmic Web S5: the Voronoi clustering model (van de Weygaert, arXiv:0707.2877, Sec. 4.1) and
// the epic's close-out presets. Independent checks:
//   • geometry: with zero width, a wall galaxy is equidistant from its two nearest nuclei, a filament
//     galaxy from its three nearest and a cluster galaxy from its four nearest (computed here by brute
//     force over every periodic image, not through the generator's neighbour lists); with a width, the
//     same seed moves each galaxy off its structure by an isotropic Gaussian jitter (mean square
//     3 sigma^2, none beyond 6 sigma);
//   • D(r) (Einasto 2026, Eqs. 2-4) of a walls-only sample is about 2, and of a filaments-only
//     sample about 1, between the width and the cell size, where xi >> 1;
//   • the component fractions follow the settings; determinism by seed;
//   • every Cosmic Web preset renders the same live, from the command line and as a poster.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.CosmicWeb;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2025VoronoiTests
{
    private readonly ITestOutputHelper _out;
    public S2025VoronoiTests(ITestOutputHelper output) => _out = output;

    private static VoronoiSettings Only(VoronoiComponent c, int nuclei, int points, double thickness, int seed = 11) => new(
        seed, 100, nuclei, points,
        c == VoronoiComponent.Field ? 1 : 0, c == VoronoiComponent.Wall ? 1 : 0,
        c == VoronoiComponent.Filament ? 1 : 0, c == VoronoiComponent.Cluster ? 1 : 0, thickness);

    /// <summary>Signed distances from a point to the bisectors of its nearest nucleus and its 2nd, 3rd and
    /// 4th nearest, (d_k^2 - d_1^2) / (2 |v_k - v_1|), by brute force over all 27 images of every nucleus.</summary>
    private static double[] BisectorGaps(double[] nuclei, double box, double x, double y, double z)
    {
        var c = new System.Collections.Generic.List<(double D2, double X, double Y, double Z)>();
        for (int a = 0; a < nuclei.Length / 3; a++)
            for (int iz = -1; iz <= 1; iz++) for (int iy = -1; iy <= 1; iy++) for (int ix = -1; ix <= 1; ix++)
            {
                double nx = nuclei[3 * a] + ix * box, ny = nuclei[3 * a + 1] + iy * box, nz = nuclei[3 * a + 2] + iz * box;
                double dx = x - nx, dy = y - ny, dz = z - nz;
                c.Add((dx * dx + dy * dy + dz * dz, nx, ny, nz));
            }
        var s = c.OrderBy(q => q.D2).Take(4).ToArray();
        var gaps = new double[3];
        for (int k = 1; k < 4; k++)
        {
            double ex = s[k].X - s[0].X, ey = s[k].Y - s[0].Y, ez = s[k].Z - s[0].Z;
            gaps[k - 1] = (s[k].D2 - s[0].D2) / (2 * Math.Sqrt(ex * ex + ey * ey + ez * ez));
        }
        return gaps;
    }

    [Theory]
    [InlineData(VoronoiComponent.Wall, 1)]
    [InlineData(VoronoiComponent.Filament, 2)]
    [InlineData(VoronoiComponent.Cluster, 3)]
    public void Galaxies_Lie_On_Their_Structure_Within_The_Width(VoronoiComponent target, int gaps)
    {
        var s = VoronoiElement.Sanitise(Only(target, 40, 4000, 0.0));
        var comps = new byte[s.Points];
        var pts = VoronoiElement.Generate(s, TestContext.Current.CancellationToken, comps);
        var nuclei = VoronoiElement.Nuclei(s);
        int reached = 0;
        double worst = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            if ((VoronoiComponent)comps[i] != target) continue;   // a move that found no structure (rare) stops early
            reached++;
            var g = BisectorGaps(nuclei, s.BoxSize, pts.Xyz[3 * i], pts.Xyz[3 * i + 1], pts.Xyz[3 * i + 2]);
            for (int k = 0; k < gaps; k++) worst = Math.Max(worst, g[k]);
        }
        _out.WriteLine($"{target}: {reached} reached, worst bisector gap {worst:0.######}");
        Assert.True(reached > 0.98 * pts.Count, $"only {reached} of {pts.Count} reached {target}");
        Assert.True(worst <= 1e-4, $"worst gap {worst}");   // float storage of coordinates up to 100 (ulp ~ 8e-6)

        // With a width sigma, the same seed gives the same structure point plus a Gaussian jitter.
        const double sigma = 0.4;
        var jittered = VoronoiElement.Generate(s with { Thickness = sigma }, TestContext.Current.CancellationToken);
        double sum2 = 0, max = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double d2 = 0;
            for (int k = 0; k < 3; k++) { double d = VoronoiElement.MinImage(jittered.Xyz[3 * i + k] - (double)pts.Xyz[3 * i + k], 100); d2 += d * d; }
            sum2 += d2; max = Math.Max(max, Math.Sqrt(d2));
        }
        double meanSq = sum2 / pts.Count;
        _out.WriteLine($"{target}: jitter mean square {meanSq:0.####} (3 sigma^2 = {3 * sigma * sigma:0.####}), max {max:0.###}");
        Assert.InRange(meanSq, 3 * sigma * sigma * 0.94, 3 * sigma * sigma * 1.06);
        Assert.True(max < 6 * sigma, $"jitter {max}");
    }

    [Fact]
    public void The_Wall_Point_Is_Where_The_Radial_Ray_First_Leaves_The_Cell()
    {
        // Independent of the neighbour list: march the ray outward in small steps until the nearest nucleus
        // changes, and compare with the exact crossing.
        var s = VoronoiElement.Sanitise(Only(VoronoiComponent.Wall, 30, 1, 0));
        var nuclei = VoronoiElement.Nuclei(s);
        var nbs = VoronoiElement.NeighbourOffsets(nuclei, s.BoxSize);
        var rng = new SplitMix64(9);
        for (int trial = 0; trial < 200; trial++)
        {
            int a = (int)(rng.NextDouble() * 30);
            var (ux, uy, uz) = rng.UnitVector();
            var (_, t) = VoronoiElement.FirstWall(nbs[a], 0, 0, 0, ux, uy, uz);
            double step = 1e-3, r = 0;
            while (VoronoiElement.Nearest(nuclei, s.BoxSize, nuclei[3 * a] + (r + step) * ux, nuclei[3 * a + 1] + (r + step) * uy, nuclei[3 * a + 2] + (r + step) * uz) == a)
                r += step;
            Assert.InRange(t, r - 2 * step, r + 2 * step);
        }
    }

    [Theory]
    [InlineData(VoronoiComponent.Wall, 2.0)]
    [InlineData(VoronoiComponent.Filament, 1.0)]
    public void D_Of_r_Is_Two_For_Walls_And_One_For_Filaments(VoronoiComponent target, double expected)
    {
        // Big cells (walls: 4 nuclei, mean spacing 63 Mpc/h; filaments: 8, 50 Mpc/h) and thin structures, so
        // xi >> 1 over a decade of r.
        var s = Only(target, target == VoronoiComponent.Wall ? 4 : 8, 200_000, target == VoronoiComponent.Wall ? 0.01 : 0.02);
        var pts = VoronoiElement.Generate(s, TestContext.Current.CancellationToken);
        var d = FractalDimension.Measure(pts, CosmicWebDimensionSettings.Default(40_000), TestContext.Current.CancellationToken);
        for (int k = 0; k < d.R.Length; k++) _out.WriteLine($"{target} r={d.R[k]:0.###} DD={d.Pairs[k]} xi={d.Xi[k]:0.##} D={d.D[k]:0.###}");
        // The intermediate range: above 10 widths, and where xi >> 1 (g = 1 + xi close to xi).
        var range = Enumerable.Range(0, d.R.Length).Where(k => d.R[k] >= 10 * s.Thickness && d.Xi[k] > 20 && double.IsFinite(d.D[k])).ToArray();
        Assert.True(range.Length >= 5, $"{range.Length} bins in range");
        double mean = range.Average(k => d.D[k]);
        _out.WriteLine($"{target}: mean D {mean:0.###} over r {d.R[range[0]]:0.##}..{d.R[range[^1]]:0.##}");
        Assert.InRange(mean, expected - 0.15, expected + 0.15);
        Assert.All(range, k => Assert.InRange(d.D[k], expected - 0.3, expected + 0.3));
    }

    [Fact]
    public void Fractions_Follow_The_Settings_And_The_Seed_Fixes_The_Points()
    {
        var s = new VoronoiSettings(3, 100, 50, 40_000, 0.1, 0.2, 0.3, 0.4, 0.3);
        var comps = new byte[s.Points];
        var a = VoronoiElement.Generate(s, TestContext.Current.CancellationToken, comps);
        double[] f = new double[4];
        foreach (byte c in comps) f[c] += 1.0 / comps.Length;
        Assert.InRange(f[0], 0.09, 0.11);
        Assert.InRange(f[1], 0.185, 0.215);
        Assert.InRange(f[2], 0.285, 0.315);
        Assert.InRange(f[3], 0.38, 0.42);
        Assert.True(a.Xyz.SequenceEqual(VoronoiElement.Generate(s, TestContext.Current.CancellationToken).Xyz));
        Assert.False(a.Xyz.SequenceEqual(VoronoiElement.Generate(s with { Seed = 4 }, TestContext.Current.CancellationToken).Xyz));
        Assert.All(a.Xyz, v => Assert.InRange(v, 0f, 100f));
    }

    // ── Batch, presets, help ──

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
    public void Voronoi_Settings_Survive_The_Command_Line()
    {
        var p = new FractalParameters
        {
            CosmicWebGenerator = CosmicWebGenerator.Voronoi, CosmicWebVoronoiNuclei = 77, CosmicWebVoronoiPoints = 12_345,
            CosmicWebVoronoiField = 0.05, CosmicWebVoronoiWalls = 0.25, CosmicWebVoronoiFilaments = 0.45, CosmicWebVoronoiClusters = 0.25,
            CosmicWebVoronoiThickness = 0.8,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("CosmicWeb", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void Every_Cosmic_Web_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        Calculators.Gpu.CosmicWebVolumeBackends.Selection = Calculators.Gpu.CosmicWebVolumeBackends.Choice.Cpu;
        try
        {
            var presets = FractalRegionLibrary.Instance.BuiltIns.Where(r => r.FractalType == FractalType.CosmicWeb).ToArray();
            Assert.True(presets.Length >= 11, $"{presets.Length} presets");
            foreach (var region in presets)
            {
                var p = new FractalParameters();
                region.Params!.ApplyTo(p);
                CosmicWebCalculator Render(FractalParameters fp)
                {
                    var c = new CosmicWebCalculator(48, 30) { ColorMap = new HsvPalette(), Zoom = region.Zoom, FractalParameters = fp };
                    c.Calculate(TestContext.Current.CancellationToken);
                    return c;
                }
                var live = Render(p);
                Assert.True(live.ColorBuffer.SequenceEqual(Render(ViaCommandLine(p)).ColorBuffer), $"{region.Name}: --param differs");
                var poster = PosterRenderer.RenderToPixels(new PosterRequest
                {
                    FractalType = FractalType.CosmicWeb, Width = 48, Height = 30, CenterX = 0, CenterY = 0, Zoom = region.Zoom,
                    MaxIterations = live.MaxIterations, Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
                }, TestContext.Current.CancellationToken, out _, out _);
                Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{region.Name}: poster differs");
                Assert.True(live.ColorBuffer.Distinct().Count() > 5, $"{region.Name}: flat");
            }
        }
        finally { Calculators.Gpu.CosmicWebVolumeBackends.Selection = Calculators.Gpu.CosmicWebVolumeBackends.Choice.Auto; }
    }

    [Fact]
    public void Help_Names_The_Voronoi_Source_And_Every_Preset()
    {
        string help = HelpTextBundle.MathCosmicWebText.Replace("\r\n", " ").Replace("\n", " ");
        Assert.Contains("van de Weygaert", help);
        foreach (var r in FractalRegionLibrary.Instance.BuiltIns.Where(r => r.FractalType == FractalType.CosmicWeb))
            Assert.Contains(r.Name, help);
    }
}
