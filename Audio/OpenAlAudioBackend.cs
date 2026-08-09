// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/OpenAlAudioBackend.cs
//
// #271 (parent #58) — cross-platform live-audio capture via OpenAL (ALC_EXT_CAPTURE),
// closing the Linux/macOS microphone gap and (on Linux) the system-loopback gap
// that NoopAudioBackend leaves open.
//
//   Microphone     — alcCaptureOpenDevice(null, …): default capture device. Works
//                    on Linux (ALSA/PulseAudio/PipeWire) and macOS (CoreAudio).
//   SystemLoopback — Linux only. PulseAudio / PipeWire publish each output sink's
//                    monitor as a capture device named "<sink>.monitor"; we open
//                    the default sink's monitor. macOS has no native loopback
//                    (needs a virtual device such as BlackHole) so the flag is
//                    withheld there and the picker greys it.
//   File / Synth   — identical to NoopAudioBackend: decode / pull through
//                    SampleProviderPump. Lets one backend cover every source when
//                    the OpenAL runtime is present.
//
// Capture is poll-based (no callback in the ALC API): a dedicated thread drains
// alcGetAvailableSamples into a short buffer, converts PCM16 → float32 [-1,1],
// and raises DataAvailable, mirroring the WindowsNAudioBackend event contract.
//
// Selection: AvaloniaShellBootstrap.CreateAudioBackend picks this backend on
// non-Windows hosts when OpenAlRuntime.IsAvailable(); otherwise NoopAudioBackend.
// It is never selected on Windows (WindowsNAudioBackend/NAudio owns that path).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;

namespace FracturingFog.Audio
{
    public sealed unsafe class OpenAlAudioBackend : IAudioCaptureBackend
    {
        // Capture request format. OpenAL Soft resamples the hardware rate to this.
        private const int CaptureSampleRate = 44100;
        private const int CaptureChannels = 1;      // Mono16 request.
        private const int CaptureRingSamples = CaptureSampleRate; // 1s device ring.
        private const int DrainChunk = 2048;        // samples pulled per poll.
        private const int PollMs = 10;

        private readonly object _lock = new();

        private ALContext? _alc;
        private Capture? _capture;
        private readonly bool _hasMonitor;

        // Live capture (mic / loopback) state.
        private Device* _captureDevice;
        private Thread? _pollThread;
        private volatile bool _polling;

        // File / synth state (shared drain loop with NoopAudioBackend).
        private SampleProviderPump? _pump;
        private AudioFileReader? _fileReader;
        private ISampleProvider? _synthSource;

        private bool _disposed;

        public OpenAlAudioBackend()
        {
            // Bind the OpenAL API + capture extension once. Soft-name resolution
            // first (matches the bundled Silk.NET.OpenAL.Soft.Native asset); fall
            // back to the system soname. A failure here leaves _capture null and
            // the backend degrades to file/synth only — but CreateAudioBackend
            // gates on OpenAlRuntime.IsAvailable() so that is the rare race where
            // the lib vanished between probe and construction.
            foreach (bool soft in new[] { true, false })
            {
                try
                {
                    var alc = ALContext.GetApi(soft);
                    if (alc.TryGetExtension<Capture>((Device*)null, out var cap) && cap != null)
                    {
                        _alc = alc;
                        _capture = cap;
                        break;
                    }
                    alc.Dispose();
                }
                catch
                {
                    // try the next name container
                }
            }

            _hasMonitor = TryFindMonitorDevice() != null;
        }

        public AudioBackendCapabilities Capabilities
        {
            get
            {
                var caps = AudioBackendCapabilities.FilePlayback |
                           AudioBackendCapabilities.SynthPlayback;
                if (_capture != null)
                {
                    caps |= AudioBackendCapabilities.Microphone;
                    if (_hasMonitor) caps |= AudioBackendCapabilities.SystemLoopback;
                }
                return caps;
            }
        }

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
                if (_disposed) throw new ObjectDisposedException(nameof(OpenAlAudioBackend));
                if (IsRunning) return;

