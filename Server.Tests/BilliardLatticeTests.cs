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

// #1539 — lattice geometries (SquareLattice / HexLattice) and the filled Polygon.
// Oracles from the grids themselves: a centre is a lattice site when undoing the rotation
// and the basis gives integers; the n-nearest patch is checked against a brute-force list
// of sites; the fill against the closed-form site counts of the triangle (triangular
// numbers), square (squares) and hexagon (centred hexagonal numbers).
public class BilliardLatticeTests
{
    private static readonly double H = Math.Sqrt(3.0) / 2.0;

    private static FractalParameters Params(BilliardGeometry g, int n, double sep = 1.0, double r = 0.3,
                                            double rot = 0.0, int sides = 6, bool fill = false,
                                            BilliardGateMode gate = BilliardGateMode.ExitGap)
        => new()
        {
            BilliardGeometry = g, BilliardDiskCount = n, BilliardSeparation = sep, BilliardDiskRadius = r,
            BilliardLatticeRotation = rot, BilliardPolygonSides = sides, BilliardPolygonFill = fill,
            BilliardGateMode = gate, BilliardGateCount = 8,
        };

    private static List<(double X, double Y)> Centres(BilliardScene s)
        => Enumerable.Range(0, s.DiskCount).Select(i => (s.DiskX(i), s.DiskY(i))).ToList();

