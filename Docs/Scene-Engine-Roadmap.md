# Scene Engine — World-Class 2D/3D Fractal Animation Roadmap

Status: **research / planning**. No code shipped yet. Project-wide roadmap
altitude, alongside [`Animation-Roadmap.md`](Animation-Roadmap.md),
[`Lighting-FX-Roadmap.md`](Lighting-FX-Roadmap.md), and
[`Performance-Roadmap.md`](Performance-Roadmap.md).

Branch: `feature/scene-engine`. Per-phase commits enforced (one commit per
`S`-phase completion, mirroring the Animation / Lighting / Performance
roadmap discipline).

Goal (user-stated): make Fracturing Fog a **world-class 2D/3D animation
engine** for the fractals it renders — cinematic Scenes, life-like 3D that
"pops," silky animation, max fidelity on high-end hardware, usable
performance on low-mid hardware, and a hard 90% ceiling on total CPU/memory
so the software never crashes the host.

---

## The reframe: what already exists vs. what's actually missing

Fracturing Fog is **not** an early-stage renderer. Most of the "give it the
works" fidelity wishlist is already shipped. This roadmap deliberately does
**not** re-build it. Grounding:

- **3D lighting / FX (shipped)** — Cook-Torrance GGX PBR, Burley SSS,
  triplanar texturing, roughness-convolved HDRI IBL, single-scatter
  volumetrics (FBM clouds + self-shadow + god-rays), recursive reflection
  bounces, hex-bokeh HDR DoF, edge-ink, true per-eye stereo. All 8
  raymarchers. See [`Lighting-FX-Roadmap.md`](Lighting-FX-Roadmap.md).
- **Performance (shipped)** — ILGPU GPU raymarch port for all 8 3D
  fractals, buffer pooling, low-res interactive preview, adaptive
  volumetric LOD, SIMD bloom, perturbation + double-double / quad-double
  deep zoom. See [`Performance-Roadmap.md`](Performance-Roadmap.md).
- **Parameter animation (shipped, Phases 0–6)** — render-gated animation
  bus, per-type animatable-param registry, `AnimationData` asset +
  `AnimationLibrary`, editor, slideshow integration, per-track animated-
  param ceiling with hardware-derived defaults. See
  [`Animation-Roadmap.md`](Animation-Roadmap.md).
- **Asset infra (shipped)** — Asset Manager UI + Region Editor
  ([`AssetManager-DevPlan.md`](Technical/AssetManager-DevPlan.md),
  [`RegionEditor-DevPlan.md`](Technical/RegionEditor-DevPlan.md)).

**The three real gaps this roadmap fills:**

1. **No Scene concept.** Nothing composes shots into a timeline with a
   moving camera. Animation today mutates *parameters*; it does not move
   the *camera* or sequence *shots*.
2. **No resource governor.** Nothing enforces the 90% CPU/memory ceiling.
3. **No unified hardware-tier layer.** The performance knobs all exist but
   are not wired to a single tiered profile a novice can pick from.

---

## The organising principle: two render modes

The user's spec pulls two ways at once — "silky smooth realtime on low-mid
hardware" **and** "push high-end hardware to maximum fidelity." A single
render path cannot serve both. Every real animation tool splits **viewport
preview** from **final render**; we adopt the same split, and it dissolves
the tension:

| Mode | Priority | Behaviour |
|------|----------|-----------|
| **Realtime / preview** | smoothness | Governed. Adaptive quality. Sheds resolution, param count, and effect stack to hold framerate. Obeys the 90% resource cap. This is what runs while authoring a Scene. |
| **Offline / export** | fidelity | Frame-locked. Each frame renders to completion, decoupled from wall-clock. Slower-than-realtime is fine and expected. Enables motion blur, high sample counts, deterministic output. This is what produces the MP4. |

A Scene is authored and previewed in realtime mode, then *rendered* in
offline mode. Low-mid hardware gets a usable **preview**; every machine
gets a max-fidelity **output**. This split is Phase **S0** and everything
else depends on it.

---

## Scene data model

A Scene is a generalisation of what
[`SlideshowConfig`](../Abstractions/Models/SlideshowConfig.cs) +
[`SlideshowEngine`](../UI.Avalonia/Slideshow/SlideshowEngine.cs) already do:
they cross-fade `region + theme + animation` triples. A Scene adds an
ordered **timeline** and a **camera path** over the same primitives. It
references existing assets — it does not replace them.

