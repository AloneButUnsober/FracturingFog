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

using System;
using System.Threading;
using System.Threading.Tasks;
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
        private Task? _task;

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
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => Run(_cts.Token));
        }

        public void Stop()
        {
            var cts = _cts; _cts = null;
            var task = _task; _task = null;
            try { cts?.Cancel(); } catch { }
            try { task?.Wait(TimeSpan.FromSeconds(1)); } catch { }
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
                    try { Task.Delay(msPerChunk, ct).Wait(ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _onFailed?.Invoke(ex); }
        }
    }
}
