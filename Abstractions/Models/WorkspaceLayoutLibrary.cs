// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/WorkspaceLayoutLibrary.cs
//
// Keyed collection of named window-arrangement workspaces (#433, slice 1/3 —
// #469), persisted to %APPDATA%\FracturingFog\window-workspaces.json.
//
// Static file gateway (Load / Save / Upsert / Delete / GetActive / Import /
// Export), modelled on SlideshowConfigLibrary. Two deliberate differences: no
// legacy migration (this is a new feature, nothing to migrate) and no forced
// "Default" entry — a fresh install has zero workspaces until the user saves
// one, so an empty library is valid.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    /// <summary>On-disk envelope: the saved workspaces + the active name.</summary>
    public sealed class WorkspaceLayoutFile
    {
        public int Version { get; set; } = 1;
        public string? ActiveName { get; set; }
        public List<WorkspaceLayout> Layouts { get; set; } = new();
    }

    /// <summary>Static gateway over the on-disk workspace library. Mirrors the
    /// Load/Save shape of the other stores so host code swaps in with minimal
    /// ceremony.</summary>
    public static class WorkspaceLayoutLibrary
    {
        private static string SettingsDir => AppDataPaths.Root;
        private static string LayoutsFile => Path.Combine(SettingsDir, "window-workspaces.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Load the workspace file. Returns an empty (but non-null) file
        /// when nothing is saved yet or the file is missing/unreadable — an empty
        /// library is a valid state for this feature.</summary>
        public static WorkspaceLayoutFile Load()
        {
            try
            {
                if (File.Exists(LayoutsFile))
                {
                    var json = File.ReadAllText(LayoutsFile);
                    var file = JsonSerializer.Deserialize<WorkspaceLayoutFile>(json, JsonOpts);
                    if (file != null)
                    {
                        file.Layouts ??= new List<WorkspaceLayout>();
                        EnsureActiveValid(file);
                        return file;
                    }
                }
            }
            catch
            {
                // fall through to a fresh empty file
            }

            return new WorkspaceLayoutFile();
        }

        /// <summary>Persist the workspace file. Best-effort — IO errors are
        /// swallowed (mirrors the other stores).</summary>
        public static void Save(WorkspaceLayoutFile file)
        {
            if (file == null) return;
            EnsureActiveValid(file);
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(file, JsonOpts);
                AtomicFile.WriteAllText(LayoutsFile, json);
            }
            catch { }
        }

        /// <summary>Resolve <see cref="WorkspaceLayoutFile.ActiveName"/> to a
        /// concrete workspace (clone), or null when the library is empty.</summary>
        public static WorkspaceLayout? GetActive(WorkspaceLayoutFile file)
        {
            if (file == null || file.Layouts.Count == 0) return null;

            foreach (var w in file.Layouts)
            {
                if (string.Equals(w.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return w.Clone();
            }
            return file.Layouts[0].Clone();
        }

        /// <summary>Look up one workspace by name (clone), or null.</summary>
        public static WorkspaceLayout? Get(WorkspaceLayoutFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (var w in file.Layouts)
            {
                if (string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                    return w.Clone();
            }
            return null;
        }

        /// <summary>Add or replace a workspace by name, mark it active, and
        /// persist. A blank name is rejected (no-op) — workspaces are always
        /// user-named.</summary>
        public static void Upsert(WorkspaceLayoutFile file, WorkspaceLayout layout)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(layout);
            if (string.IsNullOrWhiteSpace(layout.Name)) return;

            for (int i = 0; i < file.Layouts.Count; i++)
            {
                if (string.Equals(file.Layouts[i].Name, layout.Name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Layouts[i] = layout.Clone();
                    file.ActiveName = layout.Name;
                    Save(file);
                    return;
                }
            }
            file.Layouts.Add(layout.Clone());
            file.ActiveName = layout.Name;
            Save(file);
        }

        /// <summary>Delete a workspace by name. Returns true on a real delete.</summary>
        public static bool Delete(WorkspaceLayoutFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return false;

            for (int i = 0; i < file.Layouts.Count; i++)
            {
                if (string.Equals(file.Layouts[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    file.Layouts.RemoveAt(i);
                    EnsureActiveValid(file);
                    Save(file);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Read one or many workspaces from <paramref name="path"/> and
        /// upsert each into the library. Accepts a bare workspace object as well
        /// as a JSON array. Same-name workspaces are replaced; the last one in the
        /// file ends up active. Returns imported names in file order, empty on any
        /// IO/parse error or when no element carried a name.</summary>
        public static IReadOnlyList<string> Import(WorkspaceLayoutFile file, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            try
            {
                var json = File.ReadAllText(path);
                var parsed = ParseLayouts(json);
                if (parsed.Count == 0) return Array.Empty<string>();

                var names = new List<string>(parsed.Count);
                foreach (var w in parsed)
                {
                    Upsert(file, w); // persists; marks the imported workspace active
                    names.Add(w.Name);
                }
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // Root shape decides: '[' = many workspaces, anything else = one. Nameless
        // entries are dropped rather than defaulted, so a malformed element can't
        // silently create an unnamed workspace via Upsert (which also rejects it).
        private static List<WorkspaceLayout> ParseLayouts(string json)
        {
            var parsed = new List<WorkspaceLayout>();
            if (string.IsNullOrWhiteSpace(json)) return parsed;

            if (json.TrimStart().StartsWith("["))
            {
                var many = JsonSerializer.Deserialize<List<WorkspaceLayout>>(json, JsonOpts);
                if (many != null) parsed.AddRange(many);
            }
            else
            {
                var one = JsonSerializer.Deserialize<WorkspaceLayout>(json, JsonOpts);
                if (one != null) parsed.Add(one);
            }

            parsed.RemoveAll(w => w == null || string.IsNullOrWhiteSpace(w.Name));
            return parsed;
        }

        /// <summary>Write a single workspace (looked up by name) to
        /// <paramref name="path"/>. Returns true on success.</summary>
        public static bool Export(WorkspaceLayoutFile file, string name, string path)
        {
            if (file == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                foreach (var w in file.Layouts)
                {
                    if (string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SettingsDir);
                        var json = JsonSerializer.Serialize(w, JsonOpts);
                        File.WriteAllText(path, json);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // Keep ActiveName pointing at a real entry, or null when empty. Unlike
        // SlideshowConfigLibrary this never fabricates a "Default" — an empty
        // library is valid.
        private static void EnsureActiveValid(WorkspaceLayoutFile file)
        {
            if (file.Layouts.Count == 0)
            {
                file.ActiveName = null;
                return;
            }
            foreach (var w in file.Layouts)
            {
                if (string.Equals(w.Name, file.ActiveName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            file.ActiveName = file.Layouts[0].Name;
        }
    }
}
