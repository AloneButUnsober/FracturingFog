# Lighting / FX Roadmap

Companion to `Fractal-Expansion-Roadmap.md`. Tracks the volumetric-lighting +
post-FX rendering pipeline (`Engine/Rendering/Lighting/`,
`Abstractions/Rendering/Lighting/LightingFxData.cs`,
`UI.Avalonia/ViewModels/FractalParamsViewModel.Lighting.cs`,
`UI.Avalonia/Views/FractalParamsView.axaml` lighting expanders).

Phases 1–24 + the `b` follow-up bindings landed across two batched commits:

- Phase 1–24 + first follow-up wave: pre-summary commits (see `git log
  --grep="Lighting"`).
- Deferred-wave bundle: **`5b5a741`** (Phases 6b/8b/11b/15b/21c/22b/23b/24b +
  animation-tick fix + Phase 1c legacy-light removal in 8 calculators).

Status as of this doc write: working tree clean on
`feature/cross-platform-full`.

---

## Deferred items (off-roadmap, large refactors)

These were explicitly left out of the deferred-wave bundle because each is a
multi-day refactor that touches the GPU dispatcher, the raymarcher hot loop,
or both. They're listed in roughly the order they were originally suggested
to be tackled.

### 12b — GPU port: tonemap + bloom + volumetric kernels

**Current state.** All three passes run on CPU inside
`Engine/Rendering/Lighting/ScreenSpacePost.cs`
(`ApplyToneMapBloom`) and `ShadingPipeline.cs`
(volumetric in-scatter loops + `FbmCloud3D`). They sit after the
raymarcher's iteration buffer is filled and before the framebuffer
upload.

**Why deferred.** Bloom in particular is a 2-pass separable Gaussian over
the full HDR buffer. At 1920×1080 + 6 mip downsamples this is the single
biggest CPU cost in the post-pass chain. Tonemap is per-pixel but cheap;
it's bundled here for kernel-locality reasons.

**Scope.**
1. Add a `GpuPostKernels` partial for `Bloom*`, `Tonemap*`, and
   `VolumetricInScatter*` mirroring the CPU implementations.
2. Plumb through `FracturingFog.Rendering.Lighting.GpuPostKernels.cs`
   (already scaffolded as a stub in commit `5b5a741`).
3. Honour the existing `UseGpuPost` flag — when false, fall back to CPU
   so the renderer remains usable without an ILGPU device.
4. Bit-identity gate: when `UseGpuPost` is false **or** bloom/volumetric
   parameters are at their default-zero values, the GPU path must not
   even dispatch (avoids surprising the user with a device init when
   none of the FX are in use).

**Touch points.** `Engine/Rendering/Lighting/ScreenSpacePost.cs`,
`Engine/Rendering/Lighting/ShadingPipeline.cs`,
`Engine/Rendering/Lighting/GpuPostKernels.cs`,
`Engine/Rendering/FractalRenderHost.cs` (dispatch routing).

**Risk.** Mixed-precision: CPU path uses `double` for accumulators;
ILGPU kernels will run `float`. Document the expected magnitude of
divergence and add a tolerance band to any visual-regression tests
that compare CPU vs GPU output.

---

### 12c — GPU port: edge-ink (Sobel + Frei-Chen)

**Current state.** `ScreenSpacePost.ApplyEdgeInk` runs both the Sobel
3×3 convolution and the Frei-Chen 4-subspace projection on CPU. Same
hot path as 12b but a separate kernel because it operates on the
normal buffer rather than the colour buffer.

**Why deferred.** Edge detection is a single-pass stencil — perfect
GPU candidate but the kernel is small enough that the CPU version is
not the bottleneck. Worth doing **after** 12b since the colour-buffer
will already be GPU-resident.

**Scope.**
1. Sobel and Frei-Chen kernels in `GpuPostKernels` (one each, since
   the basis projections differ).
2. Switch on `EdgeKernel` field already wired in 23b.
3. Reuse the GPU normal buffer written by 12b's volumetric pass to
   avoid an extra upload.

**Touch points.** `Engine/Rendering/Lighting/ScreenSpacePost.cs`,
`Engine/Rendering/Lighting/GpuPostKernels.cs`.

**Risk.** Frei-Chen's 4 basis projections per channel need
non-trivial register pressure — verify on a low-end GPU before
shipping default-on.

---

### 16b — Recursive reflection bounces + roughness-convolved IBL

**Current state.** `ShadingPipeline.Shade` does one reflection bounce
(`ReflectionStrength` knob). HDRI sampling on miss is point-sampled,
not roughness-convolved. The reflection vector is mirror-only — no
GGX importance sampling, no per-roughness lobe.

**Why deferred.** Recursive bounces require the raymarcher's primary-
ray code to be reentrant for secondary rays. Current code keeps state
in locals; making it reentrant means lifting the march state into a
struct that can be invoked twice (mirror bounce 1 → bounce 2 → IBL
fallback).

**Scope.**
1. Extract the sphere-trace loop in each calculator into a static
   helper that takes ray origin + direction and returns hit/normal/
   roughness.
2. In `Shade`, after the primary hit, call the helper N times (N =
   `MaxBounces` knob, default 1 = current behaviour).
3. For each bounce, importance-sample a GGX lobe rather than a
   pure mirror. Reuse the existing `Roughness` knob.
4. Roughness-convolved IBL: prefilter the HDRI into a mip chain at
   load time (`HdriRegistry.cs`), sample the mip that matches the
   surface roughness on miss.

