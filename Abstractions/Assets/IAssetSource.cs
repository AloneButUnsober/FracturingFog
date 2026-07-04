// Abstractions/Assets/IAssetSource.cs
//
// Asset Manager (Animation Roadmap Sub-goal A) core contract. One top-level
// "everything I've saved" view enumerates every saved asset across every type
// through these adapters. See Docs/Technical/AssetManager-DevPlan.md.
//
// UI-free by design: this is the data side only. Routing a row to its type's
// editor ("Open") is a UI concern and lives in the Avalonia shell (A2) via a
// router keyed on AssetKind — not on this interface, so the contract stays in
// Abstractions and is unit-testable without a UI.

using System;
using System.Collections.Generic;

namespace FracturingFog.Abstractions.Assets
{
    /// <summary>Every saved-asset type the Asset Manager surfaces. Ordering
    /// matches the left-hand type tree in the dev-plan design sketch.</summary>
    public enum AssetKind
    {
        Region = 0,
        ColorTheme = 1,
        Animation = 2,
        UserEquation = 3,
        SandboxEquation = 4,
        UserBulb = 5,
        SlideshowConfig = 6,
        Watermark = 7,
    }

    /// <summary>One row in the Asset Manager's middle list. <paramref name="SizeOnDisk"/>
    /// is an approximation (serialized byte length of the single entry) since
    /// the underlying stores pack many assets into one JSON file — there is no
    /// per-asset file to stat. <paramref name="CreatedAt"/> is null when the
    /// source model tracks no timestamp (all of them, today). Thumbnails are a
    /// deferred follow-up (dev-plan open question) — always null for now.</summary>
    public sealed record AssetDescriptor(
        string Name,
        AssetKind Kind,
        DateTime? CreatedAt,
        long SizeOnDisk,
        byte[]? ThumbnailBytes);

    /// <summary>Thin adapter over one library singleton. Most singletons already
    /// enumerate by name, so adapters are trivial wrappers.</summary>
    public interface IAssetSource
    {
        /// <summary>Which asset type this source enumerates.</summary>
        AssetKind Kind { get; }

        /// <summary>Human-readable heading for the type tree (e.g. "Colour themes").</summary>
        string DisplayName { get; }

        /// <summary>Snapshot of the source's current saved assets. Callers must
        /// not assume live-updating — re-call after a save event.</summary>
        IEnumerable<AssetDescriptor> Enumerate();

        /// <summary>Delete the named asset through the source's own remove path
        /// (which persists). Returns true when an asset was removed.</summary>
        bool Delete(string name);
    }
}
