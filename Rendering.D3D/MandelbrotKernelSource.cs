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
