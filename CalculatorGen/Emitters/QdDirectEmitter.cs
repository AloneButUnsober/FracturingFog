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

    public string EmitQdDirectBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}QD zr_q_new = {e.Re};\n" +
            $"{indent}QD zi_q_new = {e.Im};";
    }
}
