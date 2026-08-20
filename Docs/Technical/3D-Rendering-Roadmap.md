# 3D Rendering Roadmap

Tracking document for Fracturing Fog's deliberate expansion into 3D rendering —
what to build, what to borrow from mature 3D / DCC software (Blender, Houdini,
Arnold, the Frostbite/Hillaire volumetrics line), and — just as important — what
**not** to build so FF stays FF and does not drift into being a worse Blender.

Status legend: ☐ not started · ◐ in progress · ☑ shipped

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
- **Remaining:** GUI "Export AOV EXR" action, float lighting-component AOVs (diffuse/
  specular/AO/shadow — needs a `Shade` out-param overload), wire float capture into
  the `--aov-exr` orchestrator (replace the 8-bit normal/depth), light compositor,
  motion-vector AOV.

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
- **Remaining:** video/slideshow frame path (raw-frame stage has no post-buffer
  hook yet), the *core* true-linear/float intermediate (couples S7), default-look
  validation, SIMD.

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
- **Remaining:** click-to-focus, GPU relief DOF (+ twin/gate), DOF on the 3D-fractal
  cameras, in-camera exposure control, **motion blur** (own slice over Scene time).

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
- **Remaining (deep, still open on #402):** GPU AOV emit so denoise doesn't force
  CPU; adaptive-supersample coupling (fewer samples when denoise on); full SVGF
  variance/temporal weighting; parallelize/SIMD.

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
- **Remaining:** full internal glass march (refract-and-continue through the solid,
  CPU → twin → GPU), rough refraction (couple S4), AbsorptionColor picker + batch
  flags, GPU-kernel refraction.

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
- **Remaining:** wire the **live camera frustum** populate + per-pixel depth into
  the relief / 3D render (replacing the background `VolumetricInScatterSegment`
  march), multi-light in-scatter (reuse #388), **temporal reprojection** (Scene
  Engine history, additive gated), GPU froxel compute pass.

### S7 — Float / multi-layer EXR export ◐ (#394)
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
  round-trip/byte-stability tests. **Remaining:** feed real float AOV layers (needs
  S1), optional ZIP compression, GUI/batch format surface, Blender smoke.

### S8 — Richer light types: point / spot / area ◐ (#404)
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
- **Remaining:** UI (LightingFx dialog: 3 lights × type/pos/range/cone) + batch
  flags, **positional lights in the volumetric march** (fog site stays directional),
  force-CPU on the 8 GPU 3D-fractal calculators (relief path gated; those still
  render directional-only on GPU), area lights (couple S4).

### S9 — Mesh export maturation ◐ (#391)
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
     the regression guard. **Remaining:** weld-based manifold repair on true-3D
     output, validate `UserBulbMeshExporter`, export-time warn UI.
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
     cap off, cap tri-for-tri no-op when interior. **Remaining:** a UI toggle for
     the cap; dual contouring for sharp Mandelbox/KIFS edges.
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
     outward solid. **Remaining:** a fractal-meaningful driver (orbit trap / escape)
     if derivable view-independently; 3MF colour.
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
     **Remaining:** 3MF (colour + units); optional texture/emissive extensions.
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

### S10 — PaletteBuilder as a perceptual, colorblind-first color assistant ☐ (#392)
The home of FF's **art idiom**. Making FF *great* — not just deep-zooming — means
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
8. **S10 (PaletteBuilder)** — independent art-idiom axis; couples to **S2** only for
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
