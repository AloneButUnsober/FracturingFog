// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S8 #492 — GPU relief area-light soft-shadow penumbra. The relief kernel + CPU
// twin take a per-light soft-shadow hardness (ShadowK0/1/2) already capped by each
// light's AreaAngularRadius (ShadingPipeline.EffectiveShadowK = softer of the
// global ShadowSoftK and cot(radius)), precomputed CPU-side in ReliefUniforms.Build.
// The on-device GPU-vs-twin parity is validated by --reliefgpuraymarch (the new
// "area soft shadow" scene); what is unit-testable here is the CPU→uniforms bridge:
// Build must carry the area-capped per-light k, and punctual lights must resolve to
// the global k so a non-area scene is byte-identical.

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8ReliefAreaShadowKTests
{
    private static ReliefUniforms Build(LightingFxData fx)
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        return ReliefUniforms.Build(64, 48, 64, 48, sy: 1.0, aspect: 64.0 / 48.0,
            invLip: 1.0, maxH: 1.0, p, in fx);
    }

    [Fact]
    public void Build_Punctual_ShadowK_Equals_GlobalK()
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSoftK = 8.0;
        // All lights punctual (AreaAngularRadius 0 by default) → per-light k == global.
        var u = Build(fx);
        Assert.Equal(8.0, u.ShadowK0, 12);
        Assert.Equal(8.0, u.ShadowK1, 12);
        Assert.Equal(8.0, u.ShadowK2, 12);
    }

    [Fact]
    public void Build_AreaLight_Caps_ShadowK_PerLight()
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSoftK = 24.0;                 // sharp global k
        fx.Light1.AreaAngularRadius = 12.0;    // cot(12°) ≈ 4.7 < 24 → capped softer
        fx.Light2.AreaAngularRadius = 0.0;     // punctual → global 24
        fx.Light3.AreaAngularRadius = 120.0;   // ≥ 90° → fully soft (k 0)

        var u = Build(fx);
        Assert.Equal(ShadingPipeline.EffectiveShadowK(24.0, 12.0), u.ShadowK0, 12);
        Assert.True(u.ShadowK0 < 24.0 && u.ShadowK0 > 0.0, "12° area caps k below the global hardness");
        Assert.Equal(24.0, u.ShadowK1, 12);    // punctual unchanged
        Assert.Equal(0.0, u.ShadowK2, 12);     // hemisphere-sized → fully soft
    }
}
