// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog.Audio;
using Xunit;

namespace FracturingFog.Server.Tests;

// #266 / Audio-Reactive Phase 7 — deterministic offline audio source. Covers the
// seekable reconstruction (band interpolation, beat/downbeat envelope decay,
// transient window, tempo-locked phase), the inactive/empty contract, and the
// end-to-end determinism of the ffmpeg-free AnalyzePcm bake (same samples ->
// identical frames at the same time).
public sealed class OfflineAudioModulationTests
{
    private const int P = 6;

    private static OfflineAudioModulationSource Source(
        double[] times, BandEnergy[] bands, double[] bpms,
        double[]? beatT = null, float[]? beatS = null,
        double[]? downT = null, float[]? downS = null)
        => new(times, bands, bpms,
               beatT ?? Array.Empty<double>(), beatS ?? Array.Empty<float>(),
               downT ?? Array.Empty<double>(), downS ?? Array.Empty<float>());

    private static BandEnergy Bass(float b) => new(b, 0, 0, 0, 0, 0);

    // ── Reconstruction ──────────────────────────────────────────────────────────

    [Fact]
    public void SampleAt_Interpolates_Bands_Between_Samples()
    {
        var src = Source(
            new[] { 0.0, 1.0, 2.0 },
            new[] { Bass(0f), Bass(1f), Bass(0f) },
            new[] { 0.0, 0.0, 0.0 });

        Assert.Equal(0.5f, src.SampleAt(0.5).Bass, precision: P);
        Assert.Equal(0.5f, src.SampleAt(1.5).Bass, precision: P);
        Assert.Equal(1.0f, src.SampleAt(1.0).Bass, precision: P);
    }

    [Fact]
    public void SampleAt_Before_First_And_After_Last_Clamp()
    {
        var src = Source(
            new[] { 1.0, 2.0 },
            new[] { Bass(0.2f), Bass(0.8f) },
            new[] { 0.0, 0.0 });

        Assert.Equal(0.2f, src.SampleAt(-5.0).Bass, precision: P); // clamps to first
        Assert.Equal(0.8f, src.SampleAt(99.0).Bass, precision: P); // clamps to last
    }

    [Fact]
    public void SampleAt_Beat_Envelope_Decays_And_Transient_Windows()
    {
        var src = Source(
            new[] { 0.0 }, new[] { Bass(0f) }, new[] { 0.0 },
            beatT: new[] { 1.0 }, beatS: new[] { 1.0f });

        // At the onset: full pulse, transient set.
        var at = src.SampleAt(1.0);
        Assert.Equal(1.0f, at.BeatPulse, precision: 3);
        Assert.True(at.Transient);

        // One decay constant later: ~1/e, transient cleared (past 60 ms window).
        var later = src.SampleAt(1.0 + 0.18);
        Assert.Equal((float)Math.Exp(-1.0), later.BeatPulse, precision: 3);
        Assert.False(later.Transient);

        // Before the beat: nothing.
        Assert.Equal(0f, src.SampleAt(0.5).BeatPulse, precision: P);
    }

    [Fact]
    public void SampleAt_Tempo_Phase_Anchored_To_Downbeat()
    {
        var src = Source(
            new[] { 0.0 }, new[] { Bass(0f) }, new[] { 120.0 }, // 120 bpm -> 0.5 s/beat
            downT: new[] { 0.0 }, downS: new[] { 1.0f });

        Assert.Equal(0.5f, src.SampleAt(0.25).BpmPhaseSaw, precision: P); // quarter -> half saw
        Assert.Equal(0.0f, src.SampleAt(0.5).BpmPhaseSaw, precision: P);  // full beat -> wraps to 0
    }

    [Fact]
    public void Empty_Source_Is_Inactive_And_Returns_Inactive_Frame()
    {
        var src = Source(Array.Empty<double>(), Array.Empty<BandEnergy>(), Array.Empty<double>());
        Assert.False(src.IsActive);
        Assert.False(src.SampleAt(1.0).IsActive);
    }

    [Fact]
    public void Nonempty_Source_Is_Active()
    {
        var src = Source(new[] { 0.0 }, new[] { Bass(0.5f) }, new[] { 0.0 });
        Assert.True(src.IsActive);
        Assert.True(src.SampleAt(0.0).IsActive);
    }

    // ── AnalyzePcm bake ─────────────────────────────────────────────────────────

    // A steady 2 s stereo tone with periodic bass thumps — deterministic, no RNG.
    private static float[] SynthPcm(int sampleRate, int channels, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        var buf = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            double t = f / (double)sampleRate;
            double bass = Math.Sin(2 * Math.PI * 60 * t) * 0.3;
            // A short loud thump every 0.5 s to seed onsets.
            double phase = t - Math.Floor(t / 0.5) * 0.5;
            double thump = phase < 0.02 ? Math.Sin(2 * Math.PI * 90 * t) * 0.9 : 0.0;
            float s = (float)(bass + thump);
            for (int c = 0; c < channels; c++) buf[f * channels + c] = s;
        }
        return buf;
    }

    [Fact]
    public void AnalyzePcm_Is_Deterministic()
    {
        var pcm = SynthPcm(44100, 2, 2.0);

        var a = OfflineAudioAnalysis.AnalyzePcm(pcm, 44100, 2);
        var b = OfflineAudioAnalysis.AnalyzePcm(pcm, 44100, 2);

        Assert.True(a.IsActive);
        // Same samples -> same timeline -> identical frame at every probe time.
        for (double t = 0.0; t <= 2.0; t += 0.05)
        {
            var fa = a.SampleAt(t);
            var fb = b.SampleAt(t);
            Assert.Equal(fa.Bass, fb.Bass, precision: P);
            Assert.Equal(fa.Rms, fb.Rms, precision: P);
            Assert.Equal(fa.BeatPulse, fb.BeatPulse, precision: P);
            Assert.Equal(fa.DownbeatPulse, fb.DownbeatPulse, precision: P);
            Assert.Equal(fa.BpmPhaseSaw, fb.BpmPhaseSaw, precision: P);
            Assert.Equal(fa.Bpm, fb.Bpm, precision: P);
            Assert.Equal(fa.Transient, fb.Transient);
        }
    }

    [Fact]
    public void AnalyzePcm_Produces_Band_Energy()
    {
        var pcm = SynthPcm(44100, 2, 2.0);
        var src = OfflineAudioAnalysis.AnalyzePcm(pcm, 44100, 2);

        // A steady bass tone must show non-zero low-band energy somewhere.
        bool anyBass = false;
        for (double t = 0.2; t <= 2.0; t += 0.1)
            if (src.SampleAt(t).Bass > 0f) { anyBass = true; break; }
        Assert.True(anyBass);
    }

    [Fact]
    public void AnalyzeFile_Missing_Inputs_Return_Null()
    {
        Assert.Null(OfflineAudioAnalysis.AnalyzeFile("does-not-exist.mp3", "also-missing-ffmpeg.exe"));
        Assert.Null(OfflineAudioAnalysis.AnalyzeFile("", ""));
    }
}
