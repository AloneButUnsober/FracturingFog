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
//
// #1664 — BuildFloatExp: the same evaluator with no double (float-exp, as #1331's
// CSPerturbFX), for GPUs with slow or no fp64.

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
" + MandelbrotKernelSource.WaveFetch + MandelbrotKernelSource.DzOut + @"
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

        gFinalZD[idx] = float4((float)zr, (float)zi, (float)(drv * DzOutScale()), (float)(div * DzOutScale()));   // #1660
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

    // ── #1664 — the same evaluator on the float-exp kernels (#1331) ─────────────
    //
    // CSPerturbLA with no double: for GPUs whose fp64 runs at 1/32..1/64 of fp32 (or not
    // at all), where the fast deep path is the float-exp one. Every value that leaves
    // float range at depth is float-exp (a float mantissa, max(|m.x|, |m.y|) in [1, 2),
    // with one shared int exponent; MandelbrotKernelSource.FxHelpers): δ, dc, dz/dc, the
    // steps' A and B, both thresholds, and the AT's δc bound, C and 1/Z. The reference
    // orbit, a step's reference point, z = Z + δ and the AT's own iterate z' and c' are
    // O(1) and plain float, as in CSPerturbFX. The threshold tests compare float-exp
    // numbers exactly (exponent, then mantissa), so a pixel takes the steps the double
    // kernel takes except where a test falls within a float rounding of its bound.
    // Steps, schedules and outputs are CSPerturbLA's; the derivative goes out as
    // CSPerturbFX's (dz/dc · 2^gScaleE, #1660).

    /// <summary>#1664 — the float-exp LA entry point (<see cref="BuildFloatExp"/>).</summary>
    public const string FloatExpEntryPoint = "CSPerturbLAFX";

    /// <summary>#1664 — 32-bit words per step of the float-exp step buffer (t2):
    /// Ref re, im (float); A re, im (float mantissa), A exponent; B re, im, B exponent;
    /// Thr mantissa, exponent; ThrC mantissa, exponent (<c>LaStageTable.GpuStepsFloatExp</c>).</summary>
    public const int StepWordsFloatExp = 12;

    /// <summary>#1664 — the float-exp LA kernel. Bindings: b0 params (112 bytes, all
    /// 32-bit: <c>LaFxParams</c> on the host), t0 the reference orbit as float2, t2 the
    /// float-exp steps (12 words each), t3 the integers (as <see cref="Build"/>), t15 the
    /// #1394 mask; u0..u2 iter / smooth / finalZD, u3 gWork. (t1 is unused.)</summary>
    public static string BuildFloatExp() => $"#define PERTURB_CHUNK {MandelbrotKernelSource.PerturbChunk}\n" + @"
// 28 32-bit scalars (112 bytes). Matches the C# LaFxParams byte-for-byte.
cbuffer PerturbLaFxParams : register(b0)
{
    float gScaleM;      // pixel scale = gScaleM · 2^gScaleE, gScaleM in [1, 2)
    int   gScaleE;
    float gOffX0;
    float gOffY0;
    int   gWidth;
    int   gHeight;
    int   gMaxIter;
    int   gRefLen;
    int   gRowBase;
    int   gStages;
    int   gAtLen;       // the AT's step length (0 = no AT)
    int   gStepsBase;   // first step's index in gLaInt (= 2·gStages)
    int   gWave;
    int   gBandRows;
    int   gBand;
    float gEscapeR2;
    float gAtThrCM;     // the AT's δc bound (Chebyshev), float-exp
    int   gAtThrCE;
    float gAtEsc2;      // the AT's escape radius squared
    float gAtRefCR;     // c' = RefC + C·δc (RefC plain float)
    float gAtRefCI;
    float gAtCR;        // C, float-exp
    float gAtCI;
    int   gAtCE;
    float gAtInvZR;     // z = InvZ·z', float-exp
    float gAtInvZI;
    int   gAtInvZE;
    int   gPadL0;
}

StructuredBuffer<float2> gRefZ  : register(t0);
StructuredBuffer<uint>   gLaFx  : register(t2);
StructuredBuffer<uint>   gLaInt : register(t3);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);
RWStructuredBuffer<uint>   gWork    : register(u3);
" + MandelbrotKernelSource.WaveFetch + MandelbrotKernelSource.FxHelpers + @"
// a · b, float-exp.
void FxMul(float2 am, int ae, float2 bm, int be, out float2 rm, out int re)
{
    rm = CMul(am, bm); re = ae + be;
    FxNorm(rm, re);
}

