// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VLAO audit finding #12 (issue #295): a region with LightingOverride == null
// inherited whatever lighting was active on load, so the same region JSON
// rendered differently on differently-configured installs. The opt-in
// LightingIsAuthoritative flag + ApplyLightingAuthoritative reset lighting to
// stock defaults on recall, making the region portable.

using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegionAuthoritativeLightingTests
{
    private static FractalParameters DramaticLit()
    {
        var p = new FractalParameters();
        var fx = p.Lighting;
        fx.FogDensity = 0.9;
        fx.VolumeSteps = 32;
        fx.BloomStrength = 2.0;
        p.Lighting = fx;
        return p;
    }

    [Fact]
    public void Authoritative_NullOverride_ResetsToDefault()
    {
        var region = new FractalRegion();   // no LightingOverride
        var p = DramaticLit();
        region.ApplyLightingAuthoritative(p);

        var def = LightingFxData.CreateDefault();
        Assert.Equal(def.FogDensity, p.Lighting.FogDensity);
        Assert.Equal(def.VolumeSteps, p.Lighting.VolumeSteps);
        Assert.Equal(def.BloomStrength, p.Lighting.BloomStrength);
    }

    [Fact]
    public void Authoritative_WithOverride_AppliesIt()
    {
        var snap = LightingFxData.CreateDefault();
        snap.FogDensity = 0.42;
        var region = new FractalRegion { LightingOverride = LightingFxPresetData.FromFx(in snap) };

        var p = new FractalParameters();
        region.ApplyLightingAuthoritative(p);
        Assert.Equal(0.42, p.Lighting.FogDensity);
    }

    [Fact]
    public void NonAuthoritative_ApplyLightingTo_LeavesAloneOnNull()
    {
        var region = new FractalRegion();   // null override
        var p = DramaticLit();
        region.ApplyLightingTo(p);
        // Legacy path: user's active lighting is preserved (inherited).
        Assert.Equal(0.9, p.Lighting.FogDensity);
    }

    [Fact]
    public void LightingIsAuthoritative_RoundTrips_Through_Json()
    {
        var region = new FractalRegion { Name = "R", LightingIsAuthoritative = true };
        string json = JsonSerializer.Serialize(region);
        var back = JsonSerializer.Deserialize<FractalRegion>(json)!;
        Assert.True(back.LightingIsAuthoritative);
    }

    [Fact]
    public void LightingIsAuthoritative_False_Is_Omitted_From_Json()
    {
        var region = new FractalRegion { Name = "R" };   // default false
        string json = JsonSerializer.Serialize(region);
        Assert.DoesNotContain("LightingIsAuthoritative", json);
    }
}
