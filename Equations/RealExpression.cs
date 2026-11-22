// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Equations/RealExpression.cs
//
// #1994 — real-valued expressions of several named variables, with SYMBOLIC partial
// derivatives, for the user-written Hamiltonian families (#1994 User Potential, #1995 User
// Lens, #1997 User Hamiltonian; #1989 shares it).
//
// Parsing goes through the equation language's own front end (EquationFrontEnd, #1101) with
// a builder of its own, so the syntax, the function names and arities, `let` / `var`
// bindings, `if ... then ... else` / `?:`, and the "Did you mean" errors are exactly the
// language's. The builder makes a small real AST (RNode): constants, variables, unary and
// binary operators, calls and conditionals. Bindings are inlined (a let-bound name becomes
// its value), so every node is a pure function of the variables.
//
// Derivative(k) differentiates the tree with respect to variable k by the usual rules
// (sum, product, quotient, chain; pow with a constant or a variable exponent; conditionals
// differentiate their branches; floor / sign / comparisons are piecewise constant, so their
// derivative is 0 where it exists). The result is simplified as it is built (constants
// folded, x + 0, x * 1, x * 0 dropped), so a polynomial's gradient stays small.
//
// Compile() turns a tree into a delegate through System.Linq.Expressions (one typed
// expression, no interpretation per step), reading the variables from a double[] in the
// order given to TryParse. Small integer powers (x^2, x^3, x^4, x^-1, x^-2) compile to
// multiplications, as hand-written force code would, so a built-in family and its user-
// written twin round the same way more often. Eval() is a plain interpreter of the same
// tree, used by tests as an independent check of the compiled delegate.
//
// The values are real: the complex-only functions of the 2D language (re, im, conj, arg,
// norm, fold) and the constant i are refused with a message.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LE = System.Linq.Expressions.Expression;

namespace FracturingFog.Models
{
    /// <summary>#1994 — a node of a real expression tree (see file header).</summary>
    public abstract class RNode
    {
        internal abstract double Eval(ReadOnlySpan<double> v);
        internal abstract RNode D(int k);
        internal abstract LE Build(LE vars);
        internal abstract bool UsesVar(int k);
        public abstract override string ToString();
    }

    internal sealed class RConst : RNode
    {
        public readonly double Value;
        public RConst(double v) { Value = v; }
        internal override double Eval(ReadOnlySpan<double> v) => Value;
        internal override RNode D(int k) => R.Zero;
        internal override LE Build(LE vars) => LE.Constant(Value);
        internal override bool UsesVar(int k) => false;
        public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal sealed class RVar : RNode
    {
        public readonly int Index;
        public RVar(int i) { Index = i; }
        internal override double Eval(ReadOnlySpan<double> v) => v[Index];
        internal override RNode D(int k) => k == Index ? R.One : R.Zero;
        internal override LE Build(LE vars) => LE.ArrayIndex(vars, LE.Constant(Index));
        internal override bool UsesVar(int k) => k == Index;
        public override string ToString() => "$" + Index;
    }

    internal sealed class RUnary : RNode
    {
        public readonly char Op;   // '-' negate, '!' logical not
        public readonly RNode A;
        public RUnary(char op, RNode a) { Op = op; A = a; }
        internal override double Eval(ReadOnlySpan<double> v)
            => Op == '-' ? -A.Eval(v) : (A.Eval(v) != 0 ? 0.0 : 1.0);
        internal override RNode D(int k) => Op == '-' ? R.Neg(A.D(k)) : R.Zero;
        internal override LE Build(LE vars)
            => Op == '-' ? LE.Negate(A.Build(vars))
                         : LE.Condition(LE.NotEqual(A.Build(vars), LE.Constant(0.0)), LE.Constant(0.0), LE.Constant(1.0));
        internal override bool UsesVar(int k) => A.UsesVar(k);
        public override string ToString() => Op + "(" + A + ")";
    }

