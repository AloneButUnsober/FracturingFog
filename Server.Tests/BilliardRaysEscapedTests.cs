// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1510 — Billiard Rays S5 Escaped mode. Oracles independent of the calculator:
//   • each drawn ray's class (escaped / crossing / trapped) equals a brute-force all-pairs
//     check against the window's previous rays, over the whole play circle, whatever the view;
//   • escaped + crossing + trapped = drawn rays;
//   • only escaped rays' paths are drawn: the density total is their in-view length.
public class BilliardRaysEscapedTests
{
    private const int W = 90, H = 70;

    private static FractalParameters Params(int window = 15, int rays = 700) => new()
    {
        BilliardRaysCount = rays, BilliardRaysAngleCount = 3, BilliardRaysAngle = 31, BilliardRaysMinBounces = 1,
        BilliardRaysMode = BilliardRayMode.Escaped, BilliardRaysCrossingWindow = window,
        BilliardRaysEscapedColour = BilliardEscapedColour.Grey, BilliardMaxBounces = 2,
    };

    private static BilliardRaysCalculator Render(FractalParameters p, double zoom = 0.6, double cx = 0, double cy = 0)
    {
        var c = new BilliardRaysCalculator(W, H) { FractalParameters = p, Zoom = zoom, CenterX = cx, CenterY = cy, RecordCrossings = true };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static List<(List<BilliardPoint> Path, int Gate)> DrawnRays(FractalParameters p)
    {
        var scene = BilliardScene.Build(p);
        var rays = new List<(List<BilliardPoint>, int)>();
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene))
        {
            var path = new List<BilliardPoint>();
            var o = BilliardRaysCalculator.Launch(scene, l, path);
            if (o.Valid && o.Bounces >= p.BilliardRaysMinBounces && path.Count >= 2) rays.Add((path, o.GateId));
        }
        return rays;
    }

    private static double Orient(BilliardPoint a, BilliardPoint b, BilliardPoint c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Proper(BilliardPoint a, BilliardPoint b, BilliardPoint c, BilliardPoint d)
    {
        double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
        return o1 * o2 < 0 && o3 * o4 < 0;
    }

    private static List<BilliardRayClass> BruteForceClasses(FractalParameters p)
    {
        var rays = DrawnRays(p);
        var classes = new List<BilliardRayClass>();
        for (int r = 0; r < rays.Count; r++)
        {
            if (rays[r].Gate < 0) { classes.Add(BilliardRayClass.Trapped); continue; }
            bool crossed = false;
            for (int q = Math.Max(0, r - p.BilliardRaysCrossingWindow); q < r && !crossed; q++)
                for (int a = 1; a < rays[r].Path.Count && !crossed; a++)
                    for (int b = 1; b < rays[q].Path.Count && !crossed; b++)
                        crossed = Proper(rays[r].Path[a - 1], rays[r].Path[a], rays[q].Path[b - 1], rays[q].Path[b]);
            classes.Add(crossed ? BilliardRayClass.Crossing : BilliardRayClass.Escaped);
        }
        return classes;
    }

    [Fact]
    public void Classes_Match_A_Brute_Force_Check_Whatever_The_View()
    {
        var p = Params();
        var expect = BruteForceClasses(p);
        Assert.True(expect.Count(c => c == BilliardRayClass.Escaped) > 5, "the check should see escaped rays");
        Assert.True(expect.Count(c => c == BilliardRayClass.Crossing) > 5, "the check should see crossing rays");
        Assert.Contains(BilliardRayClass.Trapped, expect);

        var whole = Render(p);
        Assert.Equal(expect, whole.RayClasses);
        // A zoomed-in view off to one side sees few of the crossings, but classes whole rays.
        var corner = Render(p, zoom: 4.0, cx: 1.9, cy: -1.4);
        Assert.Equal(expect, corner.RayClasses);
        Assert.Equal(whole.LastEscapedRays, corner.LastEscapedRays);
    }

    [Fact]
    public void Classes_Add_Up_To_The_Drawn_Rays()
    {
        var c = Render(Params(window: 40, rays: 1500));
        Assert.Equal(c.LastRaysDrawn, c.LastEscapedRays + c.LastCrossingRays + c.LastTrappedRays);
        Assert.Equal(c.LastRaysDrawn, c.RayClasses.Count);
        Assert.Equal(c.LastEscapedRays, c.RayClasses.Count(x => x == BilliardRayClass.Escaped));
        Assert.Equal(c.LastTrappedRays, c.RayClasses.Count(x => x == BilliardRayClass.Trapped));
    }

    [Fact]
    public void Only_Escaped_Rays_Are_Drawn()
    {
        var p = Params();
        var c = Render(p);
        var rays = DrawnRays(p);
        double pitch = 4.0 / W / 0.6;
        double x0 = -(W / 2.0 + 0.5) * pitch, x1 = (W / 2.0 - 0.5) * pitch;
        double y0 = -((H / 2.0 - 0.5) * pitch), y1 = (H / 2.0 + 0.5) * pitch;
        double expect = 0;
        for (int r = 0; r < rays.Count; r++)
        {
            if (c.RayClasses[r] != BilliardRayClass.Escaped) continue;
            var path = rays[r].Path;
            for (int k = 1; k < path.Count; k++) expect += Clipped(path[k - 1], path[k], x0, x1, y0, y1);
        }
        Assert.True(expect > 0);
        Assert.Equal(1.0, c.Density.Sum() / expect, 9);

        p.BilliardRaysEscapedColour = BilliardEscapedColour.Hidden;
        var hidden = Render(p);
        Assert.Equal(0.0, hidden.Density.Sum());
        Assert.Equal(c.RayClasses, hidden.RayClasses);
        Assert.Equal(c.CrossCount, hidden.CrossCount);
    }

    private static double Clipped(BilliardPoint a, BilliardPoint b, double x0, double x1, double y0, double y1)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
        foreach (var (p0, d, mn, mx) in new[] { (a.X, dx, x0, x1), (a.Y, dy, y0, y1) })
        {
            if (Math.Abs(d) < 1e-300) { if (p0 < mn || p0 > mx) return 0; continue; }
            double ta = (mn - p0) / d, tb = (mx - p0) / d;
            lo = Math.Max(lo, Math.Min(ta, tb)); hi = Math.Min(hi, Math.Max(ta, tb));
        }
        return hi > lo ? (hi - lo) * Math.Sqrt(dx * dx + dy * dy) : 0;
    }

    [Fact]
    public void Window_Zero_Leaves_No_Crossing_Rays()
    {
        var c = Render(Params(window: 0));
        Assert.Equal(0, c.LastCrossingRays);
        Assert.Equal(c.LastRaysDrawn, c.LastEscapedRays + c.LastTrappedRays);
        Assert.True(c.LastEscapedRays > 0);
    }

    [Fact]
    public void Status_Bar_Shows_The_Counts_In_Escaped_Mode_Only()
    {
        var c = Render(Params());
        Assert.Equal(BilliardRaysCalculator.StatusText(c.LastEscapedRays, c.LastCrossingRays, c.LastTrappedRays), c.LastPrecisionLabel);
        Assert.Equal("rays: 3 escaped / 2 crossing / 1 trapped", BilliardRaysCalculator.StatusText(3, 2, 1));
        var p = Params();
        p.BilliardRaysMode = BilliardRayMode.Crossings;
        var other = Render(p);
        Assert.Null(other.LastPrecisionLabel);
        Assert.Equal(0, other.LastEscapedRays);
        Assert.Empty(other.RayClasses);
    }

    [Fact]
    public void Escaped_Colours_Change_Only_The_Escaped_Paths()
    {
        var p = Params();
        var grey = Render(p);
        foreach (var col in new[] { BilliardEscapedColour.GateDistance, BilliardEscapedColour.PathLength, BilliardEscapedColour.Bounces })
        {
            p.BilliardRaysEscapedColour = col;
            var c = Render(p);
            Assert.Equal(grey.Density, c.Density);
            Assert.Equal(grey.CrossCount, c.CrossCount);
            Assert.True(c.TintWeight.Sum() > 0 || col == BilliardEscapedColour.GateDistance);
        }
    }

    [Fact]
    public void Escaped_Settings_Round_Trip_And_Show_In_The_Panel()
    {
        var p = Params();
        p.BilliardRaysEscapedColour = BilliardEscapedColour.Bounces;
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardRayMode.Escaped, q.BilliardRaysMode);
        Assert.Equal(BilliardEscapedColour.Bounces, q.BilliardRaysEscapedColour);

        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        Assert.False(vm.RaysShowsEscaped);
        vm.BilliardRaysMode = BilliardRayMode.Escaped;
        Assert.True(vm.RaysShowsEscaped && vm.RaysShowsCrossings && !vm.RaysShowsDivergence);
    }
}
