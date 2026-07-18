// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Cross-platform fallback IAudioCaptureBackend. Supports file playback (via
    /// NAudio's AudioFileReader, which is managed-only for WAV/MP3/AIFF and so
    /// works on Linux/macOS) and fractal-synth pull (analyzer-only, no speaker
    /// output — the host has no portable speaker sink without WASAPI/CoreAudio).
    ///
    /// System loopback and microphone are intentionally unsupported: every
    /// cross-platform option (PulseAudio / PipeWire / CoreAudio HAL) needs a
    /// platform-specific addon and is tracked separately in the cross-platform
    /// roadmap. UI greys those source buttons when this backend is active.
    ///
    /// Phase X.B / Slice B.3.
    /// </summary>
    public sealed class NoopAudioBackend : IAudioCaptureBackend
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;
        private Task? _pumpTask;
        private AudioFileReader? _fileReader;
        private ISampleProvider? _synthSource;
        private bool _disposed;

        public AudioBackendCapabilities Capabilities =>
            AudioBackendCapabilities.FilePlayback |
            AudioBackendCapabilities.SynthPlayback;

        public bool IsRunning { get; private set; }

        public event Action<ReadOnlyMemory<float>, AudioFormat>? DataAvailable;
        public event Action<Exception>? Failed;
        public event Action? EndOfStream;

        /// <summary>
        /// Attach a fractal-synth sample source. Must be called before
        /// <see cref="Start"/>(<see cref="AudioSourceKind.FractalSynth"/>, …).
        /// </summary>
        public void AttachSynthSource(ISampleProvider source)
        {
            lock (_lock) { _synthSource = source; }
        }

        public void Start(AudioSourceKind source, AudioFormat preferredFormat, string? filePath)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NoopAudioBackend));
                if (IsRunning) return;

                switch (source)
                {
                    case AudioSourceKind.File: StartFile(filePath); break;
                    case AudioSourceKind.FractalSynth: StartSynth(); break;
                    case AudioSourceKind.SystemLoopback:
                    case AudioSourceKind.Microphone:
                        throw new NotSupportedException(
                            $"NoopAudioBackend does not support '{source}' on this platform.");
                    default:
                        throw new NotSupportedException($"Unknown source '{source}'.");
                }
                IsRunning = true;
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            Task? task;
            lock (_lock)
            {
                if (!IsRunning) return;
                cts = _cts; _cts = null;
                task = _pumpTask; _pumpTask = null;
                _fileReader?.Dispose();
                _fileReader = null;
                IsRunning = false;
            }
            try { cts?.Cancel(); } catch { }
            try { task?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            cts?.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
        }

        private void StartFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Audio file not found.", path ?? "(null)");

            _fileReader = new AudioFileReader(path);
            var reader = _fileReader;
            var format = new AudioFormat(reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample);
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpProvider(reader, format, _cts.Token, fireEndOfStream: true));
        }

        private void StartSynth()
        {
            var src = _synthSource;
            if (src == null)
            {
                // No attached source — the driver may feed samples via PushExternalSamples.
                // Nothing to pump here; remain "running" so the UI shows the engine as live.
                return;
            }
            var format = new AudioFormat(src.WaveFormat.SampleRate,
                src.WaveFormat.Channels, src.WaveFormat.BitsPerSample);
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpProvider(src, format, _cts.Token, fireEndOfStream: false));
        }

        /// <summary>
        /// Drains an ISampleProvider chunk-by-chunk into the DataAvailable event,
        /// sleeping just enough to match real-time playback so the analyzer's
        /// onset detector receives samples at the rate they were recorded.
        /// </summary>
        private void PumpProvider(ISampleProvider source, AudioFormat format,
            CancellationToken ct, bool fireEndOfStream)
        {
            const int ChunkFrames = 1024;
            int sampleCount = ChunkFrames * format.Channels;
            var buf = new float[sampleCount];
            int msPerChunk = Math.Max(1, ChunkFrames * 1000 / Math.Max(1, format.SampleRate));

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = source.Read(buf, 0, buf.Length);
                    if (read <= 0)
                    {
                        if (fireEndOfStream) RaiseEndOfStream();
                        return;
                    }
                    DataAvailable?.Invoke(buf.AsMemory(0, read), format);
                    try { Task.Delay(msPerChunk, ct).Wait(ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RaiseFailed(ex);
            }
        }

        private void RaiseEndOfStream()
        {
            bool wasRunning;
            lock (_lock)
            {
                wasRunning = IsRunning;
                if (wasRunning)
                {
                    _fileReader?.Dispose();
                    _fileReader = null;
                    IsRunning = false;
                }
            }
            if (wasRunning) EndOfStream?.Invoke();
        }

        private void RaiseFailed(Exception ex)
        {
            bool wasRunning;
            lock (_lock)
            {
                wasRunning = IsRunning;
                if (wasRunning)
                {
                    _fileReader?.Dispose();
                    _fileReader = null;
                    IsRunning = false;
                }
            }
            if (wasRunning) Failed?.Invoke(ex);
        }
    }
}