```
SceneData                       // persisted to %APPDATA%/FracturingFog/scenes.json
├── Name, Description, Category  // same shape as AnimationData / ColorThemeData
├── Shots : List<Shot>          // ordered timeline
│   └── Shot = {
│         RegionName            : string   // existing FractalRegion ref
│         ColorThemeName        : string?  // existing theme ref
│         AnimationName         : string?  // existing AnimationData ref
│         LightingPresetName    : string?  // existing LightingFxPresetData ref
│         CameraTrack           : CameraTrack?   // NEW — see below
│         DurationSeconds       : double
│         TransitionIn / Out    : TransitionSpec // reuse crossfade + new kinds
│       }
├── GlobalTracks : List<ParamTrack>?   // scene-wide overrides (exposure, IBL rot)
└── Tags : List<string>
```

Storage reuses the singleton-JSON-library pattern shared by
`FractalRegionLibrary`, `UserColorThemeLibrary`, and
[`AnimationLibrary`](../Engine/Models/AnimationLibrary.cs): one more
singleton (`SceneLibrary`) + one JSON file. No new persistence shape.

### The one genuinely new engine surface: the camera track

Everything in a Shot except `CameraTrack` already exists and already plays
through the animation bus + slideshow engine. The camera path is the new
capability — and it is the "reach out and touch it" 3D feel the user wants.

The 8 raymarchers already accept camera state: `camX/camY/camZ`, the
`right` basis vector, and `StereoEyeOffset` (added in Lighting-FX Phase
20b — see
[`LightingFxData.cs`](../Abstractions/Rendering/Lighting/LightingFxData.cs)
and the eight `Engine/Calculators/*Calculator.cs` +
`Engine/Calculators/Gpu/*GpuCalculator.cs` that shift `camX += right.X *
EyeOffset`). A `CameraTrack` keyframes those inputs:

```
CameraTrack
├── Keys : List<CameraKey>
│   └── CameraKey = { TimeSeconds, Position(x,y,z), Target(x,y,z),
│                     FovDegrees, FocalDistance, Roll, Easing }
└── PathKind : { Linear | CatmullRom | Bezier }   // spline through keys
```

This yields orbit, dolly, dolly-zoom (Vertigo/Hitchcock), fly-through, and
rack-focus (keyframing the already-shipped DoF `FocalDistance`) for free —
the raymarchers already consume every field; the track just supplies
time-varying values through the existing animation bus tick.

---

## The resource governor (the 90% cap)

This is the hardest and most nuanced sub-goal. Honest breakdown, because
"never take more than 90% of CPU and memory" is not a single switch:

- **Memory hard cap — feasible.** Windows Job Objects
  (`JOBOBJECT_EXTENDED_LIMIT_INFORMATION.ProcessMemoryLimit`) enforce a
  real ceiling. But hitting it means allocation failure, so pair it with a
  **soft watermark** that sheds caches first (the P0 buffer pools, HDRI
  mips, low-res preview buffers) well before the hard limit.
- **CPU hard cap — technically possible, usually wrong.** Job Objects
  support CPU rate control (`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`,
  Win8+). But throttling our *own* process starves the UI thread — the
  exact UX collapse we are trying to avoid. **Primary mechanism is an
  adaptive governor:** a sampler watches CPU% + frametime and turns the
  hardware-tier knobs down (worker-thread count, resolution scale,
  animated-param ceiling, effect stack) to hold headroom. The OS job-
  object cap sits underneath only as a last-resort backstop.
- **Cross-platform caveat.** Job Objects are Windows-only. The managed
  governor is the portable primary; the OS cap is a Windows-only
  hardener. Consistent with the Avalonia cross-platform direction in
  [`CLAUDE.md`](../CLAUDE.md).
- **The cap is a backstop, not an always-on throttle.** On a discrete-GPU
  workstation the target is to sit *near* 90% deliberately (push the
  hardware); on a laptop iGPU the governor protects the UI thread. Same
  90% target, opposite intent — this must be explicit in the UI or it
  reads as a bug.

Built as its own cross-cutting service (`ResourceGovernor`), not folded
into the renderer.

---

## Hardware tiers (novice-friendly performance)

