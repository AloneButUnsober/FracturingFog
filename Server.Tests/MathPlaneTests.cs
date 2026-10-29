// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1548 (Math Plane S1) — scene text format, rendering positions, region and
// --batch round trips. Pixel positions are predicted through
// ChaoticBilliardCalculator.PlaneAt (the shared 2D plane mapping, inverted here)
// rather than MathPlaneCalculator's own ToPixel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneTests
{
    private const int W = 320, H = 240;

    private static uint[] Render(string scene, double cx = 0, double cy = 0, double zoom = 0.5, int w = W, int h = H)
    {
        var calc = new MathPlaneCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom,
            FractalParameters = new FractalParameters { MathPlaneScene = scene },
        };
        calc.Calculate(CancellationToken.None);
        return calc.ColorBuffer;
    }

    /// <summary>Pixel (centre) of world point (x, y): solve PlaneAt(px, py) = (x, -y)
    /// (world y is minus plane y). PlaneAt is affine, so two probes give it.</summary>
    private static (double X, double Y) PredictPixel(double x, double y, double cx, double cy, double zoom)
    {
        var o = ChaoticBilliardCalculator.PlaneAt(0, 0, W, H, cx, cy, zoom);
        var u = ChaoticBilliardCalculator.PlaneAt(1, 1, W, H, cx, cy, zoom);
        double pitchX = u.X - o.X, pitchY = u.Y - o.Y;
        return ((x - o.X) / pitchX, (-y - o.Y) / pitchY);
    }

    private static (double X, double Y, int N) Centroid(uint[] px, uint color)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int i = 0; i < px.Length; i++)
            if (px[i] == color) { sx += i % W + 0.5; sy += i / W + 0.5; n++; }
        return n == 0 ? (double.NaN, double.NaN, 0) : (sx / n, sy / n, n);
    }

    private const string Bare = "axes off; grid off; ticks off; ";

    // ── Text format ──

    [Fact]
    public void Default_IsTheBasisVectors()
    {
        Assert.True(MathPlaneScene.TryParse(new FractalParameters().MathPlaneScene, out var s, out var e), e?.ToString());
        Assert.Equal(new[] { "i", "j" }, s.Objects.Select(o => o.Name));
        Assert.Equal(new double[] { 1, 0 }, s.Objects[0].Coords);
        Assert.Equal(new double[] { 0, 1 }, s.Objects[1].Coords);
    }

    [Fact]
    public void RandomScenes_RoundTrip()
    {
        var rng = new Random(1548);
        var modes = new[] { GridLabelSettings.Default, new GridLabelSettings(GridLabelMode.Offset),
                            new GridLabelSettings(GridLabelMode.Fixed, 0), new GridLabelSettings(GridLabelMode.Scientific, 7) };
        for (int trial = 0; trial < 200; trial++)
        {
            var objs = new List<MathPlaneObject>();
            int n = rng.Next(0, 6);
            for (int k = 0; k < n; k++)
            {
                var kind = (MathPlaneObjectKind)rng.Next(4);
                bool from = kind == MathPlaneObjectKind.Vector && rng.Next(2) == 0;
                int count = kind switch
                {
                    MathPlaneObjectKind.Segment => 4,
                    MathPlaneObjectKind.Polyline => 2 * rng.Next(2, 6),
                    _ => from ? 4 : 2,
                };
                var coords = Enumerable.Range(0, count).Select(_ => Math.Round((rng.NextDouble() - 0.5) * 20, rng.Next(0, 16))).ToArray();
                objs.Add(new MathPlaneObject(kind, "o" + k + (rng.Next(3) == 0 ? "'" : ""), coords,
                    0xFF000000 | (uint)rng.Next(0, 0x1000000), rng.Next(4) != 0, rng.Next(4) != 0, from));
            }
            var scene = new MathPlaneScene
            {
                ShowAxes = rng.Next(2) == 0, ShowGrid = rng.Next(2) == 0, ShowTicks = rng.Next(2) == 0,
                Labels = modes[rng.Next(modes.Length)], Objects = objs,
            };
            string text = scene.Serialize();
            Assert.DoesNotContain('\n', text);
            Assert.True(MathPlaneScene.TryParse(text, out var back, out var err), $"{err} in: {text}");
            Assert.Equal(scene, back);
        }
    }

    [Fact]
    public void Newlines_AndSemicolons_AreBothSeparators()
    {
        Assert.True(MathPlaneScene.TryParse("point A 1 2\r\nvector v 3 4 from 1 1;  segment s 0 0 1 1", out var s, out _));
        Assert.Equal(3, s.Objects.Count);
        Assert.Equal((1.0, 1.0), s.Objects[1].Tail);
    }

    [Theory]
    [InlineData("pointy A 1 2", 1, 1, "unknown statement")]
    [InlineData("point A 1", 1, 9, "needs 2 numbers")]
    [InlineData("point A 1 2 #12345", 1, 13, "not #RRGGBB")]
    [InlineData("point A 1 2 bold", 1, 13, "unknown option")]
    [InlineData("point A 1 2 3", 1, 13, "too many numbers")]
    [InlineData("point 1A 1 2", 1, 7, "needs a name")]
    [InlineData("point A 1 2; point A 3 4", 2, 20, "already used")]
    [InlineData("v2; point A 1 2", 1, 1, "unsupported scene version")]
    [InlineData("labels fixed:31", 1, 8, "'labels' takes")]
    [InlineData("polyline p 0 0", 1, 14, "two or more")]
    [InlineData("vector v from 1 1", 1, 10, "'from' comes after")]
    [InlineData("grid maybe", 1, 6, "takes on or off")]
    public void Errors_NameStatementColumnAndReason(string text, int stmt, int col, string why)
    {
        Assert.False(MathPlaneScene.TryParse(text, out var s, out var e));
        Assert.Empty(s.Objects);
        Assert.Equal(stmt, e!.Statement);
        Assert.Equal(col, e.Column);
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void Names_AreAsciiIdentifiers()
    {
        Assert.True(MathPlaneScene.IsName("v'"));
        Assert.True(MathPlaneScene.IsName("p_1"));
        Assert.False(MathPlaneScene.IsName("v'x"));
        Assert.False(MathPlaneScene.IsName("é"));
        Assert.False(MathPlaneScene.IsName("'"));
    }

    // ── Rendering ──

    [Theory]
    [InlineData(1.0, 2.0, 0.0, 0.0, 0.5)]
    [InlineData(-1.5, -0.75, 0.3, -0.4, 0.8)]
    [InlineData(10.0, 5.0, 10.0, -5.0, 2.0)]   // centre (10, 5) in world = plane y -5
    public void Point_LandsWhereThePlaneMappingPutsIt(double x, double y, double cx, double cy, double zoom)
    {
        const uint c = 0xFF12AB34;
        var px = Render(Bare + $"point A {x} {y} #12AB34 nolabel", cx, cy, zoom);
        var (gx, gy, n) = Centroid(px, c);
        Assert.True(n > 10, "point not drawn");
        var (ex, ey) = PredictPixel(x, y, cx, cy, zoom);
        Assert.InRange(gx - ex, -0.5, 0.5);
        Assert.InRange(gy - ey, -0.5, 0.5);
    }

    [Fact]
    public void Vector_TipIsAtTailPlusVector()
    {
        const uint c = 0xFF00FF00;
        var px = Render(Bare + "vector v 2 1 from -1 -1 #00FF00 nolabel");
        var (tx, ty) = PredictPixel(1, 0, 0, 0, 0.5);       // (-1, -1) + (2, 1)
        var (bx, by) = PredictPixel(-1, -1, 0, 0, 0.5);
        double dx = tx - bx, dy = ty - by, len = Math.Sqrt(dx * dx + dy * dy);
        // Furthest fully covered pixel along the direction is the arrow's point
        // (antialiased edge pixels are blends, so allow ~2 px short).
        double far = double.MinValue, near = double.MaxValue;
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i] != c) continue;
            double t = ((i % W + 0.5 - bx) * dx + (i / W + 0.5 - by) * dy) / len;
            far = Math.Max(far, t); near = Math.Min(near, t);
        }
        Assert.InRange(far - len, -2.5, 0.5);
        Assert.InRange(near, -2.0, 1.0);
    }

    [Fact]
    public void Axes_SitOnTheOrigin()
    {
        var px = Render("grid off; ticks off", 0.7, -0.4, 0.5);
        var (ox, oy) = PredictPixel(0, 0, 0.7, -0.4, 0.5);
        int col = (int)Math.Floor(ox), row = (int)Math.Floor(oy);
        // The brightest column / row near the prediction is the axis.
        int BestCol() => Enumerable.Range(col - 3, 7).OrderByDescending(x => Lum(px[(H / 4) * W + x])).First();
        int BestRow() => Enumerable.Range(row - 3, 7).OrderByDescending(y => Lum(px[y * W + W / 4])).First();
        Assert.InRange(BestCol(), col - 1, col + 1);
        Assert.InRange(BestRow(), row - 1, row + 1);
    }

    private static int Lum(uint p) => (int)((p >> 16) & 0xFF) + (int)((p >> 8) & 0xFF) + (int)(p & 0xFF);

    [Fact]
    public void Hidden_DrawsNothing_AndEmptySceneIsBackgroundPlusGrid()
    {
        var hidden = Render(Bare + "point A 1 1 #12AB34 hidden");
        Assert.All(hidden, p => Assert.Equal(MathPlaneCalculator.BackgroundColor, p));
        var plane = Render("");
        Assert.True(plane.Distinct().Count() > 2);   // grid + axes + labels
    }

    [Fact]
    public void BadScene_RendersErrorInYellow_AndReportsIt()
    {
        var calc = new MathPlaneCalculator(W, H) { FractalParameters = new FractalParameters { MathPlaneScene = "point A" } };
        calc.Calculate(CancellationToken.None);
        Assert.NotNull(calc.LastSceneError);
        Assert.StartsWith("Scene error:", calc.LastPrecisionLabel);
        Assert.Contains(calc.ColorBuffer, p => p == MathPlaneCalculator.ErrorColor);
    }

    [Fact]
    public void Render_IsDeterministic()
        => Assert.Equal(Render("point A 1 2; vector v -1 2; polyline p 0 0 1 1 2 0 3 2"),
                        Render("point A 1 2; vector v -1 2; polyline p 0 0 1 1 2 0 3 2"));

    // ── Region snapshot, --param and the Command builder ──

    [Fact]
    public void Region_Snapshot_IsCanonicalOneLine_AndRestores()
    {
        var p = new FractalParameters { MathPlaneScene = "point A 1 2\nvector v 3 4 #0072B2\nlabels sci:3" };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        Assert.DoesNotContain('\n', snap.MathPlaneScene);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(dst);
        MathPlaneScene.TryParse(p.MathPlaneScene, out var want, out _);
        MathPlaneScene.TryParse(dst.MathPlaneScene, out var got, out _);
        Assert.Equal(want, got);
    }

    [Fact]
    public void Region_Snapshot_KeepsAnEmptyScene()
    {
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, new FractalParameters { MathPlaneScene = "" })!;
        var dst = new FractalParameters();
        snap.ApplyTo(dst);
        Assert.True(MathPlaneScene.TryParse(dst.MathPlaneScene, out var s, out _));
        Assert.Empty(s.Objects);
    }

    [Fact]
    public void CommandBuilder_RoundTrips_AndRendersTheSame()
    {
        var p = new FractalParameters { MathPlaneScene = "point A 1 2 #E69F00\nvector v -2 1 from 1 1\nsegment s 0 0 -3 -1\nlabels fixed:2" };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Equal(FractalType.MathPlane, o.FractalType);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var perr)!.ApplyTo(fresh);
        Assert.Null(perr);
        Assert.Equal(Render(p.MathPlaneScene), Render(fresh.MathPlaneScene));
    }

    [Fact]
    public void Poster_MatchesLiveCalculator()
    {
        const string scene = "point A 1 2; vector v -1 2; segment s 0 0 2 -1";
        var poster = PosterRenderer.RenderToPixels(new PosterRequest
        {
            FractalType = FractalType.MathPlane, Width = W, Height = H,
            CenterX = 0.25, CenterY = -0.5, Zoom = 0.6, MaxIterations = 128,
            Quality = QualityPreset.Draft, ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { MathPlaneScene = scene },
            Path = string.Empty, Format = ImageFileFormat.Png,
        }, CancellationToken.None, out _, out _);
        Assert.Equal(Render(scene, 0.25, -0.5, 0.6), poster);
    }
}
