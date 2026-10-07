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
    // #1173-I: domain warp (FractalDomainWarp). gWarpStrength 0 = off (the
    // un-warped c expression is untouched). gWarpK = 3 * frequency (1 when the
    // frequency is <= 0); gWarpHalfSpan = half the longer view span, plane units.
    float gWarpStrength;
    float gWarpK;
    float gWarpHalfSpan;
    // 18 fields x 4 bytes = 72, padded by the host to 80 (float4 multiple).
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

// #1173-I: |(x, y)| without fp32 overflow. A high-power Multibrot escapes with
// |z| ~ 1e21, so x*x overflows and sqrt gave +inf (smooth -inf, the pixel black).
// Bit-identical to sqrt(x*x + y*y) whenever that is finite.
float SafeMag(float x, float y)
{
    float m2 = x * x + y * y;
    if (m2 <= 3.0e38) return sqrt(m2);
    float a = max(abs(x), abs(y));
    float b = min(abs(x), abs(y)) / a;
    return a * sqrt(1.0 + b * b);
}

// #1173-I: the CPU FractalDomainWarp.Apply on the pixel's offset from the view
// centre (fx, fy pixels), then c = centre + warped offset.
void ApplyDomainWarp(float fx, float fy, inout float cx, inout float cy)
{
    float ox = fx * gScaleHi + fx * gScaleLo;
    float oy = fy * gScaleHi + fy * gScaleLo;
    float nx = ox / gWarpHalfSpan;
    float ny = oy / gWarpHalfSpan;
    ox += gWarpStrength * sin(ny * gWarpK + nx * 1.3) * gWarpHalfSpan;
    oy += gWarpStrength * sin(nx * gWarpK - ny * 1.3) * gWarpHalfSpan;
    cx = gCXHi + gCXLo + ox;
    cy = gCYHi + gCYLo + oy;
}

