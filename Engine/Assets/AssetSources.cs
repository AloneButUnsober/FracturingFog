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
    }

    public sealed class ColorThemeAssetSource : IAssetSource
    {
        public AssetKind Kind => AssetKind.ColorTheme;
        public string DisplayName => "Colour themes";

        public IEnumerable<AssetDescriptor> Enumerate()
        {
            foreach (var t in UserColorThemeLibrary.Instance.Themes)
                yield return new AssetDescriptor(t.Name, Kind, null, AssetSizing.Bytes(t), null);
        }

        public bool Delete(string name) => UserColorThemeLibrary.Instance.Remove(name);

        public string? ExportJson(string name) => AssetSizing.Json(
            UserColorThemeLibrary.Instance.Themes
                .FirstOrDefault(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)));
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
    }
}
