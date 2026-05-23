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

using FracturingFog.FFMath;

using Vortice.Direct3D12.Video;

namespace FracturingFog.Models
{
    // ── Data model ────────────────────────────────────────────────────────────

    /// <summary>
    /// RegionType distinguishes built-in regions (read-only, defined in code) from
    /// user-defined regions (modifiable and persisted to JSON).
    /// </summary>
    public enum RegionType
    {
        /// <summary>Built-In</summary>
        BuiltIn,
        /// <summary>User-Defined</summary>
        UserDefined
    }

    /// <summary>
    /// A named Mandelbrot coordinate bookmark.
    /// </summary>
    public sealed class FractalRegion
    {
        /// <summary>Display name shown in the UI.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Real part of the complex-plane view centre (Hi word of a double-double).</summary>
        public double CenterX { get; set; }

        /// <summary>Imaginary part of the complex-plane view centre (Hi word of a double-double).</summary>
        public double CenterY { get; set; }

        /// <summary>
        /// Low (round-off) word of the real centre.  Captures the bits that fall
        /// below ulp(CenterX) — essential at zoom ≳ 1e15 where pixel size is
        /// smaller than what a single double can address.  Defaults to 0 for
        /// backwards compatibility with regions saved before DD precision.
        /// </summary>
        public double CenterXLo { get; set; }

        /// <summary>Low (round-off) word of the imaginary centre.  See <see cref="CenterXLo"/>.</summary>
        public double CenterYLo { get; set; }
        /// <summary>QD limb 2 of real centre — used at zoom > 1e25 (~62-digit precision).
        /// Defaults to 0 for backwards compatibility with DD-only regions.</summary>
        public double CenterX2 { get; set; }

        /// <summary>QD limb 3 of real centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterX3 { get; set; }

        /// <summary>QD limb 2 of imaginary centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterY2 { get; set; }

        /// <summary>QD limb 3 of imaginary centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterY3 { get; set; }

        /// <summary>Full double-double real centre, assembled from CenterX (Hi) + CenterXLo (Lo).</summary>
        [JsonIgnore]
        public DD CenterDDX
        {
            get => new DD(CenterX, CenterXLo);
            set { CenterX = value.Hi; CenterXLo = value.Lo; }
        }

        /// <summary>Full double-double imaginary centre.</summary>
        [JsonIgnore]
        public DD CenterDDY
        {
            get => new DD(CenterY, CenterYLo);
            set { CenterY = value.Hi; CenterYLo = value.Lo; }
        }

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

        /// <summary>
        /// Quality Preset Name for JSON serialization.  This is a string property that maps to the QualityPreset object.
        /// </summary>
        public string QualityPresetName
        {
            get { return QualityPreset.Name; }
            set { QualityPreset = QualityPreset.FromName(value); }
        }

        /// <summary>One-line description for the UI tooltip.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Fractal type this region targets. Serialized as the enum name (e.g. "Mandelbrot") so
        /// the JSON stays human-readable and survives enum value reordering. Defaults to
        /// <see cref="FractalType.Mandelbrot"/> for backwards compatibility with regions saved
        /// before fractal-type-aware bookmarks existed.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public FractalType FractalType { get; set; } = FractalType.Mandelbrot;

        /// <summary>
        /// Name of the saved <see cref="UserEquationEntry"/> this region depends on
        /// when <see cref="FractalType"/> is <see cref="FractalType.UserEquation"/>.
        /// On recall the source is looked up by name in <see cref="UserEquationStore"/>,
        /// so editing the saved equation later updates every region that references it.
        /// Null/empty for non-UserEquation regions, or for ad-hoc equations the user
        /// never saved.
        /// </summary>
        public string? UserEquationName { get; set; }

        /// <summary>
        /// Name of the saved <see cref="SandboxEquationEntry"/> this region depends on
        /// when <see cref="FractalType"/> is <see cref="FractalType.Sandbox"/>.
        /// On recall the source is looked up by name in <see cref="SandboxEquationStore"/>.
        /// Null/empty for non-Sandbox regions, or for ad-hoc sources the user never saved.
        /// </summary>
        public string? SandboxName { get; set; }

        /// <summary>
        /// Optional friendly name for the UserBulb (3D) source captured by this region.
        /// UserBulb has no shared library yet, so the source itself is embedded in
        /// <see cref="UserBulbSource"/>. The name is informational.
        /// </summary>
        public string? UserBulbName { get; set; }

