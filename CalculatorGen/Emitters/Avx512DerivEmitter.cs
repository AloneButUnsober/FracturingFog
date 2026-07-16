// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Avx512DerivEmitter.cs
//
// Emits the dz/dc derivative update inside the AVX-512 perturbation
// lane. Bindings match the scope the SIMD lane provides — z is the FULL
// perturbed value (Zr_v + dr), bound to (zr_v, zi_v). dz/dc lives in
// (drv, div) and outputs (drv_new, div_new).
//
// Output: SSA-temp prelude + two new-value Vector512<double> assignments.

using System.Globalization;
using System.Text;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class Avx512DerivEmitter : EmitterBase
{
    private int _tmpId;
    private readonly StringBuilder _prelude = new();
    private readonly string _indent;

    public Avx512DerivEmitter(string indent = "                    ")
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
        return $"dv{kind}{_tmpId}";
    }

    private ComplexExpr Bind(string re, string im)
    {
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tim).Append(" = ").Append(im).Append(';').Append('\n');
        return new ComplexExpr(tre, tim, ImZero: false);
    }

    private ComplexExpr BindReOnly(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return new ComplexExpr(tre, "Vector512<double>.Zero", ImZero: true);
    }

    private string NewBoundRe(string re)
    {
        string tre = NewTemp("re");
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre).Append(" = ").Append(re).Append(';').Append('\n');
        return tre;
    }

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        return BindReOnly($"Vector512.Create({lit})");
    }

    protected override ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Add({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Add({a.Re}, {b.Re})"), b.Im, ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Add({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx512F.Add({a.Re}, {b.Re})", $"Avx512F.Add({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Subtract({a.Re}, {b.Re})");
        if (a.ImZero)
            return new ComplexExpr(
                NewBoundRe($"Avx512F.Subtract({a.Re}, {b.Re})"),
                NewBoundRe($"Avx512F.Subtract(Vector512<double>.Zero, {b.Im})"),
                ImZero: false);
        if (b.ImZero)
            return new ComplexExpr(NewBoundRe($"Avx512F.Subtract({a.Re}, {b.Re})"), a.Im, ImZero: false);
        return Bind($"Avx512F.Subtract({a.Re}, {b.Re})", $"Avx512F.Subtract({a.Im}, {b.Im})");
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Multiply({a.Re}, {b.Re})");
        if (a.ImZero)
            return Bind($"Avx512F.Multiply({a.Re}, {b.Re})",
                        $"Avx512F.Multiply({a.Re}, {b.Im})");
        if (b.ImZero)
            return Bind($"Avx512F.Multiply({a.Re}, {b.Re})",
                        $"Avx512F.Multiply({a.Im}, {b.Re})");
        string re = $"Avx512F.FusedMultiplyAddNegated({a.Im}, {b.Im}, Avx512F.Multiply({a.Re}, {b.Re}))";
        string im = $"Avx512F.FusedMultiplyAdd({a.Re}, {b.Im}, Avx512F.Multiply({a.Im}, {b.Re}))";
        return Bind(re, im);
    }

    protected override ComplexExpr OpNeg(ComplexExpr a)
    {
        if (a.ImZero)
            return BindReOnly($"Avx512F.Subtract(Vector512<double>.Zero, {a.Re})");
        return Bind($"Avx512F.Subtract(Vector512<double>.Zero, {a.Re})",
                    $"Avx512F.Subtract(Vector512<double>.Zero, {a.Im})");
    }

    protected override ComplexExpr OpDiv(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return BindReOnly($"Avx512F.Divide({a.Re}, {b.Re})");
        if (b.ImZero)
            return Bind($"Avx512F.Divide({a.Re}, {b.Re})",
                        $"Avx512F.Divide({a.Im}, {b.Re})");
        string denom = $"Avx512F.FusedMultiplyAdd({b.Re}, {b.Re}, Avx512F.Multiply({b.Im}, {b.Im}))";
        if (a.ImZero)
            return Bind(
                $"Avx512F.Divide(Avx512F.Multiply({a.Re}, {b.Re}), {denom})",
                $"Avx512F.Subtract(Vector512<double>.Zero, Avx512F.Divide(Avx512F.Multiply({a.Re}, {b.Im}), {denom}))");
        string nre = $"Avx512F.FusedMultiplyAdd({a.Re}, {b.Re}, Avx512F.Multiply({a.Im}, {b.Im}))";
        string nim = $"Avx512F.FusedMultiplyAddNegated({a.Re}, {b.Im}, Avx512F.Multiply({a.Im}, {b.Re}))";
        return Bind($"Avx512F.Divide({nre}, {denom})", $"Avx512F.Divide({nim}, {denom})");
    }

    // Transcendentals on Vector512 — per-lane scalar fallback (8 lanes).
    // Same shape as Avx2Emitter; chosen identities mirror ScalarEmitter.
    private ComplexExpr EmitPerLaneTranscendental(ComplexExpr a, string opName)
    {
        string ar = a.Re;
        string ai = a.ImZero ? "Vector512<double>.Zero" : a.Im;
        string tre = NewTemp("re");
        string tim = NewTemp("im");
        string ns = tre; // salt for lane locals
        _prelude.Append(_indent).Append("Vector512<double> ").Append(tre)
            .Append("; Vector512<double> ").Append(tim).Append(';').Append('\n');
        _prelude.Append(_indent).Append("{\n");
        for (int k = 0; k < 8; k++)
            _prelude.Append(_indent).Append("    double e").Append(k).Append("re_").Append(ns)
                .Append(" = ").Append(ar).Append(".GetElement(").Append(k).Append(");\n");
        if (!a.ImZero)
        {
            for (int k = 0; k < 8; k++)
                _prelude.Append(_indent).Append("    double e").Append(k).Append("im_").Append(ns)
                    .Append(" = ").Append(ai).Append(".GetElement(").Append(k).Append(");\n");
        }
        else
        {
            _prelude.Append(_indent).Append("    double ");
            for (int k = 0; k < 8; k++)
            {
                if (k > 0) _prelude.Append(',');
                _prelude.Append("e").Append(k).Append("im_").Append(ns).Append("=0");
            }
            _prelude.Append(";\n");
        }
        _prelude.Append(_indent).Append("    double ");
        for (int k = 0; k < 8; k++)
        {
            if (k > 0) _prelude.Append(',');
            _prelude.Append("r").Append(k).Append('_').Append(ns);
        }
        for (int k = 0; k < 8; k++)
        {
            _prelude.Append(',').Append("i").Append(k).Append('_').Append(ns);
        }
        _prelude.Append(";\n");
        for (int k = 0; k < 8; k++)
            _prelude.Append(_indent).Append("    ").Append(
                ScalarPerLane(opName, $"e{k}re_{ns}", $"e{k}im_{ns}", $"r{k}_{ns}", $"i{k}_{ns}"))
                .Append('\n');
        _prelude.Append(_indent).Append("    ").Append(tre).Append(" = Vector512.Create(");
        for (int k = 0; k < 8; k++) { if (k > 0) _prelude.Append(','); _prelude.Append("r").Append(k).Append('_').Append(ns); }
        _prelude.Append(");\n");
        _prelude.Append(_indent).Append("    ").Append(tim).Append(" = Vector512.Create(");
        for (int k = 0; k < 8; k++) { if (k > 0) _prelude.Append(','); _prelude.Append("i").Append(k).Append('_').Append(ns); }
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
        _ => throw new InvalidOperationException($"Avx512DerivEmitter: unknown transcendental {op}"),
    };

    protected override ComplexExpr OpSin(ComplexExpr a) => EmitPerLaneTranscendental(a, "sin");
    protected override ComplexExpr OpCos(ComplexExpr a) => EmitPerLaneTranscendental(a, "cos");
    protected override ComplexExpr OpExp(ComplexExpr a) => EmitPerLaneTranscendental(a, "exp");
    protected override ComplexExpr OpLog(ComplexExpr a) => EmitPerLaneTranscendental(a, "log");

    // Piecewise — mask blend across 8 Vector512 lanes via Avx512F.Compare
    // → Vector512.ConditionalSelect. Both branches eager-evaluated by
    // EmitterBase; prelude expansion runs unconditionally so every lane
    // has both values, blend picks one per lane on the mask.
    protected override ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV)
    {
        string mask = EmitMask(cond);
        string thenIm = thenV.ImZero ? "Vector512<double>.Zero" : thenV.Im;
        string elseIm = elseV.ImZero ? "Vector512<double>.Zero" : elseV.Im;
        return Bind(
            $"Vector512.ConditionalSelect({mask}, {thenV.Re}, {elseV.Re})",
            $"Vector512.ConditionalSelect({mask}, {thenIm}, {elseIm})");
    }

    private string EmitMask(CondNode c)
    {
        if (c is not Cmp cmp)
            throw new InvalidOperationException($"Avx512DerivEmitter: unhandled CondNode {c.GetType().Name}");
        string l = EmitCondTermVec(cmp.Left);
        string r = EmitCondTermVec(cmp.Right);
        // Vector512 compare returns Vector512<double> mask (NaN/zero
        // payload). Ordered → NaN operands compare false (matches C#).
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
        return NewBoundRe($"Avx512F.Compare({l}, {r}, {mode})");
    }

    private string EmitCondTermVec(CondTerm t)
    {
        switch (t)
        {
            case CondRe r:
                return Emit(r.Of).Re;
            case CondIm im:
                var ev = Emit(im.Of);
                return ev.ImZero ? "Vector512<double>.Zero" : ev.Im;
            case CondAbs2 a:
                var av = Emit(a.Of);
                if (av.ImZero)
                    return NewBoundRe($"Avx512F.Multiply({av.Re}, {av.Re})");
                return NewBoundRe(
                    $"Avx512F.FusedMultiplyAdd({av.Re}, {av.Re}, Avx512F.Multiply({av.Im}, {av.Im}))");
            case CondArg ag:
                // atan2 has no AVX-512 intrinsic — per-lane scalarise via
                // the same fallback OpArg-equivalent uses. Discard the
                // imag temp the helper binds; the cond term only consumes
                // the real vector.
                var argv = Emit(ag.Of);
                return EmitPerLaneTranscendental(argv, "arg").Re;
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return NewBoundRe($"Vector512.Create({lit})");
            default:
                throw new InvalidOperationException($"Avx512DerivEmitter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    public string EmitDerivBody(AstNode root)
    {
        var final = Emit(root);
        var sb = new StringBuilder();
        sb.Append(_prelude);
        sb.Append(_indent).Append("Vector512<double> drv_new = ").Append(final.Re).Append(';').Append('\n');
        sb.Append(_indent).Append("Vector512<double> div_new = ").Append(final.Im).Append(';');
        return sb.ToString();
    }
}
