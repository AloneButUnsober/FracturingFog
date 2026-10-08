// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/BuddhaUniformSampler.cs
//
// #838 / #1218 — the Buddhabrot family's uniform sampler, the one algorithm both
// paths run: the GPU kernel (Rendering.D3D/BuddhaKernelSource.cs, D3D11 + Vulkan)
// and this CPU twin in strict IEEE float (C# does not contract multiply-adds).
// The same counter-based stream, cycle detection, band split and splat, so the
// GPU, the CPU and --batch render the same image for a given seed — and, because
// each sample owns its stream, independently of the CPU core count. Keep it in
// step with the HLSL; S838GpuBuddhabrotTests and --d3dbuddhaprobe hold the
// backends to it.

namespace FracturingFog.Rendering;

public static class BuddhaUniformSampler
{
    private static uint Wang(uint x) { x = (x ^ 61u) ^ (x >> 16); x *= 9u; x ^= x >> 4; x *= 0x27d4eb2du; x ^= x >> 15; return x; }

    private static uint Pcg(ref uint s)
    {
        s = s * 747796405u + 2891336453u;
        uint w = ((s >> (int)((s >> 28) + 4u)) ^ s) * 277803737u;
        return (w >> 22) ^ w;
    }

    private static float Rnd(ref uint s) => (float)(Pcg(ref s) >> 8) * (1.0f / 16777216.0f);

    private static void Step(ref float zr, ref float zi, float cx, float cy)
    {
        float nzr = zr * zr - zi * zi + cx, nzi = 2.0f * zr * zi + cy;
        zr = nzr; zi = nzi;
    }

    /// <summary>Run the whole batch, ADDING into the arrays like
    /// <see cref="IGpuKernel.RunBuddhaBatch"/>.</summary>
    public static void Run(in GpuBuddhaBatch b, uint[] hitsR, uint[] hitsG, uint[] hitsB)
        => Run(b, hitsR, hitsG, hitsB, 0, b.Samples);

    /// <summary>Samples [<paramref name="firstSample"/>, firstSample + <paramref name="count"/>)
    /// of the batch, ADDING into the arrays. Callers split a batch across threads;
    /// with <paramref name="atomic"/> every thread adds into the same arrays
    /// (Interlocked, as the GPU's InterlockedAdd — integer sums, so the result does
    /// not depend on the split). A GPU backend differs from this only where its
    /// division is approximate (a point one pixel over, a sample one band over).</summary>
    public static void Run(in GpuBuddhaBatch b, uint[] hitsR, uint[] hitsG, uint[] hitsB, int firstSample, int count,
                           bool atomic = false)
    {
        int w = b.Width, h = b.Height, maxOrbit = b.MaxOrbit;
        float scale = (float)b.Scale, midX = (float)b.MidX, midY = (float)b.MidY;
        bool inSet = b.InSet, hd = b.HighDefinition;
        var bands = new[] { hitsR, hitsG, hitsB };

        void Add(uint[] t, int i, uint wt)
        {
            if (atomic) System.Threading.Interlocked.Add(ref t[i], wt);
            else t[i] += wt;
        }
        void Bilinear(uint[] t, float fx, float fy, uint wt, ref uint rng)
        {
            float x0 = System.MathF.Floor(fx), y0 = System.MathF.Floor(fy);
            int xi = Rnd(ref rng) < fx - x0 ? (int)x0 + 1 : (int)x0;
            int yi = Rnd(ref rng) < fy - y0 ? (int)y0 + 1 : (int)y0;
            if ((uint)xi < (uint)w && (uint)yi < (uint)h) Add(t, yi * w + xi, wt);
        }
        void Point(uint[] t, float zr, float zi, uint wt, ref uint rng)
        {
            if (!hd)
            {
                int ix = (int)((zr - midX) / scale + w * 0.5f), iy = (int)((zi - midY) / scale + h * 0.5f);
                if ((uint)ix < (uint)w && (uint)iy < (uint)h) Add(t, iy * w + ix, wt);
            }
            else
            {
                float inv = 1.0f / scale, fx = (zr - midX) * inv + w * 0.5f;
                Bilinear(t, fx, (zi - midY) * inv + h * 0.5f, wt, ref rng);
                Bilinear(t, fx, (-zi - midY) * inv + h * 0.5f, wt, ref rng);
            }
        }

        int minIter = b.MinIter;
        for (uint g = (uint)firstSample, end = (uint)(firstSample + count); g < end; g++)
        {
            uint rng = Wang(b.Seed ^ Wang(g * 0x9E3779B9u + Wang((uint)b.Batch + 0x632BE5ABu)));
            float cx = -2.5f + Rnd(ref rng) * 4.0f, cy = -1.5f + Rnd(ref rng) * 3.0f;
            if (!inSet)
            {
                float dx = cx + 1f;
                if (dx * dx + cy * cy < 0.0625f) continue;
                float xq = cx - 0.25f, q = xq * xq + cy * cy;
                if (q * (q + xq) < 0.25f * cy * cy) continue;
            }

            // Pass 1 (Classify).
            float zr = 0, zi = 0, sum = 0, sr = 0, si = 0;
            int sk = 0, next = 1, lam = 0, iter;
            for (iter = 0; iter < maxOrbit; iter++)
            {
                float m2 = zr * zr + zi * zi;
                sum += m2;
                if (m2 > 4f) break;
                if (iter > 0)
                {
                    if (zr == sr && zi == si) { lam = iter - sk; break; }
                    if (iter == next) { sr = zr; si = zi; sk = iter; next <<= 1; }
                }
                Step(ref zr, ref zi, cx, cy);
            }
            if (lam > 0)
            {
                if (!inSet) continue;
                for (int i = 1; i <= lam; i++)
                {
                    Step(ref zr, ref zi, cx, cy);
                    int first = iter + i;
                    if (first >= maxOrbit) break;
                    float m2 = zr * zr + zi * zi;
                    sum += (float)((maxOrbit - 1 - first) / lam + 1) * m2;
                }
                iter = maxOrbit;
            }
            bool escaped = iter < maxOrbit;
            if (inSet == escaped) continue;
            if (escaped && iter < minIter) continue;   // #1218 — fast escapers wash the |c| <= 2 disc
            int cls = escaped ? iter
                : (int)(System.MathF.Sqrt(System.MathF.Min(1f, sum / (float)System.Math.Max(1, iter) * 0.25f)) * (float)maxOrbit);
            int recLen = escaped ? iter : maxOrbit;
            var t = bands[cls < b.Low ? 0 : cls < b.Mid ? 1 : 2];

            // Pass 2 (Splat).
            zr = 0; zi = 0; sr = 0; si = 0; sk = 0; next = 1; lam = 0;
            int k;
            for (k = 0; k < recLen; k++)
            {
                if (k > 0) Point(t, zr, zi, 1, ref rng);   // #1218 — z0 = 0 is every orbit's: not drawn
                if (inSet && k > 0)
                {
                    if (zr == sr && zi == si) { lam = k - sk; break; }
                    if (k == next) { sr = zr; si = zi; sk = k; next <<= 1; }
                }
                Step(ref zr, ref zi, cx, cy);
            }
            for (int i = 1; lam > 0 && i <= lam; i++)
            {
                Step(ref zr, ref zi, cx, cy);
                int first = k + i;
                if (first >= recLen) break;
                Point(t, zr, zi, (uint)((recLen - 1 - first) / lam + 1), ref rng);
            }
        }
    }
}
