// LowResPreview.cs
//
// P2 — shared low-res interactive preview helper. UserBulbCalculator already
// halves its render dims when LowResPreview is true; this helper extracts
// that pattern so the other six raymarchers (Mandelbulb, Mandelbox, KIFS,
// QJulia, QMandel, Bicomplex, Kleinian) can pick it up uniformly.
//
// Usage pattern in each calculator's Calculate:
//
//   bool lowRes = LowResPreview;
//   double scale = lowRes ? Math.Clamp(FractalParameters.LowResPreviewScale, 0.25, 1.0) : 1.0;
//   var dims = LowResPreview.ComputeDims(Width, Height, scale);
//   uint[] renderBuffer = lowRes ? new uint[dims.Width * dims.Height] : ColorBuffer;
//   // ... allocate depthBuf / normalBuf / hdrBuf at dims.Width × dims.Height ...
//   // ... raymarch into renderBuffer at dims dimensions ...
//   // ... post-passes at dims dimensions ...
//   if (lowRes) LowResPreview.UpscaleNearest(renderBuffer, dims.Width, dims.Height,
//                                            ColorBuffer, Width, Height);
//
// Trade-off: nearest-neighbour upscale shows blocky pixels under motion but
// stays cheap (one indexed read per output pixel). Bilinear would smooth the
// motion but smear filament detail. Matches the visual contract UserBulb has
// shipped with since its preview path landed.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Rendering;

public static class LowResPreview
{
    public readonly struct Dims
    {
        public int Width { get; }
        public int Height { get; }
        public Dims(int w, int h) { Width = w; Height = h; }
    }

    /// <summary>Compute the preview render dims for a full-res target at the
    /// given scale factor. Always &gt;= 1 in each axis so a tiny preview at a
    /// very small viewport still produces a valid buffer.</summary>
    public static Dims ComputeDims(int fullWidth, int fullHeight, double scale)
    {
        if (scale >= 1.0) return new Dims(fullWidth, fullHeight);
        int pw = Math.Max(1, (int)Math.Round(fullWidth * scale));
        int ph = Math.Max(1, (int)Math.Round(fullHeight * scale));
        return new Dims(pw, ph);
    }

    /// <summary>Nearest-neighbour upscale of <paramref name="src"/> into
    /// <paramref name="dst"/>. Both buffers are BGRA packed uint. Parallel-row
    /// driver — same shape as the UserBulb path.</summary>
    public static void UpscaleNearest(
        uint[] src, int srcW, int srcH,
        uint[] dst, int dstW, int dstH)
    {
        Parallel.For(0, dstH, y =>
        {
            int sy = Math.Min(srcH - 1, y * srcH / dstH);
            int sRow = sy * srcW;
            int dRow = y * dstW;
            for (int x = 0; x < dstW; x++)
            {
                int sx = Math.Min(srcW - 1, x * srcW / dstW);
                dst[dRow + x] = src[sRow + sx];
            }
        });
    }
}
