// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/LiveRecordingEncoder.cs
//
// Instant record (#944): turns a LiveFrameRecorder intermediate (unique PNGs +
// per-frame hold manifest) into the format the user picked after Stop.
//
//   * ffmpeg present → one concat-demuxer pass: the manifest carries each
//     frame's hold, the fps filter resamples to a constant output rate, and an
//     optional lanczos scale shrinks it. Every format is available.
//   * ffmpeg absent  → MP4 through the host's native writer (Media Foundation
//     on Windows), GIF through the built-in GifSequenceWriter, PNG sequence by
//     file copy. H.265 / WebM / FFV1 need ffmpeg.
//
// Both paths share CfrSchedule, which maps variable per-frame holds onto a
// constant-rate timeline by cumulative rounding (no drift over long takes).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Render;

using SkiaSharp;

namespace FracturingFog.Imaging
{
    public static class LiveRecordingEncoder
    {
        public static string ExtensionFor(LiveRecordingFormat f) => f switch
        {
            LiveRecordingFormat.Mp4H264 => "mp4",
            LiveRecordingFormat.Mp4H265 => "mp4",
            LiveRecordingFormat.WebmVp9 => "webm",
            LiveRecordingFormat.MkvFfv1 => "mkv",
            LiveRecordingFormat.Gif => "gif",
            _ => "",
        };

        /// <summary>Formats with no non-ffmpeg fallback.</summary>
        public static bool RequiresFfmpeg(LiveRecordingFormat f) =>
            f is LiveRecordingFormat.Mp4H265 or LiveRecordingFormat.WebmVp9 or LiveRecordingFormat.MkvFfv1;

        /// <summary>Output dimensions for a scale percentage, forced even.</summary>
        public static (int W, int H) ScaledSize(int w, int h, int scalePercent)
        {
            int pct = Math.Clamp(scalePercent, 10, 100);
            int ow = Math.Max(2, (int)(w * pct / 100.0) & ~1);
            int oh = Math.Max(2, (int)(h * pct / 100.0) & ~1);
            return (ow, oh);
        }

        /// <summary>How many constant-rate output frames each captured frame
        /// occupies. Cumulative rounding keeps the total length exact; a frame
        /// shorter than one output period may get 0 (dropped), as a real
        /// constant-rate camera would.</summary>
        public static int[] CfrSchedule(IReadOnlyList<LiveRecordingFrame> frames, int fps)
        {
            var counts = new int[frames.Count];
            double t = 0;
            long prev = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                t += frames[i].Seconds;
                long edge = (long)Math.Round(t * fps, MidpointRounding.AwayFromZero);
                counts[i] = (int)(edge - prev);
                prev = edge;
            }
            // Never emit an empty video: show at least the last frame once.
            if (frames.Count > 0 && prev == 0) counts[^1] = 1;
            return counts;
        }

        /// <summary>ffmpeg argument string for a concat-manifest encode. Public
        /// for tests.</summary>
        public static string BuildFfmpegArgs(LiveRecordingResult rec, LiveRecordingExportOptions opt, string outputPath)
        {
            int fps = Math.Clamp(opt.Fps, 1, 120);
            var (ow, oh) = ScaledSize(rec.Width, rec.Height, opt.ScalePercent);
            string scale = (ow == rec.Width && oh == rec.Height)
                ? ""
                : $",scale={ow}:{oh}:flags=lanczos";
            string input = $"-y -hide_banner -f concat -safe 0 -i \"{Path.GetFileName(rec.ManifestPath)}\"";
            string vf = $"fps={fps.ToString(CultureInfo.InvariantCulture)}{scale}";

            string codec = opt.Format switch
            {
                LiveRecordingFormat.Mp4H264 => opt.Quality == LiveRecordingQuality.Lossless
                    ? "-c:v libx264 -preset veryslow -qp 0 -pix_fmt yuv444p -movflags +faststart"
                    : $"-c:v libx264 -preset slow -crf {Crf(opt.Quality, 18, 23, 28)} -pix_fmt yuv420p -movflags +faststart",
                LiveRecordingFormat.Mp4H265 => opt.Quality == LiveRecordingQuality.Lossless
                    ? "-c:v libx265 -preset slow -x265-params lossless=1 -pix_fmt yuv444p -tag:v hvc1 -movflags +faststart"
                    : $"-c:v libx265 -preset slow -crf {Crf(opt.Quality, 20, 26, 31)} -pix_fmt yuv420p -tag:v hvc1 -movflags +faststart",
                LiveRecordingFormat.WebmVp9 => opt.Quality == LiveRecordingQuality.Lossless
                    ? "-c:v libvpx-vp9 -lossless 1 -row-mt 1"
                    : $"-c:v libvpx-vp9 -b:v 0 -crf {Crf(opt.Quality, 24, 32, 40)} -row-mt 1 -pix_fmt yuv420p",
                LiveRecordingFormat.MkvFfv1 => "-c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slices 24 -slicecrc 1",
                LiveRecordingFormat.Gif => "-loop 0",
                _ => throw new ArgumentOutOfRangeException(nameof(opt), $"{opt.Format} is not an ffmpeg format."),
            };

            if (opt.Format == LiveRecordingFormat.Gif)
            {
                // Two-pass palette in one graph; diff stats favour the moving
                // parts, Low quality trades dither for smaller files.
                string dither = opt.Quality == LiveRecordingQuality.Low ? "bayer:bayer_scale=3" : "sierra2_4a";
                vf += $",split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither={dither}";
                return $"{input} -filter_complex \"{vf}\" {codec} \"{outputPath}\"";
            }
            return $"{input} -vf \"{vf}\" {codec} \"{outputPath}\"";
        }

