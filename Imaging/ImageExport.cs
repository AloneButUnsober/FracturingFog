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

namespace FracturingFog.Imaging
{
    /// <summary>Shared BGRA-buffer → file save pipeline + watermark + contrast
    /// colour helpers. Extracted from MainForm so every shell reuses one copy.</summary>
    public static class ImageExport
    {
        /// <summary>Write a BGRA <paramref name="pixels"/> buffer to
        /// <paramref name="path"/> as PNG/TIFF/BMP, then (if a watermark string
        /// is supplied) re-save with the outlined watermark composited on top.</summary>
        public static unsafe void SavePixelsToFile(
            uint[] pixels, int w, int h, string path, ImageFormat format,
            string watermarkText, Color fontColor, string subText = "", bool poster = false)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
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

            Debug.WriteLine($"Watermark text: '{watermarkText}'");
            if (!string.IsNullOrEmpty(watermarkText))
            {
                using var g = Graphics.FromImage(bmp);
                AddWaterMark(g, watermarkText, w, h, fontColor, subText, poster);
                bmp.Save(path, format);
            }
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
    }
}
