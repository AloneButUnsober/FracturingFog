// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneSceneStore.cs
//
// #1637 — "My scenes": the user's saved Math Plane scenes, next to the read-only
// built-in demos (MathPlanePresets, #1555). Each entry is a MathPlanePreset (the
// same record the demos use), so loading one is the same Apply + view as a demo.
// Mirrors UserWatermarkStore: lazy instance, indented JSON, per-entry tolerant
// load (#966, entries this build can't read are kept on disk), atomic write,
// non-fatal failures. File: <data root>\mathplanescenes.json.
//
// Batch parity: a saved scene is only Math Plane params plus a view, all of which
// --param / --x / --y / --zoom and the Command builder already carry.
//
// #1694 — import / export for sharing: ExportJson writes the same array shape as
// the store file; ImportFile reads through the ImportedJson safeguard (#1212), skips
// entries it can't read (counted, never blocking the readable ones) and never
// overwrites: a name that clashes with a saved scene or a built-in demo gets a
// " (2)", " (3)" ... suffix, so the demos stay read-only and nothing is lost.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FracturingFog.Abstractions;

namespace FracturingFog.Models
{
    public sealed class MathPlaneSceneStore
    {
        private static MathPlaneSceneStore? _instance;

        /// <summary>The app's store under <see cref="AppDataPaths.Root"/>, loaded on first use.</summary>
        public static MathPlaneSceneStore Instance
        {
            get
            {
                if (_instance == null)
                {
                    var s = new MathPlaneSceneStore(Path.Combine(AppDataPaths.Root, FileName));
                    s.Load();
                    _instance = s;
                }
                return _instance;
            }
        }

        public const string FileName = "mathplanescenes.json";

        /// <summary>A store backed by <paramref name="filePath"/> (tests use a temp file).
        /// Call <see cref="Load"/> to read it.</summary>
        public MathPlaneSceneStore(string filePath) => FilePath = filePath;

        public string FilePath { get; }

        /// <summary>The saved scenes, in save order.</summary>
        public List<MathPlanePreset> Scenes { get; } = new();

        private readonly TolerantJsonList<MathPlanePreset> _persisted = new();

        /// <summary>Entries in the file this build could not read (kept on disk).</summary>
        public int UnreadableCount => _persisted.UnreadableCount;

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public void Load()
        {
            Scenes.Clear();
            foreach (var s in _persisted.LoadFile(FilePath, BuildJsonOptions()))
                if (s != null && !string.IsNullOrWhiteSpace(s.Name)) Scenes.Add(s with { Scene = s.Scene ?? "", Idea = s.Idea ?? "" });
        }

