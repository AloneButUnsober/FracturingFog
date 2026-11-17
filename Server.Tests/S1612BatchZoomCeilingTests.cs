// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Batch;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>#1612 — past the deepest tier's zoom ceiling a batch render collapsed
/// to one flat colour and still exited 0, so the parser refused those zooms. #1860
/// (epic #1428, C10) — the deepest tier is Unbounded (#1861), with no ceiling: views
/// past 1e100 parse and render in it; only a zoom that is no zoom is refused.</summary>
public class S1612BatchZoomCeilingTests
{
    private static bool Parse(out string? error, params string[] args)
        => BatchOptions.TryParse(args, 0, out _, out error);

    [Theory]
    [InlineData("1.6e148")]
    [InlineData("5.5e275")]
    [InlineData("2.3e296")]
    [InlineData("1e500")]
    public void ZoomPastTheExtremeCeiling_Parses(string zoom)
    {
        Assert.Equal(double.PositiveInfinity, QualityPreset.Deepest.ZoomMax);
        Assert.True(Parse(out var error, "--x", "0", "--y", "0", "--zoom", zoom, "--out", "o.png"), error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1e120")]
    public void NonPositiveZoom_IsRefused(string zoom)
    {
        Assert.False(Parse(out var error, "--x", "0", "--y", "0", "--zoom", zoom, "--out", "o.png"));
        Assert.Contains("--zoom", error);
    }

    [Fact]
    public void ZoomAtCeiling_Parses()
    {
        Assert.Equal(1e100, QualityPreset.Extreme.ZoomMax);
        Assert.True(Parse(out var error, "--x", "0", "--y", "0", "--zoom", "1e100", "--out", "o.png"), error);
    }

    [Fact]
    public void VideoStartZoomPastTheExtremeCeiling_Parses()
    {
        Assert.True(Parse(out var error, "--mode", "video", "--x", "0", "--y", "0", "--zoom", "1e50",
            "--start-zoom", "1e120", "--out", "o"), error);
    }
}
