// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1860 (epic #1428, C10) — the headless batch past 1e100 / past OD. Checked:
//   • The Command builder emits a deep live view (exact centre, zoom past double, the
//     Unbounded tier) and the batch parser reads back the same exact centre, log zoom and
//     tier; the C9 gap (#1861) is gone.
//   • --x / --y: a decimal longer than a double fills the eight limbs (their sum is the
//     text to OD's precision); one past OD is the exact centre; up to 17 digits a double
//     stays a double. --zoom: past double as 1e500, any depth accepted, non-positive refused.
//   • PosterRenderer (the batch / export still path) renders a request carrying the exact
//     centre and log zoom pixel-for-pixel as a calculator set up as the live host sets it
//     (limbs, Zoom, SetDepth); dropping either changes the image (both are load-bearing),
//     and a request whose zoom was changed drops the stale log (stamp rule).
//   • The live export request (FractalRenderHost.CreatePosterRequest) carries the log
//     zoom past double, the aspect factor as a log shift.
//   • A region's exact centre reaches the calculator on CopyCentreTo.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Calculators;
using FracturingFog.Cli;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1860BatchDeepTests
{
    // The Misiurewicz point near -0.1011 + 0.9563i (preperiod 4, Newton-refined, see
    // S1851), to 340 digits: dense, with structure at every scale.
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static string[] Args(BatchCommandSnapshot snap)
    {
        var args = BatchCommandBuilder.BuildWithReport(snap).Args.ToArray();
        for (int i = 0; i < args.Length; i++) if (args[i] == "<OUTPUT.png>") args[i] = "out.png";
        return args;
    }

    [Fact]
    public void The_Command_Builder_Round_Trips_A_Deep_View()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var exact));
        var od = exact.ToDeep();
        var snap = new BatchCommandSnapshot
        {
            CenterX = od.Re.X0, CenterXLimbs = new[] { od.Re.X1, od.Re.X2, od.Re.X3, od.Re.X4, od.Re.X5, od.Re.X6, od.Re.X7 },
            CenterY = od.Im.X0, CenterYLimbs = new[] { od.Im.X1, od.Im.X2, od.Im.X3, od.Im.X4, od.Im.X5, od.Im.X6, od.Im.X7 },
            CenterExactX = CoordNotation.FormatExact(exact.Re), CenterExactY = CoordNotation.FormatExact(exact.Im),
            Zoom = double.MaxValue, ZoomLog10 = 500.123456789, Iterations = 9000, QualityName = "Unbounded",
        };
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(snap), g => g.Contains("#1860"));

        var args = Args(snap);
        Assert.True(BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        var back = opts.CliExactCentre();
        Assert.NotNull(back);
        Assert.True(back!.Value.Precision >= exact.Precision);
        Assert.Equal(exact.Re, back.Value.Re.WithPrecision(exact.Re.Precision));
        Assert.Equal(exact.Im, back.Value.Im.WithPrecision(exact.Im.Precision));
        Assert.Equal(double.MaxValue, opts.Zoom);
        Assert.InRange(opts.ZoomLog10!.Value, 500.123456789 - 1e-12, 500.123456789 + 1e-12);
        Assert.Equal("Unbounded", opts.QualityName);
        Assert.Same(QualityPreset.Unbounded, QualityPreset.FromName(opts.QualityName));
        Assert.Equal(9000, opts.Iterations);
        // The limbs read back are the exact centre's OD split.
        Assert.Equal(new[] { od.Re.X0, od.Re.X1, od.Re.X2, od.Re.X3, od.Re.X4, od.Re.X5, od.Re.X6, od.Re.X7 }, opts.CenterXLimbs);

        // Within double the zoom stays the round-trip double; no exact centre, the limbs.
        var shallow = Args(new BatchCommandSnapshot { CenterX = -0.75, CenterY = 0.1, Zoom = 1.2345678901234567e150 });
        Assert.Equal("1.2345678901234567E+150", shallow[Array.IndexOf(shallow, "--zoom") + 1]);
        Assert.True(BatchOptions.TryParse(shallow, 0, out var so, out err), err);
        Assert.Equal(1.2345678901234567e150, so.Zoom);
        Assert.Null(so.ZoomLog10);
        Assert.Null(so.CliExactCentre());
    }

    [Fact]
    public void Coordinates_And_Zoom_Parse_At_Any_Depth()
    {
        // 30 digits: past a double, within OD — eight limbs, no exact centre.
        const string x30 = "-0.743643887037158704752191506114";
        Assert.True(BatchOptions.TryParseCoordinate(x30, out var l30));
        Assert.Equal(8, l30.Length);
        // Their sum is the text to its own 30 digits (half a unit in the last place).
        var sum = BigFloat.FromOD(new OD(l30[0], l30[1], l30[2], l30[3], l30[4], l30[5], l30[6], l30[7]), 200);
        Assert.InRange(Math.Abs(BigFloat.Sub(sum, BigFloat.Parse(x30, 200), 200).ToDouble()), 0, 5e-31);
        Assert.NotEqual(0, l30[1]);   // more than the double
        Assert.True(BatchOptions.TryParse(new[] { "--x", x30, "--y", "0.1318259042053119", "--zoom", "1e20", "--out", "o.png" }, 0, out var o30, out var e30), e30);
        Assert.Null(o30.CliExactCentre());

        // 17 digits stays the double.
        Assert.True(BatchOptions.TryParseCoordinate("-0.7436438870371587", out var l17));
        Assert.Equal(new[] { -0.7436438870371587 }, l17);

        // Past OD: the exact centre, its limbs the OD split.
        Assert.True(BatchOptions.TryParse(new[] { "--x", Mx, "--y", My, "--zoom", "1e300", "--out", "o.png" }, 0, out var od, out var eod), eod);
        Assert.NotNull(od.CliExactCentre());
        Assert.Equal(od.CliExactCentre()!.Value.ToDeep().Re.X0, od.CenterX);
        Assert.Null(od.ZoomLog10);   // 1e300 is a double

        // --zoom past double, and past the Extreme tier's 1e100 (no longer refused).
        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "2.5e1000", "--out", "o.png" }, 0, out var deep, out var ed), ed);
        Assert.Equal(double.MaxValue, deep.Zoom);
        Assert.InRange(deep.ZoomLog10!.Value, 1000.39, 1000.40);
        Assert.InRange(deep.ZoomLog10Value, 1000.39, 1000.40);
        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1e150", "--out", "o.png" }, 0, out _, out var e150), e150);
        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "-1", "--out", "o.png" }, 0, out _, out _));
        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "abc", "--out", "o.png" }, 0, out _, out _));
    }

    // A tier like Unbounded with one AA sample, so the poster is the calculator's frame.
    private static readonly QualityPreset OneSample = new()
    {
        Tier = QualityTier.Unbounded, Name = "Unbounded1", ZoomMin = QualityPreset.DefaultZoomMin, ZoomMax = double.PositiveInfinity,
        WheelZoomFactor = 1.06, IterBase = 2048, IterMax = IterationLimits.ExploreCeiling, IterPerDecade = 1024,
        AllowHighPrecision = true, HPZoomThreshold = 1e12, AaSamples = 1, TaaMaxSamples = 1,
    };

    private const int W = 24, H = 16, Iter = 4500;
    private const double Zl = 320.3;

    private static PosterRequest Request(ExactCentre? exact, DeepComplex od, double zoom, double zoomLog10) => new()
    {
        FractalType = FractalType.Mandelbrot, Width = W, Height = H,
        CenterX = od.Re.X0, CenterXLo = od.Re.X1, CenterX2 = od.Re.X2, CenterX3 = od.Re.X3,
        CenterX4 = od.Re.X4, CenterX5 = od.Re.X5, CenterX6 = od.Re.X6, CenterX7 = od.Re.X7,
        CenterY = od.Im.X0, CenterYLo = od.Im.X1, CenterY2 = od.Im.X2, CenterY3 = od.Im.X3,
        CenterY4 = od.Im.X4, CenterY5 = od.Im.X5, CenterY6 = od.Im.X6, CenterY7 = od.Im.X7,
        Zoom = zoom, ZoomLog10 = zoomLog10, CenterExact = exact,
        MaxIterations = Iter, ColorMap = ColorPalette.BuiltIns[0], Quality = OneSample,
        Path = string.Empty, Format = ImageFileFormat.Png,
    };

    [Fact]
    public void The_Poster_Renders_The_Exact_Centre_And_Log_Zoom_As_The_Live_Calculator()
    {
        // About two pixels off the Misiurewicz point at 1e320 (past OD and past double).
        int prec = BigFloat.PrecisionForZoom(330) + 64;
        Assert.True(ExactCentre.TryParse(Mx, My, out var m, prec));
        var exact = new ExactCentre(BigFloat.Add(m.Re, BigFloat.Parse("3.1415926535e-321", prec), prec),
                                    BigFloat.Add(m.Im, BigFloat.Parse("2.7182818284e-321", prec), prec));
        var od = exact.ToDeep();
        double zoom = ZoomLog.FromLog10(Zl);
        Assert.Equal(double.MaxValue, zoom);

        var poster = PosterRenderer.RenderToPixels(Request(exact, od, zoom, Zl), CancellationToken.None, out _, out _);

        // As FractalRenderHost sets the live calculator: limbs, Zoom, then SetDepth.
        var live = new MandelbrotCalculator(W, H)
        {
            CenterX = od.Re.X0, CenterXLo = od.Re.X1, CenterX2 = od.Re.X2, CenterX3 = od.Re.X3,
            CenterX4 = od.Re.X4, CenterX5 = od.Re.X5, CenterX6 = od.Re.X6, CenterX7 = od.Re.X7,
            CenterY = od.Im.X0, CenterYLo = od.Im.X1, CenterY2 = od.Im.X2, CenterY3 = od.Im.X3,
            CenterY4 = od.Im.X4, CenterY5 = od.Im.X5, CenterY6 = od.Im.X6, CenterY7 = od.Im.X7,
            Zoom = zoom, MaxIterations = Iter, ColorMap = ColorPalette.BuiltIns[0], Quality = OneSample,
        };
        live.SetDepth(exact, Zl);
        live.Calculate(CancellationToken.None);
        Assert.Equal(live.ColorBuffer.Take(W * H), poster);
        int distinct = poster.Distinct().Count();
        Assert.True(distinct > 8, $"{distinct} colours: the frame has no structure to compare");

        // Both carriers are load-bearing: without the exact centre (the OD point, ~1e-127
        // away) or without the log (the zoom saturated at double.MaxValue) the frame differs.
        var noExact = PosterRenderer.RenderToPixels(Request(null, od, zoom, Zl), CancellationToken.None, out _, out _);
        Assert.NotEqual(poster, noExact);
        var noLog = PosterRenderer.RenderToPixels(Request(exact, od, zoom, double.NaN), CancellationToken.None, out _, out _);
        Assert.NotEqual(poster, noLog);

        // A request whose zoom was changed drops the stale log (stamp rule).
        var req = Request(exact, od, zoom, Zl);
        Assert.Equal(Zl, req.DepthZoomLog10);
        Assert.True(double.IsNaN((req with { Zoom = 1e300 }).DepthZoomLog10));
    }

    [Fact]
    public void The_Live_Export_Request_Carries_The_Log_Zoom()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var exact));
        var state = new FractalViewState { FractalType = FractalType.Mandelbrot, Quality = QualityPreset.Unbounded };
        state.SetCenterExact(exact);
        state.SetZoomLog10(500);
        const int sw = 96, sh = 64;
        using var host = new FractalRenderHost(new NullRenderer(), state, sw, sh, ColorPalette.BuiltIns[0]);

        var same = host.CreatePosterRequest(sw, sh, rotate: false, string.Empty, ImageFileFormat.Png);
        Assert.Equal(500, same.DepthZoomLog10);
        Assert.Equal(exact, same.CenterExact);

        // View mode at another aspect: the factor becomes a shift of the log.
        var wide = host.CreatePosterRequest(2 * sw, sh, rotate: false, string.Empty, ImageFileFormat.Png,
            aspectMode: PosterAspectMode.View);
        double f = PosterRenderer.ViewZoomFactor(sw, sh, 2 * sw, sh);
        Assert.NotEqual(1.0, f);
        Assert.Equal(500 + Math.Log10(f), wide.DepthZoomLog10, 12);

        // Within double nothing changes: no log.
        state.Zoom = 1e50;
        Assert.True(double.IsNaN(host.CreatePosterRequest(sw, sh, false, string.Empty, ImageFileFormat.Png).DepthZoomLog10));
    }

    [Fact]
    public void A_Regions_Exact_Centre_Reaches_The_Calculator()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var exact));
        var region = new FractalRegion { FractalType = FractalType.Mandelbrot, Centre = exact.ToDeep() };
        region.SetCentreExact(exact);
        var calc = new MandelbrotCalculator(8, 8);
        region.CopyCentreTo(calc);
        Assert.Equal(exact, calc.CenterExact);
        Assert.Equal(exact.ToDeep().Re.X5, calc.CenterX5);
    }
}