The perf knobs already exist (quality presets Draft→Extreme,
`LowResPreview.ScaleFactor`, `VolumeStepsFalloff`, animated-param ceiling,
`UseGpuRender`, GPU-vendor detection). They are **not** wired to a single
selector. A novice should pick one thing:

| Tier | Target hardware | Drives |
|------|-----------------|--------|
| **Potato** | iGPU / old laptop | half-res preview, effects minimal, param ceiling 4, CPU fallback OK |
| **Balanced** | mid GPU | leans slightly toward performance (per user's low-mid spec) |
| **Wow** | discrete GPU workstation | full effect stack, high sample counts, sit near the 90% cap |

Tier = a profile that sets all the sub-knobs. An "Advanced" drawer exposes
the individual knobs for manual rebalance (per the user's "user-tunable
parameters so they feel in control" ask). **Rule: wire existing knobs to
the tier — do not add parallel knobs.** Knob explosion is the top
confusion risk (there are already 5+ overlapping perf controls).

Default tier is derived from the same hardware probe the animation ceiling
already uses (logical CPU count + GPU vendor string from the D3D init
path).

---

## Phase plan

Avalonia-only per [`CLAUDE.md`](../CLAUDE.md). WinForms untouched. One
commit per completed phase.

| Phase | Scope | Risk | Depends on |
|-------|-------|------|-----------|
| **S0** | Two-mode split: realtime-governed vs offline-frame-locked render path | med | — |
| **S1** | `ResourceGovernor` service — soft watermark + adaptive tier feedback; Windows job-object backstop | high | S0 |
| **S2** | Hardware-tier profile (Potato / Balanced / Wow) wiring existing knobs to one master + Advanced drawer | low | S1 |
| **S3** | `CameraTrack` + keyframe interpolation (Linear / Catmull-Rom / Bezier) plumbed through the 8 raymarchers via the animation bus | med | — |
| **S4** | `SceneData` asset + `SceneLibrary` (scenes.json) + Asset Manager node | low | S3 |
| **S5** | `SceneEditorView` — timeline strip + camera keyframe row (reuses `AnimationEditorView` patterns) | med | S4 |
| **S6** | Scene playback through the animation bus + `SlideshowEngine` cross-fade | med | S4 |
| **S7** | Offline scene render → MP4 (reuse `Export/Mp4Writer` + ffmpeg) + accumulation motion blur | med | S0, S6 |
| **S8** | Polish: easing curves UI, rack focus, exposure / IBL-rotation tracks, audio-reactive scenes | low | S3–S7 |

S3 and S0/S1/S2 are independent tracks — the camera work and the
governor/tier work can proceed in parallel. MVP is shippable after **S6**
(author + preview a cinematic Scene end-to-end); S7 adds export, S8 is
polish.

---

## Phase detail

### S0 — Two render modes

**Status — Shipped.** `Abstractions/Render/RenderMode.cs`: `RenderMode` enum
+ `RenderModePolicy` record (`Realtime` / `Offline` / `OfflineFastGpu`)
carrying frame-time budget, deterministic-CPU pin, and governor
participation; `ResolveUseGpuRender()` is the single gate keeping
deterministic exports off the float GPU path; `RenderModeScope` is the
thread-affine ambient current policy (nesting + restore-on-dispose,
defaults to `Realtime`). Ships behind current behaviour — no consumer yet
(S1/S2/S7). 8 tests in `Server.Tests/RenderModeScopeTests.cs`.

Formalise the realtime-vs-offline split. Realtime path stays the current
interactive renderer, now under governor control. Offline path renders each
frame to completion into a buffer, ignoring wall-clock — this is mostly a
driver loop around the existing `Calculate` + post-pass chain that does not
early-out on a frame-time budget. The existing slideshow video path already
records animated content frame-by-frame; S0 generalises that into a named
mode both Scenes and existing video export share.

Determinism note: the CPU path is `double`; the GPU path is `float` and not
bit-identical (documented in the Performance + Lighting roadmaps). Offline
render pins the CPU path by default so exported MP4s are reproducible; a
"fast GPU export (non-deterministic)" opt-in covers the high-end case.

### S1 — Resource governor

See [The resource governor](#the-resource-governor-the-90-cap). Sampler +
soft watermark (cache shedding) + adaptive knob feedback + Windows job-
object hard backstop. Tests drive the governor with a fake resource sampler
and assert it steps the tier down before the hard cap and recovers when
pressure drops.

### S2 — Hardware tiers

See [Hardware tiers](#hardware-tiers-novice-friendly-performance). Pure
wiring: a `PerformanceTier` profile sets existing knobs; Advanced drawer
exposes them. Default tier from the existing hardware probe.

### S3 — Camera track

The new engine surface. `CameraTrack` + `CameraKey` + spline interpolation.
Plumbs time-varying camera state into the 8 raymarchers through the
existing animation bus tick (the bus already gates renders so camera
motion inherits the flicker-free handshake for free). Round-trip tests:
every field a `CameraKey` claims is actually consumed by each raymarcher's
primary-ray construction.

### S4 — Scene asset + library

`SceneData` DTO + `SceneLibrary` singleton + scenes.json. Slots into the
Asset Manager as a new node type. Built-in demo scenes ship in-source (same
pattern as built-in regions / animations).

### S5 — Scene editor

`SceneEditorView.axaml`: horizontal timeline of shots, per-shot property
panel (region / theme / animation / lighting-preset pickers reuse existing
dialogs), camera keyframe row with add/drag/delete, live preview button
that plays the scene in realtime mode.

### S6 — Scene playback

Drive shot sequencing through `SlideshowEngine`'s cross-fade machinery;
drive per-shot param + camera motion through the animation bus. Transitions
extend the existing crossfade with scene-appropriate kinds (cut, crossfade,
light-sweep, param-morph).

### S7 — Offline render + motion blur

Frame-locked render of a Scene to MP4 via `Export/Mp4Writer` + the ffmpeg
pipeline. Accumulation motion blur (render N sub-frames per output frame at
sub-tick camera offsets, average) — only viable in offline mode, large
fidelity boost for camera motion.

### S8 — Polish

Bezier easing editor (this is the deferred Animation-roadmap `D.1` keyframe
work, which Scenes need anyway), rack-focus preset, exposure / tonemap /
IBL-sky-rotation global tracks, audio-reactive scenes (deferred Animation-
roadmap `D.4`).

---

## Risks & open questions

- **R1 — Knob explosion.** Already 5+ overlapping perf controls; Scenes +
  governor + tiers risk burying the novice user. *Mitigation:* S2's single
  tier selector is mandatory before shipping any new knob. New controls
  land inside the Advanced drawer, off by default.
- **R2 — CPU cap starves the UI.** A naïve CPU throttle degrades the exact
  UX it protects. *Mitigation:* governor-first (adaptive quality), OS cap
  as backstop only (S1).
- **R3 — GPU/CPU non-determinism in export.** `float` GPU vs `double` CPU.
  *Mitigation:* offline render pins CPU path by default (S0).
- **R4 — Camera track vs. existing param animations conflict.** Both
  integrate into the same params record per tick. *Mitigation:* the
  animation bus already defines a deterministic tick order (documented in
  Animation-roadmap Phase 5); camera track slots in as one more registered
  animator with a defined precedence.
- **R5 — Scene scope creep toward a full NLE.** A fractal Scene tool is not
  Premiere. *Mitigation:* procedural + keyframe motion only; no per-pixel
  compositing, no audio mixing, no multi-track video layering beyond the
  single fractal render.
- **R6 — Cross-platform governor.** Job Objects are Windows-only.
  *Mitigation:* managed governor is primary and portable; job-object cap is
  an additive Windows hardener, not a dependency.

---

## Companion pages

- [Animation Roadmap](Animation-Roadmap.md) — param animation the camera
  track and scene playback build on.
- [Lighting + FX Roadmap](Lighting-FX-Roadmap.md) — the shipped 3D fidelity
  stack Scenes render with; stereo `EyeOffset` plumbing the camera track
  reuses.
- [Performance Roadmap](Performance-Roadmap.md) — the perf knobs S2 wires
  into tiers; the two-mode split extends its realtime-vs-offline thinking.
- [Architecture Overview](Technical/Architecture-Overview.md) — module map;
  where `SceneLibrary` and `ResourceGovernor` slot in.
- [`CLAUDE.md`](../CLAUDE.md) — Avalonia-is-canonical rule bounding scope.
