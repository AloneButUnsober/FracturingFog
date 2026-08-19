// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S6 integration follow-up (3D-Rendering-Roadmap.md, #389 / #408):
// FroxelVolumePass assembles the froxel primitives (grid + integrator) into a
// populate → integrate → composite pipeline. Contract: an empty medium leaves the
// beauty untouched; a uniform medium attenuates farther pixels more than near ones
// and its far transmittance follows Beer-Lambert; colored in-scatter tints the fog;
// and noise makes columns heterogeneous.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelVolumePassTests
{
    private static FroxelVolumePass Build(int dz = 32, double near = 1.0, double far = 50.0)
        => new(new FroxelGrid(4, 4, dz, near, far));

    private static FroxelMedium Uniform(double density, double ext, uint light = 0xFF000000u) => new()
    {
        BaseDensity = density, Extinction = ext,
        LightColor = light, LightIntensity = light == 0xFF000000u ? 0.0 : 1.0,
        Lx = 0, Ly = 1, Lz = 0, ViewDx = 0, ViewDy = 0, ViewDz = 1,
        Anisotropy = 0.0, NoiseAmount = 0.0, NoiseScale = 1.0, NoiseOctaves = 3,
        WorldExtent = 4.0,
    };

    private static (uint[] beauty, float[] depth) Frame(int w, int h, uint color, float depth)
    {
        var b = new uint[w * h]; var d = new float[w * h];
        for (int i = 0; i < b.Length; i++) { b[i] = color; d[i] = depth; }
        return (b, d);
    }

    [Fact]
    public void Empty_Medium_Leaves_Beauty_Unchanged()
    {
        var pass = Build();
        pass.Populate(Uniform(density: 0.0, ext: 0.0));
        var (beauty, depth) = Frame(8, 8, 0xFF3366CCu, 1.0f);
        var outB = pass.Composite(beauty, depth, 8, 8);
        Assert.Equal(beauty, outB);
    }

    [Fact]
    public void Uniform_Medium_Attenuates_Far_More_Than_Near()
    {
        var pass = Build();
        pass.Populate(Uniform(density: 1.0, ext: 0.1));
        // White beauty, no in-scatter (black light) → farther = darker (more fog).
        var (bn, dn) = Frame(4, 4, 0xFFFFFFFFu, 0.05f);  // near
        var (bf, df) = Frame(4, 4, 0xFFFFFFFFu, 0.95f);  // far
        var on = pass.Composite(bn, dn, 4, 4);
        var of = pass.Composite(bf, df, 4, 4);
        int near = (int)(on[0] & 0xFF);
        int far = (int)(of[0] & 0xFF);
        Assert.True(far < near, $"far pixel ({far}) should be darker than near ({near})");
    }

    [Fact]
    public void Far_Transmittance_Follows_BeerLambert()
    {
        int dz = 32; double near = 1.0, far = 33.0;
        var pass = Build(dz, near, far);
        double ext = 0.05;
        pass.Populate(Uniform(density: 1.0, ext: ext));
        // Column sampled at the last slice: transmittance ≈ exp(-ext · total path).
        // Total path = sum of slice thicknesses = far - near.
        var s = pass.SampleColumn(0, 0, dz - 1);
        double expected = Math.Exp(-ext * (far - near));
        Assert.Equal(expected, s.trans, 4);
        Assert.Equal(0.0, s.inR, 9);   // black light → no in-scatter
    }

    [Fact]
    public void Colored_InScatter_Tints_The_Fog()
    {
        var pass = Build();
        // Red light in-scatter over a black beauty → the fog reddens the pixel.
        pass.Populate(Uniform(density: 1.0, ext: 0.1, light: 0xFFFF0000u));
        var (beauty, depth) = Frame(4, 4, 0xFF000000u, 0.95f);
        var outB = pass.Composite(beauty, depth, 4, 4);
        int r = (int)((outB[0] >> 16) & 0xFF);
        int g = (int)((outB[0] >> 8) & 0xFF);
        int b = (int)(outB[0] & 0xFF);
        Assert.True(r > 0, "red in-scatter should light the fog");
        Assert.True(r > g && r > b, $"fog should be red-tinted (r={r}, g={g}, b={b})");
    }

    [Fact]
    public void Noise_Makes_Columns_Heterogeneous()
    {
        var pass = Build();
        var m = Uniform(density: 1.0, ext: 0.2);
        m = m with { NoiseAmount = 0.9, NoiseScale = 0.5 };
        pass.Populate(m);
        double t00 = pass.SampleColumn(0, 0, 31).trans;
        double t31 = pass.SampleColumn(3, 3, 31).trans;
        Assert.NotEqual(t00, t31);
    }

    [Fact]
    public void Composite_Preserves_Alpha()
    {
        var pass = Build();
        pass.Populate(Uniform(density: 1.0, ext: 0.3, light: 0xFFFFFFFFu));
        var (beauty, depth) = Frame(4, 4, 0x80112233u, 0.9f);
        var outB = pass.Composite(beauty, depth, 4, 4);
        Assert.Equal(0x80u, (outB[0] >> 24) & 0xFF);
    }
}
