// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #265 / Audio-Reactive Phase 6 — scene audio tracks: a SceneGlobalTarget scalar
// driven live from an audio signal instead of keyframes. Covers the pure Apply
// (target write / inactive gate / later-wins / multi-target), the animator, and
// JSON persistence on SceneData.
public sealed class SceneAudioTrackTests
{
    private const int P = 9;

    private sealed class FakeModSource : IAudioModulationSource
    {
        public bool IsActive { get; set; } = true;
        public long BeatCount { get; set; }
        public long DownbeatCount { get; set; }
        public AudioModulationFrame Frame { get; set; }
        public AudioModulationFrame Sample() => Frame;
        public AudioModulationFrame SampleAt(double seconds) => Frame;
    }

    // Field order: bass, lowMid, mid, highMid, high, rms, beatPulse,
    // downbeatPulse, bpmPhaseSaw, bpmPhaseSine, transient, bpm, isActive.
    private static AudioModulationFrame BassFrame(float bass, bool active = true) =>
        new(bass, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, active);

    private static AudioModulationFrame RmsFrame(float rms, bool active = true) =>
        new(0, 0, 0, 0, 0, rms, 0, 0, 0, 0, false, 0, active);

    // ── Pure Apply ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Writes_Target_From_Signal()
    {
        var track = new SceneAudioTrack
        {
            Target = SceneGlobalTarget.Exposure,
            Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 1.0, OutMax = 2.0 },
        };
        var p = new FractalParameters();

        track.Apply(p, BassFrame(1f));                 // signal 1 -> OutMax
        Assert.Equal(2.0, p.Lighting.Exposure, precision: P);

        track.Apply(p, BassFrame(0f));                 // signal 0 -> OutMin
        Assert.Equal(1.0, p.Lighting.Exposure, precision: P);
    }

    [Fact]
    public void Apply_Set_Inactive_Frame_Leaves_Base()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 0.0, OutMax = 5.0 },
            },
        };
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;

        SceneAudioTracks.Apply(tracks, p, BassFrame(1f, active: false));
        Assert.Equal(before, p.Lighting.Exposure, precision: P); // inactive analyzer = no write
    }

    [Fact]
    public void Apply_Set_Later_Track_Wins_Same_Target()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 0.2, OutMax = 0.2 },
            },
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 0.9, OutMax = 0.9 },
            },
        };
        var p = new FractalParameters();

        SceneAudioTracks.Apply(tracks, p, BassFrame(1f));
        Assert.Equal(0.9, p.Lighting.Exposure, precision: P); // second track applied last
    }

    [Fact]
    public void Apply_Set_Multiple_Targets()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMin = 1.0, OutMax = 1.5 },
            },
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.BloomStrength,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Rms, OutMin = 0.0, OutMax = 0.8 },
            },
        };
        var p = new FractalParameters();

        SceneAudioTracks.Apply(tracks, p, RmsFrame(1f));
        Assert.Equal(1.5, p.Lighting.Exposure, precision: P);
        Assert.Equal(0.8, p.Lighting.BloomStrength, precision: P);
    }

    [Fact]
    public void Apply_Null_Or_Empty_Set_NoOp()
    {
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;

        SceneAudioTracks.Apply(null, p, BassFrame(1f));
        SceneAudioTracks.Apply(new List<SceneAudioTrack>(), p, BassFrame(1f));
        Assert.Equal(before, p.Lighting.Exposure, precision: P);
    }

    // ── Animator ───────────────────────────────────────────────────────────────

    [Fact]
    public void Animator_Tick_Applies_When_Source_Active()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 1.0, OutMax = 3.0 },
            },
        };
        var p = new FractalParameters();
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new SceneAudioTrackAnimator(tracks, p, src);

        Assert.True(anim.HasWork);
        anim.Tick(0.05);
        Assert.Equal(3.0, p.Lighting.Exposure, precision: P);
    }

    [Fact]
    public void Animator_Tick_Inactive_Source_NoOp()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 0.0, OutMax = 9.0 },
            },
        };
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;
        var src = new FakeModSource { IsActive = false, Frame = BassFrame(1f, active: false) };
        var anim = new SceneAudioTrackAnimator(tracks, p, src);

        anim.Tick(0.05);
        Assert.Equal(before, p.Lighting.Exposure, precision: P);
    }

    [Fact]
    public void Animator_Tick_Disabled_NoOp()
    {
        var tracks = new[]
        {
            new SceneAudioTrack
            {
                Target = SceneGlobalTarget.Exposure,
                Binding = new AudioModulationBinding { Source = AudioSignalKind.Bass, OutMin = 0.0, OutMax = 9.0 },
            },
        };
        var p = new FractalParameters();
        double before = p.Lighting.Exposure;
        var src = new FakeModSource { Frame = BassFrame(1f) };
        var anim = new SceneAudioTrackAnimator(tracks, p, src) { IsEnabled = false };

        anim.Tick(0.05);
        Assert.Equal(before, p.Lighting.Exposure, precision: P);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    [Fact]
    public void SceneData_AudioTracks_RoundTrip_Json()
    {
        var scene = new SceneData { Name = "Audio Scene" };
        scene.AudioTracks.Add(new SceneAudioTrack
        {
            Target = SceneGlobalTarget.BloomStrength,
            Binding = new AudioModulationBinding
            {
                Source = AudioSignalKind.Rms,
                Curve = AudioResponseCurve.Smoothstep,
                Gain = 1.5,
                OutMin = 0.1,
                OutMax = 0.7,
                Invert = true,
            },
        });

        var opts = SceneLibrary.BuildJsonOptions();
        string json = JsonSerializer.Serialize(scene, opts);
        var back = JsonSerializer.Deserialize<SceneData>(json, opts);

        Assert.NotNull(back);
        Assert.Single(back!.AudioTracks);
        var t = back.AudioTracks[0];
        Assert.Equal(SceneGlobalTarget.BloomStrength, t.Target);
        Assert.Equal(AudioSignalKind.Rms, t.Binding.Source);
        Assert.Equal(AudioResponseCurve.Smoothstep, t.Binding.Curve);
        Assert.Equal(1.5, t.Binding.Gain, precision: P);
        Assert.Equal(0.1, t.Binding.OutMin, precision: P);
        Assert.Equal(0.7, t.Binding.OutMax, precision: P);
        Assert.True(t.Binding.Invert);
    }
}
