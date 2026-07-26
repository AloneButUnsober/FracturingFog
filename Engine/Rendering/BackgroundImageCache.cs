// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/BackgroundImageCache.cs
//
// Decodes the 2D alpha-composite background image (issue #96 — Image mode) into
// a packed BGRA uint[] once per path and caches the result. Consumed by the
// present pass in FractalRenderHost when Interior2DBackgroundMode.Image is
// active. Decodes via SkiaSharp (SKBitmap.Decode → Bgra8888) so it stays cross-
// platform and matches the buffer layout the swap-chain upload already uses.

using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace FracturingFog.Rendering
{
    /// <summary>Process-wide single-entry cache for the decoded 2D background
    /// image. Single entry is enough — only one background image is active at a
    /// time — and it avoids re-decoding on every frame.</summary>
    internal static class BackgroundImageCache
    {
        private static readonly object Gate = new();
        private static string? _path;
        private static long _stampTicks;      // last-write time of the cached file
        private static uint[]? _pixels;       // packed 0xAARRGGBB (BGRA in memory), w*h
        private static int _w, _h;

        /// <summary>Returns the decoded pixels for <paramref name="path"/>, or
        /// false when the path is empty or cannot be decoded. Re-decodes when the
        /// path changes or the file's last-write time moves so an edited image
        /// refreshes without a restart.</summary>
        public static bool TryGet(string? path, out uint[] pixels, out int width, out int height)
        {
            pixels = Array.Empty<uint>();
            width = height = 0;
            if (string.IsNullOrWhiteSpace(path)) return false;

            long stamp = 0;
            try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return false; }

            lock (Gate)
            {
                if (_pixels != null &&
                    string.Equals(_path, path, StringComparison.Ordinal) &&
                    _stampTicks == stamp)
                {
                    pixels = _pixels; width = _w; height = _h;
                    return true;
                }

                if (!Decode(path, out var px, out int w, out int h))
                {
                    // Cache the failure for this (path, stamp) so we don't hammer
                    // the disk decoding a broken file every frame.
                    _path = path; _stampTicks = stamp; _pixels = null; _w = _h = 0;
                    return false;
                }

                _path = path; _stampTicks = stamp; _pixels = px; _w = w; _h = h;
                pixels = px; width = w; height = h;
                return true;
            }
        }

        private static bool Decode(string path, out uint[] pixels, out int width, out int height)
        {
            pixels = Array.Empty<uint>();
            width = height = 0;
            try
            {
                using var decoded = SKBitmap.Decode(path);
                if (decoded == null) return false;

                int w = decoded.Width, h = decoded.Height;
                if (w <= 0 || h <= 0) return false;

                // Normalise to Bgra8888/Unpremul so the memory layout matches the
                // uint[] the composite expects (0xAARRGGBB when read as uint).
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var bmp = new SKBitmap(info);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                if (!decoded.ScalePixels(bmp, sampling)) return false;

                var buf = new uint[w * h];
                var span = bmp.GetPixelSpan();
                if (span.Length < buf.Length * 4) return false;
                MemoryMarshal.Cast<byte, uint>(span).Slice(0, buf.Length).CopyTo(buf);

                pixels = buf; width = w; height = h;
                return true;
            }
            catch { return false; }
        }
    }
}
