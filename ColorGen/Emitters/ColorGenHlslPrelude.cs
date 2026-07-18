// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ColorGenHlslPrelude.cs — T3.1 phase 2
//
// Builds the HLSL helper prelude (mod / hash / hsv / hsl / palette-N) that
// ColorGenHlslEmitter's emitted EvalPalette body depends on. The prelude is
// stitched into the compute-shader source ahead of the emitted body.
//
// The palette arity set is supplied by the emitter — only the helpers the
// program actually uses get emitted, keeping the shader small.

using System.Collections.Generic;
using System.Text;

namespace FracturingFog.ColorGen.Emitters;

public static class ColorGenHlslPrelude
{
    /// <summary>Build the prelude block. <paramref name="paletteArities"/>
    /// is the set of N values seen in palette(t, c0..cN-1) calls. Other
    /// helpers (mod, hash, hsv, hsl) are always emitted — they're cheap and
    /// not worth tracking individually.</summary>
    public static string Build(IReadOnlyCollection<int> paletteArities)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// ── ColorGen HLSL helpers (auto-emitted) ─────────────────────────────");
        // GLSL-style mod: x - y * floor(x/y). HLSL fmod is truncating.
        sb.AppendLine(@"
float cg_mods(float x, float y)
{
    if (y == 0.0) return 0.0;
    return x - y * floor(x / y);
}
float3 cg_modv(float3 x, float3 y)
{
    return float3(cg_mods(x.x, y.x), cg_mods(x.y, y.y), cg_mods(x.z, y.z));
}

float cg_hash(float x)
{
    return frac(sin(x * 12.9898) * 43758.5453);
}
float cg_hash2(float x, float y)
{
    return frac(sin(x * 12.9898 + y * 78.233) * 43758.5453);
}

float3 cg_fromHsv(float h, float s, float v)
{
    h = h - floor(h);
    s = saturate(s);
    v = saturate(v);
    float hh = h * 6.0;
    int i = (int)floor(hh);
    float f = hh - (float)i;
    float p = v * (1.0 - s);
    float q = v * (1.0 - f * s);
    float t = v * (1.0 - (1.0 - f) * s);
    int seg = i - 6 * (i / 6);
    if (seg == 0) return float3(v, t, p);
    if (seg == 1) return float3(q, v, p);
    if (seg == 2) return float3(p, v, t);
    if (seg == 3) return float3(p, q, v);
    if (seg == 4) return float3(t, p, v);
    return float3(v, p, q);
}

float3 cg_fromHsl(float h, float s, float l)
{
    h = h - floor(h);
    s = saturate(s);
    l = saturate(l);
    float c = (1.0 - abs(2.0 * l - 1.0)) * s;
    float hh = h * 6.0;
    float x = c * (1.0 - abs(cg_mods(hh, 2.0) - 1.0));
    float m = l - c * 0.5;
    int seg = (int)floor(hh);
    seg = seg - 6 * (seg / 6);
    float3 rgb;
    if      (seg == 0) rgb = float3(c, x, 0.0);
    else if (seg == 1) rgb = float3(x, c, 0.0);
    else if (seg == 2) rgb = float3(0.0, c, x);
    else if (seg == 3) rgb = float3(0.0, x, c);
    else if (seg == 4) rgb = float3(x, 0.0, c);
    else               rgb = float3(c, 0.0, x);
    return rgb + m.xxx;
}

// OkLab (Björn Ottosson) — perceptually uniform colour (F9). Matches the
// CPU Cg3.FromOkLab / MixOkLab helpers for CPU/GPU parity.
float cg_srgbToLinear(float c)
{
    c = saturate(c);
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}
float cg_linearToSrgb(float c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}
float3 cg_fromOkLab(float L, float a, float b)
{
    float l_ = L + 0.3963377774 * a + 0.2158037573 * b;
    float m_ = L - 0.1055613458 * a - 0.0638541728 * b;
    float s_ = L - 0.0894841775 * a - 1.2914855480 * b;
    float l = l_ * l_ * l_;
    float m = m_ * m_ * m_;
    float s = s_ * s_ * s_;
    float r =  4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
    float g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
    float bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
    return float3(cg_linearToSrgb(r), cg_linearToSrgb(g), cg_linearToSrgb(bb));
}
float3 cg_fromOkLch(float L, float c, float h)
{
    return cg_fromOkLab(L, c * cos(h), c * sin(h));
}
float3 cg_toOkLab(float3 col)
{
    float r = cg_srgbToLinear(col.r);
    float g = cg_srgbToLinear(col.g);
    float b = cg_srgbToLinear(col.b);
    float l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
    float m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
    float s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
    float l_ = sign(l) * pow(abs(l), 1.0 / 3.0);
    float m_ = sign(m) * pow(abs(m), 1.0 / 3.0);
    float s_ = sign(s) * pow(abs(s), 1.0 / 3.0);
    return float3(
        0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
        1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
        0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
}
float3 cg_mixOkLab(float3 a, float3 b, float t)
{
    float3 la = cg_toOkLab(a);
    float3 lb = cg_toOkLab(b);
    float3 lab = lerp(la, lb, t);
    return cg_fromOkLab(lab.x, lab.y, lab.z);
}");

        // Per-arity palette helpers. Each emitted lerp picks the segment
        // via a chained if; HLSL's if-flatten will keep this branchless at
        // small N.
        foreach (int n in paletteArities)
        {
            if (n < 1) continue;
            sb.Append("float3 cg_palette").Append(n).Append("(float t");
            for (int i = 0; i < n; i++) sb.Append(", float3 c").Append(i);
            sb.AppendLine(")");
            sb.AppendLine("{");
            if (n == 1)
            {
                sb.AppendLine("    return c0;");
            }
            else
            {
                sb.AppendLine("    float fr = t - floor(t);");
                sb.Append("    float pos = fr * ").Append(n).AppendLine(".0;");
                sb.Append("    int k = (int)floor(pos); k = k - ").Append(n).Append(" * (k / ").Append(n).AppendLine(");");
                sb.Append("    int k2 = k + 1; if (k2 >= ").Append(n).AppendLine(") k2 = 0;");
                sb.AppendLine("    float f = pos - floor(pos);");
                // Inline if-chain selecting (c_k, c_k2).
                sb.AppendLine("    float3 a, b;");
                for (int i = 0; i < n; i++)
                {
                    sb.Append(i == 0 ? "    if (k == " : "    else if (k == ");
                    sb.Append(i).Append(") a = c").Append(i).AppendLine(";");
                }
                sb.AppendLine("    else a = c0;");
                for (int i = 0; i < n; i++)
                {
                    sb.Append(i == 0 ? "    if (k2 == " : "    else if (k2 == ");
                    sb.Append(i).Append(") b = c").Append(i).AppendLine(";");
                }
                sb.AppendLine("    else b = c0;");
                sb.AppendLine("    return lerp(a, b, f);");
            }
            sb.AppendLine("}");
        }

        sb.AppendLine("// ── end ColorGen helpers ─────────────────────────────────────────────");
        return sb.ToString();
    }
}
