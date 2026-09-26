// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/AnimationLibrary.cs
//
// Singleton library of user-defined animation assets, persisted to JSON in
// %APPDATA%\FracturingFog\animations.json.
//
// Mirrors UserColorThemeLibrary / FractalRegionLibrary:
//   * Singleton, lazy-initialised on first access.
//   * System.Text.Json with indented output for human-editable files.
//   * Failures during load/save are non-fatal. #964: load is per-entry — an
//     entry this build can't read (e.g. a FractalType from a newer/other
//     build) is skipped but kept verbatim and written back on Save, and an
//     unparseable file is backed up before anything can overwrite it.
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

        // #964/#966 — entries this build could not deserialize, kept verbatim so
        // Save round-trips them instead of silently deleting the user's data.
        private readonly TolerantJsonList<AnimationData> _persisted = new();

        /// <summary>Number of entries in the file this build could not read (kept
        /// on disk untouched, not shown). Non-zero after loading a file written by
        /// a newer or other-branch build.</summary>
        public int UnreadableCount => _persisted.UnreadableCount;

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
            Animations.Clear();
            foreach (var a in _persisted.LoadFile(AnimationsFile, BuildJsonOptions()))
                if (!string.IsNullOrWhiteSpace(a.Name)) Animations.Add(a);
            MergeBuiltIns();
        }

        /// <summary>Persists the current <see cref="Animations"/> list to disk.</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = _persisted.Serialize(Animations, BuildJsonOptions(), a => a.Name);
                AtomicFile.WriteAllText(AnimationsFile, json);
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

            // #632 (Renderer C2) — precision-sweep convergence. Ramps the low
            // tier up the ladder Float→QuadDouble while a Hold track pins the
            // reference tier at QuadDouble, so the divergence field dims toward
            // black as the low tier catches up to the reference. One "frame per
            // tier step" falls out of the enum animator's rounding at this Min/
            // Max span; the per-pixel rate of convergence is the image. Slow
            // FrequencyHz — every tick re-iterates the fractal at both tiers.
            yield return new AnimationData
            {
                Name = "Precision convergence sweep",
                Category = "Built-in",
                Description = "Ramps the low precision tier Float→QuadDouble against a "
                            + "QuadDouble reference — the fragility field dims as each "
                            + "tier converges. Author on a deep-zoom PrecisionField view.",
                TargetFractalTypes = new List<FracturingFog.FractalType>
                {
                    FracturingFog.FractalType.PrecisionField,
                },
                Tracks = new List<AnimationTrack>
                {
                    new AnimationTrack
                    {
                        ParamName = "PrecisionLowTier",
                        Mode = AnimationMode.Linear,   // sawtooth ramp 0 → 3, wraps
                        Min = 0,
                        Max = 3,
                        FrequencyHz = 0.05,            // ~20 s per full ladder sweep
                        Enabled = true,
                    },
                    new AnimationTrack
                    {
                        ParamName = "PrecisionHighTier",
                        Mode = AnimationMode.Hold,     // pin the reference at QuadDouble
                        Min = 3,
                        Max = 3,
                        FrequencyHz = 0.0,
                        Enabled = true,
                    },
                },
                Tags = new List<string> { "experimental", "2D", "precision" },
            };

            // #895 — the Indra's Pearls marquee. Walk the Maskit parameter μ along
            // the slice (μ real triangle-sweep across cusp groups) while holding
            // the imaginary part just inside the boundary, so the two-generator
            // group is seen "degenerating into a limit curve" and reforming. Best
            // on a Maskit-family view in CurveTrace render mode.
            yield return new AnimationData
            {
                Name = "Maskit slice sweep",
                Category = "Built-in",
                Description = "Sweeps the Maskit parameter μ along the slice — the "
                            + "group degenerates through successive cusp limit curves "
                            + "and reforms. Author on an Indra's Pearls (Maskit family) "
                            + "view; CurveTrace render mode gives the crisp line look.",
                TargetFractalTypes = new List<FracturingFog.FractalType>
                {
                    FracturingFog.FractalType.IndrasPearls,
                },
                Tracks = new List<AnimationTrack>
                {
                    new AnimationTrack
                    {
                        ParamName = "IndrasMaskitMuRe",
                        Mode = AnimationMode.Triangle,  // walk μ real across the slice
                        Min = -1.0,
                        Max = 1.0,
                        FrequencyHz = 0.03,             // ~33 s per full there-and-back
                        Enabled = true,
                    },
                    new AnimationTrack
                    {
                        ParamName = "IndrasMaskitMuIm",
                        Mode = AnimationMode.Hold,      // just inside the boundary
                        Min = 1.9,
                        Max = 1.9,
                        FrequencyHz = 0.0,
                        Enabled = true,
                    },
                },
                Tags = new List<string> { "experimental", "2D", "indras", "kleinian" },
            };

            // #911 S1 — parabolic implosion (Tier A, naïve). Circle the Julia
            // parameter c around a parabolic c₀ (c = c₀ + εe^{iθ}) via the Complex
            // polar/Lissajous sweep centred on c₀; the Julia set discontinuously
            // reorganises — the explosion. Pair each with its "Parabolic c = …"
            // built-in region. NOT the faithful Lavaurs limit (Tier C) — a
            // sequence of ordinary perturbed Julia sets. See
            // Docs/Technical/Parabolic-Implosion-DesignPlan.md.
            foreach (var (name, cx, cy, tag) in new[]
            {
                ("Parabolic implosion (c=1/4)",     0.25,   0.0,    "c=1/4"),
                ("Parabolic implosion (c=-3/4)",   -0.75,   0.0,    "c=-3/4"),
                ("Parabolic implosion (1/3 bulb)", -0.125,  0.6495, "1/3-bulb"),
            })
            {
                yield return new AnimationData
                {
                    Name = name,
                    Category = "Built-in",
                    Description = "Naïve parabolic implosion: circles the Julia c around the "
                                + "parabolic parameter c₀ = (" + cx.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + ", " + cy.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") at a small "
                                + "radius; the Julia set reorganises discontinuously (the explosion). "
                                + "Author on the matching 'Parabolic c = …' Julia region. Not the "
                                + "exact Lavaurs limit — a perturbed-Julia sequence (Tier A).",
                    TargetFractalTypes = new List<FracturingFog.FractalType>
                    {
                        FracturingFog.FractalType.Julia,
                    },
                    Tracks = new List<AnimationTrack>
                    {
                        new AnimationTrack
                        {
                            ParamName = "JuliaC",
                            Mode = AnimationMode.Lissajous,   // c = c₀ + ε·e^{iθ}
                            Min = 0.03, Max = 0.03,           // fixed radius ε
                            CenterX = cx, CenterY = cy,       // circle centred on c₀
                            FrequencyHz = 0.05,               // ~20 s per full loop
                            Enabled = true,
                        },
                    },
                    Tags = new List<string> { "experimental", "2D", "parabolic", "julia", tag },
                };
            }

            // #920 — parabolic implosion (FAITHFUL, Lavaurs limit) for each parabolic
            // root p/q. Sweeps the internal angle θ along the main cardioid boundary
            // toward the p/q root (c(θ) = e^{2πiθ}/2 − e^{4πiθ}/4, multiplier e^{2πiθ};
            // the p/q root IS the cardioid point c(p/q)); as θ → p/q the near-parabolic
            // Julia set blooms period-q satellite cascades — the faithful implosion
            // (J(f_{c(θ)}) → J(g_α), Lavaurs's theorem), the rigorous counterpart of the
            // naïve Lissajous circle above. A SECOND track ramps EscapeIterationScale in
            // lock-step (both Triangle, same FrequencyHz, no phase offset), so shallow
            // frames stay fast and the deep frames near the root get the iterations the
            // crawl needs. Author on the matching 'Parabolic implosion …'
            // region. See Docs/Technical/Parabolic-Implosion-DesignPlan.md.
            //   thetaFar = shallow start, thetaNear = deepest approach to the p/q root.
            foreach (var (name, thetaFar, thetaNear, tag) in new[]
            {
                ("Parabolic implosion (cardioid, c=1/4)",       0.140, 0.045, "c=1/4"),   // period-1 cusp
                ("Parabolic implosion (cardioid, c=-3/4)",      0.420, 0.460, "c=-3/4"),  // period-2, θ→1/2
                ("Parabolic implosion (cardioid, 1/3 bulb)",    0.290, 0.300, "1/3-bulb"),// period-3, θ→1/3
                ("Parabolic implosion (cardioid, 1/4 bulb)",    0.190, 0.225, "1/4-bulb"),// period-4, θ→1/4
                ("Parabolic implosion (cardioid, 2/5 bulb)",    0.350, 0.385, "2/5-bulb"),// period-5, θ→2/5
            })
            {
                yield return new AnimationData
                {
                    Name = name,
                    Category = "Built-in",
                    Description = "Parabolic implosion (Lavaurs limit): sweeps the Julia "
                                + "c along the cardioid boundary toward the parabolic root (internal "
                                + "angle θ → " + tag + "); the near-parabolic Julia set blooms satellite "
                                + "spirals — the implosion cascade. The rigorous counterpart of the naïve "
                                + "'Parabolic implosion (" + tag + ")'. A synced iteration ramp keeps the "
                                + "deep frames crisp. Author on the matching high-iteration 'Parabolic "
                                + "implosion …' region.",
                    TargetFractalTypes = new List<FracturingFog.FractalType>
                    {
                        FracturingFog.FractalType.Julia,
                    },
                    Tracks = new List<AnimationTrack>
                    {
                        new AnimationTrack
                        {
                            ParamName = "JuliaC",
                            Mode = AnimationMode.CardioidApproach,   // c(θ) on the cardioid, θ → root
                            Min = thetaFar,                          // shallow near-parabolic (Phase→0)
                            Max = thetaNear,                         // deepest approach to the root (Phase→π)
                            FrequencyHz = 0.04,                      // ~25 s per there-and-back
                            Enabled = true,
                        },
                        new AnimationTrack
                        {
                            ParamName = "EscapeIterationScale",
                            Mode = AnimationMode.Triangle,           // in lock-step with the θ sweep
                            Min = 1.0,                               // shallow → base iterations
                            Max = 6.0,                               // deep → 6× iterations
                            FrequencyHz = 0.04,                      // MUST match the JuliaC track
                            Enabled = true,
                        },
                    },
                    Tags = new List<string> { "experimental", "2D", "parabolic", "julia", "cardioid", tag },
                };
            }

            // #920 user-picked p/q — a single GENERAL faithful-implosion animation that
            // works for ANY parabolic root the user selects. It animates the approach depth
            // (FaithfulImplosionApproach → 0) while the Julia c is driven from the chosen
            // p/q root by FaithfulImplosion mode (EffectiveJuliaC). Iterations auto-ramp
            // from the approach depth in the render host, so no separate iteration track is
            // needed. Author on the 'Parabolic implosion — pick p/q' region and
            // set p/q in the Julia panel.
            yield return new AnimationData
            {
                Name = "Parabolic implosion (cardioid, pick p/q)",
                Category = "Built-in",
                Description = "Parabolic implosion at a USER-chosen parabolic root: set p/q in the "
                            + "Julia panel (FaithfulImplosion mode), then this sweeps the approach "
                            + "depth toward the root (θ → p/q) so the near-parabolic Julia set blooms "
                            + "its period-q cascade. Iterations auto-ramp as the approach deepens. "
                            + "Works for any p/q — the general form of the per-root presets.",
                TargetFractalTypes = new List<FracturingFog.FractalType>
                {
                    FracturingFog.FractalType.Julia,
                },
                Tracks = new List<AnimationTrack>
                {
                    new AnimationTrack
                    {
                        ParamName = "FaithfulImplosionApproach",
                        Mode = AnimationMode.Triangle,   // sweep the θ-gap in and out
                        Min = 0.03,                      // deepest approach to the root
                        Max = 0.14,                      // shallow near-parabolic
                        FrequencyHz = 0.04,              // ~25 s per there-and-back
                        Enabled = true,
                    },
                },
                Tags = new List<string> { "experimental", "2D", "parabolic", "julia", "cardioid", "user-pq" },
            };
        }
    }
}
