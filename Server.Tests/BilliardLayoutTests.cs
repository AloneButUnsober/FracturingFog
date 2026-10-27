// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.ComponentModel;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1503 — NDisk used to keep however many disks fitted a fixed box. Oracles:
//   • the scene holds exactly the requested count, with every pair at least the
//     escape gap apart (checked from the scene's own disks, not the placer);
//   • a field the old fixed-box placer already fitted is unchanged (the old placer
//     is reproduced here verbatim as the reference);
//   • every disk lies inside the play circle;
//   • the Ring overlap warning agrees with the distance between the scene's
//     neighbouring disks.
public class BilliardLayoutTests
{
    private static readonly double[] Radii = { 0.05, 0.3, 0.8, 1.2, 1.5 };
    private static readonly double[] Seps = { 0.1, 1.0, 3.0 };

    [Fact]
    public void NDisk_Places_Every_Requested_Disk_With_An_Escape_Gap()
    {
        foreach (double r in Radii)
            foreach (double sep in Seps)
                foreach (int seed in new[] { 0, 1, 1503 })
                    for (int n = 1; n <= 64; n += (n < 8 ? 1 : 7))
                    {
                        var s = BilliardScene.Build(new FractalParameters
                        {
                            BilliardGeometry = BilliardGeometry.NDisk,
                            BilliardDiskCount = n, BilliardDiskRadius = r,
                            BilliardSeparation = sep, BilliardSeed = seed,
                        });
                        Assert.Equal(n, s.DiskCount);
                        double min = 2.0 * r * BilliardScene.NDiskGapFactor;
                        for (int i = 0; i < n; i++)
                            for (int j = i + 1; j < n; j++)
                            {
                                double d = Math.Sqrt(Sq(s.DiskX(i) - s.DiskX(j)) + Sq(s.DiskY(i) - s.DiskY(j)));
                                Assert.True(d >= min * (1 - 1e-12), $"n={n} r={r} sep={sep} seed={seed}: disks {i},{j} {d} apart");
                            }
                    }
    }

    [Fact]
    public void Fields_The_Old_Placer_Fitted_Are_Unchanged()
    {
        int fitted = 0, grown = 0;
        foreach (double r in Radii)
            foreach (double sep in Seps)
                foreach (int seed in new[] { 0, 7, 1503 })
                    for (int n = 1; n <= 64; n++)
                    {
                        var (ox, oy) = OldPlacer(n, r, sep, seed);
                        var (nx, ny) = BilliardScene.RandomCentres(n, r, sep, seed);
                        if (ox.Count == n)
                        {
                            fitted++;
                            Assert.Equal(ox, nx);
                            Assert.Equal(oy, ny);
                        }
                        else grown++;
                    }
        // The sweep must exercise both branches to mean anything.
        Assert.True(fitted > 50 && grown > 50, $"fitted {fitted}, grown {grown}");
    }

    [Fact]
    public void Defaults_Now_Place_More_Than_The_Old_Stall()
    {
        // The reported case: radius 0.8, separation 1.0 — the old box stalled well short of 10.
        var (ox, _) = OldPlacer(10, 0.8, 1.0, 0);
        Assert.True(ox.Count < 10);
        var s = BilliardScene.Build(new FractalParameters { BilliardGeometry = BilliardGeometry.NDisk, BilliardDiskCount = 10 });
        Assert.Equal(10, s.DiskCount);
    }

    [Fact]
    public void Every_Disk_Lies_Inside_The_Play_Circle()
    {
        foreach (var g in new[] { BilliardGeometry.ThreeDisk, BilliardGeometry.Ring, BilliardGeometry.NDisk })
            foreach (double r in Radii)
                foreach (double sep in Seps)
                    foreach (int n in new[] { 1, 2, 5, 17, 64 })
                    {
                        var s = BilliardScene.Build(new FractalParameters
                        {
                            BilliardGeometry = g, BilliardDiskCount = n,
                            BilliardDiskRadius = r, BilliardSeparation = sep,
                        });
                        for (int i = 0; i < s.DiskCount; i++)
                        {
                            double edge = Math.Sqrt(Sq(s.DiskX(i)) + Sq(s.DiskY(i))) + s.DiskR(i);
                            Assert.True(edge < s.PlayRadius, $"{g} n={n} r={r} sep={sep}: disk {i} edge {edge} >= play {s.PlayRadius}");
                        }
                    }
    }

