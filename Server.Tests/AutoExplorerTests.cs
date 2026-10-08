// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S6 (#1190): interest score + beam-search explorer.
public sealed class AutoExplorerTests
{
    private readonly ITestOutputHelper _out;
    public AutoExplorerTests(ITestOutputHelper output) => _out = output;

    private static PosterRequest Template(FractalType t) => new()
    {
        FractalType = t,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = new FractalParameters(),
        Path = "unused.png",
        Format = ImageFileFormat.Png,
    };

    private static ExploreView View(double x, double y, double zoom) => new(new DeepComplex(x, y), zoom);

    private static InterestScore ScoreAt(FractalType t, double x, double y, double zoom)
    {
        var f = ExploreProbe.Render(Template(t), View(x, y, zoom), 96, default);
        Assert.NotNull(f);
        return InterestScorer.Score(f!);
    }

    // ── Scorer invariants on synthetic fields ────────────────────────────

    [Fact]
    public void AllInside_ScoresZero()
    {
        var f = ProbeField.FromSmooth(32, 32, new float[32 * 32], 1000);
        Assert.Equal(0, InterestScorer.Score(f).Total);
    }

    [Fact]
    public void UniformEscape_ScoresZero()
    {
        var v = Enumerable.Repeat(3.2f, 32 * 32).ToArray();
        Assert.Equal(0, InterestScorer.Score(ProbeField.FromSmooth(32, 32, v, 1000)).Total, 6);
    }

    [Fact]
    public void FlatColour_ScoresZero()
    {
        var px = Enumerable.Repeat(0xFF336699u, 32 * 32).ToArray();
        Assert.Equal(0, InterestScorer.Score(ProbeField.FromArgb(32, 32, px)).Total, 6);
    }

    [Fact]
    public void BoxDimension_OfALine_IsOne_AndOfAFilledSquare_IsTwo()
    {
        const int N = 128;
        var line = new bool[N * N];
        for (int x = 0; x < N; x++) line[64 * N + x] = true;
        Assert.Equal(1.0, InterestScorer.BoxDimension(line, N, N), 2);
        var full = Enumerable.Repeat(true, N * N).ToArray();
        Assert.Equal(2.0, InterestScorer.BoxDimension(full, N, N), 2);
    }

    // ── Real probes (Mandelbrot) ─────────────────────────────────────────

    [Fact]
    public void Valleys_OutscoreTheCardioidInterior_AndEmptySpace()
    {
        var seahorse = ScoreAt(FractalType.Mandelbrot, -0.745, 0.11, 40);
        var elephant = ScoreAt(FractalType.Mandelbrot, 0.28, 0.008, 40);
        var interior = ScoreAt(FractalType.Mandelbrot, -0.2, 0.0, 20);
        var outside = ScoreAt(FractalType.Mandelbrot, 3.0, 3.0, 20);
        _out.WriteLine($"seahorse {seahorse}\nelephant {elephant}\ninterior {interior}\noutside  {outside}");

        Assert.True(interior.Total < 0.02, $"interior {interior}");
        Assert.True(interior.InsideFraction > 0.99, $"interior {interior}");
        Assert.True(outside.Total < 0.02, $"outside {outside}");
        Assert.True(seahorse.Total > 0.3, $"seahorse {seahorse}");
        Assert.True(elephant.Total > 0.3, $"elephant {elephant}");
    }

