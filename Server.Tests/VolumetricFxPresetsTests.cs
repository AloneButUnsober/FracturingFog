// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #306 — VolumetricFxPresets catalogue. Presets are fog-subset starting points
// applied OVER the current lighting: they must change fog knobs but leave lights,
// material, camera, sky and post alone. NoneName / unknown must be no-ops.

using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class VolumetricFxPresetsTests
{
    [Fact]
    public void Names_LeadWith_NoneSentinel_ThenEveryPreset()
    {
        var names = VolumetricFxPresets.Names;
        Assert.Equal(VolumetricFxPresets.NoneName, names[0]);
        Assert.Equal(VolumetricFxPresets.All.Count + 1, names.Count);
        foreach (var p in VolumetricFxPresets.All)
            Assert.Contains(p.Name, names);
    }

    [Fact]
    public void ApplyByName_None_Or_Unknown_Is_NoOp()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.123;

        Assert.Equal(fx.FogDensity, VolumetricFxPresets.ApplyByName(null, fx).FogDensity);
        Assert.Equal(fx.FogDensity, VolumetricFxPresets.ApplyByName("", fx).FogDensity);
        Assert.Equal(fx.FogDensity, VolumetricFxPresets.ApplyByName(VolumetricFxPresets.NoneName, fx).FogDensity);
        Assert.Equal(fx.FogDensity, VolumetricFxPresets.ApplyByName("no-such-preset", fx).FogDensity);
    }

    [Fact]
    public void EveryPreset_Changes_Fog_And_Preserves_NonFog_State()
    {
        // A distinctive non-fog baseline: lights / material / camera that a
        // preset must NOT touch.
        var baseFx = LightingFxData.CreateDefault();
        baseFx.Light1.Intensity = 3.5;
        baseFx.Light2.Intensity = 2.25;
        baseFx.Roughness = 0.42;
        baseFx.Metallic = 0.9;
        baseFx.Exposure = 2.0;
        baseFx.ReflectionStrength = 0.8;
        baseFx.SkyMode = SkyMode.Hdri;

        foreach (var preset in VolumetricFxPresets.All)
        {
            var outFx = preset.Apply(baseFx);

            // Non-fog state is untouched.
            Assert.Equal(3.5,  outFx.Light1.Intensity);
            Assert.Equal(2.25, outFx.Light2.Intensity);
            Assert.Equal(0.42, outFx.Roughness);
            Assert.Equal(0.9,  outFx.Metallic);
            Assert.Equal(2.0,  outFx.Exposure);
            Assert.Equal(0.8,  outFx.ReflectionStrength);
            Assert.Equal(SkyMode.Hdri, outFx.SkyMode);

            // "Clear (no fog)" deliberately zeroes fog; the rest establish a
            // visible medium.
            if (preset.Name == "Clear (no fog)")
            {
                Assert.Equal(0.0, outFx.FogDensity);
                Assert.Equal(0,   outFx.VolumeSteps);
            }
            else
            {
                Assert.True(outFx.FogDensity > 0.0, $"{preset.Name} should set fog density");
                Assert.True(outFx.VolumeSteps > 0,  $"{preset.Name} should set volume steps");
            }
        }
    }
}
