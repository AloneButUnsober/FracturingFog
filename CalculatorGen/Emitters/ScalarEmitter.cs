// ScalarEmitter.cs
//
// Emits a single-iteration update body in scalar `double` arithmetic.
// Each call to EmitNewValueBody walks the AST once and returns two
// new-value temp declarations:
//
//   double <prefix>r_new = …;
//   double <prefix>i_new = …;
//
// The surrounding template chooses when to commit them back to the
// running state (zr/zi or dr/di), which keeps z-update and derivative-
// update independent and lets them be ordered freely (both depend only
// on pre-step state).
//
// Imag-zero optimisation
//   When a subexpression's imaginary part is provably zero
//   (RealConst inputs, or arithmetic chains of real-only inputs) we
//   collapse the dead-zero terms in the emitted multiply / add code.
//   This roughly halves the work in d-bodies dominated by real
//   coefficients (e.g. dz/dc for z^n + c has a 'n · z^(n-1)' real
//   coefficient).

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class ScalarEmitter : EmitterBase
{
    protected override string ZRe => "zr";
    protected override string ZIm => "zi";
    protected override string CRe => "cr";
    protected override string CIm => "ci";
    protected override string DRe => "dr";
    protected override string DIm => "di";

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
        if (a.ImZero) // a is purely real
            return new($"({a.Re} * {b.Re})", $"({a.Re} * {b.Im})", ImZero: false);
        if (b.ImZero) // b is purely real
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

    /// <summary>Render `double <prefix>r_new = …; double <prefix>i_new = …;`.</summary>
    public string EmitNewValueBody(AstNode root, string prefix, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}double {prefix}r_new = {e.Re};\n" +
            $"{indent}double {prefix}i_new = {e.Im};";
    }
}
