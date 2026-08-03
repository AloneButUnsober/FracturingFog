// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
//   Complex.ImaginaryOne  → i             (PR8 — DSL has 'i' literal now)
//   new Complex(a, 0)     → a             (real part only — drop the wrapper)
//   new Complex(0, b)     → (b)*i
//   new Complex(a, b)     → (a + (b)*i)
//   Complex.Sin(x)        → sin(x)
//   Complex.Cos(x)        → cos(x)
//   Complex.Exp(x)        → exp(x)
//   Complex.Log(x)        → log(x)
//   Complex.Conjugate(x)  → conj(x)
//   Complex.Divide(a, b)  → ((a)/(b))
//   Complex.Pow(x, k_int) → x^k         (k ≥ 2)
//                         → x           (k == 1)
//                         → 1           (k == 0)
//                         → 1/x^|k|     (k < 0)
//   Complex.Pow(x, expr)  → exp(expr*log(x))    (non-integer exponent)
//
// Member access (#27 Phase 5a — the DSL has these functions; only the C#
// property-access syntax needed a rewrite so saved equations keep working
// after the raw-C# path was removed)
//   x.Real                → re(x)
//   x.Imaginary           → im(x)
//   x.Phase               → arg(x)
//   x.Magnitude           → sqrt(x*conj(x))   (|x|; avoids `abs`, whose meaning
//                           differs between the CalcGen DSL (|x|²) and the
//                           SandboxExpression runtime (|x|) — x*conj(x) = |x|²
//                           and sqrt of that = |x| under both)
//
// Explicit reject (with crisp error messages)
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

