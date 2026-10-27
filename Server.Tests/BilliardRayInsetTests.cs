// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1447 — the chaotic-billiard ray-path inset. Oracles are the physics and the
// render, not the inspector itself:
//   • a recorded path obeys specular reflection: every interior vertex lies on a
//     disk surface, and the angle in equals the angle out about the normal there;
//   • recording a path does not change the outcome, and the path ends at the
//     outcome's end point heading along its end direction;
//   • the launch the inset traces for a pointer is the one the render coloured
//     that pixel with (same bounce count, at any client scale);
//   • each gate's drawn sector is the set of directions Gate() puts in it.
public class BilliardRayInsetTests
{
    private static FractalParameters Params(BilliardLaunchMode launch) => new()
    {
        BilliardGeometry = BilliardGeometry.ThreeDisk,
        BilliardGateMode = BilliardGateMode.ExitGap,
        BilliardLaunchMode = launch,
    };

    private static IEnumerable<(double X, double Y)> Launches()
    {
        var rng = new Random(1447);
        for (int i = 0; i < 4000; i++)
            yield return (rng.NextDouble() * 4.0 - 2.0, rng.NextDouble() * 4.0 - 2.0);
    }

    [Theory]
    [InlineData(BilliardLaunchMode.FarField)]
    [InlineData(BilliardLaunchMode.PhaseSpace)]
    public void Recorded_Path_Obeys_Reflection_And_Matches_The_Outcome(BilliardLaunchMode launch)
    {
        var scene = BilliardScene.Build(Params(launch));
        int multiBounce = 0;
        foreach (var (x, y) in Launches())
        {
            var plain = scene.Launch(x, y);
            var path = new List<BilliardPoint>();
            var o = scene.Launch(x, y, path);
            Assert.Equal(plain, o);   // recording changes nothing
            if (!o.Valid) { Assert.Empty(path); continue; }

            bool escaped = o.GateId >= 0;
            Assert.Equal(o.Bounces + (escaped ? 2 : 1), path.Count);
            var end = path[^1];
            Assert.Equal(o.EndX, end.X, 9);
            Assert.Equal(o.EndY, end.Y, 9);
            if (escaped && path.Count >= 2)
            {
                var prev = path[^2];
                double lx = end.X - prev.X, ly = end.Y - prev.Y, l = Math.Sqrt(lx * lx + ly * ly);
                if (l > 1e-6)
                {
                    Assert.Equal(o.EndDx, lx / l, 9);
                    Assert.Equal(o.EndDy, ly / l, 9);
                }
            }

            // Interior vertices: on a disk, reflection law about its normal.
            int interiorEnd = escaped ? path.Count - 1 : path.Count;
            for (int i = 1; i < interiorEnd; i++)
            {
                var p = path[i];
                int disk = -1;
                for (int k = 0; k < scene.DiskCount; k++)
                {
                    double d = Math.Sqrt(Sq(p.X - scene.DiskX(k)) + Sq(p.Y - scene.DiskY(k)));
                    if (Math.Abs(d - scene.DiskR(k)) < 1e-9) { disk = k; break; }
                }
                Assert.True(disk >= 0, $"vertex {i} of launch ({x}, {y}) is on no disk surface");
                if (i + 1 >= path.Count) continue;
                double nx = (p.X - scene.DiskX(disk)) / scene.DiskR(disk);
                double ny = (p.Y - scene.DiskY(disk)) / scene.DiskR(disk);
                var (ix, iy) = Unit(p.X - path[i - 1].X, p.Y - path[i - 1].Y);
                var (ox, oy) = Unit(path[i + 1].X - p.X, path[i + 1].Y - p.Y);
                double inN = ix * nx + iy * ny, outN = ox * nx + oy * ny;
                Assert.Equal(-inN, outN, 7);                                // normal part flips
                Assert.Equal(ix * -ny + iy * nx, ox * -ny + oy * nx, 7);   // tangent part kept
            }
            if (o.Bounces >= 2) multiBounce++;
        }
        Assert.True(multiBounce > 50, $"only {multiBounce} multi-bounce launches: the check saw too little");
    }

