// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/LiveFrameRecorder.cs
//
// Instant record (#944): samples "whatever the render window is showing" at a
// fixed capture rate into a lossless temp intermediate.
//
// A dedicated pump thread ticks at the capture rate. Each tick it checks the
// producer's frame version (bumped by NotifyFrameChanged on every present); if
// the display changed it snapshots the frame and queues a fast PNG encode,
// otherwise the previous frame's on-screen hold simply grows. So a static view
// costs nothing to record, bursty animation keeps wall-clock-exact timing, and
// the final export (format / quality / fps) is chosen after Stop.
//
// Output: unique-frame PNGs (f_000001.png …) + frames.ffconcat (ffmpeg concat
// demuxer manifest carrying each frame's hold) in a temp folder.
//
// Backpressure: when PNG encodes fall behind (4K at 60 fps on a slow disk), a
// sample is skipped rather than blocking — the pending change is picked up on a
// later tick and the previous frame's hold absorbs the gap.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Render;

using SkiaSharp;

namespace FracturingFog.Imaging
{
    public sealed class LiveFrameRecorder : IDisposable
    {
        /// <summary>Returns a caller-owned copy of the presented BGRA frame, or
        /// an empty array / null when nothing is presented.</summary>
        public delegate uint[]? FrameSource(out int width, out int height);

        public const int MinFps = 1;
        public const int MaxFps = 60;
        private const int MaxPendingWrites = 6;
        public const string ManifestFileName = "frames.ffconcat";

        private readonly string _folder;
        private readonly int _fps;
        private readonly FrameSource _source;
        private readonly object _lock = new();
        private readonly List<(string File, long StartTicks)> _entries = new();
        private readonly Stopwatch _clock = new();
        private readonly ManualResetEventSlim _stopSignal = new(false);
        private readonly CountdownEvent _pending = new(1);
        private Thread? _pump;
        private long _version = 1;      // start "dirty" so the first tick captures
        private long _seenVersion;
        private int _width, _height;
        private int _inFlight;
        private bool _stopped;
        private bool _finalized;
        private Exception? _writeFault;

        public LiveFrameRecorder(string folder, int fps, FrameSource source)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _fps = Math.Clamp(fps, MinFps, MaxFps);
            Directory.CreateDirectory(folder);
        }

        public string Folder => _folder;
        public int CaptureFps => _fps;
        public TimeSpan Elapsed => _clock.Elapsed;
        public int FrameCount { get { lock (_lock) return _entries.Count; } }

        /// <summary>Mark the presented frame as changed. Cheap; call from the
        /// producer's present path.</summary>
        public void NotifyFrameChanged() => Interlocked.Increment(ref _version);