        /// <summary>
        /// Full UserBulb (3D) Step-function source recorded when the region was saved.
        /// Restored verbatim and recompiled on recall so the saved view renders the
        /// same fractal even if the user has edited the live source since. Null/empty
        /// for non-UserBulb regions.
        /// </summary>
        public string? UserBulbSource { get; set; }

        /// <summary>UserBulb camera distance (radial). 0 = use parameter default on recall.</summary>
        public double UserBulbCameraDistance { get; set; }
        /// <summary>UserBulb camera theta (azimuth, radians).</summary>
        public double UserBulbCameraTheta { get; set; }
        /// <summary>UserBulb camera phi (polar, radians).</summary>
        public double UserBulbCameraPhi { get; set; }
        /// <summary>UserBulb light theta (radians).</summary>
        public double UserBulbLightTheta { get; set; }
        /// <summary>UserBulb light phi (radians).</summary>
        public double UserBulbLightPhi { get; set; }

        /// <summary>
        /// Region type (built-in or user-defined).  This is not serialized to JSON; instead, all loaded regions are
        /// assumed to be user-defined unless explicitly marked as built-in.
        /// </summary>
        [JsonIgnore]
        public RegionType RegionType { get; set; } = RegionType.UserDefined;

        /// <summary>
        /// Is Built In region (read-only, defined in code) vs User-Defined (modifiable and persisted to JSON).
        /// </summary>
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

