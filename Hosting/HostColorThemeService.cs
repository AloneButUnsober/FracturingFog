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
using FracturingFog.Rendering;
using FracturingFog.ViewState;

namespace FracturingFog.Hosting
{
    /// <inheritdoc/>
    public sealed class HostColorThemeService : IColorThemeService
    {
        private readonly FractalRenderHost? _renderHost;

        /// <summary>
        /// Construct a host theme service. Pass the active render host so the
        /// service can push freshly-built IColorMap instances onto it when
        /// <see cref="ApplyTheme"/> fires. The parameterless overload below
        /// remains for the editor-only path (FromImage / save / export) where
        /// no render host exists.
        /// </summary>
        public HostColorThemeService(FractalRenderHost? renderHost = null)
        {
            _renderHost = renderHost;
        }

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

        /// <inheritdoc/>
        public bool ApplyRegion(string regionName, FractalViewState state)
        {
            if (string.IsNullOrEmpty(regionName) || state == null) return false;
            FractalRegion? region = null;
            foreach (var r in FractalRegionLibrary.Instance.All)
            {
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                {
                    region = r;
                    break;
                }
            }
            if (region == null) return false;

            state.CenterX  = region.CenterX;  state.CenterXLo = region.CenterXLo;
            state.CenterX2 = region.CenterX2; state.CenterX3  = region.CenterX3;
            state.CenterY  = region.CenterY;  state.CenterYLo = region.CenterYLo;
            state.CenterY2 = region.CenterY2; state.CenterY3  = region.CenterY3;
            state.Zoom = region.Zoom > 0 ? region.Zoom : state.Zoom;
            state.FractalType = region.FractalType;
            if (region.QualityPreset != null) state.Quality = region.QualityPreset;
            if (region.Iterations > 0)
            {
                state.IterLocked = true;
                state.LockedIterations = region.Iterations;
            }
            else
            {
                state.IterLocked = false;
                state.LockedIterations = 0;
            }
            return true;
        }

        /// <inheritdoc/>
        public bool ApplyTheme(string themeName)
        {
            if (string.IsNullOrEmpty(themeName) || _renderHost == null) return false;
            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return false;
            _renderHost.ColorMap = map;
            _renderHost.RepaintWithPostFx();
            return true;
        }

        /// <inheritdoc/>
        public bool SaveCurrentAsRegion(string regionName, FractalViewState state)
        {
            if (string.IsNullOrWhiteSpace(regionName) || state == null) return false;

            // Refuse to clobber a built-in. User-defined regions get
            // replace-by-name semantics — last save wins.
            var existing = FractalRegionLibrary.Instance.All
                .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.Ordinal));
            if (existing != null && existing.IsBuiltIn) return false;
            if (existing != null) FractalRegionLibrary.Instance.UserRegions.Remove(existing);

            var region = new FractalRegion
            {
                Name = regionName,
                CenterX  = state.CenterX,  CenterXLo = state.CenterXLo,
                CenterX2 = state.CenterX2, CenterX3  = state.CenterX3,
                CenterY  = state.CenterY,  CenterYLo = state.CenterYLo,
                CenterY2 = state.CenterY2, CenterY3  = state.CenterY3,
                Zoom = state.Zoom,
                Iterations = state.IterLocked ? state.LockedIterations : 0,
                FractalType = state.FractalType,
                QualityPreset = state.Quality ?? QualityPreset.Standard,
                RegionType = RegionType.UserDefined,
                Description = "",
            };
            FractalRegionLibrary.Instance.UserRegions.Add(region);
            FractalRegionLibrary.Instance.Save();
            return true;
        }

        /// <inheritdoc/>
        public bool DeleteRegion(string regionName)
        {
            if (string.IsNullOrEmpty(regionName)) return false;
            var lib = FractalRegionLibrary.Instance;
            var victim = lib.UserRegions.FirstOrDefault(r =>
                string.Equals(r.Name, regionName, StringComparison.Ordinal));
            if (victim == null) return false; // built-ins not deletable
            lib.UserRegions.Remove(victim);
            lib.Save();
            return true;
        }
    }
}
