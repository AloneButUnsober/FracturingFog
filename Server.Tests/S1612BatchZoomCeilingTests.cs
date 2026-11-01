// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Batch;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>#1612 — past the deepest tier's zoom ceiling a batch render collapsed
/// to one flat colour and still exited 0. The parser now refuses those zooms.</summary>
public class S1612BatchZoomCeilingTests
{
    private static bool Parse(out string? error, params string[] args)
        => BatchOptions.TryParse(args, 0, out _, out error);

    [Theory]
    [InlineData("1.6e148")]
    [InlineData("5.5e275")]
    [InlineData("2.3e296")]
    public void ZoomPastCeiling_IsRefused_NamingTheCeiling(string zoom)
    {
        Assert.False(Parse(out var error, "--x", "0", "--y", "0", "--zoom", zoom, "--out", "o.png"));
        Assert.Contains("--zoom", error);
        Assert.Contains("1E+100", error);
        Assert.Contains("#1428", error);
    }

    [Fact]
    public void ZoomAtCeiling_Parses()
    {
        Assert.Equal(1e100, QualityPreset.Extreme.ZoomMax);
        Assert.True(Parse(out var error, "--x", "0", "--y", "0", "--zoom", "1e100", "--out", "o.png"), error);
    }

    [Fact]
    public void VideoStartZoomPastCeiling_IsRefused()
    {
        Assert.False(Parse(out var error, "--mode", "video", "--x", "0", "--y", "0", "--zoom", "1e50",
            "--start-zoom", "1e120", "--out", "o"));
        Assert.Contains("--start-zoom", error);
    }
}
