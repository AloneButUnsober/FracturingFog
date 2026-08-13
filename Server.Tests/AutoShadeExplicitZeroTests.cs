// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VLAO audit finding #13 (issue #296): FillAutoShadeDefaults refilled AO /
// shadow enable knobs that the user had explicitly set to 0, silently
// re-enabling a feature they disabled. The opt-in
// Relief2DAutoShadeKeepExplicitZeros flag now preserves those zeros while
// still supplying the harmless material look defaults.

using System.Reflection;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AutoShadeExplicitZeroTests
{
    private static readonly MethodInfo FillAutoShade =
        typeof(HeightfieldRaymarch2D).GetMethod(
            "FillAutoShadeDefaults",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static LightingFxData Fill(LightingFxData fx, bool keepExplicitZeros)
    {
        object[] args = { fx, keepExplicitZeros };
        FillAutoShade.Invoke(null, args);   // ref value-type writes back into args[0]
        return (LightingFxData)args[0];
    }

    [Fact]
    public void Legacy_Fills_ZeroEnableKnobs()
    {
        var fx = LightingFxData.CreateDefault();
        fx.AoSamples = 0;
        fx.ShadowSteps = 0;
        var back = Fill(fx, keepExplicitZeros: false);
        Assert.Equal(5, back.AoSamples);
        Assert.Equal(24, back.ShadowSteps);
    }

    [Fact]
    public void KeepExplicitZeros_Preserves_DisabledAo_And_Shadows()
    {
        var fx = LightingFxData.CreateDefault();
        fx.AoSamples = 0;      // user disabled AO on purpose
        fx.ShadowSteps = 0;    // user disabled shadows on purpose
        var back = Fill(fx, keepExplicitZeros: true);
        Assert.Equal(0, back.AoSamples);
        Assert.Equal(0, back.ShadowSteps);
    }

    [Fact]
    public void KeepExplicitZeros_Still_Fills_Harmless_LookDefaults()
    {
        var fx = LightingFxData.CreateDefault();
        fx.AoSamples = 0;
        fx.AmbientStrength = 0;
        fx.SpecularStrength = 0;
        var back = Fill(fx, keepExplicitZeros: true);
        // Enable knob preserved…
        Assert.Equal(0, back.AoSamples);
        // …but the material look defaults still apply.
        Assert.True(back.AmbientStrength > 0);
        Assert.True(back.SpecularStrength > 0);
    }

    [Fact]
    public void Clone_Carries_KeepExplicitZeros_Flag()
    {
        var p = new FractalParameters { Relief2DAutoShadeKeepExplicitZeros = true };
        Assert.True(p.Clone().Relief2DAutoShadeKeepExplicitZeros);
    }
}
