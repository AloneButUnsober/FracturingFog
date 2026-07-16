// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// HlslPaletteHelpers.cs — Wave 3.6 follow-on
//
// Shared HLSL helper strings used by hand-written IGpuHlslPalette themes.
// Mirrors Fractals.HsvToRgb's exact algebra (no input clamping; saturate
// happens in cg_pack_bgra at the kernel layer). One theme's prelude is
// emitted per shader compile (the kernel rebuilds when PaletteId changes),
// so duplicate function names across themes are fine.

namespace FracturingFog.Models
{
    internal static class HlslPaletteHelpers
    {
        /// <summary>HSV→RGB matching <see cref="Fractals.HsvToRgb"/> bit-for-shader
        /// (no input clamping). Returns a <c>float3</c> in 0..1 (channels may
        /// exceed 1.0 when <c>v</c> &gt; 1; cg_pack_bgra saturates at pack time).
        /// Includes <c>cg_mods</c> helper for GLSL-style mod operations.</summary>
        public const string HsvAndMods = @"
float cg_mods(float x, float y)
{
    if (y == 0.0) return 0.0;
    return x - y * floor(x / y);
}
float3 cg_hsv_to_rgb(float h, float s, float v)
{
    if (s == 0.0) return float3(v, v, v);
    float hh = h * 6.0;
    int i = (int)floor(hh);
    float f = hh - (float)i;
    float p = v * (1.0 - s);
    float q = v * (1.0 - s * f);
    float t = v * (1.0 - s * (1.0 - f));
    int seg = i - 6 * (i / 6);
    if      (seg == 0) return float3(v, t, p);
    else if (seg == 1) return float3(q, v, p);
    else if (seg == 2) return float3(p, v, t);
    else if (seg == 3) return float3(p, q, v);
    else if (seg == 4) return float3(t, p, v);
    else               return float3(v, p, q);
}
";

        /// <summary>Just <c>cg_mods</c> on its own for themes that don't need HSV.</summary>
        public const string ModsOnly = @"
float cg_mods(float x, float y)
{
    if (y == 0.0) return 0.0;
    return x - y * floor(x / y);
}
";
    }
}
