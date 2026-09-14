// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Covers the pure UI-scale step ladder (#809 / S1 #810): snap, step
/// up/down, saturation, and the store round-trip. The live service lives in
/// UI.Avalonia (not referenced here); all value logic it uses is tested via the
/// Abstractions helpers below.</summary>
public sealed class UiScaleLadderTests
{
    [Fact]
    public void Steps_include_100_percent_and_are_ascending()
    {
        Assert.Contains(1.0, UiScaleLadder.Steps);
        for (int i = 1; i < UiScaleLadder.Steps.Length; i++)
            Assert.True(UiScaleLadder.Steps[i] > UiScaleLadder.Steps[i - 1]);
        Assert.Equal(UiScaleLadder.Steps[0], UiScaleLadder.Min);
        Assert.Equal(UiScaleLadder.Steps[^1], UiScaleLadder.Max);
    }

    [Theory]
    [InlineData(0.0)]     // unset (store default)
    [InlineData(-1.0)]    // negative
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Snap_maps_invalid_input_to_default(double bad)
    {
        Assert.Equal(UiScaleLadder.Default, UiScaleLadder.Snap(bad));
    }

    [Fact]
    public void Snap_clamps_to_ladder_ends()
    {
        Assert.Equal(UiScaleLadder.Min, UiScaleLadder.Snap(0.1));
        Assert.Equal(UiScaleLadder.Max, UiScaleLadder.Snap(9.9));
    }

    [Theory]
    [InlineData(1.04, 1.00)]  // rounds down to nearest step
    [InlineData(1.06, 1.10)]  // rounds up to nearest step
    [InlineData(1.50, 1.50)]  // exact member stays put
    [InlineData(1.40, 1.50)]  // midpoint-ish between 1.25 and 1.50 → nearer 1.50
    public void Snap_picks_nearest_step(double input, double expected)
    {
        Assert.Equal(expected, UiScaleLadder.Snap(input));
    }

    [Fact]
    public void Next_and_Prev_walk_one_rung_and_saturate()
    {
        Assert.Equal(1.10, UiScaleLadder.Next(1.00));
        Assert.Equal(0.90, UiScaleLadder.Prev(1.00));

        // Saturation at the ends — never past Min/Max.
        Assert.Equal(UiScaleLadder.Max, UiScaleLadder.Next(UiScaleLadder.Max));
        Assert.Equal(UiScaleLadder.Min, UiScaleLadder.Prev(UiScaleLadder.Min));
    }

    [Fact]
    public void Next_and_Prev_snap_arbitrary_input_first()
    {
        // 1.04 snaps to 1.00, so Next is 1.10 and Prev is 0.90.
        Assert.Equal(1.10, UiScaleLadder.Next(1.04));
        Assert.Equal(0.90, UiScaleLadder.Prev(1.04));
    }

    [Fact]
    public void Store_round_trips_and_snaps_on_save()
    {
        // Data root is redirected to a temp dir for the whole test process
        // (TestDataRootIsolation), so these read/write the throwaway file.
        UiScaleStore.Save(1.5);
        Assert.Equal(1.5, UiScaleStore.Load());

        UiScaleStore.Save(0.7);
        Assert.Equal(0.7, UiScaleStore.Load());

        // An off-ladder value is snapped before persisting.
        UiScaleStore.Save(1.06);
        Assert.Equal(1.10, UiScaleStore.Load());
    }
}
