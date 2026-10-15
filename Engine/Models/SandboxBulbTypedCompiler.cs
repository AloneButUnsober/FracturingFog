// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/SandboxBulbTypedCompiler.cs
//
// #1110 — typed Expression-tree compile for the Sandbox-Bulb DSL. The #283
// compiler (SandboxBulbCompiler) is dynamically typed: every value is an
// SbxVal3 (kind + four doubles), every operator a call into SbxVal3.Add/Mul/…,
// and every function goes through SbxFuncEval.Apply's switch with four SbxVal3
// arguments. That costs 5–20× on transcendental maps (#1104 measurements).
//
// Here each node's kind (real / vec / quat) is inferred statically — z and c
// take the axis mode's kind, n / params / t are real, a let or a chain step
// output takes its value's kind — and the tree is built over double / Vec3 /
// Quat directly, calling Math.* / Vec3.* / Quat.* without the SbxVal3 detour.
//
// Parity: every rule mirrors the interpreter (SbxVal3 static ops, SbxFuncEval)
// for the statically known kinds, operation for operation, so the result is
// bit-identical to the tree walk. That includes the interpreter's odd mixes:
// + and - read a real as (r, 0, 0) / the quat (0, r, 0, 0) while qmul and the
// conversions read it as (r, r, r) / (r, 0, 0, 0); vec × quat scales by the
// quat's X; real / quat is a vec. S1110TypedBulbCompileTests pins this on raw
// IEEE bits.
//
// Where a kind can't be fixed statically — a ternary whose branches differ in
// kind (a KIFS swap fold `cond ? vec(…) : z` in Quat mode) — the value is an
// SbxVal3 ("dynamic") and the nodes that consume it call the interpreter's own
// ops (SbxVal3.Add/…, SbxFuncEval.Apply), so they are exact by construction;
// everything else stays typed. sin & co. on a quat go through Apply too, which
// throws just as the interpreter does.
//
// #27 boundary: same as SandboxBulbCompiler. Not Roslyn, no source compiler, no
// assembly load; the tree only wires together a fixed set of methods we own
// (Vec3, Quat, SbxVal3, SbxFuncEval, the Math functions the interpreter calls).

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace FracturingFog.Models
{
    /// <summary>A typed Vec3-mode step. <paramref name="env"/> is only read for
    /// the scalar extras (params, <c>t</c>), which the caller writes as reals.</summary>
    internal delegate Vec3 SbxTypedVecStep(Vec3 z, Vec3 c, double n, SbxVal3[] env);

    /// <summary>A typed Quat-mode step (see <see cref="SbxTypedVecStep"/>).</summary>
    internal delegate Quat SbxTypedQuatStep(Quat z, Quat c, double n, SbxVal3[] env);

    internal static class SandboxBulbTypedCompiler
    {
        /// <summary>Compile a single expression. Null (with <paramref name="error"/>)
        /// only on an unexpected tree; the caller falls back.
        /// <paramref name="dynamicNodes"/> counts the nodes that had to run on
        /// SbxVal3 (0 when every kind was inferred).</summary>
        public static Delegate? TryCompile(Sbx3Node root, IReadOnlyList<int> extraSlots, bool quatMode,
            out string? error, out int dynamicNodes)
            => TryCompile(new[] { root }, Array.Empty<int>(), extraSlots, quatMode, out error, out dynamicNodes);

        /// <summary>Compile a chain: each step's result is bound to its output
        /// slot for the steps after it; the last step's result is the step value
        /// (SandboxBulbChain.EvalStep's semantics).</summary>
        public static Delegate? TryCompile(IReadOnlyList<Sbx3Node> roots, IReadOnlyList<int> outputSlots,
            IReadOnlyList<int> extraSlots, bool quatMode, out string? error, out int dynamicNodes)
        {
            error = null;
            dynamicNodes = 0;
            try
            {
                var b = new Builder(extraSlots, quatMode);
                var steps = new List<Expression>();
                var locals = new List<ParameterExpression>();
                TV last = default;
                for (int i = 0; i < roots.Count; i++)
                {
                    last = b.Emit(roots[i]);
                    if (i < roots.Count - 1 || i < outputSlots.Count)
                    {
                        if (i >= outputSlots.Count) throw new NotSupportedException("chain step without an output slot");
                        var p = Expression.Variable(last.E.Type, "step" + i);
                        locals.Add(p);
                        steps.Add(Expression.Assign(p, last.E));
                        last = new TV(p, last.K);
                        b.Bind(outputSlots[i], last);
                    }
                }
                steps.Add(quatMode ? AsQuat(last) : AsVec(last));
                var body = b.Wrap(Expression.Block(locals, steps));
                dynamicNodes = b.DynamicNodes;
                return quatMode
                    ? Expression.Lambda<SbxTypedQuatStep>(body, b.Z, b.C, b.N, b.Env).Compile()
                    : Expression.Lambda<SbxTypedVecStep>(body, b.Z, b.C, b.N, b.Env).Compile();
            }
            catch (Exception ex)   // defensive: a malformed tree is a wiring bug, not a user error
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Static kind of a node: double / Vec3 / Quat, or Dyn — an
        /// SbxVal3 whose kind is only known at run time.</summary>
        private enum TK : byte { Real, Vec, Quat, Dyn }

        private readonly record struct TV(Expression E, TK K);

        private sealed class Builder
        {
            public readonly ParameterExpression Z, C, N, Env;
            public int DynamicNodes;
            private readonly HashSet<int> _extras;
            private readonly Dictionary<int, TV> _slots = new();
            // Extras are read from env once, at entry, the first time a slot is used.
            private readonly List<ParameterExpression> _preludeVars = new();
            private readonly List<Expression> _prelude = new();

            public Builder(IReadOnlyList<int> extraSlots, bool quatMode)
            {
                var zt = quatMode ? typeof(Quat) : typeof(Vec3);
                Z = Expression.Parameter(zt, "z");
                C = Expression.Parameter(zt, "c");
                N = Expression.Parameter(typeof(double), "n");
                Env = Expression.Parameter(typeof(SbxVal3[]), "env");
                var zk = quatMode ? TK.Quat : TK.Vec;
                _slots[SandboxBulbExpression.SlotZ] = new TV(Z, zk);
                _slots[SandboxBulbExpression.SlotC] = new TV(C, zk);
                _slots[SandboxBulbExpression.SlotN] = new TV(N, TK.Real);
                _extras = new HashSet<int>();
                foreach (int s in extraSlots) if (s >= 0) _extras.Add(s);
            }

            public void Bind(int slot, TV v) => _slots[slot] = v;

            public Expression Wrap(Expression body)
            {
                if (_prelude.Count == 0) return body;
                var all = new List<Expression>(_prelude) { body };
                return Expression.Block(_preludeVars, all);
            }

            public TV Emit(Sbx3Node node) => node switch
            {
                Sbx3Const c   => Const(c.V),
                Sbx3Slot s    => Slot(s.Slot),
                Sbx3Let l     => Let(l),
                Sbx3Unary u   => Unary(u),
                Sbx3Binary b  => Binary(b),
                Sbx3Ternary t => Ternary(t),
                Sbx3Member m  => Member(m),
                Sbx3Call call => Call(call),
                _ => throw new NotSupportedException("unsupported node " + node.GetType().Name),
            };

            private TV Dyn(Expression e) { DynamicNodes++; return new TV(e, TK.Dyn); }

            // ── Leaves ───────────────────────────────────────────────────────
            // Vec3 / Quat constants are built inline (a struct constant would
            // live boxed in the closure and be unboxed on every read).
            private static TV Const(SbxVal3 v) => v.Kind switch
            {
                SbxKind.Quat => new TV(NewQuat(D(v.W), D(v.X), D(v.Y), D(v.Z)), TK.Quat),
                SbxKind.Vec  => new TV(NewVec(D(v.X), D(v.Y), D(v.Z)), TK.Vec),
                _            => new TV(D(v.X), TK.Real),
            };

            private TV Slot(int slot)
            {
                if (_slots.TryGetValue(slot, out var v)) return v;
                if (!_extras.Contains(slot)) throw new NotSupportedException($"unbound slot {slot}");
                // Params and t: written as reals by EvalStep, so .X is the value.
                var p = Expression.Variable(typeof(double), "x" + slot);
                _preludeVars.Add(p);
                _prelude.Add(Expression.Assign(p,
                    Expression.Field(Expression.ArrayIndex(Env, Expression.Constant(slot)), SbxX)));
                var tv = new TV(p, TK.Real);
                _slots[slot] = tv;
                return tv;
            }

            private TV Let(Sbx3Let l)
            {
                var val = Emit(l.Value);
                var p = Expression.Variable(val.E.Type, "l" + l.Slot);
                bool had = _slots.TryGetValue(l.Slot, out var prior);
                _slots[l.Slot] = new TV(p, val.K);
                TV body;
                try { body = Emit(l.Body); }
                finally { if (had) _slots[l.Slot] = prior; else _slots.Remove(l.Slot); }
                return new TV(Expression.Block(body.E.Type, new[] { p }, Expression.Assign(p, val.E), body.E), body.K);
            }

            // ── Operators ────────────────────────────────────────────────────
            private TV Unary(Sbx3Unary u)
            {
                var a = Emit(u.A);
                if (u.Op != '-') return R(Expression.Condition(Bool(a), D(0.0), D(1.0)));   // Not
                if (a.K == TK.Dyn) return Dyn(Expression.Call(MNeg, a.E));
                return new TV(Expression.Negate(a.E), a.K);   // Neg: componentwise on every kind
            }

            private TV Binary(Sbx3Binary b)
            {
                switch (b.OpKind)
                {
                    case SbxBinOp.And:
                        return R(Expression.Condition(Bool(Emit(b.A)),
                            Expression.Condition(Bool(Emit(b.B)), D(1.0), D(0.0)), D(0.0)));
                    case SbxBinOp.Or:
                        return R(Expression.Condition(Bool(Emit(b.A)), D(1.0),
                            Expression.Condition(Bool(Emit(b.B)), D(1.0), D(0.0))));
                }
                var a = Emit(b.A);
                var c = Emit(b.B);
                if (b.OpKind <= SbxBinOp.Pow && (a.K == TK.Dyn || c.K == TK.Dyn) && !(b.OpKind == SbxBinOp.Pow && a.K != TK.Dyn))
                    return Dyn(Expression.Call(DynOp(b.OpKind), Box(a), Box(c)));
                switch (b.OpKind)
                {
                    case SbxBinOp.Add: return AddSub(a, c, sub: false);
                    case SbxBinOp.Sub: return AddSub(a, c, sub: true);
                    case SbxBinOp.Mul: return Mul(a, c);
                    case SbxBinOp.Div: return Div(a, c);
                    case SbxBinOp.Pow: return Pow(a, c);
                }
                // Comparisons: on AsReal of both sides (SbxFuncEval.Compare).
                Expression x = Real(a), y = Real(c);
                Expression test = b.OpKind switch
                {
                    SbxBinOp.Lt => Expression.LessThan(x, y),
                    SbxBinOp.Gt => Expression.GreaterThan(x, y),
                    SbxBinOp.Le => Expression.LessThanOrEqual(x, y),
                    SbxBinOp.Ge => Expression.GreaterThanOrEqual(x, y),
                    SbxBinOp.Eq => Expression.Equal(x, y),
                    SbxBinOp.Ne => Expression.NotEqual(x, y),
                    _ => throw new NotSupportedException("op " + b.Op),
                };
                return R(Expression.Condition(test, D(1.0), D(0.0)));
            }

            // SbxVal3.Add / Sub: the wider kind wins; a real is (r, 0, 0) as a
            // vec and the quat (0, r, 0, 0); a vec is the quat (0, x, y, z).
            private static TV AddSub(TV a, TV b, bool sub)
            {
                TK k = a.K == TK.Quat || b.K == TK.Quat ? TK.Quat
                     : a.K == TK.Vec || b.K == TK.Vec ? TK.Vec : TK.Real;
                Expression x = Widen(a, k), y = Widen(b, k);
                return new TV(sub ? Expression.Subtract(x, y) : Expression.Add(x, y), k);
            }

            private static Expression Widen(TV v, TK to)
            {
                if (v.K == to) return v.E;
                if (to == TK.Vec) return NewVec(v.E, D(0.0), D(0.0));                   // from real
                if (v.K == TK.Real) return NewQuat(D(0.0), v.E, D(0.0), D(0.0));      // real → quat
                return Expression.Call(MFromVec3, v.E, D(0.0));                         // vec → quat
            }

            // SbxVal3.Mul, case for case.
            private static TV Mul(TV a, TV b)
            {
                switch (a.K, b.K)
                {
                    case (TK.Quat, TK.Quat):
                    case (TK.Quat, TK.Real):
                    case (TK.Real, TK.Quat):
                        return Q(Expression.Multiply(a.E, b.E));   // Hamilton / broadcast
                    case (TK.Real, TK.Real):
                        return R(Expression.Multiply(a.E, b.E));
                    case (TK.Vec, TK.Vec):
                        return Spill2(a.E, b.E, (x, y) => V(NewVec(
                            Expression.Multiply(VXo(x), VXo(y)),
                            Expression.Multiply(VYo(x), VYo(y)),
                            Expression.Multiply(VZo(x), VZo(y)))));
                    case (TK.Vec, TK.Real):
                    case (TK.Real, TK.Vec):
                        return V(Expression.Multiply(a.E, b.E));
                    case (TK.Vec, TK.Quat):     // (a.X*b.X, a.Y*b.X, a.Z*b.X)
                        return V(Expression.Multiply(a.E, QX(b.E)));
                    default:                    // quat × vec: (b.X*a.X, b.Y*a.X, b.Z*a.X)
                        return Spill(a.E, q => V(Expression.Multiply(b.E, QX(q))));
                }
            }

            // SbxVal3.Div, in its own test order.
            private static TV Div(TV a, TV b)
            {
                if (a.K == TK.Quat && b.K == TK.Real)
                    return Spill2(a.E, b.E, (q, r) => Q(NewQuat(
                        Expression.Divide(QWo(q), r), Expression.Divide(QX(q), r),
                        Expression.Divide(QYo(q), r), Expression.Divide(QZo(q), r))));
                if (a.K == TK.Real && b.K == TK.Real)
                    return R(Expression.Divide(a.E, b.E));
                if (a.K == TK.Vec && b.K == TK.Vec)
                    return Spill2(a.E, b.E, (x, y) => V(NewVec(
                        Expression.Divide(VXo(x), VXo(y)),
                        Expression.Divide(VYo(x), VYo(y)),
                        Expression.Divide(VZo(x), VZo(y)))));
                if (a.K == TK.Vec)   // b real or quat: divide by b.X
                    return V(Expression.Divide(a.E, b.K == TK.Real ? b.E : QX(b.E)));
                // real / vec, real / quat, quat / vec, quat / quat: (a.X/b.X, a.X/b.Y, a.X/b.Z).
                return Spill2(a.E, b.E, (x, y) =>
                {
                    Expression ax = a.K == TK.Real ? x : QX(x);
                    return V(NewVec(Expression.Divide(ax, XOf(y, b.K)), Expression.Divide(ax, YOf(y, b.K)),
                        Expression.Divide(ax, ZOf(y, b.K))));
                });
            }

            // SbxVal3.Pow: the base picks quaternion / triplex / real power; the
            // exponent is read AsReal (a dynamic exponent too).
            private TV Pow(TV a, TV e) => a.K switch
            {
                TK.Quat => Q(Expression.Call(MQuatPow, a.E, Real(e))),
                TK.Vec  => V(Expression.Call(MVecPow, a.E, Real(e))),
                TK.Real => R(Expression.Call(MMathPow, a.E, Real(e))),
                _       => Dyn(Expression.Call(DynOp(SbxBinOp.Pow), a.E, Box(e))),
            };

            private TV Ternary(Sbx3Ternary t)
            {
                var cond = Bool(Emit(t.Cond));
                var a = Emit(t.Then);
                var b = Emit(t.Else);
                if (a.K == b.K) return new TV(Expression.Condition(cond, a.E, b.E), a.K);
                // Branches of different kinds: the value's kind is only known at run time.
                return Dyn(Expression.Condition(cond, Box(a), Box(b)));
            }

            // SbxFuncEval.MemberAxis: a real repeats on .x/.y/.z; .w is 0 off a quat.
            private TV Member(Sbx3Member m)
            {
                var t = Emit(m.Target);
                if (t.K == TK.Dyn)
                    return R(Expression.Field(Expression.Call(MMember, t.E, Expression.Constant(m.Axis)), SbxX));
                switch (m.Axis)
                {
                    case 'x': return R(XOf(t.E, t.K));
                    case 'y': return R(YOf(t.E, t.K));
                    case 'z': return R(ZOf(t.E, t.K));
                    case 'w':
                        return t.K == TK.Quat
                            ? R(QWo(t.E))
                            : R(Expression.Block(t.E, D(0.0)));   // the target is still evaluated
                    default: throw new NotSupportedException("axis " + m.Axis);
                }
            }

            // ── Functions (SbxFuncEval.Apply) ────────────────────────────────
            private TV Call(Sbx3Call call)
            {
                var args = new TV[call.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = Emit(call.Args[i]);
                TV A(int i) => args[i];
                switch (call.Func)
                {
                    case SbxFuncId.Vec:
                        return InOrder(args, a => V(NewVec(Real(a[0]), Real(a[1]), Real(a[2]))));
                    case SbxFuncId.QVec:   // W is the fourth argument
                        return InOrder(args, a => Q(NewQuat(Real(a[3]), Real(a[0]), Real(a[1]), Real(a[2]))));
                    case SbxFuncId.QMul:   return Q(Expression.Multiply(AsQuat(A(0)), AsQuat(A(1))));
                    case SbxFuncId.QConj:  return Q(Expression.Call(AsQuat(A(0)), MConj));
                    case SbxFuncId.QPow:   return Q(Expression.Call(MQuatPow, AsQuat(A(0)), Real(A(1))));
                    case SbxFuncId.Length: return R(Expression.Property(AsVec(A(0)), VLength));
                    case SbxFuncId.Dot:    return R(Expression.Call(VecM("Dot", 2), AsVec(A(0)), AsVec(A(1))));
                    case SbxFuncId.Cross:  return V(Expression.Call(VecM("Cross", 2), AsVec(A(0)), AsVec(A(1))));
                    case SbxFuncId.Normalize: return V(Expression.Call(AsVec(A(0)), MNormalized));
                    case SbxFuncId.Triplex: return V(Expression.Call(MVecPow, AsVec(A(0)), Real(A(1))));
                    case SbxFuncId.Rot:    return V(Expression.Call(MRot, AsVec(A(0)), AsVec(A(1)), Real(A(2))));
                    case SbxFuncId.BoxFold: return V(Expression.Call(MBoxFold, AsVec(A(0)), Real(A(1))));
                    case SbxFuncId.SphereFold: return V(Expression.Call(MSphereFold, AsVec(A(0)), Real(A(1)), Real(A(2))));
                    case SbxFuncId.AbsX:   return V(Expression.Call(VecM("AbsX", 1), AsVec(A(0))));
                    case SbxFuncId.AbsY:   return V(Expression.Call(VecM("AbsY", 1), AsVec(A(0))));
                    case SbxFuncId.AbsZ:   return V(Expression.Call(VecM("AbsZ", 1), AsVec(A(0))));
                    case SbxFuncId.Mod:    return V(Expression.Call(MMod, AsVec(A(0)), Real(A(1))));
                    case SbxFuncId.SMin:   return R(Expression.Call(MSMin, Real(A(0)), Real(A(1)), Real(A(2))));
                    case SbxFuncId.Pow2:   return A(0).K == TK.Dyn ? Apply(call.Func, args) : Pow(A(0), A(1));
                    case SbxFuncId.Floor:  return R(Expression.Call(Math1("Floor"), Real(A(0))));
                    case SbxFuncId.Sign:   return R(Expression.Convert(Expression.Call(Math1("Sign"), Real(A(0))), typeof(double)));
                    case SbxFuncId.Min:    return R(Expression.Call(Math2("Min"), Real(A(0)), Real(A(1))));
                    case SbxFuncId.Max:    return R(Expression.Call(Math2("Max"), Real(A(0)), Real(A(1))));
                    case SbxFuncId.Clamp:  return R(Expression.Call(MClamp, Real(A(0)), Real(A(1)), Real(A(2))));
                    case SbxFuncId.Sin: case SbxFuncId.Cos: case SbxFuncId.Tan:
                    case SbxFuncId.Sinh: case SbxFuncId.Cosh: case SbxFuncId.Tanh:
                    case SbxFuncId.Exp: case SbxFuncId.Log: case SbxFuncId.Sqrt:
                        // The interpreter throws for these on a quat: Apply keeps that.
                        return A(0).K is TK.Quat or TK.Dyn ? Apply(call.Func, args) : Scalar(A(0), ScalarName(call.Func));
                    case SbxFuncId.Abs:
                        return A(0).K == TK.Dyn ? Apply(call.Func, args) : Scalar(A(0), "Abs");
                    case SbxFuncId.Norm:
                        return A(0).K == TK.Dyn ? Apply(call.Func, args) : Norm(A(0));
                }
                // The one-argument quaternion-algebra functions (qexp … qcoth).
                if (QuatFunc(call.Func) is { } qm)
                    return Q(Expression.Call(qm, AsQuat(A(0))));
                throw new NotSupportedException("function " + call.Name);
            }

            /// <summary>The interpreter's own applier, for a kind only known at run time.</summary>
            private TV Apply(SbxFuncId f, TV[] args)
            {
                Expression Arg(int i) => i < args.Length ? Box(args[i]) : Expression.Default(typeof(SbxVal3));
                return Dyn(Expression.Call(MApply, Expression.Constant(f), Arg(0), Arg(1), Arg(2), Arg(3)));
            }

            private static string ScalarName(SbxFuncId f) => f switch
            {
                SbxFuncId.Sin => "Sin", SbxFuncId.Cos => "Cos", SbxFuncId.Tan => "Tan",
                SbxFuncId.Sinh => "Sinh", SbxFuncId.Cosh => "Cosh", SbxFuncId.Tanh => "Tanh",
                SbxFuncId.Exp => "Exp", SbxFuncId.Log => "Log", _ => "Sqrt",
            };

            // ApplyScalar / ApplyAll: elementwise on a vec, and (abs only) on a
            // quat including W.
            private static TV Scalar(TV a, string fn)
            {
                var m = Math1(fn);
                switch (a.K)
                {
                    case TK.Real: return R(Expression.Call(m, a.E));
                    case TK.Vec:
                        return Spill(a.E, v => V(NewVec(Expression.Call(m, VXo(v)),
                            Expression.Call(m, VYo(v)), Expression.Call(m, VZo(v)))));
                    default:
                        return Spill(a.E, q => Q(NewQuat(Expression.Call(m, QWo(q)),
                            Expression.Call(m, QX(q)), Expression.Call(m, QYo(q)), Expression.Call(m, QZo(q)))));
                }
            }

            // Squared length in the interpreter's operation order.
            private static TV Norm(TV a) => a.K switch
            {
                TK.Real => Spill(a.E, r => R(Expression.Multiply(r, r))),
                TK.Vec => Spill(a.E, v => R(Sum(Sq(VXo(v)), Sq(VYo(v)), Sq(VZo(v))))),
                _ => Spill(a.E, q => R(Sum(Sq(QWo(q)), Sq(QX(q)), Sq(QYo(q)), Sq(QZo(q))))),
            };

            private static Expression Sq(Expression x) => Expression.Multiply(x, x);
            private static Expression Sum(params Expression[] xs)
            {
                Expression s = xs[0];
                for (int i = 1; i < xs.Length; i++) s = Expression.Add(s, xs[i]);
                return s;
            }

            // ── Helpers ──────────────────────────────────────────────────────
            /// <summary>Evaluate every argument once, left to right (the
            /// interpreter's order), before a constructor that reorders them.</summary>
            private static TV InOrder(TV[] args, Func<TV[], TV> body)
            {
                var vars = new List<ParameterExpression>();
                var inits = new List<Expression>();
                var bound = new TV[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].E is ParameterExpression or ConstantExpression) { bound[i] = args[i]; continue; }
                    var p = Expression.Variable(args[i].E.Type, "a" + i);
                    vars.Add(p);
                    inits.Add(Expression.Assign(p, args[i].E));
                    bound[i] = new TV(p, args[i].K);
                }
                var r = body(bound);
                if (vars.Count == 0) return r;
                inits.Add(r.E);
                return new TV(Expression.Block(r.E.Type, vars, inits), r.K);
            }

            private static TV R(Expression e) => new(e, TK.Real);
            private static TV V(Expression e) => new(e, TK.Vec);
            private static TV Q(Expression e) => new(e, TK.Quat);
        }

        // ── Conversions (SbxVal3.AsReal / AsVec / AsQuat, and back) ──────────
        /// <summary>AsReal: a vec or quat reads as its length.</summary>
        private static Expression Real(TV v) => v.K switch
        {
            TK.Real => v.E,
            TK.Vec  => Expression.Property(v.E, VLength),
            TK.Quat => Expression.Property(v.E, QLength),
            _       => Expression.Call(v.E, MAsReal),
        };

        private static Expression Bool(TV v) => Expression.NotEqual(Real(v), D(0.0));

        /// <summary>AsVec: a real is (r, r, r); a quat drops W.</summary>
        private static Expression AsVec(TV v) => v.K switch
        {
            TK.Vec  => v.E,
            TK.Quat => Expression.Call(v.E, MToVec3),
            TK.Real => Spill(v.E, r => NewVec(r, r, r)),
            _       => Expression.Call(v.E, MAsVec),
        };

        /// <summary>AsQuat: a real is (r, 0, 0, 0); a vec is (0, x, y, z).</summary>
        private static Expression AsQuat(TV v) => v.K switch
        {
            TK.Quat => v.E,
            TK.Vec  => Expression.Call(MFromVec3, v.E, D(0.0)),
            TK.Real => NewQuat(v.E, D(0.0), D(0.0), D(0.0)),
            _       => Expression.Call(v.E, MAsQuat),
        };

        /// <summary>The value as the interpreter's SbxVal3.</summary>
        private static Expression Box(TV v) => v.K switch
        {
            TK.Real => Expression.Call(MBoxR, v.E),
            TK.Vec  => Expression.Call(MBoxV, v.E),
            TK.Quat => Expression.Call(MBoxQ, v.E),
            _       => v.E,
        };

        // Component reads: a real repeats (MemberAxis, and Div's fields of a real).
        private static Expression XOf(Expression e, TK k) => k switch { TK.Real => e, TK.Vec => VXo(e), _ => QX(e) };
        private static Expression YOf(Expression e, TK k) => k switch { TK.Real => e, TK.Vec => VYo(e), _ => QYo(e) };
        private static Expression ZOf(Expression e, TK k) => k switch { TK.Real => e, TK.Vec => VZo(e), _ => QZo(e) };

        /// <summary>Bind <paramref name="e"/> to a temporary when it is used
        /// more than once (a parameter or constant is used directly).</summary>
        private static T Spill<T>(Expression e, Func<Expression, T> body) where T : notnull
        {
            if (e is ParameterExpression or ConstantExpression) return body(e);
            var p = Expression.Variable(e.Type);
            var r = body(p);
            return r switch
            {
                TV tv => (T)(object)new TV(Expression.Block(tv.E.Type, new[] { p }, Expression.Assign(p, e), tv.E), tv.K),
                Expression x => (T)(object)Expression.Block(x.Type, new[] { p }, Expression.Assign(p, e), x),
                _ => throw new InvalidOperationException(),
            };
        }

        private static TV Spill2(Expression a, Expression b, Func<Expression, Expression, TV> body)
            => Spill(a, x => Spill(b, y => body(x, y)));

        private static ConstantExpression D(double d) => Expression.Constant(d, typeof(double));
        private static Expression NewVec(Expression x, Expression y, Expression z) => Expression.New(CVec, x, y, z);
        private static Expression NewQuat(Expression w, Expression x, Expression y, Expression z) => Expression.New(CQuat, w, x, y, z);
        private static Expression VXo(Expression v) => Expression.Property(v, VX);
        private static Expression VYo(Expression v) => Expression.Property(v, VY);
        private static Expression VZo(Expression v) => Expression.Property(v, VZ);
        private static Expression QWo(Expression q) => Expression.Property(q, QW);
        private static Expression QX(Expression q) => Expression.Property(q, QXp);
        private static Expression QYo(Expression q) => Expression.Property(q, QY);
        private static Expression QZo(Expression q) => Expression.Property(q, QZ);

        private static MethodInfo DynOp(SbxBinOp op) => op switch
        {
            SbxBinOp.Add => MDynAdd, SbxBinOp.Sub => MDynSub, SbxBinOp.Mul => MDynMul,
            SbxBinOp.Div => MDynDiv, _ => MDynPow,
        };

        private static MethodInfo? QuatFunc(SbxFuncId f)
        {
            string? name = f switch
            {
                SbxFuncId.QExp => "Exp", SbxFuncId.QLog => "Log", SbxFuncId.QSqrt => "Sqrt", SbxFuncId.QInv => "Inverse",
                SbxFuncId.QSin => "Sin", SbxFuncId.QCos => "Cos", SbxFuncId.QTan => "Tan",
                SbxFuncId.QSinh => "Sinh", SbxFuncId.QCosh => "Cosh", SbxFuncId.QTanh => "Tanh",
                SbxFuncId.QAsin => "Asin", SbxFuncId.QAcos => "Acos", SbxFuncId.QAtan => "Atan",
                SbxFuncId.QAsinh => "Asinh", SbxFuncId.QAcosh => "Acosh", SbxFuncId.QAtanh => "Atanh",
                SbxFuncId.QCsc => "Csc", SbxFuncId.QSec => "Sec", SbxFuncId.QCot => "Cot",
                SbxFuncId.QCsch => "Csch", SbxFuncId.QSech => "Sech", SbxFuncId.QCoth => "Coth",
                _ => null,
            };
            return name == null ? null : typeof(Quat).GetMethod(name, new[] { typeof(Quat) });
        }

        // ── Cached members (our own types and System.Math only) ──────────────
        private static readonly ConstructorInfo CVec = typeof(Vec3).GetConstructor(new[] { typeof(double), typeof(double), typeof(double) })!;
        private static readonly ConstructorInfo CQuat = typeof(Quat).GetConstructor(new[] { typeof(double), typeof(double), typeof(double), typeof(double) })!;
        // Vec3 / Quat are record structs: X/Y/Z/W are properties; SbxVal3's are fields.
        private static readonly PropertyInfo VX = typeof(Vec3).GetProperty(nameof(Vec3.X))!;
        private static readonly PropertyInfo VY = typeof(Vec3).GetProperty(nameof(Vec3.Y))!;
        private static readonly PropertyInfo VZ = typeof(Vec3).GetProperty(nameof(Vec3.Z))!;
        private static readonly PropertyInfo VLength = typeof(Vec3).GetProperty(nameof(Vec3.Length))!;
        private static readonly PropertyInfo QW = typeof(Quat).GetProperty(nameof(Quat.W))!;
        private static readonly PropertyInfo QXp = typeof(Quat).GetProperty(nameof(Quat.X))!;
        private static readonly PropertyInfo QY = typeof(Quat).GetProperty(nameof(Quat.Y))!;
        private static readonly PropertyInfo QZ = typeof(Quat).GetProperty(nameof(Quat.Z))!;
        private static readonly PropertyInfo QLength = typeof(Quat).GetProperty(nameof(Quat.Length))!;
        private static readonly FieldInfo SbxX = typeof(SbxVal3).GetField(nameof(SbxVal3.X))!;
        private static readonly MethodInfo MFromVec3 = typeof(Quat).GetMethod(nameof(Quat.FromVec3))!;
        private static readonly MethodInfo MToVec3 = typeof(Quat).GetMethod(nameof(Quat.ToVec3))!;
        private static readonly MethodInfo MConj = typeof(Quat).GetMethod(nameof(Quat.Conjugate))!;
        private static readonly MethodInfo MQuatPow = typeof(Quat).GetMethod(nameof(Quat.Pow), new[] { typeof(Quat), typeof(double) })!;
        private static readonly MethodInfo MVecPow = typeof(Vec3).GetMethod(nameof(Vec3.Pow), new[] { typeof(Vec3), typeof(double) })!;
        private static readonly MethodInfo MNormalized = typeof(Vec3).GetMethod(nameof(Vec3.Normalized), Type.EmptyTypes)!;
        private static readonly MethodInfo MRot = typeof(Vec3).GetMethod(nameof(Vec3.Rot))!;
        private static readonly MethodInfo MBoxFold = typeof(Vec3).GetMethod(nameof(Vec3.BoxFold))!;
        private static readonly MethodInfo MSphereFold = typeof(Vec3).GetMethod(nameof(Vec3.SphereFold))!;
        private static readonly MethodInfo MMod = typeof(Vec3).GetMethod(nameof(Vec3.Mod))!;
        private static readonly MethodInfo MSMin = typeof(Vec3).GetMethod(nameof(Vec3.SMin))!;
        private static readonly MethodInfo MMathPow = typeof(Math).GetMethod(nameof(Math.Pow), new[] { typeof(double), typeof(double) })!;
        private static readonly MethodInfo MClamp = typeof(Math).GetMethod(nameof(Math.Clamp), new[] { typeof(double), typeof(double), typeof(double) })!;
        // The interpreter's dynamic surface, for TK.Dyn values.
        private static readonly MethodInfo MAsReal = typeof(SbxVal3).GetMethod(nameof(SbxVal3.AsReal), Type.EmptyTypes)!;
        private static readonly MethodInfo MAsVec = typeof(SbxVal3).GetMethod(nameof(SbxVal3.AsVec), Type.EmptyTypes)!;
        private static readonly MethodInfo MAsQuat = typeof(SbxVal3).GetMethod(nameof(SbxVal3.AsQuat), Type.EmptyTypes)!;
        private static readonly MethodInfo MBoxR = typeof(SbxVal3).GetMethod(nameof(SbxVal3.R), new[] { typeof(double) })!;
        private static readonly MethodInfo MBoxV = typeof(SbxVal3).GetMethod(nameof(SbxVal3.V), new[] { typeof(Vec3) })!;
        private static readonly MethodInfo MBoxQ = typeof(SbxVal3).GetMethod(nameof(SbxVal3.Q), new[] { typeof(Quat) })!;
        private static readonly MethodInfo MDynAdd = DynBin(nameof(SbxVal3.Add)), MDynSub = DynBin(nameof(SbxVal3.Sub)),
            MDynMul = DynBin(nameof(SbxVal3.Mul)), MDynDiv = DynBin(nameof(SbxVal3.Div)), MDynPow = DynBin(nameof(SbxVal3.Pow));
        private static readonly MethodInfo MNeg = typeof(SbxVal3).GetMethod(nameof(SbxVal3.Neg), new[] { typeof(SbxVal3) })!;
        private static readonly MethodInfo MApply = typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.Apply))!;
        private static readonly MethodInfo MMember = typeof(SbxFuncEval).GetMethod(nameof(SbxFuncEval.MemberAxis))!;

        private static MethodInfo DynBin(string name) =>
            typeof(SbxVal3).GetMethod(name, new[] { typeof(SbxVal3), typeof(SbxVal3) })!;
        private static MethodInfo VecM(string name, int arity)
        {
            var ps = new Type[arity];
            for (int i = 0; i < arity; i++) ps[i] = typeof(Vec3);
            return typeof(Vec3).GetMethod(name, ps)!;
        }
        private static MethodInfo Math1(string name) => typeof(Math).GetMethod(name, new[] { typeof(double) })!;
        private static MethodInfo Math2(string name) => typeof(Math).GetMethod(name, new[] { typeof(double), typeof(double) })!;
    }
}
