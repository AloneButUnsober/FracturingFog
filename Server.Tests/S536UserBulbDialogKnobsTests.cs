// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #536 — the User Bulb dialog's AO and Fog knobs. They used to apply only while
// the Lighting & FX value was 0, so with Lighting AO / fog set the dialog did
// nothing; and the dialog fog fogged only the object, leaving a crisp sky behind
// a fogged bulb. Now a set dialog value wins, and the dialog fog fogs the
// background too (and, with Froxel 3D on, drives that volume). Zoomed out so
// the frame corners are background; uniform sky (top == bottom) so the expected
// background colour doesn't depend on the ray direction.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S536UserBulbDialogKnobsTests
{
    private const int W = 96, H = 72;
    private const uint Sky = 0xFF3060A0u;

    private static (uint[] buf, uint inSet) Render(Action<FractalParameters> tweak, double zoom = 1.0)
    {
        var fx = LightingFxData.CreateDefault();
        fx.BgTopColor = Sky;
        fx.BgBottomColor = Sky;
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        tweak(fp);
        var c = new UserBulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], FractalParameters = fp, Zoom = zoom };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), c.ColorMap.InSetColor);
    }

    private static void SetLighting(FractalParameters p, Func<LightingFxData, LightingFxData> f) => p.Lighting = f(p.Lighting);

    [Fact]
    public void Dialog_AO_Takes_Effect_Even_When_Lighting_AO_Is_Set()
    {
        var lightingOnly = Render(p => SetLighting(p, f => { f.AoSamples = 2; return f; })).buf;
        var withDialog = Render(p => { SetLighting(p, f => { f.AoSamples = 2; return f; }); p.UserBulbAOSamples = 8; }).buf;
        Assert.NotEqual(lightingOnly, withDialog);
    }

    [Fact]
    public void Dialog_Fog_Takes_Effect_Even_When_Lighting_Fog_Is_Set()
    {
        var lightingOnly = Render(p => SetLighting(p, f => { f.FogDensity = 0.1; return f; })).buf;
        var withDialog = Render(p => { SetLighting(p, f => { f.FogDensity = 0.1; return f; }); p.UserBulbFogDensity = 0.8; }).buf;
        Assert.NotEqual(lightingOnly, withDialog);
    }

    [Fact]
    public void Dialog_Fog_Fogs_The_Background_Not_Only_The_Bulb()
    {
        var (clear, inSet) = Render(_ => { }, zoom: 0.25);
        var (fogged, _) = Render(p => p.UserBulbFogDensity = 0.5, zoom: 0.25);

        // Expected background: the miss colour with the same fog as background fog.
        var fx = LightingFxData.CreateDefault();
        fx.BgTopColor = Sky; fx.BgBottomColor = Sky;
        fx.FogDensity = 0.5; fx.FogBackground = true;
        uint expected = ShadingPipeline.MissColor(0, 0, 1, in fx, inSet);   // uniform sky → any direction
        Assert.NotEqual(clear[0], expected);
        foreach (int i in new[] { 0, W - 1, (H - 1) * W, H * W - 1 })
            Assert.Equal(expected, fogged[i]);
    }

    [Fact]
    public void Dialog_Fog_Drives_The_Froxel_Volume()
    {
        var off = Render(p => SetLighting(p, f => { f.Froxel3D = true; return f; }), zoom: 0.25).buf;
        var on = Render(p => { SetLighting(p, f => { f.Froxel3D = true; return f; }); p.UserBulbFogDensity = 0.5; }, zoom: 0.25).buf;
        Assert.NotEqual(off[0], on[0]);   // the volume fills empty space too
    }

    [Fact]
    public void Dialog_Knobs_At_Zero_Leave_Lighting_In_Charge()
    {
        var a = Render(p => SetLighting(p, f => { f.AoSamples = 4; f.FogDensity = 0.3; return f; })).buf;
        var b = Render(p => { SetLighting(p, f => { f.AoSamples = 4; f.FogDensity = 0.3; return f; }); p.UserBulbAOSamples = 0; p.UserBulbFogDensity = 0; }).buf;
        Assert.Equal(a, b);
    }
}
