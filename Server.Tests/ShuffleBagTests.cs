// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Slideshow RNG ordering (Animation Roadmap follow-up): the pure
/// draw-without-replacement <see cref="ShuffleBag{T}"/>. Covers full-cycle
/// coverage, no back-to-back repeat across the reshuffle boundary,
/// determinism under a seeded RNG, and live rebuild when the source set
/// changes.
/// </summary>
public sealed class ShuffleBagTests
{
    private static ShuffleBag<int> Seeded(int seed)
        => new(new Random(seed).Next);

    [Fact]
    public void Draw_YieldsEveryItemOnce_BeforeAnyRepeat()
    {
        var items = new[] { 1, 2, 3, 4, 5 };
        var bag = Seeded(1234);

        var cycle = new List<int>();
        for (int i = 0; i < items.Length; i++) cycle.Add(bag.Draw(items));

        Assert.Equal(items.OrderBy(x => x), cycle.OrderBy(x => x)); // a permutation
        Assert.Equal(items.Length, cycle.Distinct().Count());       // no repeats within a cycle
    }

    [Fact]
    public void Draw_NeverRepeatsAcrossReshuffleBoundary()
    {
        var items = new[] { 1, 2, 3, 4 };
        var bag = Seeded(77);

        int? prev = null;
        for (int i = 0; i < 400; i++)
        {
            int cur = bag.Draw(items);
            Assert.NotEqual(prev, cur); // includes the cycle boundary
            prev = cur;
        }
    }

    [Fact]
    public void Draw_IsDeterministic_ForSameSeed()
    {
        var items = new[] { 10, 20, 30, 40, 50, 60 };
        var a = Seeded(999);
        var b = Seeded(999);

        for (int i = 0; i < 50; i++)
            Assert.Equal(a.Draw(items), b.Draw(items));
    }

    [Fact]
    public void Draw_DiffersForDifferentSeeds()
    {
        var items = Enumerable.Range(0, 12).ToArray();
        var a = Seeded(1);
        var b = Seeded(2);

        var seqA = Enumerable.Range(0, 24).Select(_ => a.Draw(items)).ToList();
        var seqB = Enumerable.Range(0, 24).Select(_ => b.Draw(items)).ToList();

        Assert.NotEqual(seqA, seqB);
    }

    [Fact]
    public void Draw_RebuildsAndOnlyReturnsCurrentMembers_WhenSetChanges()
    {
        var bag = Seeded(5);
        var first = new[] { 1, 2, 3 };
        bag.Draw(first);
        bag.Draw(first);

        // Region deleted + new one saved mid-show.
        var second = new[] { 3, 4, 5, 6 };
        var drawn = new List<int>();
        for (int i = 0; i < 8; i++) drawn.Add(bag.Draw(second));

        Assert.All(drawn, d => Assert.Contains(d, second));
        Assert.DoesNotContain(1, drawn);
        Assert.DoesNotContain(2, drawn);
    }

    [Fact]
    public void Draw_EmptySource_ReturnsDefault()
    {
        var bag = Seeded(3);
        Assert.Equal(0, bag.Draw(Array.Empty<int>()));
    }

    [Fact]
    public void Draw_SingleItem_RepeatsThatItem()
    {
        var bag = Seeded(8);
        var one = new[] { 42 };
        for (int i = 0; i < 5; i++) Assert.Equal(42, bag.Draw(one));
    }
}
