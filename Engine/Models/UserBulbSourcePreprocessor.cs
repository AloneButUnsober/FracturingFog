// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserBulbSourcePreprocessor.cs
//
// #27 / #211 — translates a historical C# 3D-bulb body (Vec3 / Quat form) into
// the safe SandboxBulbExpression DSL, so saved user bulbs keep working after the
// raw-C# Roslyn path was removed (Phase 3). The 3D analogue of
// EquationPreprocessor: that one rewrites 2D `Complex.*` equations; this one
// rewrites `Vec3`/`Quat` step bodies.
//
// Two layers:
//   1. Statement-block desugar — `var`/typed decls, reassignments, `if (cond) v =
//      …;` / `if (cond) return …;` guards, and a final `return …;` (or bare
//      trailing expression) desugar to the DSL's `let … in` + ternary (which the
//      SandboxBulbExpression parser already accepts). Sequential reassignment
//      relies on same-name `let` shadowing: each `let` allocates a fresh slot,
//      the value expression sees the prior binding, the body sees the new one.
//   2. Token/call rewrites — `new Vec3(…)`→`vec(…)`, `new Quat(…)`→`qvec(…)`,
//      `Vec3.Pow(b,e)`→`((b)^(e))` (triplex power operator), every other
//      `Vec3.Fn`/`Quat.Fn`/`Math.Fn` call to its lowercase DSL builtin, `.X`→`.x`
//      (incl. `.W`→`.w` for Quat), and the `Math.PI`/`Math.E`/`Vec3.Zero`
//      constants.
//
// Translation is best-effort and conservative: any construct with no DSL form
// (brace blocks, `else`, `for`/`while`, an unknown `Foo.Bar` member, a method the
// map doesn't cover such as `Quat.FromVec3`/`.ToVec3()`/`.Length`) leaves a token
// the DSL grammar rejects. The caller (UserBulbDslMigration) validates the output
// by parsing it; on failure the saved bulb is left editable, unchanged. This file
// is pure string work — no Vec3/Quat math, no BCL surface beyond regex.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FracturingFog.Models;