    internal sealed class RBinary : RNode
    {
        public readonly string Op;
        public readonly RNode A, B;
        public RBinary(string op, RNode a, RNode b) { Op = op; A = a; B = b; }

        internal override double Eval(ReadOnlySpan<double> v)
        {
            if (Op == "&&") return A.Eval(v) != 0 && B.Eval(v) != 0 ? 1.0 : 0.0;
            if (Op == "||") return A.Eval(v) != 0 || B.Eval(v) != 0 ? 1.0 : 0.0;
            double a = A.Eval(v), b = B.Eval(v);
            return Op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                "^" => R.PowValue(a, b, B),
                "<" => a < b ? 1.0 : 0.0,
                ">" => a > b ? 1.0 : 0.0,
                "<=" => a <= b ? 1.0 : 0.0,
                ">=" => a >= b ? 1.0 : 0.0,
                "==" => a == b ? 1.0 : 0.0,
                "!=" => a != b ? 1.0 : 0.0,
                _ => throw new InvalidOperationException("Unknown op " + Op),
            };
        }

        internal override RNode D(int k)
        {
            switch (Op)
            {
                case "+": return R.Add(A.D(k), B.D(k));
                case "-": return R.Sub(A.D(k), B.D(k));
                case "*": return R.Add(R.Mul(A.D(k), B), R.Mul(A, B.D(k)));
                case "/":
                    // (a' b - a b') / b^2, or a' / b when b is free of the variable.
                    if (!B.UsesVar(k)) return R.Div(A.D(k), B);
                    return R.Div(R.Sub(R.Mul(A.D(k), B), R.Mul(A, B.D(k))), R.Pow(B, R.Const(2)));
                case "^": return R.PowD(A, B, k);
                default: return R.Zero;   // comparisons and logic: piecewise constant
            }
        }

        internal override LE Build(LE vars)
        {
            if (Op == "^") return R.PowBuild(A, B, vars);
            var a = A.Build(vars);
            var b = B.Build(vars);
            LE Bool(LE c) => LE.Condition(c, LE.Constant(1.0), LE.Constant(0.0));
            LE Truth(LE x) => LE.NotEqual(x, LE.Constant(0.0));
            return Op switch
            {
                "+" => LE.Add(a, b),
                "-" => LE.Subtract(a, b),
                "*" => LE.Multiply(a, b),
                "/" => LE.Divide(a, b),
                "<" => Bool(LE.LessThan(a, b)),
                ">" => Bool(LE.GreaterThan(a, b)),
                "<=" => Bool(LE.LessThanOrEqual(a, b)),
                ">=" => Bool(LE.GreaterThanOrEqual(a, b)),
                "==" => Bool(LE.Equal(a, b)),
                "!=" => Bool(LE.NotEqual(a, b)),
                "&&" => Bool(LE.AndAlso(Truth(a), Truth(b))),
                "||" => Bool(LE.OrElse(Truth(a), Truth(b))),
                _ => throw new InvalidOperationException("Unknown op " + Op),
            };
        }

        internal override bool UsesVar(int k) => A.UsesVar(k) || B.UsesVar(k);
        public override string ToString() => "(" + A + " " + Op + " " + B + ")";
    }

    internal sealed class RCond : RNode
    {
        public readonly RNode C, A, B;
        public RCond(RNode c, RNode a, RNode b) { C = c; A = a; B = b; }
        internal override double Eval(ReadOnlySpan<double> v) => C.Eval(v) != 0 ? A.Eval(v) : B.Eval(v);
        internal override RNode D(int k) => R.Cond(C, A.D(k), B.D(k));
        internal override LE Build(LE vars)
            => LE.Condition(LE.NotEqual(C.Build(vars), LE.Constant(0.0)), A.Build(vars), B.Build(vars));
        internal override bool UsesVar(int k) => C.UsesVar(k) || A.UsesVar(k) || B.UsesVar(k);
        public override string ToString() => "(" + C + " ? " + A + " : " + B + ")";
    }

