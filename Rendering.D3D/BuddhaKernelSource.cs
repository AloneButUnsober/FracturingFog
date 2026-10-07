// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/BuddhaKernelSource.cs
//
// #838 (GPU parity plan G4.7) — the Buddhabrot family's uniform Monte Carlo sample
// pass as one HLSL compute shader, compiled by FXC (D3D11) and, linked into
// Rendering.Vulkan, by DXC (SPIR-V). No vk:: attributes (the two-compiler rule of
// MandelbrotKernelSource); bindings are pinned on Vulkan by the -fvk-*-shift flags.
//
// The GPU twin of BuddhaFamilyCalculator.RunUniformBatch:
//   - each thread draws one c from a PCG stream keyed by (seed, batch, thread) over
//     the same fixed domain [-2.5, 1.5] x [-1.5, 1.5];
//   - pass 1 iterates z^2 + c (float) to classify the orbit exactly as the CPU's
//     IterateOrbit (escape vs in-set keep rule; band from the escape iteration or
//     the in-set mean |z|^2 curve);
//   - pass 2 iterates the kept orbit again and InterlockedAdds each recorded point
//     into the band's histogram (nearest pixel, or HD stochastic bilinear + real-
//     axis mirror), instead of storing the orbit.
// The image is a different random realization than the CPU's (different RNG,
// float orbits), with the same statistics. Integer atomics make it deterministic
// for a given seed. Composite / recolour / relief stay on the CPU, untouched.
// Metropolis-Hastings sampling stays on the CPU (see the G4.7 row of
// Docs/Technical/GPU-Parity-DevelopmentPlan.md).
//
// Cycle detection (Brent, exact float compare). A float orbit that revisits a
// value is periodic from there on, exactly, in the arithmetic the kernel runs:
//   - escape mode: it can never escape, so it is rejected at once instead of
//     iterating to maxOrbit (the rare in-set c held its whole warp for 50 000
//     iterations, in half of all warps at the default settings);
//   - in-set mode: the rest of the orbit is the cycle repeated, so its points are
//     splatted once each with their repeat count (and summed the same way)
//     instead of 50 000 single atomic adds on a handful of hot pixels.
// Both give the result the full loop would give, only cheaper.
//
// Both passes iterate through the one Step() with `precise` (no contraction /
// reassociation): pass 2 must replay pass 1's orbit bit for bit. Without it the
// compiler fused the two loops' multiply-adds differently, so a c classified as
// escaping at ~maxOrbit could replay as an orbit that falls into an attracting
// cycle and piles maxOrbit hits on a few pixels (one pixel took 10% of the high
// band on a GT 710, a visible top/bottom imbalance). With it the kernel is plain
// IEEE float, which GpuBuddhaReference (Engine) replays exactly on the CPU.

namespace FracturingFog.Rendering;

public static class BuddhaKernelSource
{
    public const string EntryPoint = "CSBuddha";

    /// <summary>Threads per group (1-D dispatch).</summary>
    public const int GroupSize = 64;

    /// <summary>TDR tiling budget for the FIRST dispatch of a batch — max worst-case
    /// orbit iterations (threads x orbit length x 2 passes). The worst case (every
    /// orbit runs to maxOrbit) is far from the typical one, so later dispatches are
    /// sized from measured time instead (<see cref="NextChunk"/>).
    /// FF_BUDDHA_DISPATCH_BUDGET overrides.</summary>
    public static long DispatchIterBudget =
        long.TryParse(System.Environment.GetEnvironmentVariable("FF_BUDDHA_DISPATCH_BUDGET"), out long b) && b > 0 ? b : 400_000_000;

    /// <summary>Target GPU time per dispatch. Each dispatch is one submitted packet;
    /// the Windows watchdog fires at 2 s, so this leaves a wide margin while keeping
    /// the per-submit overhead (which dominated with fixed small dispatches: 761 ms
    /// vs 178 ms for the default frame on a GT 710) negligible.</summary>
    public const double TargetDispatchMs = 150.0;

    public const int MaxThreadsPerDispatch = 1 << 22;

    /// <summary>Threads in the first dispatch of a batch: the worst-case budget,
    /// at least one group, a multiple of <see cref="GroupSize"/>.</summary>
    public static int FirstChunk(int maxOrbit)
    {
        long t = System.Math.Max(GroupSize, DispatchIterBudget / ((long)System.Math.Max(1, maxOrbit) * 2));
        t = t / GroupSize * GroupSize;
        return (int)System.Math.Min(t, MaxThreadsPerDispatch);
    }