    [Fact]
    public void SameSeed_SamePath()
    {
        var o = new AutoExploreOptions { Seed = 7, Depth = 3, Beam = 2, ProbeSize = 48 };
        var probe = ExploreProbe.For(Template(FractalType.Mandelbrot), o.ProbeSize);
        var a = AutoExplorer.Run(View(-0.5, 0, 1), o, probe);
        var b = AutoExplorer.Run(View(-0.5, 0, 1), o, probe);
        var pa = a.Best!.Path(); var pb = b.Best!.Path();
        Assert.Equal(pa.Count, pb.Count);
        for (int i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i].View.Center.Re.X0, pb[i].View.Center.Re.X0);
            Assert.Equal(pa[i].View.Center.Im.X0, pb[i].View.Center.Im.X0);
            Assert.Equal(pa[i].View.Zoom, pb[i].View.Zoom);
            Assert.Equal(pa[i].Score.Total, pb[i].Score.Total);
        }
        foreach (var n in pa) _out.WriteLine($"L{n.Level} ({n.View.Center.Re.X0:G9}, {n.View.Center.Im.X0:G9}) z {n.View.Zoom:G3}  {n.Score}");
    }

    [Fact]
    public void Descent_FromHome_EndsOnTheBoundary_Deeper()
    {
        var o = new AutoExploreOptions { Seed = 1, Depth = 5, ProbeSize = 64 };
        var r = AutoExplorer.Run(View(-0.5, 0, 1), o, ExploreProbe.For(Template(FractalType.Mandelbrot), o.ProbeSize));
        var best = r.Best!;
        _out.WriteLine($"{r.Stop} probes {r.Probes}: ({best.View.Center.Re.X0:G12}, {best.View.Center.Im.X0:G12}) z {best.View.Zoom:G4} {best.Score}");
        Assert.Equal(ExploreStop.Completed, r.Stop);
        Assert.Equal(5, best.Level);
        Assert.True(best.Score.Total > 0.3, best.Score.ToString());
        // Independent check (plain escape time, not the probe): a view near the
        // boundary has escape times spread over a wide range; empty space and
        // interiors do not.
        var (lo, hi) = EscapeRange(best.View, 25, 20_000);
        _out.WriteLine($"escape range {lo}..{hi}");
        Assert.True(hi >= 200 && hi >= 4 * lo, $"escape range {lo}..{hi}");
    }

    private static (int Min, int Max) EscapeRange(ExploreView v, int grid, int maxIter)
    {
        double extent = AutoExplorer.PlaneExtent / v.Zoom;
        int min = int.MaxValue, max = 0;
        for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                double cr = v.Center.Re.X0 + (i / (grid - 1.0) - 0.5) * extent;
                double ci = v.Center.Im.X0 + (j / (grid - 1.0) - 0.5) * extent;
                double zr = 0, zi = 0;
                int n = 0;
                while (n < maxIter && zr * zr + zi * zi <= 4)
                {
                    (zr, zi) = (zr * zr - zi * zi + cr, 2 * zr * zi + ci);
                    n++;
                }
                min = Math.Min(min, n); max = Math.Max(max, n);
            }
        return (min, max);
    }

    [Theory]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Lyapunov)]
    [InlineData(FractalType.Julia)]
    public void OtherFamilies_Explore(FractalType t)
    {
        var vs = new FracturingFog.ViewState.FractalViewState();
        vs.SnapToFractalDefault(t);
        var o = new AutoExploreOptions { Seed = 3, Depth = 3, ProbeSize = 48, MaxZoom = AutoExplorer.MaxZoomFor(t) };
        var r = AutoExplorer.Run(View(vs.CenterX, vs.CenterY, vs.Zoom), o, ExploreProbe.For(Template(t), o.ProbeSize));
        var best = r.Best!;
        _out.WriteLine($"{t}: {r.Stop} L{best.Level} ({best.View.Center.Re.X0:G9}, {best.View.Center.Im.X0:G9}) z {best.View.Zoom:G3} {best.Score}");
        Assert.True(best.Score.Total > 0.1, best.Score.ToString());
    }

    // ── Spec ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("seed=5", "seed=5")]
    [InlineData("seed=5,depth=8,beam=4", "seed=5,depth=8,beam=4")]
    [InlineData(" beam=2 , seed=-3, step=2.5, probe=64, start=16, minscore=0.1 ",
                "seed=-3,beam=2,start=16,step=2.5,probe=64,minscore=0.1")]
    public void Spec_RoundTrips(string spec, string canonical)
    {
        Assert.True(AutoExplorer.TryParseSpec(spec, out var o, out var err), err);
        Assert.Equal(canonical, AutoExplorer.FormatSpec(o));
        Assert.True(AutoExplorer.TryParseSpec(canonical, out var o2, out _));
        Assert.Equal(o, o2);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("depth=0")]
    [InlineData("beam=x")]
    [InlineData("colour=red")]
    public void Spec_RejectsBadInput(string spec)
        => Assert.False(AutoExplorer.TryParseSpec(spec, out _, out _));
}