public static class UserBulbSourcePreprocessor
{
    /// <summary>Translate a C# Vec3/Quat bulb body to SandboxBulbExpression DSL
    /// text. Returns the DSL string, or <c>null</c> when the body uses a
    /// construct with no DSL form (the caller then leaves the source editable).
    /// This performs the syntactic rewrite only; the caller is responsible for
    /// validating that the result parses.</summary>
    public static string? Preprocess(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            string stripped = StripComments(source);
            var stmts = SplitStatements(stripped);
            if (stmts.Count == 0) return null;
            string expr = Desugar(stmts, 0);
            string dsl = ApplyRewrites(expr).Trim();
            return string.IsNullOrWhiteSpace(dsl) ? null : dsl;
        }
        catch (NotSupportedException) { return null; }
        catch (Exception) { return null; }
    }

    // ── statement-block front-end ───────────────────────────────────────────

    private static string StripComments(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//[^\n]*", "");
        return s;
    }

    /// <summary>Split into top-level (paren-depth-0) `;`-terminated statements
    /// plus a trailing bare expression. A `{`/`}` brace block has no DSL form —
    /// bail so the whole body is treated as untranslatable.</summary>
    private static List<string> SplitStatements(string s)
    {
        var list = new List<string>();
        int depth = 0, last = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '{' || c == '}') throw new NotSupportedException("brace block");
            else if (c == ';' && depth == 0)
            {
                string frag = s.Substring(last, i - last).Trim();
                if (frag.Length > 0) list.Add(frag);
                last = i + 1;
            }
        }
        string tail = s.Substring(last).Trim();
        if (tail.Length > 0) list.Add(tail);
        return list;
    }

    /// <summary>Recursively desugar statement <paramref name="idx"/> and the rest
    /// of the block into a single DSL expression. Throws
    /// <see cref="NotSupportedException"/> on a form with no DSL shape.</summary>
    private static string Desugar(List<string> stmts, int idx)
    {
        if (idx >= stmts.Count) throw new NotSupportedException("empty block");
        string st = stmts[idx];
        bool last = idx == stmts.Count - 1;

        // if (cond) <simple-stmt>
        if (Regex.IsMatch(st, @"^if\s*\("))
        {
            int open = st.IndexOf('(');
            int close = FindMatchingParen(st, open);
            if (close < 0) throw new NotSupportedException("if paren");
            string cond = st.Substring(open + 1, close - open - 1).Trim();
            string body = st.Substring(close + 1).Trim();

            if (IsReturn(body, out string retExpr))
            {
                // if (cond) return E;  →  (cond) ? (E) : (rest)
                string rest = Desugar(stmts, idx + 1);
                return $"(({cond}) ? ({retExpr}) : ({rest}))";
            }
            var asg = TrySplitAssign(body);
            if (asg == null) throw new NotSupportedException("if body");
            if (last) throw new NotSupportedException("if-assign as last statement");
            string tail = Desugar(stmts, idx + 1);
            // if (cond) id = E;  →  let id = ((cond) ? (E) : id) in rest
            return $"let {asg.Value.Id} = (({cond}) ? ({asg.Value.Expr}) : {asg.Value.Id}) in {tail}";
        }

        // return E   (must be the block's value)
        if (IsReturn(st, out string tailExpr))
        {
            if (!last) throw new NotSupportedException("return before end of block");
            return tailExpr;
        }

        // TYPE? id = E   (declaration or reassignment)
        var a = TrySplitAssign(StripTypePrefix(st));
        if (a != null)
        {
            if (last) throw new NotSupportedException("assignment produces no block value");
            string tail = Desugar(stmts, idx + 1);
            return $"let {a.Value.Id} = ({a.Value.Expr}) in {tail}";
        }

        // Bare trailing expression = implicit return.
        if (last) return st;
        throw new NotSupportedException("unrecognised statement");
    }

    private static bool IsReturn(string s, out string expr)
    {
        if (s.StartsWith("return", StringComparison.Ordinal)
            && (s.Length == 6 || !IsIdentPart(s[6])))
        {
            expr = s.Substring(6).Trim();
            return expr.Length > 0;
        }
        expr = string.Empty;
        return false;
    }

    /// <summary>Strip a leading C# type keyword from a declaration
    /// (`var v = …`, `Vec3 v = …`, `double a = …`) so what remains is a plain
    /// `id = expr` assignment. Only fires on `KEYWORD<space>identifier`, so an
    /// expression like `Vec3.Rot(…)` (dot, not space) is untouched.</summary>
    private static string StripTypePrefix(string s)
        => Regex.Replace(s, @"^(var|Vec3|Quat|double|float|int|long|decimal|bool)\s+(?=[A-Za-z_])", "");

    /// <summary>Split `id = expr` on the first top-level `=` that is not part of
    /// a comparison (`==`, `<=`, `>=`, `!=`). Returns null when the left side is
    /// not a bare identifier (so an equation like `a == b` is not misread).</summary>
    private static (string Id, string Expr)? TrySplitAssign(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '=' && depth == 0)
            {
                char prev = i > 0 ? s[i - 1] : '\0';
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (prev == '=' || prev == '<' || prev == '>' || prev == '!') continue; // ==,<=,>=,!=
                if (next == '=') continue; // '==' seen from the left
                string lhs = s.Substring(0, i).Trim();
                string rhs = s.Substring(i + 1).Trim();
                if (rhs.Length == 0) return null;
                return Regex.IsMatch(lhs, @"^[A-Za-z_][A-Za-z0-9_]*$") ? (lhs, rhs) : null;
            }
        }
        return null;
    }

    // ── token / call rewrites ───────────────────────────────────────────────

    private static readonly Dictionary<string, string> VecMap = new(StringComparer.Ordinal)
    {
        // Pow is handled structurally (→ `^` operator), so it is deliberately
        // absent here: a stray `Vec3.Pow(` left by one rewrite pass is picked up
        // by the RewriteCall in the next pass rather than mis-mapped.
        ["Sin"] = "sin", ["Cos"] = "cos", ["Tan"] = "tan",
        ["Sinh"] = "sinh", ["Cosh"] = "cosh", ["Tanh"] = "tanh",
        ["Exp"] = "exp", ["Log"] = "log", ["Sqrt"] = "sqrt", ["Abs"] = "abs",
        ["AbsX"] = "absx", ["AbsY"] = "absy", ["AbsZ"] = "absz",
        ["BoxFold"] = "boxfold", ["SphereFold"] = "spherefold", ["Rot"] = "rot",
        ["Dot"] = "dot", ["Cross"] = "cross", ["Normalize"] = "normalize",
        ["Length"] = "length", ["Mod"] = "mod", ["Triplex"] = "triplex", ["SMin"] = "smin",
    };

    private static readonly Dictionary<string, string> QuatMap = new(StringComparer.Ordinal)
    {
        ["Sin"] = "qsin", ["Cos"] = "qcos", ["Tan"] = "qtan",
        ["Sinh"] = "qsinh", ["Cosh"] = "qcosh", ["Tanh"] = "qtanh",
        ["Asin"] = "qasin", ["Acos"] = "qacos", ["Atan"] = "qatan",
        ["Asinh"] = "qasinh", ["Acosh"] = "qacosh", ["Atanh"] = "qatanh",
        ["Exp"] = "qexp", ["Log"] = "qlog", ["Sqrt"] = "qsqrt",
        ["Inverse"] = "qinv", ["Conjugate"] = "qconj",
        ["Mul"] = "qmul", ["Pow"] = "qpow",
        ["Csc"] = "qcsc", ["Sec"] = "qsec", ["Cot"] = "qcot",
        ["Csch"] = "qcsch", ["Sech"] = "qsech", ["Coth"] = "qcoth",
    };

    private static readonly Dictionary<string, string> MathMap = new(StringComparer.Ordinal)
    {
        ["Sin"] = "sin", ["Cos"] = "cos", ["Tan"] = "tan",
        ["Sinh"] = "sinh", ["Cosh"] = "cosh", ["Tanh"] = "tanh",
        ["Exp"] = "exp", ["Log"] = "log", ["Sqrt"] = "sqrt", ["Abs"] = "abs",
        ["Floor"] = "floor", ["Sign"] = "sign", ["Min"] = "min", ["Max"] = "max", ["Pow"] = "pow",
    };

    private static string ApplyRewrites(string s)
    {
        for (int pass = 0; pass < 32; pass++)
        {
            string before = s;
            // Vec3.Pow(base, exp) → ((base)^(exp)) — triplex Mandelbulb power.
            // Looped so a nested Vec3.Pow(Vec3.Pow(…)) fully collapses.
            s = RewriteCall(s, "Vec3.Pow", args =>
                args.Length == 2 ? $"(({args[0].Trim()})^({args[1].Trim()}))" : null);
            // Every other Vec3.Fn / Quat.Fn / Math.Fn call → its DSL builtin.
            s = Regex.Replace(s, @"\b(Vec3|Quat|Math)\.([A-Za-z_][A-Za-z0-9_]*)\s*\(", RenameCallee);
            if (s == before) break;
        }

        s = Regex.Replace(s, @"\bnew\s+Vec3\s*\(", "vec(");
        s = Regex.Replace(s, @"\bnew\s+Quat\s*\(", "qvec(");
        s = Regex.Replace(s, @"\bVec3\.Zero\b", "vec(0,0,0)");
        s = Regex.Replace(s, @"\bQuat\.Zero\b", "qvec(0,0,0,0)");
        s = Regex.Replace(s, @"\bMath\.PI\b", "pi");
        s = Regex.Replace(s, @"\bMath\.E\b", "e");
        // Component access: .X/.Y/.Z (and Quat .W) → lowercase DSL members.
        s = Regex.Replace(s, @"\.(X|Y|Z|W)\b", m => "." + m.Groups[1].Value.ToLowerInvariant());
        return s;
    }

    private static string RenameCallee(Match m)
    {
        string type = m.Groups[1].Value;
        string name = m.Groups[2].Value;
        var map = type == "Vec3" ? VecMap : type == "Quat" ? QuatMap : MathMap;
        // Unmapped members (Quat.FromVec3, Math.Atan2, …) are left verbatim so
        // the downstream parse rejects the body and it stays editable.
        return map.TryGetValue(name, out string? dsl) ? dsl + "(" : m.Value;
    }

    // ── shared paren/arg helpers (same shape as EquationPreprocessor) ────────

    private static string RewriteCall(string source, string funcName, Func<string[], string?> rewrite)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (i + funcName.Length < source.Length
                && source.Substring(i, funcName.Length) == funcName
                && source[i + funcName.Length] == '(')
            {
                bool boundaryOk = i == 0 || !IsIdentPart(source[i - 1]);
                if (boundaryOk)
                {
                    int parenStart = i + funcName.Length;
                    int closeIdx = FindMatchingParen(source, parenStart);
                    if (closeIdx > parenStart)
                    {
                        string inner = source.Substring(parenStart + 1, closeIdx - parenStart - 1);
                        string? replacement = rewrite(SplitTopLevelCommas(inner));
                        if (replacement != null)
                        {
                            sb.Append(replacement);
                            i = closeIdx + 1;
                            continue;
                        }
                    }
                }
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    private static int FindMatchingParen(string s, int openIdx)
    {
        if (openIdx >= s.Length || s[openIdx] != '(') return -1;
        int depth = 0;
        for (int j = openIdx; j < s.Length; j++)
        {
            if (s[j] == '(') depth++;
            else if (s[j] == ')')
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return -1;
    }

    private static string[] SplitTopLevelCommas(string s)
    {
        var parts = new List<string>();
        int depth = 0, last = 0;
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0)
            {
                parts.Add(s.Substring(last, k - last));
                last = k + 1;
            }
        }
        parts.Add(s.Substring(last));
        return parts.ToArray();
    }
}
