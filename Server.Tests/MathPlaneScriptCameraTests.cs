// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1697 (scripted scenes SS3) — the camera action. Independent check: a scripted
// camera renders the same pixels as the unscripted scene viewed at the moved
// centre and zoom, over the user's own pan; the view state itself never changes.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptCameraTests
{
    private const int W = 240, H = 160;
    private const string Objects = "v1; vector i 1 0; vector j 0 1; point A 1 1; curve f explicit \"sin(x)\"; orbit 8";

    private static uint[] Render(string scene, double s, double cx, double cy, double zoom,
                                 MathPlaneBackdrop backdrop = MathPlaneBackdrop.None, MathPlaneCalculator? calc = null)
    {
        calc ??= new MathPlaneCalculator(W, H) { MaxIterations = 64 };
        calc.CenterX = cx; calc.CenterY = cy; calc.Zoom = zoom;
        calc.FractalParameters = new FractalParameters
        {
            MathPlaneScene = scene, MathPlaneScriptTime = s, MathPlaneBackdrop = backdrop,
            MathPlaneCRe = -0.12, MathPlaneCIm = 0.75,
        };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-0.7, 0.4)]     // the user's own pan stays underneath
    public void AFinishedCamera_IsTheSceneViewedAtTheMovedView(double userX, double userY)
    {
        // camera 1 -0.5 2: world centre + (1, -0.5) (y up), zoom x 2. CenterY is y-down.
        var scripted = Render(Objects + "; step 1 camera 1 -0.5 2", 1, userX, userY, 0.4);
        var moved = Render(Objects, 0, userX + 1, userY + 0.5, 0.8);
        Assert.Equal(moved, scripted);
    }

    [Fact]
    public void TheBackdropFollowsTheCamera()
    {
        var calc = new MathPlaneCalculator(W, H) { MaxIterations = 64 };
        // Warm the backdrop cache at s = 0, then move: it must not reuse the old view.
        Render(Objects + "; step 1 camera 0.3 0.2 1.5", 0, 0, 0, 0.4, MathPlaneBackdrop.Julia, calc);
        var scripted = Render(Objects + "; step 1 camera 0.3 0.2 1.5", 1, 0, 0, 0.4, MathPlaneBackdrop.Julia, calc);
        var moved = Render(Objects, 0, 0.3, -0.2, 0.6, MathPlaneBackdrop.Julia);
        Assert.Equal(moved, scripted);
    }

    [Fact]
    public void TheCalculatorsViewIsRestoredAfterEachFrame()
    {
        var calc = new MathPlaneCalculator(W, H) { MaxIterations = 64 };
        Render(Objects + "; step 1 camera 2 3 4", 1, 0.25, -0.5, 0.4, calc: calc);
        Assert.Equal(0.25, calc.CenterX);
        Assert.Equal(-0.5, calc.CenterY);
        Assert.Equal(0.4, calc.Zoom);
        Assert.Equal(new MathPlaneCamera(2, 3, 4), calc.LastScriptCamera);
    }

    [Fact]
    public void AtTheStart_TheCameraIsIdentity()
    {
        var calc = new MathPlaneCalculator(W, H) { MaxIterations = 64 };
        var a = Render(Objects + "; step 2 camera 1 1 3", 0, 0, 0, 0.4, calc: calc);
        Assert.True(calc.LastScriptCamera.IsIdentity);
        Assert.Equal(Render(Objects, 0, 0, 0, 0.4), a);
    }

    private static MathPlaneCamera At(string script, double s)
    {
        Assert.True(MathPlaneScene.TryParse(Objects + script, out var scene, out var e), e?.ToString());
        return MathPlaneScript.Evaluate(scene, new FractalParameters(), s).Camera ?? MathPlaneCamera.Identity;
    }

    [Fact]
    public void TheOffsetIsLinear_AndTheZoomGeometric()
    {
        var mid = At("; step 2 ease none camera 2 -1 4", 1);
        Assert.Equal(1.0, mid.Dx, 12);
        Assert.Equal(-0.5, mid.Dy, 12);
        Assert.Equal(2.0, mid.Zoom, 12);          // sqrt(1 * 4), not (1 + 4) / 2
        var quarter = At("; step 4 ease none camera 0 0 16", 1);
        Assert.Equal(2.0, quarter.Zoom, 12);      // 16^(1/4)
    }

    [Fact]
    public void ALaterCamera_StartsWhereTheEarlierOneLeft_AndZoomDefaultsToOne()
    {
        var c = At("; step 1 ease none camera 2 0 4; step 2 ease none camera 0 2", 2);
        Assert.Equal(1.0, c.Dx, 12);
        Assert.Equal(1.0, c.Dy, 12);
        Assert.Equal(2.0, c.Zoom, 12);            // 4 -> 1 geometrically, halfway
        Assert.True(At("; step 1 camera 2 0 4; step 1 camera 0 0", 2).IsIdentity);
    }

    [Theory]
    [InlineData("; step 1 camera 1", "x y [zoom]")]
    [InlineData("; step 1 camera 1 1 0", "zoom factor")]
    [InlineData("; step 1 camera 1 1 -2", "zoom factor")]
    [InlineData("; step 1 camera 1 1 2 3", "unexpected text")]
    [InlineData("; step 2 camera 1 1; with 1 camera 0 0", "already animated")]
    public void BadCameras_AreErrors(string script, string expect)
    {
        Assert.False(MathPlaneScene.TryParse(Objects + script, out _, out var err));
        Assert.Contains(expect, err!.Message);
    }

    [Fact]
    public void CameraSteps_RoundTripThroughTheText()
    {
        Assert.True(MathPlaneScene.TryParse(Objects + "; step 1 camera 1 -2; step 2 ease out camera 0.5 0.25 3", out var scene, out var e), e?.ToString());
        Assert.True(MathPlaneScene.TryParse(scene.Serialize(), out var again, out _));
        Assert.Equal(scene, again);
        Assert.Contains("camera 1 -2;", scene.Serialize());
    }
}
