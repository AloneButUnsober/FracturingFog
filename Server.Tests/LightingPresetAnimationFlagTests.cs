// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

// #998 (CB6 of #64) — --lighting-preset and --animation, and the two fidelity
// gaps they close: Lighting & FX settings no flag carries, and a live animation.
public sealed class LightingPresetAnimationFlagTests
{
    // ── Animation pose at time t ─────────────────────────────────────────────

    private static AnimationData Anim() => new()
    {
        Name = "t",
        Tracks =
        {
            new AnimationTrack { ParamName = nameof(FractalParameters.BulbPower), Mode = AnimationMode.Sine, Min = 4, Max = 12, FrequencyHz = 0.37, PhaseOffsetRadians = 0.2 },
            new AnimationTrack { ParamName = nameof(FractalParameters.MultibrotExponent), Mode = AnimationMode.Triangle, Min = 2, Max = 9, FrequencyHz = 0.21 },
            new AnimationTrack { ParamName = nameof(FractalParameters.JuliaC), Mode = AnimationMode.Lissajous, Min = 0.7, Max = 0.8, FrequencyHz = 0.13, CenterX = -0.1 },
            new AnimationTrack { ParamName = "NoSuchParam", Mode = AnimationMode.Sine, Min = 0, Max = 1 },
        },
    };

    [Fact]
    public void ApplyAt_EqualsTheLiveBusTickingFrameByFrame()
    {
        // Oracle: the interactive bus — one animator set ticked every frame.
        const int fps = 30, frames = 97;
        var live = new FractalParameters();
        var animators = Anim().ToAnimators(live).ToList();
        for (int f = 0; f < frames; f++) foreach (var a in animators) a.Tick(1.0 / fps);

        var batch = new FractalParameters();
        int bound = Anim().ApplyAt(batch, frames / (double)fps);

        Assert.Equal(3, bound);   // the unknown param is skipped, not an error
        Assert.Equal(live.BulbPower, batch.BulbPower, 9);
        Assert.Equal(live.MultibrotExponent, batch.MultibrotExponent);
        Assert.Equal(live.JuliaC.Real, batch.JuliaC.Real, 9);
        Assert.Equal(live.JuliaC.Imaginary, batch.JuliaC.Imaginary, 9);
    }

    [Fact]
    public void ApplyAt_TimeZeroLeavesTheBaseline_AndFramesAreIndependent()
    {
        var p = new FractalParameters { BulbPower = 8 };
        Anim().ApplyAt(p, 0);
        Assert.Equal(8, p.BulbPower);

        var a = new FractalParameters();
        Anim().ApplyAt(a, 5.0);
        Anim().ApplyAt(a, 2.0);   // jumping back lands on t = 2, not 7
        var b = new FractalParameters();
        Anim().ApplyAt(b, 2.0);
        Assert.Equal(b.BulbPower, a.BulbPower, 12);
        Assert.Equal(b.JuliaC, a.JuliaC);
    }

    // ── Lighting fidelity ────────────────────────────────────────────────────

    [Fact]
    public void DefaultLighting_HasNothingUnexpressed()
        => Assert.Empty(LightingFidelity.UnexpressedFields(LightingFxData.CreateDefault()));

    [Fact]
    public void FlagCarriedSettings_AreNotReported()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light2.Intensity = 0.7;
        fx.Light3.Type = LightType.Spot;
        fx.FogDensity = 0.4;
        fx.Transmission = 0.9;
        fx.StereoMode = StereoMode.Fake;   // its own gap
        Assert.Empty(LightingFidelity.UnexpressedFields(fx));
    }

    [Fact]
    public void OtherSettings_AreReportedByName()
    {
        var fx = LightingFxData.CreateDefault();
        fx.AoSamples = 8;
        fx.BloomStrength = 0.5;
        var missing = LightingFidelity.UnexpressedFields(fx);
        Assert.Contains("AoSamples", missing);
        Assert.Contains("BloomStrength", missing);
        Assert.Equal(2, missing.Count);
    }

    [Fact]
    public void MatchPreset_FindsAnExactPreset_Only()
    {
        var fx = LightingFxData.CreateDefault();
        fx.AoSamples = 8;
        fx.ShadowSteps = 32;
        var presets = new[]
        {
            new LightingFxPreset { Name = "Other", Data = LightingFxPresetData.FromFx(LightingFxData.CreateDefault()) },
            new LightingFxPreset { Name = "Moody", Data = LightingFxPresetData.FromFx(fx) },
        };
        Assert.Equal("Moody", LightingFidelity.MatchPreset(fx, presets));
        fx.DebugHudFlags = 0x8;   // a debug switch is not part of the look
        Assert.Equal("Moody", LightingFidelity.MatchPreset(fx, presets));
        fx.AoStrength += 0.1;
        Assert.Null(LightingFidelity.MatchPreset(fx, presets));
    }

    // ── Builder ──────────────────────────────────────────────────────────────

    private static FractalParameters WithAo()
    {
        var p = new FractalParameters();
        var fx = p.Lighting; fx.AoSamples = 8; p.Lighting = fx;
        return p;
    }

    [Fact]
    public void UnexpressedLighting_IsAGap_UntilAPresetCarriesIt()
    {
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Parameters = WithAo() });
        Assert.Contains(gaps, g => g.Contains("AoSamples") && g.Contains("--lighting-preset"));

        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot { Parameters = WithAo(), LightingPresetName = "Moody Fog" });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Lighting & FX"));
        Assert.Contains("--lighting-preset \"Moody Fog\"", report.Command);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        Assert.Equal("Moody Fog", o.LightingPresetName);
    }

    [Fact]
    public void LiveAnimation_IsAGap()
    {
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { LiveAnimationName = "Breathe" });
        Assert.Contains(gaps, g => g.Contains("'Breathe'") && g.Contains("--animation"));
    }

    [Fact]
    public void Animation_IsVideoOnly_InThePanel()
    {
        var c = new CommandComposer();
        c.Batch(() => { c.Set(BatchFlags.Region, "R"); c.Set(BatchFlags.Animation, "Breathe"); c.Set(BatchFlags.Out, "o"); });
        Assert.DoesNotContain(BatchFlags.Animation, c.Args());
        c.Mode = BatchMode.Video;
        Assert.Contains(BatchFlags.Animation, c.Args());
        Assert.True(BatchOptions.TryParse(c.Args().ToArray(), 0, out var o, out var err), err);
        Assert.Equal("Breathe", o.AnimationName);
    }
}
