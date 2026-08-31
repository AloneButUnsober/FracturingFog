// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
    protected override string PrevRe => "pr_dd";
    protected override string PrevIm => "pi_dd";
    protected override string IterRe => "iter_dd";
    protected override string IterImLiteral => "(DD)0.0";

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
        string reAbs = $"(((DD)({a.Re})).Hi < 0.0 ? -({a.Re}) : ({a.Re}))";
        if (a.ImZero) return new(reAbs, "0.0", ImZero: true);
        string imAbs = $"(((DD)({a.Im})).Hi < 0.0 ? -({a.Im}) : ({a.Im}))";
        return new(reAbs, imAbs, ImZero: false);
    }

    // Per-component real functions — degrade to double on the .Hi limb (DD has
    // no floor/round/…), apply independently to Re and Im, preserve ImZero.
    private static ComplexExpr PerComp(ComplexExpr a, Func<string, string> f)
    {
        string re = $"(DD)({f($"((DD)({a.Re})).Hi")})";
        if (a.ImZero) return new(re, "(DD)0.0", ImZero: true);
        string im = $"(DD)({f($"((DD)({a.Im})).Hi")})";
        return new(re, im, ImZero: false);
    }
    protected override ComplexExpr OpFloor(ComplexExpr a) => PerComp(a, s => $"Math.Floor({s})");
    protected override ComplexExpr OpRound(ComplexExpr a) => PerComp(a, s => $"Math.Round({s})");
    protected override ComplexExpr OpCeil(ComplexExpr a)  => PerComp(a, s => $"Math.Ceiling({s})");
    protected override ComplexExpr OpTrunc(ComplexExpr a) => PerComp(a, s => $"Math.Truncate({s})");
    protected override ComplexExpr OpFract(ComplexExpr a) => PerComp(a, s => $"(({s}) - Math.Floor({s}))");
    protected override ComplexExpr OpSign(ComplexExpr a)  => PerComp(a, s => $"(double)Math.Sign({s})");

    // Transcendentals: DD library lacks sin/cos/exp/log. Promote
    // .Hi → double, compute, demote back. Same degradation tradeoff
    // as QdEmitter — accuracy ~16 digits inside the call.
    private static ComplexExpr ScalarComplex(ComplexExpr a, string opName)
    {
        string re = $"((DD)({a.Re})).Hi";
        string im = a.ImZero ? "0.0" : $"((DD)({a.Im})).Hi";
        // Inverse trig / hyperbolic degrade to double inside the Complex call
        // (same trade-off as sin/exp/log). Demote the (Real, Imaginary) back to DD.
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
            return new($"(DD)({ex}).Real", $"(DD)({ex}).Imaginary", ImZero: false);
        }
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
    protected override ComplexExpr OpAsin(ComplexExpr a)  => ScalarComplex(a, "asin");
    protected override ComplexExpr OpAcos(ComplexExpr a)  => ScalarComplex(a, "acos");
    protected override ComplexExpr OpAtan(ComplexExpr a)  => ScalarComplex(a, "atan");
    protected override ComplexExpr OpAsinh(ComplexExpr a) => ScalarComplex(a, "asinh");
    protected override ComplexExpr OpAcosh(ComplexExpr a) => ScalarComplex(a, "acosh");
    protected override ComplexExpr OpAtanh(ComplexExpr a) => ScalarComplex(a, "atanh");
    // arg / atan2 return a real angle; precision degrades to plain double
    // inside the atan2 call (same as the imag-part of OpLog). DD has no
    // implicit double cast — extract .Hi explicitly to feed Math.Atan2.
    protected override ComplexExpr OpArg(ComplexExpr a) =>
        new($"(DD)Math.Atan2({(a.ImZero ? "0.0" : $"((DD)({a.Im})).Hi")}, ((DD)({a.Re})).Hi)", "(DD)0.0", ImZero: true);
    protected override ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        new($"(DD)Math.Atan2(((DD)({y.Re})).Hi, ((DD)({x.Re})).Hi)", "(DD)0.0", ImZero: true);

    // min/max emit DD-aware ternaries on the .Hi limb (matches OpIf's
    // comparison strategy). mod scalarises through double — DD precision
    // doesn't survive the %, same trade-off as atan2 above. Operands
    // wrapped in ((DD)x).Hi so RealConst-emitted double literals work.
    protected override ComplexExpr OpMin(ComplexExpr a, ComplexExpr b) =>
        new($"(((DD)({a.Re})).Hi <= ((DD)({b.Re})).Hi ? ((DD)({a.Re})) : ((DD)({b.Re})))", "(DD)0.0", ImZero: true);
    protected override ComplexExpr OpMax(ComplexExpr a, ComplexExpr b) =>
        new($"(((DD)({a.Re})).Hi >= ((DD)({b.Re})).Hi ? ((DD)({a.Re})) : ((DD)({b.Re})))", "(DD)0.0", ImZero: true);
    protected override ComplexExpr OpMod(ComplexExpr a, ComplexExpr b) =>
        new($"(DD)(((DD)({a.Re})).Hi % ((DD)({b.Re})).Hi)", "(DD)0.0", ImZero: true);

    // #27 Phase 6 — real-lift parity ops. re/im keep full DD; abs/clamp compare
    // on the high limb (.Hi), matching the atan2/min/max degradation pattern.
    protected override ComplexExpr OpRe(ComplexExpr a) =>
        new($"((DD)({a.Re}))", "(DD)0.0", ImZero: true);

    protected override ComplexExpr OpIm(ComplexExpr a) =>
        new(a.ImZero ? "(DD)0.0" : $"((DD)({a.Im}))", "(DD)0.0", ImZero: true);

    // abs(x) = |x|. Real input keeps full DD via sign-flip; complex magnitude
    // degrades to double inside the sqrt (same trade-off as OpArg/OpMod).
    protected override ComplexExpr OpAbs(ComplexExpr a)
    {
        if (a.ImZero)
            return new($"(((DD)({a.Re})).Hi < 0.0 ? -((DD)({a.Re})) : ((DD)({a.Re})))", "(DD)0.0", ImZero: true);
        string reHi = $"((DD)({a.Re})).Hi";
        string imHi = $"((DD)({a.Im})).Hi";
        return new($"(DD)Math.Sqrt({reHi} * {reHi} + {imHi} * {imHi})", "(DD)0.0", ImZero: true);
    }

    protected override ComplexExpr OpClamp(ComplexExpr x, ComplexExpr lo, ComplexExpr hi) =>
        new($"(((DD)({x.Re})).Hi < ((DD)({lo.Re})).Hi ? ((DD)({lo.Re})) : " +
            $"(((DD)({x.Re})).Hi > ((DD)({hi.Re})).Hi ? ((DD)({hi.Re})) : ((DD)({x.Re}))))",
            "(DD)0.0", ImZero: true);

    // pow(base, exp): transcendental — degrade to double on the .Hi limb
    // (same trade-off as sin/exp/log/abs). Both-real → Math.Pow; else
    // Complex.Pow (zero-guarded principal branch). Result demoted back to DD.
    protected override ComplexExpr OpPow(ComplexExpr a, ComplexExpr b)
    {
        string aRe = $"((DD)({a.Re})).Hi";
        string bRe = $"((DD)({b.Re})).Hi";
        if (a.ImZero && b.ImZero)
            return new($"(DD)Math.Pow({aRe}, {bRe})", "(DD)0.0", ImZero: true);
        string aIm = a.ImZero ? "0.0" : $"((DD)({a.Im})).Hi";
        string bIm = b.ImZero ? "0.0" : $"((DD)({b.Im})).Hi";
        string pw = $"System.Numerics.Complex.Pow(new System.Numerics.Complex({aRe}, {aIm}), " +
                    $"new System.Numerics.Complex({bRe}, {bIm}))";
        return new($"(DD)({pw}).Real", $"(DD)({pw}).Imaginary", ImZero: false);
    }

    // Piecewise: condition compares DD values via .Hi (high-double).
    // Sufficient for typical Mandelbrot-style thresholds — the
    // boundary locus is itself measure-zero, so the low-double
    // contribution to the comparison rarely matters. Branches are
    // selected by C# ternary on a DD expression; both branches were
    // eager-evaluated so any Math.Sin/etc work already ran.
    protected override ComplexExpr OpIf(CondNode cond, ComplexExpr thenV, ComplexExpr elseV)
    {
        string c = RenderCond(cond);
        bool bothZero = thenV.ImZero && elseV.ImZero;
        string re = $"({c} ? ({thenV.Re}) : ({elseV.Re}))";
        string im = bothZero ? "0.0"
                  : thenV.ImZero ? $"({c} ? (DD)0.0 : ({elseV.Im}))"
                  : elseV.ImZero ? $"({c} ? ({thenV.Im}) : (DD)0.0)"
                  : $"({c} ? ({thenV.Im}) : ({elseV.Im}))";
        return new ComplexExpr(re, im, bothZero);
    }

    private string RenderCond(CondNode c) => c switch
    {
        Cmp cmp => $"({RenderCondTerm(cmp.Left)} {CmpOpString(cmp.Op)} {RenderCondTerm(cmp.Right)})",
        _ => throw new InvalidOperationException($"DdDirectEmitter: unhandled CondNode {c.GetType().Name}"),
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
                return $"({Emit(r.Of).Re}).Hi";
            case CondIm im:
                var ev = Emit(im.Of);
                return ev.ImZero ? "0.0" : $"({ev.Im}).Hi";
            case CondAbs2 a:
                var av = Emit(a.Of);
                string reSq = $"(({av.Re}).Hi * ({av.Re}).Hi)";
                if (av.ImZero) return reSq;
                return $"({reSq} + ({av.Im}).Hi * ({av.Im}).Hi)";
            case CondArg ag:
                var agv = Emit(ag.Of);
                string agRe = $"((DD)({agv.Re})).Hi";
                string agIm = agv.ImZero ? "0.0" : $"((DD)({agv.Im})).Hi";
                return $"Math.Atan2({agIm}, {agRe})";
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return lit;
            default:
                throw new InvalidOperationException($"DdDirectEmitter: unhandled CondTerm {t.GetType().Name}");
        }
    }

    public string EmitDdDirectBody(AstNode root, string indent)
    {
        var e = Emit(root);
        return
            $"{indent}DD zr_dd_new = {e.Re};\n" +
            $"{indent}DD zi_dd_new = {e.Im};";
    }
}
