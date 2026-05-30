// Abstractions/Models/ColorTheme/IColorThemeService.cs
//
// Bridge between the UI-neutral ColorThemeEditor VM in UI.Avalonia and the
// host's theme registry / user library / JSON+C# serializer (all of which
// currently live in the main FracturingFog WinExe alongside the runtime
// ColorMap classes and the System.Drawing-based code).
//
// The VM speaks only this interface — it never touches ColorPalette,
// DataDrivenColorThemes, UserColorThemeLibrary, or any other host-side
// theme infrastructure directly. The host implements the interface and
// translates between ColorThemeData (System.Drawing) and ColorThemeDef
// (this assembly).

using System.Collections.Generic;

using FracturingFog.ViewState;

namespace FracturingFog.Models
{
    /// <summary>Sort/filter mode for a colour-theme combo. Mirrors the WinForms
    /// <c>Controls.ColorComboSortMode</c>. The enum lives in Abstractions (not
    /// the main project's <c>ColorPaletteType</c>) so UI.Avalonia can reference
    /// it; the actual palette-kind names cross the boundary as strings.</summary>
    public enum ThemeSortMode
    {
        /// <summary>Grouped by palette kind with "— {kind} —" headers; alpha within.</summary>
        Default,
        /// <summary>Flat alphabetical across every kind.</summary>
        All,
        /// <summary>Filtered to a single palette kind (see kindFilter), alpha.</summary>
        ByKind,
    }

    /// <summary>Sort/filter mode for a region combo. Mirrors the WinForms
    /// <c>Controls.RegionComboSortMode</c>.</summary>
    public enum RegionSortMode
    {
        /// <summary>Built-ins first (alpha), then user regions (alpha). All fractal types.</summary>
        Default,
        /// <summary>Filtered to a single <see cref="FractalType"/>, built-ins first, alpha.</summary>
        ByFractalType,
    }

    /// <summary>
    /// Host-provided service implementing every theme-registry / serializer
    /// operation the Avalonia ColorThemeEditor needs.
    /// </summary>
    public interface IColorThemeService
    {
        /// <summary>
        /// Display names of every theme available in the host's combined
        /// built-in + user library. Used to populate the editor's Theme
        /// combo box. Order is host's choice (typically library-default).
        /// </summary>
        IReadOnlyList<string> EnumerateThemeNames();

        /// <summary>
        /// Display names of every region available in the host's region
        /// library, in whatever sort order the host currently uses. Used to
        /// populate the editor's Region combo so the user can jump the main
        /// view to an interesting spot before tuning colour.
        /// </summary>
        IReadOnlyList<string> EnumerateRegionNames();

        /// <summary>
        /// Theme names for a colour combo built per <paramref name="mode"/>.
        /// In <see cref="ThemeSortMode.Default"/> the list is grouped by palette
        /// kind with selectable "— {kind} —" header rows (callers must ignore a
        /// selection whose text starts with "—"). <paramref name="kindFilter"/>
        /// is the kind name (from <see cref="EnumerateThemeKinds"/>) used only in
        /// <see cref="ThemeSortMode.ByKind"/>. When <paramref name="editableOnly"/>
        /// is true only themes openable in the editor are listed.
        /// </summary>
        IReadOnlyList<string> EnumerateThemeNames(ThemeSortMode mode, string? kindFilter, bool editableOnly);

        /// <summary>Display names of every palette kind, in enum order. Used to
        /// build the per-kind entries of the theme combo's right-click sort
        /// menu (the kind enum itself is main-project-only).</summary>
        IReadOnlyList<string> EnumerateThemeKinds();

        /// <summary>
        /// Region names for a region combo built per <paramref name="mode"/>.
        /// The list always begins with a selectable "— select region —"
        /// placeholder (callers must ignore a selection starting with "—").
        /// <paramref name="typeFilter"/> applies only in
        /// <see cref="RegionSortMode.ByFractalType"/>.
        /// </summary>
        IReadOnlyList<string> EnumerateRegionNames(RegionSortMode mode, FractalType typeFilter);

