// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ImageExportGdi.cs
//
// Phase X.A / Slice A.7 — Windows-only ImageExport surface carved out of the
// cross-platform engine so the engine can drop its System.Drawing.Common
// reference. The engine keeps the Skia-based save pipeline; everything that
// touches System.Drawing.Imaging.ImageFormat, Graphics, Font, Pen, Brush, or
// any GDI+ Bitmap API lives here.
//
// Backwards-compatible: WinExe callers that previously called
// FracturingFog.Imaging.ImageExport.{SavePixelsToFile, AddWaterMark,
// MeasureWatermarkBBox, DrawOutlinedString} with System.Drawing.Imaging.ImageFormat
// now call FracturingFog.Imaging.ImageExportGdi instead. Pixel output and
// watermark layout are unchanged.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;

using SkiaSharp;

namespace FracturingFog.Imaging
{
    /// <summary>
    /// Windows-only GDI+ + ImageFormat save / watermark pipeline. Kept
    /// byte-for-byte compatible with the pre-Slice-A.7 engine surface.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ImageExportGdi
    {
        // ── Public Save overloads (legacy ImageFormat path) ───────────────

        /// <summary>Write a BGRA <paramref name="pixels"/> buffer to
        /// <paramref name="path"/> using the legacy <see cref="ImageFormat"/>
        /// token. If a watermark string is supplied, composite an outlined
        /// watermark via GDI+ before saving.</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false,
            float dpi = 0f)
        {
            if (string.IsNullOrEmpty(watermarkText))
            {
                SaveBgraSkia(pixels, w, h, path, format, dpi);
                return;
            }

            SaveWithGdiWatermark(pixels, w, h, path, format,
                text: watermarkText, fontColor: fontColor, subText: subText,
                poster: poster, dpi: dpi);
        }

        /// <summary>Save BGRA pixels then composite a resolved watermark on
        /// top, using the legacy <see cref="ImageFormat"/> token.</summary>
        public static void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            WatermarkRender? wm, bool poster = false, float dpi = 0f)
        {
            bool hasWm = wm != null && (!string.IsNullOrEmpty(wm.TopText) || !string.IsNullOrEmpty(wm.SubText));

            if (!hasWm)
            {
                SaveBgraSkia(pixels, w, h, path, format, dpi);
                return;
            }

            SaveWithGdiWatermark(pixels, w, h, path, format, wm!, poster, dpi);
        }

        // ── GDI+ watermark renderers (public, called by WinExe overlay path) ──

        /// <summary>Render the region/theme watermark + program sub-line in
        /// the lower-right corner with a contrasting outline.</summary>
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

        /// <summary>Compute the on-image bounding box the watermark will
        /// occupy. Used by the slideshow overlay to allocate only a small
        /// bitmap instead of a full-frame one.</summary>
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

            if (wm.BackgroundColor != null)
            {
                var bg = Color.FromArgb(wm.BackgroundColor.A,
                    wm.BackgroundColor.R, wm.BackgroundColor.G, wm.BackgroundColor.B);
                const int bgPad = 4;
                using var bgBrush = new SolidBrush(bg);
                g.FillRectangle(bgBrush, bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2);
            }

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

            const int pad = 8;
            int x0 = System.Math.Max(0, bx - pad);
            int y0 = System.Math.Max(0, by - pad);
            int x1 = System.Math.Min(width, bx + bw + pad);
            int y1 = System.Math.Min(height, by + bh + pad);
            return new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }

        // ── Private helpers ───────────────────────────────────────────────

        // Skia BGRA save with ImageFormat → SKEncodedImageFormat mapping. Used
        // by the no-watermark fast path of the legacy ImageFormat overloads.
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
                Debug.WriteLine($"ImageExportGdi.SaveBgraSkia: TIFF unsupported by SkiaSharp; saved {path} as PNG.");
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

        // GDI+ save with byte-for-byte parity to the pre-extraction code.
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

            SaveBmpAs(bmp, path, format);
        }

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

            SaveBmpAs(bmp, path, format);
        }

        // TIFF encoder selection with LZW compression. Other formats fall
        // through to plain Bitmap.Save.
        private static void SaveBmpAs(Bitmap bmp, string path, ImageFormat format)
        {
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
                    return;
                }
            }
            bmp.Save(path, format);
        }
    }
}
