// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BulbDslCanonical.cs
//
// #1367 (S6 of #1360) — gives the editor's Format / Minimise-parens guard
// (DslCanonical, in Equations) the Bulb parse-tree fingerprint. UI code cannot
// reference Engine, so Engine registers itself when it loads.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public static class BulbDslCanonical
    {
        /// <summary>Install the Bulb fingerprint into <see cref="DslCanonical"/>.
        /// Idempotent; runs automatically when Engine loads.</summary>
        public static void Register() => DslCanonical.BulbCanonicalizer = Of;

        /// <summary>Fingerprint of a Bulb source (lenient names), or null when it
        /// does not parse.</summary>
        public static string? Of(string source)
        {
            try { return SandboxBulbExpression.ToSExpression(SandboxBulbExpression.ParseLenient(source).Root); }
            catch (Exception) { return null; }
        }

#pragma warning disable CA2255 // module initializer in a library: intentional self-registration
        [ModuleInitializer]
        internal static void AutoRegister() => Register();
#pragma warning restore CA2255
    }
}
