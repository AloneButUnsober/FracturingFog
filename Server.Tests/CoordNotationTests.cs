// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #791 — CX/CY coordinate notation. These pin the "single-value by default"
/// contract that has been stomped on before: DefaultUsePipe must stay false,
/// single-value output must not contain pipes, and the pipe form must still
/// round-trip losslessly so nobody has an excuse to flip the default back.
/// </summary>
public sealed class CoordNotationTests
{
    // Deep coord with real low limbs (DD/QD).
    private const double Hi = -0.7436438870371587;
    private const double Lo = 1.23e-17;
    private const double L2 = -4.56e-34;
    private const double L3 = 7.89e-51;

    [Fact]
    public void DefaultUsePipe_IsSingleValue()
    {
        // Tripwire: flipping this to true re-introduces the regressed behaviour.
        Assert.False(CoordNotation.DefaultUsePipe);
    }

    [Fact]
    public void FormatDefault_IsSingleValue_NoPipe()
    {
        string def = CoordNotation.Format(CoordNotation.DefaultUsePipe, Hi, Lo, L2, L3);
        string single = CoordNotation.FormatSingle(Hi, Lo, L2, L3);
        Assert.Equal(single, def);
        Assert.DoesNotContain("|", def);
    }

    [Fact]
    public void FormatSingle_DeepCoord_HasNoPipe_AndRecoversHi()
    {
        string s = CoordNotation.FormatSingle(Hi, Lo, L2, L3);
        Assert.DoesNotContain("|", s);

        Assert.True(CoordNotation.TryParse(s, out double hi, out _, out _, out _));
        // Single-value carries ~29 digits — the Hi limb comes back to full
        // double precision, which is what the box shows for everyday use.
        Assert.Equal(Hi, hi, 15);
    }

    [Fact]
    public void FormatPipe_DeepCoord_HasPipe_AndRoundTripsExactly()
    {
        string s = CoordNotation.FormatPipe(Hi, Lo, L2, L3);
        Assert.Contains("|", s);

        Assert.True(CoordNotation.TryParse(s, out double hi, out double lo, out double l2, out double l3));
        // G17 preserves every double bit, so the pipe form is lossless.
        Assert.Equal(Hi, hi);
        Assert.Equal(Lo, lo);
        Assert.Equal(L2, l2);
        Assert.Equal(L3, l3);
    }

    [Fact]
    public void TryParse_AcceptsPipeInput_OnPaste()
    {
        // Pasting an FF-native pipe value must still work regardless of the
        // display default.
        string pasted = "-0.75|1.2e-17|3.4e-34";
        Assert.True(CoordNotation.TryParse(pasted, out double hi, out double lo, out double l2, out double l3));
        Assert.Equal(-0.75, hi);
        Assert.Equal(1.2e-17, lo);
        Assert.Equal(3.4e-34, l2);
        Assert.Equal(0.0, l3);
    }

    [Fact]
    public void TryParse_AcceptsSingleDecimal_PeelingIntoLimbs()
    {
        string single = "-0.7436438870371587";
        Assert.True(CoordNotation.TryParse(single, out double hi, out _, out _, out _));
        Assert.Equal(-0.7436438870371587, hi, 15);
    }

    [Fact]
    public void ShallowCoord_SingleAndPipe_AgreeAndHaveNoPipe()
    {
        // No low limbs → both notations collapse to the same plain decimal.
        double hi = -0.5;
        string single = CoordNotation.FormatSingle(hi);
        string pipe = CoordNotation.FormatPipe(hi);
        Assert.DoesNotContain("|", single);
        Assert.DoesNotContain("|", pipe);
        Assert.Equal(single, pipe);
    }
}
