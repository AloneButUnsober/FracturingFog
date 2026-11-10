// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1432 — audio→param bindings in batch: the --audio-bind spec, its parse
// round-trip through the Command builder, and the per-frame drive. The drive is
// checked against closed forms computed here, not against the animator: a linear
// Position binding equals min + (max − min)·signal(f/fps); a release-only one-pole
// decays as exp(−n·dt/τ) after the signal drops; a Rate binding moves by
// speed·n/fps.
public sealed class BatchAudioDriveTests
{
    // A seekable source whose bass is a function of time.
    private sealed class TimedSource : IAudioModulationSource
    {
        private readonly Func<double, float> _bass;
        public TimedSource(Func<double, float> bass) => _bass = bass;
        public bool IsActive { get; set; } = true;
        public long BeatCount => 0;
        public long DownbeatCount => 0;
        public List<double> Reads { get; } = new();
        public AudioModulationFrame Sample() => throw new InvalidOperationException("batch must seek, not read live");
        public AudioModulationFrame SampleAt(double seconds)
        {
            Reads.Add(seconds);
            return IsActive ? new(_bass(seconds), 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 120, true) : AudioModulationFrame.Inactive;
        }
    }

    private static AudioParamBinding Bind(string spec)
    {
        Assert.True(AudioBindSpec.TryParse(spec, out var b, out var err), err);
        return b;
    }

    // ── Spec ───────────────────────────────────────────────────────────────

