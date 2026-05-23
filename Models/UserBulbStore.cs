// Models/UserBulbStore.cs
//
// Singleton persistence for user-defined 3D bulb equations.  Each entry has a
// human-readable Name and the raw C# source body that UserBulbCalculator
// compiles.  Stored as JSON in %APPDATA%\FracturingFog\userbulbs.json.
//
// Mirrors UserEquationStore in spirit: lazy singleton, indented JSON,
// failures during load/save are non-fatal.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FracturingFog.Models
{
    public sealed class UserBulbEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class UserBulbStore
    {
        private static UserBulbStore? _instance;
        public static UserBulbStore Instance => _instance ??= new UserBulbStore();

        private UserBulbStore() { }

        public List<UserBulbEntry> Equations { get; } = new();

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

        private static string EquationsFile =>
            Path.Combine(SettingsDir, "userbulbs.json");

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
                var loaded = JsonSerializer.Deserialize<List<UserBulbEntry>>(json, BuildJsonOptions());
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
                File.WriteAllText(EquationsFile, json);
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
        public UserBulbEntry? SaveEquation(string name, string source)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            for (int i = 0; i < Equations.Count; i++)
            {
                if (Equations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Equations[i].Source = source ?? string.Empty;
                    Save();
                    return Equations[i];
                }
            }

            var entry = new UserBulbEntry { Name = name, Source = source ?? string.Empty };
            Equations.Add(entry);
            Save();
            return entry;
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

        public UserBulbEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Equations)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
