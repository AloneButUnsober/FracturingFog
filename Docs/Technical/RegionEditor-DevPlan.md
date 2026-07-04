# Region Editor UI — Dev Plan

> Companion pages: [Technical Index](_Index.md) · [Animation Roadmap](../Animation-Roadmap.md) (Sub-goal B) · [Asset Manager Dev Plan](AssetManager-DevPlan.md) (Sub-goal A, deferred) · [Regions Guide](../User/Regions-Guide.md)

**Status: IN PROGRESS.** Source design: [Animation Roadmap §Sub-goal B](../Animation-Roadmap.md).
Sibling [Asset Manager](AssetManager-DevPlan.md) is deferred and will
reuse this editor as its Region detail-pane.

---

## Problem

Regions are *saved* (name-prompt dialog) and *recalled* (combo pick),
but cannot be *edited in place*. To rename a region, retag its curated
themes, attach an animation, or drop a stale lighting override, the user
must delete and re-save — which forces recapturing the live view
geometry and loses any metadata they don't re-enter.

Concretely, [`HostColorThemeService.SaveCurrentAsRegion`](../../Hosting/HostColorThemeService.cs)
always rebuilds the region from the **live** `FractalViewState`. There is
no path that edits a saved region's metadata while **preserving** its
stored geometry (Center / Zoom / Iterations / QD limbs).

## Goal

A real edit-in-place path for saved regions:

- Right-click / "Edit" a region → dialog pre-populated from the **saved**
  record, geometry preserved.
- Edit metadata: Name, Description, attached Animation, CuratedThemes
  whitelist, keep/clear LightingOverride, keep/clear EmbeddedWatermark.
- Geometry (Center, Zoom, Iterations) is **not** free-text editable — it
  is shown read-only, with an optional "Capture current view" to re-snap
  it from the live view (parallels the save flow). *(Capture button is a
  Phase R3 nicety, not required for MVP.)*
- Built-in regions are immutable: editing one clones it into a new
  user-region (user picks a new name), leaving the built-in untouched.

## Existing pieces we build on

- [`FractalRegion`](../../Engine/Models/FractalRegion.cs) — editable
  metadata fields already exist: `Name`, `Description`, `AnimationName`,
  `CuratedThemes`, `LightingOverride`, `EmbeddedWatermark`, `FractalType`.
- [`FractalRegionLibrary`](../../Engine/Models/FractalRegion.cs) —
  `UserRegions`, `AddUserRegion`, `RemoveUserRegion`, `FindByName`,
  `Save`. Replace-by-name = remove + add.
- [`HostColorThemeService`](../../Hosting/HostColorThemeService.cs) —
  `SaveCurrentAsRegion` already does replace-by-name + built-in-clobber
  refusal; `EnumerateAnimationNames`, `EnumerateRegionNames`.
- Editor-window pattern: `AnimationEditorView` / `ColorThemeEditorView`
  (modeless `Window` + VM, launched from `ShellViewModel`, tracked in
  `MainWindow.axaml.cs`). We mirror it.
- Toolbar has a Theme "Edit" button next to the region/theme combos
  ([`MainWindow.axaml:115`](../../UI.Avalonia/Views/MainWindow.axaml)) —
  we add a Region "Edit" button as the parallel affordance.

---

## Phases (each phase = one commit)

### Phase R0 — service layer (metadata-preserving edit)  ✅

New DTO + two service methods; no UI. Establishes the edit contract and
is unit-testable headless.

- New `RegionEditModel` DTO in `Abstractions/Models/` — editable fields
  plus read-only geometry echo (for display) plus `IsBuiltIn` /
  `OriginalName`.
- `IColorThemeService.GetRegionForEdit(string name) : RegionEditModel?`
  — snapshots a saved (or built-in) region into the DTO.
- `IColorThemeService.UpdateRegionMetadata(RegionEditModel edits) : RegionUpdateResult`
  — writes metadata back onto the existing user region, **preserving its
  stored geometry**; built-in `OriginalName` → clone to a new user
  region; refuses name collisions with a *different* existing region.
- `HostColorThemeService` implements both against `FractalRegionLibrary`.
- Tests in `Server.Tests`: preserve-geometry round-trip, rename,
  built-in clone-on-edit, name-collision refusal, animation attach/detach.

### Phase R1 — editor VM + view

- `RegionEditorViewModel` (UI.Avalonia/ViewModels) — binds the DTO,
  exposes animation-name + theme-name pick lists from the service, Save /
  Cancel commands, validation (non-empty name, collision preflight),
  keep/clear toggles for lighting + watermark.
- `RegionEditorView.axaml` (UI.Avalonia/Views) — modeless `Window`
  mirroring `AnimationEditorView` chrome. Read-only geometry block;
  editable metadata rows; Save/Cancel.

### Phase R2 — wire entry point

- Region "Edit" button on the toolbar (parallel to Theme Edit), enabled
  when a region is selected.
- `ShellViewModel.ShowRegionEditor(name)` — mirror `ShowAnimationEditor`:
  build VM from `GetRegionForEdit`, wire `RegionSavedToLibrary` →
  refresh region combo + reselect, `CloseRequested` → hide.
- `MainWindow.axaml.cs` — track the `RegionEditorView` window instance
  like `_animationEditorWin`.
- Built-in selected → editor opens in clone mode (name cleared / suffixed,
  Save always creates a new user region).

### Phase R3 — nicety: "Capture current view" (optional)

- Button in the editor that re-snaps geometry from the live
  `FractalViewState` (reuses the save-flow capture), so users can both
  retag metadata *and* update the framing in one edit. Deferred until
  R0–R2 land.

---

## Risk

Low. Persistence shape unchanged; library already supports add/remove;
only a "replace-by-name preserving geometry" service path + one dialog
are new. Built-ins stay immutable by construction (clone-on-edit).

## Open questions

- **CuratedThemes editing UI.** Full multi-select against the theme
  library, or a comma list for MVP? Lean: checklist against
  `EnumerateThemeNames` in R1, but ship a simple list first if it bloats.
- **Rename ripple.** Regions are referenced by name from slideshow
  configs (`FilterFractalTypes` is by type, not name — low risk) and the
  toolbar's last-selected. Rename updates the combo; no back-references
  to rewrite today, but note it here in case slideshow gains
  by-name region whitelists later.
