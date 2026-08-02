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
// Grammar (right-recursive descent) — superset of SandboxExpression:
//   expr     := let_expr
//   let_expr := "let" IDENT "=" expr "in" expr | ternary
//   ternary  := or_expr ("?" expr ":" expr)?
//   or_expr  := and_expr ("||" and_expr)*
//   and_expr := not_expr ("&&" not_expr)*
//   not_expr := "!" not_expr | cmp_expr
//   cmp_expr := add_expr ((<|>|<=|>=|==|!=) add_expr)?
//   add_expr := mul_expr (("+"|"-") mul_expr)*
//   mul_expr := pow_expr (("*"|"/") pow_expr)*
//   pow_expr := unary ("^" pow_expr)?         ; right-assoc
//   unary    := "-" unary | primary
//   primary  := NUMBER | IDENT member* | IDENT "(" args ")" member* | "(" expr ")" member*
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
//   length(vec)                                         vec3 -> real
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

namespace FracturingFog.Models
{
    /// <summary>Parser error carrying the offending source position so the
    /// editor can highlight the failing span. <see cref="Position"/> is a
    /// 0-based character index into the original source.</summary>
    public sealed class SbxParseException : FormatException
    {
        public int Position { get; }
        public int Length { get; }
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
            return Op == '-' ? SbxVal3.Neg(v) : SbxVal3.R(v.AsBool() ? 0.0 : 1.0);
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
                SbxBinOp.Lt  => SbxVal3.R(a.AsReal() <  b.AsReal() ? 1.0 : 0.0),
                SbxBinOp.Gt  => SbxVal3.R(a.AsReal() >  b.AsReal() ? 1.0 : 0.0),
                SbxBinOp.Le  => SbxVal3.R(a.AsReal() <= b.AsReal() ? 1.0 : 0.0),
                SbxBinOp.Ge  => SbxVal3.R(a.AsReal() >= b.AsReal() ? 1.0 : 0.0),
                SbxBinOp.Eq  => SbxVal3.R(a.AsReal() == b.AsReal() ? 1.0 : 0.0),
                SbxBinOp.Ne  => SbxVal3.R(a.AsReal() != b.AsReal() ? 1.0 : 0.0),
                _            => throw new InvalidOperationException("Unknown op " + Op),
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
        {
            var v = Target.Eval(env);
            return Axis switch
            {
                'x' => SbxVal3.R(v.X),
                'y' => SbxVal3.R(v.IsVecOrQuat ? v.Y : v.X),
                'z' => SbxVal3.R(v.IsVecOrQuat ? v.Z : v.X),
                'w' => SbxVal3.R(v.IsQuat ? v.W : 0.0),
                _   => throw new InvalidOperationException("Bad axis " + Axis)
            };
        }
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
            _ => throw new InvalidOperationException("Unknown function " + name),
        };

