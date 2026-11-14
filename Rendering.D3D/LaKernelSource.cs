// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// LaKernelSource.cs — #1630 (epic #1623, S7): the HLSL source of the LA stage
// evaluator for the deep perturbation kernels. Like MandelbrotKernelSource it is
// dependency-free and linked into the Vulkan projects: one source, FXC (cs_5_0) on
// Windows, DXC (-spirv) for Vulkan.
//
// DERIVED CODE: the kernel is the HLSL form of the engine's scalar evaluator
// (Engine/Calculators/MandelbrotCalculator.La.cs: RunLaAt, LaRunStages and the plain
// tail of EvaluateLa), whose AT and stage loop are C# translations of Imagina's
// EvaluateAT / EvaluateLA and LAInfo::EvaluateDzdc (Imagina by GitHub user 5E-324,
// profile name "Yu, Zhuoran", https://github.com/5E-324/Imagina, HInfLAEvaluator.{h,cpp}
// at commit cd046377, AGPL-3.0; THIRD-PARTY-NOTICES.md §3,
// Docs/Resources-Bibliography.md#imagina). The plain tail is FF's rebased loop
// (Zhuoran's rebasing), as the CPU evaluator's. Design:
// Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2.4–2.5, §3, §4.6.
//
// The pixel runs the scalar evaluator's steps in the same order (AT, then the stages
// from the top, then the plain tail, climbing back to the stages on a rebase), as a
// state machine so the #1340 wavefront schedule can run it PERTURB_CHUNK trips at a
// time. A trip is one AT step, one stage decision (a step taken, or a stage dropped),
// or one plain step.

namespace FracturingFog.Rendering;

/// <summary>#1630 — HLSL of the LA stage perturbation kernel (see the file header).</summary>
public static class LaKernelSource
{
    public const string EntryPoint = "CSPerturbLA";

    /// <summary>Doubles per step in the step buffer (t2): Ref re/im, A re/im, B re/im, Thr, ThrC.</summary>
    public const int StepDoubles = 8;

    /// <summary>Test hook (#1661): compile the kernel with the plain tail's |δ|² nudged
    /// up at reference index 0, the rounding difference NVIDIA's D3D11 compiler produced
    /// between |z|² and |δ|² there. The kernel's output must not change (and it must
    /// finish). Read by <see cref="Build"/>; set it before the kernel first compiles.</summary>
    public static bool SkewPlainMagnitudesForTests { get; set; }

    /// <summary>The kernel. Bindings: b0 params (144 bytes, <c>LaParams</c> on the host),
    /// t0 / t1 the reference orbit (refLen entries), t2 the steps (8 doubles each), t3 the
    /// integers (2 per stage: first step, step count without the sentinel; then 2 per
    /// step: Len, Next), t15 the #1394 mask; u0..u2 iter / smooth / finalZD, u3 gWork.</summary>
    public static string Build() => $"#define PERTURB_CHUNK {MandelbrotKernelSource.PerturbChunk}\n" + (SkewPlainMagnitudesForTests ? "#define LA_SKEW_J0 1\n" : "") + @"
// 12 doubles (96 bytes) then 12 ints (48 bytes). Matches the C# LaParams byte-for-byte.
cbuffer PerturbParams : register(b0)
{
    double gScale;
    double gEscapeR2;
    double gOffX0;
    double gOffY0;
    double gAtThrC;     // the AT's δc bound (Chebyshev)
    double gAtEsc2;     // the AT's escape radius squared
    double gAtRefCR;    // c' = RefC + C·δc
    double gAtRefCI;
    double gAtCR;
    double gAtCI;
    double gAtInvZR;    // z = InvZ·z'
    double gAtInvZI;
    int    gWidth;
    int    gHeight;
    int    gMaxIter;
    int    gRefLen;     // reference entries (the plain tail rebases at refLen - 1)
    int    gRowBase;
    int    gStages;
    int    gAtLen;      // the AT's step length (0 = no AT)
    int    gStepsBase;  // first step's index in gLaInt (= 2·gStages)
    int    gWave;       // #1340: 1 = wavefront schedule
    int    gBandRows;
    int    gBand;
    int    gPad0;
}

StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);
StructuredBuffer<double> gLa    : register(t2);
StructuredBuffer<uint>   gLaInt : register(t3);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);
RWStructuredBuffer<uint>   gWork    : register(u3);
" + MandelbrotKernelSource.WaveFetch + @"
double ChebD(double x, double y) { return max(abs(x), abs(y)); }

