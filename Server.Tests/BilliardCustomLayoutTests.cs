// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1504 — the Custom billiard geometry and the ray-path inset layout editor. Oracles:
//   • a Custom list made from a generated layout's disks renders the same frame;
//   • Custom exit gates agree with a brute-force "which angular gap between disk
//     centres is this direction in", computed from the parsed list, not the scene;
//   • the disk list survives the region snapshot and the --param command line;
//   • editor gestures obey BilliardLayout.CanPlace, checked by an independent
//     pairwise scan after every edit.
public class BilliardCustomLayoutTests
{
    private static List<BilliardDisk> DisksOf(FractalParameters p)
    {
        var s = BilliardScene.Build(p);
        return Enumerable.Range(0, s.DiskCount).Select(i => new BilliardDisk(s.DiskX(i), s.DiskY(i), s.DiskR(i))).ToList();
    }

    private static (uint[] Color, float[] Smooth) Render(FractalParameters p)
    {
        var calc = new ChaoticBilliardCalculator(80, 56) { FractalParameters = p, Zoom = 1.3 };
        calc.Calculate(TestContext.Current.CancellationToken);
        return (calc.ColorBuffer.ToArray(), calc.SmoothBuffer.ToArray());
    }

    public static IEnumerable<object[]> Sources()
    {
        foreach (var g in new[] { BilliardGeometry.ThreeDisk, BilliardGeometry.Ring })
            foreach (var launch in new[] { BilliardLaunchMode.FarField, BilliardLaunchMode.PhaseSpace })
                foreach (var gate in new[] { BilliardGateMode.ExitGap, BilliardGateMode.Sector })
                    yield return new object[] { g, launch, gate };
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Custom_From_A_Generated_Layout_Renders_The_Same_Frame(BilliardGeometry g, BilliardLaunchMode launch, BilliardGateMode gate)
    {
        var src = new FractalParameters
        {
            BilliardGeometry = g, BilliardDiskCount = 5, BilliardLaunchMode = launch, BilliardGateMode = gate,
        };
        var custom = src.Clone();
        custom.BilliardGeometry = BilliardGeometry.Custom;
        custom.BilliardCustomDisks = BilliardLayout.FormatCustomDisks(DisksOf(src));
        var a = Render(src);
        var b = Render(custom);
        Assert.Equal(a.Color, b.Color);
        Assert.Equal(a.Smooth, b.Smooth);
    }

    [Fact]
    public void Empty_Custom_Falls_Back_To_ThreeDisk()
    {
        var three = new FractalParameters { BilliardGeometry = BilliardGeometry.ThreeDisk };
        var custom = new FractalParameters { BilliardGeometry = BilliardGeometry.Custom, BilliardCustomDisks = "" };
        Assert.Equal(Render(three).Color, Render(custom).Color);
    }

    [Fact]
    public void Custom_Exit_Gates_Are_The_Angular_Gaps_Between_Disk_Centres()
    {
        var rng = new Random(1504);
        int checkedDirs = 0;
        for (int trial = 0; trial < 40; trial++)
        {
            int n = 2 + rng.Next(7);
            var disks = new List<BilliardDisk>();
            while (disks.Count < n)
            {
                double a = rng.NextDouble() * 2 * Math.PI, d = 0.5 + rng.NextDouble() * 2.5;
                var c = new BilliardDisk(d * Math.Cos(a), d * Math.Sin(a), 0.1 + rng.NextDouble() * 0.3);
                if (BilliardLayout.CanPlace(disks, disks.Count, c)) disks.Add(c);
            }
            var p = new FractalParameters
            {
                BilliardGeometry = BilliardGeometry.Custom, BilliardGateMode = BilliardGateMode.ExitGap,
                BilliardCustomDisks = BilliardLayout.FormatCustomDisks(disks),
            };
            var scene = BilliardScene.Build(p);
            Assert.Equal(n, scene.GateCount);
            // Disk 0 of the list is disk 0 of the scene (the PhaseSpace launch disk).
            Assert.Equal(disks[0].X, scene.DiskX(0));
            Assert.Equal(disks[0].Y, scene.DiskY(0));

            // Brute force from the list: rank the centres by angle from disk 0.
            double a0 = Math.Atan2(disks[0].Y, disks[0].X);
            double Rel(double a) { double r = (a - a0) % (2 * Math.PI); return r < 0 ? r + 2 * Math.PI : r; }
            var rels = disks.Select(d => Rel(Math.Atan2(d.Y, d.X))).OrderBy(r => r).ToArray();
            for (int k = 0; k < 200; k++)
            {
                double dir = rng.NextDouble() * 2 * Math.PI;
                double rel = Rel(dir);
                int expect = Array.FindLastIndex(rels, r => r <= rel);
                // Skip directions within 1e-9 of a boundary: either side is right there.
                if (rels.Any(r => Math.Abs(r - rel) < 1e-9)) continue;
                Assert.Equal(expect, scene.Gate(Math.Cos(dir), Math.Sin(dir)));
                checkedDirs++;
            }

            // The inset's sectors tile the circle in gate order.
            var inset = BilliardRayInspector.Scene(p, null);
            Assert.True(inset.Custom);
            Assert.Equal(2 * Math.PI, inset.GateWidths.Sum(), 9);
            for (int g = 0; g < n; g++)
            {
                double mid = inset.GateStartAngles[g] + inset.GateWidths[g] / 2;
                if (inset.GateWidths[g] > 1e-6) Assert.Equal(g, scene.Gate(Math.Cos(mid), Math.Sin(mid)));
            }
        }
        Assert.True(checkedDirs > 5000);
    }

    [Fact]
    public void Disk_List_Parses_Back_Exactly_And_Skips_Bad_Entries()
    {
        var disks = new List<BilliardDisk> { new(0.1, -2.0 / 3.0, 0.3), new(Math.PI, 1e-17, 1.25), new(-1.5, 0.75, 0.05) };
        Assert.Equal(disks, BilliardLayout.ParseCustomDisks(BilliardLayout.FormatCustomDisks(disks)));
        var parsed = BilliardLayout.ParseCustomDisks(" 1,2,0.5 ; bad ; 1,2 ; 3,4,-1 ; 5,NaN,1 ; 6,7,0.25 ;");
        Assert.Equal(new List<BilliardDisk> { new(1, 2, 0.5), new(6, 7, 0.25) }, parsed);
        var many = string.Join(";", Enumerable.Range(0, 100).Select(i => $"{i},0,0.1"));
        Assert.Equal(BilliardLayout.MaxDisks, BilliardLayout.ParseCustomDisks(many).Count);
    }

    [Fact]
    public void Custom_Layout_Round_Trips_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.Custom,
            BilliardCustomDisks = BilliardLayout.FormatCustomDisks(new List<BilliardDisk> { new(0.5, 0.25, 0.3), new(-1.0 / 3.0, 1.1, 0.45) }),
        };
        var snap = RegionFractalParams.Snapshot(FractalType.ChaoticBilliard, p)!;
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(dst);
        Assert.Equal(BilliardGeometry.Custom, dst.BilliardGeometry);
        Assert.Equal(p.BilliardCustomDisks, dst.BilliardCustomDisks);

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.ChaoticBilliard, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.ChaoticBilliard, out var perr)!.ApplyTo(fresh);
        Assert.Null(perr);
        Assert.Equal(BilliardGeometry.Custom, fresh.BilliardGeometry);
        Assert.Equal(p.BilliardCustomDisks, fresh.BilliardCustomDisks);
        Assert.Equal(Render(p).Color, Render(fresh).Color);

        // Not captured for a generated geometry, even with a kept custom list.
        p.BilliardGeometry = BilliardGeometry.Ring;
        Assert.Null(RegionFractalParams.Snapshot(FractalType.ChaoticBilliard, p)!.BilliardCustomDisks);
    }

    // A host wired the way AvaloniaShellBootstrap wires it, over one parameter set.
    private sealed class Host
    {
        public readonly FractalParameters P = new() { BilliardGeometry = BilliardGeometry.ThreeDisk, BilliardLaunchMode = BilliardLaunchMode.PhaseSpace };
        public readonly List<bool> Applies = new();
        public readonly BilliardRayInsetViewModel Vm;
        public Host()
        {
            Vm = new BilliardRayInsetViewModel
            {
                GetScene = () => BilliardRayInspector.Scene(P, null),
                TraceAtPlane = (x, y) => BilliardRayInspector.Trace(P, null, x, y),
                TraceAtPointer = (x, y, w, h) => BilliardRayInspector.TraceAt(P, null, x, y, w, h, 0, 0, 1),
                ApplyLayout = (disks, commit) =>
                {
                    P.BilliardCustomDisks = BilliardLayout.FormatCustomDisks(disks);
                    P.BilliardGeometry = BilliardGeometry.Custom;
                    Applies.Add(commit);
                },
                NewDiskRadius = () => P.BilliardDiskRadius,
            };
            Vm.Refresh();
        }
        public List<BilliardDisk> Disks => Vm.Scene!.Disks.ToList();
    }

    [Fact]
    public void Dragging_A_Disk_Moves_It_Previews_Then_Commits()
    {
        var h = new Host();
        var d0 = h.Disks[0];

        // A click without a move changes nothing.
        Assert.True(h.Vm.BeginDrag(d0.X, d0.Y));
        h.Vm.EndDrag();
        Assert.Empty(h.Applies);
        Assert.Equal(BilliardGeometry.ThreeDisk, h.P.BilliardGeometry);

        // Grab off-centre: the disk keeps the grab offset.
        Assert.True(h.Vm.BeginDrag(d0.X + 0.1, d0.Y));
        h.Vm.DragTo(d0.X + 0.1, d0.Y + 0.4);
        Assert.Equal(new[] { false }, h.Applies);
        Assert.Equal(BilliardGeometry.Custom, h.P.BilliardGeometry);
        var moved = h.Disks.Single(d => Math.Abs(d.X - d0.X) < 1e-12 && Math.Abs(d.Y - (d0.Y + 0.4)) < 1e-12);
        Assert.Equal(d0.R, moved.R);
        h.Vm.EndDrag();
        Assert.Equal(new[] { false, true }, h.Applies);
        Assert.False(h.Vm.IsDragging);
    }

    [Fact]
    public void Drag_Into_Another_Disk_Is_Refused()
    {
        var h = new Host();
        var d = h.Disks;
        Assert.True(h.Vm.BeginDrag(d[0].X, d[0].Y));
        h.Vm.DragTo(d[1].X, d[1].Y);   // onto disk 1
        h.Vm.EndDrag();
        Assert.Empty(h.Applies);
        Assert.Equal(d, h.Disks);
    }

    [Fact]
    public void Wheel_Add_And_Remove_Edit_The_Layout()
    {
        var h = new Host();
        h.P.BilliardDiskRadius = 0.2;   // ThreeDisk at radius 0.2; added disks take it too
        h.Vm.Refresh();
        var d = h.Disks;

        Assert.True(h.Vm.ResizeAt(d[2].X, d[2].Y, -2));
        Assert.Equal(d[2].R * Math.Pow(BilliardRayInsetViewModel.WheelRadiusStep, -2), h.Disks.Single(x => x.X == d[2].X && x.Y == d[2].Y).R, 12);
        Assert.False(h.Vm.ResizeAt(5, 5, 1));                         // nothing there

        int before = h.Disks.Count;
        Assert.True(h.Vm.AddAt(0, -2.6));
        Assert.Equal(before + 1, h.Disks.Count);
        Assert.Contains(new BilliardDisk(0, -2.6, 0.2), h.Disks);
        Assert.False(h.Vm.AddAt(d[0].X, d[0].Y));                     // on a disk

        Assert.True(h.Vm.RemoveAt(0, -2.6));
        Assert.Equal(before, h.Disks.Count);
        while (h.Disks.Count > 1) Assert.True(h.Vm.RemoveAt(h.Disks[^1].X, h.Disks[^1].Y));
        Assert.False(h.Vm.RemoveAt(h.Disks[0].X, h.Disks[0].Y));      // the last disk stays
        Assert.All(h.Applies, Assert.True);
    }

    [Fact]
    public void Random_Edits_Keep_Every_Layout_Rule()
    {
        var h = new Host();
        var rng = new Random(15040);
        for (int step = 0; step < 600; step++)
        {
            double x = rng.NextDouble() * 12 - 6, y = rng.NextDouble() * 12 - 6;
            switch (rng.Next(4))
            {
                case 0:
                    var d = h.Disks[rng.Next(h.Disks.Count)];
                    if (h.Vm.BeginDrag(d.X, d.Y)) { h.Vm.DragTo(x, y); h.Vm.DragTo(x * 0.5, y * 0.5); h.Vm.EndDrag(); }
                    break;
                case 1: h.Vm.ResizeAt(x, y, rng.Next(-3, 4)); break;
                case 2: h.Vm.AddAt(x, y); break;
                default: if (rng.Next(3) == 0) h.Vm.RemoveAt(x, y); break;
            }
            var disks = BilliardLayout.ParseCustomDisks(h.P.BilliardCustomDisks);
            for (int i = 0; i < disks.Count; i++)
            {
                Assert.InRange(disks[i].R, BilliardLayout.MinRadius, BilliardLayout.MaxRadius);
                Assert.True(Math.Sqrt(disks[i].X * disks[i].X + disks[i].Y * disks[i].Y) + disks[i].R <= BilliardLayout.MaxReach);
                for (int j = i + 1; j < disks.Count; j++)
                {
                    double dist = Math.Sqrt(Math.Pow(disks[i].X - disks[j].X, 2) + Math.Pow(disks[i].Y - disks[j].Y, 2));
                    Assert.True(dist >= (disks[i].R + disks[j].R) * BilliardLayout.MinGapFactor * (1 - 1e-12),
                                $"step {step}: disks {i},{j} closer than the escape gap");
                }
            }
        }
        Assert.True(h.Applies.Count > 100);
    }

    [Fact]
    public void Pins_Retrace_On_The_Same_Launch_When_A_Disk_Moves()
    {
        var h = new Host();
        Assert.True(h.Vm.Pin(new PointerInput(40, 30, 100, 80, PointerButton.Left, InputModifiers.Control)));
        var before = h.Vm.Pins[0];
        var d = h.Disks[1];
        Assert.True(h.Vm.BeginDrag(d.X, d.Y));
        h.Vm.DragTo(d.X + 0.3, d.Y - 0.3);
        var during = h.Vm.Pins[0];
        Assert.Equal(before.PlaneX, during.PlaneX);
        Assert.Equal(before.PlaneY, during.PlaneY);
        Assert.Equal(BilliardRayInspector.Trace(h.P, null, before.PlaneX, before.PlaneY).Points, during.Points);
        h.Vm.EndDrag();
    }

    [Fact]
    public void Panel_Warns_About_Touching_Custom_Disks()
    {
        Assert.Contains("No custom layout", BilliardLayout.Warning(BilliardGeometry.Custom, 5, 0.8, 1.0, ""));
        Assert.Empty(BilliardLayout.Warning(BilliardGeometry.Custom, 5, 0.8, 1.0, "0,0,0.5;2,0,0.5"));
        Assert.Contains("touch or overlap", BilliardLayout.Warning(BilliardGeometry.Custom, 5, 0.8, 1.0, "0,0,0.5;0.9,0,0.5"));
    }
}