    [Fact]
    public void Ring_Play_Circle_Is_The_Closed_Form()
    {
        // Ring / ThreeDisk renders stay byte-identical: their play circle is unchanged.
        foreach (double r in Radii)
            foreach (double sep in Seps)
            {
                var s = BilliardScene.Build(new FractalParameters
                {
                    BilliardGeometry = BilliardGeometry.Ring, BilliardDiskCount = 9,
                    BilliardDiskRadius = r, BilliardSeparation = sep,
                });
                Assert.Equal(Math.Max(3.0, sep * 1.5 + r * 2.0), s.PlayRadius);
            }
    }

    [Fact]
    public void Ring_Warning_Matches_The_Scene_Geometry()
    {
        int warned = 0, open = 0;
        foreach (var g in new[] { BilliardGeometry.ThreeDisk, BilliardGeometry.Ring })
            foreach (double r in Radii)
                foreach (double sep in Seps)
                    for (int n = 2; n <= 64; n += 3)
                    {
                        var s = BilliardScene.Build(new FractalParameters
                        {
                            BilliardGeometry = g, BilliardDiskCount = n,
                            BilliardDiskRadius = r, BilliardSeparation = sep,
                        });
                        double d = Math.Sqrt(Sq(s.DiskX(0) - s.DiskX(1)) + Sq(s.DiskY(0) - s.DiskY(1)));
                        bool closed = d <= s.DiskR(0) + s.DiskR(1);
                        bool warns = BilliardLayout.Warning(g, n, r, sep).Length > 0;
                        Assert.Equal(closed, warns);
                        if (warns) warned++; else open++;
                    }
        Assert.True(warned > 10 && open > 10, $"warned {warned}, open {open}");
        Assert.Empty(BilliardLayout.Warning(BilliardGeometry.NDisk, 64, 1.5, 0.1));   // not a ring
    }

    [Fact]
    public void Params_Panel_Raises_The_Warning_When_The_Ring_Closes()
    {
        var p = new FractalParameters { BilliardGeometry = BilliardGeometry.Ring, BilliardDiskCount = 4, BilliardDiskRadius = 0.3, BilliardSeparation = 1.0 };
        var vm = new FractalParamsViewModel(FractalType.ChaoticBilliard, p);
        Assert.False(vm.HasBilliardLayoutWarning);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.BilliardDiskCount = 40;
        Assert.True(vm.HasBilliardLayoutWarning);
        Assert.Contains(nameof(FractalParamsViewModel.HasBilliardLayoutWarning), raised);
        vm.BilliardDiskRadius = 0.05;
        Assert.False(vm.HasBilliardLayoutWarning);
    }

    // The pre-#1503 NDisk placer, verbatim: fixed box, gives up after 200 n attempts.
    private static (List<double> X, List<double> Y) OldPlacer(int n, double diskR, double sep, int seed)
    {
        var rng = new Random(seed);
        var xs = new List<double>(n);
        var ys = new List<double>(n);
        double half = Math.Max(diskR * 2.0, sep);
        int attempts = 0, cap = n * 200;
        while (xs.Count < n && attempts++ < cap)
        {
            double x = (rng.NextDouble() * 2.0 - 1.0) * half;
            double y = (rng.NextDouble() * 2.0 - 1.0) * half;
            bool ok = true;
            for (int e = 0; e < xs.Count; e++)
            {
                double dx = x - xs[e], dy = y - ys[e];
                double min = (diskR + diskR) * 1.05;
                if (dx * dx + dy * dy < min * min) { ok = false; break; }
            }
            if (ok) { xs.Add(x); ys.Add(y); }
        }
        return (xs, ys);
    }

    private static double Sq(double v) => v * v;
}