        private static int Crf(LiveRecordingQuality q, int high, int med, int low) => q switch
        {
            LiveRecordingQuality.High => high,
            LiveRecordingQuality.Medium => med,
            _ => low,
        };

        /// <summary>Encode <paramref name="rec"/> to <paramref name="outputPath"/>
        /// (a folder for <see cref="LiveRecordingFormat.PngSequence"/>).
        /// <paramref name="nativeMp4Factory"/> is the host's (path, w, h) writer
        /// used for MP4 when ffmpeg is unavailable. Does not delete the
        /// intermediate — the caller owns it.</summary>
        public static async Task<(bool Ok, string Log)> EncodeAsync(
            LiveRecordingResult rec, LiveRecordingExportOptions opt, string outputPath,
            Func<string, int, int, IVideoWriter?>? nativeMp4Factory = null,
            bool ffmpegAllowed = true,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (rec.Frames.Count == 0) return (false, "Nothing was recorded.");

            if (opt.Format == LiveRecordingFormat.PngSequence)
                return await Task.Run(() => ExportPngSequence(rec, opt, outputPath, progress, ct), ct).ConfigureAwait(false);

            bool haveFfmpeg = ffmpegAllowed && FfmpegEncoder.IsAvailable();
            if (haveFfmpeg)
            {
                string args = BuildFfmpegArgs(rec, opt, outputPath);
                double total = Math.Max(rec.DurationSeconds, 0.001);
                return await FfmpegEncoder.RunAsync(args, rec.Folder, ct, line =>
                {
                    if (progress != null && TryParseFfmpegTime(line, out double secs))
                        progress.Report(Math.Clamp(secs / total, 0, 1));
                }).ConfigureAwait(false);
            }

            if (RequiresFfmpeg(opt.Format))
                return (false, $"{opt.Format} export needs ffmpeg. Install it from FFmpeg Setup, or pick MP4 (H.264), GIF or PNG sequence.");

            return await Task.Run(() => opt.Format == LiveRecordingFormat.Gif
                ? ExportViaWriter(rec, opt, progress, ct, variableRate: true,
                    (w, h) => new GifWriterAdapter(new GifSequenceWriter(outputPath, w, h)))
                : ExportViaWriter(rec, opt, progress, ct, variableRate: false,
                    (w, h) => nativeMp4Factory?.Invoke(outputPath, w, h)), ct)
                .ConfigureAwait(false);
        }