[numthreads(8, 8, 1)]
void CSPerturbLA(uint3 tid : SV_DispatchThreadID)
{
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    double dcR = 0.0, dcI = 0.0, mdc = 0.0;
    double dr = 0.0, di = 0.0, zr = 0.0, zi = 0.0, drv = 0.0, div = 0.0;
    double xr = 0.0, xi = 0.0, er = 0.0, ei = 0.0, cr = 0.0, ci = 0.0;   // the AT's z', dz'/dc', c'
    int n = 0, phase = 0, stage = 0, j = 0, k = 0, atMax = 0;
    int top = gStages - 1;
    [loop] [allow_uav_condition]
    for (;;)
    {
        if (idx < 0)
        {
            uint lx, ly;
            int got = WaveFetch(gWave, taken, tid.xy, (uint)gWidth, rows, (uint)gBand, true, lx, ly);
            if (got == 0) break;
            if (got == 2) continue;
            int px = (int)lx;
            int py = gRowBase + (int)ly;
            idx = py * gWidth + px;
            if (MaskSkips(idx)) { idx = -1; continue; }   // #1394

            dcR = (gOffX0 + (double)px) * gScale;
            dcI = (gOffY0 + (double)py) * gScale;
            mdc = ChebD(dcR, dcI);
            dr = 0.0; di = 0.0; zr = 0.0; zi = 0.0; drv = 1.0; div = 0.0;
            n = 0; stage = top; j = 0;
            phase = 1;
            if (gAtLen > 0 && mdc <= gAtThrC)
            {
                phase = 0;
                atMax = (gMaxIter - 1) / gAtLen + 1;
                cr = dcR * gAtCR - dcI * gAtCI + gAtRefCR;
                ci = dcR * gAtCI + dcI * gAtCR + gAtRefCI;
                xr = 0.0; xi = 0.0; er = 0.0; ei = 0.0; k = 0;
            }
        }

        bool done = false;
        [loop]
        for (int trip = 0; trip < PERTURB_CHUNK && !done; trip++)
        {
            if (phase == 0)
            {
                // ── The AT (RunLaAt) ──
                double nEr = 2.0 * (xr * er - xi * ei) + 1.0, nEi = 2.0 * (xr * ei + xi * er);
                er = nEr; ei = nEi;
                double nXr = xr * xr - xi * xi + cr, nXi = 2.0 * xr * xi + ci;
                xr = nXr; xi = nXi;
                k++;
                bool inSet = k >= atMax;
                if (inSet || xr * xr + xi * xi > gAtEsc2)
                {
                    zr = xr * gAtInvZR - xi * gAtInvZI; zi = xr * gAtInvZI + xi * gAtInvZR;
                    double gr = er * gAtCR - ei * gAtCI, gi = er * gAtCI + ei * gAtCR;
                    drv = gr * gAtInvZR - gi * gAtInvZI; div = gr * gAtInvZI + gi * gAtInvZR;
                    dr = zr; di = zi;
                    if (inSet) { n = gMaxIter; done = true; }
                    else { n = k * gAtLen; phase = 1; stage = top; j = 0; }
                }
            }
            else if (phase == 1)
            {
                // ── The stages (LaRunStages) ──
                // (Structured if / else, no continue: written so while chasing #1661. The
                // hang was the tail's rebase test at j = 0, below, not the control flow.)
                if (stage < 0)
                {
                    phase = 2;   // j is the reference index
                }
                else
                {
                    uint s0 = gLaInt[2 * stage], last = gLaInt[2 * stage + 1];
                    uint ei2 = gStepsBase + 2 * (s0 + (uint)j);
                    if (mdc >= gLa[s0 * 8 + 7])
                    {
                        j = (int)gLaInt[ei2 + 1]; stage--;
                    }
                    else
                    {
                        uint e = (s0 + (uint)j) * 8;
                        double sRr = gLa[e], sRi = gLa[e + 1], sAr = gLa[e + 2], sAi = gLa[e + 3];
                        double sBr = gLa[e + 4], sBi = gLa[e + 5], sThr = gLa[e + 6], sThrC = gLa[e + 7];
                        int sLen = (int)gLaInt[ei2], sNext = (int)gLaInt[ei2 + 1];
                        double ar = 2.0 * sRr + dr, ai = 2.0 * sRi + di;
                        double wr = dr * ar - di * ai, wi = dr * ai + di * ar;
                        if (ChebD(wr, wi) >= sThr || mdc >= sThrC || sLen > gMaxIter - n)
                        {
                            j = sNext; stage--;
                        }
                        else
                        {
                            double zsr = sRr + dr, zsi = sRi + di;
                            double tr = 2.0 * (zsr * drv - zsi * div), ti = 2.0 * (zsr * div + zsi * drv);
                            double nDrv = sAr * tr - sAi * ti + sBr;
                            double nDiv = sAr * ti + sAi * tr + sBi;
                            drv = nDrv; div = nDiv;
                            double nDr = sAr * wr - sAi * wi + (sBr * dcR - sBi * dcI);
                            double nDi = sAr * wi + sAi * wr + (sBr * dcI + sBi * dcR);
                            dr = nDr; di = nDi;
                            n += sLen;
                            j++;
                            uint nx = (s0 + (uint)j) * 8;
                            zr = gLa[nx] + dr; zi = gLa[nx + 1] + di;
                            if ((uint)j == last || ChebD(dr, di) > ChebD(zr, zi))
                            {
                                j = 0; dr = zr; di = zi;   // rebase: Z_0 = 0, exact; climb back to the top
                                stage = top;
                            }
                        }
                    }
                }
            }
            else
            {
                // ── The plain tail (FF's rebased loop; a rebase climbs back) ──
                if (n >= gMaxIter)
                {
                    done = true;
                }
                else
                {
                    double Zr = gRefZr[j], Zi = gRefZi[j];
                    zr = Zr + dr; zi = Zi + di;
                    double zmag2 = zr * zr + zi * zi;
                    double dmag2 = dr * dr + di * di;
#ifdef LA_SKEW_J0
                    if (j == 0) dmag2 += dmag2 * 1e-15;   // SkewPlainMagnitudesForTests
#endif
                    // #1661 — no magnitude rebase at j = 0. There Z_0 = 0, so z = δ and a
                    // rebase changes nothing; but it climbs back to the stages without a
                    // step, and they hand the pixel straight back here. |z|² < |δ|² must
                    // be false, and is in exact arithmetic. A compiler that contracts the
                    // two sums differently (NVIDIA's D3D11 one on a GT 710) makes it true
                    // by an ulp, and the pixel then cycles forever: a device hang (TDR).
                    // The CPU evaluator has the same test and no contraction.
                    if (zmag2 >= gEscapeR2)
                    {
                        done = true;
                    }
                    else if ((j > 0 && zmag2 < dmag2) || j + 1 >= gRefLen)
                    {
                        dr = zr; di = zi;
                        phase = 1; stage = top; j = 0;
                    }
                    else
                    {
                        double nDrv2 = 2.0 * (zr * drv - zi * div) + 1.0;
                        double nDiv2 = 2.0 * (zr * div + zi * drv);
                        drv = nDrv2; div = nDiv2;
                        double a = 2.0 * Zr + dr, b = 2.0 * Zi + di;
                        double nDr2 = a * dr - b * di + dcR;
                        double nDi2 = a * di + b * dr + dcI;
                        dr = nDr2; di = nDi2;
                        j++; n++;
                    }
                }
            }
        }
        if (!done) continue;

        gFinalZD[idx] = float4((float)zr, (float)zi, (float)drv, (float)div);
        if (n >= gMaxIter)
        {
            gIter[idx]   = (uint)gMaxIter;
            gSmooth[idx] = 0.0;
        }
        else
        {
            gIter[idx] = (uint)n;
            float magf = sqrt((float)(zr * zr + zi * zi));
            gSmooth[idx] = (float)n + 1.0 - log2(log2(magf));
        }
        idx = -1;
    }
}
";
}