        /// <summary>
        /// Load a theme by name and return its UI-neutral definition.
        /// Returns null when the theme is a hand-coded class that exposes
        /// no editable parameters (the editor disables Apply / Save / Export
        /// for these).
        /// </summary>
        ColorThemeDef? LoadTheme(string themeName);

        /// <summary>True if a theme with this name already exists in the
        /// user library (used to confirm overwrite on Save).</summary>
        bool ThemeExistsInLibrary(string name);

        /// <summary>Persist the given definition to the user theme library.</summary>
        void SaveToLibrary(ColorThemeDef def);

        /// <summary>Serialize the definition to JSON exactly as the user
        /// library does (so a re-import is byte-identical).</summary>
        string SerializeJson(ColorThemeDef def);

        /// <summary>Generate a concrete-class C# source file equivalent to
        /// this theme (drop-in replacement for the data-driven runtime).</summary>
        string GenerateCSharp(ColorThemeDef def);

        /// <summary>
        /// Resolve <paramref name="regionName"/> through the host region library
        /// and stamp its centre / zoom / fractal type / per-engine parameters
        /// into <paramref name="state"/>. Returns true when the region was
        /// found and applied. The caller is responsible for triggering a render.
        /// </summary>
        bool ApplyRegion(string regionName, FractalViewState state);

        /// <summary>
        /// Build the colour map named <paramref name="themeName"/> and push it
        /// onto the host's active render host. Returns true on success.
        /// Implementations need a concrete reference to the render host —
        /// passed in via the host service's constructor at bootstrap time.
        /// </summary>
        bool ApplyTheme(string themeName);

        /// <summary>
        /// Persist the current view state as a new user region under the given
        /// name. Returns true on success. Implementations write through to the
        /// host's region library (built-in regions are never overwritten —
        /// the host should pop a friendly error and bail in that case).
        /// </summary>
        bool SaveCurrentAsRegion(string regionName, FractalViewState state);

        /// <summary>
        /// Remove the named region from the user library. Returns true if a
        /// region was actually removed. Built-in regions are never deletable;
        /// implementations should return false rather than throwing on that
        /// path so the caller can surface a friendly message.
        /// </summary>
        bool DeleteRegion(string regionName);

        /// <summary>
        /// Serialize the host's user-defined regions to the given file path as
        /// a JSON bundle. Implementations may exclude region types whose source
        /// isn't portable (UserEquation references by name only; UserBulb
        /// embeds source useless without the surrounding compile pipeline).
        /// The returned <see cref="RegionExportResult"/> carries counts and an
        /// optional error message.
        /// </summary>
        RegionExportResult ExportUserRegionsToFile(string path);

        /// <summary>
        /// Read a regions-bundle JSON file (new bundle format or legacy bare
        /// array) and merge its contents into the user library. Duplicates by
        /// name are skipped. Returns counts and an optional error message.
        /// </summary>
        RegionImportResult ImportRegionsFromFile(string path);

        /// <summary>
        /// Serialize the host's user-defined colour themes to the given file
        /// path as a JSON array (byte-identical to the user library on disk so
        /// a re-import round-trips). Built-in/algorithmic themes are never
        /// exported. The returned <see cref="ThemeExportResult"/> carries the
        /// count and an optional error message.
        /// </summary>
        ThemeExportResult ExportUserThemesToFile(string path);

        /// <summary>
        /// Read a colour-theme JSON array and merge its entries into the user
        /// library. Duplicates by name (case-insensitive) are skipped. Returns
        /// counts and an optional error message.
        /// </summary>
        ThemeImportResult ImportThemesFromFile(string path);

        /// <summary>
        /// Remove the named theme from the user library. Returns true if a
        /// theme was actually removed. Built-in/algorithmic themes are never
        /// deletable; implementations return false rather than throwing on that
        /// path so the caller can surface a friendly message.
        /// </summary>
        bool DeleteTheme(string themeName);

