// #2016 — --aa-samples N: supersamples per pixel in place of the quality preset's, at
// any depth (past 1e100 batch renders in Unbounded, 2×2). 1 = one sample per pixel and
// no temporal AA, so benchmarks can compare FF with renderers that take one sample.

using System;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2016AaSamplesTests
{
    private static string[] Args(params string[] extra) =>
        new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" }.Concat(extra).ToArray();

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("9", 9)]
    [InlineData("16", 16)]
    public void Parses_The_Square_Counts(string value, int expected)
    {
        Assert.True(BatchOptions.TryParse(Args(BatchFlags.AaSamples, value), 2, out var o, out var err), err);
        Assert.Equal(expected, o.AaSamples);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("8")]
    [InlineData("25")]
    [InlineData("x")]
    public void Rejects_Other_Values(string value)
    {
        Assert.False(BatchOptions.TryParse(Args(BatchFlags.AaSamples, value), 2, out _, out var err));
        Assert.Contains(BatchFlags.AaSamples, err ?? "");
    }

    [Fact]
    public void Unset_Keeps_The_Preset()
    {
        Assert.True(BatchOptions.TryParse(Args(), 2, out var o, out var err), err);
        Assert.Null(o.AaSamples);
    }

    [Fact]
    public void Is_Catalogued_For_Images_With_The_Parser_Choices()
    {
        var spec = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.AaSamples);
        Assert.Equal(BatchFlagKind.Choice, spec.Kind);
        Assert.Equal(BatchModes.Image, spec.Modes);
        Assert.Equal(BatchOptions.AaSampleChoices.Select(n => n.ToString()), spec.Choices);
    }

    [Fact]
    public void WithAaSamples_Copies_The_Preset_With_Only_The_Count_Changed()
    {
        var u = QualityPreset.Unbounded;
        Assert.Equal(4, u.AaSamples);
        var one = u.WithAaSamples(1);
        Assert.Equal(1, one.AaSamples);
        Assert.Equal(4, u.AaSamples);                      // the shared preset is untouched
        Assert.Equal(u.Tier, one.Tier);
        Assert.Equal(u.Name, one.Name);
        Assert.Equal(u.TaaMaxSamples, one.TaaMaxSamples);
        Assert.Equal(u.IterPerDecade, one.IterPerDecade);
        Assert.Equal(u.HPZoomThreshold, one.HPZoomThreshold);
        Assert.Equal(u.ComputeIterations(1e120), one.ComputeIterations(1e120));
        Assert.Same(u, u.WithAaSamples(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => u.WithAaSamples(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => u.WithAaSamples(0));
    }
}
