// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1506 — Billiard Rays S1 (tracker #1505). Oracles independent of the splatter:
//   • the grid total equals the traced paths' length inside the view, computed by
//     clipping the polylines to the view rectangle in world space (slab test here);
//   • no ray length lands inside a mirror (rays reflect at the surface);
//   • each sweep emits exactly the requested count, placed as its definition says;
//   • the image is the same from the live calculator and the poster renderer, and
//     does not depend on the core count;
//   • the parameters survive the region snapshot and --param.
public class BilliardRaysTests
{
    private const int W = 120, H = 90;

    private static BilliardRaysCalculator Render(FractalParameters p, double zoom = 0.6, double cx = 0, double cy = 0,
                                                 int w = W, int h = H)
    {
        var c = new BilliardRaysCalculator(w, h) { FractalParameters = p, Zoom = zoom, CenterX = cx, CenterY = cy };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    // Length of segment a-b inside [x0, x1] x [y0, y1] (slab method).
    private static double ClippedLength(BilliardPoint a, BilliardPoint b, double x0, double x1, double y0, double y1)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
        foreach (var (p0, d, mn, mx) in new[] { (a.X, dx, x0, x1), (a.Y, dy, y0, y1) })
        {
            if (Math.Abs(d) < 1e-300) { if (p0 < mn || p0 > mx) return 0; continue; }
            double ta = (mn - p0) / d, tb = (mx - p0) / d;
            lo = Math.Max(lo, Math.Min(ta, tb));
            hi = Math.Min(hi, Math.Max(ta, tb));
        }
        return hi > lo ? (hi - lo) * Math.Sqrt(dx * dx + dy * dy) : 0;
    }

