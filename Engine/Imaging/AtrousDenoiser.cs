// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AtrousDenoiser.cs
//
// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389): a guided, edge-
// avoiding À-Trous wavelet denoiser (Dammertz et al. 2010, "Edge-Avoiding
// À-Trous Wavelet Transform for fast Global Illumination Filtering"; the SVGF
// lineage). AO, soft shadow and reflections are Monte Carlo — noisy — and today
// FF pays that noise down with supersamples. A denoiser keyed on the normal +
// depth AOVs (S1) cuts the sample budget for equal quality: it smooths within a
// surface but stops at geometric edges, so detail survives while noise averages
// out.
//
// The filter is a pure, deterministic, separable-kernel pass — no RNG, no device
// state — so it runs identically on the live path and under --batch (the CPU-
// parity discipline the roadmap requires). It is the natural consumer of S1: the
// normal / depth guides are exactly the AOVs S1 promotes.
//
// This first slice is the operator + its edge-stopping weights, guided by
// OPTIONAL float normal / depth planes (null guides → a plain color-edge-stopping
// bilateral). Iterations == 0 is the identity, so the default render is
// byte-for-byte unchanged. Wiring the render's own float AOVs into the guides is
// the S1 float-AOV follow-up.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>À-Trous denoiser tuning. <see cref="Iterations"/> 0 = off (identity).</summary>
public sealed class AtrousParams
{
    /// <summary>Number of À-Trous passes; the tap dilation doubles each pass
    /// (1, 2, 4, …), so N passes reach a (2^(N+1)-1)-wide footprint. 0 = off.</summary>
    public int Iterations { get; set; }

    /// <summary>Color edge-stop. Smaller = sharper (stops at fainter color
    /// differences); larger = smoother. Squared-RGB distance in [0,1] units.</summary>
    public double ColorSigma { get; set; } = 0.10;

    /// <summary>Normal edge-stop (consulted only when a normal guide is given).
    /// Smaller = sharper geometric creases preserved.</summary>
    public double NormalSigma { get; set; } = 0.30;

    /// <summary>Depth edge-stop (consulted only when a depth guide is given).
    /// Smaller = sharper silhouettes preserved.</summary>
    public double DepthSigma { get; set; } = 0.20;
}

/// <summary>Guided edge-avoiding À-Trous wavelet denoiser (roadmap S4).</summary>
public static class AtrousDenoiser
{
    // Separable B3-spline 5-tap kernel; the 2D weights are the outer product.
    private static readonly double[] Kernel = { 1.0 / 16, 1.0 / 4, 3.0 / 8, 1.0 / 4, 1.0 / 16 };

    /// <summary>Denoise a straight-alpha BGRA buffer in edge-avoiding À-Trous
    /// passes. <paramref name="normalXyz"/> (w*h*3, components in [-1,1]) and
    /// <paramref name="depth"/> (w*h) are optional guides; null disables that
    /// edge-stop term (a null-guide run is a color-only bilateral). Alpha is
    /// carried through from the input untouched. Returns a new buffer; the input
    /// is not modified. Iterations ≤ 0 returns a copy unchanged.</summary>
    public static uint[] Denoise(uint[] color, int w, int h, AtrousParams p,
        float[]? normalXyz = null, float[]? depth = null)
    {
        if (color == null) throw new ArgumentNullException(nameof(color));
        long n = (long)w * h;
        if (color.Length < n) throw new ArgumentException("Denoise: color buffer smaller than width*height.");
        var outArgb = (uint[])color.Clone();
        if (p == null || p.Iterations <= 0) return outArgb;

        // Decode to float RGB planes (display space, /255) + keep alpha.
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        var alpha = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint c = color[i];
            alpha[i] = (c >> 24) & 0xFF;
            r[i] = ((c >> 16) & 0xFF) / 255f;
            g[i] = ((c >> 8) & 0xFF) / 255f;
            b[i] = (c & 0xFF) / 255f;
        }

        var tr = new float[n]; var tg = new float[n]; var tb = new float[n];

        double cSig = Math.Max(1e-6, p.ColorSigma);
        double nSig = Math.Max(1e-6, p.NormalSigma);
        double zSig = Math.Max(1e-6, p.DepthSigma);
        bool hasN = normalXyz != null && normalXyz.Length >= n * 3;
        bool hasZ = depth != null && depth.Length >= n;

        for (int it = 0; it < p.Iterations; it++)
        {
            int step = 1 << it;   // À-Trous hole size: 1, 2, 4, …
            // Each output pixel is independent within a pass — it reads the r/g/b
            // planes and writes only its own tr/tg/tb[pi], so rows run in parallel
            // with byte-identical results (per-pixel accumulation order is unchanged).
            // Parallel.For is synchronous: it completes before the ping-pong swap below
            // reassigns the plane refs, so the closure captures never race the swap.
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                int pi = y * w + x;
                float cr = r[pi], cg = g[pi], cb = b[pi];
                double nx = 0, ny = 0, nz = 0;
                if (hasN) { nx = normalXyz![pi * 3]; ny = normalXyz[pi * 3 + 1]; nz = normalXyz[pi * 3 + 2]; }
                double cz = hasZ ? depth![pi] : 0;

                double sumR = 0, sumG = 0, sumB = 0, cumW = 0;
                for (int ky = 0; ky < 5; ky++)
                for (int kx = 0; kx < 5; kx++)
                {
                    int sx = Clamp(x + (kx - 2) * step, 0, w - 1);
                    int sy = Clamp(y + (ky - 2) * step, 0, h - 1);
                    int qi = sy * w + sx;

                    // Color edge-stop (squared RGB distance).
                    double dr = cr - r[qi], dg = cg - g[qi], db = cb - b[qi];
                    double wCol = Math.Exp(-(dr * dr + dg * dg + db * db) / cSig);

                    double wNorm = 1.0;
                    if (hasN)
                    {
                        double dot = nx * normalXyz![qi * 3] + ny * normalXyz[qi * 3 + 1] + nz * normalXyz[qi * 3 + 2];
                        if (dot < 0) dot = 0;
                        wNorm = Math.Exp(-(1.0 - dot) / nSig);
                    }

                    double wDepth = 1.0;
                    if (hasZ)
                    {
                        double dz = cz - depth![qi];
                        wDepth = Math.Exp(-Math.Abs(dz) / zSig);
                    }

                    double wgt = Kernel[kx] * Kernel[ky] * wCol * wNorm * wDepth;
                    sumR += r[qi] * wgt; sumG += g[qi] * wgt; sumB += b[qi] * wgt;
                    cumW += wgt;
                }

                if (cumW > 0)
                {
                    tr[pi] = (float)(sumR / cumW);
                    tg[pi] = (float)(sumG / cumW);
                    tb[pi] = (float)(sumB / cumW);
                }
                else { tr[pi] = cr; tg[pi] = cg; tb[pi] = cb; }
                }
            });

            // Ping-pong: the filtered result feeds the next (wider) pass.
            (r, tr) = (tr, r);
            (g, tg) = (tg, g);
            (b, tb) = (tb, b);
        }

        for (int i = 0; i < n; i++)
        {
            byte R = (byte)Math.Clamp(r[i] * 255f + 0.5f, 0f, 255f);
            byte G = (byte)Math.Clamp(g[i] * 255f + 0.5f, 0f, 255f);
            byte B = (byte)Math.Clamp(b[i] * 255f + 0.5f, 0f, 255f);
            outArgb[i] = (alpha[i] << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
        }
        return outArgb;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
}