    [Theory]
    [InlineData(BilliardLaunchMode.FarField, 0.1, -0.2, 1.7)]
    [InlineData(BilliardLaunchMode.PhaseSpace, 0.4, 0.1, 3.0)]
    public void Pointer_Traces_The_Launch_The_Render_Used(BilliardLaunchMode launch, double cx, double cy, double zoom)
    {
        const int w = 96, h = 64;
        var p = Params(launch);
        var calc = new ChaoticBilliardCalculator(w, h)
        {
            FractalParameters = p, CenterX = cx, CenterY = cy, Zoom = zoom,
        };
        calc.Calculate();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                var path = BilliardRayInspector.TraceAt(p, null, x, y, w, h, cx, cy, zoom);
                if (calc.ColorBuffer[idx] == ChaoticBilliardCalculator.OutsideSectionColor) { Assert.False(path.Valid); continue; }
                Assert.Equal(calc.SmoothBuffer[idx], path.Bounces);
                // A client at twice the size showing the same frame maps to the same launch.
                var big = BilliardRayInspector.TraceAt(p, null, 2 * x, 2 * y, 2 * w, 2 * h, cx, cy, zoom);
                Assert.Equal(path.PlaneX, big.PlaneX, 12);
                Assert.Equal(path.PlaneY, big.PlaneY, 12);
            }
    }

    [Fact]
    public void Gate_Sectors_Match_Gate_Classification()
    {
        foreach (var mode in new[] { BilliardGateMode.ExitGap, BilliardGateMode.Sector })
        {
            var p = Params(BilliardLaunchMode.FarField);
            p.BilliardGateMode = mode;
            p.BilliardGateCount = 5;
            var scene = BilliardScene.Build(p);
            var inset = BilliardRayInspector.Scene(p, null);
            Assert.Equal(scene.GateCount, inset.GateStartAngles.Count);
            double width = 2.0 * Math.PI / scene.GateCount;
            for (int g = 0; g < scene.GateCount; g++)
                foreach (double f in new[] { 0.05, 0.5, 0.95 })
                {
                    double a = inset.GateStartAngles[g] + f * width;
                    Assert.Equal(g, scene.Gate(Math.Cos(a), Math.Sin(a)));
                }
            Assert.Equal(scene.DiskCount, inset.Disks.Count);
        }
    }

    [Fact]
    public void Path_Colour_Is_The_Gate_Colour_And_Trapped_Is_Visible()
    {
        var p = Params(BilliardLaunchMode.PhaseSpace);
        var inset = BilliardRayInspector.Scene(p, null);
        // Without a billiard theme the gates take the Okabe-Ito fallback (as the render).
        Assert.Equal(0xFFE69F00u, inset.GateColors[0]);
        Assert.Equal(0xFF56B4E9u, inset.GateColors[1]);
        foreach (var (x, y) in Launches())
        {
            var path = BilliardRayInspector.Trace(p, null, x, y);
            if (!path.Valid) continue;
            Assert.Equal(path.GateId < 0 ? BilliardRayInspector.DarkFallbackColor : inset.GateColors[path.GateId], path.Color);
        }
    }

    [Fact]
    public void Ctrl_Click_Pins_Instead_Of_Panning_And_Hover_Only_Without_Buttons()
    {
        var vs = new FractalViewState { FractalType = FractalType.ChaoticBilliard };
        var input = new FractalInputController(vs);
        int pins = 0, hovers = 0;
        input.InspectClickHandler = _ => { pins++; return true; };
        input.HoverObserver = _ => hovers++;
        double cx = vs.CenterX, cy = vs.CenterY;

        input.OnPointerMove(new PointerInput(10, 10, 100, 100, PointerButton.None, InputModifiers.None));
        Assert.Equal(1, hovers);

        input.OnPointerDown(new PointerInput(10, 10, 100, 100, PointerButton.Left, InputModifiers.Control));
        input.OnPointerMove(new PointerInput(60, 60, 100, 100, PointerButton.Left, InputModifiers.Control));
        input.OnPointerUp(new PointerInput(60, 60, 100, 100, PointerButton.Left, InputModifiers.Control));
        Assert.Equal(1, pins);
        Assert.Equal(1, hovers);              // a held button is not a hover
        Assert.Equal(cx, vs.CenterX);         // no pan
        Assert.Equal(cy, vs.CenterY);

        // A plain left drag still pans and never reaches the pin hook.
        input.OnPointerDown(new PointerInput(10, 10, 100, 100, PointerButton.Left, InputModifiers.None));
        input.OnPointerMove(new PointerInput(60, 60, 100, 100, PointerButton.Left, InputModifiers.None));
        input.OnPointerUp(new PointerInput(60, 60, 100, 100, PointerButton.Left, InputModifiers.None));
        Assert.Equal(1, pins);
        Assert.NotEqual(cx, vs.CenterX);
    }

    [Fact]
    public void Inset_View_Model_Pins_Retrace_And_Report_The_Wrong_Type()
    {
        var p = Params(BilliardLaunchMode.PhaseSpace);
        bool billiard = true;
        var vm = new BilliardRayInsetViewModel
        {
            GetScene = () => billiard ? BilliardRayInspector.Scene(p, null) : null,
            TraceAtPlane = (x, y) => billiard ? BilliardRayInspector.Trace(p, null, x, y) : null,
            TraceAtPointer = (x, y, w, h) => billiard ? BilliardRayInspector.TraceAt(p, null, x, y, w, h, 0, 0, 1) : null,
        };
        vm.Refresh();
        for (int i = 0; i < BilliardRayInsetViewModel.MaxPins + 2; i++)
            Assert.True(vm.Pin(new PointerInput(10 + i, 20, 100, 80, PointerButton.Left, InputModifiers.Control)));
        Assert.Equal(BilliardRayInsetViewModel.MaxPins, vm.Pins.Count);
        var first = vm.Pins[0];
        Assert.Equal(BilliardRayInspector.TraceAt(p, null, 12, 20, 100, 80, 0, 0, 1).PlaneX, first.PlaneX);   // oldest two dropped
        Assert.StartsWith("A  ", vm.Status);

        // A parameter change re-traces the pins at their plane points.
        p.BilliardMaxBounces = 1;
        vm.Refresh();
        Assert.Equal(first.PlaneX, vm.Pins[0].PlaneX);
        Assert.All(vm.Pins, pin => Assert.True(pin.Bounces <= 1));

        billiard = false;
        vm.Refresh();
        Assert.Equal(BilliardRayInsetViewModel.WrongTypeText, vm.Status);
        Assert.False(vm.Pin(new PointerInput(1, 1, 100, 80, PointerButton.Left, InputModifiers.Control)));   // falls through to a pan
        vm.ClearPins();
        Assert.Empty(vm.Pins);
    }

    private static double Sq(double v) => v * v;
    private static (double, double) Unit(double x, double y)
    {
        double l = Math.Sqrt(x * x + y * y);
        return (x / l, y / l);
    }
}