    internal sealed class RCall : RNode
    {
        public readonly string Name;
        public readonly RNode[] Args;
        public RCall(string name, RNode[] args) { Name = name; Args = args; }

        internal override double Eval(ReadOnlySpan<double> v)
        {
            double a = Args[0].Eval(v);
            double b = Args.Length > 1 ? Args[1].Eval(v) : 0;
            double c = Args.Length > 2 ? Args[2].Eval(v) : 0;
            return R.Apply(Name, a, b, c);
        }

        internal override RNode D(int k)
        {
            var u = Args[0];
            RNode du = u.D(k);
            RNode Chain(RNode f) => R.Mul(f, du);
            switch (Name)
            {
                case "sin": return Chain(R.Call("cos", u));
                case "cos": return Chain(R.Neg(R.Call("sin", u)));
                case "tan": return Chain(R.Div(R.One, R.Pow(R.Call("cos", u), R.Const(2))));
                case "sinh": return Chain(R.Call("cosh", u));
                case "cosh": return Chain(R.Call("sinh", u));
                case "tanh": return Chain(R.Sub(R.One, R.Pow(R.Call("tanh", u), R.Const(2))));
                case "exp": return Chain(R.Call("exp", u));
                case "log": return Chain(R.Div(R.One, u));
                case "sqrt": return Chain(R.Div(R.Const(0.5), R.Call("sqrt", u)));
                case "sqr": return Chain(R.Mul(R.Const(2), u));
                case "abs": return Chain(R.Call("sign", u));
                case "asin": return Chain(R.Div(R.One, R.Call("sqrt", R.Sub(R.One, R.Pow(u, R.Const(2))))));
                case "acos": return Chain(R.Neg(R.Div(R.One, R.Call("sqrt", R.Sub(R.One, R.Pow(u, R.Const(2)))))));
                case "atan": return Chain(R.Div(R.One, R.Add(R.One, R.Pow(u, R.Const(2)))));
                case "asinh": return Chain(R.Div(R.One, R.Call("sqrt", R.Add(R.Pow(u, R.Const(2)), R.One))));
                case "acosh": return Chain(R.Div(R.One, R.Call("sqrt", R.Sub(R.Pow(u, R.Const(2)), R.One))));
                case "atanh": return Chain(R.Div(R.One, R.Sub(R.One, R.Pow(u, R.Const(2)))));
                case "floor": case "ceil": case "round": case "trunc": case "sign": return R.Zero;
                case "fract": return du;   // x - floor(x)
                case "pow": return R.PowD(Args[0], Args[1], k);
                case "atan2":
                {
                    // d atan2(y, x) = (x dy - y dx) / (x^2 + y^2)
                    var y = Args[0]; var x = Args[1];
                    return R.Div(R.Sub(R.Mul(x, y.D(k)), R.Mul(y, x.D(k))), R.Add(R.Pow(x, R.Const(2)), R.Pow(y, R.Const(2))));
                }
                case "min": return R.Cond(R.Bin("<=", Args[0], Args[1]), Args[0].D(k), Args[1].D(k));
                case "max": return R.Cond(R.Bin(">=", Args[0], Args[1]), Args[0].D(k), Args[1].D(k));
                case "mod":
                    // a - b floor(a / b)
                    return R.Sub(Args[0].D(k), R.Mul(R.Call("floor", R.Div(Args[0], Args[1])), Args[1].D(k)));
                case "clamp":
                    return R.Cond(R.Bin("<", Args[0], Args[1]), Args[1].D(k),
                                  R.Cond(R.Bin(">", Args[0], Args[2]), Args[2].D(k), Args[0].D(k)));
                default: throw new InvalidOperationException("No derivative for " + Name);
            }
        }

