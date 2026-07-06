# UI Overhaul Plan — Fracturing Fog (Avalonia shell)

Branch: `feature/ui-overhaul` (off `feature/scene-engine`).
Status: **active**. Started 2026-07-06.

Canonical UI is `UI.Avalonia/` (WinForms deprecated — see root `CLAUDE.md`).
All work here lands in `UI.Avalonia/` only.

---

## 1. Why

The Avalonia UI works but is inefficient, hides functionality, and confuses.
The ~15 reported symptoms trace to **two root causes** plus downstream
organizational debt:

**Root 1 — no shared design system.** Every dialog hand-styles itself.
`FloatingMenuView.axaml` defines its own button classes, label widths, coord
styles. `AvaloniaDialogs.cs` (~1800 lines) builds Poster/Prompt/Video dialogs
*imperatively in C#* with per-control inline `MinWidth` / `Margin`. Nothing is
shared, so the same parameter is a slider in one dialog and a numeric field in
another, fields are too narrow (each author guesses a width), buttons are
non-uniform.
→ Explains: narrow/clipped numeric fields, slider-vs-numeric inconsistency,
non-uniform buttons.

**Root 2 — no window manager.** Each dialog self-shows: `ShowDialog(owner)` vs
`Show()`, `WindowStartupLocation` set per-dialog, scattered Win32
nested-modal `win.Activate()` workarounds. No single owner of placement,
z-order, or screen-fit.
→ Explains: windows opening on-top/under unpredictably, dialogs rendering
off-screen or clipped on small monitors, oversized dialogs.

**Downstream (organizational):** overlong floating menu, overlong main-window
context menu, buried features (Volumetric Lighting FX only reachable via
fractal-params), clunky collapsible dropdowns with short unfilterable lists,
no central jumping-off point.

Fix the roots first, or the reorg just repaints the chaos.

## 2. Goals

