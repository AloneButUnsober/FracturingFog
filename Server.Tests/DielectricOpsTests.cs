// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S5 (3D-Rendering-Roadmap.md, parent #389) — the dielectric
// refraction / Fresnel / absorption math. Contract: Snell's law holds, total
// internal reflection is detected, Fresnel runs f0→1, and Beer-Lambert tints a
// path with the reference color at the reference distance.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DielectricOpsTests
{
    // Normal incidence passes straight through, undeviated and unit length.
    [Fact]
    public void Refract_NormalIncidence_Undeviated()
    {
        var (x, y, z, tir) = DielectricOps.Refract(0, 0, -1, 0, 0, 1, eta: 1.0 / 1.5);
        Assert.False(tir);
        Assert.Equal(0.0, x, 9);
        Assert.Equal(0.0, y, 9);
        Assert.Equal(-1.0, z, 9);
    }

    // Snell's law: sin(theta_t) / sin(theta_i) == eta.
    [Fact]
    public void Refract_Obeys_Snells_Law()
    {
        double eta = 1.0 / 1.5;   // air → glass
        double s = Math.Sin(Math.PI / 4), c = Math.Cos(Math.PI / 4);
        // Incident at 45° into a +Y-facing surface.
        var (x, y, z, tir) = DielectricOps.Refract(s, -c, 0, 0, 1, 0, eta);
        Assert.False(tir);
        Assert.Equal(1.0, Math.Sqrt(x * x + y * y + z * z), 9);

        double sinI = s;                             // horizontal component of I
        double sinT = Math.Sqrt(x * x + z * z);      // horizontal component of refracted
        Assert.Equal(eta, sinT / sinI, 6);
        // Entering the denser medium bends toward the normal → smaller angle.
        Assert.True(sinT < sinI);
    }

    // Beyond the critical angle, glass → air totally internally reflects.
    [Fact]
    public void Refract_TotalInternalReflection()
    {
        double eta = 1.5;   // glass → air
        double s = Math.Sin(Math.PI / 3), c = Math.Cos(Math.PI / 3);   // 60° > critical (~41.8°)
        var (x, y, z, tir) = DielectricOps.Refract(s, -c, 0, 0, 1, 0, eta);
        Assert.True(tir);
        // The returned direction is the reflection: +Y component flips up.
        Assert.True(y > 0, "TIR should reflect back into the medium (+Y)");
        Assert.Equal(1.0, Math.Sqrt(x * x + y * y + z * z), 9);
    }

    // Mirror reflection about the normal.
    [Fact]
    public void Reflect_Mirrors_About_Normal()
    {
        var (x, y, z) = DielectricOps.Reflect(0, 0, -1, 0, 0, 1);
        Assert.Equal((0.0, 0.0, 1.0), (x, y, z));
    }

    // Schlick Fresnel: normal incidence = f0 (~0.04 for glass), grazing → 1,
    // monotone increasing as the angle opens.
    [Fact]
    public void Fresnel_Runs_F0_To_One()
    {
        double f0 = DielectricOps.F0(1.0, 1.5);
        Assert.Equal(0.04, f0, 4);

        Assert.Equal(f0, DielectricOps.FresnelSchlick(1.0, f0), 6);   // normal incidence
        Assert.Equal(1.0, DielectricOps.FresnelSchlick(0.0, f0), 6);  // grazing

        double prev = -1;
        for (int i = 10; i >= 0; i--)   // cos 1 → 0, reflectance should rise
        {
            double r = DielectricOps.FresnelSchlick(i / 10.0, f0);
            Assert.True(r >= prev, $"Fresnel not monotone at cos={i / 10.0}");
            prev = r;
        }
    }

    // Beer-Lambert: clear at distance 0, reproduces the tint at the reference
    // distance, darkens further out; white tint never absorbs.
    [Fact]
    public void BeerLambert_Absorbs_Along_Path()
    {
        // Red-passing tint: R survives, G/B absorbed.
        uint tint = 0xFFFF0000u;
        Assert.Equal((1.0, 1.0, 1.0), DielectricOps.BeerLambert(tint, refDistance: 2, distance: 0));
        Assert.Equal((1.0, 1.0, 1.0), DielectricOps.BeerLambert(0xFFFFFFFFu, 2, 5));   // clear

        var atRef = DielectricOps.BeerLambert(tint, refDistance: 2, distance: 2);
        Assert.Equal(1.0, atRef.r, 6);     // R fully survives
        Assert.Equal(0.0, atRef.g, 6);     // G absorbed to the tint (0) at ref dist
        Assert.Equal(0.0, atRef.b, 6);

        // A partially-absorbing green tint darkens with distance.
        uint green = 0xFF008000u;          // G ≈ 0.5 survives per ref distance
        var near = DielectricOps.BeerLambert(green, 2, 1);
        var far = DielectricOps.BeerLambert(green, 2, 4);
        Assert.True(far.g < near.g, "longer path should absorb more");
    }
}
