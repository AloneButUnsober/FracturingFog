// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #271 (parent #58) — cross-platform live-audio (OpenAL) coverage.
//
// Environment-agnostic by design: the OpenAL native runtime may or may not load
// on a given CI leg, so these assert the *contract* around it (probe never
// throws + is idempotent, capability wiring, backend caps shape, the shared
// pump, and the Tier B election semantics) rather than a specific runtime value.
// The device-touching path is covered separately by the `--openalprobe` self-test.

using System;
using System.Threading;
using FracturingFog.Audio;
using FracturingFog.Models;
using NAudio.Wave;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class OpenAlAudioBackendTests
{
    // ── OpenAlRuntime probe ──────────────────────────────────────────────

    [Fact]
    public void OpenAlRuntime_IsAvailable_NeverThrows_AndIsStable()
    {
        bool first = OpenAlRuntime.IsAvailable();
        bool second = OpenAlRuntime.IsAvailable();
        Assert.Equal(first, second); // cached / stable within a call sequence
    }

    [Fact]
    public void OpenAlRuntime_Refresh_ReturnsSameAsIsAvailable()
    {
        bool refreshed = OpenAlRuntime.Refresh();
        Assert.Equal(refreshed, OpenAlRuntime.IsAvailable());
    }

    // ── capability probe matrix ──────────────────────────────────────────

    [Fact]
    public void CapabilityProbe_AlwaysIncludesFileAndSynth()
    {
        var caps = AudioCapabilityProbe.Detect();
        Assert.True((caps & AudioBackendCapabilities.FilePlayback) != 0);
        Assert.True((caps & AudioBackendCapabilities.SynthPlayback) != 0);
    }

    [Fact]
    public void CapabilityProbe_Windows_ReportsFullCaps()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-only assertion

        var caps = AudioCapabilityProbe.Detect();
        Assert.True((caps & AudioBackendCapabilities.SystemLoopback) != 0);
        Assert.True((caps & AudioBackendCapabilities.Microphone) != 0);
        Assert.True((caps & AudioBackendCapabilities.FilePlayback) != 0);
        Assert.True((caps & AudioBackendCapabilities.SynthPlayback) != 0);
    }

    [Fact]
    public void CapabilityProbe_NonWindows_LoopbackImpliesRuntimeAndLinux()
    {
        if (OperatingSystem.IsWindows()) return;

        var caps = AudioCapabilityProbe.Detect();
        // Loopback is only ever advertised on Linux with the runtime present.
        if ((caps & AudioBackendCapabilities.SystemLoopback) != 0)
        {
            Assert.True(OperatingSystem.IsLinux());
            Assert.True(OpenAlRuntime.IsAvailable());
        }
        // Mic is only advertised when the runtime is present.
        if ((caps & AudioBackendCapabilities.Microphone) != 0)
            Assert.True(OpenAlRuntime.IsAvailable());
    }

    // ── NoopAudioBackend caps floor ──────────────────────────────────────

    [Fact]
    public void NoopBackend_Caps_AreFileAndSynthOnly()
    {
        using var be = new NoopAudioBackend();
        Assert.Equal(
            AudioBackendCapabilities.FilePlayback | AudioBackendCapabilities.SynthPlayback,
            be.Capabilities);
    }

    [Fact]
    public void NoopBackend_LiveSources_ThrowNotSupported()
    {
        using var be = new NoopAudioBackend();
        Assert.Throws<NotSupportedException>(
            () => be.Start(AudioSourceKind.Microphone, AudioFormat.Default, null));
        Assert.Throws<NotSupportedException>(
            () => be.Start(AudioSourceKind.SystemLoopback, AudioFormat.Default, null));
    }

    // ── shared SampleProviderPump ────────────────────────────────────────

    [Fact]
    public void SampleProviderPump_EmitsData_ThenEndOfStream()
    {
        var source = new FiniteSampleProvider(totalFrames: 4096, sampleRate: 44100, channels: 1);
        long emitted = 0;
        var eos = new ManualResetEventSlim(false);

        var pump = new SampleProviderPump(
            source, fireEndOfStream: true,
            onData: (mem, _) => Interlocked.Add(ref emitted, mem.Length),
            onEndOfStream: () => eos.Set(),
            onFailed: ex => throw ex);

        pump.Start();
        Assert.True(eos.Wait(TimeSpan.FromSeconds(5)), "pump did not reach end-of-stream");
        pump.Stop();

        Assert.Equal(4096, Interlocked.Read(ref emitted));
    }

    // ── Tier B election semantics ────────────────────────────────────────

    [Fact]
    public void AudioRuntimeElection_SuppressPrompt_OnlyForManualOrSkip()
    {
        Assert.False(new AudioRuntimePreferences { Election = AudioRuntimeElection.None }.SuppressPrompt());
        Assert.True(new AudioRuntimePreferences { Election = AudioRuntimeElection.Manual }.SuppressPrompt());
        Assert.True(new AudioRuntimePreferences { Election = AudioRuntimeElection.Skip }.SuppressPrompt());
    }

    // ISampleProvider yielding a fixed number of frames, then 0 (EOS).
    private sealed class FiniteSampleProvider : ISampleProvider
    {
        private int _remaining;
        public WaveFormat WaveFormat { get; }

        public FiniteSampleProvider(int totalFrames, int sampleRate, int channels)
        {
            _remaining = totalFrames * channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _remaining);
            for (int i = 0; i < n; i++) buffer[offset + i] = 0f;
            _remaining -= n;
            return n;
        }
    }
}
