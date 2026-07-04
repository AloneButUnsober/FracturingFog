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
using System.Text;
using System.Text.Json;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Models;

namespace FracturingFog.Assets
{
    /// <summary>Shared helpers for the adapters.</summary>
    internal static class AssetSizing
    {
        // Approximate per-asset "size on disk": the stores pack many assets
        // into one JSON file, so there is no per-asset file to stat. Serialized
        // byte length of the single entry is the closest cheap proxy.
        public static long Bytes<T>(T entry)
        {
            try { return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry)); }
            catch { return 0; }
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
    }
}
