// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S8 (3D-Rendering-Roadmap.md, parent #389) — point / spot / area
// light sampling. Contract: Directional is the legacy identity (toward-light
// unchanged, attenuation 1); Point falls off inverse-square with a smooth range
// window; Spot multiplies by a smooth cone factor.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LightSamplerTests
{
    private static double Cos(double deg) => Math.Cos(deg * Math.PI / 180.0);

    // Directional ignores position/surface and returns the incoming direction
    // with attenuation 1 — a directional scene is byte-identical to pre-S8.
    [Fact]
    public void Directional_Is_Identity()
    {
        var (lx, ly, lz, a) = LightSampler.Sample(
            LightType.Directional, 0, 1, 0, 5, 5, 5, range: 3, innerCos: 0.9, outerCos: 0.8,
            sx: 2, sy: 2, sz: 2);
        Assert.Equal((0.0, 1.0, 0.0), (lx, ly, lz));
        Assert.Equal(1.0, a);
    }

    // Point light: direction points from the surface toward the light, unit length.
    [Fact]
    public void Point_Direction_Points_At_Light()
    {
        var (lx, ly, lz, _) = LightSampler.Sample(
            LightType.Point, 0, 1, 0, posX: 0, posY: 10, posZ: 0, range: 0, innerCos: 0, outerCos: 0,
            sx: 0, sy: 0, sz: 0);
        Assert.Equal(0.0, lx, 9);
        Assert.Equal(1.0, ly, 9);
        Assert.Equal(0.0, lz, 9);
        Assert.Equal(1.0, Math.Sqrt(lx * lx + ly * ly + lz * lz), 9);
    }

    // Point falls off as 1/d² (range 0 = pure inverse-square): doubling the
    // distance quarters the attenuation.
    [Fact]
    public void Point_Falls_Off_Inverse_Square()
    {
        var near = LightSampler.Sample(LightType.Point, 0, 1, 0, 0, 10, 0, 0, 0, 0, 0, 0, 0);
        var far = LightSampler.Sample(LightType.Point, 0, 1, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(4.0, near.atten / far.atten, 3);   // (20/10)² = 4
    }

    // The range window drives attenuation to 0 at/after the range.
    [Fact]
    public void Point_Range_Window_Cuts_Off()
    {
        var atRange = LightSampler.Sample(LightType.Point, 0, 1, 0, 0, 10, 0, range: 10, 0, 0, 0, 0, 0);
        Assert.Equal(0.0, atRange.atten, 9);
        var inside = LightSampler.Sample(LightType.Point, 0, 1, 0, 0, 5, 0, range: 10, 0, 0, 0, 0, 0);
        Assert.True(inside.atten > 0.0);
    }

    // Spot: full on the cone axis, zero well outside the outer cone.
    [Fact]
    public void Spot_Full_On_Axis_Zero_Outside()
    {
        double inner = Cos(15), outer = Cos(25);
        // Surface directly below the light along the axis (toDir = +Y).
        var onAxis = LightSampler.Sample(LightType.Spot, 0, 1, 0, 0, 10, 0, 0, inner, outer, 0, 0, 0);
        // Surface far to the side → dir to light well off the +Y axis (>25°).
        var offAxis = LightSampler.Sample(LightType.Spot, 0, 1, 0, 0, 10, 0, 0, inner, outer, 8, 0, 0);

        // On-axis keeps the full inverse-square value; off-axis is fully masked.
        var pointRef = LightSampler.Sample(LightType.Point, 0, 1, 0, 0, 10, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(pointRef.atten, onAxis.atten, 9);
        Assert.Equal(0.0, offAxis.atten, 9);
    }

    // The cone factor is monotone across the penumbra and pinned at the ends.
    [Fact]
    public void SmoothCone_Is_Monotone_And_Pinned()
    {
        double inner = Cos(15), outer = Cos(25);
        Assert.Equal(1.0, LightSampler.SmoothCone(1.0, inner, outer), 9);        // on axis
        Assert.Equal(1.0, LightSampler.SmoothCone(inner, inner, outer), 9);      // at inner edge
        Assert.Equal(0.0, LightSampler.SmoothCone(outer, inner, outer), 9);      // at outer edge
        Assert.Equal(0.0, LightSampler.SmoothCone(Cos(40), inner, outer), 9);    // outside

        double prev = -1;
        for (int d = 25; d >= 15; d--)   // sweep outer→inner, factor should rise
        {
            double f = LightSampler.SmoothCone(Cos(d), inner, outer);
            Assert.True(f >= prev, $"cone not monotone at {d}°");
            prev = f;
        }
    }
}