        public void Start()
        {
            if (_pump != null) throw new InvalidOperationException("Already started.");
            _clock.Start();
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "LiveFrameRecorder" };
            _pump.Start();
        }

        /// <summary>Test seam: sample once at an explicit timeline position
        /// instead of running the pump thread.</summary>
        public void SampleAt(long ticks) => Sample(ticks);

        private void PumpLoop()
        {
            long period = TimeSpan.TicksPerSecond / _fps;
            long next = 0;
            while (!_stopSignal.IsSet)
            {
                Sample(_clock.Elapsed.Ticks);
                next += period;
                long now = _clock.Elapsed.Ticks;
                if (next < now) next = now; // fell behind — don't burst to catch up
                int waitMs = (int)((next - now) / TimeSpan.TicksPerMillisecond);
                if (waitMs > 0) _stopSignal.Wait(waitMs);
            }
        }

        private void Sample(long ticks)
        {
            long v = Interlocked.Read(ref _version);
            if (v == _seenVersion) return;                     // unchanged → hold grows
            if (Volatile.Read(ref _inFlight) >= MaxPendingWrites) return; // backpressure

            uint[]? frame;
            int w, h;
            try { frame = _source(out w, out h); }
            catch { return; }
            if (frame == null || w < 2 || h < 2 || frame.Length < w * h) return;
            _seenVersion = v;

            if (_width == 0) { _width = w & ~1; _height = h & ~1; }
            uint[] pixels = FitOpaque(frame, w, h, _width, _height);

            string file;
            lock (_lock)
            {
                if (_stopped) return;
                file = $"f_{_entries.Count + 1:D6}.png";
                _entries.Add((file, ticks));
            }

            string path = Path.Combine(_folder, file);
            Interlocked.Increment(ref _inFlight);
            _pending.AddCount();
            Task.Run(() =>
            {
                try { SavePngFast(pixels, _width, _height, path); }
                catch (Exception ex) { _writeFault ??= ex; }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    _pending.Signal();
                }
            });
        }

        /// <summary>Stop sampling, wait for queued PNGs, write the manifest.
        /// Returns null (and deletes the folder) when nothing was captured.</summary>
        public LiveRecordingResult? Stop() => StopAt(null);

        public LiveRecordingResult? StopAt(long? endTicksOverride)
        {
            if (_finalized) throw new InvalidOperationException("Already stopped.");
            _finalized = true;
            _stopSignal.Set();
            _pump?.Join();
            long end = endTicksOverride ?? _clock.Elapsed.Ticks;
            _clock.Stop();

            List<(string File, long StartTicks)> entries;
            lock (_lock) { _stopped = true; entries = new(_entries); }

            _pending.Signal();   // release the initial count
            _pending.Wait();

            if (entries.Count == 0 || _writeFault != null)
            {
                TryDeleteFolder();
                if (_writeFault != null)
                    throw new IOException($"Live recording frame write failed: {_writeFault.Message}", _writeFault);
                return null;
            }

            var frames = new List<LiveRecordingFrame>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                long stop = i + 1 < entries.Count ? entries[i + 1].StartTicks : Math.Max(end, entries[i].StartTicks);
                double secs = (stop - entries[i].StartTicks) / (double)TimeSpan.TicksPerSecond;
                // A frame always shows for at least one capture period.
                frames.Add(new LiveRecordingFrame { FileName = entries[i].File, Seconds = Math.Max(secs, 1.0 / _fps) });
            }

            string manifest = Path.Combine(_folder, ManifestFileName);
            File.WriteAllText(manifest, BuildConcatManifest(frames), new UTF8Encoding(false));

            double total = 0;
            foreach (var f in frames) total += f.Seconds;
            return new LiveRecordingResult
            {
                Folder = _folder,
                ManifestPath = manifest,
                Frames = frames,
                Width = _width,
                Height = _height,
                CaptureFps = _fps,
                DurationSeconds = total,
            };
        }

        /// <summary>ffmpeg concat-demuxer script. The last file is listed twice:
        /// the demuxer ignores the final entry's duration otherwise.</summary>
        public static string BuildConcatManifest(IReadOnlyList<LiveRecordingFrame> frames)
        {
            var sb = new StringBuilder("ffconcat version 1.0\n");
            foreach (var f in frames)
            {
                sb.Append("file '").Append(f.FileName).Append("'\n");
                sb.Append("duration ").Append(f.Seconds.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
            }
            if (frames.Count > 0)
                sb.Append("file '").Append(frames[^1].FileName).Append("'\n");
            return sb.ToString();
        }

        // The render window presents opaquely (alpha ignored), so force A=255 to
        // record exactly what is shown. A mid-recording resize (window drag,
        // fullscreen toggle) is nearest-resampled to the first frame's size so
        // the sequence stays encodable at one resolution.
        public static uint[] FitOpaque(uint[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new uint[dw * dh];
            if (sw >= dw && sh >= dh && sw - dw <= 1 && sh - dh <= 1)
            {
                for (int y = 0; y < dh; y++)
                {
                    int so = y * sw, d0 = y * dw;
                    for (int x = 0; x < dw; x++) dst[d0 + x] = src[so + x] | 0xFF000000u;
                }
                return dst;
            }
            for (int y = 0; y < dh; y++)
            {
                int sy = (int)((long)y * sh / dh);
                int so = sy * sw, d0 = y * dw;
                for (int x = 0; x < dw; x++)
                    dst[d0 + x] = src[so + (int)((long)x * sw / dw)] | 0xFF000000u;
            }
            return dst;
        }

        // Speed over size: the intermediate is temporary, so zlib level 1 with
        // the Sub filter keeps a 1080p encode in the low milliseconds.
        private static unsafe void SavePngFast(uint[] pixels, int w, int h, string path)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            fixed (uint* p = pixels)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)p, info.RowBytes);
                var opts = new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1);
                using var data = pixmap.Encode(opts);
                if (data == null) throw new IOException("PNG encode failed.");
                using var fs = File.Create(path);
                data.SaveTo(fs);
            }
        }

        private void TryDeleteFolder()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch { }
        }

        /// <summary>Abort without producing a result (host teardown).</summary>
        public void Dispose()
        {
            if (!_finalized)
            {
                try { StopAt(null); } catch { }
                TryDeleteFolder();
            }
            _stopSignal.Dispose();
        }
    }
}
