# Asset Manager UI — Dev Plan

> Companion pages: [Technical Index](_Index.md) · [Animation Roadmap](../Animation-Roadmap.md) (Sub-goal A) · [Region Editor Dev Plan](RegionEditor-DevPlan.md) (Sub-goal B) · [Regions Guide](../User/Regions-Guide.md)

**Status: DEFERRED.** Tracking doc only — no code has shipped and none is
in progress. This plan captures the design so future-us doesn't
re-derive it. Region Editor ([Sub-goal B](RegionEditor-DevPlan.md)) is
being built first; Asset Manager builds *on top of* it (Region rows in
the Asset Manager route to the Region Editor as their detail-pane
editor). Do not start this until Region Editor has shipped.

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
| A3    | Bulk ops — multi-select → export-as-bundle (zip of JSON)    | low  | half-day   |

A2 depends on Region Editor for the Region row route. A3 is optional.

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
