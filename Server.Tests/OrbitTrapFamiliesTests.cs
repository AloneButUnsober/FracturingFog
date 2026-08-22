// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 (3D-Rendering-Roadmap §S9, #391) — orbit-trap colour driver across
// the struct-based DE families. After the Mandelbulb reference (#450), the other
// raymarcher families now implement IOrbitTrapEstimator too, so their mesh exports
// carry fractal structure in their colour instead of the radial fallback. These lock:
// each supported family's DE (built via the shared RaymarchMeshSampler factory)
// advertises the interface and returns a normalized, varying trap; and that KIFS
// (a delegate-adapter DE) does NOT — it keeps the radial fallback, as documented.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public class OrbitTrapFamiliesTests
{
    private static FractalParameters Params() => new()
    {
        BulbPower = 8, BulbIterations = 10,
        MandelboxScale = 2.0, MandelboxFixedRadius = 1.0, MandelboxMinRadius = 0.5,
        MandelboxBailout = 16.0, MandelboxIterations = 10,
        QJuliaCX = 0.18, QJuliaCY = 0.22, QJuliaCZ = 0.12, QJuliaCW = 0.0,
        QJuliaBailout = 16.0, QJuliaIterations = 10, QJuliaSliceW = 0.0,
        QMandelBailout = 16.0, QMandelIterations = 10, QMandelSliceW = 0.0, QMandelSliceZ = 0.0,
        BicomplexBailout = 16.0, BicomplexIterations = 10, BicomplexSliceW = 0.0,
        KleinianSphereScale = 1.0, KleinianIterations = 10,
    };

    [Theory]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.QuaternionJulia)]
    [InlineData(FractalType.QuaternionMandelbrot)]
    [InlineData(FractalType.BicomplexMandelbrot)]
    [InlineData(FractalType.Kleinian)]
    public void Family_De_Advertises_A_Normalized_Varying_Orbit_Trap(FractalType type)
    {
        var de = RaymarchMeshSampler.For(type, Params());
        Assert.NotNull(de);
        var trap = de as IOrbitTrapEstimator;
        Assert.NotNull(trap);

        double range = RaymarchMeshSampler.SuggestedRange(type, Params());
        var vals = new List<double>();
        for (int i = 0; i < 7; i++)
        for (int j = 0; j < 7; j++)
        for (int k = 0; k < 3; k++)
        {
            double x = (i / 6.0 - 0.5) * 2.0 * range * 0.6;
            double y = (j / 6.0 - 0.5) * 2.0 * range * 0.6;
            double z = (k / 2.0 - 0.5) * 2.0 * range * 0.4;
            double v = trap!.OrbitTrap(x, y, z);
            Assert.InRange(v, 0.0, 1.0);
            vals.Add(Math.Round(v, 4));
        }
        Assert.True(vals.Distinct().Count() >= 4,
            $"{type} orbit trap should carry structure, got {vals.Distinct().Count()} distinct");
    }

    [Fact]
    public void Kifs_Keeps_The_Radial_Fallback()
    {
        // KIFS is built through a delegate adapter, not a trap-aware struct, so it is
        // (intentionally) not an IOrbitTrapEstimator — the colour source falls back
        // to the radial driver for it.
        var de = RaymarchMeshSampler.For(FractalType.Kifs, Params());
        Assert.NotNull(de);
        Assert.False(de is IOrbitTrapEstimator);
    }
}
