// Models/AnimationLibrary.cs
//
// Singleton library of user-defined animation assets, persisted to JSON in
// %APPDATA%\FracturingFog\animations.json.
//
// Mirrors UserColorThemeLibrary / FractalRegionLibrary:
//   * Singleton, lazy-initialised on first access.
//   * System.Text.Json with indented output for human-editable files.
//   * Failures during load/save are non-fatal — user loses custom animations
//     rather than crashing the app.
//
// Built-in defaults (e.g. the existing Julia C orbit, plus a few sensible
// procedural motion presets) ship in-source via the BuiltInAnimations() seed
// and are merged into the library on first load.

using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Animation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    /// <summary>Singleton library of saved <see cref="AnimationData"/>
    /// entries. Animation Roadmap Phase 2 deliverable.</summary>
    public sealed class AnimationLibrary
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        private static AnimationLibrary? _instance;

        public static AnimationLibrary Instance
            => _instance ??= new AnimationLibrary();

        private AnimationLibrary() { }

        // ── Storage paths ─────────────────────────────────────────────────────

        private static string SettingsDir => AppDataPaths.Root;

        private static string AnimationsFile =>
            Path.Combine(SettingsDir, "animations.json");

        // ── In-memory contents ────────────────────────────────────────────────

        /// <summary>Mutable list of animations. Don't add/remove directly —
        /// use <see cref="Add"/> / <see cref="Remove"/> / <see cref="ReplaceOrAdd"/>
        /// so writes are persisted.</summary>
        public List<AnimationData> Animations { get; } = new();

        // ── JSON options ──────────────────────────────────────────────────────

        public static JsonSerializerOptions BuildJsonOptions()
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            opts.Converters.Add(new JsonStringEnumConverter());
            return opts;
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>Loads animations from disk. Safe to call when the file
        /// is missing or corrupt — the in-memory list ends up empty (plus
        /// any built-in defaults merged on first run).</summary>
        public void Load()
        {
            try
            {
                Animations.Clear();
                if (File.Exists(AnimationsFile))
                {
                    string json = File.ReadAllText(AnimationsFile);
                    var loaded = JsonSerializer.Deserialize<List<AnimationData>>(json, BuildJsonOptions());
                    if (loaded != null)
                    {
                        foreach (var a in loaded)
                            if (a != null) Animations.Add(a);
                    }
                }

                MergeBuiltIns();
            }
            catch
            {
                Animations.Clear();
                MergeBuiltIns();
            }
        }

        /// <summary>Persists the current <see cref="Animations"/> list to disk.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Animations, BuildJsonOptions());
                File.WriteAllText(AnimationsFile, json);
            }
            catch
            {
                // Non-fatal — user loses any unsaved custom animations.
            }
        }

        // ── Mutators ──────────────────────────────────────────────────────────

        /// <summary>Adds a new animation and persists. Returns false if an
        /// animation with the same Name already exists (case-insensitive) or
        /// if <paramref name="data"/> is invalid.</summary>
        public bool Add(AnimationData? data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return false;

            foreach (var a in Animations)
                if (a.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase))
                    return false;

            Animations.Add(data);
            Save();
            return true;
        }

        /// <summary>Inserts a new animation, or replaces an existing entry
        /// with the same Name (case-insensitive). Returns false only if
        /// <paramref name="data"/> is invalid.</summary>
        public bool ReplaceOrAdd(AnimationData? data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return false;

            for (int i = 0; i < Animations.Count; i++)
            {
                if (Animations[i].Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Animations[i] = data;
                    Save();
                    return true;
                }
            }

            Animations.Add(data);
            Save();
            return true;
        }

        /// <summary>Removes an animation by name and persists. Returns false
        /// if no animation with that name exists.</summary>
        public bool Remove(string name)
        {
            for (int i = 0; i < Animations.Count; i++)
            {
                if (Animations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Animations.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Find an animation by name (case-insensitive). Null if
        /// not in the library.</summary>
        public AnimationData? GetByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var a in Animations)
                if (a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return a;
            return null;
        }

        // ── Built-in defaults ─────────────────────────────────────────────────

        private void MergeBuiltIns()
        {
            foreach (var seed in BuiltInAnimations())
            {
                bool exists = false;
                foreach (var existing in Animations)
                {
                    if (existing.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) Animations.Add(seed);
            }
        }

        /// <summary>Seed animations that ship with the app. Currently a single
        /// preset that reproduces the legacy Julia C orbit so the bus has a
        /// shipping demonstration. Phase 3 editor lets users build more.</summary>
        private static IEnumerable<AnimationData> BuiltInAnimations()
        {
            yield return new AnimationData
            {
                Name = "Julia C orbit",
                Category = "Built-in",
                Description = "Polar orbit of the Julia c constant — the classic Julia speed animation.",
                TargetFractalTypes = new List<FracturingFog.FractalType>
                {
                    FracturingFog.FractalType.Julia,
                },
                Tracks = new List<AnimationTrack>
                {
                    new AnimationTrack
                    {
                        ParamName = "JuliaC",
                        Mode = AnimationMode.Lissajous,
                        Min = 0.5,
                        Max = 0.5,
                        FrequencyHz = 0.0318,
                        Enabled = true,
                    },
                },
                Tags = new List<string> { "calm", "2D" },
            };
        }
    }
}
