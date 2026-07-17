// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ImageExport.cs
//
// Shell-neutral image-IO + contrast helpers extracted verbatim from the
// MainForm WinForms partials (ImageCapture.cs / MainForm.cs). Both shells
// (the legacy WinForms WinExe and the Avalonia App) call these.
//
// Phase X.A / Slice A.7 — fully cross-platform. All System.Drawing.Common
// dependencies (Bitmap, Graphics, Font, Pen, Brush, ImageFormat) have moved
// to FracturingFog.Imaging.ImageExportGdi in the Windows-only
// FracturingFog.Win assembly. This file uses only:
//   * SkiaSharp for pixel save + watermark composition (cross-platform)
//   * System.Drawing.Primitives (Color struct) which ships with the BCL
//     and has no GDI+ dependency.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using SkiaSharp;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Cross-platform BGRA-buffer → file save pipeline + watermark +
    /// contrast colour helpers. Uses SkiaSharp end-to-end. Windows-only legacy
    /// callers that need the System.Drawing.Imaging.ImageFormat overloads
    /// should use <see cref="ImageExportGdi"/> in FracturingFog.Win.</summary>
    public static class ImageExport
    {
        // ── Public Save overloads (portable ImageFileFormat path) ─────────

        /// <summary>Write a BGRA <paramref name="pixels"/> buffer to
        /// <paramref name="path"/> using the portable <see cref="ImageFileFormat"/>
        /// token. When a string watermark is supplied it is composited via
        /// SkiaSharp on every platform (consistent rendering).</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFileFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false,
            float dpi = 0f)
        {
            SaveBgraSkia(pixels, w, h, path, format, dpi);
            if (string.IsNullOrEmpty(watermarkText)) return;
            CompositeWatermarkSkia(path, format,
                topText: watermarkText, subText: subText, fontColor: fontColor,
                poster: poster);
        }

        /// <summary>Save BGRA pixels then composite a resolved watermark on
        /// top via SkiaSharp. <paramref name="poster"/> no longer selects a
        /// layout — WatermarkPainterSkia scales the watermark from the image
        /// dimensions, so posters, wallpapers and screenshots all agree with
        /// the on-screen overlay. The parameter is retained for callers.</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFileFormat format,
            WatermarkRender? wm, bool poster = false, float dpi = 0f)
        {
            SaveBgraSkia(pixels, w, h, path, format, dpi);

            bool hasWm = wm != null && (!string.IsNullOrEmpty(wm.TopText) || !string.IsNullOrEmpty(wm.SubText));
            if (!hasWm) return;

            CompositeWatermarkRenderSkia(path, format, wm!, poster);
        }

        // ── SkiaSharp save path ───────────────────────────────────────────
        //
        // BGRA uint[] -> SKBitmap.InstallPixels -> SKImage.Encode. Matches
        // GDI+ output bit-identically for PNG/BMP. JPEG/WebP fall through to
        // SkiaSharp quality 95 (visually lossless). TIFF logs a debug line
        // and saves as PNG instead (SkiaSharp does not encode TIFF).

        private static void SaveBgraSkia(uint[] pixels, int w, int h, string path,
            ImageFileFormat format, float dpi)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            unsafe
            {
                fixed (uint* src = pixels)
                {
                    Buffer.MemoryCopy(src, (void*)bmp.GetPixels(),
                        (long)w * h * 4, (long)w * h * 4);
                }
            }

            SKEncodedImageFormat skFmt = MapToSkiaFormat(format, path, out bool unsupportedTiff);
            int quality = skFmt == SKEncodedImageFormat.Jpeg ? 95 : 100;

            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(skFmt, quality);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);

            if (unsupportedTiff)
                Debug.WriteLine($"SaveBgraSkia: TIFF unsupported by SkiaSharp; saved {path} as PNG.");
            // DPI metadata: PNG pHYs / JPEG JFIF / TIFF would need manual
            // chunk injection. SkiaSharp does not expose it; output declares
            // the encoder default (96 dpi). Documented gap.
            _ = dpi;
        }

        // Portable ImageFileFormat → SKEncodedImageFormat mapping.
        private static SKEncodedImageFormat MapToSkiaFormat(
            ImageFileFormat format, string path, out bool unsupportedTiff)
        {
            unsupportedTiff = false;
            switch (format)
            {
                case ImageFileFormat.Png:  return SKEncodedImageFormat.Png;
                case ImageFileFormat.Jpeg: return SKEncodedImageFormat.Jpeg;
                case ImageFileFormat.Bmp:  return SKEncodedImageFormat.Bmp;
                case ImageFileFormat.Gif:  return SKEncodedImageFormat.Gif;
                case ImageFileFormat.Webp: return SKEncodedImageFormat.Webp;
                case ImageFileFormat.Tiff:
                    unsupportedTiff = true;
                    return SKEncodedImageFormat.Png;
                case ImageFileFormat.Auto:
                default:
                    return MapExtToSkiaFormat(path);
            }
        }

        private static SKEncodedImageFormat MapExtToSkiaFormat(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
                ".bmp" => SKEncodedImageFormat.Bmp,
                ".webp" => SKEncodedImageFormat.Webp,
                ".gif" => SKEncodedImageFormat.Gif,
                _ => SKEncodedImageFormat.Png,
            };
        }

        // ── SkiaSharp watermark composition ───────────────────────────────

        private static void CompositeWatermarkSkia(
            string path, ImageFileFormat format,
            string topText, string subText, Color fontColor, bool poster)
        {
            // The string overload has no placement/justify of its own — it is
            // the default bottom-right region/theme watermark by definition.
            var wm = new WatermarkRender
            {
                TopText = topText ?? string.Empty,
                SubText = subText ?? string.Empty,
                TextColor = new RgbDef(fontColor.R, fontColor.G, fontColor.B),
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
            };
            CompositeWatermarkRenderSkia(path, format, wm, poster);
        }

        private static void CompositeWatermarkRenderSkia(
            string path, ImageFileFormat format, WatermarkRender wm, bool poster)
        {
            using var existing = SKBitmap.Decode(path);
            if (existing == null) return;
            int width = existing.Width;
            int height = existing.Height;

            using var surface = SKSurface.Create(existing.Info);
            var canvas = surface.Canvas;
            canvas.DrawBitmap(existing, 0, 0);

            // Shared painter: same geometry, fonts, stacking and placement the
            // live overlay uses, scaled up so a high-res export's watermark
            // covers the same fraction of the frame as it does on screen.
            // `poster` no longer selects a layout — size follows the image.
            WatermarkPainterSkia.Paint(canvas, wm, width, height);

            using var snap = surface.Snapshot();
            SKEncodedImageFormat skFmt = MapToSkiaFormat(format, path, out _);
            using var data = snap.Encode(skFmt, 100);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);
        }

        // ── Contrast picker ───────────────────────────────────────────────

        /// <summary>
        /// Returns a colour that contrasts well against <paramref name="swatch"/>.
        /// When <paramref name="watermark"/> is true and a pixel buffer is supplied,
        /// the method samples the lower-right region of the image (where the
        /// watermark will be placed) instead of using the swatch, yielding a colour
        /// that is always readable against the actual rendered content.
        /// </summary>
        public static Color ComputeContrastColor(
            Color swatch,
            bool watermark = false,
            uint[]? pixels = null,
            int imgW = 0,
            int imgH = 0)
        {
            Color baseColor = swatch;

            if (watermark && pixels != null && imgW > 0 && imgH > 0)
            {
                const int regionW = 320;
                const int regionH = 46;
                int x0 = Math.Max(0, imgW - regionW - 20);
                int y0 = Math.Max(0, imgH - regionH - 2);
                int x1 = Math.Min(imgW, imgW);
                int y1 = Math.Min(imgH, imgH);

                long sumR = 0, sumG = 0, sumB = 0, count = 0;
                for (int row = y0; row < y1; row++)
                {
                    int rb = row * imgW;
                    for (int col = x0; col < x1; col++)
                    {
                        uint p = pixels[rb + col];
                        sumR += (p >> 16) & 0xFF;
                        sumG += (p >> 8) & 0xFF;
                        sumB += p & 0xFF;
                        count++;
                    }
                }

                if (count > 0)
                    baseColor = Color.FromArgb(
                        (int)(sumR / count),
                        (int)(sumG / count),
                        (int)(sumB / count));
            }

            float r = baseColor.R / 255f, g = baseColor.G / 255f, b = baseColor.B / 255f;
            float cmax = System.Math.Max(r, System.Math.Max(g, b));
            float cmin = System.Math.Min(r, System.Math.Min(g, b));
            float delta = cmax - cmin;
            float l = (cmax + cmin) * 0.5f;
            float h2 = 0f;
            if (delta > 0.001f)
            {
                if (cmax == r) h2 = ((g - b) / delta) % 6f;
                else if (cmax == g) h2 = (b - r) / delta + 2f;
                else h2 = (r - g) / delta + 4f;
                h2 = (h2 / 6f + 1f) % 1f;
            }
            float s2 = delta < 0.001f ? 0f : delta / (1f - System.Math.Abs(2f * l - 1f));
            float hc = (h2 + 0.5f) % 1f;
            float lc = l < 0.5f
                ? System.Math.Clamp(1f - l * 0.6f, 0.65f, 1.0f)
                : System.Math.Clamp(1f - l * 1.4f, 0.0f, 0.35f);
            float sc = System.Math.Clamp(s2 * 0.5f + 0.5f, 0.5f, 1.0f);
            float cv = (1f - System.Math.Abs(2f * lc - 1f)) * sc;
            float xv = cv * (1f - System.Math.Abs((hc * 6f) % 2f - 1f));
            float m = lc - cv * 0.5f;
            float rr, gg, bb;
            switch ((int)(hc * 6f))
            {
                case 0: rr = cv; gg = xv; bb = 0; break;
                case 1: rr = xv; gg = cv; bb = 0; break;
                case 2: rr = 0; gg = cv; bb = xv; break;
                case 3: rr = 0; gg = xv; bb = cv; break;
                case 4: rr = xv; gg = 0; bb = cv; break;
                default: rr = cv; gg = 0; bb = xv; break;
            }
            int alpha = watermark ? 205 : 255;
            return Color.FromArgb(
                alpha,
                (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
                (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
                (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
        }

        /// <summary>Backward-compatible overload used by the grid overlay
        /// (no pixel sampling).</summary>
        public static Color ComputeContrastColorSimple(Color swatch, bool fade = false)
        {
            var c = ComputeContrastColor(swatch);
            return fade ? Color.FromArgb(75, c.R, c.G, c.B) : c;
        }
    }
}
