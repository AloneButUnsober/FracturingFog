// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1878 (epic #1428, C7f) — auto-explore and the Misiurewicz loop past 1e100. Checked:
//   • Children of an exact view at 1e150 (dense 340-digit centre, past OD) are exact: each
//     centre is the parent's plus the grid offset ((i − 1)/3 · 3.5 / zoom), computed here
//     independently in BigFloat, at ×step zoom (log); shallow children are unchanged
//     (no exact centre, the same doubles as before).
//   • ExploreProbe renders an exact view as a live-style calculator does (limbs, Zoom,
//     SetDepth): the same smooth field; without the exact centre it differs.
//   • A real 2-level run from 1e150 descends with exact views (to ~1e151.2), is the same
//     for the same seed, and the Mandelbrot family has no zoom limit.
//   • A loop frame past double carries log zoom + log10(|λ|^(cycles·t)) + the square
//     factor; in double's range it is the old product, unchanged.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Abstractions.Explore;
using FracturingFog.Calculators;
using FracturingFog.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1878DeepExploreLoopTests
{
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

    private static PosterRequest Template() => new()
    {
        FractalType = FractalType.Mandelbrot,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = new FractalParameters(),
        Path = "unused.png",
        Format = ImageFileFormat.Png,
    };

    private static ExploreView Deep(double zl)
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var c));
        return new ExploreView(c.ToDeep(), ZoomLog.FromLog10(zl), c, ZoomLog.BeyondDouble(zl) ? zl : double.NaN);
    }

    [Fact]
    public void Deep_Children_Are_Exact_At_The_Grid_Offsets()
    {
        var parent = Deep(150);
        var o = new AutoExploreOptions { Grid = 3, ZoomStep = 4 };
        var kids = AutoExplorer.Children(parent, o, jitter: 0, new Random(1)).ToList();
        Assert.Equal(9, kids.Count);
        const int prec = 1300;
        var step = BigFloat.Parse("3.5e-150", prec);   // PlaneExtent / zoom
        var three = BigFloat.FromDouble(3, prec);
        int k = 0;
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                var v = kids[k++];
                Assert.NotNull(v.Exact);
                Assert.Equal(150 + Math.Log10(4), v.ZoomLog10Value, 10);
                var wantRe = BigFloat.Add(parent.Exact!.Value.Re.WithPrecision(prec), BigFloat.Div(BigFloat.Mul(step, BigFloat.FromDouble(i - 1, prec), prec), three, prec), prec);
                var wantIm = BigFloat.Add(parent.Exact!.Value.Im.WithPrecision(prec), BigFloat.Div(BigFloat.Mul(step, BigFloat.FromDouble(j - 1, prec), prec), three, prec), prec);
                double err = new CFx(BigFloat.Sub(v.Exact!.Value.Re.WithPrecision(prec), wantRe, prec).ToFloatExp(),
                                     BigFloat.Sub(v.Exact!.Value.Im.WithPrecision(prec), wantIm, prec).ToFloatExp()).Log2Abs();
                Assert.True(err < -150 * 3.3219280948873622 - 40, $"child {i},{j} off by 2^{err:F1}");
            }

        // Shallow: unchanged (no exact centre, the old double offsets).
        var shallow = new ExploreView(new DeepComplex(-0.75, 0.1), 1e10);
        var sk = AutoExplorer.Children(shallow, o, 0, new Random(1)).ToList();
        Assert.All(sk, v => Assert.Null(v.Exact));
        Assert.Equal(shallow.Center.Translate(-3.5 / 1e10 / 3, -3.5 / 1e10 / 3).Re.X0, sk[0].Center.Re.X0);
    }

    [Fact]
    public void The_Probe_Renders_An_Exact_View_As_The_Live_Calculator()
    {
        var v = Deep(150);
        var field = ExploreProbe.Render(Template(), v, 32, default);
        Assert.NotNull(field);

        var q = MinibrotJump.QualityFor(QualityPreset.Standard, v.Zoom);
        var od = v.Center;
        var live = new MandelbrotCalculator(32, 32)
        {
            CenterX = od.Re.X0, CenterXLo = od.Re.X1, CenterX2 = od.Re.X2, CenterX3 = od.Re.X3,
            CenterX4 = od.Re.X4, CenterX5 = od.Re.X5, CenterX6 = od.Re.X6, CenterX7 = od.Re.X7,
            CenterY = od.Im.X0, CenterYLo = od.Im.X1, CenterY2 = od.Im.X2, CenterY3 = od.Im.X3,
            CenterY4 = od.Im.X4, CenterY5 = od.Im.X5, CenterY6 = od.Im.X6, CenterY7 = od.Im.X7,
            Zoom = v.Zoom, MaxIterations = q.ComputeIterationsLog10(150), ColorMap = ColorPalette.BuiltIns[0], Quality = q,
        };
        live.SetDepth(v.Exact, v.ZoomLog10);
        live.Calculate(CancellationToken.None);
        Assert.Equal(live.SmoothBuffer.Take(32 * 32), field!.Value.Take(32 * 32));

        var noExact = ExploreProbe.Render(Template(), v with { Exact = null }, 32, default);
        Assert.NotEqual(field.Value.Take(32 * 32), noExact!.Value.Take(32 * 32));
    }

    [Fact]
    public void A_Deep_Run_Descends_Exactly_And_Reproduces()
    {
        Assert.Equal(double.PositiveInfinity, AutoExplorer.MaxZoomFor(FractalType.Mandelbrot));
        var o = new AutoExploreOptions { Seed = 11, Depth = 2, Beam = 2, ProbeSize = 32, MaxZoom = AutoExplorer.MaxZoomFor(FractalType.Mandelbrot), MinScore = 0 };
        var probe = ExploreProbe.For(Template(), o.ProbeSize);
        var a = AutoExplorer.Run(Deep(150), o, probe);
        var b = AutoExplorer.Run(Deep(150), o, probe);
        Assert.Equal(2, a.Best!.Level);
        Assert.NotNull(a.Best.View.Exact);
        Assert.Equal(150 + 2 * Math.Log10(4), a.Best.View.ZoomLog10Value, 9);
        var pa = a.Best.Path(); var pb = b.Best!.Path();
        Assert.Equal(pa.Count, pb.Count);
        for (int i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i].View.Exact, pb[i].View.Exact);
            Assert.Equal(pa[i].Score.Total, pb[i].Score.Total);
        }
        Assert.Equal(2, a.Finalists.Count);   // the beam kept two separate views
    }

    [Fact]
    public void A_Loop_Frame_Carries_The_Log_Zoom()
    {
        var loop = MisiurewiczLoop.For(1.33, 30, 1);
        const int w = 40, h = 30;
        int side = MisiurewiczLoopRenderer.SquareSide(w, h);
        // The loop's zoom law, from the start: ×|λ|^(cycles·t).
        Assert.Equal(Math.Pow(loop.Magnitude, loop.Cycles * 0.5), loop.ZoomAt(1.0, 0.5), 12);
        Assert.True(ExactCentre.TryParse(Mx, My, out var c));
        var od = c.ToDeep();
        var req = Template() with
        {
            Width = w, Height = h,
            CenterX = od.Re.X0, CenterXLo = od.Re.X1, CenterX2 = od.Re.X2, CenterX3 = od.Re.X3, CenterX4 = od.Re.X4, CenterX5 = od.Re.X5, CenterX6 = od.Re.X6, CenterX7 = od.Re.X7,
            CenterY = od.Im.X0, CenterYLo = od.Im.X1, CenterY2 = od.Im.X2, CenterY3 = od.Im.X3, CenterY4 = od.Im.X4, CenterY5 = od.Im.X5, CenterY6 = od.Im.X6, CenterY7 = od.Im.X7,
            CenterExact = c, Zoom = double.MaxValue, ZoomLog10 = 320.25, MaxIterations = 4000, Quality = QualityPreset.Unbounded,
        };
        // Past double: the frame's square is rendered at the log zoom (checked through the
        // request the renderer derives: it equals a direct render at that log).
        double want = 320.25 + Math.Log10(loop.ZoomAt(1.0, 0.5)) + Math.Log10((double)Math.Max(w, h) / side);
        var frame = MisiurewiczLoopRenderer.RenderFrame(req, loop, 0.5, CancellationToken.None);
        var sq = req with
        {
            Width = side, Height = side, Zoom = ZoomLog.FromLog10(want), ZoomLog10 = want,
            MaxIterations = req.MaxIterations + (int)Math.Ceiling(loop.IterationShiftAt(0.5)),
            ColorMap = new ShiftedColorMap(req.ColorMap, (float)loop.IterationShiftAt(0.5)),
        };
        var src = PosterRenderer.RenderToPixels(sq, CancellationToken.None, out int sw, out int sh);
        var dst = new uint[w * h];
        MisiurewiczLoopRenderer.RotateCrop(src, sw, sh, dst, w, h, loop.CameraTurnAt(0.5));
        Assert.Equal(dst, frame);
    }
}
