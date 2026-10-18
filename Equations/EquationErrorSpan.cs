// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EquationErrorSpan.cs
//
// #1365 (S4 of #1360) — map a parser error message back to a character span
// in the source. The parsers (EquationFrontEnd and its builders) format their
// position with EquationSyntax.At as "... at line L, col C." — this is the
// inverse, shared by every editor that only receives the message string (the
// Sandbox host path, the User Equation validator). Hoisted from
// UserEquationViewModel (#1089), which did the same privately.

using System;
using System.Text.RegularExpressions;

namespace FracturingFog.Models
{
    public static class EquationErrorSpan
    {
        private static readonly Regex ColRx = new(@"\bcol\s+(\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex LineRx = new(@"\bline\s+(\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex UnknownNameRx =
            new(@"Unknown (?:identifier|function) '([^']+)'", RegexOptions.CultureInvariant);
        private static readonly Regex DidYouMeanRx = new(@"Did you mean '([^']+)'", RegexOptions.CultureInvariant);

        /// <summary>Locate the span <paramref name="message"/> points at in
        /// <paramref name="source"/>: the character at its line/col, widened to
        /// the whole name for "Unknown identifier/function 'x'". False when the
        /// message carries no position.</summary>
        public static bool TryLocate(string source, string? message, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (string.IsNullOrEmpty(message)) return false;
            var m = ColRx.Match(message);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int col)) return false;
            int line = 1;
            var lm = LineRx.Match(message);
            if (lm.Success) int.TryParse(lm.Groups[1].Value, out line);
            source ??= string.Empty;
            start = ColToOffset(source, line, col);
            length = 1;
            var id = UnknownNameRx.Match(message);
            if (id.Success) length = id.Groups[1].Value.Length;
            length = Math.Max(0, Math.Min(length, source.Length - start));
            return true;
        }

        /// <summary>The Did-you-mean suggestion in <paramref name="message"/>, or null.</summary>
        public static string? DidYouMean(string? message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var m = DidYouMeanRx.Match(message);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Inverse of <see cref="EquationSyntax.At"/>: walk to the line,
        /// then add (col-1). Clamps to the source length so a stale span past the
        /// end of a freshly trimmed buffer doesn't throw.</summary>
        public static int ColToOffset(string source, int line, int col)
        {
            if (line <= 1) return Math.Min(Math.Max(0, col - 1), source.Length);
            int offset = 0;
            int seen = 1;
            while (seen < line && offset < source.Length)
            {
                int nl = source.IndexOf('\n', offset);
                if (nl < 0) break;
                offset = nl + 1;
                seen++;
            }
            return Math.Min(offset + Math.Max(0, col - 1), source.Length);
        }
    }
}
