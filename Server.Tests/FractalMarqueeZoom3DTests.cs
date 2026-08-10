// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using Xunit;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;

namespace FracturingFog.Server.Tests;

// #286 — 3D middle-drag marquee zoom. Mirrors the 2D right-drag box zoom on
// the middle button (right stays camera orbit in 3D). On release the camera
// target recentres on the rect midpoint and Zoom multiplies by the fit factor.
public class FractalMarqueeZoom3DTests
{
    private const int W = 800, H = 600;
    // Must match FractalInputController.Bulb3DFovScale / CurrentScale3D.
    private const double FovScale = 0.57735026918962576;
    private static double Scale3D => 2.0 * FovScale / H;

    private static (FractalInputController c, FractalViewState s) Make3D()
    {
        var s = new FractalViewState { FractalType = FractalType.UserBulb, Zoom = 1.0, CenterX = 0.0, CenterY = 0.0 };
        return (new FractalInputController(s), s);
    }

    private static PointerInput P(int x, int y, PointerButton b) => new(x, y, W, H, b, InputModifiers.None);

    [Fact]
    public void Middle_drag_recentres_and_zooms_by_fit_factor()
    {
        var (c, s) = Make3D();
        double z0 = s.Zoom;

        // Rect 200×150 at (500,300) → midpoint (600,375), off-centre.
        c.OnPointerDown(P(500, 300, PointerButton.Middle));
        c.OnPointerMove(P(700, 450, PointerButton.Middle));
        c.OnPointerUp(P(700, 450, PointerButton.Middle));

        double factor = System.Math.Min((double)W / 200, (double)H / 150); // = 4
        Assert.Equal(z0 * factor, s.Zoom, 9);

        // Recentre matches the 3D double-click focus mapping: (midPx - half)·s3.
        Assert.Equal((600 - W * 0.5) * Scale3D, s.CenterX, 9);
        Assert.Equal((375 - H * 0.5) * Scale3D, s.CenterY, 9);
    }

    [Fact]
    public void Middle_drag_raises_then_clears_the_selection_overlay()
    {
        var (c, s) = Make3D();
        var events = new List<SelectionBoxChange?>();
        c.SelectionBoxChanged += (_, e) => events.Add(e);

        c.OnPointerDown(P(100, 100, PointerButton.Middle));
        c.OnPointerMove(P(300, 260, PointerButton.Middle));
        c.OnPointerUp(P(300, 260, PointerButton.Middle));

        Assert.NotEmpty(events);
        Assert.NotNull(events[0]);            // preview appeared
        Assert.Null(events[^1]);              // cleared on release
    }

    [Fact]
    public void Tiny_middle_drag_is_ignored()
    {
        var (c, s) = Make3D();
        double z0 = s.Zoom, cx0 = s.CenterX, cy0 = s.CenterY;

        // Below BoxMinPixels (8) in both dims → treated as a stray click.
        c.OnPointerDown(P(400, 300, PointerButton.Middle));
        c.OnPointerMove(P(403, 302, PointerButton.Middle));
        c.OnPointerUp(P(403, 302, PointerButton.Middle));

        Assert.Equal(z0, s.Zoom);
        Assert.Equal(cx0, s.CenterX);
        Assert.Equal(cy0, s.CenterY);
    }

    [Fact]
    public void Right_drag_in_3D_orbits_camera_not_box_zoom()
    {
        var (c, s) = Make3D();
        double z0 = s.Zoom;
        double theta0 = s.FractalParameters.UserBulbCameraTheta;
        var events = new List<SelectionBoxChange?>();
        c.SelectionBoxChanged += (_, e) => events.Add(e);

        c.OnPointerDown(P(200, 300, PointerButton.Right));
        c.OnPointerMove(P(500, 300, PointerButton.Right));
        c.OnPointerUp(P(500, 300, PointerButton.Right));

        Assert.Equal(z0, s.Zoom);                                        // no zoom
        Assert.Empty(events);                                            // no marquee overlay
        Assert.NotEqual(theta0, s.FractalParameters.UserBulbCameraTheta); // camera orbited
    }

    [Fact]
    public void Middle_drag_in_2D_does_nothing()
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, Zoom = 1.0 };
        var c = new FractalInputController(s);
        double z0 = s.Zoom;
        var events = new List<SelectionBoxChange?>();
        c.SelectionBoxChanged += (_, e) => events.Add(e);

        c.OnPointerDown(P(200, 200, PointerButton.Middle));
        c.OnPointerMove(P(500, 400, PointerButton.Middle));
        c.OnPointerUp(P(500, 400, PointerButton.Middle));

        Assert.Equal(z0, s.Zoom);
        Assert.Empty(events);
    }
}
