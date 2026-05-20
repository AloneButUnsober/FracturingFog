// Imaging/PaletteExtraction/BitmapSampler.cs
//
// Pull a flat byte[] of RGB pixels from a Bitmap, optionally downsampled
// for speed and with near-black/near-white filtering applied. Downsampling
// is a big quality+speed win: clustering a 4MP image is 100x slower than
// clustering a 256x256 thumbnail with no noticeable palette change.

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
        /// Extracts a flat byte array of [R,G,B,R,G,B,…] from the bitmap,
        /// dropping near-black and near-white pixels if requested.
        /// </summary>
        public static byte[] ExtractPixels(Bitmap bmp,
                                           bool excludeBlack,
                                           bool excludeWhite,
                                           out int pixelCount,
                                           int blackThreshold = 24,
                                           int whiteThreshold = 240)
        {
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
                            byte b = p[0], g = p[1], r = p[2];
                            p += 4;

                            if (excludeBlack && r <= blackThreshold && g <= blackThreshold && b <= blackThreshold)
                                continue;
                            if (excludeWhite && r >= whiteThreshold && g >= whiteThreshold && b >= whiteThreshold)
                                continue;

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
    }
}