        /// <summary>
        /// Instance of the library.  Lazy-initialized on first access.
        /// </summary>
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
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
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.High
            },
        ];

        // ── Interesting random-zoom regions for the slideshow ────────────────────
        // These are hand-picked coordinates that are visually striking but not
        // shown as named bookmarks in the UI.  The slideshow draws from both
        // _builtIns (and user regions) and _randomPool.
        private static readonly FractalRegion[] _randomPool =
        [
            // ── Deep seahorse spirals ──────────────────────────────────────────────
            new() { Name="R:SeahorseA",  CenterX=-0.74878, CenterY=0.06508, Zoom=12000.0, Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseB",  CenterX=-0.74529, CenterY=0.11307, Zoom=8000.0,  Iterations=1800, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseC",  CenterX=-0.74542, CenterY=0.13161, Zoom=290.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseD",  CenterX=-0.77568, CenterY=0.13646, Zoom=15000.0, Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Elephant valley variations ─────────────────────────────────────────
            new() { Name="R:ElephantA",  CenterX=0.32530,  CenterY=0.04868, Zoom=4000.0,  Iterations=1600, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ElephantB",  CenterX=0.375534459723856,  CenterY=-0.221346110647405,Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ElephantC",  CenterX=0.35516,  CenterY=0.09486, Zoom=200.0,  Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Mini Mandelbrots (self-similar copies) ─────────────────────────────
            new() { Name="R:MiniA",      CenterX=-1.6271862274936, CenterY=0.00000, Zoom=55.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniB",      CenterX=-0.160229506084313, CenterY=1.03460261104092, Zoom=60.0,  Iterations=500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniC",      CenterX=-1.25067386008417, CenterY=0.0201413514898332, Zoom=54602629.0,  Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniD",      CenterX=0.366432439759528,  CenterY=-0.676487494685914,Zoom=3065.0,  Iterations=1400, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniE",      CenterX=-1.94157, CenterY=0.00000, Zoom=502.0, Iterations=1200, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Spiral galaxies / triple spirals ───────────────────────────────────
            new() { Name="R:SpiralA",    CenterX=-0.562474314086615, CenterY=0.64138011514593, Zoom=91,  Iterations=1200, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralB",    CenterX=-0.0976515101078047, CenterY=0.654455924064267, Zoom=227,  Iterations=1114, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralC",    CenterX=-0.52768, CenterY=0.52768, Zoom=3000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralD",    CenterX=-0.053974358974359, CenterY=0.663897435897436, Zoom=50.0, Iterations=500, QualityPreset=QualityPreset.Standard},
            // ── Period-3 bulb and neighbourhood ───────────────────────────────────
            new() { Name="R:Period3A",   CenterX=-0.0958466539313279, CenterY=0.653567154869739, Zoom=93.0,  Iterations=500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:Period3B",   CenterX=-0.13500, CenterY=0.65000, Zoom=1500.0,  Iterations=1200, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:Period3C",   CenterX=-0.16667, CenterY=1.04000, Zoom=1736.0,  Iterations=670, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Lightning / filament zones ─────────────────────────────────────────
            new() { Name="R:LightA",     CenterX=-0.626614850667933, CenterY=0.384657235048688, Zoom=744.0,  Iterations=650, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:LightB",     CenterX=-0.507263617832552, CenterY=0.526971432700647, Zoom=175.0,  Iterations=550, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:LightC",     CenterX=-0.740972025145092, CenterY=0.104494920892684, Zoom=800.0,   Iterations=650, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Parabolic / satellite bulbs ────────────────────────────────────────
            new() { Name="R:ParabA",     CenterX=-1.40115, CenterY=0.00000, Zoom=4000.0,  Iterations=2500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ParabB",     CenterX=-1.31079592300444, CenterY=0.0731247515540183, Zoom=64694.7,  Iterations=1750, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // Stopped here.
            new() { Name="R:ParabC",     CenterX=0.25033364354215, CenterY=0.25033364354215, Zoom=20003.0, Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Deep double spirals ────────────────────────────────────────────────
            new() { Name="R:DblSpiralA", CenterX=-0.72700, CenterY=0.18900, Zoom=5000.0,  Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DblSpiralB", CenterX=-0.74108, CenterY=0.16858, Zoom=30000.0, Iterations=3500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DblSpiralC", CenterX=-0.73657, CenterY=0.18781, Zoom=18000.0, Iterations=3000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Upper filament / star clusters ────────────────────────────────────
            new() { Name="R:StarA",      CenterX=-0.159158498023715, CenterY=1.02331660079051, Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:StarB",      CenterX=1.02331660079051, CenterY=1.02525867534908, Zoom=5000.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:StarC",      CenterX=-0.22700, CenterY=1.11600, Zoom=3500.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Needle tip zone ───────────────────────────────────────────────────
            new() { Name="R:NeedleA",    CenterX=-1.99991, CenterY=0.00000, Zoom=15000.0, Iterations=3000, QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
            new() { Name="R:NeedleB",    CenterX=-1.99999, CenterY=0.00000, Zoom=50000.0, Iterations=5000, QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
            // ── Cauliflower / cardioid edge ────────────────────────────────────────
            new() { Name="R:CauliA",     CenterX=0.25010,  CenterY=0.00000, Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:CauliB",     CenterX=0.25033364354215,  CenterY=3.9525691699605E-06, Zoom=8000.0,  Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Deep zoom demo points (DD precision) ──────────────────────────────
            new() { Name="R:DeepA",      CenterX=-0.743643887037151, CenterY=0.131825904205330, Zoom=1e14, Iterations=8000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DeepB",      CenterX=-0.73364389241974, CenterY=0.245521140671023, Zoom=5e13, Iterations=6000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DeepC",      CenterX=0.001643721971153, CenterY=0.822467633298876,  Zoom=3e9, Iterations=10000,QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
        ];

        // ── Public collections ────────────────────────────────────────────────

        public bool IncludeExtremeInAll { get; set; } = false; // For now, we exclude extreme regions from the main list to keep the UI focused on more accessible areas.  This can be made user-configurable in the future.

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
                //foreach (var r in _randomPool) yield return r;
            }
        }

        /// <summary>
        /// All slideshow-eligible regions: built-ins, user-defined, and interesting random pool.
        /// User regions are filtered to <see cref="FractalType.Mandelbrot"/> only — the slideshow
        /// pipeline assumes Mandelbrot semantics (escape-time render, log-zoom interpolation), so
        /// mixing in Julia/Newton/etc. regions without switching the active calculator would break it.
        /// </summary>
        public IEnumerable<FractalRegion> AllSlideshowRegions
        {
            get
            {

                foreach (var r in _builtIns) yield return r;
                if (IncludeExtremeInAll)
                {
                    foreach (var r in UserRegions)
                        if (r.FractalType == FractalType.Mandelbrot) yield return r;
                }
                else
                {
                    foreach (var r in UserRegions.FindAll(r => !r.QualityPreset.Equals(QualityPreset.Extreme) //)) yield return r;
                    && r.FractalType == FractalType.Sandbox)) yield return r;
                }

                foreach (var r in _randomPool) yield return r;
            }
        }

        public int MaxRegionNameLength
        {
            get
            {
                int max = 0;
                foreach (var r in All)
                    if (r.Name.Length > max)
                        max = r.Name.Length;
                return max;
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
