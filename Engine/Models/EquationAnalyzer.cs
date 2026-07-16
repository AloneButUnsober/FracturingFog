// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/EquationAnalyzer.cs
//
// Static AST walker that extracts structural features from a parsed
// SandboxExpression tree. The resulting EquationProfile is consumed by
// theme-recommendation logic to pick color maps suited to the equation's
// behavior (escape characteristics, symmetry, transcendental components,
// iteration dependence, etc.).
//
// Pure analysis pass — no side effects, no Roslyn, no I/O.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Structural fingerprint of a parsed sandbox equation. Each flag/field
    /// reflects a property detectable from the AST alone, without evaluating
    /// the expression. Themes are scored against this profile.
    /// </summary>
    public sealed class EquationProfile
    {
        /// <summary>True when conj(...) appears anywhere — equation is
        /// antiholomorphic at that point. Tricorn-like. Breaks distance
        /// estimation and derivative bailout themes.</summary>
        public bool Antiholomorphic { get; init; }

        /// <summary>True when abs(...) appears applied to z, re(z), or im(z)
        /// — burning-ship-like absolute-value folding. Produces sharp creases
        /// that look poor with cyclic / smooth themes.</summary>
        public bool HasAbs { get; init; }

        /// <summary>True when any transcendental function (sin/cos/tan/exp/log)
        /// is applied to a z-dependent subtree. Escape behavior is unbounded
        /// and erratic; smooth iteration coloring is unreliable, derivative-
        /// bailout themes break, cyclic / hue themes work well.</summary>
        public bool Transcendental { get; init; }

        /// <summary>Highest polynomial degree of z found, conservatively. -1
        /// when no z^k or pow(z, k) found with k a real constant; otherwise
        /// max k seen. Used to flag multibrot-like slow approach.</summary>
        public int MaxPolyDegree { get; init; }

        /// <summary>True when the equation references n (iteration index).
        /// Iteration-dependent maps produce non-monotone "smooth" values —
        /// histogram-equalised themes hold up better than gradient ones.</summary>
        public bool IterationDependent { get; init; }

        /// <summary>True when the imaginary unit i appears literally — usually
        /// indicates rotation / spiral structure that suits hue-cycling.</summary>
        public bool HasImaginaryConst { get; init; }

        /// <summary>True when c is referenced. Equations that ignore c are
        /// "Julia-like" — single connected component, often dominated by
        /// interior. Favors interior-aware themes.</summary>
        public bool HasCRef { get; init; }

        /// <summary>True when z is referenced (almost always — but flagged
        /// for completeness).</summary>
        public bool HasZRef { get; init; }

        /// <summary>True when a conditional (ternary or logical op) appears
        /// in the value path. Mixed-dynamic systems often produce sharp
        /// regional boundaries — high-contrast themes read better than
        /// smooth gradients.</summary>
        public bool HasBranching { get; init; }

        /// <summary>True if escape behavior is judged "fast" — pure low-degree
        /// polynomial in z. Smooth iteration coloring works perfectly. False
        /// for transcendental / high-degree / antiholomorphic equations where
        /// smoothing is questionable.</summary>
        public bool SmoothEscapeReliable =>
            !Transcendental && !Antiholomorphic && MaxPolyDegree > 0 && MaxPolyDegree <= 3;

        /// <summary>True if exterior distance estimation (DE) is mathematically
        /// valid for this equation. DE assumes holomorphic dynamics with a
        /// closed-form derivative. Breaks for conj, abs, n-dependent maps,
        /// and is unreliable for transcendental escapes.</summary>
        public bool DistanceEstimateValid =>
            !Antiholomorphic && !HasAbs && !Transcendental && !IterationDependent;
    }

    /// <summary>
    /// Walks a parsed <see cref="SandboxExpression"/> AST and builds an
    /// <see cref="EquationProfile"/>. Single forward pass; no allocation
    /// beyond the returned profile and a small accumulator.
    /// </summary>
    public static class EquationAnalyzer
    {
        public static EquationProfile Analyze(SandboxExpression expr)
        {
            if (expr == null) throw new ArgumentNullException(nameof(expr));
            var acc = new Accumulator();
            Walk(expr.Root, acc, zScope: false);
            return new EquationProfile
            {
                Antiholomorphic    = acc.Antiholomorphic,
                HasAbs             = acc.HasAbs,
                Transcendental     = acc.Transcendental,
                MaxPolyDegree      = acc.MaxPolyDegree,
                IterationDependent = acc.IterationDependent,
                HasImaginaryConst  = acc.HasImaginaryConst,
                HasCRef            = acc.HasCRef,
                HasZRef            = acc.HasZRef,
                HasBranching       = acc.HasBranching,
            };
        }

        public static EquationProfile? TryAnalyze(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            try { return Analyze(SandboxExpression.Parse(source)); }
            catch { return null; }
        }

        private sealed class Accumulator
        {
            public bool Antiholomorphic;
            public bool HasAbs;
            public bool Transcendental;
            public int  MaxPolyDegree = -1;
            public bool IterationDependent;
            public bool HasImaginaryConst;
            public bool HasCRef;
            public bool HasZRef;
            public bool HasBranching;
        }

        // zScope tracks whether the current subtree is being walked as the
        // base of a power expression — used to count polynomial degree of z.
        private static void Walk(SbxNode node, Accumulator acc, bool zScope)
        {
            switch (node)
            {
                case SbxConst c:
                    if (!c.V.IsReal && c.V.I != 0.0 && c.V.R == 0.0)
                        acc.HasImaginaryConst = true;
                    break;

                case SbxSlot s:
                    if (s.Slot == SandboxExpression.SlotZ) acc.HasZRef = true;
                    else if (s.Slot == SandboxExpression.SlotC) acc.HasCRef = true;
                    else if (s.Slot == SandboxExpression.SlotN) acc.IterationDependent = true;
                    break;

                case SbxUnary u:
                    Walk(u.A, acc, zScope: false);
                    break;

                case SbxBinary b:
                    if (b.Op == "^")
                    {
                        // Right-side numeric constant ⇒ polynomial degree contribution.
                        int deg = TryReadPolyDegree(b);
                        if (deg > acc.MaxPolyDegree) acc.MaxPolyDegree = deg;
                        Walk(b.A, acc, zScope: false);
                        Walk(b.B, acc, zScope: false);
                    }
                    else if (b.Op == "*" && (IsZRef(b.A) && IsZRef(b.B)))
                    {
                        // z*z — degree 2 contribution.
                        if (acc.MaxPolyDegree < 2) acc.MaxPolyDegree = 2;
                        Walk(b.A, acc, zScope: false);
                        Walk(b.B, acc, zScope: false);
                    }
                    else
                    {
                        if (b.Op is "&&" or "||" or "<" or ">" or "<=" or ">=" or "==" or "!=")
                            acc.HasBranching = true;
                        Walk(b.A, acc, zScope: false);
                        Walk(b.B, acc, zScope: false);
                    }
                    break;

                case SbxTernary t:
                    acc.HasBranching = true;
                    Walk(t.Cond, acc, zScope: false);
                    Walk(t.Then, acc, zScope: false);
                    Walk(t.Else, acc, zScope: false);
                    break;

                case SbxLet l:
                    Walk(l.Value, acc, zScope: false);
                    Walk(l.Body, acc, zScope: false);
                    break;

                case SbxCall call:
                    HandleCall(call, acc);
                    break;
            }
        }

        private static void HandleCall(SbxCall call, Accumulator acc)
        {
            switch (call.Name)
            {
                case "conj":
                    if (ContainsZ(call.Args[0])) acc.Antiholomorphic = true;
                    break;
                case "abs":
                    if (ContainsZ(call.Args[0])) acc.HasAbs = true;
                    break;
                case "sin": case "cos": case "tan": case "exp": case "log":
                    if (ContainsZ(call.Args[0])) acc.Transcendental = true;
                    break;
                case "pow":
                    // pow(z, k) with k constant ⇒ polynomial degree contribution.
                    if (IsZRef(call.Args[0]) && TryConstReal(call.Args[1], out double k))
                    {
                        int deg = (int)Math.Round(k);
                        if (k > 0 && deg > acc.MaxPolyDegree) acc.MaxPolyDegree = deg;
                    }
                    break;
            }
            foreach (var a in call.Args) Walk(a, acc, zScope: false);
        }

        // Recognise z^k where k is a constant real or pow(z, k) collapsed
        // into ^ syntax. Returns -1 when not a clean polynomial degree.
        private static int TryReadPolyDegree(SbxBinary pow)
        {
            if (!IsZRef(pow.A)) return -1;
            if (!TryConstReal(pow.B, out double k)) return -1;
            if (k <= 0) return -1;
            return (int)Math.Round(k);
        }

        private static bool IsZRef(SbxNode n) =>
            n is SbxSlot s && s.Slot == SandboxExpression.SlotZ;

        private static bool TryConstReal(SbxNode n, out double value)
        {
            if (n is SbxConst c && c.V.IsReal) { value = c.V.R; return true; }
            value = 0;
            return false;
        }

        private static bool ContainsZ(SbxNode n)
        {
            switch (n)
            {
                case SbxSlot s: return s.Slot == SandboxExpression.SlotZ;
                case SbxUnary u: return ContainsZ(u.A);
                case SbxBinary b: return ContainsZ(b.A) || ContainsZ(b.B);
                case SbxTernary t: return ContainsZ(t.Cond) || ContainsZ(t.Then) || ContainsZ(t.Else);
                case SbxLet l: return ContainsZ(l.Value) || ContainsZ(l.Body);
                case SbxCall c:
                    foreach (var a in c.Args) if (ContainsZ(a)) return true;
                    return false;
                default: return false;
            }
        }
    }
}
