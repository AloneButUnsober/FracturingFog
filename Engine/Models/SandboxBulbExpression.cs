// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/SandboxBulbExpression.cs
//
// Safe expression DSL for the 3D Sandbox-Bulb fractal type. Parses a user-
// supplied string into an AST that an interpreter evaluates per raymarch step.
// 3D analogue of SandboxExpression: that one walks Complex values per pixel;
// this one walks tagged {real | vec3} values per Step call inside a
// numerical-DE raymarch.
//
// No BCL exposure: no File.IO, no reflection, no P/Invoke, no allocation
// beyond AST + per-thread env array.
//
// Comments: `// line` and `/* block */` are skipped anywhere whitespace is
// (a lone `/` is still division).
//
// Grammar (#1101: parsed by the shared EquationFrontEnd; this file supplies the
// 3D builder). #1100 — the same surface rules as the 2D
// equation language (Docs/Technical/Equation-Language.md), plus member access:
//   program  := block
//   block    := "return" expr ";"?
//             | "if" "(" expr ")" "return" expr ";"? block      ; guard
//             | "if" "(" expr ")" IDENT "=" expr ";"? block     ; conditional reassign
//             | TYPE? IDENT "=" expr ";"? block                 ; declare / reassign
//             | expr ";"?
//   expr     := let_expr
//   let_expr := "let" IDENT "=" expr "in" expr
//             | "if" or_expr "then" expr "else" expr | ternary
//   ternary  := or_expr ("?" expr ":" expr)?
//   or_expr  := and_expr ("||" and_expr)*
//   and_expr := not_expr ("&&" not_expr)*
//   not_expr := "!" not_expr | cmp_expr
//   cmp_expr := add_expr ((<|>|<=|>=|==|!=) add_expr)?
//   add_expr := mul_expr (("+"|"-") mul_expr)*
//   mul_expr := unary (("*"|"/") unary)*
//   unary    := ("-"|"+") unary | pow_expr      ; #1100: -z^2 = -(z^2)
//   pow_expr := primary ("^" unary)?           ; right-assoc
//   primary  := NUMBER | IDENT member* | IDENT "(" args ")" member* | "(" expr ")" member*
//   TYPE     := var | Vec3 | Quat | double | int | float   (ignored)
//   (Language version 1, before #1100: pow_expr := unary ("^" pow_expr)?, so
//   -z^2 was (-z)^2; ParseLegacy keeps that reading for BulbLanguageMigration.)
//   member   := "." ("x"|"y"|"z")
//
// Built-in identifiers:
//   z, c, n               (input slots; z,c are Vec3, n is Real)
//   pi, e                 (real constants)
// Vec3 literal:
//   vec(x, y, z)
// Operator semantics:
//   vec + vec, vec - vec, -vec               componentwise
//   vec * scalar, scalar * vec, vec / scalar broadcast
//   vec * vec                                 componentwise (Hadamard)
//   vec ^ scalar                              triplex Mandelbulb power
//   scalar ^ scalar                           real Math.Pow
// Functions (overload by arg kind unless noted):
//   sin cos tan sinh cosh tanh exp log sqrt abs        scalar OR componentwise
//                                                       (abs is componentwise on
//                                                       vec/quat — the fold idiom)
//   length(vec)                                         vec3 -> real
//   norm(x)                                             squared length -> real (#1100)
//   dot(vec, vec)                                       -> real
//   cross(vec, vec)                                     -> vec3
//   normalize(vec)                                      -> vec3
//   triplex(vec, scalar)                                Mandelbulb power
//   rot(vec, axis, angle)                               Rodrigues
//   boxfold(vec, limit)                                 Mandelbox box-fold
//   spherefold(vec, rmin, rmax)                         Mandelbox sphere-fold
//   absx(vec) absy(vec) absz(vec)                       per-axis abs
//   mod(vec, period)                                    periodic space
//   smin(a, b, k)                                       scalar smooth-min
//   pow(a, b)                                           explicit pow alias
//   floor(s) sign(s) min(a,b) max(a,b) clamp(x,lo,hi)   scalar utils
//
// Slots 0..2 reserved: z, c, n.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FracturingFog.Models
{
    /// <summary>Parser error carrying the offending source position so the
    /// editor can highlight the failing span. <see cref="Position"/> is a
    /// 0-based character index into the original source.</summary>
    public sealed class SbxParseException : FormatException
    {
        public int Position { get; }
        public int Length { get; }
        /// <summary>#1102 — 0-based chain step the error is in; -1 for the
        /// single source. <see cref="Position"/> is within that step's text.</summary>
        public int StepIndex { get; init; } = -1;
        public SbxParseException(string message, int position, int length = 1)
            : base(message) { Position = position; Length = Math.Max(1, length); }
    }

    /// <summary>Value kind for <see cref="SbxVal3"/>: scalar real, 3-vector,
    /// or 4-quaternion. The DSL is real-only by mathematical surface but the
    /// runtime value type widens to carry W so Quat-mode shares the same
    /// interpreter + AST.</summary>
    public enum SbxKind : byte { Real, Vec, Quat }

    // Wave 4.4 — packed binary opcode resolved at parse time. Interpreter
    // hot path switches on this enum (jump table) rather than `string Op`
    // (chained string compares per call). String op retained on the AST
    // node for emitter + analytic-DE pattern matchers.
    internal enum SbxBinOp : byte
    {
        Add, Sub, Mul, Div, Pow,
        Lt, Gt, Le, Ge, Eq, Ne,
        And, Or,
    }

    // Wave 4.4 — packed function id resolved at parse time. Same rationale
    // as SbxBinOp.
    internal enum SbxFuncId : byte
    {
        // 3-arg
        Vec, Rot, SphereFold, SMin, Clamp,
        // 4-arg
        QVec,
        // 2-arg
        QMul, QPow, Dot, Cross, Triplex, BoxFold, Mod, Pow2, Min, Max,
        // 1-arg
        QConj, Length, Normalize, AbsX, AbsY, AbsZ, Floor, Sign,
        Sin, Cos, Tan, Sinh, Cosh, Tanh, Exp, Log, Sqrt, Abs,
        // 1-arg quaternion-algebra transcendentals (distinct from the
        // componentwise scalar funcs above — these treat the arg as a
        // quaternion, not a 4-tuple). Map to Quat.* in Quat.cs.
        QExp, QLog, QSqrt, QInv,
        QSin, QCos, QTan, QSinh, QCosh, QTanh,
        QAsin, QAcos, QAtan, QAsinh, QAcosh, QAtanh,
        QCsc, QSec, QCot, QCsch, QSech, QCoth,
        // #1100 — appended so every existing id keeps its value.
        Norm,
    }

    /// <summary>Tagged value: scalar real, 3-vector, or quaternion. W is
    /// only read when Kind = Quat. Name retained for back-compat — Vec3-only
    /// callers see Real+Vec semantics identical to the pre-Quat shape.</summary>
    public readonly struct SbxVal3
    {
        public readonly SbxKind Kind;
        public readonly double X, Y, Z, W;

        public bool IsVec  => Kind == SbxKind.Vec;
        public bool IsQuat => Kind == SbxKind.Quat;
        public bool IsReal => Kind == SbxKind.Real;
        public bool IsVecOrQuat => Kind != SbxKind.Real;

        public SbxVal3(double r) { Kind = SbxKind.Real; X = r; Y = 0; Z = 0; W = 0; }
        public SbxVal3(double x, double y, double z) { Kind = SbxKind.Vec; X = x; Y = y; Z = z; W = 0; }
        public SbxVal3(Vec3 v) { Kind = SbxKind.Vec; X = v.X; Y = v.Y; Z = v.Z; W = 0; }
        public SbxVal3(double w, double x, double y, double z, bool _quat)
        { Kind = SbxKind.Quat; W = w; X = x; Y = y; Z = z; }
        public SbxVal3(Quat q) { Kind = SbxKind.Quat; W = q.W; X = q.X; Y = q.Y; Z = q.Z; }

        public Vec3 AsVec() => Kind switch
        {
            SbxKind.Vec  => new Vec3(X, Y, Z),
            SbxKind.Quat => new Vec3(X, Y, Z),
            _            => new Vec3(X, X, X),
        };
        public Quat AsQuat() => Kind switch
        {
            SbxKind.Quat => new Quat(W, X, Y, Z),
            SbxKind.Vec  => new Quat(0, X, Y, Z),
            _            => new Quat(X, 0, 0, 0),
        };
        public double AsReal() => Kind switch
        {
            SbxKind.Vec  => Math.Sqrt(X * X + Y * Y + Z * Z),
            SbxKind.Quat => Math.Sqrt(W * W + X * X + Y * Y + Z * Z),
            _            => X,
        };
        public bool AsBool() => AsReal() != 0.0;

        public static SbxVal3 R(double r) => new(r);
        public static SbxVal3 V(double x, double y, double z) => new(x, y, z);
        public static SbxVal3 V(Vec3 v) => new(v);
        public static SbxVal3 Q(double w, double x, double y, double z) => new(w, x, y, z, true);
        public static SbxVal3 Q(Quat q) => new(q);

        public static SbxVal3 Add(SbxVal3 a, SbxVal3 b)
        {
            if (a.IsQuat || b.IsQuat)
                return Q(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            if (a.IsVec || b.IsVec)
                return new SbxVal3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            return R(a.X + b.X);
        }

        public static SbxVal3 Sub(SbxVal3 a, SbxVal3 b)
        {
            if (a.IsQuat || b.IsQuat)
                return Q(a.W - b.W, a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            if (a.IsVec || b.IsVec)
                return new SbxVal3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            return R(a.X - b.X);
        }

        public static SbxVal3 Mul(SbxVal3 a, SbxVal3 b)
        {
            // Quat × Quat is Hamilton; Quat × Real (and vice-versa) broadcasts.
            if (a.IsQuat && b.IsQuat) return Q(a.AsQuat() * b.AsQuat());
            if (a.IsQuat && b.IsReal) return Q(a.W * b.X, a.X * b.X, a.Y * b.X, a.Z * b.X);
            if (a.IsReal && b.IsQuat) return Q(b.W * a.X, b.X * a.X, b.Y * a.X, b.Z * a.X);
            // Vec3 paths unchanged from Stage 1.
            if (!a.IsVec && !b.IsVec) return R(a.X * b.X);
            if (a.IsVec && b.IsVec) return new SbxVal3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
            if (a.IsVec) return new SbxVal3(a.X * b.X, a.Y * b.X, a.Z * b.X);
            return new SbxVal3(b.X * a.X, b.Y * a.X, b.Z * a.X);
        }

        public static SbxVal3 Div(SbxVal3 a, SbxVal3 b)
        {
            if (a.IsQuat && b.IsReal) return Q(a.W / b.X, a.X / b.X, a.Y / b.X, a.Z / b.X);
            if (!a.IsVec && !b.IsVec && !a.IsQuat && !b.IsQuat) return R(a.X / b.X);
            if (a.IsVec && b.IsVec) return new SbxVal3(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
            if (a.IsVec) return new SbxVal3(a.X / b.X, a.Y / b.X, a.Z / b.X);
            return new SbxVal3(a.X / b.X, a.X / b.Y, a.X / b.Z);
        }

        public static SbxVal3 Neg(SbxVal3 a) => a.Kind switch
        {
            SbxKind.Quat => Q(-a.W, -a.X, -a.Y, -a.Z),
            SbxKind.Vec  => new SbxVal3(-a.X, -a.Y, -a.Z),
            _            => R(-a.X),
        };

        /// <summary>Vec → triplex Mandelbulb power. Quat → Quat.Pow (exact
        /// self-multiply for non-negative integer exponents, analytic form for
        /// fractional/negative). Real → Math.Pow. Never throws — undefined
        /// cases yield non-finite values that escape the pixel, matching the
        /// Quat escape contract (see Quat.cs).</summary>
        public static SbxVal3 Pow(SbxVal3 a, SbxVal3 b)
        {
            if (a.IsQuat) return Q(Quat.Pow(a.AsQuat(), b.AsReal()));
            if (a.IsVec) return V(Vec3.Pow(a.AsVec(), b.AsReal()));
            return R(Math.Pow(a.X, b.AsReal()));
        }
    }

    public abstract class Sbx3Node
    {
        public abstract SbxVal3 Eval(SbxVal3[] env);
    }

    public sealed class Sbx3Const : Sbx3Node
    {
        public readonly SbxVal3 V;
        public Sbx3Const(SbxVal3 v) { V = v; }
        public override SbxVal3 Eval(SbxVal3[] env) => V;
    }

    public sealed class Sbx3Slot : Sbx3Node
    {
        public readonly int Slot;
        public Sbx3Slot(int s) { Slot = s; }
        public override SbxVal3 Eval(SbxVal3[] env) => env[Slot];
    }

    public sealed class Sbx3Let : Sbx3Node
    {
        public readonly int Slot;
        public readonly Sbx3Node Value, Body;
        public Sbx3Let(int slot, Sbx3Node value, Sbx3Node body) { Slot = slot; Value = value; Body = body; }
        public override SbxVal3 Eval(SbxVal3[] env)
        {
            env[Slot] = Value.Eval(env);
            return Body.Eval(env);
        }
    }

    public sealed class Sbx3Unary : Sbx3Node
    {
        public readonly char Op;
        public readonly Sbx3Node A;
        public Sbx3Unary(char op, Sbx3Node a) { Op = op; A = a; }
        public override SbxVal3 Eval(SbxVal3[] env)
        {
            var v = A.Eval(env);
            return Op == '-' ? SbxVal3.Neg(v) : SbxFuncEval.Not(v);
        }
    }

    public sealed class Sbx3Binary : Sbx3Node
    {
        public readonly string Op;
        internal readonly SbxBinOp OpKind;
        public readonly Sbx3Node A, B;
        public Sbx3Binary(string op, Sbx3Node a, Sbx3Node b)
        { Op = op; OpKind = ResolveOp(op); A = a; B = b; }

        private static SbxBinOp ResolveOp(string op) => op switch
        {
            "+"  => SbxBinOp.Add,
            "-"  => SbxBinOp.Sub,
            "*"  => SbxBinOp.Mul,
            "/"  => SbxBinOp.Div,
            "^"  => SbxBinOp.Pow,
            "<"  => SbxBinOp.Lt,
            ">"  => SbxBinOp.Gt,
            "<=" => SbxBinOp.Le,
            ">=" => SbxBinOp.Ge,
            "==" => SbxBinOp.Eq,
            "!=" => SbxBinOp.Ne,
            "&&" => SbxBinOp.And,
            "||" => SbxBinOp.Or,
            _    => throw new InvalidOperationException("Unknown op " + op),
        };

        public override SbxVal3 Eval(SbxVal3[] env)
        {
            // Short-circuit ops handled before eager arg eval.
            if (OpKind == SbxBinOp.And)
                return SbxVal3.R(A.Eval(env).AsBool() && B.Eval(env).AsBool() ? 1.0 : 0.0);
            if (OpKind == SbxBinOp.Or)
                return SbxVal3.R(A.Eval(env).AsBool() || B.Eval(env).AsBool() ? 1.0 : 0.0);

            var a = A.Eval(env);
            var b = B.Eval(env);
            return OpKind switch
            {
                SbxBinOp.Add => SbxVal3.Add(a, b),
                SbxBinOp.Sub => SbxVal3.Sub(a, b),
                SbxBinOp.Mul => SbxVal3.Mul(a, b),
                SbxBinOp.Div => SbxVal3.Div(a, b),
                SbxBinOp.Pow => SbxVal3.Pow(a, b),
                _            => SbxFuncEval.Compare(OpKind, a, b),
            };
        }
    }

    public sealed class Sbx3Ternary : Sbx3Node
    {
        public readonly Sbx3Node Cond, Then, Else;
        public Sbx3Ternary(Sbx3Node c, Sbx3Node t, Sbx3Node e) { Cond = c; Then = t; Else = e; }
        public override SbxVal3 Eval(SbxVal3[] env) => Cond.Eval(env).AsBool() ? Then.Eval(env) : Else.Eval(env);
    }

    /// <summary>Member access: .x / .y / .z / .w (Quat). Real broadcasts on
    /// .x/.y/.z (scalar repeats), Real.w is 0.</summary>
    public sealed class Sbx3Member : Sbx3Node
    {
        public readonly Sbx3Node Target;
        public readonly char Axis; // 'x','y','z','w'
        public Sbx3Member(Sbx3Node t, char a) { Target = t; Axis = a; }
        public override SbxVal3 Eval(SbxVal3[] env)
            => SbxFuncEval.MemberAxis(Target.Eval(env), Axis);
    }

    public sealed class Sbx3Call : Sbx3Node
    {
        public readonly string Name;
        internal readonly SbxFuncId Func;
        public readonly Sbx3Node[] Args;
        public Sbx3Call(string name, Sbx3Node[] args)
        { Name = name; Func = ResolveFunc(name); Args = args; }

        internal static SbxFuncId ResolveFunc(string name) => name switch
        {
            "vec"        => SbxFuncId.Vec,
            "rot"        => SbxFuncId.Rot,
            "spherefold" => SbxFuncId.SphereFold,
            "smin"       => SbxFuncId.SMin,
            "clamp"      => SbxFuncId.Clamp,
            "qvec"       => SbxFuncId.QVec,
            "qmul"       => SbxFuncId.QMul,
            "qpow"       => SbxFuncId.QPow,
            "qexp"       => SbxFuncId.QExp,
            "qlog"       => SbxFuncId.QLog,
            "qsqrt"      => SbxFuncId.QSqrt,
            "qinv"       => SbxFuncId.QInv,
            "qsin"       => SbxFuncId.QSin,
            "qcos"       => SbxFuncId.QCos,
            "qtan"       => SbxFuncId.QTan,
            "qsinh"      => SbxFuncId.QSinh,
            "qcosh"      => SbxFuncId.QCosh,
            "qtanh"      => SbxFuncId.QTanh,
            "qasin"      => SbxFuncId.QAsin,
            "qacos"      => SbxFuncId.QAcos,
            "qatan"      => SbxFuncId.QAtan,
            "qasinh"     => SbxFuncId.QAsinh,
            "qacosh"     => SbxFuncId.QAcosh,
            "qatanh"     => SbxFuncId.QAtanh,
            "qcsc"       => SbxFuncId.QCsc,
            "qsec"       => SbxFuncId.QSec,
            "qcot"       => SbxFuncId.QCot,
            "qcsch"      => SbxFuncId.QCsch,
            "qsech"      => SbxFuncId.QSech,
            "qcoth"      => SbxFuncId.QCoth,
            "dot"        => SbxFuncId.Dot,
            "cross"      => SbxFuncId.Cross,
            "triplex"    => SbxFuncId.Triplex,
            "boxfold"    => SbxFuncId.BoxFold,
            "mod"        => SbxFuncId.Mod,
            "pow"        => SbxFuncId.Pow2,
            "min"        => SbxFuncId.Min,
            "max"        => SbxFuncId.Max,
            "qconj"      => SbxFuncId.QConj,
            "length"     => SbxFuncId.Length,
            "normalize"  => SbxFuncId.Normalize,
            "absx"       => SbxFuncId.AbsX,
            "absy"       => SbxFuncId.AbsY,
            "absz"       => SbxFuncId.AbsZ,
            "floor"      => SbxFuncId.Floor,
            "sign"       => SbxFuncId.Sign,
            "sin"        => SbxFuncId.Sin,
            "cos"        => SbxFuncId.Cos,
            "tan"        => SbxFuncId.Tan,
            "sinh"       => SbxFuncId.Sinh,
            "cosh"       => SbxFuncId.Cosh,
            "tanh"       => SbxFuncId.Tanh,
            "exp"        => SbxFuncId.Exp,
            "log"        => SbxFuncId.Log,
            "sqrt"       => SbxFuncId.Sqrt,
            "abs"        => SbxFuncId.Abs,
            "norm"       => SbxFuncId.Norm,
            _ => throw new InvalidOperationException("Unknown function " + name),
        };

        public override SbxVal3 Eval(SbxVal3[] env)
        {
            // Evaluate present args left-to-right (preserves let-in-arg side
            // effects + eval order), then dispatch through the shared applier.
            // #283 — SbxFuncEval.Apply is the single source of truth for every
            // built-in's semantics, called by both this interpreter and the
            // Expression-tree compiler, so the two paths cannot diverge.
            SbxVal3 a0 = Args.Length > 0 ? Args[0].Eval(env) : default;
            SbxVal3 a1 = Args.Length > 1 ? Args[1].Eval(env) : default;
            SbxVal3 a2 = Args.Length > 2 ? Args[2].Eval(env) : default;
            SbxVal3 a3 = Args.Length > 3 ? Args[3].Eval(env) : default;
            return SbxFuncEval.Apply(Func, a0, a1, a2, a3);
        }
    }

    /// <summary>Single source of truth for the semantics of every built-in
    /// function. Takes already-evaluated argument values (up to four; unused
    /// slots are <c>default</c>) so it is callable both from the AST
    /// interpreter (<see cref="Sbx3Call.Eval"/>) and from the compiled
    /// Expression-tree kernel (#283). Keeping one implementation guarantees the
    /// interpreter and the compiled delegate are bit-identical.</summary>
    internal static class SbxFuncEval
    {
        public static SbxVal3 Apply(SbxFuncId func, SbxVal3 a0, SbxVal3 a1, SbxVal3 a2, SbxVal3 a3)
        {
            switch (func)
            {
                case SbxFuncId.Vec:    return SbxVal3.V(a0.AsReal(), a1.AsReal(), a2.AsReal());
                case SbxFuncId.QVec:   return SbxVal3.Q(a3.AsReal(), a0.AsReal(), a1.AsReal(), a2.AsReal());
                case SbxFuncId.QMul:   return SbxVal3.Q(a0.AsQuat() * a1.AsQuat());
                case SbxFuncId.QConj:  return SbxVal3.Q(a0.AsQuat().Conjugate());
                case SbxFuncId.QExp:   return SbxVal3.Q(Quat.Exp(a0.AsQuat()));
                case SbxFuncId.QLog:   return SbxVal3.Q(Quat.Log(a0.AsQuat()));
                case SbxFuncId.QSqrt:  return SbxVal3.Q(Quat.Sqrt(a0.AsQuat()));
                case SbxFuncId.QInv:   return SbxVal3.Q(Quat.Inverse(a0.AsQuat()));
                case SbxFuncId.QSin:   return SbxVal3.Q(Quat.Sin(a0.AsQuat()));
                case SbxFuncId.QCos:   return SbxVal3.Q(Quat.Cos(a0.AsQuat()));
                case SbxFuncId.QTan:   return SbxVal3.Q(Quat.Tan(a0.AsQuat()));
                case SbxFuncId.QSinh:  return SbxVal3.Q(Quat.Sinh(a0.AsQuat()));
                case SbxFuncId.QCosh:  return SbxVal3.Q(Quat.Cosh(a0.AsQuat()));
                case SbxFuncId.QTanh:  return SbxVal3.Q(Quat.Tanh(a0.AsQuat()));
                case SbxFuncId.QAsin:  return SbxVal3.Q(Quat.Asin(a0.AsQuat()));
                case SbxFuncId.QAcos:  return SbxVal3.Q(Quat.Acos(a0.AsQuat()));
                case SbxFuncId.QAtan:  return SbxVal3.Q(Quat.Atan(a0.AsQuat()));
                case SbxFuncId.QAsinh: return SbxVal3.Q(Quat.Asinh(a0.AsQuat()));
                case SbxFuncId.QAcosh: return SbxVal3.Q(Quat.Acosh(a0.AsQuat()));
                case SbxFuncId.QAtanh: return SbxVal3.Q(Quat.Atanh(a0.AsQuat()));
                case SbxFuncId.QCsc:   return SbxVal3.Q(Quat.Csc(a0.AsQuat()));
                case SbxFuncId.QSec:   return SbxVal3.Q(Quat.Sec(a0.AsQuat()));
                case SbxFuncId.QCot:   return SbxVal3.Q(Quat.Cot(a0.AsQuat()));
                case SbxFuncId.QCsch:  return SbxVal3.Q(Quat.Csch(a0.AsQuat()));
                case SbxFuncId.QSech:  return SbxVal3.Q(Quat.Sech(a0.AsQuat()));
                case SbxFuncId.QCoth:  return SbxVal3.Q(Quat.Coth(a0.AsQuat()));
                case SbxFuncId.QPow:   return SbxVal3.Pow(a0.IsQuat ? a0 : SbxVal3.Q(a0.AsQuat()), a1);
                case SbxFuncId.Length:   return SbxVal3.R(a0.AsVec().Length);
                case SbxFuncId.Dot:      return SbxVal3.R(Vec3.Dot(a0.AsVec(), a1.AsVec()));
                case SbxFuncId.Cross:    return SbxVal3.V(Vec3.Cross(a0.AsVec(), a1.AsVec()));
                case SbxFuncId.Normalize:return SbxVal3.V(a0.AsVec().Normalized());
                case SbxFuncId.Triplex:  return SbxVal3.V(Vec3.Pow(a0.AsVec(), a1.AsReal()));
                case SbxFuncId.Rot:      return SbxVal3.V(Vec3.Rot(a0.AsVec(), a1.AsVec(), a2.AsReal()));
                case SbxFuncId.BoxFold:  return SbxVal3.V(Vec3.BoxFold(a0.AsVec(), a1.AsReal()));
                case SbxFuncId.SphereFold:return SbxVal3.V(Vec3.SphereFold(a0.AsVec(), a1.AsReal(), a2.AsReal()));
                case SbxFuncId.AbsX:     return SbxVal3.V(Vec3.AbsX(a0.AsVec()));
                case SbxFuncId.AbsY:     return SbxVal3.V(Vec3.AbsY(a0.AsVec()));
                case SbxFuncId.AbsZ:     return SbxVal3.V(Vec3.AbsZ(a0.AsVec()));
                case SbxFuncId.Mod:      return SbxVal3.V(Vec3.Mod(a0.AsVec(), a1.AsReal()));
                case SbxFuncId.SMin:     return SbxVal3.R(Vec3.SMin(a0.AsReal(), a1.AsReal(), a2.AsReal()));
                case SbxFuncId.Pow2:     return SbxVal3.Pow(a0, a1);
                case SbxFuncId.Floor:    return SbxVal3.R(Math.Floor(a0.AsReal()));
                case SbxFuncId.Sign:     return SbxVal3.R(Math.Sign(a0.AsReal()));
                case SbxFuncId.Min:      return SbxVal3.R(Math.Min(a0.AsReal(), a1.AsReal()));
                case SbxFuncId.Max:      return SbxVal3.R(Math.Max(a0.AsReal(), a1.AsReal()));
                case SbxFuncId.Clamp:    return SbxVal3.R(Math.Clamp(a0.AsReal(), a1.AsReal(), a2.AsReal()));

                // Transcendentals: defined elementwise on Real/Vec only.
                // Per-component on Quat is geometrically meaningless (treats
                // the quaternion as a 4-tuple, not as a rotation/algebra
                // element), so reject explicitly.
                case SbxFuncId.Sin:  return ApplyScalar(a0, Math.Sin,  "sin");
                case SbxFuncId.Cos:  return ApplyScalar(a0, Math.Cos,  "cos");
                case SbxFuncId.Tan:  return ApplyScalar(a0, Math.Tan,  "tan");
                case SbxFuncId.Sinh: return ApplyScalar(a0, Math.Sinh, "sinh");
                case SbxFuncId.Cosh: return ApplyScalar(a0, Math.Cosh, "cosh");
                case SbxFuncId.Tanh: return ApplyScalar(a0, Math.Tanh, "tanh");
                case SbxFuncId.Exp:  return ApplyScalar(a0, Math.Exp,  "exp");
                case SbxFuncId.Log:  return ApplyScalar(a0, Math.Log,  "log");
                case SbxFuncId.Sqrt: return ApplyScalar(a0, Math.Sqrt, "sqrt");
                // abs is well-defined componentwise on Quat (per-axis fold).
                case SbxFuncId.Abs:  return ApplyAll(a0, Math.Abs);
                // #1100 — squared length: x² / dot(v, v) / |q|² (2D's norm).
                case SbxFuncId.Norm: return SbxVal3.R(a0.Kind switch
                {
                    SbxKind.Quat => a0.W * a0.W + a0.X * a0.X + a0.Y * a0.Y + a0.Z * a0.Z,
                    SbxKind.Vec  => a0.X * a0.X + a0.Y * a0.Y + a0.Z * a0.Z,
                    _            => a0.X * a0.X,
                });
            }
            throw new InvalidOperationException("Unknown function id " + func);
        }

        /// <summary>Member access .x/.y/.z/.w. Shared by the interpreter
        /// (<see cref="Sbx3Member"/>) and the compiler.</summary>
        public static SbxVal3 MemberAxis(SbxVal3 v, char axis) => axis switch
        {
            'x' => SbxVal3.R(v.X),
            'y' => SbxVal3.R(v.IsVecOrQuat ? v.Y : v.X),
            'z' => SbxVal3.R(v.IsVecOrQuat ? v.Z : v.X),
            'w' => SbxVal3.R(v.IsQuat ? v.W : 0.0),
            _   => throw new InvalidOperationException("Bad axis " + axis),
        };

        /// <summary>Logical NOT: 1 when the value is falsey, else 0.</summary>
        public static SbxVal3 Not(SbxVal3 v) => SbxVal3.R(v.AsBool() ? 0.0 : 1.0);

        /// <summary>Real-valued comparison ops (not And/Or — those short-circuit
        /// in the caller). Shared by interpreter + compiler.</summary>
        public static SbxVal3 Compare(SbxBinOp op, SbxVal3 a, SbxVal3 b)
        {
            double x = a.AsReal(), y = b.AsReal();
            return op switch
            {
                SbxBinOp.Lt => SbxVal3.R(x <  y ? 1.0 : 0.0),
                SbxBinOp.Gt => SbxVal3.R(x >  y ? 1.0 : 0.0),
                SbxBinOp.Le => SbxVal3.R(x <= y ? 1.0 : 0.0),
                SbxBinOp.Ge => SbxVal3.R(x >= y ? 1.0 : 0.0),
                SbxBinOp.Eq => SbxVal3.R(x == y ? 1.0 : 0.0),
                SbxBinOp.Ne => SbxVal3.R(x != y ? 1.0 : 0.0),
                _           => throw new InvalidOperationException("Not a comparison op " + op),
            };
        }

        /// <summary>Applies <paramref name="f"/> elementwise on Real or Vec.
        /// Throws on Quat — caller has misused a transcendental function on
        /// a quaternion value.</summary>
        private static SbxVal3 ApplyScalar(SbxVal3 v, Func<double, double> f, string name) => v.Kind switch
        {
            SbxKind.Quat => throw new InvalidOperationException(
                $"Function '{name}' is not defined componentwise on Quat. Project to a Vec3 component (e.g. q.x) first."),
            SbxKind.Vec  => SbxVal3.V(f(v.X), f(v.Y), f(v.Z)),
            _            => SbxVal3.R(f(v.X)),
        };

        /// <summary>Applies <paramref name="f"/> elementwise on every
        /// component, including W for Quat. Used by abs (per-axis fold).</summary>
        private static SbxVal3 ApplyAll(SbxVal3 v, Func<double, double> f) => v.Kind switch
        {
            SbxKind.Quat => SbxVal3.Q(f(v.W), f(v.X), f(v.Y), f(v.Z)),
            SbxKind.Vec  => SbxVal3.V(f(v.X), f(v.Y), f(v.Z)),
            _            => SbxVal3.R(f(v.X)),
        };
    }

    /// <summary>Parsed Sandbox-Bulb expression. Evaluated per Step call inside
    /// the numerical-DE raymarch driven by SandboxBulbCalculator (TBD).</summary>
    public sealed class SandboxBulbExpression
    {
        public Sbx3Node Root { get; }
        public int EnvSize { get; }

        public const int SlotZ = 0;
        public const int SlotC = 1;
        public const int SlotN = 2;
        public const int ReservedSlots = 3;

        /// <summary>Slot indices for extra scalar bindings supplied at parse time
        /// (named params + reserved time `t`). Empty when none.</summary>
        public IReadOnlyList<int> ExtraScalarSlots { get; }

        private SandboxBulbExpression(Sbx3Node root, int envSize, IReadOnlyList<int> extra)
        { Root = root; EnvSize = envSize; ExtraScalarSlots = extra; }

        public static SandboxBulbExpression Parse(string source)
            => Parse(source, Array.Empty<string>());

        /// <summary>Parse with named scalar bindings (params + `t`). Each name
        /// becomes an identifier usable in the expression; values are written
        /// per Step call via <see cref="EvalStep"/>.</summary>
        public static SandboxBulbExpression Parse(string source, IReadOnlyList<string> extraScalarNames)
        {
            return Run(new Builder(source, extraScalarNames), source);
        }

        /// <summary>Parse with a pre-built binding table — used by chain to
        /// share slot assignments across sequential step expressions.</summary>
        public static SandboxBulbExpression ParseWithScope(
            string source,
            IDictionary<string, int> bindings,
            int startEnvSize)
        {
            return Run(new Builder(source, bindings, startEnvSize), source, extras: Array.Empty<int>());
        }

        /// <summary>#1100 — function names, for Did-you-mean and help.</summary>
        public static readonly IReadOnlyList<string> FunctionNames = new[]
        {
            "sin", "cos", "tan", "sinh", "cosh", "tanh", "exp", "log", "sqrt", "abs", "norm",
            "length", "normalize", "absx", "absy", "absz", "floor", "sign",
            "dot", "cross", "triplex", "boxfold", "mod", "pow", "min", "max",
            "vec", "rot", "spherefold", "smin", "clamp", "qvec",
            "qmul", "qpow", "qconj", "qexp", "qlog", "qsqrt", "qinv",
            "qsin", "qcos", "qtan", "qsinh", "qcosh", "qtanh",
            "qasin", "qacos", "qatan", "qasinh", "qacosh", "qatanh",
            "qcsc", "qsec", "qcot", "qcsch", "qsech", "qcoth",
        };

        /// <summary>#1100 — parse with the PRE-#1100 (language version 1) rules:
        /// unary minus binds tighter than <c>^</c> (<c>-x^y</c> = <c>(-x)^y</c>).
        /// When <paramref name="edits"/> is given it collects the text edits that
        /// make the source mean the same under the current rules
        /// (BulbLanguageMigration applies them). Identifiers are lenient: an
        /// unknown name (a chain step output, a param) gets a fresh slot, so the
        /// migration needs no scope.</summary>
        public static SandboxBulbExpression ParseLegacy(string source, List<(int Pos, int Len, string Text)>? edits = null)
        {
            return Run(new Builder(source, Array.Empty<string>()) { Lenient = true }, source, legacy: true, edits: edits);
        }

        /// <summary>#1100 — current rules with lenient identifiers (see
        /// <see cref="ParseLegacy"/>); the migration's self-check parses with it.</summary>
        public static SandboxBulbExpression ParseLenient(string source)
        {
            return Run(new Builder(source, Array.Empty<string>()) { Lenient = true }, source);
        }

        /// <summary>#1100 — the tree as an S-expression (slots as <c>$k</c>), for
        /// the migration self-check and grammar tests.</summary>
        public static string ToSExpression(Sbx3Node node)
        {
            var sb = new System.Text.StringBuilder();
            Write(node, sb);
            return sb.ToString();

            static void Write(Sbx3Node n, System.Text.StringBuilder sb)
            {
                switch (n)
                {
                    case Sbx3Const k:
                        sb.Append(k.V.Kind == SbxKind.Real
                            ? k.V.X.ToString("R", CultureInfo.InvariantCulture)
                            : $"<{k.V.Kind} {k.V.W.ToString("R", CultureInfo.InvariantCulture)} {k.V.X.ToString("R", CultureInfo.InvariantCulture)} {k.V.Y.ToString("R", CultureInfo.InvariantCulture)} {k.V.Z.ToString("R", CultureInfo.InvariantCulture)}>");
                        break;
                    case Sbx3Slot sl: sb.Append('$').Append(sl.Slot); break;
                    case Sbx3Let l:
                        sb.Append("(let $").Append(l.Slot).Append(' ');
                        Write(l.Value, sb); sb.Append(' '); Write(l.Body, sb); sb.Append(')');
                        break;
                    case Sbx3Unary u:
                        sb.Append(u.Op == '-' ? "(neg " : "(not "); Write(u.A, sb); sb.Append(')');
                        break;
                    case Sbx3Binary b:
                        sb.Append('(').Append(b.Op).Append(' '); Write(b.A, sb); sb.Append(' '); Write(b.B, sb); sb.Append(')');
                        break;
                    case Sbx3Ternary t:
                        sb.Append("(? "); Write(t.Cond, sb); sb.Append(' '); Write(t.Then, sb); sb.Append(' '); Write(t.Else, sb); sb.Append(')');
                        break;
                    case Sbx3Member m:
                        sb.Append("(.").Append(m.Axis).Append(' '); Write(m.Target, sb); sb.Append(')');
                        break;
                    case Sbx3Call c:
                        sb.Append('(').Append(c.Name);
                        foreach (var a in c.Args) { sb.Append(' '); Write(a, sb); }
                        sb.Append(')');
                        break;
                    default: sb.Append('?').Append(n.GetType().Name); break;
                }
            }
        }

        public SbxVal3[] NewEnv() => new SbxVal3[EnvSize];

        // #283 — optional Expression-tree-compiled root. Null until TryCompile
        // succeeds; when set, the Eval*Compiled entries dispatch through it
        // instead of the virtual AST walk. Correctness is identical because the
        // compiler and the interpreter share SbxFuncEval.Apply + the SbxVal3
        // static ops. Falls back silently to the interpreter if compilation of
        // a construct is unsupported.
        private Func<SbxVal3[], SbxVal3>? _compiledRoot;

        /// <summary>True once a compiled root delegate is available.</summary>
        public bool IsCompiled => _compiledRoot != null;

        /// <summary>Attempt to JIT-compile the AST to a delegate (System.Linq.
        /// Expressions — not Roslyn, no source compile, #27-safe: only the same
        /// fixed op surface the interpreter already invokes). Idempotent;
        /// returns true when a compiled root is present afterwards.</summary>
        public bool TryCompile()
        {
            if (_compiledRoot != null) return true;
            _compiledRoot = SandboxBulbCompiler.TryCompile(Root);
            return _compiledRoot != null;
        }

        /// <summary>Evaluate Step(z, c, n) → Vec3. env must come from <see cref="NewEnv"/>.
        /// extras (when provided) must match the names passed to <see cref="Parse(string, IReadOnlyList{string})"/>.</summary>
        public Vec3 EvalStep(Vec3 z, Vec3 c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            env[SlotZ] = SbxVal3.V(z);
            env[SlotC] = SbxVal3.V(c);
            env[SlotN] = SbxVal3.R(n);
            for (int i = 0; i < ExtraScalarSlots.Count && i < extras.Length; i++)
                env[ExtraScalarSlots[i]] = SbxVal3.R(extras[i]);
            var root = _compiledRoot;
            return (root != null ? root(env) : Root.Eval(env)).AsVec();
        }

        /// <summary>Quat-mode evaluator. Same AST, Quat-tagged z and c slots.
        /// Result is projected back to Quat; .AsQuat handles Vec/Real fallbacks
        /// (X/Y/Z used; W defaults to 0).</summary>
        public Quat EvalStepQuat(Quat z, Quat c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            env[SlotZ] = SbxVal3.Q(z);
            env[SlotC] = SbxVal3.Q(c);
            env[SlotN] = SbxVal3.R(n);
            for (int i = 0; i < ExtraScalarSlots.Count && i < extras.Length; i++)
                env[ExtraScalarSlots[i]] = SbxVal3.R(extras[i]);
            var root = _compiledRoot;
            return (root != null ? root(env) : Root.Eval(env)).AsQuat();
        }

        // ── Parser ────────────────────────────────────────────────────────────
        // #1101 — the syntax lives in EquationFrontEnd (shared with the 2D
        // language); this builder supplies the 3D language: real / vec / quat
        // values, member access, the bulb function table, params and chain scope.

        private static readonly EquationSyntax Syntax = new(
            AllowMembers: true,
            TypeKeywords: new HashSet<string>(StringComparer.Ordinal) { "var", "Vec3", "Quat", "double", "int", "float" },
            NonAssignableWords: new HashSet<string>(StringComparer.Ordinal) { "let", "in", "return", "if", "then", "else" });

        private static SandboxBulbExpression Run(Builder b, string source, bool legacy = false,
            List<(int Pos, int Len, string Text)>? edits = null, IReadOnlyList<int>? extras = null)
        {
            var root = new EquationFrontEnd<Sbx3Node>(source ?? string.Empty, b, Syntax, legacy, edits).ParseProgram();
            return new SandboxBulbExpression(root, b.EnvSize, extras ?? b.ExtraSlots);
        }

        private sealed class Builder : IEquationBuilder<Sbx3Node>
        {
            private readonly string _src;
            private readonly Dictionary<string, int> _scope = new(StringComparer.Ordinal);
            public int EnvSize;
            public readonly List<int> ExtraSlots = new();
            /// <summary>#1100 — unknown names get a fresh slot (migration parses).</summary>
            public bool Lenient;

            /// <summary>Adopt an externally-built scope table (shared across chain
            /// steps). EnvSize starts at the supplied value and grows for lets.</summary>
            public Builder(string src, IDictionary<string, int> bindings, int startEnvSize)
            {
                _src = src ?? string.Empty;
                foreach (var kv in bindings) _scope[kv.Key] = kv.Value;
                EnvSize = startEnvSize;
            }

            public Builder(string src, IReadOnlyList<string> extraScalarNames)
            {
                _src = src ?? string.Empty;
                _scope["z"] = SlotZ;
                _scope["c"] = SlotC;
                _scope["n"] = SlotN;
                EnvSize = ReservedSlots;
                if (extraScalarNames != null)
                {
                    foreach (var name in extraScalarNames)
                    {
                        if (string.IsNullOrEmpty(name)) { ExtraSlots.Add(-1); continue; }
                        if (IsReservedName(name)) throw new SbxParseException($"Reserved name '{name}' cannot be a param.", 0);
                        if (_scope.ContainsKey(name)) throw new SbxParseException($"Duplicate param '{name}'.", 0);
                        int slot = EnvSize++;
                        _scope[name] = slot;
                        ExtraSlots.Add(slot);
                    }
                }
            }

            private static bool IsReservedName(string name) =>
                name is "z" or "c" or "n" or "pi" or "e" or "let" or "in"
                    or "if" or "then" or "else" or "return";

            private static bool IsKeyword(string w) =>
                w is "let" or "in" or "return" or "if" or "then" or "else";

            // Errors in the 2D language's format: "… at line L, col C. Did you
            // mean 'x'?", keeping the span for the editor.
            private SbxParseException Fail(string what, int pos, int len = 1, string didYouMean = "")
                => new($"{what} at {EquationSyntax.At(_src, pos)}.{didYouMean}", pos, len);

            public Exception Error(string message, int position, int length = 1)
                => new SbxParseException(message, position, length);

            public Sbx3Node Number(double value) => new Sbx3Const(SbxVal3.R(value));

            public Sbx3Node Identifier(string name, int position)
            {
                if (name == "pi") return new Sbx3Const(SbxVal3.R(Math.PI));
                if (name == "e")  return new Sbx3Const(SbxVal3.R(Math.E));
                if (_scope.TryGetValue(name, out int slot)) return new Sbx3Slot(slot);
                if (Lenient && !IsKeyword(name)) { int s2 = EnvSize++; _scope[name] = s2; return new Sbx3Slot(s2); }
                throw Fail($"Unknown identifier '{name}'", position, name.Length,
                    EquationSyntax.DidYouMean(name, _scope.Keys.Concat(new[] { "pi", "e" }).Concat(FunctionNames)));
            }

            public Sbx3Node Call(string name, int nameStart, IReadOnlyList<Sbx3Node> args)
            {
                string lname = name.ToLowerInvariant();
                int expected = ArityOf(lname);
                if (expected < 0)
                    throw Fail($"Unknown function '{name}'", nameStart, name.Length, EquationSyntax.DidYouMean(lname, FunctionNames));
                if (expected != int.MaxValue && args.Count != expected)
                    throw Fail($"Function '{name}' takes {expected} arg(s), got {args.Count}", nameStart, name.Length);
                return new Sbx3Call(lname, args.ToArray());
            }

            private static int ArityOf(string name) => name switch
            {
                "sin" or "cos" or "tan" or "sinh" or "cosh" or "tanh"
                    or "exp" or "log" or "sqrt" or "abs" or "norm"
                    or "length" or "normalize"
                    or "absx" or "absy" or "absz"
                    or "floor" or "sign"
                    or "qconj"
                    or "qexp" or "qlog" or "qsqrt" or "qinv"
                    or "qsin" or "qcos" or "qtan"
                    or "qsinh" or "qcosh" or "qtanh"
                    or "qasin" or "qacos" or "qatan"
                    or "qasinh" or "qacosh" or "qatanh"
                    or "qcsc" or "qsec" or "qcot"
                    or "qcsch" or "qsech" or "qcoth" => 1,
                "dot" or "cross" or "triplex" or "boxfold" or "mod"
                    or "pow" or "min" or "max"
                    or "qmul" or "qpow" => 2,
                "vec" or "rot" or "spherefold" or "smin" or "clamp" => 3,
                "qvec" => 4,
                _ => -1
            };

            public Sbx3Node Unary(char op, Sbx3Node operand) => new Sbx3Unary(op, operand);
            public Sbx3Node Binary(string op, Sbx3Node a, Sbx3Node b) => new Sbx3Binary(op, a, b);
            public Sbx3Node Ternary(Sbx3Node cond, Sbx3Node then, Sbx3Node otherwise) => new Sbx3Ternary(cond, then, otherwise);
            public Sbx3Node Member(Sbx3Node target, char axis) => new Sbx3Member(target, axis);
            public Sbx3Node Let(int slot, Sbx3Node value, Sbx3Node body) => new Sbx3Let(slot, value, body);
            public Sbx3Node IfThenCondition(Sbx3Node cond, List<(int Pos, int Len, string Text)>? edits) => cond;

            public void CheckLetName(string name, int position)
            {
                if (IsReservedName(name)) throw Fail($"Cannot rebind reserved name '{name}'", position, name.Length);
            }

            public void CheckAssignName(string name, int position)
            {
                if (name is "pi" or "e") throw Fail($"Cannot assign to the constant '{name}'", position);
            }

            public (int Slot, int Prior, bool HadPrior) Bind(string name)
            {
                bool hadPrior = _scope.TryGetValue(name, out int prior);
                int slot = EnvSize++;
                _scope[name] = slot;
                return (slot, prior, hadPrior);
            }

            public void Unbind(string name, int prior, bool hadPrior)
            {
                if (hadPrior) _scope[name] = prior;
                else _scope.Remove(name);
            }

            public bool TryGetBound(string name, out Sbx3Node node)
            {
                if (_scope.TryGetValue(name, out int slot)) { node = new Sbx3Slot(slot); return true; }
                node = null!;
                return false;
            }

            public int Mark() => EnvSize;
            public void Reset(int mark) => EnvSize = mark;
        }
    }
}
