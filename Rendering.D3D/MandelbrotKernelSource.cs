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
    // #1340: 1 = wavefront schedule (HlslEntry): the threads pull pixels from
    // gWork[0]; 0 = one thread per pixel. Only HlslEntry reads it.
    int   gWave;
    // 19 fields x 4 bytes = 76, padded by the host to 80 (float4 multiple).
}

// #1227: the fractal kind the loop is specialised for. The hosts compile one
// variant per kind (ForKind prepends #define FF_KIND n), so the kinds a frame
// does not use fold away. One loop holding every kind's step (Multibrot's inner
// loop, Phoenix's carried z and dz/dc) needs registers for all of them, and the
// lost occupancy halved plain Mandelbrot on a GT 710 (SpZoom1e4 1080p 148 -> 309
// ms). Without FF_KIND the kind is read from the cbuffer, as before.
#ifdef FF_KIND
#define KIND FF_KIND
#else
#define KIND gFractalKind
#endif

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

    /// <summary>#1227 — <paramref name="hlsl"/> (any variant built on
    /// <see cref="HlslBase"/>) specialised for one fractal kind: the loop keeps only
    /// that kind's step. The kind must match the cbuffer's gFractalKind at dispatch.</summary>
    public static string ForKind(string hlsl, int kind) => "#define FF_KIND " + kind + "\n" + hlsl;

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

    /// <summary>CSMain for the orbit colour kernel — HlslEntry (#1384: both
    /// schedules) with accumulator declarations before the loop, reset at each new
    /// pixel, a per-iteration Sample block (it &gt; 0, after the escape break, on the
    /// RAW z — Mandelbrot orbit), and the accumulated means passed to EvalPalette on
    /// the escape branch.</summary>
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

    /// <summary>#1384 — the accumulator declarations of <paramref name="decl"/>
    /// (<see cref="OrbitDecl"/>) as assignments: the wavefront schedule starts every
    /// pixel a lane takes from the same values. "float a = 1.0, b = 2.0;" becomes
    /// "a = 1.0; b = 2.0;".</summary>
    private static string OrbitResetOf(string decl)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string line in decl.Split('\n'))
            foreach (string stmt in line.Split(';'))
            {
                string t = stmt.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("float ", System.StringComparison.Ordinal)) t = t[6..];
                else if (t.StartsWith("int ", System.StringComparison.Ordinal)) t = t[4..];
                else if (t.StartsWith("double ", System.StringComparison.Ordinal)) t = t[7..];
                foreach (string part in t.Split(','))
                    sb.Append("            ").Append(part.Trim()).Append(";\n");
            }
        return sb.ToString();
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

        // #1384 — HlslEntry with the orbit hooks: the same wavefront / one-thread-per-
        // pixel loop, the accumulators re-initialised at each pixel (orbitReset).
        return HlslEntry(true, inSetColor, escapeColor, bulbSkipColor, decl, OrbitResetOf(decl), samp);
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

    /// <summary>Thread-group height the perturbation kernels are written with
    /// ([numthreads(8, 8, 1)]); #1339 may tune another (GpuGroupShape.Y).</summary>
    public const int PerturbGroupRows = 8;

    /// <summary>Rows per perturbation dispatch band for the given frame, derived
    /// from <see cref="PerturbDispatchIterBudget"/> and the frame's width +
    /// maxIter (or the override). Always ≤ height, and a whole number of
    /// <paramref name="groupRows"/> (the dispatch's thread-group height; at least
    /// one group) unless it is the whole height.
    /// #1334: a band dispatches ⌈rows / 8⌉ groups from its gRowBase, so a band of
    /// rows that is not a multiple of 8 also ran the first rows of the next band
    /// (the same pixels, written twice). Deep frames whose budget gave fewer than 8
    /// rows (1920 wide from maxIter ≈ 2600) ran every row up to 8 times. A
    /// one-group band runs exactly what the old under-8 bands already dispatched,
    /// so no dispatch got longer.</summary>
    public static int PerturbBandRows(int width, int height, int maxIter, int groupRows = PerturbGroupRows)
    {
        groupRows = System.Math.Max(1, groupRows);
        int rows;
        if (PerturbBandRowsOverride > 0) rows = PerturbBandRowsOverride;
        else
        {
            long denom = (long)System.Math.Max(1, width) * System.Math.Max(1, maxIter);
            rows = (int)System.Math.Min(int.MaxValue, System.Math.Max(1, PerturbDispatchIterBudget / denom));
        }
        rows = System.Math.Max(groupRows, rows / groupRows * groupRows);
        return System.Math.Min(rows, System.Math.Max(1, height));
    }

    /// <summary>#1394 — the bands of a perturbation dispatch, as (first row, rows).
    /// Without a mask: <see cref="PerturbBandRows"/> rows each (the last band shorter),
    /// exactly as before. With one (<paramref name="maskWords"/>, the GpuPixelMask
    /// layout: a header word, then one bit per pixel), a band's budget counts only its
    /// marked pixels, so a sparse solid-region pass runs in a few tall bands instead
    /// of many short ones: a band takes as long as its slowest pixel, and at 50 000
    /// iterations on a GT 710 the 68 one-group bands of a 960x540 frame made 3.5 % of
    /// its pixels cost a fifth of the frame. The budget is the larger of
    /// <see cref="PerturbDispatchIterBudget"/> and the unmasked band's (one group of a
    /// wide deep frame is already over it), so no masked band iterates more than a
    /// band of the same frame without a mask. Bands stay whole groups of
    /// <paramref name="groupRows"/> rows (but the last), at least one group each.</summary>
    public static (int RowBase, int Rows)[] PerturbBands(int width, int height, int maxIter,
        int groupRows = PerturbGroupRows, uint[]? maskWords = null)
    {
        var bands = new System.Collections.Generic.List<(int, int)>();
        if (maskWords == null || PerturbBandRowsOverride > 0)
        {
            int rows = PerturbBandRows(width, height, maxIter, groupRows);
            for (int y = 0; y < height; y += rows) bands.Add((y, System.Math.Min(rows, height - y)));
            return bands.ToArray();
        }
        groupRows = System.Math.Max(1, groupRows);
        long perPixel = System.Math.Max(1, maxIter);
        long budget = System.Math.Max(PerturbDispatchIterBudget,
            (long)PerturbBandRows(width, height, maxIter, groupRows) * width * perPixel);
        for (int y = 0; y < height;)
        {
            int rows = 0;
            long cost = 0;
            while (y + rows < height)
            {
                int g = System.Math.Min(groupRows, height - y - rows);
                long c = MarkedPixels(maskWords, (long)(y + rows) * width, (long)(y + rows + g) * width) * perPixel;
                if (rows > 0 && cost + c > budget) break;
                cost += c;
                rows += g;
            }
            bands.Add((y, rows));
            y += rows;
        }
        return bands.ToArray();
    }

    // The marked pixels in [from, to) of a GpuPixelMask word array (bits after the header).
    private static long MarkedPixels(uint[] words, long from, long to)
    {
        long count = 0;
        for (long i = from; i < to;)
        {
            long w = 1 + (i >> 5);
            int bit = (int)(i & 31);
            int len = (int)System.Math.Min(32 - bit, to - i);
            if (w >= words.Length) break;
            uint bits = words[w] >> bit;
            if (len < 32) bits &= (1u << len) - 1;
            count += System.Numerics.BitOperations.PopCount(bits);
            i += len;
        }
        return count;
    }

    /// <summary>Perf-fallback budget (ms). After the first row band completes,
    /// each backend extrapolates band0·bandCount; if it exceeds this, the GPU is
    /// too slow at this depth (weak FP64) and the dispatch aborts so the caller
    /// falls back to the CPU deep path. Tunable via FF_GPU_PERTURB_BUDGET_MS;
    /// default 3000 ms. 0 or negative disables the check (always finish on GPU).</summary>
    public static double PerturbBudgetMs = ParsePerturbBudget(System.Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS"));

    /// <summary>Default <see cref="PerturbBudgetMs"/> when FF_GPU_PERTURB_BUDGET_MS is unset or unparseable.</summary>
    public const double DefaultPerturbBudgetMs = 3000.0;

    /// <summary>Parses an FF_GPU_PERTURB_BUDGET_MS value (#1490): a positive number is the
    /// budget in ms, 0 or negative is 0 (check disabled), and unset, blank, NaN or garbage is
    /// <see cref="DefaultPerturbBudgetMs"/>. Before #1490, 0 and negatives fell back to the
    /// default, so the documented "0 disables" only worked by setting the field in code.</summary>
    public static double ParsePerturbBudget(string? value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double ms) || double.IsNaN(ms))
            return DefaultPerturbBudgetMs;
        return ms > 0 ? ms : 0.0;
    }

    /// <summary>True when the extrapolated full-frame GPU time (first band ×
    /// band count) exceeds <see cref="PerturbBudgetMs"/> — i.e. abort to CPU.</summary>
    public static bool PerturbTooSlow(double band0Ms, int bandCount)
        => PerturbTooSlow(band0Ms, bandCount, PerturbBudgetMs);

    /// <summary><see cref="PerturbTooSlow(double, int)"/> against an explicit budget; a
    /// budget of 0 or less never aborts.</summary>
    public static bool PerturbTooSlow(double band0Ms, int bandCount, double budgetMs)
        => budgetMs > 0 && band0Ms * bandCount > budgetMs;

    /// <summary>Marker message for the perf-abort exception so the calculator can
    /// tell "GPU too slow" apart from a genuine device-lost error.</summary>
    public const string PerturbTooSlowMarker = "GPU-PERTURB-TOO-SLOW";

    /// <summary>Compose the double perturbation kernel. Standalone HLSL (its own
    /// cbuffer + reference-orbit SRVs + output UAVs); requires FP64 support on
    /// the device (Vulkan <c>shaderFloat64</c> / D3D double shader ops).</summary>
    public static string BuildPerturb() => PerturbChunkDefine + @"
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
    int    gWave;       // #1340: 1 = wavefront schedule (was gPad0)
    int    gBandRows;   // #1340: rows in this band (was gPad1)
    int    gBand;       // #1340: band index, its gWork counter (was gPad2)
}

