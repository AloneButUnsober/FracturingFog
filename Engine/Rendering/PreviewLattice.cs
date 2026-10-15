// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Threading.Tasks;
using FracturingFog.FFMath;

namespace FracturingFog.Rendering
{
    /// <summary>
    /// #1272 (SM-11 residual) — world-anchored progressive previews. A drag used
    /// to render each ¼ / ½ preview centred on the new view, so its coarse pixel
    /// grid sampled different points of the fractal every step and thin features
    /// flickered ("jumpy" drag, worst with fast palettes). <see cref="Snap"/> puts
    /// the preview centre on a lattice of whole preview texels from a fixed world
    /// anchor, so consecutive previews sample the same points; <see cref="Resample"/>
    /// then draws it on the screen with the exact sub-texel offset.
    /// </summary>
    public static class PreviewLattice
    {
        /// <summary>Preview texel coordinate of screen pixel (x, y) is
        /// (Ax + x·K, Ay + y·K).</summary>
        public sealed record Map(double Ax, double Ay, double K);

        /// <summary>Largest anchor offset (in preview texels) kept before the
        /// caller should restart the lattice, so the offset stays exact in double.</summary>
        public const double MaxOffsetTexels = 1e6;

        /// <summary>The preview centre on the anchor's lattice nearest the view
        /// centre, and the screen → texel map. Screen pixel x shows world
        /// centre + (x − w/2)·s; preview texel i sits at centre_p + (i − pw/2)·sp
        /// (the calculators' pixel convention). Null when the view is more than
        /// <see cref="MaxOffsetTexels"/> from the anchor (or the offset is not
        /// finite): restart the lattice at the view centre.</summary>
        public static (DeepComplex PreviewCentre, Map Map)? Snap(
            DeepComplex centre, DeepComplex anchor, double s, double sp, int w, int h, int pw, int ph)
        {
            // Offset from the anchor in preview texels (exact OD difference, #1319).
            double dx = (centre.Re - anchor.Re).X0 / sp;
            double dy = (centre.Im - anchor.Im).X0 / sp;
            if (!(Math.Abs(dx) < MaxOffsetTexels && Math.Abs(dy) < MaxOffsetTexels)) return null;
            double mx = Math.Round(dx), my = Math.Round(dy);
            var p = new DeepComplex(OD.FromCenterOffset(anchor.Re, mx, sp), OD.FromCenterOffset(anchor.Im, my, sp));
            // centre − centre_p = (dx − mx)·sp, so i = (dx − mx) + (x − w/2)·K + pw/2, K = s / sp.
            double k = s / sp;
            return (p, new Map((dx - mx) - w * 0.5 * k + pw * 0.5, (dy - my) - h * 0.5 * k + ph * 0.5, k));
        }

        /// <summary>Bilinear resample of a preview (sw × sh) to the screen (dw × dh)
        /// through <paramref name="map"/>, clamping at the preview edge. Channel-wise
        /// on packed BGRA, so alpha is resampled like the colour.</summary>
        public static void Resample(uint[] src, int sw, int sh, Map map, uint[] dst, int dw, int dh)
        {
            double maxU = sw - 1, maxV = sh - 1;
            Parallel.For(0, dh, y =>
            {
                double v = Math.Clamp(map.Ay + y * map.K, 0.0, maxV);
                int y0 = (int)v, y1 = Math.Min(y0 + 1, sh - 1);
                double fy = v - y0;
                int r0 = y0 * sw, r1 = y1 * sw, o = y * dw;
                for (int x = 0; x < dw; x++)
                {
                    double u = Math.Clamp(map.Ax + x * map.K, 0.0, maxU);
                    int x0 = (int)u, x1 = Math.Min(x0 + 1, sw - 1);
                    double fx = u - x0;
                    uint a = src[r0 + x0], b = src[r0 + x1], c = src[r1 + x0], d = src[r1 + x1];
                    uint res = 0;
                    for (int sh8 = 0; sh8 < 32; sh8 += 8)
                    {
                        double ta = (a >> sh8) & 0xFF, tb = (b >> sh8) & 0xFF;
                        double bc = (c >> sh8) & 0xFF, bd = (d >> sh8) & 0xFF;
                        double top = ta + (tb - ta) * fx, bot = bc + (bd - bc) * fx;
                        uint ch = (uint)(top + (bot - top) * fy + 0.5);
                        res |= Math.Min(ch, 255u) << sh8;
                    }
                    dst[o + x] = res;
                }
            });
        }
    }
}
