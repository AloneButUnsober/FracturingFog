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

// #1508 — Billiard Rays S3 crossings. Oracles independent of the grid search:
//   • the crossings found equal a brute-force all-pairs check (orientation test) over the
//     drawn rays in the window, inside the view;
//   • every crossing lies on both segments, and its path lengths add up to each ray's length;
//   • the result does not depend on how rays are chunked;
//   • window 0 finds no ray-ray crossings and leaves self-crossings alone.
public class BilliardRaysCrossingTests
{
    private const int W = 100, H = 75;
    private const double Zoom = 0.7, Cx = 0.1, Cy = 0.15;

    private static FractalParameters Params(int window, bool self = true, int rays = 500) => new()
    {
        BilliardRaysCount = rays, BilliardRaysAngleCount = 3, BilliardRaysAngle = 17, BilliardRaysMinBounces = 1,
        BilliardRaysMode = BilliardRayMode.Crossings, BilliardRaysCrossingWindow = window,
        BilliardRaysSelfCrossings = self, BilliardMaxBounces = 30,
    };

    private static BilliardRaysCalculator Render(FractalParameters p, int chunk = BilliardRaysCalculator.ChunkSize)
    {
        var c = new BilliardRaysCalculator(W, H)
        {
            FractalParameters = p, Zoom = Zoom, CenterX = Cx, CenterY = Cy, RecordCrossings = true, ChunkRays = chunk,
        };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static List<List<BilliardPoint>> DrawnRays(FractalParameters p)
    {
        var scene = BilliardScene.Build(p);
        var rays = new List<List<BilliardPoint>>();
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene))
        {
            var path = new List<BilliardPoint>();
            var o = BilliardRaysCalculator.Launch(scene, l, path);
            if (o.Valid && o.Bounces >= p.BilliardRaysMinBounces && path.Count >= 2) rays.Add(path);
        }
        return rays;
    }

    private static (double X0, double X1, double Y0, double Y1) View()
    {
        double pitch = 4.0 / W / Zoom;
        return (Cx - (W / 2.0 + 0.5) * pitch, Cx + (W / 2.0 - 0.5) * pitch,
                -(Cy + (H / 2.0 - 0.5) * pitch), -(Cy - (H / 2.0 + 0.5) * pitch));
    }

    private static double Orient(BilliardPoint a, BilliardPoint b, BilliardPoint c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    // Proper crossing by orientations, and its point (from a-b's parameter).
    private static bool Proper(BilliardPoint a, BilliardPoint b, BilliardPoint c, BilliardPoint d, out double px, out double py)
    {
        px = py = 0;
        double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
        if (!(o1 * o2 < 0 && o3 * o4 < 0)) return false;
        double s = o3 / (o3 - o4);
        px = a.X + s * (b.X - a.X); py = a.Y + s * (b.Y - a.Y);
        return true;
    }

    private static HashSet<(int, int, int, int)> BruteForce(FractalParameters p, bool self)
    {
        var rays = DrawnRays(p);
        var (x0, x1, y0, y1) = View();
        var set = new HashSet<(int, int, int, int)>();
        for (int r = 0; r < rays.Count; r++)
            for (int q = Math.Max(0, r - p.BilliardRaysCrossingWindow); q <= r; q++)
            {
                if (q == r && !self) continue;
                for (int ka = 1; ka < rays[r].Count; ka++)
                    for (int kb = 1; kb < rays[q].Count; kb++)
                    {
                        if (q == r && kb >= ka - 1) continue;
                        if (!Proper(rays[r][ka - 1], rays[r][ka], rays[q][kb - 1], rays[q][kb], out double px, out double py)) continue;
                        // Keep clear of the view edge, where the two tests may round apart.
                        if (px < x0 || px >= x1 || py < y0 || py >= y1) continue;
                        set.Add((r, q, ka, kb));
                    }
            }
        return set;
    }

    [Fact]
    public void Grid_Search_Finds_Exactly_The_Brute_Force_Crossings()
    {
        var p = Params(window: 40, rays: 4000); // over 1024 drawn rays, so the finder compacts
        var c = Render(p);
        var found = c.Crossings.Select(x => (x.Current, x.Previous, x.CurrentSegment, x.PreviousSegment)).ToHashSet();
        var expect = BruteForce(p, self: true);
        Assert.True(expect.Count > 200, $"only {expect.Count} crossings in the check");
        Assert.True(expect.Any(e => e.Item1 == e.Item2), "the check should include self-crossings");
        Assert.Equal(expect.Count, c.Crossings.Count);   // no duplicates
        Assert.Equal(expect.OrderBy(e => e), found.OrderBy(e => e));
        Assert.Equal(c.Crossings.Count(x => !x.IsSelf), c.LastCrossings);
        Assert.Equal(c.Crossings.Count(x => x.IsSelf), c.LastSelfCrossings);
    }

    [Fact]
    public void Every_Crossing_Lies_On_Both_Segments_And_Its_Lengths_Add_Up()
    {
        var p = Params(window: 25);
        var c = Render(p);
        var rays = DrawnRays(p);
        Assert.NotEmpty(c.Crossings);
        foreach (var x in c.Crossings)
        {
            var cur = rays[x.Current]; var prev = rays[x.Previous];
            Assert.True(DistToSegment(x.X, x.Y, cur[x.CurrentSegment - 1], cur[x.CurrentSegment]) < 1e-9);
            Assert.True(DistToSegment(x.X, x.Y, prev[x.PreviousSegment - 1], prev[x.PreviousSegment]) < 1e-9);
            Assert.Equal(PathLength(cur), x.CurrentFromEntry + x.CurrentToExit, 9);
            Assert.Equal(PathLength(prev), x.PreviousFromEntry + x.PreviousToExit, 9);
            Assert.InRange(x.Angle, 0, Math.PI / 2 + 1e-12);
            Assert.Equal(x.CurrentSegment - x.PreviousSegment, x.DeltaN);
            Assert.True(x.Previous <= x.Current && x.Current - x.Previous <= 25);
        }
    }

    private static double PathLength(List<BilliardPoint> path)
    {
        double s = 0;
        for (int k = 1; k < path.Count; k++) s += Math.Sqrt(Math.Pow(path[k].X - path[k - 1].X, 2) + Math.Pow(path[k].Y - path[k - 1].Y, 2));
        return s;
    }

    private static double DistToSegment(double x, double y, BilliardPoint a, BilliardPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(a.X + t * dx - x, 2) + Math.Pow(a.Y + t * dy - y, 2));
    }

    [Fact]
    public void Chunking_Does_Not_Change_The_Crossings()
    {
        var p = Params(window: 60, rays: 900);
        var whole = Render(p);
        var chunked = Render(p, chunk: 37);
        Assert.Equal(whole.Crossings, chunked.Crossings);
        Assert.Equal(whole.CrossCount, chunked.CrossCount);
        Assert.Equal(whole.ColorBuffer, chunked.ColorBuffer);
    }

    [Fact]
    public void Window_Zero_Finds_No_Ray_Crossings_And_Keeps_Self_Crossings()
    {
        var none = Render(Params(window: 0));
        var some = Render(Params(window: 40));
        Assert.Equal(0, none.LastCrossings);
        Assert.All(none.Crossings, x => Assert.True(x.IsSelf));
        Assert.True(none.LastSelfCrossings > 0);
        Assert.Equal(some.LastSelfCrossings, none.LastSelfCrossings);

        // Self-crossings are counted either way but drawn only when asked.
        var hidden = Render(Params(window: 0, self: false));
        Assert.Equal(none.LastSelfCrossings, hidden.LastSelfCrossings);
        Assert.Empty(hidden.Crossings);
        Assert.Equal(0.0, hidden.CrossCount.Sum());
    }

    [Fact]
    public void Count_Grid_Holds_Every_Drawn_Crossing()
    {
        var c = Render(Params(window: 40));
        Assert.Equal(c.Crossings.Count, c.CrossCount.Sum());
    }

    [Fact]
    public void Modes_Leave_The_Paths_Alone_And_Crossings_Make_The_Height_Field()
    {
        var p = Params(window: 40);
        p.BilliardRaysMode = BilliardRayMode.Paths;
        var paths = Render(p);
        Assert.Empty(paths.CrossCount);
        p.BilliardRaysCrossingWindow = 7; p.BilliardRaysCrossingValue = BilliardCrossingValue.Angle;
        Assert.Equal(paths.ColorBuffer, Render(p).ColorBuffer);   // crossing settings ignored in Paths

        p.BilliardRaysMode = BilliardRayMode.Both;
        var both = Render(p);
        Assert.Equal(paths.Density, both.Density);

        p.BilliardRaysMode = BilliardRayMode.Crossings;
        foreach (var v in Enum.GetValues<BilliardCrossingValue>())
        {
            p.BilliardRaysCrossingValue = v;
            var c = Render(p);
            Assert.Equal(0.0, c.Density.Sum());
            for (int i = 0; i < W * H; i++)
            {
                if (c.CrossCount[i] <= 0) Assert.Equal(0f, c.SmoothBuffer[i]);
                else Assert.InRange(c.SmoothBuffer[i], 0f, 1f);
            }
            Assert.Contains(c.SmoothBuffer, s => s > 0);
        }
    }

    [Fact]
    public void Crossing_Values_Are_As_Defined()
    {
        var x = new BilliardRayCrossing(0, 0, 5, 3, 4, 1, Math.PI / 4, 1.0, 2.0, 3.0, 4.0);
        Assert.Equal(3, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.DeltaN, x, 10));
        Assert.Equal(0.5, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.Angle, x, 10), 12);
        Assert.Equal(0.1, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.CurrentFromEntry, x, 10), 12);
        Assert.Equal(0.2, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.CurrentToExit, x, 10), 12);
        Assert.Equal(0.3, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.PreviousFromEntry, x, 10), 12);
        Assert.Equal(1.0, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.PreviousToExit, x, 2), 12);   // clamped
        Assert.Equal(1.0, BilliardRaysCalculator.CrossingValue(BilliardCrossingValue.Count, x, 10));
    }

    [Fact]
    public void Crossing_Settings_Round_Trip_And_Show_In_The_Panel()
    {
        var p = Params(window: 321);
        p.BilliardRaysMode = BilliardRayMode.Both;
        p.BilliardRaysCrossingValue = BilliardCrossingValue.PreviousToExit;
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardRayMode.Both, q.BilliardRaysMode);
        Assert.Equal(321, q.BilliardRaysCrossingWindow);
        Assert.Equal(BilliardCrossingValue.PreviousToExit, q.BilliardRaysCrossingValue);
        Assert.True(q.BilliardRaysSelfCrossings);

        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        Assert.False(vm.RaysShowsCrossings);
        vm.BilliardRaysMode = BilliardRayMode.Crossings;
        Assert.True(vm.RaysShowsCrossings);
        vm.BilliardRaysCrossingWindow = 999_999;
        Assert.Equal(BilliardCrossingFinder.MaxWindow, vm.BilliardRaysCrossingWindow);
    }
}
