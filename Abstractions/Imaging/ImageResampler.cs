// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Abstractions.Imaging;

/// <summary>
/// Pure image-space resampling used by the video-slideshow Ken-Burns hold leg
/// (#806): bilinearly sample a moving sub-rect of an already-rendered frame into
/// a full-size destination, giving a "moving still" pan/zoom without recomputing
/// the fractal. Kept UI-free and side-effect-free so it can be unit-tested.
/// Buffers are packed 8-bits-per-channel (BGRA/RGBA — channel order is
/// irrelevant, each byte lane is interpolated independently).
/// </summary>
public static class ImageResampler
{
    /// <summary>
    /// Bilinearly resample the source sub-rect
    /// <c>[sx, sy] .. [sx + viewW, sy + viewH]</c> of a <paramref name="w"/>×
    /// <paramref name="h"/> image into a full <paramref name="w"/>×
    /// <paramref name="h"/> <paramref name="dst"/>. Sample coordinates are
    /// clamped to the source edges. <paramref name="dst"/> must hold at least
    /// <c>w*h</c> pixels.
    /// </summary>
    public static void ResampleRectBilinear(
        uint[] src, int w, int h, double sx, double sy, double viewW, double viewH, uint[] dst)
    {
        if (src == null || dst == null || w <= 0 || h <= 0) return;
        double stepX = viewW / w, stepY = viewH / h;
        for (int y = 0; y < h; y++)
        {
            double fy = sy + (y + 0.5) * stepY - 0.5;
            if (fy < 0) fy = 0; else if (fy > h - 1) fy = h - 1;
            int y0 = (int)fy; int y1 = y0 + 1 < h ? y0 + 1 : y0;
            double wy = fy - y0;
            int row0 = y0 * w, row1 = y1 * w, drow = y * w;
            for (int x = 0; x < w; x++)
            {
                double fx = sx + (x + 0.5) * stepX - 0.5;
                if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
                int x0 = (int)fx; int x1 = x0 + 1 < w ? x0 + 1 : x0;
                double wx = fx - x0;

                dst[drow + x] = BilerpPacked(
                    src[row0 + x0], src[row0 + x1], src[row1 + x0], src[row1 + x1], wx, wy);
            }
        }
    }

    /// <summary>Bilinear blend of four packed 8-bit-per-lane pixels.</summary>
    public static uint BilerpPacked(uint c00, uint c10, uint c01, uint c11, double wx, double wy)
    {
        uint outv = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            double a = ((c00 >> shift) & 0xFF) * (1 - wx) + ((c10 >> shift) & 0xFF) * wx;
            double b = ((c01 >> shift) & 0xFF) * (1 - wx) + ((c11 >> shift) & 0xFF) * wx;
            double v = a * (1 - wy) + b * wy;
            uint iv = (uint)(v + 0.5);
            if (iv > 255) iv = 255;
            outv |= iv << shift;
        }
        return outv;
    }
}