**Touch points.** All 7 raymarcher `Calculate` methods,
`Engine/Rendering/Lighting/ShadingPipeline.cs`,
`Engine/Rendering/Lighting/HdriRegistry.cs`.

**Risk.** Cost scales linearly with `MaxBounces`. Default must stay
at 1. Document in UI tooltip that `MaxBounces > 2` is interactive-
preview-only.

---

### 20b — True per-eye stereo (camera offset plumbed into raymarchers)

**Current state.** Phase 20 / 21c ships a side-by-side stereo
post-pass (`ScreenSpacePost.cs`, `StereoRender.cs`) that takes the
mono-rendered framebuffer and warps it to look like a stereo pair.
Cheap but **not** true stereo — there's no parallax on close
objects because both eyes see the same ray hits.

**Why deferred.** True stereo means rendering twice with the camera
shifted left/right by half the IPD (interpupillary distance). At
1920×1080 that's a flat 2× cost. The raymarcher needs a parameter
for "eye offset along right-vector" — currently hard-coded to 0.

**Scope.**
1. Add `StereoEyeOffset` (or pass-through `eyeOffset` parameter) to
   each calculator's primary ray construction.
2. Render the left eye into the left half of the buffer, the right
   eye into the right. Reuse the existing side-by-side framebuffer
   layout from Phase 20.
3. Wire `StereoFovDegrees` (already shipped in 21c) to control the
   off-axis frustum so the two eyes converge on a focal plane at the
   user-set distance.
4. Defer barrel-distortion correction for HMD output — Phase 20's
   `StereoRender.cs` already has a pre-warp pass that can be reused.

**Touch points.** All 7 raymarcher `Calculate` methods (primary
ray construction), `Engine/Rendering/Lighting/StereoRender.cs`,
`Engine/Rendering/FractalRenderHost.cs` (stereo-enabled dispatch).

**Risk.** Doubles render time when stereo is on. Need a
`StereoMode = Off | Fake | True` enum on `LightingFxData` and a UI
toggle that defaults to `Fake` (= current behaviour) for backward
compatibility.

---

### 21b — HDR depth-of-field + 3-pass hex blur (McIntosh 2012)

**Current state.** No DoF pass. The `LensFx` block has vignette,
chromatic aberration, Brown decentring, and anamorphic squeeze, but
no depth-driven blur. Depth is available — the raymarcher writes it
into `SmoothBuffer` (per-pixel `t` along the primary ray).

**Why deferred.** Hexagonal bokeh via McIntosh's 3-pass approach
(McIntosh, Riecke, DiPaola, "Efficiently Simulating the Bokeh of
Polygonal Apertures in a Post-Process Depth of Field Shader", 2012)
is the highest-quality CPU-affordable bokeh, but it needs three
separable directional blurs followed by a min-blend, and the input
must be HDR (linear-light) for the bokeh highlights to bloom right.

**Scope.**
1. New `HdrDofPass` in `ScreenSpacePost.cs`.
2. Inputs: HDR colour buffer (from 12b output, or current CPU HDR
   buffer if 12b not yet shipped), depth buffer (from `SmoothBuffer`),
   focal distance + aperture + bokeh-shape (hex / circle / iris).
3. Three skewed-box blurs at +0°, +120°, +240° per the McIntosh
   paper. Min-blend the three intermediate buffers to form the hex.
4. UI: focal-distance knob (already half-shipped under
   `LensFocalDepth`?), aperture (f-stop), bokeh-shape combo.
5. Honour the existing `FocalDistance`/`Aperture` fields if they
   exist; else add them to `LightingFxData`.

**Touch points.** `Abstractions/Rendering/Lighting/LightingFxData.cs`,
`Engine/Models/LightingFxPresetData.cs`,
`UI.Avalonia/ViewModels/FractalParamsViewModel.Lighting.cs`,
`UI.Avalonia/Views/FractalParamsView.axaml`,
`Engine/Rendering/Lighting/ScreenSpacePost.cs`.

**Risk.** Three full-resolution skewed blurs are expensive on CPU.
Pair this with 12b — the GPU path can do all three in parallel and
makes the hex blur viable at interactive framerates.

---

## Recommended order

1. **12b first** (biggest perf win; unblocks 12c and 21b).
2. **12c** (mechanical follow-up to 12b; small kernels).
3. **21b** (uses the GPU HDR buffer from 12b).
4. **16b** (largest architectural lift; reentrant raymarcher).
5. **20b** (depends on nothing else; can be done in parallel with
   16b if a second contributor picks it up).

---

## Cross-cutting work for the lighting pipeline

- **Bit-identity smoke test.** Every phase committed so far preserves
  output at default knob values. A scripted comparison
  (`--batch --headless` render → SHA256) on each of the 7 raymarchers
  would catch regressions caused by future GPU ports.
- **Default-zero gating.** All FX knobs default to a zero value that
  means "pass through". The GPU dispatcher should skip kernel
  dispatch for any pass whose knob is at default, not just early-out
  inside the kernel — saves the upload round-trip.
- **`LightingFxData` is a value type.** Any new GPU path must copy-
  mutate-write-back through the auto-property; see the CS8156 error
  fix in commit `5b5a741` for the pattern.
- **HDRI auto-load.** `HdriRegistry.TryLoadFromFile` parses Radiance
  `.hdr`/`.pic`. Currently called lazily from `ShadingPipeline.SampleEnvAmbientHdri`.
  Consider preloading the active environment on parameter change so the
  first render doesn't stall on file IO.
