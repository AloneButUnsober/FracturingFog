// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Backend-neutral driver that owns the active <see cref="IAudioCaptureBackend"/>,
    /// pipes captured float samples into a <see cref="BeatAnalyzer"/>, and exposes
    /// the analyzer as an <see cref="IBeatSource"/> to slideshow / video consumers.
    ///
    /// Phase X.B / Slice B.1: scaffolded alongside the legacy <see cref="AudioEngine"/>;
    /// the bootstrap continues to construct AudioEngine until B.2 lands the
    /// Windows NAudio backend and B.4 flips selection.
    ///
    /// Threading: Start/Stop/Reconfigure called from the UI thread.
    /// DataAvailable / Failed / EndOfStream fire on backend threads; the driver
    /// forwards into the analyzer (which is internally lock-guarded) and raises
    /// <see cref="Stopped"/> on the same thread.
    /// </summary>
    public sealed class AudioCaptureDriver : IDisposable
    {
        private readonly object _lock = new();
        private readonly IAudioCaptureBackend? _backend;
        private readonly BeatAnalyzer _analyzer;
        private AudioSettings _settings;
        private bool _disposed;

        public IBeatSource BeatSource => _analyzer;
        public AudioSettings Settings => _settings;
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Backend capability set, or <see cref="AudioBackendCapabilities.None"/>
        /// when running headless (no backend). UI layers grey-out sources missing
        /// from this flag set.
        /// </summary>
        public AudioBackendCapabilities Capabilities
            => _backend?.Capabilities ?? AudioBackendCapabilities.None;

        /// <summary>Raised when the engine stops on its own (EndOfStream or Failed).</summary>
        public event EventHandler? Stopped;

        /// <summary>Raised when the backend reports a non-recoverable error.</summary>
        public event EventHandler<Exception>? Failed;

        public AudioCaptureDriver(IAudioCaptureBackend? backend, AudioSettings? settings = null)
        {
            _backend = backend;
            _settings = settings ?? new AudioSettings();
            _analyzer = new BeatAnalyzer(sampleRate: 44100, channels: 2);
            _analyzer.Sensitivity = _settings.Sensitivity;
            if (_settings.BandWeights != null)
                _analyzer.SetBandWeights(_settings.BandWeights);

            if (_backend != null)
            {
                _backend.DataAvailable += OnBackendData;
                _backend.EndOfStream += OnBackendEndOfStream;
                _backend.Failed += OnBackendFailed;
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AudioCaptureDriver));
                if (IsRunning) return;
                if (_backend == null) return; // no platform support — pure analyzer-pull mode
                EnsureCapability(_settings.Source);
                _backend.Start(_settings.Source, AudioFormat.Default, _settings.FilePath);
                IsRunning = true;
            }
        }

        public void Stop()
        {
            bool fire = false;
            lock (_lock)
            {
                if (!IsRunning) return;
                try { _backend?.Stop(); } catch { }
                IsRunning = false;
                fire = true;
            }
            if (fire) Stopped?.Invoke(this, EventArgs.Empty);
        }

        public void Reconfigure(AudioSettings newSettings)
        {
            lock (_lock)
            {
                bool wasRunning = IsRunning;
                if (wasRunning) try { _backend?.Stop(); } catch { }
                _settings = newSettings;
                _analyzer.Sensitivity = newSettings.Sensitivity;
                if (newSettings.BandWeights != null)
                    _analyzer.SetBandWeights(newSettings.BandWeights);
                if (wasRunning && _backend != null)
                {
                    EnsureCapability(_settings.Source);
                    _backend.Start(_settings.Source, AudioFormat.Default, _settings.FilePath);
                }
                else if (!wasRunning)
                {
                    // remain stopped
                }
                else
                {
                    IsRunning = false;
                }
            }
        }

        /// <summary>
        /// Push externally generated PCM samples (e.g. from FractalSynth in closed-loop
        /// mode) directly into the analyzer. Interleaved float32 [-1, 1]. Safe to call
        /// whether or not <see cref="IsRunning"/> is true.
        /// </summary>
        public void PushExternalSamples(ReadOnlySpan<float> samples, int channels, int sampleRate)
        {
            _analyzer.EnsureFormat(sampleRate, channels);
            _analyzer.ProcessSamples(samples);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
            if (_backend != null)
            {
                _backend.DataAvailable -= OnBackendData;
                _backend.EndOfStream -= OnBackendEndOfStream;
                _backend.Failed -= OnBackendFailed;
                try { _backend.Dispose(); } catch { }
            }
        }

        private void EnsureCapability(AudioSourceKind source)
        {
            var need = source switch
            {
                AudioSourceKind.SystemLoopback => AudioBackendCapabilities.SystemLoopback,
                AudioSourceKind.Microphone => AudioBackendCapabilities.Microphone,
                AudioSourceKind.File => AudioBackendCapabilities.FilePlayback,
                AudioSourceKind.FractalSynth => AudioBackendCapabilities.SynthPlayback,
                _ => AudioBackendCapabilities.None,
            };
            if ((Capabilities & need) == 0)
                throw new NotSupportedException(
                    $"Audio backend lacks capability '{need}' required for source '{source}'.");
        }

        private void OnBackendData(ReadOnlyMemory<float> samples, AudioFormat format)
        {
            if (!format.IsValid) return;
            _analyzer.EnsureFormat(format.SampleRate, format.Channels);
            _analyzer.ProcessSamples(samples.Span);
        }

        private void OnBackendEndOfStream()
        {
            bool fire = false;
            lock (_lock)
            {
                if (!IsRunning) return;
                IsRunning = false;
                fire = true;
            }
            if (fire) Stopped?.Invoke(this, EventArgs.Empty);
        }

        private void OnBackendFailed(Exception ex)
        {
            bool fire = false;
            lock (_lock)
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    fire = true;
                }
            }
            Failed?.Invoke(this, ex);
            if (fire) Stopped?.Invoke(this, EventArgs.Empty);
        }
    }
}
