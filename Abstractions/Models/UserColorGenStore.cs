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
            try
            {
                Entries.Clear();
                if (!File.Exists(EntriesFile)) return;
                string json = File.ReadAllText(EntriesFile);
                var loaded = JsonSerializer.Deserialize<List<UserColorGenEntry>>(json, BuildJsonOptions());
                if (loaded == null) return;
                foreach (var e in loaded)
                    if (e != null && !string.IsNullOrWhiteSpace(e.Name)) Entries.Add(e);
            }
            catch { Entries.Clear(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Entries, BuildJsonOptions());
                File.WriteAllText(EntriesFile, json);
            }
            catch { /* non-fatal */ }
        }

        /// <summary>Insert or replace by Name (case-insensitive).</summary>
        public UserColorGenEntry? SaveEntry(string name, string source, string description = "")
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Entries[i].Source = source ?? string.Empty;
                    Entries[i].Description = description ?? string.Empty;
                    Save();
                    return Entries[i];
                }
            }
            var entry = new UserColorGenEntry { Name = name, Source = source ?? "", Description = description ?? "" };
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
