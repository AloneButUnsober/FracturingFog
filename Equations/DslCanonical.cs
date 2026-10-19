// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslCanonical.cs
//
// #1367 (S6 of #1360) — a parse-tree fingerprint per dialect, the guard behind
// the editor's Format / Minimise-parens actions: an edit is only applied when
// the source still parses to the identical tree. 2D parses here (Sandbox
// interpreter language); the Bulb parser lives in Engine, which UI code cannot
// reference, so Engine registers its fingerprint at load
// (BulbDslCanonical.Register).

using System;

namespace FracturingFog.Models
{
    public static class DslCanonical
    {
        /// <summary>Bulb fingerprint (source → tree string, or null when it does
        /// not parse). Set by Engine; null until Engine is loaded.</summary>
        public static Func<string, string?>? BulbCanonicalizer { get; set; }

        /// <summary>The parse-tree fingerprint of <paramref name="source"/>, or
        /// null when it does not parse (or the dialect's parser is unavailable).
        /// Equal fingerprints mean the same tree.</summary>
        public static string? Of(string? source, DslDialect dialect)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            if (dialect == DslDialect.Bulb3D) return BulbCanonicalizer?.Invoke(source);
            try { return EquationLanguage.ToSExpression(EquationLanguage.Parse(source)); }
            catch (Exception) { return null; }
        }
    }
}
