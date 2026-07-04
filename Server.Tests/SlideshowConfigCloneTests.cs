using System.Collections.Generic;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// SlideshowConfig.Clone is a JSON round-trip so newly-added fields can't
/// silently drop (the member-wise clone had dropped RandomSeed). These lock
/// in full-field fidelity, deep independence, and null-collection
/// normalisation.
/// </summary>
public sealed class SlideshowConfigCloneTests
{
    private static SlideshowConfig Populated() => new()
    {
        Name = "MyPreset",
        Type = SlideshowType.Animation,
        Timing = new SlideshowSettings
        {
            UseExtremeRegions = true,
            TotalDisplayMsPerRegion = 42_000,
            ColorThemeFadeMs = 1_500,
            RegionFadeMs = 2_500,
            FadeSteps = 30,
            RandomSeed = 777,
            UseRegionWatermark = true,
            RecordSlideshow = true,
            RecordEncodePreset = "Ffv1Mkv",
        },
        AudioReactive = true,
        IncludedRegions = new() { "R1", "R2" },
        IncludedColorThemes = new() { "T1" },
        FilterFractalTypes = new() { "Mandelbrot", "Julia" },
        FilterQualityPresets = new() { "Ultra" },
        IncludedAnimations = new() { "A1" },
        FilterAnimations = new() { "tagX" },
        RandomizeAnimationsByFractalType = true,
        EnableAnimations = true,
        AdaptiveSweep = new AdaptiveSweepConfig { Enabled = true, Start = 10, End = 90, Loop = true, BeatFraction = 0.25 },
        PostFx = new PostFxConfig { Enabled = true, Values = new Dictionary<string, double> { ["brightness"] = 1.2 } },
        Video = new VideoSettingsConfig { SpeedPreset = "Fast", ThemesPerLeg = 4, SaveVideo = true },
    };

    [Fact]
    public void Clone_CarriesEveryField()
    {
        var c = Populated().Clone();

        Assert.Equal("MyPreset", c.Name);
        Assert.Equal(SlideshowType.Animation, c.Type);

        Assert.True(c.Timing.UseExtremeRegions);
        Assert.Equal(42_000, c.Timing.TotalDisplayMsPerRegion);
        Assert.Equal(1_500, c.Timing.ColorThemeFadeMs);
        Assert.Equal(2_500, c.Timing.RegionFadeMs);
        Assert.Equal(30, c.Timing.FadeSteps);
        Assert.Equal(777, c.Timing.RandomSeed);
        Assert.True(c.Timing.UseRegionWatermark);
        Assert.True(c.Timing.RecordSlideshow);
        Assert.Equal("Ffv1Mkv", c.Timing.RecordEncodePreset);

        Assert.True(c.AudioReactive);
        Assert.Equal(new[] { "R1", "R2" }, c.IncludedRegions);
        Assert.Equal(new[] { "T1" }, c.IncludedColorThemes);
        Assert.Equal(new[] { "Mandelbrot", "Julia" }, c.FilterFractalTypes);
        Assert.Equal(new[] { "Ultra" }, c.FilterQualityPresets);
        Assert.Equal(new[] { "A1" }, c.IncludedAnimations);
        Assert.Equal(new[] { "tagX" }, c.FilterAnimations);
        Assert.True(c.RandomizeAnimationsByFractalType);
        Assert.True(c.EnableAnimations);

        Assert.True(c.AdaptiveSweep.Enabled);
        Assert.Equal(90, c.AdaptiveSweep.End);
        Assert.Equal(0.25, c.AdaptiveSweep.BeatFraction);

        Assert.True(c.PostFx.Enabled);
        Assert.Equal(1.2, c.PostFx.Values["brightness"]);

        Assert.NotNull(c.Video);
        Assert.Equal("Fast", c.Video!.SpeedPreset);
        Assert.Equal(4, c.Video.ThemesPerLeg);
        Assert.True(c.Video.SaveVideo);
    }

    [Fact]
    public void Clone_IsDeepIndependent()
    {
        var orig = Populated();
        var c = orig.Clone();

        c.IncludedRegions.Add("R3");
        c.Timing.RandomSeed = 1;
        c.PostFx.Values["brightness"] = 9.9;
        c.Video!.SpeedPreset = "Slow";

        Assert.Equal(new[] { "R1", "R2" }, orig.IncludedRegions);
        Assert.Equal(777, orig.Timing.RandomSeed);
        Assert.Equal(1.2, orig.PostFx.Values["brightness"]);
        Assert.Equal("Fast", orig.Video!.SpeedPreset);
    }

    [Fact]
    public void Clone_NullCollections_NormalizeToEmpty()
    {
        var cfg = new SlideshowConfig
        {
            IncludedRegions = null!,
            FilterAnimations = null!,
        };

        var c = cfg.Clone();

        Assert.NotNull(c.IncludedRegions);
        Assert.Empty(c.IncludedRegions);
        Assert.NotNull(c.FilterAnimations);
        Assert.Empty(c.FilterAnimations);
    }
}
