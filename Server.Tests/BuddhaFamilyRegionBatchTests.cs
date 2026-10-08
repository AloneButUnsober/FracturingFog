// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1134 — the classic Buddhabrot family (Buddhabrot / Nebulabrot / AntiBuddhabrot /
// AntiNebulabrot) carries its Monte Carlo sampler settings in the region snapshot,
// so a saved region reproduces its sampling and --batch / the Command builder can
// express it (--param Key=Value).
public sealed class BuddhaFamilyRegionBatchTests
{
    public static TheoryData<FractalType> Classic() => new()
    {
        FractalType.BuddhaBrot, FractalType.Nebulabrot, FractalType.AntiBuddhabrot, FractalType.AntiNebulabrot,
    };

    private static FractalParameters NonDefault() => new()
    {
        BuddhaSamples = 123_457, BuddhaIterLow = 17, BuddhaIterMid = 171, BuddhaIterHigh = 1717,
        BuddhaQualityMode = BuddhaQualityMode.HighDefinition, BuddhaMetropolis = true,
        BuddhaProgressive = !new FractalParameters().BuddhaProgressive,
        BuddhaSeed = 4242, BuddhaZoomCompensation = !new FractalParameters().BuddhaZoomCompensation,
        BuddhaScaleSamplesWithWindow = !new FractalParameters().BuddhaScaleSamplesWithWindow,   // #1224
    };

    private static void AssertSampler(FractalParameters want, FractalParameters got)
    {
        Assert.Equal(want.BuddhaSamples, got.BuddhaSamples);
        Assert.Equal(want.BuddhaIterLow, got.BuddhaIterLow);
        Assert.Equal(want.BuddhaIterMid, got.BuddhaIterMid);
        Assert.Equal(want.BuddhaIterHigh, got.BuddhaIterHigh);
        Assert.Equal(want.BuddhaQualityMode, got.BuddhaQualityMode);
        Assert.Equal(want.BuddhaMetropolis, got.BuddhaMetropolis);
        Assert.Equal(want.BuddhaProgressive, got.BuddhaProgressive);
        Assert.Equal(want.BuddhaSeed, got.BuddhaSeed);
        Assert.Equal(want.BuddhaZoomCompensation, got.BuddhaZoomCompensation);
        Assert.Equal(want.BuddhaScaleSamplesWithWindow, got.BuddhaScaleSamplesWithWindow);
    }

    [Theory]
    [MemberData(nameof(Classic))]
    public void Region_RoundTripsTheSamplerSettings_ThroughJson(FractalType type)
    {
        var live = NonDefault();
        var snap = RegionFractalParams.Snapshot(type, live);
        Assert.NotNull(snap);
        var stored = JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!;
        var recalled = new FractalParameters();
        stored.ApplyTo(recalled);
        AssertSampler(live, recalled);
    }

    [Theory]
    [MemberData(nameof(Classic))]
    public void Defaults_AreOmitted(FractalType type)
    {
        var snap = RegionFractalParams.Snapshot(type, new FractalParameters());   // all-default → null
        Assert.True(snap == null || snap.ToKeyValues().Count == 0);
    }

    // #960 — recalling a region saved at defaults puts a moved sampler back.
    [Theory]
    [MemberData(nameof(Classic))]
    public void Recall_ResetsAMovedSampler_ToTheRegionsValues(FractalType type)
    {
        var p = NonDefault();
        var region = new FractalRegion { FractalType = type, Params = RegionFractalParams.Snapshot(type, new FractalParameters()) };
        region.ApplyFamilyParams(p);
        AssertSampler(new FractalParameters(), p);
    }

    [Theory]
    [MemberData(nameof(Classic))]
    public void LiveView_RoundTripsThroughTheCommandBuilder(FractalType type)
    {
        var p = NonDefault();
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = type, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(type, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Contains(report.Args, a => a.StartsWith("BuddhaSeed=", StringComparison.Ordinal));
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, type, out var perr)!.ApplyTo(fresh);
        Assert.Null(perr);
        AssertSampler(p, fresh);
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Buddha", StringComparison.OrdinalIgnoreCase));
    }

    // The sampler keys stay out of unrelated families (FamilyProperties is
    // discovered from the snapshot).
    [Fact]
    public void SamplerKeys_AreBuddhaFamilyOnly()
    {
        var mandel = RegionFractalParams.FamilyProperties(FractalType.Mandelbrot).Select(pi => pi.Name).ToHashSet();
        Assert.DoesNotContain(nameof(FractalParameters.BuddhaSeed), mandel);
        foreach (var t in new[] { FractalType.BuddhaBrot, FractalType.AntiNebulabrot, FractalType.DualBuddhabrot })
            Assert.Contains(nameof(FractalParameters.BuddhaSeed),
                RegionFractalParams.FamilyProperties(t).Select(pi => pi.Name));
    }
}