// Reference orbit Z_n, Hi-limb doubles (the CPU _refZr/_refZi). Length gRefLen.
StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);   // .xy = zr,zi  .zw = drv,div
// #1340: per-band pixel counters of the wavefront schedule, zeroed by the host.
RWStructuredBuffer<uint>   gWork    : register(u3);
" + WaveFetch + @"
// #1340 — two schedules, one compiled shader (gWave): one thread per pixel of the
// band, or a fixed number of threads pulling the band's pixels from gWork[gBand]
// and iterating them PERTURB_CHUNK steps at a time (HlslEntry). A pixel runs the
// same instructions either way, so every output is bit-identical.
[numthreads(8, 8, 1)]
void CSPerturb(uint3 tid : SV_DispatchThreadID)
{
    // TDR tiling — a deep-zoom full-image dispatch can run tens of seconds on a
    // weak-FP64 GPU and trip the OS watchdog (DXGI_ERROR_DEVICE_REMOVED), which
    // also kills the shared present device. The host splits the frame into row
    // bands and offsets each dispatch by gRowBase; the actual pixel row is
    // gRowBase + the band-local row.
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    double dcR = 0.0, dcI = 0.0;
    double dr = 0.0, di = 0.0, drv = 0.0, div = 0.0, zr = 0.0, zi = 0.0;
    int m = 0, iter = 0;
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

            // dc = pixelOffset · scale (double).
            dcR = (gOffX0 + (double)px) * gScale;
            dcI = (gOffY0 + (double)py) * gScale;

            dr = 0.0; di = 0.0;         // δ_0 = 0
            drv = 1.0; div = 0.0;       // dz/dc (IQ convention) for distance + normals
            m = 0;                      // reference-orbit index
            zr = 0.0; zi = 0.0;         // full value z = Z[m] + δ (last = escape z)
            iter = 0;
        }

        bool escaped = false;
        [loop]
        for (int trip = 0; trip < PERTURB_CHUNK && iter < gMaxIter; trip++, iter++)
        {
            double Zr = gRefZr[m];
            double Zi = gRefZi[m];
            zr = Zr + dr;
            zi = Zi + di;

            double zmag2 = zr * zr + zi * zi;
            if (zmag2 >= gEscapeR2) { escaped = true; break; }

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
        if (!escaped && iter < gMaxIter) continue;

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
        idx = -1;
    }
}
";

    // ── #1331 — deep-zoom perturbation in fp32 + exponent ("floatexp") ────────
    //
    // BuildPerturb's rebased δ loop without a single double, for GPUs whose fp64
    // runs at 1/24..1/64 of fp32 (or not at all). δ and the derivative dz/dc are
    // a complex float mantissa with ONE shared int exponent (value = m · 2^e,
    // max(|m.x|, |m.y|) in [1, 2)), renormalised after each step with exponent
    // bit arithmetic; the reference orbit Z (O(1) at any depth) is plain float2.
    //   δ' = (2Z + δ_f)·δ + dc,   δ_f = δ as a float (0 once it underflows): the δ
    //   part of 2Z + δ only matters when |δ| is comparable to |Z|, where it is in
    //   float range anyway; dc enters with its own exponent and is aligned.
    //   dz' = 2·z·dz + 1, z = Z + δ_f in float (the escape test and the rebase
    //   test |z| < |δ| run on that float z, as the double kernel's do).
    // The cbuffer is floats and ints only, so the kernel needs no fp64 support.
    // Outputs and banding are BuildPerturb's; the derivative goes out as a float,
    // which saturates past ~1e38 exactly as the double kernel's (float) cast does.

    /// <summary>Compute-shader entry point for the float-exp perturbation variant (#1331).</summary>
    public const string PerturbFxEntryPoint = "CSPerturbFX";

    /// <summary>#1331 — the scaled-fp32 entry of the same source, for pixel scales
    /// down to 2^<see cref="PerturbF32MinScaleExponent"/>.</summary>
    public const string PerturbF32EntryPoint = "CSPerturbF32";

    /// <summary>#1331 — the smallest pixel-scale exponent the scaled-fp32 entry
    /// takes (zoom ~1e30); deeper frames run float-exp.</summary>
    public const int PerturbF32MinScaleExponent = -100;

    /// <summary>#1331 — compose the float-exp perturbation kernel (see above).</summary>
    public static string BuildPerturbFloatExp() => PerturbChunkDefine + @"
