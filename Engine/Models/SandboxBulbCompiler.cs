// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/SandboxBulbCompiler.cs
//
// #283 — Expression-tree JIT for the Sandbox-Bulb DSL. Walks a validated
// Sbx3Node AST and emits a System.Linq.Expressions lambda
// Func<SbxVal3[], SbxVal3>, then .Compile()s it to IL. Removes the per-node
// virtual-dispatch cost of the tree-walking interpreter on the raymarch hot
// path (evaluated MaxSteps × iterations × pixels times per frame).
//
// #27 boundary: this is NOT Roslyn and NOT a source compiler. No user string
// reaches a code generator — the AST is already parsed + validated by
// SandboxBulbExpression, and the emitter only wires together a FIXED set of
// operations that the interpreter already invokes (the SbxVal3 static ops and
// SbxFuncEval). Every leaf call targets a method we own; there is no reflection
// over user-named members, no BCL surface widening. The compiled delegate can
// do nothing the interpreter could not.
//
// Parity: node semantics are shared with the interpreter through SbxFuncEval
// (functions, member access, logical-not, comparisons) and the SbxVal3 static
// arithmetic ops, so the compiled kernel is bit-identical to the walker.
// SandboxBulbCompilerParityTests pins this.

using System;
using System.Linq.Expressions;
using System.Reflection;

namespace FracturingFog.Models
{
    internal static class SandboxBulbCompiler
    {
        // ── Cached method handles (our own types only; resolved once) ────────
        private static readonly MethodInfo MAdd = Op("Add"), MSub = Op("Sub"),
            MMul = Op("Mul"), MDiv = Op("Div"), MPow = Op("Pow");
        private static readonly MethodInfo MNeg =
            typeof(SbxVal3).GetMethod(nameof(SbxVal3.Neg), new[] { typeof(SbxVal3) })!;
        private static readonly MethodInfo MAsBool =
            typeof(SbxVal3).GetMethod(nameof(SbxVal3.AsBool), Type.EmptyTypes)!;
        private static readonly MethodInfo MApply =
            typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.Apply))!;
        private static readonly MethodInfo MMember =
            typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.MemberAxis))!;
        private static readonly MethodInfo MNot =
            typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.Not))!;
        private static readonly MethodInfo MCompare =
            typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.Compare))!;

        private static readonly ConstantExpression One =
            Expression.Constant(SbxVal3.R(1.0), typeof(SbxVal3));
        private static readonly ConstantExpression Zero =
            Expression.Constant(SbxVal3.R(0.0), typeof(SbxVal3));
        private static readonly ConstantExpression DefaultVal =
            Expression.Constant(default(SbxVal3), typeof(SbxVal3));

        private static MethodInfo Op(string name) =>
            typeof(SbxVal3).GetMethod(name, new[] { typeof(SbxVal3), typeof(SbxVal3) })!;

        /// <summary>Compile the AST to a delegate. Returns null (caller falls
        /// back to the interpreter) if any construct cannot be emitted — the
        /// AST covers a fixed node set, so this is defensive, not expected.</summary>
        public static Func<SbxVal3[], SbxVal3>? TryCompile(Sbx3Node root)
        {
            try
            {
                var env = Expression.Parameter(typeof(SbxVal3[]), "env");
                var body = Emit(root, env);
                return Expression.Lambda<Func<SbxVal3[], SbxVal3>>(body, env).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Expression Emit(Sbx3Node node, ParameterExpression env)
        {
            switch (node)
            {
                case Sbx3Const c:
                    return Expression.Constant(c.V, typeof(SbxVal3));

                case Sbx3Slot s:
                    return Expression.ArrayIndex(env, Expression.Constant(s.Slot));

                case Sbx3Let l:
                {
                    // env[slot] = value; then body. Block returns the body value.
                    var target = Expression.ArrayAccess(env, Expression.Constant(l.Slot));
                    var assign = Expression.Assign(target, Emit(l.Value, env));
                    return Expression.Block(typeof(SbxVal3), assign, Emit(l.Body, env));
                }

                case Sbx3Unary u:
                    return u.Op == '-'
                        ? Expression.Call(MNeg, Emit(u.A, env))
                        : Expression.Call(MNot, Emit(u.A, env));

                case Sbx3Binary b:
                    return EmitBinary(b, env);

                case Sbx3Ternary t:
                    return Expression.Condition(
                        BoolOf(Emit(t.Cond, env)),
                        Emit(t.Then, env),
                        Emit(t.Else, env),
                        typeof(SbxVal3));

                case Sbx3Member m:
                    return Expression.Call(MMember, Emit(m.Target, env),
                        Expression.Constant(m.Axis, typeof(char)));

                case Sbx3Call call:
                    return EmitCall(call, env);

                default:
                    throw new NotSupportedException("Unsupported node " + node.GetType().Name);
            }
        }

        private static Expression EmitBinary(Sbx3Binary b, ParameterExpression env)
        {
            switch (b.OpKind)
            {
                case SbxBinOp.Add: return Expression.Call(MAdd, Emit(b.A, env), Emit(b.B, env));
                case SbxBinOp.Sub: return Expression.Call(MSub, Emit(b.A, env), Emit(b.B, env));
                case SbxBinOp.Mul: return Expression.Call(MMul, Emit(b.A, env), Emit(b.B, env));
                case SbxBinOp.Div: return Expression.Call(MDiv, Emit(b.A, env), Emit(b.B, env));
                case SbxBinOp.Pow: return Expression.Call(MPow, Emit(b.A, env), Emit(b.B, env));

                // Short-circuit: B is only evaluated when A's truthiness allows.
                // Mirrors the interpreter (Sbx3Binary.Eval) exactly: result is
                // R(1) / R(0). && → if !A then 0 else (B ? 1 : 0).
                case SbxBinOp.And:
                    return Expression.Condition(
                        BoolOf(Emit(b.A, env)),
                        Expression.Condition(BoolOf(Emit(b.B, env)), One, Zero, typeof(SbxVal3)),
                        Zero, typeof(SbxVal3));
                case SbxBinOp.Or:
                    return Expression.Condition(
                        BoolOf(Emit(b.A, env)),
                        One,
                        Expression.Condition(BoolOf(Emit(b.B, env)), One, Zero, typeof(SbxVal3)),
                        typeof(SbxVal3));

                default: // comparisons
                    return Expression.Call(MCompare,
                        Expression.Constant(b.OpKind, typeof(SbxBinOp)),
                        Emit(b.A, env), Emit(b.B, env));
            }
        }

        private static Expression EmitCall(Sbx3Call call, ParameterExpression env)
        {
            // SbxFuncEval.Apply(func, a0, a1, a2, a3) — pad absent args with
            // default (unread by that function id, matching the interpreter,
            // which passes `default` for arg slots beyond the call's arity).
            Expression Arg(int i) => i < call.Args.Length ? Emit(call.Args[i], env) : DefaultVal;
            return Expression.Call(MApply,
                Expression.Constant(call.Func, typeof(SbxFuncId)),
                Arg(0), Arg(1), Arg(2), Arg(3));
        }

        /// <summary>value.AsBool() as an Expression&lt;bool&gt; for Condition tests.</summary>
        private static Expression BoolOf(Expression val) => Expression.Call(val, MAsBool);
    }
}