        internal override LE Build(LE vars)
        {
            if (Name == "pow") return R.PowBuild(Args[0], Args[1], vars);
            var args = Args.Select(a => a.Build(vars)).ToArray();
            var m = typeof(R).GetMethod(nameof(R.Apply), new[] { typeof(string), typeof(double), typeof(double), typeof(double) })!;
            System.Reflection.MethodInfo? direct = Name switch
            {
                "sin" => typeof(Math).GetMethod(nameof(Math.Sin)),
                "cos" => typeof(Math).GetMethod(nameof(Math.Cos)),
                "tan" => typeof(Math).GetMethod(nameof(Math.Tan)),
                "sinh" => typeof(Math).GetMethod(nameof(Math.Sinh)),
                "cosh" => typeof(Math).GetMethod(nameof(Math.Cosh)),
                "tanh" => typeof(Math).GetMethod(nameof(Math.Tanh)),
                "exp" => typeof(Math).GetMethod(nameof(Math.Exp)),
                "log" => typeof(Math).GetMethod(nameof(Math.Log), new[] { typeof(double) }),
                "sqrt" => typeof(Math).GetMethod(nameof(Math.Sqrt)),
                "abs" => typeof(Math).GetMethod(nameof(Math.Abs), new[] { typeof(double) }),
                "asin" => typeof(Math).GetMethod(nameof(Math.Asin)),
                "acos" => typeof(Math).GetMethod(nameof(Math.Acos)),
                "atan" => typeof(Math).GetMethod(nameof(Math.Atan)),
                "asinh" => typeof(Math).GetMethod(nameof(Math.Asinh)),
                "acosh" => typeof(Math).GetMethod(nameof(Math.Acosh)),
                "atanh" => typeof(Math).GetMethod(nameof(Math.Atanh)),
                "floor" => typeof(Math).GetMethod(nameof(Math.Floor), new[] { typeof(double) }),
                "ceil" => typeof(Math).GetMethod(nameof(Math.Ceiling), new[] { typeof(double) }),
                "trunc" => typeof(Math).GetMethod(nameof(Math.Truncate), new[] { typeof(double) }),
                "atan2" => typeof(Math).GetMethod(nameof(Math.Atan2)),
                _ => null,
            };
            if (direct != null) return LE.Call(direct, args);
            if (Name == "sqr") return LE.Multiply(args[0], args[0]);
            return LE.Call(m, LE.Constant(Name), args[0],
                           args.Length > 1 ? args[1] : LE.Constant(0.0), args.Length > 2 ? args[2] : LE.Constant(0.0));
        }

        internal override bool UsesVar(int k) => Args.Any(a => a.UsesVar(k));
        public override string ToString() => Name + "(" + string.Join(", ", Args.Select(a => a.ToString())) + ")";
    }

    /// <summary>#1994 — simplifying constructors and the function table.</summary>
    internal static class R
    {
        public static readonly RNode Zero = new RConst(0), One = new RConst(1);

        public static RNode Const(double v) => v == 0 ? Zero : v == 1 ? One : new RConst(v);
        static bool IsC(RNode n, out double v) { if (n is RConst c) { v = c.Value; return true; } v = 0; return false; }

        public static RNode Add(RNode a, RNode b)
        {
            if (IsC(a, out var x) && IsC(b, out var y)) return Const(x + y);
            if (IsC(a, out x) && x == 0) return b;
            if (IsC(b, out y) && y == 0) return a;
            return new RBinary("+", a, b);
        }

        public static RNode Sub(RNode a, RNode b)
        {
            if (IsC(a, out var x) && IsC(b, out var y)) return Const(x - y);
            if (IsC(b, out y) && y == 0) return a;
            if (IsC(a, out x) && x == 0) return Neg(b);
            return new RBinary("-", a, b);
        }

        public static RNode Mul(RNode a, RNode b)
        {
            if (IsC(a, out var x) && IsC(b, out var y)) return Const(x * y);
            if ((IsC(a, out x) && x == 0) || (IsC(b, out y) && y == 0)) return Zero;
            if (IsC(a, out x) && x == 1) return b;
            if (IsC(b, out y) && y == 1) return a;
            if (IsC(a, out x) && x == -1) return Neg(b);
            if (IsC(b, out y) && y == -1) return Neg(a);
            return new RBinary("*", a, b);
        }

