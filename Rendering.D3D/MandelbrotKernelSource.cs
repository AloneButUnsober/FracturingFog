// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MandelbrotKernelSource.cs — the HLSL *source strings* for the SP Mandelbrot
// compute kernel, split out so a single source feeds two compilers:
//   • D3DCompiler (FXC, cs_5_0) on Windows — MandelbrotGpuKernel.
//   • DXC (cs_6_0 -spirv) on Linux/macOS — the Vulkan compute backend.
//
// This file is DELIBERATELY dependency-free (no Vortice / D3D / Windows
// using-directives) so it can be `<Compile Include ... Link>`-ed into the
// cross-platform Vulkan projects without dragging the D3D closure along. See
// Docs/Technical/Vulkan-Compute-DevelopmentPlan.md §3 ("one HLSL source, two
// compilers"). The strings carry NO `[[vk::binding]]` attributes — those would
// break FXC. The Vulkan side pins descriptor bindings with DXC `-fvk-*-shift`
// flags instead (register class → binding range), keeping this source identical
// for both back ends.

namespace FracturingFog.Rendering;

/// <summary>
/// Shared HLSL source for the SP Mandelbrot escape-time kernel. See the file
/// header for the two-compiler rationale.
/// </summary>
public static class MandelbrotKernelSource
{
    /// <summary>Compute-shader entry point name (both compilers).</summary>
    public const string EntryPoint = "CSMain";

    /// <summary>cbuffer + IO bindings + shared early-out helpers. Register
    /// classes: b0 = Params cbuffer; u0..u2 = iter/smooth/finalZD UAVs;
    /// t0 = per-row cap SRV.</summary>
    public const string HlslBase = @"
cbuffer Params : register(b0)
{
    int   gWidth;
    int   gHeight;
    int   gMaxIter;
    float gBailout2;       // typically 4.0
    float gCXHi;
    float gCXLo;
    float gCYHi;
    float gCYLo;
    float gScaleHi;
    float gScaleLo;
    int   gUsePerRow;      // 0 = use gMaxIter for every row, 1 = use gPerRow
    // Phase 3: alt-fractal selector. 0=Mandelbrot, 1=Julia, 2=BurningShip,
    // 3=Tricorn. Cardioid + period-2 bulb skip only applies to kind 0.
    int   gFractalKind;
    float gParam0;         // Julia c.re
    float gParam1;         // Julia c.im
    float gDitherStrength; // F11b: 0 = off (plain round); else ±0.5-LSB amp.
    // 16 fields × 4 bytes = 64 (float4 multiple — same size as phase 1.b).
}

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
// Phase 1.b: final z + dz/dc per pixel. .xy = zr, zi; .zw = dr, di.
// Lets the CPU writeback path drive distance-estimate + normal
// themes that need the final orbit state. Aux buffers stay CPU.
RWStructuredBuffer<float4> gFinalZD : register(u2);
// Phase 1.b: per-row maxIter cap. Bound only when gUsePerRow != 0;
// otherwise the shader uses gMaxIter for every row.
StructuredBuffer<uint>     gPerRow  : register(t0);

bool InCardioid(float cx, float cy)
{
    // |1 - sqrt(1 - 4c)| <= 1  →  expanded form (no sqrt) per the standard
    // Wikipedia early-out. q = (x - 1/4)^2 + y^2.
    float xm = cx - 0.25;
    float q = xm * xm + cy * cy;
    return q * (q + xm) <= 0.25 * cy * cy;
}

bool InPeriod2Bulb(float cx, float cy)
{
    // Disk of radius 1/4 centred at (-1, 0).
    float dx = cx + 1.0;
    return dx * dx + cy * cy <= 0.0625;
}
";

    /// <summary>Compose the full base (non-colour) kernel: header + CSMain
    /// with the colour splices empty. This is the exact source V1 DXC-compiles
    /// to SPIR-V, and (via <see cref="HlslBase"/> + <see cref="HlslEntry"/>)
    /// the exact source FXC compiles for the D3D base variant.</summary>
    public static string BuildBase() => HlslBase + HlslEntry(emitColor: false);

