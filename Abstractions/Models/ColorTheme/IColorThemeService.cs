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
    }
}
