// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #935 — AdaptiveSweepMath is the one sweep curve shared by the Control
/// Center HE Sweep, the slideshow per-leg sweep and the video slideshow.
/// Locks the default 0..100 behaviour (unchanged from the pre-range sweep)
/// and the Start/End range semantics.
/// </summary>
public sealed class AdaptiveSweepMathTests
{
    [Theory]
    [InlineData(AdaptiveSweepMode.Forward, 0.0, 0)]
    [InlineData(AdaptiveSweepMode.Forward, 0.5, 50)]
    [InlineData(AdaptiveSweepMode.Forward, 1.0, 100)]
    [InlineData(AdaptiveSweepMode.Reverse, 0.0, 100)]
    [InlineData(AdaptiveSweepMode.Reverse, 1.0, 0)]
    [InlineData(AdaptiveSweepMode.PingPong, 0.0, 0)]
    [InlineData(AdaptiveSweepMode.PingPong, 0.5, 100)]
    [InlineData(AdaptiveSweepMode.PingPong, 1.0, 0)]
    public void Default_range_matches_the_original_full_sweep(AdaptiveSweepMode mode, double phase, int expected)
        => Assert.Equal(expected, AdaptiveSweepMath.Value(phase, 0, 100, mode));

    [Theory]
    [InlineData(AdaptiveSweepMode.Forward, 20, 60)]
    [InlineData(AdaptiveSweepMode.Reverse, 60, 20)]
    [InlineData(AdaptiveSweepMode.PingPong, 20, 20)]
    public void Range_bounds_initial_and_terminal(AdaptiveSweepMode mode, int initial, int terminal)
    {
        Assert.Equal(initial, AdaptiveSweepMath.Initial(20, 60, mode));
        Assert.Equal(terminal, AdaptiveSweepMath.Terminal(20, 60, mode));
    }

    [Theory]
    [InlineData(AdaptiveSweepMode.Forward)]
    [InlineData(AdaptiveSweepMode.Reverse)]
    [InlineData(AdaptiveSweepMode.PingPong)]
    public void Values_stay_inside_the_range(AdaptiveSweepMode mode)
    {
        for (int i = -5; i <= 105; i++)
        {
            int v = AdaptiveSweepMath.Value(i / 100.0, 30, 70, mode);
            Assert.InRange(v, 30, 70);
        }
    }

    [Fact]
    public void Inverted_range_sweeps_downward()
    {
        Assert.Equal(80, AdaptiveSweepMath.Initial(80, 10, AdaptiveSweepMode.Forward));
        Assert.Equal(45, AdaptiveSweepMath.Value(0.5, 80, 10, AdaptiveSweepMode.Forward));
        Assert.Equal(10, AdaptiveSweepMath.Terminal(80, 10, AdaptiveSweepMode.Forward));
    }
}