        public static RNode Div(RNode a, RNode b)
        {
            if (IsC(a, out var x) && IsC(b, out var y)) return Const(x / y);
            if (IsC(a, out x) && x == 0) return Zero;
            if (IsC(b, out y) && y == 1) return a;
            return new RBinary("/", a, b);
        }

        public static RNode Neg(RNode a)
        {
            if (IsC(a, out var x)) return Const(-x);
            if (a is RUnary { Op: '-' } u) return u.A;
            return new RUnary('-', a);
        }

        public static RNode Pow(RNode a, RNode b)
        {
            if (IsC(b, out var y))
            {
                if (y == 0) return One;
                if (y == 1) return a;
                if (IsC(a, out var x)) return Const(Math.Pow(x, y));
            }
            return new RBinary("^", a, b);
        }

        public static RNode Bin(string op, RNode a, RNode b) => new RBinary(op, a, b);

        /// <summary><paramref name="n"/> with every read of let-slot <paramref name="slot"/> replaced by
        /// <paramref name="value"/>, rebuilt through the simplifying constructors.</summary>
        public static RNode Subst(RNode n, int slot, RNode value) => n switch
        {
            RSlot s => s.Slot == slot ? value : s,
            RUnary u => u.Op == '-' ? Neg(Subst(u.A, slot, value)) : new RUnary(u.Op, Subst(u.A, slot, value)),
            RBinary b => b.Op switch
            {
                "+" => Add(Subst(b.A, slot, value), Subst(b.B, slot, value)),
                "-" => Sub(Subst(b.A, slot, value), Subst(b.B, slot, value)),
                "*" => Mul(Subst(b.A, slot, value), Subst(b.B, slot, value)),
                "/" => Div(Subst(b.A, slot, value), Subst(b.B, slot, value)),
                "^" => Pow(Subst(b.A, slot, value), Subst(b.B, slot, value)),
                _ => Bin(b.Op, Subst(b.A, slot, value), Subst(b.B, slot, value)),
            },
            RCond c => Cond(Subst(c.C, slot, value), Subst(c.A, slot, value), Subst(c.B, slot, value)),
            RCall f => Call(f.Name, f.Args.Select(a => Subst(a, slot, value)).ToArray()),
            _ => n,
        };

        /// <summary>True when a let-slot read is left in the tree (a name read before its value).</summary>
        public static bool HasSlot(RNode n) => n switch
        {
            RSlot => true,
            RUnary u => HasSlot(u.A),
            RBinary b => HasSlot(b.A) || HasSlot(b.B),
            RCond c => HasSlot(c.C) || HasSlot(c.A) || HasSlot(c.B),
            RCall f => f.Args.Any(HasSlot),
            _ => false,
        };
        public static RNode Cond(RNode c, RNode a, RNode b)
        {
            if (IsC(c, out var v)) return v != 0 ? a : b;
            if (IsC(a, out var x) && IsC(b, out var y) && x.Equals(y)) return a;
            return new RCond(c, a, b);
        }

        public static RNode Call(string name, params RNode[] args)
        {
            if (args.All(a => a is RConst))
            {
                double a0 = ((RConst)args[0]).Value;
                double a1 = args.Length > 1 ? ((RConst)args[1]).Value : 0, a2 = args.Length > 2 ? ((RConst)args[2]).Value : 0;
                return Const(Apply(name, a0, a1, a2));
            }
            return new RCall(name, args);
        }

        /// <summary>d(a^b)/dk: b a^(b-1) a' for b free of k, else a^b (b' ln a + b a' / a).</summary>
        public static RNode PowD(RNode a, RNode b, int k)
        {
            if (!b.UsesVar(k))
            {
                if (!a.UsesVar(k)) return Zero;
                var e = IsC(b, out var y) ? Const(y - 1) : Sub(b, One);
                return Mul(Mul(b, Pow(a, e)), a.D(k));
            }
            return Mul(Pow(a, b), Add(Mul(b.D(k), Call("log", a)), Div(Mul(b, a.D(k)), a)));
        }