/// <summary>Diagnostic info from <see cref="EquationPreprocessor.Preprocess(string, out PreprocessDiagnostic?)"/>.
/// <see cref="Start"/> + <see cref="Length"/> point at the offending substring
/// in the ORIGINAL source.
///
/// Suggestions are split by editor — applying a DSL form (`abs(z)`, `sin(z)`)
/// to a Roslyn-compiled C# editor would fail compilation, and vice versa. UI
/// must pick the field matching the active editor. A null field means there
/// is no safe in-place fix for that editor; the user must rewrite.</summary>
public sealed record PreprocessDiagnostic(
    string Message,
    int Start,
    int Length,
    string? SuggestionCSharp,
    string? SuggestionDsl);

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
        string s = Preprocess(source, out PreprocessDiagnostic? diag);
        error = diag?.Message;
        return s;
    }

    /// <summary>
    /// Span-aware overload. <paramref name="diagnostic"/>'s Start/Length point
    /// at the offending substring in the original source (so an editor can
    /// highlight or select it). Suggestion holds an inline replacement when
    /// one exists, null otherwise.
    /// </summary>
    public static string Preprocess(string source, out PreprocessDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // Track how much of the head we discarded so diagnostic spans can
        // be mapped back to offsets in the ORIGINAL source. Lead = leading
        // whitespace + optional `return\s+`. Substring rewrites below
        // operate on `s`, but every diagnostic Start gets `lead` added so
        // the editor highlights the right character range.
        int lead = 0;
        while (lead < source.Length && char.IsWhiteSpace(source[lead])) lead++;
        string s = source[lead..];
        var mReturn = Regex.Match(s, @"^return\s+");
        if (mReturn.Success) { lead += mReturn.Length; s = s[mReturn.Length..]; }
        s = s.TrimEnd().TrimEnd(';').TrimEnd();

        // Constants — straightforward token substitution. Word-boundary
        // anchored so `Complex.Zero1` (unlikely but possible) isn't hit.
        s = Regex.Replace(s, @"\bComplex\.Zero\b",  "0");
        s = Regex.Replace(s, @"\bComplex\.One\b",   "1");
        // PR8: 'i' literal is now first-class in the DSL — rewrite the BCL
        // form directly. No diagnostic (it's no longer a rejected
        // construct) and downstream consumers see a plain `i` token.
        s = Regex.Replace(s, @"\bComplex\.ImaginaryOne\b", "i");

        // PR8: `new Complex(a, b)` now translates to a DSL `(a + (b)*i)`
        // expression. Special cases drop redundant parts:
        //   (a, 0)  → a
        //   (0, b)  → (b)*i
        //   (0, 1)  → i
        //   (0, -1) → -i
        // Anything else: full `(a + (b)*i)` form. We do this BEFORE the
        // function-call rewrites so the trailing `)` of the ctor doesn't
        // get parsed as part of a Complex.X(...) call.
        for (int ctorPass = 0; ctorPass < 32; ctorPass++)
        {
            var mNew = Regex.Match(s, @"\bnew\s+Complex\s*\(");
            if (!mNew.Success) break;
            int openIdx = mNew.Index + mNew.Length - 1;
            int closeIdx = FindMatchingParen(s, openIdx);
            if (closeIdx < 0) break; // unbalanced — let downstream error surface
            string inner = s.Substring(openIdx + 1, closeIdx - openIdx - 1);
            string[] args = SplitTopLevelCommas(inner);
            string replacement;
            if (args.Length == 2)
            {
                string realPart = args[0].Trim();
                string imagPart = args[1].Trim();
                bool imagIsZero = imagPart == "0" || imagPart == "0.0" || imagPart == "0d" || imagPart == "0f";
                bool realIsZero = realPart == "0" || realPart == "0.0" || realPart == "0d" || realPart == "0f";
                bool imagIsOne  = imagPart == "1" || imagPart == "1.0" || imagPart == "1d" || imagPart == "1f";
                bool imagIsNegOne = imagPart == "-1" || imagPart == "-1.0" || imagPart == "-1d" || imagPart == "-1f";
                if (imagIsZero && !realIsZero) replacement = $"({realPart})";
                else if (realIsZero && imagIsOne) replacement = "i";
                else if (realIsZero && imagIsNegOne) replacement = "(-i)";
                else if (realIsZero) replacement = $"(({imagPart})*i)";
                else replacement = $"(({realPart}) + ({imagPart})*i)";
            }
            else if (args.Length == 1)
            {
                // `new Complex(x)` — implicit zero imag in BCL semantics.
                replacement = $"({args[0].Trim()})";
            }
            else
            {
                // Bad arity — drop a diagnostic and bail so the downstream
                // parser produces a clearer error.
                diagnostic = new PreprocessDiagnostic(
                    "'new Complex(...)' expects 1 or 2 arguments.",
                    mNew.Index + lead, closeIdx - mNew.Index + 1,
                    SuggestionCSharp: null, SuggestionDsl: null);
                return s;
            }
            s = s.Substring(0, mNew.Index) + replacement + s.Substring(closeIdx + 1);
        }

        // #27 Phase 5a — member-access rewrite. Run before the Complex.Abs /
        // unsupported-member scans so an operand like `Complex.Sin(z).Real`
        // resolves to `re(Complex.Sin(z))` and the inner call is picked up by
        // the normal rewrite loop below.
        s = RewriteMemberAccess(s);

        var mAbs = Regex.Match(s, @"\bComplex\.Abs\s*\(");
        if (mAbs.Success)
        {
            // Extend match through the balanced argument list so the span
            // highlights `Complex.Abs(x)` whole, not just the `Complex.Abs(`.
            int close = FindMatchingParen(s, mAbs.Index + mAbs.Length - 1);
            int spanLen = close > 0 ? close - mAbs.Index + 1 : mAbs.Length;
            string? csFix = null;
            string? dslFix = null;
            if (close > 0)
            {
                string innerExpr = s.Substring(mAbs.Index + mAbs.Length, close - (mAbs.Index + mAbs.Length)).Trim();
                // DSL form: `abs(x)` (squared magnitude, |x|²).
                dslFix = $"abs({innerExpr})";
                // C# form: x * Complex.Conjugate(x). Yields |x|² as a Complex
                // (real part = |x|², imag = 0). Compiles under Roslyn AND
                // passes the CalcGen preprocessor — the Conjugate call gets
                // rewritten to `conj(...)` downstream.
                csFix = $"({innerExpr} * Complex.Conjugate({innerExpr}))";
            }
            diagnostic = new PreprocessDiagnostic(
                "Complex.Abs(x) returns |x| (square root of |x|²). " +
                "The CalcGen DSL has only `abs(x)` which means |x|² (squared magnitude). " +
                "If you can use the squared form, rewrite as `abs(x)`. " +
                "If you genuinely need the sqrt, it's not available.",
                mAbs.Index + lead, spanLen, SuggestionCSharp: csFix, SuggestionDsl: dslFix);
            return s;
        }

        // Surface unsupported `Complex.X` members BEFORE the rewrite loop
        // mutates the string. Doing it after would mean span offsets here
        // no longer correspond to the user's typed text (Sin/Cos rewrites
        // shorten the string). Recognised members are skipped — anything
        // else short-circuits with a span pointing at the user's character.
        var known = new HashSet<string>(StringComparer.Ordinal)
        { "Sin", "Cos", "Tan", "Sinh", "Cosh", "Tanh", "Sqrt",
          "Exp", "Log", "Conjugate", "Pow", "Phase", "Divide",
          "Zero", "One", "ImaginaryOne", "Abs" };
        foreach (Match m in Regex.Matches(s, @"\bComplex\.([A-Za-z_][A-Za-z0-9_]*)\b"))
        {
            string member = m.Groups[1].Value;
            if (known.Contains(member)) continue;
            // Levenshtein-suggest the closest recognised member so a typo
            // like `Complex.Sni` offers `Complex.Sin` as a one-click fix.
            // Only the call-shaped members (Sin/Cos/Exp/Log/Conjugate/Pow)
            // make sense as replacements — Zero/One/ImaginaryOne/Abs are
            // properties or already-rejected forms.
            string[] callable = { "Sin", "Cos", "Tan", "Sinh", "Cosh", "Tanh", "Sqrt",
                                  "Exp", "Log", "Conjugate", "Pow", "Phase" };
            string? best = null;
            int bestD = int.MaxValue;
            foreach (var k in callable)
            {
                int d = Levenshtein(member, k);
                if (d < bestD) { bestD = d; best = k; }
            }
            // C# form: `Complex.Sin` (PascalCase, BCL-shaped).
            // DSL form: `sin` (lowercase, no namespace) — and `Conjugate`
            // shortens to `conj` in DSL too.
            string? csFix  = bestD <= 2 && best != null ? $"Complex.{best}" : null;
            string? dslFix = bestD <= 2 && best != null
                ? (best switch { "Conjugate" => "conj", "Phase" => "arg", _ => best.ToLowerInvariant() })
                : null;
            string hint = csFix != null ? $" Did you mean '{csFix}'?" : "";
            diagnostic = new PreprocessDiagnostic(
                $"Unsupported '{m.Value}'.{hint} The CalcGen DSL recognises only " +
                "Complex.Pow / Sin / Cos / Tan / Sinh / Cosh / Tanh / Sqrt / Exp / Log / " +
                "Conjugate / Zero / One. " +
                "Other System.Numerics.Complex members have no DSL equivalent.",
                m.Index + lead, m.Length, SuggestionCSharp: csFix, SuggestionDsl: dslFix);
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
            s = RewriteCall(s, "Complex.Tan",       args => args.Length == 1 ? $"tan({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Sinh",      args => args.Length == 1 ? $"sinh({args[0].Trim()})" : null);
            s = RewriteCall(s, "Complex.Cosh",      args => args.Length == 1 ? $"cosh({args[0].Trim()})" : null);
            s = RewriteCall(s, "Complex.Tanh",      args => args.Length == 1 ? $"tanh({args[0].Trim()})" : null);
            s = RewriteCall(s, "Complex.Sqrt",      args => args.Length == 1 ? $"sqrt({args[0].Trim()})" : null);
            s = RewriteCall(s, "Complex.Exp",       args => args.Length == 1 ? $"exp({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Log",       args => args.Length == 1 ? $"log({args[0].Trim()})"  : null);
            s = RewriteCall(s, "Complex.Conjugate", args => args.Length == 1 ? $"conj({args[0].Trim()})" : null);
            // Complex.Divide(a, b) is plain division; wrap both operands so the
            // surrounding precedence is preserved.
            s = RewriteCall(s, "Complex.Divide", args => args.Length == 2 ? $"(({args[0].Trim()})/({args[1].Trim()}))" : null);
            // Complex.Phase is the BCL accessor for arg(z). Translates 1:1.
            s = RewriteCall(s, "Complex.Phase",     args => args.Length == 1 ? $"arg({args[0].Trim()})"  : null);
            // Math.Atan2(y, x) is real-valued; DSL atan2 lifts to complex.
            s = RewriteCall(s, "Math.Atan2",        args => args.Length == 2 ? $"atan2({args[0].Trim()}, {args[1].Trim()})" : null);
            // Math.Min / Max / IEEERemainder → DSL counterparts.
            s = RewriteCall(s, "Math.Min",          args => args.Length == 2 ? $"min({args[0].Trim()}, {args[1].Trim()})" : null);
            s = RewriteCall(s, "Math.Max",          args => args.Length == 2 ? $"max({args[0].Trim()}, {args[1].Trim()})" : null);
            s = RewriteCall(s, "Math.IEEERemainder", args => args.Length == 2 ? $"mod({args[0].Trim()}, {args[1].Trim()})" : null);

            // Pow: only POSITIVE integer exponents get the `(x)^n` fast path
            // (polynomial-detector / perturbation Taylor friendly, and 0^n = 0
            // matches Complex.Pow). Negative and non-integer/expression
            // exponents translate to the `pow(x, y)` DSL function, which the
            // SandboxExpression runtime evaluates via Complex.Pow — crucially
            // replicating .NET's zero guards (`Pow(0, k)` = 0 for k != 0,
            // `Pow(x, 0)` = 1). The earlier `1/(x)^n` / `exp(y·log x)` forms did
            // NOT: they yield NaN at x = 0, so maps singular at the z = 0 seed
            // (negative powers of z; `sin(z)^k` at z = 0) rendered blank whereas
            // the original Complex.Pow rendered them. #27 Phase 5a.
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
                    // Negative integer: pow() (Complex.Pow), NOT 1/x^|n| — the
                    // latter is NaN at x = 0 where Complex.Pow(0, -n) = 0.
                    return $"pow({baseExpr}, {n})";
                }
                // General exponent: pow() (Complex.Pow), NOT exp(y·log x) — the
                // latter is NaN when the base is 0 (e.g. exponent 0 gives NaN
                // instead of Complex.Pow(x, 0) = 1).
                return $"pow({baseExpr}, {expExpr})";
            });

            if (s == before) break;
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

    /// <summary>
    /// #27 Phase 5a — rewrite `operand.Real / .Imaginary / .Phase / .Magnitude`
    /// (System.Numerics.Complex accessors) into the DSL functions the sandbox
    /// interpreter already supports: re / im / arg / sqrt(abs(·)). The operand
    /// is the value immediately to the left of the `.` — a bare identifier /
    /// number, a parenthesised group, or a call result (`f(x)` incl. a dotted
    /// callee like `Complex.Sin(x)`). Applied to a fixed point so chained /
    /// nested accessors fully resolve.
    /// </summary>
    private static string RewriteMemberAccess(string source)
    {
        string s = source;
        for (int guard = 0; guard < 64; guard++)
        {
            var m = Regex.Match(s, @"\.(Real|Imaginary|Magnitude|Phase)\b");
            if (!m.Success) break;

            int dotIndex = m.Index;
            int opStart = FindOperandStart(s, dotIndex);
            if (opStart < 0)
                break; // no valid operand — let the downstream parser report it

            string operand = s.Substring(opStart, dotIndex - opStart);
            int memberEnd = dotIndex + 1 + m.Groups[1].Value.Length; // past `.Member`
            string repl = m.Groups[1].Value switch
            {
                "Real"      => $"re({operand})",
                "Imaginary" => $"im({operand})",
                "Phase"     => $"arg({operand})",
                // |x| without `abs` (whose |x| vs |x|² meaning differs between
                // the CalcGen DSL and the SandboxExpression runtime): x*conj(x)
                // = |x|² in both, and sqrt of that is |x|.
                "Magnitude" => $"sqrt(({operand})*conj({operand}))",
                _           => operand,
            };
            s = s.Substring(0, opStart) + repl + s.Substring(memberEnd);
        }
        return s;
    }

    /// <summary>Index where the operand immediately left of <paramref name="dotIndex"/>
    /// begins, or -1 when there is no value there. Handles a balanced `(…)`
    /// group (optionally prefixed by a dotted callee name) and a bare
    /// identifier / number run.</summary>
    private static int FindOperandStart(string s, int dotIndex)
    {
        int i = dotIndex - 1;
        if (i < 0) return -1;
        char c = s[i];

        if (c == ')')
        {
            int depth = 0;
            while (i >= 0)
            {
                if (s[i] == ')') depth++;
                else if (s[i] == '(') { depth--; if (depth == 0) break; }
                i--;
            }
            if (i < 0) return -1; // unbalanced
            // Absorb a preceding dotted callee (`Complex.Sin`, `sin`).
            int j = i - 1;
            while (j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.')) j--;
            return j + 1;
        }

        if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
        {
            int j = i;
            while (j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '.')) j--;
            return j + 1;
        }

        return -1;
    }

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

    // Tiny iterative Levenshtein for member-name suggestions (same logic
    // as EquationLexer.Levenshtein — duplicated here so this file has no
    // dependency on the lexer module).
    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
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
