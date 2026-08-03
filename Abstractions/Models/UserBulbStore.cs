// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserBulbStore.cs
//
// Singleton persistence for user-defined 3D bulb equations.  Each entry has a
// human-readable Name and the raw C# source body that UserBulbCalculator
// compiles.  Stored as JSON in %APPDATA%\FracturingFog\userbulbs.json.
//
// Mirrors UserEquationStore in spirit: lazy singleton, indented JSON,
// failures during load/save are non-fatal.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Assets;

namespace FracturingFog.Models
{
    public sealed class UserBulbEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// When true, this entry is surfaced as a first-class fractal type in
        /// the main fractal dropdown via <see cref="RegisteredFractalCatalog"/>.
        /// Defaults false; missing field in legacy JSON deserialises to false.
        /// </summary>
        public bool Promoted { get; set; }

        /// <summary>
        /// Optional multi-step chain. When non-empty, the runtime uses the
        /// chain in preference to <see cref="Source"/> — see
        /// <c>FractalParameters.UserBulbChain</c>. Null/empty on legacy
        /// single-source entries.
        /// </summary>
        public List<UserBulbChainStep>? Chain { get; set; }

        /// <summary>
        /// Render/view/animation settings captured when the equation was saved
        /// (axis mode, Julia, camera, lights, colour driver, render budget,
        /// FOV, Time, named params). Applied on load so switching between saved
        /// equations restores each one's own settings — not just its source.
        /// Null on legacy entries saved before this field existed (load then
        /// leaves the current settings untouched, matching old behaviour). The
        /// nested snapshot's own <see cref="UserBulbSnapshot.Entry"/> is unused
        /// here and left empty to avoid self-reference.
        /// </summary>
        public UserBulbSnapshot? Settings { get; set; }
    }

    public sealed class UserBulbStore
    {
        private static UserBulbStore? _instance;
        public static UserBulbStore Instance => _instance ??= new UserBulbStore();

        private UserBulbStore() { }

        public List<UserBulbEntry> Equations { get; } = new();

        private static string SettingsDir => AppDataPaths.Root;

        private static string EquationsFile =>
            Path.Combine(SettingsDir, "userbulbs.json");

        private static JsonSerializerOptions BuildJsonOptions() => new()
        {
            WriteIndented = true,
        };

        /// <summary>
        /// Options for the .fbulb snapshot envelope. Nullable knobs are
        /// elided on write so a snapshot only persists what the producer
        /// actually set, keeping files small + forward-compatible.
        /// </summary>
        private static JsonSerializerOptions BuildSnapshotOptions() => new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public void Load()
        {
            try
            {
                Equations.Clear();
                if (!File.Exists(EquationsFile))
                {
                    SeedDefaults();
                    Save();
                    return;
                }

                string json = File.ReadAllText(EquationsFile);
                var loaded = JsonSerializer.Deserialize<List<UserBulbEntry>>(json, BuildJsonOptions());
                if (loaded == null)
                {
                    SeedDefaults();
                    Save();
                    return;
                }

                foreach (var e in loaded)
                    if (e != null && !string.IsNullOrWhiteSpace(e.Name)) Equations.Add(e);

                if (Equations.Count == 0)
                {
                    SeedDefaults();
                    Save();
                }
                else
                {
                    // Pre-existing userbulbs.json may be missing newly-shipped
                    // built-ins (Phase B.3 hybrids) and/or still hold the
                    // pre-Phase-2b raw-C# built-in bodies. Merge/repair, then
                    // upgrade untouched built-ins to the safe DSL; persist if
                    // anything changed so the user sees it on next load too.
                    bool changed = TopUpBuiltins();
                    changed |= MigrateBuiltinsToDsl();
                    if (changed) Save();
                }
            }
            catch
            {
                Equations.Clear();
                SeedDefaults();
            }
        }

        // #27 Phase 2b — built-in preset bodies migrated from raw C# to the
        // safe DSL (SandboxBulbExpression). new Vec3 -> vec, Vec3.Fn/Math.Fn ->
        // lowercase builtins, member .X -> .x, `var`/`if` -> let/ternary. Each
        // built-in pins Compiler = Sandbox so it runs on the interpreter
        // regardless of the global default. Parity with the old C# math is
        // proven by SandboxBulbDslAuditTests. See [[project_usercode_surface_reduction]].
        internal const string DslSquare = "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c";
        internal const string DslBulb8 = "z^8 + c";
        internal const string DslBulb4 = "z^4 + c";
        internal const string DslSin = "sin(z)*1.5 + c";
        internal const string DslAbs8 = "abs(z)^8 + c";
        internal const string DslMandelbox = "spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c";
        internal const string DslCoshSin = "sin(z)*cosh(z) + c";
        internal const string DslBreathing = "z^(4 + 2*sin(t)) + c";
        internal const string DslFoldedAbsY = "absy(z)^8 + c";
        internal const string DslReflected =
            "let w = vec(abs(z.x), abs(z.y), z.z) in " +
            "vec(w.x*w.x - w.y*w.y - w.z*w.z, 2*w.x*w.y, 2*w.x*w.z) + c";
        internal const string DslHybridMbox = "(spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c)^8.0 + c";
        internal const string DslHybridMenger = "z^8.0 + c";
        internal const string DslKifsSinglePass =
            "// Single-pass fallback — chain form below carries fold/rot/scale.\n" +
            "// Needs DE Mode = Scalar KIFS (scale 3): the per-iteration rotation\n" +
            "// defeats the numerical-Jacobian DE.\n" +
            "let v0 = abs(z) in\n" +
            "let v1 = (v0.x - v0.y < 0 ? vec(v0.y, v0.x, v0.z) : v0) in\n" +
            "let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in\n" +
            "let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in\n" +
            "rot(v3, vec(0, 1, 0), 0.3) * 3.0 - vec(2, 2, 0)";
        internal const string DslQuatJulia =
            "// Switch Axis Mode -> Quat + Julia Mode on in the editor.\n" +
            "// Triplex squared map; c held constant by Julia mode.\n" +
            "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c";

        /// <summary>Fresh snapshot that pins the safe DSL compiler. Merges onto
        /// an existing snapshot when a built-in already carries settings.</summary>
        private static UserBulbSnapshot SandboxPin(UserBulbSnapshot? existing = null)
        {
            var s = existing ?? new UserBulbSnapshot();
            s.Compiler = UserBulbCompilerKind.Sandbox;
            return s;
        }

        private void SeedDefaults()
        {
            Equations.Add(new UserBulbEntry { Name = "Square triplex (z*z + c)",
                Source = DslSquare, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Mandelbulb p=8",
                Source = DslBulb8, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Mandelbulb p=4",
                Source = DslBulb4, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Sin-bulb",
                Source = DslSin, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Abs-bulb p=8",
                Source = DslAbs8, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Mandelbox",
                Source = DslMandelbox, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Cosh × Sin bulb",
                Source = DslCoshSin, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Animated breathing bulb (uses t)",
                Source = DslBreathing, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Folded abs-Y bulb",
                Source = DslFoldedAbsY, Settings = SandboxPin() });
            Equations.Add(new UserBulbEntry { Name = "Reflected triplex",
                Source = DslReflected, Settings = SandboxPin() });

            // Phase B.3 hybrid chains. Source kept as a single-pass fallback
            // for legacy loaders; Chain is the canonical form and overrides
            // Source at runtime.
            Equations.Add(new UserBulbEntry
            {
                Name = "Hybrid: Mandelbox + Mandelbulb",
                Source = DslHybridMbox,
                Chain = UserBulbChainPrimitives.MandelboxBulbHybrid(),
                Settings = SandboxPin(),
            });
            Equations.Add(new UserBulbEntry
            {
                Name = "Hybrid: Menger + Mandelbulb",
                Source = DslHybridMenger,
                Chain = UserBulbChainPrimitives.MengerBulbHybrid(),
                Settings = SandboxPin(),
            });

            // Wave 4.11 — pure KIFS folds + Quat-Julia preset.
            Equations.Add(new UserBulbEntry
            {
                Name = "Menger sponge step",
                Source = UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdMenger)!.Source,
                Settings = SandboxPin(),
            });
            Equations.Add(new UserBulbEntry
            {
                Name = "Sierpinski tetrahedron",
                Source = UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdSierpinski)!.Source,
                Settings = SandboxPin(),
            });
            Equations.Add(new UserBulbEntry
            {
                Name = "Kaleidoscopic IFS (fold + rot + scale)",
                Source = DslKifsSinglePass,
                Chain = UserBulbChainPrimitives.KaleidoscopicIfsChain(),
                Settings = SandboxPin(new UserBulbSnapshot
                {
                    KifsScale = UserBulbChainPrimitives.KaleidoscopicIfsScale,
                    CameraDistance = 3.0,
                    Iterations = 12,
                }),
            });
            Equations.Add(new UserBulbEntry
            {
                Name = "Quaternion Julia (Quat mode, set Julia c)",
                Source = DslQuatJulia,
                Settings = SandboxPin(),
            });
        }

        /// <summary>
        /// Appends any built-in entry not already present by name. Used to
        /// merge new built-ins (e.g. Phase B.3 hybrids) into a userbulbs.json
        /// that predates them. Also repairs hybrid chains shipped in earlier
        /// builds whose later steps read original pixel `z` instead of the
        /// prior fold output (rendered identical to a plain Mandelbulb).
        /// Returns true when at least one entry was added or repaired.
        /// </summary>
        private bool TopUpBuiltins()
        {
            bool changed = false;
            void Ensure(string name, Func<UserBulbEntry> factory)
            {
                if (GetByName(name) is not null) return;
                Equations.Add(factory());
                changed = true;
            }
            void Repair(string name, string priorOutputName, Func<List<UserBulbChainStep>> rebuild)
            {
                var entry = GetByName(name);
                if (entry?.Chain == null || entry.Chain.Count < 2) return;
                // Buggy first cut: step 1 produced <priorOutputName>, step 2
                // used `z` directly. Detect by absence of the prior name in
                // step 2's source.
                if (entry.Chain[1].Source.Contains(priorOutputName)) return;
                entry.Chain = rebuild();
                changed = true;
            }

            Ensure("Hybrid: Mandelbox + Mandelbulb", () => new UserBulbEntry
            {
                Name = "Hybrid: Mandelbox + Mandelbulb",
                Source = DslHybridMbox,
                Chain = UserBulbChainPrimitives.MandelboxBulbHybrid(),
                Settings = SandboxPin(),
            });
            Ensure("Hybrid: Menger + Mandelbulb", () => new UserBulbEntry
            {
                Name = "Hybrid: Menger + Mandelbulb",
                Source = DslHybridMenger,
                Chain = UserBulbChainPrimitives.MengerBulbHybrid(),
                Settings = SandboxPin(),
            });
            Ensure("Menger sponge step", () => new UserBulbEntry
            {
                Name = "Menger sponge step",
                Source = UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdMenger)!.Source,
                Settings = SandboxPin(),
            });
            Ensure("Sierpinski tetrahedron", () => new UserBulbEntry
            {
                Name = "Sierpinski tetrahedron",
                Source = UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdSierpinski)!.Source,
                Settings = SandboxPin(),
            });
            Ensure("Kaleidoscopic IFS (fold + rot + scale)", () => new UserBulbEntry
            {
                Name = "Kaleidoscopic IFS (fold + rot + scale)",
                Source = DslKifsSinglePass,
                Chain = UserBulbChainPrimitives.KaleidoscopicIfsChain(),
                Settings = SandboxPin(new UserBulbSnapshot
                {
                    KifsScale = UserBulbChainPrimitives.KaleidoscopicIfsScale,
                    CameraDistance = 3.0,
                    Iterations = 12,
                }),
            });
            Ensure("Quaternion Julia (Quat mode, set Julia c)", () => new UserBulbEntry
            {
                Name = "Quaternion Julia (Quat mode, set Julia c)",
                Source = DslQuatJulia,
                Settings = SandboxPin(),
            });
            Repair("Hybrid: Mandelbox + Mandelbulb",
                   UserBulbChainPrimitives.IdMandelbox,
                   UserBulbChainPrimitives.MandelboxBulbHybrid);
            Repair("Hybrid: Menger + Mandelbulb",
                   UserBulbChainPrimitives.IdMenger,
                   UserBulbChainPrimitives.MengerBulbHybrid);

            // Second repair pass: Menger hybrid shipped in an earlier build
            // composed bulb-pow without contracting the scale-3 fold output,
            // which escaped past bailout on iter 0 → solid-colour render.
            // Detect by absence of the contraction factor `* 0.3` in step 2
            // and re-seed from the current factory.
            {
                var entry = GetByName("Hybrid: Menger + Mandelbulb");
                if (entry?.Chain != null && entry.Chain.Count >= 2
                    && !entry.Chain[1].Source.Contains("* 0.3"))
                {
                    entry.Chain = UserBulbChainPrimitives.MengerBulbHybrid();
                    changed = true;
                }
            }

            // Kaleidoscopic-IFS repair: earlier builds shipped a Sierpinski-fold
            // chain with per-iteration rotation, which is invisible under the
            // numerical DE and carries no KIFS-scale setting → renders as a
            // sparse speck. Re-seed to the Menger-fold chain and attach the
            // Scalar-KIFS settings. Detect by a chain lacking the KIFS-scale
            // setting or still using the old rotation-of-Sierpinski step.
            {
                var entry = GetByName("Kaleidoscopic IFS (fold + rot + scale)");
                bool needsFix = entry?.Chain is { Count: > 0 }
                    && (entry.Settings?.KifsScale is not > 0.0
                        || (entry.Chain.Count > 1 && entry.Chain[1].Source.Contains(UserBulbChainPrimitives.IdSierpinski)));
                if (needsFix)
                {
                    entry!.Chain = UserBulbChainPrimitives.KaleidoscopicIfsChain();
                    entry.Settings = new UserBulbSnapshot
                    {
                        KifsScale = UserBulbChainPrimitives.KaleidoscopicIfsScale,
                        CameraDistance = 3.0,
                        Iterations = 12,
                    };
                    changed = true;
                }
            }

            // Un-corruption pass: a prior build overwrote this built-in with a
            // Hamilton `z*z + c` square plus a forced Julia-c / Quat Settings
            // snapshot that raymarched to a solid ball, and persisted it into
            // userbulbs.json — so the damage survived rebuilds. Restore the
            // original Vec3-triplex source with no forced Settings (the user
            // drives Axis Mode = Quat + Julia c in the editor, as before).
            // Self-limiting: after the reset the source no longer matches and
            // Settings is null, so it never fires again. Scoped to the built-in
            // name only, so user-authored equations are never touched.
            {
                var q = GetByName("Quaternion Julia (Quat mode, set Julia c)");
                // Detect the past corruption by its Hamilton `z*z + c` square
                // (the correct triplex uses z.x*z.x…). #27 Phase 2b: the reset
                // target is now the DSL triplex with the Sandbox compiler pin —
                // the old `Settings != null` clause was dropped because a
                // legitimate entry now always carries a pin snapshot.
                if (q != null && q.Source.Contains("z*z + c"))
                {
                    q.Source = DslQuatJulia;
                    q.Chain = null;
                    q.Settings = SandboxPin();
                    changed = true;
                }
            }
            return changed;
        }

        // #27 Phase 2b — single-source built-ins whose stored body may still be
        // the pre-migration raw C#. Keyed by the exact shipped C# so a user's
        // own edit (any other text) is preserved, honouring the read-only
        // built-in contract [[feedback_no_save_over_examples]]. Chain-bearing
        // built-ins re-seed to DSL on a fresh install; a stored raw-C# body a
        // user never edited that predates DSL no longer runs (Phase 3 removed
        // the Roslyn path) — the user re-picks the DSL built-in.
        private static readonly (string Name, string OldCs, string NewDsl)[] _dslMigrations =
        {
            ("Square triplex (z*z + c)",
             "return new Vec3(\n    z.X*z.X - z.Y*z.Y - z.Z*z.Z,\n    2*z.X*z.Y,\n    2*z.X*z.Z) + c;", DslSquare),
            ("Mandelbulb p=8", "return Vec3.Pow(z, 8) + c;", DslBulb8),
            ("Mandelbulb p=4", "return Vec3.Pow(z, 4) + c;", DslBulb4),
            ("Sin-bulb", "return Vec3.Sin(z) * 1.5 + c;", DslSin),
            ("Abs-bulb p=8", "return Vec3.Pow(Vec3.Abs(z), 8) + c;", DslAbs8),
            ("Mandelbox",
             "var v = Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0);\nreturn v * 2.0 + c;", DslMandelbox),
            ("Cosh × Sin bulb", "return Vec3.Sin(z) * Vec3.Cosh(z) + c;", DslCoshSin),
            ("Animated breathing bulb (uses t)", "return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;", DslBreathing),
            ("Folded abs-Y bulb", "return Vec3.Pow(Vec3.AbsY(z), 8) + c;", DslFoldedAbsY),
            ("Reflected triplex",
             "var w = new Vec3(Math.Abs(z.X), Math.Abs(z.Y), z.Z);\nreturn new Vec3(w.X*w.X - w.Y*w.Y - w.Z*w.Z, 2*w.X*w.Y, 2*w.X*w.Z) + c;", DslReflected),
        };

        /// <summary>Upgrade untouched built-in bodies from raw C# to the safe
        /// DSL and pin the Sandbox compiler. Only rewrites an entry whose stored
        /// source still exactly matches the shipped C# (or the new DSL awaiting
        /// a pin) — a user edit differs and is left alone. Idempotent: skips
        /// entries already pinned to Sandbox. Returns true if anything changed.</summary>
        private bool MigrateBuiltinsToDsl()
        {
            bool changed = false;
            foreach (var (name, oldCs, newDsl) in _dslMigrations)
            {
                var e = GetByName(name);
                if (e == null) continue;
                if (e.Settings?.Compiler == UserBulbCompilerKind.Sandbox) continue;
                if (!SourcesEqual(e.Source, oldCs) && !SourcesEqual(e.Source, newDsl)) continue;
                e.Source = newDsl;
                e.Settings = SandboxPin(e.Settings);
                changed = true;
            }
            return changed;
        }

        private static bool SourcesEqual(string? a, string? b)
            => string.Equals(
                (a ?? string.Empty).Replace("\r\n", "\n").Trim(),
                (b ?? string.Empty).Replace("\r\n", "\n").Trim(),
                StringComparison.Ordinal);

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(Equations, BuildJsonOptions());
                AtomicFile.WriteAllText(EquationsFile, json);
            }
            catch
            {
                // Non-fatal.
            }
        }

        /// <summary>
        /// Inserts or replaces an entry by Name (case-insensitive). Returns the
        /// stored entry, or null if name is blank. Chain is cloned per-step
        /// so the caller can keep mutating its own list afterwards; null/empty
        /// chain clears any prior chain on a replaced entry.
        /// </summary>
        public UserBulbEntry? SaveEquation(string name, string source, IReadOnlyList<UserBulbChainStep>? chain = null, UserBulbSnapshot? settings = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            List<UserBulbChainStep>? chainCopy = null;
            if (chain != null && chain.Count > 0)
            {
                chainCopy = new List<UserBulbChainStep>(chain.Count);
                foreach (var s in chain) chainCopy.Add(s.Clone());
            }

            for (int i = 0; i < Equations.Count; i++)
            {
                if (Equations[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    Equations[i].Source = source ?? string.Empty;
                    Equations[i].Chain = chainCopy;
                    Equations[i].Settings = settings;
                    Save();
                    return Equations[i];
                }
            }

            var entry = new UserBulbEntry { Name = name, Source = source ?? string.Empty, Chain = chainCopy, Settings = settings };
            Equations.Add(entry);
            Save();
            return entry;
        }

        /// <summary>
        /// Sets the <see cref="UserBulbEntry.Promoted"/> flag on the named
        /// entry and persists. Returns true when the entry exists and state
        /// changed; false when no such entry or already in target state.
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

        public UserBulbEntry? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var e in Equations)
                if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>Export one entry to a .fbulb JSON file (bare entry only —
        /// no runtime knobs). Retained for legacy callers; new code should
        /// build a <see cref="UserBulbSnapshot"/> and call
        /// <see cref="ExportSnapshot"/> to also capture axis mode / Julia /
        /// camera / lights / colour / view.</summary>
        public bool ExportEntry(string name, string filePath)
        {
            var entry = GetByName(name);
            if (entry == null) return false;
            try
            {
                File.WriteAllText(filePath, JsonSerializer.Serialize(entry, BuildJsonOptions()));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Import a bare .fbulb entry JSON file. Renames on collision
        /// (suffix N). Snapshot-aware import lives in
        /// <see cref="ImportSnapshot"/>.</summary>
        public UserBulbEntry? ImportEntry(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var entry = JsonSerializer.Deserialize<UserBulbEntry>(json, BuildJsonOptions());
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return null;
                MergeImportedEntry(entry);
                return entry;
            }
            catch { return null; }
        }

        /// <summary>
        /// Write a full snapshot (entry + runtime knobs) as .fbulb JSON.
        /// Returns false on I/O error or null snapshot. Caller owns building
        /// the snapshot from the live FractalParameters; the store keeps no
        /// hidden state.
        /// </summary>
        public bool ExportSnapshot(UserBulbSnapshot snapshot, string filePath)
        {
            if (snapshot is null) return false;
            try
            {
                File.WriteAllText(filePath,
                    JsonSerializer.Serialize(snapshot, BuildSnapshotOptions()));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Read a .fbulb file and import its first entry. Convenience wrapper
        /// over <see cref="ImportSnapshots"/> for callers that only care about
        /// one entry; note a multi-entry file still imports in full. Returns
        /// null on parse failure or when no element carried an entry name.
        /// </summary>
        public UserBulbSnapshot? ImportSnapshot(string filePath)
        {
            var all = ImportSnapshots(filePath);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// Read a .fbulb file and import every entry it holds. Recognises the
        /// Wave 4.13 snapshot envelope (Version + Entry + nullable knobs),
        /// pre-4.13 bare-entry JSON (legacy format produced by
        /// <see cref="ExportEntry"/>) — the latter yields a snapshot with only
        /// Entry populated — and a JSON array of either form. Each entry is
        /// renamed on name collision before being added, and entries merge in
        /// file order. Returns the imported snapshots, empty on parse failure
        /// or when no element carried an entry name.
        /// </summary>
        public IReadOnlyList<UserBulbSnapshot> ImportSnapshots(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var imported = new List<UserBulbSnapshot>();
                foreach (var element in AssetJsonFile.SplitEntries(json))
                {
                    var snapshot = TryParseSnapshot(element);
                    if (snapshot?.Entry is null || string.IsNullOrWhiteSpace(snapshot.Entry.Name))
                        continue;
                    MergeImportedEntry(snapshot.Entry);
                    imported.Add(snapshot);
                }
                return imported;
            }
            catch { return Array.Empty<UserBulbSnapshot>(); }
        }

        private static UserBulbSnapshot? TryParseSnapshot(string json)
        {
            // Peek root shape: snapshot envelopes carry a "Version" property;
            // bare entries do not. Cheaper than two failed full deserialises.
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("Version", out _) && root.TryGetProperty("Entry", out _))
                return JsonSerializer.Deserialize<UserBulbSnapshot>(json, BuildSnapshotOptions());

            // Legacy: bare UserBulbEntry JSON. Wrap in a snapshot so callers
            // get one return shape.
            var entry = JsonSerializer.Deserialize<UserBulbEntry>(json, BuildJsonOptions());
            if (entry is null) return null;
            return new UserBulbSnapshot
            {
                Version = 0, // 0 = legacy, distinguishable from 1 = envelope.
                Entry = entry,
            };
        }

        private void MergeImportedEntry(UserBulbEntry entry)
        {
            string baseName = entry.Name;
            int suffix = 1;
            while (GetByName(entry.Name) != null)
            {
                entry.Name = $"{baseName} ({suffix++})";
            }
            Equations.Add(entry);
            Save();
        }
    }
}
