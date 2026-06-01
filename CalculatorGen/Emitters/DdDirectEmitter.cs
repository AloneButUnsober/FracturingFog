// DdDirectEmitter.cs
//
// Emits the per-iteration z-update body in DoubleDouble arithmetic.
//
// Used by the HP-direct per-pixel fallback that runs when the
// perturbation reference orbit escapes early or a per-pixel δ glitches
// against its reference. The body iterates z directly (no perturbation,
// no reference orbit) in DD precision — matches what
// MandelbrotCalculator.ComputePixelHP does for the legacy calculator.
//
// Variable bindings (DD-typed locals in the surrounding scope):
//   ZRef → (zr_dd, zi_dd)   DD-precision z
//   CRef → (cr_dd, ci_dd)   DD-precision c
//
// Output shape:
//   DD zr_dd_new = …;
//   DD zi_dd_new = …;

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class DdDirectEmitter : EmitterBase
{
    protected override string ZRe => "zr_dd";
    protected override string ZIm => "zi_dd";
    protected override string CRe => "cr_dd";
    protected override string CIm => "ci_dd";
    protected override string DRe => throw new InvalidOperationException("DRef unsupported in DD-direct path");
    protected override string DIm => throw new InvalidOperationException("DRef unsupported in DD-direct path");

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
        string reAbs = $"({a.Re}.Hi < 0.0 ? -({a.Re}) : ({a.Re}))";
        if (a.ImZero) return new(reAbs, "0.0", ImZero: true);
        string imAbs = $"({a.Im}.Hi < 0.0 ? -({a.Im}) : ({a.Im}))";
        return new(reAbs, imAbs, ImZero: false);
    }

    // Transcendentals: DD library lacks sin/cos/exp/log. Promote
    // .Hi → double, compute, demote back. Same degradation tradeoff
    // as QdEmitter — accuracy ~16 digits inside the call.
    private static ComplexExpr ScalarComplex(ComplexExpr a, string opName)
    {
        string re = $"{a.Re}.Hi";
        string im = a.ImZero ? "0.0" : $"{a.Im}.Hi";
        return opName switch
        {
            "sin" => a.ImZero
                ? new($"(DD)Math.Sin({re})", "0.0", ImZero: true)
                : new($"(DD)(Math.Sin({re}) * Math.Cosh({im}))",
                      $"(DD)(Math.Cos({re}) * Math.Sinh({im}))", ImZero: false),
            "cos" => a.ImZero
                ? new($"(DD)Math.Cos({re})", "0.0", ImZero: true)
                : new($"(DD)(Math.Cos({re}) * Math.Cosh({im}))",
                      $"(DD)(-(Math.Sin({re}) * Math.Sinh({im})))", ImZero: false),
            "exp" => a.ImZero
                ? new($"(DD)Math.Exp({re})", "0.0", ImZero: true)
                : new($"(DD)(Math.Exp({re}) * Math.Cos({im}))",
                      $"(DD)(Math.Exp({re}) * Math.Sin({im}))", ImZero: false),
            "log" => a.ImZero
                ? new($"(DD)Math.Log({re})", "0.0", ImZero: true)
                : new($"(DD)(0.5 * Math.Log({re} * {re} + {im} * {im}))",
                      $"(DD)Math.Atan2({im}, {re})", ImZero: false),
            _ => throw new InvalidOperationException($"DdDirectEmitter: unknown transcendental {opName}"),
        };
    }

    protected override ComplexExpr OpSin(ComplexExpr a) => ScalarComplex(a, "sin");
    protected override ComplexExpr OpCos(ComplexExpr a) => ScalarComplex(a, "cos");
    protected override ComplexExpr OpExp(ComplexExpr a) => ScalarComplex(a, "exp");
    protected override ComplexExpr OpLog(ComplexExpr a) => ScalarComplex(a, "log");

    public string EmitDdDirectBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}DD zr_dd_new = {e.Re};\n" +
            $"{indent}DD zi_dd_new = {e.Im};";
    }
}
