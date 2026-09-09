// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #327 — Relief 3D low-res PREVIEW relief was Mandelbrot-only. FractalRenderHost now
// runs a dedicated alt (non-Mandelbrot) preview sidecar for relief-eligible
// height-field types during interaction (pan/zoom), so they get the same low-res 3D
// preview instead of a flat held frame → 3D snap. The host's progressive/upload loop
// is D3D-coupled and not headless-testable, but its preview relief COMPUTATION is the
// pieces exercised here: the sidecar twin is built by CreateReliefFieldCalc (the same
// factory the host uses), configured like SyncAltStateFromMandel, and its field is fed
// through HeightfieldRaymarch2D.MakePreviewParams + Render — exactly the upload-branch
// path. These lock that an alt type produces a real, deterministic relief preview.

using System;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S327AltReliefPreviewTests
{
    private const int PW = 120, PH = 90;   // quarter/half-style preview dims

    private static FractalParameters ReliefParams() => new FractalParameters
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraElevationDeg = 45,
    };

    // The host resolves the alt preview twin from CreateReliefFieldCalc for every
    // relief-eligible height-field type (the #327 target set) — so the factory must
    // build one for the escape-time alt families and User Equation alike.
    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.UserEquation)]
    public void PreviewTwin_Is_Built_For_Relief_Eligible_Alt_Types(FractalType type)
    {
        Assert.True(FractalRenderHost.SupportsHiResReliefField(type));
        var twin = FractalRenderHost.CreateReliefFieldCalc(type, PW, PH);
        Assert.NotNull(twin);
        Assert.IsAssignableFrom<IHeightFieldSource>(twin);
    }

    // An alt escape-time type produces a relief-applied preview: its low-res field,
    // run through the preview params the host uses, extrudes a surface (hit fraction
    // > 0) and is deterministic (stable during a drag).
    [Fact]
    public void Alt_EscapeTime_PreviewRelief_Extrudes_And_Is_Deterministic()
    {
        var e = (EscapeTimeCalculator)FractalRenderHost.CreateReliefFieldCalc(FractalType.BurningShip, PW, PH)!;
        e.FractalType = FractalType.BurningShip;
        e.FractalParameters = ReliefParams();
        e.ColorMap = new MonoBandMap();
        e.CenterX = -0.5; e.CenterY = -0.5; e.Zoom = 0.4; e.MaxIterations = 200;
        e.Calculate(default);

        var prp = HeightfieldRaymarch2D.MakePreviewParams(ReliefParams());
        Assert.True(prp.Relief2DRaymarch);

        var dst1 = new uint[PW * PH];
        HeightfieldRaymarch2D.Render((uint[])e.ColorBuffer.Clone(),
            (float[])e.SmoothBuffer.Clone(), PW, PH, prp, dst1, out double hit);
        Assert.True(hit > 0.0, "alt preview relief found no surface");

        var dst2 = new uint[PW * PH];
        HeightfieldRaymarch2D.Render((uint[])e.ColorBuffer.Clone(),
            (float[])e.SmoothBuffer.Clone(), PW, PH, prp, dst2, out _);
        Assert.Equal(dst1, dst2);
    }

    // The DSL family (added to the relief-eligible set in #726) also produces a
    // preview-relief surface — closing the flat→3D flash for User Equation too.
    [Fact]
    public void Alt_UserEquation_PreviewRelief_Extrudes()
    {
        var u = (UserEquationCalculator)FractalRenderHost.CreateReliefFieldCalc(FractalType.UserEquation, PW, PH)!;
        u.CenterX = -0.5; u.CenterY = 0.0; u.Zoom = 1.0; u.MaxIterations = 200;
        u.ColorMap = new MonoBandMap();
        u.FractalParameters = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightScale = 1.4,
            Relief2DCameraElevationDeg = 45,
            UserEquationSource = "z*z + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        };
        u.Calculate(default);

        var prp = HeightfieldRaymarch2D.MakePreviewParams(u.FractalParameters);
        var dst = new uint[PW * PH];
        HeightfieldRaymarch2D.Render((uint[])u.ColorBuffer.Clone(),
            (float[])u.SmoothBuffer.Clone(), PW, PH, prp, dst, out double hit);
        Assert.True(hit > 0.0, "DSL preview relief found no surface");
    }
}
