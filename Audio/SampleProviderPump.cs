// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/SampleProviderPump.cs
//
// #271 (parent #58) — shared real-time drain loop for an NAudio ISampleProvider,
// factored out of NoopAudioBackend so both the cross-platform fallback backend
// and OpenAlAudioBackend reuse one implementation for File + FractalSynth
// sources. (Mic / loopback in OpenAlAudioBackend use the ALC capture poll loop
// instead — a provider isn't involved there.)
//
// Pulls fixed-size chunks off the provider, sleeps just enough to match the
// source's real-time playback rate so the BeatAnalyzer onset detector receives
// samples at the cadence they were recorded, and raises data / end-of-stream /
// failure through caller-supplied delegates. Delegates fire on the pump thread;
// the caller marshals as needed.
//
// The loop owns a dedicated background thread (like OpenAlAudioBackend's capture
// poll) and paces on the cancellation wait handle. It used to run on the thread
// pool (Task.Run) and pace with Task.Delay(..).Wait(), whose timer completion
// also needs a pool thread, so a saturated pool (a busy render, a parallel test
// run) could stall the real-time feed for seconds.

using System;
using System.Threading;
using NAudio.Wave;

namespace FracturingFog.Audio
{
    internal sealed class SampleProviderPump : IDisposable
    {
        private const int ChunkFrames = 1024;

        private readonly ISampleProvider _source;
        private readonly AudioFormat _format;
        private readonly bool _fireEndOfStream;
        private readonly Action<ReadOnlyMemory<float>, AudioFormat> _onData;
        private readonly Action? _onEndOfStream;
        private readonly Action<Exception>? _onFailed;

        private CancellationTokenSource? _cts;
        private Thread? _thread;

        public SampleProviderPump(
            ISampleProvider source,
            bool fireEndOfStream,
            Action<ReadOnlyMemory<float>, AudioFormat> onData,
            Action? onEndOfStream,
            Action<Exception>? onFailed)
        {
            _source = source;
            _format = new AudioFormat(
                source.WaveFormat.SampleRate,
                source.WaveFormat.Channels,
                source.WaveFormat.BitsPerSample);
            _fireEndOfStream = fireEndOfStream;
            _onData = onData;
            _onEndOfStream = onEndOfStream;
            _onFailed = onFailed;
        }

        public void Start()
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _thread = new Thread(() => Run(cts.Token))
            {
                IsBackground = true,
                Name = "SampleProviderPump",
            };
            _thread.Start();
        }

        public void Stop()
        {
            var cts = _cts; _cts = null;
            var thread = _thread; _thread = null;
            try { cts?.Cancel(); } catch { }
            // A delegate may call Stop on the pump thread itself (e.g. from
            // onEndOfStream); joining it there would only wait out the timeout.
            if (thread != null && thread != Thread.CurrentThread)
            {
                try { thread.Join(TimeSpan.FromSeconds(1)); } catch { }
            }
            cts?.Dispose();
        }

        public void Dispose() => Stop();

        private void Run(CancellationToken ct)
        {
            int sampleCount = ChunkFrames * Math.Max(1, _format.Channels);
            var buf = new float[sampleCount];
            int msPerChunk = Math.Max(1, ChunkFrames * 1000 / Math.Max(1, _format.SampleRate));

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = _source.Read(buf, 0, buf.Length);
                    if (read <= 0)
                    {
                        if (_fireEndOfStream) _onEndOfStream?.Invoke();
                        return;
                    }
                    _onData(buf.AsMemory(0, read), _format);
                    // Signalled on cancel; times out to pace the next chunk.
                    if (ct.WaitHandle.WaitOne(msPerChunk)) return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _onFailed?.Invoke(ex); }
        }
    }
}
