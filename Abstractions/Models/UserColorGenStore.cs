// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserColorGenStore.cs
//
// Singleton persistence for user-defined ColorGen DSL sources. Each entry
// has a human-readable Name + the raw DSL source the ColorGenEditor
// compiles. Stored as JSON in %APPDATA%\FracturingFog\colorgen.json.
//
// Mirrors UserEquationStore in shape so the editor's Save / Load /
// Delete / Promote workflow is identical from the user's perspective.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    public sealed class UserColorGenEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        /// <summary>Free-text description embedded in generated C# class
        /// header + INamedColorMap.DisplayDescription. Optional.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>#611 — the selected trap shape (an OrbitTrapShape member name)
        /// for the DSL <c>trap</c> input. Absent / empty ⇒ "Point" (back-compat:
        /// pre-#611 themes have no shape and default to the origin point-trap).</summary>
        public string TrapShape { get; set; } = "Point";

        /// <summary>#615 — optional packed-ARGB colour (8 hex digits, "AARRGGBB")
        /// for the beyond-escape-radius surround. Absent / empty ⇒ no override
        /// (back-compat: the escape gradient paints the surround as before).</summary>
        public string OutOfBoundsColorArgb { get; set; } = string.Empty;
    }

    public sealed class UserColorGenStore
    {
        private static UserColorGenStore? _instance;
        public static UserColorGenStore Instance => _instance ??= new UserColorGenStore();
        private UserColorGenStore() { }

        public List<UserColorGenEntry> Entries { get; } = new();

        private static string SettingsDir => AppDataPaths.Root;

        private static string EntriesFile =>
            Path.Combine(SettingsDir, "colorgen.json");

        private static JsonSerializerOptions BuildJsonOptions() => new() { WriteIndented = true };

        public void Load()
        {
            // #966 — per-entry load; entries this build can't read are preserved for Save.
            Entries.Clear();
            foreach (var e in _persisted.LoadFile(EntriesFile, BuildJsonOptions()))
                if (!string.IsNullOrWhiteSpace(e.Name)) Entries.Add(e);
        }

        // #966 — preserves entries this build could not read across Load/Save.
        private readonly TolerantJsonList<UserColorGenEntry> _persisted = new();

        /// <summary>Entries in colorgen.json this build could not read (kept on disk).</summary>
        public int UnreadableCount => _persisted.UnreadableCount;

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = _persisted.Serialize(Entries, BuildJsonOptions(), x => x.Name);
                AtomicFile.WriteAllText(EntriesFile, json);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>Insert or replace by Name (case-insensitive).</summary>
        public UserColorGenEntry? SaveEntry(string name, string source, string description = "", string trapShape = "Point", string outOfBoundsColorArgb = "")
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string shape = string.IsNullOrWhiteSpace(trapShape) ? "Point" : trapShape;
            string oob = outOfBoundsColorArgb ?? string.Empty;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Entries[i].Source = source ?? string.Empty;
                    Entries[i].Description = description ?? string.Empty;
                    Entries[i].TrapShape = shape;
                    Entries[i].OutOfBoundsColorArgb = oob;
                    Save();
                    return Entries[i];
                }
            }
            var entry = new UserColorGenEntry { Name = name, Source = source ?? "", Description = description ?? "", TrapShape = shape, OutOfBoundsColorArgb = oob };
            Entries.Add(entry);
            Save();
            return entry;
        }

        public bool Remove(string name)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Entries.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        public UserColorGenEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Entries)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
