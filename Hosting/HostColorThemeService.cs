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
            // Fallback: the slideshow draws from AllSlideshowRegions, which also
            // includes the random-pool entries that FractalRegionLibrary.All
            // omits. Without this, slideshow picks from that pool resolve to null
            // and the region never changes.
            if (region == null)
            {
                foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                {
                    if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                    {
                        region = r;
                        break;
                    }
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
            // ApplyColorMap recolours the current frame in place (Mandelbrot) or
            // recomputes (alt calculators) so the theme change shows immediately.
            // The old ColorMap + RepaintWithPostFx path re-uploaded the stale
            // buffer, so themes only took effect after the next pan/zoom.
            _renderHost.ApplyColorMap(map);
            return true;
        }

        /// <inheritdoc/>
        public bool ApplyThemeSilent(string themeName)
        {
            if (string.IsNullOrEmpty(themeName) || _renderHost == null) return false;
            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return false;
            // ColorMap setter propagates to every calculator (no upload/present);
            // the next Trigger recomputes with this palette.
            _renderHost.ColorMap = map;
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

        /// <inheritdoc/>
        public RegionExportResult ExportUserRegionsToFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new RegionExportResult(0, 0, "Empty path.");

            try
            {
                // Skip UserEquation + UserBulb — their source isn't portable
                // via a plain regions JSON (UserEquation references by name
                // only, UserBulb embeds source useless without the surrounding
                // compile pipeline). Matches legacy MainForm.OnExportRegionsClick.
                var userRegions = FractalRegionLibrary.Instance.UserRegions
                    .Where(r => r.FractalType != FractalType.UserEquation
                             && r.FractalType != FractalType.UserBulb)
                    .ToList();
                if (userRegions.Count == 0)
                    return new RegionExportResult(0, 0, "No exportable custom regions.");

                var sandboxNames = userRegions
                    .Where(r => r.FractalType == FractalType.Sandbox && !string.IsNullOrWhiteSpace(r.SandboxName))
                    .Select(r => r.SandboxName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var sandboxEquations = new List<SandboxEquationEntry>();
                foreach (var name in sandboxNames)
                {
                    var entry = SandboxEquationStore.Instance.GetByName(name);
                    if (entry != null)
                        sandboxEquations.Add(new SandboxEquationEntry
                        {
                            Name = entry.Name,
                            Source = entry.Source,
                            Promoted = entry.Promoted,
                        });
                }

                var bundle = new RegionExportBundle
                {
                    Version = 2,
                    Regions = userRegions,
                    SandboxEquations = sandboxEquations,
                };

                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(bundle, opts));
                return new RegionExportResult(userRegions.Count, sandboxEquations.Count, null);
            }
            catch (Exception ex)
            {
                return new RegionExportResult(0, 0, ex.Message);
            }
        }

        /// <inheritdoc/>
        public RegionImportResult ImportRegionsFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new RegionImportResult(0, 0, 0, "Empty path.");
            if (!File.Exists(path))
                return new RegionImportResult(0, 0, 0, "File does not exist.");

            List<FractalRegion>? imported;
            List<SandboxEquationEntry>? importedSandbox = null;

            try
            {
                string text = File.ReadAllText(path);
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("{"))
                {
                    var bundle = JsonSerializer.Deserialize<RegionExportBundle>(text);
                    imported = bundle?.Regions;
                    importedSandbox = bundle?.SandboxEquations;
                }
                else
                {
                    imported = JsonSerializer.Deserialize<List<FractalRegion>>(text);
                }
            }
            catch (Exception ex)
            {
                return new RegionImportResult(0, 0, 0, ex.Message);
            }

            if (imported == null || imported.Count == 0)
                return new RegionImportResult(0, 0, 0, "File contains no region entries.");

            int sandboxAdded = 0;
            if (importedSandbox != null && importedSandbox.Count > 0)
            {
                SandboxEquationStore.Instance.Load();
                foreach (var eq in importedSandbox)
                {
                    if (eq == null || string.IsNullOrWhiteSpace(eq.Name)) continue;
                    if (SandboxEquationStore.Instance.GetByName(eq.Name) != null) continue;
                    SandboxEquationStore.Instance.Equations.Add(new SandboxEquationEntry
                    {
                        Name = eq.Name,
                        Source = eq.Source ?? string.Empty,
                        Promoted = eq.Promoted,
                    });
                    sandboxAdded++;
                }
                if (sandboxAdded > 0) SandboxEquationStore.Instance.Save();
            }

            int added = 0, skipped = 0;
            foreach (var region in imported)
            {
                if (string.IsNullOrWhiteSpace(region.Name)) { skipped++; continue; }
                region.RegionType = RegionType.UserDefined;
                if (FractalRegionLibrary.Instance.FindByName(region.Name) != null)
                {
                    skipped++;
                    continue;
                }
                FractalRegionLibrary.Instance.UserRegions.Add(region);
                added++;
            }

            if (added > 0) FractalRegionLibrary.Instance.Save();
            return new RegionImportResult(added, skipped, sandboxAdded, null);
        }

        /// <inheritdoc/>
        public ThemeExportResult ExportUserThemesToFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new ThemeExportResult(0, "Empty path.");

            try
            {
                // Force a reload so the export reflects whatever's currently on
                // disk (the user may have imported / edited since launch).
                UserColorThemeLibrary.Instance.Load();
                var themes = UserColorThemeLibrary.Instance.Themes;
                if (themes.Count == 0)
                    return new ThemeExportResult(0, "No user themes to export.");

                // Reuse the library's own serializer options so the file is
                // byte-identical to colorthemes.json (a re-import round-trips).
                string json = JsonSerializer.Serialize(themes, UserColorThemeLibrary.BuildJsonOptions());
                File.WriteAllText(path, json);
                return new ThemeExportResult(themes.Count, null);
            }
            catch (Exception ex)
            {
                return new ThemeExportResult(0, ex.Message);
            }
        }

        /// <inheritdoc/>
        public ThemeImportResult ImportThemesFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new ThemeImportResult(0, 0, "Empty path.");
            if (!File.Exists(path))
                return new ThemeImportResult(0, 0, "File does not exist.");

            List<ColorThemeData>? imported;
            try
            {
                string text = File.ReadAllText(path);
                imported = JsonSerializer.Deserialize<List<ColorThemeData>>(
                    text, UserColorThemeLibrary.BuildJsonOptions());
            }
            catch (Exception ex)
            {
                return new ThemeImportResult(0, 0, ex.Message);
            }

            if (imported == null || imported.Count == 0)
                return new ThemeImportResult(0, 0, "File contains no theme entries.");

            // Merge against the current on-disk library; skip dups by name.
            UserColorThemeLibrary.Instance.Load();
            int added = 0, skipped = 0;
            foreach (var theme in imported)
            {
                if (theme == null || string.IsNullOrWhiteSpace(theme.Name)) { skipped++; continue; }
                if (UserColorThemeLibrary.Instance.Themes
                        .Any(t => t.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                theme.Category = "User";
                UserColorThemeLibrary.Instance.Themes.Add(theme);
                added++;
            }

            if (added > 0)
            {
                UserColorThemeLibrary.Instance.Save();
                ColorPalette.RebuildUserPalettes();
            }
            return new ThemeImportResult(added, skipped, null);
        }

        /// <inheritdoc/>
        public bool DeleteTheme(string themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName)) return false;
            // Remove() only touches the user library; built-in/algorithmic
            // themes aren't in it, so it returns false for those (the host
            // surfaces a friendly "built-in cannot be deleted" message).
            return UserColorThemeLibrary.Instance.Remove(themeName);
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateSlideshowRegionNames()
        {
            var list = new List<string>();
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                list.Add(r.Name);
            return list;
        }

        /// <inheritdoc/>
        public double GetRegionZoom(string regionName)
        {
            if (string.IsNullOrEmpty(regionName)) return 0.0;
            foreach (var r in FractalRegionLibrary.Instance.All)
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                    return r.Zoom;
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                    return r.Zoom;
            return 0.0;
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateThemeNamesForZoom(double zoom)
            => ColorPalette.GetPaletteNamesForZoom(zoom);

        /// <inheritdoc/>
        public uint[]? RenderThemeOffscreen(string themeName, int width, int height)
        {
            if (_renderHost == null || string.IsNullOrEmpty(themeName)) return null;
            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return null;
            // Recolours the live Mandelbrot frame in place + returns a copy.
            // Returns null for alt calculators (no cheap recolor).
            return _renderHost.RecolorActiveToBuffer(map);
        }

        /// <inheritdoc/>
        public uint[]? RenderRegionOffscreen(string regionName, string themeName, int width, int height)
        {
            if (string.IsNullOrEmpty(regionName) || width <= 0 || height <= 0) return null;

            FractalRegion? region = null;
            foreach (var r in FractalRegionLibrary.Instance.All)
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal)) { region = r; break; }
            if (region == null)
                foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                    if (string.Equals(r.Name, regionName, StringComparison.Ordinal)) { region = r; break; }
            if (region == null) return null;

            // Cross-fade only supports Mandelbrot regions (the slideshow pool is
            // Mandelbrot-only); other types fall back to a hard cut.
            if (region.FractalType != FractalType.Mandelbrot) return null;

            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return null;

            var quality = region.QualityPreset ?? QualityPreset.Standard;
            int iters = region.Iterations > 0 ? region.Iterations : quality.ComputeIterations(region.Zoom);

            try
            {
                var calc = new MandelbrotCalculator(width, height)
                {
                    CenterX = region.CenterX, CenterXLo = region.CenterXLo,
                    CenterX2 = region.CenterX2, CenterX3 = region.CenterX3,
                    CenterY = region.CenterY, CenterYLo = region.CenterYLo,
                    CenterY2 = region.CenterY2, CenterY3 = region.CenterY3,
                    Zoom = region.Zoom,
                    MaxIterations = iters,
                    ColorMap = map,
                    Quality = quality,
                };
                calc.Calculate(System.Threading.CancellationToken.None);
                var src = calc.ColorBuffer;
                var copy = new uint[src.Length];
                System.Array.Copy(src, copy, src.Length);
                return copy;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// On-disk format for region export bundles (Version >= 2). Mirrors
        /// the private nested type in legacy MainForm.cs.
        /// </summary>
        private sealed class RegionExportBundle
        {
            public int Version { get; set; } = 2;
            public List<FractalRegion> Regions { get; set; } = new();
            public List<SandboxEquationEntry> SandboxEquations { get; set; } = new();
        }
    }
}
