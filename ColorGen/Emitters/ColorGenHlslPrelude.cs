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
