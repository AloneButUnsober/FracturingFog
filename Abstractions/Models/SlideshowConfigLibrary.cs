// SlideshowConfigLibrary.cs
//
// Keyed collection of named slideshow presets, persisted to
// %APPDATA%\FracturingFog\slideshow-configs.json. On first load it migrates
// the legacy single-config file (slideshow-settings.json, owned by
// SlideshowSettingsStore) into a "Default" entry so users keep their tuning.
//
// Import/Export read and write a single SlideshowConfig file (one preset per
// file) so users can share named presets without disturbing the rest of the
// library.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>On-disk envelope: collection of presets + the active name.</summary>
    public sealed class SlideshowConfigFile
    {
        public int Version { get; set; } = 1;
        public string ActiveName { get; set; } = "Default";
        public List<SlideshowConfig> Configs { get; set; } = new();
    }

    /// <summary>Singleton-ish static gateway over the on-disk preset library.
    /// Static API matches the legacy <see cref="SlideshowSettingsStore"/> shape
    /// (Load / Save) so the host code can swap to it with minimal ceremony.</summary>
    public static class SlideshowConfigLibrary
    {
        private static string SettingsDir => AppDataPaths.Root;

        private static string ConfigsFile => Path.Combine(SettingsDir, "slideshow-configs.json");
        private const string DefaultConfigName = "Default";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Load the preset file, falling back to legacy migration when
        /// the new file is absent. Always returns a non-empty file with at
        /// least the "Default" entry.</summary>
        public static SlideshowConfigFile Load()
        {
            try
            {
                if (File.Exists(ConfigsFile))
                {
                    var json = File.ReadAllText(ConfigsFile);
                    var file = JsonSerializer.Deserialize<SlideshowConfigFile>(json, JsonOpts);
                    if (file != null && file.Configs.Count > 0)
                    {
                        EnsureActiveValid(file);
                        return file;
                    }
                }
            }
            catch
            {
                // fall through to migration / seed
            }

            return MigrateOrSeed();
        }

        /// <summary>Persist the preset file to disk. Best-effort — IO errors
        /// swallowed (mirrors legacy SlideshowSettingsStore behavior).</summary>
        public static void Save(SlideshowConfigFile file)
        {
            if (file == null) return;
            EnsureActiveValid(file);
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(file, JsonOpts);
                File.WriteAllText(ConfigsFile, json);
            }
            catch { }
        }

        /// <summary>Resolve <see cref="SlideshowConfigFile.ActiveName"/> to a
        /// concrete config (clone). Falls back to the first entry, then to a
        /// fresh default if the file is empty.</summary>
        public static SlideshowConfig GetActive(SlideshowConfigFile file)
        {
            if (file == null || file.Configs.Count == 0)
                return new SlideshowConfig { Name = DefaultConfigName };

            foreach (var c in file.Configs)
            {
                if (string.Equals(c.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return c.Clone();
            }
            return file.Configs[0].Clone();
        }

        /// <summary>Add or replace a preset by name, mark it active, and persist.</summary>
        public static void Upsert(SlideshowConfigFile file, SlideshowConfig config)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(config.Name)) config.Name = DefaultConfigName;

            for (int i = 0; i < file.Configs.Count; i++)
            {
                if (string.Equals(file.Configs[i].Name, config.Name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Configs[i] = config.Clone();
                    file.ActiveName = config.Name;
                    Save(file);
                    return;
                }
            }
            file.Configs.Add(config.Clone());
            file.ActiveName = config.Name;
            Save(file);
        }

        /// <summary>Delete a preset by name. The "Default" preset cannot be
        /// removed (the library guarantees one always exists). Returns true
        /// on a real delete.</summary>
        public static bool Delete(SlideshowConfigFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return false;
            if (string.Equals(name, DefaultConfigName, StringComparison.OrdinalIgnoreCase)) return false;

            for (int i = 0; i < file.Configs.Count; i++)
            {
                if (string.Equals(file.Configs[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Configs.RemoveAt(i);
                    EnsureActiveValid(file);
                    Save(file);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Read a single preset from <paramref name="path"/> and upsert
        /// it into the library. Returns the imported preset's name, or null on
        /// any IO/parse error.</summary>
        public static string? Import(SlideshowConfigFile file, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<SlideshowConfig>(json, JsonOpts);
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.Name)) return null;
                Upsert(file, cfg);
                return cfg.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Write a single preset (looked up by name) to
        /// <paramref name="path"/>. Returns true on success.</summary>
        public static bool Export(SlideshowConfigFile file, string name, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                foreach (var c in file.Configs)
                {
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SettingsDir);
                        var json = JsonSerializer.Serialize(c, JsonOpts);
                        File.WriteAllText(path, json);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ── internal ──────────────────────────────────────────────────────────

        private static SlideshowConfigFile MigrateOrSeed()
        {
            // Prefer migrating the legacy SlideshowSettings store so user
            // timing values survive the schema bump.
            SlideshowSettings legacy;
            try { legacy = SlideshowSettingsStore.Load(); }
            catch { legacy = new SlideshowSettings(); }

            var file = new SlideshowConfigFile
            {
                ActiveName = DefaultConfigName,
                Configs = { SlideshowConfig.FromLegacy(DefaultConfigName, legacy, audioReactive: false) },
            };
            Save(file);
            return file;
        }

        private static void EnsureActiveValid(SlideshowConfigFile file)
        {
            if (file.Configs.Count == 0)
            {
                file.Configs.Add(new SlideshowConfig { Name = DefaultConfigName });
                file.ActiveName = DefaultConfigName;
                return;
            }
            foreach (var c in file.Configs)
            {
                if (string.Equals(c.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            file.ActiveName = file.Configs[0].Name;
        }
    }
}
