// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EquationMigration.cs
//
// #1088 (#937 Phase 3) — upgrade saved equation text from language version 1
// to the current rules without changing what it renders.
//
// Version 1 (everything saved before #1088) differs from version 2 in two
// places:
//   • unary minus bound tighter than ^:  -x^y  meant (-x)^y;
//   • inside an `if … then` condition, a comparison operand that is directly
//     abs(x) meant |x|² (CalcGen's old condition shorthand).
// Version 2: -x^y = -(x^y), and abs(x) = |x| everywhere (|x|² is norm(x)).
//
// The upgrade parses the text with the version-1 rules (SandboxExpression.
// ParseLegacy), which records the edits that make it mean the same under
// version 2 — wrap a signed base in parentheses, "(-x)^y"; rename that abs to
// norm — and applies them. Safety check: the version-1 tree of the original
// must equal the version-2 tree of the result (EquationLanguage.ToSExpression);
// otherwise the text is left unchanged and the reason reported. Text that does
// not parse (C#-style sources awaiting translation, typos) is left as is.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FracturingFog.Models
{
    public static class EquationMigration
    {
        /// <summary>The equation-language version this build reads and writes.
        /// Saved entries without a version are version 1.</summary>
        public const int CurrentLanguageVersion = 2;

        public readonly record struct Result(string Source, bool Changed, string? Note);

        /// <summary>Upgrade one equation text (equation, z0 seed or bailout
        /// condition) from version 1 to the current rules.</summary>
        public static Result UpgradeFromVersion1(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return new Result(source ?? string.Empty, false, null);

            var edits = new List<(int Pos, int Len, string Text)>();
            SandboxExpression legacy;
            try { legacy = SandboxExpression.ParseLegacy(source, edits); }
            catch (FormatException ex) { return new Result(source, false, "not parsed, left as is: " + ex.Message); }
            if (edits.Count == 0) return new Result(source, false, null);

            var sb = new StringBuilder(source);
            // Right to left so earlier positions stay valid; for equal positions
            // undo the recording order (an inner wrap's ")" was recorded later).
            foreach (var (pos, len, text) in edits.Select((e, i) => (e, i))
                         .OrderByDescending(t => t.e.Pos).ThenByDescending(t => t.i).Select(t => t.e))
            {
                sb.Remove(pos, len);
                sb.Insert(pos, text);
            }
            string upgraded = sb.ToString();

            try
            {
                var now = SandboxExpression.Parse(upgraded);
                if (EquationLanguage.ToSExpression(now) != EquationLanguage.ToSExpression(legacy))
                    return new Result(source, false, "self-check failed, left as is (please report): " + upgraded);
            }
            catch (FormatException ex)
            {
                return new Result(source, false, "self-check failed, left as is (please report): " + ex.Message);
            }
            return new Result(upgraded, true, null);
        }
    }
}