    private static double Dist((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Nearest(List<(double X, double Y)> c)
    {
        double m = double.MaxValue;
        for (int i = 0; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++) m = Math.Min(m, Dist(c[i], c[j]));
        return m;
    }

    // Grid coordinates of a world point, or null when it is not a site.
    private static (int I, int J)? ToSite(bool hex, (double X, double Y) p, double sep, double rotDeg)
    {
        double t = -rotDeg * Math.PI / 180.0;
        double x = (Math.Cos(t) * p.X - Math.Sin(t) * p.Y) / sep, y = (Math.Sin(t) * p.X + Math.Cos(t) * p.Y) / sep;
        double j = hex ? y / H : y, i = hex ? x - 0.5 * j : x;
        long ri = (long)Math.Round(i), rj = (long)Math.Round(j);
        return Math.Abs(i - ri) < 1e-9 && Math.Abs(j - rj) < 1e-9 ? ((int)ri, (int)rj) : null;
    }

    private static double SiteLength(bool hex, int i, int j)
        => hex ? Math.Sqrt(Math.Pow(i + 0.5 * j, 2) + Math.Pow(j * H, 2)) : Math.Sqrt(i * i + j * j);

    [Theory]
    [InlineData(BilliardGeometry.SquareLattice, 1, 0.0)]
    [InlineData(BilliardGeometry.SquareLattice, 13, 0.0)]
    [InlineData(BilliardGeometry.SquareLattice, 50, 17.5)]
    [InlineData(BilliardGeometry.HexLattice, 7, 0.0)]
    [InlineData(BilliardGeometry.HexLattice, 40, -33.0)]
    [InlineData(BilliardGeometry.HexLattice, 256, 90.0)]
    public void Lattice_Centres_Are_The_N_Sites_Nearest_The_Origin(BilliardGeometry g, int n, double rot)
    {
        bool hex = g == BilliardGeometry.HexLattice;
        const double sep = 1.25;
        var s = BilliardScene.Build(Params(g, n, sep, 0.2, rot));
        Assert.Equal(n, s.DiskCount);
        var sites = Centres(s).Select(c => ToSite(hex, c, sep, rot)).ToList();
        Assert.All(sites, v => Assert.True(v.HasValue, "centre off the lattice"));
        var set = sites.Select(v => v!.Value).ToHashSet();
        Assert.Equal(n, set.Count);                           // no site twice
        Assert.Equal((0, 0), sites[0]!.Value);                // disk 0 is the origin site
        // Brute force: no site left out is nearer the origin than one taken.
        double far = set.Max(v => SiteLength(hex, v.I, v.J));
        for (int i = -40; i <= 40; i++)
            for (int j = -40; j <= 40; j++)
                if (!set.Contains((i, j)))
                    Assert.True(SiteLength(hex, i, j) >= far - 1e-12, $"site ({i},{j}) skipped");
        if (n >= 2) Assert.Equal(sep, Nearest(Centres(s)), 9);
    }

    [Theory]
    [InlineData(BilliardGeometry.SquareLattice, 5)]
    [InlineData(BilliardGeometry.SquareLattice, 9)]
    [InlineData(BilliardGeometry.SquareLattice, 21)]
    [InlineData(BilliardGeometry.HexLattice, 7)]
    [InlineData(BilliardGeometry.HexLattice, 19)]
    [InlineData(BilliardGeometry.HexLattice, 61)]
    public void Complete_Shells_Are_Symmetric(BilliardGeometry g, int n)
    {
        var c = Centres(BilliardScene.Build(Params(g, n)));
        Assert.Equal(0.0, c.Sum(p => p.X), 9);
        Assert.Equal(0.0, c.Sum(p => p.Y), 9);
        // Turning by the grid's own symmetry (90 or 60 degrees) maps the patch onto itself.
        double t = g == BilliardGeometry.HexLattice ? Math.PI / 3 : Math.PI / 2;
        foreach (var p in c)
        {
            var q = (Math.Cos(t) * p.X - Math.Sin(t) * p.Y, Math.Sin(t) * p.X + Math.Cos(t) * p.Y);
            Assert.Contains(c, o => Dist(o, q) < 1e-9);
        }
    }

    [Fact]
    public void Rotation_Turns_Every_Centre()
    {
        var a = Centres(BilliardScene.Build(Params(BilliardGeometry.HexLattice, 30)));
        var b = Centres(BilliardScene.Build(Params(BilliardGeometry.HexLattice, 30, rot: 25.0)));
        double t = 25.0 * Math.PI / 180.0;
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(Math.Cos(t) * a[i].X - Math.Sin(t) * a[i].Y, b[i].X, 12);
            Assert.Equal(Math.Sin(t) * a[i].X + Math.Cos(t) * a[i].Y, b[i].Y, 12);
        }
    }

    [Fact]
    public void Lattices_Use_Sector_Gates()
    {
        var s = BilliardScene.Build(Params(BilliardGeometry.SquareLattice, 25));
        Assert.False(s.ExitGap);
        Assert.Equal(8, s.GateCount);
        // The play circle still covers every disk (#1503).
        foreach (var p in Centres(s)) Assert.True(Math.Sqrt(p.X * p.X + p.Y * p.Y) + 0.3 < s.PlayRadius);
    }

    [Theory]
    [InlineData(BilliardGeometry.SquareLattice)]
    [InlineData(BilliardGeometry.HexLattice)]
    public void Lattice_Warning_Matches_The_Nearest_Neighbour_Distance(BilliardGeometry g)
    {
        int warned = 0, open = 0;
        foreach (double sep in new[] { 0.5, 0.8, 1.0, 1.6 })
            foreach (double r in new[] { 0.2, 0.4, 0.5, 0.79 })
            {
                var s = BilliardScene.Build(Params(g, 20, sep, r));
                bool closed = Nearest(Centres(s)) <= 2 * r;
                bool warns = BilliardLayout.Warning(g, 20, r, sep).Length > 0;
                Assert.Equal(closed, warns);
                if (warns) warned++; else open++;
            }
        Assert.True(warned > 3 && open > 3, $"warned {warned}, open {open}");
        Assert.Equal(string.Empty, BilliardLayout.Warning(g, 1, 0.8, 0.5));   // one disk cannot touch another
    }

    // ── Fill ──

    // Disks per edge m + 1 = q gaps: the full lattice patch holds T(q + 1), (q + 1)^2 or the
    // centred hexagonal number 3q(q + 1) + 1 sites.
    [Theory]
    [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 5)]
    [InlineData(4, 1)] [InlineData(4, 3)] [InlineData(4, 7)]
    [InlineData(6, 1)] [InlineData(6, 2)] [InlineData(6, 4)]
    public void Fill_Of_A_Tiling_Polygon_Is_The_Whole_Lattice_Patch(int k, int q)
    {
        int n = k * q;   // q - 1 disks between corners on every edge
        int expected = k switch { 3 => (q + 1) * (q + 2) / 2, 4 => (q + 1) * (q + 1), _ => 3 * q * (q + 1) + 1 };
        var s = BilliardScene.Build(Params(BilliardGeometry.Polygon, n, sep: 2.0, r: 0.05, sides: k, fill: true));
        Assert.Equal(expected, s.DiskCount);
        var c = Centres(s);
        double side = 2 * 2.0 * Math.Sin(Math.PI / k);
        Assert.Equal(side / q, Nearest(c), 9);
        Assert.All(c, p => Assert.True(InwardDistance(p, k, 2.0) > -1e-9, "disk outside the polygon"));
        Assert.Equal(c.Count, c.Distinct().Count());
    }

    private static double InwardDistance((double X, double Y) p, int k, double sep)
    {
        double apothem = sep * Math.Cos(Math.PI / k), m = double.MaxValue;
        for (int e = 0; e < k; e++)
        {
            double a = (e + 0.5) * 2 * Math.PI / k;
            m = Math.Min(m, apothem - (p.X * Math.Cos(a) + p.Y * Math.Sin(a)));
        }
        return m;
    }

    [Theory]
    [InlineData(5, 15)] [InlineData(7, 21)] [InlineData(8, 16)] [InlineData(8, 40)]
    [InlineData(9, 30)] [InlineData(12, 36)] [InlineData(6, 20)]
    public void Fill_Keeps_The_Perimeter_Spacing_And_Stays_Inside(int k, int n)
    {
        var perim = Centres(BilliardScene.Build(Params(BilliardGeometry.Polygon, n, sep: 2.0, r: 0.05, sides: k)));
        var s = BilliardScene.Build(Params(BilliardGeometry.Polygon, n, sep: 2.0, r: 0.05, sides: k, fill: true));
        var c = Centres(s);
        Assert.True(c.Count > perim.Count, "nothing filled");
        Assert.Equal(perim, c.Take(perim.Count));   // the perimeter is unchanged and first
        double side = 2 * 2.0 * Math.Sin(Math.PI / k), d = side / ((n - k) / k + 1);
        foreach (var p in c.Skip(perim.Count))
        {
            Assert.True(InwardDistance(p, k, 2.0) >= 0.5 * d - 1e-9, "interior disk too near an edge");
            foreach (var o in c) if (o != p) Assert.True(Dist(o, p) >= d * (1 - 1e-6), "interior disk crowds a neighbour");
        }
    }

