// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>Serializable DSL-editor preferences persisted by
    /// <see cref="DslEditorPrefsStore"/> (#1362). Keyed by editor window
    /// ("UserEquation", "Sandbox", "UserBulb") so every editor box in one window
    /// shares a mode.</summary>
    public sealed class DslEditorPrefs
    {
        /// <summary>Code view on/off per editor key. A missing key = off (plain
        /// text box, the pre-#1360 behaviour).</summary>
        public Dictionary<string, bool> CodeView { get; set; } = new(StringComparer.Ordinal);

        /// <summary>#1419 — the User Equation editor's Paste LaTeX / Paste MathML
        /// imports drop redundant grouping brackets (parse-tree-guarded). Missing
        /// in an older file = on.</summary>
        public bool MinimiseImportBrackets { get; set; } = true;
    }

    /// <summary>Loads/saves <see cref="DslEditorPrefs"/> to
    /// <see cref="AppDataPaths.Root"/>\dsl-editor.json. Best-effort; never throws.</summary>
    public static class DslEditorPrefsStore
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string SettingsFile => Path.Combine(SettingsDir, "dsl-editor.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Reads the persisted prefs. Returns empty prefs (all editors
        /// plain) when the file is absent or unreadable.</summary>
        public static DslEditorPrefs Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new DslEditorPrefs();
                var json = File.ReadAllText(SettingsFile);
                var prefs = JsonSerializer.Deserialize<DslEditorPrefs>(json, JsonOpts) ?? new DslEditorPrefs();
                // Re-key with an ordinal comparer: the deserializer builds a default one.
                prefs.CodeView = new Dictionary<string, bool>(prefs.CodeView ?? new(), StringComparer.Ordinal);
                return prefs;
            }
            catch
            {
                return new DslEditorPrefs();
            }
        }

        /// <summary>Persists <paramref name="prefs"/>. Silently no-ops on I/O failure.</summary>
        public static void Save(DslEditorPrefs prefs)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(prefs, JsonOpts);
                AtomicFile.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}
