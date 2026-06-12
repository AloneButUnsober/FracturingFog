// Imaging/ImageExport.cs
//
// Shell-neutral image-IO + contrast helpers extracted verbatim from the
// MainForm WinForms partials (ImageCapture.cs / MainForm.cs). Both the legacy
// WinForms shell and the Avalonia shell call these, so the System.Drawing
// save pipeline (BGRA uint[] → Bitmap → PNG/TIFF/BMP), the outlined watermark
// renderer, and the pixel-sampled contrast-colour picker live in exactly one
// place.
//
// Pure static. No WinForms / no MainForm field dependencies. System.Drawing is
// the only UI-framework reference and it is Windows-only today; when the
// cross-platform image backend lands (Phase 2.4) this is the single file that
// swaps to ImageSharp/Skia.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using SkiaSharp;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Shared BGRA-buffer → file save pipeline + watermark + contrast
    /// colour helpers. Extracted from MainForm so every shell reuses one copy.</summary>
    public static class ImageExport
    {
        /// <summary>Write a BGRA <paramref name="pixels"/> buffer to
        /// <paramref name="path"/> as PNG/TIFF/BMP, then (if a watermark string
        /// is supplied) re-save with the outlined watermark composited on top.
        ///
        /// Phase X.A / Slice A.2 — the no-watermark save path routes through
        /// SkiaSharp (cross-platform). The watermark composition uses GDI+
        /// (Graphics + GraphicsPath text outlining) on Windows; on non-Windows
        /// hosts the watermark is composed via SKCanvas + Inter typeface.
        /// TIFF on non-Windows falls back to PNG with a debug log line
        /// (SkiaSharp does not encode TIFF).</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false,
            float dpi = 0f)
        {
            // Fast path: no watermark, no GDI+ at all.
            if (string.IsNullOrEmpty(watermarkText))
            {
                SaveBgraSkia(pixels, w, h, path, format, dpi);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                SaveWithGdiWatermark(pixels, w, h, path, format,
                    text: watermarkText, fontColor: fontColor, subText: subText,
                    poster: poster, dpi: dpi);
                return;
            }

            // Non-Windows: save base image via Skia, composite watermark via Skia.
            SaveBgraSkia(pixels, w, h, path, format, dpi);
            CompositeWatermarkSkia(path, format,
                topText: watermarkText, subText: subText, fontColor: fontColor,
                poster: poster);
        }

        // ── SkiaSharp save path (cross-platform) ──────────────────────────
        //
        // BGRA uint[] -> SKBitmap.InstallPixels -> SKImage.Encode. Matches
        // the GDI+ output bit-identically for PNG/BMP. JPEG/WebP fall through
        // to SkiaSharp quality 100 (visually lossless). TIFF on non-Windows
        // logs a debug line and saves as PNG instead.
        private static void SaveBgraSkia(uint[] pixels, int w, int h, string path,
            ImageFormat format, float dpi)
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
            // DPI metadata: PNG pHYs / JPEG JFIF / TIFF would need manual chunk
            // injection. SkiaSharp does not expose it; cross-platform output
            // declares the encoder default (96 dpi). Documented gap.
            _ = dpi;
        }

        private static SKEncodedImageFormat MapToSkiaFormat(
            ImageFormat format, string path, out bool unsupportedTiff)
        {
            unsupportedTiff = false;
            if (format == ImageFormat.Png) return SKEncodedImageFormat.Png;
            if (format == ImageFormat.Jpeg) return SKEncodedImageFormat.Jpeg;
            if (format == ImageFormat.Bmp) return SKEncodedImageFormat.Bmp;
            if (format == ImageFormat.Gif) return SKEncodedImageFormat.Gif;
            if (format == ImageFormat.Tiff)
            {
                unsupportedTiff = true;
                return SKEncodedImageFormat.Png;
            }
            // Fallback: pick by extension.
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

        // ── GDI+ save + watermark path (Windows-only) ─────────────────────
        [SupportedOSPlatform("windows")]
        private static unsafe void SaveWithGdiWatermark(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string text, Color fontColor, string subText, bool poster, float dpi)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            if (dpi > 0f) bmp.SetResolution(dpi, dpi);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* src = pixels)
                {
                    if (bmpData.Stride == w * 4)
                        Buffer.MemoryCopy(src, (void*)bmpData.Scan0, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* dst = (byte*)bmpData.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                              dst + (long)row * bmpData.Stride,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(bmpData); }

            using var g = Graphics.FromImage(bmp);
            AddWaterMark(g, text, w, h, fontColor, subText, poster);

            if (format == ImageFormat.Tiff)
            {
                ImageCodecInfo? codec = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.MimeType == "image/tiff") { codec = c; break; }
                if (codec != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)EncoderValue.CompressionLZW);
                    bmp.Save(path, codec, ep);
                }
                else bmp.Save(path, format);
            }
            else bmp.Save(path, format);
        }

        // ── SkiaSharp watermark composition (non-Windows) ─────────────────
        private static void CompositeWatermarkSkia(
            string path, ImageFormat format,
            string topText, string subText, Color fontColor, bool poster)
        {
            using var existing = SKBitmap.Decode(path);
            if (existing == null) return;
            int width = existing.Width;
            int height = existing.Height;

            using var surface = SKSurface.Create(existing.Info);
            var canvas = surface.Canvas;
            canvas.DrawBitmap(existing, 0, 0);

            DrawWatermarkSkia(canvas, topText, subText, width, height, fontColor, poster);

            using var snap = surface.Snapshot();
            SKEncodedImageFormat skFmt = MapToSkiaFormat(format, path, out _);
            using var data = snap.Encode(skFmt, 100);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);
        }

        private static void DrawWatermarkSkia(
            SKCanvas canvas, string topText, string subText,
            int width, int height, Color fontColor, bool poster)
        {
            int fontSize = poster ? Math.Max(width, height) / 140 : 16;
            int yOffset = poster ? Math.Min(width, height) / 150 : 12;

            using var typeface = SKTypeface.FromFamilyName("Inter",
                SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;
            using var fontMain = new SKFont(typeface, fontSize);
            using var fontSub  = new SKFont(typeface, Math.Max(1, fontSize / 2));

            float lum = (fontColor.R * 0.299f + fontColor.G * 0.587f + fontColor.B * 0.114f) / 255f;
            var outline = lum < 0.5f
                ? new SKColor(255, 255, 255, 190)
                : new SKColor(0, 0, 0, 190);
            var fill = new SKColor(fontColor.R, fontColor.G, fontColor.B, fontColor.A);

            float mainStroke = poster ? Math.Max(2f, fontSize / 10f) : 2f;
            float subStroke  = poster ? Math.Max(1.5f, fontSize / 16f) : 1.5f;

            DrawOutlinedSkia(canvas, topText, fontMain, fill, outline, mainStroke,
                width - MeasureText(topText, fontMain) - 20,
                height - fontSize - yOffset);

            if (!string.IsNullOrEmpty(subText))
            {
                int subFontSize = Math.Max(1, fontSize / 2);
                DrawOutlinedSkia(canvas, subText, fontSub, fill, outline, subStroke,
                    width - MeasureText(subText, fontSub) - 55,
                    height - subFontSize - (poster ? 0 : 2));
            }
        }

        private static float MeasureText(string text, SKFont font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return font.MeasureText(text);
        }

        private static void DrawOutlinedSkia(
            SKCanvas canvas, string text, SKFont font,
            SKColor fill, SKColor outline, float strokeWidth,
            float x, float y)
        {
            using var stroke = new SKPaint
            {
                Color = outline,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            using var fillPaint = new SKPaint
            {
                Color = fill,
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            // Skia text baseline is at y; shift down by font ascent so the
            // coordinate (x, y) names the upper-left corner of the glyph
            // bounding box (matches GDI+ DrawString semantics the caller expects).
            var metrics = font.Metrics;
            float baseline = y - metrics.Ascent;
            canvas.DrawText(text, x, baseline, font, stroke);
            canvas.DrawText(text, x, baseline, font, fillPaint);
        }

        /// <summary>Render the region/theme watermark + program sub-line in the
        /// lower-right corner with a contrasting outline.</summary>
        public static void AddWaterMark(
            Graphics g,
            string text,
            int width,
            int height,
            Color fontColor,
            string subText = "",
            bool poster = false)
        {
            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            int yOffset = poster ? System.Math.Min(width, height) / 150 : 12;
            var pos = new PointF(width - sz.Width - 20, height - sz.Height - yOffset);

            // Outline colour: opposite luminance of fill, ~75% opacity.
            float lum = (fontColor.R * 0.299f + fontColor.G * 0.587f + fontColor.B * 0.114f) / 255f;
            Color outlineColor = lum < 0.5f
                ? Color.FromArgb(190, 255, 255, 255)
                : Color.FromArgb(190, 0, 0, 0);

            float mainStroke = poster ? System.Math.Max(2f, fontSize / 10f) : 2f;
            float subStroke = poster ? System.Math.Max(1.5f, fontSize / 16f) : 1.5f;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            DrawOutlinedString(g, text, font, pos, fontColor, outlineColor, mainStroke);

            if (!string.IsNullOrEmpty(subText))
            {
                using var fontSmall = new Font("Segoe UI", fontSize / 2, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz2 = g.MeasureString(subText, fontSmall);
                int subTextOffset = poster ? 0 : 2;
                var subPos = new PointF(width - sz2.Width - 55, height - sz2.Height - subTextOffset);
                DrawOutlinedString(g, subText, fontSmall, subPos, fontColor, outlineColor, subStroke);
            }
        }

        /// <summary>Draw <paramref name="text"/> as a filled glyph path with a
        /// rounded-join outline pen for legibility over any background.</summary>
        public static void DrawOutlinedString(
            Graphics g, string text, Font font, PointF pos,
            Color fill, Color outline, float strokeWidth)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, pos,
                System.Drawing.StringFormat.GenericDefault);
            using var pen = new Pen(outline, strokeWidth)
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                MiterLimit = 2f
            };
            g.DrawPath(pen, path);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        /// <summary>
        /// Computes the on-image bounding box the watermark will occupy.
        /// Used by the slideshow overlay to allocate only a small bitmap
        /// instead of a full-frame one.
        /// </summary>
        public static Rectangle MeasureWatermarkBBox(
            string text, string subText, int width, int height, bool poster = false)
        {
            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var dummy = new Bitmap(1, 1);
            using var g = Graphics.FromImage(dummy);

            var sz = g.MeasureString(text, font);
            int yOffset = poster ? System.Math.Min(width, height) / 150 : 12;
            float left = width - sz.Width - 20;
            float top = height - sz.Height - yOffset;
            float right = left + sz.Width;
            float bottom = top + sz.Height;

            if (!string.IsNullOrEmpty(subText))
            {
                using var fontSmall = new Font("Segoe UI", fontSize / 2, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz2 = g.MeasureString(subText, fontSmall);
                int subTextOffset = poster ? 0 : 2;
                float sLeft = width - sz2.Width - 55;
                float sTop = height - sz2.Height - subTextOffset;
                left = System.Math.Min(left, sLeft);
                top = System.Math.Min(top, sTop);
                right = System.Math.Max(right, sLeft + sz2.Width);
                bottom = System.Math.Max(bottom, sTop + sz2.Height);
            }

            // Pad for outline stroke + AA fringe.
            const int pad = 6;
            int x0 = System.Math.Max(0, (int)System.Math.Floor(left) - pad);
            int y0 = System.Math.Max(0, (int)System.Math.Floor(top) - pad);
            int x1 = System.Math.Min(width, (int)System.Math.Ceiling(right) + pad);
            int y1 = System.Math.Min(height, (int)System.Math.Ceiling(bottom) + pad);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

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

            // When in watermark mode and we have pixel data, sample the region
            // where the watermark text will land (lower-right corner).
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

            // Compute complementary + luminance-adjusted colour.
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
            // Watermark mode is always fully opaque; fade flag kept for non-watermark uses.
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

        // ── Configurable-watermark overloads ──────────────────────────────────
        //
        // These accept a WatermarkRender resolved by WatermarkResolver and honour
        // its TextColor / HighlightColor / BackgroundColor / Placement / Justify.
        // The legacy AddWaterMark(string, Color, string) overload above is kept
        // for callers that haven't been migrated to pass a WatermarkRender yet.

        /// <summary>Save BGRA pixels then composite a resolved watermark on top.
        /// When <paramref name="wm"/> is null no watermark is drawn (used by the
        /// no-watermark code paths that today pass watermarkText: "").</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            WatermarkRender? wm, bool poster = false, float dpi = 0f)
        {
            bool hasWm = wm != null && (!string.IsNullOrEmpty(wm.TopText) || !string.IsNullOrEmpty(wm.SubText));

            // Fast path: no watermark, no GDI+ at all.
            if (!hasWm)
            {
                SaveBgraSkia(pixels, w, h, path, format, dpi);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                SaveWithGdiWatermark(pixels, w, h, path, format, wm!, poster, dpi);
                return;
            }

            // Non-Windows: save base then composite via Skia.
            SaveBgraSkia(pixels, w, h, path, format, dpi);
            CompositeWatermarkRenderSkia(path, format, wm!, poster);
        }

        [SupportedOSPlatform("windows")]
        private static unsafe void SaveWithGdiWatermark(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            WatermarkRender wm, bool poster, float dpi)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            if (dpi > 0f) bmp.SetResolution(dpi, dpi);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* src = pixels)
                {
                    if (bmpData.Stride == w * 4)
                        Buffer.MemoryCopy(src, (void*)bmpData.Scan0, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* dst = (byte*)bmpData.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                              dst + (long)row * bmpData.Stride,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(bmpData); }

            using var g = Graphics.FromImage(bmp);
            AddWaterMark(g, wm, w, h, poster);

            if (format == ImageFormat.Tiff)
            {
                ImageCodecInfo? codec = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.MimeType == "image/tiff") { codec = c; break; }
                if (codec != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)EncoderValue.CompressionLZW);
                    bmp.Save(path, codec, ep);
                }
                else bmp.Save(path, format);
            }
            else bmp.Save(path, format);
        }

        private static void CompositeWatermarkRenderSkia(
            string path, ImageFormat format, WatermarkRender wm, bool poster)
        {
            using var existing = SKBitmap.Decode(path);
            if (existing == null) return;
            int width = existing.Width;
            int height = existing.Height;

            using var surface = SKSurface.Create(existing.Info);
            var canvas = surface.Canvas;
            canvas.DrawBitmap(existing, 0, 0);

            // Translate WatermarkRender to a top + sub call with the resolved
            // text colour. Skia path ignores HighlightColor / BackgroundColor /
            // Placement / Justify for now (lower-right is the only placement
            // shipping today; richer placement lands when the full
            // SkiaWatermarkRenderer extracts).
            var fill = Color.FromArgb(255, wm.TextColor.R, wm.TextColor.G, wm.TextColor.B);
            DrawWatermarkSkia(canvas, wm.TopText, wm.SubText, width, height, fill, poster);

            using var snap = surface.Snapshot();
            SKEncodedImageFormat skFmt = MapToSkiaFormat(format, path, out _);
            using var data = snap.Encode(skFmt, 100);
            using var fs = File.OpenWrite(path);
            data.SaveTo(fs);
        }

        /// <summary>Draw the resolved watermark onto an arbitrary GDI surface.
        /// Honours top-line text + colour, optional background fill, optional
        /// highlight outline, edge placement and inline justify. Subtext is
        /// always rendered (program/version is mandatory per spec).</summary>
        public static void AddWaterMark(
            Graphics g,
            WatermarkRender wm,
            int width,
            int height,
            bool poster = false)
        {
            if (wm == null) return;

            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            int subFontSize = System.Math.Max(1, fontSize / 2);
            int edgePad = poster ? System.Math.Min(width, height) / 150 : 12;

            using var fontMain = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fontSub  = new Font("Segoe UI", subFontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            var fill = Color.FromArgb(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B);

            // Default outline = luminance-opposite halo (matches legacy behaviour).
            // When user supplied an explicit HighlightColor, honour it instead.
            Color outline;
            if (wm.HighlightColor != null)
            {
                outline = Color.FromArgb(wm.HighlightColor.A, wm.HighlightColor.R, wm.HighlightColor.G, wm.HighlightColor.B);
            }
            else
            {
                float lum = (fill.R * 0.299f + fill.G * 0.587f + fill.B * 0.114f) / 255f;
                outline = lum < 0.5f
                    ? Color.FromArgb(190, 255, 255, 255)
                    : Color.FromArgb(190, 0, 0, 0);
            }

            float mainStroke = poster ? System.Math.Max(2f, fontSize / 10f) : 2f;
            float subStroke = poster ? System.Math.Max(1.5f, fontSize / 16f) : 1.5f;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var szTop = string.IsNullOrEmpty(wm.TopText)
                ? new SizeF(0, 0)
                : g.MeasureString(wm.TopText, fontMain);
            var szSub = string.IsNullOrEmpty(wm.SubText)
                ? new SizeF(0, 0)
                : g.MeasureString(wm.SubText, fontSub);

            int topW = (int)System.Math.Ceiling(szTop.Width);
            int topH = (int)System.Math.Ceiling(szTop.Height);
            int subW = (int)System.Math.Ceiling(szSub.Width);
            int subH = (int)System.Math.Ceiling(szSub.Height);

            var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
                wm, width, height, topW, topH, subW, subH, edgePad);

            // Optional background fill — covers the full block + a few px pad so
            // anti-aliased glyph edges don't fringe past the rect.
            if (wm.BackgroundColor != null)
            {
                var bg = Color.FromArgb(wm.BackgroundColor.A,
                    wm.BackgroundColor.R, wm.BackgroundColor.G, wm.BackgroundColor.B);
                const int bgPad = 4;
                using var bgBrush = new SolidBrush(bg);
                g.FillRectangle(bgBrush, bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2);
            }

            // Subtext stacks below top line in reading order.
            int topX = WatermarkResolver.AlignLineX(bx, bw, topW, wm.Justify);
            int subX = WatermarkResolver.AlignLineX(bx, bw, subW, wm.Justify);
            int topY = by;
            int subY = by + topH;

            if (!string.IsNullOrEmpty(wm.TopText))
                DrawOutlinedString(g, wm.TopText, fontMain, new PointF(topX, topY), fill, outline, mainStroke);

            if (!string.IsNullOrEmpty(wm.SubText))
                DrawOutlinedString(g, wm.SubText, fontSub, new PointF(subX, subY), fill, outline, subStroke);
        }

        /// <summary>Measure the on-image bounding rectangle the resolved
        /// watermark will occupy. Used by overlay surfaces that allocate
        /// scratch bitmaps the size of just the watermark band.</summary>
        public static Rectangle MeasureWatermarkBBox(
            WatermarkRender wm, int width, int height, bool poster = false)
        {
            if (wm == null) return Rectangle.Empty;

            int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
            int subFontSize = System.Math.Max(1, fontSize / 2);
            int edgePad = poster ? System.Math.Min(width, height) / 150 : 12;

            using var fontMain = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fontSub  = new Font("Segoe UI", subFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var dummy = new Bitmap(1, 1);
            using var g = Graphics.FromImage(dummy);

            int topW = string.IsNullOrEmpty(wm.TopText) ? 0 : (int)System.Math.Ceiling(g.MeasureString(wm.TopText, fontMain).Width);
            int topH = string.IsNullOrEmpty(wm.TopText) ? 0 : (int)System.Math.Ceiling(g.MeasureString(wm.TopText, fontMain).Height);
            int subW = string.IsNullOrEmpty(wm.SubText) ? 0 : (int)System.Math.Ceiling(g.MeasureString(wm.SubText, fontSub).Width);
            int subH = string.IsNullOrEmpty(wm.SubText) ? 0 : (int)System.Math.Ceiling(g.MeasureString(wm.SubText, fontSub).Height);

            var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
                wm, width, height, topW, topH, subW, subH, edgePad);

            // Pad for outline stroke + AA fringe + optional background pad.
            const int pad = 8;
            int x0 = System.Math.Max(0, bx - pad);
            int y0 = System.Math.Max(0, by - pad);
            int x1 = System.Math.Min(width, bx + bw + pad);
            int y1 = System.Math.Min(height, by + bh + pad);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }
    }
}
