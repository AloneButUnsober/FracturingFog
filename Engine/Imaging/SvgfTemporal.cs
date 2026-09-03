// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/SvgfTemporal.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389 / #402): the temporal
// half of SVGF (Schied et al. 2017, "Spatiotemporal Variance-Guided Filtering").
// The guided À-Trous denoiser (AtrousDenoiser) already filters spatially; SVGF's
// big win is accumulating the noisy signal ACROSS frames — reproject the previous
// accumulated frame along the motion-vector AOV (S1, #398) and blend it with the
// current frame, so Monte-Carlo noise (AO / soft shadow / reflections) averages
// down over time instead of being paid for by per-frame supersamples.
//
// The catch is disocclusion: where the surface a pixel now shows was hidden last
// frame (a silhouette edge, a fresh camera reveal), the reprojected history is
// wrong and must be REJECTED — otherwise it ghosts. Rejection keys on the same
// guides the spatial filter uses: an off-frame reprojection, a normal that no
// longer agrees, or a depth that jumped. A rejected pixel falls back to the
// current frame (alpha 0), the standard temporal-AA disocclusion fallback.
//
// Pure, deterministic, parallel (no RNG, no device state) → identical on the live
// path and under --batch. A null history (the first frame) or alpha 0 returns the
// current frame unchanged, so the default pipeline is byte-identical.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>SVGF temporal accumulation over the motion-vector AOV (roadmap S4).</summary>
public static class SvgfTemporal
{
    /// <summary>Blend <paramref name="current"/> (this frame's noisy BGRA) with the
    /// reprojected <paramref name="history"/> (the previous accumulated BGRA) along
    /// <paramref name="motion"/> (w*h*2 interleaved du,dv — where each pixel was last
    /// frame). <paramref name="alpha"/> is the history weight (0 = no accumulation =
    /// current unchanged; ~0.9 = a long, stable trail). Optional current/history
    /// <paramref name="normal"/> (w*h*3) and <paramref name="depth"/> (w*h) guides
    /// reject the history on a disocclusion (normal disagreement below
    /// <paramref name="normalThreshold"/>, or relative depth jump above
    /// <paramref name="depthRelThreshold"/>); a rejected or off-frame pixel keeps the
    /// current colour. Alpha is carried through from the current pixel. Returns a new
    /// buffer; inputs are not modified. A null history or alpha ≤ 0 returns a copy of
    /// <paramref name="current"/> unchanged.</summary>
    public static uint[] Accumulate(
        uint[] current, uint[]? history, float[]? motion, int w, int h, double alpha,
        float[]? curNormal = null, float[]? histNormal = null,
        float[]? curDepth = null, float[]? histDepth = null,
        double normalThreshold = 0.9, double depthRelThreshold = 0.1)
    {
        if (current == null) throw new ArgumentNullException(nameof(current));
        long n = (long)w * h;
        if (current.Length < n) throw new ArgumentException("Accumulate: current smaller than width*height.");
        var outp = (uint[])current.Clone();
        if (history == null || history.Length < n || alpha <= 0.0) return outp;

        double a = Math.Min(alpha, 1.0);
        double cw = 1.0 - a;   // current weight
        bool hasMotion = motion != null && motion.Length >= n * 2;
        bool hasNormal = curNormal != null && histNormal != null
                         && curNormal.Length >= n * 3 && histNormal.Length >= n * 3;
        bool hasDepth = curDepth != null && histDepth != null
                        && curDepth.Length >= n && histDepth.Length >= n;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                // Where this pixel's surface projected in the previous frame.
                int sx = x, sy = y;
                if (hasMotion)
                {
                    sx = (int)Math.Round(x + motion![i * 2]);
                    sy = (int)Math.Round(y + motion[i * 2 + 1]);
                }
                if ((uint)sx >= (uint)w || (uint)sy >= (uint)h)
                    continue;                     // off-frame → keep current (disocclusion)
                int j = sy * w + sx;

                // Disocclusion rejection on the geometry guides.
                if (hasNormal)
                {
                    double dot = curNormal![i * 3] * histNormal![j * 3]
                               + curNormal[i * 3 + 1] * histNormal[j * 3 + 1]
                               + curNormal[i * 3 + 2] * histNormal[j * 3 + 2];
                    if (dot < normalThreshold) continue;
                }
                if (hasDepth)
                {
                    double cd = curDepth![i], hd = histDepth![j];
                    double denom = Math.Max(Math.Abs(cd), 1e-4);
                    if (Math.Abs(cd - hd) / denom > depthRelThreshold) continue;
                }

                uint cc = current[i], hc = history[j];
                double r = ((cc >> 16) & 0xFF) * cw + ((hc >> 16) & 0xFF) * a;
                double g = ((cc >> 8) & 0xFF) * cw + ((hc >> 8) & 0xFF) * a;
                double b = (cc & 0xFF) * cw + (hc & 0xFF) * a;
                uint R = (uint)Math.Clamp(r + 0.5, 0, 255);
                uint G = (uint)Math.Clamp(g + 0.5, 0, 255);
                uint B = (uint)Math.Clamp(b + 0.5, 0, 255);
                outp[i] = (cc & 0xFF000000u) | (R << 16) | (G << 8) | B;
            }
        });
        return outp;
    }
}
