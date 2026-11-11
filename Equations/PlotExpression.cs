// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Equations/PlotExpression.cs
//
// #1550 (Math Plane S3) — the plot language: the equation language's syntax
// (EquationFrontEnd, #1101) with a third builder whose inputs are the real plot
// variables x, y, t, theta (and complex z, c for the complex maps of S6).
//
// It reuses the 2D language's AST and interpreter (SbxNode / SbxCall), so the
// function table, arities, constants (pi, e, i) and the "Did you mean" errors are
// exactly the 2D language's; no grammar changes, and the 2D / bulb parse trees are
// untouched (ParserBehaviourSnapshotTests). Only the identifier set differs.
//
// #1714 (Math Plane 3D T2): a parametric surface reads u and v. They are opt-in
// (TryParse(..., surface: true)), so the 2D language keeps 'u' and 'v' as free
// let-names; with them, slots 6 and 7 hold u and v and EvalUV sets them.
//
// Real results: a plot value is real. A complex result whose imaginary part is
// negligible counts as real; otherwise the value is NaN (a gap in the curve),
// e.g. sqrt(x) for x < 0.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Models
{
    public sealed class PlotExpression
    {
        public const int SlotX = 0, SlotY = 1, SlotT = 2, SlotTheta = 3, SlotZ = 4, SlotC = 5;
        /// <summary>#1714 — slots of the surface parameters u, v (with <c>surface: true</c>).</summary>
        public const int SlotU = 6, SlotV = 7;

        /// <summary>The plot language's input variables, in slot order.</summary>
        public static readonly IReadOnlyList<string> Variables = new[] { "x", "y", "t", "theta", "z", "c" };

        /// <summary>#1714 — the variables of a surface expression: <see cref="Variables"/>, then u, v.</summary>
        public static readonly IReadOnlyList<string> SurfaceVariables = new[] { "x", "y", "t", "theta", "z", "c", "u", "v" };

        private readonly SbxNode _root;
        private readonly int _envSize;
        private readonly bool[] _uses;
        private readonly IReadOnlyList<string> _names;
        [ThreadStatic] private static SbxVal[]? t_env;

        private PlotExpression(SbxNode root, int envSize, bool[] uses, IReadOnlyList<string> names)
        { _root = root; _envSize = envSize; _uses = uses; _names = names; }

        /// <summary>True when the source reads <paramref name="variable"/> (one of
        /// <see cref="Variables"/>, or of <see cref="SurfaceVariables"/> for a surface).</summary>
        public bool Uses(string variable)
        {
            int i = ((IList<string>)_names).IndexOf(variable);
            return i >= 0 && _uses[i];
        }

        /// <summary>The variables the source reads.</summary>
        public IEnumerable<string> UsedVariables()
        {
            for (int i = 0; i < _names.Count; i++) if (_uses[i]) yield return _names[i];
        }

        /// <summary>Parse <paramref name="source"/>; on failure <paramref name="error"/>
        /// is the language's message ("Unknown identifier 'q' at line 1, col 3. Did you mean 'x'?").</summary>
        public static bool TryParse(string? source, out PlotExpression? expr, out string? error)
            => TryParse(source, surface: false, out expr, out error);

        /// <summary>#1714 — as <see cref="TryParse(string?, out PlotExpression?, out string?)"/>;
        /// with <paramref name="surface"/> the source may also read u and v (<see cref="EvalUV"/>).</summary>
        public static bool TryParse(string? source, bool surface, out PlotExpression? expr, out string? error)
        {
            expr = null; error = null;
            if (string.IsNullOrWhiteSpace(source)) { error = "The expression is empty."; return false; }
            try
            {
                var names = surface ? SurfaceVariables : Variables;
                var b = new Builder(source, names);
                var root = new EquationFrontEnd<SbxNode>(source, b, Syntax, legacy: false, edits: null).ParseProgram();
                expr = new PlotExpression(root, b.EnvSize, b.Uses, names);
                return true;
            }
            catch (FormatException ex) { error = ex.Message; return false; }
        }

        /// <summary>The real value at the given inputs (NaN where it is not real).
        /// Thread-safe: each thread has its own evaluation scratch.</summary>
        public double Eval(double x = 0, double y = 0, double t = 0, double theta = 0)
        {
            var env = Env();
            env[SlotX] = SbxVal.Real(x);
            env[SlotY] = SbxVal.Real(y);
            env[SlotT] = SbxVal.Real(t);
            env[SlotTheta] = SbxVal.Real(theta);
            env[SlotZ] = SbxVal.Cx(new Complex(x, y));
            env[SlotC] = SbxVal.Cx(Complex.Zero);
            return ToReal(_root.Eval(env));
        }

        /// <summary>#1714 — the real value at surface parameters (u, v), for an expression
        /// parsed with <c>surface: true</c>; the other inputs are 0.</summary>
        public double EvalUV(double u, double v)
        {
            var env = Env();
            env[SlotX] = SbxVal.Real(0);
            env[SlotY] = SbxVal.Real(0);
            env[SlotT] = SbxVal.Real(0);
            env[SlotTheta] = SbxVal.Real(0);
            env[SlotZ] = SbxVal.Cx(Complex.Zero);
            env[SlotC] = SbxVal.Cx(Complex.Zero);
            if (_names.Count > SlotV)
            {
                env[SlotU] = SbxVal.Real(u);
                env[SlotV] = SbxVal.Real(v);
            }
            return ToReal(_root.Eval(env));
        }

        /// <summary>The complex value at <paramref name="z"/>, <paramref name="c"/>
        /// (x, y read the real and imaginary parts of z).</summary>
        public Complex EvalComplex(Complex z, Complex c, double t = 0)
        {
            var env = Env();
            env[SlotX] = SbxVal.Real(z.Real);
            env[SlotY] = SbxVal.Real(z.Imaginary);
            env[SlotT] = SbxVal.Real(t);
            env[SlotTheta] = SbxVal.Real(0);
            env[SlotZ] = SbxVal.Cx(z);
            env[SlotC] = SbxVal.Cx(c);
            return _root.Eval(env).AsComplex();
        }

        private SbxVal[] Env()
        {
            var env = t_env;
            if (env == null || env.Length < _envSize) t_env = env = new SbxVal[Math.Max(_envSize, 16)];
            return env;
        }

        private static double ToReal(SbxVal v)
        {
            if (v.IsReal) return v.R;
            return Math.Abs(v.I) <= 1e-12 * Math.Max(1.0, Math.Abs(v.R)) ? v.R : double.NaN;
        }

        // ── Builder: the plot identifiers; everything else as the 2D language ──

        private static readonly EquationSyntax Syntax = new(
            AllowMembers: false,
            TypeKeywords: new HashSet<string>(StringComparer.Ordinal) { "var", "double" },
            NonAssignableWords: new HashSet<string>(StringComparer.Ordinal) { "let", "in", "return", "if" });

        private sealed class Builder : IEquationBuilder<SbxNode>
        {
            private readonly string _src;
            private readonly Dictionary<string, int> _scope = new(StringComparer.Ordinal);
            private readonly IReadOnlyList<string> _names;
            public readonly bool[] Uses;
            public int EnvSize;

            public Builder(string src, IReadOnlyList<string> names)
            {
                _src = src;
                _names = names;
                Uses = new bool[names.Count];
                EnvSize = names.Count;
                for (int i = 0; i < names.Count; i++) _scope[names[i]] = i;
            }

            private string At(int pos) => EquationSyntax.At(_src, pos);

            public Exception Error(string message, int position, int length = 1) => new FormatException(message);
            public SbxNode Number(double value) => new SbxConst(SbxVal.Real(value));

            public SbxNode Identifier(string name, int position)
            {
                if (_scope.TryGetValue(name, out int slot))
                {
                    if (slot < _names.Count) Uses[slot] = true;
                    return new SbxSlot(slot);
                }
                switch (name.ToLowerInvariant())
                {
                    case "pi": return new SbxConst(SbxVal.Real(Math.PI));
                    case "e":  return new SbxConst(SbxVal.Real(Math.E));
                    case "i":  return new SbxConst(SbxVal.Cx(0.0, 1.0));
                }
                throw new FormatException(
                    $"Unknown identifier '{name}' at {At(position)}.{EquationSyntax.DidYouMean(name, Candidates())}");
            }

            private IEnumerable<string> Candidates()
            {
                foreach (var v in _names) yield return v;
                yield return "pi"; yield return "e"; yield return "i";
                foreach (var k in _scope.Keys) yield return k;
                foreach (var f in SandboxExpression.FunctionNames) yield return f;
            }

            public SbxNode Call(string name, int nameStart, IReadOnlyList<SbxNode> args)
            {
                string lname = name.ToLowerInvariant();
                int expected = SandboxExpression.FunctionArity(lname);
                if (expected < 0)
                    throw new FormatException(
                        $"Unknown function '{name}' at {At(nameStart)}.{EquationSyntax.DidYouMean(lname, SandboxExpression.FunctionNames)}");
                if (args.Count != expected)
                    throw new FormatException($"Function '{name}' at {At(nameStart)} takes {expected} arg(s), got {args.Count}.");
                return new SbxCall(lname, System.Linq.Enumerable.ToArray(args));
            }

            public SbxNode Unary(char op, SbxNode operand) => new SbxUnary(op, operand);
            public SbxNode Binary(string op, SbxNode a, SbxNode b) => new SbxBinary(op, a, b);
            public SbxNode Ternary(SbxNode cond, SbxNode then, SbxNode otherwise) => new SbxTernary(cond, then, otherwise);
            public SbxNode Member(SbxNode target, char axis) => throw new FormatException("Member access is not part of the plot language.");
            public SbxNode Let(int slot, SbxNode value, SbxNode body) => new SbxLet(slot, value, body);
            public SbxNode IfThenCondition(SbxNode cond, List<(int Pos, int Len, string Text)>? edits) => cond;

            public void CheckLetName(string name, int position)
            {
                if (((IList<string>)_names).Contains(name) || name is "pi" or "e" or "i" or "let" or "in")
                    throw new FormatException($"Cannot rebind reserved name '{name}' at {At(position)}.");
            }

            public void CheckAssignName(string name, int position) => CheckLetName(name, position);

            public (int Slot, int Prior, bool HadPrior) Bind(string name)
            {
                bool hadPrior = _scope.TryGetValue(name, out int prior);
                int slot = EnvSize++;
                _scope[name] = slot;
                return (slot, prior, hadPrior);
            }

            public void Unbind(string name, int prior, bool hadPrior)
            {
                if (hadPrior) _scope[name] = prior; else _scope.Remove(name);
            }

            public bool TryGetBound(string name, out SbxNode node)
            {
                if (_scope.TryGetValue(name, out int slot)) { node = new SbxSlot(slot); return true; }
                node = null!;
                return false;
            }

            public int Mark() => EnvSize;
            public void Reset(int mark) => EnvSize = mark;
        }
    }
}
