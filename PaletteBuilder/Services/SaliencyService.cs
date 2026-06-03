// Services/SaliencyService.cs
//
// Spectral residual saliency (Hou & Zhang 2007). Converts an RGB scanline
// buffer to a normalised per-pixel saliency map in [0,1]: high → likely
// subject, low → likely background. Used by BitmapSampler to drop
// background pixels before clustering so the palette is drawn from the
// interesting parts of the image.
//
// Algorithm:
//   1. Luminance Y = 0.2126·R + 0.7152·G + 0.0722·B, normalised to [0,1].
//   2. 2-D FFT.
//   3. Take log of magnitude; phase preserved.
//   4. Local-average (3×3 mean) of log magnitude → smoothed spectrum.
//   5. Spectral residual = log magnitude − smoothed.
//   6. Reconstruct complex spectrum with exp(residual) × e^iφ.
//   7. Inverse FFT; square magnitude.
//   8. Gaussian blur (σ ≈ 4).
//   9. Normalise to [0,1].
//
// FFT runs on whatever dimensions BitmapSampler.Downsample produced — no
// power-of-2 padding because MathNet's mixed-radix 1-D FFT handles
// arbitrary sizes and the downsampled images are tiny (≤ 256² typical).
//
// Note: MathNet 5.0.0's managed provider throws NotSupportedException on
// Fourier.Forward2D / Inverse2D — multi-dim FFT requires the native MKL
// provider. We avoid the native dependency by composing the 2-D FFT
// ourselves: 1-D row-wise pass, then 1-D column-wise pass (the standard
// separability identity for 2-D DFT).

using System;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace PaletteBuilder.Services
{
    public static class SaliencyService
    {
        /// <summary>
        /// Compute a saliency map for the given RGB scanline buffer.
        /// Returns a float[width*height] in [0,1] where higher = more salient.
        /// </summary>
        public static float[] Compute(byte[] rgb, int width, int height)
        {
            int n = width * height;
            if (n == 0 || rgb.Length < n * 3)
                return Array.Empty<float>();

            // Step 1 — luminance
            var c = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                double y = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                c[i] = new Complex(y, 0);
            }

            // Step 2 — forward 2-D FFT (separable rows then columns)
            Forward2DSeparable(c, width, height);

            // Step 3-5 — log-amplitude, smoothed, residual
            var logAmp = new float[n];
            var phase = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = c[i].Magnitude;
                logAmp[i] = (float)Math.Log(a + 1e-9);
                phase[i] = (float)c[i].Phase;
            }

            var smoothed = MeanFilter3x3(logAmp, width, height);
            var residual = new float[n];
            for (int i = 0; i < n; i++) residual[i] = logAmp[i] - smoothed[i];

            // Step 6 — reconstruct
            for (int i = 0; i < n; i++)
            {
                double mag = Math.Exp(residual[i]);
                c[i] = new Complex(mag * Math.Cos(phase[i]), mag * Math.Sin(phase[i]));
            }

            // Step 7 — inverse FFT, magnitude squared
            Inverse2DSeparable(c, width, height);
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                double m = c[i].Magnitude;
                s[i] = (float)(m * m);
            }

            // Step 8 — Gaussian blur σ ≈ 4
            GaussianBlurSeparable(s, width, height, sigma: 4f);

            // Step 9 — normalise
            float max = 0;
            for (int i = 0; i < n; i++) if (s[i] > max) max = s[i];
            if (max > 0)
                for (int i = 0; i < n; i++) s[i] /= max;

            return s;
        }

        // ── Separable 2-D FFT (rows then columns) ──────────────────────────
        //
        // Builds the 2-D DFT as F(u,v) = Σ_x Σ_y f(x,y) e^{-2πi(ux/W + vy/H)}
        // by first doing a 1-D FFT along each row (transforming x → u) then
        // a 1-D FFT along each column of the result (transforming y → v).
        // The 1-D Fourier.Forward / Inverse calls in MathNet's managed
        // provider are fully implemented for arbitrary lengths.
        //
        // FourierOptions.AsymmetricScaling (the .Default value) does the
        // unscaled forward and 1/N-scaled inverse — same convention as
        // numpy.fft.fft2 / ifft2, so composing it row-wise then column-wise
        // gives the correct 2-D transform.

        private static void Forward2DSeparable(Complex[] data, int width, int height)
        {
            var row = new Complex[width];
            for (int y = 0; y < height; y++)
            {
                int off = y * width;
                Array.Copy(data, off, row, 0, width);
                Fourier.Forward(row, FourierOptions.Default);
                Array.Copy(row, 0, data, off, width);
            }

            var col = new Complex[height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) col[y] = data[y * width + x];
                Fourier.Forward(col, FourierOptions.Default);
                for (int y = 0; y < height; y++) data[y * width + x] = col[y];
            }
        }

        private static void Inverse2DSeparable(Complex[] data, int width, int height)
        {
            var col = new Complex[height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) col[y] = data[y * width + x];
                Fourier.Inverse(col, FourierOptions.Default);
                for (int y = 0; y < height; y++) data[y * width + x] = col[y];
            }

            var row = new Complex[width];
            for (int y = 0; y < height; y++)
            {
                int off = y * width;
                Array.Copy(data, off, row, 0, width);
                Fourier.Inverse(row, FourierOptions.Default);
                Array.Copy(row, 0, data, off, width);
            }
        }

        // ── 3×3 mean filter (separable not worth at this kernel size) ──────

        private static float[] MeanFilter3x3(float[] src, int w, int h)
        {
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                int yPrev = Math.Max(0, y - 1);
                int yNext = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int xPrev = Math.Max(0, x - 1);
                    int xNext = Math.Min(w - 1, x + 1);
                    float sum =
                        src[yPrev * w + xPrev] + src[yPrev * w + x] + src[yPrev * w + xNext] +
                        src[y * w + xPrev] + src[y * w + x] + src[y * w + xNext] +
                        src[yNext * w + xPrev] + src[yNext * w + x] + src[yNext * w + xNext];
                    dst[y * w + x] = sum / 9f;
                }
            }
            return dst;
        }

        // ── Separable Gaussian blur ────────────────────────────────────────

        private static void GaussianBlurSeparable(float[] buf, int w, int h, float sigma)
        {
            int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
            var kernel = new float[radius * 2 + 1];
            float twoSigma2 = 2 * sigma * sigma;
            float kSum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                float v = (float)Math.Exp(-(i * i) / twoSigma2);
                kernel[i + radius] = v;
                kSum += v;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= kSum;

            var tmp = new float[buf.Length];

            // Horizontal pass
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xk = Math.Clamp(x + k, 0, w - 1);
                        acc += buf[row + xk] * kernel[k + radius];
                    }
                    tmp[row + x] = acc;
                }
            }

            // Vertical pass
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yk = Math.Clamp(y + k, 0, h - 1);
                        acc += tmp[yk * w + x] * kernel[k + radius];
                    }
                    buf[y * w + x] = acc;
                }
            }
        }
    }
}