    // ── Colour variant (V2 on Vulkan; long-standing on D3D) ──────────────────
    //
    // The colour-emitting kernel adds a packed-BGRA output buffer plus a
    // spliced-in EvalPalette. Register class u3 = gColor UAV -> on Vulkan the
    // DXC -fvk-u-shift maps it to binding 203 (UShift + 3). The prelude carries
    // NO vk:: attributes (same two-compiler rule as HlslBase); FXC and DXC both
    // consume it verbatim. See Docs/Technical/Vulkan-Compute-DevelopmentPlan.md
    // §V2.

    /// <summary>gColor UAV + ordered-dither pack + EvalPalette signature, up to
    /// (and including) the opening brace. The IGpuHlslPalette body is spliced
    /// after this, then <see cref="ColorPreludeTail"/> closes the function.</summary>
    public const string ColorPreludeHead = @"
RWStructuredBuffer<uint> gColor : register(u3);

// F11b: centred 8x8 Bayer thresholds ((raw+0.5)/64 - 0.5), the GPU twin of
// GradientColorMap.Bayer8. Added to each channel before the round so a
// shallow gradient dithers instead of banding. gDitherStrength == 0 -> the
// offset is 0 and the pack is byte-identical to the plain round.
static const float cg_bayer8[64] =
{
    -0.4921875,  0.0078125, -0.3671875,  0.1328125, -0.4609375,  0.0390625, -0.3359375,  0.1640625,
     0.2578125, -0.2421875,  0.3828125, -0.1171875,  0.2890625, -0.2109375,  0.4140625, -0.0859375,
    -0.3046875,  0.1953125, -0.4296875,  0.0703125, -0.2734375,  0.2265625, -0.3984375,  0.1015625,
     0.4453125, -0.0546875,  0.3203125, -0.1796875,  0.4765625, -0.0234375,  0.3515625, -0.1484375,
    -0.4453125,  0.0546875, -0.3203125,  0.1796875, -0.4765625,  0.0234375, -0.3515625,  0.1484375,
     0.3046875, -0.1953125,  0.4296875, -0.0703125,  0.2734375, -0.2265625,  0.3984375, -0.1015625,
    -0.2578125,  0.2421875, -0.3828125,  0.1171875, -0.2890625,  0.2109375, -0.4140625,  0.0859375,
     0.4921875, -0.0078125,  0.3671875, -0.1328125,  0.4609375, -0.0390625,  0.3359375, -0.1640625,
};

uint cg_pack_bgra(float3 c, uint px, uint py)
{
    c = saturate(c);
    float o = gDitherStrength * cg_bayer8[(py & 7) * 8 + (px & 7)];
    uint r = (uint)clamp(c.r * 255.0 + 0.5 + o, 0.0, 255.0);
    uint g = (uint)clamp(c.g * 255.0 + 0.5 + o, 0.0, 255.0);
    uint b = (uint)clamp(c.b * 255.0 + 0.5 + o, 0.0, 255.0);
    return 0xFF000000u | (r << 16) | (g << 8) | b;
}

float3 EvalPalette(
    float in_smooth, float in_dist, float in_iter, float in_maxIter,
    float in_t, float in_nx, float in_ny, float in_zr, float in_zi,
    float in_dzr, float in_dzi, float in_arg, float in_mag,
    float in_isInSet, float in_pxScale)
{";

    /// <summary>Closes the EvalPalette function opened by
    /// <see cref="ColorPreludeHead"/>.</summary>
    public const string ColorPreludeTail = "}";

    /// <summary>Colour-write splice for the in-set branch of CSMain. Distance +
    /// normal aren't computed in-shader (dist=0, nx=ny=0); in_isInSet=1.</summary>
    public const string InSetColorSplice = @"
        gColor[idx] = cg_pack_bgra(EvalPalette(
            0.0, 0.0, (float)gMaxIter, (float)gMaxIter,
            0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0), x, y);
";

    /// <summary>Colour-write splice for the escape branch of CSMain.</summary>
    public const string EscapeColorSplice = @"
        float t_iter = gMaxIter > 0 ? sm / (float)gMaxIter : 0.0;
        float in_arg = atan2(zi, zr);
        float in_mag = sqrt(zr * zr + zi * zi);
        gColor[idx] = cg_pack_bgra(EvalPalette(
            sm, 0.0, (float)it, (float)gMaxIter,
            t_iter, 0.0, 0.0, zr, zi, dr, di, in_arg, in_mag, 0.0, 0.0), x, y);
";

    /// <summary>Colour-write splice for the whole-cardioid / period-2 bulb-skip
    /// branch. finalZD is (0,0,1,0) here, so in_dzr=1; in_isInSet=1.</summary>
    public const string BulbSkipColorSplice = @"
        gColor[idx] = cg_pack_bgra(EvalPalette(
            0.0, 0.0, (float)gMaxIter, (float)gMaxIter,
            0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0), x, y);
";

    /// <summary>Compose the full colour-emitting kernel for a GPU palette:
    /// header + palette helpers + colour prelude + EvalPalette body + CSMain
    /// with the colour splices filled. Shared by D3D (FXC) and V2 Vulkan
    /// (DXC). <paramref name="paletteBody"/> is the IGpuHlslPalette body (a
    /// let-bindings + <c>return float3</c>); <paramref name="paletteHelpers"/>
    /// its prelude (cg_hsv_to_rgb, etc.), empty when none.</summary>
    public static string BuildColor(string? paletteHelpers, string? paletteBody)
    {
        string helpers = string.IsNullOrEmpty(paletteHelpers) ? "" : paletteHelpers + "\n";
        string body = string.IsNullOrEmpty(paletteBody) ? "    return float3(0.0, 0.0, 0.0);" : paletteBody;
        return HlslBase
            + helpers
            + ColorPreludeHead
            + body + "\n"
            + ColorPreludeTail + "\n"
            + HlslEntry(emitColor: true, InSetColorSplice, EscapeColorSplice, BulbSkipColorSplice);
    }

    // ── Perturbation variant (V6, issue #82) ─────────────────────────────────
    //
    // Deep-zoom (Zoom ≫ MaxGpuZoom) escape-time by PERTURBATION over a
    // precomputed reference orbit, run on the GPU in `double`. This is the exact
    // twin of MandelbrotCalculator.ComputePixelPTRebased (the default, non-DD
    // path): the δ-chain and dc are plain `double`, the reference orbit is the
    // Hi-limb double sequence the CPU already computes, and Zhuoran rebasing
    // (SM-2) keeps the chain glitch-free. NO in-shader limb (DD/QD) math — the
    // spike (dev-plan §14) proved that unnecessary for this path. δ stays double
    // at any depth (Docs/Deep-Zoom-Perturbation.md §2); only the reference orbit
    // + centre need precision, and those are built CPU-side.
    //
    // Self-contained (own cbuffer + bindings, NOT HlslBase) so it can be a
    // separate compiled module. Same two-compiler rule: no vk:: attributes; DXC
    // pins bindings with -fvk-*-shift (b0→0, t0/t1→100/101, u0..u2→200..202),
    // FXC uses the registers directly. Outputs iter + smooth + finalZD(zr,zi,
    // drv,div) so the calculator's FillAuxAndColorHP drives colour/dist/normal
    // on the CPU exactly as it does for the CPU PT path.
    //
    // dc for pixel (x,y) = (gOffX0 + x, gOffY0 + y) · gScale — the caller passes
    // the pixel-(0,0) offset so the calculator's image-space column/row offsets
    // (sub-rect, effective-image centre) map through unchanged.

    /// <summary>Compute-shader entry point for the perturbation variant.</summary>
    public const string PerturbEntryPoint = "CSPerturb";

    /// <summary>Compute-shader entry point for the SA (Series-Approximation)
    /// iteration-skipping perturbation variant (#88 spike).</summary>
    public const string PerturbSaEntryPoint = "CSPerturbSA";

    /// <summary>TDR tiling budget — max iter-pixels (rows·width·maxIter) per
    /// perturbation dispatch. A deep-zoom full-image dispatch can run tens of
    /// seconds on a weak-FP64 GPU and trip the OS GPU watchdog (device lost),
    /// which on D3D also kills the shared present device. Splitting the frame
    /// into row bands keeps each dispatch short. Shared by both backends.</summary>
    public const long PerturbDispatchIterBudget = 40_000_000;

    /// <summary>Test/override hook — force a specific band height (&gt;0) so a
    /// smoke run can exercise the multi-band path deterministically. Seeded from
    /// FF_PERTURB_BANDROWS; 0 = auto (budget-derived).</summary>
    public static int PerturbBandRowsOverride =
        int.TryParse(System.Environment.GetEnvironmentVariable("FF_PERTURB_BANDROWS"), out int pbr) && pbr > 0 ? pbr : 0;

    /// <summary>Rows per perturbation dispatch band for the given frame, derived
    /// from <see cref="PerturbDispatchIterBudget"/> and the frame's width +
    /// maxIter (or the override). Always ≥ 1 and ≤ height.</summary>
    public static int PerturbBandRows(int width, int height, int maxIter)
    {
        if (PerturbBandRowsOverride > 0) return System.Math.Min(PerturbBandRowsOverride, height);
        long denom = (long)System.Math.Max(1, width) * System.Math.Max(1, maxIter);
        int rows = (int)System.Math.Max(1, PerturbDispatchIterBudget / denom);
        return System.Math.Min(rows, height);
    }

    /// <summary>Perf-fallback budget (ms). After the first row band completes,
    /// each backend extrapolates band0·bandCount; if it exceeds this, the GPU is
    /// too slow at this depth (weak FP64) and the dispatch aborts so the caller
    /// falls back to the CPU deep path. Tunable via FF_GPU_PERTURB_BUDGET_MS;
    /// default 3000 ms. 0 or negative disables the check (always finish on GPU).</summary>
    public static double PerturbBudgetMs =
        double.TryParse(System.Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double bms) && bms > 0 ? bms : 3000.0;

    /// <summary>True when the extrapolated full-frame GPU time (first band ×
    /// band count) exceeds <see cref="PerturbBudgetMs"/> — i.e. abort to CPU.</summary>
    public static bool PerturbTooSlow(double band0Ms, int bandCount)
        => PerturbBudgetMs > 0 && band0Ms * bandCount > PerturbBudgetMs;

    /// <summary>Marker message for the perf-abort exception so the calculator can
    /// tell "GPU too slow" apart from a genuine device-lost error.</summary>
    public const string PerturbTooSlowMarker = "GPU-PERTURB-TOO-SLOW";

    /// <summary>Compose the double perturbation kernel. Standalone HLSL (its own
    /// cbuffer + reference-orbit SRVs + output UAVs); requires FP64 support on
    /// the device (Vulkan <c>shaderFloat64</c> / D3D double shader ops).</summary>
    public static string BuildPerturb() => @"
// Layout note: doubles FIRST so every double lands 8-byte-aligned at a fixed
// offset (0/8/16/24) with no 16-byte-row straddle, then the ints. Matches the
// C# PerturbParams / PerturbParamsBlob (both backends) byte-for-byte. 64 bytes.
cbuffer PerturbParams : register(b0)
{
    double gScale;      // world units per pixel
    double gEscapeR2;   // escape radius squared (matches CPU EscapeRadius2)
    double gOffX0;      // column offset of pixel x=0 (pixels; add x)
    double gOffY0;      // row offset of pixel y=0 (pixels; add y)
    int    gWidth;
    int    gHeight;
    int    gMaxIter;
    int    gRefLen;
    int    gRowBase;    // TDR tiling: this dispatch covers rows [gRowBase, gRowBase+groupsY*8)
    int    gPad0;
    int    gPad1;
    int    gPad2;
}

// Reference orbit Z_n, Hi-limb doubles (the CPU _refZr/_refZi). Length gRefLen.
StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);   // .xy = zr,zi  .zw = drv,div

[numthreads(8, 8, 1)]
void CSPerturb(uint3 tid : SV_DispatchThreadID)
{
    // TDR tiling — a deep-zoom full-image dispatch can run tens of seconds on a
    // weak-FP64 GPU and trip the OS watchdog (DXGI_ERROR_DEVICE_REMOVED), which
    // also kills the shared present device. The host splits the frame into row
    // bands and offsets each dispatch by gRowBase; the actual pixel row is
    // gRowBase + tid.y.
    int px = (int)tid.x;
    int py = gRowBase + (int)tid.y;
    if (px >= gWidth || py >= gHeight) return;
    int idx = py * gWidth + px;

    // dc = pixelOffset · scale (double).
    double dcR = (gOffX0 + (double)px) * gScale;
    double dcI = (gOffY0 + (double)py) * gScale;

    double dr = 0.0, di = 0.0;      // δ_0 = 0
    double drv = 1.0, div = 0.0;    // dz/dc (IQ convention) for distance + normals
    int m = 0;                      // reference-orbit index
    double zr = 0.0, zi = 0.0;      // full value z = Z[m] + δ (last = escape z)

    int iter;
    [loop]
    for (iter = 0; iter < gMaxIter; iter++)
    {
        double Zr = gRefZr[m];
        double Zi = gRefZi[m];
        zr = Zr + dr;
        zi = Zi + di;

        double zmag2 = zr * zr + zi * zi;
        if (zmag2 >= gEscapeR2) break;

        // Derivative of the FULL orbit — independent of rebasing, before it.
        double ndrv = 2.0 * (zr * drv - zi * div) + 1.0;
        double ndiv = 2.0 * (zr * div + zi * drv);
        drv = ndrv; div = ndiv;

        // Rebase when the reference no longer anchors this pixel or is exhausted.
        double dmag2 = dr * dr + di * di;
        if (zmag2 < dmag2 || m + 1 >= gRefLen)
        {
            dr = zr; di = zi;
            Zr = 0.0; Zi = 0.0;
            m = 0;
        }

        // δ_{n+1} = (2·Z[m] + δ)·δ + dc   (Z[m]=0 right after a rebase).
        double a = 2.0 * Zr + dr;
        double b = 2.0 * Zi + di;
        double ndr = a * dr - b * di + dcR;
        double ndi = a * di + b * dr + dcI;
        dr = ndr; di = ndi;
        m++;
    }

    gFinalZD[idx] = float4((float)zr, (float)zi, (float)drv, (float)div);
    if (iter >= gMaxIter)
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
    }
    else
    {
        gIter[idx] = (uint)iter;
        // Match FillAuxAndColorHP: iters + 1 - log2(log2(mag)), mag = |z|.
        float magf = sqrt((float)(zr * zr + zi * zi));
        gSmooth[idx] = (float)iter + 1.0 - log2(log2(magf));
    }
}
";

    // ── #88 SA (Series-Approximation) iteration-skipping perturbation ──────────
    //
    // Extends BuildPerturb with an SA prelude: skip the first k iterations
    // analytically by evaluating the 3rd-order δ-polynomial in dc, then run the
    // identical rebased δ loop from iter=k, m=k. Mirrors the CPU SA prelude
    // (Engine/Math/SeriesApproximation.cs + the FindSkip/EvalDelta call sites in
    // MandelbrotCalculator) but seeds the REBASED perturbation loop rather than
    // the DD/QD full-value loop.
    //
    // FindSkip runs in-shader with SQUARED magnitudes (HLSL has no double sqrt
    // intrinsic; squaring both sides of |C|·|dc| ≤ τ·|B| is exact enough — SA
    // correctness is robust to a ±1 difference in k because both k and k±1 are
    // below-tolerance skip points). Coefficients A_n,B_n,C_n,D_n arrive as eight
    // double SSBOs (t2..t9), length gRefLen+1. Correctness is speed-independent,
    // so this validates on weak-FP64 hardware (GT710/lavapipe); the perf payoff
    // is deferred to strong-FP64 HW (see Docs/Technical/GPU-DeepZoom-Handoff.md).
    public static string BuildPerturbSA() => @"
