// PerturbationEmitter.cs
//
// Emits the per-iteration δ update for the Tier 4 perturbation path.
//
// Variable bindings (different from ScalarEmitter because Z/C now mean
// reference orbit and view centre, with per-pixel offsets δ and ε):
//
//   ZRef    → (Zr, Zi)   reference orbit iterate
//   CRef    → (Cr, Ci)   view-centre c
//   DeltaRef → (dr, di)  per-pixel δ
//   EpsRef   → (er, ei)  per-pixel ε
//
// Output shape mirrors ScalarEmitter — two assignments:
//   double dr_new = …;
//   double di_new = …;
// The surrounding template stores them back into dr/di after the line.
//
// Imag-zero optimisation carries over from EmitterBase: real constants
// fold through Mul/Add without dragging zero-imag terms.

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class PerturbationEmitter : EmitterBase
{
    protected override string ZRe     => "Zr";
    protected override string ZIm     => "Zi";
    protected override string CRe     => "Cr";
    protected override string CIm     => "Ci";
    protected override string DRe     => throw new InvalidOperationException("DRef unsupported in perturbation");
    protected override string DIm     => throw new InvalidOperationException("DRef unsupported in perturbation");
    protected override string DeltaRe => "dr";
    protected override string DeltaIm => "di";
    protected override string EpsRe   => "er";
    protected override string EpsIm   => "ei";

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

    public string EmitDeltaBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}double dr_new = {e.Re};\n" +
            $"{indent}double di_new = {e.Im};";
    }
}