        /// <summary>a^b: small integer powers as products (x^2 = x x), else Math.Pow.</summary>
        public static LE PowBuild(RNode a, RNode b, LE vars)
        {
            var ea = a.Build(vars);
            if (b is RConst c && c.Value == Math.Round(c.Value) && Math.Abs(c.Value) <= 4 && c.Value != 0)
            {
                var p = LE.Parameter(typeof(double));
                int n = (int)Math.Abs(c.Value);
                LE prod = p;
                for (int i = 1; i < n; i++) prod = LE.Multiply(prod, p);
                if (c.Value < 0) prod = LE.Divide(LE.Constant(1.0), prod);
                return LE.Block(new[] { p }, LE.Assign(p, ea), prod);
            }
            return LE.Call(typeof(Math).GetMethod(nameof(Math.Pow))!, ea, b.Build(vars));
        }

        /// <summary>The interpreter's a^b, matching <see cref="PowBuild"/>.</summary>
        public static double PowValue(double a, double b, RNode bNode)
        {
            if (bNode is RConst c && c.Value == Math.Round(c.Value) && Math.Abs(c.Value) <= 4 && c.Value != 0)
            {
                int n = (int)Math.Abs(c.Value);
                double prod = a;
                for (int i = 1; i < n; i++) prod *= a;
                return c.Value < 0 ? 1.0 / prod : prod;
            }
            return Math.Pow(a, b);
        }

        public static double Apply(string name, double a, double b, double c) => name switch
        {
            "sin" => Math.Sin(a),
            "cos" => Math.Cos(a),
            "tan" => Math.Tan(a),
            "sinh" => Math.Sinh(a),
            "cosh" => Math.Cosh(a),
            "tanh" => Math.Tanh(a),
            "exp" => Math.Exp(a),
            "log" => Math.Log(a),
            "sqrt" => Math.Sqrt(a),
            "sqr" => a * a,
            "abs" => Math.Abs(a),
            "asin" => Math.Asin(a),
            "acos" => Math.Acos(a),
            "atan" => Math.Atan(a),
            "asinh" => Math.Asinh(a),
            "acosh" => Math.Acosh(a),
            "atanh" => Math.Atanh(a),
            "floor" => Math.Floor(a),
            "ceil" => Math.Ceiling(a),
            "round" => Math.Round(a),
            "trunc" => Math.Truncate(a),
            "sign" => Math.Sign(a),
            "fract" => a - Math.Floor(a),
            "pow" => Math.Pow(a, b),
            "atan2" => Math.Atan2(a, b),
            "min" => Math.Min(a, b),
            "max" => Math.Max(a, b),
            "mod" => a - b * Math.Floor(a / b),
            "clamp" => Math.Clamp(a, Math.Min(b, c), Math.Max(b, c)),
            _ => double.NaN,
        };
    }

    /// <summary>#1994 — a parsed real expression of named variables, with symbolic partial
    /// derivatives and a compiled evaluator (see file header).</summary>
    public sealed class RealExpression
    {
        private static readonly HashSet<string> ComplexOnly = new(StringComparer.Ordinal) { "re", "im", "conj", "arg", "norm", "fold" };

        public IReadOnlyList<string> Variables { get; }
        public RNode Root { get; }
        public string Source { get; }

        private Func<double[], double>? _compiled;

        private RealExpression(string source, IReadOnlyList<string> vars, RNode root) { Source = source; Variables = vars; Root = root; }

