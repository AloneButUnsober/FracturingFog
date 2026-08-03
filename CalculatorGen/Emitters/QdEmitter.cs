// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
    protected override string PrevRe => "pr_q";
    protected override string PrevIm => "pi_q";
    protected override string IterRe => "iter_q";
    protected override string IterImLiteral => "(QD)0.0";

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
        // QD has no built-in abs; reach into Hi via explicit conversion
        // and rebuild a positive QD by negating when Hi < 0. Cheap pattern:
        // sign-flip the whole DD/QD via `x.X0 < 0 ? -x : x`.
        string reAbs = $"({a.Re}.X0 < 0.0 ? -({a.Re}) : ({a.Re}))";
        if (a.ImZero) return new(reAbs, "0.0", ImZero: true);
        string imAbs = $"({a.Im}.X0 < 0.0 ? -({a.Im}) : ({a.Im}))";
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
    // double via .X0, compute, promote result back. Precision degrades
    // to ~16 decimal digits inside the transcendental call; the
    // surrounding QD chain preserves precision for + − ×. Acceptable
    // for hot-load capability — deep-zoom precision around sin/cos/etc
    // is a Phase D-4+ extension (DD/QD transcendental library).
    private static ComplexExpr ScalarComplex(ComplexExpr a, string opName)
    {
        string re = a.ImZero ? $"{a.Re}.X0" : $"{a.Re}.X0";
        string im = a.ImZero ? "0.0" : $"{a.Im}.X0";
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
            _ => throw new InvalidOperationException($"QdEmitter: unknown transcendental {opName}"),
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
    protected override ComplexExpr OpArg(ComplexExpr a) =>
        new($"(QD)Math.Atan2({(a.ImZero ? "0.0" : $"((QD)({a.Im})).X0")}, ((QD)({a.Re})).X0)", "(QD)0.0", ImZero: true);
    protected override ComplexExpr OpAtan2(ComplexExpr y, ComplexExpr x) =>
        new($"(QD)Math.Atan2(((QD)({y.Re})).X0, ((QD)({x.Re})).X0)", "(QD)0.0", ImZero: true);
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

    // Piecewise — compare on QD .X0 (high limb), select via C# ternary.
    // Eager-evaluated branches.
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
        _ => throw new InvalidOperationException($"QdEmitter: unhandled CondNode {c.GetType().Name}"),
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
                // arg(x) inside a QD cond. atan2 has no QD precision —
                // collapse to plain double on .X0 limbs (same trade-off
                // as QD OpLog's imaginary part). Operands wrapped in
                // ((QD)x).X0 so bare-double RealConst limbs normalise.
                var agv = Emit(ag.Of);
                string agRe = $"((QD)({agv.Re})).X0";
                string agIm = agv.ImZero ? "0.0" : $"((QD)({agv.Im})).X0";
                return $"Math.Atan2({agIm}, {agRe})";
            case CondConst k:
                string lit = k.Value.ToString("R", CultureInfo.InvariantCulture);
                if (!lit.Contains('.') && !lit.Contains('e') && !lit.Contains('E')) lit += ".0";
                return lit;
            default:
                throw new InvalidOperationException($"QdEmitter: unhandled CondTerm {t.GetType().Name}");
        }
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
