// PerturbDerivEmitter.cs
//
// Emits the dz/dc derivative update inside the perturbation loop.
//
// The recurrence is the same as the scalar derivative path —
//     dz/dc_{n+1} = (∂p/∂z)(z, c) · dz/dc_n + (∂p/∂c)(z, c)
// — but z must be evaluated at the FULL perturbed value z = Z + δ, not at
// the reference orbit Z alone. The surrounding template aliases
// `zr = Zr + dr` / `zi = Zi + di` before invoking the emitted body, so
// this emitter binds Z to those names. dz/dc itself lives in a separate
// pair of locals (drv/div) so the names don't collide with the
// perturbation δ (which already uses dr/di).
//
// Output shape:
//   double drv_new = …;
//   double div_new = …;
// The template then assigns drv/div from drv_new/div_new.

using System.Globalization;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.CalculatorGen.Emitters;

public sealed class PerturbDerivEmitter : EmitterBase
{
    protected override string ZRe => "zr";
    protected override string ZIm => "zi";
    protected override string CRe => "Cr";
    protected override string CIm => "Ci";
    protected override string DRe => "drv";
    protected override string DIm => "div";

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
        // Same closed form as ScalarEmitter — needed because dz/dc
        // chain rule for log(u) introduces u'/u into the derivative
        // tree even when the original equation has no Div node.
        if (a.ImZero && b.ImZero)
            return new($"({a.Re} / {b.Re})", "0.0", ImZero: true);
        if (b.ImZero)
            return new($"({a.Re} / {b.Re})", $"({a.Im} / {b.Re})", ImZero: false);
        string d = $"({b.Re} * {b.Re} + {b.Im} * {b.Im})";
        if (a.ImZero)
            return new($"({a.Re} * {b.Re} / {d})", $"(-({a.Re} * {b.Im}) / {d})", ImZero: false);
        return new(
            $"(({a.Re} * {b.Re} + {a.Im} * {b.Im}) / {d})",
            $"(({a.Im} * {b.Re} - {a.Re} * {b.Im}) / {d})",
            ImZero: false);
    }

    // Transcendentals appear in derivative trees (∂sin(u)/∂z =
    // cos(u)·u') even when the original equation doesn't disable
    // perturbation — but item 14 hits this branch only via
    // SupportsDe=true, SupportsPerturbation=false equations whose
    // derivative emitter is wired anyway via the dz/dc chain. Inline
    // scalar identities mirror ScalarEmitter.
    protected override ComplexExpr OpSin(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Sin({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Sin({a.Re}) * Math.Cosh({a.Im}))",
            $"(Math.Cos({a.Re}) * Math.Sinh({a.Im}))", ImZero: false);
    }
    protected override ComplexExpr OpCos(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Cos({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Cos({a.Re}) * Math.Cosh({a.Im}))",
            $"(-(Math.Sin({a.Re}) * Math.Sinh({a.Im})))", ImZero: false);
    }
    protected override ComplexExpr OpExp(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Exp({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Exp({a.Re}) * Math.Cos({a.Im}))",
            $"(Math.Exp({a.Re}) * Math.Sin({a.Im}))", ImZero: false);
    }
    protected override ComplexExpr OpLog(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Log({a.Re})", "0.0", ImZero: true);
        return new(
            $"(0.5 * Math.Log({a.Re} * {a.Re} + {a.Im} * {a.Im}))",
            $"Math.Atan2({a.Im}, {a.Re})", ImZero: false);
    }

    public string EmitDerivBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}double drv_new = {e.Re};\n" +
            $"{indent}double div_new = {e.Im};";
    }
}