        /// <summary>Parse <paramref name="source"/> as a real expression of
        /// <paramref name="variables"/> (in that order for <see cref="Eval"/> and the compiled form).</summary>
        public static bool TryParse(string? source, IReadOnlyList<string> variables, out RealExpression? expr, out string? error)
        {
            expr = null; error = null;
            if (string.IsNullOrWhiteSpace(source)) { error = "The expression is empty."; return false; }
            try
            {
                var b = new Builder(source, variables);
                var root = new EquationFrontEnd<RNode>(source, b, Syntax, legacy: false, edits: null).ParseProgram();
                if (R.HasSlot(root)) { error = "A name is read before it is given a value."; return false; }
                expr = new RealExpression(source, variables, root);
                return true;
            }
            catch (FormatException ex) { error = ex.Message; return false; }
        }

        /// <summary>True when the expression reads variable <paramref name="k"/>.</summary>
        public bool Uses(int k) => Root.UsesVar(k);

        /// <summary>The symbolic partial derivative with respect to variable <paramref name="k"/>.</summary>
        public RealExpression Derivative(int k) => new(Source + " d/d" + Variables[k], Variables, Root.D(k));

        /// <summary>The symbolic partial derivative with respect to the named variable.</summary>
        public RealExpression Derivative(string variable) => Derivative(IndexOf(variable));

        public int IndexOf(string variable)
        {
            for (int i = 0; i < Variables.Count; i++) if (Variables[i] == variable) return i;
            throw new ArgumentException("Unknown variable " + variable);
        }

        /// <summary>#1995 — the sum of this and <paramref name="other"/> (same variables).</summary>
        public RealExpression Plus(RealExpression other) => new(Source + " + " + other.Source, Variables, R.Add(Root, other.Root));

        /// <summary>#1995 — the symbolic Laplacian: the sum of the second partials over every variable.</summary>
        public RealExpression Laplacian()
        {
            RNode sum = R.Zero;
            for (int k = 0; k < Variables.Count; k++) sum = R.Add(sum, Root.D(k).D(k));
            return new RealExpression("lap(" + Source + ")", Variables, sum);
        }

        /// <summary>Interpreted value at <paramref name="values"/> (variables in order).</summary>
        public double Eval(ReadOnlySpan<double> values) => Root.Eval(values);

        /// <summary>The compiled evaluator: the variables' values in order, in a double[].</summary>
        public Func<double[], double> Compile()
        {
            if (_compiled != null) return _compiled;
            var p = LE.Parameter(typeof(double[]), "v");
            var body = Root.Build(p);
            return _compiled = System.Linq.Expressions.Expression.Lambda<Func<double[], double>>(body, p).Compile();
        }

        /// <summary>The tree as text (variables as $index), for diagnostics.</summary>
        public override string ToString() => Root.ToString();

        // ── Builder ──

        private static readonly EquationSyntax Syntax = new(
            AllowMembers: false,
            TypeKeywords: new HashSet<string>(StringComparer.Ordinal) { "var", "double" },
            NonAssignableWords: new HashSet<string>(StringComparer.Ordinal) { "let", "in", "return", "if" });

        private sealed class Builder : IEquationBuilder<RNode>
        {
            private readonly string _src;
            private readonly IReadOnlyList<string> _names;
            private readonly Dictionary<string, int> _scope = new(StringComparer.Ordinal);
            private int _next;

            public Builder(string src, IReadOnlyList<string> names)
            {
                _src = src; _names = names; _next = names.Count;
                for (int i = 0; i < names.Count; i++) _scope[names[i]] = i;
            }

            private string At(int pos) => EquationSyntax.At(_src, pos);
            public Exception Error(string message, int position, int length = 1) => new FormatException(message);
            public RNode Number(double value) => R.Const(value);

            public RNode Identifier(string name, int position)
            {
                if (_scope.TryGetValue(name, out int slot))
                    return slot < _names.Count ? new RVar(slot) : new RSlot(slot);
                switch (name.ToLowerInvariant())
                {
                    case "pi": return R.Const(Math.PI);
                    case "e": return R.Const(Math.E);
                    case "i": throw new FormatException($"'i' at {At(position)}: this expression is real (no complex numbers).");
                }
                throw new FormatException(
                    $"Unknown identifier '{name}' at {At(position)}.{EquationSyntax.DidYouMean(name, Candidates())}");
            }