// a >= b for non-negative float-exp reals (normalised: mantissa in [1, 2), or 0 with
// FxZeroExp, the smallest exponent).
bool FxGE(float am, int ae, float bm, int be) { return ae > be || (ae == be && am >= bm); }

// A non-negative float as float-exp.
void FxReal(float a, out float m, out int e)
{
    float2 v = float2(a, 0.0); e = 0;
    FxNorm(v, e);
    m = v.x;
}

float ChebM(float2 m) { return max(abs(m.x), abs(m.y)); }

void FxWriteLa(int idx, int n, float2 z, float2 dzf)
{
    gFinalZD[idx] = float4(z.x, z.y, dzf.x, dzf.y);
    if (n >= gMaxIter)
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
    }
    else
    {
        gIter[idx] = (uint)n;
        float magf = sqrt(dot(z, z));
        gSmooth[idx] = (float)n + 1.0 - log2(log2(magf));
    }
}

[numthreads(8, 8, 1)]
void CSPerturbLAFX(uint3 tid : SV_DispatchThreadID)
{
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    float2 dcM = float2(0.0, 0.0); int dcE = FxZeroExp;
    float mdcM = 0.0; int mdcE = FxZeroExp;
    float2 dM = float2(0.0, 0.0); int dE = FxZeroExp;   // δ
    float2 vM = float2(1.0, 0.0); int vE = 0;           // dz/dc
    float2 z = float2(0.0, 0.0);
    float2 x = float2(0.0, 0.0), c = float2(0.0, 0.0);  // the AT's z', c'
    float2 eM = float2(0.0, 0.0); int eE = FxZeroExp;   // the AT's dz'/dc'
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

            dcM = float2(gOffX0 + (float)px, gOffY0 + (float)py) * gScaleM;
            dcE = gScaleE;
            FxNorm(dcM, dcE);
            mdcM = ChebM(dcM); mdcE = dcE;
            dM = float2(0.0, 0.0); dE = FxZeroExp;
            vM = float2(1.0, 0.0); vE = 0;
            z = float2(0.0, 0.0);
            n = 0; stage = top; j = 0;
            phase = 1;
            if (gAtLen > 0 && FxGE(gAtThrCM, gAtThrCE, mdcM, mdcE))
            {
                phase = 0;
                atMax = (gMaxIter - 1) / gAtLen + 1;
                float2 tM; int tE;
                FxMul(dcM, dcE, float2(gAtCR, gAtCI), gAtCE, tM, tE);
                c = FxToFloat(tM, tE) + float2(gAtRefCR, gAtRefCI);
                x = float2(0.0, 0.0); eM = float2(0.0, 0.0); eE = FxZeroExp; k = 0;
            }
        }

        bool done = false;
        [loop]
        for (int trip = 0; trip < PERTURB_CHUNK && !done; trip++)
        {
            if (phase == 0)
            {
                // ── The AT (RunLaAt) ──
                float2 neM = CMul(2.0 * x, eM); int neE = eE;
                FxNorm(neM, neE);
                FxAdd(neM, neE, float2(1.0, 0.0), 0, eM, eE);
                x = CMul(x, x) + c;
                k++;
                bool inSet = k >= atMax;
                if (inSet || dot(x, x) > gAtEsc2)
                {
                    float2 xm = x; int xe = 0;
                    FxNorm(xm, xe);
                    float2 zM; int zE;
                    FxMul(xm, xe, float2(gAtInvZR, gAtInvZI), gAtInvZE, zM, zE);
                    z = FxToFloat(zM, zE);
                    float2 gM; int gE;
                    FxMul(eM, eE, float2(gAtCR, gAtCI), gAtCE, gM, gE);
                    FxMul(gM, gE, float2(gAtInvZR, gAtInvZI), gAtInvZE, vM, vE);
                    dM = zM; dE = zE;   // δ = z (reference index 0)
                    if (inSet) { n = gMaxIter; done = true; }
                    else { n = k * gAtLen; phase = 1; stage = top; j = 0; }
                }
            }
            else if (phase == 1)
            {
                // ── The stages (LaRunStages) ──
                if (stage < 0)
                {
                    phase = 2;   // j is the reference index
                }
                else
                {
                    uint s0 = gLaInt[2 * stage], last = gLaInt[2 * stage + 1];
                    uint ei2 = gStepsBase + 2 * (s0 + (uint)j);
                    uint f0 = s0 * 12;
                    if (FxGE(mdcM, mdcE, asfloat(gLaFx[f0 + 10]), asint(gLaFx[f0 + 11])))
                    {
                        j = (int)gLaInt[ei2 + 1]; stage--;
                    }
                    else
                    {
                        uint e = (s0 + (uint)j) * 12;
                        float2 sR = float2(asfloat(gLaFx[e]), asfloat(gLaFx[e + 1]));
                        float2 sAM = float2(asfloat(gLaFx[e + 2]), asfloat(gLaFx[e + 3])); int sAE = asint(gLaFx[e + 4]);
                        float2 sBM = float2(asfloat(gLaFx[e + 5]), asfloat(gLaFx[e + 6])); int sBE = asint(gLaFx[e + 7]);
                        float sThrM = asfloat(gLaFx[e + 8]); int sThrE = asint(gLaFx[e + 9]);
                        float sThrCM = asfloat(gLaFx[e + 10]); int sThrCE = asint(gLaFx[e + 11]);
                        int sLen = (int)gLaInt[ei2], sNext = (int)gLaInt[ei2 + 1];
                        float2 df = FxToFloat(dM, dE);
                        float2 wM = CMul(dM, 2.0 * sR + df); int wE = dE;   // w = δ·(2·Ref + δ)
                        FxNorm(wM, wE);
                        if (FxGE(ChebM(wM), wE, sThrM, sThrE) || FxGE(mdcM, mdcE, sThrCM, sThrCE) || sLen > gMaxIter - n)
                        {
                            j = sNext; stage--;
                        }
                        else
                        {
                            // dz/dc' = A·(2·zs·dz/dc) + B
                            float2 tM = CMul(2.0 * (sR + df), vM); int tE = vE;
                            FxNorm(tM, tE);
                            float2 atM; int atE;
                            FxMul(sAM, sAE, tM, tE, atM, atE);
                            FxAdd(atM, atE, sBM, sBE, vM, vE);
                            // δ' = A·w + B·δc
                            float2 awM; int awE;
                            FxMul(sAM, sAE, wM, wE, awM, awE);
                            float2 bcM; int bcE;
                            FxMul(sBM, sBE, dcM, dcE, bcM, bcE);
                            FxAdd(awM, awE, bcM, bcE, dM, dE);
                            n += sLen;
                            j++;
                            uint nx = (s0 + (uint)j) * 12;
                            z = float2(asfloat(gLaFx[nx]), asfloat(gLaFx[nx + 1])) + FxToFloat(dM, dE);
                            float zcM; int zcE;
                            FxReal(ChebM(z), zcM, zcE);
                            if ((uint)j == last || (FxGE(ChebM(dM), dE, zcM, zcE) && !(dE == zcE && ChebM(dM) == zcM)))
                            {
                                j = 0; dM = z; dE = 0; FxNorm(dM, dE);   // rebase: Z_0 = 0; climb back to the top
                                stage = top;
                            }
                        }
                    }
                }
            }
            else
            {
                // ── The plain tail (CSPerturbFX's step; a rebase climbs back) ──
                if (n >= gMaxIter)
                {
                    done = true;
                }
                else
                {
                    float2 Z = gRefZ[j];
                    float2 df = FxToFloat(dM, dE);
                    z = Z + df;
                    float zmag2 = dot(z, z);
                    // #1661 — no magnitude rebase at j = 0 (see CSPerturbLA).
                    if (zmag2 >= gEscapeR2)
                    {
                        done = true;
                    }
                    else if ((j > 0 && zmag2 < dot(df, df)) || j + 1 >= gRefLen)
                    {
                        dM = z; dE = 0; FxNorm(dM, dE);
                        phase = 1; stage = top; j = 0;
                    }
                    else
                    {
                        float2 nvM = CMul(2.0 * z, vM); int nvE = vE;
                        FxNorm(nvM, nvE);
                        FxAdd(nvM, nvE, float2(1.0, 0.0), 0, vM, vE);
                        float2 tM = CMul(2.0 * Z + df, dM); int tE = dE;
                        FxNorm(tM, tE);
                        FxAdd(tM, tE, dcM, dcE, dM, dE);
                        j++; n++;
                    }
                }
            }
        }
        if (!done) continue;

        FxWriteLa(idx, n, z, FxToFloat(vM, vE + gScaleE));   // #1660: dz/dc · 2^gScaleE (DzOut)
        idx = -1;
    }
}
";
}
