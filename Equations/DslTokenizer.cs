// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslTokenizer.cs
//
// #1363 (S2 of #1360) — lenient tokenizer for editor highlighting. The parsers
// (EquationFrontEnd) are scannerless and throw on the first error; an editor
// needs a token stream for ANY text, half-typed or broken, so this is a
// separate, never-throwing lexer that only classifies words. It does not judge
// grammar; parse errors stay the parsers' job (squiggles, #1365).
//
// Two dialects:
//   Complex2D — the Sandbox interpreter language, used by both the Sandbox and
//               User Equation editors (UE renders through the same interpreter).
//   Bulb3D    — the User Bulb step / DE-body language, including the C#-style
//               forms UserBulbSourcePreprocessor accepts (`new Vec3(…)`,
//               `Math.Sqrt(…)`, `Vec3.Dot(…)`, `.X`).
//
// Word lists come from the parsers' own tables (SandboxExpression,
// BulbVocabulary), so the highlighter cannot drift from the language.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    public enum DslDialect
    {
        Complex2D,
        Bulb3D,
    }

    public enum DslTokenKind
    {
        /// <summary>A name the language does not predefine (locals, params).</summary>
        Identifier,
        /// <summary>A built-in input or constant (z, c, n, pi, …).</summary>
        Variable,
        /// <summary>A built-in function name followed by '('.</summary>
        Function,
        /// <summary>return / if / then / else / let / in (and `new` in Bulb).</summary>
        Keyword,
        /// <summary>Declaration type words (var, Complex, Vec3, …) and C#-style
        /// prefixes (Math, Vec3, Quat).</summary>
        Type,
        Number,
        Operator,
        Bracket,
        /// <summary>A member after '.' (z.x, v.Y, Math.PI).</summary>
        Member,
        Comment,
        /// <summary>A character no dialect accepts, e.g. '@'.</summary>
        Error,
    }

    public readonly record struct DslToken(DslTokenKind Kind, int Start, int Length)
    {
        public int End => Start + Length;
    }

    public static class DslTokenizer
    {
        private static readonly HashSet<string> Keywords2D =
            new(StringComparer.Ordinal) { "return", "if", "then", "else", "let", "in" };

        private static readonly HashSet<string> KeywordsBulb =
            new(StringComparer.Ordinal) { "return", "if", "then", "else", "let", "in", "new" };

        // SandboxExpression's declaration types (its EquationSyntax).
        private static readonly HashSet<string> Types2D =
            new(StringComparer.Ordinal) { "var", "Complex", "double", "int", "float", "long", "decimal" };

        // C#-style static prefixes the bulb preprocessor rewrites.
        private static readonly HashSet<string> BulbPrefixes =
            new(StringComparer.Ordinal) { "Math", "Vec3", "Quat" };

        private static readonly HashSet<string> Functions2D =
            new(SandboxExpression.FunctionNames, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> FunctionsBulb =
            new(BulbVocabulary.FunctionNames, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Builtins2D =
            new(SandboxExpression.BuiltInIdentifiers, StringComparer.Ordinal);

        private static readonly HashSet<string> BuiltinsBulb =
            new(BulbVocabulary.BuiltInIdentifiers, StringComparer.Ordinal);

        private static readonly string[] TwoCharOps = { "==", "!=", "<=", ">=", "&&", "||" };

        /// <summary>Tokenize <paramref name="text"/>. Whitespace is skipped; every
        /// other character lands in exactly one token, in order. Never throws.</summary>
        public static List<DslToken> Tokenize(string? text, DslDialect dialect)
        {
            var tokens = new List<DslToken>();
            if (string.IsNullOrEmpty(text)) return tokens;
            bool bulb = dialect == DslDialect.Bulb3D;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char ch = text[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }

                // Comments: `// line` and `/* block */` (unterminated runs to the end).
                if (ch == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    int end = text.IndexOf('\n', i);
                    if (end < 0) end = n;
                    tokens.Add(new DslToken(DslTokenKind.Comment, i, end - i));
                    i = end;
                    continue;
                }
                if (ch == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int end = close < 0 ? n : close + 2;
                    tokens.Add(new DslToken(DslTokenKind.Comment, i, end - i));
                    i = end;
                    continue;
                }

                if (char.IsDigit(ch) || (ch == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    while (i < n && char.IsDigit(text[i])) i++;
                    if (i < n && text[i] == '.') { i++; while (i < n && char.IsDigit(text[i])) i++; }
                    // Exponent only when digits follow: `2e` is 2 then the constant e.
                    if (i < n && (text[i] == 'e' || text[i] == 'E'))
                    {
                        int j = i + 1;
                        if (j < n && (text[j] == '+' || text[j] == '-')) j++;
                        if (j < n && char.IsDigit(text[j]))
                        {
                            i = j;
                            while (i < n && char.IsDigit(text[i])) i++;
                        }
                    }
                    tokens.Add(new DslToken(DslTokenKind.Number, start, i - start));
                    continue;
                }

                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string word = text.Substring(start, i - start);
                    tokens.Add(new DslToken(Classify(word, text, tokens, start, i, bulb), start, i - start));
                    continue;
                }

                switch (ch)
                {
                    case '(': case ')': case '[': case ']': case '{': case '}':
                        tokens.Add(new DslToken(DslTokenKind.Bracket, i, 1));
                        i++;
                        continue;
                }

                if (i + 1 < n && Array.IndexOf(TwoCharOps, text.Substring(i, 2)) >= 0)
                {
                    tokens.Add(new DslToken(DslTokenKind.Operator, i, 2));
                    i += 2;
                    continue;
                }
                if ("+-*/^%<>=!?:,;.&|".IndexOf(ch) >= 0)
                {
                    tokens.Add(new DslToken(DslTokenKind.Operator, i, 1));
                    i++;
                    continue;
                }

                tokens.Add(new DslToken(DslTokenKind.Error, i, 1));
                i++;
            }
            return tokens;
        }

        private static DslTokenKind Classify(string word, string text, List<DslToken> prior, int start, int end, bool bulb)
        {
            bool call = NextNonSpace(text, end) == '(';

            // After '.': a member (z.x, v.Y) or, behind a C#-style prefix, a
            // static call / constant (Math.Sqrt(…), Math.PI, Vec3.Zero).
            if (bulb && prior.Count > 0 && IsDot(prior[^1], text))
            {
                if (prior.Count > 1 && prior[^2].Kind == DslTokenKind.Type
                    && BulbPrefixes.Contains(text.Substring(prior[^2].Start, prior[^2].Length)))
                    return call ? DslTokenKind.Function : DslTokenKind.Variable;
                return DslTokenKind.Member;
            }

            if ((bulb ? KeywordsBulb : Keywords2D).Contains(word)) return DslTokenKind.Keyword;
            if (bulb ? BulbVocabulary.TypeKeywords.Contains(word) || BulbPrefixes.Contains(word) : Types2D.Contains(word))
                return DslTokenKind.Type;
            if (call && (bulb ? FunctionsBulb : Functions2D).Contains(word)) return DslTokenKind.Function;
            if ((bulb ? BuiltinsBulb : Builtins2D).Contains(word)) return DslTokenKind.Variable;
            return DslTokenKind.Identifier;
        }

        private static bool IsDot(DslToken t, string text)
            => t.Kind == DslTokenKind.Operator && t.Length == 1 && text[t.Start] == '.';

        private static char NextNonSpace(string text, int from)
        {
            for (int i = from; i < text.Length; i++)
                if (!char.IsWhiteSpace(text[i])) return text[i];
            return '\0';
        }
    }
}
