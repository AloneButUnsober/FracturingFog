// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using Xunit;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #251 / IDEA-6 — the Acid Warp auto-VJ playlist. Locks in the shuffle
// contract: classic intro first (when asked), then every pattern once per
// cycle with no repeats, deterministically given a seeded RNG.
public class AcidWarpPlaylistTests
{
    private const int Count = 20;

    [Fact]
    public void StartsWithClassic_ThenShuffles()
    {
        var rng = new Random(1234);
        var pl = new AcidWarpPlaylist(rng.Next, Count, startWithClassic: true);
        Assert.Equal(AcidWarpIntro.ClassicPattern, pl.Next());
    }

    [Fact]
    public void One_Cycle_Covers_Every_Pattern_Once()
    {
        var rng = new Random(99);
        var pl = new AcidWarpPlaylist(rng.Next, Count, startWithClassic: false);
        var seen = new HashSet<int>();
        for (int i = 0; i < Count; i++)
        {
            int p = pl.Next();
            Assert.InRange(p, 0, Count - 1);
            Assert.True(seen.Add(p), $"pattern {p} repeated within one cycle");
        }
        Assert.Equal(Count, seen.Count);
    }

    [Fact]
    public void Seeded_Runs_Are_Reproducible()
    {
        var a = new AcidWarpPlaylist(new Random(7).Next, Count, startWithClassic: true);
        var b = new AcidWarpPlaylist(new Random(7).Next, Count, startWithClassic: true);
        for (int i = 0; i < Count * 2; i++)
            Assert.Equal(a.Next(), b.Next());
    }

    [Fact]
    public void No_Back_To_Back_Repeat_Across_Reshuffle()
    {
        var pl = new AcidWarpPlaylist(new Random(5).Next, Count, startWithClassic: false);
        int prev = -1;
        for (int i = 0; i < Count * 4; i++)
        {
            int p = pl.Next();
            Assert.NotEqual(prev, p);
            prev = p;
        }
    }
}
