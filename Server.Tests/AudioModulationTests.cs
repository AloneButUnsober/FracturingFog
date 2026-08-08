// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog.Audio;

namespace FracturingFog.Server.Tests;

// #260 / Audio-Reactive Phase 1 — foundation. Covers the analytic envelope /
// tempo-phase math of AudioModulationSource (driven off a fake IBeatSource with
// an injected clock) and the pure shaping pipeline of AudioModulationBinding.
public class AudioModulationTests
{
    private const double Eps = 1e-4;

    // ── Fake beat source ────────────────────────────────────────────────
    private sealed class FakeBeatSource : IBeatSource
    {
        public bool IsActive { get; set; } = true;
        public double EstimatedBpm { get; set; }
        public BandEnergy CurrentEnergy { get; set; } = BandEnergy.Empty;
        public event EventHandler<BeatEventArgs>? Beat;
        public event EventHandler<BeatEventArgs>? Downbeat;

        public void RaiseBeat(DateTime t, double strength) =>
            Beat?.Invoke(this, new BeatEventArgs { TimestampUtc = t, Strength = strength });

        public void RaiseDownbeat(DateTime t, double strength) =>
            Downbeat?.Invoke(this, new BeatEventArgs { TimestampUtc = t, Strength = strength });
    }

    private static (FakeBeatSource fake, AudioModulationSource src, Func<DateTime> now, Action<double> advance)
        Build(DateTime? start = null)
    {
        var t = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime now = t;
        var fake = new FakeBeatSource();
        var src = new AudioModulationSource(fake, () => now);
        return (fake, src, () => now, secs => now = now.AddSeconds(secs));
    }

    // ── Source: activity gating ─────────────────────────────────────────
    [Fact]
    public void Inactive_Source_Returns_Inactive_Frame()
    {
        var (fake, src, _, _) = Build();
        fake.IsActive = false;
        fake.CurrentEnergy = new BandEnergy(1f, 1f, 1f, 1f, 1f, 1f);

        var f = src.Sample();

        Assert.False(f.IsActive);
        Assert.Equal(0f, f.Bass);
        Assert.Equal(0f, f.Rms);
        Assert.Equal(0f, f.BeatPulse);
    }

    // ── Source: band / rms passthrough ──────────────────────────────────
    [Fact]
    public void Bands_And_Rms_Pass_Through_Clamped()
    {
        var (fake, src, _, _) = Build();
        fake.CurrentEnergy = new BandEnergy(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f);

        var f = src.Sample();

        Assert.True(f.IsActive);
        Assert.Equal(0.1f, f.Bass, Eps);
        Assert.Equal(0.2f, f.LowMid, Eps);
        Assert.Equal(0.3f, f.Mid, Eps);
        Assert.Equal(0.4f, f.HighMid, Eps);
        Assert.Equal(0.5f, f.High, Eps);
        Assert.Equal(0.6f, f.Rms, Eps);
    }

    // ── Source: beat envelope ───────────────────────────────────────────
    [Fact]
    public void BeatPulse_Zero_Before_Any_Beat()
    {
        var (_, src, _, _) = Build();
        Assert.Equal(0f, src.Sample().BeatPulse);
    }

    [Fact]
    public void BeatPulse_Decays_By_1_Over_E_After_One_Tau()
    {
        var (fake, src, now, advance) = Build();
        src.DecaySeconds = 0.2;
        fake.RaiseBeat(now(), strength: 1.0);

        Assert.Equal(1.0f, src.Sample().BeatPulse, Eps);   // at t=0

        advance(0.2);                                       // one tau later
        Assert.Equal((float)(1.0 / Math.E), src.Sample().BeatPulse, 1e-3);

        advance(0.2);                                       // two tau
        Assert.Equal((float)(1.0 / (Math.E * Math.E)), src.Sample().BeatPulse, 1e-3);
    }