    public static IEnumerable<object[]> Sweeps()
        => Enum.GetValues<BilliardRaySweep>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Sweeps))]
    public void Grid_Total_Is_The_Traced_Length_Inside_The_View(BilliardRaySweep sweep)
    {
        var p = new FractalParameters { BilliardRaysSweep = sweep, BilliardRaysCount = 3000, BilliardRaysSpread = 120, BilliardRaysMinBounces = 0 };
        double zoom = 0.9, cx = 0.3, cy = -0.2;
        var c = Render(p, zoom, cx, cy);

        // View rectangle in world space: pixel i spans plane [(i - 0.5 - W/2), (i + 0.5 - W/2)] pitch
        // about the centre; world y is minus plane y.
        double pitch = 4.0 / W / zoom;
        double x0 = cx - (W / 2.0 + 0.5) * pitch, x1 = cx + (W / 2.0 - 0.5) * pitch;
        double py0 = cy - (H / 2.0 + 0.5) * pitch, py1 = cy + (H / 2.0 - 0.5) * pitch;
        double y0 = -py1, y1 = -py0;

        var scene = BilliardScene.Build(p);
        double expect = 0;
        foreach (var l in BilliardRaysCalculator.BuildSweep(p, scene))
        {
            var path = new List<BilliardPoint>();
            var o = BilliardRaysCalculator.Launch(scene, l, path);
            if (!o.Valid) continue;
            for (int k = 1; k < path.Count; k++) expect += ClippedLength(path[k - 1], path[k], x0, x1, y0, y1);
        }
        Assert.True(expect > 100 * pitch, $"sweep {sweep} put almost nothing in view ({expect})");
        Assert.Equal(1.0, c.Density.Sum() / expect, 9);
    }

    [Fact]
    public void No_Ray_Length_Lands_Inside_A_Mirror()
    {
        var p = new FractalParameters { BilliardRaysCount = 20000, BilliardRaysMinBounces = 0, BilliardRaysAngleCount = 5 };
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        double pitch = 4.0 / W / 0.6;
        int inside = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (wx, wy) = BilliardRaysCalculator.WorldAt(x, y, W, H, 0, 0, 0.6);
                for (int k = 0; k < scene.DiskCount; k++)
                {
                    double d = Math.Sqrt(Math.Pow(wx - scene.DiskX(k), 2) + Math.Pow(wy - scene.DiskY(k), 2));
                    if (d < scene.DiskR(k) - pitch)   // the whole pixel is inside
                    {
                        inside++;
                        Assert.Equal(0.0, c.Density[y * W + x]);
                    }
                }
            }
        Assert.True(inside > 500);
    }

    [Fact]
    public void Picture_Is_Y_Up_Like_The_Inset()
    {
        // ThreeDisk disk 0 sits at the top (0, sep); the mirror point (0, -sep) is a gap.
        var c = Render(new FractalParameters { BilliardRaysCount = 10 });
        double pitch = 4.0 / W / 0.6;
        int Px(double wx) => (int)Math.Round(wx / pitch + W / 2.0);
        int Py(double wy) => (int)Math.Round(-wy / pitch + H / 2.0);
        Assert.Equal(BilliardRaysCalculator.DiskFillColor, c.ColorBuffer[Py(1.0) * W + Px(0)]);
        Assert.NotEqual(BilliardRaysCalculator.DiskFillColor, c.ColorBuffer[Py(-1.0) * W + Px(0)]);
        Assert.True(Py(1.0) < H / 2);

        var hidden = Render(new FractalParameters { BilliardRaysCount = 10, BilliardRaysShowDisks = false });
        Assert.NotEqual(BilliardRaysCalculator.DiskFillColor, hidden.ColorBuffer[Py(1.0) * W + Px(0)]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(12345)]
    public void Every_Sweep_Emits_Exactly_The_Requested_Count(int n)
    {
        foreach (var s in Enum.GetValues<BilliardRaySweep>())
        {
            var p = new FractalParameters { BilliardRaysSweep = s, BilliardRaysCount = n, BilliardRaysAngleCount = 3 };
            Assert.Equal(n, BilliardRaysCalculator.BuildSweep(p, BilliardScene.Build(p)).Count);
        }
    }

    [Fact]
    public void Sweeps_Place_Their_Rays_As_Defined()
    {
        var p = new FractalParameters { BilliardRaysCount = 300, BilliardRaysAngleCount = 3, BilliardRaysAngle = 20, BilliardRaysSpread = 50 };
        var scene = BilliardScene.Build(p);
        double r = scene.PlayRadius, a0 = 20 * Math.PI / 180;

        // BSweep: three beams of 100, at 20 / 140 / 260 degrees, b evenly across [-r, r].
        var b = BilliardRaysCalculator.BuildSweep(p, scene);
        for (int k = 0; k < 3; k++)
        {
            var beam = b.Skip(100 * k).Take(100).ToList();
            Assert.All(beam, l => Assert.Equal(BilliardRayLaunch.LaunchKind.FarField, l.Kind));
            Assert.All(beam, l => Assert.Equal(a0 + k * 2 * Math.PI / 3, l.B, 12));
            for (int j = 0; j < 100; j++) Assert.Equal(-r + (j + 0.5) * 2 * r / 100, beam[j].A, 12);
        }

        // PointSource: every ray starts at the source, directions span the fan.
        p.BilliardRaysSweep = BilliardRaySweep.PointSource;
        p.BilliardRaysSourceX = -2.7; p.BilliardRaysSourceY = 0.4;
        var ps = BilliardRaysCalculator.BuildSweep(p, scene);
        Assert.All(ps, l => { Assert.Equal(-2.7, l.A); Assert.Equal(0.4, l.B); Assert.Equal(1.0, l.C * l.C + l.D * l.D, 12); });
        var dirs = ps.Select(l => Math.Atan2(l.D, l.C)).ToList();
        Assert.All(dirs, d => Assert.InRange(Diff(d, a0), -25 * Math.PI / 180 - 1e-12, 25 * Math.PI / 180 + 1e-12));
        Assert.True(dirs.Max(d => Diff(d, a0)) - dirs.Min(d => Diff(d, a0)) > 49 * Math.PI / 180);

        // AngleSweep: starts on the play circle, aimed within spread/2 of the centre.
        p.BilliardRaysSweep = BilliardRaySweep.AngleSweep;
        var asw = BilliardRaysCalculator.BuildSweep(p, scene);
        foreach (var l in asw)
        {
            Assert.Equal(r, Math.Sqrt(l.A * l.A + l.B * l.B), 12);
            double inward = Math.Atan2(-l.B, -l.A), aim = Math.Atan2(l.D, l.C);
            Assert.InRange(Diff(aim, inward), -25 * Math.PI / 180 - 1e-12, 25 * Math.PI / 180 + 1e-12);
        }

        // Random sweeps: reproducible by seed, different across seeds.
        foreach (var s in new[] { BilliardRaySweep.RandomFarField, BilliardRaySweep.PhaseSpace })
        {
            p.BilliardRaysSweep = s;
            var one = BilliardRaysCalculator.BuildSweep(p, scene);
            Assert.Equal(one, BilliardRaysCalculator.BuildSweep(p, scene));
            p.BilliardRaysSeed = 99;
            Assert.NotEqual(one, BilliardRaysCalculator.BuildSweep(p, scene));
            p.BilliardRaysSeed = 1;
        }
    }

    private static double Diff(double a, double b)
    {
        double d = (a - b) % (2 * Math.PI);
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    [Fact]
    public void Min_Bounces_Drops_Exactly_The_Short_Rays()
    {
        var p = new FractalParameters { BilliardRaysCount = 4000, BilliardRaysMinBounces = 3, BilliardRaysAngleCount = 2 };
        var c = Render(p);
        var scene = BilliardScene.Build(p);
        int expect = BilliardRaysCalculator.BuildSweep(p, scene)
            .Count(l => BilliardRaysCalculator.Launch(scene, l, null) is { Valid: true, Bounces: >= 3 });
        Assert.Equal(4000, c.LastRaysEmitted);
        Assert.Equal(expect, c.LastRaysDrawn);
        Assert.True(expect > 0 && expect < 4000);
    }

    [Fact]
    public void Live_And_Poster_Render_The_Same_Image_At_Any_Chunking()
    {
        var p = new FractalParameters { BilliardRaysCount = BilliardRaysCalculator.ChunkSize + 777, BilliardRaysAngleCount = 4 };
        var a = Render(p);
        var b = Render(p);
        Assert.Equal(a.Density, b.Density);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);

        var req = new PosterRequest
        {
            FractalType = FractalType.BilliardRays, Width = W, Height = H,
            CenterX = 0, CenterY = 0, Zoom = 0.6, MaxIterations = a.MaxIterations,
            Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
        };
        var pixels = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
        Assert.Equal(a.ColorBuffer, pixels);
    }

    [Fact]
    public void Band_Count_Depends_Only_On_The_Height()
    {
        Assert.Equal(BilliardRaysCalculator.BandCount(1080), BilliardRaysCalculator.BandCount(1080));
        Assert.InRange(BilliardRaysCalculator.BandCount(1), 1, 1);
        Assert.InRange(BilliardRaysCalculator.BandCount(100000), 1, 128);
    }

    [Fact]
    public void Parameters_Round_Trip_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.Custom,
            BilliardCustomDisks = "0.5,0.25,0.3;-0.8,1.1,0.45;0.2,-1.2,0.5",
            BilliardMaxBounces = 77,
            BilliardRaysSweep = BilliardRaySweep.PointSource, BilliardRaysCount = 2500, BilliardRaysAngleCount = 4,
            BilliardRaysAngle = 33.5, BilliardRaysSpread = 210, BilliardRaysSourceX = -1.9, BilliardRaysSourceY = 0.35,
            BilliardRaysSeed = 42, BilliardRaysMinBounces = 1, BilliardRaysShowDisks = false,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var viaJson = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(viaJson);

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BilliardRays, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var viaCli = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BilliardRays, out var perr)!.ApplyTo(viaCli);
        Assert.Null(perr);

        foreach (var q in new[] { viaJson, viaCli })
        {
            Assert.Equal(p.BilliardCustomDisks, q.BilliardCustomDisks);
            Assert.Equal(p.BilliardGeometry, q.BilliardGeometry);
            Assert.Equal(p.BilliardMaxBounces, q.BilliardMaxBounces);
            Assert.Equal(p.BilliardRaysSweep, q.BilliardRaysSweep);
            Assert.Equal(p.BilliardRaysCount, q.BilliardRaysCount);
            Assert.Equal(p.BilliardRaysAngleCount, q.BilliardRaysAngleCount);
            Assert.Equal(p.BilliardRaysAngle, q.BilliardRaysAngle);
            Assert.Equal(p.BilliardRaysSpread, q.BilliardRaysSpread);
            Assert.Equal(p.BilliardRaysSourceX, q.BilliardRaysSourceX);
            Assert.Equal(p.BilliardRaysSourceY, q.BilliardRaysSourceY);
            Assert.Equal(p.BilliardRaysSeed, q.BilliardRaysSeed);
            Assert.Equal(p.BilliardRaysMinBounces, q.BilliardRaysMinBounces);
            Assert.Equal(p.BilliardRaysShowDisks, q.BilliardRaysShowDisks);
            Assert.Equal(Render(p).ColorBuffer, Render(q).ColorBuffer);
        }
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Billiard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Params_Panel_Shows_The_Controls_Each_Sweep_Uses()
    {
        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        Assert.True(vm.IsBilliardRays && vm.IsBilliardScene && !vm.IsChaoticBilliard);
        Assert.True(vm.IsRaysBSweep && vm.RaysUsesAngle && !vm.RaysUsesSpread && !vm.RaysUsesSeed);
        vm.BilliardRaysSweep = BilliardRaySweep.PointSource;
        Assert.True(vm.IsRaysPointSource && vm.RaysUsesSpread && vm.RaysUsesAngle && !vm.RaysUsesSeed);
        vm.BilliardRaysSweep = BilliardRaySweep.RandomFarField;
        Assert.True(vm.RaysUsesSeed && !vm.RaysUsesAngle && !vm.RaysUsesSpread);
        vm.BilliardRaysCount = 0;
        Assert.Equal(1, vm.BilliardRaysCount);
    }

    [Fact]
    public void Inset_Shows_The_Layout_Without_Hover_For_Billiard_Rays()
    {
        var p = new FractalParameters();
        var vm = new BilliardRayInsetViewModel
        {
            GetScene = () => BilliardRayInspector.Scene(p, null),
            TraceAtPointer = (_, _, _, _) => null,
            CanTracePointer = () => false,
        };
        vm.Refresh();
        Assert.NotNull(vm.Scene);
        Assert.Equal(BilliardRayInsetViewModel.LayoutOnlyText, vm.Status);
        Assert.False(vm.Pin(new FracturingFog.Input.PointerInput(1, 1, 10, 10,
            FracturingFog.Input.PointerButton.Left, FracturingFog.Input.InputModifiers.Control)));
    }
}
