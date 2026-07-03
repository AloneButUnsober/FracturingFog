using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// End-to-end proof of the slideshow RNG ordering mechanism the engine
/// relies on: SlideshowEngine seeds its RNG per Start with
/// <c>settings.RandomSeed != 0 ? new Random(seed) : new Random()</c> and
/// draws regions through a <see cref="ShuffleBag{T}"/>. These tests mirror
/// that exact usage headlessly (the live engine needs a render host + theme
/// service), so they lock in the user-visible behaviour: a fixed seed
/// replays the same order, seed 0 varies, and every cycle covers the pool.
/// </summary>
public sealed class SlideshowOrderingTests
{
    private static readonly string[] Pool =
        { "A", "B", "C", "D", "E", "F", "G", "H" };

    // Reproduces SlideshowEngine.Start's seed expression + region-bag draw.
    private static List<string> Run(int randomSeed, int draws)
    {
        var rng = randomSeed != 0 ? new Random(randomSeed) : new Random();
        var bag = new ShuffleBag<string>(n => rng.Next(n), StringComparer.Ordinal);
        var seq = new List<string>(draws);
        for (int i = 0; i < draws; i++) seq.Add(bag.Draw(Pool));
        return seq;
    }

    [Fact]
    public void FixedSeed_ReplaysIdenticalRegionOrder()
    {
        Assert.Equal(Run(42, 40), Run(42, 40));
    }

    [Fact]
    public void ZeroSeed_VariesBetweenRuns()
    {
        // Two independent entropy-seeded runs over 40 draws of an 8-region
        // pool: identical sequences are astronomically unlikely.
        Assert.NotEqual(Run(0, 40), Run(0, 40));
    }

    [Fact]
    public void EveryCycle_CoversWholePool_NoBackToBackRepeat()
    {
        var seq = Run(1234, Pool.Length * 4); // 4 full cycles

        // Each contiguous cycle is a full permutation of the pool.
        for (int c = 0; c < 4; c++)
        {
            var cycle = seq.Skip(c * Pool.Length).Take(Pool.Length);
            Assert.Equal(Pool.OrderBy(x => x), cycle.OrderBy(x => x));
        }

        // No region shown twice in a row, including across cycle boundaries.
        for (int i = 1; i < seq.Count; i++)
            Assert.NotEqual(seq[i - 1], seq[i]);
    }
}