// #1173-I: Multibrot z^d + c with dz/dc, d = (int)gParam0 (the host keeps it in
// [2, MaxGpuMultibrotExponent]). Mirrors MultibrotKernel.Step: closed forms for
// d = 3, 4, 5. Other d (2 included) are polar on the CPU; here z^(d-1) is built
// by repeated complex multiplication instead - the same value, without the
// fp32 pow / atan2 / cos / sin error.
void MultibrotStep(inout float zr, inout float zi, inout float dr, inout float di, float cr, float ci)
{
    int d = (int)gParam0;
    float nzr, nzi, pr, pi;
    if (d == 3)
    {
        float zr2 = zr * zr; float zi2 = zi * zi;
        nzr = zr * (zr2 - 3.0 * zi2) + cr;
        nzi = zi * (3.0 * zr2 - zi2) + ci;
        pr = 3.0 * (zr2 - zi2);
        pi = 6.0 * zr * zi;
    }
    else if (d == 4)
    {
        float u = zr * zr - zi * zi; float v = 2.0 * zr * zi;
        nzr = u * u - v * v + cr;
        nzi = 2.0 * u * v + ci;
        pr = 4.0 * (zr * u - zi * v);
        pi = 4.0 * (zr * v + zi * u);
    }
    else if (d == 5)
    {
        float u = zr * zr - zi * zi; float v = 2.0 * zr * zi;
        float U = u * u - v * v;     float V = 2.0 * u * v;
        nzr = zr * U - zi * V + cr;
        nzi = zr * V + zi * U + ci;
        pr = 5.0 * U;
        pi = 5.0 * V;
    }
    else
    {
        if (zr * zr + zi * zi == 0.0) { zr = cr; zi = ci; return; }   // CPU: z = c, dz/dc unchanged
        float qr = zr, qi = zi;                  // q = z^(d-1)
        [loop]
        for (int k = 2; k < d; k++)
        {
            float t = qr * zr - qi * zi;
            qi = qr * zi + qi * zr;
            qr = t;
        }
        nzr = qr * zr - qi * zi + cr;            // z^d = q * z
        nzi = qr * zi + qi * zr + ci;
        pr = d * qr;                             // dz^d/dz = d z^(d-1)
        pi = d * qi;
    }
    float ndr = pr * dr - pi * di + 1.0;
    float ndi = pr * di + pi * dr;
    zr = nzr; zi = nzi; dr = ndr; di = ndi;
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

    /// <summary>Colour-write splice for the escape branch of CSMain.
    /// in_dist is the exterior distance estimate |z|·ln|z|/|dz| — mirrors the
    /// CPU FillAuxAndColorHP formula so distance-shaded palettes (HSV, DSL
    /// themes reading `dist`, …) darken with distance on the GPU instead of
    /// rendering a flat full-brightness fill. HLSL `log` is natural log, so it
    /// matches the CPU Math.Log inner term exactly.</summary>
    public const string EscapeColorSplice = @"
        float t_iter = gMaxIter > 0 ? sm / (float)gMaxIter : 0.0;
        float in_arg = atan2(zi, zr);
        float in_mag = SafeMag(zr, zi);
        float de_dz = SafeMag(dr, di);
        float in_dist = de_dz > 1e-10 ? in_mag * log(in_mag) / de_dz : 0.0;
        gColor[idx] = cg_pack_bgra(EvalPalette(
            sm, in_dist, (float)it, (float)gMaxIter,
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

    // ── F16 (#603) — orbit-accumulator colour variant ────────────────────────
    //
    // An orbit ColorGen theme reads per-iteration accumulators the escape-only
    // kernel never computes. This variant splices a per-iteration sampling loop
    // (accumulating ONLY the mask'd inputs) into CSMain, extends EvalPalette with
    // the 11 orbit params, and passes the accumulated means at the escape write.
    // Mirrors the CPU InterpretedOrbitColorMap.Sample maths in float. Scope: the
    // shallow-escape kernel, exterior pixels; in-set uses the normal isInSet=1
    // path (orbit params 0). Deep zoom has its own orbit kernel (BuildPerturbOrbit,
    // #607). Mask bit order matches GpuOrbitInputOrder / GpuOrbitInputs.
    public const int OrbTrapMin = 1 << 0, OrbTrapCross = 1 << 1, OrbTrapRing = 1 << 2,
                     OrbTrapHyperbola = 1 << 3, OrbTrapHexagon = 1 << 4, OrbStripe = 1 << 5,
                     OrbTia = 1 << 6, OrbCurvature = 1 << 7, OrbLyapunov = 1 << 8,
                     OrbGaussian = 1 << 9, OrbExp = 1 << 10;

    /// <summary>The 11 orbit params, always appended to EvalPalette (unused ones
    /// are passed 0 at the call site — HLSL tolerates unused params, and only the
    /// mask'd accumulators are actually computed in the loop).</summary>
    private const string OrbitParams =
        "    float in_trapMin, float in_trapCross, float in_trapRing, float in_trapHyperbola, float in_trapHexagon,\n" +
        "    float in_stripeAvg, float in_tiaAvg, float in_curvature, float in_lyapunov, float in_gaussian, float in_expSmooth)";

    /// <summary>ColorPreludeHead with the 11 orbit params spliced into the
    /// EvalPalette signature (before the closing paren).</summary>
    private static string OrbitPreludeHead() =>
        ColorPreludeHead.Replace(
            "    float in_isInSet, float in_pxScale)",
            "    float in_isInSet, float in_pxScale,\n" + OrbitParams);

    /// <summary>#1173-J — the orbit kernel's per-pixel trap output: the slot-1
    /// trap minimum (<c>in_trapMin</c>) on escape, 0 for in-set / bulb-skip pixels
    /// and when the theme does not read <c>trapMin</c> — exactly what the CPU orbit
    /// path writes to <c>MandelbrotCalculator.TrapBuffer</c> for an exterior theme.
    /// It is the orbit-trap relief height source. Register u4 → Vulkan binding 204
    /// (UShift + 4). Only the orbit variant declares it.</summary>
    public const string OrbitTrapUav = @"
RWStructuredBuffer<float> gTrap : register(u4);
";

    /// <summary>Compose the orbit colour kernel: header + helpers + orbit
    /// EvalPalette + CSMain with per-iteration accumulation for the mask'd
    /// inputs. Shared by D3D (FXC) and Vulkan (DXC) exactly like BuildColor.</summary>
    public static string BuildColorOrbit(string? paletteHelpers, string? paletteBody, int mask)
    {
        string helpers = string.IsNullOrEmpty(paletteHelpers) ? "" : paletteHelpers + "\n";
        string body = string.IsNullOrEmpty(paletteBody) ? "    return float3(0.0, 0.0, 0.0);" : paletteBody;
        return HlslBase
            + OrbitTrapUav
            + helpers
            + OrbitPreludeHead()
            + body + "\n"
            + ColorPreludeTail + "\n"
            + HlslEntryOrbit(mask);
    }

    /// <summary>CSMain for the orbit colour kernel — a copy of HlslEntry with
    /// accumulator declarations before the loop, a per-iteration Sample block
    /// (it &gt; 0, after the escape break, on the RAW z — Mandelbrot orbit), and
    /// the accumulated means passed to EvalPalette on the escape branch.</summary>
    public static string HlslEntryOrbit(int mask)
    {
        string decl = OrbitDecl(mask), samp = OrbitSample(mask), means = OrbitMeans(mask);
        return HlslEntryOrbitBody(decl, samp, means);
    }

    /// <summary>Accumulator declarations for the mask'd orbit inputs (function scope,
    /// before the iteration loop). Shared by the shallow orbit kernel and the
    /// deep-zoom perturbation orbit kernel (G4.6).</summary>
    private static string OrbitDecl(int mask)
    {
        bool On(int bit) => (mask & bit) != 0;

        // Accumulator declarations (only what the mask needs).
        var decl = new System.Text.StringBuilder();
        if (On(OrbTrapMin))       decl.Append("    float acc_trapMin = 3.0e38;\n");
        if (On(OrbTrapCross))     decl.Append("    float acc_trapCross = 3.0e38;\n");
        if (On(OrbTrapRing))      decl.Append("    float acc_trapRing = 3.0e38;\n");
        if (On(OrbTrapHyperbola)) decl.Append("    float acc_trapHyperbola = 3.0e38;\n");
        if (On(OrbTrapHexagon))   decl.Append("    float acc_trapHexagon = 3.0e38;\n");
        if (On(OrbStripe))        decl.Append("    float acc_stripeSum = 0.0; int acc_stripeCount = 0;\n");
        if (On(OrbTia))           decl.Append("    float acc_tiaSum = 0.0; int acc_tiaCount = 0;\n");
        if (On(OrbLyapunov))      decl.Append("    float acc_lyaSum = 0.0; int acc_lyaCount = 0;\n");
        if (On(OrbGaussian))      decl.Append("    float acc_gauSum = 0.0; int acc_gauCount = 0;\n");
        if (On(OrbExp))           decl.Append("    float acc_expSum = 0.0; int acc_expCount = 0;\n");
        if (On(OrbCurvature))     decl.Append(
            "    float cvPrevZr = 0.0, cvPrevZi = 0.0, cvPrevSegR = 0.0, cvPrevSegI = 0.0;\n" +
            "    float acc_cvSum = 0.0; int acc_cvCount = 0;\n");
        return decl.ToString();
    }

    /// <summary>Per-iteration sample block for the mask'd inputs (mirrors
    /// InterpretedOrbitColorMap.Sample in float). Reads the float locals zr, zi
    /// (pre-update z_it, RAW), cIterR, cIterI (c) and the int it (iteration index).
    /// <paramref name="deepTia"/>: emit the deep kernel's cancellation-free
    /// triangle-inequality sample (needs the double locals pzrD, pziD = z_{it-1})
    /// in place of the float |z − c| form.</summary>
    private static string OrbitSample(int mask, bool deepTia = false)
    {
        bool On(int bit) => (mask & bit) != 0;

        // Per-iteration sample block (mirrors InterpretedOrbitColorMap.Sample).
        // zr, zi = pre-update z_it (RAW); cIterR, cIterI = c; it = iteration index.
        var samp = new System.Text.StringBuilder();
        if (On(OrbTrapMin))
            samp.Append("            { float d = sqrt(zr*zr + zi*zi); acc_trapMin = min(acc_trapMin, d); }\n");
        if (On(OrbTrapCross))
            samp.Append("            { float d = min(abs(zr), abs(zi)); acc_trapCross = min(acc_trapCross, d); }\n");
        if (On(OrbTrapRing))
            samp.Append("            { float dx = zr + 1.0; float dy = zi; float d = abs(sqrt(dx*dx + dy*dy) - 0.3); acc_trapRing = min(acc_trapRing, d); }\n");
        if (On(OrbTrapHyperbola))
            samp.Append("            { float f = abs(zr*zi) - 1.0; float g = max(sqrt(zr*zr + zi*zi), 1e-6); float d = abs(f) / g; acc_trapHyperbola = min(acc_trapHyperbola, d); }\n");
        if (On(OrbTrapHexagon))
            samp.Append(
                "            {\n" +
                "                const float kx = -0.8660254037844387; const float ky = 0.5; const float kz = 0.5773502691896257;\n" +
                "                float px = abs(zr); float py = abs(zi);\n" +
                "                float dot2 = 2.0 * min(kx*px + ky*py, 0.0);\n" +
                "                px -= dot2*kx; py -= dot2*ky;\n" +
                "                px -= clamp(px, -kz, kz); py -= 1.0;\n" +
                "                acc_trapHexagon = min(acc_trapHexagon, sqrt(px*px + py*py));\n" +
                "            }\n");
        if (On(OrbStripe))
            samp.Append("            { float s = 0.5 + 0.5*sin(7.0*atan2(zi, zr)); acc_stripeSum += s; acc_stripeCount += 1; }\n");
        if (On(OrbTia) && deepTia)
            // t = (|z| - m) / (M - m), m = ||w| - |c||, M = |w| + |c|, w = z_{n-1}^2 = z - c.
            // In float both differences cancel once |w| << |c| (near a minibrot, where
            // the orbit passes close to 0): M - m is 2 min(|w|, |c|) exactly, and for
            // |w| <= |c|, |z| - m = (|c + w|^2 - |c|^2) / (|z| + |c|) + |w|
            //                     = (2 Re(conj(c) w) + |w|^2) / (|z| + |c|) + |w|.
            // w comes from z_{n-1} in double, so it keeps its relative precision.
            samp.Append(
                "            if (it >= 2) {\n" +
                "                float wr = (float)(pzrD * pzrD - pziD * pziD);\n" +
                "                float wi = (float)(2.0 * pzrD * pziD);\n" +
                "                float absW = sqrt(wr*wr + wi*wi);\n" +
                "                float absC = sqrt(cIterR*cIterR + cIterI*cIterI);\n" +
                "                float absZ = sqrt(zr*zr + zi*zi);\n" +
                "                float den = 2.0 * min(absW, absC);\n" +
                "                float num = absW <= absC\n" +
                "                    ? (2.0 * (cIterR*wr + cIterI*wi) + absW*absW) / (absZ + absC) + absW\n" +
                "                    : absZ - (absW - absC);\n" +
                "                if (den > 1e-12) { acc_tiaSum += num / den; acc_tiaCount += 1; }\n" +
                "            }\n");
        else if (On(OrbTia))
            samp.Append(
                "            if (it >= 2) {\n" +
                "                float zMcR = zr - cIterR; float zMcI = zi - cIterI;\n" +
                "                float absZprev2 = sqrt(zMcR*zMcR + zMcI*zMcI);\n" +
                "                float absC = sqrt(cIterR*cIterR + cIterI*cIterI);\n" +
                "                float absZ = sqrt(zr*zr + zi*zi);\n" +
                "                float mlo = abs(absZprev2 - absC); float mhi = absZprev2 + absC;\n" +
                "                if (mhi - mlo > 1e-12) { acc_tiaSum += (absZ - mlo) / (mhi - mlo); acc_tiaCount += 1; }\n" +
                "            }\n");
        if (On(OrbLyapunov))
            samp.Append("            { float a = sqrt(zr*zr + zi*zi); if (a > 1e-12) { acc_lyaSum += log(2.0*a); acc_lyaCount += 1; } }\n");
        if (On(OrbGaussian))
            samp.Append("            { float dgr = zr - round(zr); float dgi = zi - round(zi); acc_gauSum += sqrt(dgr*dgr + dgi*dgi); acc_gauCount += 1; }\n");
        if (On(OrbExp))
            samp.Append("            { float a = sqrt(zr*zr + zi*zi); acc_expSum += exp(-a); acc_expCount += 1; }\n");
        if (On(OrbCurvature))
            samp.Append(
                "            if (it == 1) { cvPrevZr = zr; cvPrevZi = zi; }\n" +
                "            else {\n" +
                "                float segR = zr - cvPrevZr; float segI = zi - cvPrevZi;\n" +
                "                if (it == 2) { cvPrevSegR = segR; cvPrevSegI = segI; }\n" +
                "                else {\n" +
                "                    float crs = cvPrevSegR*segI - cvPrevSegI*segR;\n" +
                "                    float dt  = cvPrevSegR*segR + cvPrevSegI*segI;\n" +
                "                    if (crs != 0.0 || dt != 0.0) { acc_cvSum += abs(atan2(crs, dt)); acc_cvCount += 1; }\n" +
                "                    cvPrevSegR = segR; cvPrevSegI = segI;\n" +
                "                }\n" +
                "                cvPrevZr = zr; cvPrevZi = zi;\n" +
                "            }\n");
        return samp.ToString();
    }

    /// <summary>The post-loop means as the 11 float locals in_trapMin … in_expSmooth
    /// (un-mask'd ones 0.0) — the values EvalPalette receives on the shallow kernel
    /// and the deep orbit kernel writes to gOrbit.</summary>
    private static string OrbitMeans(int mask)
    {
        bool On(int bit) => (mask & bit) != 0;

        // Post-loop means → in_* locals (all 11; unmask'd ones are 0.0).
        var means = new System.Text.StringBuilder();
        means.Append("        float in_trapMin = ").Append(On(OrbTrapMin) ? "(acc_trapMin < 3.0e38 ? acc_trapMin : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_trapCross = ").Append(On(OrbTrapCross) ? "(acc_trapCross < 3.0e38 ? acc_trapCross : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_trapRing = ").Append(On(OrbTrapRing) ? "(acc_trapRing < 3.0e38 ? acc_trapRing : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_trapHyperbola = ").Append(On(OrbTrapHyperbola) ? "(acc_trapHyperbola < 3.0e38 ? acc_trapHyperbola : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_trapHexagon = ").Append(On(OrbTrapHexagon) ? "(acc_trapHexagon < 3.0e38 ? acc_trapHexagon : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_stripeAvg = ").Append(On(OrbStripe) ? "(acc_stripeCount > 0 ? acc_stripeSum / acc_stripeCount : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_tiaAvg = ").Append(On(OrbTia) ? "(acc_tiaCount > 0 ? acc_tiaSum / acc_tiaCount : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_curvature = ").Append(On(OrbCurvature) ? "(acc_cvCount > 0 ? acc_cvSum / acc_cvCount : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_lyapunov = ").Append(On(OrbLyapunov) ? "(acc_lyaCount > 0 ? acc_lyaSum / acc_lyaCount : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_gaussian = ").Append(On(OrbGaussian) ? "(acc_gauCount > 0 ? acc_gauSum / acc_gauCount : 0.0)" : "0.0").Append(";\n");
        means.Append("        float in_expSmooth = ").Append(On(OrbExp) ? "(acc_expCount > 0 ? acc_expSum / acc_expCount : 0.0)" : "0.0").Append(";\n");
        return means.ToString();
    }

    private static string HlslEntryOrbitBody(string decl, string samp, string means)
    {
        const string orbitArgs = ",\n            in_trapMin, in_trapCross, in_trapRing, in_trapHyperbola, in_trapHexagon,\n" +
                                 "            in_stripeAvg, in_tiaAvg, in_curvature, in_lyapunov, in_gaussian, in_expSmooth";
        const string zeroArgs  = ", 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0";

        string inSetColor = @"
        gColor[idx] = cg_pack_bgra(EvalPalette(
            0.0, 0.0, (float)gMaxIter, (float)gMaxIter,
            0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0" + zeroArgs + @"), x, y);
        gTrap[idx] = 0.0;
";
        string bulbSkipColor = @"
        gColor[idx] = cg_pack_bgra(EvalPalette(
            0.0, 0.0, (float)gMaxIter, (float)gMaxIter,
            0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0" + zeroArgs + @"), x, y);
        gTrap[idx] = 0.0;
";
        string escapeColor =
            "        float t_iter = gMaxIter > 0 ? sm / (float)gMaxIter : 0.0;\n" +
            "        float in_arg = atan2(zi, zr);\n" +
            "        float in_mag = SafeMag(zr, zi);\n" +
            "        float de_dz = SafeMag(dr, di);\n" +
            "        float in_dist = de_dz > 1e-10 ? in_mag * log(in_mag) / de_dz : 0.0;\n" +
            means +
            "        gColor[idx] = cg_pack_bgra(EvalPalette(\n" +
            "            sm, in_dist, (float)it, (float)gMaxIter,\n" +
            "            t_iter, 0.0, 0.0, zr, zi, dr, di, in_arg, in_mag, 0.0, 0.0" + orbitArgs + "), x, y);\n" +
            "        gTrap[idx] = in_trapMin;\n";

        return $@"
[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{{
    uint x = tid.x;
    uint y = tid.y;
    if ((int)x >= gWidth || (int)y >= gHeight) return;

    int idx = (int)y * gWidth + (int)x;

    float fx = (float)x - 0.5 * gWidth;
    float fy = (float)y - 0.5 * gHeight;
    float cx = gCXHi + fx * gScaleHi + gCXLo + fx * gScaleLo;
    float cy = gCYHi + fy * gScaleHi + gCYLo + fy * gScaleLo;
    if (gWarpStrength != 0.0) ApplyDomainWarp(fx, fy, cx, cy);   // #1173-I

    int rowMaxIt = gMaxIter;
    if (gUsePerRow != 0)
    {{
        uint rc = gPerRow[y];
        if (rc > 0) rowMaxIt = (int)rc;
    }}

    if (gFractalKind == 0 && (InCardioid(cx, cy) || InPeriod2Bulb(cx, cy)))
    {{
        gIter[idx]    = (uint)gMaxIter;
        gSmooth[idx]  = 0.0;
        gFinalZD[idx] = float4(0.0, 0.0, 1.0, 0.0);
        {bulbSkipColor}
        return;
    }}

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
    float dr = gFractalKind == 5 ? 0.0 : 1.0;   // #1173-I: Phoenix's dz/dc starts at 0 (CPU)
    float di = 0.0;
    float pzr = 0.0, pzi = 0.0, pdr = 0.0, pdi = 0.0;   // Phoenix previous z, dz/dc
    int   it = 0;
{decl}    [loop]
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

        // F16 orbit sample — pre-update RAW z, it > 0 (matches CPU Sample).
        if (it > 0)
        {{
{samp}        }}

        if (gFractalKind == 4)
        {{
            MultibrotStep(zr, zi, dr, di, cIterR, cIterI);   // #1173-I
        }}
        else if (gFractalKind == 5)
        {{
            // #1173-I: Phoenix z' = z^2 + c + p * z_prev (p = gParam0 + i gParam1),
            // dz'/dc = 2 z dz/dc + 1 + p * dz_prev/dc (PhoenixKernel.StepWithPrevDeriv).
            float ppr = gParam0 * pzr - gParam1 * pzi;
            float ppi = gParam0 * pzi + gParam1 * pzr;
            float pdR = gParam0 * pdr - gParam1 * pdi;
            float pdI = gParam0 * pdi + gParam1 * pdr;
            float ndr = 2.0 * (zr * dr - zi * di) + 1.0 + pdR;
            float ndi = 2.0 * (zr * di + zi * dr) + pdI;
            float nzr = zr * zr - zi * zi + cIterR + ppr;
            float nzi = 2.0 * zr * zi + cIterI + ppi;
            pzr = zr; pzi = zi; pdr = dr; pdi = di;
            zr = nzr; zi = nzi; dr = ndr; di = ndi;
        }}
        else
        {{
            float newDr = 2.0 * (fzr * dr - fzi * di) + 1.0;
            float newDi = 2.0 * (fzr * di + fzi * dr);
            dr = newDr;
            di = newDi;

            float zrNew = zr2 - zi2 + cIterR;
            float zi_new_unscaled = fzr * fzi;
            zi = zi_new_unscaled + zi_new_unscaled + cIterI;
            zr = zrNew;
        }}
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
        float mag = SafeMag(zr, zi);
        float nu = log2(log2(max(mag, 1.001)));   // #1173-H: the CPU smooth is log2(log2|z|), not log2(ln|z|) (+0.529 off)
        float sm = (float)it + 1.0 - nu;
        gSmooth[idx] = sm;
{escapeColor}    }}
}}
";
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

    // ── #607 (G4.6) — deep-zoom perturbation with orbit accumulation ──────────
    //
    // The GPU twin of MandelbrotCalculator.ComputePixelOrbitPerturbation (#609):
    // BuildPerturb's rebased δ loop, plus the F16 orbit accumulators sampled on the
    // reconstructed full value z = Z[m] + δ (pre-update, iter > 0 — the CPU
    // convention). The sample maths is the shallow orbit kernel's, in float, on
    // (float)z: z itself is O(1) at any depth, only δ needs the double. No SA/BLA:
    // an orbit theme needs every iteration's z (the CPU deep orbit path skips
    // nothing either).
    //
    // Colour stays on the CPU: the kernel writes the mask'd means (bit order,
    // PerturbOrbitStride floats per pixel) to gOrbit (u3), and the calculator feeds
    // them to the theme's MapWithOrbit through the same FinalizeOrbitPixel the CPU
    // path uses. The cbuffer is BuildPerturb's 64 bytes with two pad ints reused
    // as the float view centre (TIA's c = centre + dc).
    //
    // One deliberate departure from the float sample code: the triangle-inequality
    // sample is rearranged to avoid cancellation (see OrbitSample's deepTia). Its
    // M - m and |z| - m differences lose everything in float once |z_{n-1}|² << |c|,
    // which happens near a minibrot, where the orbit passes close to 0 — the plain
    // float form gave a median TIA error of 2e-3 against the CPU at the S88 1e15 view.

    /// <summary>Compute-shader entry point for the orbit-accumulating perturbation
    /// variant (#607).</summary>
    public const string PerturbOrbitEntryPoint = "CSPerturbOrbit";

    /// <summary>All 11 orbit-input bits.</summary>
    public const int OrbAll = (1 << 11) - 1;

    /// <summary>Floats per pixel in the deep orbit kernel's gOrbit output: one per
    /// mask'd input, in bit order.</summary>
    public static int PerturbOrbitStride(int mask) => System.Numerics.BitOperations.PopCount((uint)(mask & OrbAll));

    private static readonly string[] OrbitMeanNames =
    {
        "in_trapMin", "in_trapCross", "in_trapRing", "in_trapHyperbola", "in_trapHexagon",
        "in_stripeAvg", "in_tiaAvg", "in_curvature", "in_lyapunov", "in_gaussian", "in_expSmooth",
    };

    /// <summary>Compose the orbit-accumulating double perturbation kernel for
    /// <paramref name="mask"/> (non-zero). Bindings: b0 params, t0/t1 reference
    /// orbit, u0..u2 iter/smooth/finalZD (as BuildPerturb), u3 gOrbit.</summary>
    public static string BuildPerturbOrbit(int mask)
    {
        mask &= OrbAll;
        if (mask == 0) throw new System.ArgumentException("the deep orbit kernel needs at least one orbit input", nameof(mask));
        int stride = PerturbOrbitStride(mask);
        bool tia = (mask & OrbTia) != 0;
        var writes = new System.Text.StringBuilder();
        var zeros = new System.Text.StringBuilder();
        for (int b = 0, k = 0; b < OrbitMeanNames.Length; b++)
        {
            if ((mask & (1 << b)) == 0) continue;
            writes.Append("        gOrbit[obase + ").Append(k).Append("] = ").Append(OrbitMeanNames[b]).Append(";\n");
            zeros.Append("        gOrbit[obase + ").Append(k).Append("] = 0.0;\n");
            k++;
        }

        return @"
// BuildPerturb's 64-byte cbuffer; gCRe/gCIm take the place of gPad0/gPad1.
cbuffer PerturbParams : register(b0)
{
    double gScale;
    double gEscapeR2;
    double gOffX0;
    double gOffY0;
    int    gWidth;
    int    gHeight;
    int    gMaxIter;
    int    gRefLen;
    int    gRowBase;
    float  gCRe;        // view centre (float): the TIA sample's c = centre + dc
    float  gCIm;
    int    gPad2;
}

StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);   // .xy = zr,zi  .zw = drv,div
RWStructuredBuffer<float>  gOrbit   : register(u3);   // " + stride + @" mask'd means per pixel

[numthreads(8, 8, 1)]
void CSPerturbOrbit(uint3 tid : SV_DispatchThreadID)
{
    int px = (int)tid.x;
    int py = gRowBase + (int)tid.y;
    if (px >= gWidth || py >= gHeight) return;
    int idx = py * gWidth + px;

    double dcR = (gOffX0 + (double)px) * gScale;
    double dcI = (gOffY0 + (double)py) * gScale;
    float cIterR = gCRe + (float)dcR;
    float cIterI = gCIm + (float)dcI;

    double dr = 0.0, di = 0.0;      // δ_0 = 0
    double drv = 1.0, div = 0.0;    // dz/dc of the full orbit
    int m = 0;                      // reference-orbit index
    double zrD = 0.0, ziD = 0.0;    // full value z = Z[m] + δ (last = escape z)
" + (tia ? "    double pzrD = 0.0, pziD = 0.0;  // z_{n-1}, for the triangle-inequality term\n" : "")
  + OrbitDecl(mask) + @"
    int iter;
    [loop]
    for (iter = 0; iter < gMaxIter; iter++)
    {
        double Zr = gRefZr[m];
        double Zi = gRefZi[m];
        zrD = Zr + dr;
        ziD = Zi + di;

        double zmag2 = zrD * zrD + ziD * ziD;
        if (zmag2 >= gEscapeR2) break;

        // Orbit sample on the reconstructed full z, before the δ / reference
        // advance, iter > 0 (ComputePixelOrbitPerturbation's convention).
        if (iter > 0)
        {
            float zr = (float)zrD;
            float zi = (float)ziD;
            int it = iter;
" + OrbitSample(mask, deepTia: true) + @"        }
" + (tia ? "        pzrD = zrD; pziD = ziD;\n" : "") + @"
        double ndrv = 2.0 * (zrD * drv - ziD * div) + 1.0;
        double ndiv = 2.0 * (zrD * div + ziD * drv);
        drv = ndrv; div = ndiv;

        double dmag2 = dr * dr + di * di;
        if (zmag2 < dmag2 || m + 1 >= gRefLen)
        {
            dr = zrD; di = ziD;
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

    gFinalZD[idx] = float4((float)zrD, (float)ziD, (float)drv, (float)div);
    int obase = idx * " + stride + @";
    if (iter >= gMaxIter)
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
" + zeros + @"    }
    else
    {
        gIter[idx] = (uint)iter;
        float magf = sqrt((float)(zrD * zrD + ziD * ziD));
        gSmooth[idx] = (float)iter + 1.0 - log2(log2(magf));
" + OrbitMeans(mask) + writes + @"    }
}
";
    }

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
    int    gSafeMax;    // SeriesApproximation.SafeMax — max valid coeff index (0 = no SA)
    int    gBlaLevels;  // #88 / G4.5b: BlaTable.Levels, 0 = no BLA (was gPad0)
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
// #88 / G4.5b: BlaTable.Data flattened, 5 doubles per entry (A re/im, B re/im, r^2;
// A and B already Hi+Lo collapsed, as the CPU applies them). Level k holds
// gRefLen >> k entries of L = 2^k steps, levels stored back to back.
StructuredBuffer<double> gBla : register(t10);

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

    // #88 / G4.5b: BLA level starts (level k is gRefLen >> k entries long).
    int blaStart[32];
    {
        int acc = 0;
        for (int lv = 0; lv < 32; lv++) { blaStart[lv] = acc; acc += lv < gBlaLevels ? (gRefLen >> lv) : 0; }
    }

    // ── Identical rebased δ loop as BuildPerturb, resumed from iterStart/m=k ──
    int iter;
    [loop]
    for (iter = iterStart; iter < gMaxIter; iter++)
    {
        // #88 / G4.5b: BLA skip, BlaTable.Lookup on the reference index m: the
        // longest aligned step (L >= 2) whose validity radius holds |delta|, then
        // delta' = A delta + B dc and dz' = A dz (the CPU drops B there too).
        // m + L stays below gRefLen so the next reference read is in range.
        if (gBlaLevels > 1)
        {
            double dm2 = dr * dr + di * di;
            int found = -1, foundL = 0;
            [loop]
            for (int lk = gBlaLevels - 1; lk >= 1; lk--)
            {
                int l = 1 << lk;
                if ((m & (l - 1)) != 0) continue;
                int bi = m >> lk;
                if (bi >= (gRefLen >> lk) || m + l >= gRefLen || iter + l > gMaxIter) continue;
                int e = (blaStart[lk] + bi) * 5;
                double r2 = gBla[e + 4];
                if (r2 > 0.0 && dm2 < r2) { found = e; foundL = l; break; }
            }
            if (found >= 0)
            {
                double baR = gBla[found], baI = gBla[found + 1];
                double bbR = gBla[found + 2], bbI = gBla[found + 3];
                double ndr = baR * dr - baI * di + (bbR * dcR - bbI * dcI);
                double ndi = baR * di + baI * dr + (bbR * dcI + bbI * dcR);
                double nvr = baR * drv - baI * div;
                double nvi = baR * div + baI * drv;
                dr = ndr; di = ndi; drv = nvr; div = nvi;
                m += foundL;
                iter += foundL - 1;   // the loop's ++ makes it +L
                continue;
            }
        }

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
    if (gWarpStrength != 0.0) ApplyDomainWarp(fx, fy, cx, cy);   // #1173-I

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
    float dr = gFractalKind == 5 ? 0.0 : 1.0;   // #1173-I: Phoenix's dz/dc starts at 0 (CPU)
    float di = 0.0;
    float pzr = 0.0, pzi = 0.0, pdr = 0.0, pdi = 0.0;   // Phoenix previous z, dz/dc
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

        if (gFractalKind == 4)
        {{
            MultibrotStep(zr, zi, dr, di, cIterR, cIterI);   // #1173-I
        }}
        else if (gFractalKind == 5)
        {{
            // #1173-I: Phoenix z' = z^2 + c + p * z_prev (p = gParam0 + i gParam1),
            // dz'/dc = 2 z dz/dc + 1 + p * dz_prev/dc (PhoenixKernel.StepWithPrevDeriv).
            float ppr = gParam0 * pzr - gParam1 * pzi;
            float ppi = gParam0 * pzi + gParam1 * pzr;
            float pdR = gParam0 * pdr - gParam1 * pdi;
            float pdI = gParam0 * pdi + gParam1 * pdr;
            float ndr = 2.0 * (zr * dr - zi * di) + 1.0 + pdR;
            float ndi = 2.0 * (zr * di + zi * dr) + pdI;
            float nzr = zr * zr - zi * zi + cIterR + ppr;
            float nzi = 2.0 * zr * zi + cIterI + ppi;
            pzr = zr; pzi = zi; pdr = dr; pdi = di;
            zr = nzr; zi = nzi; dr = ndr; di = ndi;
        }}
        else
        {{
            float newDr = 2.0 * (fzr * dr - fzi * di) + 1.0;
            float newDi = 2.0 * (fzr * di + fzi * dr);
            dr = newDr;
            di = newDi;

            float zrNew = zr2 - zi2 + cIterR;
            float zi_new_unscaled = fzr * fzi;
            zi = zi_new_unscaled + zi_new_unscaled + cIterI;
            zr = zrNew;
        }}
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
        float mag = SafeMag(zr, zi);
        float nu = log2(log2(max(mag, 1.001)));   // #1173-H: the CPU smooth is log2(log2|z|), not log2(ln|z|) (+0.529 off)
        float sm = (float)it + 1.0 - nu;
        gSmooth[idx] = sm;
        {escapeColor}
    }}
}}
";
    }
}
