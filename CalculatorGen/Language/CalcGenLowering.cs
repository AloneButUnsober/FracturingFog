// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Language/CalcGenLowering.cs
//
// #1087 (#937 Phase 2) — lower the unified equation language's AST (SbxNode,
// see EquationLanguage) into CalcGen's AST (AstNode) so Compile & Load /
// Generate accept every equation the live interpreter renders, with the
// interpreter's meaning:
//
//   • let / statement bindings  → inlined (the lowered value is shared by
//     reference; the expanded size is capped, see MaxNodes).
//   • ?: / if-then-else / && || ! / comparisons → CalcGen If over a widened
//     condition grammar (Cmp + CondAnd / CondOr / CondNot). A comparison or
//     logical op used as a VALUE becomes If(cond, 1, 0) — the interpreter's
//     real 1 / 0.
//   • ^ with a literal integer exponent 0..64 → Pow (CalcGen's fast integer
//     power); any other exponent → PowC (= SbxVal.Pow: both real → Math.Pow,
//     else Complex.Pow).
//   • tan / sinh / cosh / tanh / sqrt / sqr → the same expansions CalcGen's own
//     parser uses; norm → re² + im².
//
// Real vs complex. The interpreter keeps a value REAL while every input to it
// is real, and its real-valued operations (comparisons, min / max / clamp /
// atan2, the mod period) read a value through AsReal — the value itself when
// real, its MAGNITUDE when complex. CalcGen values are always complex and its
// real-valued ops read the real part. So the lowering tracks each value's kind:
//   Real    → read as is;
//   Complex → read through abs(·) (the magnitude);
//   Unknown → the kind depends on the VALUE (log / sqrt of a real, pow of reals,
//             asin/acos/acosh/atanh of a real, a ternary mixing kinds): CalcGen
//             can't reproduce the interpreter's choice, so such a value is
//             refused where it is read as a real (comparison, min, …). Wrapping
//             it in re() / im() / abs() / norm() makes the meaning explicit.
// Truthiness (a value used as a condition) is |x| != 0 in the interpreter for
// either kind, so it lowers to norm(x) != 0 regardless.
//
// mod(x, p) is the interpreter's centred per-component modulo
// x − p·floor(x/p + ½) (CalcGen's own Mod node is a truncated real remainder),
// built from existing per-component nodes.

using System;
using System.Collections.Generic;

