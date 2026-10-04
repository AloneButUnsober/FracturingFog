// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BulbLanguageMigration.cs
//
// #1100 — upgrade saved User Bulb 3D text from language version 1 to the
// current rules without changing what it renders. The 3D analogue of
// EquationMigration (#1088).
//
// Version 1 (everything saved before #1100) read unary minus tighter than ^:
// -x^y meant (-x)^y. Version 2 reads -(x^y), like the 2D equation language.
//
// The upgrade parses the text with the version-1 rules
// (SandboxBulbExpression.ParseLegacy), which records the edits that keep the
// meaning — wrap a signed base before ^ in parentheses, "(-x)^y" — and applies
// them. Identifiers are lenient in both parses (chain-step outputs and params
// need no scope). Safety check: the version-1 tree of the original must equal
// the version-2 tree of the result, or the text is left unchanged. Text that
// doesn't parse (C#-style bodies awaiting translation, typos) is left as is.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace FracturingFog.Models
{
    public static class BulbLanguageMigration
    {
        /// <summary>The bulb-language version this build reads and writes.
        /// Saved bulbs (and region inline sources) without a version are 1.</summary>
        public const int CurrentLanguageVersion = 2;

        public readonly record struct Result(string Source, bool Changed, string? Note);

        public static Result UpgradeFromVersion1(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return new Result(source ?? string.Empty, false, null);

            var edits = new List<(int Pos, int Len, string Text)>();
            SandboxBulbExpression legacy;
            try { legacy = SandboxBulbExpression.ParseLegacy(source, edits); }
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
                var now = SandboxBulbExpression.ParseLenient(upgraded);
                if (SandboxBulbExpression.ToSExpression(now.Root) != SandboxBulbExpression.ToSExpression(legacy.Root))
                    return new Result(source, false, "self-check failed, left as is (please report): " + upgraded);
            }
            catch (FormatException ex)
            {
                return new Result(source, false, "self-check failed, left as is (please report): " + ex.Message);
            }
            return new Result(upgraded, true, null);
        }

        /// <summary>The store hook: upgraded text, or null when unchanged.</summary>
        public static string? UpgradeOrNull(string? source)
        {
            var r = UpgradeFromVersion1(source);
            return r.Changed ? r.Source : null;
        }

        /// <summary>Give the UI-free store (Abstractions) the upgrader. Runs when
        /// Engine loads; hosts also call it before the first store Load.</summary>
        [ModuleInitializer]
        internal static void RegisterOnLoad() => Register();

        public static void Register() => UserBulbStore.LanguageUpgrader ??= UpgradeOrNull;
    }
}