    [Fact]
    public void BeatPulse_Scales_With_Strength()
    {
        var (fake, src, now, _) = Build();
        fake.RaiseBeat(now(), strength: 0.5);
        Assert.Equal(0.5f, src.Sample().BeatPulse, Eps);
    }

    [Fact]
    public void DownbeatPulse_Independent_Of_Beat()
    {
        var (fake, src, now, _) = Build();
        fake.RaiseBeat(now(), 1.0);
        Assert.Equal(0f, src.Sample().DownbeatPulse);      // no downbeat yet
        fake.RaiseDownbeat(now(), 0.8);
        Assert.Equal(0.8f, src.Sample().DownbeatPulse, Eps);
    }

    // ── Source: transient one-shot ──────────────────────────────────────
    [Fact]
    public void Transient_True_Inside_Window_Then_False()
    {
        var (fake, src, now, advance) = Build();
        src.TransientWindowSeconds = 0.060;
        fake.RaiseBeat(now(), 1.0);

        Assert.True(src.Sample().Transient);               // just landed
        advance(0.030);
        Assert.True(src.Sample().Transient);               // still inside window
        advance(0.050);                                    // now 0.080 > window
        Assert.False(src.Sample().Transient);
    }

    // ── Source: tempo-locked phase ──────────────────────────────────────
    [Fact]
    public void BpmPhaseSaw_Ramps_And_Wraps()
    {
        var (fake, src, now, advance) = Build();
        fake.EstimatedBpm = 120;                           // period = 0.5 s
        fake.RaiseDownbeat(now(), 1.0);                    // anchor phase

        Assert.Equal(0f, src.Sample().BpmPhaseSaw, Eps);
        advance(0.25);
        Assert.Equal(0.5f, src.Sample().BpmPhaseSaw, Eps);
        advance(0.25);                                     // full period -> wrap
        Assert.Equal(0f, src.Sample().BpmPhaseSaw, Eps);
    }

    [Fact]
    public void BpmPhaseSine_Follows_Saw()
    {
        var (fake, src, now, advance) = Build();
        fake.EstimatedBpm = 120;
        fake.RaiseDownbeat(now(), 1.0);

        Assert.Equal(0.5f, src.Sample().BpmPhaseSine, Eps); // sin(0) -> 0.5
        advance(0.125);                                     // quarter beat, saw=0.25
        Assert.Equal(1.0f, src.Sample().BpmPhaseSine, Eps); // sin(pi/2) -> 1.0
    }

    [Fact]
    public void No_Bpm_Yields_Zero_Phase()
    {
        var (fake, src, now, _) = Build();
        fake.EstimatedBpm = 0;
        fake.RaiseBeat(now(), 1.0);
        Assert.Equal(0f, src.Sample().BpmPhaseSaw);
    }

    [Fact]
    public void SampleAt_Falls_Back_To_Sample_For_Live_Source()
    {
        var (fake, src, _, _) = Build();
        fake.CurrentEnergy = new BandEnergy(0.7f, 0f, 0f, 0f, 0f, 0.3f);
        Assert.Equal(src.Sample().Bass, src.SampleAt(123.4).Bass, Eps);
    }

    // ── Frame.Signal dispatch ───────────────────────────────────────────
    [Fact]
    public void Frame_Signal_Reads_Correct_Field()
    {
        var f = new AudioModulationFrame(
            Bass: 0.11f, LowMid: 0.22f, Mid: 0.33f, HighMid: 0.44f, High: 0.55f,
            Rms: 0.66f, BeatPulse: 0.77f, DownbeatPulse: 0.88f,
            BpmPhaseSaw: 0.12f, BpmPhaseSine: 0.34f,
            Transient: true, Bpm: 128, IsActive: true);

        Assert.Equal(0.11f, f.Signal(AudioSignalKind.Bass));
        Assert.Equal(0.66f, f.Signal(AudioSignalKind.Rms));
        Assert.Equal(0.77f, f.Signal(AudioSignalKind.BeatPulse));
        Assert.Equal(0.88f, f.Signal(AudioSignalKind.DownbeatPulse));
        Assert.Equal(0.12f, f.Signal(AudioSignalKind.BpmPhaseSaw));
        Assert.Equal(0.34f, f.Signal(AudioSignalKind.BpmPhaseSine));
    }

