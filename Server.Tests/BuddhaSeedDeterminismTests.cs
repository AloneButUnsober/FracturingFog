// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #193 — the Buddhabrot family is a Monte Carlo density plot. Its RNG used to be
// seeded from Environment.TickCount, so every re-sample was a different random
// realization: identical params rendered a different image, and any setting
// change (which forces a full re-sample on this alt calculator) made the fractal
// appear to 'morph'. FractalParameters.BuddhaSeed now seeds the sampler
// deterministically. These lock in that:
//   • same seed + same params → bit-identical ColorBuffer
//   • different seed → a different image (seed actually drives the sampler)
public class BuddhaSeedDeterminismTests
{
    private static uint[] Render(int seed, bool metropolis = false, bool progressive = false)
    {
        var calc = new BuddhabrotCalculator(64, 64)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            FractalParameters = new FractalParameters
            {
                BuddhaSamples = 200_000,
                BuddhaSeed = seed,
                BuddhaMetropolis = metropolis,
                BuddhaProgressive = progressive,
            },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void SameSeed_ProducesIdenticalImage()
    {
        var a = Render(12345);
        var b = Render(12345);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentImage()
    {
        var a = Render(12345);
        var b = Render(67890);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SameSeed_Deterministic_WithMetropolisAndProgressive()
    {
        var a = Render(999, metropolis: true, progressive: true);
        var b = Render(999, metropolis: true, progressive: true);
        Assert.Equal(a, b);
    }
}
