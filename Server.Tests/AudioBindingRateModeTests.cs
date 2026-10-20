// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Cli;
using FracturingFog.Audio;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1431 — audio bindings: Rate (integrate) mode, attack / release smoothing,
// wrap / clamp into the target's range, re-seed on an outside edit, the
// persistence of the new fields, the row VM's mode switch, and the #1432
// live-only gap report.
public sealed class AudioBindingRateModeTests
{
    private const double Eps = 1e-9;

    private sealed class FakeModSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount => 0;
        public long DownbeatCount => 0;
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    private sealed class Target
    {
        public double Scalar { get; set; }
        public int Count { get; set; }
    }

    private static AudioModulationFrame Rms(float rms) =>
        new(0, 0, 0, 0, 0, rms, 0, 0, 0, 0, false, 0, true);

    private static (Target T, FakeModSource Src, AudioModulatorAnimator Anim) Make(
        AudioModulationBinding binding, float rms = 1f, double start = 0.0,
        double min = double.NegativeInfinity, double max = double.PositiveInfinity,
        string param = nameof(Target.Scalar))
    {
        var t = new Target { Scalar = start };
        var src = new FakeModSource { Frame = Rms(rms) };
        var anim = AudioModulatorAnimator.TryCreate(t, param, src, binding,
                                                    AnimatableParamCost.Cheap, min, max)!;
        return (t, src, anim);
    }