            private IEnumerable<string> Candidates()
            {
                foreach (var v in _names) yield return v;
                yield return "pi"; yield return "e";
                foreach (var k in _scope.Keys) yield return k;
                foreach (var f in SandboxExpression.FunctionNames) if (!ComplexOnly.Contains(f)) yield return f;
            }

            public RNode Call(string name, int nameStart, IReadOnlyList<RNode> args)
            {
                string lname = name.ToLowerInvariant();
                if (ComplexOnly.Contains(lname))
                    throw new FormatException($"Function '{name}' at {At(nameStart)} is for complex numbers; this expression is real.");
                int expected = SandboxExpression.FunctionArity(lname);
                if (expected < 0)
                    throw new FormatException(
                        $"Unknown function '{name}' at {At(nameStart)}.{EquationSyntax.DidYouMean(lname, SandboxExpression.FunctionNames.Where(f => !ComplexOnly.Contains(f)))}");
                if (args.Count != expected)
                    throw new FormatException($"Function '{name}' at {At(nameStart)} takes {expected} arg(s), got {args.Count}.");
                return R.Call(lname, args.ToArray());
            }

            public RNode Unary(char op, RNode operand) => op == '-' ? R.Neg(operand) : new RUnary(op, operand);
            public RNode Binary(string op, RNode a, RNode b)
            {
                return op switch
                {
                    "+" => R.Add(a, b),
                    "-" => R.Sub(a, b),
                    "*" => R.Mul(a, b),
                    "/" => R.Div(a, b),
                    "^" => R.Pow(a, b),
                    _ => R.Bin(op, a, b),
                };
            }
            public RNode Ternary(RNode cond, RNode then, RNode otherwise) => R.Cond(cond, then, otherwise);
            public RNode Member(RNode target, char axis) => throw new FormatException("Member access is not part of this language.");
            // The value is parsed before the name is bound, and the body after: inline the value
            // wherever the body reads the name.
            public RNode Let(int slot, RNode value, RNode body) => R.Subst(body, slot, value);
            public RNode IfThenCondition(RNode cond, List<(int Pos, int Len, string Text)>? edits) => cond;

            public void CheckLetName(string name, int position)
            {
                if (((IList<string>)_names).Contains(name) || name is "pi" or "e" or "i" or "let" or "in")
                    throw new FormatException($"Cannot rebind reserved name '{name}' at {At(position)}.");
            }
            public void CheckAssignName(string name, int position) => CheckLetName(name, position);

            public (int Slot, int Prior, bool HadPrior) Bind(string name)
            {
                bool had = _scope.TryGetValue(name, out int prior);
                int slot = _next++;
                _scope[name] = slot;
                return (slot, prior, had);
            }

            public void Unbind(string name, int prior, bool hadPrior)
            {
                if (hadPrior) _scope[name] = prior; else _scope.Remove(name);
            }

            public bool TryGetBound(string name, out RNode node)
            {
                if (_scope.TryGetValue(name, out int slot))
                {
                    node = slot < _names.Count ? new RVar(slot) : new RSlot(slot);
                    return true;
                }
                node = null!;
                return false;
            }

            public int Mark() => _next;
            public void Reset(int mark) => _next = mark;
        }
    }

    /// <summary>A read of a let-bound name, before inlining (never left in a finished tree).</summary>
    internal sealed class RSlot : RNode
    {
        public readonly int Slot;
        public RSlot(int s) { Slot = s; }
        internal override double Eval(ReadOnlySpan<double> v) => throw new InvalidOperationException("unbound slot");
        internal override RNode D(int k) => throw new InvalidOperationException("unbound slot");
        internal override LE Build(LE vars) => throw new InvalidOperationException("unbound slot");
        internal override bool UsesVar(int k) => false;
        public override string ToString() => "#" + Slot;
    }
}
