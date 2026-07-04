# Animated Fractal Parameters — Feasibility &amp; Roadmap

Status: **research / planning**. No code shipped yet. Lives alongside
[`Fractal-Expansion-Roadmap.md`](Fractal-Expansion-Roadmap.md) and
[`Lighting-FX-Roadmap.md`](Lighting-FX-Roadmap.md) — same "project-wide
roadmap" altitude.

This document covers:

1. Whether the Julia-speed animation pattern generalises to *every*
   `FractalParameters` field (TL;DR: yes, with a render-gate bus).
2. A new **Animation** asset type — saved, recallable, attachable to
   regions, parallel to `ColorThemeData` in lifecycle and storage.
3. A new **Animation Slideshow** type, plus animation hooks added to the
   existing Image slideshow.
4. Per-parameter animation toggles + a configurable "animated-param
   ceiling" to keep slow targets (deep zoom, raymarched 3D) usable.
5. Two adjacent sub-goals: a real **region editor** and a
   cross-asset-type **asset manager** UI.

Existing extension points to reuse:

- `FractalParameters` — already the single source of truth for every
  per-fractal field. No new model surface required; we *animate* it,
  we don't replace it.
- Julia animation's render-gate handshake in
  [`FractalParamsViewModel.cs`](../UI.Avalonia/ViewModels/FractalParamsViewModel.cs)
  — already proven to keep animation silky-smooth even when individual
  renders take seconds at ultra-zoom (the only existing param-race
  synchroniser in the project).
- Per-type capability bits in
  [`FractalCapabilityMap`](../Abstractions/Models/Enums.cs) — pattern we
  copy for "which params on this type are animatable."
- Singleton JSON library pattern shared by
  [`FractalRegionLibrary`](../Engine/Models/FractalRegion.cs),
  [`UserColorThemeLibrary`](../Engine/Models/UserColorThemeLibrary.cs),
  `SlideshowConfigLibrary`,
  [`UserBulbStore`](../Abstractions/Models/UserBulbStore.cs),
  [`UserEquationStore`](../Abstractions/Models/UserEquationStore.cs).
  We add one more (`AnimationLibrary`) rather than inventing a new
  persistence shape.
- `SlideshowConfig.FilterFractalTypes` / `IncludedColorThemes`
  whitelist pattern — we add an `IncludedAnimations` /
  `FilterAnimations` peer field rather than a new filter mechanism.
- `RenderFrameInfo` completion event already routed back into the
  param VM — the bus we hang every animator off.

---

## Snapshot of what exists today

Concrete starting points the design builds on (Avalonia paths only;
WinForms shell is frozen per
[`CLAUDE.md`](../CLAUDE.md)).

### Existing Julia speed animation

[`UI.Avalonia/ViewModels/FractalParamsViewModel.cs`](../UI.Avalonia/ViewModels/FractalParamsViewModel.cs)
fields `JuliaAnimating`, `JuliaAnimateSpeed`, `JuliaAnimateForward`
drive `JuliaC` by polar rotation. Mechanism:

- `DispatcherTimer` at 50 ms `Background` priority integrates
  `theta += dir * speed * dt` every tick — silent updates.
- `_juliaRenderInFlight` flag plus `NotifyRenderCompleted()` (line 289)
  form the gate: the next `Trigger(params)` only fires once the prior
  render reports back. No mid-render param swaps → no flicker.
- 50 ms tick is faster than most render frames at ultra-zoom; updates
  *coalesce* against the gate rather than queue.

This is the prototype for every animator we want to add. Generalising
it is the work in §Phase 0.

### `FractalParameters` field census

