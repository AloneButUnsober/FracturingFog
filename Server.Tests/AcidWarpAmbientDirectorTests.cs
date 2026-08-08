// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #251 / IDEA-6 — auto-VJ ambient loop timing. The director owns *when* to
// advance and *to which pattern*; colour cycling is external, so a locked loop
// still cycles colour (verified indirectly: lock freezes advancement only).
public class AcidWarpAmbientDirectorTests
{
    private const int Count = 20;

    private static AcidWarpAmbientDirector Make(int holdMs, bool classic = true, int seed = 42)
    {
        var rng = new Random(seed);
        var playlist = new AcidWarpPlaylist(rng.Next, Count, startWithClassic: classic);
        return new AcidWarpAmbientDirector(playlist, holdMs);
    }

    [Fact]
    public void First_Pattern_Is_Classic_When_Requested()
    {
        var d = Make(1000, classic: true);
        Assert.Equal(AcidWarpIntro.ClassicPattern, d.CurrentPattern);
    }

    [Fact]
    public void Auto_Advance_Fires_On_Timer()
    {
        var d = Make(1000);
        int start = d.CurrentPattern;

        Assert.False(d.Tick(500));            // half the hold — no advance yet
        Assert.Equal(start, d.CurrentPattern);

        Assert.True(d.Tick(500));             // hold elapsed — advance
        Assert.InRange(d.CurrentPattern, 0, Count - 1);
        Assert.Equal(0, d.ElapsedMs);         // clock reset for the next hold
    }

    [Fact]
    public void Lock_Freezes_Advancement()
    {
        var d = Make(1000);
        int held = d.CurrentPattern;
        d.Locked = true;

        // Ten holds' worth of ticks must not advance while locked.
        for (int i = 0; i < 20; i++) Assert.False(d.Tick(1000));
        Assert.Equal(held, d.CurrentPattern);

        // Unlock → the very next full-hold tick advances again.
        d.Locked = false;
        Assert.True(d.Tick(1000));
        Assert.NotEqual(held, d.CurrentPattern);
    }

    [Fact]
    public void Pause_Freezes_Hold_Clock()
    {
        var d = Make(1000);
        int held = d.CurrentPattern;
        d.Paused = true;
        for (int i = 0; i < 5; i++) Assert.False(d.Tick(1000));
        Assert.Equal(0, d.ElapsedMs);         // no time accrued while paused
        Assert.Equal(held, d.CurrentPattern);
    }

    [Fact]
    public void RequestNext_Advances_Even_When_Locked()
    {
        var d = Make(1000);
        int held = d.CurrentPattern;
        d.Locked = true;
        d.RequestNext();
        Assert.True(d.Tick(0));               // manual next overrides the lock
        Assert.NotEqual(held, d.CurrentPattern);
    }

    [Fact]
    public void HoldMs_Is_Floored()
    {
        var d = Make(1);                      // below the 100 ms floor
        Assert.True(d.HoldMs >= 100);
        d.HoldMs = -5;
        Assert.True(d.HoldMs >= 100);
    }

    [Fact]
    public void Advance_Draws_Playlist_Order()
    {
        // A director seeded identically to a bare playlist must visit the same
        // sequence: ctor consumes the first draw, each advance the next.
        int seed = 777;
        var reference = new AcidWarpPlaylist(new Random(seed).Next, Count, startWithClassic: true);
        var d = Make(1000, classic: true, seed: seed);

        Assert.Equal(reference.Next(), d.CurrentPattern);   // both consumed draw #1
        for (int i = 0; i < Count * 2; i++)
        {
            Assert.True(d.Tick(1000));
            Assert.Equal(reference.Next(), d.CurrentPattern);
        }
    }
}