using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.Models
{
    public static class CalcGenLowering
    {
        /// <summary>Expanded-tree size cap: inlining `let` duplicates the bound
        /// value at every use, and the derivative CalcGen builds for distance
        /// estimation grows with it. Past this, CalcGen is refused (the live
        /// interpreter still renders the equation).</summary>
        public const long MaxNodes = 4000;

        public sealed record Result(AstNode? Ast, string? Refusal, long NodeCount)
        {
            public bool Ok => Ast != null;
        }

        /// <summary>Lower a parsed equation. Never throws for a parsed input:
        /// a construct CalcGen can't reproduce comes back as <see cref="Result.Refusal"/>.</summary>
        public static Result Lower(SandboxExpression expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            var lw = new Lowerer();
            try
            {
                var v = lw.Value(expression.Root);
                if (v.Size > MaxNodes) throw new Refused(TooLarge(v.Size));
                return new Result(v.Ast, null, v.Size);
            }
            catch (Refused r)
            {
                return new Result(null, r.Message, 0);
            }
        }

        /// <summary>Parse with the unified language and lower. Throws
        /// <see cref="FormatException"/> with the parse error or the refusal.</summary>
        public static AstNode ParseAndLower(string source)
        {
            var e = EquationLanguage.Parse(source);
            var r = Lower(e);
            if (!r.Ok) throw new FormatException(r.Refusal);
            return r.Ast!;
        }

        private static string TooLarge(long size) =>
            $"CalcGen can't compile this equation: inlining its bindings gives {size} terms (limit {MaxNodes}). " +
            "The live view still renders it.";

        private enum Kind { Real, Complex, Unknown }

        private readonly record struct L(AstNode Ast, Kind Kind, long Size);

        private sealed class Refused : Exception
        {
            public Refused(string message) : base(message) { }
        }

        private sealed class Lowerer
        {
            private readonly Dictionary<int, L> _locals = new();

            // ── Values ───────────────────────────────────────────────────

            public L Value(SbxNode n)
            {
                var v = ValueCore(n);
                if (v.Size > MaxNodes) throw new Refused(TooLarge(v.Size));
                return v;
            }

            private L ValueCore(SbxNode n)
            {
                switch (n)
                {
                    case SbxConst k:
                        if (k.V.IsReal) return new L(new RealConst(k.V.R), Kind.Real, 1);
                        if (k.V.R == 0.0 && k.V.I == 1.0) return new L(new ImagUnit(), Kind.Complex, 1);
                        return new L(new Add(new RealConst(k.V.R), new Mul(new RealConst(k.V.I), new ImagUnit())), Kind.Complex, 5);

                    case SbxSlot s:
                        return s.Slot switch
                        {
                            SandboxExpression.SlotZ => new L(new ZRef(), Kind.Complex, 1),
                            SandboxExpression.SlotC => new L(new CRef(), Kind.Complex, 1),
                            SandboxExpression.SlotPrev => new L(new PrevRef(), Kind.Complex, 1),
                            SandboxExpression.SlotN or SandboxExpression.SlotIter => new L(new IterRef(), Kind.Real, 1),
                            _ => _locals.TryGetValue(s.Slot, out var bound)
                                ? bound
                                : throw new Refused($"CalcGen can't compile this equation: unbound local ${s.Slot}."),
                        };

                    case SbxLet let:
                    {
                        bool had = _locals.TryGetValue(let.Slot, out var prior);
                        _locals[let.Slot] = Value(let.Value);
                        try { return Value(let.Body); }
                        finally { if (had) _locals[let.Slot] = prior; else _locals.Remove(let.Slot); }
                    }

                    case SbxUnary u when u.Op == '-':
                    {
                        var a = Value(u.A);
                        return new L(new Neg(a.Ast), a.Kind, a.Size + 1);
                    }

                    case SbxUnary:
                    case SbxBinary { Op: "&&" or "||" or "<" or ">" or "<=" or ">=" or "==" or "!=" }:
                        return Boolean(Cond(n));

                    case SbxBinary b:
                        return Arith(b);

                    case SbxTernary t:
                    {
                        var c = Cond(t.Cond);
                        var a = Value(t.Then);
                        var e = Value(t.Else);
                        return new L(new If(c.Node, a.Ast, e.Ast), Join(a.Kind, e.Kind, sameOnly: true), 1 + c.Size + a.Size + e.Size);
                    }

                    case SbxCall call:
                        return Call(call);

                    default:
                        throw new Refused($"CalcGen can't compile this equation: unsupported construct {n.GetType().Name}.");
                }
            }

            // A comparison / logical result used as a value: the interpreter's real 1 / 0.
            private static L Boolean((CondNode Node, long Size) c)
                => new(new If(c.Node, new RealConst(1.0), new RealConst(0.0)), Kind.Real, c.Size + 3);

            private L Arith(SbxBinary b)
            {
                if (b.Op == "^")
                {
                    var bas = Value(b.A);
                    if (b.B is SbxConst { V.IsReal: true } k && k.V.R >= 0 && k.V.R <= 64 && k.V.R == Math.Floor(k.V.R))
                        return new L(new Pow(bas.Ast, (int)k.V.R), bas.Kind, bas.Size + 1);
                    var ex = Value(b.B);
                    return new L(new PowC(bas.Ast, ex.Ast), PowKind(bas.Kind, ex.Kind), bas.Size + ex.Size + 1);
                }
                var l = Value(b.A);
                var r = Value(b.B);
                AstNode node = b.Op switch
                {
                    "+" => new Add(l.Ast, r.Ast),
                    "-" => new Sub(l.Ast, r.Ast),
                    "*" => new Mul(l.Ast, r.Ast),
                    "/" => new Div(l.Ast, r.Ast),
                    _ => throw new Refused($"CalcGen can't compile this equation: unsupported operator '{b.Op}'."),
                };
                return new L(node, Join(l.Kind, r.Kind, sameOnly: false), l.Size + r.Size + 1);
            }

            // Real op Real → Real; any Complex operand → Complex; otherwise Unknown.
            private static Kind Join(Kind a, Kind b, bool sameOnly)
            {
                if (sameOnly) return a == b ? a : Kind.Unknown;
                if (a == Kind.Complex || b == Kind.Complex) return Kind.Complex;
                return a == Kind.Real && b == Kind.Real ? Kind.Real : Kind.Unknown;
            }

            // SbxVal.Pow: both real → Math.Pow (real), else Complex.Pow (complex).
            private static Kind PowKind(Kind a, Kind b)
            {
                if (a == Kind.Real && b == Kind.Real) return Kind.Real;
                if (a == Kind.Complex || b == Kind.Complex) return Kind.Complex;
                return Kind.Unknown;
            }

            private L Call(SbxCall call)
            {
                var a = call.Args;
                L x = a.Length > 0 && call.Name is not ("atan2" or "min" or "max" or "clamp" or "mod" or "pow")
                    ? Value(a[0]) : default;
                L Un(AstNode node, Kind kind, long extra = 1) => new(node, kind, x.Size + extra);
                // Value-dependent kind: real for a real operand inside the real
                // domain, complex outside it.
                Kind DomainKind(Kind k) => k == Kind.Complex ? Kind.Complex : Kind.Unknown;

                switch (call.Name)
                {
                    case "sin": return Un(new Sin(x.Ast), x.Kind);
                    case "cos": return Un(new Cos(x.Ast), x.Kind);
                    case "exp": return Un(new Exp(x.Ast), x.Kind);
                    case "tan": return new L(new Div(new Sin(x.Ast), new Cos(x.Ast)), x.Kind, 2 * x.Size + 3);
                    case "sinh": return new L(Sinh(x.Ast), x.Kind, 2 * x.Size + 6);
                    case "cosh": return new L(Cosh(x.Ast), x.Kind, 2 * x.Size + 6);
                    case "tanh": return new L(new Div(Sinh(x.Ast), Cosh(x.Ast)), x.Kind, 4 * x.Size + 13);
                    case "log": return Un(new Log(x.Ast), DomainKind(x.Kind));
                    case "sqrt": return Un(new Exp(new Mul(new RealConst(0.5), new Log(x.Ast))), DomainKind(x.Kind), 4);
                    case "sqr": return new L(new Mul(x.Ast, x.Ast), x.Kind, 2 * x.Size + 1);
                    case "asin": return Un(new Asin(x.Ast), DomainKind(x.Kind));
                    case "acos": return Un(new Acos(x.Ast), DomainKind(x.Kind));
                    case "acosh": return Un(new Acosh(x.Ast), DomainKind(x.Kind));
                    case "atanh": return Un(new Atanh(x.Ast), DomainKind(x.Kind));
                    case "atan": return Un(new Atan(x.Ast), x.Kind);
                    case "asinh": return Un(new Asinh(x.Ast), x.Kind);
                    case "abs": return Un(new AbsOp(x.Ast), Kind.Real);
                    case "norm":
                        return new L(new Add(new Mul(new ReOp(x.Ast), new ReOp(x.Ast)), new Mul(new ImOp(x.Ast), new ImOp(x.Ast))),
                                     Kind.Real, 4 * x.Size + 7);
                    case "conj": return Un(new Conj(x.Ast), x.Kind);
                    case "re": return Un(new ReOp(x.Ast), Kind.Real);
                    case "im": return Un(new ImOp(x.Ast), Kind.Real);
                    case "arg": return Un(new Arg(x.Ast), Kind.Real);
                    case "floor": return Un(new Floor(x.Ast), x.Kind);
                    case "round": return Un(new Round(x.Ast), x.Kind);
                    case "ceil": return Un(new Ceil(x.Ast), x.Kind);
                    case "trunc": return Un(new Trunc(x.Ast), x.Kind);
                    case "fract": return Un(new Fract(x.Ast), x.Kind);
                    case "sign": return Un(new Sign(x.Ast), x.Kind);
                    case "fold": return Un(new Folded(x.Ast), x.Kind);

                    case "pow":
                    {
                        var bas = Value(a[0]);
                        var ex = Value(a[1]);
                        return new L(new PowC(bas.Ast, ex.Ast), PowKind(bas.Kind, ex.Kind), bas.Size + ex.Size + 1);
                    }
                    case "atan2":
                    {
                        var y = AsReal(a[0], "atan2");
                        var xx = AsReal(a[1], "atan2");
                        return new L(new Atan2(y.Ast, xx.Ast), Kind.Real, y.Size + xx.Size + 1);
                    }
                    case "min":
                    case "max":
                    {
                        var l = AsReal(a[0], call.Name);
                        var r = AsReal(a[1], call.Name);
                        AstNode node = call.Name == "min" ? new Min(l.Ast, r.Ast) : new Max(l.Ast, r.Ast);
                        return new L(node, Kind.Real, l.Size + r.Size + 1);
                    }
                    case "clamp":
                    {
                        var v = AsReal(a[0], "clamp");
                        var lo = AsReal(a[1], "clamp");
                        var hi = AsReal(a[2], "clamp");
                        return new L(new Clamp(v.Ast, lo.Ast, hi.Ast), Kind.Real, v.Size + lo.Size + hi.Size + 1);
                    }
                    case "mod":
                    {
                        // x − p·floor(x/p + (½ + ½i)): centred, per component; the
                        // imaginary ½ only matters when x is complex (a real x has
                        // Im 0 → floor(½) = 0 → Im stays 0).
                        var mx = Value(a[0]);
                        var p = AsReal(a[1], "mod");
                        var half = new Add(new RealConst(0.5), new Mul(new RealConst(0.5), new ImagUnit()));
                        var node = new Sub(mx.Ast, new Mul(p.Ast, new Floor(new Add(new Div(mx.Ast, p.Ast), half))));
                        return new L(node, mx.Kind, 2 * mx.Size + 2 * p.Size + 10);
                    }
                    default:
                        throw new Refused($"CalcGen can't compile this equation: no CalcGen form for '{call.Name}'.");
                }
            }

            // A value read through SbxVal.AsReal: itself when real, |x| when complex.
            private L AsReal(SbxNode n, string where)
            {
                var v = Value(n);
                return v.Kind switch
                {
                    Kind.Real => v,
                    Kind.Complex => new L(new AbsOp(v.Ast), Kind.Real, v.Size + 1),
                    _ => throw new Refused(Ambiguous(where)),
                };
            }

            private static string Ambiguous(string where) =>
                $"CalcGen can't compile this equation: {where} reads a value that is real or complex depending on " +
                "its value (log / sqrt / pow / asin / acos / acosh / atanh of a real, or a ?: mixing the two). " +
                "Wrap it in re(), im(), abs() or norm() to say which; the live view still renders it as is.";

            private static AstNode Sinh(AstNode x) => new Div(new Sub(new Exp(x), new Exp(new Neg(x))), new RealConst(2.0));
            private static AstNode Cosh(AstNode x) => new Div(new Add(new Exp(x), new Exp(new Neg(x))), new RealConst(2.0));

            // ── Conditions ───────────────────────────────────────────────

            public (CondNode Node, long Size) Cond(SbxNode n)
            {
                switch (n)
                {
                    case SbxBinary { Op: "&&" } b:
                    {
                        var l = Cond(b.A); var r = Cond(b.B);
                        return (new CondAnd(l.Node, r.Node), l.Size + r.Size + 1);
                    }
                    case SbxBinary { Op: "||" } b:
                    {
                        var l = Cond(b.A); var r = Cond(b.B);
                        return (new CondOr(l.Node, r.Node), l.Size + r.Size + 1);
                    }
                    case SbxUnary { Op: '!' } u:
                    {
                        var x = Cond(u.A);
                        return (new CondNot(x.Node), x.Size + 1);
                    }
                    case SbxBinary { Op: "<" or ">" or "<=" or ">=" or "==" or "!=" } b:
                    {
                        var l = Term(b.A);
                        var r = Term(b.B);
                        var op = b.Op switch
                        {
                            "<" => CmpOp.Lt, ">" => CmpOp.Gt, "<=" => CmpOp.Le,
                            ">=" => CmpOp.Ge, "==" => CmpOp.Eq, _ => CmpOp.Ne,
                        };
                        return (new Cmp(op, l.Term, r.Term), l.Size + r.Size + 1);
                    }
                    default:
                    {
                        // Truthiness: |x| != 0 for either kind (SbxVal.AsBool).
                        var v = Value(n);
                        return (new Cmp(CmpOp.Ne, new CondAbs2(v.Ast), new CondConst(0.0)), v.Size + 2);
                    }
                }
            }

            // A comparison operand, read through AsReal. Recognises CalcGen's
            // native condition terms first (re / im / norm / arg / constant) so
            // those conditions keep CalcGen's existing gating; anything else is
            // the real part of its real (or magnitude-wrapped complex) value.
            private (CondTerm Term, long Size) Term(SbxNode n)
            {
                switch (n)
                {
                    case SbxConst { V.IsReal: true } k:
                        return (new CondConst(k.V.R), 1);
                    case SbxUnary { Op: '-', A: SbxConst { V.IsReal: true } nk }:
                        return (new CondConst(-nk.V.R), 1);
                    case SbxCall { Name: "re" } c:
                    { var v = Value(c.Args[0]); return (new CondRe(v.Ast), v.Size + 1); }
                    case SbxCall { Name: "im" } c:
                    { var v = Value(c.Args[0]); return (new CondIm(v.Ast), v.Size + 1); }
                    case SbxCall { Name: "norm" } c:
                    { var v = Value(c.Args[0]); return (new CondAbs2(v.Ast), v.Size + 1); }
                    case SbxCall { Name: "arg" } c:
                    { var v = Value(c.Args[0]); return (new CondArg(v.Ast), v.Size + 1); }
                    default:
                    {
                        var v = AsReal(n, "a comparison");
                        return (new CondRe(v.Ast), v.Size + 1);
                    }
                }
            }
        }
    }
}