~150 fields in
[`Abstractions/Models/FractalParameters.cs`](../Abstractions/Models/FractalParameters.cs).
Grouped by which fractal types read them. The full inventory is in
the [Animatable parameter inventory](#animatable-parameter-inventory)
appendix — short version:

- **2D escape-time scalars / complex numbers** (Mandelbrot, Julia,
  Phoenix, Newton, Halley, Secant, Spider, Glynn, Logistic, Magnet
  1/2, Multibrot, …) — small primitives, cheapest to animate, no
  pipeline state invalidation. Top candidates for MVP.
- **3D raymarch params** (Mandelbox scale / radii, KIFS fold + scale +
  offset, Bulb power, quaternion slice plane, Bicomplex slice axis,
  Kleinian sphere scale) — expensive per frame (raymarched DE +
  lighting + DoF + volumetric) but produce the most striking visuals.
- **IFS / procedural** (`IFSPresetName`, `LSystemDepth`,
  `AttractorA/B/C/D`, `FlameGamma`, `FlameVibrancy`, `DlaParticles`,
  `ApollonianDepth`, `Plasma*`) — mixed; some are scalar, some are
  iteration-count-bound and animating them invalidates the
  accumulator. Animatable but cost depends on which.
- **User-defined fractal source** (`UserEquationSource`,
  `SandboxSource`, `UserBulbSource`) — *not* animatable directly. Source
  swaps trigger Roslyn / CalcGen recompile and JIT, which can take
  hundreds of ms. The `UserBulbParams` and `UserBulbTime` *inside* a
  given source body, however, are animatable.
- **Shared 3D `Lighting` block** — already a struct of floats / colors /
  angles. Animatable; partially overlaps the
  [Lighting-FX Roadmap](Lighting-FX-Roadmap.md).

Per-type "animatable params" can be derived statically from the
existing per-type visibility blocks in
[`FractalParamsView.axaml`](../UI.Avalonia/Views/FractalParamsView.axaml).
That XAML is effectively a hand-written registry already.

### Slideshow surface

[`Abstractions/Models/SlideshowConfig.cs`](../Abstractions/Models/SlideshowConfig.cs)
defines `SlideshowType { Image, Video }` (line 23) plus filter and
include whitelists. Engine in
[`UI.Avalonia/Slideshow/SlideshowEngine.cs`](../UI.Avalonia/Slideshow/SlideshowEngine.cs)
drives both. Fractal-type filter dropdown
(`FilterFractalTypes`, line 184) is the exact UI affordance the
animation-filter dropdown should mirror.

### Region asset

[`Engine/Models/FractalRegion.cs`](../Engine/Models/FractalRegion.cs)
already carries optional references to other assets
(`CuratedThemes`, `LightingOverride`, `EmbeddedWatermark`,
`UserEquationName`, `SandboxName`, `UserBulbName`). Adding optional
`AnimationName` is the same shape of field. No editor exists — users
re-save a region to "edit" it (see §Sub-goal B).

### Asset persistence

Every asset type rolls its own singleton library + JSON file under
`%APPDATA%/FracturingFog/`. There is **no** common asset manager UI
today. See §Sub-goal A.

---

## Goals &amp; non-goals

**Goals:**

- Animation is an *asset*, not just a transient UI state — saved,
  named, recalled, shareable, attached to regions.
- Smooth playback across all fractal types — no flicker, jitter,
  flashing, or torn frames. Reuse the render-gate that already
  makes Julia animation flicker-free.
- Animation Slideshow type that picks regions which have an animation
  attached (or randomly assigns one of the user's animations to a
  type-compatible region) and cross-fades between region + theme +
  animation triples.
- Existing Image and Video slideshow types pick up optional
  animation support — when an animation is attached or randomly
  assigned, it plays; when not, behaviour is unchanged.
- Per-parameter animation toggles in the UI so users dial back load
  on slow targets.
- Configurable animated-param ceiling (manual + optional
  hardware-derived default) to prevent users authoring 30-param
  animations on a laptop iGPU and then wondering why it stutters.

**Non-goals (for this roadmap):**

- Animating `UserEquationSource` / `SandboxSource` / `UserBulbSource`
  bodies themselves. Source hot-swap is a separate, much harder
  problem (CalcGen recompile + JIT warmup + perturbation cache
  invalidation). Listed in §Deferred.
- A keyframe timeline editor with bezier handles. MVP uses procedural
  motion (linear / sine / triangle / lissajous between `Min` and
  `Max` per param). Keyframes are §Deferred.
- Removing or refactoring the WinForms shell. Out of scope —
  [`CLAUDE.md`](../CLAUDE.md) freezes WinForms.

---

## Phase 0 — Animation bus (prerequisite)

Goal: extract Julia animation's render-gate handshake into a reusable
*animation bus* that can drive any number of parameter animators
simultaneously, all synchronised against the same render-completion
signal.

Why first: without this, every per-param animator would race the
renderer independently and the "silky smooth, no flicker" goal would
collapse on the second animator the user enables.

Work:

- New `IParameterAnimator` interface in `Abstractions/`:
  `Tick(dt)` → mutates `FractalParameters` in place; `IsEnabled`;
  `Name`; `TargetParams` (which `FractalParameters` fields it touches).
- New `ParameterAnimationBus` owned by `FractalParamsViewModel`. It
  holds a list of active animators, a single `DispatcherTimer`
  (50 ms `Background`, matching Julia), and the existing
  `_renderInFlight` / `NotifyRenderCompleted` gate.
- Convert the current Julia code path into a `JuliaCAnimator :
  IParameterAnimator` registered on the bus. Behaviour stays
  identical for the user; the bus is purely a refactor.
- Bus tick order: integrate every enabled animator silently into the
  param record → if `_renderInFlight == false`, fire one
  `Trigger(params)` → wait for completion → repeat. Coalescing
  preserved.
- Tests: a `Server.Tests`-style unit test that drives the bus with
  fake animators + a fake render gate and asserts (a) all enabled
  animators are integrated before each Trigger, (b) no Trigger fires
  while the gate is held, (c) integration runs every tick even
  while the gate is held (so the param record keeps advancing — when
  the gate releases, the user sees the *current* state, not the
  state at the moment of the last Trigger).

Deliver: animation bus shipped, Julia animation reimplemented through
it with zero user-visible change. This phase ships *behind* current
behaviour — nothing else turns on yet.

---

## Phase 1 — Per-type animatable-parameter registry

Goal: enumerate which `FractalParameters` fields are animatable on
which `FractalType`. Drives UI dropdowns, slideshow compatibility
filters, and animation-asset loading validation ("can this animation
play on this region?").

Why now: without this registry, the asset model from Phase 2 can't
validate, and the slideshow code from Phase 4 can't filter.

Work:

- New `FractalAnimatableParamsMap` (peer of `FractalCapabilityMap`
  in [`Enums.cs`](../Abstractions/Models/Enums.cs)). For each
  `FractalType`, a static set of animatable param names + their
  type (scalar, complex, vec3, color, …) + a sensible default
  Min / Max for procedural motion.
- Source of truth: hand-written, derived from the existing
  per-type visibility blocks in `FractalParamsView.axaml`. ~60 enum
  values × ~5–15 params each = ~500 entries. Tedious but
  mechanical. Probably 1–2 day chunk if done by eyeball; could be
  generated with a Roslyn analyser walking the XAML triggers if we
  want to keep it permanently in sync (see §Open Questions).
- Tests: round-trip — every animatable param the registry claims
  exists on a given type *can actually be read and written* via
  reflection on `FractalParameters`.

Deliver: registry shipped, but nothing consumes it yet (Phase 2 onward).

---

## Phase 2 — `AnimationData` asset + library

Goal: persistable animation as a first-class asset, in the same
shape as `ColorThemeData`.

Work:

- New `AnimationData` DTO in `Abstractions/Models/`:
  - `Name : string` — user-visible.
  - `TargetFractalTypes : List<FractalType>` — explicitly which
    fractal types this animation can play on (validated against
    `FractalAnimatableParamsMap`).
  - `Tracks : List<AnimationTrack>` where each track =
    `{ ParamName, Mode (Linear | Sine | Triangle | Lissajous |
    Hold), Min, Max, FrequencyHz, PhaseOffsetRadians }`.
  - `Duration : double?` — optional total length in seconds; null
    = loops forever.
  - `Tags : List<string>` — for slideshow filter UI.
- Procedural-only for MVP. Keyframes deferred (§Deferred). Procedural
  is enough for the user's stated goal — "play available animations
  randomly on a region" doesn't need keyframes, it needs a small
  library of varied motion profiles.
- New `AnimationLibrary` singleton — parallel to
  `UserColorThemeLibrary`. JSON at
  `%APPDATA%/FracturingFog/animations.json`. Built-in defaults
  shipped in-source (similar to how 35 built-in regions ship code-static).
- Runtime instantiation: `AnimationData.ToAnimators()` returns one
  `IParameterAnimator` per `Track`, all registered on the bus from
  Phase 0.

Deliver: animations are saveable, loadable, and *playable* through
the bus. No UI to *attach* them to regions yet — that's Phase 3.

---

## Phase 3 — Animation UI: editor dialog + region attach

Goal: end users can author an animation, save it, attach it to a
region.

Work:

- New `AnimationEditorView.axaml` in `UI.Avalonia/Views/`. Small
  dialog: target fractal type picker → animatable param list (from
  Phase 1 registry, filtered by selected types) → per-param `Mode /
  Min / Max / Frequency / Phase` row. "Preview" button activates the
  animation on the live render so the user sees what they're
  building. "Save" persists to the library.
- Region "Save Region" dialog gets an optional Animation dropdown
  (parallel to existing watermark toggle). Stored as
  `FractalRegion.AnimationName : string?`.
- Region recall (`LoadRegionFractalParams` path in
  [`AvaloniaShellBootstrap.cs`](../Hosting/AvaloniaShellBootstrap.cs))
  picks up the animation, instantiates animators on the bus,
  starts the bus.
- Editing existing regions still requires re-save (the existing
  "delete + resave" pattern). A real region editor is §Sub-goal B.

Deliver: full author-save-attach-play loop works for a single region
+ animation pair. No slideshow integration yet.

---

## Phase 4 — Animation Slideshow type

Goal: new `SlideshowType.Animation` peer of `Image` and `Video`.

Work:

- Add `SlideshowType.Animation = 2` to
  [`SlideshowConfig.cs`](../Abstractions/Models/SlideshowConfig.cs)
  enum (line 23). JSON-compat: enum was already int-serialised, so
  old configs continue to deserialise as Image / Video.
- New `SlideshowConfig` fields:
  - `IncludedAnimations : List<string>?` (whitelist; null = all).
  - `FilterAnimations : List<string>` (peer of `FilterFractalTypes`).
  - `RandomizeAnimationsByFractalType : bool` — when true, slideshow
    picks a random animation from the user's library that's
    compatible with the chosen region's fractal type, ignoring any
    animation the region itself carries.
- `SlideshowEngine` per-leg logic for Animation type:
  1. Pick region (existing whitelist / filter logic).
  2. Pick color theme (existing logic).
  3. Pick animation: region's attached animation if present and
     `RandomizeAnimationsByFractalType == false`; otherwise random
     from library filtered by `region.FractalType ∈ animation.TargetFractalTypes`.
  4. Cross-fade in. While the leg plays, animation bus is live —
     params advance per tick. Cross-fade out.
  5. Stop animation, start next leg.
- Type compatibility hard-stop: if no animation in the library is
  compatible with the chosen region, slideshow falls back to a
  static (non-animated) leg for that region. Logged, not popped up.
- Settings dialog gets a new "Animations" tab parallel to the
  existing "Themes" tab: include/exclude list + filter dropdown.

Deliver: Animation Slideshow type runs end-to-end with the
user's library. Falls back gracefully on type-mismatched regions.

---

## Phase 5 — Animation hooks on Image &amp; Video slideshow types

Goal: existing slideshow types pick up animation support without
breaking any current user's saved configs.

Work:

- Image slideshow per-leg: same animation-pick logic as Phase 4,
  but the leg-cross-fade timing is unchanged. Animation runs *during*
  the static-image leg's hold portion.
- Video slideshow per-leg: animation runs *concurrently* with the
  zoom-leg progression. There's a subtle interaction with
  `AdaptiveSweep` — both are integrating into the same params record
  every tick. Bus tick order: zoom-leg first, then animation bus, so
  animation overrides take precedence on conflicting fields.
  Conflicting fields are rare in practice (zoom only touches center
  + zoom; animation touches per-type params), but we document the
  order explicitly.
- Slideshow Settings dialog: "Animations filter" dropdown panel,
  parallel to existing type-filter panel. Per the user's design ask.
- Backwards compat: old configs default to no animation; behaviour
  identical to today. Only opt-in.

Deliver: animation available across all three slideshow types.

---

## Phase 6 — Per-parameter toggles + animated-param ceiling

Goal: keep playback smooth on weak hardware and let users dial back
load explicitly.

Work:

- Per-track `Enabled : bool` in `AnimationTrack`. UI: per-row toggle
  in the animation editor; bus skips disabled tracks on tick.
- Global `AnimatedParamCeiling : int` setting (in app settings, not
  per-animation). Bus enforces: if `enabled-track-count > ceiling`,
  silently drops the lowest-priority tracks for the duration of the
  leg. Priority order: explicitly-disabled-then-re-enabled tracks
  first (LRU), then high-cost params (raymarched 3D > 2D escape >
  scalar), then declaration order. Logged.
- Default ceiling: derived from a tiny hardware probe at first
  launch (logical CPU count, GPU vendor string already available
  via the D3D init path, `IsRunningUnderGpu` already a field on the
  param record). Heuristic:
  - 2D escape only: ceiling 12.
  - 3D raymarched (UserBulb, Mandelbox, KIFS, Kleinian, Quaternion):
    ceiling 4 default, 6 if discrete GPU detected.
  - Override-able in Settings.
- Tests: hardware probe returns reproducible ceilings under
  controlled fake-hardware inputs.

Deliver: smooth playback on a laptop iGPU, no auto-throttling
surprises for users on a 4090.

---

## Sub-goal A — Asset Manager UI

User ask: a general overall asset manager that surfaces every saved
asset and routes to its specific editor.

### Today's state

Each asset type has its own sidebar / dialog / library singleton:

- Regions: sidebar list in main shell.
- Color themes: `ColorThemeEditor` floating window.
- User equations: dialog launched from menu.
- Sandbox / UserBulb / SlideshowConfigs: dialogs from menu.
- Watermarks: stored as part of regions, no manager.
- (After this roadmap:) Animations.

There is no top-level "show me everything I've saved" view. Users
hunting for an old asset have to remember which dialog it lives in.

### Feasibility

Medium effort. The data plumbing is already there — every library
singleton already exposes its inventory by name. The work is
purely Avalonia view + a routing layer.

### Sketch

- New `AssetManagerView.axaml`. Three-pane: type tree on left
  (Regions / Themes / Animations / Equations / SlideshowConfigs /
  Watermarks), filterable list in middle, detail / edit pane on
  right. Same shape as VS Code's Explorer.
- `IAssetSource` interface in `Abstractions/`: each library
  implements `Enumerate() : IEnumerable<AssetDescriptor>` and
  `Open(name) : void`. Descriptor carries
  `Name / Type / CreatedAt / SizeOnDisk / ThumbnailBytes?`.
- Wrap each existing singleton with a one-file adapter that returns
  `IAssetSource`. Trivial — `FractalRegionLibrary` already enumerates.
- Detail pane defers to existing editors via the existing dialog
  system — Asset Manager is a router, not a new editor.
- Bulk operations: select multiple → export-as-bundle (zip of JSON
  files) for sharing. Optional, Phase 2.

### Phasing

Probably one focused PR for the read-only view, a second for
edit-routing, a third for bulk operations. Each is half-day
to one-day shaped.

### Risk

Low. No persistence changes, no engine touch. Pure UI on top of
existing libraries.

---

## Sub-goal B — Region editor

User ask: a real region asset manager + editor.

### Today's state

Regions are *saved* via a dialog and *recalled* by double-click,
but cannot be *edited in place*. To rename a region or adjust its
saved iteration cap, users delete and re-save.

### Feasibility

Easy-to-medium. The save path
(`AvaloniaShellBootstrap.SaveRegionRequested`) already mutates
`FractalRegionLibrary` and persists. An "edit existing region"
path is the same code path with a "replace by name" branch instead
of "add new."

### Sketch

- Right-click region in sidebar → "Edit". Opens the existing
  save-region dialog, pre-populated. Save replaces the existing
  record (preserving `RegionType` so built-ins remain immutable —
  built-ins should be cloned-on-edit into a user region with a
  new name).
- Edit fields: Name, Description, CuratedThemes whitelist, attached
  Animation (from Phase 3), LightingOverride toggle, EmbeddedWatermark
  toggle. Geometry fields (Center, Zoom, Iterations) are *not*
  editable here — they're captured from the current view via a
  "Capture current view" button (same as save flow).
- Built-in regions: edit creates a user-region clone, preserving the
  built-in as-is.
- Slots into Asset Manager (§Sub-goal A) as the detail-pane editor
  for Region rows.

### Risk

Low. The persistence shape doesn't change; the dialog already
exists; the library already supports add/remove. Only a "replace
by name" code path is new.

---

## Deferred items (off-roadmap, large refactors)

These are explicitly out of the initial Animation roadmap. Listed
so future-us doesn't re-derive the why.

### D.1 — Keyframe timeline editor

Procedural motion (Linear / Sine / Triangle / Lissajous between
Min / Max) covers the user's stated use cases: random animations
applied to regions during slideshow, parameter "twiddle" for live
viewing. A real keyframe editor with bezier handles is a separate
UI surface roughly the size of the existing color-theme editor.
Probably one of the next things after the MVP ships if users ask
for it.

### D.2 — Animating fractal source bodies

Source-level animation (interpolating between two `UserBulbSource`
bodies, morphing CalcGen DSL trees) is much harder. Requires:

- Roslyn or CalcGen pipeline that can ingest *two* source bodies
  and emit an interpolated calculator without per-frame
  recompilation cost. CalcGen's current 5-path pipeline (see
  [`CalculatorGen-Architecture.md`](Technical/CalculatorGen-Architecture.md))
  is offline / one-shot, not per-frame.
- Probably a "blend amount" param plumbed through every kernel
  variant.
- Perturbation reference cache invalidation strategy — the deep-
  zoom reference is recomputed any time the formula changes, and
  that's seconds-to-minutes at extreme zoom.

Real research project, not a roadmap bullet.

### D.3 — Cross-fade *between* animations

Each leg currently picks one animation. Cross-fading two animations
during a leg (so the *animation itself* fades out as the next
fades in, on top of region + theme cross-fade) is doable but the
visual reading is muddy in practice — two simultaneously-running
animations on the same params produce phase-beat artefacts. Skip
unless users specifically ask.

### D.4 — Audio-reactive animations

The slideshow already has a `SlideshowConfig.AudioReactive` toggle
that beat-syncs region cross-fades (see
[`Slideshow-AudioReactive-Guide.md`](User/Slideshow-AudioReactive-Guide.md)).
Routing the same beat signal into animation `FrequencyHz` /
`PhaseOffsetRadians` is straightforward but earns its own
roadmap entry — interacts with the existing audio band assignment
and would need user-facing config. Deferred to the audio-reactive
roadmap follow-up.

### D.5 — Animation export to MP4

Capture already records the framebuffer. Animation playback is just
frame-by-frame param mutation, so a "record this animation as MP4"
button is mostly UI glue on top of existing
[`ImageCapture.cs`](../ImageCapture.cs) + ffmpeg pipeline. Not in
the initial scope because the slideshow video path already records
animated content; standalone "animation only" export is a smaller
feature than it sounds and ships after the slideshow integration
in Phase 5.

### D.6 — Removing the WinForms shell

Per [`CLAUDE.md`](../CLAUDE.md), out of scope. Animation only ships
in the Avalonia shell. WinForms users keep the existing Julia
animation as-is (the bus refactor in Phase 0 leaves WinForms
unaffected because WinForms doesn't host the bus).

---

## Risks &amp; open questions

**R1 — Per-frame param-record cloning.** `FractalParameters` is a
biggish class. The bus integrates every animator into the live
record on every 50 ms tick. If the calculator reads
`FractalParameters` by reference (it does — it's a class), animation
ticks that land while a calc is reading mid-iteration could see
torn reads. **Mitigation:** the existing render-gate already
serialises this for Julia; bus uses the same lock. Verify in Phase 0
tests that read-during-integration is impossible by construction.

**R2 — Capability bits don't enumerate animatable fields.**
`FractalCapabilityMap` answers "what does this fractal *produce*",
not "what does it *consume*." Phase 1's
`FractalAnimatableParamsMap` is a peer of it, not an extension.
Risk is that the two drift over time as new fractals land.
**Mitigation:** add an analyser-style assertion in CI that every
`FractalType` value has an entry in both maps (or an explicit "no
animatable params" marker for cases like `Logistic` where it doesn't
make sense).

**R3 — UserBulb already has its own animation.**
`FractalParameters.UserBulbTime` (float) is read by the UserBulb
raymarcher as a `time` uniform and is updated by an internal
animation hook independent of the Julia bus. **Mitigation:** make
the bus aware — `UserBulbTime` becomes a registered animator like
any other, with its existing default behaviour preserved by a
shipped-default `AnimationData` ("UserBulb default time motion").
Do this as part of Phase 0 so we don't fork two animation systems.

**R4 — How does the registry stay in sync with `FractalParamsView.axaml`?**
Hand-maintenance of the registry is doable for the 60 current
fractal types but error-prone as new ones land. Options:
  (a) Reflection + attributes on `FractalParameters` fields:
      `[Animatable(ForTypes = [FractalType.Mandelbox, ...], Min = 0,
      Max = 5)]`. Build registry from reflection at startup. Loses
      compile-time guarantees but is single-source-of-truth.
  (b) Roslyn analyser that walks the XAML triggers + emits the
      registry as generated code. Compile-time guarantees, harder
      to write.
  (c) Hand-maintained + CI assertion that the registry contains
      every type. Simple, requires discipline.
Recommendation: **(a)** for MVP — attributes are easy and keep the
registry living next to the fields they describe. Revisit after
Phase 1 ships if drift becomes a real problem.

**R5 — Color-theme interaction.**
Some animations (animating a 3D `Lighting.LightDirection`) will
fight color themes that bake their own light direction into PBR
material bands. **Mitigation:** the
`FractalAnimatableParamsMap` carries a per-param
`ConflictsWithThemeKind : ColorThemeKind?` field. Slideshow
animation-pick logic filters animations whose conflicting params
are about to be overridden by the picked theme. Documented in the
Animation Editor: editing a conflicting param shows a warning.

**R6 — Performance regression risk on the existing Julia animation.**
Phase 0 refactors the only ship-proven param animator. If we break
it, every user who runs the slideshow audio-reactive demo notices
immediately. **Mitigation:** golden test in `Server.Tests` that
drives a fake render gate at a fixed rate and asserts the
`JuliaC` value at known intervals matches the legacy
implementation bit-for-bit before / after refactor.

---

## Animatable parameter inventory

Reference list of every `FractalParameters` field that's a candidate
for animation, grouped by which `FractalType` consumes it. Phase 1
turns this into the registry. Source:
[`FractalParameters.cs`](../Abstractions/Models/FractalParameters.cs) +
[`FractalParamsView.axaml`](../UI.Avalonia/Views/FractalParamsView.axaml).

| Field                              | Type      | Consumers (FractalType)                       | Notes                                          |
|------------------------------------|-----------|-----------------------------------------------|------------------------------------------------|
| `JuliaC` (Complex)                 | Complex   | Julia, related Julia variants                 | The existing animated param. Cheapest.         |
| `MultibrotExponent`                | int       | Multibrot                                     | Integer; animate in `double` and round.        |
| `PhoenixP` (Complex)               | Complex   | Phoenix                                       | Cheap. 2D escape.                              |
| `GlynnC` (Complex)                 | Complex   | Glynn                                         | Cheap.                                         |
| `NewtonExponent`                   | int       | Newton, Halley, Secant                        | Round-trip via double.                         |
| `NewtonRelaxation`                 | double    | Newton, Halley, Secant                        | Cheap.                                         |
| `SecantInitialOffset` (Complex)    | Complex   | Secant                                        | Cheap.                                         |
| `SpiderCDecay`                     | double    | Spider                                        | Cheap.                                         |
| `LogisticBurnIn` / `LogisticSeed`  | int / double | Logistic                                   | Re-render full accumulator each tick — slow.   |
| `IFSIterations`                    | int       | IFS                                           | Cheap if low; accumulator-bound.               |
| `LSystemDepth`                     | int       | L-System                                      | Exponential growth — clamp animation range.    |
| `PlasmaRoughness` / `PlasmaSeed`   | double / int | Plasma                                     | Cheap; seed changes invalidate cache.          |
| `AttractorA/B/C/D`                 | double × 4 | Strange Attractors                           | Per-param; cheap; classic "tweak" target.      |
| `FlameGamma` / `FlameVibrancy`     | double × 2 | Flame                                        | Cheap; visual sweet spot.                      |
| `ApollonianDepth`                  | int       | Apollonian                                    | Recursive — clamp.                             |
| `ApollonianMinPixelRadius`         | double    | Apollonian                                    | Cheap.                                         |
| `DlaParticles`                     | int       | DLA                                           | Re-runs simulation — slow.                     |
| `MandelboxScale`                   | double    | Mandelbox                                     | 3D raymarched — expensive but striking.        |
| `MandelboxFixedRadius` / `MinRadius` | double × 2 | Mandelbox                                   | 3D.                                            |
| `MandelboxIterations`              | int       | Mandelbox                                     | 3D; high cost.                                 |
| `KifsFold` / `KifsScale`           | double × 2 | KIFS                                         | 3D.                                            |
| `KifsOffsetX/Y/Z`                  | double × 3 | KIFS                                         | 3D.                                            |
| `BulbPower`                        | double    | Mandelbulb, UserBulb                          | 3D classic — power 2 → 8 sweep.                |
| `QJuliaCX/Y/Z/W`                   | double × 4 | Quaternion Julia                             | 4D animation — striking.                       |
| `QJuliaSliceW` / `QMandelSliceW`   | double × 2 | Quaternion Julia / Mandelbrot                | The "slice through 4D" param.                  |
| `BicomplexSliceW`                  | double    | Bicomplex Mandelbrot                          | 3D.                                            |
| `BicomplexSliceAxis`               | int       | Bicomplex Mandelbrot                          | Discrete; "step" mode rather than smooth.      |
| `KleinianSphereScale`              | double    | Kleinian                                      | 3D.                                            |
| `KleinianIterations`               | int       | Kleinian                                      | 3D; expensive.                                 |
| `UserBulbTime`                     | float     | UserBulb                                      | Already animated — fold into bus per R3.       |
| `UserBulbParams[*]`                | double[]  | UserBulb                                      | Per-user-bulb tunables. Variable count.        |
| `UserEquationRotationDegrees`      | double    | UserEquation                                  | Cheap; 0–360 cycle.                            |
| `Lighting.*` (sub-block)           | mixed     | All 3D types                                  | Direction, color, intensity, etc.              |
| `Iterations` (param-level)         | int       | All escape-time                               | Animating this is a perf bomb. Mark off-limits or hard-clamp range. |

Anything not in this list is intentionally excluded (source bodies,
quality presets, FractalType itself — switching fractal types
mid-animation is a separate feature, not an animation).

---

## Phase ordering &amp; rough sizing

| Phase | Scope                              | Risk    | Est. effort | Blocks                          |
|-------|------------------------------------|---------|-------------|---------------------------------|
| 0     | Animation bus (Julia refactor)     | medium  | 2–3 days    | every later phase               |
| 1     | Per-type animatable param registry | low     | 1–2 days    | Phase 2, 4, 5                   |
| 2     | `AnimationData` + library          | low     | 1 day       | Phase 3, 4, 5                   |
| 3     | Editor + region attach UI          | medium  | 2–3 days    | Phase 4 nicety, not required    |
| 4     | Animation Slideshow type           | medium  | 2–3 days    | nothing                          |
| 5     | Image + Video slideshow hooks      | low     | 1 day       | nothing                          |
| 6     | Per-track toggles + ceiling        | low     | 1 day       | nothing                          |
| A     | Asset manager UI                   | low     | 2–3 days    | independent — can ship any time |
| B     | Region editor                      | low     | 1 day       | independent — can ship any time |

Total core (0–6): ~10–14 working days. Sub-goals (A+B): ~3–4 more.
MVP shippable after Phase 4 (animation slideshow works
end-to-end); Phase 5 + 6 are polish.

---

## Companion pages

- [Fractal Expansion Roadmap](Fractal-Expansion-Roadmap.md) — new
  families. New families inherit animation support automatically
  once their params are in `FractalAnimatableParamsMap`.
- [Lighting + FX Roadmap](Lighting-FX-Roadmap.md) — shares the
  `Lighting` sub-block this roadmap animates.
- [Performance Roadmap](Performance-Roadmap.md) — Phase 6's ceiling
  defaults depend on the hardware-probe surface from the
  Performance Roadmap.
- [Slideshow + Audio-Reactive Guide](User/Slideshow-AudioReactive-Guide.md)
  — existing slideshow context; this roadmap extends what's there.
- [Regions Guide](User/Regions-Guide.md) — current region asset behaviour;
  Sub-goal B updates this.
- [`CLAUDE.md`](../CLAUDE.md) — Avalonia-is-canonical rule that bounds
  scope.
