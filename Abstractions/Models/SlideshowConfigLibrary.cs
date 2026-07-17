// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SlideshowConfigLibrary.cs
//
// Keyed collection of named slideshow presets, persisted to
// %APPDATA%\FracturingFog\slideshow-configs.json. On first load it migrates
// the legacy single-config file (slideshow-settings.json, owned by
// SlideshowSettingsStore) into a "Default" entry so users keep their tuning.
//
// Export writes a single SlideshowConfig file (one preset per file) so users
// can share named presets without disturbing the rest of the library. Import
// reads that form and a JSON array of presets, so a hand-assembled or
// bulk-exported multi-preset file lands in one pass.

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
                        NormalizeLegacyNames(file);
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
                AtomicFile.WriteAllText(ConfigsFile, json);
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

        /// <summary>Read one or many presets from <paramref name="path"/> and
        /// upsert each into the library. Accepts a bare preset object as well
        /// as a JSON array of presets. Same-name presets are replaced (matching
        /// the single-preset import this grew out of), and the last preset in
        /// the file ends up active. Returns the imported names in file order,
        /// empty on any IO/parse error or when no element carried a name.</summary>
        public static IReadOnlyList<string> Import(SlideshowConfigFile file, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            try
            {
                var json = File.ReadAllText(path);
                var parsed = ParseConfigs(json);
                if (parsed.Count == 0) return Array.Empty<string>();

                // Normalize the whole batch through one wrapper before any
                // upsert, so legacy names in the file resolve the same way they
                // do on Load() — the wrapper shares the parsed references.
                var wrap = new SlideshowConfigFile { ActiveName = parsed[0].Name };
                wrap.Configs.AddRange(parsed);
                NormalizeLegacyNames(wrap);

                var names = new List<string>(parsed.Count);
                foreach (var cfg in parsed)
                {
                    Upsert(file, cfg); // persists; marks the imported preset active
                    names.Add(cfg.Name);
                }
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // Root shape decides: '[' = many presets, anything else = one. Nameless
        // entries are dropped rather than defaulted, so a malformed element
        // can't silently overwrite the "Default" preset via Upsert.
        private static List<SlideshowConfig> ParseConfigs(string json)
        {
            var parsed = new List<SlideshowConfig>();
            if (string.IsNullOrWhiteSpace(json)) return parsed;

            if (json.TrimStart().StartsWith("["))
            {
                var many = JsonSerializer.Deserialize<List<SlideshowConfig>>(json, JsonOpts);
                if (many != null) parsed.AddRange(many);
            }
            else
            {
                var one = JsonSerializer.Deserialize<SlideshowConfig>(json, JsonOpts);
                if (one != null) parsed.Add(one);
            }

            parsed.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Name));
            return parsed;
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

        // Rewrite any saved pre-ASCII (Unicode) region / color-theme names in
        // the include lists to their current ASCII names via the alias map, so
        // filter matching against the live (renamed) libraries keeps working.
        // Self-healing: the ASCII names persist on the next Save.
        private static void NormalizeLegacyNames(SlideshowConfigFile file)
        {
            foreach (var c in file.Configs)
            {
                MapInPlace(c.IncludedRegions);
                MapInPlace(c.IncludedColorThemes);
            }

            static void MapInPlace(List<string>? names)
            {
                if (names == null) return;
                for (int i = 0; i < names.Count; i++)
                {
                    var mapped = LegacyNameAliases.Resolve(names[i]);
                    if (mapped != null) names[i] = mapped;
                }
            }
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
