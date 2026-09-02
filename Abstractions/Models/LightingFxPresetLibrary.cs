// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Models/LightingFxPresetLibrary.cs — #580.
//
// Keyed collection of user-named Volumetric Lighting & FX presets, persisted to
// %APPDATA%\FracturingFog\lighting-fx-presets.json. Each entry wraps a
// LightingFxPresetData (the JSON-friendly mirror of the runtime LightingFxData
// struct) under a user-chosen name.
//
// Static file gateway (Load / Save / Get / GetActive / Upsert / Delete / Import
// / Export), modelled directly on WorkspaceLayoutLibrary (#433): no legacy
// migration (new feature) and no forced "Default" entry — a fresh install has
// zero presets until the user saves one, so an empty library is valid.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>One user-named Lighting &amp; FX preset: a display name plus the
    /// full serialized lighting/FX block.</summary>
    public sealed class LightingFxPreset
    {
        public string Name { get; set; } = "";
        public LightingFxPresetData Data { get; set; } = new();

        /// <summary>Deep copy (name + cloned data) so a stored preset never
        /// aliases a caller's mutable instance.</summary>
        public LightingFxPreset Clone() => new()
        {
            Name = Name,
            Data = Data?.Clone() ?? new LightingFxPresetData(),
        };
    }

    /// <summary>On-disk envelope: the saved presets + the active name.</summary>
    public sealed class LightingFxPresetFile
    {
        public int Version { get; set; } = 1;
        public string? ActiveName { get; set; }
        public List<LightingFxPreset> Presets { get; set; } = new();
    }

    /// <summary>Static gateway over the on-disk Lighting &amp; FX preset library.
    /// Mirrors <see cref="WorkspaceLayoutLibrary"/> so host + asset-source code
    /// swaps in with minimal ceremony.</summary>
    public static class LightingFxPresetLibrary
    {
        private static string SettingsDir => AppDataPaths.Root;
        private static string PresetsFile => Path.Combine(SettingsDir, "lighting-fx-presets.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Load the preset file. Returns an empty (but non-null) file when
        /// nothing is saved yet or the file is missing/unreadable — an empty
        /// library is a valid state for this feature.</summary>
        public static LightingFxPresetFile Load()
        {
            try
            {
                if (File.Exists(PresetsFile))
                {
                    var json = File.ReadAllText(PresetsFile);
                    var file = JsonSerializer.Deserialize<LightingFxPresetFile>(json, JsonOpts);
                    if (file != null)
                    {
                        file.Presets ??= new List<LightingFxPreset>();
                        file.Presets.RemoveAll(p => p == null);
                        foreach (var p in file.Presets) p.Data ??= new LightingFxPresetData();
                        EnsureActiveValid(file);
                        return file;
                    }
                }
            }
            catch
            {
                // fall through to a fresh empty file
            }

            return new LightingFxPresetFile();
        }

        /// <summary>Persist the preset file. Best-effort — IO errors are swallowed
        /// (mirrors the other stores).</summary>
        public static void Save(LightingFxPresetFile file)
        {
            if (file == null) return;
            EnsureActiveValid(file);
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(file, JsonOpts);
                AtomicFile.WriteAllText(PresetsFile, json);
            }
            catch { }
        }

        /// <summary>Resolve <see cref="LightingFxPresetFile.ActiveName"/> to a
        /// concrete preset (clone), or null when the library is empty.</summary>
        public static LightingFxPreset? GetActive(LightingFxPresetFile file)
        {
            if (file == null || file.Presets.Count == 0) return null;
            foreach (var p in file.Presets)
            {
                if (string.Equals(p.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return p.Clone();
            }
            return file.Presets[0].Clone();
        }

        /// <summary>Look up one preset by name (clone), or null.</summary>
        public static LightingFxPreset? Get(LightingFxPresetFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (var p in file.Presets)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Clone();
            }
            return null;
        }

        /// <summary>Add or replace a preset by name, mark it active, and persist.
        /// A blank name is rejected (no-op) — presets are always user-named.</summary>
        public static void Upsert(LightingFxPresetFile file, LightingFxPreset preset)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(preset);
            if (string.IsNullOrWhiteSpace(preset.Name)) return;

            for (int i = 0; i < file.Presets.Count; i++)
            {
                if (string.Equals(file.Presets[i].Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Presets[i] = preset.Clone();
                    file.ActiveName = preset.Name;
                    Save(file);
                    return;
                }
            }
            file.Presets.Add(preset.Clone());
            file.ActiveName = preset.Name;
            Save(file);
        }

        /// <summary>Delete a preset by name. Returns true on a real delete.</summary>
        public static bool Delete(LightingFxPresetFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return false;

            for (int i = 0; i < file.Presets.Count; i++)
            {
                if (string.Equals(file.Presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Presets.RemoveAt(i);
                    EnsureActiveValid(file);
                    Save(file);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Read one or many presets from <paramref name="path"/> and
        /// upsert each into the library. Accepts a bare preset object as well as a
        /// JSON array. Same-name presets are replaced; the last one in the file
        /// ends up active. Returns imported names in file order, empty on any
        /// IO/parse error or when no element carried a name.</summary>
        public static IReadOnlyList<string> Import(LightingFxPresetFile file, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            try
            {
                var json = File.ReadAllText(path);
                var parsed = ParsePresets(json);
                if (parsed.Count == 0) return Array.Empty<string>();

                var names = new List<string>(parsed.Count);
                foreach (var p in parsed)
                {
                    Upsert(file, p); // persists; marks the imported preset active
                    names.Add(p.Name);
                }
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // Root shape decides: '[' = many presets, anything else = one. Nameless
        // entries are dropped rather than defaulted, so a malformed element can't
        // silently create an unnamed preset via Upsert (which also rejects it).
        private static List<LightingFxPreset> ParsePresets(string json)
        {
            var parsed = new List<LightingFxPreset>();
            if (string.IsNullOrWhiteSpace(json)) return parsed;

            if (json.TrimStart().StartsWith("["))
            {
                var many = JsonSerializer.Deserialize<List<LightingFxPreset>>(json, JsonOpts);
                if (many != null) parsed.AddRange(many);
            }
            else
            {
                var one = JsonSerializer.Deserialize<LightingFxPreset>(json, JsonOpts);
                if (one != null) parsed.Add(one);
            }

            parsed.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name));
            foreach (var p in parsed) p.Data ??= new LightingFxPresetData();
            return parsed;
        }

        /// <summary>Serialize one preset (looked up by name) to standalone JSON
        /// for the Asset Manager export bundle. Null when the preset is absent.</summary>
        public static string? ExportJson(LightingFxPresetFile file, string name)
        {
            var p = Get(file, name);
            if (p == null) return null;
            try { return JsonSerializer.Serialize(p, JsonOpts); }
            catch { return null; }
        }

        /// <summary>Write a single preset (looked up by name) to
        /// <paramref name="path"/>. Returns true on success.</summary>
        public static bool Export(LightingFxPresetFile file, string name, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                var p = Get(file, name);
                if (p == null) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SettingsDir);
                var json = JsonSerializer.Serialize(p, JsonOpts);
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Parse one preset from standalone JSON (the inverse of a single
        /// <see cref="Export"/>), using the same enum-string handling the file
        /// uses. Null on blank / malformed input or a nameless entry. Used by the
        /// Asset Manager import adapter (#580).</summary>
        public static LightingFxPreset? ParseOne(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var p = JsonSerializer.Deserialize<LightingFxPreset>(json, JsonOpts);
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) return null;
                p.Data ??= new LightingFxPresetData();
                return p;
            }
            catch { return null; }
        }

        // Keep ActiveName pointing at a real entry, or null when empty. Unlike
        // SlideshowConfigLibrary this never fabricates a "Default" — an empty
        // library is valid.
        private static void EnsureActiveValid(LightingFxPresetFile file)
        {
            if (file.Presets.Count == 0)
            {
                file.ActiveName = null;
                return;
            }
            foreach (var p in file.Presets)
            {
                if (string.Equals(p.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            file.ActiveName = file.Presets[0].Name;
        }
    }
}
