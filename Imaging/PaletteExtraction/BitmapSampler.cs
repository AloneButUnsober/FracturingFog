// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/BitmapSampler.cs
//
// Pull a flat byte[] of RGB pixels from an SKBitmap, optionally downsampled
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
//
// SkiaSharp port (2026-06): SKBitmap replaces System.Drawing.Bitmap so
// webp/heic/heif decode comes for free and EXIF orientation flows through
// SKCodec.EncodedOrigin rather than the brittle PropertyItem path. All
// bitmaps are forced to SKColorType.Bgra8888 + Premul so the unsafe pixel
// reader can read b,g,r,a in that order without per-format branches.

using System;
using SkiaSharp;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public static class BitmapSampler
    {
        private static SKImageInfo BgraInfo(int w, int h)
            => new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);

        /// <summary>
        /// Returns the source bitmap scaled to fit within
        /// <paramref name="maxDim"/> on its longest side. Caller owns the
        /// returned bitmap and must Dispose it.
        /// </summary>
        public static SKBitmap Downsample(SKBitmap src, int maxDim)
        {
            int w = src.Width, h = src.Height;
            int longest = Math.Max(w, h);
            if (longest <= maxDim)
                return src.Copy(SKColorType.Bgra8888);

            float scale = (float)maxDim / longest;
            int nw = Math.Max(1, (int)(w * scale));
            int nh = Math.Max(1, (int)(h * scale));

            var dst = new SKBitmap(BgraInfo(nw, nh));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            if (!src.ScalePixels(dst, sampling))
            {
                dst.Dispose();
                throw new InvalidOperationException("SKBitmap.ScalePixels failed during downsample.");
            }
            return dst;
        }

        /// <summary>
        /// Crop a bitmap to a normalised rectangle (x, y, w, h all 0..1).
        /// Returns a new Bitmap owned by the caller. Rect is clamped into
        /// the source; null / empty rect returns a copy of the whole bitmap.
        /// </summary>
        public static SKBitmap CropNormalised(SKBitmap src, float xN, float yN, float wN, float hN)
        {
            if (wN <= 0 || hN <= 0 || (xN == 0 && yN == 0 && wN >= 1 && hN >= 1))
                return src.Copy(SKColorType.Bgra8888);

            int x = Math.Clamp((int)(xN * src.Width), 0, src.Width - 1);
            int y = Math.Clamp((int)(yN * src.Height), 0, src.Height - 1);
            int w = Math.Clamp((int)(wN * src.Width), 1, src.Width - x);
            int h = Math.Clamp((int)(hN * src.Height), 1, src.Height - y);

            var dst = new SKBitmap(BgraInfo(w, h));
            using var subset = new SKBitmap();
            if (!src.ExtractSubset(subset, SKRectI.Create(x, y, w, h)))
            {
                dst.Dispose();
                throw new InvalidOperationException("SKBitmap.ExtractSubset failed for ROI crop.");
            }
            // ExtractSubset shares pixels with src; copy into the freshly-owned
            // dst so the caller's Dispose semantics match the old GDI path.
            if (!subset.CopyTo(dst, SKColorType.Bgra8888))
            {
                dst.Dispose();
                throw new InvalidOperationException("SKBitmap.CopyTo failed for ROI crop.");
            }
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
        public static byte[] ExtractPixels(SKBitmap bmp,
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
            if (bmp.ColorType != SKColorType.Bgra8888)
                throw new ArgumentException("BitmapSampler.ExtractPixels requires SKColorType.Bgra8888.", nameof(bmp));

            bool satFilter = minSaturation > 0f || maxSaturation < 1f;
            bool lumFilter = minLightness > 0f || maxLightness < 1f;
            bool salFilter = saliencyMap != null && saliencyThreshold > 0f
                              && saliencyMap.Length >= bmp.Width * bmp.Height;

            int total = bmp.Width * bmp.Height;
            byte[] outBuf = new byte[total * 3];
            int written = 0;

            IntPtr pixelsPtr = bmp.GetPixels();
            if (pixelsPtr == IntPtr.Zero)
                throw new InvalidOperationException("SKBitmap.GetPixels returned null pointer.");

            unsafe
            {
                byte* row = (byte*)pixelsPtr.ToPointer();
                int stride = bmp.RowBytes;
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

            pixelCount = written;
            if (written == total)
                return outBuf;

            byte[] trimmed = new byte[written * 3];
            Buffer.BlockCopy(outBuf, 0, trimmed, 0, written * 3);
            return trimmed;
        }

        /// <summary>
        /// Rotate / flip <paramref name="src"/> to match the visual
        /// orientation requested by <paramref name="origin"/>. Returns a
        /// fresh bitmap; the original is disposed when a transform is
        /// applied. No-op for Default / TopLeft.
        ///
        /// SKEncodedOrigin numeric values 1..8 match the EXIF tag 0x0112
        /// definition, so this is the SkiaSharp port of the old GDI
        /// ApplyExifOrientation switch — same semantics.
        ///
        /// Note: some TIFFs leave EncodedOrigin at Default even when their
        /// EXIF IFD carries an orientation tag. The prior System.Drawing
        /// path was equally inconsistent for TIFF; treated as acceptable
        /// parity.
        /// </summary>
        public static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
        {
            if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
                return src;

            int w = src.Width, h = src.Height;
            bool transpose = origin == SKEncodedOrigin.LeftTop
                          || origin == SKEncodedOrigin.RightTop
                          || origin == SKEncodedOrigin.RightBottom
                          || origin == SKEncodedOrigin.LeftBottom;
            int dw = transpose ? h : w;
            int dh = transpose ? w : h;

            var dst = new SKBitmap(BgraInfo(dw, dh));

            // Inverse mapping: for each dst pixel (xp, yp), find the source
            // pixel (sx, sy) it draws from. EXIF orientation semantics:
            //   1 TopLeft     identity
            //   2 TopRight    horizontal flip
            //   3 BottomRight 180 rotation
            //   4 BottomLeft  vertical flip
            //   5 LeftTop     transpose (swap axes)
            //   6 RightTop    90 CW
            //   7 RightBottom transverse (transpose + 180)
            //   8 LeftBottom  90 CCW
            IntPtr srcPtr = src.GetPixels();
            IntPtr dstPtr = dst.GetPixels();
            if (srcPtr == IntPtr.Zero || dstPtr == IntPtr.Zero)
            {
                dst.Dispose();
                throw new InvalidOperationException("SKBitmap.GetPixels returned null during orientation copy.");
            }

            int srcStride = src.RowBytes;
            int dstStride = dst.RowBytes;
            unsafe
            {
                byte* srcBase = (byte*)srcPtr.ToPointer();
                byte* dstBase = (byte*)dstPtr.ToPointer();
                for (int yp = 0; yp < dh; yp++)
                {
                    uint* dstRow = (uint*)(dstBase + yp * dstStride);
                    for (int xp = 0; xp < dw; xp++)
                    {
                        int sx, sy;
                        switch (origin)
                        {
                            case SKEncodedOrigin.TopRight:    sx = w - 1 - xp; sy = yp;             break;
                            case SKEncodedOrigin.BottomRight: sx = w - 1 - xp; sy = h - 1 - yp;     break;
                            case SKEncodedOrigin.BottomLeft:  sx = xp;         sy = h - 1 - yp;     break;
                            case SKEncodedOrigin.LeftTop:     sx = yp;         sy = xp;             break;
                            case SKEncodedOrigin.RightTop:    sx = yp;         sy = h - 1 - xp;     break;
                            case SKEncodedOrigin.RightBottom: sx = w - 1 - yp; sy = h - 1 - xp;     break;
                            case SKEncodedOrigin.LeftBottom:  sx = w - 1 - yp; sy = xp;             break;
                            default:                          sx = xp;         sy = yp;             break;
                        }
                        uint* srcPixel = (uint*)(srcBase + sy * srcStride + sx * 4);
                        dstRow[xp] = *srcPixel;
                    }
                }
            }
            src.Dispose();
            return dst;
        }
    }
}
