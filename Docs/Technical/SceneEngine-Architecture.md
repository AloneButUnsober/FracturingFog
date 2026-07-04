# Scene Engine — Architecture

> **Companion pages:** [Technical Index](_Index.md) · end-user counterpart:
> [Scene Engine User Guide](../User/SceneEngine-UserGuide.md) · the phase-by-phase
> planning + shipped-status record: [Scene Engine Roadmap](../Scene-Engine-Roadmap.md).

This is the developer reference for the Scene Engine — the cinematic layer that
composes shots into a timeline, flies a keyframed camera through the eight 3-D
raymarchers, sequences transitions, applies scene-wide look tracks, and renders
the result offline to video. It assumes you have read the
[Architecture Overview](Architecture-Overview.md) and know the animation bus from
the [Animation Roadmap](../Animation-Roadmap.md).

The guiding design constraint, from the roadmap: **the pure, deterministic,
unit-tested core lands in `Abstractions/`; the impure consumers (persistence,
render host, editor UI) live in `Engine/`, `UI.Avalonia/`, and `Batch/` and are
kept thin.** Every phase shipped its core behind current behaviour first, then
wired a consumer. That layering is why almost everything on this page is a pure
function you can call from a test.

---

## Module map

| File | Project | Role |
|------|---------|------|
| `Render/RenderMode.cs` | Abstractions | S0 — `RenderMode` enum + `RenderModePolicy` + thread-affine `RenderModeScope` (realtime vs. offline vs. offline-fast-GPU). |
| `Render/ResourceGovernor.cs` | Abstractions | S1 — pure adaptive quality control loop + `ProcessResourceSampler` + `IResourceCapBackstop`. |
| `Render/PerformanceTier.cs` | Abstractions | S2 — `PerformanceTier` (Potato/Balanced/Wow), `TierKnobs`, `PerformanceTierProfile`. |
| `Render/CameraTrack.cs` | Abstractions | S3 — `CameraState`, `CameraKey`, `CameraTrack`, `CameraInterpolation`, `CameraEase`. The new engine surface. |
| `Render/CameraParamBinding.cs` | Abstractions | S3 — the seam from a `CameraState` onto the per-type camera fields on `FractalParameters`. |
| `Animation/CameraTrackAnimator.cs` | Abstractions | S3 — `IParameterAnimator` that advances a scene clock and drives a track onto the bus. |
| `Animation/SceneData.cs` | Abstractions | S4 — the `SceneData` / `SceneShot` DTOs + `SceneTransitionKind`. |
| `Models/SceneLibrary.cs` | Engine | S4 — singleton JSON library + built-in demo scenes. |
| `Assets/AssetSources.cs` (`SceneAssetSource`) | Engine | S5 — Asset Manager node type. |
| `ViewModels/SceneEditorViewModel.cs` + `Views/SceneEditorView.axaml` | UI.Avalonia | S5 — the editor. |
| `Animation/SceneTimeline.cs` | Abstractions | S6 — pure playback schedule + `SceneTransitions` (visual resolution + light-sweep weight). |
| `Animation/SceneRenderPlan.cs` | Abstractions | S7 — pure offline frame plan (motion-blur sub-frames + transition composites). |
| `Export/SceneVideoRenderer.cs` | Engine | S7 — the offline renderer that consumes the plan. |
| `Animation/SceneParamMorph.cs` | Abstractions | S8 — component-wise param lerp for the ParamMorph transition. |
| `Animation/SceneGlobalTrack.cs` | Abstractions | S8 — scene-wide keyframed post scalars + binding + multi-track apply. |
| `Animation/SceneGlobalTrackAnimator.cs` | Abstractions | S8 — bus animator for realtime global tracks. |
| `Batch/BatchRenderer.cs` (`RenderScene`) | Batch | S7 — the `--batch --mode scene` driver. |

Tests (all in `Server.Tests`): `RenderModeScopeTests` (8), `ResourceGovernorTests`
(9), `PerformanceTierTests` (12), `CameraTrackTests` (17 + 6 for D.1 easing),
`SceneLibraryTests` (8 + 2 tone-map), `SceneTimelineTests` (9),
`SceneRenderPlanTests` (12), `SceneTransitionVisualsTests` (6),
`SceneGlobalTrackTests` (18), plus `AssetSourceTests` growth for the ninth source.

