// DdEmitter.cs
//
// Emits the per-iteration δ-update body in DoubleDouble (DD) arithmetic.
//
// Used by the perturbation deep-zoom path when Zoom passes the
// DD threshold: per-pixel δ in plain double loses ULPs because adjacent
// pixels' ε values differ by only one bit of double precision. Running
// the δ recurrence in DD (~31 decimal digits) preserves the per-pixel
// signal through the (2·Z·δ + δ² + ε) step.
//
// Variable bindings (different from PerturbationEmitter — these reference
// DD-typed locals so the emitted expression compiles in a DD scope):
//
//   ZRef    → (Zr_dd, Zi_dd)   reference orbit iterate (Hi + Lo)
//   CRef    → (Cr_dd, Ci_dd)   view-centre c (unused in perturbation)
//   DeltaRef → (dr_dd, di_dd)  per-pixel δ
//   EpsRef   → (er_dd, ei_dd)  per-pixel ε
//
// Output shape:
//   DD dr_dd_new = …;
//   DD di_dd_new = …;
// The surrounding template stores them back into dr_dd/di_dd after the line.
//
// Imag-zero optimisation carries over from EmitterBase (DD has implicit
// conversion from double, so a bare literal works in arithmetic).

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class DdEmitter : EmitterBase
{
    protected override string ZRe     => "Zr_dd";
    protected override string ZIm     => "Zi_dd";
    protected override string CRe     => "Cr_dd";
    protected override string CIm     => "Ci_dd";
    protected override string DRe     => throw new InvalidOperationException("DRef unsupported in DD perturbation");
    protected override string DIm     => throw new InvalidOperationException("DRef unsupported in DD perturbation");
    protected override string DeltaRe => "dr_dd";
    protected override string DeltaIm => "di_dd";
    protected override string EpsRe   => "er_dd";
    protected override string EpsIm   => "ei_dd";

    protected override ComplexExpr Const(double v)
    {
        string lit = v.ToString("R", CultureInfo.InvariantCulture);
        if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
        // DD has implicit operator DD(double), so a bare literal is fine.
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

    /// <summary>Emit `DD dr_dd_new = …; DD di_dd_new = …;`.</summary>
    public string EmitDdDeltaBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}DD dr_dd_new = {e.Re};\n" +
            $"{indent}DD di_dd_new = {e.Im};";
    }
}
