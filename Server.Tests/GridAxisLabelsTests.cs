// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1559 — grid ticks + label modes. The invariants are checked against values
// built independently of GridAxis: a double's exact decimal expansion computed
// here via powers of five, and labels parsed back with a separate decimal parser.

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class GridAxisLabelsTests
{
    // ── Independent exact arithmetic: values as (BigInteger n) * 10^-Scale ──

    private const int Scale = 1200;   // enough decimals for any double limb

    /// <summary>d exactly, scaled by 10^Scale: d = m * 2^x = m * 5^-x * 10^x.</summary>
    private static BigInteger ExactScaled(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int rawExp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        int x = rawExp == 0 ? -1074 : rawExp - 1075;
        if (rawExp != 0) mant |= 1L << 52;
        BigInteger m = d < 0 ? -mant : mant;
        // m * 2^x * 10^Scale
        return x >= 0
            ? m * BigInteger.Pow(2, x) * BigInteger.Pow(10, Scale)
            : m * BigInteger.Pow(5, -x) * BigInteger.Pow(10, Scale + x);
    }

    /// <summary>Parse a plain or scientific decimal label, scaled by 10^Scale.</summary>
    private static BigInteger ParseScaled(string s)
    {
        int exp = 0;
        int ei = s.IndexOf('e');
        if (ei >= 0) { exp = int.Parse(s[(ei + 1)..], CultureInfo.InvariantCulture); s = s[..ei]; }
        bool neg = s.StartsWith('-');
        s = s.TrimStart('+', '-');
        int dot = s.IndexOf('.');
        string digits = dot < 0 ? s : s.Remove(dot, 1);
        int decimals = dot < 0 ? 0 : s.Length - dot - 1;
        BigInteger v = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        int shift = Scale + exp - decimals;
        v = shift >= 0 ? v * BigInteger.Pow(10, shift) : v / BigInteger.Pow(10, -shift);
        return neg ? -v : v;
    }

    private static BigInteger TickScaled(GridTick t, int e) => t.Mantissa * BigInteger.Pow(10, Scale + e);

    private static GridAxis Axis(double[] limbs, double half)
    {
        var (s, e) = GridAxis.NiceStep(2 * half / 7.0);
        return GridAxis.Build(limbs, half, s, e);
    }

    // Deep coordinate with real low limbs (DD/QD/OD).
    private static readonly double[] Deep =
        { -0.7436438870371587, 1.23e-17, -4.56e-34, 7.89e-51, -1.2e-67, 3.4e-84, 0, 0 };

    // ── Tick placement ──

    [Theory]
    [InlineData(1.0)]
    [InlineData(1e-10)]
    [InlineData(1e-20)]   // the old overlay collapsed past ~1e-15 here
    [InlineData(1e-40)]
    [InlineData(1e-60)]
    public void Ticks_AreDistinct_InView_AndOffsetsMatchExactCentre(double half)
    {
        var axis = Axis(Deep, half);
        Assert.InRange(axis.Ticks.Count, 3, 20);

        BigInteger centre = Deep.Aggregate(BigInteger.Zero, (acc, l) => acc + ExactScaled(l));
        BigInteger halfS = ExactScaled(half);
        BigInteger stepS = axis.StepMantissa * BigInteger.Pow(10, Scale + axis.Exponent);
        for (int i = 0; i < axis.Ticks.Count; i++)
        {
            var t = axis.Ticks[i];
            BigInteger value = TickScaled(t, axis.Exponent);
            BigInteger off = value - centre;
            Assert.True(BigInteger.Abs(off) <= halfS, $"tick {i} outside the view");
            // Offset in doubles agrees with the exact offset to ~1e-12 of the step.
            double stepD = axis.Step;
            double exactOff = (double)(off * BigInteger.Pow(10, 30) / stepS) / 1e30 * stepD;
            Assert.True(Math.Abs(t.OffsetFromCentre - exactOff) <= 1e-12 * stepD,
                $"tick {i}: offset {t.OffsetFromCentre:R} vs exact {exactOff:R}");
            if (i > 0)
                Assert.Equal(axis.Ticks[i - 1].Mantissa + axis.StepMantissa, t.Mantissa);
        }
        // The next tick either side would be outside the view.
        BigInteger first = TickScaled(axis.Ticks[0], axis.Exponent);
        BigInteger last = TickScaled(axis.Ticks[^1], axis.Exponent);
        Assert.True(first - stepS < centre - halfS);
        Assert.True(last + stepS > centre + halfS);
    }

    [Fact]
    public void Origin_IsAZeroTick_WhenVisible()
    {
        var axis = Axis(new[] { 0.3 }, 1.0);
        Assert.Contains(axis.Ticks, t => t.IsZero && Math.Abs(t.OffsetFromCentre + 0.3) < 1e-15);
    }

    [Theory]
    [InlineData(0.7, 1, 0)]
    [InlineData(0.15, 2, -1)]
    [InlineData(1.0, 1, 0)]
    [InlineData(1.5, 2, 0)]
    [InlineData(3.0, 5, 0)]
    [InlineData(7.0, 1, 1)]
    [InlineData(1e-30, 1, -30)]
    public void NiceStep_IsOneTwoFive(double raw, int s, int e)
        => Assert.Equal((s, e), GridAxis.NiceStep(raw));

    // ── Labels ──

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-0.75, 0.01)]
    [InlineData(12.345, 3e-4)]
    [InlineData(0.25, 2.5)]
    public void Auto_Shallow_LabelsExact_Distinct_Minimal(double centre, double half)
    {
        var axis = Axis(new[] { centre }, half);
        var set = GridLabelFormatter.Format(axis, GridLabelSettings.Default);
        Assert.Equal(GridLabelMode.Auto, set.Used);
        Assert.Null(set.Anchor);
        Assert.Equal(set.Labels.Count, set.Labels.Distinct().Count());
        for (int i = 0; i < axis.Ticks.Count; i++)
        {
            Assert.Equal(TickScaled(axis.Ticks[i], axis.Exponent), ParseScaled(set.Labels[i]));
            Assert.DoesNotContain("e", set.Labels[i]);
        }
        // Minimal: some label needs every decimal the step implies.
        int need = Math.Max(0, -axis.Exponent);
        int most = set.Labels.Max(l => l.Contains('.') ? l.Length - l.IndexOf('.') - 1 : 0);
        Assert.Equal(need, most);
    }

    [Fact]
    public void Auto_AtStepHalf_DoesNotPadZeros()
    {
        var axis = GridAxis.Build(new[] { 1.0 }, 1.2, 5, -1);
        var labels = GridLabelFormatter.Format(axis, GridLabelSettings.Default).Labels;
        Assert.Contains("1.5", labels);
        Assert.Contains("1", labels);       // not "1.000000" (the pre-#1559 form)
    }

    [Fact]
    public void Auto_LargeValues_FallBackToScientific()
    {
        var axis = Axis(new[] { 3e20 }, 1e20);
        var set = GridLabelFormatter.Format(axis, GridLabelSettings.Default);
        Assert.Equal(GridLabelMode.Scientific, set.Used);
        for (int i = 0; i < axis.Ticks.Count; i++)
            Assert.Equal(TickScaled(axis.Ticks[i], axis.Exponent), ParseScaled(set.Labels[i]));
        Assert.All(set.Labels, l => Assert.True(l.Length <= GridLabelSettings.AutoMaxLength));
    }

    [Theory]
    [InlineData(1e-12)]
    [InlineData(1e-40)]
    [InlineData(1e-60)]
    public void Auto_DeepZoom_FallsBackToOffset_AnchorPlusOffsetIsExact(double half)
    {
        var axis = Axis(Deep, half);
        var set = GridLabelFormatter.Format(axis, GridLabelSettings.Default);
        Assert.Equal(GridLabelMode.Offset, set.Used);
        Assert.NotNull(set.Anchor);
        BigInteger anchor = ParseScaled(set.Anchor!);
        for (int i = 0; i < axis.Ticks.Count; i++)
        {
            BigInteger off = set.Labels[i] == "0" ? BigInteger.Zero : ParseScaled(set.Labels[i]);
            Assert.Equal(TickScaled(axis.Ticks[i], axis.Exponent), anchor + off);
        }
        Assert.Equal(set.Labels.Count, set.Labels.Distinct().Count());
        // The anchor is the tick nearest the view centre.
        Assert.Contains("0", set.Labels);
    }

    [Fact]
    public void Fixed_RoundsHalfAwayFromZero_ToExactlyNDecimals()
    {
        Assert.Equal("1.250", GridLabelFormatter.FixedDecimals(125, -2, 3));
        Assert.Equal("1.3", GridLabelFormatter.FixedDecimals(125, -2, 1));
        Assert.Equal("-1.3", GridLabelFormatter.FixedDecimals(-125, -2, 1));
        Assert.Equal("0.00", GridLabelFormatter.FixedDecimals(1, -5, 2));
        Assert.Equal("500", GridLabelFormatter.FixedDecimals(5, 2, 0));
        Assert.Equal("0.0010", GridLabelFormatter.FixedDecimals(1, -3, 4));
    }

    [Fact]
    public void Scientific_HasExactlyNSignificantFigures()
    {
        Assert.Equal("1.25e-8", GridLabelFormatter.Scientific(125, -10, 3));
        Assert.Equal("1.3e-8", GridLabelFormatter.Scientific(125, -10, 2));
        Assert.Equal("1.0e1", GridLabelFormatter.Scientific(996, -2, 2));   // 9.96 -> 10
        Assert.Equal("-5.000e3", GridLabelFormatter.Scientific(-5, 3, 4));
    }

    [Fact]
    public void Plain_IsExact()
    {
        Assert.Equal("-0.0005", GridLabelFormatter.Plain(-5, -4));
        Assert.Equal("1200", GridLabelFormatter.Plain(12, 2));
        Assert.Equal("0.1", GridLabelFormatter.Plain(10, -2));
        Assert.Equal("0", GridLabelFormatter.Plain(0, -7));
    }

    [Fact]
    public void Build_RejectsNonFinite_WithNoTicks()
    {
        Assert.Empty(GridAxis.Build(new[] { double.NaN }, 1, 1, 0).Ticks);
        Assert.Empty(GridAxis.Build(new[] { 0.0 }, double.PositiveInfinity, 1, 0).Ticks);
        Assert.Empty(GridAxis.Build(new[] { 0.0 }, 0, 1, 0).Ticks);
    }
}

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class GridLabelPrefsStoreTests
{
    [Fact]
    public void SaveLoad_RoundTrips_AndClampsDigits()
    {
        var before = GridLabelPrefsStore.Load();
        try
        {
            GridLabelPrefsStore.Save(new GridLabelSettings(GridLabelMode.Scientific, 99));
            Assert.Equal(new GridLabelSettings(GridLabelMode.Scientific, GridLabelSettings.MaxDigits),
                         GridLabelPrefsStore.Load());
        }
        finally { GridLabelPrefsStore.Save(before); }
    }
}
