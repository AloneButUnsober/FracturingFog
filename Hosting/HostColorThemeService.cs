// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

        /// <inheritdoc/>
        public bool RegionReliefLocked { get; set; }

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

            // #27 Phase 0 — stamp user-code provenance before any Compile call.
            // A region loaded from a cross-user import is untrusted, so an inline
            // raw-C# source it carries is refused by the gate. Sources pulled
            // from the local library stores below are the user's own and are
            // re-marked Interactive.
            p.UserCodeOrigin = region.ExternalOrigin
                ? FracturingFog.Security.UserCodeOrigin.ExternalFile
                : FracturingFog.Security.UserCodeOrigin.Interactive;

            if (region.FractalType == FractalType.UserEquation
                && !string.IsNullOrWhiteSpace(region.UserEquationName))
            {
                var entry = UserEquationStore.Instance.GetByName(region.UserEquationName);
                if (entry != null)
                {
                    p.UserEquationSource = entry.Source;
                    p.UserEquationName = entry.Name;
                    p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; // local library = trusted
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
                    p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; // local library = trusted
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

            // #253 — domain warp toggles WITH the region (authoritative, like
            // Relief 3D below): reset it off first, then ApplyTo restores it for
            // a region that saved a warp. So loading any region without a warp
            // block turns the swirl off instead of leaking the prior state.
            p.DomainWarpEnabled = false;

            // Multi-type video roadmap P1 (#91): overlay the snapshotted core
            // per-family params (Julia constant, Newton exponent, Apollonian
            // knobs, …) captured at save time. No-op for legacy regions (null
            // Params) and for Mandelbrot. Applied last so it wins over defaults
            // but sits alongside the source-compiled types above.
            region.Params?.ApplyTo(p);

            // Relief 3D (2D heightfield / Oblique raymarch). Authoritative on
            // recall so relief toggles WITH the region: a relief region restores
            // its saved view, a plain region turns relief off. Unless the user
            // locked relief ("Lock Relief 3D") — then leave the current state.
            if (!RegionReliefLocked)
                region.ApplyRelief3DAuthoritative(p);
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
        public bool GetRegionLightingIsAuthoritative(string regionName)
        {
            if (string.IsNullOrEmpty(regionName)) return false;
            var region = FractalRegionLibrary.Instance.All.FirstOrDefault(
                r => string.Equals(r.Name, regionName, StringComparison.Ordinal))
                ?? FractalRegionLibrary.Instance.AllSlideshowRegions.FirstOrDefault(
                    r => string.Equals(r.Name, regionName, StringComparison.Ordinal));
            return region?.LightingIsAuthoritative ?? false;
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
        public bool TryGetRegionCuratedThemeToApply(string regionName, out string themeName)
        {
            themeName = string.Empty;
            if (string.IsNullOrWhiteSpace(regionName)) return false;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            if (r == null || !r.UseCuratedThemesOnly || r.CuratedThemes == null) return false;

            // First curated name that still resolves to a real theme; unknown
            // names (deleted themes) are skipped rather than applied. Legacy
            // names (e.g. the old "Acid Warp Spectrum") are mapped forward so
            // saved regions keep resolving, and the *current* name is returned
            // (the theme combo only holds current names).
            var known = new HashSet<string>(EnumerateThemeNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var raw in r.CuratedThemes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (known.Contains(raw)) { themeName = raw; return true; }
                var aliased = LegacyNameAliases.Resolve(raw);
                if (aliased != null && known.Contains(aliased)) { themeName = aliased; return true; }
            }
            return false;
        }

        /// <inheritdoc/>
        public bool? GetRegionCycleEnabled(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            return r?.PaletteCycleEnabled;
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

        // ── Scene Engine Roadmap Phase S5 — Scene persistence (SceneLibrary) ──

        /// <inheritdoc/>
        public IReadOnlyList<string> EnumerateSceneNames()
        {
            var lib = SceneLibrary.Instance;
            var list = new List<string>(lib.Scenes.Count);
            foreach (var s in lib.Scenes) list.Add(s.Name);
            return list;
        }

        /// <inheritdoc/>
        public FracturingFog.Abstractions.Animation.SceneData? GetScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return null;
            return SceneLibrary.Instance.GetByName(sceneName);
        }

        /// <inheritdoc/>
        public bool SceneExistsInLibrary(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return false;
            return SceneLibrary.Instance.GetByName(sceneName) != null;
        }

        /// <inheritdoc/>
        public bool SaveScene(FracturingFog.Abstractions.Animation.SceneData scene)
        {
            if (scene == null || string.IsNullOrWhiteSpace(scene.Name)) return false;
            return SceneLibrary.Instance.ReplaceOrAdd(scene);
        }

        /// <inheritdoc/>
        public bool DeleteScene(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return false;
            return SceneLibrary.Instance.Remove(sceneName);
        }

        public bool SaveCurrentAsRegion(string regionName, FractalViewState state, WatermarkDef? embeddedWatermark = null, string? animationName = null, System.Collections.Generic.IReadOnlyList<FracturingFog.Audio.AudioParamBinding>? audioBindings = null)
        {
            if (string.IsNullOrWhiteSpace(regionName) || state == null) return false;

            // Refuse to clobber a built-in. User-defined regions get
            // replace-by-name semantics — last save wins.
            var existing = FractalRegionLibrary.Instance.All
                .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.Ordinal));
            if (existing != null && existing.IsBuiltIn) return false;
            if (existing != null) FractalRegionLibrary.Instance.UserRegions.Remove(existing);

            var region = BuildGeometryFromLiveState(state);
            region.Name = regionName;
            region.Description = "";
            region.EmbeddedWatermark = embeddedWatermark?.Clone();
            region.AnimationName = string.IsNullOrWhiteSpace(animationName) ? null : animationName;
            // #268 — persist audio→param bindings so this region's audio reactivity
            // comes back on recall. Null / empty leaves the region audio-clean.
            region.AudioBindings = (audioBindings != null && audioBindings.Count > 0)
                ? new System.Collections.Generic.List<FracturingFog.Audio.AudioParamBinding>(audioBindings)
                : null;

            FractalRegionLibrary.Instance.UserRegions.Add(region);
            FractalRegionLibrary.Instance.Save();
            return true;
        }

        /// <inheritdoc/>
        public System.Collections.Generic.IReadOnlyList<FracturingFog.Audio.AudioParamBinding>? GetRegionAudioBindings(string regionName)
        {
            if (string.IsNullOrWhiteSpace(regionName)) return null;
            var r = FractalRegionLibrary.Instance.All
                .FirstOrDefault(x => string.Equals(x.Name, regionName, StringComparison.OrdinalIgnoreCase));
            return r?.AudioBindings;
        }

        /// <summary>
        /// Build a fresh user-defined region carrying geometry, quality, fractal
        /// type, per-engine source identity, the Relief 3D snapshot, AND the
        /// Lighting &amp; FX snapshot captured from the <b>live</b>
        /// <paramref name="state"/>. Free-text metadata (Name, Description,
        /// animation, curated themes, watermark) is left at defaults for the
        /// caller to fill. Shared by <see cref="SaveCurrentAsRegion"/> and the
        /// Region Editor's "Capture current view" (Phase R3).
        /// </summary>
        private static FractalRegion BuildGeometryFromLiveState(FractalViewState state)
        {
            var p = state.FractalParameters;
            return new FractalRegion
            {
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
                // Multi-type video roadmap P1 (#91): snapshot the core per-family
                // params for zoomable-2D non-Mandelbrot regions so an unattended
                // video-slideshow leg reconstructs the exact look (Julia constant,
                // Newton exponent, etc.). Null for Mandelbrot + default-suffices
                // families. Recall applies it in LoadRegionFractalParams.
                Params = RegionFractalParams.Snapshot(state.FractalType, p),
                // Relief 3D (2D heightfield / Oblique raymarch) snapshot — restores
                // the full relief look on recall (camera, tone curve, isolation,
                // mesh knobs). Null when relief is off, so plain 2D regions stay clean.
                Relief3D = Relief3DSettings.Snapshot(p),
                // Lighting & FX (VL / fog / AO / lights / sky) snapshot. A region
                // is a look snapshot, so — like a saved shot in a 3D app — it
                // captures the scene lighting, not just geometry. Recall restores
                // it (see FractalRegion.ApplyLightingTo / ShellViewModel jump).
                // Auto-capture: what you see is what saves.
                LightingOverride = p != null
                    ? LightingFxPresetData.FromFx(p.Lighting)
                    : null,
            };
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
                UseCuratedThemesOnly = r.UseCuratedThemesOnly,
                LightingIsAuthoritative = r.LightingIsAuthoritative,
                // Reflect the saved toggle, or the type default when the region
                // carries no opinion, so the editor checkbox opens in the state
                // recall would actually use.
                CycleEnabled = r.PaletteCycleEnabled ?? (r.FractalType == FractalType.AcidWarp),
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
            => UpdateRegionMetadata(edits, null);

        /// <inheritdoc/>
        public RegionUpdateResult UpdateRegionMetadata(RegionEditModel edits, FractalViewState? recaptureGeometryFrom)
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

            // Geometry source: by default preserve the saved region's geometry
            // + per-engine source fields (metadata-only edit, camera unmoved).
            // Phase R3 "Capture current view" passes the live view state to
            // re-snap geometry instead, so the user can retag metadata and
            // re-frame in a single edit.
            var region = recaptureGeometryFrom != null
                ? BuildGeometryFromLiveState(recaptureGeometryFrom)
                : CloneRegionGeometry(source);
            region.RegionType  = RegionType.UserDefined;
            region.Name        = newName;
            region.Description  = edits.Description ?? string.Empty;
            region.AnimationName = string.IsNullOrWhiteSpace(edits.AnimationName) ? null : edits.AnimationName;
            region.CuratedThemes = (edits.CuratedThemes != null && edits.CuratedThemes.Count > 0)
                ? new List<string>(edits.CuratedThemes)
                : null;
            // Only meaningful when a curated pool exists; drop the flag when the
            // whitelist is empty so a region can't claim "curated only" with none.
            region.UseCuratedThemesOnly = edits.UseCuratedThemesOnly && region.CuratedThemes != null;
            // VLAO audit #295 — persist the authoritative-lighting opt-in.
            region.LightingIsAuthoritative = edits.LightingIsAuthoritative;
            // Persist the Cycle toggle only for Acid Fog regions (the only place
            // the editor surfaces it); other types stay null so recall uses the
            // type default and JSON stays clean.
            region.PaletteCycleEnabled = region.FractalType == FractalType.AcidWarp
                ? edits.CycleEnabled
                : (bool?)null;
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
            // Preserve the captured snapshots on a metadata-only edit (no
            // recapture) — otherwise renaming/retagging a region silently drops
            // its per-family params and Relief 3D view.
            Params   = src.Params,
            Relief3D = src.Relief3D,
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
                // #27 Phase 0 — mark cross-user imports untrusted so applying a
                // raw-C# UserEquation/UserBulb source stamps ExternalFile and the
                // gate refuses it. NB: the flag is runtime-only ([JsonIgnore]);
                // once persisted into the user's own regions.json it is treated
                // as a trusted local region on the next launch. Phases 1-3 remove
                // the raw-C# path entirely and close that reload gap.
                region.ExternalOrigin = true;
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
                // Root shape decides: '[' = the library form ExportUserThemesToFile
                // writes, anything else = one bare theme object (what the Asset
                // Manager's per-row export produces). Accepting both means a
                // single-theme export round-trips through this importer.
                if (text.TrimStart().StartsWith("["))
                {
                    imported = JsonSerializer.Deserialize<List<ColorThemeData>>(
                        text, UserColorThemeLibrary.BuildJsonOptions());
                }
                else
                {
                    var one = JsonSerializer.Deserialize<ColorThemeData>(
                        text, UserColorThemeLibrary.BuildJsonOptions());
                    imported = one == null ? null : new List<ColorThemeData> { one };
                }
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

                // Relief 3D: the live view applies the heightfield-relief
                // post-pass in FractalRenderHost.UploadProcessedBuffer, but this
                // standalone offscreen render skipped it — so a slideshow
                // cross-fade INTO a relief region blended the FLAT 2D escape-time
                // image and then hard-popped to the 3D relief on commit (and OUT
                // faded away the relief the same way). Reapply the region's saved
                // relief here so the fade carries the same 3D look it commits to.
                // CPU raymarch oracle (no GPU relief kernel on this offscreen
                // path) — one-shot per leg on the engine's background thread.
                var rp = new FractalParameters();
                region.ApplyRelief3DAuthoritative(rp);
                if (rp.Relief2DEnabled
                    && calc is IHeightFieldSource hfs
                    && hfs.SmoothBuffer is { } field && field.Length >= width * height)
                {
                    var reliefDst = new uint[copy.Length];
                    if (rp.Relief2DRaymarch)
                        FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.Render(
                            copy, field, width, height, rp, reliefDst);
                    else
                        FracturingFog.Rendering.Lighting.HeightfieldRelief2D.Apply(
                            copy, reliefDst, field, width, height, rp);
                    return reliefDst;
                }
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
