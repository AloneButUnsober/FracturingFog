# Lighting & FX — 3D raymarch vs Relief 3D parity

Reference for the "why does effect X work on a 3D fractal but not on Relief 3D?"
question. Written after users repeatedly hit the gap (most recently the Tone Map
surprise: the *Tone Map* dropdown in the **Volumetric Lighting & FX** dialog is a
no-op on Relief 3D).

Tracking issue: [#652 — Relief 3D lighting/FX parity](https://github.com/AloneButUnsober/FracturingFog/issues/652).
Keep this doc ↔ issue linked both ways.

Related code:
- `Abstractions/Rendering/Lighting/LightingFxData.cs` — the shared FX param block.
- `Engine/Rendering/Lighting/ShadingPipeline.cs` — **stage 1**, per-hit shading.
- `Engine/Rendering/Lighting/ScreenSpacePost.cs` — **stage 2**, whole-buffer post.
- `Engine/Calculators/MandelbulbCalculator.cs` (~`Calculate` tail) — the 3D calc
  post chain.
- `Engine/Rendering/Lighting/HeightfieldRaymarch2D.cs` — the Relief raymarcher.
- `Engine/Rendering/FractalRenderHost.cs` (~`ApplyCachedRelief` / the relief
  compose tail) — what Relief actually runs.
- `Abstractions/Imaging/ViewTransform.cs` — the *other* tonemap (Relief's).
- `UI.Avalonia/Views/LightingFxDialog.axaml` + `ViewModels/FractalParamsViewModel*` —
  the dialog + gating.

---

## The two-stage model

FF's lighting/FX runs in two stages. The 3D-vs-Relief gap lives **entirely in
stage 2.**

### Stage 1 — per-hit surface + volume shading (`ShadingPipeline.Shade<TDe>`)

One shared function. Every 3D raymarcher **and** the Relief raymarch route each
ray hit through it, so **everything here reaches Relief 3D for free:**

- Lights 1–3: directional / point / spot / area (position, range, cone, soft
  penumbra).
- Ambient + DE-cone AO (`AoSamples` / `AoStrength`).
- Soft shadows (IQ soft-shadow, `ShadowSteps` / `ShadowSoftK` / `ShadowLightMask`).
- Fog + volumetric in-scatter (god-rays), cloud noise / self-shadow, Henyey-
  Greenstein phase, fog color, palette-mapped volumetrics, per-light fog mask.
- PBR-lite: roughness / metallic / specular / sub-surface.
- Glass: transmission / IOR / Beer-Lambert absorption / internal march.
- Reflections: N-bounce, Fresnel, optional GGX sampling.
- Triplanar procedural texture.
- Sky / IBL / HDRI, `ShowSkyBackdrop`.
- Caustics.

Relief also gets the debug HUD + AOV view modes (re-added by the host, below).

### Stage 2 — whole-buffer post passes (`ScreenSpacePost.*`)

The **3D calculators** run a fixed post chain after shading (see
`MandelbulbCalculator`):

```
ApplySsao → ApplyHdrDof → ApplyToneMapBloom → ApplyEdgeInk → ApplyDebugHud
```

Relief renders through `HeightfieldRaymarch2D` — **not a calculator** — so it
never runs that chain. `FractalRenderHost` re-adds only **two** of these for
Relief: the `ViewTransform` tonemap and `ApplyDebugHud`. Every other stage-2 pass
is absent on Relief.

---

## Gap table (`LightingFxData` knobs)

| Knob | 3D raymarch | Relief 3D | Stage | Reason |
|---|:---:|:---:|---|---|
| Lights (dir/point/spot/area) | ✅ | ✅ | 1 | shared `Shade` |
| Ambient, DE-cone AO | ✅ | ✅ | 1 | shared |
| Soft shadows | ✅ | ✅ | 1 | shared |
| Fog + volumetric in-scatter, god-rays, cloud noise, HG, fog color, vol-palette | ✅ | ✅ | 1 | shared (+ Relief froxel twin) |
| PBR (rough/metal/spec/subsurface) | ✅ | ✅ | 1 | shared |
| Glass (transmission/IOR/absorption/internal march) | ✅ | ✅ | 1 | shared |
| Reflections (N-bounce, GGX) | ✅ | ✅ | 1 | shared |
| Triplanar texture | ✅ | ✅ | 1 | shared |
| Sky / IBL / HDRI, ShowSkyBackdrop | ✅ | ✅ | 1 | shared |
| Caustics | ✅ | ✅ | 1 | shared |
| Debug HUD / AOV views | ✅ | ✅ | 2 | Relief re-added by host |
| **Tone Map operator** (`ToneMap`) | ✅ | ❌ | 2 | `ApplyToneMapBloom`, calc-only. Relief tonemaps via `ViewTransform` — see below |
| **Exposure** (`Exposure`) | ✅ | ❌ | 2 | same pass. Relief has separate `ViewExposureEv` |
| **Bloom** (threshold / strength) | ✅ | ❌ | 2 | `ApplyToneMapBloom`, not run |
| **Chromatic aberration** | ✅ | ❌ | 2 | `ApplyLensPost`, not run |
| **Lens distortion / vignette / tangential / anamorphic** | ✅ | ❌ | 2 | `ApplyLensPost`, not run |
| **Edge ink** (Sobel / Frei-Chen) | ✅ | ❌ | 2 | `ApplyEdgeInk`, not run |
| **Screen-space SSAO** (`SsaoSamples`) | ✅ | ❌ | 2 | `ApplySsao` needs the G-buffer; Relief only gets DE-cone AO |
| **DoF** (`DofAperture/Focus/Samples/ThinLens`) | ✅ | ⚠️ | 2 | Relief has its **own** knobs — `Relief2DDofApertureRadius` / `Relief2DDofFocusDistance` — on the Relief 3D dialog, a separate implementation. The FX-dialog DoF knobs are 3D-only. |
| **Stereo** (mode / IPD / convergence / SBS) | ✅ | ❌ | 2 | host stereo orchestration wired only for the 3D calculator cameras |

### Reason buckets

- **Not-implemented, no technical blocker** — Tone Map operator, Exposure, Bloom,
  lens/chroma/vignette, edge ink, SSAO. All are pure post-passes. Relief already
  captures a **pre-clamp HDR beauty buffer** (`ReliefAovBuffers.HdrBeauty`, landed
  in the S2 wave, #396) — the exact input `ApplyToneMapBloom` wants — so wiring is
  plumbing, not physics. This is the closeable work.
- **Deliberately parallel** — DoF. Relief's oblique camera differs from the 3D
  perspective cameras, so it got its own aperture/focus model + focus-pick
  (`TryPickReliefDofFocus`). Same feature, different knobs.
- **Genuinely not done** — Stereo for Relief (per-eye host orchestration is
  3D-calculator-only).

---

## Two "synonymous but different" traps

### 1. "Tone Map" is two independent controls

- **3D:** the *Tone Map* dropdown in the FX dialog = `LightingFxData.ToneMap`
  (None / Reinhard / ReinhardExtended / **ACES-Hill**), applied inside
  `ApplyToneMapBloom` → **3D calculators only.**
- **Relief:** tonemaps via the separate **`ViewTransform`** view/output control
  (None / Reinhard / **ACES-Narkowicz** / AgX / Filmic), applied globally in
  `FractalRenderHost`. Exposure for Relief is `ViewExposureEv`, not `Exposure`.

Same word "ACES", *different fit*, *different dialog*. Setting the FX-dialog Tone
Map on a Relief scene does nothing; use the View Transform control. Note the
`ViewTransform` is global, so it also stacks on top of the 3D calculators' own
tonemap — a 3D scene can be double-tonemapped if both are set.

### 2. "Volumetric lighting affects the object (3D) vs the whole render (Relief)"

Not a code difference — it is the **same** `VolumetricInScatter` march driven by
the same knobs. The perception differs because the scene composition differs:

- **3D:** one fractal object fills the frame with sky behind. The in-scatter march
  along the primary ray reads as haze/god-rays clinging to and lighting **the
  object.**
- **Relief:** a terrain heightfield on a ground plane under a sky dome. The same
  march fills the **whole landscape + atmosphere**, so it reads as an overall wash
  rather than object-attached lighting.

Identical machinery, different subject geometry.

---

## Root cause & UI note

The FX dialog (`FractalParamsViewModel.Lighting.cs`) is a **single flat binding
set** on `_p.Lighting` with no per-fractal-type gating, so it shows every knob to
both 3D and Relief. Stage-2 knobs therefore *look* available on Relief but silently
no-op.

**Mitigation shipped alongside this doc:** the FX dialog now gates its stage-2-only
controls when the context is Relief (`FractalParamsViewModel.Stage2PostFxApplies` /
`IsReliefLightingContext`): the Tone Map / Bloom / Lens block and the SSAO / Edge /
Stereo / DoF controls are disabled, and a banner (colorblind-safe `#FFCC00`) points
the user at **View ▸ View Transform** for Relief tonemapping.

## To close the gap (future work)

Wire the stage-2 post passes onto the Relief output using the already-captured
`ReliefAovBuffers.HdrBeauty` buffer, in `FractalRenderHost`'s relief compose tail:

1. `ApplyToneMapBloom` (Tone Map operator + Exposure + Bloom) over the HDR beauty.
   Decide the interaction with `ViewTransform` (likely: FX Tone Map replaces the
   Relief ViewTransform when set, or they compose in a defined order).
2. `ApplyLensPost` (chroma / distortion / vignette / tangential / anamorphic) —
   byte-buffer pass, trivially portable.
3. `ApplyEdgeInk` — needs the Relief depth + normal G-buffer (already produced for
   denoise/AOV).
4. `ApplySsao` — same G-buffer.
5. Stereo — larger; needs per-eye Relief camera orchestration.

Each is independently shippable; do them as separate slices and flip the UI gate
per feature as it lands.
