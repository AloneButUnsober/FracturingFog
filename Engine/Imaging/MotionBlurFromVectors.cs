// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/MotionBlurFromVectors.cs
//
// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389 / #398): the first
// consumer of the motion-vector AOV. The relief render fills a per-pixel screen-
// space motion vector (where each surface point was last frame); this smears the
// beauty along that vector to produce per-pixel motion blur — the fast, correct
// alternative to accumulating N fully-rendered sub-frames. A fast-moving silhouette
// streaks; a static background stays crisp; the cost is one cheap gather, not N
// raymarches.
//
// Pure, deterministic, separable-free gather (no RNG, no device state) → it runs
// identically on the live path and under --batch (the CPU-parity discipline the
// roadmap requires). Strength 0 (or a null / all-zero motion field) is the
// identity, so the default render is byte-for-byte unchanged.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>Per-pixel vector motion blur driven by the S1 motion-vector AOV.</summary>
public static class MotionBlurFromVectors
{
    /// <summary>Smear <paramref name="beauty"/> (straight-alpha BGRA, w*h) along the
    /// per-pixel screen motion vector in <paramref name="motion"/> (w*h*2, interleaved
    /// du,dv in pixels). Each output pixel averages <paramref name="samples"/> nearest-
    /// neighbour taps spread symmetrically along its motion vector scaled by
    /// <paramref name="strength"/> (1 = the full inter-frame displacement). Alpha is
    /// carried through from the centre pixel. Returns a new buffer; the input is not
    /// modified. Strength ≤ 0, samples ≤ 1, or a null motion field returns a copy
    /// unchanged; a pixel whose (scaled) motion is sub-pixel is copied straight.</summary>
    public static uint[] Apply(uint[] beauty, float[]? motion, int w, int h,
        double strength, int samples)
    {
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        long n = (long)w * h;
        if (beauty.Length < n) throw new ArgumentException("Apply: beauty smaller than width*height.");
        var outp = (uint[])beauty.Clone();
        if (motion == null || motion.Length < n * 2 || strength <= 0.0 || samples <= 1)
            return outp;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                double du = motion[idx * 2] * strength;
                double dv = motion[idx * 2 + 1] * strength;
                // Sub-pixel motion → no visible streak; keep the crisp pixel.
                if (Math.Abs(du) + Math.Abs(dv) < 1.0) continue;

                double sumR = 0, sumG = 0, sumB = 0;
                for (int k = 0; k < samples; k++)
                {
                    // Symmetric spread across the vector: t ∈ [-0.5, +0.5].
                    double t = (double)k / (samples - 1) - 0.5;
                    int sx = (int)Math.Round(x + du * t);
                    int sy = (int)Math.Round(y + dv * t);
                    if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                    if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                    uint c = beauty[sy * w + sx];
                    sumR += (c >> 16) & 0xFF;
                    sumG += (c >> 8) & 0xFF;
                    sumB += c & 0xFF;
                }
                double inv = 1.0 / samples;
                uint R = (uint)Math.Clamp(sumR * inv + 0.5, 0, 255);
                uint G = (uint)Math.Clamp(sumG * inv + 0.5, 0, 255);
                uint B = (uint)Math.Clamp(sumB * inv + 0.5, 0, 255);
                uint a = beauty[idx] & 0xFF000000u;           // keep the centre alpha
                outp[idx] = a | (R << 16) | (G << 8) | B;
            }
        });
        return outp;
    }
}
