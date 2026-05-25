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

namespace FracturingFog.Models
{
    public sealed class SandboxEquationEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

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

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

        private static string EquationsFile =>
            Path.Combine(SettingsDir, "sandboxequations.json");

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
                var loaded = JsonSerializer.Deserialize<List<SandboxEquationEntry>>(json, BuildJsonOptions());
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

        public SandboxEquationEntry? SaveEquation(string name, string source)
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