// 64 bytes, no doubles. Matches the C# PerturbFxParams blob byte-for-byte.
cbuffer PerturbFxParams : register(b0)
{
    float gScaleM;      // pixel scale = gScaleM · 2^gScaleE, gScaleM in [1, 2)
    int   gScaleE;
    float gOffX0;       // column offset of pixel x=0 (pixels; add x)
    float gOffY0;       // row offset of pixel y=0 (pixels; add y)
    int   gWidth;
    int   gHeight;
    int   gMaxIter;
    int   gRefLen;
    int   gRowBase;     // TDR tiling: this dispatch covers rows [gRowBase, gRowBase+groupsY*8)
    float gEscapeR2;
    int   gWave;        // #1384: 1 = wavefront schedule (was gPad0)
    int   gBandRows;    // #1384: rows in this band (was gPad1)
    int   gBand;        // #1384: band index, its gWork counter
    int   gPadF0;
    int   gPadF1;
    int   gPadF2;
}

StructuredBuffer<float2> gRefZ : register(t0);   // reference orbit Z_n as float

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);   // .xy = zr,zi  .zw = drv,div
// #1384: per-band pixel counters of the wavefront schedule, zeroed by the host.
RWStructuredBuffer<uint>   gWork    : register(u3);
" + WaveFetch + @"

static const int FxZeroExp = -1000000;

// 2^k as a float, exact, for -126 <= k <= 127 (built from the exponent bits:
// no exp2 / ldexp, which run on the slow special-function units).
float Pow2(int k) { return asfloat((uint)(k + 127) << 23); }

// m · 2^e with max(|m.x|,|m.y|) in [1, 2), or (0, FxZeroExp) for zero.
void FxNorm(inout float2 m, inout int e)
{
    float a = max(abs(m.x), abs(m.y));
    if (a == 0.0) { m = float2(0.0, 0.0); e = FxZeroExp; return; }
    int k = (int)((asuint(a) >> 23) & 0xFF) - 127;   // floor(log2 a) (normal floats)
    k = max(k, -126);
    m *= Pow2(-k);                                    // exact · 2^-k
    e += k;
}

// m · 2^e as a float (0 when it underflows; |m| < 2 so e <= 127 stays finite).
float2 FxToFloat(float2 m, int e)
{
    if (e < -126) return float2(0.0, 0.0);
    if (e > 127) return m * 3.4e38;
    return m * Pow2(e);
}

// (am · 2^ae) + (bm · 2^be), normalised. Past a 40-bit gap the smaller term
// can't reach a float mantissa.
void FxAdd(float2 am, int ae, float2 bm, int be, out float2 rm, out int re)
{
    int d = ae - be;
    if (d >= 0) { rm = d > 40 ? am : am + bm * Pow2(-d); re = ae; }
    else        { rm = d < -40 ? bm : bm + am * Pow2(d); re = be; }
    FxNorm(rm, re);
}

float2 CMul(float2 a, float2 b) { return float2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x); }

void FxWrite(int idx, int iter, float2 z, float2 dzf);

