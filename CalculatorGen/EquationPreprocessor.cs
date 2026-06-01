// EquationPreprocessor.cs
//
// Translates a User-Equation text box's C# `Complex.*` syntax into the
// CalcGen DSL accepted by EquationLexer + EquationParser. Lets users keep
// writing in the historical System.Numerics.Complex form while CalcGen's
// hot-load + generate paths consume the restricted DSL.
//
// Translation table
//   return X;             → X
//   Complex.Zero          → 0
//   Complex.One           → 1
//   Complex.Sin(x)        → sin(x)
//   Complex.Cos(x)        → cos(x)
//   Complex.Exp(x)        → exp(x)
//   Complex.Log(x)        → log(x)
//   Complex.Conjugate(x)  → conj(x)
//   Complex.Pow(x, k_int) → x^k         (k ≥ 2)
//                         → x           (k == 1)
//                         → 1           (k == 0)
//                         → 1/x^|k|     (k < 0)
//   Complex.Pow(x, expr)  → exp(expr*log(x))    (non-integer exponent)
//
// Explicit reject (with crisp error messages)
//   Complex.ImaginaryOne  — no 'i' literal in DSL
//   new Complex(a, b)     — same; no 'i' literal
//   Complex.Abs(x)        — DSL `abs(x)` is |x|² (squared mag), not |x|
//   Any other Complex.X   — falls through, reported on second pass
//
// Operator precedence and structure of the surrounding expression are
// preserved by parenthesising every rewritten substring.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace FracturingFog.CalculatorGen;

public static class EquationPreprocessor
{
    /// <summary>
    /// Translate a raw User-Equation text (which may contain C# `Complex.*`
    /// calls and a `return ... ;` wrapper) into the CalcGen DSL.
    /// Returns the translated DSL string. On unsupported constructs sets
    /// <paramref name="error"/> to a user-facing message and returns the
    /// (partial) translated string for diagnostics.
    /// </summary>
    public static string Preprocess(string source, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // Strip surrounding `return ... ;` so the user can paste the
        // contents of their step function directly.
        string s = source.Trim();
        s = Regex.Replace(s, @"^\s*return\s+", "");
        s = s.TrimEnd(';').Trim();

        // Constants — straightforward token substitution. Word-boundary
        // anchored so `Complex.Zero1` (unlikely but possible) isn't hit.
        s = Regex.Replace(s, @"\bComplex\.Zero\b",  "0");
        s = Regex.Replace(s, @"\bComplex\.One\b",   "1");

        // Hard-reject constructs with no DSL counterpart.
        if (Regex.IsMatch(s, @"\bComplex\.ImaginaryOne\b"))
        {
            error = "Complex.ImaginaryOne ('i') has no representation in the CalcGen DSL — " +
                    "there is no 'i' literal. Decompose the equation so it uses only " +
                    "z, c, real literals, and the DSL ops (+, -, *, /, ^Int, sin, cos, exp, log, conj, fold).";
            return s;
        }
        if (Regex.IsMatch(s, @"\bnew\s+Complex\s*\("))
        {
            error = "'new Complex(a, b)' has no representation in the CalcGen DSL — " +
                    "there is no 'i' literal. Use real-only expressions on z and c.";
            return s;
        }
        if (Regex.IsMatch(s, @"\bComplex\.Abs\s*\("))
        {
            error = "Complex.Abs(x) returns |x| (square root of |x|²). " +
                    "The CalcGen DSL has only `abs(x)` which means |x|² (squared magnitude). " +
                    "If you can use the squared form, rewrite as `abs(x)`. " +
                    "If you genuinely need the sqrt, it's not available.";
            return s;
        }

        // Member-call rewrites. Applied to a fixed point so nested calls
        // like `Complex.Pow(Complex.Pow(z, 2), 3)` translate fully — each
        // pass rewrites the outermost call; the freshly-revealed inner
        // call is picked up on the next pass. Cap at 32 iters to prevent
        // a pathological loop on a never-collapsing input (shouldn't
        // happen given each successful rewrite shortens or stabilises
        // the string, but defensive).
        for (int pass = 0; pass < 32; pass++)
        {
            string before = s;
            s = RewriteCall(s, "Complex.Sin",       args => args.Length == 1 ? $"sin({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Cos",       args => args.Length == 1 ? $"cos({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Exp",       args => args.Length == 1 ? $"exp({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Log",       args => args.Length == 1 ? $"log({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Conjugate", args => args.Length == 1 ? $"conj({args[0].Trim()})" : null);

            // Pow has a special-case integer-exponent fast path so common
            // cases like `Complex.Pow(z, -3)` translate to a clean `1/(z)^3`
            // instead of `exp(-3*log(z))` (which is correct but loses the
            // polynomial-detector classification, perturbation Taylor, etc.).
            s = RewriteCall(s, "Complex.Pow", args =>
            {
                if (args.Length != 2) return null;
                string baseExpr = args[0].Trim();
                string expExpr  = args[1].Trim();
                if (int.TryParse(expExpr, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out int n))
                {
                    if (n == 0) return "1";
                    if (n == 1) return $"({baseExpr})";
                    if (n > 0) return $"({baseExpr})^{n}";
                    // Negative: rewrite 1 / x^|n|. Wraps in extra parens so
                    // surrounding precedence still bites correctly.
                    return $"(1/({baseExpr})^{-n})";
                }
                // General complex exponent: x^y ≡ exp(y · log(x)). Loses any
                // SA / perturbation Taylor benefit (becomes a transcendental
                // chain) but keeps the math correct.
                return $"exp(({expExpr})*log({baseExpr}))";
            });

            if (s == before) break;
        }

        // Anything still wearing a `Complex.` prefix slipped past every
        // known translation — flag it explicitly so the user gets a
        // pointed message instead of a downstream lexer "Unknown identifier
        // 'Complex'" diagnostic.
        var leftover = Regex.Match(s, @"\bComplex\.[A-Za-z_][A-Za-z0-9_]*\b");
        if (leftover.Success)
        {
            error = $"Unsupported '{leftover.Value}'. The CalcGen DSL recognises only " +
                    "Complex.Pow / Sin / Cos / Exp / Log / Conjugate / Zero / One. " +
                    "Other System.Numerics.Complex members have no DSL equivalent.";
        }
        return s;
    }

    /// <summary>
    /// Find every call to <paramref name="funcName"/> whose `(` follows
    /// the name immediately, parse balanced parens to recover the
    /// comma-separated argument list, and replace the entire `name(args)`
    /// span with whatever <paramref name="rewrite"/> returns. Returning
    /// null from rewrite leaves the original text intact (arity mismatch
    /// etc. — surfaced by the downstream parser instead of here).
    /// </summary>
    private static string RewriteCall(string source, string funcName, Func<string[], string?> rewrite)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            // Tentative match: funcName begins at i AND is followed by '('.
            if (i + funcName.Length < source.Length
                && source.Substring(i, funcName.Length) == funcName
                && source[i + funcName.Length] == '(')
            {
                // Reject if the preceding char makes this an identifier
                // suffix (e.g. `XComplex.Pow(` should not match `Complex.Pow`).
                bool boundaryOk = i == 0 || !IsIdentPart(source[i - 1]);
                if (boundaryOk)
                {
                    int parenStart = i + funcName.Length;       // index of '('
                    int closeIdx = FindMatchingParen(source, parenStart);
                    if (closeIdx > parenStart)
                    {
                        string inner = source.Substring(parenStart + 1, closeIdx - parenStart - 1);
                        string[] args = SplitTopLevelCommas(inner);
                        string? replacement = rewrite(args);
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
        int depth = 0; int last = 0;
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