    /// <summary>Size the next dispatch from the last one: <paramref name="threads"/>
    /// threads took <paramref name="ms"/>; aim at <see cref="TargetDispatchMs"/>,
    /// growing at most 4x and shrinking at most 4x per step.</summary>
    public static int NextChunk(int threads, double ms)
    {
        double f = System.Math.Clamp(TargetDispatchMs / System.Math.Max(ms, 0.5), 0.25, 4.0);
        long t = (long)(threads * f) / GroupSize * GroupSize;
        return (int)System.Math.Clamp(t, GroupSize, MaxThreadsPerDispatch);
    }

    /// <summary>The dispatch plan shared by both backends: the batch's threads in
    /// chunks, the chunk adapting after every full dispatch. Calls
    /// <paramref name="dispatch"/>(threadBase, threads), which returns the
    /// dispatch's GPU time in ms.</summary>
    public static void Plan(int threads, int maxOrbit, System.Func<int, int, double> dispatch)
    {
        int chunk = FirstChunk(maxOrbit);
        for (int baseT = 0, count; baseT < threads; baseT += count)
        {
            count = System.Math.Min(chunk, threads - baseT);
            double ms = dispatch(baseT, count);
            if (count == chunk) chunk = NextChunk(count, ms);
        }
    }

    public static string Build() => @"
// 64 bytes: 11 ints then 5 floats. Matches the C# BuddhaParams blobs byte-for-byte.
cbuffer BuddhaParams : register(b0)
{
    int   gWidth;
    int   gHeight;
    int   gMaxOrbit;
    int   gInSet;        // 1 = keep bounded orbits (Anti*), 0 = escaping (Buddhabrot)
    int   gLow;          // band thresholds on the class iteration
    int   gMid;
    int   gHd;           // 1 = stochastic bilinear + real-axis mirror splat
    uint  gSeed;
    uint  gBatch;
    uint  gThreadBase;   // TDR tiling: first global thread (= sample) of this dispatch
    uint  gThreadCount;  // samples in this batch (threads past it do nothing)
    float gScale;        // plane units per pixel
    float gMidX;
    float gMidY;
    float gPad0;
    float gPad1;
}

RWStructuredBuffer<uint> gHits : register(u0);   // 3 x W x H: band 0 | band 1 | band 2

uint Wang(uint x)
{
    x = (x ^ 61u) ^ (x >> 16);
    x *= 9u;
    x = x ^ (x >> 4);
    x *= 0x27d4eb2du;
    x = x ^ (x >> 15);
    return x;
}

uint Pcg(inout uint s)
{
    s = s * 747796405u + 2891336453u;
    uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}

// [0, 1) with 24 bits.
float Rnd(inout uint s) { return (float)(Pcg(s) >> 8) * (1.0 / 16777216.0); }

// z <- z^2 + c, the only orbit step either pass takes (see `precise` above).
void Step(inout float zr, inout float zi, float cx, float cy)
{
    precise float nzr = zr * zr - zi * zi + cx;
    precise float nzi = 2.0 * zr * zi + cy;
    zr = nzr;
    zi = nzi;
}

bool InCardioidOrBulb(float cx, float cy)
{
    float dx = cx + 1.0;
    if (dx * dx + cy * cy < 0.0625) return true;
    float xq = cx - 0.25;
    float q = xq * xq + cy * cy;
    return q * (q + xq) < 0.25 * cy * cy;
}

// Pass 1 — BuddhaFamilyCalculator.IterateOrbit without the orbit store or the
// Metropolis score. The recorded points are z_0 .. z_{recLen-1} (the escaping z
// is not one of them).
bool Classify(float cx, float cy, out int recLen, out int cls)
{
    recLen = 0; cls = 0;
    if (gInSet == 0 && InCardioidOrBulb(cx, cy)) return false;

    float zr = 0.0, zi = 0.0;
    precise float sumZ2 = 0.0;
    float snapR = 0.0, snapI = 0.0;
    int snapK = 0, nextSnap = 1, lam = 0;
    int iter;
    [loop]
    for (iter = 0; iter < gMaxOrbit; iter++)
    {
        precise float m2 = zr * zr + zi * zi;
        sumZ2 += m2;
        if (m2 > 4.0) break;
        if (iter > 0)
        {
            if (zr == snapR && zi == snapI) { lam = iter - snapK; break; }   // z_iter = z_{iter-lam}
            if (iter == nextSnap) { snapR = zr; snapI = zi; snapK = iter; nextSnap <<= 1; }
        }
        Step(zr, zi, cx, cy);
    }

    if (lam > 0)
    {
        if (gInSet == 0) return false;   // periodic: never escapes
        // Indices iter+1 .. maxOrbit-1 repeat the cycle z_{iter+1} .. z_{iter+lam}.
        [loop]
        for (int i = 1; i <= lam; i++)
        {
            Step(zr, zi, cx, cy);
            int first = iter + i;
            if (first >= gMaxOrbit) break;
            precise float m2 = zr * zr + zi * zi;
            sumZ2 += (float)((gMaxOrbit - 1 - first) / lam + 1) * m2;
        }
        iter = gMaxOrbit;
    }

    bool escaped = iter < gMaxOrbit;
    bool keep = gInSet != 0 ? !escaped : escaped;
    if (!keep) return false;
    if (escaped) cls = iter;
    else
    {
        // In-set: the CPU's mean |z|^2 band curve, sqrt(min(1, mean / 4)).
        float mean = sumZ2 / (float)max(1, iter);
        cls = (int)(sqrt(min(1.0, mean * 0.25)) * (float)gMaxOrbit);
    }
    recLen = escaped ? iter : gMaxOrbit;
    return true;
}

void SplatBilinear(uint bandBase, float fx, float fy, uint w, inout uint rng)
{
    float x0 = floor(fx), y0 = floor(fy);
    int xi = Rnd(rng) < fx - x0 ? (int)x0 + 1 : (int)x0;
    int yi = Rnd(rng) < fy - y0 ? (int)y0 + 1 : (int)y0;
    if ((uint)xi < (uint)gWidth && (uint)yi < (uint)gHeight)
        InterlockedAdd(gHits[bandBase + (uint)(yi * gWidth + xi)], w);
}

// One recorded point with weight w (w > 1 only for a repeated cycle point; the HD
// splat then picks its cell once for all w repeats, the same expected density).
void SplatPoint(uint bandBase, float zr, float zi, uint w, inout uint rng)
{
    if (gHd == 0)
    {
        int ix = (int)((zr - gMidX) / gScale + gWidth * 0.5);
        int iy = (int)((zi - gMidY) / gScale + gHeight * 0.5);
        if ((uint)ix < (uint)gWidth && (uint)iy < (uint)gHeight)
            InterlockedAdd(gHits[bandBase + (uint)(iy * gWidth + ix)], w);
    }
    else
    {
        float invScale = 1.0 / gScale;
        float fx = (zr - gMidX) * invScale + gWidth * 0.5;
        SplatBilinear(bandBase, fx, (zi - gMidY) * invScale + gHeight * 0.5, w, rng);
        SplatBilinear(bandBase, fx, (-zi - gMidY) * invScale + gHeight * 0.5, w, rng);
    }
}

// Pass 2 — re-iterate the kept orbit and splat its recLen recorded points into
// the band histogram (SplatOrbitStd / SplatOrbitHD). An in-set orbit runs the
// same cycle detection as Classify and splats the cycle with repeat counts.
void Splat(float cx, float cy, int recLen, int cls, inout uint rng)
{
    uint band = cls < gLow ? 0u : (cls < gMid ? 1u : 2u);
    uint bandBase = band * (uint)(gWidth * gHeight);
    float zr = 0.0, zi = 0.0;
    float snapR = 0.0, snapI = 0.0;
    int snapK = 0, nextSnap = 1, lam = 0;
    int k;
    [loop]
    for (k = 0; k < recLen; k++)
    {
        SplatPoint(bandBase, zr, zi, 1u, rng);
        if (gInSet != 0 && k > 0)
        {
            if (zr == snapR && zi == snapI) { lam = k - snapK; break; }
            if (k == nextSnap) { snapR = zr; snapI = zi; snapK = k; nextSnap <<= 1; }
        }
        Step(zr, zi, cx, cy);
    }
    if (lam > 0)
    {
        [loop]
        for (int i = 1; i <= lam; i++)
        {
            Step(zr, zi, cx, cy);
            int first = k + i;
            if (first >= recLen) break;
            SplatPoint(bandBase, zr, zi, (uint)((recLen - 1 - first) / lam + 1), rng);
        }
    }
}

[numthreads(64, 1, 1)]
void CSBuddha(uint3 tid : SV_DispatchThreadID)
{
    uint g = gThreadBase + tid.x;
    if (g >= gThreadCount) return;
    uint rng = Wang(gSeed ^ Wang(g * 0x9E3779B9u + Wang(gBatch + 0x632BE5ABu)));
    float cx = -2.5 + Rnd(rng) * 4.0;
    float cy = -1.5 + Rnd(rng) * 3.0;
    int recLen, cls;
    if (Classify(cx, cy, recLen, cls)) Splat(cx, cy, recLen, cls, rng);
}
";
}
