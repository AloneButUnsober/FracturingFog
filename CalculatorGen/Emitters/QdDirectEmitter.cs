// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
    protected override string PrevRe => "pr_q";
    protected override string PrevIm => "pi_q";
    protected override string IterRe => "iter_q";
    protected override string IterImLiteral => "(QD)0.0";

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
        string reAbs = $"(((QD)({a.Re})).X0 < 0.0 ? -({a.Re}) : ({a.Re}))";
        if (a.ImZero) return new(reAbs, "0.0", ImZero: true);
        string imAbs = $"(((QD)({a.Im})).X0 < 0.0 ? -({a.Im}) : ({a.Im}))";
        return new(reAbs, imAbs, ImZero: false);
    }

    // Per-component real functions — degrade to double on the .X0 limb (QD has
    // no floor/round/…), apply independently to Re and Im, preserve ImZero.
    private static ComplexExpr PerComp(ComplexExpr a, Func<string, string> f)
    {
        string re = $"(QD)({f($"((QD)({a.Re})).X0")})";
        if (a.ImZero) return new(re, "(QD)0.0", ImZero: true);
        string im = $"(QD)({f($"((QD)({a.Im})).X0")})";
        return new(re, im, ImZero: false);
    }
    protected override ComplexExpr OpFloor(ComplexExpr a) => PerComp(a, s => $"Math.Floor({s})");
    protected override ComplexExpr OpRound(ComplexExpr a) => PerComp(a, s => $"Math.Round({s})");
    protected override ComplexExpr OpCeil(ComplexExpr a)  => PerComp(a, s => $"Math.Ceiling({s})");
    protected override ComplexExpr OpTrunc(ComplexExpr a) => PerComp(a, s => $"Math.Truncate({s})");
    protected override ComplexExpr OpFract(ComplexExpr a) => PerComp(a, s => $"(({s}) - Math.Floor({s}))");
    protected override ComplexExpr OpSign(ComplexExpr a)  => PerComp(a, s => $"(double)Math.Sign({s})");

    // Transcendentals: QD library lacks sin/cos/exp/log. Promote to
    // double via .X0, compute, demote back. Same degradation tradeoff
    // as QdEmitter — accuracy ~16 digits inside the transcendental call.
    private static ComplexExpr ScalarComplex(ComplexExpr a, string opName)
    {
        string re = $"((QD)({a.Re})).X0";
        string im = a.ImZero ? "0.0" : $"((QD)({a.Im})).X0";
        // Inverse trig / hyperbolic degrade to double inside the Complex call
        // (same trade-off as sin/exp/log). Demote (Real, Imaginary) back to QD.
        if (opName is "asin" or "acos" or "atan" or "asinh" or "acosh" or "atanh")
        {
            string z = $"new System.Numerics.Complex({re}, {im})";
            string ex = opName switch
            {
                "asin"  => $"System.Numerics.Complex.Asin({z})",
                "acos"  => $"System.Numerics.Complex.Acos({z})",
                "atan"  => $"System.Numerics.Complex.Atan({z})",
                "asinh" => $"System.Numerics.Complex.Log({z} + System.Numerics.Complex.Sqrt({z} * {z} + System.Numerics.Complex.One))",
                "acosh" => $"System.Numerics.Complex.Log({z} + System.Numerics.Complex.Sqrt({z} * {z} - System.Numerics.Complex.One))",
                _       => $"(0.5 * System.Numerics.Complex.Log((System.Numerics.Complex.One + {z}) / (System.Numerics.Complex.One - {z})))",
            };
            return new($"(QD)({ex}).Real", $"(QD)({ex}).Imaginary", ImZero: false);
        }
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
    protected override ComplexExpr OpAsin(ComplexExpr a)  => ScalarComplex(a, "asin");
    protected override ComplexExpr OpAcos(ComplexExpr a)  => ScalarComplex(a, "acos");
    protected override ComplexExpr OpAtan(ComplexExpr a)  => ScalarComplex(a, "atan");
    protected override ComplexExpr OpAsinh(ComplexExpr a) => ScalarComplex(a, "asinh");
    protected override ComplexExpr OpAcosh(ComplexExpr a) => ScalarComplex(a, "acosh");
    protected override ComplexExpr OpAtanh(ComplexExpr a) => ScalarComplex(a, "atanh");
    // arg / atan2 degrade to double inside the atan2 call (same pattern
    // as OpLog's imag part). Wrap each operand in ((QD)x).X0 — handles
    // both QD-typed expressions and bare double literals from Const.
    protected override ComplexExpr OpArg(ComplexExpr a) =>
        new($"(QD)Math.Atan2({(a.ImZero ? "0.0" : $"((QD)({a.Im})).X0")}, ((QD)({a.Re})).X0)", "(QD)0.0", ImZero: true);
    protected override ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        new($"(QD)Math.Atan2(((QD)({y.Re})).X0, ((QD)({x.Re})).X0)", "(QD)0.0", ImZero: true);

    // QD high limb access via ((QD)x).X0 normalises constants.
    protected override ComplexExpr OpMin(ComplexExpr a, ComplexExpr b) =>
        new($"(((QD)({a.Re})).X0 <= ((QD)({b.Re})).X0 ? ((QD)({a.Re})) : ((QD)({b.Re})))", "(QD)0.0", ImZero: true);
    protected override ComplexExpr OpMax(ComplexExpr a, ComplexExpr b) =>
        new($"(((QD)({a.Re})).X0 >= ((QD)({b.Re})).X0 ? ((QD)({a.Re})) : ((QD)({b.Re})))", "(QD)0.0", ImZero: true);
    protected override ComplexExpr OpMod(ComplexExpr a, ComplexExpr b) =>
        new($"(QD)(((QD)({a.Re})).X0 % ((QD)({b.Re})).X0)", "(QD)0.0", ImZero: true);

    // #27 Phase 6 — real-lift parity ops (re/im keep full QD; abs/clamp on .X0).
    protected override ComplexExpr OpRe(ComplexExpr a) =>
        new($"((QD)({a.Re}))", "(QD)0.0", ImZero: true);

    protected override ComplexExpr OpIm(ComplexExpr a) =>
        new(a.ImZero ? "(QD)0.0" : $"((QD)({a.Im}))", "(QD)0.0", ImZero: true);

    protected override ComplexExpr OpAbs(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"(((QD)({a.Re})).X0 < 0.0 ? -((QD)({a.Re})) : ((QD)({a.Re})))", "(QD)0.0", ImZero: true);
        string reHi = $"((QD)({a.Re})).X0";
        string imHi = $"((QD)({a.Im})).X0";
        return new($"(QD)Math.Sqrt({reHi} * {reHi} + {imHi} * {imHi})", "(QD)0.0", ImZero: true);
    }

    protected override ComplexExpr OpClamp(ComplexExpr x, ComplexExpr lo, ComplexExpr hi) =>
        new($"(((QD)({x.Re})).X0 < ((QD)({lo.Re})).X0 ? ((QD)({lo.Re})) : " +
            $"(((QD)({x.Re})).X0 > ((QD)({hi.Re})).X0 ? ((QD)({hi.Re})) : ((QD)({x.Re}))))",
            "(QD)0.0", ImZero: true);

    // pow(base, exp): transcendental — degrade to double on the .X0 limb
    // (same trade-off as sin/exp/log/abs). Both-real → Math.Pow; else
    // Complex.Pow (zero-guarded principal branch). Result demoted back to QD.
    protected override ComplexExpr OpPow(ComplexExpr a, ComplexExpr b)
    {
        string aRe = $"((QD)({a.Re})).X0";
        string bRe = $"((QD)({b.Re})).X0";
        if (a.ImZero && b.ImZero)
            return new($"(QD)Math.Pow({aRe}, {bRe})", "(QD)0.0", ImZero: true);
        string aIm = a.ImZero ? "0.0" : $"((QD)({a.Im})).X0";
        string bIm = b.ImZero ? "0.0" : $"((QD)({b.Im})).X0";
        string pw = $"System.Numerics.Complex.Pow(new System.Numerics.Complex({aRe}, {aIm}), " +
                    $"new System.Numerics.Complex({bRe}, {bIm}))";
        return new($"(QD)({pw}).Real", $"(QD)({pw}).Imaginary", ImZero: false);
    }

    // Piecewise: condition compares QD values via .X0 (high limb).
    // Branches selected by C# ternary on QD expression; both eager-
    // evaluated.
    protected override ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV)
    {
        string c = RenderCond(cond);
        bool bothZero = thenV.ImZero && elseV.ImZero;
        string re = $"({c} ? ({thenV.Re}) : ({elseV.Re}))";
        string im = bothZero ? "0.0"
                  : thenV.ImZero ? $"({c} ? (QD)0.0 : ({elseV.Im}))"
                  : elseV.ImZero ? $"({c} ? ({thenV.Im}) : (QD)0.0)"
                  : $"({c} ? ({thenV.Im}) : ({elseV.Im}))";
        return new ComplexExpr(re, im, bothZero);
    }

    private string RenderCond(CondNode c) => c switch
    {
        Cmp cmp => $"({RenderCondTerm(cmp.Left)} {CmpOpString(cmp.Op)} {RenderCondTerm(cmp.Right)})",
        _ => throw new InvalidOperationException($"QdDirectEmitter: unhandled CondNode {c.GetType().Name}"),
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
                return $"({Emit(r.Of).Re}).X0";
            case CondIm im:
                var ev = Emit(im.Of);
                return ev.ImZero ? "0.0" : $"({ev.Im}).X0";
            case CondAbs2 a:
                var av = Emit(a.Of);
                string reSq = $"(({av.Re}).X0 * ({av.Re}).X0)";
                if (av.ImZero) return reSq;
                return $"({reSq} + ({av.Im}).X0 * ({av.Im}).X0)";
            case CondArg ag:
                var agv = Emit(ag.Of);
                string agRe = $"((QD)({agv.Re})).X0";
                string agIm = agv.ImZero ? "0.0" : $"((QD)({agv.Im})).X0";
                return $"Math.Atan2({agIm}, {agRe})";
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return lit;
            default:
                throw new InvalidOperationException($"QdDirectEmitter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    public string EmitQdDirectBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}QD zr_q_new = {e.Re};\n" +
            $"{indent}QD zi_q_new = {e.Im};";
    }
}
