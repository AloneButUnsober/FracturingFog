// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/SandboxEquationStore.cs
//
// Singleton persistence for Sandbox fractal equations. Mirrors
// UserEquationStore but the source is a restricted expression DSL parsed by
// SandboxExpression — no Roslyn, no BCL access. Stored in
// %APPDATA%\FracturingFog\sandboxequations.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    public sealed class SandboxEquationEntry : System.Text.Json.Serialization.IJsonOnDeserialized
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        // #1088 — equation-language version of this entry's text. Entries made in
        // code are the current version; a JSON entry WITHOUT the field was saved
        // before #1088 and is version 1 (the store upgrades it on Load / import).
        private bool _languageVersionRead;

        [System.Text.Json.Serialization.JsonIgnore]
        public int LanguageVersion { get; set; } = EquationMigration.CurrentLanguageVersion;

        [System.Text.Json.Serialization.JsonPropertyName("LanguageVersion")]
        public int? LanguageVersionJson
        {
            get => LanguageVersion;
            set { _languageVersionRead = true; LanguageVersion = value ?? 1; }
        }

        void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
        {
            if (!_languageVersionRead) LanguageVersion = 1;
        }

        /// <summary>
        /// When true, this entry is surfaced as a first-class fractal type in
        /// the main fractal dropdown via <see cref="RegisteredFractalCatalog"/>.
        /// Defaults false; missing field in legacy JSON deserialises to false.
        /// </summary>
        public bool Promoted { get; set; }
    }

    public sealed class SandboxEquationStore
    {
        private static SandboxEquationStore? _instance;
        public static SandboxEquationStore Instance => _instance ??= new SandboxEquationStore();

        private SandboxEquationStore() { }

        public List<SandboxEquationEntry> Equations { get; } = new();

        private static string SettingsDir => AppDataPaths.Root;

        private static string EquationsFile =>
            Path.Combine(SettingsDir, "sandboxequations.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        public void Load()
        {
            // #966 — per-entry load; entries this build can't read are preserved for Save.
            Equations.Clear();
            foreach (var e in _persisted.LoadFile(EquationsFile, BuildJsonOptions()))
                if (!string.IsNullOrWhiteSpace(e.Name)) Equations.Add(e);
            UpgradeLegacyEntries(persist: true);   // #1088
        }

        /// <summary>#1088 — upgrade entries saved under equation-language version 1
        /// so they render the same under the current rules (EquationMigration).
        /// With <paramref name="persist"/>, snapshots the file first
        /// (UserDataBackup) and saves. Returns the number of texts rewritten;
        /// entries are stamped current either way, so a second run is a no-op.</summary>
        public int UpgradeLegacyEntries(bool persist)
        {
            var legacy = Equations.FindAll(x => x.LanguageVersion < EquationMigration.CurrentLanguageVersion);
            if (legacy.Count == 0) return 0;
            if (persist) UserDataBackup.SnapshotBeforeMigration(EquationsFile, "equation-language-v2");
            int changed = 0;
            foreach (var e in legacy)
            {
                var r0 = EquationMigration.UpgradeFromVersion1(e.Source);
                if (r0.Changed) { e.Source = r0.Source; changed++; }
                e.LanguageVersion = EquationMigration.CurrentLanguageVersion;
            }
            if (persist) Save();
            return changed;
        }

        // #966 — preserves entries this build could not read across Load/Save.
        private readonly TolerantJsonList<SandboxEquationEntry> _persisted = new();

        /// <summary>Entries in the file this build could not read (kept on disk).</summary>
        public int UnreadableCount => _persisted.UnreadableCount;

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = _persisted.Serialize(Equations, BuildJsonOptions(), x => x.Name);
                AtomicFile.WriteAllText(EquationsFile, json);
            }
            catch
            {
                // Non-fatal.
            }
        }

        public SandboxEquationEntry? SaveEquation(string name, string source)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            for (int i = 0; i < Equations.Count; i++)
            {
                if (Equations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Equations[i].Source = source ?? string.Empty;
                    Equations[i].LanguageVersion = EquationMigration.CurrentLanguageVersion;   // #1088
                    Save();
                    return Equations[i];
                }
            }

            var entry = new SandboxEquationEntry { Name = name, Source = source ?? string.Empty };
            Equations.Add(entry);
            Save();
            return entry;
        }

        /// <summary>
        /// Sets the <see cref="SandboxEquationEntry.Promoted"/> flag on the
        /// named entry and persists. Returns true when the entry exists and
        /// state changed; false when no such entry or already in target state.
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

        public SandboxEquationEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Equations)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
