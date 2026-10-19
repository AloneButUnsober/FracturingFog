// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslCompletion.cs
//
// #1366 (S5 of #1360) — what the DSL editor offers at the caret: the word being
// typed, the candidate list (built-in functions with signatures, keywords,
// types, built-in inputs, names already used in the source, plus host names
// such as User Bulb params and chain-step outputs), and the call the caret is
// inside for signature help. Pure and lenient like DslTokenizer: any text,
// half-typed or broken, gives a sensible answer and never throws.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Models
{
    public enum DslCompletionKind
    {
        Function,
        Keyword,
        Type,
        Variable,
        /// <summary>A name from the source or the host (locals, params, chain outputs).</summary>
        Name,
        /// <summary>A vector / quaternion component after '.'.</summary>
        Member,
    }

    public sealed record DslCompletionItem(string Text, DslCompletionKind Kind, string Description);

    /// <summary>The word at the caret: [Start, caret) is the typed prefix.</summary>
    public readonly record struct DslCompletionContext(int Start, string Prefix, bool IsMember);

    public static class DslCompletion
    {
        private static readonly string[] Keywords2D = { "return", "if", "then", "else", "let", "in" };
        private static readonly string[] KeywordsBulb = { "return", "if", "then", "else", "let", "in" };
        private static readonly string[] Types2D = { "var", "Complex", "double", "int", "float", "long", "decimal" };

        private static readonly Dictionary<string, string> Vars2D = new(StringComparer.Ordinal)
        {
            ["z"] = "Current orbit value (complex).",
            ["c"] = "Pixel constant (Mandelbrot) or Julia constant.",
            ["n"] = "Iteration index.",
            ["iter"] = "Iteration index (alias of n, CalcGen spelling).",
            ["prev"] = "Previous z (0 on the first step).",
            ["pi"] = "3.14159…",
            ["e"] = "2.71828…",
            ["i"] = "Imaginary unit.",
        };

        private static readonly Dictionary<string, string> VarsBulb = new(StringComparer.Ordinal)
        {
            ["z"] = "Current point (Vec3, or Quat in quaternion mode).",
            ["c"] = "Pixel constant (or the Julia constant).",
            ["n"] = "Iteration index.",
            ["t"] = "Animation time.",
            ["dr"] = "Running derivative bound (DE body only).",
            ["de"] = "Distance estimate so far (DE body only).",
            ["pi"] = "3.14159…",
            ["e"] = "2.71828…",
        };

        private static readonly string[] Members = { "x", "y", "z", "w" };

        /// <summary>The word ending at <paramref name="caret"/>, or null where
        /// completion makes no sense (inside a comment or a number, or a
        /// member position in a dialect without members).</summary>
        public static DslCompletionContext? ContextAt(string? text, int caret, DslDialect dialect)
        {
            text ??= string.Empty;
            if (caret < 0 || caret > text.Length) return null;
            foreach (var t in DslTokenizer.Tokenize(text, dialect))
            {
                if (t.Start >= caret) break;
                if (t.Kind != DslTokenKind.Comment) continue;
                // Right after a closed "/* … */" is outside; the end of a line
                // comment or of an unterminated block comment is still inside.
                bool closedBlock = text[t.Start + 1] == '*' && t.Length >= 4
                                   && text[t.End - 2] == '*' && text[t.End - 1] == '/';
                if (caret < t.End || (caret == t.End && !closedBlock)) return null;
            }
            int start = caret;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            if (start < caret && char.IsDigit(text[start])) return null;
            bool member = start > 0 && text[start - 1] == '.';
            if (member && dialect != DslDialect.Bulb3D) return null;
            // A number then '.', e.g. "2." — not a member access.
            if (member && start >= 2 && char.IsDigit(text[start - 2])) return null;
            return new DslCompletionContext(start, text.Substring(start, caret - start), member);
        }

        /// <summary>Candidates for the word at <paramref name="caret"/> whose text
        /// starts with the typed prefix (case-insensitive), exact match included.</summary>
        public static IReadOnlyList<DslCompletionItem> Items(string? text, int caret, DslDialect dialect,
            IEnumerable<string>? hostNames = null)
        {
            text ??= string.Empty;
            if (ContextAt(text, caret, dialect) is not { } ctx) return Array.Empty<DslCompletionItem>();
            var all = new List<DslCompletionItem>();

            if (ctx.IsMember)
            {
                foreach (var m in Members)
                    all.Add(new DslCompletionItem(m, DslCompletionKind.Member, m == "w" ? "Quaternion w component." : $"{m} component."));
            }
            else
            {
                bool bulb = dialect == DslDialect.Bulb3D;
                foreach (var s in DslSignatures.For(dialect).Values.OrderBy(s => s.Name, StringComparer.Ordinal))
                    all.Add(new DslCompletionItem(s.Name, DslCompletionKind.Function, $"{s.Display} — {s.Doc}"));
                foreach (var k in bulb ? KeywordsBulb : Keywords2D)
                    all.Add(new DslCompletionItem(k, DslCompletionKind.Keyword, "Keyword."));
                foreach (var ty in bulb ? BulbVocabulary.TypeKeywords.OrderBy(x => x, StringComparer.Ordinal) : (IEnumerable<string>)Types2D)
                    all.Add(new DslCompletionItem(ty, DslCompletionKind.Type, "Declaration type."));
                foreach (var (v, doc) in bulb ? VarsBulb : Vars2D)
                    all.Add(new DslCompletionItem(v, DslCompletionKind.Variable, doc));

                // Names already in the source (locals) and from the host (params,
                // chain outputs); the word being typed is not offered to itself.
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var t in DslTokenizer.Tokenize(text, dialect))
                    if (t.Kind == DslTokenKind.Identifier && !(t.Start == ctx.Start))
                        names.Add(text.Substring(t.Start, t.Length));
                if (hostNames != null)
                    foreach (var h in hostNames)
                        if (!string.IsNullOrWhiteSpace(h)) names.Add(h.Trim());
                var taken = new HashSet<string>(all.Select(a => a.Text), StringComparer.Ordinal);
                foreach (var nm in names)
                    if (taken.Add(nm)) all.Add(new DslCompletionItem(nm, DslCompletionKind.Name, "Name in scope."));
            }

            // An exact match stays in the list (typing "z" or "c" still offers the
            // input with its description), and the editor selects it.
            return all.Where(a => a.Text.StartsWith(ctx.Prefix, StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }

        /// <summary>The built-in call the caret is inside — innermost unclosed
        /// `name(` before the caret — and which argument (0-based) it is on, or
        /// null outside a built-in call.</summary>
        public static (DslSignature Signature, int ArgIndex)? SignatureAt(string? text, int caret, DslDialect dialect)
        {
            text ??= string.Empty;
            caret = Math.Clamp(caret, 0, text.Length);
            var tokens = DslTokenizer.Tokenize(text.Substring(0, caret), dialect);
            // Per open bracket: the token index of the bracket and commas seen at its level.
            var stack = new List<(int Tok, int Commas)>();
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                char ch = text[t.Start];
                if (t.Kind == DslTokenKind.Bracket)
                {
                    if (ch is '(' or '[' or '{') stack.Add((i, 0));
                    else if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                }
                else if (t.Kind == DslTokenKind.Operator && ch == ',' && stack.Count > 0)
                {
                    stack[^1] = (stack[^1].Tok, stack[^1].Commas + 1);
                }
            }
            for (int s = stack.Count - 1; s >= 0; s--)
            {
                int open = stack[s].Tok;
                if (text[tokens[open].Start] != '(' || open == 0) continue;
                var fn = tokens[open - 1];
                if (fn.Kind != DslTokenKind.Function) return null; // inside a plain group or unknown call
                var sig = DslSignatures.Find(dialect, text.Substring(fn.Start, fn.Length));
                return sig == null ? null : (sig, stack[s].Commas);
            }
            return null;
        }

        /// <summary>One-line doc for a built-in name (function or input), for hover.</summary>
        public static string? DocFor(string name, DslDialect dialect)
        {
            if (DslSignatures.Find(dialect, name) is { } s) return $"{s.Display} — {s.Doc}";
            var vars = dialect == DslDialect.Bulb3D ? VarsBulb : Vars2D;
            return vars.TryGetValue(name, out var d) ? $"{name} — {d}" : null;
        }
    }
}