Efficient, non-confusing UI; sleek but usable styling; every feature
represented correctly and usably; well-organized windowing; thoughtful editing.
Preserve what the user likes: the minimalist render window ("outboard
controller" paradigm), the HUD + Lighting overlays.

## 3. Chosen direction (decided 2026-07-06)

- **Shell paradigm: Hybrid — shell + poppable panels.** A `SplitView`
  control-center (nav-rail + content) is the default home; every panel can
  detach into a managed, multi-monitor-aware floating window and be recalled
  individually. (`Dock.Avalonia` library rejected in favor of a lighter
  custom shell with full dark-theme control.)
- **Sequencing: foundation-first.** Design-system + WindowService + the
  View→UserControl conversion land and stabilize before the shell is built.
  Each phase ships independently and improves the current UI even pre-shell.

### Architectural rule created by Hybrid

Every feature View must render in **two hosts** (docked panel AND floating
window). Therefore:

> **Views are `UserControl`, never `Window`.** A thin host wraps them: a
> `ContentControl` slot when docked inside the shell, a generic shell `Window`
> (owned by `WindowService`) when popped out.

Today the opposite holds — `FloatingMenuView`, `SlideshowSettingsView`, etc.
are `Window` subclasses. Converting them is the backbone migration (Phase F3),
mechanical and per-view, touching no feature logic.

## 4. What Avalonia gives us (capability notes)

- Docking-like behavior without a lib: `SplitView` nav pane + custom edge-snap.
- Multi-monitor: `Screens` API (`window.Screens.All`, `ScreenFromPoint`).
- Guiding-hand modes: bind `IsBeginnerMode`, drive `Classes` / `IsVisible` on
  advanced sections (show/hide, no duplicate UI).
- Overlays for post-FX: `Popup`/layered panel over the render surface (extend
  existing HUD/Lighting overlay infra).
- Screen-fit: bind dialog `MaxHeight` to `Screen.WorkingArea`.
- Filterable lists: `ListBox`/`TreeView` + filter `TextBox` (replaces the
  clunky collapsible dropdowns, no window-resize jank).
- No native MDI, no UWP-style adaptive VisualStateManager — classes +
  bounds-binding cover the gap.

## 5. Phases

Foundation (F) first, then shell (S). Commit per phase.

### Phase F1 — Design system
- `UI.Avalonia/Themes/ControlThemes.axaml`: shared `ControlTheme` for Button
  (uniform min-width/padding), TextBox, ComboBox, section headers, group
  borders. Merge into `App.axaml`.
- Reusable controls: `LabeledNumericField` (label + numeric auto-sized to fit
  its value range), `LabeledSlider`.
- Canonical rule: bounded ~0–100 params → slider; unbounded/precise → numeric.
- Retrofit `FloatingMenuView` onto the shared resources (local `Window.Styles`
  block deleted; classes now come from the global merge).
- Kills: narrow fields, slider/numeric inconsistency, non-uniform buttons.

**Scope note (decided during F1):** the *infrastructure* — `Tokens.axaml`,
`ControlThemes.axaml`, `LabeledNumericField`, `LabeledSlider`, App wiring — plus
the `FloatingMenuView` de-dup ships as F1. **Per-dialog field retrofit** (moving
each view's cramped numeric fields / inconsistent slider-vs-numeric params onto
`Labeled*`) is **folded into F3**: each view is retrofitted at the same time it
is converted `Window`→`UserControl`, so every view is touched once, not twice.
This keeps F1 low-risk (no behavioral edits to feature dialogs) and avoids a
30-dialog sweep that can't be visually verified in one commit.

Status: **F1 done** (commit pending).

### Phase F2 — WindowService
- `WindowService` (static, `UI.Avalonia/Services/`) — single entry to open any
  window: `ShowDialogAsync` (modal), `Show` (modeless), `Prepare` (treatment
  without showing, for pop-out hosts in F3/S2). Owns owner resolution,
  startup placement, multi-monitor targeting (`Placement.SecondaryMonitor`
  primitive landed; auto-populate policy toggle is S2), screen-fit
  `MaxWidth`/`MaxHeight` clamp, and an on-open position clamp that nudges any
  spilled window back onto its screen.
- All ~14 show call-sites in `AvaloniaDialogs.cs` routed through it; the three
  scattered `win.Activate()` Win32 nested-modal hacks are centralized (deliberate
  `Topmost=true` on the video/recording prompts kept).
- Kills: on-top/under chaos, off-screen-on-small-screen, oversized dialogs.
- Note: content-overflow *inside* a clamped window (dialog taller than screen
  with no internal scroll) is a per-dialog concern handled during F3.

Status: **F2 done** (commit pending). Full solution build green.

### Phase F3 — View → UserControl conversion (+ folded field retrofit)
- Convert each feature View from `Window` to `UserControl`; floating = wrap in
  the generic host window via WindowService. No feature logic changed.
- **While each view is open for conversion**, retrofit its numeric/slider
  fields onto `LabeledNumericField` / `LabeledSlider` (the F1 field-retrofit,
  folded here so every view is touched once).

**Pattern (established on `AppSettingsView`, build green):**
1. `ViewModels/IClosableDialog.cs` — `event EventHandler<bool>? CloseRequested`.
2. `Services/PanelHostWindow.cs` + `PanelHostOptions` — generic pop-out host:
   owns window chrome (title/size/background), Esc-to-close, and wires the
   panel VM's `CloseRequested` → window close.
3. `WindowService.ShowPanelDialogAsync(panel, options, owner)` → `Task<bool?>`.
4. View: `<Window>` → `<UserControl>`, chrome removed (moved to host options);
   `.axaml.cs`: `Window` → `UserControl`, drop self-close + `EscapeCloseBehavior`.
5. VM: add `: IClosableDialog` (event already present on most).
6. Field retrofit: swap bare `NumericUpDown`/slider rows for `LabeledNumericField`
   / `LabeledSlider`.
7. `AvaloniaDialogs` show-helper: build panel, `await ShowPanelDialogAsync`,
   read `vm.Result` after.

**Remaining views to convert** (~19): SlideshowSettings, VideoSettings,
AudioSettings, RegionEditor, SceneEditor, AnimationEditor, FractalParams (+
ParamSections), ColorThemeEditor, ColorGenEditor, WatermarkEditor, Cookbook,
EquationMorph, UserEquation, UserBulb, MasterConfig, ServerAdmin,
Cluster/Job/Worker views, HelpViewer, Sandbox. **Not converted:** `MainWindow`
(the render window), the Mini* tool windows, `FloatingMenuView` (the modeless
main menu — revisited when the shell replaces it in S1).

Status: **F3 pattern + AppSettingsView done** (commit pending). Remaining
conversions are mechanical replication of the pattern above, but each is a
contract change not runtime-verifiable headlessly — sweep strategy TBD with user.

### Phase S1 — Shell
- `SplitView`: left nav-rail (grouped ~5–6 nav groups collapsing the current
  ~11 menu sections) + content region hosting the UserControls.
- Beginner/Power toggle → `IsBeginnerMode` bound, drives advanced-section
  visibility ("guiding hand").

### Phase S2 — Poppable + overlays + reorg
- Detach button per panel → floats via WindowService (2nd-monitor aware).
- Move brightness/contrast/post-FX to a HUD overlay.
- Regroup the overlong main-window context menu; surface Volumetric Lighting
  FX at top level.
- Edge-snap between floating windows (custom; scope TBD).

## 6. Open decisions

- Edge-snap for floating panels: full snap vs smart-place only (snap = extra
  work). Revisit at S2.
- Nav-rail taxonomy: propose at S1 start (collapse ~11 sections → ~5–6 groups).

## 7. Symptom → phase traceability

| Reported symptom | Root | Fixed in |
|---|---|---|
| Narrow / clipped numeric fields | R1 | F1 |
| Slider vs numeric inconsistency | R1 | F1 |
| Non-uniform / too-narrow buttons | R1 | F1 |
| Windows open on-top/under unpredictably | R2 | F2 |
| Dialogs off-screen / clipped on small monitors | R2 | F2 |
| Oversized dialogs | R2 | F2 |
| Overlong floating menu | org | S1 |
| No central jumping-off point | org | S1 |
| Overlong main-window context menu | org | S2 |
| Volumetric Lighting FX buried | org | S2 |
| Clunky collapsible dropdowns, unfilterable lists | org | F1 control + S2 |
| Post-FX would suit overlays | org | S2 |
| 2nd-monitor auto-populate | vision | F2 + S2 |
| Docking / windows align to each other | vision | S2 |
| Control center + guiding hand | vision | S1 |