// #1384 — both entries run CSPerturb's two schedules (#1340), one compiled shader
// each: one thread per pixel of the band, or persistent threads pulling the band's
// pixels from gWork[gBand], PERTURB_CHUNK steps at a time; bit-identical.
[numthreads(8, 8, 1)]
void CSPerturbFX(uint3 tid : SV_DispatchThreadID)
{
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    float2 dcM = float2(0.0, 0.0); int dcE = FxZeroExp;
    float2 dM = float2(0.0, 0.0); int dE = FxZeroExp;
    float2 vM = float2(1.0, 0.0); int vE = 0;
    int m = 0, iter = 0;
    float2 z = float2(0.0, 0.0);
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

            // dc = pixelOffset · scale, as float-exp.
            dcM = float2(gOffX0 + (float)px, gOffY0 + (float)py) * gScaleM;
            dcE = gScaleE;
            FxNorm(dcM, dcE);

            dM = float2(0.0, 0.0); dE = FxZeroExp;   // δ_0 = 0
            vM = float2(1.0, 0.0); vE = 0;           // dz/dc = 1
            m = 0;
            z = float2(0.0, 0.0);
            iter = 0;
        }

    bool escaped = false;
    [loop]
    for (int trip = 0; trip < PERTURB_CHUNK && iter < gMaxIter; trip++, iter++)
    {
        float2 Z = gRefZ[m];
        float2 df = FxToFloat(dM, dE);
        z = Z + df;

        float zmag2 = dot(z, z);
        if (zmag2 >= gEscapeR2) { escaped = true; break; }

        // dz' = 2·z·dz + 1 (the full orbit, before any rebase).
        float2 nvM = CMul(2.0 * z, vM); int nvE = vE;
        FxNorm(nvM, nvE);
        FxAdd(nvM, nvE, float2(1.0, 0.0), 0, vM, vE);

        // Rebase when the reference no longer anchors this pixel or is exhausted.
        if (zmag2 < dot(df, df) || m + 1 >= gRefLen)
        {
            dM = z; dE = 0; FxNorm(dM, dE);
            df = z;
            Z = float2(0.0, 0.0);
            m = 0;
        }

        // δ' = (2Z + δ_f)·δ + dc.
        float2 tM = CMul(2.0 * Z + df, dM); int tE = dE;
        FxNorm(tM, tE);
        FxAdd(tM, tE, dcM, dcE, dM, dE);
        m++;
    }
    if (!escaped && iter < gMaxIter) continue;

    float2 dzf = FxToFloat(vM, vE);
    FxWrite(idx, iter, z, dzf);
    idx = -1;
    }
}

void FxWrite(int idx, int iter, float2 z, float2 dzf)
{
    gFinalZD[idx] = float4(z.x, z.y, dzf.x, dzf.y);
    if (iter >= gMaxIter)
    {
        gIter[idx]   = (uint)gMaxIter;
        gSmooth[idx] = 0.0;
    }
    else
    {
        gIter[idx] = (uint)iter;
        float magf = sqrt(dot(z, z));
        gSmooth[idx] = (float)iter + 1.0 - log2(log2(magf));
    }
}

// The scaled-fp32 variant, for frames whose pixel scale 2^gScaleE is in float
// range (the host takes it while gScaleE >= -100, zoom ~1e30): δ is held as
// δ' = δ / S with S = 2^gScaleE, the derivative as v' = v · S, both plain float,
// so a step costs what the double kernel's does at the fp32 rate:
//   δ'' = (2Z + δ)·δ' + dc'  with δ = δ'·S,   v'' = 2·z·v' + S.
// |δ'| stays below ~2/S and |δ| above ~S, both inside float range at that depth.
[numthreads(8, 8, 1)]
void CSPerturbF32(uint3 tid : SV_DispatchThreadID)
{
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    float S = Pow2(gScaleE), invS = Pow2(-gScaleE);
    float2 dc = float2(0.0, 0.0);
    float2 d = float2(0.0, 0.0);
    float2 v = float2(S, 0.0);
    int m = 0, iter = 0;
    float2 z = float2(0.0, 0.0);
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

            dc = float2(gOffX0 + (float)px, gOffY0 + (float)py) * gScaleM;   // dc / S
            d = float2(0.0, 0.0);      // δ' = δ / S
            v = float2(S, 0.0);        // v' = (dz/dc) · S, dz/dc = 1
            m = 0;
            z = float2(0.0, 0.0);
            iter = 0;
        }

    bool escaped = false;
    [loop]
    for (int trip = 0; trip < PERTURB_CHUNK && iter < gMaxIter; trip++, iter++)
    {
        float2 Z = gRefZ[m];
        float2 df = d * S;            // δ
        z = Z + df;
        float zmag2 = dot(z, z);
        if (zmag2 >= gEscapeR2) { escaped = true; break; }

        v = CMul(2.0 * z, v) + float2(S, 0.0);

        if (zmag2 < dot(df, df) || m + 1 >= gRefLen)
        {
            d = z * invS; df = z;
            Z = float2(0.0, 0.0);
            m = 0;
        }

        d = CMul(2.0 * Z + df, d) + dc;
        m++;
    }
    if (!escaped && iter < gMaxIter) continue;

    FxWrite(idx, iter, z, v * invS);
    idx = -1;
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

    /// <summary>#1340 — loop trips a wavefront lane runs on its perturbation pixel
    /// between refills (a trip is one δ step, or one BLA skip).</summary>
    public const int PerturbChunk = 32;

    private static readonly string PerturbChunkDefine = $"#define PERTURB_CHUNK {PerturbChunk}\n";

    /// <summary>Compose the orbit-accumulating double perturbation kernel for
    /// <paramref name="mask"/> (non-zero). Bindings: b0 params, t0/t1 reference
    /// orbit, u0..u2 iter/smooth/finalZD (as BuildPerturb), u3 gOrbit, u4 gWork
    /// (#1384: the wavefront schedule's per-band counters).</summary>
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

        return PerturbChunkDefine + @"
