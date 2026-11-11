// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1697 follow-up — mid-script, the section's pointer read-out matches the picture.
// Independent check: render the scene, find the point's disc in the pixels, hover
// over that pixel, and the read-out names the point and gives its scripted position
// (through the script camera, the moved point and the evaluated map).

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptPointerTests
{
    private const int W = 640, H = 360;
    private const uint Pink = 0xFFCC79A7;

    private static (MathPlaneSectionViewModel Vm, FractalViewState View) Rig(string scene, double s,
        Action<FractalParameters>? setup = null)
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane, CenterX = 0.2, CenterY = -0.1, Zoom = 0.5 };
        view.FractalParameters.MathPlaneScene = scene;
        setup?.Invoke(view.FractalParameters);
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { });
        vm.ScriptTime = s;
        return (vm, view);
    }

    /// <summary>The centre of the pink point's disc in the rendered frame.</summary>
    private static (int X, int Y) FindPoint(FractalViewState view)
    {
        var calc = new MathPlaneCalculator(W, H)
        {
            CenterX = view.CenterX, CenterY = view.CenterY, Zoom = view.Zoom, FractalParameters = view.FractalParameters,
        };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        var px = calc.ColorBuffer.Select((c, i) => (c, i)).Where(t => t.c == Pink).Select(t => t.i).ToList();
        Assert.True(px.Count > 20, "the point is drawn on screen");
        return ((int)Math.Round(px.Average(i => i % W)), (int)Math.Round(px.Average(i => i / W)));
    }

    private static (double X, double Y) ReadPointer(string readout)
    {
        var m = Regex.Match(readout, @"x = (\S+), y = (\S+?)(\s|$)");
        Assert.True(m.Success, readout);
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1.0, 1.5, 0.5)]      // moved and the camera finished
    [InlineData(0.5, 0.75, 0.25)]    // half-way: point half moved, camera half moved
    public void HoveringThePointInThePicture_ReadsThePoint(double s, double wantX, double wantY)
    {
        var (vm, view) = Rig("v1; point A 0 0 #CC79A7 nolabel; step 1 ease none move A 1.5 0.5; with camera 1 0.3 2", s);
        var (x, y) = FindPoint(view);
        vm.OnHover(new PointerInput(x, y, W, H, PointerButton.None, InputModifiers.None));
        Assert.StartsWith("A", vm.HoverReadout);
        var (rx, ry) = ReadPointer(vm.PointerReadout);
        // Within a couple of pixels of the drawn centre (whole-pixel pointer, disc centroid).
        double tol = 3 * MathPlaneSceneEdit.Pitch(W, view.Zoom * (s >= 1 ? 2 : Math.Sqrt(2)));
        Assert.InRange(rx - wantX, -tol, tol);
        Assert.InRange(ry - wantY, -tol, tol);
    }

    [Fact]
    public void UnderAScriptedMap_TheHoverFindsTheDrawnImage()
    {
        // The shear plays to t = 1 in the script: A at (0, 1) is drawn at (1, 1).
        var (vm, view) = Rig("v1; point A 0 1 #CC79A7 nolabel; step 1 t 1", 1, p =>
        {
            p.MathPlaneMapMode = MathPlaneMapMode.Matrix;
            p.MathPlaneM11 = 1; p.MathPlaneM12 = 1; p.MathPlaneM21 = 0; p.MathPlaneM22 = 1;
            p.MathPlaneT = 0;
        });
        var (x, y) = FindPoint(view);
        vm.OnHover(new PointerInput(x, y, W, H, PointerButton.None, InputModifiers.None));
        Assert.StartsWith("A", vm.HoverReadout);
        var (rx, ry) = ReadPointer(vm.PointerReadout);
        double tol = 3 * MathPlaneSceneEdit.Pitch(W, view.Zoom);
        Assert.InRange(rx - 1, -tol, tol);
        Assert.InRange(ry - 1, -tol, tol);
    }

    [Fact]
    public void WithoutAScript_TheReadOutIsTheView()
    {
        var (vm, view) = Rig("v1; point A 0.5 -0.25 #CC79A7 nolabel", 0);
        var (x, y) = FindPoint(view);
        vm.OnHover(new PointerInput(x, y, W, H, PointerButton.None, InputModifiers.None));
        Assert.StartsWith("A", vm.HoverReadout);
        var (rx, ry) = ReadPointer(vm.PointerReadout);
        double tol = 3 * MathPlaneSceneEdit.Pitch(W, view.Zoom);
        Assert.InRange(rx - 0.5, -tol, tol);
        Assert.InRange(ry + 0.25, -tol, tol);
    }
}
