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
using System.Numerics;
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

    /// <summary>Opt-in SIMD path (roadmap S4, #402). Vectorizes the per-pass gather
    /// over 8 pixels at a time with a fast poly-exp. NOT byte-identical to the scalar
    /// oracle — it works in float32 and reorders the accumulation — so it is off by
    /// default and the scalar path stays the <c>--batch</c> parity reference. Falls
    /// back to scalar automatically when SIMD isn't hardware-accelerated.</summary>
    public bool UseSimd { get; set; }
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
    /// is not modified. Iterations ≤ 0 returns a copy unchanged.
    /// <para>SVGF variance guiding (roadmap S4, #402): when <paramref name="variance"/>
    /// (w*h, per-pixel luminance variance) is supplied AND <paramref name="varianceScale"/>
    /// &gt; 0, the colour edge-stop is loosened where the estimate is noisy — the effective
    /// colour sigma scales by <c>1 + varianceScale·sqrt(variance)</c>, so high-variance
    /// (unconverged) pixels blur MORE while low-variance (converged) detail is preserved.
    /// A null variance or scale 0 leaves the colour weight exactly as before
    /// (byte-identical).</para></summary>
    public static uint[] Denoise(uint[] color, int w, int h, AtrousParams p,
        float[]? normalXyz = null, float[]? depth = null,
        float[]? variance = null, double varianceScale = 0.0)
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
        bool hasVar = variance != null && variance.Length >= n && varianceScale > 0.0;

        // Opt-in SIMD path (#402). float32 + reordered accumulation → not byte-identical,
        // so it is gated and the scalar path below stays the parity oracle. The normal
        // guide is interleaved xyz per pixel, which a contiguous vector load can't read,
        // so de-interleave it into three planar arrays once (amortised over the passes).
        bool useSimd = p.UseSimd && Vector.IsHardwareAccelerated && Vector<float>.Count >= 4;
        float[]? nX = null, nY = null, nZ = null;
        if (useSimd && hasN)
        {
            nX = new float[n]; nY = new float[n]; nZ = new float[n];
            for (long i = 0; i < n; i++) { nX[i] = normalXyz![i * 3]; nY[i] = normalXyz[i * 3 + 1]; nZ[i] = normalXyz[i * 3 + 2]; }
        }

        for (int it = 0; it < p.Iterations; it++)
        {
            int step = 1 << it;   // À-Trous hole size: 1, 2, 4, …
            if (useSimd)
            {
                SimdPass(step, w, h, r, g, b, tr, tg, tb, hasN, nX, nY, nZ, hasZ, depth,
                    hasVar, variance, cSig, nSig, zSig, varianceScale);
                (r, tr) = (tr, r); (g, tg) = (tg, g); (b, tb) = (tb, b);
                continue;
            }
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
                // SVGF variance guiding (#402): loosen the colour edge-stop where the
                // per-pixel variance estimate is high. hasVar false → cSig unchanged.
                double cSigPix = hasVar
                    ? cSig * (1.0 + varianceScale * Math.Sqrt(Math.Max(0.0, variance![pi])))
                    : cSig;

                double sumR = 0, sumG = 0, sumB = 0, cumW = 0;
                for (int ky = 0; ky < 5; ky++)
                for (int kx = 0; kx < 5; kx++)
                {
                    int sx = Clamp(x + (kx - 2) * step, 0, w - 1);
                    int sy = Clamp(y + (ky - 2) * step, 0, h - 1);
                    int qi = sy * w + sx;

                    // Color edge-stop (squared RGB distance), variance-scaled sigma.
                    double dr = cr - r[qi], dg = cg - g[qi], db = cb - b[qi];
                    double wCol = Math.Exp(-(dr * dr + dg * dg + db * db) / cSigPix);

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

    // ── SIMD path (#402) — opt-in, not byte-identical (float32 + poly-exp) ──────

    // Fast 2^f polynomial exp for x ≤ 0 (all À-Trous weight args are ≤ 0). Scalar
    // and vector share the SAME approximation so the SIMD interior + its scalar
    // borders are self-consistent.
    private const float Log2eF = 1.442695041f;
    private const float PC1 = 0.6931472f, PC2 = 0.2402265f, PC3 = 0.0555041f;

    private static float SFExp(float x)
    {
        if (x < -60f) x = -60f;
        float t = x * Log2eF;
        float fl = MathF.Floor(t);
        float f = t - fl;
        float poly = 1f + f * (PC1 + f * (PC2 + f * PC3));
        int e = (int)fl + 127;
        return poly * BitConverter.Int32BitsToSingle(e << 23);
    }

    private static Vector<float> VExp(Vector<float> x)
    {
        x = Vector.Max(x, new Vector<float>(-60f));
        var t = x * new Vector<float>(Log2eF);
        var fl = Vector.Floor(t);
        var f = t - fl;
        var poly = Vector<float>.One + f * (new Vector<float>(PC1) + f * (new Vector<float>(PC2) + f * new Vector<float>(PC3)));
        var e = Vector.ConvertToInt32(fl) + new Vector<int>(127);
        var pow2 = Vector.AsVectorSingle(Vector.ShiftLeft(e, 23));
        return poly * pow2;
    }

    private static void SimdPass(int step, int w, int h,
        float[] r, float[] g, float[] b, float[] tr, float[] tg, float[] tb,
        bool hasN, float[]? nX, float[]? nY, float[]? nZ,
        bool hasZ, float[]? depth, bool hasVar, float[]? variance,
        double cSig, double nSig, double zSig, double varianceScale)
    {
        int lanes = Vector<float>.Count;
        float cSigF = (float)cSig, invNSig = (float)(1.0 / nSig), invZSig = (float)(1.0 / zSig), varScaleF = (float)varianceScale;

        Parallel.For(0, h, y =>
        {
            void PixelScalar(int x)
            {
                int pi = y * w + x;
                float cr = r[pi], cg = g[pi], cb = b[pi];
                float nx = 0, ny = 0, nz = 0;
                if (hasN) { nx = nX![pi]; ny = nY![pi]; nz = nZ![pi]; }
                float cz = hasZ ? depth![pi] : 0f;
                float invCSig = hasVar ? 1f / (cSigF * (1f + varScaleF * MathF.Sqrt(MathF.Max(0f, variance![pi])))) : 1f / cSigF;

                float sumR = 0, sumG = 0, sumB = 0, cumW = 0;
                for (int ky = 0; ky < 5; ky++)
                {
                    int sy = Clamp(y + (ky - 2) * step, 0, h - 1);
                    for (int kx = 0; kx < 5; kx++)
                    {
                        int sx = Clamp(x + (kx - 2) * step, 0, w - 1);
                        int qi = sy * w + sx;
                        float dr = cr - r[qi], dg = cg - g[qi], db = cb - b[qi];
                        float wCol = SFExp(-(dr * dr + dg * dg + db * db) * invCSig);
                        float wNorm = 1f;
                        if (hasN) { float dot = nx * nX![qi] + ny * nY![qi] + nz * nZ![qi]; if (dot < 0f) dot = 0f; wNorm = SFExp((dot - 1f) * invNSig); }
                        float wDepth = 1f;
                        if (hasZ) { float dz = cz - depth![qi]; wDepth = SFExp(-MathF.Abs(dz) * invZSig); }
                        float wgt = (float)(Kernel[kx] * Kernel[ky]) * wCol * wNorm * wDepth;
                        sumR += r[qi] * wgt; sumG += g[qi] * wgt; sumB += b[qi] * wgt; cumW += wgt;
                    }
                }
                if (cumW > 0f) { tr[pi] = sumR / cumW; tg[pi] = sumG / cumW; tb[pi] = sumB / cumW; }
                else { tr[pi] = cr; tg[pi] = cg; tb[pi] = cb; }
            }

            void PixelVector(int x)
            {
                int pi = y * w + x;
                var cr = new Vector<float>(r, pi); var cg = new Vector<float>(g, pi); var cb = new Vector<float>(b, pi);
                Vector<float> nx = default, ny = default, nz = default;
                if (hasN) { nx = new Vector<float>(nX!, pi); ny = new Vector<float>(nY!, pi); nz = new Vector<float>(nZ!, pi); }
                var cz = hasZ ? new Vector<float>(depth!, pi) : Vector<float>.Zero;
                Vector<float> invCSig = hasVar
                    ? Vector<float>.One / (new Vector<float>(cSigF) * (Vector<float>.One + new Vector<float>(varScaleF) * Vector.SquareRoot(Vector.Max(Vector<float>.Zero, new Vector<float>(variance!, pi)))))
                    : new Vector<float>(1f / cSigF);
                var vInvN = new Vector<float>(invNSig);
                var vInvZ = new Vector<float>(invZSig);

                Vector<float> sumR = default, sumG = default, sumB = default, cumW = default;
                for (int ky = 0; ky < 5; ky++)
                {
                    int sy = Clamp(y + (ky - 2) * step, 0, h - 1);
                    int rowBase = sy * w + x;
                    for (int kx = 0; kx < 5; kx++)
                    {
                        int qb = rowBase + (kx - 2) * step;   // interior → all lanes in-bounds
                        var qr = new Vector<float>(r, qb); var qg = new Vector<float>(g, qb); var qbv = new Vector<float>(b, qb);
                        var dr = cr - qr; var dg = cg - qg; var db = cb - qbv;
                        var wCol = VExp(-(dr * dr + dg * dg + db * db) * invCSig);
                        var wNorm = Vector<float>.One;
                        if (hasN)
                        {
                            var dot = nx * new Vector<float>(nX!, qb) + ny * new Vector<float>(nY!, qb) + nz * new Vector<float>(nZ!, qb);
                            dot = Vector.Max(Vector<float>.Zero, dot);
                            wNorm = VExp((dot - Vector<float>.One) * vInvN);
                        }
                        var wDepth = Vector<float>.One;
                        if (hasZ) { var dz = cz - new Vector<float>(depth!, qb); wDepth = VExp(-Vector.Abs(dz) * vInvZ); }
                        var wgt = new Vector<float>((float)(Kernel[kx] * Kernel[ky])) * wCol * wNorm * wDepth;
                        sumR += qr * wgt; sumG += qg * wgt; sumB += qbv * wgt; cumW += wgt;
                    }
                }
                var pos = Vector.GreaterThan(cumW, Vector<float>.Zero);
                var invW = Vector<float>.One / cumW;
                Vector.ConditionalSelect(pos, sumR * invW, cr).CopyTo(tr, pi);
                Vector.ConditionalSelect(pos, sumG * invW, cg).CopyTo(tg, pi);
                Vector.ConditionalSelect(pos, sumB * invW, cb).CopyTo(tb, pi);
            }

            int loInt = 2 * step, hiInt = w - 2 * step;
            for (int bx = 0; bx < Math.Min(loInt, w); bx++) PixelScalar(bx);
            int x = loInt;
            for (; x >= loInt && x + lanes <= hiInt; x += lanes) PixelVector(x);
            for (; x < w; x++) PixelScalar(x);
        });
    }
}
