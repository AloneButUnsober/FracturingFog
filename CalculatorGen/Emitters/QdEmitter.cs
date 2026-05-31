// QdEmitter.cs
//
// Emits the per-iteration step body in QuadDouble (QD) arithmetic.
//
// Used by the perturbation deep-zoom path to iterate the reference
// orbit in quad-precision (~62 decimal digits, valid to zoom ~1e50)
// while leaving per-pixel δ in plain doubles. The emitted strings
// reference QD-typed locals; the QD struct's operators provide the
// hardware-FMA-backed arithmetic.
//
// Variable bindings:
//   ZRef → (zr, zi)   QD complex iterate
//   CRef → (Cr, Ci)   QD complex view-centre
//   DRef/DeltaRef/EpsRef are not used in the reference orbit; the
//   emitter throws if they appear (defensive — they shouldn't).
//
// Imag-zero optimisation is preserved: real-only subexpressions
// drop their dead-zero Im terms.
//
// Output shape:
//   QD zr_new = …;
//   QD zi_new = …;
// The template assigns back to zr/zi after the line.

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class QdEmitter : EmitterBase
{
    // Bindings deliberately differ from ScalarEmitter so QD-body emission
    // can live in the same lexical scope as the outer double-precision
    // method without C# CS0136 "name reused" complaints. The template
    // declares `QD zr_q, zi_q, CrQd, CiQd` locals.
    protected override string ZRe => "zr_q";
    protected override string ZIm => "zi_q";
    protected override string CRe => "CrQd";
    protected override string CIm => "CiQd";
    protected override string DRe => throw new InvalidOperationException("DRef unsupported in QD reference orbit");
    protected override string DIm => throw new InvalidOperationException("DRef unsupported in QD reference orbit");

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        // QD has implicit operator QD(double), so a bare literal is fine in arithmetic.
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

    /// <summary>Emit `QD zr_q_new = …; QD zi_q_new = …;`.</summary>
    public string EmitQdBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}QD zr_q_new = {e.Re};\n" +
            $"{indent}QD zi_q_new = {e.Im};";
    }
}
