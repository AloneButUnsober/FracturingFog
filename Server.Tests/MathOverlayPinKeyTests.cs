// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1748 — the O key pins the math overlay's point under the pointer, so pinning needs no trip to
// the Control Center button (which drags the hover point along the way).

using System;
using System.Reflection;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathOverlayPinKeyTests
{
    private const int W = 96, H = 64;

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static void Hover(FractalInputController input, int x, int y)
        => input.OnPointerMove(new PointerInput(x, y, W, H, PointerButton.None, InputModifiers.None));

    [Fact]
    public void On2D_OPinsThePlanePointUnderThePointer_AndDoesNothingWhileTheOverlayIsOff()
    {
        var vs = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1 };
        using var host = new FractalRenderHost(new NullRenderer(), vs, W, H, ColorPalette.BuiltIns[0]);
        var input = new FractalInputController(vs);
        var vm = new MainViewModel(host, input);

        Hover(input, 10, 20);
        Assert.False(vm.PinMathOverlayAtPointer());   // overlay off: the key is not used
        Assert.Null(host.MathOverlayPin);

        vm.MathOverlay = true;
        Hover(input, 70, 12);
        Hover(input, 30, 40);   // the pointer rests here when O is pressed
        Assert.True(vm.PinMathOverlayAtPointer());
        var (re, im) = MathOverlay.ClientToPlane(30, 40, W, H, -0.5, 0, 1);
        Assert.Equal(re, host.MathOverlayPin!.Value.Re);
        Assert.Equal(im, host.MathOverlayPin!.Value.Im);
        Assert.False(vm.MathOverlayFollow);

        // Following is off now; O still re-pins wherever the pointer is.
        Hover(input, 5, 6);
        Assert.True(vm.PinMathOverlayAtPointer());
        Assert.Equal(MathOverlay.ClientToPlane(5, 6, W, H, -0.5, 0, 1).Re, host.MathOverlayPin!.Value.Re);
    }

    [Fact]
    public void On3D_OPinsTheSurfacePointUnderThePointer_AndRefusesTheSky()
    {
        var vs = new FractalViewState { FractalType = FractalType.Mandelbulb };
        using var host = new FractalRenderHost(new NullRenderer(), vs, W, H, ColorPalette.BuiltIns[0]);
        // A frame: eye at z = 6 looking down -z; a wall at ray distance 5 on the left half, sky on the right.
        var cam = new Raymarch3DCamera((0, 0, 6), (0, 0, -1), (1, 0, 0), (0, 1, 0), Math.Tan(Math.PI / 6), 0, 0, W, H);
        var depth = new float[W * H];
        for (int i = 0; i < depth.Length; i++) depth[i] = i % W < W / 2 ? 5f : float.PositiveInfinity;
        var frameType = typeof(FractalRenderHost).GetNestedType("Overlay3DFrame", BindingFlags.NonPublic)!;
        typeof(FractalRenderHost).GetField("_overlay3DFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, Activator.CreateInstance(frameType, cam, depth, W, H));

        var input = new FractalInputController(vs);
        var vm = new MainViewModel(host, input);
        vm.MathOverlay = true;

        Hover(input, 20, 30);   // on the wall
        Assert.True(vm.PinMathOverlayAtPointer());
        var p = host.MathOverlay3DPin!.Value;
        var dir = cam.RayDirection(20.5, 30.5);   // independent: the camera's own ray through that pixel's centre
        Assert.Equal(6 + 5 * dir.Z, p.Z, 9);
        Assert.Equal(5 * dir.X, p.X, 9);
        Assert.Equal(5 * dir.Y, p.Y, 9);

        // Over the sky: refused, and the earlier wall point is not pinned again silently.
        host.MathOverlay3DPin = null;
        Hover(input, 20, 30);
        Hover(input, 80, 30);
        Assert.True(vm.PinMathOverlayAtPointer());
        Assert.Null(host.MathOverlay3DPin);
        Assert.Contains("sky", vm.MathOverlayPinError);
    }
}
