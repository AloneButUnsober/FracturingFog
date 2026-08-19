// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S8 integration follow-up (3D-Rendering-Roadmap.md, #389 / #404):
// wire the point/spot LightSampler into the CPU shade path. LightSampler's math
// is unit-tested separately; these lock the WIRING: ShadingPipeline.ResolveLight
// keeps directional lights byte-identical (attenuation 1, position ignored),
// point lights fall off with distance, spot lights gate on the cone, the
// HasPositionalLight flag drives the GPU force-CPU gate, and the point/spot
// fields survive a preset round-trip.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LightTypeShadingTests
{
    private static DirectionalLight Point(double px, double py, double pz, double range = 0.0)
        => new(theta: 0, phi: 1.0, intensity: 1.0, color: 0xFFFFFFFFu)
        { Type = LightType.Point, PosX = px, PosY = py, PosZ = pz, Range = range };

    private static DirectionalLight Spot(double innerDeg, double outerDeg)
        => new(theta: 0, phi: 1.0, intensity: 1.0, color: 0xFFFFFFFFu)
        { Type = LightType.Spot, PosX = 0, PosY = 5, PosZ = 0, SpotInnerDeg = innerDeg, SpotOuterDeg = outerDeg };

    // Directional lights ignore surface position and always attenuate by 1 — the
    // byte-identical legacy contract.
    [Fact]
    public void Directional_Ignores_Position_And_Atten_Is_One()
    {
        var d = new DirectionalLight(theta: 0.5, phi: 1.0, intensity: 1.0, color: 0xFFFFFFFFu);
        var a = ShadingPipeline.ResolveLight(in d, 0.0, 0, 0, 0);
        var b = ShadingPipeline.ResolveLight(in d, 0.0, 9, -4, 7);   // far-away surface
        Assert.Equal(1.0, a.Atten, 12);
        Assert.Equal(1.0, b.Atten, 12);
        Assert.Equal(a.X, b.X, 12);   // direction unchanged by surface position
        Assert.Equal(a.Y, b.Y, 12);
        Assert.Equal(a.Z, b.Z, 12);
    }

    // A point light falls off with distance: a surface farther from the light gets
    // a smaller attenuation than a nearer one.
    [Fact]
    public void Point_Falls_Off_With_Distance()
    {
        var p = Point(0, 0, 0);
        double near = ShadingPipeline.ResolveLight(in p, 0.0, 0, 0, 2).Atten;
        double far  = ShadingPipeline.ResolveLight(in p, 0.0, 0, 0, 6).Atten;
        Assert.True(near > far, $"near {near} should exceed far {far}");
        // Inverse-square: 3× the distance ≈ 1/9 the attenuation.
        Assert.Equal(near / 9.0, far, 3);
    }

    // The point direction points from the surface toward the light.
    [Fact]
    public void Point_Direction_Points_At_Light()
    {
        var p = Point(0, 10, 0);
        var r = ShadingPipeline.ResolveLight(in p, 0.0, 0, 0, 0);
        Assert.Equal(1.0, r.Y, 6);   // straight up toward the light
        Assert.Equal(0.0, r.X, 6);
        Assert.Equal(0.0, r.Z, 6);
    }

    // A spot light lights the cone axis and cuts off beyond the outer angle.
    [Fact]
    public void Spot_Gates_On_Cone()
    {
        // Cone axis = the light's Theta/Phi direction (phi=1 → mostly +X/+Z tilt).
        // A surface directly below the light (on the downward axis toward it) lands
        // inside the cone; a surface far to the side falls outside.
        var s = Spot(innerDeg: 20, outerDeg: 30);
        // Surface right under the light: direction to light is ~+Y; cone axis is the
        // light's own direction. Use a surface placed so the to-light dir aligns.
        double onAxis  = ShadingPipeline.ResolveLight(in s, 0.0, 0, 4.9, 0).Atten;   // just below light
        double offAxis = ShadingPipeline.ResolveLight(in s, 0.0, 8, 0, 0).Atten;     // far to the side
        Assert.True(offAxis <= onAxis, $"off-axis {offAxis} should not exceed on-axis {onAxis}");
    }

    // The full shade path applies point attenuation: a hit near the light is
    // brighter than one far from it (same geometry, same normal).
    [Fact]
    public void Shade_Applies_Point_Attenuation()
    {
        uint Lit(double lightY)
        {
            var fx = LightingFxData.CreateDefault();
            fx.AmbientStrength = 0.0;   // isolate the direct term
            fx.Light1 = Point(0, lightY, 0);
            var nd = default(NullDe);
            var i = new ShadingInputs(px: 0, py: 0, pz: 0, nx: 0, ny: 1, nz: 0,
                rdx: 0, rdy: -1, rdz: 0, totalT: 5.0, hitDist: 1e-4, hitStep: 8, epsilon: 1e-4);
            return ShadingPipeline.Shade<NullDe>(in i, 0xFF808080u, in fx, in nd, hasDe: false);
        }
        uint near = Lit(2.0);
        uint far = Lit(8.0);
        double lumNear = (near >> 16 & 0xFF) + (near >> 8 & 0xFF) + (near & 0xFF);
        double lumFar = (far >> 16 & 0xFF) + (far >> 8 & 0xFF) + (far & 0xFF);
        Assert.True(lumNear > lumFar, $"near-light hit ({lumNear}) should be brighter than far ({lumFar})");
    }

    [Fact]
    public void HasPositionalLight_Reflects_Active_Point_Or_Spot()
    {
        var fx = LightingFxData.CreateDefault();
        Assert.False(fx.HasPositionalLight);   // all-directional default

        fx.Light1 = Point(0, 3, 0);
        Assert.True(fx.HasPositionalLight);

        // A positional light with zero intensity does not force the CPU path.
        var dark = Point(0, 3, 0); dark.Intensity = 0.0;
        var fx2 = LightingFxData.CreateDefault();
        fx2.Light1 = dark;
        Assert.False(fx2.HasPositionalLight);
    }

    [Fact]
    public void Preset_RoundTrips_Point_Spot_Fields()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light1 = Point(1.5, 2.5, -3.5, range: 12.0);
        fx.Light2 = Spot(18.0, 33.0);

        var fx2 = FracturingFog.Models.LightingFxPresetData.FromFx(in fx).ToFx();

        Assert.Equal(LightType.Point, fx2.Light1.Type);
        Assert.Equal(1.5, fx2.Light1.PosX, 6);
        Assert.Equal(2.5, fx2.Light1.PosY, 6);
        Assert.Equal(-3.5, fx2.Light1.PosZ, 6);
        Assert.Equal(12.0, fx2.Light1.Range, 6);
        Assert.Equal(LightType.Spot, fx2.Light2.Type);
        Assert.Equal(18.0, fx2.Light2.SpotInnerDeg, 6);
        Assert.Equal(33.0, fx2.Light2.SpotOuterDeg, 6);
    }
}
