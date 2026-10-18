// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrVerifier.cs
//
// #1305 / S1 (#1396) - does a composited fractal QR actually scan? Decodes
// with ZXing.Net, an implementation independent of QrEncoder, so success is a
// real invariant (decoded payload == intended payload), not self-consistency.
//
// One clean decode is not enough: a phone sees the code smaller, softer and
// compressed. Each pass degrades the image the way a camera / messenger would
// and decodes again; the code "scans" only when every pass returns the payload.

using System;
using System.Collections.Generic;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>Outcome of one decode pass.</summary>
    public sealed record QrVerifyPass(string Name, bool Ok, string? Decoded);

    /// <summary>Outcome of <see cref="QrVerifier.Verify"/>.</summary>
    public sealed record QrVerifyResult(IReadOnlyList<QrVerifyPass> Passes)
    {
        public bool Ok
        {
            get
            {
                foreach (var p in Passes) if (!p.Ok) return false;
                return Passes.Count > 0;
            }
        }

        /// <summary>One-line summary, e.g. "scans (4/4 passes)" or
        /// "does NOT scan: failed blur, jpeg".</summary>
        public string Summary
        {
            get
            {
                var failed = new List<string>();
                foreach (var p in Passes) if (!p.Ok) failed.Add(p.Name);
                return failed.Count == 0
                    ? $"scans ({Passes.Count}/{Passes.Count} passes)"
                    : $"does NOT scan: failed {string.Join(", ", failed)} ({Passes.Count - failed.Count}/{Passes.Count} passed)";
            }
        }
    }

    public static class QrVerifier
    {
        /// <summary>Longest side the full-resolution pass decodes at; larger
        /// images are box-downscaled first (a phone never sees more).</summary>
        public const int MaxDecodeSide = 2048;
        /// <summary>Module size (px) the "small" pass shrinks the code to.</summary>
        public const double SmallModulePx = 3.0;
        public const int JpegQuality = 70;

        /// <summary>Decode <paramref name="bgra"/> (straight-alpha BGRA / ARGB
        /// uint, composited over white) once per robustness pass.
        /// <paramref name="modulePx"/> is the module size in this image (0 =
        /// unknown, skips the module-relative passes).</summary>
        public static QrVerifyResult Verify(uint[] bgra, int w, int h, string expected, double modulePx = 0)
        {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            if (w <= 0 || h <= 0 || bgra.Length < w * h) throw new ArgumentException("Bad image dimensions.");

            byte[] luma = Luma(bgra, w, h);
            int lw = w, lh = h;
            double mpx = modulePx;
            int longest = Math.Max(w, h);
            if (longest > MaxDecodeSide)
            {
                double s = (double)MaxDecodeSide / longest;
                (luma, lw, lh) = Resize(luma, w, h, Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
                mpx *= s;
            }

            var passes = new List<QrVerifyPass>();
            passes.Add(Pass("full", luma, lw, lh, expected));

            if (mpx > SmallModulePx * 1.5)
            {
                double s = SmallModulePx / mpx;
                var (sm, sw, sh) = Resize(luma, lw, lh, Math.Max(21, (int)Math.Round(lw * s)), Math.Max(21, (int)Math.Round(lh * s)));
                passes.Add(Pass("small", sm, sw, sh, expected));
            }

            // Soft focus: box blur a quarter-module wide (at least 1 px).
            int radius = Math.Max(1, (int)Math.Round(mpx > 0 ? mpx / 4 : 1));
            passes.Add(Pass("blur", BoxBlur(luma, lw, lh, radius), lw, lh, expected));

            passes.Add(Pass("jpeg", JpegRoundTrip(luma, lw, lh, JpegQuality), lw, lh, expected));
            return new QrVerifyResult(passes);
        }

        /// <summary>Single plain decode; null when nothing decodes.</summary>
        public static string? Decode(uint[] bgra, int w, int h) => Decode(Luma(bgra, w, h), w, h);

        private static QrVerifyPass Pass(string name, byte[] luma, int w, int h, string expected)
        {
            string? text = Decode(luma, w, h);
            return new QrVerifyPass(name, text == expected, text);
        }

        private static string? Decode(byte[] luma, int w, int h)
        {
            var hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.TRY_HARDER] = true,
                [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                [DecodeHintType.CHARACTER_SET] = "UTF-8",
            };
            var reader = new QRCodeReader();
            var source = new RGBLuminanceSource(luma, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
            foreach (var binarizer in new Binarizer[] { new HybridBinarizer(source), new GlobalHistogramBinarizer(source) })
            {
                try
                {
                    var r = reader.decode(new BinaryBitmap(binarizer), hints);
                    if (r?.Text != null) return r.Text;
                }
                catch (ReaderException) { }
                finally { reader.reset(); }
            }
            // Light-on-dark codes: retry inverted.
            var inv = new InvertedLuminanceSource(source);
            try
            {
                var r = reader.decode(new BinaryBitmap(new HybridBinarizer(inv)), hints);
                return r?.Text;
            }
            catch (ReaderException) { return null; }
        }

        /// <summary>BT.601 luma of straight-alpha BGRA composited over white -
        /// the same weighting ZXing and camera Y planes use.</summary>
        public static byte[] Luma(uint[] bgra, int w, int h)
        {
            var y = new byte[w * h];
            for (int i = 0; i < w * h; i++) y[i] = LumaOf(bgra[i]);
            return y;
        }

        public static byte LumaOf(uint p)
        {
            int a = (int)(p >> 24), r = (int)(p >> 16) & 0xFF, g = (int)(p >> 8) & 0xFF, b = (int)p & 0xFF;
            int l = (299 * r + 587 * g + 114 * b + 500) / 1000;
            if (a < 255) l = (l * a + 255 * (255 - a) + 127) / 255;
            return (byte)l;
        }

        // Area-average resize (downscale) / nearest (upscale) of an 8-bit plane.
        private static (byte[] Data, int W, int H) Resize(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    long sum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++) sum += src[row + xx];
                    }
                    dst[y * dw + x] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
                }
            }
            return (dst, dw, dh);
        }

        private static byte[] BoxBlur(byte[] src, int w, int h, int r)
        {
            var tmp = new byte[src.Length];
            var dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = 0, n = 0;
                    for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) { s += src[y * w + k]; n++; }
                    tmp[y * w + x] = (byte)(s / n);
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = 0, n = 0;
                    for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { s += tmp[k * w + x]; n++; }
                    dst[y * w + x] = (byte)(s / n);
                }
            return dst;
        }

        private static byte[] JpegRoundTrip(byte[] luma, int w, int h, int quality)
        {
            var info = new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
            using var bmp = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(luma, 0, bmp.GetPixels(), luma.Length);
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            using var decoded = SKBitmap.Decode(data);
            if (decoded == null) return luma;
            var outInfo = new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
            using var gray = new SKBitmap(outInfo);
            if (!decoded.CopyTo(gray, SKColorType.Gray8)) return luma;
            var result = new byte[w * h];
            System.Runtime.InteropServices.Marshal.Copy(gray.GetPixels(), result, 0, result.Length);
            return result;
        }

        /// <summary>Read a saved image file back into BGRA for verification.
        /// Null when Skia cannot decode the format (e.g. EXR).</summary>
        public static uint[]? LoadBgra(string path, out int w, out int h)
        {
            w = h = 0;
            using var bmp = SKBitmap.Decode(path);
            if (bmp == null) return null;
            w = bmp.Width; h = bmp.Height;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var conv = new SKBitmap(info);
            if (!bmp.CopyTo(conv, SKColorType.Bgra8888)) return null;
            var px = new uint[w * h];
            unsafe
            {
                fixed (uint* dst = px)
                    Buffer.MemoryCopy((void*)conv.GetPixels(), dst, (long)w * h * 4, (long)w * h * 4);
            }
            return px;
        }
    }
}