// BuildPerturb's cbuffer with gCRe/gCIm in place of its gWave/gBandRows, and
// #1384's gWave / gBandRows / gBand after them: 80 bytes. Matches the C#
// PerturbOrbitParams / PerturbOrbitParamsBlob (both backends) byte-for-byte.
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
    int    gWave;       // #1384: 1 = wavefront schedule (was gPad2)
    int    gBandRows;   // #1384: rows in this band
    int    gBand;       // #1384: band index, its gWork counter
    int    gPadO0;
    int    gPadO1;
}

StructuredBuffer<double> gRefZr : register(t0);
StructuredBuffer<double> gRefZi : register(t1);

RWStructuredBuffer<uint>   gIter    : register(u0);
RWStructuredBuffer<float>  gSmooth  : register(u1);
RWStructuredBuffer<float4> gFinalZD : register(u2);   // .xy = zr,zi  .zw = drv,div
RWStructuredBuffer<float>  gOrbit   : register(u3);   // " + stride + @" mask'd means per pixel
// #1384: per-band pixel counters of the wavefront schedule, zeroed by the host.
RWStructuredBuffer<uint>   gWork    : register(u4);
" + WaveFetch + @"
// #1384 — CSPerturb's two schedules (#1340), one compiled shader: one thread per
// pixel of the band, or persistent threads pulling the band's pixels from
// gWork[gBand], PERTURB_CHUNK steps at a time. A new pixel starts from the same
// state (the accumulators included), so every output is bit-identical.
[numthreads(8, 8, 1)]
void CSPerturbOrbit(uint3 tid : SV_DispatchThreadID)
{
    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    double dcR = 0.0, dcI = 0.0;
    float cIterR = 0.0, cIterI = 0.0;
    double dr = 0.0, di = 0.0;      // δ_0 = 0
    double drv = 1.0, div = 0.0;    // dz/dc of the full orbit
    int m = 0;                      // reference-orbit index
    double zrD = 0.0, ziD = 0.0;    // full value z = Z[m] + δ (last = escape z)
" + (tia ? "    double pzrD = 0.0, pziD = 0.0;  // z_{n-1}, for the triangle-inequality term\n" : "")
  + OrbitDecl(mask) + @"
    int iter = 0;
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
            cIterR = gCRe + (float)dcR;
            cIterI = gCIm + (float)dcI;
            dr = 0.0; di = 0.0;
            drv = 1.0; div = 0.0;
            m = 0;
            zrD = 0.0; ziD = 0.0;
" + (tia ? "            pzrD = 0.0; pziD = 0.0;\n" : "")
  + OrbitResetOf(OrbitDecl(mask)) + @"            iter = 0;
        }

        bool escaped = false;
        [loop]
        for (int trip = 0; trip < PERTURB_CHUNK && iter < gMaxIter; trip++, iter++)
        {
        double Zr = gRefZr[m];
        double Zi = gRefZi[m];
        zrD = Zr + dr;
        ziD = Zi + di;

        double zmag2 = zrD * zrD + ziD * ziD;
        if (zmag2 >= gEscapeR2) { escaped = true; break; }

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
        if (!escaped && iter < gMaxIter) continue;

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
        idx = -1;
    }
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
    public static string BuildPerturbSA() => PerturbChunkDefine + @"
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
    int    gWave;       // #1340: 1 = wavefront schedule (was gPad1)
    int    gBandRows;   // #1340: rows in this band (was gPad2)
    int    gBand;       // #1340: band index, its gWork counter (was gPad3)
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
// #1340: per-band pixel counters of the wavefront schedule, zeroed by the host.
RWStructuredBuffer<uint>   gWork    : register(u3);
" + WaveFetch + @"
// #1340 — two schedules, one compiled shader (gWave): one thread per pixel of the
// band, or a fixed number of threads pulling the band's pixels from gWork[gBand]
// and iterating them PERTURB_CHUNK loop trips at a time (HlslEntry). A pixel runs
// the same instructions either way, so every output is bit-identical.
[numthreads(8, 8, 1)]
void CSPerturbSA(uint3 tid : SV_DispatchThreadID)
{
    // #88 / G4.5b: BLA level starts (level k is gRefLen >> k entries long).
    int blaStart[32];
    {
        int acc = 0;
        for (int lv = 0; lv < 32; lv++) { blaStart[lv] = acc; acc += lv < gBlaLevels ? (gRefLen >> lv) : 0; }
    }

    uint rows = (uint)min(gBandRows, gHeight - gRowBase);
    bool taken = false;
    int idx = -1;
    double dcR = 0.0, dcI = 0.0;
    double dr = 0.0, di = 0.0, drv = 0.0, div = 0.0, zr = 0.0, zi = 0.0;
    int m = 0, iter = 0;
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

            dr = 0.0; di = 0.0;             // δ_0 = 0
            drv = 1.0; div = 0.0;           // dz/dc (IQ convention)
            m = 0;                          // reference-orbit index
            int iterStart = 0;              // SA skip target
            zr = 0.0; zi = 0.0;

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

            iter = iterStart;
        }

        // ── Identical rebased δ loop as BuildPerturb, resumed from iterStart/m=k ──
        // #1340: PERTURB_CHUNK trips at a time; a trip is one step or one BLA skip.
        bool escaped = false;
        [loop]
        for (int trip = 0; trip < PERTURB_CHUNK && iter < gMaxIter; trip++, iter++)
        {
            // #88 / G4.5b: BLA skip, BlaTable.Lookup on the reference index m: the
            // longest aligned step (L >= 2) whose validity radius holds |delta|, then
            // delta' = A delta + B dc and dz' = A dz (the CPU drops B there too).
            // m + L stays below gRefLen so the next reference read is in range.
            if (gBlaLevels > 1)
            {
                double dm2 = dr * dr + di * di;
                int found = -1, foundL = 0;
                // A level-k entry starts only at multiples of 2^k, so start at the
                // highest level m is aligned to (its trailing zeros), as the CPU
                // Lookup does since #1346, instead of probing and skipping every
                // level above it on each iteration.
                int top = m == 0 ? gBlaLevels - 1 : min(gBlaLevels - 1, (int)firstbitlow((uint)m));
                [loop]
                for (int lk = top; lk >= 1; lk--)
                {
                    int l = 1 << lk;
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
            if (zmag2 >= gEscapeR2) { escaped = true; break; }

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
        if (!escaped && iter < gMaxIter) continue;

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
        idx = -1;
    }
}
";

    /// <summary>#1342 — the cache / compile key of an SP variant: the fractal kind
    /// in the low byte, the GpuPeriodicity mode (Engine) above it.</summary>
    public static int VariantKey(int kind, int periodicity) => kind | (periodicity << 8);

    /// <summary>#1342 — <paramref name="hlsl"/> (a variant built on
    /// <see cref="HlslBase"/>) specialised for <see cref="VariantKey"/>
    /// <paramref name="key"/>: <see cref="ForKind"/>, plus <c>FF_PERIODICITY</c>
    /// (1 = the periodicity early-out, 2 = it plus the diagnostic smooth) for a
    /// non-zero mode. Without it the loop is the plain one, unchanged.</summary>
    public static string ForVariant(string hlsl, int key)
    {
        int mode = key >> 8;
        string s = ForKind(hlsl, key & 0xFF);
        return mode != 0 ? "#define FF_PERIODICITY " + mode + "\n" + s : s;
    }

    /// <summary>#1342 — periodicity state, declared after the iteration state. The
    /// snapshot starts at the initial state, so every snapshot is a state the orbit
    /// really had (z_0 = 0 for Mandelbrot, the pixel for Julia; Phoenix's previous
    /// z starts at 0). <c>nextSnap</c> is the step count of the next snapshot.</summary>
    private const string PeriodicityDecl = @"    float snapR = zr, snapI = zi, snapPR = pzr, snapPI = pzi;
    int nextSnap = 0;
";

    /// <summary>#1342 — steps per periodicity block. Checked every step, the compare
    /// and the snapshot slowed an all-exterior frame by ~47% on a GT 710. Once per
    /// block the cost falls with the block (16 / 32 / 64 steps: seahorse 1e4 at
    /// 2048 iterations +11 / +6 / +3%), while a cycle is found a block or two
    /// later (period-3 bulb at cap 10k: 12.7 / 14.7 / 19.7 ms, 56 ms plain).</summary>
    private const int PeriodicityBlock = 32;

    /// <summary>#1342 — the periodicity early-out, between blocks (z is the state
    /// after <c>it</c> steps). The test is bit-exact on the whole state the next
    /// step reads — z, plus the previous z for Phoenix (dz/dc never feeds back into
    /// z). Equal to an earlier state, the orbit repeats from there and never
    /// escapes, and every state of the cycle already passed the bailout test, so the
    /// pixel is exactly what running to the cap gives: in set. Bits, not ==, so a
    /// flushed denormal or a ±0 never counts as a repeat. Escaping pixels never
    /// repeat, so their iter, smooth and colour are untouched; only the final z /
    /// dz/dc of an in-set pixel (which no reader uses) changes.
    /// <para>The schedule is Brent's, as on the CPU (MandelbrotCalculator
    /// PeriodicitySnapshotStart), on block boundaries: snapshots after 32, 64, 128,
    /// … steps, each held as long as the steps before it, so a cycle of period p is
    /// found once that span reaches lcm(p, 32) ≤ 32p.</para></summary>
    private const string PeriodicityStep = @"        if (asuint(zr) == asuint(snapR) && asuint(zi) == asuint(snapI)
            && (KIND != 5 || (asuint(pzr) == asuint(snapPR) && asuint(pzi) == asuint(snapPI))))
        {
            it += rowMaxIt;   // > rowMaxIt: in set, detected after it - rowMaxIt steps
            break;
        }
        if (it >= nextSnap)
        {
            snapR = zr; snapI = zi; snapPR = pzr; snapPI = pzi;
            nextSnap = it + it;
        }
