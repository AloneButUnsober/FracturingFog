// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Avx2DerivEmitter.cs
//
// Emits the dz/dc derivative update inside the AVX-2 perturbation lane.
// Bindings match the scope the SIMD lane provides — z is the FULL
// perturbed value (Zr_v + dr), bound to (zr_v, zi_v). dz/dc lives in
// (drv, div) and outputs (drv_new, div_new).
//
// Output: SSA-temp prelude + two new-value Vector256<double> assignments.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx2DerivEmitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;

    public Avx2DerivEmitter(string indent = "                    ")
    {
        _indent = indent;
    }

    protected override string ZRe => "zr_v";
    protected override string ZIm => "zi_v";
    protected override string CRe => "Cr_v";
    protected override string CIm => "Ci_v";
    protected override string DRe => "drv";
    protected override string DIm => "div";

    private string NewTemp(string kind)
    {
        _tmpId++;
        return $"dv2{kind}{_tmpId}";
    }

    private ComplexExpr Bind(string re, string im)
    {
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector256<double>.Zero", ImZero: true);
    }

    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector256<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
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
        if (a.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Add({a.Re}, {b.Re})", $"Avx.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Subtract({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(
                NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx.Subtract(Vector256<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx.Subtract({a.Re}, {b.Re})", $"Avx.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx.Multiply({a.Re}, {b.Re})");
        if (a.ImZero)
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Re}, {b.Im})");
        if (b.ImZero)
            return Bind($"Avx.Multiply({a.Re}, {b.Re})",
                        $"Avx.Multiply({a.Im}, {b.Re})");
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
            return Bind($"Avx.Divide({a.Re}, {b.Re})",
                        $"Avx.Divide({a.Im}, {b.Re})");
        string denom = $"Fma.MultiplyAdd({b.Re}, {b.Re}, Avx.Multiply({b.Im}, {b.Im}))";
        if (a.ImZero)
            return Bind(
                $"Avx.Divide(Avx.Multiply({a.Re}, {b.Re}), {denom})",
                $"Avx.Subtract(Vector256<double>.Zero, Avx.Divide(Avx.Multiply({a.Re}, {b.Im}), {denom}))");
        string nre = $"Fma.MultiplyAdd({a.Re}, {b.Re}, Avx.Multiply({a.Im}, {b.Im}))";
        string nim = $"Fma.MultiplyAddNegated({a.Re}, {b.Im}, Avx.Multiply({a.Im}, {b.Re}))";
        return Bind($"Avx.Divide({nre}, {denom})", $"Avx.Divide({nim}, {denom})");
    }

    // Transcendentals on Vector256 — per-lane scalar fallback (4 lanes).
    private ComplexExpr EmitPerLaneTranscendental(ComplexExpr a, string opName)
    {
        string ar = a.Re;
        string ai = a.ImZero ? "Vector256<double>.Zero" : a.Im;
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        string ns = tre; // salt for lane locals
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
            _prelude.Append(_indent).Append("    double ");
            for (int k = 0; k < 4; k++)
            {
                if (k > 0) _prelude.Append(',');
                _prelude.Append("e").Append(k).Append("im_").Append(ns).Append("=0");
            }
            _prelude.Append(";\n");
        }
        _prelude.Append(_indent).Append("    double ");
        for (int k = 0; k < 4; k++)
        {
            if (k > 0) _prelude.Append(',');
            _prelude.Append("r").Append(k).Append('_').Append(ns);
        }
        for (int k = 0; k < 4; k++)
        {
            _prelude.Append(',').Append("i").Append(k).Append('_').Append(ns);
        }
        _prelude.Append(";\n");
        for (int k = 0; k < 4; k++)
            _prelude.Append(_indent).Append("    ").Append(
                ScalarPerLane(opName, $"e{k}re_{ns}", $"e{k}im_{ns}", $"r{k}_{ns}", $"i{k}_{ns}"))
                .Append('\n');
        _prelude.Append(_indent).Append("    ").Append(tre).Append(" = Vector256.Create(");
        for (int k = 0; k < 4; k++) { if (k > 0) _prelude.Append(','); _prelude.Append("r").Append(k).Append('_').Append(ns); }
        _prelude.Append(");\n");
        _prelude.Append(_indent).Append("    ").Append(tim).Append(" = Vector256.Create(");
        for (int k = 0; k < 4; k++) { if (k > 0) _prelude.Append(','); _prelude.Append("i").Append(k).Append('_').Append(ns); }
        _prelude.Append(");\n");
        _prelude.Append(_indent).Append("}\n");
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private static string ScalarPerLane(string op, string re, string im, string rOut, string iOut) => op switch
    {
        "sin" => $"{rOut} = Math.Sin({re}) * Math.Cosh({im}); {iOut} = Math.Cos({re}) * Math.Sinh({im});",
        "cos" => $"{rOut} = Math.Cos({re}) * Math.Cosh({im}); {iOut} = -(Math.Sin({re}) * Math.Sinh({im}));",
        "exp" => $"{{ double ex = Math.Exp({re}); {rOut} = ex * Math.Cos({im}); {iOut} = ex * Math.Sin({im}); }}",
        "log" => $"{rOut} = 0.5 * Math.Log({re} * {re} + {im} * {im}); {iOut} = Math.Atan2({im}, {re});",
        "arg" => $"{rOut} = Math.Atan2({im}, {re}); {iOut} = 0.0;",
        // √ per lane (analytic-DE derivative trees) via full complex Sqrt.
        "sqrt" => $"{{ var _p = System.Numerics.Complex.Sqrt(new System.Numerics.Complex({re}, {im})); {rOut} = _p.Real; {iOut} = _p.Imaginary; }}",
        _ => throw new InvalidOperationException($"Avx2DerivEmitter: unknown transcendental {op}"),
    };

    protected override ComplexExpr OpSin(ComplexExpr a) => EmitPerLaneTranscendental(a, "sin");
    protected override ComplexExpr OpCos(ComplexExpr a) => EmitPerLaneTranscendental(a, "cos");
    protected override ComplexExpr OpExp(ComplexExpr a) => EmitPerLaneTranscendental(a, "exp");
    protected override ComplexExpr OpLog(ComplexExpr a) => EmitPerLaneTranscendental(a, "log");
    // √ from the inverse trig / hyperbolic DE rules. #215.
    protected override ComplexExpr OpSqrt(ComplexExpr a) => EmitPerLaneTranscendental(a, "sqrt");

    // Piecewise — mask blend across 4 Vector256 lanes via Avx.Compare
    // → Vector256.ConditionalSelect. Both branches eager-evaluated by
    // EmitterBase; prelude expansion runs unconditionally so every lane
    // has both values, blend picks one per lane on the mask.
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
            throw new InvalidOperationException($"Avx2DerivEmitter: unhandled CondNode {c.GetType().Name}");
        string l = EmitCondTermVec(cmp.Left);
        string r = EmitCondTermVec(cmp.Right);
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
                return NewBoundRe(
                    $"Fma.MultiplyAdd({av.Re}, {av.Re}, Avx.Multiply({av.Im}, {av.Im}))");
            case CondArg ag:
                var argv = Emit(ag.Of);
                return EmitPerLaneTranscendental(argv, "arg").Re;
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return NewBoundRe($"Vector256.Create({lit})");
            default:
                throw new InvalidOperationException($"Avx2DerivEmitter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    public string EmitDerivBody(AstNode root)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector256<double> drv_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector256<double> div_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
