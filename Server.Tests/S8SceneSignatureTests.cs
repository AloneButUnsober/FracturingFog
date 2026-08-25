// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S8 (#404) — LightingFxData.SceneSignature() + the UserBulb temporal-
// reuse cache keying on it. Since Phase 1c the live Lighting struct is the
// authoritative look for UserBulb 3D, but BuildSceneKey hashed the dead legacy
// UserBulbLight* fields, so a point/spot switch (or any lighting edit) didn't
// invalidate the cache — the frame only updated once navigation/equation changed
// the key. These lock: (1) the signature moves when a light or FX field changes
// and is stable otherwise; (2) UserBulb's cached render actually repaints when
// the lighting changes with temporal reuse on.

using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8SceneSignatureTests
{
    [Fact]
    public void Signature_Is_Stable_For_Identical_State()
    {
        var a = LightingFxData.CreateDefault();
        var b = LightingFxData.CreateDefault();
        Assert.Equal(a.SceneSignature(), b.SceneSignature());
    }

    [Fact]
    public void Signature_Changes_When_A_Light_Becomes_Positional()
    {
        var baseFx = LightingFxData.CreateDefault();
        int baseSig = baseFx.SceneSignature();

        var pointFx = LightingFxData.CreateDefault();
        var l = pointFx.Light1;
        l.Type = LightType.Point;                 // directional → point
        pointFx.Light1 = l;
        Assert.NotEqual(baseSig, pointFx.SceneSignature());

        var movedFx = LightingFxData.CreateDefault();
        var lm = movedFx.Light1;
        lm.Type = LightType.Point;
        lm.PosX = 5.0;                            // moving the point light must move the sig
        movedFx.Light1 = lm;
        Assert.NotEqual(pointFx.SceneSignature(), movedFx.SceneSignature());
    }

    [Fact]
    public void Signature_Changes_For_Other_Fx_Edits()
    {
        int baseSig = LightingFxData.CreateDefault().SceneSignature();

        var fog = LightingFxData.CreateDefault(); fog.FogDensity = 0.5;
        Assert.NotEqual(baseSig, fog.SceneSignature());

        var tone = LightingFxData.CreateDefault(); tone.ToneMap = ToneMapOperator.Aces;
        Assert.NotEqual(baseSig, tone.SceneSignature());

        var spot = LightingFxData.CreateDefault();
        var l = spot.Light1; l.Type = LightType.Spot; l.SpotOuterDeg = 40; spot.Light1 = l;
        Assert.NotEqual(baseSig, spot.SceneSignature());
    }

    // The user-reported bug: with temporal reuse on, a UserBulb 3D render did not
    // repaint when the lighting changed (stale cache) until nav/equation forced a
    // new scene key. With SceneSignature in the key, the second render must differ.
    [Fact]
    public void UserBulb_TemporalReuse_Repaints_On_Lighting_Change()
    {
        var fp = new FractalParameters
        {
            UserBulbSource = "z^8 + c",
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbIterations = 6,
            UserBulbMaxSteps = 48,
            UserBulbTemporalReuse = true,        // the cache path under test
            UserBulbSuperSample = 1,             // cache only saves at ss == 1
            Lighting = LightingFxData.CreateDefault(),   // directional key light
        };

        var calc = new UserBulbCalculator(48, 48) { FractalParameters = fp };
        calc.Calculate();
        uint[] directional = (uint[])calc.ColorBuffer.Clone();

        // Flip Light1 to a point light beside the surface — same everything else.
        var fx = fp.Lighting;
        var l = fx.Light1;
        l.Type = LightType.Point;
        l.Intensity = 3.0;
        l.PosX = 2.0; l.PosY = 2.0; l.PosZ = 2.0;
        fx.Light1 = l;
        fp.Lighting = fx;

        calc.Calculate();
        uint[] positional = (uint[])calc.ColorBuffer.Clone();

        int diff = 0;
        for (int i = 0; i < directional.Length; i++)
            if (directional[i] != positional[i]) diff++;
        Assert.True(diff > 0,
            "UserBulb temporal-reuse cache served a stale frame across a lighting change (SceneSignature not in the key)");
    }
}
