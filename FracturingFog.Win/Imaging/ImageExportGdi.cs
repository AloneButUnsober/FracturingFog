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

using FracturingFog.Models;
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

        /// <summary>Render the default region/theme watermark + program
        /// sub-line. Layout comes from the shared WatermarkLayout, so this
        /// agrees with the live overlay and the Skia export paths.</summary>
        public static void AddWaterMark(
            Graphics g,
            string text,
            int width,
            int height,
            Color fontColor,
            string subText = "",
            bool poster = false)
        {
            AddWaterMark(g, new WatermarkRender
            {
                TopText = text ?? string.Empty,
                SubText = subText ?? string.Empty,
                TextColor = new RgbDef(fontColor.R, fontColor.G, fontColor.B),
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
            }, width, height, poster);
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
            => MeasureWatermarkBBox(new WatermarkRender
            {
                TopText = text ?? string.Empty,
                SubText = subText ?? string.Empty,
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
            }, width, height, poster);

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

            var plan = BuildPlan(wm, width, height);
            if (plan == null) return;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (plan.Background is { } bgRect && plan.BackgroundColor != null)
            {
                using var bgBrush = new SolidBrush(ToGdi(plan.BackgroundColor));
                g.FillRectangle(bgBrush, bgRect.X, bgRect.Y, bgRect.W, bgRect.H);
            }

            DrawPlanLine(g, plan.Top, plan.TopFill, plan.Halo, plan.ShadowOffset);
            DrawPlanLine(g, plan.Sub, plan.SubFill, plan.Halo, plan.ShadowOffset);
        }

        // Shared geometry, GDI+ measurement. `poster` is no longer a layout
        // switch anywhere — WatermarkLayout scales off the image height, which
        // is what keeps a 4K export matching the on-screen proportions.
        private static WatermarkPlan? BuildPlan(WatermarkRender wm, int width, int height)
        {
            using var dummy = new Bitmap(1, 1);
            using var g = Graphics.FromImage(dummy);

            return WatermarkLayout.Compute(
                wm, width, height,
                WatermarkLayout.ScaleForImage(height),
                WatermarkLayout.HaloForInk(wm.TextColor.R, wm.TextColor.G, wm.TextColor.B),
                (text, fontPx) =>
                {
                    using var f = MakeGdiFont(fontPx);
                    var sz = g.MeasureString(text ?? string.Empty, f);
                    return (sz.Width, sz.Height);
                });
        }

        private static Font MakeGdiFont(float fontPx)
            => new Font("Arial", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);

        private static Color ToGdi(RgbaDef c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        private static void DrawPlanLine(
            Graphics g, WatermarkLine? line, RgbaDef fill, RgbaDef halo, int shadow)
        {
            if (line == null || string.IsNullOrEmpty(line.Text)) return;

            using var font = MakeGdiFont(line.FontPx);
            using var shadowBrush = new SolidBrush(ToGdi(halo));
            using var fillBrush = new SolidBrush(ToGdi(fill));

            // GDI+ DrawString positions at the top-left of the glyph box, which
            // is exactly what the plan's X/Y name — no baseline shift needed.
            g.DrawString(line.Text, font, shadowBrush, line.X + shadow, line.Y + shadow);
            g.DrawString(line.Text, font, fillBrush, line.X, line.Y);
        }

        /// <summary>Measure the on-image bounding rectangle the resolved
        /// watermark will occupy. Used by overlay surfaces that allocate
        /// scratch bitmaps the size of just the watermark band.</summary>
        public static Rectangle MeasureWatermarkBBox(
            WatermarkRender wm, int width, int height, bool poster = false)
        {
            if (wm == null) return Rectangle.Empty;

            var plan = BuildPlan(wm, width, height);
            if (plan == null) return Rectangle.Empty;

            // Union of whatever the plan actually places, so the box tracks the
            // draw instead of re-deriving bounds that can disagree with it.
            int left = width, top = height, right = 0, bottom = 0;
            foreach (var line in new[] { plan.Top, plan.Sub })
            {
                if (line == null || string.IsNullOrEmpty(line.Text)) continue;
                left = System.Math.Min(left, line.X);
                top = System.Math.Min(top, line.Y);
                right = System.Math.Max(right, line.X + line.Width);
                bottom = System.Math.Max(bottom, line.Y + line.Height);
            }
            if (plan.Background is { } bg)
            {
                left = System.Math.Min(left, bg.X);
                top = System.Math.Min(top, bg.Y);
                right = System.Math.Max(right, bg.X + bg.W);
                bottom = System.Math.Max(bottom, bg.Y + bg.H);
            }
            if (right <= left || bottom <= top) return Rectangle.Empty;

            // Pad for the shadow offset + AA fringe.
            int pad = plan.ShadowOffset + 8;
            int x0 = System.Math.Max(0, left - pad);
            int y0 = System.Math.Max(0, top - pad);
            int x1 = System.Math.Min(width, right + pad);
            int y1 = System.Math.Min(height, bottom + pad);
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
