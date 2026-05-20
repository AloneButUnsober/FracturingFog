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

namespace FracturingFog.Models
{
    public sealed class UserEquationEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class UserEquationStore
    {
        private static UserEquationStore? _instance;
        public static UserEquationStore Instance => _instance ??= new UserEquationStore();

        private UserEquationStore() { }

        public List<UserEquationEntry> Equations { get; } = new();

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

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
        public UserEquationEntry? SaveEquation(string name, string source)
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

            var entry = new UserEquationEntry { Name = name, Source = source ?? string.Empty };
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

        public UserEquationEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Equations)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
