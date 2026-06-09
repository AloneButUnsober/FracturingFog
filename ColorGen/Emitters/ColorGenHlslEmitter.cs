// ColorGenHlslEmitter.cs — T3.1 phase 2
//
// HLSL twin of ColorGenEmitter. Walks the same CgProgram AST and emits the
// body of a `float3 EvalPalette(...)` function whose signature exposes every
// DSL input as a `float` arg:
//
//     float3 EvalPalette(
//         float in_smooth, float in_dist, float in_iter, float in_maxIter,
//         float in_t, float in_nx, float in_ny, float in_zr, float in_zi,
//         float in_dzr, float in_dzi, float in_arg, float in_mag,
//         float in_isInSet, float in_pxScale)
//     { … emitted body … }
//
// Value forms across expression boundaries:
//   • Scalar nodes → float
//   • Vec3   nodes → float3
//
// Differences from the C# emit:
//   • `double` → `float`; `Cg3` → `float3`.
//   • Vec3 broadcast falls out of HLSL scalar↔vector operator overloads —
//     no Add/AddVS/AddSV split. Vec3*Scalar, Scalar/Vec3, etc. all "just
//     work" with the native operators.
//   • `mix` → `lerp`; `fract` → `frac`; `mod` → custom helper (HLSL `fmod`
//     is truncating, GLSL/CPU `mod` is x - y*floor(x/y)); `palette` → per-
//     arity helper emitted into the prelude on demand.
//   • Channel access `.r/.g/.b` → HLSL `.r/.g/.b` swizzle (same syntax).
//   • Ternary stays `?:`.
//
// The emitter tracks the set of palette() arities used in the program so the
// caller can emit only the helpers it actually needs.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FracturingFog.ColorGen.Parser;

namespace FracturingFog.ColorGen.Emitters;

public sealed class ColorGenHlslEmitter
{
    private readonly string _indent;
    private readonly HashSet<int> _paletteArities = new();

    public ColorGenHlslEmitter(string indent = "    ") { _indent = indent; }

    /// <summary>Palette arities (stop count) seen during the last EmitBody.
    /// Caller uses these to emit per-N helpers into the HLSL prelude.</summary>
    public IReadOnlyCollection<int> PaletteArities => _paletteArities;

    /// <summary>Emit the body of `float3 EvalPalette(...)` — let-bindings +
    /// final `return <vec3>;`. The wrapping function signature is the
    /// caller's responsibility.</summary>
    public string EmitBody(CgProgram prog)
    {
        _paletteArities.Clear();
        var sb = new StringBuilder();
        foreach (var s in prog.Statements)
        {
            switch (s)
            {
                case CgLet let:
                {
                    string typeKw = let.Value.Type == CgType.Scalar ? "float" : "float3";
                    sb.Append(_indent).Append(typeKw).Append(" v_").Append(let.Name).Append(" = ");
                    sb.Append(Emit(let.Value)).AppendLine(";");
                    break;
                }
                case CgReturn ret:
                {
                    sb.Append(_indent).Append("return ").Append(Emit(ret.Value)).AppendLine(";");
                    break;
                }
            }
        }
        return sb.ToString();
    }

    private string Emit(CgNode n) => n switch
    {
        CgNumber num    => FormatNumber(num.Value),
        CgVar v         => EmitVar(v),
        CgChannel ch    => $"({Emit(ch.Target)}).{ch.Channel}",
        CgUnary u       => EmitUnary(u),
        CgBinary b      => EmitBinary(b),
        CgTernary tern  => $"(({Emit(tern.Cond)}) != 0.0 ? {Emit(tern.IfTrue)} : {Emit(tern.IfFalse)})",
        CgCall c        => EmitCall(c),
        _               => throw new System.InvalidOperationException($"HLSL emitter: unhandled node {n.GetType().Name}"),
    };

    private static string EmitVar(CgVar v) => v.IsBuiltIn ? $"in_{v.Name}" : $"v_{v.Name}";

    private string EmitUnary(CgUnary u)
    {
        string inner = Emit(u.Operand);
        return u.Op switch
        {
            CgUnaryOp.Neg => $"(-{inner})",  // HLSL: scalar+vector negation both via unary -
            CgUnaryOp.Pos => $"(+{inner})",
            CgUnaryOp.Not => $"(({inner}) == 0.0 ? 1.0 : 0.0)",
            _ => throw new System.InvalidOperationException(),
        };
    }