";

    /// <summary>#1342 — after the loop: a detected pixel left <c>it</c> above the
    /// cap (<see cref="PeriodicityStep"/>); keep its step count for the diagnostic
    /// mode and put <c>it</c> back at the cap.</summary>
    private const string PeriodicityEnd = @"    int periodicAt = it > rowMaxIt ? it - rowMaxIt : 0;
    if (it > rowMaxIt) it = rowMaxIt;
";

    /// <summary>#1340 — one SP iteration step: the escape test (breaks the
    /// enclosing loop) and the kind's update of z (and dz/dc, Phoenix's previous z).</summary>
    private const string SpStep = @"
            float fzr = zr;
            float fzi = zi;
            if (KIND == 2)      { fzr = abs(zr); fzi = abs(zi); }
            else if (KIND == 3) { fzi = -zi; }

            float zr2 = fzr * fzr;
            float zi2 = fzi * fzi;
            float mag2 = zr2 + zi2;
            if (mag2 >= gBailout2) break;

            if (KIND == 4)
            {
                MultibrotStep(zr, zi, dr, di, cIterR, cIterI);   // #1173-I
            }
            else if (KIND == 5)
            {
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
            }
            else
            {
                float newDr = 2.0 * (fzr * dr - fzi * di) + 1.0;
                float newDi = 2.0 * (fzr * di + fzi * dr);
                dr = newDr;
                di = newDi;

                float zrNew = zr2 - zi2 + cIterR;
                float zi_new_unscaled = fzr * fzi;
                zi = zi_new_unscaled + zi_new_unscaled + cIterI;
                zr = zrNew;
            }
