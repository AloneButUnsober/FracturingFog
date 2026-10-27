// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1507 — Billiard Rays S2 colour sources. Oracles:
//   • Gate channels: each grid total equals the traced length inside the view weighted by
//     each ray's gate colour, computed here from the outcome and the slab-clipped path;
//   • an attribute every drawn ray shares (one beam => one entry angle) has that exact mean
//     wherever there is density;
//   • self-intersection agrees with an orientation-test brute force on recorded paths;
//   • the colour source never changes the density (brightness).
public class BilliardRaysColourTests
{
    private const int W = 110, H = 80;
    private const double Zoom = 0.8, Cx = 0.2, Cy = -0.1;

    private static BilliardRaysCalculator Render(FractalParameters p, FracturingFog.Interefaces.IColorMap? map = null)
    {
        var c = new BilliardRaysCalculator(W, H) { FractalParameters = p, Zoom = Zoom, CenterX = Cx, CenterY = Cy };
        if (map != null) c.ColorMap = map;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static double ClippedPathLength(IReadOnlyList<BilliardPoint> path)
    {
        double pitch = 4.0 / W / Zoom;
        double x0 = Cx - (W / 2.0 + 0.5) * pitch, x1 = Cx + (W / 2.0 - 0.5) * pitch;
        double y0 = -(Cy + (H / 2.0 - 0.5) * pitch), y1 = -(Cy - (H / 2.0 + 0.5) * pitch);
        double sum = 0;
        for (int k = 1; k < path.Count; k++)
        {
            var a = path[k - 1]; var b = path[k];
            double dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
            bool skip = false;
            foreach (var (p0, d, mn, mx) in new[] { (a.X, dx, x0, x1), (a.Y, dy, y0, y1) })
            {
                if (Math.Abs(d) < 1e-300) { if (p0 < mn || p0 > mx) skip = true; continue; }
                double ta = (mn - p0) / d, tb = (mx - p0) / d;
                lo = Math.Max(lo, Math.Min(ta, tb)); hi = Math.Min(hi, Math.Max(ta, tb));
            }
            if (!skip && hi > lo) sum += (hi - lo) * Math.Sqrt(dx * dx + dy * dy);
        }
        return sum;
    }

    [Fact]
    public void Gate_Channels_Are_The_Length_Weighted_Gate_Colours()
    {
        var p = new FractalParameters
        {
            BilliardRaysCount = 3000, BilliardRaysAngleCount = 3, BilliardRaysMinBounces = 1,
            BilliardRaysColorSource = BilliardRayColorSource.Gate, BilliardMaxBounces = 12,
        };
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        double er = 0, eg = 0, eb = 0;
        int trapped = 0;
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene))
        {
            var path = new List<BilliardPoint>();
            var o = BilliardRaysCalculator.Launch(scene, l, path);
            if (!o.Valid || o.Bounces < 1 || path.Count < 2) continue;
            if (o.GateId < 0) trapped++;
            uint col = o.GateId < 0 ? BilliardRaysCalculator.TrappedColor
                : ChaoticBilliardCalculator.GateColor(null, o.GateId, scene.GateCount, scene.MaxBounces);
            double len = ClippedPathLength(path);
            er += len * ((col >> 16) & 0xFF) / 255.0;
            eg += len * ((col >> 8) & 0xFF) / 255.0;
            eb += len * (col & 0xFF) / 255.0;
        }
        Assert.True(trapped > 0, "the check should see trapped rays too");
        Assert.Equal(1.0, c.Tint0.Sum() / er, 9);
        Assert.Equal(1.0, c.Tint1.Sum() / eg, 9);
        Assert.Equal(1.0, c.Tint2.Sum() / eb, 9);
        Assert.Equal(c.Density.Sum(), c.TintWeight.Sum(), 9);   // every ray has a gate colour
    }

    [Fact]
    public void Shared_Entry_Angle_Is_The_Exact_Mean_Everywhere()
    {
        var p = new FractalParameters
        {
            BilliardRaysCount = 4000, BilliardRaysAngle = 33, BilliardRaysMinBounces = 0,
            BilliardRaysColorSource = BilliardRayColorSource.EntryAngle,
        };
        var c = Render(p);
        int lit = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (c.Density[i] <= 0) continue;
            lit++;
            Assert.Equal(c.Density[i], c.TintWeight[i], 12);
            Assert.Equal(33.0 / 360.0, c.Tint0[i] / c.TintWeight[i], 9);
        }
        Assert.True(lit > 1000);
    }

    [Fact]
    public void Rays_Launched_Inside_The_Play_Circle_Have_No_Gate_Distance()
    {
        var p = new FractalParameters
        {
            BilliardRaysSweep = BilliardRaySweep.PointSource, BilliardRaysSpread = 360, BilliardRaysCount = 3000,
            BilliardRaysColorSource = BilliardRayColorSource.GateDistance,
        };
        var c = Render(p);
        Assert.True(c.Density.Sum() > 0);
        Assert.Equal(0.0, c.TintWeight.Sum());

        // Far-field rays do have one: the gate of the direction they came from.
        p.BilliardRaysSweep = BilliardRaySweep.BSweep;
        p.BilliardRaysAngle = 200;
        var scene = BilliardScene.Build(p);
        double phi = 200 * Math.PI / 180;
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene).Take(200))
        {
            var path = new List<BilliardPoint>();
            if (!BilliardRaysCalculator.Launch(scene, l, path).Valid) continue;
            Assert.Equal(scene.Gate(-Math.Cos(phi), -Math.Sin(phi)), BilliardRaysCalculator.EntryGate(scene, path));
        }
        Assert.True(Render(p).TintWeight.Sum() > 0);
    }

    [Fact]
    public void Self_Intersection_Matches_An_Orientation_Brute_Force()
    {
        var p = new FractalParameters { BilliardRaysSweep = BilliardRaySweep.RandomFarField, BilliardRaysCount = 3000, BilliardMaxBounces = 40 };
        var scene = BilliardScene.Build(p);
        int yes = 0, no = 0;
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene))
        {
            var path = new List<BilliardPoint>();
            if (!BilliardRaysCalculator.Launch(scene, l, path).Valid) continue;
            bool expect = false;
            for (int i = 1; i < path.Count && !expect; i++)
                for (int j = i + 2; j < path.Count && !expect; j++)
                    expect = Proper(path[i - 1], path[i], path[j - 1], path[j]);
            Assert.Equal(expect, BilliardRaysCalculator.SelfIntersects(path));
            if (expect) yes++; else no++;
        }
        Assert.True(yes > 20 && no > 20, $"yes {yes}, no {no}");
    }

    private static double Orient(BilliardPoint a, BilliardPoint b, BilliardPoint c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Proper(BilliardPoint a, BilliardPoint b, BilliardPoint c, BilliardPoint d)
    {
        double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
        return o1 * o2 < 0 && o3 * o4 < 0;
    }

    [Fact]
    public void Colour_Source_Never_Changes_The_Density()
    {
        var p = new FractalParameters { BilliardRaysCount = 2500, BilliardRaysAngleCount = 2 };
        var baseline = Render(p).Density;
        foreach (var s in Enum.GetValues<BilliardRayColorSource>())
        {
            p.BilliardRaysColorSource = s;
            var c = Render(p);
            Assert.Equal(baseline, c.Density);
            if (s == BilliardRayColorSource.Density) Assert.Empty(c.TintWeight);
            else
            {
                Assert.True(c.TintWeight.All(v => v >= 0));
                if (s != BilliardRayColorSource.Gate)
                    for (int i = 0; i < W * H; i++)
                        if (c.TintWeight[i] > 0) Assert.InRange(c.Tint0[i] / c.TintWeight[i], -1e-12, 1 + 1e-12);
            }
        }
    }

    [Fact]
    public void Trapped_And_Undefined_Rays_Get_Their_Own_Colours()
    {
        var scene = BilliardScene.Build(new FractalParameters());
        var trapped = new BilliardOutcome { GateId = -1, Valid = true, Bounces = 256 };
        var path = new List<BilliardPoint> { new(0, 0), new(1, 0) };
        var g = BilliardRaysCalculator.TintOf(BilliardRayColorSource.Gate, scene, null, trapped, path);
        uint grey = BilliardRaysCalculator.TrappedColor;
        Assert.Equal(((grey >> 16) & 0xFF) / 255.0, g.C0, 12);
        Assert.False(BilliardRaysCalculator.TintOf(BilliardRayColorSource.ExitAngle, scene, null, trapped, path).Defined);
        Assert.True(BilliardRaysCalculator.TintOf(BilliardRayColorSource.Bounces, scene, null, trapped, path).Defined);
        Assert.Equal(1.0, BilliardRaysCalculator.TintOf(BilliardRayColorSource.Bounces, scene, null, trapped, path).C0, 12);
    }

    [Fact]
    public void Billiard_Themes_Colour_Gates_And_Do_Not_Blank_The_Gradient_Modes()
    {
        var p = new FractalParameters { BilliardRaysCount = 3000 };
        var theme = new BilliardGatesShadedMap();
        var density = Render(p, theme);
        uint bg = ((FracturingFog.Interefaces.IColorMap)theme).InSetColor | 0xFF000000u;
        int coloured = density.ColorBuffer.Count(c => c != bg && c != BilliardRaysCalculator.DiskFillColor
                                                         && c != BilliardRaysCalculator.DiskEdgeColor && (c & 0xFFFFFF) != 0);
        Assert.True(coloured > 200, $"only {coloured} coloured pixels with a billiard theme");

        p.BilliardRaysColorSource = BilliardRayColorSource.Gate;
        p.BilliardRaysMinBounces = 1;
        var gate = Render(p, theme);
        var scene = BilliardScene.Build(p);
        uint g0 = ChaoticBilliardCalculator.GateColor(theme, 0, scene.GateCount, scene.MaxBounces);
        var t = BilliardRaysCalculator.TintOf(BilliardRayColorSource.Gate, scene, theme,
            new BilliardOutcome { GateId = 0, Valid = true }, new List<BilliardPoint> { new(0, 0), new(1, 0) });
        Assert.Equal(((g0 >> 8) & 0xFF) / 255.0, t.C1, 12);
        Assert.True(gate.TintWeight.Sum() > 0);
    }

    [Fact]
    public void Colour_Source_And_Gates_Round_Trip_The_Region_Snapshot()
    {
        var p = new FractalParameters
        {
            BilliardRaysColorSource = BilliardRayColorSource.SelfIntersects,
            BilliardGateMode = BilliardGateMode.Sector, BilliardGateCount = 9,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardRayColorSource.SelfIntersects, q.BilliardRaysColorSource);
        Assert.Equal(BilliardGateMode.Sector, q.BilliardGateMode);
        Assert.Equal(9, q.BilliardGateCount);
        Assert.Contains(snap.ToKeyValues(), kv => kv.Key == "BilliardRaysColorSource");
    }
}
