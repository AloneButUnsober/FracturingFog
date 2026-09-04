// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/SvgfMoments.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389 / #402): the TEMPORAL
// variance for SVGF. The variance-guided À-Trous (#645) currently reads a purely
// SPATIAL luminance variance of the accumulated frame; canonical SVGF instead
// tracks the first and second luminance MOMENTS (E[l], E[l²]) across frames and
// derives the variance from them (var = E[l²] − E[l]²). That temporal variance
// converges as the signal settles, so the filter tightens on genuinely converged
// pixels rather than on any locally-flat patch.
//
// This accumulates the moments with the SAME motion reprojection + disocclusion
// rejection as the colour history (SvgfTemporal): a pixel reads its previous
// moments from where it projected last frame, unless that projection is off-frame
// or the normal / depth disagree — then it RESETS to a single sample. It also
// tracks a per-pixel history LENGTH so the consumer can ramp from the spatial
// estimate (few samples) to the temporal one (many), the standard SVGF fix for
// under-filtered disocclusions.
//
// Pure, deterministic, parallel — the CPU-parity discipline the roadmap requires.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>Temporal luminance-moment accumulation for SVGF (roadmap S4, #402).</summary>
public static class SvgfMoments
{
    private static double Luma(uint c)
        => 0.299 * ((c >> 16) & 0xFF) / 255.0
         + 0.587 * ((c >> 8) & 0xFF) / 255.0
         + 0.114 * (c & 0xFF) / 255.0;

    /// <summary>Accumulate the per-pixel luminance moments of <paramref name="current"/>
    /// (BGRA) into the previous <paramref name="histM1"/>/<paramref name="histM2"/>
    /// (E[l], E[l²]; null = first frame) along <paramref name="motion"/> (w*h*2, where
    /// each pixel was last frame), blended by <paramref name="alpha"/> (history weight).
    /// A pixel whose reprojection is off-frame, or whose normal / depth disagrees with
    /// the reprojected history, RESETS to a single sample (m1 = l, m2 = l², length 1) —
    /// the same disocclusion rejection SvgfTemporal uses on the colour. Returns the new
    /// moments and the per-pixel history length (samples accumulated, capped at 255).</summary>
    public static (float[] m1, float[] m2, byte[] length) Accumulate(
        uint[] current, float[]? histM1, float[]? histM2, byte[]? histLen, float[]? motion,
        int w, int h, double alpha,
        float[]? curNormal = null, float[]? histNormal = null,
        float[]? curDepth = null, float[]? histDepth = null,
        double normalThreshold = 0.9, double depthRelThreshold = 0.1)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));
        long n = (long)w * h;
        if (current.Length < n) throw new ArgumentException("Accumulate: current smaller than width*height.");

        var m1 = new float[n];
        var m2 = new float[n];
        var len = new byte[n];

        double a = Math.Min(Math.Max(alpha, 0.0), 1.0);
        bool hasHist = histM1 != null && histM2 != null && histM1.Length >= n && histM2.Length >= n;
        bool hasLen = histLen != null && histLen.Length >= n;
        bool hasMotion = motion != null && motion.Length >= n * 2;
        bool hasN = curNormal != null && histNormal != null
                    && curNormal.Length >= n * 3 && histNormal.Length >= n * 3;
        bool hasZ = curDepth != null && histDepth != null
                    && curDepth.Length >= n && histDepth.Length >= n;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double l = Luma(current[i]);
                double l2 = l * l;

                bool reset = !hasHist;
                int j = i;
                if (hasHist)
                {
                    int sx = x, sy = y;
                    if (hasMotion)
                    {
                        sx = (int)Math.Round(x + motion![i * 2]);
                        sy = (int)Math.Round(y + motion[i * 2 + 1]);
                    }
                    if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) reset = true;
                    else
                    {
                        j = sy * w + sx;
                        if (hasN)
                        {
                            double dot = curNormal![i * 3] * histNormal![j * 3]
                                       + curNormal[i * 3 + 1] * histNormal[j * 3 + 1]
                                       + curNormal[i * 3 + 2] * histNormal[j * 3 + 2];
                            if (dot < normalThreshold) reset = true;
                        }
                        if (!reset && hasZ)
                        {
                            double cd = curDepth![i], hd = histDepth![j];
                            if (Math.Abs(cd - hd) / Math.Max(Math.Abs(cd), 1e-4) > depthRelThreshold)
                                reset = true;
                        }
                    }
                }

                if (reset)
                {
                    m1[i] = (float)l; m2[i] = (float)l2; len[i] = 1;
                }
                else
                {
                    m1[i] = (float)(l * (1.0 - a) + histM1![j] * a);
                    m2[i] = (float)(l2 * (1.0 - a) + histM2![j] * a);
                    int nl = (hasLen ? histLen![j] : 1) + 1;
                    len[i] = (byte)Math.Min(nl, 255);
                }
            }
        });
        return (m1, m2, len);
    }
}
