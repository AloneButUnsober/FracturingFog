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
using System.Collections.Immutable;
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

        // ── Sort-aware combo enumeration (parity with WinForms Controls.cs) ──

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateThemeNames(ThemeSortMode mode, string? kindFilter, bool editableOnly)
            => EnumerateThemeNames(mode, kindFilter, editableOnly, compatFor: null);

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateThemeNames(
            ThemeSortMode mode, string? kindFilter, bool editableOnly, FractalType? compatFor)
        {
            // Force user-library reload so freshly-imported themes appear.
            ColorPalette.LoadUserThemes();

            bool Allow(string n) => !editableOnly || IsThemeEditable(n);
            var result = new List<string>();

            switch (mode)
            {
                case ThemeSortMode.All:
                    result.AddRange(CollectAllThemeNames().Where(Allow));
                    break;

                case ThemeSortMode.ByKind:
                    if (Enum.TryParse<ColorPaletteType>(kindFilter, out var k))
                    {
                        var byKind = ColorPalette.GetPalettesByType(k);
                        result.AddRange(byKind.ToImmutableSortedDictionary().Keys.Where(Allow));
                    }
                    break;

                case ThemeSortMode.ByFractalCompat:
                    // Flat list, grouped by kind for readability. Names whose
                    // required calculator data is not supplied by compatFor
                    // are dropped. Falls back to Default grouping when no
                    // compat type is supplied so the combo never goes empty.
                    if (compatFor is FractalType ft)
                    {
                        foreach (var type in Enum.GetValues<ColorPaletteType>())
                        {
                            var palettes = ColorPalette.GetPalettesByType(type);
                            if (palettes.Count == 0) continue;
                            var names = palettes.ToImmutableSortedDictionary().Keys
                                .Where(Allow)
                                .Where(n => ColorPalette.IsCompatible(ColorPalette.GetPaletteByName(n), ft))
                                .ToList();
                            if (names.Count == 0) continue;
                            result.Add($"— {type} —");
                            result.AddRange(names);
                        }
                        if (result.Count > 0) break;
                    }
                    goto default; // fall through to grouped Default

                default: // Default — grouped by kind with "— {kind} —" headers
                    foreach (var type in Enum.GetValues<ColorPaletteType>())
                    {
                        var palettes = ColorPalette.GetPalettesByType(type);
                        if (palettes.Count == 0) continue;
                        var names = palettes.ToImmutableSortedDictionary().Keys.Where(Allow).ToList();
                        if (names.Count == 0) continue;
                        result.Add($"— {type} —");
                        result.AddRange(names);
                    }
                    break;
            }
            return result;
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateThemeKinds()
            => Enum.GetValues<ColorPaletteType>().Select(t => t.ToString()).ToList();

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateRegionNames(RegionSortMode mode, FractalType typeFilter)
        {
            var result = new List<string> { "— select region —" };

            IEnumerable<FractalRegion> source = FractalRegionLibrary.Instance.All;
            IEnumerable<FractalRegion> regions = mode == RegionSortMode.ByFractalType
                ? source.Where(r => r.FractalType == typeFilter)
                        .OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name)
                : source.OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name);

            foreach (var r in regions) result.Add(r.Name);
            return result;
        }

        /// <summary>True when a theme round-trips through
        /// <see cref="DataDrivenColorThemes.Export"/> (i.e. it can be opened in
        /// the Color Theme Editor). Mirrors Controls.IsThemeEditable.</summary>
        private static bool IsThemeEditable(string name)
        {
            var map = ColorPalette.GetPaletteByName(name);
            return map != null && DataDrivenColorThemes.Export(map) != null;
        }

        /// <summary>Every theme name across all kinds, flat Ordinal-alphabetical.
        /// Mirrors Controls.CollectAllThemeNames.</summary>
        private static IEnumerable<string> CollectAllThemeNames()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in Enum.GetValues<ColorPaletteType>())
                foreach (var name in ColorPalette.GetPalettesByType(type).Keys)
                    names.Add(name);
            return names;
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
            // Source-compiled types (UserEquation / Sandbox / UserBulb) carry
            // their equation by name in the region. Pull the live source from the
            // store into FractalParameters + force a recompile so the calculator
            // renders the right equation instead of a blank in-set fill. Mirrors
            // legacy MainForm.LoadRegionFractalParams.
            LoadRegionFractalParams(region, state);
            if (region.QualityPreset != null) state.Quality = region.QualityPreset;
            // Region's saved iter count drives the next render via
            // FractalViewState.PreferredIterations (ApplyView reads it after
            // IterLocked + maxIters arg, before Quality.ComputeIterations
            // fallback). Cleared on the next user pan/zoom by
            // FractalInputController so it only governs the immediate
            // region-jump frame. Mirrors legacy MainForm.ApplyRegion:2980
            // which wrote region.Iterations directly into _calculator.MaxIterations
            // when !_iterLocked. Without this the slideshow cross-fade renders
            // its offscreen source at region.Iterations but the post-commit
            // Trigger drops back to Quality.ComputeIterations — visible iter
            // collapse the moment the fade-in completes.
            state.PreferredIterations = region.Iterations > 0 ? region.Iterations : 0;
            // A region jump must NOT toggle the iteration lock — legacy
            // MainForm.ApplyRegion leaves _iterLocked untouched and lets the
            // adaptive (zoom-scaled) iteration count drive the render. Forcing
            // the lock on here was the regression that made every built-in
            // region — and any UserEquation/Sandbox/UserBulb recall — pin the
            // lock checkbox on. The user keeps full control via the checkbox.
            return true;
        }

        /// <summary>
        /// Loads region-specific source-compiled equation parameters
        /// (UserEquation / Sandbox / UserBulb) into the shared FractalParameters
        /// and forces a recompile. The calculators only lazily compile when no
        /// delegate exists yet, so switching between two saved equations needs
        /// an explicit Compile — hence the concrete-host cast. Center/zoom/iter
        /// are owned by ApplyRegion; this only touches the equation slots.
        /// Mirrors legacy MainForm.LoadRegionFractalParams.
        /// </summary>
        private void LoadRegionFractalParams(FractalRegion region, FractalViewState state)
        {
            var p = state.FractalParameters;
            if (p == null) return;
            var host = _renderHost as FractalRenderHost;

            if (region.FractalType == FractalType.UserEquation
                && !string.IsNullOrWhiteSpace(region.UserEquationName))
            {
                var entry = UserEquationStore.Instance.GetByName(region.UserEquationName);
                if (entry != null)
                {
                    p.UserEquationSource = entry.Source;
                    p.UserEquationName = entry.Name;
                    host?.CompileUserEquation(entry.Source);
                }
            }

            if (region.FractalType == FractalType.Sandbox
                && !string.IsNullOrWhiteSpace(region.SandboxName))
            {
                var entry = SandboxEquationStore.Instance.GetByName(region.SandboxName);
                if (entry != null)
                {
                    p.SandboxSource = entry.Source;
                    p.SandboxName = entry.Name;
                    host?.CompileSandbox(entry.Source);
                }
            }

            if (region.FractalType == FractalType.UserBulb)
            {
                string? source = null;
                var entry = !string.IsNullOrWhiteSpace(region.UserBulbName)
                    ? UserBulbStore.Instance.GetByName(region.UserBulbName)
                    : null;
                if (entry != null)
                {
                    source = entry.Source;
                    p.UserBulbSource = entry.Source;
                    p.UserBulbName = entry.Name;
                }
                else if (!string.IsNullOrWhiteSpace(region.UserBulbSource))
                {
                    source = region.UserBulbSource;
                    p.UserBulbSource = region.UserBulbSource;
                    p.UserBulbName = region.UserBulbName;
                }

                if (region.UserBulbCameraDistance > 0)
                {
                    p.UserBulbCameraDistance = region.UserBulbCameraDistance;
                    p.UserBulbCameraTheta = region.UserBulbCameraTheta;
                    p.UserBulbCameraPhi = region.UserBulbCameraPhi;
                    p.UserBulbLightTheta = region.UserBulbLightTheta;
                    p.UserBulbLightPhi = region.UserBulbLightPhi;
                }

                if (!string.IsNullOrWhiteSpace(source))
                    host?.CompileUserBulb(source);
            }
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
        public bool TryGetThemeLightingPreset(
            string themeName, out FracturingFog.Rendering.Lighting.LightingFxData lighting)
        {
            lighting = default;
            if (string.IsNullOrEmpty(themeName)) return false;
            // User library is the authoritative source for bundled presets —
            // built-in C# themes don't carry LightingPreset. Case-insensitive
            // match mirrors ColorPalette.GetPaletteByName's lookup contract.
            var data = UserColorThemeLibrary.Instance.Themes.FirstOrDefault(
                t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
            if (data?.LightingPreset == null) return false;
            lighting = data.LightingPreset.ToFx();
            return true;
        }

        /// <inheritdoc/>
        public bool SaveLightingPresetToTheme(
            string themeName, in FracturingFog.Rendering.Lighting.LightingFxData lighting)
        {
            if (string.IsNullOrEmpty(themeName)) return false;
            var data = UserColorThemeLibrary.Instance.Themes.FirstOrDefault(
                t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
            // Built-in / algorithmic themes are not in the user library and
            // can't carry a preset — the caller surfaces a friendly hint.
            if (data == null) return false;
            data.LightingPreset = LightingFxPresetData.FromFx(lighting);
            UserColorThemeLibrary.Instance.Save();
            ColorPalette.RebuildUserPalettes();
            return true;
        }

        /// <inheritdoc/>
        public bool ClearLightingPresetOnTheme(string themeName)
        {
            if (string.IsNullOrEmpty(themeName)) return false;
            var data = UserColorThemeLibrary.Instance.Themes.FirstOrDefault(
                t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
            if (data == null || data.LightingPreset == null) return false;
            data.LightingPreset = null;
            UserColorThemeLibrary.Instance.Save();
            ColorPalette.RebuildUserPalettes();
            return true;
        }

        /// <inheritdoc/>
        public bool TryGetRegionLightingOverride(
            string regionName, out FracturingFog.Rendering.Lighting.LightingFxData lighting)
        {
            lighting = default;
            if (string.IsNullOrEmpty(regionName)) return false;
            var region = FractalRegionLibrary.Instance.All.FirstOrDefault(
                r => string.Equals(r.Name, regionName, StringComparison.Ordinal))
                ?? FractalRegionLibrary.Instance.AllSlideshowRegions.FirstOrDefault(
                    r => string.Equals(r.Name, regionName, StringComparison.Ordinal));
            if (region?.LightingOverride == null) return false;
            lighting = region.LightingOverride.ToFx();
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
        /// <inheritdoc/>
        public WatermarkDef? GetRegionEmbeddedWatermark(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            return r?.EmbeddedWatermark?.Clone();
        }

        /// <inheritdoc/>
        public string? GetRegionAnimationName(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            return r?.AnimationName;
        }

        /// <inheritdoc/>
        public FracturingFog.Abstractions.Animation.AnimationData? GetAnimation(string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return null;
            return AnimationLibrary.Instance.GetByName(animationName);
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateAnimationNames()
        {
            var lib = AnimationLibrary.Instance;
            var list = new List<string>(lib.Animations.Count);
            foreach (var a in lib.Animations) list.Add(a.Name);
            return list;
        }

        /// <inheritdoc/>
        public bool AnimationExistsInLibrary(string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return false;
            return AnimationLibrary.Instance.GetByName(animationName) != null;
        }

        /// <inheritdoc/>
        public bool SaveAnimation(FracturingFog.Abstractions.Animation.AnimationData animation)
        {
            if (animation == null || string.IsNullOrWhiteSpace(animation.Name)) return false;
            return AnimationLibrary.Instance.ReplaceOrAdd(animation);
        }

        public bool SaveCurrentAsRegion(string regionName, FractalViewState state, WatermarkDef? embeddedWatermark = null, string? animationName = null)
        {
            if (string.IsNullOrWhiteSpace(regionName) || state == null) return false;

            // Refuse to clobber a built-in. User-defined regions get
            // replace-by-name semantics — last save wins.
            var existing = FractalRegionLibrary.Instance.All
                .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.Ordinal));
            if (existing != null && existing.IsBuiltIn) return false;
            if (existing != null) FractalRegionLibrary.Instance.UserRegions.Remove(existing);

            var p = state.FractalParameters;
            var region = new FractalRegion
            {
                Name = regionName,
                CenterX  = state.CenterX,  CenterXLo = state.CenterXLo,
                CenterX2 = state.CenterX2, CenterX3  = state.CenterX3,
                CenterY  = state.CenterY,  CenterYLo = state.CenterYLo,
                CenterY2 = state.CenterY2, CenterY3  = state.CenterY3,
                Zoom = state.Zoom,
                // Capture the live iter count so the saved region renders at
                // the same detail it had on screen — matches legacy MainForm:2248
                // (`Iterations = _calculator?.MaxIterations ?? 512`). Falls back
                // through the same precedence ApplyView uses: lock → preferred
                // (region jump still pinned) → Quality.ComputeIterations.
                // Saving 0 when unlocked lost the live count; the recalled
                // region then re-derived a (typically lower) default from the
                // quality preset.
                Iterations = state.IterLocked
                    ? state.LockedIterations
                    : state.PreferredIterations > 0
                        ? state.PreferredIterations
                        : (state.Quality ?? QualityPreset.Standard).ComputeIterations(state.Zoom),
                FractalType = state.FractalType,
                QualityPreset = state.Quality ?? QualityPreset.Standard,
                RegionType = RegionType.UserDefined,
                // Source-compiled types carry their equation identity so recall
                // (LoadRegionFractalParams) can reload + recompile. UserEquation
                // and Sandbox reference a saved entry by name; UserBulb embeds
                // its source + camera/light. Without this, recalled equation
                // regions render a blank in-set fill. Mirrors legacy MainForm.
                UserEquationName = state.FractalType == FractalType.UserEquation ? p?.UserEquationName : null,
                SandboxName      = state.FractalType == FractalType.Sandbox      ? p?.SandboxName      : null,
                UserBulbName     = state.FractalType == FractalType.UserBulb     ? p?.UserBulbName     : null,
                UserBulbSource   = state.FractalType == FractalType.UserBulb     ? p?.UserBulbSource   : null,
                UserBulbCameraDistance = state.FractalType == FractalType.UserBulb ? p?.UserBulbCameraDistance ?? 0 : 0,
                UserBulbCameraTheta    = state.FractalType == FractalType.UserBulb ? p?.UserBulbCameraTheta    ?? 0 : 0,
                UserBulbCameraPhi      = state.FractalType == FractalType.UserBulb ? p?.UserBulbCameraPhi      ?? 0 : 0,
                UserBulbLightTheta     = state.FractalType == FractalType.UserBulb ? p?.UserBulbLightTheta     ?? 0 : 0,
                UserBulbLightPhi       = state.FractalType == FractalType.UserBulb ? p?.UserBulbLightPhi       ?? 0 : 0,
                Description = "",
                EmbeddedWatermark = embeddedWatermark?.Clone(),
                AnimationName = string.IsNullOrWhiteSpace(animationName) ? null : animationName,
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
        public RegionEditModel? GetRegionForEdit(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            if (r == null) return null;

            return new RegionEditModel
            {
                OriginalName = r.Name,
                IsBuiltIn    = r.IsBuiltIn,
                Name         = r.Name,
                Description  = r.Description ?? string.Empty,
                AnimationName = r.AnimationName,
                // Defensive copy so editor edits don't mutate the live library
                // entry before the user commits.
                CuratedThemes = r.CuratedThemes != null ? new List<string>(r.CuratedThemes) : null,
                KeepLightingOverride  = true,
                KeepEmbeddedWatermark = true,
                FractalTypeName = r.FractalType.ToString(),
                CenterX = r.CenterX,
                CenterY = r.CenterY,
                Zoom    = r.Zoom,
                Iterations = r.Iterations,
                HasLightingOverride  = r.LightingOverride != null,
                HasEmbeddedWatermark = r.EmbeddedWatermark != null,
            };
        }

        /// <inheritdoc/>
        public RegionUpdateResult UpdateRegionMetadata(RegionEditModel edits)
        {
            if (edits == null) return RegionUpdateResult.Fail("No edit data.");

            string newName = (edits.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
                return RegionUpdateResult.Fail("Region name cannot be empty.");

            var lib = FractalRegionLibrary.Instance;

            // Resolve the source region we're editing (built-in or user).
            var source = lib.All.FirstOrDefault(x =>
                string.Equals(x.Name, edits.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return RegionUpdateResult.Fail($"Region \"{edits.OriginalName}\" no longer exists.");

            // Collision: refuse a name already taken by a *different* region
            // (built-in or user). Editing in place under the same name is fine.
            var collision = lib.All.FirstOrDefault(x =>
                string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x.Name, edits.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (collision != null)
                return RegionUpdateResult.Fail($"A region named \"{newName}\" already exists.");

            bool cloned = source.IsBuiltIn;

            // Preserve the source region's geometry + per-engine source fields.
            // Metadata is applied fresh from the edit model below. Geometry is
            // NEVER taken from the live view here — this path edits a saved
            // region without moving the camera.
            var region = CloneRegionGeometry(source);
            region.RegionType  = RegionType.UserDefined;
            region.Name        = newName;
            region.Description  = edits.Description ?? string.Empty;
            region.AnimationName = string.IsNullOrWhiteSpace(edits.AnimationName) ? null : edits.AnimationName;
            region.CuratedThemes = (edits.CuratedThemes != null && edits.CuratedThemes.Count > 0)
                ? new List<string>(edits.CuratedThemes)
                : null;
            // Keep vs clear the two attached assets. Cloned built-ins carry the
            // source's override/watermark forward when kept.
            region.LightingOverride  = edits.KeepLightingOverride  ? source.LightingOverride : null;
            region.EmbeddedWatermark = edits.KeepEmbeddedWatermark ? source.EmbeddedWatermark?.Clone() : null;

            // Replace-by-name for an in-place user edit; pure add for a clone
            // (the built-in stays put). When a user region is renamed the old
            // name is removed too.
            if (!cloned)
            {
                var existing = lib.UserRegions.FirstOrDefault(x =>
                    string.Equals(x.Name, edits.OriginalName, StringComparison.OrdinalIgnoreCase));
                if (existing != null) lib.UserRegions.Remove(existing);
            }

            lib.UserRegions.Add(region);
            lib.Save();
            return RegionUpdateResult.Ok(newName, cloned);
        }

        /// <summary>
        /// Region Editor helper — copy a region's stored geometry, quality,
        /// fractal type, and per-engine source identity into a fresh
        /// user-defined <see cref="FractalRegion"/>. Metadata (Name,
        /// Description, animation, curated themes, lighting/watermark) is left
        /// at defaults for the caller to fill from the edit model.
        /// </summary>
        private static FractalRegion CloneRegionGeometry(FractalRegion src) => new()
        {
            CenterX  = src.CenterX,  CenterXLo = src.CenterXLo,
            CenterX2 = src.CenterX2, CenterX3  = src.CenterX3,
            CenterY  = src.CenterY,  CenterYLo = src.CenterYLo,
            CenterY2 = src.CenterY2, CenterY3  = src.CenterY3,
            Zoom = src.Zoom,
            Iterations = src.Iterations,
            FractalType = src.FractalType,
            QualityPreset = src.QualityPreset,
            RegionType = RegionType.UserDefined,
            UserEquationName = src.UserEquationName,
            SandboxName      = src.SandboxName,
            UserBulbName     = src.UserBulbName,
            UserBulbSource   = src.UserBulbSource,
            UserBulbCameraDistance = src.UserBulbCameraDistance,
            UserBulbCameraTheta    = src.UserBulbCameraTheta,
            UserBulbCameraPhi      = src.UserBulbCameraPhi,
            UserBulbLightTheta     = src.UserBulbLightTheta,
            UserBulbLightPhi       = src.UserBulbLightPhi,
        };

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
            var r = FindSlideshowRegion(regionName);
            return r?.Zoom ?? 0.0;
        }

        /// <inheritdoc/>
        public string GetRegionFractalTypeName(string regionName)
        {
            var r = FindSlideshowRegion(regionName);
            return r?.FractalType.ToString() ?? string.Empty;
        }

        /// <inheritdoc/>
        public string GetRegionQualityPresetName(string regionName)
        {
            var r = FindSlideshowRegion(regionName);
            return r?.QualityPreset?.Name ?? string.Empty;
        }

        private static FractalRegion? FindSlideshowRegion(string regionName)
        {
            if (string.IsNullOrEmpty(regionName)) return null;
            foreach (var r in FractalRegionLibrary.Instance.All)
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                    return r;
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                if (string.Equals(r.Name, regionName, StringComparison.Ordinal))
                    return r;
            return null;
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
            // Forward the caller's dims so the alt path can resize its
            // buffer to match the slideshow's snapshot — without this the
            // FadeAsync length check fails and the engine hard-cuts.
            return _renderHost.RecolorActiveToBuffer(map, width, height);
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

            var map = ColorPalette.GetPaletteByName(themeName);
            if (map == null) return null;

            // Non-Mandelbrot: render through the live alt-calculator fleet so
            // the slideshow cross-fade has a real incoming buffer (instead of
            // falling back to fade-to-black or hard cut). ApplyRegion first
            // so source-compiled types (UserEquation / Sandbox / UserBulb) get
            // compiled + FractalParameters populated before Calculate runs.
            if (region.FractalType != FractalType.Mandelbrot)
            {
                if (_renderHost == null) return null;
                ApplyRegion(regionName, _renderHost.ViewState);
                return _renderHost.RenderRegionToBuffer(region, map, width, height);
            }

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

        /// <inheritdoc/>
        public string? SerializeThemeJsonByName(string themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName)) return null;
            ColorPalette.LoadUserThemes();
            IColorMap map = ColorPalette.GetPaletteByName(themeName);
            // GetPaletteByName falls back to HsvPalette on miss — only emit
            // the inline payload when the requested name actually matched
            // something. Comparing the resolved map's name to the requested
            // name (case-insensitive) catches that fallback so a client
            // request for an unknown theme does not silently ship "HSV".
            string resolved = ColorPalette.GetStaticName(map);
            if (!string.Equals(resolved, themeName, StringComparison.OrdinalIgnoreCase))
                return null;

            ColorThemeData? data = DataDrivenColorThemes.Export(map);
            if (data == null) return null; // algorithmic theme — server falls back to name lookup
            var opts = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter() },
            };
            return JsonSerializer.Serialize(data, opts);
        }

        /// <inheritdoc/>
        public string? SerializeRegionJsonByName(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            FractalRegion? region = FractalRegionLibrary.Instance.FindByName(regionName);
            if (region == null) return null;

            // Refuse to transport regions whose FractalType is on the server
            // block list — the wire payload would be rejected anyway, and
            // exporting the user's UserBulb source over the network is
            // exactly what the FractalTypeAllowlist guard exists to prevent.
            if (region.FractalType == FractalType.UserEquation ||
                region.FractalType == FractalType.Sandbox ||
                region.FractalType == FractalType.UserBulb)
                return null;

            var opts = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter() },
            };
            return JsonSerializer.Serialize(region, opts);
        }
    }
}