";

    /// <summary>#1340 — steps a lane runs on its pixel between refills. The
    /// periodicity variant checks between the same blocks (<see cref="PeriodicityBlock"/>),
    /// so a pixel's arithmetic and checks are the pre-#1340 loop's.</summary>
    public const int WavefrontChunk = PeriodicityBlock;

    /// <summary>#1340 — the wavefront entry's pixel counter (u5; Vulkan binding
    /// UShift + 5). gWork[0] is zeroed by the host before a wavefront dispatch.</summary>
    public const string WorkUav = @"
RWStructuredBuffer<uint> gWork : register(u5);
" + WaveFetch;

    /// <summary>#1340 — the pixel fetch of both schedules (HlslEntry, CSPerturb,
    /// CSPerturbSA; declared after their gWork counter buffer).
    /// <para>wave = 0: this thread's own pixel (tid, band-local), once.</para>
    /// <para>wave = 1: the next slot of gWork[slot]. <c>tiled</c> (a literal at each
    /// call) picks the order: row-major, or 8×8 tiles (tiles row-major, pixels
    /// row-major in a tile) so a warp's lanes take neighbouring pixels as an 8×8 group
    /// does. Measured on a GT 710: the deep kernels need the tiles (row-major strips
    /// cost CSPerturbSA up to 15%: the lanes' iteration counts and reference-orbit
    /// reads drift apart), while the shallow kernel, which is cheap per pixel and
    /// writes four outputs, needs row-major (coalesced writes; tiles cost a filled
    /// Julia frame 10% and Phoenix a third of its gain on Vulkan).</para>
    /// Returns 0 = no pixel left (the thread exits), 1 = pixel (lx, ly) of the
    /// w × rows region, 2 = a slot past its right / bottom edge (fetch again).</summary>
    /// <para>#1394 — also declares the pixel mask (GpuPixelMask, t15; Vulkan binding
    /// TShift + 15): every entry that fetches a pixel skips it when MaskSkips says so,
    /// before writing anything, so a masked dispatch leaves the unmarked pixels'
    /// outputs alone. The D3D11 kernels leave t15 unbound without a mask (reads 0:
    /// every pixel); Vulkan binds a zero header.</para>
    public const string WaveFetch = @"
StructuredBuffer<uint> gMask : register(t15);

// #1394 — gMask[0] != 0: a mask is active; pixel idx is bit (idx & 31) of word
// 1 + (idx >> 5). Read once per pixel, before its loop: no per-step cost.
bool MaskSkips(int idx)
{
    if (gMask[0] == 0) return false;
    uint i = (uint)idx;
    return (gMask[1 + (i >> 5)] & (1u << (i & 31u))) == 0;
}

