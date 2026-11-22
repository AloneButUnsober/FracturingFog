// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2007 — the microlensing light-curve inset. Independent checks:
//   • a single lens's sampled light curve follows Paczyński's A(u) along the track (shot noise);
//   • the sampled values are the frame's own magnification grid read along the track (an
//     independent re-sampling of a horizontal track);
//   • a track that misses the view draws nothing and leaves the frame as without the inset; the
//     inset changes pixels only when it is on;
//   • settings survive the snapshot and the command line; presets render the same live, from the
//     command line and as a poster; help names them.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2007MicrolensLightCurveTests
{
    private static MicrolensingCalculator Render(FractalParameters p, int w, int h, double cx = 0, double cy = 0, double zoom = MicrolensingCalculator.DefaultZoom)
    {
        var c = new MicrolensingCalculator(w, h) { ColorMap = new HsvPalette(), FractalParameters = p, CenterX = cx, CenterY = cy, Zoom = zoom };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void A_Single_Lens_Light_Curve_Follows_Paczynski()
    {
        var p = new FractalParameters
        {
            MicroLayout = MicrolensLayout.Single, MicroShear = 0, MicroKappaSmooth = 0, MicroRaysPerPixel = 64, MicroPad = 2,
            MicroLightCurve = true, MicroTrackX = 0, MicroTrackY = 0.3, MicroTrackAngle = 0,
        };
        var c = Render(p, 240, 160);
        var lc = c.LastLightCurve!;
        Assert.NotNull(lc.Analytic);
        var ratios = lc.Magnification.Zip(lc.Analytic!, (m, a) => m / a).Where(double.IsFinite).OrderBy(r => r).ToArray();
        double median = ratios[ratios.Length / 2];
        Assert.InRange(median, 0.97, 1.03);
        // The peak sits where the track passes closest to the lens (s at the middle of a centred track).
        int peak = Array.IndexOf(lc.Analytic!, lc.Analytic!.Max());
        Assert.InRange(lc.S[peak] / lc.S[^1], 0.45, 0.55);
    }

    [Fact]
    public void The_Curve_Is_The_Frames_Grid_Along_The_Track()
    {
        const int W = 120, H = 80;
        var p = new FractalParameters { MicroRaysPerPixel = 2, MicroLightCurve = true, MicroTrackX = 0, MicroTrackY = 0.25, MicroTrackAngle = 0 };
        var c = Render(p, W, H);
        var lc = c.LastLightCurve!;
        Assert.Equal(W, lc.S.Length);   // one sample per pixel across the view
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(W, H, MicrolensingCalculator.DefaultZoom);
        int row = (int)Math.Floor(-0.25 / pitch + 0.5 * H);
        for (int x = 0; x < W; x++)
        {
            double want = (c.Magnification[(row - 1) * W + x] + c.Magnification[row * W + x] + c.Magnification[(row + 1) * W + x]) / 3;
            Assert.Equal(want, lc.Magnification[x], 9);
        }
    }

    [Fact]
    public void A_Missed_Track_Draws_Nothing_And_The_Inset_Only_Draws_When_On()
    {
        var off = new FractalParameters { MicroRaysPerPixel = 2 };
        var miss = new FractalParameters { MicroRaysPerPixel = 2, MicroLightCurve = true, MicroTrackY = 50, MicroTrackAngle = 0 };
        var on = new FractalParameters { MicroRaysPerPixel = 2, MicroLightCurve = true };
        var a = Render(off, 64, 40).ColorBuffer.ToArray();
        var m = Render(miss, 64, 40);
        Assert.Null(m.LastLightCurve);
        Assert.Equal(a, m.ColorBuffer);
        Assert.NotEqual(a, Render(on, 64, 40).ColorBuffer);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.MicrolensingMap, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MicrolensingMap, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MicrolensingMap, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Track_Settings_Survive_The_Command_Line()
    {
        var p = new FractalParameters { MicroLightCurve = true, MicroTrackX = -0.4, MicroTrackY = 0.15, MicroTrackAngle = 33.5 };
        var q = ViaCommandLine(p);
        Assert.True(q.MicroLightCurve);
        Assert.Equal((-0.4, 0.15, 33.5), (q.MicroTrackX, q.MicroTrackY, q.MicroTrackAngle));
    }

    [Fact]
    public void The_Light_Curve_Presets_Render_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in new[] { "Microlensing - Single Lens Light Curve", "Microlensing - Caustic Crossing" })
        {
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            Assert.True(p.MicroLightCurve);
            var live = Render(p, 64, 40, region.CenterX, region.CenterY, region.Zoom);
            Assert.NotNull(live.LastLightCurve);
            var cli = Render(ViaCommandLine(p), 64, 40, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs");
            var req = new PosterRequest
            {
                FractalType = FractalType.MicrolensingMap, Width = 64, Height = 40,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs");
            Assert.Contains(name, HelpTextBundle.MathMicrolensingText);
        }
    }
}
