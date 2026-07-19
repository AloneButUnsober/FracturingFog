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
