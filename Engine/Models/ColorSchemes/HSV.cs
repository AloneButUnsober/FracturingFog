using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Text;

namespace FracturingFog.Models
{
    public class HsvPalette : IColorMap, IVectorColorMap, IGpuHlslPalette
    {
        public static string Name => "Hsv";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            float hue = (smooth * 0.02f) % 1.0f;
            hue -= MathF.Floor(hue);

            float saturation = 1.0f;
            float baseValue = smooth < iterations ? 1.0f : 0.0f;
            float lightness = 1.0f - MathF.Min(distance * 0.08f, 1.0f);
            float value = baseValue * lightness;

            return Fractals.HsvToRgb(hue, saturation, value);
        }

        // ── SIMD batched mapping (4 pixels per call) ─────────────────────────
        //
        // Mirrors Map() with saturation = 1 (always), so the s==0 branch of
        // the general HsvToRgb is unreachable and the per-sector formula
        // simplifies. The 6-sector switch is vectorised by computing each
        // sector's (r, g, b) for all four lanes then blending under a lane
        // mask of "is this lane in this sector?". Total cost: ~50 SIMD ops
        // per 4-pixel block vs 4 × ~30 scalar ops in the equivalent Map()
        // calls — net win driven primarily by SIMD throughput on the floor /
        // multiply / compare operations.

        public Vector128<int> MapV(
            Vector128<float> smooth, Vector128<float> distance, int iterations,
            Vector128<float> nx, Vector128<float> ny,
            Vector128<float> finalZr, Vector128<float> finalZi,
            Vector128<float> dzdcR, Vector128<float> dzdcI)
        {
            var one = Vector128.Create(1.0f);
            var zero = Vector128<float>.Zero;
            var c0_02 = Vector128.Create(0.02f);
            var c0_08 = Vector128.Create(0.08f);
            var c255  = Vector128.Create(255.0f);
            var maxIterV = Vector128.Create((float)iterations);

            // hue = frac(smooth * 0.02)
            var hueRaw = smooth * c0_02;
            var hue = hueRaw - Vector128.Floor(hueRaw);

            // baseValue = (smooth < iterations) ? 1 : 0
            var inSetMask = Vector128.LessThan(smooth, maxIterV);
            var baseValue = Vector128.ConditionalSelect(inSetMask, one, zero);

            // lightness = 1 - min(distance * 0.08, 1)
            var distScaled = distance * c0_08;
            var distClamped = Vector128.Min(distScaled, one);
            var lightness = one - distClamped;

            // value = baseValue * lightness
            var v = baseValue * lightness;

            // h6 = hue * 6, i = floor(h6) % 6, f = h6 - floor(h6)
            var h6 = hue * Vector128.Create(6.0f);
            var floorH6 = Vector128.Floor(h6);
            var f = h6 - floorH6;
            var iVec = Vector128.ConvertToInt32(floorH6);   // hue ∈ [0,1] → i ∈ [0,5]

            // With saturation = 1: p = 0, q = v*(1-f), t = v*f
            var q = v * (one - f);
            var t = v * f;

            // Per-sector blend. Each iteration: select (r,g,b) for sector N
            // wherever iVec lane == N, leaving earlier accumulations elsewhere.
            var r = zero; var g = zero; var b = zero;
            // 0: (v, t, 0)
            BlendSector(0, iVec, v, t, zero, ref r, ref g, ref b);
            // 1: (q, v, 0)
            BlendSector(1, iVec, q, v, zero, ref r, ref g, ref b);
            // 2: (0, v, t)
            BlendSector(2, iVec, zero, v, t, ref r, ref g, ref b);
            // 3: (0, q, v)
            BlendSector(3, iVec, zero, q, v, ref r, ref g, ref b);
            // 4: (t, 0, v)
            BlendSector(4, iVec, t, zero, v, ref r, ref g, ref b);
            // 5: (v, 0, q)
            BlendSector(5, iVec, v, zero, q, ref r, ref g, ref b);

            // Pack to BGRA int32: 0xFF000000 | (R<<16) | (G<<8) | B
            // (matches Fractals.HsvToRgb's (a<<24) | (r<<16) | (g<<8) | b)
            var r8 = Vector128.ConvertToInt32(r * c255);
            var g8 = Vector128.ConvertToInt32(g * c255);
            var b8 = Vector128.ConvertToInt32(b * c255);
            var alpha = Vector128.Create(unchecked((int)0xFF000000));

            return alpha
                 | Vector128.ShiftLeft(r8, 16)
                 | Vector128.ShiftLeft(g8, 8)
                 | b8;
        }

        private static void BlendSector(
            int sector, Vector128<int> iVec,
            Vector128<float> sr, Vector128<float> sg, Vector128<float> sb,
            ref Vector128<float> r, ref Vector128<float> g, ref Vector128<float> b)
        {
            var maskI = Vector128.Equals(iVec, Vector128.Create(sector));
            var mask = maskI.AsSingle();
            r = Vector128.ConditionalSelect(mask, sr, r);
            g = Vector128.ConditionalSelect(mask, sg, g);
            b = Vector128.ConditionalSelect(mask, sb, b);
        }

        // ── GPU HLSL palette (Wave 3.6) ──────────────────────────────────────
        //
        // Mirrors Map() bit-for-shader: hue = frac(smooth*0.02), saturation =
        // 1 (hard-coded so cg_fromHsv collapses to the per-sector cases below),
        // baseValue = 0 for in-set / 1 for escaped, lightness = 1 - min(dist*
        // 0.08, 1), value = baseValue * lightness. With saturation = 1 the
        // sector colours simplify to (v,t,p)/(q,v,p)/(p,v,t)/(p,q,v)/(t,p,v)/
        // (v,p,q) where p = v*(1-s) = 0, q = v*(1-f), t = v*f.
        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    float hue = in_smooth * 0.02;
    hue = hue - floor(hue);
    float lightness = 1.0 - min(in_dist * 0.08, 1.0);
    float v = (in_isInSet > 0.5) ? 0.0 : lightness;
    float hh = hue * 6.0;
    int isec = (int)floor(hh);
    float f = hh - floor(hh);
    float q = v * (1.0 - f);
    float t = v * f;
    int seg = isec - 6 * (isec / 6);
    float3 rgb;
    if      (seg == 0) rgb = float3(v, t, 0.0);
    else if (seg == 1) rgb = float3(q, v, 0.0);
    else if (seg == 2) rgb = float3(0.0, v, t);
    else if (seg == 3) rgb = float3(0.0, q, v);
    else if (seg == 4) rgb = float3(t, 0.0, v);
    else               rgb = float3(v, 0.0, q);
    return rgb;
";

        public string PaletteId => "HsvPalette/v1";
    }
}
