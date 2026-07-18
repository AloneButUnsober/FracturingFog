// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FracturingFog.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FracturingFog.Audio.Win
{
    /// <summary>
    /// Windows IAudioCaptureBackend backed by NAudio: WASAPI loopback for system
    /// audio, WaveInEvent for microphone, AudioFileReader + WaveOutEvent for file
    /// playback, and WaveOutEvent over a caller-supplied ISampleProvider for the
    /// fractal synth.
    ///
    /// Phase X.B / Slice B.2: extracted from the legacy AudioEngine.StartCore
    /// switch. Surface unchanged from the host's perspective — only the seam moves.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsNAudioBackend : IAudioCaptureBackend
    {
        private readonly object _lock = new();
        private IWaveIn? _waveIn;
        private WaveFormat? _waveInFormat;
        private WaveOutEvent? _filePlayer;
        private AudioFileReader? _fileReader;
        private WaveOutEvent? _synthPlayer;
        private SilentSynthPump? _silentPump;
        private ISampleProvider? _synthSource;
        private bool _routeSynthThroughAnalyzer = true;
        private bool _playSynthOutput = true;
        private bool _disposed;

        public AudioBackendCapabilities Capabilities =>
            AudioBackendCapabilities.SystemLoopback |
            AudioBackendCapabilities.Microphone |
            AudioBackendCapabilities.FilePlayback |
            AudioBackendCapabilities.SynthPlayback;

        public bool IsRunning { get; private set; }

        public event Action<ReadOnlyMemory<float>, AudioFormat>? DataAvailable;
        public event Action<Exception>? Failed;
        public event Action? EndOfStream;

        /// <summary>
        /// Attach a fractal-synth sample source. Must be called before
        /// <see cref="Start"/>(<see cref="AudioSourceKind.FractalSynth"/>, …).
        /// Re-attaching while the synth source is running restarts the player.
        /// </summary>
        public void AttachSynthSource(ISampleProvider source, bool routeThroughAnalyzer, bool playOutput)
        {
            lock (_lock)
            {
                _synthSource = source;
                _routeSynthThroughAnalyzer = routeThroughAnalyzer;
                _playSynthOutput = playOutput;
                if (IsRunning && _synthPlayer != null)
                {
                    StopSynthCore();
                    StartFractalSynth();
                }
            }
        }

        public void Start(AudioSourceKind source, AudioFormat preferredFormat, string? filePath)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WindowsNAudioBackend));
                if (IsRunning) return;
                try
                {
                    switch (source)
                    {
                        case AudioSourceKind.SystemLoopback: StartLoopback(); break;
                        case AudioSourceKind.Microphone: StartMicrophone(); break;
                        case AudioSourceKind.File: StartFile(filePath); break;
                        case AudioSourceKind.FractalSynth: StartFractalSynth(); break;
                        default: throw new NotSupportedException($"Unknown source '{source}'.");
                    }
                    IsRunning = true;
                }
                catch
                {
                    StopCore();
                    throw;
                }
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
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
        }

        private void StartLoopback()
        {
            var capture = new WasapiLoopbackCapture();
            _waveInFormat = capture.WaveFormat;
            capture.DataAvailable += OnWaveData;
            capture.RecordingStopped += OnRecordingStopped;
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
            _waveInFormat = mic.WaveFormat;
            mic.DataAvailable += OnWaveData;
            mic.RecordingStopped += OnRecordingStopped;
            mic.StartRecording();
            _waveIn = mic;
        }

        private void StartFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Audio file not found.", path ?? "(null)");

            _fileReader = new AudioFileReader(path);
            var tap = new EmitTapSampleProvider(_fileReader, EmitSamples);
            _filePlayer = new WaveOutEvent { DesiredLatency = 80 };
            _filePlayer.PlaybackStopped += OnFilePlaybackStopped;
            _filePlayer.Init(tap);
            _filePlayer.Play();
        }

        private void StartFractalSynth()
        {
            if (_synthSource == null) return; // caller will AttachSynthSource later

            ISampleProvider provider = _routeSynthThroughAnalyzer
                ? new EmitTapSampleProvider(_synthSource, EmitSamples)
                : _synthSource;

            if (_playSynthOutput)
            {
                _synthPlayer = new WaveOutEvent { DesiredLatency = 80 };
                _synthPlayer.Init(provider);
                _synthPlayer.Play();
            }
            else if (_routeSynthThroughAnalyzer)
            {
                _silentPump = new SilentSynthPump(provider);
                _silentPump.Start();
            }
        }

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
                if (_waveIn is WasapiLoopbackCapture w) w.RecordingStopped -= OnRecordingStopped;
                if (_waveIn is WaveInEvent we) we.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
                _waveInFormat = null;
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

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                bool wasRunning;
                lock (_lock)
                {
                    wasRunning = IsRunning;
                    if (wasRunning) { StopCore(); IsRunning = false; }
                }
                if (wasRunning) Failed?.Invoke(e.Exception);
            }
        }

        private void OnFilePlaybackStopped(object? sender, StoppedEventArgs e)
        {
            bool wasRunning;
            lock (_lock)
            {
                wasRunning = IsRunning;
                if (wasRunning) { StopCore(); IsRunning = false; }
            }
            if (!wasRunning) return;
            if (e.Exception != null) Failed?.Invoke(e.Exception);
            else EndOfStream?.Invoke();
        }

        private void OnWaveData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            var fmt = _waveInFormat;
            if (fmt == null) return;
            int bytesPerFrame = fmt.Channels * fmt.BitsPerSample / 8;
            if (bytesPerFrame <= 0) return;
            int frames = e.BytesRecorded / bytesPerFrame;
            if (frames <= 0) return;

            int sampleCount = frames * fmt.Channels;
            var floats = new float[sampleCount];
            if (!ConvertRawToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, floats))
                return; // unsupported encoding — drop frame

            var audioFmt = new AudioFormat(fmt.SampleRate, fmt.Channels, fmt.BitsPerSample);
            DataAvailable?.Invoke(floats.AsMemory(0, sampleCount), audioFmt);
        }

        private void EmitSamples(ReadOnlySpan<float> samples, WaveFormat fmt)
        {
            if (samples.Length == 0) return;
            var copy = samples.ToArray();
            var audioFmt = new AudioFormat(fmt.SampleRate, fmt.Channels, fmt.BitsPerSample);
            DataAvailable?.Invoke(copy.AsMemory(), audioFmt);
        }

        /// <summary>
        /// Decode the WASAPI / WaveIn byte buffer into the supplied interleaved
        /// float32 destination. Returns false for unsupported encodings.
        /// </summary>
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

        /// <summary>
        /// ISampleProvider that copies each read block out via a delegate (DataAvailable
        /// emit) before passing it through to the next stage (so playback continues).
        /// </summary>
        private sealed class EmitTapSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly Action<ReadOnlySpan<float>, WaveFormat> _emit;

            public EmitTapSampleProvider(ISampleProvider source, Action<ReadOnlySpan<float>, WaveFormat> emit)
            {
                _source = source;
                _emit = emit;
            }

            public WaveFormat WaveFormat => _source.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read > 0) _emit(buffer.AsSpan(offset, read), _source.WaveFormat);
                return read;
            }
        }

        /// <summary>
        /// Pulls samples from an ISampleProvider on a timer and discards them.
        /// Used when the synth feeds the analyzer but the user has muted speakers.
        /// </summary>
        private sealed class SilentSynthPump
        {
            private readonly ISampleProvider _source;
            private readonly System.Threading.Timer _timer;
            private readonly float[] _buf;
            private const int ChunkFrames = 1024;

            public SilentSynthPump(ISampleProvider source)
            {
                _source = source;
                _buf = new float[ChunkFrames * source.WaveFormat.Channels];
                _timer = new System.Threading.Timer(_ => Drain(), null,
                    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }

            public void Start()
            {
                int periodMs = (int)Math.Max(10, ChunkFrames * 1000L / _source.WaveFormat.SampleRate);
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
    }
}
