// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2000 — the scattering bridge to Math Plane: (1) "send this pixel's path": the launch under a
// point of a Hénon–Heiles, three-body or magnetised-black-hole view, sent as a trajectory, ends at
// the gate that family's own render shows at that pixel; the send adds to a scene that already has
// the family's backdrop and otherwise starts one; (2) scattering backdrops equal the family's own
// render at the same view (dimmed, physical plane), and travel through the region snapshot,
// --param and the Command builder.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2000MathPlaneScatterBridgeTests
{
    private const int W = 64, H = 36;

    // ── The pointer mapping ──

    [Fact]
    public void PlaneAt_IsTheFamiliesOwnMapping()
    {
        var rng = new Random(2000);
        for (int k = 0; k < 200; k++)
        {
            double x = rng.NextDouble() * 900, y = rng.NextDouble() * 500;
            double w = 400 + rng.Next(800), h = 300 + rng.Next(500);
            double cx = rng.NextDouble() * 4 - 2, cy = rng.NextDouble() * 4 - 2, z = 0.1 + rng.NextDouble() * 3;
            var m = MathPlaneTrajectory.PlaneAt(x, y, w, h, cx, cy, z);
            Assert.Equal(HenonHeilesCalculator.PlaneAt(x, y, w, h, cx, cy, z), (m.A, m.B));
            var t = ThreeBodyCalculator.PlaneAt(x, y, w, h, cx, cy, z);
            Assert.Equal((t.X, t.Y), (m.A, m.B));
            var g = MagnetisedBlackHoleCalculator.PlaneAt(x, y, w, h, cx, cy, z);
            Assert.Equal((g.A, g.C), (m.A, m.B));
        }
    }

    // ── Send: the path ends at the pixel's gate ──

    private static void AssertSentPathsMatchTheRender(FractalType type, FractalParameters p, double cx, double cy, double zoom,
                                                      Func<IFractalCalculator, sbyte[]> gates, int minLaunched)
    {
        IFractalCalculator calc = type switch
        {
            FractalType.HenonHeilesBasins => new HenonHeilesCalculator(W, H) { FractalParameters = p },
            FractalType.ThreeBodyBasins => new ThreeBodyCalculator(W, H) { FractalParameters = p },
            _ => new MagnetisedBlackHoleCalculator(W, H) { FractalParameters = p },
        };
        calc.CenterX = cx; calc.CenterY = cy; calc.Zoom = zoom;
        calc.Calculate(CancellationToken.None);
        var g = gates(calc);
        var rng = new Random((int)type * 31 + 7);
        int launched = 0;
        for (int k = 0; k < 40; k++)
        {
            int px = rng.Next(W), py = rng.Next(H);
            var (a, b) = MathPlaneTrajectory.PlaneAt(px + 0.5, py + 0.5, W, H, cx, cy, zoom);
            var send = MathPlaneTrajectory.Send(type, p, a, b, cx, cy, zoom)!;
            Assert.True(MathPlaneScene.TryParse(send.Scene, out var scene, out var err), err?.ToString());
            var o = scene.Objects.Last(o => o.Kind == MathPlaneObjectKind.Trajectory);
            var path = ScatterPathTracer.Trace(o);
            sbyte want = g[py * W + px];
            if (!path.Launched) { Assert.True(want < -1, $"pixel ({px},{py}) gate {want} but no launch"); continue; }
            launched++;
            Assert.Equal(want, (sbyte)path.Gate);
        }
        Assert.True(launched >= minLaunched, $"{type}: only {launched} launches");
    }

    [Fact]
    public void Send_HenonHeilesPlane_EndsAtThePixelsExit()
        => AssertSentPathsMatchTheRender(FractalType.HenonHeilesBasins,
            new FractalParameters { HenonHeilesEnergy = 0.22, HenonHeilesMaxTime = 120 }, 0, 0, 1.2,
            c => ((HenonHeilesCalculator)c).ExitBuffer, 20);

    [Fact]
    public void Send_HenonHeilesSection_EndsAtThePixelsExit()
        => AssertSentPathsMatchTheRender(FractalType.HenonHeilesBasins,
            new FractalParameters { HenonHeilesView = HenonHeilesView.Section, HenonHeilesEnergy = 0.2, HenonHeilesMaxTime = 120 },
            0, 0, 2.0, c => ((HenonHeilesCalculator)c).ExitBuffer, 15);

    [Fact]
    public void Send_ThreeBodyFates_EndsAtThePixelsFate()
        => AssertSentPathsMatchTheRender(FractalType.ThreeBodyBasins,
            new FractalParameters { ThreeBodyJacobi = 2.9, ThreeBodyMaxTime = 30 }, 0, 0, 1.0,
            c => ((ThreeBodyCalculator)c).GateBuffer, 15);

    [Fact]
    public void Send_MagnetisedHoleMeridional_EndsAtThePixelsFate()
        => AssertSentPathsMatchTheRender(FractalType.MagnetisedBlackHole,
            new FractalParameters { MagBhView = MagnetisedBhView.Meridional, MagBhEscapeHeight = 30, MagBhMaxTime = 300 },
            -2, 0, 0.4, c => ((MagnetisedBlackHoleCalculator)c).GateBuffer, 15);

    [Fact]
    public void Send_MagnetisedHoleKick_EndsAtThePixelsFate()
        => AssertSentPathsMatchTheRender(FractalType.MagnetisedBlackHole,
            new FractalParameters { MagBhView = MagnetisedBhView.Kick, MagBhEscapeHeight = 30, MagBhMaxTime = 300 },
            4, -1.4, 0.6, c => ((MagnetisedBlackHoleCalculator)c).GateBuffer, 8);

    // ── Send: scene, backdrop, view ──

    [Fact]
    public void Send_StartsASceneThenAddsToIt()
    {
        var p = new FractalParameters
        {
            MathPlaneScene = MathPlaneScene.DefaultText, MathPlaneMapMode = MathPlaneMapMode.Matrix, MathPlaneScriptTime = 3,
        };
        var first = MathPlaneTrajectory.Send(FractalType.HenonHeilesBasins, p, 0.2, 0.1, 0.3, -0.2, 1.1)!;
        Assert.False(first.Appended);
        Assert.Equal(MathPlaneBackdrop.HenonHeiles, first.Backdrop);
        Assert.Equal((0.3, -0.2, 1.1), (first.CenterX, first.CenterY, first.Zoom));   // the family's own camera
        first.Apply(p);
        Assert.Equal(MathPlaneMapMode.None, p.MathPlaneMapMode);
        Assert.Equal(0, p.MathPlaneScriptTime);
        Assert.True(MathPlaneScene.TryParse(p.MathPlaneScene, out var s1, out _));
        var o1 = Assert.Single(s1.Objects);   // the basis vectors are gone: a new scene
        Assert.Equal(new[] { 0.2, 0.1 }, o1.Coords);

        var second = MathPlaneTrajectory.Send(FractalType.HenonHeilesBasins, p, -0.3, 0.05, 0, 0, 1)!;
        Assert.True(second.Appended);
        second.Apply(p);
        Assert.True(MathPlaneScene.TryParse(p.MathPlaneScene, out var s2, out _));
        Assert.Equal(2, s2.Objects.Count);
        Assert.NotEqual(s2.Objects[0].Name, s2.Objects[1].Name);
        Assert.NotEqual(s2.Objects[0].Color, s2.Objects[1].Color);

        // Another family starts again, over its own backdrop.
        var third = MathPlaneTrajectory.Send(FractalType.ThreeBodyBasins, p, 0.9, 0.2, 0, 0, 1)!;
        Assert.False(third.Appended);
        Assert.Equal(MathPlaneBackdrop.ThreeBody, third.Backdrop);
    }

    [Fact]
    public void Send_SectionAndKickLaunchesFrameThePhysicalPlane()
    {
        var hh = MathPlaneTrajectory.Send(FractalType.HenonHeilesBasins,
            new FractalParameters { HenonHeilesView = HenonHeilesView.Section }, 0.1, 0.05, 9, 9, 7)!;
        Assert.Equal(0, hh.CenterX);
        Assert.Equal(0, hh.CenterY);
        // Half the view's long side is 2.5: the whole Hénon–Heiles well.
        Assert.Equal(2.5, ViewCamera.PixelPitch(960, 540, hh.Zoom) * 480, 9);

        var kick = MathPlaneTrajectory.Send(FractalType.MagnetisedBlackHole,
            new FractalParameters { MagBhView = MagnetisedBhView.Kick }, 5, 1.6, 9, 9, 7)!;
        Assert.Equal(5, kick.CenterX);
        Assert.Equal(10, ViewCamera.PixelPitch(960, 540, kick.Zoom) * 480, 9);
    }

    [Fact]
    public void Send_RefusesTheLagrangeViewAndOtherTypes()
    {
        Assert.Null(MathPlaneTrajectory.Send(FractalType.ThreeBodyBasins,
            new FractalParameters { ThreeBodyView = ThreeBodyView.LagrangeBasins }, 0.1, 0.1, 0, 0, 1));
        Assert.Null(MathPlaneTrajectory.Send(FractalType.Mandelbrot, new FractalParameters(), 0.1, 0.1, 0, 0, 1));
        Assert.Equal(MathPlaneBackdrop.None, MathPlaneTrajectory.BackdropOf(FractalType.BlackHoleRingOrbits));
    }

    // ── Backdrops ──

    private static MathPlaneCalculator Plane(FractalParameters p, double cx, double cy, double zoom)
    {
        var calc = new MathPlaneCalculator(W, H) { CenterX = cx, CenterY = cy, Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    public static IEnumerable<object[]> Backdrops() => new[]
    {
        new object[] { MathPlaneBackdrop.HenonHeiles, FractalType.HenonHeilesBasins },
        new object[] { MathPlaneBackdrop.ThreeBody, FractalType.ThreeBodyBasins },
        new object[] { MathPlaneBackdrop.MagnetisedBlackHole, FractalType.MagnetisedBlackHole },
    };

    private static FractalParameters FastFamily() => new()
    {
        HenonHeilesMaxTime = 60, ThreeBodyMaxTime = 20, MagBhMaxTime = 200, MagBhEscapeHeight = 30,
        // The views the backdrop must override to reach the physical plane.
        HenonHeilesView = HenonHeilesView.Section, MagBhView = MagnetisedBhView.Kick,
    };

    [Theory]
    [MemberData(nameof(Backdrops))]
    public void Backdrop_IsTheFamilysOwnRenderInThePhysicalPlane(MathPlaneBackdrop backdrop, FractalType type)
    {
        var p = FastFamily();
        p.MathPlaneScene = "axes off; grid off; ticks off";
        p.MathPlaneBackdrop = backdrop;
        double cx = type == FractalType.MagnetisedBlackHole ? 2 : 0, cy = 0, zoom = type == FractalType.MagnetisedBlackHole ? 0.5 : 1.2;
        var plane = Plane(p, cx, cy, zoom);

        var fp = MathPlaneCalculator.ScatterBackdropParams(type, p);
        if (type == FractalType.HenonHeilesBasins) Assert.Equal(HenonHeilesView.Plane, fp.HenonHeilesView);
        if (type == FractalType.MagnetisedBlackHole) Assert.Equal(MagnetisedBhView.Meridional, fp.MagBhView);
        Assert.Equal(HenonHeilesView.Section, p.HenonHeilesView);   // the user's own view is left alone
        IFractalCalculator fam = type switch
        {
            FractalType.HenonHeilesBasins => new HenonHeilesCalculator(W, H) { FractalParameters = fp },
            FractalType.ThreeBodyBasins => new ThreeBodyCalculator(W, H) { FractalParameters = fp },
            _ => new MagnetisedBlackHoleCalculator(W, H) { FractalParameters = fp },
        };
        fam.ColorMap = plane.ColorMap;
        fam.MaxIterations = plane.MaxIterations;
        fam.Quality = plane.Quality;
        fam.CenterX = cx; fam.CenterY = cy; fam.Zoom = zoom;
        fam.Calculate(CancellationToken.None);
        var want = new uint[W * H];
        MathPlaneCalculator.DimInto(fam.ColorBuffer, want, W, H, flipRows: false);
        Assert.Equal(want, plane.ColorBuffer);
        Assert.True(want.Distinct().Count() >= 3, "a real basin picture");

        // A redraw with the same backdrop (an object edit) gives the same frame.
        p.MathPlaneScene = "axes off; grid off; ticks off; point A 0.1 0.1 hidden";
        Assert.Equal(want, Plane(p, cx, cy, zoom).ColorBuffer);
    }

    [Fact]
    public void Backdrop_PathStartsOnItsOwnPixel()
    {
        // A sent path's ring sits on the pixel it was sent from: same camera, same mapping.
        var p = FastFamily();
        p.HenonHeilesView = HenonHeilesView.Plane;
        int px = 40, py = 12;
        var (a, b) = MathPlaneTrajectory.PlaneAt(px + 0.5, py + 0.5, W, H, 0, 0, 1.2);
        var (sx, sy) = MathPlaneCalculator.ToPixel(a, b, W, H, 0, 0, 1.2);
        Assert.Equal(px + 0.5, sx, 9);
        Assert.Equal(py + 0.5, sy, 9);
        var send = MathPlaneTrajectory.Send(FractalType.HenonHeilesBasins, p, a, b, 0, 0, 1.2)!;
        Assert.Equal((0.0, 0.0, 1.2), (send.CenterX, send.CenterY, send.Zoom));
    }

    [Fact]
    public void Backdrop_TravelsThroughSnapshotParamAndCommandLine()
    {
        var p = new FractalParameters
        {
            MathPlaneBackdrop = MathPlaneBackdrop.HenonHeiles, HenonHeilesEnergy = 0.31, HenonHeilesMaxTime = 77,
            MathPlaneScene = "trajectory path1 henonheiles 0.2 0.1 E=0.31",
        };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var kv = snap.ToKeyValues();
        var map = kv.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(((int)MathPlaneBackdrop.HenonHeiles).ToString(), map["MathPlaneBackdrop"]);
        Assert.True(map.ContainsKey("HenonHeilesEnergy"));

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, Parameters = p, FamilyParams = kv,
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.Equal(MathPlaneBackdrop.HenonHeiles, q.MathPlaneBackdrop);
        Assert.Equal(0.31, q.HenonHeilesEnergy);
        Assert.Equal(77, q.HenonHeilesMaxTime);
        Assert.True(MathPlaneScene.TryParse(p.MathPlaneScene, out var sp, out _));
        Assert.True(MathPlaneScene.TryParse(q.MathPlaneScene, out var sq, out _));
        Assert.Equal(sp, sq);
    }

    [Fact]
    public void Help_AndGuide_DescribeTheBridge()
    {
        Assert.Contains("press J", HelpTextBundle.MathPlaneText);
        Assert.Contains("HenonHeiles", HelpTextBundle.MathPlaneText);
    }
}
