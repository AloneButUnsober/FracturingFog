// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1538 — the Polygon billiard geometry; #1540 — more decimals for the Billiard Rays angles.
// Oracles from the polygon itself (corners at sep (cos 2 pi j / k, sin 2 pi j / k), edges
// between them), not from the placer: every disk is classified onto a corner or an edge.
public class BilliardPolygonTests
{
    private static BilliardScene Scene(int n, int k, double r = 0.2, double sep = 1.5,
                                       BilliardGateMode gate = BilliardGateMode.ExitGap)
        => BilliardScene.Build(new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.Polygon, BilliardDiskCount = n, BilliardPolygonSides = k,
            BilliardDiskRadius = r, BilliardSeparation = sep, BilliardGateMode = gate,
        });

    private static (double X, double Y) Corner(int j, int k, double sep)
        => (sep * Math.Cos(2 * Math.PI * j / k), sep * Math.Sin(2 * Math.PI * j / k));

    private static double DistToSegment(double x, double y, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(a.X + t * dx - x, 2) + Math.Pow(a.Y + t * dy - y, 2));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(7, 3)]
    [InlineData(12, 6)]
    [InlineData(13, 6)]
    [InlineData(17, 8)]
    [InlineData(64, 12)]
    public void Disks_Fill_The_Corners_Then_Spread_Evenly_Along_The_Edges(int n, int k)
    {
        const double sep = 1.5;
        var s = Scene(n, k, sep: sep);
        Assert.Equal(n, s.DiskCount);
        var onCorner = new bool[k];
        var perEdge = Enumerable.Range(0, k).Select(_ => new System.Collections.Generic.List<double>()).ToArray();
        for (int i = 0; i < n; i++)
        {
            double x = s.DiskX(i), y = s.DiskY(i);
            int corner = Enumerable.Range(0, k).FirstOrDefault(j => Math.Abs(x - Corner(j, k, sep).X) < 1e-12 && Math.Abs(y - Corner(j, k, sep).Y) < 1e-12, -1);
            if (corner >= 0) { Assert.False(onCorner[corner]); onCorner[corner] = true; continue; }
            int edge = Enumerable.Range(0, k).Single(j => DistToSegment(x, y, Corner(j, k, sep), Corner((j + 1) % k, k, sep)) < 1e-12);
            var a = Corner(edge, k, sep);
            perEdge[edge].Add(Math.Sqrt(Math.Pow(x - a.X, 2) + Math.Pow(y - a.Y, 2)));
        }
        Assert.All(onCorner, Assert.True);
        int min = perEdge.Min(e => e.Count), max = perEdge.Max(e => e.Count);
        Assert.True(max - min <= 1, $"edge counts {min}..{max}");
        double side = 2 * sep * Math.Sin(Math.PI / k);
        foreach (var e in perEdge)
        {
            var d = e.OrderBy(v => v).ToList();
            for (int s2 = 0; s2 < d.Count; s2++) Assert.Equal(side * (s2 + 1) / (d.Count + 1), d[s2], 12);   // evenly spaced
        }
        Assert.Equal(sep, s.DiskX(0), 12);   // disk 0 is corner 0, on +x
        Assert.Equal(0.0, s.DiskY(0), 12);
    }

    [Fact]
    public void Fewer_Disks_Than_Corners_Sit_On_Corners_And_Warn()
    {
        const double sep = 1.5;
        var s = Scene(3, 8, sep: sep);
        Assert.Equal(3, s.DiskCount);
        var used = Enumerable.Range(0, 3)
            .Select(i => Enumerable.Range(0, 8).Single(j => Math.Abs(s.DiskX(i) - Corner(j, 8, sep).X) < 1e-12 && Math.Abs(s.DiskY(i) - Corner(j, 8, sep).Y) < 1e-12))
            .OrderBy(j => j).ToList();
        // Spread round the polygon: the corner gaps (cyclic) differ by at most one.
        var gaps = used.Select((j, i) => ((i + 1 < used.Count ? used[i + 1] : used[0] + 8) - j)).ToList();
        Assert.True(gaps.Max() - gaps.Min() <= 1, $"corners {string.Join(",", used)}");
        Assert.Contains("Fewer disks than corners", BilliardLayout.Warning(BilliardGeometry.Polygon, 3, 0.2, sep, null, 8));
        Assert.Empty(BilliardLayout.Warning(BilliardGeometry.Polygon, 8, 0.2, sep, null, 8));
    }

    [Theory]
    [InlineData(BilliardGateMode.ExitGap)]
    [InlineData(BilliardGateMode.Sector)]
    public void One_Disk_Per_Corner_Is_The_Ring(BilliardGateMode gate)
    {
        foreach (int k in new[] { 3, 5, 6, 8 })
        {
            var ring = new FractalParameters { BilliardGeometry = BilliardGeometry.Ring, BilliardDiskCount = k, BilliardDiskRadius = 0.3, BilliardSeparation = 1.4, BilliardGateMode = gate };
            var poly = ring.Clone();
            poly.BilliardGeometry = BilliardGeometry.Polygon;
            poly.BilliardPolygonSides = k;
            var a = new ChaoticBilliardCalculator(64, 48) { FractalParameters = ring };
            var b = new ChaoticBilliardCalculator(64, 48) { FractalParameters = poly };
            a.Calculate(TestContext.Current.CancellationToken);
            b.Calculate(TestContext.Current.CancellationToken);
            Assert.Equal(a.ColorBuffer, b.ColorBuffer);
            Assert.Equal(a.SmoothBuffer, b.SmoothBuffer);
            var ra = new BilliardRaysCalculator(64, 48) { FractalParameters = ring };
            var rb = new BilliardRaysCalculator(64, 48) { FractalParameters = poly };
            ring.BilliardRaysCount = poly.BilliardRaysCount = 2000;
            ra.Calculate(TestContext.Current.CancellationToken);
            rb.Calculate(TestContext.Current.CancellationToken);
            Assert.Equal(ra.ColorBuffer, rb.ColorBuffer);
        }
    }

    [Fact]
    public void Exit_Gates_Are_The_Gaps_Between_Perimeter_Neighbours()
    {
        var s = Scene(13, 6);
        Assert.Equal(13, s.GateCount);
        // Perimeter order = angle order from disk 0 for a convex polygon about the origin.
        double Rel(int i) { double a = Math.Atan2(s.DiskY(i), s.DiskX(i)); return a < 0 ? a + 2 * Math.PI : a; }
        for (int g = 0; g < 13; g++)
        {
            double a0 = Rel(g), a1 = g + 1 < 13 ? Rel(g + 1) : 2 * Math.PI;
            Assert.True(a1 > a0, "disks must come in perimeter order");
            double mid = (a0 + a1) / 2;
            Assert.Equal(g, s.Gate(Math.Cos(mid), Math.Sin(mid)));
        }
    }

    [Fact]
    public void Overlap_Warning_Matches_The_Nearest_Neighbour_Distance()
    {
        int warned = 0, open = 0;
        foreach (int k in new[] { 3, 4, 6, 8, 12 })
            foreach (int n in new[] { k, k + 3, 2 * k, 40 })
                foreach (double r in new[] { 0.05, 0.15, 0.3, 0.6 })
                {
                    var s = Scene(n, k, r);
                    double nearest = double.MaxValue;
                    for (int i = 0; i < s.DiskCount; i++)
                        for (int j = i + 1; j < s.DiskCount; j++)
                            nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(s.DiskX(i) - s.DiskX(j), 2) + Math.Pow(s.DiskY(i) - s.DiskY(j), 2)));
                    bool closed = nearest <= 2 * r;
                    bool warns = BilliardLayout.Warning(BilliardGeometry.Polygon, n, r, 1.5, null, k).Length > 0;
                    Assert.Equal(closed, warns);
                    if (warns) warned++; else open++;
                }
        Assert.True(warned > 10 && open > 10, $"warned {warned}, open {open}");
    }

    [Theory]
    [InlineData(FractalType.ChaoticBilliard)]
    [InlineData(FractalType.BilliardRays)]
    public void Sides_Round_Trip_The_Region_Snapshot_Only_For_Polygon(FractalType type)
    {
        var p = new FractalParameters { BilliardGeometry = BilliardGeometry.Polygon, BilliardPolygonSides = 8, BilliardDiskCount = 16 };
        var snap = RegionFractalParams.Snapshot(type, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardGeometry.Polygon, q.BilliardGeometry);
        Assert.Equal(8, q.BilliardPolygonSides);
        Assert.Contains(snap.ToKeyValues(), kv => kv.Key == "BilliardPolygonSides" && kv.Value == "8");
        p.BilliardGeometry = BilliardGeometry.Ring;
        Assert.Null(RegionFractalParams.Snapshot(type, p)!.BilliardPolygonSides);
    }

    [Fact]
    public void Panel_Shows_Sides_For_Polygon_And_Clamps_It()
    {
        var vm = new FractalParamsViewModel(FractalType.ChaoticBilliard, new FractalParameters());
        Assert.False(vm.IsBilliardPolygon);
        vm.BilliardGeometry = BilliardGeometry.Polygon;
        Assert.True(vm.IsBilliardPolygon);
        vm.BilliardPolygonSides = 40;
        Assert.Equal(BilliardLayout.MaxSides, vm.BilliardPolygonSides);
        vm.BilliardPolygonSides = 1;
        Assert.Equal(BilliardLayout.MinSides, vm.BilliardPolygonSides);
        vm.BilliardDiskCount = 2;   // fewer than 3 corners
        Assert.True(vm.HasBilliardLayoutWarning);
    }

    // ── #1540 ──

    [Fact]
    public void Angles_Keep_Their_Full_Double()
    {
        const double a = 33.1234567890123;
        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        vm.BilliardRaysAngle = a;
        vm.BilliardRaysSpread = 12.0000000001;
        Assert.Equal(a, vm.BilliardRaysAngle);
        var p = new FractalParameters { BilliardRaysAngle = a };
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!))!.ApplyTo(q);
        Assert.Equal(a, q.BilliardRaysAngle);
    }

    [Fact]
    public void Panel_Shows_Ten_Decimals_For_The_Angles_And_Source()
    {
        string axaml = File.ReadAllText(Path.Combine(RepoRoot(), "UI.Avalonia", "Views", "ParamSections", "ProceduralParamsView.axaml"));
        foreach (var binding in new[] { "BilliardRaysAngle", "BilliardRaysSpread", "BilliardRaysSourceX", "BilliardRaysSourceY" })
        {
            var m = Regex.Match(axaml, "FormatString=\"([^\"]*)\"\\s*\\r?\\n\\s*Value=\"\\{Binding " + binding + ",");
            Assert.True(m.Success, $"{binding} control not found");
            string fmt = m.Groups[1].Value;
            Assert.Equal("0.##########", fmt);
            // What the box shows parses back to the value to 1e-10.
            foreach (double v in new[] { 33.1234567891, -0.0000000001, 359.9999999999, 12.5 })
            {
                string shown = v.ToString(fmt, CultureInfo.InvariantCulture);
                Assert.Equal(v, double.Parse(shown, CultureInfo.InvariantCulture), 10);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