    [Fact]
    public void Spec_Parses_Every_Key()
    {
        var b = Bind("SlitCascadeWavelength=Bass,min=0.4,max=1.4,gain=2,bias=-0.1,curve=smoothstep,invert=true,mode=rate,edge=pingpong,attack=0.05,release=0.4");
        Assert.Equal("SlitCascadeWavelength", b.ParamName);
        Assert.True(b.Enabled);
        var m = b.Binding;
        Assert.Equal(AudioSignalKind.Bass, m.Source);
        Assert.Equal(0.4, m.OutMin); Assert.Equal(1.4, m.OutMax); Assert.Equal(2, m.Gain); Assert.Equal(-0.1, m.Bias);
        Assert.Equal(AudioResponseCurve.Smoothstep, m.Curve); Assert.True(m.Invert);
        Assert.Equal(AudioBindingMode.Rate, m.Mode); Assert.Equal(AudioRateEdge.PingPong, m.Edge);
        Assert.Equal(0.05, m.AttackSeconds); Assert.Equal(0.4, m.ReleaseSeconds);
        var d = Bind("X=beatpulse").Binding;   // signal case-insensitive; the rest default
        Assert.Equal(AudioSignalKind.BeatPulse, d.Source);
        Assert.Equal(0.0, d.OutMin); Assert.Equal(1.0, d.OutMax);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Bass", "PARAM=SIGNAL")]
    [InlineData("X=Treble", "unknown audio signal")]
    [InlineData("X=Bass,depth=2", "unknown key")]
    [InlineData("X=Bass,min=0,min=1", "twice")]
    [InlineData("X=Bass,max=loud", "number")]
    [InlineData("X=Bass,max=NaN", "number")]
    [InlineData("X=Bass,attack=-1", "0 or more")]
    [InlineData("X=Bass,curve=1", "curve expects")]
    [InlineData("X=Bass,edge=bounce", "edge expects")]
    [InlineData("X=Bass,invert=yes", "invert expects")]
    [InlineData("X=Bass,min", "key=value")]
    public void Spec_Rejects_Bad_Input_With_A_Reason(string spec, string reason)
    {
        Assert.False(AudioBindSpec.TryParse(spec, out _, out var err));
        Assert.Contains(reason, err);
    }

    [Fact]
    public void Every_Binding_Field_Survives_Format_And_Parse()
    {
        // Each settable field of AudioModulationBinding moved off its default, one at a
        // time: a field the spec does not carry fails here when it is added.
        var props = typeof(AudioModulationBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetSetMethod() != null).ToList();
        Assert.True(props.Count >= 11, string.Join(", ", props.Select(p => p.Name)));
        foreach (var p in props)
        {
            var b = new AudioModulationBinding();
            object v = p.PropertyType == typeof(double) ? 0.3125
                : p.PropertyType == typeof(bool) ? true
                : Enum.GetValues(p.PropertyType).GetValue(Enum.GetValues(p.PropertyType).Length - 1)!;
            p.SetValue(b, v);
            var back = Bind(AudioBindSpec.Format(new AudioParamBinding { ParamName = "P", Enabled = true, Binding = b })).Binding;
            Assert.Equal(v, p.GetValue(back));
        }
    }

    [Fact]
    public void Options_Collect_The_File_And_Every_Binding()
    {
        Assert.True(BatchOptions.TryParse(new[] { "--mode", "video", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o",
            "--audio", "m.wav", "--audio-bind", "A=Bass,max=2", "--audio-bind", "B=BeatPulse" }, 0, out var o, out var err), err);
        Assert.Equal("m.wav", o.AudioPath);
        Assert.Equal(new[] { "A", "B" }, o.AudioBinds.Select(b => b.ParamName));
        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png",
            "--audio-bind", "A=Nope" }, 0, out _, out var bad));
        Assert.Contains("--audio-bind", bad);
    }

    // ── Command builder ────────────────────────────────────────────────────

    [Fact]
    public void Command_Builder_Emits_Live_Bindings_That_Parse_Back()
    {
        var live = new[]
        {
            Bind("SlitCascadeWavelength=Bass,min=0.4,max=1.4,curve=smoothstep,attack=0.05,release=0.4"),
            Bind("SlitCascadeWidth=Rms,min=0.1,max=0.3,mode=rate,edge=wrap"),
        };
        var p = new FractalParameters();
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.SlitCascade, Parameters = p,
            AudioDrivenParams = live.Select(b => b.ParamName).ToArray(), AudioBindings = live,
        });
        var args = report.Args.Concat(new[] { "--mode", "video", "--audio", "m.wav" }).ToArray();
        Assert.True(BatchOptions.TryParse(args, 0, out var o, out var err), err);
        Assert.Equal(2, o.AudioBinds.Count);
        foreach (var b in live)
        {
            var got = o.AudioBinds.Single(x => x.ParamName == b.ParamName);
            Assert.Equal(AudioBindSpec.Format(b), AudioBindSpec.Format(got));
        }
        Assert.Contains(report.Gaps, g => g.Contains("SlitCascadeWavelength") && g.Contains("--audio FILE") && g.Contains("#1432"));
        var none = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot { Fractal = FractalType.SlitCascade, Parameters = p });
        Assert.DoesNotContain(BatchFlags.AudioBind, none.Args);
    }

    // ── Drive ──────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_Lets_The_Command_Line_Replace_A_Region_Binding()
    {
        var region = new[] { Bind("A=Bass,max=1"), Bind("B=Rms"), new AudioParamBinding { ParamName = "C", Enabled = false } };
        var merged = BatchAudioDrive.Merge(region, new[] { Bind("A=BeatPulse,max=5") });
        Assert.Equal(new[] { "B", "A" }, merged.Select(b => b.ParamName));
        Assert.Equal(AudioSignalKind.BeatPulse, merged.Single(b => b.ParamName == "A").Binding.Source);
    }

    [Fact]
    public void Position_Binding_Follows_The_Signal_At_Each_Frame_Time()
    {
        const double fps = 25;
        var src = new TimedSource(t => (float)(0.5 + 0.5 * Math.Sin(3.0 * t)));
        var p = new FractalParameters();
        var skipped = new List<string>();
        var drive = BatchAudioDrive.Create(p, FractalType.SlitCascade, src,
            new[] { Bind("SlitCascadeWavelength=Bass,min=0.4,max=1.4"), Bind("NotASetting=Bass") }, fps, skipped);
        Assert.Equal(new[] { "SlitCascadeWavelength" }, drive.Driven);
        Assert.Contains(skipped, s => s.StartsWith("NotASetting"));
        for (int f = 0; f < 60; f++)
        {
            drive.ApplyFrame(f);
            double t = f / fps;
            Assert.Equal(t, src.Reads[^1], 12);
            Assert.Equal(0.4 + 1.0 * (float)(0.5 + 0.5 * Math.Sin(3.0 * t)), p.SlitCascadeWavelength, 9);
        }
    }

    [Fact]
    public void Release_Smoothing_Decays_As_The_One_Pole_Closed_Form()
    {
        const double fps = 30, tau = 0.4;
        var src = new TimedSource(t => t < 1.0 ? 1f : 0f);   // drops at frame 30
        var p = new FractalParameters();
        var drive = BatchAudioDrive.Create(p, FractalType.SlitCascade, src,
            new[] { Bind($"SlitCascadeWavelength=Bass,min=0,max=1,release={tau}") }, fps);
        for (int f = 0; f <= 30 + 20; f++)
        {
            drive.ApplyFrame(f);
            if (f < 30) { Assert.Equal(1.0, p.SlitCascadeWavelength, 12); continue; }
            int n = f - 29;   // frames since the drop
            Assert.Equal(Math.Exp(-n / fps / tau), p.SlitCascadeWavelength, 9);
        }
    }

    [Fact]
    public void Rate_Binding_Integrates_The_Speed_Per_Frame()
    {
        const double fps = 20;
        var src = new TimedSource(_ => 1f);
        var p = new FractalParameters { SlitCascadeWavelength = 0.5 };
        var drive = BatchAudioDrive.Create(p, FractalType.SlitCascade, src,
            new[] { Bind("SlitCascadeWavelength=Bass,min=0,max=0.25,mode=rate") }, fps);   // 0.25 / s at full signal
        for (int f = 0; f < 40; f++) drive.ApplyFrame(f);
        Assert.Equal(0.5 + 0.25 * 40 / fps, p.SlitCascadeWavelength, 9);
    }

    [Fact]
    public void A_Repeat_Call_For_The_Same_Frame_Rewrites_Without_Advancing()
    {
        const double fps = 30;
        var src = new TimedSource(t => t < 0.5 ? 1f : 0f);
        var p = new FractalParameters();
        var drive = BatchAudioDrive.Create(p, FractalType.SlitCascade, src,
            new[] { Bind("SlitCascadeWavelength=Bass,min=0,max=1,release=0.4") }, fps);
        for (int f = 0; f <= 20; f++) drive.ApplyFrame(f);
        double at20 = p.SlitCascadeWavelength;
        p.SlitCascadeWavelength = 3.0;          // e.g. a timeline animation's sub-frame pose
        int reads = src.Reads.Count;
        drive.ApplyFrame(20);
        Assert.Equal(at20, p.SlitCascadeWavelength);
        Assert.Equal(reads, src.Reads.Count);   // no new sample: the smoothing did not advance
        drive.ApplyFrame(21);
        Assert.True(p.SlitCascadeWavelength < at20);
    }

    [Fact]
    public void An_Inactive_Source_Leaves_The_Setting_Alone()
    {
        var src = new TimedSource(_ => 1f) { IsActive = false };
        var p = new FractalParameters { SlitCascadeWavelength = 0.77 };
        var drive = BatchAudioDrive.Create(p, FractalType.SlitCascade, src, new[] { Bind("SlitCascadeWavelength=Bass,min=0,max=1") }, 30);
        for (int f = 0; f < 5; f++) drive.ApplyFrame(f);
        Assert.Equal(0.77, p.SlitCascadeWavelength);
    }

    [Fact]
    public void A_Preset_Region_Drives_Its_Own_Binding()
    {
        // The #1604 demo: the beat switches the which-path detector on.
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == "Double Slit - Beat Collapse");
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        var beat = new BeatSource(t => (t % 0.5) < 0.05 ? 1f : 0f);
        var drive = BatchAudioDrive.Create(p, region.FractalType, beat, BatchAudioDrive.Merge(region.AudioBindings, null), 20);
        Assert.Equal(new[] { "DualOrbitWhichPath" }, drive.Driven);
        drive.ApplyFrame(0);
        Assert.Equal(1.0, p.DualOrbitWhichPath, 9);   // on the beat
        for (int f = 1; f <= 8; f++) drive.ApplyFrame(f);
        // Released between beats with the preset's 0.15 s release: e^{−8·(1/20)/0.15}.
        double release = region.AudioBindings!.Single().Binding.ReleaseSeconds;
        Assert.Equal(Math.Exp(-8.0 / 20.0 / release), p.DualOrbitWhichPath, 9);
    }

    private sealed class BeatSource : IAudioModulationSource
    {
        private readonly Func<double, float> _beat;
        public BeatSource(Func<double, float> beat) => _beat = beat;
        public bool IsActive => true;
        public long BeatCount => 0;
        public long DownbeatCount => 0;
        public AudioModulationFrame Sample() => throw new InvalidOperationException();
        public AudioModulationFrame SampleAt(double t) => new(0, 0, 0, 0, 0, 0, _beat(t), 0, 0, 0, false, 120, true);
    }
}
