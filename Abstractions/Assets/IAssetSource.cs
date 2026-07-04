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

    /// <summary>Outcome of importing one asset entry from a bundle (A3 import).</summary>
    public enum AssetImportStatus
    {
        /// <summary>JSON was blank / unparseable / carried no name.</summary>
        Failed = 0,
        /// <summary>New asset added.</summary>
        Added = 1,
        /// <summary>Existing same-name asset overwritten (import ran with overwrite).</summary>
        Replaced = 2,
        /// <summary>Same-name asset already existed and overwrite was off — left untouched.</summary>
        SkippedExists = 3,
    }

    /// <summary>Per-entry import result: the outcome plus the asset name (when known,
    /// for the summary the UI shows).</summary>
    public readonly record struct AssetImportResult(AssetImportStatus Status, string? Name)
    {
        public static readonly AssetImportResult Fail = new(AssetImportStatus.Failed, null);
    }

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

        /// <summary>Serialize the named asset to standalone JSON for the bulk
        /// export bundle (A3). Returns null when the asset no longer exists or
        /// can't be serialized.</summary>
        string? ExportJson(string name);

        /// <summary>Import one asset from standalone JSON (the inverse of
        /// <see cref="ExportJson"/>, for the A3 bundle import). The entry's own
        /// Name (not the bundle filename) keys the store. When an asset of that
        /// name already exists, <paramref name="overwrite"/> decides between
        /// replace and skip. Persists through the store's own save path.</summary>
        AssetImportResult ImportJson(string json, bool overwrite);
    }
}
