// QdDirectEmitter.cs
//
// Emits the per-iteration z-update body in QuadDouble arithmetic for the
// HP-direct per-pixel fallback. Bindings differ from QdEmitter so this
// body can be invoked in a scope where the per-pixel c lives in DD/QD
// locals named cr_q/ci_q rather than CrQd/CiQd.
//
// Variable bindings:
//   ZRef → (zr_q, zi_q)   QD-precision z
//   CRef → (cr_q, ci_q)   QD-precision c (built from view-centre + pixel ε)
//
// Output shape:
//   QD zr_q_new = …;
//   QD zi_q_new = …;

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class QdDirectEmitter : EmitterBase
{
    protected override string ZRe => "zr_q";
    protected override string ZIm => "zi_q";
    protected override string CRe => "cr_q";
    protected override string CIm => "ci_q";
    protected override string DRe => throw new InvalidOperationException("DRef unsupported in QD-direct path");
    protected override string DIm => throw new InvalidOperationException("DRef unsupported in QD-direct path");

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        return new ComplexExpr(lit, "0.0", ImZero: true);
    }

    protected override ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b)
    {
        bool bothZero = a.ImZero && b.ImZero;
        string im = bothZero ? "0.0"
                  : a.ImZero  ? b.Im
                  : b.ImZero  ? a.Im
                  : $"({a.Im} + {b.Im})";
        return new($"({a.Re} + {b.Re})", im, bothZero);
    }

    protected override ComplexExpr OpSub(ComplexExpr a, ComplexExpr b)
    {
        bool bothZero = a.ImZero && b.ImZero;
        string im = bothZero ? "0.0"
                  : a.ImZero  ? $"(-{b.Im})"
                  : b.ImZero  ? a.Im
                  : $"({a.Im} - {b.Im})";
        return new($"({a.Re} - {b.Re})", im, bothZero);
    }

    protected override ComplexExpr OpMul(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero)
            return new($"({a.Re} * {b.Re})", "0.0", ImZero: true);
        if (a.ImZero)
            return new($"({a.Re} * {b.Re})", $"({a.Re} * {b.Im})", ImZero: false);
        if (b.ImZero)
            return new($"({a.Re} * {b.Re})", $"({a.Im} * {b.Re})", ImZero: false);
        return new($"({a.Re} * {b.Re} - {a.Im} * {b.Im})",
                   $"({a.Re} * {b.Im} + {a.Im} * {b.Re})",
                   ImZero: false);
    }

    protected override ComplexExpr OpNeg(ComplexExpr a)
    {
        string im = a.ImZero ? "0.0" : $"(-{a.Im})";
        return new($"(-{a.Re})", im, a.ImZero);
    }

    protected override ComplexExpr OpDiv(ComplexExpr a, ComplexExpr b)
    {
        if (a.ImZero && b.ImZero) return new($"({a.Re} / {b.Re})", "0.0", ImZero: true);
        if (b.ImZero) return new($"({a.Re} / {b.Re})", $"({a.Im} / {b.Re})", ImZero: false);
        string d = $"({b.Re} * {b.Re} + {b.Im} * {b.Im})";
        if (a.ImZero)
            return new($"({a.Re} * {b.Re} / {d})", $"(-({a.Re} * {b.Im}) / {d})", ImZero: false);
        return new(
            $"(({a.Re} * {b.Re} + {a.Im} * {b.Im}) / {d})",
            $"(({a.Im} * {b.Re} - {a.Re} * {b.Im}) / {d})",
            ImZero: false);
    }

    protected override ComplexExpr OpConj(ComplexExpr a)
    {
        string im = a.ImZero ? "0.0" : $"(-{a.Im})";
        return new(a.Re, im, a.ImZero);
    }

    protected override ComplexExpr OpFold(ComplexExpr a)
    {
        string reAbs = $"({a.Re}.X0 < 0.0 ? -({a.Re}) : ({a.Re}))";
        if (a.ImZero) return new(reAbs, "0.0", ImZero: true);
        string imAbs = $"({a.Im}.X0 < 0.0 ? -({a.Im}) : ({a.Im}))";
        return new(reAbs, imAbs, ImZero: false);
    }

    // Transcendentals: QD library lacks sin/cos/exp/log. Promote to
    // double via .X0, compute, demote back. Same degradation tradeoff
    // as QdEmitter — accuracy ~16 digits inside the transcendental call.
    private static ComplexExpr ScalarComplex(ComplexExpr a, string opName)
    {
        string re = $"{a.Re}.X0";
        string im = a.ImZero ? "0.0" : $"{a.Im}.X0";
        return opName switch
        {
            "sin" => a.ImZero
                ? new($"(QD)Math.Sin({re})", "0.0", ImZero: true)
                : new($"(QD)(Math.Sin({re}) * Math.Cosh({im}))",
                      $"(QD)(Math.Cos({re}) * Math.Sinh({im}))", ImZero: false),
            "cos" => a.ImZero
                ? new($"(QD)Math.Cos({re})", "0.0", ImZero: true)
                : new($"(QD)(Math.Cos({re}) * Math.Cosh({im}))",
                      $"(QD)(-(Math.Sin({re}) * Math.Sinh({im})))", ImZero: false),
            "exp" => a.ImZero
                ? new($"(QD)Math.Exp({re})", "0.0", ImZero: true)
                : new($"(QD)(Math.Exp({re}) * Math.Cos({im}))",
                      $"(QD)(Math.Exp({re}) * Math.Sin({im}))", ImZero: false),
            "log" => a.ImZero
                ? new($"(QD)Math.Log({re})", "0.0", ImZero: true)
                : new($"(QD)(0.5 * Math.Log({re} * {re} + {im} * {im}))",
                      $"(QD)Math.Atan2({im}, {re})", ImZero: false),
            _ => throw new InvalidOperationException($"QdDirectEmitter: unknown transcendental {opName}"),
        };
    }

    protected override ComplexExpr OpSin(ComplexExpr a) => ScalarComplex(a, "sin");
    protected override ComplexExpr OpCos(ComplexExpr a) => ScalarComplex(a, "cos");
    protected override ComplexExpr OpExp(ComplexExpr a) => ScalarComplex(a, "exp");
    protected override ComplexExpr OpLog(ComplexExpr a) => ScalarComplex(a, "log");

    public string EmitQdDirectBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}QD zr_q_new = {e.Re};\n" +
            $"{indent}QD zi_q_new = {e.Im};";
    }
}
