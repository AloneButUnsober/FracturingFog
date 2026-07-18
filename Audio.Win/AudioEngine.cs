// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace FracturingFog.Audio
{
    [SupportedOSPlatform("windows")]
    /// <summary>
    /// Owns the active capture source (loopback / file / mic / fractal synth) and
    /// pushes captured PCM samples into a <see cref="BeatAnalyzer"/>. Exposes the
    /// analyzer as an <see cref="IBeatSource"/> for slideshow consumption.
    ///
    /// Thread-safety: Start/Stop/Reconfigure must be called from the UI thread.
    /// Beat/Downbeat events fire on capture threads — consumers must marshal to
    /// the UI thread themselves.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        private readonly object _lock = new();
        private IWaveIn? _waveIn;
        private WaveOutEvent? _filePlayer;
        private AudioFileReader? _fileReader;
        private WaveOutEvent? _synthPlayer;
        private FractalSynth? _synth;
        private BeatAnalyzer _analyzer;
        private AudioSettings _settings;
        private bool _disposed;

        public IBeatSource BeatSource => _analyzer;
        public AudioSettings Settings => _settings;
        public bool IsRunning { get; private set; }

        /// <summary>Raised when the engine stops on its own (e.g. file playback finished).</summary>
        public event EventHandler? Stopped;

        public AudioEngine(AudioSettings? settings = null)
        {
            _settings = settings ?? new AudioSettings();
            _analyzer = new BeatAnalyzer(sampleRate: 44100, channels: 2);
            _analyzer.Sensitivity = _settings.Sensitivity;
            if (_settings.BandWeights != null)
                _analyzer.SetBandWeights(_settings.BandWeights);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AudioEngine));
                if (IsRunning) return;
                StartCore();
                IsRunning = true;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!IsRunning) return;
                StopCore();
                IsRunning = false;
            }
            Stopped?.Invoke(this, EventArgs.Empty);
        }

        public void Reconfigure(AudioSettings newSettings)
        {
            lock (_lock)
            {
                bool wasRunning = IsRunning;
                if (wasRunning) StopCore();
                _settings = newSettings;
                _analyzer.Sensitivity = newSettings.Sensitivity;
                if (newSettings.BandWeights != null)
                    _analyzer.SetBandWeights(newSettings.BandWeights);
                if (wasRunning)
                {
                    StartCore();
                }
            }
        }

        /// <summary>
        /// Push externally generated PCM samples (e.g. from FractalSynth in closed-loop mode)
        /// directly into the analyzer. Interleaved float32 [-1,1].
        /// </summary>
        public void PushSynthSamples(ReadOnlySpan<float> samples, int channels, int sampleRate)
        {
            if (!IsRunning) return;
            _analyzer.EnsureFormat(sampleRate, channels);
            _analyzer.ProcessSamples(samples);
        }

        private void StartCore()
        {
            switch (_settings.Source)
            {
                case AudioSourceKind.SystemLoopback:
                    StartLoopback();
                    break;
                case AudioSourceKind.Microphone:
                    StartMicrophone();
                    break;
                case AudioSourceKind.File:
                    StartFile();
                    break;
                case AudioSourceKind.FractalSynth:
                    StartFractalSynth();
                    break;
            }
        }

        /// <summary>
        /// Attach (or replace) the active fractal synth instance. Used when the
        /// source is FractalSynth — owner provides the configured synth so the
        /// engine can route its output to the analyzer / speakers.
        /// </summary>
        public void AttachSynth(FractalSynth synth)
        {
            lock (_lock)
            {
                _synth = synth;
                if (IsRunning && _settings.Source == AudioSourceKind.FractalSynth)
                {
                    StopSynthCore();
                    StartFractalSynth();
                }
            }
        }

        private void StartFractalSynth()
        {
            if (_synth == null)
            {
                // Caller hasn't supplied a synth yet — we'll wait for AttachSynth.
                _analyzer.EnsureFormat(44100, 2);
                return;
            }
            _analyzer.EnsureFormat(_synth.WaveFormat.SampleRate, _synth.WaveFormat.Channels);
            ISampleProvider provider = _settings.RouteSynthThroughAnalyzer
                ? new AnalyzerTapSampleProvider(_synth, _analyzer)
                : _synth;
            if (_settings.PlaySynthOutput)
            {
                _synthPlayer = new WaveOutEvent { DesiredLatency = 80 };
                _synthPlayer.Init(provider);
                _synthPlayer.Play();
            }
            else if (_settings.RouteSynthThroughAnalyzer)
            {
                // No speaker output, but we still need to pull samples to feed
                // the analyzer. Spin up a silent consumer.
                var pump = new SilentPump(provider);
                pump.Start();
                _silentPump = pump;
            }
        }

        private SilentPump? _silentPump;

        private void StopSynthCore()
        {
            if (_synthPlayer != null)
            {
                try { _synthPlayer.Stop(); } catch { }
                _synthPlayer.Dispose();
                _synthPlayer = null;
            }
            if (_silentPump != null)
            {
                _silentPump.Stop();
                _silentPump = null;
            }
        }

        private void StopCore()
        {
            if (_waveIn != null)
            {
                try { _waveIn.StopRecording(); } catch { }
                _waveIn.DataAvailable -= OnWaveData;
                _waveIn.Dispose();
                _waveIn = null;
            }
            if (_filePlayer != null)
            {
                try { _filePlayer.Stop(); } catch { }
                _filePlayer.PlaybackStopped -= OnFilePlaybackStopped;
                _filePlayer.Dispose();
                _filePlayer = null;
            }
            if (_fileReader != null)
            {
                _fileReader.Dispose();
                _fileReader = null;
            }
            StopSynthCore();
        }

        private void StartLoopback()
        {
            var capture = new WasapiLoopbackCapture();
            _analyzer.EnsureFormat(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
            capture.DataAvailable += OnWaveData;
            capture.StartRecording();
            _waveIn = capture;
        }

        private void StartMicrophone()
        {
            var mic = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 30,
            };
            _analyzer.EnsureFormat(44100, 1);
            mic.DataAvailable += OnWaveData;
            mic.StartRecording();
            _waveIn = mic;
        }

        private void StartFile()
        {
            var path = _settings.FilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Audio file not found.", path ?? "(null)");

            _fileReader = new AudioFileReader(path);
            _analyzer.EnsureFormat(_fileReader.WaveFormat.SampleRate, _fileReader.WaveFormat.Channels);

            // Tap that copies samples into the analyzer before they reach the speakers.
            var tap = new AnalyzerTapSampleProvider(_fileReader, _analyzer);
            _filePlayer = new WaveOutEvent { DesiredLatency = 80 };
            _filePlayer.PlaybackStopped += OnFilePlaybackStopped;
            _filePlayer.Init(tap);
            _filePlayer.Play();
        }

        private void OnFilePlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lock)
            {
                if (!IsRunning) return;
                StopCore();
                IsRunning = false;
            }
            Stopped?.Invoke(this, EventArgs.Empty);
        }

        private void OnWaveData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            var fmt = _waveIn?.WaveFormat;
            if (fmt == null) return;
            int bytesPerFrame = fmt.Channels * fmt.BitsPerSample / 8;
            if (bytesPerFrame <= 0) return;
            int frames = e.BytesRecorded / bytesPerFrame;
            if (frames <= 0) return;
            int sampleCount = frames * fmt.Channels;
            // Slice B.6: byte → float conversion lives here now that
            // BeatAnalyzer.ProcessRawBytes (NAudio.Wave.WaveFormat shape) is gone.
            // ConvertRawToFloat mirrors WindowsNAudioBackend's helper.
            var floats = new float[sampleCount];
            if (!ConvertRawToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, floats)) return;
            _analyzer.EnsureFormat(fmt.SampleRate, fmt.Channels);
            _analyzer.ProcessSamples(floats.AsSpan(0, sampleCount));
        }

        private static bool ConvertRawToFloat(ReadOnlySpan<byte> bytes, WaveFormat fmt, Span<float> dest)
        {
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            {
                var src = MemoryMarshal.Cast<byte, float>(bytes);
                src.Slice(0, Math.Min(src.Length, dest.Length)).CopyTo(dest);
                return true;
            }
            if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                var src = MemoryMarshal.Cast<byte, short>(bytes);
                int n = Math.Min(src.Length, dest.Length);
                for (int i = 0; i < n; i++) dest[i] = src[i] / 32768f;
                return true;
            }
            if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 32)
            {
                var src = MemoryMarshal.Cast<byte, int>(bytes);
                int n = Math.Min(src.Length, dest.Length);
                for (int i = 0; i < n; i++) dest[i] = src[i] / (float)int.MaxValue;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
        }
    }

    /// <summary>
    /// Pulls samples from an <see cref="ISampleProvider"/> on a background timer
    /// and discards them. Used when the user wants synth → analyzer routing
    /// without speaker output (silent closed-loop sync).
    /// </summary>
    internal sealed class SilentPump
    {
        private readonly ISampleProvider _source;
        private readonly System.Threading.Timer _timer;
        private readonly float[] _buf;
        private const int ChunkFrames = 1024;

        public SilentPump(ISampleProvider source)
        {
            _source = source;
            _buf = new float[ChunkFrames * source.WaveFormat.Channels];
            _timer = new System.Threading.Timer(_ => Drain(), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        public void Start()
        {
            // ~23 ms per chunk at 44.1kHz / 1024 frames.
            int periodMs = (int)System.Math.Max(10, ChunkFrames * 1000L / _source.WaveFormat.SampleRate);
            _timer.Change(0, periodMs);
        }

        public void Stop()
        {
            _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _timer.Dispose();
        }

        private void Drain()
        {
            try { _source.Read(_buf, 0, _buf.Length); } catch { }
        }
    }

    /// <summary>
    /// ISampleProvider that copies each read block into the analyzer before
    /// passing it through to the next stage (so the user still hears the file
    /// while we analyze it).
    /// </summary>
    internal sealed class AnalyzerTapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly BeatAnalyzer _analyzer;

        public AnalyzerTapSampleProvider(ISampleProvider source, BeatAnalyzer analyzer)
        {
            _source = source;
            _analyzer = analyzer;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                _analyzer.ProcessSamples(buffer.AsSpan(offset, read));
            }
            return read;
        }
    }
}
