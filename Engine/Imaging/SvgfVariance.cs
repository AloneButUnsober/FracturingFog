// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/SvgfVariance.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389 / #402): the variance
// estimate that guides SVGF's À-Trous filter. SVGF loosens the colour edge-stop
// where the signal is still noisy (unconverged) and tightens it where it has
// settled, so it filters aggressively exactly where noise lives without smearing
// converged detail. The driver is a per-pixel LUMINANCE VARIANCE.
//
// The canonical SVGF variance is temporal — the running variance of the temporally
// accumulated luminance (E[l²] − E[l]²), spatially padded with a small filter for
// the first few frames. This ships the two building blocks:
//   * EstimateSpatial — a local-neighbourhood luminance variance from a single
//     frame (the still-image / no-history fallback, and the spatial pad).
//   * FromMoments — variance from accumulated first/second luminance moments (the
//     temporal path, once SvgfTemporal also accumulates the moments).
// Both are pure, deterministic and parallel. The output feeds AtrousDenoiser's
// `variance` guide.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>Per-pixel luminance-variance estimation for SVGF (roadmap S4, #402).</summary>
public static class SvgfVariance
{
    // Rec. 601 luma; matches the perceptual weighting the colour edge-stop cares about.
    private static double Luma(uint c)
        => 0.299 * ((c >> 16) & 0xFF) / 255.0
         + 0.587 * ((c >> 8) & 0xFF) / 255.0
         + 0.114 * (c & 0xFF) / 255.0;

    /// <summary>Local luminance variance over a (2·<paramref name="radius"/>+1)² window
    /// of <paramref name="color"/> (straight-alpha BGRA). Flat regions → ~0; noisy or
    /// edge regions → &gt;0. The window is clamped at the image border. Deterministic.</summary>
    public static float[] EstimateSpatial(uint[] color, int w, int h, int radius = 1)
    {
        if (color == null) throw new ArgumentNullException(nameof(color));
        long n = (long)w * h;
        if (color.Length < n) throw new ArgumentException("EstimateSpatial: color smaller than width*height.");
        if (radius < 1) radius = 1;

        // Decode luminance once so the window pass doesn't re-unpack each tap.
        var lum = new double[n];
        for (int i = 0; i < n; i++) lum[i] = Luma(color[i]);

        var outVar = new float[n];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0, sum2 = 0; int cnt = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int sy = y + dy;
                    if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                    int row = sy * w;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx;
                        if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                        double l = lum[row + sx];
                        sum += l; sum2 += l * l; cnt++;
                    }
                }
                double mean = sum / cnt;
                double var = sum2 / cnt - mean * mean;
                outVar[y * w + x] = (float)Math.Max(0.0, var);   // clamp tiny negatives
            }
        });
        return outVar;
    }

    /// <summary>Variance from accumulated luminance moments: <c>var = E[l²] − E[l]²</c>,
    /// per pixel, given the first-moment <paramref name="moment1"/> (mean luminance) and
    /// second-moment <paramref name="moment2"/> (mean of luminance²) buffers (each w*h).
    /// This is the temporal SVGF variance once a history accumulates the moments.
    /// Deterministic; negatives (numerical) are clamped to 0.</summary>
    public static float[] FromMoments(float[] moment1, float[] moment2, int w, int h)
    {
        if (moment1 == null) throw new ArgumentNullException(nameof(moment1));
        if (moment2 == null) throw new ArgumentNullException(nameof(moment2));
        long n = (long)w * h;
        if (moment1.Length < n || moment2.Length < n)
            throw new ArgumentException("FromMoments: moment buffer smaller than width*height.");

        var outVar = new float[n];
        Parallel.For(0, (int)n, i =>
        {
            double m1 = moment1[i];
            double v = moment2[i] - m1 * m1;
            outVar[i] = (float)Math.Max(0.0, v);
        });
        return outVar;
    }
}