    // ── Rate mode ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rate_Integrates_Speed_From_Current_Value()
    {
        // drive 0.5 -> speed lerp(0, 2) = 1 unit/s; 10 x 0.1 s -> +1.0.
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 2 };
        var (t, _, anim) = Make(b, rms: 0.5f, start: 3.0);
        for (int i = 0; i < 10; i++) anim.Tick(0.1);
        Assert.Equal(4.0, t.Scalar, 1e-12);
    }

    [Fact]
    public void Rate_Never_Moves_Back_When_The_Signal_Drops()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 1 };
        var (t, src, anim) = Make(b, rms: 1f);
        anim.Tick(0.5);
        double peak = t.Scalar;
        Assert.Equal(0.5, peak, Eps);
        src.Frame = Rms(0f);              // silence -> speed 0: holds, never falls back
        anim.Tick(0.5);
        Assert.Equal(peak, t.Scalar, Eps);
    }

    [Fact]
    public void Rate_Wraps_Around_The_Range()
    {
        // AcidWarpFlow-like: 0..20, wrap. 19.9 + 1/s * 0.2 s = 20.1 -> 0.1.
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 1, Edge = AudioRateEdge.Wrap };
        var (t, _, anim) = Make(b, rms: 1f, start: 19.9, min: 0, max: 20);
        anim.Tick(0.2);
        Assert.Equal(0.1, t.Scalar, 1e-9);
    }

    [Fact]
    public void Rate_Wraps_Negative_Speed_Below_Min()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = -1, OutMax = -1, Edge = AudioRateEdge.Wrap };
        var (t, _, anim) = Make(b, start: 0.1, min: 0, max: 20);
        anim.Tick(0.2);
        Assert.Equal(19.9, t.Scalar, 1e-9);
    }

    // ── #1436 ping-pong ────────────────────────────────────────────────────────

    private static AudioModulationBinding PingPong(double speed) => new()
    {
        Mode = AudioBindingMode.Rate, OutMin = speed, OutMax = speed, Edge = AudioRateEdge.PingPong,
    };

    [Fact]
    public void PingPong_Turns_At_The_Top_And_Keeps_Going_Down()
    {
        // 0..10 at 1/s from 9.5: +1 -> 10.5 reflects to 9.5, then down.
        var (t, _, anim) = Make(PingPong(1), start: 9.5, min: 0, max: 10);
        anim.Tick(1.0);
        Assert.Equal(9.5, t.Scalar, 1e-9);
        anim.Tick(1.0);
        Assert.Equal(8.5, t.Scalar, 1e-9);   // still heading down — no stall, no jump
    }

    [Fact]
    public void PingPong_Turns_At_The_Bottom_And_Heads_Back_Up()
    {
        var (t, _, anim) = Make(PingPong(1), start: 9.5, min: 0, max: 10);
        for (int i = 0; i < 10; i++) anim.Tick(1.0);     // up 0.5, down 9.5 -> 0.5... down to 0.5
        Assert.Equal(0.5, t.Scalar, 1e-9);
        anim.Tick(1.0);                                   // 0.5 -> -0.5 reflects to 0.5, now up
        Assert.Equal(0.5, t.Scalar, 1e-9);
        anim.Tick(1.0);
        Assert.Equal(1.5, t.Scalar, 1e-9);
    }

    [Fact]
    public void PingPong_Folds_Several_Bounces_In_One_Step()
    {
        // 2 + 25 = 27 over a 0..10 range: period 20 -> phase 7, going up -> 7.
        var (t, _, anim) = Make(PingPong(25), start: 2, min: 0, max: 10);
        anim.Tick(1.0);
        Assert.Equal(7.0, t.Scalar, 1e-9);
        // independent check: every write stays inside the range
        for (int i = 0; i < 50; i++) { anim.Tick(0.37); Assert.InRange(t.Scalar, 0.0, 10.0); }
    }

    [Fact]
    public void PingPong_Negative_Speed_Bounces_Off_The_Bottom()
    {
        var (t, _, anim) = Make(PingPong(-1), start: 0.5, min: 0, max: 10);
        anim.Tick(1.0);                                   // 0.5 - 1 -> reflects to 0.5
        Assert.Equal(0.5, t.Scalar, 1e-9);
        anim.Tick(1.0);                                   // continuing the fold
        Assert.InRange(t.Scalar, 0.0, 10.0);
    }

    [Fact]
    public void PingPong_Reseeds_Heading_Up_After_An_Outside_Edit()
    {
        var (t, _, anim) = Make(PingPong(1), start: 9.5, min: 0, max: 10);
        anim.Tick(1.0);                                   // bounced, heading down
        t.Scalar = 3.0;                                   // user drags the slider
        anim.Tick(1.0);
        Assert.Equal(4.0, t.Scalar, 1e-9);
    }

    [Fact]
    public void Legacy_Wrap_Flag_Loads_As_Wrap_Edge_And_Is_Never_Written()
    {
        var b = JsonSerializer.Deserialize<AudioModulationBinding>("{\"Mode\":1,\"Wrap\":true}")!;
        Assert.Equal(AudioRateEdge.Wrap, b.Edge);
        string json = JsonSerializer.Serialize(b);
        Assert.DoesNotContain("\"Wrap\"", json);
        Assert.Contains("\"Edge\":1", json);
    }

    [Fact]
    public void Rate_Clamps_At_The_Range_End_Without_Wrap()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 5 };
        var (t, _, anim) = Make(b, rms: 1f, start: 7.0, min: 0, max: 8);
        anim.Tick(1.0);
        Assert.Equal(8.0, t.Scalar, Eps);
    }

    [Fact]
    public void Rate_Reseeds_From_An_Outside_Edit()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 1 };
        var (t, _, anim) = Make(b, rms: 1f);
        anim.Tick(0.5);
        t.Scalar = 10.0;                  // the user drags the slider / a region recall
        anim.Tick(0.5);
        Assert.Equal(10.5, t.Scalar, Eps);
    }

    [Fact]
    public void Rate_On_An_Int_Param_Keeps_The_Fractional_Remainder()
    {
        // 0.3/s: each 0.1 s tick adds 0.03, far below one step — the rounded
        // setter must not swallow it (accumulator stays fractional).
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0.3, OutMax = 0.3 };
        var (t, _, anim) = Make(b, param: nameof(Target.Count));
        for (int i = 0; i < 10; i++) anim.Tick(0.1);   // 0.3 -> rounds to 0
        Assert.Equal(0, t.Count);
        for (int i = 0; i < 10; i++) anim.Tick(0.1);   // 0.6 -> rounds to 1
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Inactive_Source_Leaves_Rate_Target_Untouched()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 1, OutMax = 1 };
        var (t, src, anim) = Make(b, start: 2.0);
        src.IsActive = false;
        anim.Tick(1.0);
        Assert.Equal(2.0, t.Scalar, Eps);
    }

    // ── Smoothing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Zero_Slew_Position_Matches_Stateless_Evaluate()
    {
        var b = new AudioModulationBinding { Source = AudioSignalKind.Rms, Curve = AudioResponseCurve.Smoothstep,
                                             Gain = 1.3, Bias = -0.1, OutMin = -2, OutMax = 6 };
        var (t, src, anim) = Make(b);
        foreach (float v in new[] { 0f, 0.9f, 0.2f, 1f, 0.5f })
        {
            src.Frame = Rms(v);
            anim.Tick(0.05);
            Assert.Equal(b.Evaluate(src.Frame), t.Scalar, Eps);
        }
    }

    [Fact]
    public void Attack_Rises_With_Its_Time_Constant()
    {
        var b = new AudioModulationBinding { OutMin = 0, OutMax = 1, AttackSeconds = 0.5 };
        var (t, src, anim) = Make(b, rms: 0f);
        anim.Tick(0.05);                  // primes at 0 (no fade-in from nothing)
        Assert.Equal(0.0, t.Scalar, Eps);
        src.Frame = Rms(1f);
        anim.Tick(0.5);                   // one tau -> 1 - 1/e of the step
        Assert.Equal(1.0 - Math.Exp(-1.0), t.Scalar, 1e-9);
    }

    [Fact]
    public void Release_Falls_With_Its_Own_Time_Constant()
    {
        var b = new AudioModulationBinding { OutMin = 0, OutMax = 1, AttackSeconds = 0, ReleaseSeconds = 1.0 };
        var (t, src, anim) = Make(b, rms: 1f);
        anim.Tick(0.05);                  // primes at 1
        src.Frame = Rms(0f);
        anim.Tick(1.0);                   // one release tau -> 1/e remains
        Assert.Equal(Math.Exp(-1.0), t.Scalar, 1e-9);
        src.Frame = Rms(1f);
        anim.Tick(0.05);                  // attack 0 -> instant rise
        Assert.Equal(1.0, t.Scalar, Eps);
    }

    [Fact]
    public void Smoothing_Slews_The_Rate_Speed()
    {
        var b = new AudioModulationBinding { Mode = AudioBindingMode.Rate, OutMin = 0, OutMax = 1, AttackSeconds = 1.0 };
        var (t, src, anim) = Make(b, rms: 0f);
        anim.Tick(0.1);                   // prime at speed 0
        src.Frame = Rms(1f);
        anim.Tick(0.1);                   // speed only 1 - e^-0.1 of the way to 1/s
        Assert.Equal((1.0 - Math.Exp(-0.1)) * 0.1, t.Scalar, 1e-12);
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    [Fact]
    public void Default_Fields_Are_Omitted_And_Old_Json_Loads_As_Position()
    {
        string json = JsonSerializer.Serialize(new AudioModulationBinding());
        Assert.DoesNotContain("Mode", json);
        Assert.DoesNotContain("Wrap", json);
        Assert.DoesNotContain("Edge", json);
        Assert.DoesNotContain("Attack", json);
        Assert.DoesNotContain("Release", json);

        var old = JsonSerializer.Deserialize<AudioModulationBinding>("{\"Source\":1,\"OutMax\":4}")!;
        Assert.Equal(AudioBindingMode.Position, old.Mode);
        Assert.Equal(AudioRateEdge.Clamp, old.Edge);
        Assert.Equal(0.0, old.AttackSeconds);
        Assert.Equal(0.0, old.ReleaseSeconds);
    }

    [Fact]
    public void Region_Persists_Rate_Fields()
    {
        var region = new FractalRegion
        {
            Name = "Flow",
            FractalType = FractalType.AcidWarp,
            AudioBindings = new List<AudioParamBinding>
            {
                new()
                {
                    ParamName = "AcidWarpFlow", Enabled = true,
                    Binding = new AudioModulationBinding
                    {
                        Mode = AudioBindingMode.Rate, Edge = AudioRateEdge.PingPong,
                        AttackSeconds = 0.1, ReleaseSeconds = 0.8, OutMin = 0.05, OutMax = 1.5,
                    },
                },
            },
        };
        var back = JsonSerializer.Deserialize<List<FractalRegion>>(
            JsonSerializer.Serialize(new List<FractalRegion> { region }))!;
        var b = back[0].AudioBindings![0].Binding;
        Assert.Equal(AudioBindingMode.Rate, b.Mode);
        Assert.Equal(AudioRateEdge.PingPong, b.Edge);
        Assert.Equal(0.1, b.AttackSeconds, Eps);
        Assert.Equal(0.8, b.ReleaseSeconds, Eps);
    }

    // ── Manager / row VM ───────────────────────────────────────────────────────

    private static AudioModulationManager Manager() =>
        new(() => null, () => { }, () => null, () => FractalType.AcidWarp, () => null);

    private static AnimatableParamDescriptor Flow() =>
        FractalAnimatableParamsMap.For(FractalType.AcidWarp).Single(d => d.ParamName == "AcidWarpFlow");

    [Fact]
    public void AcidWarpFlow_Is_Periodic_And_Seeds_Wrap()
    {
        var d = Flow();
        Assert.True(d.Wraps);
        Assert.Equal(AudioRateEdge.Wrap, Manager().GetOrCreateBinding(d).Edge);
    }

    [Fact]
    public void Manager_Export_Clones_The_New_Fields()
    {
        var mgr = Manager();
        var b = mgr.GetOrCreateBinding(Flow());
        b.Mode = AudioBindingMode.Rate; b.AttackSeconds = 0.2; b.ReleaseSeconds = 0.6;
        var copy = mgr.ExportBindings().Single().Binding;
        Assert.NotSame(b, copy);
        Assert.Equal(AudioBindingMode.Rate, copy.Mode);
        Assert.Equal(AudioRateEdge.Wrap, copy.Edge);
        Assert.Equal(0.2, copy.AttackSeconds, Eps);
        Assert.Equal(0.6, copy.ReleaseSeconds, Eps);
    }

    [Fact]
    public void Row_Mode_Switch_Reseeds_An_Untouched_Range_Both_Ways()
    {
        var row = new AudioBindingRowViewModel(Flow(), Manager());
        Assert.Equal(0.0, row.OutMin); Assert.Equal(20.0, row.OutMax);

        row.Mode = AudioBindingMode.Rate;               // 0 .. a tenth of the range per second
        Assert.Equal(0.0, row.OutMin); Assert.Equal(2.0, row.OutMax);
        Assert.Equal("Max/s:", row.MaxLabel);

        row.Mode = AudioBindingMode.Position;           // back to the param range
        Assert.Equal(0.0, row.OutMin); Assert.Equal(20.0, row.OutMax);
    }

    [Fact]
    public void Row_Mode_Switch_Keeps_A_User_Range()
    {
        var row = new AudioBindingRowViewModel(Flow(), Manager());
        row.OutMin = 4; row.OutMax = 5;
        row.Mode = AudioBindingMode.Rate;
        Assert.Equal(4.0, row.OutMin); Assert.Equal(5.0, row.OutMax);
    }

    [Fact]
    public void AnyEnabledFor_Matches_Only_Enabled_Bindings_Of_The_Param()
    {
        var list = new List<AudioParamBinding>
        {
            new() { ParamName = "AcidWarpFlow", Enabled = false },
            new() { ParamName = "AcidWarpFrequency", Enabled = true },
        };
        Assert.False(AudioModulationManager.AnyEnabledFor(list, "AcidWarpFlow"));
        list[0].Enabled = true;
        Assert.True(AudioModulationManager.AnyEnabledFor(list, "AcidWarpFlow"));
        Assert.False(AudioModulationManager.AnyEnabledFor(null, "AcidWarpFlow"));
    }

    // ── #1432 batch gap ────────────────────────────────────────────────────────

    [Fact]
    public void Audio_Driven_Params_Are_Reported_As_A_Batch_Gap()
    {
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot()),
                              g => g.Contains("Audio-driven"));
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot
        {
            AudioDrivenParams = new[] { "AcidWarpFlow" },
        });
        Assert.Contains(gaps, g => g.Contains("Audio-driven") && g.Contains("AcidWarpFlow") && g.Contains("#1432"));
    }
}