// cbuffer: 5 doubles FIRST (offsets 0/8/16/24/32) then 10 ints. 80 bytes.
// Matches the C# PerturbSaParamsBlob byte-for-byte.
cbuffer PerturbParams : register(b0)
{
    double gScale;
    double gEscapeR2;
    double gOffX0;
    double gOffY0;
    double gSaTol;      // SA truncation tolerance (CPU SaTolerance = 1e-3)
    int    gWidth;
    int    gHeight;
    int    gMaxIter;
    int    gRefLen;
    int    gRowBase;
    int    gSafeMax;    // SeriesApproximation.SafeMax — max valid coeff index
    int    gPad0;
    int    gPad1;
    int    gPad2;
    int    gPad3;
}

StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);
// SA coefficients (complex): A linear, B quadratic, C cubic, D quartic-bound.
StructuredBuffer<double> gAR : register(t2);
StructuredBuffer<double> gAI : register(t3);
StructuredBuffer<double> gBR : register(t4);
StructuredBuffer<double> gBI : register(t5);
StructuredBuffer<double> gCR : register(t6);
StructuredBuffer<double> gCI : register(t7);
StructuredBuffer<double> gDR : register(t8);
StructuredBuffer<double> gDI : register(t9);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);

[numthreads(8, 8, 1)]
void CSPerturbSA(uint3 tid : SV_DispatchThreadID)
{
    int px = (int)tid.x;
    int py = gRowBase + (int)tid.y;
    if (px >= gWidth || py >= gHeight) return;
    int idx = py * gWidth + px;

    double dcR = (gOffX0 + (double)px) * gScale;
    double dcI = (gOffY0 + (double)py) * gScale;

    double dr = 0.0, di = 0.0;      // δ_0 = 0
    double drv = 1.0, div = 0.0;    // dz/dc (IQ convention)
    int m = 0;                      // reference-orbit index
    int iterStart = 0;              // SA skip target
    double zr = 0.0, zi = 0.0;

    // ── SA FindSkip (squared-magnitude binary search, mirrors CPU FindSkip) ──
    double dcMag2 = dcR * dcR + dcI * dcI;
    int hi = min(gSafeMax, gMaxIter - 1);
    int k = 0;
    if (dcMag2 == 0.0)
    {
        k = hi;                     // centre pixel — full skip is safe
    }
    else if (hi > 0)
    {
        double tol2 = gSaTol * gSaTol;
        int lo = 0, best = 0;
        [loop]
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            double Bm2 = gBR[mid] * gBR[mid] + gBI[mid] * gBI[mid];
            double Cm2 = gCR[mid] * gCR[mid] + gCI[mid] * gCI[mid];
            double Dm2 = gDR[mid] * gDR[mid] + gDI[mid] * gDI[mid];
            bool cubicOk  = Cm2 * dcMag2 <= tol2 * Bm2;   // (|C|·|dc|)² ≤ (τ·|B|)²
            bool quarticOk = Dm2 * dcMag2 <= tol2 * Cm2;  // (|D|·|dc|)² ≤ (τ·|C|)²
            if (cubicOk && quarticOk) { best = mid; lo = mid + 1; }
            else                      { hi = mid - 1; }
        }
        k = best;
    }

    // Apply the skip only when it clears the CPU guard (k ≥ 16, k ≤ refLen).
    if (k >= 16 && k <= gRefLen)
    {
        // EvalDelta(k): δ_k = A_k·dc + B_k·dc² + C_k·dc³.
        double dc2R = dcR * dcR - dcI * dcI;
        double dc2I = 2.0 * dcR * dcI;
        double dc3R = dc2R * dcR - dc2I * dcI;
        double dc3I = dc2R * dcI + dc2I * dcR;
        double aR = gAR[k] * dcR - gAI[k] * dcI;
        double aI = gAR[k] * dcI + gAI[k] * dcR;
        double bR = gBR[k] * dc2R - gBI[k] * dc2I;
        double bI = gBR[k] * dc2I + gBI[k] * dc2R;
        double cR = gCR[k] * dc3R - gCI[k] * dc3I;
        double cI = gCR[k] * dc3I + gCI[k] * dc3R;
        dr = aR + bR + cR;
        di = aI + bI + cI;

        // EvalDDelta(k): dδ_k/dc = A_k + 2·B_k·dc + 3·C_k·dc² — derivative seed.
        double twoBR = 2.0 * (gBR[k] * dcR - gBI[k] * dcI);
        double twoBI = 2.0 * (gBR[k] * dcI + gBI[k] * dcR);
        double threeCR = 3.0 * (gCR[k] * dc2R - gCI[k] * dc2I);
        double threeCI = 3.0 * (gCR[k] * dc2I + gCI[k] * dc2R);
        drv = gAR[k] + twoBR + threeCR;
        div = gAI[k] + twoBI + threeCI;

        m = k;
        iterStart = k;
    }

    // ── Identical rebased δ loop as BuildPerturb, resumed from iterStart/m=k ──
    int iter;
    [loop]
    for (iter = iterStart; iter < gMaxIter; iter++)
    {
        double Zr = gRefZr[m];
        double Zi = gRefZi[m];
        zr = Zr + dr;
        zi = Zi + di;

        double zmag2 = zr * zr + zi * zi;
        if (zmag2 >= gEscapeR2) break;

        double ndrv = 2.0 * (zr * drv - zi * div) + 1.0;
        double ndiv = 2.0 * (zr * div + zi * drv);
        drv = ndrv; div = ndiv;

        double dmag2 = dr * dr + di * di;
        if (zmag2 < dmag2 || m + 1 >= gRefLen)
        {
            dr = zr; di = zi;
            Zr = 0.0; Zi = 0.0;
            m = 0;
        }

        double a = 2.0 * Zr + dr;
        double b = 2.0 * Zi + di;
        double ndr = a * dr - b * di + dcR;
        double ndi = a * di + b * dr + dcI;
        dr = ndr; di = ndi;
        m++;
    }

    gFinalZD[idx] = float4((float)zr, (float)zi, (float)drv, (float)div);
    if (iter >= gMaxIter)
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
    }
    else
    {
        gIter[idx] = (uint)iter;
        float magf = sqrt((float)(zr * zr + zi * zi));
        gSmooth[idx] = (float)iter + 1.0 - log2(log2(magf));
    }
}
";

    /// <summary>Per-emit CSMain. The three colour splice points are empty in
    /// the base variant and filled by <c>MandelbrotGpuKernel</c> (D3D, V2 on
    /// Vulkan) for the colour-emitting variant.</summary>
    public static string HlslEntry(bool emitColor, string inSetColor = "", string escapeColor = "", string bulbSkipColor = "")
    {
        return $@"
[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{{
    uint x = tid.x;
    uint y = tid.y;
    if ((int)x >= gWidth || (int)y >= gHeight) return;

    int idx = (int)y * gWidth + (int)x;

    // Reconstruct cx / cy using the split centre.
    float fx = (float)x - 0.5 * gWidth;
    float fy = (float)y - 0.5 * gHeight;
    float cx = gCXHi + fx * gScaleHi + gCXLo + fx * gScaleLo;
    float cy = gCYHi + fy * gScaleHi + gCYLo + fy * gScaleLo;

    // Per-row cap lookup. Falls back to gMaxIter when disabled or when
    // the buffer holds 0 for this row (defensive).
    int rowMaxIt = gMaxIter;
    if (gUsePerRow != 0)
    {{
        uint rc = gPerRow[y];
        if (rc > 0) rowMaxIt = (int)rc;
    }}

    // Whole-cardioid + period-2 bulb early-out. Mandelbrot-only — Julia /
    // BurningShip / Tricorn have different in-set shapes. Always writes
    // gMaxIter so the in-set gate is consistent across bands regardless of
    // per-row cap. Final z+dz are (0,0,1,0) — matches the CPU bulb-skip
    // writeback.
    if (gFractalKind == 0 && (InCardioid(cx, cy) || InPeriod2Bulb(cx, cy)))
    {{
        gIter[idx]    = (uint)gMaxIter;
        gSmooth[idx]  = 0.0;
        gFinalZD[idx] = float4(0.0, 0.0, 1.0, 0.0);
        {bulbSkipColor}
        return;
    }}

    // Per-fractal init. Mandelbrot/BurningShip/Tricorn: z_0 = 0, c =
    // pixel coord. Julia: z_0 = pixel coord, c = (gParam0, gParam1) const.
    float zr, zi;
    float cIterR, cIterI;
    if (gFractalKind == 1)
    {{
        zr = cx;     zi = cy;
        cIterR = gParam0; cIterI = gParam1;
    }}
    else
    {{
        zr = 0.0;    zi = 0.0;
        cIterR = cx; cIterI = cy;
    }}
    float dr = 1.0;
    float di = 0.0;
    int   it = 0;
    [loop]
    for (; it < rowMaxIt; it++)
    {{
        float fzr = zr;
        float fzi = zi;
        if (gFractalKind == 2)      {{ fzr = abs(zr); fzi = abs(zi); }}
        else if (gFractalKind == 3) {{ fzi = -zi; }}

        float zr2 = fzr * fzr;
        float zi2 = fzi * fzi;
        float mag2 = zr2 + zi2;
        if (mag2 >= gBailout2) break;

        float newDr = 2.0 * (fzr * dr - fzi * di) + 1.0;
        float newDi = 2.0 * (fzr * di + fzi * dr);
        dr = newDr;
        di = newDi;

        float zrNew = zr2 - zi2 + cIterR;
        float zi_new_unscaled = fzr * fzi;
        zi = zi_new_unscaled + zi_new_unscaled + cIterI;
        zr = zrNew;
    }}

    gFinalZD[idx] = float4(zr, zi, dr, di);
    if (it >= rowMaxIt)
    {{
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
        {inSetColor}
    }}
    else
    {{
        gIter[idx] = (uint)it;
        float mag = sqrt(zr * zr + zi * zi);
        float nu = log(log(max(mag, 1.001))) / log(2.0);
        float sm = (float)it + 1.0 - nu;
        gSmooth[idx] = sm;
        {escapeColor}
    }}
}}
";
    }
}