                switch (source)
                {
                    case AudioSourceKind.Microphone: StartCapture(null); break;
                    case AudioSourceKind.SystemLoopback: StartLoopback(); break;
                    case AudioSourceKind.File: StartFile(filePath); break;
                    case AudioSourceKind.FractalSynth: StartSynth(); break;
                    default: throw new NotSupportedException($"Unknown source '{source}'.");
                }
                IsRunning = true;
            }
        }

        public void Stop()
        {
            Thread? poll;
            SampleProviderPump? pump;
            lock (_lock)
            {
                if (!IsRunning) return;
                _polling = false;
                poll = _pollThread; _pollThread = null;
                pump = _pump; _pump = null;
                _fileReader?.Dispose();
                _fileReader = null;
                IsRunning = false;
            }
            pump?.Stop();
            try { poll?.Join(TimeSpan.FromSeconds(1)); } catch { }
            CloseCaptureDevice();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
            try { _alc?.Dispose(); } catch { }
            _alc = null;
            _capture = null;
        }

        // ── live capture (mic + loopback) ────────────────────────────────

        private void StartLoopback()
        {
            string? monitor = TryFindMonitorDevice();
            if (monitor == null)
                throw new NotSupportedException(
                    "System loopback is unavailable: no monitor capture device " +
                    "found (needs PulseAudio / PipeWire on Linux).");
            StartCapture(monitor);
        }

        private void StartCapture(string? deviceName)
        {
            var cap = _capture
                ?? throw new NotSupportedException("OpenAL capture extension is not available.");

            Device* dev = cap.CaptureOpenDevice(
                deviceName!, (uint)CaptureSampleRate, BufferFormat.Mono16, CaptureRingSamples);
            if (dev == null)
                throw new InvalidOperationException(
                    $"alcCaptureOpenDevice failed for '{deviceName ?? "(default)"}'.");

            _captureDevice = dev;
            cap.CaptureStart(dev);

            _polling = true;
            _pollThread = new Thread(() => PollLoop(cap, dev))
            {
                IsBackground = true,
                Name = "OpenAlCapture",
            };
            _pollThread.Start();
        }

        private void PollLoop(Capture cap, Device* dev)
        {
            var pcm = new short[DrainChunk];
            var floats = new float[DrainChunk];
            var fmt = new AudioFormat(CaptureSampleRate, CaptureChannels, 16);

            try
            {
                while (_polling)
                {
                    int avail = cap.GetAvailableSamples(dev);
                    if (avail >= DrainChunk)
                    {
                        fixed (short* p = pcm)
                            cap.CaptureSamples(dev, p, DrainChunk);
                        for (int i = 0; i < DrainChunk; i++)
                            floats[i] = pcm[i] / 32768f;
                        DataAvailable?.Invoke(floats.AsMemory(0, DrainChunk), fmt);
                    }
                    else
                    {
                        Thread.Sleep(PollMs);
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
            }
        }

        private void CloseCaptureDevice()
        {
            var cap = _capture;
            Device* dev = _captureDevice;
            _captureDevice = null;
            if (cap == null || dev == null) return;
            try { cap.CaptureStop(dev); } catch { }
            try { cap.CaptureCloseDevice(dev); } catch { }
        }

        /// <summary>
        /// Returns the default sink's monitor capture-device name, or the first
        /// device whose name contains ".monitor", or null when none exists
        /// (macOS / bare ALSA). Never throws.
        /// </summary>
        private string? TryFindMonitorDevice()
        {
            if (_alc is not ALContext alc) return null;
            try
            {
                if (!alc.TryGetExtension<CaptureEnumerationEnumeration>((Device*)null, out var en) || en == null)
                    return null;

                IEnumerable<string> devices =
                    en.GetStringList(Silk.NET.OpenAL.Extensions.EXT.Enumeration.GetCaptureContextStringList.CaptureDeviceSpecifiers);
                if (devices == null) return null;

                foreach (var d in devices)
                    if (!string.IsNullOrEmpty(d) &&
                        d.Contains(".monitor", StringComparison.OrdinalIgnoreCase))
                        return d;
            }
            catch
            {
                // Enumeration unsupported → treat as "no loopback".
            }
            return null;
        }

        // ── file / synth (shared pump, same as NoopAudioBackend) ─────────

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
            if (src == null) return; // driver may push via PushExternalSamples
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
                if (wasRunning) { _fileReader?.Dispose(); _fileReader = null; IsRunning = false; }
            }
            if (wasRunning) EndOfStream?.Invoke();
        }

        private void RaiseFailed(Exception ex)
        {
            bool wasRunning;
            lock (_lock)
            {
                wasRunning = IsRunning;
                if (wasRunning) { _fileReader?.Dispose(); _fileReader = null; IsRunning = false; }
            }
            if (wasRunning) Failed?.Invoke(ex);
        }
    }
}
