// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslFormatter.cs
//
// #1367 (S6 of #1360) — the DSL editor's Format and Minimise-parens actions.
//
// Format is whitespace-only and token-level: it never re-prints from a parse
// tree (that would drop comments, user names and statement layout — the
// reason AstPrinter was ruled out in #1360). Rules, in the project's own style
// (`z*z + c`):
//   * / ^ %         tight
//   + - and other   one space each side (binary); unary minus / plus / ! tight
//   binary ops
//   , ;             no space before; one space after ','; a line break after ';'
//                   when more code follows on the same line
//   ( [ { ) ] } .   no space inside brackets, none before a call's '(' or
//                   around a member '.'
//   line breaks     the user's own are kept (blank runs collapse to one);
//                   lines are re-indented: two spaces per open bracket, or two
//                   for a continuation (previous line ends in an operator,
//                   comma or keyword such as `in` / `then`, or the line starts
//                   with a binary operator)
//   comments        kept verbatim, one space from neighbouring code
//
// Guards (both actions): the result must lex to the identical token sequence
// (Format) and, whenever the source parses, to the identical parse tree
// (DslCanonical). Otherwise the action is refused and the text is untouched.
//
// Minimise parens removes grouping brackets one pair at a time, keeping a
// removal only when the parse tree is unchanged — correct by construction, no
// precedence table to drift from the parser.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FracturingFog.Models
{
    public sealed record DslFormatResult(bool Ok, string Text, string Message);

    public static class DslFormatter
    {
        private const string Indent = "  ";
        private static readonly HashSet<string> TightOps = new(StringComparer.Ordinal) { "*", "/", "^", "%" };
        private static readonly HashSet<string> ContinuationKeywords =
            new(StringComparer.Ordinal) { "in", "then", "else", "return", "let", "if", "new" };

        /// <summary>Whitespace-only format. Refused (text unchanged) when the
        /// source has characters the language does not accept, or when the
        /// guards would not hold.</summary>
        public static DslFormatResult Format(string? source, DslDialect dialect)
        {
            source ??= string.Empty;
            var tokens = DslTokenizer.Tokenize(source, dialect);
            if (tokens.Count == 0) return new DslFormatResult(true, source, "Nothing to format.");
            if (tokens.Any(t => t.Kind == DslTokenKind.Error))
                return new DslFormatResult(false, source, "Format skipped: fix the highlighted characters first.");

            string formatted = Render(source, tokens);

            // Guard 1: identical token stream (kind + text).
            if (!SameTokens(source, tokens, formatted, dialect))
                return new DslFormatResult(false, source, "Format skipped: it would change the equation's tokens.");
            // Guard 2: identical parse tree, whenever the source parses.
            string? before = DslCanonical.Of(source, dialect);
            if (before != null && DslCanonical.Of(formatted, dialect) != before)
                return new DslFormatResult(false, source, "Format skipped: it would change what the equation means.");

            return formatted == source
                ? new DslFormatResult(true, source, "Already formatted.")
                : new DslFormatResult(true, formatted, "Formatted.");
        }

        /// <summary>Remove grouping brackets that do not change the parse tree.
        /// Needs a source that parses (that tree is the reference).</summary>
        public static DslFormatResult MinimiseParens(string? source, DslDialect dialect)
        {
            source ??= string.Empty;
            string? reference = DslCanonical.Of(source, dialect);
            if (reference == null)
                return new DslFormatResult(false, source, "Minimise brackets needs an equation that parses.");

            string text = source;
            int removed = 0;
            bool progress = true;
            while (progress)
            {
                progress = false;
                var tokens = DslTokenizer.Tokenize(text, dialect);
                var map = DslBrackets.Match(tokens, text);
                // Outermost first: wide redundant groups go before the ones inside them.
                var candidates = Enumerable.Range(0, tokens.Count)
                    .Where(i => IsGroupingOpen(text, tokens, map, i))
                    .OrderBy(i => map.Depth[i]);
                foreach (int open in candidates)
                {
                    int close = map.Partner[open];
                    string trial = text.Remove(tokens[close].Start, 1).Remove(tokens[open].Start, 1);
                    if (DslCanonical.Of(trial, dialect) == reference)
                    {
                        text = trial;
                        removed++;
                        progress = true;
                        break; // offsets moved: re-lex
                    }
                }
            }
            return removed == 0
                ? new DslFormatResult(true, source, "No redundant brackets.")
                : new DslFormatResult(true, text, removed == 1 ? "Removed 1 pair of brackets." : $"Removed {removed} pairs of brackets.");
        }

        // A matched '(' that groups an expression: not a call's bracket (a name
        // right before it), and not empty.
        private static bool IsGroupingOpen(string text, IReadOnlyList<DslToken> tokens, DslBracketMap map, int i)
        {
            if (tokens[i].Kind != DslTokenKind.Bracket || text[tokens[i].Start] != '(' || map.Partner[i] < 0) return false;
            if (map.Partner[i] == i + 1) return false;
            int prev = PrevCode(tokens, i);
            return prev < 0 || tokens[prev].Kind is not (DslTokenKind.Function or DslTokenKind.Identifier
                or DslTokenKind.Type or DslTokenKind.Member or DslTokenKind.Variable);
        }

        private static string Render(string src, List<DslToken> tokens)
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                string text = src.Substring(t.Start, t.Length);
                int gapStart = i == 0 ? 0 : tokens[i - 1].End;
                int newlines = 0;
                for (int k = gapStart; k < t.Start; k++) if (src[k] == '\n') newlines++;

                bool closing = t.Kind == DslTokenKind.Bracket && text is ")" or "]" or "}";
                if (i > 0)
                {
                    bool afterSemicolon = Is(src, tokens[i - 1], ";") && t.Kind != DslTokenKind.Comment;
                    if (newlines > 0 || afterSemicolon)
                    {
                        sb.Append('\n');
                        if (newlines > 1) sb.Append('\n');
                        sb.Append(IndentFor(src, tokens, i, closing ? depth - 1 : depth));
                    }
                    else if (NeedsSpace(src, tokens, i))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(text);

                if (t.Kind == DslTokenKind.Bracket)
                {
                    if (text is "(" or "[" or "{") depth++;
                    else depth = Math.Max(0, depth - 1);
                }
            }
            if (src.EndsWith('\n')) sb.Append('\n');
            return sb.ToString();
        }

        private static string IndentFor(string src, List<DslToken> tokens, int i, int depth)
        {
            int level = Math.Max(0, depth);
            if (level == 0)
            {
                int prev = PrevCode(tokens, i);
                // Continuation: the previous line ends mid-expression, or this
                // line starts with a binary operator ("+ c" under "z*z").
                bool leadingOp = tokens[i].Kind == DslTokenKind.Operator && !IsUnary(src, tokens, i)
                                 && !Is(src, tokens[i], ";");
                if ((prev >= 0 && IsContinuation(src, tokens[prev])) || leadingOp) level = 1;
            }
            return string.Concat(Enumerable.Repeat(Indent, level));
        }

        private static bool IsContinuation(string src, DslToken t) => t.Kind switch
        {
            DslTokenKind.Operator => !Is(src, t, ";"),
            DslTokenKind.Bracket => src[t.Start] is '(' or '[' or '{',
            DslTokenKind.Keyword => ContinuationKeywords.Contains(src.Substring(t.Start, t.Length)),
            _ => false,
        };

        private static bool NeedsSpace(string src, List<DslToken> tokens, int i)
        {
            var cur = tokens[i];
            var prev = tokens[i - 1];
            string c = src.Substring(cur.Start, cur.Length);
            string p = src.Substring(prev.Start, prev.Length);

            if (prev.Kind == DslTokenKind.Comment || cur.Kind == DslTokenKind.Comment) return true;
            if (c is "," or ";") return false;
            if (cur.Kind == DslTokenKind.Bracket && c is ")" or "]" or "}") return false;
            if (prev.Kind == DslTokenKind.Bracket && p is "(" or "[" or "{") return false;
            if (c == "." || p == ".") return false;
            if (p == ",") return true;
            if (cur.Kind == DslTokenKind.Bracket && c is "(" or "[")
            {
                if (prev.Kind is DslTokenKind.Function or DslTokenKind.Identifier or DslTokenKind.Type
                    or DslTokenKind.Member or DslTokenKind.Variable) return false;
                if (prev.Kind == DslTokenKind.Bracket) return false;
            }
            if (IsUnary(src, tokens, i - 1)) return false;
            if (IsUnary(src, tokens, i)) return true;
            if ((cur.Kind == DslTokenKind.Operator && TightOps.Contains(c))
                || (prev.Kind == DslTokenKind.Operator && TightOps.Contains(p))) return false;
            return true;
        }

        // '-', '+' or '!' with nothing to its left that could be an operand.
        private static bool IsUnary(string src, List<DslToken> tokens, int i)
        {
            var t = tokens[i];
            if (t.Kind != DslTokenKind.Operator || t.Length != 1 || src[t.Start] is not ('-' or '+' or '!')) return false;
            int prev = PrevCode(tokens, i);
            if (prev < 0) return true;
            var p = tokens[prev];
            return p.Kind switch
            {
                DslTokenKind.Operator => true,
                DslTokenKind.Keyword => true,
                DslTokenKind.Bracket => src[p.Start] is '(' or '[' or '{',
                _ => false,
            };
        }

        private static int PrevCode(IReadOnlyList<DslToken> tokens, int i)
        {
            for (int k = i - 1; k >= 0; k--)
                if (tokens[k].Kind != DslTokenKind.Comment) return k;
            return -1;
        }

        private static bool Is(string src, DslToken t, string text)
            => t.Length == text.Length && string.CompareOrdinal(src, t.Start, text, 0, text.Length) == 0;

        private static bool SameTokens(string src, List<DslToken> a, string formatted, DslDialect dialect)
        {
            var b = DslTokenizer.Tokenize(formatted, dialect);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Kind != b[i].Kind || a[i].Length != b[i].Length) return false;
                if (string.CompareOrdinal(src, a[i].Start, formatted, b[i].Start, a[i].Length) != 0) return false;
            }
            return true;
        }
    }
}
