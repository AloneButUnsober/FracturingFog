// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SampleProviderPump ran its real-time loop on a thread-pool thread (Task.Run)
// and paced itself with Task.Delay(..).Wait(), whose timer completion also
// needs a pool thread. When the pool was saturated, the loop could not start or
// advance. The full test suite hit this (OpenAlAudioBackendTests'
// "pump did not reach end-of-stream" after 5 s), and so would live audio-reactive
// playback while a render saturates the pool. The pump now owns a dedicated
// thread and paces on the cancellation wait handle.
//
// This test saturates the pool on purpose, so it runs alone (non-parallel
// collection) and always releases the blocked work items.

using System;
using System.Threading;
using FracturingFog.Audio;
using NAudio.Wave;
using Xunit;

namespace FracturingFog.Server.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolStarvationCollection { public const string Name = "ThreadPoolStarvation"; }

[Collection(ThreadPoolStarvationCollection.Name)]
public sealed class SampleProviderPumpStarvationTests
{
    [Fact]
    public void Pump_Reaches_EndOfStream_While_The_ThreadPool_Is_Saturated()
    {
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        int blockers = minWorkers + 64;   // well past what the pool runs without injecting threads
        using var gate = new ManualResetEventSlim(false);
        for (int i = 0; i < blockers; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => gate.Wait(), null);

        try
        {
            var source = new FiniteSampleProvider(totalFrames: 4096, sampleRate: 44100, channels: 1);
            long emitted = 0;
            using var eos = new ManualResetEventSlim(false);
            using var pump = new SampleProviderPump(
                source, fireEndOfStream: true,
                onData: (mem, _) => Interlocked.Add(ref emitted, mem.Length),
                onEndOfStream: () => eos.Set(),
                onFailed: _ => { });

            pump.Start();
            // 4096 frames at 44.1 kHz is ~0.1 s of real-time pacing.
            Assert.True(eos.Wait(TimeSpan.FromSeconds(3)), "pump starved by a saturated thread pool");
            pump.Stop();
            Assert.Equal(4096, Interlocked.Read(ref emitted));
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public void Stop_From_The_EndOfStream_Callback_Does_Not_Hang()
    {
        // Backends may stop the pump from its own end-of-stream callback, which
        // runs on the pump thread. Stop must not wait for itself.
        var source = new FiniteSampleProvider(totalFrames: 1024, sampleRate: 44100, channels: 1);
        using var done = new ManualResetEventSlim(false);
        SampleProviderPump? pump = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        pump = new SampleProviderPump(
            source, fireEndOfStream: true,
            onData: (_, _) => { },
            onEndOfStream: () => { pump!.Stop(); done.Set(); },
            onFailed: _ => { });
        pump.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(3)), "end-of-stream never fired");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(0.9), $"Stop waited on its own thread ({elapsed.Elapsed})");
    }

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
            _remaining -= n;
            return n;
        }
    }
}