        /// <summary>
        /// Display names of the host's curated slideshow regions (the subset
        /// the WinForms slideshow cycles through). Used by the Avalonia
        /// slideshow engine to pick the next region.
        /// </summary>
        IReadOnlyList<string> EnumerateSlideshowRegionNames();

        /// <summary>Zoom level of the named region (0 when not found). Used to
        /// filter the theme pool to themes recommended at that depth.</summary>
        double GetRegionZoom(string regionName);

        /// <summary>Theme names recommended for the given zoom level — themes
        /// whose max-recommended-zoom is below <paramref name="zoom"/> are
        /// excluded so a deep-zoom region doesn't get a washed-out palette.</summary>
        IReadOnlyList<string> EnumerateThemeNamesForZoom(double zoom);

        /// <summary>
        /// Set the active colour map to the named theme WITHOUT recolouring or
        /// presenting the current frame. Used by the slideshow region commit:
        /// the map must be in place before the region recompute so the new frame
        /// renders with the right palette, but presenting here would flash the
        /// outgoing region recoloured. Returns false when the theme is unknown.
        /// </summary>
        bool ApplyThemeSilent(string themeName);

        /// <summary>
        /// Recolour the current frame with the named theme and return the new
        /// BGRA buffer WITHOUT presenting (used as the incoming image for a
        /// slideshow theme cross-fade). The live colour map is updated to the
        /// new theme so the post-fade state is consistent. Returns null when the
        /// active fractal has no cheap recolor path (caller falls back to a hard
        /// cut). <paramref name="width"/>/<paramref name="height"/> are advisory;
        /// the returned buffer matches the live render size.
        /// </summary>
        uint[]? RenderThemeOffscreen(string themeName, int width, int height);

        /// <summary>
        /// Render the named region (with the named theme) to a fresh offscreen
        /// BGRA buffer at <paramref name="width"/>×<paramref name="height"/> —
        /// a full calculation that does NOT disturb the live view. Used as the
        /// incoming image for a slideshow region cross-fade. Returns null for
        /// non-Mandelbrot regions or unresolved names.
        /// </summary>
        uint[]? RenderRegionOffscreen(string regionName, string themeName, int width, int height);
    }

    /// <summary>Outcome of <see cref="IColorThemeService.ExportUserRegionsToFile"/>.</summary>
    public readonly struct RegionExportResult
    {
        public RegionExportResult(int regionCount, int sandboxCount, string? error)
        {
            RegionCount = regionCount;
            SandboxEquationCount = sandboxCount;
            ErrorMessage = error;
        }
        public int RegionCount { get; }
        public int SandboxEquationCount { get; }
        public string? ErrorMessage { get; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }

    /// <summary>Outcome of <see cref="IColorThemeService.ImportRegionsFromFile"/>.</summary>
    public readonly struct RegionImportResult
    {
        public RegionImportResult(int added, int skipped, int sandboxAdded, string? error)
        {
            Added = added;
            Skipped = skipped;
            SandboxEquationsAdded = sandboxAdded;
            ErrorMessage = error;
        }
        public int Added { get; }
        public int Skipped { get; }
        public int SandboxEquationsAdded { get; }
        public string? ErrorMessage { get; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }

    /// <summary>Outcome of <see cref="IColorThemeService.ExportUserThemesToFile"/>.</summary>
    public readonly struct ThemeExportResult
    {
        public ThemeExportResult(int themeCount, string? error)
        {
            ThemeCount = themeCount;
            ErrorMessage = error;
        }
        public int ThemeCount { get; }
        public string? ErrorMessage { get; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }

    /// <summary>Outcome of <see cref="IColorThemeService.ImportThemesFromFile"/>.</summary>
    public readonly struct ThemeImportResult
    {
        public ThemeImportResult(int added, int skipped, string? error)
        {
            Added = added;
            Skipped = skipped;
            ErrorMessage = error;
        }
        public int Added { get; }
        public int Skipped { get; }
        public string? ErrorMessage { get; }
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }
}
