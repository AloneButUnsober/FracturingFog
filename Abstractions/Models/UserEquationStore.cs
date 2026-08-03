// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserEquationStore.cs
//
// Singleton persistence for user-defined fractal equations.  Each entry has a
// human-readable Name and the raw C# source body that UserEquationCalculator
// compiles.  Stored as JSON in %APPDATA%\FracturingFog\userequations.json.
//
// Mirrors UserColorThemeLibrary in spirit: lazy singleton, indented JSON,
// failures during load/save are non-fatal.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>Which editor tab produced this entry's <see cref="UserEquationEntry.Source"/>.
    /// UserEquation = C#-style body fed through Roslyn / CalcGen preprocessor.
    /// Dsl = bare CalcGen DSL fed straight to CalculatorGen. Legacy entries
    /// (no Kind field) deserialise to UserEquation.</summary>
    public enum UserEquationKind
    {
        UserEquation = 0,
        Dsl = 1,
    }

    public sealed class UserEquationEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// When true, this entry is surfaced as a first-class fractal type in
        /// the main fractal dropdown via <see cref="RegisteredFractalCatalog"/>.
        /// Defaults false; missing field in legacy JSON deserialises to false.
        /// </summary>
        public bool Promoted { get; set; }

        /// <summary>Which editor tab this source was authored in. Drives
        /// which tab a Load restores into. Defaults to UserEquation so
        /// pre-existing JSON entries remain valid.</summary>
        public UserEquationKind Kind { get; set; } = UserEquationKind.UserEquation;
    }

    public sealed class UserEquationStore
    {
        private static UserEquationStore? _instance;
        public static UserEquationStore Instance => _instance ??= new UserEquationStore();

        private UserEquationStore() { }

        public List<UserEquationEntry> Equations { get; } = new();

        private static string SettingsDir => AppDataPaths.Root;

        private static string EquationsFile =>
            Path.Combine(SettingsDir, "userequations.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        public void Load()
        {
            try
            {
                Equations.Clear();
                if (!File.Exists(EquationsFile)) return;

                string json = File.ReadAllText(EquationsFile);
                var loaded = JsonSerializer.Deserialize<List<UserEquationEntry>>(json, BuildJsonOptions());
                if (loaded == null) return;

                foreach (var e in loaded)
                    if (e != null && !string.IsNullOrWhiteSpace(e.Name)) Equations.Add(e);
            }
            catch
            {
                Equations.Clear();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Equations, BuildJsonOptions());
                AtomicFile.WriteAllText(EquationsFile, json);
            }
            catch
            {
                // Non-fatal.
            }
        }

        /// <summary>
        /// Inserts or replaces an entry by Name (case-insensitive). Returns the
        /// stored entry, or null if name is blank.
        /// </summary>
        public UserEquationEntry? SaveEquation(string name, string source, UserEquationKind kind = UserEquationKind.UserEquation)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            for (int i = 0; i < Equations.Count; i++)
            {
                if (Equations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Equations[i].Source = source ?? string.Empty;
                    Equations[i].Kind = kind;
                    Save();
                    return Equations[i];
                }
            }

            var entry = new UserEquationEntry { Name = name, Source = source ?? string.Empty, Kind = kind };
            Equations.Add(entry);
            Save();
            return entry;
        }

        /// <summary>
        /// Sets the <see cref="UserEquationEntry.Promoted"/> flag on the named
        /// entry and persists. Returns true when the entry exists and state
        /// changed; false when no such entry or already in target state.
        /// </summary>
        public bool SetPromoted(string name, bool promoted)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var e in Equations)
            {
                if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Promoted == promoted) return false;
                e.Promoted = promoted;
                Save();
                return true;
            }
            return false;
        }

        public bool Remove(string name)
        {
            for (int i = 0; i < Equations.Count; i++)
            {
                if (Equations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Equations.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        public UserEquationEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Equations)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>
        /// #27 Phase 5a — normalise saved equations onto the safe DSL while
        /// keeping them on the live-rendering <see cref="UserEquationKind.UserEquation"/>
        /// path. The store is UI-free, so the translation (EquationPreprocessor →
        /// SandboxExpression, which live in higher layers) is injected:
        /// <paramref name="translate"/> returns the DSL text when a C# source
        /// translates and validates, or null to leave it as-is (no DSL form — it
        /// stays editable and the live path shows the DSL error).
        ///
        /// Two operations:
        /// <list type="bullet">
        /// <item>Rewrite a <c>UserEquation</c> entry's C# source to its DSL form
        ///   (Kind kept). Idempotent — an entry already stored as its own DSL is
        ///   skipped (the translation equals the source).</item>
        /// <item>When <paramref name="flipDslEntriesToUserEquation"/> is set (a
        ///   one-time corrective), move every <c>Dsl</c>-tagged entry back to the
        ///   <c>UserEquation</c> tab. A prior migration wrongly flipped entries to
        ///   <c>Dsl</c>, which routed them to an editor tab that does not render
        ///   live (only the CalcGen "Compile &amp; Load" codegen path) — the
        ///   safe interpreter renders the same DSL directly on the UserEquation
        ///   tab, so this restores default rendering. The DSL source is kept.</item>
        /// </list>
        ///
        /// Before the first change the current <c>userequations.json</c> is
        /// snapshotted via <see cref="UserDataBackup"/> so a user's original
        /// authored text is recoverable ([[feedback_no_save_over_examples]]).
        /// Returns the number of entries changed.
        /// </summary>
        public int MigrateUserEquationsToDsl(Func<string, string?> translate, bool flipDslEntriesToUserEquation)
        {
            if (translate == null) return 0;

            var flips = new List<UserEquationEntry>();
            var rewrites = new List<(UserEquationEntry Entry, string Dsl)>();

            foreach (var e in Equations)
            {
                if (flipDslEntriesToUserEquation && e.Kind == UserEquationKind.Dsl)
                {
                    // Source is already DSL; just move it back to the live tab.
                    flips.Add(e);
                    continue;
                }
                if (e.Kind != UserEquationKind.UserEquation) continue;
                if (string.IsNullOrWhiteSpace(e.Source)) continue;
                string? dsl = translate(e.Source);
                if (string.IsNullOrWhiteSpace(dsl)) continue;   // no DSL form — leave it
                if (SourcesEqual(dsl!, e.Source)) continue;      // already DSL text — idempotent
                rewrites.Add((e, dsl!));
            }

            if (flips.Count == 0 && rewrites.Count == 0) return 0;

            // Snapshot the original file before the destructive change.
            UserDataBackup.SnapshotBeforeMigration(EquationsFile, "dslmigration");

            foreach (var e in flips) e.Kind = UserEquationKind.UserEquation;
            foreach (var (entry, dsl) in rewrites) entry.Source = dsl; // Kind stays UserEquation
            Save();
            return flips.Count + rewrites.Count;
        }

        private static bool SourcesEqual(string? a, string? b)
            => string.Equals(
                (a ?? string.Empty).Replace("\r\n", "\n").Trim(),
                (b ?? string.Empty).Replace("\r\n", "\n").Trim(),
                StringComparison.Ordinal);
    }
}