    // ── Binding: shaping pipeline ───────────────────────────────────────
    private static AudioModulationFrame FrameWith(AudioSignalKind kind, float value)
    {
        // Build a frame whose target signal carries `value`, rest zero.
        float b = 0, lm = 0, m = 0, hm = 0, h = 0, rms = 0, bp = 0, dp = 0, saw = 0, sine = 0;
        switch (kind)
        {
            case AudioSignalKind.Bass: b = value; break;
            case AudioSignalKind.Rms: rms = value; break;
            case AudioSignalKind.BeatPulse: bp = value; break;
            case AudioSignalKind.BpmPhaseSaw: saw = value; break;
            default: rms = value; break;
        }
        return new AudioModulationFrame(b, lm, m, hm, h, rms, bp, dp, saw, sine, false, 0, true);
    }

    [Fact]
    public void Binding_Linear_Maps_Into_Output_Range()
    {
        var bind = new AudioModulationBinding
        {
            Source = AudioSignalKind.Bass, OutMin = 2.0, OutMax = 8.0,
        };
        Assert.Equal(2.0, bind.Evaluate(FrameWith(AudioSignalKind.Bass, 0f)), Eps);
        Assert.Equal(5.0, bind.Evaluate(FrameWith(AudioSignalKind.Bass, 0.5f)), Eps);
        Assert.Equal(8.0, bind.Evaluate(FrameWith(AudioSignalKind.Bass, 1f)), Eps);
    }

    [Fact]
    public void Binding_Gain_And_Bias_Clamp_To_Unit_Before_Map()
    {
        var bind = new AudioModulationBinding
        {
            Source = AudioSignalKind.Rms, Gain = 4.0, Bias = 0.0, OutMin = 0, OutMax = 10,
        };
        // 0.5 * 4 = 2 -> clamp01 -> 1 -> maps to OutMax.
        Assert.Equal(10.0, bind.Evaluate(FrameWith(AudioSignalKind.Rms, 0.5f)), Eps);

        bind.Gain = 1.0; bind.Bias = 0.25;
        // 0.5 + 0.25 = 0.75 -> maps to 7.5
        Assert.Equal(7.5, bind.Evaluate(FrameWith(AudioSignalKind.Rms, 0.5f)), Eps);
    }

    [Fact]
    public void Binding_Invert_Flips_Signal()
    {
        var bind = new AudioModulationBinding
        {
            Source = AudioSignalKind.Rms, Invert = true, OutMin = 0, OutMax = 1,
        };
        Assert.Equal(0.75, bind.Evaluate(FrameWith(AudioSignalKind.Rms, 0.25f)), Eps);
    }

    [Theory]
    [InlineData(AudioResponseCurve.Exp, 0.5f, 0.25)]        // x^2
    [InlineData(AudioResponseCurve.Log, 0.25f, 0.5)]        // sqrt(x)
    [InlineData(AudioResponseCurve.Smoothstep, 0.5f, 0.5)]  // x^2(3-2x)
    [InlineData(AudioResponseCurve.Linear, 0.4f, 0.4)]
    public void Binding_Curves_Shape_Signal(AudioResponseCurve curve, float input, double expected)
    {
        var bind = new AudioModulationBinding
        {
            Source = AudioSignalKind.Rms, Curve = curve, OutMin = 0, OutMax = 1,
        };
        Assert.Equal(expected, bind.Evaluate(FrameWith(AudioSignalKind.Rms, input)), 1e-4);
    }

    [Fact]
    public void Binding_Clamps_Signal_Above_One()
    {
        var bind = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMax = 1 };
        // Frame carries an out-of-range signal; binding must clamp to 1.
        var f = FrameWith(AudioSignalKind.Rms, 5f);
        Assert.Equal(1.0, bind.Evaluate(f), Eps);
    }
}
