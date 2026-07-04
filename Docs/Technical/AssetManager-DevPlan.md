# Asset Manager UI — Dev Plan

> Companion pages: [Technical Index](_Index.md) · [Animation Roadmap](../Animation-Roadmap.md) (Sub-goal A) · [Region Editor Dev Plan](RegionEditor-DevPlan.md) (Sub-goal B) · [Regions Guide](../User/Regions-Guide.md)

**Status: SHIPPED (A0–A3).** All four phases have landed on
`feature/cross-platform-full`. Region rows route to the Region Editor
([Sub-goal B](RegionEditor-DevPlan.md)) as designed. The sections below are
retained as the design record; see "Implementation notes" at the end for how
the shipped code maps to this plan.

Source of this design: [Animation Roadmap §Sub-goal A](../Animation-Roadmap.md).

---

## Why deferred

- **Depends on Region Editor.** The Asset Manager is a *router*, not an
  editor — each row opens the type's own editor in the detail pane.
  Regions are the only asset type with no in-place editor today, so the
  Asset Manager can't route Region rows anywhere until Region Editor
  exists.
- **Pure additive UI, no urgency.** Every library singleton already
  exposes its inventory by name; nothing is blocked on this. It can ship
  any time after its one dependency lands.
- **User asked to defer it** in favour of the Region Editor.

---

## Goal

One top-level "show me everything I've saved" view that surfaces every
saved asset across every type and routes each to its own editor. Today
each asset type hides behind its own dialog / sidebar / library
singleton — users hunting an old asset must remember which dialog owns
it.

## Today's state (asset inventory surfaces)

| Asset type        | Library singleton                       | Current surface                     |
|-------------------|-----------------------------------------|-------------------------------------|
| Regions           | `FractalRegionLibrary`                  | Toolbar combo + FloatingMenu        |
| Colour themes     | `UserColorThemeLibrary`                 | `ColorThemeEditorView` window       |
| Animations        | `AnimationLibrary`                      | `AnimationEditorView` window        |
| User equations    | `UserEquationStore`                     | Dialog from menu                    |
| Sandbox sources   | `SandboxEquationStore`                  | Dialog from menu                    |
| UserBulb sources  | `UserBulbStore`                         | Dialog from menu                    |
| Slideshow configs | `SlideshowConfigLibrary`                | Dialog from menu                    |
| Watermarks        | `UserWatermarkStore`                    | Embedded in regions; no manager     |

There is no cross-type view.

---

## Design sketch

Three-pane Avalonia view, VS-Code-Explorer shape:

- **Left** — type tree: Regions / Themes / Animations / Equations /
  Sandbox / UserBulb / SlideshowConfigs / Watermarks.
- **Middle** — filterable list of the selected type's assets.
- **Right** — detail / edit pane. Defers to the type's existing editor
  (Asset Manager is a router, not a new editor).

### `IAssetSource` abstraction

New interface in `Abstractions/`:

```csharp
public interface IAssetSource
{
    AssetKind Kind { get; }
    IEnumerable<AssetDescriptor> Enumerate();
    void Open(string name);           // routes to the type's editor
}

public sealed record AssetDescriptor(
    string Name,
    AssetKind Kind,
    DateTime? CreatedAt,
    long SizeOnDisk,
    byte[]? ThumbnailBytes);
```

Each existing singleton gets a thin one-file adapter implementing
`IAssetSource`. Most already enumerate by name (`EnumerateRegionNames`,
`EnumerateAnimationNames`, …) so the adapters are trivial.

---

## Phasing (each phase = one PR / one commit)

| Phase | Scope                                                        | Risk | Est.       |
|-------|-------------------------------------------------------------|------|------------|
| A0    | `IAssetSource` + `AssetDescriptor` + adapters (no UI)       | low  | half-day   |
| A1    | Read-only three-pane view (list + detail, no editing)       | low  | 1 day      |
| A2    | Edit routing — detail pane opens each type's own editor     | low  | 1 day      |
| A3    | Bulk ops — export-as-bundle + import-bundle (zip of JSON)   | low  | half-day   |

A2 depends on Region Editor for the Region row route. A3 is optional; export and
import ship together (import is the inverse of the same zip format).

