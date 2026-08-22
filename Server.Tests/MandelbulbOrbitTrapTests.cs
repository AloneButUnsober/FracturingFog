// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 (3D-Rendering-Roadmap §S9, #391) — orbit-trap colour driver. The
// Mandelbulb DE now reports a view-independent, fractal-meaningful orbit trap
// (closest the iteration orbit passes to the origin, normalized), so the mesh export
// colour source can drive the palette with fractal structure instead of the radial
// fallback. These lock the estimator's contract: values in [0,1], deterministic, and
// varying across space (a flat value would carry no structure); and that the DE
// advertises IOrbitTrapEstimator so the colour source can detect it.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public class MandelbulbOrbitTrapTests
{
    [Fact]
    public void Mandelbulb_De_Advertises_Orbit_Trap()
    {
        var de = new MandelbulbDe(8.0, 12);
        Assert.IsAssignableFrom<IDistanceEstimator>(de);
        Assert.IsAssignableFrom<IOrbitTrapEstimator>(de);
    }

    [Fact]
    public void Orbit_Trap_Is_Normalized_And_Deterministic()
    {
        var de = new MandelbulbDe(8.0, 12);
        for (double x = -1.0; x <= 1.0; x += 0.25)
        for (double y = -1.0; y <= 1.0; y += 0.25)
        {
            double v = de.OrbitTrap(x, y, 0.13);
            Assert.InRange(v, 0.0, 1.0);
            Assert.Equal(v, de.OrbitTrap(x, y, 0.13), 12);   // deterministic
        }
    }

    [Fact]
    public void Orbit_Trap_Varies_Across_Space()
    {
        var de = new MandelbulbDe(8.0, 16);
        var vals = new System.Collections.Generic.List<double>();
        for (double x = -1.2; x <= 1.2; x += 0.1)
        for (double y = -1.2; y <= 1.2; y += 0.1)
            vals.Add(Math.Round(de.OrbitTrap(x, y, 0.05), 4));
        int distinct = vals.Distinct().Count();
        Assert.True(distinct > 8, $"orbit trap should carry structure, got {distinct} distinct values");
    }
}
