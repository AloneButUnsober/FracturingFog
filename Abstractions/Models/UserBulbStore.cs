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
using FracturingFog.Abstractions;

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
                else if (TopUpBuiltins())
                {
                    // Pre-existing userbulbs.json was missing newly-shipped
                    // built-ins (e.g. Phase B.3 hybrids) — persist the merge
                    // so the user sees them on next load too.
                    Save();
                }
            }
            catch
            {
                Equations.Clear();
                SeedDefaults();
            }
        }

        private void SeedDefaults()
        {
            Equations.Add(new UserBulbEntry { Name = "Square triplex (z*z + c)",
                Source = "return new Vec3(\n    z.X*z.X - z.Y*z.Y - z.Z*z.Z,\n    2*z.X*z.Y,\n    2*z.X*z.Z) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Mandelbulb p=8",
                Source = "return Vec3.Pow(z, 8) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Mandelbulb p=4",
                Source = "return Vec3.Pow(z, 4) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Sin-bulb",
                Source = "return Vec3.Sin(z) * 1.5 + c;" });
            Equations.Add(new UserBulbEntry { Name = "Abs-bulb p=8",
                Source = "return Vec3.Pow(Vec3.Abs(z), 8) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Mandelbox",
                Source = "var v = Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0);\nreturn v * 2.0 + c;" });
            Equations.Add(new UserBulbEntry { Name = "Cosh × Sin bulb",
                Source = "return Vec3.Sin(z) * Vec3.Cosh(z) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Animated breathing bulb (uses t)",
                Source = "return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Folded abs-Y bulb",
                Source = "return Vec3.Pow(Vec3.AbsY(z), 8) + c;" });
            Equations.Add(new UserBulbEntry { Name = "Reflected triplex",
                Source = "var w = new Vec3(Math.Abs(z.X), Math.Abs(z.Y), z.Z);\nreturn new Vec3(w.X*w.X - w.Y*w.Y - w.Z*w.Z, 2*w.X*w.Y, 2*w.X*w.Z) + c;" });

            // Phase B.3 hybrid chains. Source kept as a single-pass fallback
            // for legacy loaders; Chain is the canonical form and overrides
            // Source at runtime.
            Equations.Add(new UserBulbEntry
            {
                Name = "Hybrid: Mandelbox + Mandelbulb",
                Source = "return Vec3.Pow(Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0) * 2.0 + c, 8.0) + c;",
                Chain = UserBulbChainPrimitives.MandelboxBulbHybrid(),
            });
            Equations.Add(new UserBulbEntry
            {
                Name = "Hybrid: Menger + Mandelbulb",
                Source = "return Vec3.Pow(z, 8.0) + c;",
                Chain = UserBulbChainPrimitives.MengerBulbHybrid(),
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
                Source = "return Vec3.Pow(Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0) * 2.0 + c, 8.0) + c;",
                Chain = UserBulbChainPrimitives.MandelboxBulbHybrid(),
            });
            Ensure("Hybrid: Menger + Mandelbulb", () => new UserBulbEntry
            {
                Name = "Hybrid: Menger + Mandelbulb",
                Source = "return Vec3.Pow(z, 8.0) + c;",
                Chain = UserBulbChainPrimitives.MengerBulbHybrid(),
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
            return changed;
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

        /// <summary>
        /// Inserts or replaces an entry by Name (case-insensitive). Returns the
        /// stored entry, or null if name is blank. Chain is cloned per-step
        /// so the caller can keep mutating its own list afterwards; null/empty
        /// chain clears any prior chain on a replaced entry.
        /// </summary>
        public UserBulbEntry? SaveEquation(string name, string source, IReadOnlyList<UserBulbChainStep>? chain = null)
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
                    Save();
                    return Equations[i];
                }
            }

            var entry = new UserBulbEntry { Name = name, Source = source ?? string.Empty, Chain = chainCopy };
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

        /// <summary>Export one entry to a .fbulb JSON file.</summary>
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

        /// <summary>Import a .fbulb JSON file. Renames on collision (suffix N).</summary>
        public UserBulbEntry? ImportEntry(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var entry = JsonSerializer.Deserialize<UserBulbEntry>(json, BuildJsonOptions());
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return null;
                string baseName = entry.Name;
                int suffix = 1;
                while (GetByName(entry.Name) != null)
                {
                    entry.Name = $"{baseName} ({suffix++})";
                }
                Equations.Add(entry);
                Save();
                return entry;
            }
            catch { return null; }
        }
    }
}
