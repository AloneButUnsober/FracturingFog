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
    protected override string PrevRe => "pr";
    protected override string PrevIm => "pi";
    protected override string IterRe => "iter";

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

    // (a + bi)/(c + di) = ((ac + bd) + (bc − ad)i) / (c² + d²)
    protected override ComplexExpr OpDiv(ComplexExpr a, ComplexExpr b)
    {
        // Both real: simple division.
        if (a.ImZero && b.ImZero)
            return new($"({a.Re} / {b.Re})", "0.0", ImZero: true);
        // Real numerator over complex denominator.
        if (a.ImZero)
        {
            string denom = $"({b.Re} * {b.Re} + {b.Im} * {b.Im})";
            return new(
                $"({a.Re} * {b.Re} / {denom})",
                $"(-({a.Re} * {b.Im}) / {denom})",
                ImZero: false);
        }
        // Complex over real.
        if (b.ImZero)
            return new($"({a.Re} / {b.Re})", $"({a.Im} / {b.Re})", ImZero: false);
        // Full complex division.
        string d = $"({b.Re} * {b.Re} + {b.Im} * {b.Im})";
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
        string im = a.ImZero ? "0.0" : $"Math.Abs({a.Im})";
        return new($"Math.Abs({a.Re})", im, a.ImZero);
    }

    // sin(a+bi) = sin(a)·cosh(b) + i·cos(a)·sinh(b).
    // When b is zero, collapses to (sin(a), 0). The same imag-zero
    // optimisation as other ops — no temp local needed; complex
    // emitters that need shared subexpression reuse can hoist the
    // sin/cos pair via the substitution comment in the generated body.
    protected override ComplexExpr OpSin(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Sin({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Sin({a.Re}) * Math.Cosh({a.Im}))",
            $"(Math.Cos({a.Re}) * Math.Sinh({a.Im}))",
            ImZero: false);
    }

    // cos(a+bi) = cos(a)·cosh(b) − i·sin(a)·sinh(b).
    protected override ComplexExpr OpCos(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Cos({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Cos({a.Re}) * Math.Cosh({a.Im}))",
            $"(-(Math.Sin({a.Re}) * Math.Sinh({a.Im})))",
            ImZero: false);
    }

    // exp(a+bi) = e^a · (cos(b) + i·sin(b)).
    protected override ComplexExpr OpExp(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Exp({a.Re})", "0.0", ImZero: true);
        return new(
            $"(Math.Exp({a.Re}) * Math.Cos({a.Im}))",
            $"(Math.Exp({a.Re}) * Math.Sin({a.Im}))",
            ImZero: false);
    }

    // log(a+bi) = (1/2)·log(a²+b²) + i·atan2(b, a).
    // Pole at (0, 0): emitted code produces -Inf real / NaN imag —
    // calculator's bailout/escape check filters those pixels.
    protected override ComplexExpr OpLog(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"Math.Log({a.Re})", "0.0", ImZero: true);
        return new(
            $"(0.5 * Math.Log({a.Re} * {a.Re} + {a.Im} * {a.Im}))",
            $"Math.Atan2({a.Im}, {a.Re})",
            ImZero: false);
    }

    // arg(a+bi) = atan2(b, a) ∈ (-π, π]. Lift to complex (arg, 0). When
    // the input has ImZero, the angle is 0 (positive a) or π (negative a)
    // — emit Math.Atan2(0, a) so the sign of a still picks the right
    // branch instead of just emitting "0.0".
    protected override ComplexExpr OpArg(ComplexExpr a) =>
        new($"Math.Atan2({(a.ImZero ? "0.0" : a.Im)}, {a.Re})", "0.0", ImZero: true);

    // atan2(y, x) lifted to complex (atan2, 0). The complex inputs are
    // expected to be real-lifted (ImZero=true) — the imaginary parts of
    // y and x are dropped; only their real components feed atan2. This
    // matches mathematical atan2(y_real, x_real); users passing genuinely
    // complex y/x should rewrite via re()/im() to make intent explicit.
    protected override ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        new($"Math.Atan2({y.Re}, {x.Re})", "0.0", ImZero: true);

    // min / max / mod operate on the real parts of complex inputs (imag
    // discarded). Lifted back to complex as (result, 0).
    protected override ComplexExpr OpMin(ComplexExpr a, ComplexExpr b) =>
        new($"Math.Min({a.Re}, {b.Re})", "0.0", ImZero: true);

    protected override ComplexExpr OpMax(ComplexExpr a, ComplexExpr b) =>
        new($"Math.Max({a.Re}, {b.Re})", "0.0", ImZero: true);

    protected override ComplexExpr OpMod(ComplexExpr a, ComplexExpr b) =>
        new($"(({a.Re}) % ({b.Re}))", "0.0", ImZero: true);

    // Piecewise selection — scalar C# ternary on the rendered cond
    // expression. Both branches were eager-evaluated by EmitterBase so
    // any Sin/Cos/etc work has been folded into the ComplexExpr inline
    // strings; the runtime ternary picks one. ImZero requires BOTH
    // branches to be ImZero — otherwise the imaginary part might be
    // non-zero on the unselected branch.
    protected override ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV)
    {
        string c = RenderCond(cond);
        string re = $"({c} ? {thenV.Re} : {elseV.Re})";
        bool bothZero = thenV.ImZero && elseV.ImZero;
        string im = bothZero ? "0.0"
                  : thenV.ImZero ? $"({c} ? 0.0 : {elseV.Im})"
                  : elseV.ImZero ? $"({c} ? {thenV.Im} : 0.0)"
                  : $"({c} ? {thenV.Im} : {elseV.Im})";
        return new ComplexExpr(re, im, bothZero);
    }

    private string RenderCond(CondNode c) => c switch
    {
        Cmp cmp => $"({RenderCondTerm(cmp.Left)} {CmpOpString(cmp.Op)} {RenderCondTerm(cmp.Right)})",
        _ => throw new InvalidOperationException($"ScalarEmitter: unhandled CondNode {c.GetType().Name}"),
    };

    private static string CmpOpString(CmpOp op) => op switch
    {
        CmpOp.Gt => ">",
        CmpOp.Lt => "<",
        CmpOp.Ge => ">=",
        CmpOp.Le => "<=",
        CmpOp.Eq => "==",
        CmpOp.Ne => "!=",
        _ => throw new InvalidOperationException($"Unknown CmpOp {op}"),
    };

    private string RenderCondTerm(CondTerm t)
    {
        switch (t)
        {
            case CondRe r:
                return Emit(r.Of).Re;
            case CondIm im:
                var ev = Emit(im.Of);
                return ev.ImZero ? "0.0" : ev.Im;
            case CondAbs2 a:
                var av = Emit(a.Of);
                string reSq = $"({av.Re} * {av.Re})";
                if (av.ImZero) return reSq;
                return $"({reSq} + {av.Im} * {av.Im})";
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return lit;
            default:
                throw new InvalidOperationException($"ScalarEmitter: unhandled CondTerm {t.GetType().Name}");
        }
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
