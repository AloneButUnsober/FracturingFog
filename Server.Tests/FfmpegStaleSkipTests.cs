// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #951 — "Continue Without Video Save" (FfmpegUserElection.Skip) must not
// silently outlive the reason it was chosen: a Skip picked because ffmpeg was
// missing (or of unknown provenance, legacy prefs) clears once ffmpeg is
// installed, while a deliberate Skip made WITH ffmpeg installed is kept until
// the user re-enables video. Pure instance logic — no prefs file is touched.

using System.Text.Json;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FfmpegStaleSkipTests
{
    [Fact]
    public void Skip_chosen_without_ffmpeg_clears_once_installed()
    {
        var p = new FfmpegPreferences();
        p.ChooseSkip(ffmpegPresent: false);
        Assert.True(p.IsVideoDisabledByUser());

        Assert.False(p.ReconcileStaleSkip(ffmpegPresent: false));   // still missing → keep
        Assert.True(p.IsVideoDisabledByUser());

        Assert.True(p.ReconcileStaleSkip(ffmpegPresent: true));
        Assert.False(p.IsVideoDisabledByUser());
        Assert.Equal(FfmpegUserElection.Manual, p.Election);
        Assert.Null(p.SkippedWithFfmpegPresent);
    }

    [Fact]
    public void Legacy_skip_of_unknown_provenance_clears_when_installed()
    {
        // The #951 report: prefs written before provenance was tracked.
        var p = JsonSerializer.Deserialize<FfmpegPreferences>("{\"Election\":3}")!;
        Assert.True(p.IsVideoDisabledByUser());
        Assert.Null(p.SkippedWithFfmpegPresent);
        Assert.True(p.ReconcileStaleSkip(ffmpegPresent: true));
        Assert.False(p.IsVideoDisabledByUser());
    }

    [Fact]
    public void Deliberate_skip_with_ffmpeg_installed_is_kept_until_reenabled()
    {
        var p = new FfmpegPreferences();
        p.ChooseSkip(ffmpegPresent: true);
        Assert.False(p.ReconcileStaleSkip(ffmpegPresent: true));
        Assert.True(p.IsVideoDisabledByUser());

        Assert.True(p.ReEnableVideo());
        Assert.False(p.IsVideoDisabledByUser());
        Assert.False(p.ReEnableVideo());   // idempotent
    }

    [Fact]
    public void Non_skip_elections_are_untouched()
    {
        foreach (var e in new[] { FfmpegUserElection.None, FfmpegUserElection.Manual, FfmpegUserElection.AutoDownload })
        {
            var p = new FfmpegPreferences { Election = e };
            Assert.False(p.ReconcileStaleSkip(ffmpegPresent: true));
            Assert.Equal(e, p.Election);
        }
    }

    [Fact]
    public void Provenance_survives_a_save_round_trip()
    {
        var p = new FfmpegPreferences();
        p.ChooseSkip(ffmpegPresent: true);
        var back = JsonSerializer.Deserialize<FfmpegPreferences>(JsonSerializer.Serialize(p))!;
        Assert.Equal(true, back.SkippedWithFfmpegPresent);
        Assert.False(back.ReconcileStaleSkip(ffmpegPresent: true));
    }
}
