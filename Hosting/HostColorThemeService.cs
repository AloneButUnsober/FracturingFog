// Hosting/HostColorThemeService.cs
//
// Concrete IColorThemeService for the Avalonia shell. Bridges:
//
//   • ColorPalette.GetPaletteNames()       — combined built-in + user library
//   • FractalRegionLibrary.Instance.All    — region enumeration
//   • DataDrivenColorThemes.Export(map)    — IColorMap → ColorThemeData
//     (then ColorThemeDefAdapter.ToDef    — ColorThemeData → ColorThemeDef)
//   • UserColorThemeLibrary.Instance       — persistence
//
// Lives in the main FracturingFog project so it can reference the heavy
// System.Drawing-based runtime types without dragging them into
// UI.Avalonia. The Avalonia shell receives this as an IColorThemeService
// through the bootstrapper and never touches IColorMap directly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Hosting
{
    /// <inheritdoc/>
    public sealed class HostColorThemeService : IColorThemeService
    {
        /// <summary>
        /// Translate a theme definition into a runtime IColorMap. Used by the
        /// Avalonia shell when a preview pushes a not-yet-saved theme onto
        /// the render host. Returns null if the def is structurally invalid
        /// (e.g. fewer than two stops).
        /// </summary>
        public static IColorMap? BuildColorMap(ColorThemeDef def)
        {
            if (def == null) return null;
            var data = ColorThemeDefAdapter.ToData(def);
            return DataDrivenColorThemes.Create(data);
        }

        public IReadOnlyList<string> EnumerateThemeNames()
        {
            // Force user-library reload so freshly-imported themes appear.
            ColorPalette.LoadUserThemes();
            return ColorPalette.GetPaletteNames();
        }

        public IReadOnlyList<string> EnumerateRegionNames()
        {
            var list = new List<string>();
            foreach (var r in FractalRegionLibrary.Instance.All)
                list.Add(r.Name);
            return list;
        }

        public ColorThemeDef? LoadTheme(string themeName)
        {
            if (string.IsNullOrEmpty(themeName)) return null;
            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return null;

            var data = DataDrivenColorThemes.Export(map);
            return data == null ? null : ColorThemeDefAdapter.ToDef(data);
        }

        public bool ThemeExistsInLibrary(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return UserColorThemeLibrary.Instance.Themes
                .Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        }

        public void SaveToLibrary(ColorThemeDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            var data = ColorThemeDefAdapter.ToData(def);
            UserColorThemeLibrary.Instance.ReplaceOrAdd(data);
            ColorPalette.RebuildUserPalettes();
        }

        public string SerializeJson(ColorThemeDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            var data = ColorThemeDefAdapter.ToData(def);
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            return JsonSerializer.Serialize(new[] { data }, opts);
        }

        public string GenerateCSharp(ColorThemeDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            var data = ColorThemeDefAdapter.ToData(def);
            string className = ColorThemeCsExporter.MakeClassName(def.Name);
            return ColorThemeCsExporter.BuildCSharpSource(data, className);
        }
    }
}
