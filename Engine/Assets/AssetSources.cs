// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Assets/AssetSources.cs
//
// Asset Manager (Animation Roadmap Sub-goal A, phase A0) — one thin IAssetSource
// adapter per library singleton. Each wraps the singleton's existing enumerate +
// remove paths; no new persistence, no engine touch. See
// Docs/Technical/AssetManager-DevPlan.md.
//
// Adapters live in Engine (not Abstractions) because three of the eight stores
// — FractalRegionLibrary, UserColorThemeLibrary, AnimationLibrary — are Engine
// types, and Engine already references Abstractions where the other five live.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Models;

namespace FracturingFog.Assets
{
    /// <summary>Shared helpers for the adapters.</summary>
    internal static class AssetSizing
    {
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        // Approximate per-asset "size on disk": the stores pack many assets
        // into one JSON file, so there is no per-asset file to stat. Serialized
        // byte length of the single entry is the closest cheap proxy.
        public static long Bytes<T>(T entry)
        {
            try { return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry)); }
            catch { return 0; }
        }

        // Standalone indented JSON for one entry (bulk-export bundle, A3).
        public static string? Json<T>(T? entry) where T : class
        {
            if (entry == null) return null;
            try { return JsonSerializer.Serialize(entry, Indented); }
            catch { return null; }
        }

        // Parse one entry back from standalone JSON (bundle import). Null on
        // blank / malformed input rather than throwing.
        public static T? Parse<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<T>(json); }
            catch { return null; }
        }

        // Insert-or-replace one entry into a list-backed store by case-insensitive
        // name, honouring the overwrite flag, then persist via <paramref name="save"/>.
        // Preserves every field of the deserialized entry (the store's own
        // SaveEquation helpers often carry only a subset), which matters for
        // round-tripping flags like Promoted / Kind / chain.
        public static AssetImportResult Upsert<T>(
            System.Collections.Generic.IList<T> list, T entry, string name,
            System.Func<T, string> nameOf, System.Action save, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(name)) return AssetImportResult.Fail;

            for (int i = 0; i < list.Count; i++)
            {
                if (nameOf(list[i]).Equals(name, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, name);
                    list[i] = entry;
                    save();
                    return new AssetImportResult(AssetImportStatus.Replaced, name);
                }
            }

            list.Add(entry);
            save();
            return new AssetImportResult(AssetImportStatus.Added, name);
        }
    }

    public sealed class RegionAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.Region;
        public string DisplayName => "Regions";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var r in FractalRegionLibrary.Instance.UserRegions)
                yield return new AssetDescriptor(r.Name, Kind, null, AssetSizing.Bytes(r), null);
        }

        public bool Delete(string name) => FractalRegionLibrary.Instance.RemoveUserRegion(name);

        public string? ExportJson(string name) => AssetSizing.Json(
            FractalRegionLibrary.Instance.UserRegions
                .FirstOrDefault(r => r.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var r = AssetSizing.Parse<FractalRegion>(json);
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) return AssetImportResult.Fail;
            r.RegionType = RegionType.UserDefined; // imported regions are always user assets
            var lib = FractalRegionLibrary.Instance;
            return AssetSizing.Upsert(lib.UserRegions, r, r.Name, x => x.Name, lib.Save, overwrite);
        }
    }

    public sealed class ColorThemeAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.ColorTheme;
        public string DisplayName => "Colour themes";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            // User-saved, data-driven themes first: their gradient Stops fully
            // describe a swatch, so ThemeSwatch rasterises it eagerly (few of
            // them, cheap).
            foreach (var t in UserColorThemeLibrary.Instance.Themes)
                yield return new AssetDescriptor(t.Name, Kind, null, AssetSizing.Bytes(t), ThemeSwatch.RenderPng(t));

            // Then the built-in curated roster (ColorPalette.BuiltIns) — the same
            // themes shown in the toolbar combo. These are C# IColorMap classes,
            // not library entries: no Stops to rasterise and nothing on disk to
            // delete or export, so they're read-only and carry no size. Their
            // swatch comes from sampling the map (ColorMapStrip), handed as a lazy
            // factory so the ~250 built-ins only rasterise as rows scroll into
            // view rather than all up front on Enumerate.
            foreach (var map in ColorPalette.BuiltIns)
            {
                string name = ColorPalette.GetStaticName(map);
                if (string.IsNullOrEmpty(name)) continue;
                var captured = map;
                yield return new AssetDescriptor(
                    name, Kind, null, 0, ThumbnailBytes: null,
                    ReadOnly: true,
                    ThumbnailFactory: () => ColorMapStrip.RenderPng(captured));
            }
        }

        // Only user-library themes are removable; a built-in name has no JSON
        // entry, so Remove returns false for it (nothing deleted).
        public bool Delete(string name) => UserColorThemeLibrary.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(
            UserColorThemeLibrary.Instance.Themes
                .FirstOrDefault(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var t = AssetSizing.Parse<ColorThemeData>(json);
            if (t == null || string.IsNullOrWhiteSpace(t.Name)) return AssetImportResult.Fail;
            var lib = UserColorThemeLibrary.Instance;
            bool exists = lib.Themes.Any(x => x.Name.Equals(t.Name, System.StringComparison.OrdinalIgnoreCase));
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, t.Name);
            lib.ReplaceOrAdd(t); // persists
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, t.Name);
        }
    }

    public sealed class AnimationAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.Animation;
        public string DisplayName => "Animations";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var a in AnimationLibrary.Instance.Animations)
                yield return new AssetDescriptor(a.Name, Kind, null, AssetSizing.Bytes(a), null);
        }

        public bool Delete(string name) => AnimationLibrary.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(AnimationLibrary.Instance.GetByName(name));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var a = AssetSizing.Parse<AnimationData>(json);
            if (a == null || string.IsNullOrWhiteSpace(a.Name)) return AssetImportResult.Fail;
            var lib = AnimationLibrary.Instance;
            bool exists = lib.GetByName(a.Name) != null;
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, a.Name);
            lib.ReplaceOrAdd(a); // persists
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, a.Name);
        }
    }

    public sealed class UserEquationAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.UserEquation;
        public string DisplayName => "User equations";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var e in UserEquationStore.Instance.Equations)
                yield return new AssetDescriptor(e.Name, Kind, null, AssetSizing.Bytes(e), null);
        }

        public bool Delete(string name) => UserEquationStore.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(UserEquationStore.Instance.GetByName(name));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var e = AssetSizing.Parse<UserEquationEntry>(json);
            if (e == null) return AssetImportResult.Fail;
            var store = UserEquationStore.Instance;
            return AssetSizing.Upsert(store.Equations, e, e.Name, x => x.Name, store.Save, overwrite);
        }
    }

    public sealed class SandboxEquationAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.SandboxEquation;
        public string DisplayName => "Sandbox sources";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var e in SandboxEquationStore.Instance.Equations)
                yield return new AssetDescriptor(e.Name, Kind, null, AssetSizing.Bytes(e), null);
        }

        public bool Delete(string name) => SandboxEquationStore.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(SandboxEquationStore.Instance.GetByName(name));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var e = AssetSizing.Parse<SandboxEquationEntry>(json);
            if (e == null) return AssetImportResult.Fail;
            var store = SandboxEquationStore.Instance;
            return AssetSizing.Upsert(store.Equations, e, e.Name, x => x.Name, store.Save, overwrite);
        }
    }

    public sealed class UserBulbAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.UserBulb;
        public string DisplayName => "UserBulb sources";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var e in UserBulbStore.Instance.Equations)
                yield return new AssetDescriptor(e.Name, Kind, null, AssetSizing.Bytes(e), null);
        }

        public bool Delete(string name) => UserBulbStore.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(UserBulbStore.Instance.GetByName(name));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var e = AssetSizing.Parse<UserBulbEntry>(json);
            if (e == null) return AssetImportResult.Fail;
            var store = UserBulbStore.Instance;
            return AssetSizing.Upsert(store.Equations, e, e.Name, x => x.Name, store.Save, overwrite);
        }
    }

    /// <summary>SlideshowConfigLibrary is a static file gateway, not a live
    /// singleton list — this adapter loads the preset file on each call. The
    /// "Default" preset is undeletable (the library guarantees one always
    /// exists), so Delete returns false for it.</summary>
    public sealed class SlideshowConfigAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.SlideshowConfig;
        public string DisplayName => "Slideshow configs";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            var file = SlideshowConfigLibrary.Load();
            foreach (var c in file.Configs)
                yield return new AssetDescriptor(c.Name, Kind, null, AssetSizing.Bytes(c), null);
        }

        public bool Delete(string name)
        {
            var file = SlideshowConfigLibrary.Load();
            return SlideshowConfigLibrary.Delete(file, name);
        }

        public string? ExportJson(string name) => AssetSizing.Json(
            SlideshowConfigLibrary.Load().Configs
                .FirstOrDefault(c => c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var c = AssetSizing.Parse<SlideshowConfig>(json);
            if (c == null || string.IsNullOrWhiteSpace(c.Name)) return AssetImportResult.Fail;
            var file = SlideshowConfigLibrary.Load();
            bool exists = file.Configs.Any(x => x.Name.Equals(c.Name, System.StringComparison.OrdinalIgnoreCase));
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, c.Name);
            SlideshowConfigLibrary.Upsert(file, c); // persists; also marks imported preset active
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, c.Name);
        }
    }

    /// <summary>Scene Engine Roadmap Phase S5 — the Scene asset node. Wraps
    /// <see cref="SceneLibrary"/>. Unlike the shared <see cref="AssetSizing"/>
    /// helpers (plain options), Scenes serialise through
    /// <see cref="SceneLibrary.BuildJsonOptions"/> so the nested S3
    /// <c>CameraTrack</c> and the <c>SceneTransitionKind</c> enums round-trip as
    /// human-editable strings, matching scenes.json. Built-in demo scenes
    /// enumerate too; deleting one drops it until the next <c>Load()</c>
    /// re-merges the seed (same as the Animation built-in).</summary>
    public sealed class SceneAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.Scene;
        public string DisplayName => "Scenes";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var s in SceneLibrary.Instance.Scenes)
                yield return new AssetDescriptor(s.Name, Kind, null, SceneBytes(s), null);
        }

        public bool Delete(string name) => SceneLibrary.Instance.Remove(name);

        public string? ExportJson(string name)
        {
            var scene = SceneLibrary.Instance.GetByName(name);
            if (scene == null) return null;
            try { return JsonSerializer.Serialize(scene, SceneLibrary.BuildJsonOptions()); }
            catch { return null; }
        }

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(json)) return AssetImportResult.Fail;
            SceneData? s;
            try { s = JsonSerializer.Deserialize<SceneData>(json, SceneLibrary.BuildJsonOptions()); }
            catch { return AssetImportResult.Fail; }
            if (s == null || string.IsNullOrWhiteSpace(s.Name)) return AssetImportResult.Fail;

            var lib = SceneLibrary.Instance;
            bool exists = lib.GetByName(s.Name) != null;
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, s.Name);
            lib.ReplaceOrAdd(s); // persists
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, s.Name);
        }

        // Size proxy through the library's own (enum-aware) options.
        private static long SceneBytes(SceneData s)
        {
            try { return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(s, SceneLibrary.BuildJsonOptions())); }
            catch { return 0; }
        }
    }

    /// <summary>Window-arrangement workspaces (#433). Like
    /// <see cref="SlideshowConfigAssetSource"/>, <see cref="WorkspaceLayoutLibrary"/>
    /// is a static file gateway, not a live singleton list — this adapter loads
    /// the workspace file on each call. Every workspace is user-created, so all
    /// are deletable/exportable (no built-ins, no undeletable "Default").</summary>
    public sealed class WorkspaceAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.Workspace;
        public string DisplayName => "Workspaces";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            var file = WorkspaceLayoutLibrary.Load();
            foreach (var w in file.Layouts)
                yield return new AssetDescriptor(w.Name, Kind, null, AssetSizing.Bytes(w), null);
        }

        public bool Delete(string name)
        {
            var file = WorkspaceLayoutLibrary.Load();
            return WorkspaceLayoutLibrary.Delete(file, name);
        }

        public string? ExportJson(string name) => AssetSizing.Json(
            WorkspaceLayoutLibrary.Load().Layouts
                .FirstOrDefault(w => w.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var w = AssetSizing.Parse<WorkspaceLayout>(json);
            if (w == null || string.IsNullOrWhiteSpace(w.Name)) return AssetImportResult.Fail;
            var file = WorkspaceLayoutLibrary.Load();
            bool exists = file.Layouts.Any(x => x.Name.Equals(w.Name, System.StringComparison.OrdinalIgnoreCase));
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, w.Name);
            WorkspaceLayoutLibrary.Upsert(file, w); // persists; also marks imported workspace active
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, w.Name);
        }
    }

    /// <summary>Volumetric Lighting &amp; FX presets (#580). Like
    /// <see cref="SlideshowConfigAssetSource"/> / <see cref="WorkspaceAssetSource"/>,
    /// <see cref="LightingFxPresetLibrary"/> is a static file gateway, not a live
    /// singleton list — this adapter loads the preset file on each call. Every
    /// preset is user-created, so all are deletable/exportable (no built-ins).</summary>
    public sealed class LightingFxAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.LightingFx;
        public string DisplayName => "Lighting & FX";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            var file = LightingFxPresetLibrary.Load();
            foreach (var p in file.Presets)
                yield return new AssetDescriptor(p.Name, Kind, null, AssetSizing.Bytes(p), null);
        }

        public bool Delete(string name)
        {
            var file = LightingFxPresetLibrary.Load();
            return LightingFxPresetLibrary.Delete(file, name);
        }

        public string? ExportJson(string name)
            => LightingFxPresetLibrary.ExportJson(LightingFxPresetLibrary.Load(), name);

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var p = LightingFxPresetLibrary.ParseOne(json);
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return AssetImportResult.Fail;

            var file = LightingFxPresetLibrary.Load();
            bool exists = LightingFxPresetLibrary.Get(file, p.Name) != null;
            if (exists && !overwrite) return new AssetImportResult(AssetImportStatus.SkippedExists, p.Name);
            LightingFxPresetLibrary.Upsert(file, p); // persists; marks imported preset active
            return new AssetImportResult(exists ? AssetImportStatus.Replaced : AssetImportStatus.Added, p.Name);
        }
    }

    public sealed class WatermarkAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.Watermark;
        public string DisplayName => "Watermarks";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var w in UserWatermarkStore.Instance.Watermarks)
                yield return new AssetDescriptor(w.Name, Kind, null, AssetSizing.Bytes(w), null);
        }

        public bool Delete(string name) => UserWatermarkStore.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(UserWatermarkStore.Instance.GetByName(name));

        public AssetImportResult ImportJson(string json, bool overwrite)
        {
            var w = AssetSizing.Parse<WatermarkDef>(json);
            if (w == null) return AssetImportResult.Fail;
            var store = UserWatermarkStore.Instance;
            return AssetSizing.Upsert(store.Watermarks, w, w.Name, x => x.Name, store.Save, overwrite);
        }
    }
}
