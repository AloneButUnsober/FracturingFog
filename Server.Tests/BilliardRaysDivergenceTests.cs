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

// #1509 — Billiard Rays S4 neighbour divergence. Oracles independent of the calculator:
//   • each pair is re-traced through the ChaoticBilliard launch map (BilliardScene.Launch
//     at the pair's (b, phi)), its hit sequences read off from which disk surface each
//     point lies on; the recorded bounce difference, gate change and shared-mirror count
//     must match, and pairs with no record must agree all the way;
//   • the mark sits on the shared mirror point; the drawn leg length is the in-view
//     length of the current ray's next leg (slab clip);
//   • chunking does not change the result; random sweeps have no neighbours.
public class BilliardRaysDivergenceTests
{
    private const int W = 100, H = 75;
    private const double Zoom = 0.6;

    private static FractalParameters Params(int rays = 1500) => new()
    {
        BilliardRaysCount = rays, BilliardRaysAngleCount = 3, BilliardRaysAngle = 23,
        BilliardRaysMode = BilliardRayMode.Divergence, BilliardMaxBounces = 40,
    };

    private static BilliardRaysCalculator Render(FractalParameters p, int chunk = BilliardRaysCalculator.ChunkSize)
    {
        var c = new BilliardRaysCalculator(W, H) { FractalParameters = p, Zoom = Zoom, RecordDivergences = true, ChunkRays = chunk };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    // Through the launch map: the far-field (b, phi) plane point, as ChaoticBilliard renders it.
    private static (List<BilliardPoint> Path, int[] Hits, int Gate, int Bounces) ViaLaunchMap(BilliardScene scene, double b, double phi)
    {
        var path = new List<BilliardPoint>();
        var o = scene.Launch(b, phi, path);
        var hits = new int[o.Bounces];
        for (int k = 1; k <= o.Bounces; k++)
            hits[k - 1] = Enumerable.Range(0, scene.DiskCount)
                .Single(d => Math.Abs(Math.Sqrt(Math.Pow(path[k].X - scene.DiskX(d), 2) + Math.Pow(path[k].Y - scene.DiskY(d), 2)) - scene.DiskR(d)) < 1e-9);
        return (path, hits, o.GateId, o.Bounces);
    }

    [Fact]
    public void Divergences_Match_The_Launch_Map_Pair_By_Pair()
    {
        var p = Params(rays: 6000);
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        var launches = BilliardRaysCalculator.BuildSweep(p, scene);
        var byCurrent = c.Divergences.ToDictionary(d => d.Current);
        int marked = 0, agreeing = 0;
        for (int i = 1; i < launches.Count; i++)
        {
            bool neighbours = launches[i].B == launches[i - 1].B;   // same beam
            if (!neighbours) { Assert.False(byCurrent.ContainsKey(i)); continue; }
            var a = ViaLaunchMap(scene, launches[i - 1].A, launches[i - 1].B);
            var b = ViaLaunchMap(scene, launches[i].A, launches[i].B);
            int common = 0;
            while (common < Math.Min(a.Hits.Length, b.Hits.Length) && a.Hits[common] == b.Hits[common]) common++;
            bool same = common == a.Hits.Length && common == b.Hits.Length && a.Gate == b.Gate;
            if (same || common == 0) { Assert.False(byCurrent.ContainsKey(i), $"pair {i} should have no mark"); agreeing++; continue; }
            var d = byCurrent[i];
            marked++;
            Assert.Equal(i - 1, d.Previous);
            Assert.Equal(common, d.CommonBounces);
            Assert.Equal(Math.Abs(a.Bounces - b.Bounces), d.BounceDifference);
            Assert.Equal(a.Gate != b.Gate, d.GateChanged);
            Assert.Equal(b.Path[common].X, d.X, 12);   // the current ray's last shared mirror point
            Assert.Equal(b.Path[common].Y, d.Y, 12);
        }
        Assert.Equal(marked, c.LastDivergences);
        Assert.True(marked > 20 && agreeing > 50, $"marked {marked}, agreeing {agreeing}");
    }

    [Fact]
    public void Marks_Sit_On_A_Mirror_Surface()
    {
        var p = Params();
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        Assert.NotEmpty(c.Divergences);
        foreach (var d in c.Divergences)
            Assert.Contains(Enumerable.Range(0, scene.DiskCount), k =>
                Math.Abs(Math.Sqrt(Math.Pow(d.X - scene.DiskX(k), 2) + Math.Pow(d.Y - scene.DiskY(k), 2)) - scene.DiskR(k)) < 1e-9);
    }

    [Fact]
    public void Drawn_Length_Is_The_Diverging_Legs_In_View()
    {
        var p = Params();
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        var launches = BilliardRaysCalculator.BuildSweep(p, scene);
        double pitch = 4.0 / W / Zoom;
        double x0 = -(W / 2.0 + 0.5) * pitch, x1 = (W / 2.0 - 0.5) * pitch;
        double y0 = -((H / 2.0 - 0.5) * pitch), y1 = (H / 2.0 + 0.5) * pitch;
        double expect = 0;
        foreach (var d in c.Divergences)
        {
            var path = ViaLaunchMap(scene, launches[d.Current].A, launches[d.Current].B).Path;
            var a = path[d.CommonBounces]; var b = path[Math.Min(d.CommonBounces + 1, path.Count - 1)];
            expect += Clipped(a, b, x0, x1, y0, y1);
        }
        Assert.True(expect > 10 * pitch);
        Assert.Equal(1.0, c.DivCount.Sum() / expect, 9);
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
    public void Chunking_Does_Not_Change_The_Divergences()
    {
        var p = Params();
        var whole = Render(p);
        var chunked = Render(p, chunk: 41);   // pairs straddle chunk boundaries
        Assert.Equal(whole.Divergences, chunked.Divergences);
        Assert.Equal(whole.DivCount, chunked.DivCount);
        Assert.Equal(whole.ColorBuffer, chunked.ColorBuffer);
    }

    [Fact]
    public void Neighbours_Are_Only_Adjacent_Launches()
    {
        var a = new BilliardRayLaunch(BilliardRayLaunch.LaunchKind.FarField, 0.1, 0.5, 0, 0);
        var sameBeam = a with { A = 0.2 };
        var nextBeam = a with { B = 2.6 };
        Assert.True(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.BSweep, a, sameBeam));
        Assert.False(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.BSweep, a, nextBeam));   // beam boundary
        Assert.True(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.AngleSweep, a, nextBeam));
        Assert.True(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.PointSource, a, nextBeam));
        Assert.False(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.RandomFarField, a, sameBeam));
        Assert.False(BilliardRaysCalculator.AreNeighbours(BilliardRaySweep.PhaseSpace, a, sameBeam));
    }

    [Theory]
    [InlineData(BilliardRaySweep.RandomFarField)]
    [InlineData(BilliardRaySweep.PhaseSpace)]
    public void Random_Sweeps_Have_No_Neighbours(BilliardRaySweep sweep)
    {
        var p = Params();
        p.BilliardRaysSweep = sweep;
        var c = Render(p);
        Assert.Equal(0, c.LastDivergences);
        Assert.Equal(0.0, c.DivCount.Sum());
    }

    [Fact]
    public void Point_Source_Neighbours_Are_Consecutive_Rays()
    {
        var p = Params();
        p.BilliardRaysSweep = BilliardRaySweep.PointSource;
        p.BilliardRaysSpread = 360;
        var c = Render(p);
        Assert.True(c.LastDivergences > 10);
        Assert.All(c.Divergences, d => Assert.Equal(d.Current - 1, d.Previous));
    }

    [Fact]
    public void Divergence_Modes_Keep_The_Paths_And_Give_A_Height_Field()
    {
        var p = Params();
        p.BilliardRaysMode = BilliardRayMode.Paths;
        var paths = Render(p);
        p.BilliardRaysMode = BilliardRayMode.DivergenceOverPaths;
        Assert.Equal(paths.Density, Render(p).Density);
        p.BilliardRaysMode = BilliardRayMode.Divergence;
        foreach (var v in Enum.GetValues<BilliardDivergenceValue>())
        {
            p.BilliardRaysDivergenceValue = v;
            var c = Render(p);
            Assert.Equal(0.0, c.Density.Sum());
            for (int i = 0; i < W * H; i++)
                if (c.DivCount[i] <= 0) Assert.Equal(0f, c.SmoothBuffer[i]);
                else Assert.InRange(c.SmoothBuffer[i], 0f, 1f);
            Assert.Contains(c.SmoothBuffer, s => s > 0 || v == BilliardDivergenceValue.GateChange);
        }
    }

    [Fact]
    public void Divergence_Values_Are_As_Defined()
    {
        var d = new BilliardRayDivergence(0, 0, 9, 8, 3, 5, true, 2.5);
        Assert.Equal(5, BilliardRaysCalculator.DivergenceValue(BilliardDivergenceValue.BounceDifference, d, 10));
        Assert.Equal(1, BilliardRaysCalculator.DivergenceValue(BilliardDivergenceValue.GateChange, d, 10));
        Assert.Equal(0.25, BilliardRaysCalculator.DivergenceValue(BilliardDivergenceValue.Separation, d, 10), 12);
        Assert.Equal(3, BilliardRaysCalculator.DivergenceValue(BilliardDivergenceValue.CommonBounces, d, 10));
        Assert.Equal(0, BilliardRaysCalculator.DivergenceValue(BilliardDivergenceValue.GateChange, d with { GateChanged = false }, 10));
    }

    [Fact]
    public void Divergence_Settings_Round_Trip_And_Show_In_The_Panel()
    {
        var p = Params();
        p.BilliardRaysMode = BilliardRayMode.DivergenceOverPaths;
        p.BilliardRaysDivergenceValue = BilliardDivergenceValue.Separation;
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardRayMode.DivergenceOverPaths, q.BilliardRaysMode);
        Assert.Equal(BilliardDivergenceValue.Separation, q.BilliardRaysDivergenceValue);

        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        Assert.False(vm.RaysShowsDivergence || vm.RaysShowsCrossings);
        vm.BilliardRaysMode = BilliardRayMode.Divergence;
        Assert.True(vm.RaysShowsDivergence);
        Assert.False(vm.RaysShowsCrossings);
        vm.BilliardRaysMode = BilliardRayMode.Both;
        Assert.True(vm.RaysShowsCrossings);
        Assert.False(vm.RaysShowsDivergence);
    }
}