        /// <summary>Write the file. Returns false (and keeps the in-memory list) on an IO failure.</summary>
        public bool Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(FilePath, _persisted.Serialize(Scenes, BuildJsonOptions(), x => x.Name));
                return true;
            }
            catch
            {
                return false; // Non-fatal.
            }
        }

        /// <summary>True when <paramref name="name"/> is a built-in demo's name: built-ins are read-only.</summary>
        public static bool IsBuiltInName(string? name)
            => !string.IsNullOrWhiteSpace(name)
               && MathPlanePresets.All.Any(d => d.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Why <paramref name="name"/> can't be used for a saved scene, or null when it can.</summary>
        public static string? NameProblem(string? name)
            => string.IsNullOrWhiteSpace(name) ? "Type a name for the scene."
             : IsBuiltInName(name) ? $"'{name!.Trim()}' is a built-in demo; built-ins are read-only, so pick another name."
             : null;

        /// <summary>#1694 — what an import did.</summary>
        public sealed record ImportResult(int Added, int Renamed, int Skipped, IReadOnlyList<string> Names, string? Error = null)
        {
            /// <summary>One line for the section, e.g. "Imported 3 scenes (1 renamed, 1 skipped)."</summary>
            public string Describe()
            {
                if (Error != null) return Error;
                string s = $"Imported {Added} scene{(Added == 1 ? "" : "s")}";
                var extra = new List<string>();
                if (Renamed > 0) extra.Add($"{Renamed} renamed to avoid a clash");
                if (Skipped > 0) extra.Add($"{Skipped} unreadable, skipped");
                return extra.Count > 0 ? s + " (" + string.Join(", ", extra) + ")." : s + ".";
            }
        }

        /// <summary>#1694 — <paramref name="scenes"/> as JSON, the same shape as the store file.</summary>
        public static string ExportJson(IEnumerable<MathPlanePreset> scenes)
            => JsonSerializer.Serialize(scenes.ToList(), BuildJsonOptions());

        /// <summary>#1694 — write <paramref name="scenes"/> to <paramref name="path"/> (atomic).</summary>
        public static void ExportFile(string path, IEnumerable<MathPlanePreset> scenes)
            => AtomicFile.WriteAllText(path, ExportJson(scenes));

        /// <summary>#1694 — import a file written by <see cref="ExportFile"/> (or the store file
        /// itself), through the ImportedJson safeguard. See <see cref="ImportJson"/>.</summary>
        public ImportResult ImportFile(string path)
        {
            string text;
            try { text = ImportedJson.ReadFile(path); }
            catch (Exception ex) { return new ImportResult(0, 0, 0, Array.Empty<string>(), "Could not read the file: " + ex.Message); }
            return ImportJson(text);
        }

        /// <summary>#1694 — add the scenes in <paramref name="json"/> (an array of scenes, or one
        /// scene object). Unreadable entries are skipped and counted; a name that clashes with a
        /// saved scene or a built-in demo is given a numbered suffix. Persists once.</summary>
        public ImportResult ImportJson(string json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(ImportedJson.Sanitize(json), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException ex) { return new ImportResult(0, 0, 0, Array.Empty<string>(), "Not a Math Plane scenes file: " + ex.Message); }
            using (doc)
            {
                var elements = doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => doc.RootElement.EnumerateArray().ToList(),
                    JsonValueKind.Object => new List<JsonElement> { doc.RootElement },
                    _ => null,
                };
                if (elements == null)
                    return new ImportResult(0, 0, 0, Array.Empty<string>(), "Not a Math Plane scenes file: expected a list of scenes.");
                var options = BuildJsonOptions();
                int added = 0, renamed = 0, skipped = 0;
                var names = new List<string>();
                foreach (var e in elements)
                {
                    MathPlanePreset? s;
                    try { s = e.Deserialize<MathPlanePreset>(options); }
                    catch (Exception) { s = null; }
                    if (s == null || string.IsNullOrWhiteSpace(s.Name)) { skipped++; continue; }
                    string want = s.Name.Trim();
                    string name = UniqueName(want);
                    if (name != want) renamed++;
                    Scenes.Add(s with { Name = name, Scene = s.Scene ?? "", Idea = s.Idea ?? "" });
                    names.Add(name);
                    added++;
                }
                if (added > 0) Save();
                return new ImportResult(added, renamed, skipped, names);
            }
        }

        /// <summary>#1694 — <paramref name="name"/>, or "name (2)", "name (3)" ... when it is a
        /// built-in demo's name or already saved (case-insensitive).</summary>
        public string UniqueName(string name)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Imported scene" : name.Trim();
            bool Taken(string n) => IsBuiltInName(n) || Exists(n);
            if (!Taken(baseName)) return baseName;
            for (int k = 2; ; k++)
            {
                string candidate = $"{baseName} ({k})";
                if (!Taken(candidate)) return candidate;
            }
        }

        public MathPlanePreset? GetByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string n = name.Trim();
            return Scenes.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        }

        public bool Exists(string? name) => GetByName(name) != null;

        /// <summary>Insert, or replace the entry of the same name (case-insensitive), then
        /// persist. Returns the stored scene, or null when the name is blank or a built-in's.</summary>
        public MathPlanePreset? SaveScene(MathPlanePreset scene)
        {
            if (scene == null || NameProblem(scene.Name) != null) return null;
            var stored = scene with { Name = scene.Name.Trim() };
            int i = Scenes.FindIndex(s => s.Name.Equals(stored.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) Scenes[i] = stored; else Scenes.Add(stored);
            Save();
            return stored;
        }

        public bool Remove(string? name)
        {
            var s = GetByName(name);
            if (s == null) return false;
            Scenes.Remove(s);
            Save();
            return true;
        }

        /// <summary>Rename <paramref name="oldName"/> to <paramref name="newName"/>. Returns why it
        /// failed, or null on success (the new name may differ from the old only in case).</summary>
        public string? Rename(string? oldName, string? newName)
        {
            var s = GetByName(oldName);
            if (s == null) return "No saved scene of that name.";
            if (NameProblem(newName) is { } why) return why;
            string n = newName!.Trim();
            var clash = GetByName(n);
            if (clash != null && !ReferenceEquals(clash, s)) return $"A saved scene called '{clash.Name}' already exists.";
            Scenes[Scenes.IndexOf(s)] = s with { Name = n };
            Save();
            return null;
        }
    }
}