    [Fact]
    public void Fill_Gates_Are_The_Perimeter_Gaps()
    {
        var plain = BilliardScene.Build(Params(BilliardGeometry.Polygon, 18, sep: 2.0, r: 0.1, sides: 6));
        var filled = BilliardScene.Build(Params(BilliardGeometry.Polygon, 18, sep: 2.0, r: 0.1, sides: 6, fill: true));
        Assert.True(filled.ExitGap);
        Assert.Equal(18, filled.GateCount);
        for (int g = 0; g < 18; g++)
        {
            Assert.Equal(plain.GateStartAngle(g), filled.GateStartAngle(g), 12);
            Assert.Equal(plain.GateWidth(g), filled.GateWidth(g), 12);
        }
    }

    [Fact]
    public void Fill_Past_The_Disk_Limit_Keeps_The_Centre_And_Warns()
    {
        // 6 sides x 10 gaps: 331 sites in all, more than the limit.
        var s = BilliardScene.Build(Params(BilliardGeometry.Polygon, 60, sep: 5.0, r: 0.05, sides: 6, fill: true));
        Assert.Equal(BilliardLayout.MaxDisks, s.DiskCount);
        Assert.Contains(Centres(s), p => Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9);   // the centre site is kept
        string w = BilliardLayout.Warning(BilliardGeometry.Polygon, 60, 0.05, 5.0, null, 6, polygonFill: true);
        Assert.Contains("331", w);
        Assert.Equal(string.Empty, BilliardLayout.Warning(BilliardGeometry.Polygon, 60, 0.05, 5.0, null, 6));
    }

    [Fact]
    public void Fill_Warning_Sees_Interior_Overlaps()
    {
        // 12 disks on a hexagon of corner radius 1: spacing 0.5, so r = 0.24 is open, r = 0.26 touches.
        Assert.Equal(string.Empty, BilliardLayout.Warning(BilliardGeometry.Polygon, 12, 0.24, 1.0, null, 6, true));
        Assert.NotEqual(string.Empty, BilliardLayout.Warning(BilliardGeometry.Polygon, 12, 0.26, 1.0, null, 6, true));
    }

    // ── Snapshot / panel ──

    [Theory]
    [InlineData(FractalType.ChaoticBilliard)]
    [InlineData(FractalType.BilliardRays)]
    public void Lattice_And_Fill_Round_Trip_The_Region_Snapshot(FractalType type)
    {
        var p = Params(BilliardGeometry.HexLattice, 37, rot: 12.5);
        var q = new FractalParameters();
        var snap = RegionFractalParams.Snapshot(type, p)!;
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(BilliardGeometry.HexLattice, q.BilliardGeometry);
        Assert.Equal(12.5, q.BilliardLatticeRotation);
        Assert.Equal(37, q.BilliardDiskCount);
        Assert.Contains(snap.ToKeyValues(), kv => kv.Key == "BilliardLatticeRotation");
        Assert.Null(snap.BilliardPolygonFill);

        p = Params(BilliardGeometry.Polygon, 12, fill: true);
        snap = RegionFractalParams.Snapshot(type, p)!;
        q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.True(q.BilliardPolygonFill);
        Assert.Null(snap.BilliardLatticeRotation);
        Assert.Contains(snap.ToKeyValues(), kv => kv.Key == "BilliardPolygonFill");
    }

    [Fact]
    public void Panel_Shows_Rotation_For_Lattices_And_Fill_For_Polygon()
    {
        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        vm.BilliardGeometry = BilliardGeometry.SquareLattice;
        Assert.True(vm.IsBilliardLattice);
        Assert.False(vm.IsBilliardPolygon);
        vm.BilliardLatticeRotation = 400;
        Assert.Equal(BilliardLayout.MaxLatticeRotation, vm.BilliardLatticeRotation);
        vm.BilliardDiskCount = 1000;
        Assert.Equal(BilliardLayout.MaxDisks, vm.BilliardDiskCount);
        vm.BilliardGeometry = BilliardGeometry.Polygon;
        Assert.False(vm.IsBilliardLattice);
        vm.BilliardDiskRadius = 0.05;
        vm.BilliardSeparation = 3.0;
        vm.BilliardDiskCount = 60;
        Assert.False(vm.HasBilliardLayoutWarning);
        vm.BilliardPolygonFill = true;                 // 331 sites: past the limit
        Assert.True(vm.HasBilliardLayoutWarning);
    }
}
