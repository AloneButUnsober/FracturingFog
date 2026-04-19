// Models/FractalRegion.cs
// Defines FractalRegion (a named, typed coordinate bookmark) and
// FractalRegionLibrary which owns both the 12 built-in regions and an
// unlimited number of user-defined regions persisted to JSON in
// %APPDATA%\FracturingFog\regions.json.
//
// Design decisions:
//   • Built-in regions are read-only; only user regions can be deleted.
//   • Coordinates are stored as double for maximum zoom precision.
//   • The library is a singleton (FractalRegionLibrary.Instance).
//   • JSON serialisation uses System.Text.Json with indented formatting
//     for human-readability — no third-party dependency required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    // ── Data model ────────────────────────────────────────────────────────────

    public enum RegionType { BuiltIn, UserDefined }

    /// <summary>
    /// A named Mandelbrot coordinate bookmark.
    /// </summary>
    public sealed class FractalRegion
    {
        /// <summary>Display name shown in the UI.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Real part of the complex-plane view centre.</summary>
        public double CenterX { get; set; }

        /// <summary>Imaginary part of the complex-plane view centre.</summary>
        public double CenterY { get; set; }

        /// <summary>
        /// Zoom factor: 1.0 = full set visible, higher = zoomed in.
        /// Stored as scale width (smaller = more zoomed in) for direct use with
        /// <see cref="MandelbrotCalculator.Zoom"/>.
        /// </summary>
        public double Zoom { get; set; }

        /// <summary>Suggested maximum iteration count, or 0 to use auto.</summary>
        public int Iterations { get; set; }

        /// <summary>
        /// Quality tier to use when rendering this region.
        /// </summary>
        [JsonIgnore]
        public QualityPreset QualityPreset { get; set; } = QualityPreset.Standard;

        public string QualityPresetName
        {
            get { return QualityPreset.Name; }
            set { QualityPreset = QualityPreset.FromName(value); }
        }

        /// <summary>One-line description for the UI tooltip.</summary>
        public string Description { get; set; } = string.Empty;

        [JsonIgnore]
        public RegionType RegionType { get; set; } = RegionType.UserDefined;

        [JsonIgnore]
        public bool IsBuiltIn => RegionType == RegionType.BuiltIn;
    }

    // ── Library ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Singleton library of <see cref="FractalRegion"/> bookmarks.
    /// Call <see cref="Load"/> once at startup; <see cref="Save"/> whenever
    /// the user list changes.
    /// </summary>
    public sealed class FractalRegionLibrary
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        private static FractalRegionLibrary? _instance;
        public static FractalRegionLibrary Instance
            => _instance ??= new FractalRegionLibrary();

        private FractalRegionLibrary() { }

        // ── Storage ───────────────────────────────────────────────────────────

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FracturingFog");

        private static string RegionsFile =>
            Path.Combine(SettingsDir, "regions.json");

        // ── Built-in regions ──────────────────────────────────────────────────

        private static readonly FractalRegion[] _builtIns =
        [
            new()
            {
                Name        = "Classic Full View",
                CenterX     = -0.5,
                CenterY     =  0.0,
                Zoom        =  0.5,
                Iterations  =  256,
                Description = "The default overview showing the complete Mandelbrot set.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Seahorse Valley",
                CenterX     = -0.7435669,
                CenterY     =  0.1314023,
                Zoom        =  400.0,
                Iterations  =  800,
                Description = "Classic seahorse-shaped spirals near the main cardioid neck.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Elephant Valley",
                CenterX     =  0.3245046,
                CenterY     =  0.0483453,
                Zoom        =  300.0,
                Iterations  =  700,
                Description = "Elephant-trunk filaments branching from the period-2 bulb.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Double Spiral",
                CenterX     = -0.7269,
                CenterY     =  0.1889,
                Zoom        =  2500.0,
                Iterations  = 1200,
                Description = "Interleaved double spiral arms deep in Seahorse Valley.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Lightning Storm",
                CenterX     = -0.7746806,
                CenterY     =  0.1245250,
                Zoom        =  1200.0,
                Iterations  = 1400,
                Description = "Jagged lightning-bolt filaments near the top of the main bulb.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Galaxy Spiral",
                CenterX     = -0.5622951,
                CenterY     =  0.6427316,
                Zoom        =  3000.0,
                Iterations  = 1500,
                Description = "Spiral arms resembling a barred galaxy in the upper limb.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mini Mandelbrot",
                CenterX     = -1.7497388,
                CenterY     =  0.0,
                Zoom        =  6000.0,
                Iterations  = 2000,
                Description = "A miniature copy of the whole set — self-similarity at depth.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Feigenbaum Point",
                CenterX     = -1.4011552,
                CenterY     =  0.0,
                Zoom        =  2000.0,
                Iterations  = 1800,
                Description = "The Feigenbaum accumulation point where period doublings converge.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Star Cluster",
                CenterX     = -0.5443,
                CenterY     =  0.6070,
                Zoom        =  800.0,
                Iterations  = 1200,
                Description = "Dense star-like radiating filaments above the main cardioid.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Needle Tip",
                CenterX     = -1.9999118,
                CenterY     =  0.0,
                Zoom        =  8000.0,
                Iterations  = 2500,
                Description = "Extreme zoom at the tip of the real-axis needle.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Ultra
            },
            new()
            {
                Name        = "Parabolic Bifurcation",
                CenterX     = -0.1552,
                CenterY     =  1.0300,
                Zoom        =  600.0,
                Iterations  = 1100,
                Description = "Parabolic bifurcation site — two buds splitting from one.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Triple Spiral",
                CenterX     = -0.0886,
                CenterY     =  0.6544,
                Zoom        =  5000.0,
                Iterations  = 2000,
                Description = "Three interlocked spiral arms deep in the upper filament zone.",
                RegionType  = RegionType.BuiltIn,
                QualityPreset = QualityPreset.High
            },
        ];

        // ── Public collections ────────────────────────────────────────────────

        /// <summary>Read-only list of built-in regions.</summary>
        public IReadOnlyList<FractalRegion> BuiltIns => _builtIns;

        /// <summary>Mutable list of user-defined regions.</summary>
        public List<FractalRegion> UserRegions { get; } = new();

        /// <summary>
        /// All regions (built-ins first, then user-defined) in display order.
        /// </summary>
        public IEnumerable<FractalRegion> All
        {
            get
            {
                foreach (var r in _builtIns) yield return r;
                foreach (var r in UserRegions) yield return r;
            }
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads user-defined regions from disk.  Safe to call if the file does
        /// not yet exist.
        /// </summary>
        public void Load()
        {
            try
            {
                if (!File.Exists(RegionsFile)) return;

                string json = File.ReadAllText(RegionsFile);
                var loaded = JsonSerializer.Deserialize<List<FractalRegion>>(json);
                if (loaded == null) return;

                UserRegions.Clear();
                foreach (var r in loaded)
                {
                    r.RegionType = RegionType.UserDefined;
                    UserRegions.Add(r);
                }
            }
            catch
            {
                // If the file is corrupt, silently start fresh.
                UserRegions.Clear();
            }
        }

        /// <summary>
        /// Persists user-defined regions to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(UserRegions, options);
                File.WriteAllText(RegionsFile, json);
            }
            catch
            {
                // Non-fatal — user loses saved regions but app continues.
            }
        }

        /// <summary>
        /// Adds a user region and immediately persists the library.
        /// Returns false if a user region with the same name already exists.
        /// </summary>
        public bool AddUserRegion(FractalRegion region)
        {
            region.RegionType = RegionType.UserDefined;
            // Prevent duplicate names.
            foreach (var r in UserRegions)
                if (r.Name.Equals(region.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            UserRegions.Add(region);
            Save();
            return true;
        }

        /// <summary>
        /// Removes a user-defined region by name and persists.
        /// Returns false if the region is built-in or not found.
        /// </summary>
        public bool RemoveUserRegion(string name)
        {
            for (int i = 0; i < UserRegions.Count; i++)
            {
                if (UserRegions[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    UserRegions.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Finds any region (built-in or user) by name, or null.</summary>
        public FractalRegion? FindByName(string name)
        {
            foreach (var r in All)
                if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }
    }
}