## Risk

Low. No persistence changes, no engine touch. Pure UI on top of
existing libraries.

---

## Open questions

- **Thumbnails.** Regions/themes/animations could render a small preview;
  costs a render per asset. Defer to A1 follow-up — ship names-only first.
- **Watermarks have no standalone library today** (embedded in regions +
  `UserWatermarkStore`). Decide whether the manager surfaces the store or
  the per-region embeds. Lean: surface `UserWatermarkStore` only.
- **Live refresh.** If an editor saves while the manager is open, the
  middle list must refresh. Reuse the existing `*SavedToLibrary` events
  each editor VM already raises.

---

## Implementation notes (as shipped)

- **Interface split from the sketch.** `IAssetSource` (in
  `Abstractions/Assets/IAssetSource.cs`) carries `Kind` / `DisplayName` /
  `Enumerate()` / `Delete()` / `ExportJson()` — the data side only. The
  sketch's `Open(string)` was intentionally left off: routing a row to its
  editor is a UI concern (UI.Avalonia can't reference Engine where the editors
  live), so it lives in the shell instead. `AssetDescriptor` matches the sketch;
  `CreatedAt`/`ThumbnailBytes` are always null today, `SizeOnDisk` is a
  serialized-byte approximation (stores pack many assets per JSON file).
- **Adapters + registry** live in `Engine/Assets/` (three of the eight
  singletons are Engine types). `AssetSourceRegistry.All()` is the roster; the
  host injects it into `ShellViewModel` via a new optional ctor param.
- **A1** — `AssetManagerViewModel` + `AssetManagerView` (modeless window,
  opened from the render-surface context menu "Asset Manager…").
- **A2** — `ShellViewModel.EditAsset` routes: Region/Theme/Animation/Watermark
  to their shell-owned editors by name; SlideshowConfig marks the preset active
  and raises `SlideshowSettingsRequested`; the three source editors bubble
  `AssetHostEditorRequested` to `AvaloniaShellBootstrap` (host-owned windows).
- **A3 export** — multi-select → in-memory zip of `<Type>/<name>.json`; the host
  owns the save picker + write via `AssetBundleExportRequested`.
- **A3 import** — the inverse. "Import bundle…" (footer) → `RequestImport()` →
  `AssetBundleImportRequested` to the host, which shows an open picker + a single
  overwrite Yes/No prompt, reads the bytes, and calls back
  `ShellViewModel.ImportAssetBundle(bytes, overwrite)` → `AssetManagerViewModel.
  ImportBundle`. The VM parses the zip, maps each entry's first path segment to an
  `AssetKind`, and routes the JSON to that source's new `IAssetSource.ImportJson
  (json, overwrite)`. Each entry's own stored Name (not the bundle filename) keys
  the store; same-name collisions replace or skip per the flag. Per-entry outcome
  (`AssetImportStatus`: Added / Replaced / SkippedExists / Failed) is tallied into
  an `AssetImportSummary` the host shows back. Full-fidelity round-trip — import
  deserializes the whole entry, so flags the stores' own `SaveEquation` helpers
  drop (Promoted / Kind / bulb chain) survive.
- **Live refresh — DONE.** The four shell-owned editors (Region / Colour theme /
  Animation / Watermark) call `ShellViewModel.RefreshAssetManagerIfVisible()`
  from their `*SavedToLibrary` / delete handlers, so a save/delete while the
  manager is open re-enumerates the middle list immediately. The host-owned
  source editors + slideshow still rely on open-time re-enumeration + the
  Refresh button (no shell save event to hook).
- **Thumbnails — still deferred (rendering feature, not a quick follow-up).**
  Every asset type's real preview needs either a fractal render through the host
  pipeline (regions/animations/equations/bulbs — "a render per asset", the cost
  the plan flagged) or has no meaningful image (equation/sandbox sources are
  text). Colour themes are the only cheap-ish case and even those route their
  preview through the render host today, not a self-contained gradient raster.
  Recommended path when picked up: async host-render into `ThumbnailBytes`
  populated after enumeration (so it never blocks the list), with theme swatches
  as a rasterized-gradient special case; add an image column to the middle list.
