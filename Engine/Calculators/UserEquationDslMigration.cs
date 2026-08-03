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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
        // One-time: an earlier Phase 5a build translated negative / non-integer
        // powers to `1/x^n` / `exp(y·log x)`, which are NaN at the z=0 seed and
        // rendered blank (the original Complex.Pow returned a finite value). Now
        // that the preprocessor emits pow() (Complex.Pow-exact), re-bake those
        // already-migrated sources from the original C# preserved in the backup.
        RepairPowTranslations(store);

        bool doFlip = !KindFixDone();
        int changed = store.MigrateUserEquationsToDsl(TryTranslate, flipDslEntriesToUserEquation: doFlip);
        if (doFlip) MarkKindFixDone();
        return changed;
    }

    private static string PowFixMarkerPath => AppDataPaths.Combine(".userequations-powfix");

    /// <summary>#27 Phase 5a — re-translate entries a prior build migrated with
    /// the NaN-at-zero power forms. Reads the earliest pre-migration backup
    /// (original C#), re-translates each entry with the fixed preprocessor, and
    /// updates the stored source. One-time (marker-gated), backup-guarded, and
    /// edit-preserving: an entry the user has since re-authored as C# is left
    /// alone, and only entries whose fresh translation actually differs are
    /// touched.</summary>
    private static void RepairPowTranslations(UserEquationStore store)
    {
        try
        {
            if (File.Exists(PowFixMarkerPath)) return;

            var originals = LoadEarliestBackupSources();
            if (originals.Count > 0)
            {
                var rewrites = new List<(UserEquationEntry Entry, string Dsl)>();
                foreach (var e in store.Equations)
                {
                    if (string.IsNullOrWhiteSpace(e.Name)) continue;
                    if (!originals.TryGetValue(e.Name, out string? origCs)) continue;
                    if (string.IsNullOrWhiteSpace(origCs)) continue;
                    if (!LooksLikeCSharp(origCs)) continue;         // original wasn't C# → nothing to re-bake
                    if (LooksLikeCSharp(e.Source)) continue;        // user re-edited to C# → leave it

                    string? fresh = TryTranslate(origCs);
                    if (string.IsNullOrWhiteSpace(fresh)) continue; // no DSL form
                    if (SourcesEqual(fresh!, e.Source)) continue;   // already correct
                    rewrites.Add((e, fresh!));
                }

                if (rewrites.Count > 0)
                {
                    UserDataBackup.SnapshotBeforeMigration(
                        AppDataPaths.Combine("userequations.json"), "powfix");
                    foreach (var (entry, dsl) in rewrites)
                    {
                        entry.Source = dsl;
                        entry.Kind = UserEquationKind.UserEquation;
                    }
                    store.Save();
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PowFixMarkerPath)!);
            File.WriteAllText(PowFixMarkerPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort — a missed repair only means the user re-enters the map */ }
    }

    /// <summary>Name → source from the earliest <c>userequations.json.*.dslmigration.bak</c>
    /// (the snapshot taken before the very first migration, so it holds the
    /// original C#). Empty when no backup exists.</summary>
    private static Dictionary<string, string> LoadEarliestBackupSources()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = Path.GetDirectoryName(AppDataPaths.Combine("userequations.json"))!;
            if (!Directory.Exists(dir)) return map;
            // The timestamp is embedded as yyyyMMdd-HHmmss, so lexical order is
            // chronological; the first is the pre-migration original.
            string? earliest = Directory
                .GetFiles(dir, "userequations.json.*.dslmigration.bak")
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (earliest == null) return map;

            var entries = JsonSerializer.Deserialize<List<UserEquationEntry>>(File.ReadAllText(earliest));
            if (entries == null) return map;
            foreach (var e in entries)
                if (e != null && !string.IsNullOrWhiteSpace(e.Name))
                    map[e.Name] = e.Source ?? string.Empty;
        }
        catch { /* best-effort */ }
        return map;
    }

    private static bool LooksLikeCSharp(string? s)
        => !string.IsNullOrEmpty(s)
           && (s.Contains("Complex.") || s.Contains("Math.") || s.Contains("new Complex")
               || s.Contains(';') || System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(return|var|int|double)\b"));

    private static bool SourcesEqual(string? a, string? b)
        => string.Equals(
            (a ?? string.Empty).Replace("\r\n", "\n").Trim(),
            (b ?? string.Empty).Replace("\r\n", "\n").Trim(),
            StringComparison.Ordinal);

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
