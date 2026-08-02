// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserEquationDslMigration.cs
//
// #27 Phase 5a — one-time, on-startup conversion of saved user equations from
// the historical C# `Complex.*` form to the safe Sandbox DSL. After Phase 3 the
// live equation path runs on SandboxExpression only (no raw-C# Roslyn), so a
// saved equation that the DSL can represent should be persisted as DSL — it then
// renders identically (parity is proven by UserEquationDslParityTests) and no
// longer depends on the C#-syntax front door.
//
// This lives in Engine because the translation needs EquationPreprocessor
// (CalculatorGen.Lib) and SandboxExpression (Engine) — neither of which the
// UI-free Abstractions store layer references. The store owns the file +
// backup + save; this injects the translator. Entries with no DSL form (member
// access the preprocessor can't take, statement blocks, unsupported members)
// are left untouched and stay editable, surfacing the DSL error on the live
// path.

using System;

using FracturingFog.CalculatorGen;
using FracturingFog.Models;

namespace FracturingFog;

public static class UserEquationDslMigration
{
    /// <summary>Convert the store's translatable saved equations to DSL.
    /// Idempotent and backup-guarded (see
    /// <see cref="UserEquationStore.MigrateUserEquationsToDsl"/>). Returns the
    /// number of entries converted.</summary>
    public static int Run(UserEquationStore store)
        => store?.MigrateUserEquationsToDsl(TryTranslate) ?? 0;

    /// <summary>Same translate-then-validate the live calculator performs on
    /// compile: preprocess the C# source to DSL text, and confirm it parses on
    /// the safe interpreter. Returns the DSL text on success, or null when the
    /// source has no DSL form (so the caller leaves it as-is).</summary>
    private static string? TryTranslate(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        string dsl = EquationPreprocessor.Preprocess(source, out PreprocessDiagnostic? diag);
        if (diag != null) return null; // untranslatable construct

        try
        {
            SandboxExpression.Parse(dsl);
            return dsl;
        }
        catch
        {
            return null; // translated text the DSL grammar still rejects
        }
    }
}