---

## The two render modes (S0)

The whole engine hangs off one split, formalised in `Render/RenderMode.cs`:

```csharp
public enum RenderMode { Realtime, Offline, OfflineFastGpu }
```

- **Realtime** — the interactive preview path, under governor control. Sheds
  resolution / param count / effect stack to hold framerate.
- **Offline** — frame-locked. Each frame renders to completion, decoupled from
  wall-clock. This is what produces video. Pins the **CPU (`double`) path** by
  default for reproducibility (the GPU path is `float` and not bit-identical).
- **OfflineFastGpu** — an opt-in for the high-end case that accepts the GPU
  path's non-determinism to render faster.

`RenderModePolicy` is a record carrying the frame-time budget, the
deterministic-CPU pin, and whether the mode participates in the governor.
`ResolveUseGpuRender()` is the single gate that keeps deterministic exports off
the float GPU path. `RenderModeScope` is the thread-affine ambient current
policy — it nests and restores on dispose, defaulting to `Realtime`. Determinism
rationale (roadmap R3): a pinned CPU path means an exported MP4 is byte-for-byte
reproducible across machines.

---

## The resource governor (S1) and hardware tiers (S2)

These are the "never crash the host" machinery. They are independent of the
camera/scene track — you can read this section or skip it.

### Governor

`Render/ResourceGovernor.cs` is a **pure control loop**:

```csharp
GovernorState Evaluate(ResourceSample sample, bool participatesInGovernor);
```

It ratchets a `QualityScale ∈ [floor, 1]` **down** when CPU ≥ 85 % (soft target)
or memory ≥ 0.80 (watermark), and back **up** only after `RecoverHoldTicks`
sustained calm below the recover band (75 % / 0.70). The gap between the
soft-target band and the recover band is deliberate **hysteresis** to stop
oscillation. `HardCapBreached` flags the OS backstop at the 90 % / 0.90 ceiling.

Two important behaviours:

- **Offline freezes the scale.** When `participatesInGovernor == false` the
  quality scale is pinned to 1 (full fidelity) — an export must not throttle
  itself. But the **memory cache-shed signal stays unconditional**, so an
  offline render still drops caches under memory pressure.
- **The OS cap is deferred to the host.** `IResourceCapBackstop` +
  `NoOpResourceCapBackstop` is the injection seam; the Windows Job Object
  implementation (which P/Invokes and can kill the process) is intentionally
  *not* shipped as an unverifiable default. The managed governor is the primary,
  portable mechanism; the job-object cap is an additive Windows-only hardener
  (roadmap R2, R6).

`ProcessResourceSampler` provides the live sample: cross-platform CPU %
(process CPU-time delta ÷ wall × cores) and memory fraction (working set ÷
`TotalAvailableMemoryBytes`, cgroup-aware).

### Tiers

`Render/PerformanceTier.cs` wires the *existing* perf knobs to a single selector
(roadmap R1 — knob explosion is the top confusion risk, so **tiers wire existing
knobs, they do not add parallel ones**):

- `PerformanceTier` enum: `Potato` / `Balanced` / `Wow`.
- `TierKnobs` record: preview scale, volume steps, animated-param ceiling, AA,
  precision tier, GPU / CPU-fallback gates.
- `PerformanceTierProfile` with three pure operations:
  - `Baseline(tier)` — default knobs per tier.
  - `DefaultTier(HardwareProfile)` — picks a tier from the same logical-core
    count + discrete-GPU probe the animation ceiling already uses.
  - `Resolve(baseline, qualityScale)` — folds the live governor scale onto the
    *continuous* knobs (proportional throttle, floor clamps, no boost past
    baseline) while leaving **structural** knobs (precision tier, GPU gate)
    untouched.

`Resolve` is the "apply" half of the sample→evaluate→apply loop; the periodic
driver that pushes resolved knobs onto `FractalParameters` / `LightingFxData` is
the UI consumer, wired through `AvaloniaShellBootstrap`.

