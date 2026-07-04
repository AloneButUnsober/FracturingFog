// Engine/Models/SceneLibrary.cs
//
// Scene Engine Roadmap — Phase S4: singleton library of Scene assets, persisted
// to JSON at %APPDATA%\FracturingFog\scenes.json.
//
// Mirrors AnimationLibrary / FractalRegionLibrary / UserColorThemeLibrary:
//   * Singleton, lazy-initialised on first access.
//   * System.Text.Json, indented, enums-as-string — human-editable files.
//   * Load/Save failures are non-fatal — user loses custom scenes rather than
//     crashing the app.
//   * Built-in demo scenes ship in-source via BuiltInScenes() and are merged
//     into the library on first load.
//
// The Asset Manager node type and the S5 editor are the consumers.

using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Render;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    /// <summary>Singleton library of saved <see cref="SceneData"/> entries.
    /// Scene Engine Roadmap Phase S4 deliverable.</summary>
    public sealed class SceneLibrary
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        private static SceneLibrary? _instance;

        public static SceneLibrary Instance
            => _instance ??= new SceneLibrary();

        private SceneLibrary() { }

        // ── Storage paths ─────────────────────────────────────────────────────

        private static string SettingsDir => AppDataPaths.Root;

        private static string ScenesFile =>
            Path.Combine(SettingsDir, "scenes.json");

        // ── In-memory contents ────────────────────────────────────────────────

        /// <summary>Mutable list of scenes. Don't add/remove directly — use
        /// <see cref="Add"/> / <see cref="Remove"/> / <see cref="ReplaceOrAdd"/>
        /// so writes are persisted.</summary>
        public List<SceneData> Scenes { get; } = new();

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

        /// <summary>Loads scenes from disk. Safe to call when the file is
        /// missing or corrupt — the in-memory list ends up empty (plus any
        /// built-in demo scenes merged on first run).</summary>
        public void Load()
        {
            try
            {
                Scenes.Clear();
                if (File.Exists(ScenesFile))
                {
                    string json = File.ReadAllText(ScenesFile);
                    var loaded = JsonSerializer.Deserialize<List<SceneData>>(json, BuildJsonOptions());
                    if (loaded != null)
                    {
                        foreach (var s in loaded)
                            if (s != null) Scenes.Add(s);
                    }
                }

                MergeBuiltIns();
            }
            catch
            {
                Scenes.Clear();
                MergeBuiltIns();
            }
        }

        /// <summary>Persists the current <see cref="Scenes"/> list to disk.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Scenes, BuildJsonOptions());
                AtomicFile.WriteAllText(ScenesFile, json);
            }
            catch
            {
                // Non-fatal — user loses any unsaved custom scenes.
            }
        }

        // ── Mutators ──────────────────────────────────────────────────────────

        /// <summary>Adds a new scene and persists. Returns false if a scene with
        /// the same Name already exists (case-insensitive) or if
        /// <paramref name="data"/> is invalid.</summary>
        public bool Add(SceneData? data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return false;

            foreach (var s in Scenes)
                if (s.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase))
                    return false;

            Scenes.Add(data);
            Save();
            return true;
        }

        /// <summary>Inserts a new scene, or replaces an existing entry with the
        /// same Name (case-insensitive). Returns false only if
        /// <paramref name="data"/> is invalid.</summary>
        public bool ReplaceOrAdd(SceneData? data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Name)) return false;

            for (int i = 0; i < Scenes.Count; i++)
            {
                if (Scenes[i].Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Scenes[i] = data;
                    Save();
                    return true;
                }
            }

            Scenes.Add(data);
            Save();
            return true;
        }

        /// <summary>Removes a scene by name and persists. Returns false if no
        /// scene with that name exists.</summary>
        public bool Remove(string name)
        {
            for (int i = 0; i < Scenes.Count; i++)
            {
                if (Scenes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Scenes.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Find a scene by name (case-insensitive). Null if not in the
        /// library.</summary>
        public SceneData? GetByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var s in Scenes)
                if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        // ── Built-in defaults ─────────────────────────────────────────────────

        private void MergeBuiltIns()
        {
            foreach (var seed in BuiltInScenes())
            {
                bool exists = false;
                foreach (var existing in Scenes)
                {
                    if (existing.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) Scenes.Add(seed);
            }
        }

        /// <summary>Demo scenes that ship with the app. Deliberately
        /// self-contained: they render a fractal type directly (empty
        /// <see cref="SceneShot.RegionName"/>) so they need no region-library
        /// lookup and can never break from a renamed region. They exist to show
        /// the S3 camera track working end-to-end the moment playback lands
        /// (S6). The S5 editor lets users build richer, region-backed scenes.</summary>
        private static IEnumerable<SceneData> BuiltInScenes()
        {
            // A slow full orbit of the Mandelbulb — the headline S3 payoff.
            yield return new SceneData
            {
                Name = "Mandelbulb Orbit",
                Category = "Built-in",
                Description = "A calm 360° orbit around the Mandelbulb — the built-in " +
                              "demonstration of the keyframed scene camera.",
                Tags = new List<string> { "demo", "3D", "calm" },
                Shots = new List<SceneShot>
                {
                    new SceneShot
                    {
                        Name = "Orbit",
                        FractalType = FracturingFog.FractalType.Mandelbulb,
                        DurationSeconds = 20.0,
                        Transition = SceneTransitionKind.Cut,
                        Camera = OrbitTrack(distance: 2.6, turns: 1, seconds: 20.0, phi: 0.35),
                    },
                },
            };

            // Two shots, a cross-fade between two 3D types — shows shot
            // sequencing + transitions, still region-free.
            yield return new SceneData
            {
                Name = "Bulb → Box",
                Category = "Built-in",
                Description = "Mandelbulb orbit cross-fading into a Mandelbox orbit — " +
                              "the built-in demonstration of multi-shot scene sequencing.",
                Tags = new List<string> { "demo", "3D" },
                Shots = new List<SceneShot>
                {
                    new SceneShot
                    {
                        Name = "Bulb",
                        FractalType = FracturingFog.FractalType.Mandelbulb,
                        DurationSeconds = 12.0,
                        Transition = SceneTransitionKind.Cut,
                        Camera = OrbitTrack(distance: 2.6, turns: 1, seconds: 12.0, phi: 0.3),
                    },
                    new SceneShot
                    {
                        Name = "Box",
                        FractalType = FracturingFog.FractalType.Mandelbox,
                        DurationSeconds = 12.0,
                        Transition = SceneTransitionKind.Crossfade,
                        TransitionSeconds = 2.0,
                        Camera = OrbitTrack(distance: 8.0, turns: 1, seconds: 12.0, phi: 0.25),
                    },
                },
            };

            // A Mandelbulb orbit that fades up out of near-black and settles to a
            // neutral exposure — the built-in demonstration of an S8 scene-wide
            // global track (exposure) riding over the shot's own look.
            yield return new SceneData
            {
                Name = "Exposure Ramp",
                Category = "Built-in",
                Description = "A Mandelbulb orbit whose scene-wide exposure ramps up " +
                              "out of near-black and settles — the built-in demonstration " +
                              "of a global (scene-wide) post track.",
                Tags = new List<string> { "demo", "3D", "global-track" },
                Shots = new List<SceneShot>
                {
                    new SceneShot
                    {
                        Name = "Rise",
                        FractalType = FracturingFog.FractalType.Mandelbulb,
                        DurationSeconds = 16.0,
                        Transition = SceneTransitionKind.Cut,
                        Camera = OrbitTrack(distance: 2.6, turns: 1, seconds: 16.0, phi: 0.32),
                    },
                },
                GlobalTracks = new List<SceneGlobalTrack>
                {
                    new SceneGlobalTrack
                    {
                        Target = SceneGlobalTarget.Exposure,
                        Interpolation = CameraInterpolation.Linear,
                        Keys =
                        {
                            new SceneGlobalKey(0.0, 0.15, CameraEase.EaseInOut),
                            new SceneGlobalKey(6.0, 1.0),
                            new SceneGlobalKey(16.0, 1.0),
                        },
                    },
                },
            };
        }

        /// <summary>A closed orbit: azimuth (theta) sweeps <paramref name="turns"/>
        /// full turns over <paramref name="seconds"/> at fixed distance and
        /// elevation. Keys at start / quarter / half / three-quarter / end so
        /// the spline follows a clean circle.</summary>
        private static CameraTrack OrbitTrack(double distance, int turns, double seconds, double phi)
        {
            const double twoPi = 2.0 * global::System.Math.PI;
            var track = new CameraTrack { Interpolation = CameraInterpolation.CatmullRom };
            const int steps = 4;
            for (int i = 0; i <= steps; i++)
            {
                double frac = (double)i / steps;
                track.Add(new CameraKey(
                    frac * seconds,
                    new CameraState(distance, frac * turns * twoPi, phi)));
            }
            return track;
        }
    }
}