        public override SbxVal3 Eval(SbxVal3[] env)
        {
            switch (Func)
            {
                case SbxFuncId.Vec:
                {
                    var a = Args[0].Eval(env);
                    var b = Args[1].Eval(env);
                    var c = Args[2].Eval(env);
                    return SbxVal3.V(a.AsReal(), b.AsReal(), c.AsReal());
                }
                case SbxFuncId.QVec:
                {
                    var qx = Args[0].Eval(env).AsReal();
                    var qy = Args[1].Eval(env).AsReal();
                    var qz = Args[2].Eval(env).AsReal();
                    var qw = Args[3].Eval(env).AsReal();
                    return SbxVal3.Q(qw, qx, qy, qz);
                }
                case SbxFuncId.QMul:   return SbxVal3.Q(Args[0].Eval(env).AsQuat() * Args[1].Eval(env).AsQuat());
                case SbxFuncId.QConj:  return SbxVal3.Q(Args[0].Eval(env).AsQuat().Conjugate());
                case SbxFuncId.QExp:   return SbxVal3.Q(Quat.Exp(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QLog:   return SbxVal3.Q(Quat.Log(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QSqrt:  return SbxVal3.Q(Quat.Sqrt(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QInv:   return SbxVal3.Q(Quat.Inverse(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QSin:   return SbxVal3.Q(Quat.Sin(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCos:   return SbxVal3.Q(Quat.Cos(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QTan:   return SbxVal3.Q(Quat.Tan(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QSinh:  return SbxVal3.Q(Quat.Sinh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCosh:  return SbxVal3.Q(Quat.Cosh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QTanh:  return SbxVal3.Q(Quat.Tanh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAsin:  return SbxVal3.Q(Quat.Asin(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAcos:  return SbxVal3.Q(Quat.Acos(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAtan:  return SbxVal3.Q(Quat.Atan(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAsinh: return SbxVal3.Q(Quat.Asinh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAcosh: return SbxVal3.Q(Quat.Acosh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QAtanh: return SbxVal3.Q(Quat.Atanh(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCsc:   return SbxVal3.Q(Quat.Csc(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QSec:   return SbxVal3.Q(Quat.Sec(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCot:   return SbxVal3.Q(Quat.Cot(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCsch:  return SbxVal3.Q(Quat.Csch(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QSech:  return SbxVal3.Q(Quat.Sech(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QCoth:  return SbxVal3.Q(Quat.Coth(Args[0].Eval(env).AsQuat()));
                case SbxFuncId.QPow:
                {
                    var qa = Args[0].Eval(env);
                    var qb = Args[1].Eval(env);
                    return SbxVal3.Pow(qa.IsQuat ? qa : SbxVal3.Q(qa.AsQuat()), qb);
                }
                case SbxFuncId.Length:   return SbxVal3.R(Args[0].Eval(env).AsVec().Length);
                case SbxFuncId.Dot:      return SbxVal3.R(Vec3.Dot(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsVec()));
                case SbxFuncId.Cross:    return SbxVal3.V(Vec3.Cross(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsVec()));
                case SbxFuncId.Normalize:return SbxVal3.V(Args[0].Eval(env).AsVec().Normalized());
                case SbxFuncId.Triplex:  return SbxVal3.V(Vec3.Pow(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsReal()));
                case SbxFuncId.Rot:      return SbxVal3.V(Vec3.Rot(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsVec(), Args[2].Eval(env).AsReal()));
                case SbxFuncId.BoxFold:  return SbxVal3.V(Vec3.BoxFold(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsReal()));
                case SbxFuncId.SphereFold:
                {
                    var v = Args[0].Eval(env).AsVec();
                    return SbxVal3.V(Vec3.SphereFold(v, Args[1].Eval(env).AsReal(), Args[2].Eval(env).AsReal()));
                }
                case SbxFuncId.AbsX:     return SbxVal3.V(Vec3.AbsX(Args[0].Eval(env).AsVec()));
                case SbxFuncId.AbsY:     return SbxVal3.V(Vec3.AbsY(Args[0].Eval(env).AsVec()));
                case SbxFuncId.AbsZ:     return SbxVal3.V(Vec3.AbsZ(Args[0].Eval(env).AsVec()));
                case SbxFuncId.Mod:      return SbxVal3.V(Vec3.Mod(Args[0].Eval(env).AsVec(), Args[1].Eval(env).AsReal()));
                case SbxFuncId.SMin:
                    return SbxVal3.R(Vec3.SMin(Args[0].Eval(env).AsReal(), Args[1].Eval(env).AsReal(), Args[2].Eval(env).AsReal()));
                case SbxFuncId.Pow2:
                {
                    var pa = Args[0].Eval(env);
                    var pb = Args[1].Eval(env);
                    return SbxVal3.Pow(pa, pb);
                }
                case SbxFuncId.Floor:    return SbxVal3.R(Math.Floor(Args[0].Eval(env).AsReal()));
                case SbxFuncId.Sign:     return SbxVal3.R(Math.Sign(Args[0].Eval(env).AsReal()));
                case SbxFuncId.Min:      return SbxVal3.R(Math.Min(Args[0].Eval(env).AsReal(), Args[1].Eval(env).AsReal()));
                case SbxFuncId.Max:      return SbxVal3.R(Math.Max(Args[0].Eval(env).AsReal(), Args[1].Eval(env).AsReal()));
                case SbxFuncId.Clamp:    return SbxVal3.R(Math.Clamp(Args[0].Eval(env).AsReal(), Args[1].Eval(env).AsReal(), Args[2].Eval(env).AsReal()));
            }

            // Single-arg scalar/componentwise math.
            var x = Args[0].Eval(env);
            return Func switch
            {
                // Transcendentals: defined elementwise on Real/Vec only.
                // Per-component on Quat is geometrically meaningless (treats
                // the quaternion as a 4-tuple, not as a rotation/algebra
                // element), so reject explicitly.
                SbxFuncId.Sin  => ApplyScalar(x, Math.Sin,  "sin"),
                SbxFuncId.Cos  => ApplyScalar(x, Math.Cos,  "cos"),
                SbxFuncId.Tan  => ApplyScalar(x, Math.Tan,  "tan"),
                SbxFuncId.Sinh => ApplyScalar(x, Math.Sinh, "sinh"),
                SbxFuncId.Cosh => ApplyScalar(x, Math.Cosh, "cosh"),
                SbxFuncId.Tanh => ApplyScalar(x, Math.Tanh, "tanh"),
                SbxFuncId.Exp  => ApplyScalar(x, Math.Exp,  "exp"),
                SbxFuncId.Log  => ApplyScalar(x, Math.Log,  "log"),
                SbxFuncId.Sqrt => ApplyScalar(x, Math.Sqrt, "sqrt"),
                // abs is well-defined componentwise on Quat (per-axis fold).
                SbxFuncId.Abs  => ApplyAll(x, Math.Abs),
                _ => throw new InvalidOperationException("Unknown function " + Name),
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
            var p = new Parser(source, extraScalarNames);
            var root = p.ParseProgram();
            return new SandboxBulbExpression(root, p.EnvSize, p.ExtraSlots);
        }

        /// <summary>Parse with a pre-built binding table — used by chain to
        /// share slot assignments across sequential step expressions.</summary>
        public static SandboxBulbExpression ParseWithScope(
            string source,
            IDictionary<string, int> bindings,
            int startEnvSize)
        {
            var p = new Parser(source, bindings, startEnvSize);
            var root = p.ParseProgram();
            return new SandboxBulbExpression(root, p.EnvSize, Array.Empty<int>());
        }

        public SbxVal3[] NewEnv() => new SbxVal3[EnvSize];

        /// <summary>Evaluate Step(z, c, n) → Vec3. env must come from <see cref="NewEnv"/>.
        /// extras (when provided) must match the names passed to <see cref="Parse(string, IReadOnlyList{string})"/>.</summary>
        public Vec3 EvalStep(Vec3 z, Vec3 c, int n, SbxVal3[] env, ReadOnlySpan<double> extras = default)
        {
            env[SlotZ] = SbxVal3.V(z);
            env[SlotC] = SbxVal3.V(c);
            env[SlotN] = SbxVal3.R(n);
            for (int i = 0; i < ExtraScalarSlots.Count && i < extras.Length; i++)
                env[ExtraScalarSlots[i]] = SbxVal3.R(extras[i]);
            return Root.Eval(env).AsVec();
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
            return Root.Eval(env).AsQuat();
        }

        // ── Parser ────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly string _src;
            private int _pos;
            private readonly Dictionary<string, int> _scope = new(StringComparer.Ordinal);
            public int EnvSize;
            public readonly List<int> ExtraSlots = new();

            public Parser(string src) : this(src, Array.Empty<string>()) { }

            /// <summary>Adopt an externally-built scope table (shared across
            /// chain steps). Bindings are taken as-is; EnvSize starts at the
            /// supplied value and grows for let-bindings.</summary>
            public Parser(string src, IDictionary<string, int> bindings, int startEnvSize)
            {
                _src = src ?? string.Empty;
                _pos = 0;
                foreach (var kv in bindings) _scope[kv.Key] = kv.Value;
                EnvSize = startEnvSize;
            }

            public Parser(string src, IReadOnlyList<string> extraScalarNames)
            {
                _src = src ?? string.Empty;
                _pos = 0;
                _scope["z"] = SlotZ;
                _scope["c"] = SlotC;
                _scope["n"] = SlotN;
                EnvSize = ReservedSlots;
                if (extraScalarNames != null)
                {
                    foreach (var name in extraScalarNames)
                    {
                        if (string.IsNullOrEmpty(name)) { ExtraSlots.Add(-1); continue; }
                        if (IsReservedName(name)) throw new SbxParseException($"Reserved name '{name}' cannot be a param", 0);
                        if (_scope.ContainsKey(name)) throw new SbxParseException($"Duplicate param '{name}'", 0);
                        int slot = EnvSize++;
                        _scope[name] = slot;
                        ExtraSlots.Add(slot);
                    }
                }
            }

            public Sbx3Node ParseProgram()
            {
                SkipWs();
                var node = ParseExpr();
                SkipWs();
                if (_pos < _src.Length)
                    throw new SbxParseException($"Unexpected '{_src[_pos]}' at position {_pos}", _pos);
                return node;
            }

            private Sbx3Node ParseExpr() => ParseLet();

            private Sbx3Node ParseLet()
            {
                SkipWs();
                if (MatchKeyword("let"))
                {
                    SkipWs();
                    string name = ReadIdent();
                    if (string.IsNullOrEmpty(name)) throw new SbxParseException("Expected identifier after 'let'", _pos);
                    if (IsReservedName(name)) throw new SbxParseException($"Cannot rebind reserved name '{name}'", _pos - name.Length, name.Length);
                    SkipWs();
                    Expect('=');
                    var valueExpr = ParseExpr();
                    SkipWs();
                    if (!MatchKeyword("in")) throw new SbxParseException("Expected 'in' in let-expression", _pos);

                    bool hadPrior = _scope.TryGetValue(name, out int prior);
                    int slot = EnvSize++;
                    _scope[name] = slot;
                    try
                    {
                        var body = ParseExpr();
                        return new Sbx3Let(slot, valueExpr, body);
                    }
                    finally
                    {
                        if (hadPrior) _scope[name] = prior;
                        else _scope.Remove(name);
                    }
                }
                return ParseTernary();
            }

            private Sbx3Node ParseTernary()
            {
                var cond = ParseOr();
                SkipWs();
                if (Peek() == '?')
                {
                    _pos++;
                    var thenN = ParseExpr();
                    SkipWs();
                    Expect(':');
                    var elseN = ParseExpr();
                    return new Sbx3Ternary(cond, thenN, elseN);
                }
                return cond;
            }

            private Sbx3Node ParseOr()
            {
                var left = ParseAnd();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '|' && Peek(1) == '|') { _pos += 2; left = new Sbx3Binary("||", left, ParseAnd()); }
                    else break;
                }
                return left;
            }

            private Sbx3Node ParseAnd()
            {
                var left = ParseNot();
                while (true)
                {
                    SkipWs();
                    if (Peek() == '&' && Peek(1) == '&') { _pos += 2; left = new Sbx3Binary("&&", left, ParseNot()); }
                    else break;
                }
                return left;
            }

            private Sbx3Node ParseNot()
            {
                SkipWs();
                if (Peek() == '!' && Peek(1) != '=')
                {
                    _pos++;
                    return new Sbx3Unary('!', ParseNot());
                }
                return ParseCmp();
            }

            private Sbx3Node ParseCmp()
            {
                var left = ParseAdd();
                SkipWs();
                string? op = null;
                if (Peek() == '<')      op = Peek(1) == '=' ? "<=" : "<";
                else if (Peek() == '>') op = Peek(1) == '=' ? ">=" : ">";
                else if (Peek() == '=' && Peek(1) == '=') op = "==";
                else if (Peek() == '!' && Peek(1) == '=') op = "!=";
                if (op == null) return left;
                _pos += op.Length;
                var right = ParseAdd();
                return new Sbx3Binary(op, left, right);
            }

            private Sbx3Node ParseAdd()
            {
                var left = ParseMul();
                while (true)
                {
                    SkipWs();
                    char p = Peek();
                    if (p == '+' || p == '-') { _pos++; left = new Sbx3Binary(p.ToString(), left, ParseMul()); }
                    else break;
                }
                return left;
            }

            private Sbx3Node ParseMul()
            {
                var left = ParsePow();
                while (true)
                {
                    SkipWs();
                    char p = Peek();
                    if (p == '*' || p == '/') { _pos++; left = new Sbx3Binary(p.ToString(), left, ParsePow()); }
                    else break;
                }
                return left;
            }

            private Sbx3Node ParsePow()
            {
                var left = ParseUnary();
                SkipWs();
                if (Peek() == '^') { _pos++; return new Sbx3Binary("^", left, ParsePow()); }
                return left;
            }

            private Sbx3Node ParseUnary()
            {
                SkipWs();
                if (Peek() == '-') { _pos++; return new Sbx3Unary('-', ParseUnary()); }
                if (Peek() == '+') { _pos++; return ParseUnary(); }
                return ParsePrimary();
            }

            private Sbx3Node ParsePrimary()
            {
                SkipWs();
                if (_pos >= _src.Length) throw new SbxParseException("Unexpected end of expression", Math.Max(0, _src.Length - 1));
                char p = Peek();
                Sbx3Node node;
                if (p == '(')
                {
                    _pos++;
                    node = ParseExpr();
                    SkipWs();
                    Expect(')');
                }
                else if (IsDigit(p) || (p == '.' && _pos + 1 < _src.Length && IsDigit(_src[_pos + 1])))
                {
                    node = ParseNumber();
                }
                else if (IsIdentStart(p))
                {
                    int identStart = _pos;
                    string name = ReadIdent();
                    SkipWs();
                    if (Peek() == '(') { node = ParseCall(name); }
                    else if (name == "pi") { node = new Sbx3Const(SbxVal3.R(Math.PI)); }
                    else if (name == "e")  { node = new Sbx3Const(SbxVal3.R(Math.E)); }
                    else if (_scope.TryGetValue(name, out int slot)) { node = new Sbx3Slot(slot); }
                    else throw new SbxParseException($"Unknown identifier '{name}'", identStart, name.Length);
                }
                else
                {
                    throw new SbxParseException($"Unexpected character '{p}' at {_pos}", _pos);
                }

                // Member access chain: foo.x.y etc — .x .y .z (Vec/Quat) or .w (Quat).
                while (true)
                {
                    SkipWs();
                    if (Peek() != '.') break;
                    if (_pos + 1 < _src.Length && IsDigit(_src[_pos + 1])) break;
                    _pos++;
                    SkipWs();
                    char ax = Peek();
                    if (ax != 'x' && ax != 'y' && ax != 'z' && ax != 'w')
                        throw new SbxParseException($"Expected .x/.y/.z/.w at {_pos}", _pos);
                    _pos++;
                    node = new Sbx3Member(node, ax);
                }
                return node;
            }

            private Sbx3Node ParseCall(string name)
            {
                _pos++; // consume '('
                var args = new List<Sbx3Node>();
                SkipWs();
                if (Peek() != ')')
                {
                    args.Add(ParseExpr());
                    SkipWs();
                    while (Peek() == ',') { _pos++; args.Add(ParseExpr()); SkipWs(); }
                }
                Expect(')');

                string lname = name.ToLowerInvariant();
                int expected = ArityOf(lname);
                if (expected < 0) throw new SbxParseException($"Unknown function '{name}'", _pos - name.Length - 1, name.Length);
                if (expected != int.MaxValue && args.Count != expected)
                    throw new SbxParseException($"Function '{name}' takes {expected} arg(s), got {args.Count}", _pos - 1, 1);
                return new Sbx3Call(lname, args.ToArray());
            }

            private static int ArityOf(string name) => name switch
            {
                "sin" or "cos" or "tan" or "sinh" or "cosh" or "tanh"
                    or "exp" or "log" or "sqrt" or "abs"
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

            private Sbx3Node ParseNumber()
            {
                int start = _pos;
                while (_pos < _src.Length && (IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
                {
                    _pos++;
                    if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
                    while (_pos < _src.Length && IsDigit(_src[_pos])) _pos++;
                }
                string tok = _src.Substring(start, _pos - start);
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw new SbxParseException($"Invalid number '{tok}' at {start}", start, tok.Length);
                return new Sbx3Const(SbxVal3.R(d));
            }

            // ── Helpers ───────────────────────────────────────────────────────

            private char Peek(int offset = 0)
                => (_pos + offset < _src.Length) ? _src[_pos + offset] : '\0';

            private void SkipWs()
            {
                // Skips whitespace and comments. `//` runs to end-of-line;
                // `/* */` spans lines. A lone `/` is left for the division
                // operator. #27 Phase 2a — the built-in bulb presets carry
                // explanatory `//` comments; the DSL must accept them so the
                // migrated presets (and hand-authored bulbs) keep their notes.
                while (_pos < _src.Length)
                {
                    char ch = _src[_pos];
                    if (char.IsWhiteSpace(ch)) { _pos++; continue; }
                    if (ch == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
                    {
                        _pos += 2;
                        while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
                        continue;
                    }
                    if (ch == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '*')
                    {
                        _pos += 2;
                        while (_pos + 1 < _src.Length && !(_src[_pos] == '*' && _src[_pos + 1] == '/')) _pos++;
                        _pos = Math.Min(_src.Length, _pos + 2);
                        continue;
                    }
                    break;
                }
            }

            private void Expect(char c)
            {
                SkipWs();
                if (_pos >= _src.Length || _src[_pos] != c)
                    throw new SbxParseException($"Expected '{c}' at position {_pos}", _pos);
                _pos++;
            }

            private string ReadIdent()
            {
                int start = _pos;
                if (_pos >= _src.Length || !IsIdentStart(_src[_pos])) return string.Empty;
                _pos++;
                while (_pos < _src.Length && IsIdentCont(_src[_pos])) _pos++;
                return _src.Substring(start, _pos - start);
            }

            private bool MatchKeyword(string kw)
            {
                SkipWs();
                if (_pos + kw.Length > _src.Length) return false;
                if (string.CompareOrdinal(_src, _pos, kw, 0, kw.Length) != 0) return false;
                int next = _pos + kw.Length;
                if (next < _src.Length && IsIdentCont(_src[next])) return false;
                _pos = next;
                return true;
            }

            private static bool IsReservedName(string name) =>
                name is "z" or "c" or "n" or "pi" or "e" or "let" or "in";
            private static bool IsDigit(char c) => c >= '0' && c <= '9';
            private static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
            private static bool IsIdentCont(char c) => IsIdentStart(c) || IsDigit(c);
        }
    }
}
