// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VLAO audit finding #9 (issue #293): MakePreviewParams zeroed VolumeSteps, so
// the interactive preview showed no volumetric shafts and the final frame
// looked completely different. It now caps the march instead of dropping it.

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PreviewParamsVolumetricTests
{
    private static FractalParameters WithVolumeSteps(int steps)
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DAutoShade = false };
        var fx = p.Lighting;
        fx.VolumeSteps = steps;
        fx.FogDensity = 0.5;
        p.Lighting = fx;
        return p;
    }

    [Fact]
    public void Preview_Caps_High_VolumeSteps_Instead_Of_Zeroing()
    {
        var preview = HeightfieldRaymarch2D.MakePreviewParams(WithVolumeSteps(64));
        Assert.Equal(8, preview.Lighting.VolumeSteps);   // capped, not 0
    }

    [Fact]
    public void Preview_Keeps_Low_VolumeSteps_Unchanged()
    {
        var preview = HeightfieldRaymarch2D.MakePreviewParams(WithVolumeSteps(4));
        Assert.Equal(4, preview.Lighting.VolumeSteps);
    }

    [Fact]
    public void Preview_Leaves_Disabled_Volumetrics_Off()
    {
        var preview = HeightfieldRaymarch2D.MakePreviewParams(WithVolumeSteps(0));
        Assert.Equal(0, preview.Lighting.VolumeSteps);
    }

    [Fact]
    public void Preview_Still_Drops_PerHit_Fx()
    {
        var preview = HeightfieldRaymarch2D.MakePreviewParams(WithVolumeSteps(32));
        Assert.Equal(0, preview.Lighting.AoSamples);
        Assert.Equal(0, preview.Lighting.SsaoSamples);
        Assert.Equal(0.0, preview.Lighting.ReflectionStrength);
    }
}