int WaveFetch(int wave, inout bool taken, uint2 tid, uint w, uint rows, uint slot, bool tiled, out uint lx, out uint ly)
{
    lx = 0; ly = 0;
    if (wave == 0)
    {
        if (taken || tid.x >= w || tid.y >= rows) return 0;
        taken = true;
        lx = tid.x; ly = tid.y;
        return 1;
    }
    uint q;
    InterlockedAdd(gWork[slot], 1u, q);
    if (!tiled)
    {
        if (q >= w * rows) return 0;
        ly = q / w;
        lx = q - ly * w;
        return 1;
    }
    uint tilesX = (w + 7) / 8;
    if (q >= tilesX * ((rows + 7) / 8) * 64) return 0;
    uint t = q >> 6, l = q & 63, ty = t / tilesX;
    lx = (t - ty * tilesX) * 8 + (l & 7);
    ly = ty * 8 + (l >> 3);
    return lx < w && ly < rows ? 1 : 2;
}
";

    /// <summary>Per-emit CSMain. The three colour splice points are empty in
    /// the base variant and filled by <c>MandelbrotGpuKernel</c> (D3D, V2 on
    /// Vulkan) for the colour-emitting variant.
    /// <para>#1340 — two schedules, one compiled shader (gWave in the cbuffer):</para>
    /// <list type="bullet">
    /// <item>gWave = 0: one thread per pixel (SV_DispatchThreadID, a 2D grid over
    /// the frame), as before.</item>
    /// <item>gWave = 1 ("wavefront", persistent threads): a fixed number of threads,
    /// far fewer than pixels, pull pixels from a global counter (gWork[0]) and run
    /// them <see cref="WavefrontChunk"/> steps at a time. A lane whose pixel escaped
    /// or reached the cap writes it and takes the next pixel at the next chunk, so a
    /// warp no longer idles while its slowest pixel runs on: its lanes stay busy as
    /// long as pixels are left.</item>
    /// </list>
    /// Each pixel runs the same instructions in either schedule (<see cref="SpStep"/>,
    /// the same blocks and periodicity checks, in the same binary), only in another
    /// order and on another lane, so every output is bit-identical. Two separately
    /// compiled entries were not: FXC folded the dz/dc update into a dp4 in one and
    /// into mul + mad in the other (an ulp apart), and <c>precise</c> to forbid that
    /// cost 15–25% on a GT 710.</summary>
    /// <para>#1384 — the orbit hooks (HlslEntryOrbit; empty for the base and colour
    /// entries, which then compile from the same text as before): <paramref name="orbitDecl"/>
    /// declares the accumulators, <paramref name="orbitReset"/> re-initialises them
    /// for each new pixel, <paramref name="orbitSample"/> runs each step after the
    /// escape test (it &gt; 0).</para>
    public static string HlslEntry(bool emitColor, string inSetColor = "", string escapeColor = "", string bulbSkipColor = "",
        string orbitDecl = "", string orbitReset = "", string orbitSample = "")
    {
        // (SpStep's line ends are the source file's, so the anchor carries none.)
        const string escapeTest = "if (mag2 >= gBailout2) break;";
        if (orbitSample.Length > 0 && !SpStep.Contains(escapeTest, System.StringComparison.Ordinal))
            throw new System.InvalidOperationException("SpStep has no escape test to sample after");
        string step = orbitSample.Length == 0 ? SpStep : SpStep.Replace(escapeTest,
            escapeTest + "\n\n            // F16 orbit sample — pre-update RAW z, it > 0 (matches CPU Sample).\n" +
            "            if (it > 0)\n            {\n" + orbitSample + "            }", System.StringComparison.Ordinal);
        return WorkUav + $@"
[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{{
    bool taken = false;   // gWave = 0: this thread's one pixel has been taken
    int idx = -1;         // the pixel this lane is iterating, -1 = none
    uint x = 0, y = 0;
    int rowMaxIt = 0;
    float zr = 0.0, zi = 0.0, dr = 0.0, di = 0.0;
    float cIterR = 0.0, cIterI = 0.0;
    float pzr = 0.0, pzi = 0.0, pdr = 0.0, pdi = 0.0;   // Phoenix previous z, dz/dc
    int   it = 0;
{orbitDecl}#ifdef FF_PERIODICITY
    float snapR = 0.0, snapI = 0.0, snapPR = 0.0, snapPI = 0.0;
    int nextSnap = 0;
#endif
    [loop] [allow_uav_condition]
    for (;;)
    {{
        if (idx < 0)
        {{
            int got = WaveFetch(gWave, taken, tid.xy, (uint)gWidth, (uint)gHeight, 0, false, x, y);
            if (got == 0) break;
            if (got == 2) continue;
            idx = (int)y * gWidth + (int)x;
            if (MaskSkips(idx)) {{ idx = -1; continue; }}   // #1394

            // Reconstruct cx / cy using the split centre.
            float fx = (float)x - 0.5 * gWidth;
            float fy = (float)y - 0.5 * gHeight;
            float cx = gCXHi + fx * gScaleHi + gCXLo + fx * gScaleLo;
            float cy = gCYHi + fy * gScaleHi + gCYLo + fy * gScaleLo;
            if (gWarpStrength != 0.0) ApplyDomainWarp(fx, fy, cx, cy);   // #1173-I

            // Per-row cap lookup. Falls back to gMaxIter when disabled or when
            // the buffer holds 0 for this row (defensive).
            rowMaxIt = gMaxIter;
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
            if (KIND == 0 && (InCardioid(cx, cy) || InPeriod2Bulb(cx, cy)))
            {{
                gIter[idx]    = (uint)gMaxIter;
                gSmooth[idx]  = 0.0;
                gFinalZD[idx] = float4(0.0, 0.0, 1.0, 0.0);
                {bulbSkipColor}
                idx = -1;
                continue;
            }}

            // Per-fractal init. Mandelbrot/BurningShip/Tricorn: z_0 = 0, c =
            // pixel coord. Julia: z_0 = pixel coord, c = (gParam0, gParam1) const.
            if (KIND == 1)
            {{
                zr = cx;     zi = cy;
                cIterR = gParam0; cIterI = gParam1;
            }}
            else
            {{
                zr = 0.0;    zi = 0.0;
                cIterR = cx; cIterI = cy;
            }}
            dr = KIND == 5 ? 0.0 : 1.0;   // #1173-I: Phoenix's dz/dc starts at 0 (CPU)
            di = 0.0;
            pzr = 0.0; pzi = 0.0; pdr = 0.0; pdi = 0.0;
            it = 0;
{orbitReset}#ifdef FF_PERIODICITY
            // #1342: the snapshot starts at the initial state (PeriodicityDecl).
            snapR = zr; snapI = zi; snapPR = pzr; snapPI = pzi;
            nextSnap = 0;
#endif
        }}

        int blockEnd = min(it + {WavefrontChunk}, rowMaxIt);
        [loop]
        for (; it < blockEnd; it++)
        {{
{step}
        }}
        bool done = it < blockEnd || it >= rowMaxIt;   // escaped, or at the cap
#ifdef FF_PERIODICITY
        // #1342: the periodicity check between blocks (PeriodicityStep).
        if (!done)
        {{
            if (asuint(zr) == asuint(snapR) && asuint(zi) == asuint(snapI)
                && (KIND != 5 || (asuint(pzr) == asuint(snapPR) && asuint(pzi) == asuint(snapPI))))
            {{
                it += rowMaxIt;   // > rowMaxIt: in set, detected after it - rowMaxIt steps
                done = true;
            }}
            else if (it >= nextSnap)
            {{
                snapR = zr; snapI = zi; snapPR = pzr; snapPI = pzi;
                nextSnap = it + it;
            }}
        }}
#endif
        if (!done) continue;
#ifdef FF_PERIODICITY
        int periodicAt = it > rowMaxIt ? it - rowMaxIt : 0;   // PeriodicityEnd
        if (it > rowMaxIt) it = rowMaxIt;
#endif

        if (it >= rowMaxIt)
        {{
            // #1394: in set — the CPU writeback's zeros, not the orbit's last z (which
            // differs from pixel to pixel, so no two in-set pixels of a GPU-coloured
            // frame agreed and the solid-region fill never applied).
            gFinalZD[idx] = float4(0.0, 0.0, 0.0, 0.0);
            gIter[idx]   = (uint)gMaxIter;
#if defined(FF_PERIODICITY) && FF_PERIODICITY == 2
            gSmooth[idx] = periodicAt > 0 ? -(float)periodicAt : 0.0;   // #1342 diagnostic
#else
            gSmooth[idx] = 0.0;
#endif
            {inSetColor}
        }}
        else
        {{
            gFinalZD[idx] = float4(zr, zi, dr, di);
            gIter[idx] = (uint)it;
            // #1340: precise (once per pixel, no loop cost) — FXC otherwise compiled
            // |z| differently in the periodicity on / off variants (separate
            // binaries), an ulp apart in the smooth of the colour kernel.
            precise float mag = SafeMag(zr, zi);
            precise float nu = log2(log2(max(mag, 1.001)));   // #1173-H: the CPU smooth is log2(log2|z|), not log2(ln|z|) (+0.529 off)
            precise float sm = (float)it + 1.0 - nu;
            gSmooth[idx] = sm;
            {escapeColor}
        }}
        idx = -1;
    }}
}}
";
    }
}
