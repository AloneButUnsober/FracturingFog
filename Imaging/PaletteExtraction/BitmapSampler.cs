// Imaging/PaletteExtraction/BitmapSampler.cs
//
// Pull a flat byte[] of RGB pixels from a Bitmap, optionally downsampled
// for speed and with multi-stage filtering applied (near-black / near-white
// /  transparency / saturation band / lightness band).
//
// Downsampling is a big quality+speed win: clustering a 4MP image is 100x
// slower than clustering a 256x256 thumbnail with no noticeable palette
// change.
//
// All filter knobs default to "off" so existing call sites stay compatible
// — the new alpha + sat/lum band filters were added in Phase 3 of the
// Palette Builder roadmap and only the new VM passes them.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public static class BitmapSampler
    {
        /// <summary>
        /// Returns the source bitmap scaled to fit within
        /// <paramref name="maxDim"/> on its longest side. Caller owns the
        /// returned bitmap and must Dispose it.
        /// </summary>
        public static Bitmap Downsample(Bitmap src, int maxDim)
        {
            int w = src.Width, h = src.Height;
            int longest = Math.Max(w, h);
            if (longest <= maxDim)
                return new Bitmap(src);

            float scale = (float)maxDim / longest;
            int nw = Math.Max(1, (int)(w * scale));
            int nh = Math.Max(1, (int)(h * scale));

            var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, nw, nh));
            return dst;
        }

        /// <summary>
        /// Crop a bitmap to a normalised rectangle (x, y, w, h all 0..1).
        /// Returns a new Bitmap owned by the caller. Rect is clamped into
        /// the source; null / empty rect returns a copy of the whole bitmap.
        /// </summary>
        public static Bitmap CropNormalised(Bitmap src, float xN, float yN, float wN, float hN)
        {
            if (wN <= 0 || hN <= 0 || (xN == 0 && yN == 0 && wN >= 1 && hN >= 1))
                return new Bitmap(src);

            int x = Math.Clamp((int)(xN * src.Width), 0, src.Width - 1);
            int y = Math.Clamp((int)(yN * src.Height), 0, src.Height - 1);
            int w = Math.Clamp((int)(wN * src.Width), 1, src.Width - x);
            int h = Math.Clamp((int)(hN * src.Height), 1, src.Height - y);

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            g.DrawImage(src, new Rectangle(0, 0, w, h),
                              new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            return dst;
        }

        /// <summary>
        /// Extracts a flat byte array of [R,G,B,R,G,B,…] from the bitmap,
        /// applying per-pixel filters. Pixels failing any filter are
        /// dropped; <paramref name="pixelCount"/> receives the kept count.
        /// </summary>
        /// <param name="excludeBlack">Drop near-black pixels.</param>
        /// <param name="excludeWhite">Drop near-white pixels.</param>
        /// <param name="excludeTransparent">
        /// Drop pixels with alpha &lt; <paramref name="alphaThreshold"/>.
        /// </param>
        /// <param name="minSaturation">Drop pixels with HSL S below this (0..1).</param>
        /// <param name="maxSaturation">Drop pixels with HSL S above this (0..1).</param>
        /// <param name="minLightness">Drop pixels with HSL L below this (0..1).</param>
        /// <param name="maxLightness">Drop pixels with HSL L above this (0..1).</param>
        public static byte[] ExtractPixels(Bitmap bmp,
                                           bool excludeBlack,
                                           bool excludeWhite,
                                           out int pixelCount,
                                           int blackThreshold = 24,
                                           int whiteThreshold = 240,
                                           bool excludeTransparent = false,
                                           int alphaThreshold = 16,
                                           float minSaturation = 0f,
                                           float maxSaturation = 1f,
                                           float minLightness = 0f,
                                           float maxLightness = 1f,
                                           float[]? saliencyMap = null,
                                           float saliencyThreshold = 0f)
        {
            bool satFilter = minSaturation > 0f || maxSaturation < 1f;
            bool lumFilter = minLightness > 0f || maxLightness < 1f;
            bool salFilter = saliencyMap != null && saliencyThreshold > 0f
                              && saliencyMap.Length >= bmp.Width * bmp.Height;

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int total = bmp.Width * bmp.Height;
            byte[] outBuf = new byte[total * 3];
            int written = 0;

            try
            {
                unsafe
                {
                    byte* row = (byte*)data.Scan0.ToPointer();
                    int stride = data.Stride;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        byte* p = row + y * stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            byte b = p[0], g = p[1], r = p[2], a = p[3];
                            p += 4;

                            if (excludeTransparent && a < alphaThreshold) continue;
                            if (excludeBlack && r <= blackThreshold && g <= blackThreshold && b <= blackThreshold)
                                continue;
                            if (excludeWhite && r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold)
                                continue;

                            if (salFilter && saliencyMap![y * bmp.Width + x] < saliencyThreshold)
                                continue;

                            if (satFilter || lumFilter)
                            {
                                ColorSpaces.RgbToHsl(r, g, b, out _, out float s, out float l);
                                if (satFilter && (s < minSaturation || s > maxSaturation)) continue;
                                if (lumFilter && (l < minLightness || l > maxLightness)) continue;
                            }

                            int o = written * 3;
                            outBuf[o] = r;
                            outBuf[o + 1] = g;
                            outBuf[o + 2] = b;
                            written++;
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            pixelCount = written;
            if (written == total)
                return outBuf;

            byte[] trimmed = new byte[written * 3];
            Buffer.BlockCopy(outBuf, 0, trimmed, 0, written * 3);
            return trimmed;
        }

        /// <summary>
        /// Apply the EXIF orientation tag (0x0112) to <paramref name="bmp"/>
        /// in place if present. No-op when the tag is missing or already 1.
        /// Mutates the source bitmap; safe to call before downsampling.
        /// </summary>
        public static void ApplyExifOrientation(Bitmap bmp)
        {
            const int ExifOrientationTag = 0x0112;
            try
            {
                if (Array.IndexOf(bmp.PropertyIdList, ExifOrientationTag) < 0) return;
                var item = bmp.GetPropertyItem(ExifOrientationTag);
                if (item?.Value is null || item.Value.Length == 0) return;
                int o = item.Value[0];
                RotateFlipType rft = o switch
                {
                    2 => RotateFlipType.RotateNoneFlipX,
                    3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX,
                    5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone,
                    7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone,
                };
                if (rft != RotateFlipType.RotateNoneFlipNone)
                {
                    bmp.RotateFlip(rft);
                    bmp.RemovePropertyItem(ExifOrientationTag);
                }
            }
            catch { /* best-effort; ignore corrupt EXIF */ }
        }
    }
}
