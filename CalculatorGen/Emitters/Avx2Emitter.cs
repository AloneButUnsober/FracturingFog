// Avx2Emitter.cs
//
// Emits the per-iteration body using Vector256<double> (4 lanes, AVX2 +
// FMA). Each ComplexExpr in the walk represents a register-sized vector
// of complex values — lanes are four neighbouring pixels along x.
//
// The emitter accumulates a prelude of SSA-style temporary assignments
// (one per intermediate ComplexExpr) so the rendered code is readable
// instead of a single tower of nested intrinsic calls. The JIT folds
// constant temps so there's no runtime cost.
//
// Important: one Avx2Emitter instance per body emission. The prelude
// accumulates per-instance; reusing it across two bodies would leak temp
// names and confuse the rendered output.
//
// FMA usage
//   • Avx2 + Fma are separate intrinsic classes in .NET. We emit
//     Fma.MultiplyAdd(a, b, c)      = a·b + c
//     Fma.MultiplyAddNegated(a, b, c) = -(a·b) + c
//     for the complex multiply step: (ac − bd, ad + bc).
//   • The generated calculator guards with `Avx2.IsSupported &&
//     Fma.IsSupported` at the call site and falls back to scalar; that
//     fallback is wired by the template, not this emitter.
//
// Imag-zero optimisation
//   When a subexpression is provably real (ImZero=true on its
//   ComplexExpr), the emitter binds only the Re temp and reuses
//   `Vector256<double>.Zero` as the Im placeholder. Mul shortcuts to
//   scalar-by-complex form; Add/Sub elide the dead-zero add. Cuts the
//   emitted prelude by ~30 % for typical polynomial steps + derivative
//   updates.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx2Emitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;
    private readonly string _tempPrefix;

    public Avx2Emitter(string indent = "                ", string tempPrefix = "t")
    {
        _indent = indent;
        _tempPrefix = tempPrefix;
    }

    protected override string ZRe => "zr";
    protected override string ZIm => "zi";
    protected override string CRe => "cr";
    protected override string CIm => "ci";
    protected override string DRe => "dr";
    protected override string DIm => "di";
    protected override string PrevRe => "pr";
    protected override string PrevIm => "pi";
    protected override string IterRe => "iter_v";
    protected override string IterImLiteral => "Vector256<double>.Zero";

    private string NewTemp(string kind)
    {
        _tmpId++;
        return $"{_tempPrefix}{kind}{_tmpId}";
    }

    /// <summary>Bind a Re temp and an Im temp.</summary>
    private ComplexExpr Bind(string re, string im)
    {
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    /// <summary>Bind only a Re temp; Im is the zero literal. Used when the
    /// subexpression is provably real-valued.</summary>
    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector256<double>.Zero", ImZero: true);
    }

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        return BindReOnly($"Vector256.Create({lit})");
    }

    protected override ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Add({a.Re}, {b.Re})");
        if (a.ImZero) // a.Im is the zero vector; result.Im = b.Im
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Add({a.Re}, {b.Re})", $"Avx.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Subtract({a.Re}, {b.Re})");
        if (a.ImZero) // result.Im = -b.Im
            return new ComplexExpr(
                NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx.Subtract(Vector256<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero) // result.Im = a.Im
            return new ComplexExpr(NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Subtract({a.Re}, {b.Re})", $"Avx.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Multiply({a.Re}, {b.Re})");
        if (a.ImZero) // (a.Re + 0i)·(b.Re + b.Im·i) = a.Re·b.Re + a.Re·b.Im·i
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Re}, {b.Im})");
        if (b.ImZero) // (a.Re + a.Im·i)·(b.Re + 0i) = a.Re·b.Re + a.Im·b.Re·i
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Im}, {b.Re})");
        // Full complex: (ac − bd, ad + bc)
        string re = $"Fma.MultiplyAddNegated({a.Im}, {b.Im}, Avx.Multiply({a.Re}, {b.Re}))";
        string im = $"Fma.MultiplyAdd({a.Re}, {b.Im}, Avx.Multiply({a.Im}, {b.Re}))";
        return Bind(re, im);
    }

    protected override ComplexExpr OpNeg(ComplexExpr a)
    {
        if (a.ImZero)
            return BindReOnly($"Avx.Subtract(Vector256<double>.Zero, {a.Re})");
        return Bind($"Avx.Subtract(Vector256<double>.Zero, {a.Re})",
                    $"Avx.Subtract(Vector256<double>.Zero, {a.Im})");
    }

    protected override ComplexExpr OpDiv(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Divide({a.Re}, {b.Re})");
        if (b.ImZero)
        {
            // Complex / real → element-wise.
            return Bind($"Avx.Divide({a.Re}, {b.Re})", $"Avx.Divide({a.Im}, {b.Re})");
        }
        // |b|² = b.re² + b.im²
        string denom = $"Fma.MultiplyAdd({b.Re}, {b.Re}, Avx.Multiply({b.Im}, {b.Im}))";
        if (a.ImZero)
        {
            return Bind(
                $"Avx.Divide(Avx.Multiply({a.Re}, {b.Re}), {denom})",
                $"Avx.Subtract(Vector256<double>.Zero, Avx.Divide(Avx.Multiply({a.Re}, {b.Im}), {denom}))");
        }
        // (a.re·b.re + a.im·b.im) / |b|²
        string num_re = $"Fma.MultiplyAdd({a.Re}, {b.Re}, Avx.Multiply({a.Im}, {b.Im}))";
        // (a.im·b.re − a.re·b.im) / |b|²
        string num_im = $"Fma.MultiplyAddNegated({a.Re}, {b.Im}, Avx.Multiply({a.Im}, {b.Re}))";
        return Bind($"Avx.Divide({num_re}, {denom})", $"Avx.Divide({num_im}, {denom})");
    }

    protected override ComplexExpr OpConj(ComplexExpr a)
    {
        if (a.ImZero) return a;
        return new ComplexExpr(a.Re,
            NewBoundRe($"Avx.Subtract(Vector256<double>.Zero, {a.Im})"),
            ImZero: false);
    }

    protected override ComplexExpr OpFold(ComplexExpr a)
    {
        // |x| via bitmask: clear the sign bit. AVX2 has no scalar abs;
        // Avx.AndNot with the sign-bit mask matches what JIT emits for
        // Math.Abs on a Vector256<double>.
        string sign = "Vector256.Create(-0.0)";
        if (a.ImZero)
            return BindReOnly($"Avx.AndNot({sign}, {a.Re})");
        return Bind($"Avx.AndNot({sign}, {a.Re})", $"Avx.AndNot({sign}, {a.Im})");
    }

    // Transcendentals are scalar-only on AVX2 — System.Math has no
    // Vector256 sin/cos/exp/log. Per-lane fallback: extract each of
    // the 4 lanes, apply the scalar complex identity, repack into a
    // Vector256. This kills SIMD parallelism for transcendental
    // equations, but keeps Re/Im width consistent so downstream FMA
    // chains still produce 4-wide results. For deep equations
    // dominated by transcendentals, this path becomes a per-lane loop;
    // user trades AVX2 throughput for capability.
    //
    // Identities (same as ScalarEmitter):
    //   sin(a+bi) = sin(a)·cosh(b) + i·cos(a)·sinh(b)
    //   cos(a+bi) = cos(a)·cosh(b) − i·sin(a)·sinh(b)
    //   exp(a+bi) = e^a·(cos(b) + i·sin(b))
    //   log(a+bi) = (1/2)·log(a²+b²) + i·atan2(b, a)

    private ComplexExpr EmitPerLaneTranscendental(ComplexExpr a, string opName)
    {
        // Force materialised re/im vectors so the inline per-lane body
        // can index them by name. Lane locals must be unique per call
        // — the surrounding template lexical scope may already declare
        // r0/r1/r2/r3 elsewhere, so we prefix with a per-call tempId.
        string ar = a.Re;
        string ai = a.ImZero ? "Vector256<double>.Zero" : a.Im;
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        // tre is unique; reuse its numeric suffix as a per-call salt.
        string ns = tre; // e.g. "t_re17" — appended into local names.
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre)
            .Append("; Vector256<double> ").Append(tim).Append(';').Append('\n');
        _prelude.Append(_indent).Append("{\n");
        for (int k = 0; k < 4; k++)
            _prelude.Append(_indent).Append("    double e").Append(k).Append("re_").Append(ns)
                .Append(" = ").Append(ar).Append(".GetElement(").Append(k).Append(");\n");
        if (!a.ImZero)
        {
            for (int k = 0; k < 4; k++)
                _prelude.Append(_indent).Append("    double e").Append(k).Append("im_").Append(ns)
                    .Append(" = ").Append(ai).Append(".GetElement(").Append(k).Append(");\n");
        }
        else
        {
            _prelude.Append(_indent).Append("    double e0im_").Append(ns).Append("=0,e1im_").Append(ns)
                .Append("=0,e2im_").Append(ns).Append("=0,e3im_").Append(ns).Append("=0;\n");
        }
        _prelude.Append(_indent).Append("    double r0_").Append(ns).Append(",r1_").Append(ns)
            .Append(",r2_").Append(ns).Append(",r3_").Append(ns).Append(",i0_").Append(ns)
            .Append(",i1_").Append(ns).Append(",i2_").Append(ns).Append(",i3_").Append(ns).Append(";\n");
        for (int k = 0; k < 4; k++)
        {
            _prelude.Append(_indent).Append("    ").Append(
                ScalarPerLane(opName, $"e{k}re_{ns}", $"e{k}im_{ns}", $"r{k}_{ns}", $"i{k}_{ns}"))
                .Append('\n');
        }
        _prelude.Append(_indent).Append("    ").Append(tre).Append(" = Vector256.Create(r0_")
            .Append(ns).Append(", r1_").Append(ns).Append(", r2_").Append(ns).Append(", r3_").Append(ns).Append(");\n");
        _prelude.Append(_indent).Append("    ").Append(tim).Append(" = Vector256.Create(i0_")
            .Append(ns).Append(", i1_").Append(ns).Append(", i2_").Append(ns).Append(", i3_").Append(ns).Append(");\n");
        _prelude.Append(_indent).Append("}\n");
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private static string ScalarPerLane(string op, string re, string im, string rOut, string iOut) => op switch
    {
        "sin" => $"{rOut} = Math.Sin({re}) * Math.Cosh({im}); {iOut} = Math.Cos({re}) * Math.Sinh({im});",
        "cos" => $"{rOut} = Math.Cos({re}) * Math.Cosh({im}); {iOut} = -(Math.Sin({re}) * Math.Sinh({im}));",
        "exp" => $"{{ double ex = Math.Exp({re}); {rOut} = ex * Math.Cos({im}); {iOut} = ex * Math.Sin({im}); }}",
        "log" => $"{rOut} = 0.5 * Math.Log({re} * {re} + {im} * {im}); {iOut} = Math.Atan2({im}, {re});",
        // arg(a+bi) = atan2(b, a). Result lifted to (arg, 0) — iOut = 0.
        "arg" => $"{rOut} = Math.Atan2({im}, {re}); {iOut} = 0.0;",
        _ => throw new InvalidOperationException($"Avx2Emitter: unknown transcendental {op}"),
    };

    protected override ComplexExpr OpSin(ComplexExpr a) => EmitPerLaneTranscendental(a, "sin");
    protected override ComplexExpr OpCos(ComplexExpr a) => EmitPerLaneTranscendental(a, "cos");
    protected override ComplexExpr OpExp(ComplexExpr a) => EmitPerLaneTranscendental(a, "exp");
    protected override ComplexExpr OpLog(ComplexExpr a) => EmitPerLaneTranscendental(a, "log");
    protected override ComplexExpr OpArg(ComplexExpr a) => EmitPerLaneTranscendental(a, "arg");
    // Binary atan2 has no AVX2 vector intrinsic and the existing per-lane
    // helper takes a single complex input. Scalar / DD / QD paths cover
    // atan2 fully — let the generator surface a clear error here so the
    // user knows to either rewrite via arg() or accept the scalar-only
    // path (CalcGen runs the scalar emitter unconditionally as a fallback).
    protected override ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        throw new InvalidOperationException(
            "atan2(y, x) is not implemented on the AVX2 path. Use arg(z) instead, " +
            "or accept the scalar-only fallback by removing the AVX2 calc registration.");

    // min / max have native Vector256<double> intrinsics — emit them
    // directly so the inner loop stays vectorised. Inputs treated as
    // real-valued: imag part dropped, output ImZero=true so downstream
    // Add/Mul elides the dead-zero imag like RealConst.
    protected override ComplexExpr OpMin(ComplexExpr a, ComplexExpr b) =>
        new($"Vector256.Min({a.Re}, {b.Re})", "Vector256<double>.Zero", ImZero: true);

    protected override ComplexExpr OpMax(ComplexExpr a, ComplexExpr b) =>
        new($"Vector256.Max({a.Re}, {b.Re})", "Vector256<double>.Zero", ImZero: true);

    // mod (%) has no SIMD intrinsic — fall back to per-lane scalar via the
    // existing transcendental infrastructure, but adapted to two inputs.
    // Materialise both vectors into 4 doubles each, run % per lane, repack.
    protected override ComplexExpr OpMod(ComplexExpr a, ComplexExpr b)
    {
        string ar = a.Re;
        string br = b.Re;
        string tre = NewTemp("re");
        string ns = tre;
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(';').Append('\n');
        _prelude.Append(_indent).Append("{\n");
        for (int k = 0; k < 4; k++)
            _prelude.Append(_indent).Append("    double a").Append(k).Append("_").Append(ns)
                .Append(" = ").Append(ar).Append(".GetElement(").Append(k).Append(");\n");
        for (int k = 0; k < 4; k++)
            _prelude.Append(_indent).Append("    double b").Append(k).Append("_").Append(ns)
                .Append(" = ").Append(br).Append(".GetElement(").Append(k).Append(");\n");
        _prelude.Append(_indent).Append("    ").Append(tre).Append(" = Vector256.Create(")
            .Append("a0_").Append(ns).Append(" % b0_").Append(ns).Append(", ")
            .Append("a1_").Append(ns).Append(" % b1_").Append(ns).Append(", ")
            .Append("a2_").Append(ns).Append(" % b2_").Append(ns).Append(", ")
            .Append("a3_").Append(ns).Append(" % b3_").Append(ns).Append(");\n");
        _prelude.Append(_indent).Append("}\n");
        return new ComplexExpr(tre, "Vector256<double>.Zero", ImZero: true);
    }

    // Piecewise — mask blend via Avx.Compare → Vector256.ConditionalSelect.
    // Compare returns the per-lane mask as Vector256<double> (all-1 bits
    // set, NaN payload) in matching lanes. Both branches were eager-
    // evaluated by EmitterBase so any SSA prelude they emit ran
    // unconditionally; every lane has both branch values available.
    protected override ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV)
    {
        string mask = EmitMask(cond);
        string thenIm = thenV.ImZero ? "Vector256<double>.Zero" : thenV.Im;
        string elseIm = elseV.ImZero ? "Vector256<double>.Zero" : elseV.Im;
        return Bind(
            $"Vector256.ConditionalSelect({mask}, {thenV.Re}, {elseV.Re})",
            $"Vector256.ConditionalSelect({mask}, {thenIm}, {elseIm})");
    }

    private string EmitMask(CondNode c)
    {
        if (c is not Cmp cmp)
            throw new InvalidOperationException($"Avx2Emitter: unhandled CondNode {c.GetType().Name}");
        string l = EmitCondTermVec(cmp.Left);
        string r = EmitCondTermVec(cmp.Right);
        // FloatComparisonMode.OrderedXxxNonSignaling — Ordered = NaN
        // operands compare false (matches C# `>` semantics).
        string mode = cmp.Op switch
        {
            CmpOp.Gt => "FloatComparisonMode.OrderedGreaterThanNonSignaling",
            CmpOp.Lt => "FloatComparisonMode.OrderedLessThanNonSignaling",
            CmpOp.Ge => "FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling",
            CmpOp.Le => "FloatComparisonMode.OrderedLessThanOrEqualNonSignaling",
            CmpOp.Eq => "FloatComparisonMode.OrderedEqualNonSignaling",
            CmpOp.Ne => "FloatComparisonMode.OrderedNotEqualNonSignaling",
            _ => throw new InvalidOperationException($"Unknown CmpOp {cmp.Op}"),
        };
        return NewBoundRe($"Avx.Compare({l}, {r}, {mode})");
    }

    private string EmitCondTermVec(CondTerm t)
    {
        switch (t)
        {
            case CondRe r:
                return Emit(r.Of).Re;
            case CondIm im:
                var ev = Emit(im.Of);
                return ev.ImZero ? "Vector256<double>.Zero" : ev.Im;
            case CondAbs2 a:
                var av = Emit(a.Of);
                if (av.ImZero)
                    return NewBoundRe($"Avx.Multiply({av.Re}, {av.Re})");
                return NewBoundRe($"Fma.MultiplyAdd({av.Re}, {av.Re}, Avx.Multiply({av.Im}, {av.Im}))");
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return NewBoundRe($"Vector256.Create({lit})");
            default:
                throw new InvalidOperationException($"Avx2Emitter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    /// <summary>Bind a single Re temp without claiming ImZero. Used when
    /// Add/Sub need to bind the Re temp but the Im part is reused
    /// from one of the inputs (no new bind needed).</summary>
    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
    }

    /// <summary>Render the prelude SSA temps + the two final new-value
    /// assignments. <paramref name="prefix"/> determines the output names
    /// (e.g. "z" → zr_new, zi_new ; "d" → dr_new, di_new).</summary>
    public string EmitNewValueBody(AstNode root, string prefix)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector256<double> ").Append(prefix).Append("r_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector256<double> ").Append(prefix).Append("i_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
