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
using System.IO;

using FracturingFog.Abstractions;
using FracturingFog.CalculatorGen;
using FracturingFog.Models;

namespace FracturingFog;

public static class UserEquationDslMigration
{
    // One-time marker: a prior Phase 5a build flipped migrated equations to
    // Kind=Dsl, which routed them to an editor tab that does not render live
    // (only the CalcGen "Compile & Load" codegen path). The first run of a
    // build carrying the fix flips every Dsl entry back to UserEquation so the
    // safe interpreter renders them by default; the marker keeps that a
    // one-time repair so a user's later hand-authored DSL entries are left alone.
    private static string MarkerPath => AppDataPaths.Combine(".userequations-kindfix");

    private static bool KindFixDone()
    {
        try { return File.Exists(MarkerPath); } catch { return false; }
    }

    private static void MarkKindFixDone()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort — a missed marker only re-runs the idempotent flip */ }
    }

    /// <summary>Normalise the store's saved equations onto the live DSL path.
    /// Rewrites translatable C# sources to DSL (Kind kept = UserEquation) and,
    /// once, flips any Kind=Dsl entries back to UserEquation so they render by
    /// default. Idempotent and backup-guarded (see
    /// <see cref="UserEquationStore.MigrateUserEquationsToDsl"/>). Returns the
    /// number of entries changed.</summary>
    public static int Run(UserEquationStore store)
    {
        if (store == null) return 0;
        bool doFlip = !KindFixDone();
        int changed = store.MigrateUserEquationsToDsl(TryTranslate, flipDslEntriesToUserEquation: doFlip);
        if (doFlip) MarkKindFixDone();
        return changed;
    }

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
