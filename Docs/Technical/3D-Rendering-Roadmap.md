# 3D Rendering Roadmap

Tracking document for Fracturing Fog's deliberate expansion into 3D rendering —
what to build, what to borrow from mature 3D / DCC software (Blender, Houdini,
Arnold, the Frostbite/Hillaire volumetrics line), and — just as important — what
**not** to build so FF stays FF and does not drift into being a worse Blender.

Status legend: ☐ not started · ◐ in progress (a first tranche has shipped with
tests — see each slice) · ☑ slice fully closed. A slice is marked ☑ only when it is
*entirely* done; every S1–S9 slice already has merged, tested work landed but stays
◐ because deeper GPU / full-fidelity tails remain. **S1–S9 are all underway; S10 is
deferred (not started); S11 (orbit-trap height, #592) is new/not started.**

Parent tracking issue: **#389**. Each slice below is (or becomes) its own issue;
this doc is the canonical design and the issues are the canonical task list —
keep the links in step (per the repo dev-tracking convention).

---

## 1. Positioning — what FF is (and is not)

FF's native 3D primitive is the **distance field** (DE / SDF). Every 3D thing it
renders — Mandelbulb, Mandelbox, KIFS, Quaternion Julia/Mandelbrot, Kleinian,
Bicomplex, UserBulb, and the Relief 3D heightfield extruded from a 2D fractal —
is a sphere-trace of a signed distance function. This is not a limitation dressed
as a feature: DE/SDF rendering is the primitive modern rendering research is most
excited about. **The DE is the moat.** FF is a *distance-field renderer that
happens to render fractals*, not a mesh DCC.

Two structural moats constrain every item in this doc — an enhancement that
breaks either is off-strategy:

1. **Multi-backend bit-parity via scalar twins.** The CPU scalar renderer is the
   oracle; the D3D11 and Vulkan compute kernels are diffed against it
   (`ReliefRaymarchGpu.RenderCpuMirror`, the `--reliefgpuraymarch` /
   `--vulkanrelief` gates; the same discipline for the eight 3D-fractal GPU
   kernels). Almost no fractal renderer has this — they ship one backend, usually
   GPU-only GLSL. **Every new 3D feature must be twinnable and headless/batch-
   renderable** (the `--server` / `--batch` paths), or it is not FF-shaped.
2. **Two idioms own the surface.** The *art* idiom is the color-theme / palette
   system (`IColorMap`, ColorGen DSL, PaletteBuilder). The *engineering* idiom is
   the parity-twin slice. New work routes through those two rather than inventing
   a third (e.g. resist a general shader node graph — grow the existing DSL).

## 2. As-built 3D capability (the foundation to reuse)

The single most important observation for this roadmap: **FF already computes,
per pixel, far more than it emits.** The raymarch resolves hit depth, surface
normal, albedo, material, ambient occlusion and world position — then collapses
all of it into one 8-bit beauty buffer and discards the rest.

| Capability | Code (representative) |
|---|---|
| DE sphere-trace, 8 fractal families + relief heightfield | `Engine/Calculators/*`, `Engine/Rendering/Lighting/HeightfieldRaymarch2D.cs` |
| PBR shading — 3-light Cook-Torrance GGX, soft shadow, DE-cone AO, SSAO | `Engine/Rendering/Lighting/ShadingPipeline.cs` |
| IBL (gradient / solid / HDRI env), triplanar procedural texture | `ShadingPipeline`, `ReliefHdriBuffer` |
| N-bounce reflections (GGX VNDF option) | `ShadingPipeline` Phase 16, `ReliefRaymarchGpu.Reflections` |
| Volumetrics — single-scatter in-scatter, HG phase, fog color, **palette map (D)**, FBM clouds + self-shadow, empty-space skip | `ShadingPipeline.VolumetricInScatterSegment`, `VolumePaletteBaker` (#185) |
| Debug AOV views (Beauty / normal / …) + lighting HUD | `AovView`, `DebugHudFlags` |
| Multi-backend + parity twins (CPU / D3D / Vulkan / Silk / Skia) | `ReliefRaymarchGpu`, `ReliefRaymarch{Gpu,Vulkan}Kernel`, `ReliefRaymarchKernelSource` |
| Scene Engine (keyframes, easing, global tracks, camera) + audio-reactive | `Docs/Technical/SceneEngine-Architecture.md`, `AudioReactive-Expansion.md` |
| Mesh export (relief → printable) — the DCC handoff boundary | relief mesh export |

Everything below **reuses** this — no item requires a mesh scene graph, a
modeler, or a simulation solver.

## 3. Roadmap slices (ranked by fit × payoff)

Ranking reflects *architectural fit* (reuses what exists, twinnable, headless)
times *visible payoff*. The top trio (S1–S3) is the strategic core: it moves
FF's 3D output a full tier while reusing data FF already produces and adding zero
mesh machinery.

### S1 — AOV / render-pass output + light compositor ◐ (#398)
Promote the per-pixel data the raymarch already resolves — depth, normal, world
position, albedo, AO, material id, motion — to **first-class render passes**, and
export them as a multi-layer **EXR** (see S7). Then relight / DOF / denoise /
bloom / grade in post without re-rendering. This is the actual superpower of
Blender's compositor, and FF has the source data today; the gap is that it is
discarded, not that it is uncomputed.
- **Reuse:** `AovView` (#317) already carries Beauty / normal / depth / AO /
  diffuse / specular / shadow view modes; the shading pipeline produces the rest.
- **Twin:** AOVs are deterministic scalar outputs — trivially twinnable.
- **Depends on:** S7 (EXR) for float/multi-layer output.
- **Status:** `AovExrExporter` landed — packs beauty (bare RGBA) + each `AovView`
  into named multi-layer EXR sub-layers (`normal.*`, `Z`, `AO.V`, `diffuse.*`,
  `specular.*`, `shadow.V`) via the S7 writer; 6 tests incl. a real reader
  round-trip. Values are 8-bit-sourced today.
- **Orchestration (integration follow-up, landed):** `AovExrRenderer` renders the
  scene **once per `AovView`** (beauty + normal/depth/AO/diffuse/specular/shadow/
  stepcount), toggling `LightingFxData.DebugAov`, and hands the buffers to the
  packer → one multi-layer `.exr`. Reached the render loop by extracting
  `PosterRenderer.RenderToPixels` (the composed calc+relief buffer *before* the
  b/c/gamma + view-transform + interior-composite post-pipeline — those would
  corrupt data AOVs). Batch flag `--aov-exr` (image mode only, forces `.exr`).
  4 orchestration tests (`AovExrOrchestrationTests`) incl. a real reader load +
  a beauty≠normals proof that `RenderToPixels` honours `DebugAov`. GOTCHA:
  `LightingFxData` is a **struct** property — `DebugAov` must be set by reassigning
  the whole `fp.Lighting`, not a member write (silently discarded). CPU-only (the
  shade path honours `DebugAov`); a flat 2D render yields beauty-equal planes.
- **Float-native AOVs in one pass (deep slice, landed):** the relief raymarch now
  captures a `ReliefAovBuffers` — world-space unit **normal** (raw x,y,z) + **depth**
  in **world units** — from the PRIMARY (centre-tap) hit in the SAME pass as the
  beauty (no 8-bit quantisation, no re-render). Optional `aov` arg on the core
  `HeightfieldRaymarch2D.Render`; supplying it forces the CPU trace (the GPU kernel
  can't fill it), so the beauty is byte-identical when off. `AovExrExporter.
  BuildFloatChannels` / `WriteFloatAov` pack the raw float planes (`normal.*`
  full-precision, `Z` = true world depth). These are exactly the guide buffers the
  S4 À-Trous denoiser needs — **S4 is now unblocked**. 5 tests
  (`FloatAovCaptureTests`) incl. unit-normal-on-hits, far-depth sentinel on sky,
  world-units (not 0..1) depth, raw float-channel values. NOTE: surfaced a
  pre-existing relief-prepass **static-scratch concurrency hazard** (s_compressed /
  s_prepassMaxH …) — flagged separately, independent of this work.
- **Float geometry in the orchestrator (deep tail, landed — PR #452):** the
  `--aov-exr` orchestrator now feeds the render's OWN float `ReliefAovBuffers`
  (world-space unit normal + world-units depth) straight into the multi-layer EXR
  on the relief-raymarch path — `normal.*` / `Z` are full-precision, and the two
  8-bit Normals/Depth re-render passes are skipped. Threaded through `PosterRenderer`
  (an optional capture on `RenderComposedBuffer` + a new `RenderToPixels` overload;
  external capture wins over the denoise capture and forces the CPU trace) and
  `AovExrExporter.BuildChannels`/`Write` (optional float planes that supersede the
  8-bit geometry; null args = legacy byte-compatible). Byte-identical when AOV export
  is off. 4 tests (`AovExrFloatGeometryTests`).
- **Float lighting-component AOVs (deep tail, landed — PR #453):** the shade pipeline
  now records the raw diffuse / specular / AO / shadow it resolves at each primary hit
  into a `ShadingPipeline.ShadeComponents` buffer captured in the beauty pass (optional
  `compBuf` on both `Shade` overloads; null = byte-identical), stored in
  `ReliefAovBuffers.Components`. `AovExrExporter` emits float `diffuse.*` / `specular.*`
  / `AO.V` / `shadow.V` that replace the 8-bit passes. A relief AOV EXR is now float for
  every geometry + lighting layer; only `stepcount` (a cost diagnostic) stays 8-bit — 6
  fewer 8-bit re-renders per export. 3 tests (`AovExrFloatComponentsTests`).
- **GUI "Export AOV EXR" action (landed):** render-window menu **"Export AOV EXR…"**
  → `ShellViewModel.AovExrCommand` → host picks a `.exr` path and runs
  `AovExrRenderer.RenderToFile` at the current render dimensions (via the same
  `CreatePosterRequest` a still export uses). Thin wiring over the already-tested
  orchestrator; the AOV planes are captured from `RenderToPixels` before any
  overlay/post pass, so they stay clean regardless of the watermark. GUI-only (no
  headless test — the render/pack path is covered by the orchestration tests).
- **Motion-vector AOV — operator + channel (landed — PR #577, #398):** the last
  discarded per-pixel quantity the raymarch already has the data for (world hit pos =
  camera origin + ray dir · depth). Pure operator `Rendering/Lighting/ReliefMotionVector`
  (operator-first, like every prior S1–S5 slice): `CameraView` captures the oblique
  relief camera as the ray generator sees it; `Project` is the exact inverse of ray
  generation (world point → screen pixel-centre coords + a `behind` flag); `ScreenMotion`
  differences the previous-frame projection against the current pixel (the reprojection
  convention SVGF uses) — identical current/previous cameras give exactly (0,0). Opt-in
  `ReliefAovBuffers.Motion` channel (`float[w·h·2]`, interleaved du/dv) allocated only on
  request → default byte-identical. +5 `ReliefMotionVectorTests`.
- **Light compositor (landed — PR #642, #398):** `LightCompositor` (Engine/Imaging)
  recombines the captured diffuse / specular / AO `ShadeComponents` AOV with the surface
  albedo under per-component gains + tints (DiffuseGain / SpecularGain / AoStrength /
  Ambient / diffuse+specular tints) to relight in post without re-rendering —
  `lit = (Ambient + diffuse·gain·tint)·aoEff`, `out = albedo·lit + specular·gain·tint`,
  `aoEff = 1 − (1 − AO)·AoStrength`. Pure, deterministic, parallel; composites the
  direct-lighting layers (SSS / reflections are separate additive passes, out of scope).
  Operator-first; a relight action over a captured render / AOV-EXR round-trip is the
  follow-up. +7 `LightCompositorTests`.
- **Remaining:** thread the render's current + previous-frame camera into the Motion
  channel (the wiring follow-up — landed on the #638→#641 stack); SVGF temporal (the
  deeper #402 consumer of the motion + depth + normal guides).
- **Motion-vector AOV — render wiring (landed — PR #638, #398):** the relief render now
  fills the Motion channel. It exposes its perspective camera as `aov.CurrentCamera`
  (`ReliefMotionVector.CameraView` from the `ReliefCamera` basis) whenever an AOV buffer
  is supplied, and a new optional `previousCamera` param drives a post-pass (after the
  froxel composite) that reconstructs each hit's world position from the captured
  centre-tap depth + primary ray, projects it through the previous camera
  (`ScreenMotion`), and stores the screen-space (du, dv). Perspective only; sky-miss =
  exactly zero; still frame (previous == current) ~zero. A Motion capture forces the CPU
  trace (folded into the `aovOk` gate, like a Components capture). Default off (no Motion
  buffer / no previous camera) → byte-identical. +5 `ReliefMotionVectorWiringTests`.
- **Motion-vector AOV — sequence seam (landed — PR #639, #398):** the previous-frame
  camera now threads through the offline sequence render path. `PosterRequest.PreviousCamera`
  → `PosterRenderer` (RenderComposedBuffer + both RenderToPixels overloads) →
  `ApplyReliefIfEnabled` → the relief render — the shared seam every video/slideshow/scene
  render builds through (null default → byte-identical); mirrors the PR #468 FroxelHistory
  seam. `SceneVideoRenderer` gains an opt-in `CaptureMotionVectors` (default off) that
  carries one persistent previous-camera + capture-motion AOV, advanced across clean
  continuous relief frames (same gate as the froxel history; a flat frame resets the
  baseline). Off → no AOV, no forced-CPU trace, byte-identical. +5 tests (4 seam via
  PosterRenderer + a scene determinism lock).
- **Vector motion blur — first motion-AOV consumer (landed — PR #640, #398):** the
  motion-vector AOV now has a consumer. `MotionBlurFromVectors` (Engine/Imaging) — a pure,
  deterministic, parallel gather — smears the relief beauty along each pixel's motion
  vector (N cheap taps vs N full raymarches): a fast silhouette streaks, a static
  background stays crisp. `FractalParameters.Relief2DMotionBlurStrength` (0 = off) +
  `Relief2DMotionBlurSamples` (2..64), wired in `PosterRenderer.ApplyReliefIfEnabled` — when
  strength>0 AND a previous camera is supplied the render captures a Motion AOV and the blur
  runs as a post-pass (only auto-allocating the capture when no external AOV target owns the
  buffer, so an AOV-EXR export is undisturbed). A still frame (no previous camera) stays
  byte-identical even at strength>0. +8 tests (6 operator + 2 end-to-end).
- **Vector motion blur — user surface (landed — PR #641, #398):** the blur strength was
  programmatic-only; now `--relief-motion-blur F` (0..4) + `--relief-motion-blur-samples N`
  (2..64, imply the effect) batch flags (parse → `fp.Relief2DMotionBlur*`, CLI-builder emit
  on the raymarch path only when on, Control Center snapshot) + a Motion-blur strength +
  samples row in the Relief 3D dialog (`FractalParamsViewModel.Relief2DMotionBlur*`). The
  scene path already carries it end-to-end (#639 threads the camera, #640 applies from the
  shot params), so a scene with motion capture + a strength set blurs with no extra wiring.
  Strength 0 → byte-identical. +9 `MotionBlurBatchWiringTests`. The full S1 motion-vector
  chain: #638 (fill) → #639 (seam) → #640 (consumer) → #641 (surface).
- **Remaining:** SVGF temporal (the deeper #402 consumer of the motion + depth + normal
  guides); the light compositor.

### S2 — Linear-light rendering + view transform (AgX / ACES / Filmic) ◐ (#396)
Render and composite in **linear light**, apply a filmic **view transform** at
output. FF composites in sRGB-ish 8-bit today; that is the root cause of
volumetric fog highlight blowouts and banded gradients. Lowest risk (pipeline
discipline, not new geometry), lifts *every* render, and the palette/theme system
benefits most. Blender's Filmic → AgX migration is the precedent.
- **Reuse:** the entire existing shading + palette pipeline.
- **Twin:** color transform is a pure per-pixel function — bit-parity is easy;
  the intermediate must go float (couples to S7).
- **Risk:** changes default look → gate behind a view-transform selector,
  default preserving current output until validated.
- **Status:** `ViewTransformOps` landed — pure per-pixel Reinhard / ACES / AgX /
  Filmic operators + sRGB↔linear + exposure, `None` = byte-identical identity;
  `FractalViewState.ViewTransform`/`ViewExposureEv` wired into both the live
  (`FractalRenderHost`) and export (`PosterRenderer`) display encode with screen↔
  poster parity; 8 tests. First slice tonemaps the **8-bit** buffer (a look
  operator).
- **Wiring (integration follow-up, landed):** the transform is now user-drivable
  end-to-end. Batch flags `--view-transform none|reinhard|aces|agx|filmic` (alias
  `--tonemap`) + `--exposure EV` (−16..16), parsed into `BatchOptions`, applied on
  the poster in `BatchRenderer` (image mode), and emitted by the Control Center CLI
  builder (`BatchCommandBuilder`, round-trip-tested). Live UI: a **View** dropdown +
  **Exposure** slider in the Post-FX HUD (`PostFxHudWindow`) → `FloatingMenuViewModel`
  → `ShellViewModel` → `MainViewModel` write-through to `ViewState` + `RepaintWithPostFx`
  (no recalc), mirroring the Brightness/Contrast path. 14 wiring tests
  (`ViewTransformBatchWiringTests`). Default (None / 0 EV) stays omitted =
  byte-identical.
- **Video/slideshow frame path (landed):** the offline `--batch` video + slideshow
  frame renderers now apply the view transform + exposure per frame, layered on the
  brightness/contrast pass in the SAME order as `PosterRenderer` (byte-identical when
  `None`, exposure only with a transform selected — matching the poster gate). Wired
  into `BatchRenderer.RenderVideo`, `RenderSlideshow`, and the shared
  `RenderVideoSlideshowLegs` (threaded the transform + exposure through). Scene mode
  (`SceneVideoRenderer`) has no post-FX stage of its own and is out of scope here; the
  LIVE recorder already captures the post-transform snapshot. Verified headless: an
  ACES video frame differs from a `None` frame; `None` stays byte-identical.
- **Core true-linear/float intermediate (landed — PR #651):** `LinearFloatImage`
  is the linear-light HDR intermediate the view transform was designed for —
  interleaved straight linear RGB with unbounded headroom (values > 1.0 survive) +
  a passthrough alpha plane + `FromBgra`/`ToBgra` encode-decode. `ViewTransformOps`
  factored its operator switch into a shared `Tonemap()` core and gained
  `ApplyLinear()` over a linear float buffer (plus a public `Encode()`), so the
  8-bit `ApplyToBgra` and the float path route through the SAME operator + encode.
  Result: `FromBgra → transform → ToBgra` reproduces the existing 8-bit path
  BYTE-FOR-BYTE (parity anchor, tested across every operator × 5 exposures) while a
  producer supplying linear values above 1.0 gets highlight roll-off instead of the
  hard clip the 8-bit path is stuck with. Byte-identical by default (`None` no-op,
  8-bit round-trip unchanged); +7 tests, suite 2182/2182. Seam before consumer —
  the producers below wire in next.
- **Producer wiring — relief HDR beauty (landed — PR #651):** the relief RAYMARCH
  is the first producer. `ReliefAovBuffers` gained an `HdrBeauty` plane (pre-clamp
  linear-light beauty, byte-scale 0..∞, NaN = sky/miss sentinel) filled from
  `ShadingPipeline`'s existing HDR write at the primary hit; an HDR capture forces
  the CPU trace via the `aovOk` gate (like Components/Motion). `LinearFloatImage.FromHdrByteScale(hdr, fallback, w, h)`
  bridges it into the intermediate — a NaN channel decodes the 8-bit fallback so a
  buffer with no captured HDR reduces EXACTLY to `FromBgra` (transform matches the
  plain 8-bit path byte-for-byte), captured pixels use `value/255` as linear so a
  highlight above 255 keeps real headroom. `PosterRenderer` consumes it when a view
  transform is active on a relief-raymarch poster with a NEUTRAL grade; every other
  case is the unchanged 8-bit path. Byte-identical by default; +6 tests, suite
  2188/2188.
- **Producer wiring — live relief HDR (landed — PR #651):** the on-screen relief
  view now mirrors the poster. `ReliefDenoisePass.MakeCapture` gained a `captureHdr`
  overload (ORs the HDR-beauty plane into the same capture the denoise guides use),
  and `FractalRenderHost.UploadProcessedBuffer` arms it when a view transform is
  active with a NEUTRAL grade + denoise off, then tonemaps the true-linear
  intermediate via `FromHdrByteScale` at the view-transform stage — the SAME
  producer→consumer path the poster uses, so **screen and poster match**. The HDR
  gate on both paths excludes an enabled denoise (the HDR plane is pre-denoise, so a
  guided denoise keeps the 8-bit tonemap). Byte-identical by default.
- **Producer wiring — offline video + slideshow relief (landed — PR #651):** the
  batch relief frame paths now match the poster + live view. `ApplyViewTransform`
  (BatchRenderer) is HDR-aware — given a captured relief HDR beauty it tonemaps the
  true-linear intermediate via `FromHdrByteScale`, else the plain 8-bit path — and
  `RenderReliefRegionFrame` / the zoom loop's `RenderZoomFrame` capture the HDR plane
  on the steady relief frames (zoom video single-frame, slideshow-leg zoom, still-
  slideshow re-render). Gated by `ReliefHdrWanted` (transform + neutral b/c +
  relief-raymarch + denoise off); cross-fade / motion-blur / flat frames keep the
  8-bit path. Byte-identical by default. (BatchRenderer is WinExe-only, so the batch
  wiring is build-verified; the shared `FromHdrByteScale` + relief capture are
  Engine-tested.)
- **Remaining:** the last producers (a full-float 2D composite, EXR read-back — both
  bigger, beyond the relief path), default-look validation, SIMD.

### S3 — Cinematic camera: DOF, exposure, motion blur ◐ (#400)
Depth of field is nearly free in a raymarcher — jitter the ray origin across an
aperture disc. Motion blur samples over Scene-Engine time (keyframes already
exist). Exposure / tonemap piggybacks on S2. Highest visible "wow" per line of
code.
- **Reuse:** existing camera (`BuildObliqueCamera`, perspective/ortho), Scene
  Engine time; the S2 exposure (`ViewExposureEv`).
- **Twin:** aperture/time jitter is seeded + deterministic → twinnable (mirror
  the existing `HashPair` discipline used for GGX sampling).
- **Status:** `CameraDof` landed — pure thin-lens math (concentric disc sample +
  `ThinLensRay` re-aimed through the focal point), `FractalParameters.Relief2DDof*`
  wired into the relief supersample loop (aperture 0 = pinhole identity; DOF forces
  CPU path so the parity gate is unaffected); 6 module tests + a relief blur
  integration test; `--reliefgpuraymarch` still PASS.
- **Wiring (integration follow-up, landed):** DOF is now user-drivable. Batch flags
  `--dof-aperture F` (0..1, 0 = pinhole) + `--dof-focus F` (≥0, 0 = auto-focus the
  fractal centre), both implying `--relief-raymarch` (perspective-only); parsed into
  `BatchOptions`, mapped to `fp.Relief2DDof*` in `BatchRenderer`, emitted by the CLI
  builder on the raymarch path (round-trip-tested) + populated from live params in
  `ControlCenterViewModel`. Live UI: **DOF aperture** + **DOF focus dist** rows in the
  Relief 3D dialog's Camera & quality expander (`FractalParamsViewModel` write-through
  `Fire()` re-render; focus greyed until aperture > 0). 8 wiring tests
  (`DofBatchWiringTests`). Pinhole (aperture 0) stays omitted = byte-identical.
- **GPU relief DOF (deep tail, landed — PR #454):** thin-lens DOF ported into the
  shared relief compute kernel (D3D + Vulkan) with a matching CPU twin. `CSRelief`
  factors its trace+shade into `TracePixel(o, rd)` and, when the aperture is open,
  averages `gDofSamples` lens taps (concentric-disc jitter + re-aim through the focal
  point); `ReliefRaymarchGpu.RenderCpuMirror` runs the identical loop as the oracle.
  DOF no longer forces the CPU trace. `--reliefgpuraymarch` gate exercises DOF (mean
  channel diff 0.267, 0 edge pixels) — the lens averaging keeps the disc-trig float-vs-
  double divergence inside the gate band. Pinhole stays byte-identical.
- **Compiled-shader disk cache (landed — PR #578, Closes #456):** GPU compute shaders
  were compiled from HLSL at runtime on first use and cached only for the process
  lifetime, so every launch paid a one-time FXC/DXC compile before the first GPU render
  — seconds on weak hardware, and the DOF variant's lens-loop is the slowest single
  compile. Surfaced while fixing the S3 GPU-DOF first-render regression (#454): that fix
  removed the *pathological* extra compile; this persists the compiled blob to disk so
  the baseline one-time compile is paid once per machine, not once per process. Backend-
  agnostic `Abstractions/ShaderBlobCache` (FXC bytecode + DXC SPIR-V) keyed by a SHA-256
  of the exact source + entry + profile + flags + a format-version tag; pure accelerator
  (any miss/corruption → compile from source; driver-reject → invalidate + recompile).
  Covers every runtime-compiled kernel (Mandelbrot, relief pinhole+DOF, froxel) on both
  D3D and Vulkan. Startup-latency only — per-frame dispatch is unchanged. Opt-out
  `FF_NO_SHADER_CACHE=1`. See [Performance-Roadmap](../Performance-Roadmap.md) and the
  [GPU-Shader-Cache](../User/GPU-Shader-Cache.md) user note. 11 tests
  (`ShaderBlobCacheTests`).
- **Click-to-focus (landed — PR #569):** Alt+double-click the render sets the relief
  DOF focal plane to whatever surface is under the cursor.
  `HeightfieldRaymarch2D.FocusDistanceAtPixel` renders the depth AOV once with the
  same oblique camera + field the screen used and reads back the centre-tap ray
  distance (the pinhole plane, so an open aperture doesn't bias the pick); `NoFocus`
  (0) on a sky miss / off-frame / raymarch-off. Routed via an optional
  `IFractalInputController.ReliefFocusPickHandler` (Alt+double-click, else normal
  recenter) → `IFractalRenderHost.TryPickReliefDofFocus` (read-only, mirrors the live
  albedo + `_reliefHeight`) → `MainViewModel` writes the live params (same instance
  the Relief 3D dialog edits, no desync), opens a modest aperture if shut, re-runs the
  relief post-pass. 8 tests (engine depth pick + controller Alt-gating).
- **Motion blur on the batch zoom video (landed — PR #571, #568):** the offline
  SceneVideoRenderer already averaged sub-frames; the batch video-zoom loop strobed.
  New reusable `MotionBlurAccumulator` (Engine/Rendering) — weighted BGRA-frame
  averager + `ShutterSamples` (spreads an output frame's parametric time over a
  shutter window; closed shutter → single tap = byte-identical). `RenderVideo` now
  averages `MotionBlurSubframes` sub-frames per output frame across the inter-frame
  zoom step (reuses `--motion-blur` / `--shutter`, scene-only before); froxel history
  advances once/output-frame. Accumulator unit-tested (+8); batch loop build-verified.
- **Remaining:** thin-lens DOF on the other 3D families + GPU kernels (#567; CPU
  Mandelbulb landed in PR #570), motion blur unify scene+video + live/poster (#568),
  in-camera exposure control.
- **Thin-lens DOF on the 3D cameras — CPU Mandelbulb (landed — PR #570, #567):**
  `LightingFxData.DofThinLens` averages `DofSamples` primary rays jittered across the
  aperture disc + re-aimed through the focal point (`CameraDof.ThinLensRay`) — real
  bokeh that integrates the scene, replacing the screen-space gather on that camera.
  HDR-correct (averages in linear when tonemap/bloom is on), seeded/deterministic,
  byte-identical off / aperture 0. SSAO + edge-ink are bypassed while it's on (they
  need the G-buffer the lens path skips). Wired into the preset DTO + Volumetric FX
  dialog. Remaining on #567: the other 3D families + the GPU raymarch kernels.
- **Thin-lens DOF on the other CPU 3D families (landed — PR #576, #567):** extended the
  Mandelbulb camera to the five remaining families that shade through `Shade<TDe>`
  (Mandelbox / Quat Julia / Quat Mandelbrot / Bicomplex / Kleinian). The per-pixel
  aperture-averaging is extracted into ONE shared helper `Rendering/Lighting/ThinLensDof`
  (`IsActive`/`SampleCount`/`FocusDistance` + `AccumulatePixel` — the exact arithmetic
  Mandelbulb #570 shipped inline). Each family gained a per-thread `ShadeRay` closure + a
  thin-lens branch; SSAO / screen-space DoF / edge-ink are bypassed while on, tonemap/bloom
  still consume the averaged HDR, and each family's GPU fast-path forces CPU when thin-lens
  is armed. Byte-identical off / aperture 0; the shared `DofThinLens` checkbox already drives
  all families. +15 `MultiFamilyThinLensDofTests`.
- **Remaining:** thin-lens DOF on the **GPU** raymarch kernels (all families, #567),
  in-camera exposure control.

### S4 — Guided denoiser (À-Trous / SVGF-lite) ● (#402)
AO, soft shadow and reflections are Monte Carlo → noisy → paid for with
supersamples. A bilateral / edge-avoiding denoiser **keyed on the normal + depth
AOVs from S1** cuts samples for equal quality. High fit precisely because S1
supplies the guide buffers.
- **Reuse:** S1 AOVs as guides.
- **Twin:** À-Trous is a deterministic separable filter — twinnable; keep it
  CPU-parity so `--batch` renders denoise identically.
- **Depends on:** S1.
- **Status:** `AtrousDenoiser` landed — pure deterministic B3-spline À-Trous with
  color + optional normal/depth edge-stopping weights, `Iterations` 0 = identity;
  5 tests incl. guided normal-edge preservation.
- **Integration (landed, PR #418):** `Imaging/ReliefDenoisePass` wires the operator
  into the relief-raymarch path keyed on the render's own float normal + depth AOVs
  (#416). `MakeCapture`/`Apply` at all three raymarch sites (poster, live final
  frame, cached recolour; preview left FX-stripped). `Iterations` 0 (default) ⇒ no
  capture ⇒ GPU fast path kept ⇒ **byte-identical**; a non-zero count forces the CPU
  trace (kernel emits no AOVs yet), so the guides are always the render's float data
  (CPU-parity). Surface: `FractalParameters.Relief2DDenoise{Iterations,Color/Normal/
  DepthSigma}`, `Relief3DDialog` rows, `--denoise` / `--denoise-{color,normal,depth}-
  sigma` batch flags + builder round-trip. `DenoiseBatchWiringTests` +
  `ReliefDenoiseIntegrationTests` (off = byte-identical, on changes + deterministic);
  suite 1612/1612.
- **GPU AOV emit (deep tail, landed — PR #457):** the relief compute kernel (D3D +
  Vulkan) now emits the primary-hit float normal + depth into a second UAV, twinned by
  `RenderCpuMirror` and proven by the `--reliefgpuraymarch` AOV diff (normal 1-|cos|
  mean 0.00000, depth rel-err mean 0.00004). A normal/depth-only (denoise) capture no
  longer forces the CPU trace — the GPU renders beauty + guides and only the À-Trous
  filter runs on the CPU. A component capture (AOV-EXR export) still forces CPU.
- **Adaptive-supersample coupling (landed — PR #572, #402):** `DenoiseSupersampleCoupling`
  halves the CPU relief raymarch's AA supersample while the denoiser is active (SS 4→2 =
  16→4 rays/px), so a denoised render finally casts fewer primary rays — the denoiser's
  raison d'être. `FractalParameters.Relief2DDenoiseAdaptiveSupersample` (default OFF),
  `--denoise-adaptive-ss` flag + CLI round-trip + Relief 3D dialog checkbox. Byte-identical
  off / denoise-off (+10 tests). GPU relief has no screen-space SS, so CPU-path only.
- **Parallel À-Trous (landed — PR #574, #402):** the denoiser's pixel loop runs its
  rows under `Parallel.For`. Within a pass each output pixel is independent (reads the
  r/g/b planes, writes only its own `tr/tg/tb[pi]`), so the parallel result is
  byte-identical to the serial pass — the per-pixel accumulation order is unchanged and
  the exact-value `AtrousDenoiserTests` lock it. No race against the ping-pong plane
  swap (`Parallel.For` is synchronous, completing before the swap reassigns the refs).
  +1 race-guard test (a 320×240 guided buffer denoises to identical bytes across 6 runs).
- **SVGF temporal accumulation (landed — PR #644, #402):** the temporal half of SVGF.
  `SvgfTemporal.Accumulate` reprojects the previous accumulated frame along the S1
  motion-vector AOV (#398) and blends `out = current·(1−α) + historyReproj·α`, rejecting
  disocclusion (off-frame reprojection / normal disagreement / relative depth jump → keep
  the current pixel, so revealed surfaces don't ghost). Pure, deterministic, parallel;
  null history (first frame) or α 0 → byte-identical. +8 `SvgfTemporalTests`.
- **Variance-guided À-Trous (landed — PR #645, #402):** the spatial half of SVGF.
  `SvgfVariance` (Engine/Imaging) estimates a per-pixel luminance variance — `EstimateSpatial`
  (local `(2r+1)²` variance from one frame; the no-history fallback + spatial pad) and
  `FromMoments` (`var = E[l²] − E[l]²` from accumulated luminance moments; the temporal path).
  `AtrousDenoiser` gained an optional `variance` guide + `varianceScale`: the colour
  edge-stop's sigma scales per pixel by `1 + varianceScale·sqrt(variance)`, so high-variance
  (unconverged) pixels blur more while low-variance detail is preserved. Null variance /
  scale 0 → byte-identical (the exact-value À-Trous tests still pass). +8 `SvgfVarianceTests`.
  Both SVGF halves now exist as operators.
- **SVGF united in `ReliefDenoisePass` (landed — PR #646, #402):** both halves now run in one
  pass. `SvgfHistory` holds the per-render persistent state (previous denoised colour + normal /
  depth + camera + valid flag); `ReliefDenoisePass.ApplySvgf` reprojects the previous denoised
  frame along the motion AOV and blends (`SvgfTemporal`, disocclusion-rejected), estimates the
  variance from the accumulated frame (`SvgfVariance`), runs the variance-guided À-Trous
  (`AtrousDenoiser`), and stores the result back into the history. The first frame falls back to
  the plain spatial denoise + seeds the history; the temporal toggle off defers to `Apply`.
  `Relief2DDenoiseTemporal` / `TemporalFeedback` / `VarianceScale`; `MakeCapture` adds the motion
  AOV only when temporal. Default off → byte-identical. +6 `ReliefSvgfIntegrationTests`.
- **SVGF wired into the sequence render path (landed — PR #647, #402):** `ApplySvgf` now
  runs in an actual sequence. `PosterRequest.SvgfHistory` threads through `PosterRenderer` (both
  `ApplyReliefIfEnabled` sites) → `ApplySvgf` when a history is supplied and the temporal toggle
  is on, else the plain `Apply`. `SceneVideoRenderer` owns one persistent `SvgfHistory` for the
  render, threaded into clean continuous relief frames; the request's `previousCamera` is taken
  from `history.PrevCamera` (the pass carries the camera on the history). Byte-identical off (the
  S6 scene tests, now threading the history unused, still pass). +3 `ReliefSvgfSequenceTests`.
- **SVGF UI-reachable (landed — PR #648, #402):** `Relief3DSettings` gains the denoise + SVGF
  fields (Iterations / sigmas / AdaptiveSupersample / Temporal / TemporalFeedback / VarianceScale)
  at the four region sites the froxel fields use (property / Snapshot / ApplyTo / ApplyOrDisable
  clear). The Region Editor already snapshots relief from live params (#566), so enabling the
  denoise + a new **"Temporal (SVGF)"** checkbox (+ feedback / variance rows) in the Relief 3D
  dialog + saving a region persists it — and an offline scene / batch render of that region runs
  SVGF end-to-end. Default off → byte-identical. +4 `RegionRelief3DTests`.
- **Temporal luminance moments (landed — PR #649, #402):** the last SVGF fidelity step.
  `SvgfMoments` accumulates E[l]/E[l²] across frames with the SAME motion reprojection +
  disocclusion the colour history uses, plus a per-pixel history length; `SvgfHistory` carries
  `Moment1`/`Moment2`/`Length`; `ApplySvgf` derives the temporal variance (E[l²]−E[l]²) and blends
  it from the spatial estimate toward the temporal one by history length (fresh/disoccluded →
  spatial, converged → temporal). Default off → byte-identical. +7 tests. **SVGF is now feature-
  complete** (temporal #644 + variance #645 + unite #646 + sequence wiring #647 + UI #648 +
  temporal moments #649).
- **SIMD À-Trous (landed — PR #650, #402):** an opt-in `AtrousParams.UseSimd` vectorizes the
  guided gather over `Vector<float>.Count` pixels with a shared 2^f poly-exp (`SFExp`/`VExp`),
  de-interleaving the normal guide to planar arrays and vectorizing each row's fully-interior
  span (scalar borders + remainder). It is NOT byte-identical (float32 + reordered sums +
  poly-exp) so it is off by default and the scalar path stays the `--batch` oracle; it
  auto-falls-back when SIMD isn't hardware-accelerated. +4 tests (SIMD within ≤4/5 LSB of scalar,
  deterministic, off = exact scalar). Wiring `UseSimd` through a params / batch surface is a thin
  follow-up.
- **S4 (#402) is fully addressed** ● — guided À-Trous + integration + GPU AOV emit + adaptive
  supersample + the full SVGF pipeline (temporal #644 / variance #645 / unite #646 / sequence
  wiring #647 / UI #648 / temporal moments #649) + the opt-in SIMD fast path (#650).

### S5 — Refractive / transmissive materials ◐ (#406)
Cook-Torrance GGX today is opaque. Add **transmission + IOR** → glass fractals.
Fits DE raymarching natively: at the surface, refract the ray and keep marching.
The Principled-BSDF lesson taken *only as far as FF's primitive allows* (skip the
full uber-shader — add transmission, optionally clearcoat / emission).
- **Reuse:** the surface-hit shade point + reflection march plumbing.
- **Twin:** refraction ray continuation is deterministic → twinnable across all
  three backends (mirror the reflection path already twinned in
  `ReliefRaymarchGpu.Reflections`).
- **Status:** `DielectricOps` landed — pure Snell refraction + TIR, mirror reflect,
  Schlick Fresnel, per-channel Beer-Lambert; `LightingFxData` gains
  `Transmission`/`Ior`/`Absorption*` (opaque default = byte-identical, persisted);
  6 tests.
- **Shade wiring (integration follow-up, landed):** `ShadingPipeline.Shade<TDe>`
  now refracts on a transmissive hit — refract the view ray (`DielectricOps.Refract`
  / TIR), sample the environment along the refracted dir (the distorted see-through
  background), Beer-Lambert-tint it, Fresnel-mix with the reflected environment, and
  blend into the surface by `Transmission`. `HasPositionalLight`-style, a
  transmissive material forces the **CPU** relief trace (GPU kernel has no refraction)
  so the parity gate is untouched. UI: **Transmission / IOR / Absorption dist**
  sliders in the LightingFx dialog's Material section (IOR + absorption greyed until
  transmission > 0). 4 wiring tests (`RefractionShadingTests`) incl. the opaque
  byte-identical gate + a colored-absorption tint check. SCOPE: **environment-
  refraction** approximation — one interface, no internal two-surface march.
- **GPU-kernel refraction (deep tail, landed — PR #458):** the relief kernel's
  `ShadeFlat` (D3D + Vulkan) + its CPU twin now apply the env-refraction approximation
  (refract view ray → env sample → Beer-Lambert → Fresnel-mix, `DielectricOps` twin),
  so glass no longer forces the CPU trace. `--reliefgpuraymarch` glass diff GPU vs twin
  mean channel diff 0.003; opaque stays byte-identical.
- **Full internal glass march — CPU (landed — PR #573, #406):** `LightingFxData.
  RefractInternalMarch` upgrades the env-approx to true two-surface glass: on a
  transmissive hit the shader sphere-traces the DE from the front hit through the
  solid to the back surface, so Beer-Lambert runs over the REAL thickness (thick
  parts tint / darken more) and the ray refracts a SECOND time on exit
  (`DielectricOps.Refract`, outward normal negated, `eta = ior`). TIR at the back
  keeps the internal direction (an internal-reflection bounce is a follow-up).
  CPU-path (forces CPU like all transmission), byte-identical off / opaque, LightingFx
  dialog checkbox + preset-DTO persist. +3 tests.
- **Glass batch flags + Absorption-colour picker (landed — PR #575, #406):** glass
  finally has a batch surface and a UI tint picker. New `--glass` (shorthand → 0.9) /
  `--transmission` / `--ior` / `--absorption-dist` / `--absorption-color` /
  `--glass-internal-march` flags (mirror the `--dof-*` / `--lightN-*` cadence; all imply
  `--relief-raymarch`), parsed + range-checked in `BatchOptions`, applied onto
  `fp.Lighting` in `BatchRenderer.BuildFractalParameters`, emitted by
  `BatchCommandBuilder` only on the raymarch path when transmissive (ior / absorption /
  colour emit only off-default), and populated from live params in the Control Center
  snapshot. UI: an "Absorption color" hex `TextBox` row in the LightingFx dialog
  (`AbsorptionColorHex` VM accessor, mirrors `FogColorHex`, greyed until Transmission>0).
  Opaque = byte-identical. +13 `GlassBatchWiringTests` (the WinExe apply is
  build-verified, same boundary `S6VideoReliefBatchTests` documents).
- **Remaining:** GPU internal-march twin, back-surface TIR internal-reflection bounce,
  rough refraction (couple S4).

### S6 — Froxel / unified volume march ◐ (#408)
Today's volumetrics are per-surface single-scatter. A froxel (frustum-voxel)
volume LUT à la Frostbite/Hillaire unifies fog across all 3D types and — crucially
— gives **temporal stability**, which becomes necessary the moment the Scene
Engine animates fog. Medium fit (already ray-marching), pays off when 3D goes
animated.
- **Reuse:** the in-scatter walk math; Scene Engine for temporal reprojection.
- **Twin:** froxel population is deterministic; temporal reprojection needs a
  history buffer — twin the single-frame path, treat temporal as an additive,
  gated layer.
- **Related:** #388 (multi-light relief in-scatter) is a smaller, nearer-term
  step on the same volumetric axis.
- **Status:** `FroxelGrid` (exponential near-dense depth slices + invertible
  `DepthToSlice`) + `FroxelIntegrator` (energy-conserving front-to-back column
  integration à la Hillaire + depth sample) landed; 6 tests incl. uniform-medium
  transmittance == exp(-σ·d).
- **Pass assembly (integration follow-up, landed):** `FroxelVolumePass` assembles
  the primitives into a **populate → integrate → composite** pipeline. `Populate`
  fills every froxel with noise-modulated density → extinction + single directional
  in-scatter (Henyey-Greenstein phase — the same model as the per-surface march,
  reusing the public `ShadingPipeline.FbmCloud3D`), then integrates each column
  front-to-back (`FroxelIntegrator`). `Composite(beauty, depth01, w, h)` attenuates
  each pixel by the transmittance in front of its depth and adds the accumulated
  in-scatter — a cheap depth-indexed read, no per-pixel march. Pure/deterministic
  (twinnable, --batch-stable). 6 tests (`FroxelVolumePassTests`) incl. empty-medium
  no-op, far>near attenuation, Beer-Lambert far transmittance, colored in-scatter
  tint, noise heterogeneity. World mapping is an axis-aligned slab (depth along the
  froxel Z).
- **Render wiring (landed, PR #461):** `FroxelCameraVolume` frames a `FroxelGrid`
  over the oblique relief scene (near/far from camera + slab) + builds a
  `FroxelMedium` from the fog knobs + key light, and
  `FroxelVolumePass.CompositeWorldDepth` composites by per-pixel world depth through
  the exponential `DepthToSlice`. Opt-in `FractalParameters.Relief2DFroxelVolumetrics`
  (+ `--relief-froxel`): the relief beauty renders fog-free, depth is captured, and
  the volume composites as a CPU post-pass (replacing the per-pixel background march).
  Forces the CPU trace; default off → byte-identical. 13 tests. Reachable via the
  "Froxel volumetrics" checkbox in the Relief 3D dialog + `--relief-froxel`.
- **Multi-light + positional (landed, PR #462):** `FroxelLight` + `FroxelMedium.Lights`
  sum all three scene lights per froxel — each with its own dir / colour / HG phase,
  and per-cell inverse-square/range/cone falloff for point/spot (via `LightSampler`).
  `FroxelCameraVolume.BuildMedium` fills `Lights[3]` from `fx.Light1/2/3`. Null Lights
  keeps the legacy single directional light byte-identical. Matches the per-pixel
  march's #388 model.
- **Per-light fog mask (landed, PR #463):** `LightingFxData.VolumeLightMask` (bit n =
  light n+1 lights the fog; default 0x7 = all) lets a light illuminate surfaces but be
  excluded from the fog in-scatter. Honoured by the per-pixel march, the froxel volume
  and the GPU relief kernel (reused an S8 pad row, no ParamBytes change) + CPU twin;
  persisted; UI checkboxes + `--fog-light-mask`.
- **GPU froxel compute pass (D3D landed, PR #464):** `FroxelKernelSource` (shared
  HLSL, two entry points) + `FroxelGpuKernel` (D3D11) reproduce the froxel pass on
  the device: `CSFroxelIntegrate` populates + integrates each column (one thread/
  column — noise-modulated density → extinction + multi-light HG in-scatter,
  front-to-back), `CSFroxelComposite` composites over the fog-free beauty by per-
  pixel world depth (one thread/pixel). The GPU twin of `FroxelVolumePass.Populate`
  + `FroxelCameraVolume` — both driven by the SAME `FroxelGrid` + `FroxelMedium` via
  the backend-agnostic `FroxelGpuUniforms`. Noise/HG/spot-cone/light-resolve are
  line-for-line ports of the already-parity-proven relief-kernel helpers. Proven by
  the `--froxelgpu` WARP gate (headless, no GPU): mean channel diff 0.000, max 1 LSB
  (float shader vs double CPU), fog changed 100% of pixels. `IFroxelVolumeKernel`
  seam mirrors `IReliefRaymarchKernel`. 4 uniform-seam tests. Nothing calls it in
  the live render path yet → default byte-identical.
- **GPU froxel host wiring (landed, PR #465):** the froxel kernel is threaded through
  the host so a GPU relief + froxel render composites the fog entirely on the GPU —
  lifting the previous force-CPU. `HeightfieldRaymarch2D.Render` takes an optional
  `IFroxelVolumeKernel`; when froxel is on and both a relief kernel + froxel kernel are
  attached, the relief kernel renders the fog-free beauty + depth AOV and the froxel
  kernel composites over it by that depth (the AOV depth is the SAME `sdepth` the CPU
  path feeds the composite). `FractalRenderHost.FroxelKernelFactory` + lazy
  `EnsureFroxelKernel` mirror the relief kernel; `FroxelKernelFactoryHook` is installed
  by `WindowsBootstrap` (DirectXRenderer → `FroxelGpuKernel`) + wired by
  `AvaloniaShellBootstrap`. Kernel-gated (no froxel kernel → CPU post-pass) + default
  off → byte-identical. 2 routing tests; suite 1711/1711.
- **Vulkan froxel kernel (landed, PR #466):** `FroxelVolumeVulkanKernel` is the
  cross-platform twin of the D3D `FroxelGpuKernel` — two compute pipelines from the
  SAME shared `FroxelKernelSource` (FXC→cs_5_0 on D3D, DXC→cs_6_0 -spirv here),
  `CSFroxelIntegrate` then `CSFroxelComposite` in one command buffer with a
  shader-write→read barrier between; shared descriptor layout `{b0,t0..t2,u0}` with
  two sets (integrate binds the volume at u0, composite at t2 + output at u0).
  Bootstrap's `--renderer vulkan` branch installs the froxel factory beside the relief
  kernel, so GPU relief + froxel composites fog on-GPU on Linux/macOS too. The
  `--vulkanfroxel` gate (same scene/oracle/tolerances as `--froxelgpu`) PASSES on real
  hardware (GT 710: mean 0.059, 0 edge px, 0 alpha). Shared HLSL + D3D kernel unchanged.
- **Temporal reprojection (landed, PR #467):** `FroxelHistory` holds the previous
  frame's per-cell scatter + extinction (grid-keyed by dims + near/far) and
  `BlendAndStore` exponentially blends the current frame into it BEFORE integration
  (`out = current·(1-a) + history·a`), so animated fog (drifting noise, pulsing density,
  moving lights) reads as a stable volume. `FroxelVolumePass` was refactored to fill a
  full per-cell scatter/ext grid then integrate from it (null history / feedback 0 →
  byte-identical). A grid-key change (camera move → near/far) re-seeds cleanly (a=0,
  the temporal-AA disocclusion fallback). `FractalRenderHost` owns one persistent
  history; temporal forces the CPU froxel post-pass (GPU froxel stays single-frame).
  `Relief2DFroxelTemporal` + feedback knob (0.9) + Relief 3D dialog checkbox/slider.
  11 tests; suite 1722/1722; --froxelgpu gate still PASS.
- **Temporal history seam for offline renders (landed, PR #468):** `PosterRequest.FroxelHistory`
  threads a caller-owned `FroxelHistory` through `PosterRenderer` → `ApplyReliefIfEnabled`
  → the froxel CPU post-pass, so a sequence renderer can share one instance across frames
  (needs `Relief2DFroxelTemporal`). Recon note: the LIVE video recording already gets
  temporal via `FractalRenderHost`'s persistent history; the offline `SceneVideoRenderer`
  + `--batch` video-slideshow render FLAT 2D (no Relief 3D applied at all), so the seam has
  no consumer there yet. 2 seam tests; default null → byte-identical.
- **Quality controls + user doc (landed, PR #507):** `FroxelQuality` {Low 16×16×32,
  Balanced 24×24×48, High 32×32×96} scales the froxel grid. `FractalParameters.Relief2DFroxelQuality`
  (default Balanced → byte-identical) threads through `FroxelCameraVolume.BuildGrid(cam, quality)`
  + `FroxelGpuUniforms.Build(cam, fx, quality)`, so the CPU post-pass AND the GPU kernel read the
  scaled dims off the SAME grid — lock-step, no parity break (GPU cbuffer already carried per-grid
  dims). Only the resolution scales; the near/far bracket is quality-independent. Surface:
  `--relief-froxel-quality Low|Balanced|High` (implies `--relief-froxel`), Relief 3D dialog Quality
  combo, builder emits the quality flag only when non-Balanced (bare `--relief-froxel` at Balanced),
  Reset/notify wired. **User doc:** the froxel path is documented for users in
  `Docs/User/Volumetric-Lighting-Guide.md` §9 (what it is, quality table, temporal, batch grammar) —
  closes the S6 "UI + batch / user doc" checkbox on #408. 10 tests (`S6FroxelQualityTests`).
- **Relief 3D in the batch video loop (landed):** `BatchRenderer.RenderVideo` now renders
  each frame through the composed relief+froxel path (`PosterRenderer.RenderToPixels`) when
  any `--relief` flag is set, threading ONE shared `FroxelHistory` across frames — so the
  froxel temporal-reprojection seam (#468) finally has a consumer. New `--relief-froxel-temporal`
  / `--relief-froxel-feedback` batch flags make it reachable (imply `--relief-froxel`). The
  opts→`FractalParameters` mapping was extracted to a shared `BuildFractalParameters(opts)`
  reused by image + video (no duplication). Relief off → the flat fast path, byte-identical.
  Verified headless: a relief video frame differs from a flat one; a temporal froxel video
  renders clean. 3 CLI-grammar tests (`S6VideoReliefBatchTests`).
- **Relief 3D in the batch slideshow (landed):** `BatchRenderer.RenderSlideshow` now carries
  Relief 3D on BOTH cadences — the animated video-zoom legs (`RenderVideoSlideshowLegs`) and
  the still-image cross-fade loop. A shared `RenderReliefRegionFrame` helper renders a
  region+theme through the same composed relief+froxel path, carrying the region's
  extended-precision centre limbs; ONE shared `FroxelHistory` threads the froxel temporal seam
  (#468) across the show. Theme selection keeps the cheap flat all-black probe; only the final
  frame re-renders in relief. Cross-fade sub-renders pass a null history so the temporal
  timeline is not double-advanced within one output frame. Relief off → the flat fast path,
  byte-identical. Verified headless: a slideshow frame renders as a true 3D raymarch (terrain,
  perspective, fog, silhouette) vs the flat 2D frame. +1 CLI-grammar test.
- **Relief 3D in the Scene Engine renderer (landed):** `SceneVideoRenderer.RenderShotFrame`
  built a `PosterRequest` but rendered it through the flat capture calculator, so a shot whose
  region carried a Relief 3D snapshot (`region.ApplyRelief3DTo` DID populate the params) still
  rendered FLAT — the oblique raymarch was dropped. It now diverts to
  `PosterRenderer.RenderToPixels` (composed relief+froxel buffer, same rawness as the flat path —
  before b/c / view-transform / interior composite) when the resolved params enable the raymarch
  (`Relief2DEnabled && Relief2DRaymarch`). Froxel was spatial-only here at first (per-call
  history) — since brought to cross-frame temporal parity (see the scene-temporal bullet below).
  2 Engine-side tests (`S6SceneReliefRenderTests`):
  a relief region resolves to raymarch-enabled params, and a one-shot scene renders a frame that
  differs from the flat render (relief actually applied, not a black wash).
- **GPU froxel temporal reprojection (landed):** the GPU froxel kernel was single-frame —
  turning on `Relief2DFroxelTemporal` forced the slow full-CPU froxel post-pass even with the
  compute kernels attached. The D3D `FroxelGpuKernel` now keeps its OWN persistent device-side
  history buffer (a `float4/cell` PRE-integration scatter+ext grid, `u1` in `CSFroxelIntegrate`):
  each cell's scatter+extinction is exponentially blended toward the previous frame's, then stored
  back — the exact GPU twin of `FroxelHistory.BlendAndStore`, keyed by grid identity so a camera
  move re-seeds cleanly. `IFroxelVolumeKernel.Composite` gained a `feedback` overload (default-impl
  ignores it, so a backend without device history stays single-frame). The host
  (`HeightfieldRaymarch2D`) dropped the `!froxelTemporal` gate on the GPU froxel branch and threads
  `Relief2DFroxelTemporalFeedback`. Feedback 0 (temporal off) is byte-identical to the single-frame
  composite — the `--froxelgpu` gate is unchanged (mean 0.000 / max 1 LSB). New `--froxelgputemporal`
  WARP gate: two frames with a changed medium (grid fixed) prove the GPU device-history blend tracks
  the CPU `FroxelHistory` (mean 0.000 / max 1 LSB) AND that temporal actually shifts the result
  (~98% of pixels vs the single-frame composite). Seam tests updated (`FroxelTemporal_WithKernels_
  RunsGpuFroxelWithFeedback` + a non-temporal zero-feedback lock). The shared HLSL stays
  one-source/two-compiler for the Vulkan backend.
- **Vulkan froxel temporal reprojection (landed):** the cross-platform `FroxelVolumeVulkanKernel`
  (DXC → SPIR-V) matched the D3D temporal path. The shared HLSL's `gHistory` (`u1`) landed with the
  D3D slice; the Vulkan kernel gained the matching device-side history: a 6th descriptor binding
  (`u1` → 201 via the `-fvk-u-shift` map), a persistent `float4/cell` history buffer that survives
  across `Composite` calls (reallocated only on a cell-count change → drops validity), grid-key
  invalidation, and the `feedback` overload. `u1` is statically referenced by `CSFroxelIntegrate`,
  so it is in the descriptor set layout even at feedback 0 (bound but never read/written then) —
  the `--vulkanfroxel` gate is unregressed (GT 710: mean 0.059 / max 13, single-frame). New
  `--vulkanfroxeltemporal` gate (the Vulkan sibling of `--froxelgputemporal`): two frames, a changed
  medium (grid fixed), the Vulkan device-history blend tracks the CPU `FroxelHistory` (GT 710: mean
  0.049 / max 11) and temporal shifts ~98% of pixels. Both backends now reach full froxel parity
  (populate + integrate + composite + temporal) against the one CPU oracle.
- **Froxel region-configurable (landed):** froxel fog + its cross-frame temporal reprojection were
  UI/CLI-only — the `FractalRegion` relief snapshot (`Relief3DSettings`) didn't capture them, so
  every region-sourced offline render (`SceneVideoRenderer`, the batch relief legs) ran froxel-off
  and the whole temporal path was unreachable offline. The snapshot now carries
  `FroxelVolumetrics` / `FroxelTemporal` / `FroxelTemporalFeedback` / `FroxelQuality` (captured in
  `Snapshot`, restored in `ApplyTo`, serialized as an enum string); `ApplyOrDisable`'s disable
  branch clears froxel so a plain-region recall can't leave stale fog armed. Missing JSON →
  froxel-off defaults (legacy + plain regions byte-identical). Since the region editor already
  snapshots relief from live params, enabling froxel in the UI + saving a region persists it and
  lights scene/batch froxel + temporal. `RegionRelief3DTests` +5. PR #566.
- **Remaining (enhancement follow-ups):** cross-frame froxel **temporal reprojection** for
  scenes (the offline `SceneVideoRenderer` deferred it above — CPU froxel there is spatial-only);
  sub-cell reprojection under continuous camera motion. The
- **Scene cross-frame froxel temporal (landed):** the offline `SceneVideoRenderer` no longer
  renders froxel spatial-only. It now allocates ONE shared `FroxelHistory` for the whole render
  and threads it into the shot render via `PosterRequest.FroxelHistory`, so animated fog blends
  toward the previous OUTPUT frame's pre-integration grid — the same model as the batch
  video/slideshow legs. Threaded ONLY on clean continuous frames (a single motion-blur sub-frame,
  no frozen composite) so there is exactly one froxel store per output frame; motion-blur (>1
  sub-frame) and frozen crossfade / light-sweep frames stay spatial-only (null history). Shot cuts
  change the camera grid, so `FroxelHistory`'s grid-key check re-seeds at each cut with no manual
  reset. Byte-identical when froxel-temporal is off / feedback 0 / the medium is static; the
  froxel-volumetrics *enable* is not region-carried yet, so a region-sourced scene threads the
  history but leaves it unused today (the wiring activates the moment froxel becomes
  scene-configurable). Locked by `MultiFrame_Relief_Scene_Is_Deterministic_With_Shared_Froxel_
  History` (a multi-frame relief scene rendered twice is byte-identical frame-for-frame, every
  frame non-black — the stateful shared history stays deterministic). PR #565.
- **Remaining (enhancement follow-up):** sub-cell froxel reprojection under continuous camera
  motion (the grid-key check currently re-seeds on any near/far change instead of resampling). The
  core froxel unified-volume march (D3D + Vulkan + host wiring + CPU & GPU temporal + poster seam + quality
  controls + user doc + batch-video/slideshow/scene consumers) is complete.

### S7 — Float / multi-layer EXR export ☑ (#394)
Enabler for S1 (AOV layers), S2 (linear/HDR intermediate) and S6 (HDR
volumetrics). FF is 8-bit PNG/Skia today. Add OpenEXR float output with named
layers.
- **Reuse:** existing `ImageExport` / poster path; **mirror of the pure-managed
  `OpenExrReader`** already in the tree (HDRI env loader).
- **Twin:** file format, not render math — no twin needed; assert byte-stable
  output in a test.
- **Blocks:** S1, S2, S6 at full fidelity.
- **Status:** `OpenExrWriter` landed — pure-managed scanline encoder (HALF/FLOAT,
  arbitrary named channels, uncompressed → byte-stable), an 8-bit→linear-half
  bridge so `.exr` export works now, `ImageFileFormat.Exr` + `.exr` wiring, 6
  round-trip/byte-stability tests. Real float AOV layers now feed it via S1
  (#398/#412/#452/#453). GUI/batch surface landed (#480): the screenshot picker
  offers PNG/JPEG/OpenEXR and `FractalRenderHost.SaveLastFrame` infers the encoder
  from the extension (`.exr` → writer, no watermark); batch `GuessImageFormat`
  maps `.exr` → Exr. ZIP compression landed (#481): `ExrCompression {None, Zip}`
  on the writer — 16-line zlib blocks with the EXR predictor + interleave (exact
  inverse of `OpenExrReader`'s decode, round-trips through our reader + Blender),
  reader honours the spec raw-fallback, threaded via `PosterRequest.ExrCompression`
  + batch `--exr-zip`; NONE stays default + byte-identical. **Remaining:**
  Blender/oiiotool third-party read smoke (needs an external DCC — our reader
  validates structure, a third-party read validates spec conformance).

### S8 — Richer light types: point / spot / area ● (#404)
Three directional lights + IBL today. Point/spot (inverse-square + cone) and area
lights (soft realistic shadows) are cheap per-sample changes in a DE march.
Broadens the lighting vocabulary without a scene graph.
- **Reuse:** the per-light accumulation in `ShadingPipeline` + the relief twin.
- **Twin:** per-light attenuation is a scalar change → twinnable; area-light soft
  shadow raises sample cost (coordinate with S4 denoise).
- **Status:** `LightType` (Directional/Point/Spot) + point/spot fields on the light
  model, and `LightSampler` — pure dir+attenuation sampling (inverse-square + Karis
  range window + smooth spot cone; Directional = identity); 6 tests.
- **Shade wiring (integration follow-up, landed):** `ShadingPipeline.ResolveLight`
  now resolves each of the 3 lights per shade point — directional keeps the legacy
  `LightDir(θ+orbit, φ)` with attenuation 1 (**byte-identical**); point/spot call
  `LightSampler` with the surface position so direction + falloff are surface-
  relative. Wired at both surface shade sites (`Shade` + `Shade<TDe>`): diffuse,
  specular (GGX) and SSS all scale by the attenuation. `LightingFxData.
  HasPositionalLight` forces the **CPU** trace in the relief path (the GPU kernel is
  directional-only) — same discipline as DOF/DebugAov, so the parity gate is
  untouched. Point/spot fields persist through `LightingFxPresetData` (round-trip
  tested). 7 wiring tests (`LightTypeShadingTests`) incl. inverse-square falloff +
  a full-Shade brightness check. Directional-only shading suite (74) unchanged.
- **GPU relief kernel (landed, PR #459):** point/spot now resolve **on the GPU** +
  parity twin. HLSL `SmoothCone`+`ResolveLight` (twin of `LightSampler`) resolve each
  light per pixel in `ShadeFlat` (inverse-square + Karis range window + smooth cone);
  the CPU twin `ReliefRaymarchGpu.ShadeFlat` calls `LightSampler.Sample` directly so
  the oracle matches HLSL *and* production shading. Directional stays byte-identical.
  cbuffer/blob +6 rows (kinds, pos+range, cone cosines); ParamBytes 480→576 D3D +
  Vulkan. The `fx.HasPositionalLight` force-CPU clause is **lifted** — positional
  lights render on the GPU. `--reliefgpuraymarch` gate: new `lights (point+spot)` diff
  (mean 0.015, max 7, 0 edge px). Shadow-enable/spec gates keyed on base intensity.
- **UI + batch (landed, PR #460):** the LightingFx dialog's new "Light Types
  (Point / Spot)" expander exposes per-light Type + world Position + Range + spot
  cone (positional/cone rows gated by visibility flags). Batch grammar
  `--lightN-{type,intensity,dir,pos,range,cone}` (N=1..3; any implies
  `--relief-raymarch`), composed by `BatchFlags.LightFlag` so parser + builder can't
  drift; `BatchCommandBuilder` emits them for non-directional lights; round-trip +
  validation tests. So point/spot are now fully user-reachable (GUI + CLI).
- **Positional volumetric in-scatter (landed, PR #482):** the legacy per-pixel
  god-ray `VolumeSteps` walk now resolves point/spot per fog sample too
  (inverse-square × range × cone) instead of aiming-only. `AddVolumeScatter` (CPU) +
  `ReliefScatter` (D3D/Vulkan HLSL) + `AddReliefScatter` (CPU twin) mirror line-for-
  line; directional stays byte-identical (atten 1). New `--reliefgpuraymarch` scene
  `positional fog in-scatter` passes (mean 0.004, max 3, 0 edge). 3 CPU tests. The
  froxel volumetrics (S6) already lit positionally; this closes the older march.
- **Force-CPU on the GPU 3D-fractal calculators (landed, PR #483):** the 8 GPU
  3D-fractal kernels (Mandelbulb / Mandelbox / Menger + Sierpinski / QuatJulia /
  QuatMandel / Kleinian / Bicomplex) + UserBulb's GPU path resolve only a
  directional light and ignore world position, so each host now gates its GPU
  branch on `!fx.HasPositionalLight` → a point/spot scene falls to the CPU shade
  (LightSampler-correct) instead of silently directional. All-directional scenes
  keep the GPU path, byte-identical. 1 render test (Mandelbulb point-light move).
  The #459-style upgrade — teach the 8 GPU 3D kernels to resolve positional
  lights themselves and retire this force-CPU — is tracked as its own fan-out
  (**#484**, slices #485–#488): shared `GpuShadingParams` + a `GpuKernelUtils.
  ResolveLight`, then a per-family kernel + CPU-twin + parity-probe pass.
- **Per-light colour batch flag (landed, PR #489):** `--lightN-color`
  (#RRGGBB / #AARRGGBB / 0x / bare hex) completes the per-light batch grammar
  (was type/intensity/dir/pos/range/cone); parser + command-builder emit (only
  when ≠ slot default), 7 tests.
- **Area lights (landed, PR #491) — the last S8 feature:** every light gains an
  angular size `DirectionalLight.AreaAngularRadius` (deg) — its apparent emitter
  size from the surface (sun disc ≈ 0.25°, soft panel ≈ 5–15°). `ShadingPipeline.
  EffectiveShadowK(globalK, areaDeg)` caps the IQ soft-shadow hardness at
  `cot(radius)` — a shadow can't be sharper than the emitter physically allows,
  so it's the *softer* of the global `ShadowSoftK` and the physical cap. **Purely
  analytic** (no stochastic disc sampling) → despite the original note it does
  **not** couple S4; the noisy multi-tap disc/sphere variant that would is
  deferred. Wired per light into both `Shade` sites + the volumetric in-scatter
  (`AddVolumeScatter` gains `areaAngRad`, threaded at 3 sites). Punctual (radius 0)
  → `k` unchanged, exact IEEE no-op → **byte-identical**. `LightingFxData.
  HasAreaLight` forces the CPU trace on the GPU relief kernel + the 8 GPU
  3D-fractal calculators + UserBulb GPU path (same cadence as #483; GPU per-light-
  penumbra parity is a follow-up). Preset round-trip, `--lightN-area <deg>` (0..90)
  batch flag + builder emit (positional, non-zero only), per-light "Area soft (°)"
  UI control. 12 tests (`S8AreaLightTests`); suite 1768/1768.
- **User doc (landed):** [Lights Guide](../User/Lights-Guide.md) — three lights,
  directional / point / spot / area types, colour, shadows, animation, the full
  `--lightN-*` batch grammar, and per-type performance notes. Linked from both doc
  indexes. Closes the last S8 checkbox on #404.
- **S8 COMPLETE ●** (point / spot / area + per-light colour + user doc). Remaining
  follow-ups (all separate issues): the builder emits `--lightN-*` only for
  **positional** lights — a directional light's dir/intensity/colour/area is not
  emitted because `--lightN-*` forces `--relief-raymarch` on replay; decoupling
  that + directional emit is **#490**. GPU positional-light parity for the 3D
  families (retire #483's force-CPU) is **#484** (+#485–488); GPU parity of the
  **area penumbra** (retire #491's force-CPU) is **#492**.

### S9 — Mesh export maturation ☑ (#391)
Mesh export is the **one place FF crosses from renderer into geometry producer** —
THE handoff line. The discipline: be a great mesh *exporter*, never a mesh
*editor*. Today FF meshes the relief heightfield (2.5D displaced grid → printable);
the DE opens far more. Same reuse thesis as the render slices — **FF already holds
everything a great exporter needs and just doesn't assemble it**: the DE field
(surface = DE == 0), the analytic DE gradient (exact vertex normals), the color map
(vertex colors), the shading material (roughness/metallic/albedo), and the
empty-space-skip mip grid (near-surface octree seeds for adaptive meshing).

Two DE-native superpowers Blender can't match cleanly — lean into these:
- **Exact isosurface + analytic normals.** FF has the function that *defines* the
  surface, so it meshes ground truth (marching cubes → **dual contouring**, which
  preserves sharp Mandelbox/KIFS edges from the gradient). Blender remeshes an
  approximation.
- **Analytic hollow / shell.** Print wall thickness = a second isosurface at
  `DE == −t`. FF hollows a fractal *exactly* by re-thresholding the DE; Blender's
  shell modifier self-intersects on concave fractal detail.

Sub-items (ranked fit × payoff):
1. **Watertight / manifold as a hard contract** — the mesh analog of the parity
   twin: guarantee closed 2-manifold output (wall + base + weld relief; Manifold
   Dual Contouring for true 3D) and ship a validator that reports hole count,
   non-manifold edges, bounding size, tri count, est. volume (Blender's "3D Print
   Toolbox" is the model). "Will this print?" before export.
   - **S9.1 validator LANDED (PR #420).** `Engine/Export/MeshValidator` (positions +
     index triples → `MeshReport`: watertight / edge-manifold / oriented verdict +
     boundary/non-manifold/flipped edge counts + bounds + area + signed volume +
     `.Summary()`; welds the triangle-SOUP the exporters emit before measuring edge
     incidence) + `StlMeshReader` (validate an exported file). `MeshValidatorTests`
     (cube = full contract, welded soup, open sheet, three-sheet fin, flipped face,
     degenerate, real relief export); suite 1619/1619. **Finding:** the shipped
     `HeightfieldMeshExporter` output was watertight + edge-manifold with real volume
     but its skirt-wall seams wound against the surfaces they meet (~147 flipped
     edges).
   - **S9.1a wall-winding FIXED (PR #421, Closes #419).** Wind each skirt wall so
     its top edge opposes the surface's traversal + take the wall normal from the
     actual winding (STL face normal + OBJ vn now agree, outward). Triangle count
     unchanged. Relief export now passes the FULL contract — `IsClosedManifold` with
     positive signed volume (outward); the real-export test is tightened to that as
     the regression guard.
   - **Export-time "will this print?" check LANDED (PR #442).** The exporters now run
     `MeshValidator` on the written solid via an optional `onReport` callback and the
     shell shows `MeshReport.PrintReadiness()` — a plain-language verdict (PRINT-READY,
     or NOT print-ready naming the holes / non-manifold / flipped issues + size /
     volume / triangle count) after every mesh export. Text-only (no colour-as-signal)
     for accessibility; zero cost + byte-identical output when the callback is unset.
     Wired into all three export sites (generic 3D, UserBulb, relief). The Relief 3D
     dialog's Mesh export expander also shows the verdict LIVE after each export
     (`ReliefMeshPrintStatus`, PR #444) — the print-path analog of the MC cap toggle,
     since the relief mesh is watertight by construction and has nothing to cap.
   - **Manifold auto-repair LANDED (PR #449).** `MeshRepair` (pure, appearance-
     preserving): welds for topology (keeps original indices → colour/normals
     untouched), drops degenerate + duplicate faces, flood-fills a consistent winding
     and flips globally to outward via signed volume. No-op + idempotent on clean
     output; does not fill holes / cut non-manifold edges (that's the off-limits
     interactive workbench). Opt-in `repair` flag on both true-3D exporters + a
     "Repair" checkbox on the UserBulb panel (persisted). The in-lane
     "guarantee-manifold-on-export" from §4.
2. **Isosurface export for true 3D fractals** — marching cubes / dual contouring
   on the DE; the headline capability FF is uniquely positioned for.
   - **MC validated + oriented (PR #423).** `MeshValidator` extended to
     `UserBulbMeshExporter`'s Marching Cubes: when the surface is interior to the
     sample cube it is a closed, edge-manifold, consistently-wound solid (the Bourke
     tables + edge dedup are sound). Fixed an inside-out defect — the `TriTable`
     wound faces INWARD (negative signed volume) with the `DE < iso` inside bit, so
     STL/OBJ normals pointed into the solid; reversed the emitted winding to
     `(a,c,b)` (count unchanged). `McMeshValidationTests` locks interior =
     print-ready, `ProbeBoundingRange` auto-size = closed, undersized cube = open,
     crease path = still closed.
   - **Boundary cap LANDED (PR #425, Closes #422).** Where the solid crosses a
     sample-cube face MC left the cut open (a fractal exiting the cube exported as
     a shell with holes). Default-on cap marches one extra ring of cells against a
     virtual outside shell (`DE = +OUTSIDE`) so the crossing gets a triangle flush
     at the box face — sealed, watertight, outward-wound. Byte-identical no-op when
     the surface is interior (shell corners all outside), so the auto-sized path is
     unchanged; `capBoundary` flag (default true) on both `ExportMarchingCubes`
     overloads. Tests: undersized cube now closed+outward with cap, still open with
     cap off, cap tri-for-tri no-op when interior.
   - **Cap UI toggle LANDED (PR #443).** "Seal box faces" checkbox on the UserBulb
     mesh-export panel drives `capBoundary`; persisted per bulb
     (`UserBulbSnapshot.ExportCapBoundary`, nullable → older snapshots keep the
     default-on).
   - **Dual contouring LANDED (PR #446).** The sharp-feature mesher: `DualContourMesher`
     places one QEF-solved vertex per cell (regularised normal equations over the
     cell's Hermite data — edge crossings + DE gradient normals — biased to the mass
     point, clamped to the cell, no SVD), joining the four cells around each
     sign-changing edge into a quad, so Mandelbox facets / KIFS corners snap sharp
     instead of chamfering onto grid edges like MC. `UserBulbMeshExporter.ExportDualContouring`
     shares the MC path's format dispatch, colour and print-readiness. Interior mesher
     (closed when the shape is inside the cube). Tests: sphere → closed/outward/volume,
     L∞ box → DC vertex on the true 3D corner far tighter than MC.
   - **Mesher UI selector LANDED (PR #447).** "Mesher:" combo on the UserBulb export
     panel picks Marching Cubes / Dual contouring (`MeshingMode` enum, persisted per
     bulb via `UserBulbSnapshot.ExportMeshingMode`); the host switches
     `ExportMarchingCubes` / `ExportDualContouring` accordingly.
   - **DC boundary cap LANDED (PR #448).** DC gained the #422 seal: an extra ring of
     padding cells marched against a virtual outside shell caps box-face crossings
     into a watertight solid (byte-identical no-op when interior). The "Seal box
     faces" toggle now governs both meshers. Both MC and DC are print-ready on all
     paths.
3. **Vertex-color export** — bake the theme into per-vertex color (PLY / 3MF
   color / glTF) so a color print or web drop-in carries the fractal's *palette*.
   The palette idiom crossing into mesh — the biggest differentiator.
   - **Relief PLY LANDED (PR #424).** Binary little-endian PLY writer
     (`HeightfieldMeshExporter.WritePly`) carrying position + smooth normal +
     per-vertex RGB — the format built for vertex colour (STL can't; OBJ vertex
     colour is non-standard). `.ply` dispatch + Export Relief Mesh dialog option +
     `PlyMeshReader` for validation. `PlyVertexColorExportTests`: header advertises
     the colour props, the `.ply` round-trips to a closed 2-manifold outward solid
     (through `MeshValidator`), and the baked colours vary (theme carried). The
     relief exporter already computed the colour; this stops discarding it.
   - **MC vertex colour LANDED (PR #427).** The Marching-Cubes isosurface now bakes
     a per-vertex albedo too. The screen colour driver is view-dependent (raymarch
     step count + view depth) so it can't be replayed at a bare surface point;
     instead an optional `SampleSurfaceColor` delegate drives the SAME active palette
     with a view-independent scalar (radial distance from the object centre) + the
     vertex normal, wired from the render host's `IColorMap`
     (`MakeMeshColorSource`). Lands in glTF COLOR_0 + a new MC binary-PLY writer
     (`.ply` dispatch added); OBJ stays colourless (byte-compat), STL can't hold it.
     `McVertexColorTests` lock varying colour in PLY + GLB COLOR_0 on a closed,
     outward solid.
   - **Orbit-trap driver LANDED (PR #450).** A fractal-MEANINGFUL alternative to the
     radial fallback: the optional `IOrbitTrapEstimator` interface (companion to
     `IDistanceEstimator`) reports a view-independent normalized orbit trap at a
     point; `MandelbulbDe` implements it (closest the orbit passes to the origin),
     and `MakeMeshColorSource` drives the palette with the trap when the DE supports
     it, radial otherwise. So a Mandelbulb mesh carries fractal structure in its
     colour.
   - **Orbit trap across all struct families LANDED (PR #451).** `IOrbitTrapEstimator`
     now on Mandelbox / QuaternionJulia / QuaternionMandelbrot / BicomplexMandelbrot
     (origin trap) + Kleinian (nearest sphere-boundary trap). Every struct-based 3D
     family exports with fractal-structured colour; only KIFS (a delegate-adapter DE)
     keeps the radial fallback. S9 vertex-colour is complete.
   - **3MF LANDED (PR #441).** Self-contained 3MF writer (`ThreeMfMeshWriter`) — the
     OPC ZIP ([Content_Types].xml + _rels/.rels + 3D/3dmodel.model) the colour
     slicers (PrusaSlicer/Bambu/Cura/3D Builder) prefer over STL. Carries a
     millimetre PRINT UNIT + per-vertex colour via an `<m:colorgroup>` (distinct
     colours; each triangle references a colour index per corner). Wired into BOTH
     exporters (`.3mf` dispatch) — relief bakes `Vert.C`, MC bakes the
     `SampleSurfaceColor` albedo. `ThreeMfMeshReader` validates end-to-end
     (unzip → parse → MeshValidator, closed + outward + mm unit). Save dialogs offer
     `.3mf`. Completes the colour-carry matrix: STL (geometry) / PLY (colour) /
     glTF-GLB (colour + PBR) / 3MF (colour + units).
4. **Carry the material** — export **glTF / GLB** with the PBR material so the
   mesh lands in Blender / web dressed, not grey clay. Format discipline: STL
   (dumb slicers), PLY (vertex color), glTF/GLB (PBR), 3MF (color + material +
   units). Pick formats that carry what FF uniquely has.
   - **glTF/GLB LANDED (PR #426).** Self-contained glTF 2.0 writer
     (`GltfMeshWriter`, no dependency): `.glb` single-file binary container
     (header + JSON chunk + BIN chunk) or `.gltf` with the buffer inlined as a
     base64 data URI. One mesh / one primitive; a 4-aligned buffer of POSITION +
     NORMAL + optional COLOR_0 (normalized ubyte4) + UINT indices; POSITION accessor
     min/max; `doubleSided` pbrMetallicRoughness material (matte: metallic 0,
     roughness 0.8). Base colour stays white when vertex colour is present so
     COLOR_0 drives the albedo. Wired into BOTH exporters: relief carries the theme
     as COLOR_0; MC dresses in flat matte grey unless a colour source is supplied
     (now wired — PR #427). `GltfMeshReader` validates end-to-end (GLB/gltf →
     MeshValidator, closed + outward). Save dialogs offer `.glb` / `.gltf`.
     **Remaining:** optional texture/emissive extensions. (3MF colour + units landed
     separately — see the vertex-colour item above.)
5. **Adaptive resolution** — octree-refine the DE only near the surface, seeded by
   the empty-space-skip mip FF already builds.
6. **Print-ready units / orientation / base** — mm, centered, flat base, Z-up.
7. **Analytic hollow + drain holes** as export *options* (DE-threshold shell).
8. **Decimation as a knob** (quadric, to a triangle budget) — an export option,
   NOT an interactive tool.
- **Reuse:** DE field, analytic normals, color map, PBR material, empty-space-skip
  mip — all already computed.
- **Twin:** meshing is deterministic geometry → the correctness contract is
  *watertightness/manifold validation* (assert in tests), the mesh analog of the
  render parity twin. Vertex color/material export is data plumbing (byte-stable
  test, no render twin).
- **Boundary:** auto-repair *to guarantee manifold on export* is in-lane; a mesh
  repair/sculpt *workbench* is not (see §4).

### S10 — PaletteBuilder as a perceptual, colorblind-first color assistant ☐ DEFERRED (#392)
**Deferred** — parked until the S1–S9 render/export axes mature; independent
art-idiom axis, picked up later. The home of FF's **art idiom**. Making FF *great* —
not just deep-zooming — means
making PaletteBuilder a genuinely great color assistant: perceptual, **colorblind-
first**, fractal-aware, advisory. Full design in
[PaletteBuilder-Design.md](PaletteBuilder-Design.md).

Unifying insight: **luminance is load-bearing twice** — in 3D, form reads from
shading (luminance *is* apparent relief); in colorblind vision, luminance is the
channel that survives when hue collapses. One discipline (perceptually-uniform,
luminance-structured ramps) serves 3D form-reading **and** CVD accessibility. Almost
no creative color tool is colorblind-first — an unclaimed gap, and one that matters
to this project.
- **Reuse:** OkLab extraction lib, the render engine (live fractal preview), the
  iteration histogram (`HistogramEqualizer`, #145), the ColorGen cosine idiom, the
  `#FFCC00`-not-red UI precedent — all already present.
- **Twin:** CVD simulation + ΔE + luminance-monotonicity are deterministic → assert
  in tests (the color analog of the render parity twin); perceptual round-trips get
  epsilon-stable tests.
- **3D reach:** preview the palette's *shaded gamut* under 3D lighting (couples to
  **S2** linear/tonemap), preview it as fog/god-rays (the #185 palette map), warn
  when a ramp flattens relief, and author "looks" (palette + material preset + sky
  tint) — without becoming a material editor (see §4).
- **Boundary:** a color/palette assistant, not an image editor / DAM / material node
  graph.

### S11 — Relief height from an orbit-trap distance field ☐ (#592)
Relief 3D extrudes a 2D fractal into a heightfield and raymarches it. Today the
height source is **only** the smooth iteration count (`IHeightFieldSource.
SmoothBuffer`). But an **orbit-trap min-distance** is already a per-pixel scalar
field (`OrbitAccumulator.TrapMin`) — the same shape of data — so relief could
raymarch *it* instead of / blended with smooth: "Orbit Trap - Ring" → concentric
ridges, Hexagon → a hex ridge lattice, Grid → an embossed lattice. Literal 3D
orbit-trap topography. Pure S1-thesis: **reuse a field FF already computes as a
height AOV**, no new geometry machinery.
- **Reuse:** the orbit-aware sampling path (already runs for trap themes), the
  relief raymarch, the hi-res relief field (#143 `Relief2DHiResField`).
- **Sketch:** persist a per-pixel trap-min buffer when an orbit-aware theme runs
  (today the trap value only reaches colour, never stored as a field); extend
  `IHeightFieldSource` with a trap field (or a height-source selector); relief
  selects trap / smooth / blend. Hi-res relief needs a hi-res trap recompute too.
- **Twin:** the trap field is a deterministic scalar → twinnable like `SmoothBuffer`;
  the relief raymarch parity discipline is unchanged.
- **Scope:** Mandelbrot path first (relief is Mandelbrot today); the DSL/Julia
  relief gap is separate. Height-source only — colour still comes from the theme.
- **Fit:** an AOV→height instance of S1; low risk, high novelty payoff.

## 4. Explicit non-goals (the Blender trap)

Holding this boundary is as important as shipping the slices. FF exports **mesh
for 3D print** — that is the correct handoff line: *model / sculpt / simulate* is
Blender's job; *render the distance field cinematically* is FF's.

| Tempting DCC feature | Verdict | Why |
|---|---|---|
| Mesh modeling / sculpt / UV / retopo | **Skip** | No mesh scene; off-primitive (S9 *exports* mesh; it never *edits* it) |
| Mesh repair / sculpt **workbench** | **Careful** | Auto-repair to guarantee manifold *on export* (S9) is in-lane; an interactive repair tool is not |
| Rigging / particles / physics sim | **Skip** | Off-primitive, huge, duplicates Blender |
| Full shader **node editor** | **Careful** | Grow the ColorGen DSL instead of cloning nodes |
| Asset library / geometry nodes / heavy instancing | **Skip** | Not FF's game (exception: sphere-imposters, already used for Apollonian) |
| Cryptomatte / per-object IDs | **Defer** | Low value until multi-fractal scenes exist |

## 5. Sequencing

1. **S7 (EXR)** first — it unblocks S1/S2/S6 at full fidelity and is low-risk
   (format work, no render math).
2. **S2 (linear + view transform)** — pipeline discipline; every later slice
   renders into a correct color space.
3. **S1 (AOVs / passes)** — exposes the data FF already computes.
4. **S3 (camera)** — the visible-payoff win, riding S2.
5. **S4 (denoise)** — rides S1's guide buffers.
6. **S5 (refraction)**, **S8 (lights)**, **S6 (froxel)** — independent, schedule
   by appetite; #388 is a good warm-up on the volumetric axis.
7. **S9 (mesh export)** — independent of the render pipeline; its own axis. The
   watertight-contract + validator sub-item is the low-risk starting point (hardens
   the relief mesh FF already ships); isosurface export for true 3D fractals is the
   larger, higher-payoff follow-on.
8. **S10 (PaletteBuilder)** — **deferred** (parked until S1–S9 mature). Independent
   art-idiom axis; couples to **S2** only for
   the shaded-gamut preview. Perceptual core → CVD-first suite (the differentiator)
   → fractal-aware preview → advisor + 3D items. See
   [PaletteBuilder-Design.md](PaletteBuilder-Design.md).

## 6. Strategy in one line

**Stop discarding the passes you already compute (S1), render them in linear and
tonemap (S2), and point a real camera at them (S3)** — that trio alone lifts FF's
3D a full tier, reuses what exists, keeps CPU/GPU parity, and needs zero mesh
machinery.

---

## References

- [Volumetric Lighting Guide](../User/Volumetric-Lighting-Guide.md) — §6 CPU-vs-GPU parity.
- [Relief 3D Cookbook](../User/Relief3D-Cookbook.md)
- [Volumetric Color Plan](Volumetric-Color-Plan.md) — slice A–E + #185 relief slice D.
- [SceneEngine Architecture](SceneEngine-Architecture.md) — keyframes/time for S3/S6.
- [Architecture Overview](Architecture-Overview.md)
- Related issue: #388 (multi-light relief in-scatter).
