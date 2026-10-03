// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/GifFolderEncoder.cs
//
// #1053 — encode a PngSequenceWriter frame folder (frame_000001.png …) into one
// looping animated GIF, without ffmpeg. The offline exporters (scene video,
// batch slideshow) already render to a PNG sequence and hand that folder to
// ffmpeg; this is the GIF leg of the same "folder → container" step, so a GIF
// export reuses every render path unchanged.
//
// Frame timing: GIF delays are whole centiseconds, so 1/fps rarely divides
// evenly (30 fps = 3.33 cs). Frame i is stamped at round(i·100/fps) cs on a
// centisecond grid; GifSequenceWriter derives each delay from consecutive
// stamps, so delays alternate (3,3,4,…) and the total runtime matches the
// render instead of drifting. GIF can't go below 2 cs per frame (browsers
// treat smaller as 10 cs), so above 50 fps playback is slower than the render.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using SkiaSharp;

namespace FracturingFog.Imaging
{
    public static class GifFolderEncoder
    {
        /// <summary>Highest frame rate a GIF plays back faithfully (2 cs minimum delay).</summary>
        public const int MaxFaithfulFps = 50;

        /// <summary>Timestamp (100-ns ticks) of frame <paramref name="index"/> on the
        /// centisecond grid — see the file header.</summary>
        public static long FrameTimestamp(int index, double fps)
            => (long)Math.Round(index * 100.0 / fps) * 100_000L;

        /// <summary>Encode every <c>frame_*.png</c> in <paramref name="pngFolder"/>, in
        /// name order, into <paramref name="outPath"/>. Returns (false, reason) when the
        /// folder has no frames, a frame fails to decode / changes size, or the encoder
        /// faults; the PNG folder is never touched.</summary>
        public static (bool Ok, string Log) Encode(string pngFolder, string outPath, double fps,
            CancellationToken ct = default)
        {
            if (fps <= 0) return (false, "GIF encode: fps must be > 0.");
            var frames = Directory.Exists(pngFolder)
                ? Directory.GetFiles(pngFolder, "frame_*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            if (frames.Length == 0) return (false, $"GIF encode: no frame_*.png in {pngFolder}.");

            GifSequenceWriter? gif = null;
            try
            {
                int defaultDelayCs = (int)Math.Max(2, Math.Round(100.0 / fps));
                for (int i = 0; i < frames.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (pixels, w, h) = DecodeBgra(frames[i]);
                    if (pixels == null) return (false, $"GIF encode: could not decode {Path.GetFileName(frames[i])}.");
                    gif ??= new GifSequenceWriter(outPath, w, h, defaultDelayCs);
                    if (w != gif.Width || h != gif.Height)
                        return (false, $"GIF encode: {Path.GetFileName(frames[i])} is {w}x{h}, expected {gif.Width}x{gif.Height}.");
                    gif.WriteFrame(pixels, FrameTimestamp(i, fps));
                }
                gif!.Dispose(); // flushes the held last frame; throws on encoder fault
                gif = null;
                string note = fps > MaxFaithfulFps
                    ? $" Note: GIF frames can't be shorter than 1/{MaxFaithfulFps} s, so {fps:G4} fps plays back slower."
                    : string.Empty;
                return (true, $"GIF: {frames.Length} frames → {outPath}.{note}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, "GIF encode failed: " + ex.Message);
            }
            finally
            {
                try { gif?.Dispose(); } catch { /* already failing */ }
            }
        }

        private static (uint[]? Pixels, int W, int H) DecodeBgra(string path)
        {
            using var decoded = SKBitmap.Decode(path);
            if (decoded == null) return (null, 0, 0);
            var bmp = decoded.ColorType == SKColorType.Bgra8888
                ? decoded
                : decoded.Copy(SKColorType.Bgra8888);
            try
            {
                if (bmp == null) return (null, 0, 0);
                int w = bmp.Width, h = bmp.Height;
                var px = new uint[w * h];
                var src = MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan());
                // Rows may be padded; copy row by row by RowBytes.
                int stride = bmp.RowBytes / 4;
                for (int y = 0; y < h; y++)
                    src.Slice(y * stride, w).CopyTo(px.AsSpan(y * w, w));
                return (px, w, h);
            }
            finally
            {
                if (!ReferenceEquals(bmp, decoded)) bmp?.Dispose();
            }
        }
    }
}
