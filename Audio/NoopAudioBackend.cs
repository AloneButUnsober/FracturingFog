// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using NAudio.Wave;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Cross-platform fallback IAudioCaptureBackend. Supports file playback (via
    /// NAudio's AudioFileReader, which is managed-only for WAV/MP3/AIFF and so
    /// works on Linux/macOS) and fractal-synth pull (analyzer-only, no speaker
    /// output — the host has no portable speaker sink without WASAPI/CoreAudio).
    ///
    /// System loopback and microphone are intentionally unsupported here. When
    /// the OpenAL runtime is present, AvaloniaShellBootstrap selects
    /// <see cref="OpenAlAudioBackend"/> (mic everywhere + monitor loopback on
    /// Linux) instead; this backend is the floor for hosts with no OpenAL
    /// runtime. UI greys those source buttons when this backend is active.
    ///
    /// Phase X.B / Slice B.3. #271 — drain loop factored into
    /// <see cref="SampleProviderPump"/>, shared with OpenAlAudioBackend.
    /// </summary>
    public sealed class NoopAudioBackend : IAudioCaptureBackend
    {
        private readonly object _lock = new();
        private SampleProviderPump? _pump;
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
            SampleProviderPump? pump;
            lock (_lock)
            {
                if (!IsRunning) return;
                pump = _pump; _pump = null;
                _fileReader?.Dispose();
                _fileReader = null;
                IsRunning = false;
            }
            pump?.Stop();
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
            _pump = new SampleProviderPump(
                _fileReader, fireEndOfStream: true,
                onData: (mem, fmt) => DataAvailable?.Invoke(mem, fmt),
                onEndOfStream: RaiseEndOfStream,
                onFailed: RaiseFailed);
            _pump.Start();
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
            _pump = new SampleProviderPump(
                src, fireEndOfStream: false,
                onData: (mem, fmt) => DataAvailable?.Invoke(mem, fmt),
                onEndOfStream: null,
                onFailed: RaiseFailed);
            _pump.Start();
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