    private string EmitBinary(CgBinary b)
    {
        string l = Emit(b.Lhs), r = Emit(b.Rhs);
        return b.Op switch
        {
            // HLSL scalar↔vector promotion in operators handles all
            // broadcast cases natively — no need for VS/SV variants.
            CgBinOp.Add => $"({l} + {r})",
            CgBinOp.Sub => $"({l} - {r})",
            CgBinOp.Mul => $"({l} * {r})",
            CgBinOp.Div => $"({l} / {r})",
            // GLSL/CPU mod = x - y*floor(x/y); HLSL fmod is truncating. Use
            // helper that matches CPU semantics (so palette output matches
            // CPU pixel-for-pixel as closely as float precision allows).
            CgBinOp.Mod => b.Type == CgType.Vec3 ? $"cg_modv({l}, {r})" : $"cg_mods({l}, {r})",
            CgBinOp.Pow => $"pow({l}, {r})",
            CgBinOp.Lt  => $"({l} <  {r} ? 1.0 : 0.0)",
            CgBinOp.Le  => $"({l} <= {r} ? 1.0 : 0.0)",
            CgBinOp.Gt  => $"({l} >  {r} ? 1.0 : 0.0)",
            CgBinOp.Ge  => $"({l} >= {r} ? 1.0 : 0.0)",
            CgBinOp.Eq  => $"({l} == {r} ? 1.0 : 0.0)",
            CgBinOp.Ne  => $"({l} != {r} ? 1.0 : 0.0)",
            CgBinOp.And => $"((({l}) != 0.0 && ({r}) != 0.0) ? 1.0 : 0.0)",
            CgBinOp.Or  => $"((({l}) != 0.0 || ({r}) != 0.0) ? 1.0 : 0.0)",
            _ => throw new System.InvalidOperationException(),
        };
    }

    private string EmitCall(CgCall c)
    {
        string A(int i) => Emit(c.Args[i]);
        switch (c.Name)
        {
            // Trig / exp / log — HLSL intrinsics.
            case "sin":      return $"sin({A(0)})";
            case "cos":      return $"cos({A(0)})";
            case "tan":      return $"tan({A(0)})";
            case "asin":     return $"asin({A(0)})";
            case "acos":     return $"acos({A(0)})";
            case "atan":     return $"atan({A(0)})";
            case "sinh":     return $"sinh({A(0)})";
            case "cosh":     return $"cosh({A(0)})";
            case "tanh":     return $"tanh({A(0)})";
            case "exp":      return $"exp({A(0)})";
            case "log":      return $"log({A(0)})";
            case "log2":     return $"log2({A(0)})";
            case "log10":    return $"log10({A(0)})";
            case "sqrt":     return $"sqrt({A(0)})";
            case "abs":      return $"abs({A(0)})";
            case "sign":     return $"sign({A(0)})";
            case "floor":    return $"floor({A(0)})";
            case "ceil":     return $"ceil({A(0)})";
            case "round":    return $"round({A(0)})";
            case "fract":    return $"frac({A(0)})";   // HLSL: frac, not fract.
            case "saturate": return $"saturate({A(0)})";
            case "radians":  return $"radians({A(0)})";
            case "degrees":  return $"degrees({A(0)})";
            case "atan2":    return $"atan2({A(0)}, {A(1)})";
            case "hypot":    return $"sqrt({A(0)} * {A(0)} + {A(1)} * {A(1)})";
            case "min":      return $"min({A(0)}, {A(1)})";
            case "max":      return $"max({A(0)}, {A(1)})";
            case "mod":      return $"cg_mods({A(0)}, {A(1)})";
            case "pow":      return $"pow({A(0)}, {A(1)})";
            case "step":     return $"step({A(0)}, {A(1)})";   // HLSL: step(edge, x).
            case "clamp":    return $"clamp({A(0)}, {A(1)}, {A(2)})";
            case "smoothstep": return $"smoothstep({A(0)}, {A(1)}, {A(2)})";
            case "mix":      return $"lerp({A(0)}, {A(1)}, {A(2)})";  // both scalar + vec3 form.
            case "mix_v":    return $"lerp({A(0)}, {A(1)}, {A(2)})";
            case "hash":     return $"cg_hash({A(0)})";
            case "hash2":    return $"cg_hash2({A(0)}, {A(1)})";
            case "rgb":      return $"float3({A(0)}, {A(1)}, {A(2)})";
            case "hsv":      return $"cg_fromHsv({A(0)}, {A(1)}, {A(2)})";
            case "hsl":      return $"cg_fromHsl({A(0)}, {A(1)}, {A(2)})";
            case "palette":
            {
                int stops = c.Args.Count - 1;
                _paletteArities.Add(stops);
                var sb = new StringBuilder();
                sb.Append("cg_palette").Append(stops).Append('(').Append(A(0));
                for (int i = 1; i < c.Args.Count; i++)
                {
                    sb.Append(", ").Append(Emit(c.Args[i]));
                }
                sb.Append(')');
                return sb.ToString();
            }
            case "brightness": return $"({A(0)} + {A(1)}.xxx)";
            case "contrast":   return $"(0.5 + ({A(0)} - 0.5.xxx) * (1.0 + {A(1)}))";
            case "gamma":      return $"pow(max({A(0)}, 0.0.xxx), 1.0 / max({A(1)}, 1e-6))";
            default: throw new System.InvalidOperationException($"HLSL emitter missing case for '{c.Name}'.");
        }
    }

    private static string FormatNumber(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0.0";
        // HLSL accepts plain literals as float in float context. Use round-trip
        // format ("R") so codegen reproduces the source value precisely.
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