        // "frame=  120 fps= 60 ... time=00:00:04.00 bitrate=..."
        private static bool TryParseFfmpegTime(string line, out double seconds)
        {
            seconds = 0;
            int i = line.IndexOf("time=", StringComparison.Ordinal);
            if (i < 0) return false;
            int j = line.IndexOf(' ', i);
            string ts = j > i ? line.Substring(i + 5, j - i - 5) : line.Substring(i + 5);
            if (!TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var span)) return false;
            seconds = span.TotalSeconds;
            return true;
        }

        private static (bool, string) ExportPngSequence(
            LiveRecordingResult rec, LiveRecordingExportOptions opt, string folder,
            IProgress<double>? progress, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            var counts = CfrSchedule(rec.Frames, Math.Clamp(opt.Fps, 1, 120));
            var (ow, oh) = ScaledSize(rec.Width, rec.Height, opt.ScalePercent);
            bool scaled = ow != rec.Width || oh != rec.Height;
            int outIdx = 0, total = 0;
            foreach (int c in counts) total += c;

            for (int i = 0; i < rec.Frames.Count; i++)
            {
                if (counts[i] == 0) continue;
                ct.ThrowIfCancellationRequested();
                string src = Path.Combine(rec.Folder, rec.Frames[i].FileName);
                string first = Path.Combine(folder, $"frame_{outIdx + 1:D6}.png");
                if (scaled)
                {
                    using var bmp = SKBitmap.Decode(src) ?? throw new IOException($"Cannot read {src}");
                    using var resized = bmp.Resize(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Opaque),
                        new SKSamplingOptions(SKCubicResampler.Mitchell));
                    using var data = resized.Encode(SKEncodedImageFormat.Png, 100);
                    using var fs = File.Create(first);
                    data.SaveTo(fs);
                }
                else
                {
                    File.Copy(src, first, overwrite: true);
                }
                outIdx++;
                for (int k = 1; k < counts[i]; k++)
                    File.Copy(first, Path.Combine(folder, $"frame_{++outIdx:D6}.png"), overwrite: true);
                progress?.Report(outIdx / (double)Math.Max(1, total));
            }
            return (true, $"{outIdx} frames written to {folder}");
        }

        // variableRate: write each unique frame once at its real timestamp (GIF
        // carries per-frame delays, so CFR duplicates would only bloat it);
        // otherwise emit the constant-rate schedule (MP4).
        private static (bool, string) ExportViaWriter(
            LiveRecordingResult rec, LiveRecordingExportOptions opt,
            IProgress<double>? progress, CancellationToken ct, bool variableRate,
            Func<int, int, IVideoWriter?> make)
        {
            int fps = Math.Clamp(opt.Fps, 1, 120);
            var counts = CfrSchedule(rec.Frames, fps);
            var (ow, oh) = ScaledSize(rec.Width, rec.Height, opt.ScalePercent);
            using var writer = make(ow, oh);
            if (writer == null)
                return (false, "No video encoder available (native MP4 writer failed and ffmpeg is not installed).");

            if (variableRate)
            {
                double t = 0;
                for (int i = 0; i < rec.Frames.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    uint[] px = LoadBgra(Path.Combine(rec.Folder, rec.Frames[i].FileName), ow, oh);
                    writer.WriteFrame(px, (long)(t * TimeSpan.TicksPerSecond));
                    t += rec.Frames[i].Seconds;
                    progress?.Report((i + 1) / (double)rec.Frames.Count);
                }
                return (true, $"{rec.Frames.Count} frames encoded.");
            }

            int total = 0;
            foreach (int c in counts) total += c;
            long frameIdx = 0;
            for (int i = 0; i < rec.Frames.Count; i++)
            {
                if (counts[i] == 0) continue;
                ct.ThrowIfCancellationRequested();
                uint[] px = LoadBgra(Path.Combine(rec.Folder, rec.Frames[i].FileName), ow, oh);
                for (int k = 0; k < counts[i]; k++, frameIdx++)
                    writer.WriteFrame(px, frameIdx * TimeSpan.TicksPerSecond / fps);
                progress?.Report(frameIdx / (double)Math.Max(1, total));
            }
            return (true, $"{frameIdx} frames encoded.");
        }

        private static uint[] LoadBgra(string path, int w, int h)
        {
            using var decoded = SKBitmap.Decode(path) ?? throw new IOException($"Cannot read {path}");
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bmp = (decoded.Width == w && decoded.Height == h && decoded.ColorType == SKColorType.Bgra8888)
                ? decoded.Copy()
                : decoded.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
            var px = new uint[w * h];
            MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan()).Slice(0, px.Length).CopyTo(px);
            return px;
        }

        // GifSequenceWriter is not an IVideoWriter (it predates the contract);
        // adapt it so the fallback loop drives both the same way.
        private sealed class GifWriterAdapter : IVideoWriter
        {
            private readonly GifSequenceWriter _inner;
            public GifWriterAdapter(GifSequenceWriter inner) => _inner = inner;
            public int SourceWidth => _inner.Width;
            public int SourceHeight => _inner.Height;
            public int EncodedWidth => _inner.Width;
            public int EncodedHeight => _inner.Height;
            public void WriteFrame(uint[] bgra, long timestamp100ns) => _inner.WriteFrame(bgra, timestamp100ns);
            public void Dispose() => _inner.Dispose();
        }
    }
}