---

## The camera track (S3) — the one genuinely new surface

Everything else in a shot (region, theme, animation, lighting preset) already
existed and already played through the animation bus and slideshow engine. The
camera path is the new capability.

### Data model

The 8 distance-estimation raymarchers (Mandelbulb, Mandelbox, KIFS, Quaternion
Julia, Quaternion Mandelbrot, Kleinian, Bicomplex, User Bulb) each already
consume an **orbit camera** as three scalars on `FractalParameters`:
`<Type>CameraDistance / Theta / Phi`. A `CameraTrack` keyframes exactly those
three scalars:

```csharp
public readonly record struct CameraState(double Distance, double Theta, double Phi);

public sealed class CameraKey {
    public double Time { get; set; }       // seconds from track start
    public CameraState State { get; set; }
    public CameraEase Ease { get; set; }   // per-key time reparam (D.1)
}

public sealed class CameraTrack {
    public List<CameraKey> Keys { get; set; }              // ascending Time
    public CameraInterpolation Interpolation { get; set; } // Linear|CatmullRom|Bezier
    public double Duration => last key Time;
    public void Add(CameraKey key);        // inserts sorted
    public CameraState Evaluate(double time);
}
```

`CameraState` is `(Distance, Theta, Phi)` — the orbit triple, *not* a full
`(position, target, FOV, roll)` pose. The roadmap's original `CameraKey` sketch
listed FOV / focal-distance / roll; the shipped surface is the orbit triple the
raymarchers actually read today. A FOV zoom track (for a true dolly-zoom) needs
a new raymarcher input and is future work.

### Interpolation & easing

`Evaluate(time)` clamps outside the key range (below first key → first pose;
above last → last pose) and blends inside it per `Interpolation`:

- **Linear** — component-wise lerp. Constant velocity, a velocity discontinuity
  at each key.
