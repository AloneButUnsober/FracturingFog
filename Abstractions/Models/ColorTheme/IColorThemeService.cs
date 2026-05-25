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
    }
}
