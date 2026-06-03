// ColorGenEmitter.cs
//
// Walks a CgProgram and renders the body of the generated IColorMap.Map()
// method as C# source. Two value forms cross expression boundaries:
//
//   • Scalar nodes  → double expression
//   • Vec3 nodes    → Cg3 expression (record struct with R/G/B)
//
// The runtime helpers (Cg3 struct + Hsv/Hsl/Palette/Hash/Brightness/...) are
// emitted inline into the generated theme file by ColorGenApi via the
// template — the emitter assumes their names exist in scope.
//
// Built-in input names map to "in_<name>" locals (declared at the top of
// the Map body in the template) so user `let` names — emitted as "v_<name>"
// — cannot shadow or collide with them.

using System.Globalization;
using System.Text;
using FracturingFog.ColorGen.Parser;

namespace FracturingFog.ColorGen.Emitters;

public sealed class ColorGenEmitter
{
    private readonly string _indent;
    public ColorGenEmitter(string indent = "            ") { _indent = indent; }

    /// <summary>Emit the body of Map() — let-bindings + final
    /// "return PackArgb(...);" — using <paramref name="indent"/> for the
    /// statement lines. Assumes the wrapping method already declared
    /// "double in_smooth = …;" et al.</summary>
    public string EmitBody(CgProgram prog)
    {
        var sb = new StringBuilder();
        foreach (var s in prog.Statements)
        {
            switch (s)
            {
                case CgLet let:
                {
                    string typeKw = let.Value.Type == CgType.Scalar ? "double" : "Cg3";
                    sb.Append(_indent).Append(typeKw).Append(" v_").Append(let.Name).Append(" = ");
                    sb.Append(Emit(let.Value)).AppendLine(";");
                    break;
                }
                case CgReturn ret:
                {
                    sb.Append(_indent).Append("return PackArgb(").Append(Emit(ret.Value)).AppendLine(");");
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
        CgChannel ch    => $"({Emit(ch.Target)}).{char.ToUpperInvariant(ch.Channel)}",
        CgUnary u       => EmitUnary(u),
        CgBinary b      => EmitBinary(b),
        CgTernary tern  => $"(({Emit(tern.Cond)}) != 0.0 ? {Emit(tern.IfTrue)} : {Emit(tern.IfFalse)})",
        CgCall c        => EmitCall(c),
        _               => throw new System.InvalidOperationException($"Unhandled node {n.GetType().Name}"),
    };

    private static string EmitVar(CgVar v) => v.IsBuiltIn ? $"in_{v.Name}" : $"v_{v.Name}";

    private string EmitUnary(CgUnary u)
    {
        string inner = Emit(u.Operand);
        return u.Op switch
        {
            CgUnaryOp.Neg => u.Type == CgType.Scalar ? $"(-{inner})" : $"Cg3.Neg({inner})",
            CgUnaryOp.Pos => $"(+{inner})",
            CgUnaryOp.Not => $"(({inner}) == 0.0 ? 1.0 : 0.0)",
            _ => throw new System.InvalidOperationException(),
        };
    }

    private string EmitBinary(CgBinary b)
    {
        string l = Emit(b.Lhs), r = Emit(b.Rhs);
        bool vec = b.Type == CgType.Vec3;
        return b.Op switch
        {
            CgBinOp.Add => vec ? WrapAdd(b.Lhs, b.Rhs, l, r) : $"({l} + {r})",
            CgBinOp.Sub => vec ? WrapSub(b.Lhs, b.Rhs, l, r) : $"({l} - {r})",
            CgBinOp.Mul => vec ? WrapMul(b.Lhs, b.Rhs, l, r) : $"({l} * {r})",
            CgBinOp.Div => vec ? WrapDiv(b.Lhs, b.Rhs, l, r) : $"({l} / {r})",
            CgBinOp.Mod => vec ? WrapMod(b.Lhs, b.Rhs, l, r) : $"CgScalar.Mod({l}, {r})",
            CgBinOp.Pow => vec ? WrapPow(b.Lhs, b.Rhs, l, r) : $"System.Math.Pow({l}, {r})",
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

        // Vec3 mixed-type helpers: Cg3.AddSV/VSV broadcast scalar↔vec3.
        static string WrapAdd(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Add({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.AddVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.AddSV({l}, {r})",
                _ => $"({l} + {r})"
            };
        static string WrapSub(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Sub({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.SubVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.SubSV({l}, {r})",
                _ => $"({l} - {r})"
            };
        static string WrapMul(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Mul({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.MulVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.MulSV({l}, {r})",
                _ => $"({l} * {r})"
            };
        static string WrapDiv(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Div({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.DivVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.DivSV({l}, {r})",
                _ => $"({l} / {r})"
            };
        static string WrapMod(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Mod({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.ModVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.ModSV({l}, {r})",
                _ => $"CgScalar.Mod({l}, {r})"
            };
        static string WrapPow(CgNode lhs, CgNode rhs, string l, string r) =>
            (lhs.Type, rhs.Type) switch
            {
                (CgType.Vec3, CgType.Vec3)   => $"Cg3.Pow({l}, {r})",
                (CgType.Vec3, CgType.Scalar) => $"Cg3.PowVS({l}, {r})",
                (CgType.Scalar, CgType.Vec3) => $"Cg3.PowSV({l}, {r})",
                _ => $"System.Math.Pow({l}, {r})"
            };
    }

    private string EmitCall(CgCall c)
    {
        string A(int i) => Emit(c.Args[i]);
        switch (c.Name)
        {
            case "sin":      return $"System.Math.Sin({A(0)})";
            case "cos":      return $"System.Math.Cos({A(0)})";
            case "tan":      return $"System.Math.Tan({A(0)})";
            case "asin":     return $"System.Math.Asin({A(0)})";
            case "acos":     return $"System.Math.Acos({A(0)})";
            case "atan":     return $"System.Math.Atan({A(0)})";
            case "sinh":     return $"System.Math.Sinh({A(0)})";
            case "cosh":     return $"System.Math.Cosh({A(0)})";
            case "tanh":     return $"System.Math.Tanh({A(0)})";
            case "exp":      return $"System.Math.Exp({A(0)})";
            case "log":      return $"System.Math.Log({A(0)})";
            case "log2":     return $"System.Math.Log2({A(0)})";
            case "log10":    return $"System.Math.Log10({A(0)})";
            case "sqrt":     return $"System.Math.Sqrt({A(0)})";
            case "abs":      return $"System.Math.Abs({A(0)})";
            case "sign":     return $"System.Math.Sign({A(0)})";
            case "floor":    return $"System.Math.Floor({A(0)})";
            case "ceil":     return $"System.Math.Ceiling({A(0)})";
            case "round":    return $"System.Math.Round({A(0)})";
            case "fract":    return $"CgScalar.Fract({A(0)})";
            case "saturate": return $"System.Math.Clamp({A(0)}, 0.0, 1.0)";
            case "radians":  return $"({A(0)} * (System.Math.PI / 180.0))";
            case "degrees":  return $"({A(0)} * (180.0 / System.Math.PI))";
            case "atan2":    return $"System.Math.Atan2({A(0)}, {A(1)})";
            case "hypot":    return $"CgScalar.Hypot({A(0)}, {A(1)})";
            case "min":      return $"System.Math.Min({A(0)}, {A(1)})";
            case "max":      return $"System.Math.Max({A(0)}, {A(1)})";
            case "mod":      return $"CgScalar.Mod({A(0)}, {A(1)})";
            case "pow":      return $"System.Math.Pow({A(0)}, {A(1)})";
            case "step":     return $"({A(1)} < {A(0)} ? 0.0 : 1.0)";
            case "clamp":    return $"System.Math.Clamp({A(0)}, {A(1)}, {A(2)})";
            case "smoothstep": return $"CgScalar.Smoothstep({A(0)}, {A(1)}, {A(2)})";
            case "mix":      return $"({A(0)} + ({A(1)} - {A(0)}) * {A(2)})";
            case "mix_v":    return $"Cg3.Mix({A(0)}, {A(1)}, {A(2)})";
            case "hash":     return $"CgScalar.Hash({A(0)})";
            case "hash2":    return $"CgScalar.Hash2({A(0)}, {A(1)})";
            case "rgb":      return $"new Cg3({A(0)}, {A(1)}, {A(2)})";
            case "hsv":      return $"Cg3.FromHsv({A(0)}, {A(1)}, {A(2)})";
            case "hsl":      return $"Cg3.FromHsl({A(0)}, {A(1)}, {A(2)})";
            case "palette":
            {
                var sb = new StringBuilder();
                sb.Append("Cg3.Palette(").Append(A(0));
                for (int i = 1; i < c.Args.Count; i++)
                {
                    sb.Append(", ").Append(Emit(c.Args[i]));
                }
                sb.Append(')');
                return sb.ToString();
            }
            case "brightness": return $"Cg3.Brightness({A(0)}, {A(1)})";
            case "contrast":   return $"Cg3.Contrast({A(0)}, {A(1)})";
            case "gamma":      return $"Cg3.Gamma({A(0)}, {A(1)})";
            default: throw new System.InvalidOperationException($"Emitter missing case for '{c.Name}'.");
        }
    }

    private static string FormatNumber(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "0.0";
        // "R" round-trips; append "D" so it's unambiguously a double literal.
        return v.ToString("R", CultureInfo.InvariantCulture) + "D";
    }
}