- **CatmullRom** *(default)* — uniform [Catmull-Rom spline](../Resources-Bibliography.md#catmull-rom),
  C¹ continuous, tangents from neighbouring keys, one-sided at the ends. Passes
  through every key; overshoots slightly on sharp direction changes. Basis:

  $$
  p(u) = \tfrac{1}{2}\big[\,2p_1 + (-p_0 + p_2)u + (2p_0 - 5p_1 + 4p_2 - p_3)u^2
        + (-p_0 + 3p_1 - 3p_2 + p_3)u^3\,\big]
  $$

- **Bezier** — cubic Hermite with zero endpoint tangents, i.e. smoothstep
  $u^2(3-2u)$. Settles to a stop at every key. (Per-key handle authoring — a
  true graphical Bezier curve editor — remains future polish.)

**Per-key easing** (`CameraEase`, the D.1 slice) reparametrises the normalised
segment parameter of the segment that *starts* at a key, *before* the spatial
basis reads it, so easing composes with the path shape:

```csharp
public static double ApplyEase(CameraEase ease, double u) => ease switch {
    CameraEase.EaseIn    => u * u,
    CameraEase.EaseOut   => 1 - (1 - u) * (1 - u),
    CameraEase.EaseInOut => u * u * (3 - 2 * u),   // smoothstep
    _                    => u,                     // None
};
```

Endpoints are fixed (`0→0`, `1→1`), so keys are always passed through exactly —
easing only changes traversal *speed*, never *which* pose a key lands on.

> [!NOTE]
> **Angles interpolate literally, not shortest-path.** A track from θ = 0 to
> θ = 4π orbits twice on purpose. This is a deliberate authoring affordance —
> the alternative (shortest-path angle wrapping) would make multi-turn orbits
> impossible to express. The cost is that a non-monotonic θ (e.g. 6.0 → 0.1)
> unwinds a whole turn; the user guide warns authors to keep θ monotonic.

### The binding seam

`CameraParamBinding` maps a type-agnostic `CameraState` onto the concrete
per-type fields. It is data-driven off **one authoritative dictionary**:

```csharp
[FractalType.Mandelbulb] = ("BulbCameraDistance", "BulbCameraTheta", "BulbCameraPhi"),
[FractalType.Mandelbox]  = ("MandelboxCameraDistance", ...),
// ... 8 entries total
```

`PropertyInfo` is resolved once via reflection and cached. `Apply(params, type,
state)` writes the three fields; `Read` is the inverse. `Supports(type)` /
`SupportedTypes` gate camera authoring to exactly the 8 raymarch types.

The round-trip test (`CameraTrackTests`) is load-bearing: it asserts every
property name in the map exists on `FractalParameters` as a read/write `double`,
and that `Apply`→`Read` is the identity. That test is what lets the reflection
be safe — a renamed field fails the test, not production.

### Driving it on the bus

`CameraTrackAnimator` is an `IParameterAnimator` (same contract as the
procedural param animators). It advances a scene clock each `Tick(dt)`, samples
the track, and applies via the binding. Its cost is `Moderate` so the
animated-param ceiling drops it first under load — camera counts as
raymarched-3-D work, so it sheds ahead of a cheap post track (roadmap R4: the
bus already defines a deterministic tick order; the camera slots in as one more
registered animator with a defined precedence). **Bus registration is the S6
consumer**, below.

---

## Scene assets & persistence (S4)

### The DTO

`SceneData` mirrors `AnimationData`'s shape (name key + category + tags) so it
slots into the Asset Manager identically:

```csharp
public sealed class SceneData {
    public string Name;               // library key (case-insensitive)
    public string Description;
    public string Category;           // "User" | "Built-in"
    public List<SceneShot> Shots;
    public List<SceneGlobalTrack> GlobalTracks;   // S8, scene-wide
    public List<string> Tags;
    [JsonIgnore] public double TotalDurationSeconds; // computed sum
}

public sealed class SceneShot {
    public string Name;
    public string RegionName;         // "" = render FractalType's defaults
    public string? ThemeName;         // null = region's own theme
    public string? AnimationName;     // null = region's own animation
    public FractalType FractalType;
    public ToneMapOperator? ToneMap;  // S8, null = inherit region lighting
    public CameraTrack? Camera;       // S3, 3D-only, null for 2D
    public double DurationSeconds;
    public SceneTransitionKind Transition;
    public double TransitionSeconds;
}
```

The **loose coupling** is deliberate (same as `AnimationTrack` naming a param by
string): a Scene serialises without embedding copies of its assets, and a
renamed / missing asset degrades to a resolve-time fallback rather than a
load-time crash.

### The library

`Engine/Models/SceneLibrary.cs` is a line-for-line mirror of `AnimationLibrary`:
lazy singleton, `%APPDATA%\FracturingFog\scenes.json`, indented enums-as-string
JSON via `BuildJsonOptions()` (`JsonStringEnumConverter` +
`WhenWritingNull`), non-fatal load/save, `Add` / `ReplaceOrAdd` / `Remove` /
`GetByName`, and **built-in demo scenes merged on first `Load()`**.

`BuildJsonOptions()` is the canonical serializer — the Asset Manager source uses
it too (rather than the shared `AssetSizing` helpers) so the nested `CameraTrack`
and the `SceneTransitionKind` / `CameraEase` / `CameraInterpolation` enums
round-trip as human-editable strings.

The built-ins are deliberately **region-free** (empty `RegionName` → render the
fractal type directly) so they can never break from a renamed region:

| Built-in | Demonstrates |
|----------|--------------|
| **Mandelbulb Orbit** | the S3 keyframed camera — one calm 360° fly-around |
| **Bulb → Box** | multi-shot sequencing + a cross-fade (visible in export) |
| **Exposure Ramp** | an S8 scene-wide exposure global track over a shot |

The shared orbit helper `OrbitTrack(distance, turns, seconds, phi)` is worth
reading: it explains *why* a bare azimuth sweep reads as an in-place spin and
layers an elevation swing (a `1-cos` ride, 0 at the ends so the loop is seamless)
plus a gentle dolly to add the parallax that reads as a real camera move.

### Asset Manager node (deferred S4, shipped with S5)

`SceneAssetSource` (`Engine/Assets/AssetSources.cs`) wraps `SceneLibrary`,
registered ninth in `AssetSourceRegistry`. The persistence seam is five members
on `IColorThemeService` (`EnumerateSceneNames` / `GetScene` /
`SceneExistsInLibrary` / `SaveScene` / `DeleteScene`) with inert default impls
(Abstractions can't reach Engine) overridden in `HostColorThemeService`. This is
the same VM-through-`IColorThemeService` seam the Animation Editor uses so
**UI.Avalonia never references Engine**. `AvaloniaShellBootstrap` warms
`SceneLibrary.Instance.Load()` at startup.

---

## Playback (S6): the timeline

`Animation/SceneTimeline.cs` is the pure, deterministic playback schedule. It
turns a `SceneData` into a back-to-back timeline and answers "at global time *t*,
which shot, how far in, and are we in an opening transition?".

### The cut model

Shots do **not** overlap in play time — each occupies `[Start, End)`. A shot's
transition is its **opening window**: for the first `TransitionSeconds` of shot
*i* (`i > 0`, kind ≠ Cut) the composite blends the *frozen last frame* of shot
*i-1* into the live frame of shot *i* (blend 0→1). Freezing the outgoing frame is
what keeps realtime inside the resource cap — two shots never run live at once.

`Build(scene)`:
- Drops non-positive-duration shots (`OriginalIndex` preserves the mapping back
  to `SceneData.Shots`).
- First playable shot starts at 0 with **no** opening transition.
- `Cut` shots and the first shot get a zero-length window.
- `TransitionSeconds` clamps to `[0, shot.Duration]`.

`Sample(t)` returns a `SceneSample`: the current (authoritative) entry, its local
time, whether we are in a transition, the outgoing entry, the **blend factor**,
and the transition kind. Callers that loop pass `t % TotalDuration`.

### Transition visual resolution

`SceneTransitions.ResolveVisual(authored)` maps the authored kind to what the
build renders. As of S8 every kind is honoured directly (`Cut`, `Crossfade`,
`LightSweep`, `ParamMorph`) — the pre-S8 collapse of LightSweep/ParamMorph to
Crossfade is gone. `LightSweepWeight(u, blend, feather)` supplies the pure
per-column incoming weight for the left→right wipe (monotonic in both args; the
soft edge sweeps across as blend rises).

### Bus registration & the realtime driver

`AnimationBusHost.LoadSceneShot(shot, shotAnimation, target)` registers the
shot's param animators **and** its keyframed camera as a `CameraTrackAnimator`.
**This is the deferred S3 consumer** — scene-camera motion inherits the bus's
render-completion gate and the animated-param ceiling.

`ShellViewModel.PlayScene` / `StopScene` is the realtime driver: a 50 ms
`DispatcherTimer` walks the timeline; on each shot boundary it jumps the live
view to the shot (region + theme + tone-map) and (re)loads its camera + param +
global-track motion onto the bus. Intra-shot motion is the bus's job. It loops
at the end.

> [!IMPORTANT]
> **Realtime playback cuts between shots.** Cross-fade / light-sweep /
> param-morph *compositing* (blending two rendered frames) needs both sides
> rendered at once; for two live 3-D raymarchers that breaches the ~90 %
> CPU/mem cap. So frame-composited transitions belong to the **offline path
> (S7)**, which renders sub-frames anyway. The timeline already computes the
> blend factor for S7 to consume — no re-work, just a consumer.

---

## Offline render + motion blur (S7)

### The frame plan

`Animation/SceneRenderPlan.cs` turns a `SceneData` + `SceneRenderSettings` (fps,
motion-blur sub-frames, shutter fraction) into the exact list of output frames an
encoder must emit. It is pure — no render, no I/O — and is the deferred consumer
the S6 note promised.

`SceneRenderSettings` (clamped by `Build`): `Fps ≥ 1`,
`MotionBlurSubframes ≥ 1`, `ShutterFraction ∈ (0, 1]`.

`Build(scene, settings)`:
- Frame count is `ceil(total * fps - 1e-9)` — the trailing partial frame is
  emitted (so the last shot's tail isn't truncated), with the `-1e-9` guarding
  the float edge so an exact multiple doesn't add a spurious frame.
- Per output frame *f*, each sub-frame *k* samples at
  `frameStart + (k + 0.5)/sub * shutterDur`, where `shutterDur = frameDur *
  shutter`. Sub-samples spread evenly across the open-shutter window at the
  frame's **leading edge**. Weights are uniform (`1/sub`, a box filter, summing
  to 1) — this is the classic
  [Reyes-style accumulation blur](../Resources-Bibliography.md#catmull-rom).
- The transition is resolved at the frame **midpoint** (a stable,
  shutter-independent choice). If the midpoint sample is in a resolvable
  transition, the frame is flagged `CompositeTransition` with the outgoing shot
  index, its frozen local time (its full duration = its final frame), the blend,
  and the resolved kind.

### The renderer

`Engine/Export/SceneVideoRenderer.cs` consumes the plan. It resolves each shot
**once** against the region / theme / animation libraries (self-contained — no
live render host, so it is callable headless), then for each output frame:

1. Renders every sub-frame via `PosterRenderer`'s offscreen calculator, applying
   the shot's param animation + keyframed camera at that sub-frame's local time.
2. Weight-averages the sub-frames (accumulation motion blur).
3. Inside a transition window, composites the frozen outgoing frame by the
   plan's blend — **the frame-composited cross-fade S6 deferred here**. LightSweep
   uses `LightSweepWeight` per column; ParamMorph renders the *incoming* shot with
   morphed params (below) instead of compositing two frames.
4. Applies the shot region lighting, then the scene global tracks, then the
   per-shot tone-map — **in that order**, so each overrides the last.

Peak memory is a single frame's accumulators plus the pending PNG queue — **one
calculator is live at a time**, keeping it inside the cap. Frames go through the
cross-platform `PngSequenceWriter` → `FfmpegEncoder` pipeline the batch
video/slideshow paths already use; a missing ffmpeg keeps the recoverable PNG
sequence rather than failing.

### The drivers

- **Headless:** `--batch --mode scene --scene NAME` (`BatchRenderer.RenderScene`)
  with `--motion-blur N` (1–64) / `--shutter F` / `--fps` / `--encode` /
  `--width` / `--height` / `--out` / `--keep-frames`.
- **GUI:** the Scene Editor's **⤓ Export…** button raises `ExportSceneRequested`
  (`SceneExportEventArgs`, an Engine-free DTO); the host
  (`AvaloniaShellBootstrap`) picks the path, maps the knobs onto
  `SceneVideoOptions`, and runs `SceneVideoRenderer.Render` on a background
  thread — keeping UI.Avalonia free of the Engine, per the
  `SaveFileRequested` / `MessageRequested` host-fulfilled pattern.

---

## Polish (S8): morph, global tracks, tone-map, easing

### ParamMorph

`Animation/SceneParamMorph.cs` — `Lerp(from, to, t)` is a component-wise lerp
over **every public read/write `double`** on `FractalParameters` (the continuous
shape knobs), on top of a clone of the *incoming* shot for all discrete state.
The renderer renders the incoming shot with these morphed params across the
window — the *shape itself* morphs — rather than compositing two frames. Guarded
to same-fractal-type shot pairs; degrades to a crossfade otherwise (the one
decision made at render time, from the resolved shot types).

### Global tracks

`Animation/SceneGlobalTrack.cs` — a scene-wide keyframed scalar, sampled at
**global** scene time and applied on top of every shot. It reuses the S3/D.1
`CameraInterpolation` + `CameraEase` vocabulary (default `Linear` — a look ramp
wants a monotonic sweep, not spline overshoot that could push exposure below 0).

`SceneGlobalTarget` names the continuous `FractalParameters.Lighting` post
knobs: `Exposure` (the headline), `BloomStrength`, `BloomThreshold`, `Vignette`,
`ChromaticAberration`. `SceneGlobalBinding.Apply/Read` is the one-switch,
data-driven seam (mirrors `CameraParamBinding`); because `Lighting` is a struct
it read-modify-writes the whole value. `SceneGlobalTracks.Apply` runs the whole
set at one time — **later track wins** on a shared target (mirrors
`AnimationData.Tracks`), a null/empty list is a no-op.

Consumers: the offline renderer applies them at each sub-frame's global time,
last; the realtime driver re-installs a `SceneGlobalTrackAnimator` per shot (the
bus clears its dynamic set on each cut) seeded at the shot's global start, so the
sweep continues mid-timeline across a cut instead of restarting. Cost is `Cheap`,
so the ceiling never sheds it ahead of a raymarch track.

> [!NOTE]
> **Why tone-map is per-shot, not a global track.** A tone-map operator is a
> *discrete* look decision (None / Reinhard / ReinhardExtended / ACES), not a
> continuous scalar you can keyframe — so `SceneShot.ToneMap` lives next to the
> region/theme picks, and `SceneGlobalTarget` carries only the continuous knobs.

### Per-shot tone-map

`SceneShot.ToneMap` is a nullable `ToneMapOperator`; null inherits the shot's
region lighting, a value pins the shot's HDR tone-map. The offline renderer
applies it **last** (after region lighting + global tracks); the realtime driver
pins it on the live params at each shot cut. Null omits from `scenes.json`.

---

## Deferred / future work

Per the roadmap's S8 "still open" list — none block the core author → preview →
export loop:

- **Graphical Bezier-handle curve editor** — beyond the per-key ease enum + the
  JSON global-track authoring. A heavier follow-up.
- **A Scene-Editor global-track row** — global tracks are authored today via the
  Asset Manager's JSON-editable Scene node.
- **FOV / dolly-zoom camera track** — `CameraState` is the orbit triple only; a
  true field-of-view zoom needs a new raymarcher input.
- **IBL-sky-rotation global track** — no field exists yet (the HDRI sampler
  reads the surface normal with no yaw offset). The `SceneGlobalTarget` enum +
  binding are built so it slots in for free once the Lighting-FX field lands.
- **Rack-focus preset** and **audio-reactive scenes** (Animation-roadmap D.4).

---

## Extending the engine — recipes

**Add a global-track target.** Add an enum entry to `SceneGlobalTarget`, then two
lines each in `SceneGlobalBinding.Apply` and `Read`. Add a `SceneGlobalTrackTests`
round-trip case. Done — the editor/JSON pick it up via enum reflection.

**Add a camera-bearing fractal type.** Add the three `<Type>Camera*` `double`
properties to `FractalParameters`, then one entry to `CameraParamBinding.Names`.
The round-trip test in `CameraTrackTests` will confirm the names resolve.

**Add a transition kind.** Add to `SceneTransitionKind`; teach
`SceneTransitions.ResolveVisual` how it resolves; implement the composite in
`SceneVideoRenderer`. Realtime will cut (correct — it can't composite live);
offline renders it. Add a `SceneTransitionVisualsTests` case for any pure weight
function.

**Consume the timeline elsewhere.** `SceneTimeline` and `SceneRenderPlan` are
pure and headless — a new consumer (a different encoder, a network render farm)
just walks `Frames` / `Sample(t)`. No re-work, just a consumer, as every phase
here demonstrates.

---

## See also

- [Scene Engine User Guide](../User/SceneEngine-UserGuide.md) — the end-user view.
- [Scene Engine Roadmap](../Scene-Engine-Roadmap.md) — phase-by-phase status.
- [Animation Roadmap](../Animation-Roadmap.md) — the param-animation bus the
  camera track and scene playback build on.
- [Lighting + FX Roadmap](../Lighting-FX-Roadmap.md) — the shipped 3-D fidelity
  stack Scenes render with.
- [Performance Roadmap](../Performance-Roadmap.md) — the perf knobs S2 wires into
  tiers.
- [Architecture Overview](Architecture-Overview.md) — where `SceneLibrary` and
  `ResourceGovernor` slot into the module map.
- [Resources & Bibliography](../Resources-Bibliography.md#scene-engine--camera-splines-motion-blur-cinematic-moves)
  — citations for the splines, motion blur, and tone-map operators.
