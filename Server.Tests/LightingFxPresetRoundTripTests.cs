// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression net for VLAO audit finding #11 (issue #290): LightingFxPresetData
// silently dropped runtime fields on FromFx/ToFx round-trip because the DTO was
// hand-mirrored from the LightingFxData struct with no compile-time link. These
// tests (a) round-trip each formerly-dropped field and (b) reflect over the
// struct to fail if a NEW field is added to LightingFxData without a matching
// DTO property.

using System.Linq;
using System.Reflection;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LightingFxPresetRoundTripTests
{
    private static LightingFxData RoundTrip(in LightingFxData fx)
        => LightingFxPresetData.FromFx(in fx).ToFx();

    [Fact]
    public void RoundTrip_Preserves_VolumeStepsFalloff()
    {
        var fx = LightingFxData.CreateDefault();
        fx.VolumeStepsFalloff = 0.9;
        Assert.Equal(0.9, RoundTrip(in fx).VolumeStepsFalloff);
    }

    [Fact]
    public void RoundTrip_Preserves_MaxBounces()
    {
        var fx = LightingFxData.CreateDefault();
        fx.MaxBounces = 4;
        Assert.Equal(4, RoundTrip(in fx).MaxBounces);
    }

    [Fact]
    public void RoundTrip_Preserves_UseGgxSampling()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGgxSampling = true;
        Assert.True(RoundTrip(in fx).UseGgxSampling);
    }

    [Fact]
    public void RoundTrip_Preserves_GpuToggles()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuPost = true;
        fx.UseGpuRender = true;
        var back = RoundTrip(in fx);
        Assert.True(back.UseGpuPost);
        Assert.True(back.UseGpuRender);
    }

    [Fact]
    public void RoundTrip_Preserves_Triplanar()
    {
        var fx = LightingFxData.CreateDefault();
        fx.TriplanarKind = TriplanarTextureKind.Marble;
        fx.TriplanarScale = 7.0;
        fx.TriplanarStrength = 0.7;
        fx.TriplanarTint = 0xFF112233u;
        var back = RoundTrip(in fx);
        Assert.Equal(TriplanarTextureKind.Marble, back.TriplanarKind);
        Assert.Equal(7.0, back.TriplanarScale);
        Assert.Equal(0.7, back.TriplanarStrength);
        Assert.Equal(0xFF112233u, back.TriplanarTint);
    }

    [Fact]
    public void RoundTrip_Preserves_StereoEyeOffset()
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoEyeOffset = 0.42;
        Assert.Equal(0.42, RoundTrip(in fx).StereoEyeOffset);
    }

    // Drift guard: every public instance field on LightingFxData must be carried
    // by LightingFxPresetData, either as a same-named property or flattened.
    // Excludes the three DirectionalLight structs (flattened into Light{1,2,3}
    // Theta/Phi/Intensity/Color) and VolumePalette (a per-frame baked LUT that is
    // intentionally never serialized — see the DTO comment on the field).
    [Fact]
    public void EveryLightingFxDataField_HasMatchingDtoProperty()
    {
        var flattened = new[] { "Light1", "Light2", "Light3" };
        var intentionallyUnpersisted = new[] { "VolumePalette" };

        var fxFields = typeof(LightingFxData)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name)
            .Where(n => !n.StartsWith("<"))
            .Where(n => !flattened.Contains(n))
            .Where(n => !intentionallyUnpersisted.Contains(n))
            .ToHashSet();

        var dtoProps = typeof(LightingFxPresetData)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        var missing = fxFields.Except(dtoProps).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0,
            "LightingFxPresetData is missing a DTO property (or FromFx/ToFx wiring) " +
            "for these LightingFxData fields — add them or the value drops on " +
            $"round-trip: {string.Join(", ", missing)}");
    }

    // Finding #10 (issue #294): the DTO default for sentinel-fallback fields now
    // visibly matches the runtime effective default, so a user reading the JSON
    // sees the value that actually runs (0 still maps to 24 internally for
    // legacy presets, but a fresh preset serializes the honest value).
    [Fact]
    public void DtoDefaults_Match_RuntimeEffectiveDefaults()
    {
        var dto = new LightingFxPresetData();
        Assert.Equal(24, dto.ReflectionSteps);
        Assert.Equal(1, dto.MaxBounces);

        var fx = LightingFxData.CreateDefault();
        Assert.Equal(24, fx.ReflectionSteps);
        Assert.Equal(1, fx.MaxBounces);
    }
}
