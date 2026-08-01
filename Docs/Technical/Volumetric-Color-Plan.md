# Volumetric Color Plan — colored multi-light + medium color

Design + dev-tracking doc for the volumetric in-scatter color work.
Canonical task list lives in GitHub issues; this doc is the design backing.

- Tracking issue: **#176**
- Slices: **A #177**, **B #178**, **C #179**, **D #180 (future)**, **E #181 (GPU)**
- Branch: `feat/volumetric-light-color`
- Companion roadmap: `Docs/Lighting-FX-Roadmap.md`

---

## 1. Problem

FF already has volumetric in-scatter (single-scattering Beer–Lambert god-rays,
`VolumeSteps` / `FogDensity` / FBM cloud noise; #172). But the in-scatter loop
colors the fog from **`Light1.Color` only**:

- `Engine/Rendering/Lighting/ShadingPipeline.cs`
  - byte `Shade` block ~L225–L272 (`Lr/Lg/Lb = Light1.Color`)
  - generic `Shade<TDe>` block ~L685–L740 (same)

Surface shading (`AccumulateLight`) already sums Light1/2/3. The fog does not.
FF also has:

- **no phase function** — in-scatter is isotropic (no view/light angular term),
  so god-rays lack the forward-scatter "punch" real shafts have;
- **no medium color** — the fog cannot have its own tint independent of the
  lights.

## 2. How standard volumetric renderers color fog

Physically-based single scattering (Unreal, Unity HDRP, Frostbite froxel —
Hillaire, *Physically Based and Unified Volumetric Rendering*, 2015) colors
fog from **two independent inputs**, shaped by a phase function:

```
inScatter += transmittance * density * stepSize
           * scatteringAlbedo          // the medium's own color
           * Σ_lights ( lightColor_i * lightIntensity_i
                        * shadow_i
                        * phase(cosθ_i, g) )   // θ = angle(view, light)
```

- **light color** — every light tints the fog it lights (all lights, not one).
- **scattering albedo** — the medium's own color, separate from any light.
- **phase function** — Henyey-Greenstein `p(cosθ)=(1-g²)/(4π(1+g²-2g·cosθ)^1.5)`;
  `g>0` forward-scatters (bright toward the light), `g<0` back-scatters,
  `g=0` isotropic.

Where FF sits: current Light1-only isotropic in-scatter is *behind* this model.
Slices A–C bring FF **up to** the standard; slice D (Opt 3) is a deliberate
non-PBR art extension.

## 3. Slices

### A — Opt 1: multi-light colored in-scatter (#177)

Loop Light1/2/3 into the in-scatter accumulation. Per light, gated by
`Intensity > 0`, with per-step `SoftShadow` toward that light gated by the
matching `ShadowLightMask` bit (mirrors the surface-shadow convention).

- Shared per-step walk (position, base density, transmittance `T`) stays once.
- Only the scatter **color** term is per-light: `T * scatter * lightColor_i`.
- Factor the per-light body into a private helper to avoid triplicating.

**Bit-identity.** Light2/Light3 default `Intensity = 0` → skipped → identical
to today's single-light output.

### B — Henyey-Greenstein phase function (#178)

New `LightingFxData.VolumeAnisotropy` (double, `[-1, 1]`, **default 0**).
Per step per light, multiply scatter by the HG phase of
`cosθ = dot(viewDir, lightDir_i)` with `g = VolumeAnisotropy`, normalized so
`g = 0` reproduces the current constant term (× 1) → bit-identical.

UI: knob in the volumetric expander; tooltip — negative = back-scatter halo,
positive = forward god-rays.

### C — Opt 2: medium color / scattering albedo (#179)

New `LightingFxData.FogColor` (uint packed BGRA, **default 0xFFFFFFFF white**).
Multiply the accumulated in-scatter RGB by the normalized `FogColor` (composes
with per-light color from A and phase from B). White → ×1 → bit-identical.

UI: color picker in the volumetric expander; tooltip — tint of the fog medium
itself, independent of the lights.

### D — Opt 3: palette-mapped volumetric (#180) — DONE

Map the in-scatter through the active 3D color-theme gradient so fog picks up
the same palette as the fractal surface. Explicitly **not** PBR — a
stylized/NPR deviation consistent with FF's non-PBR surface color themes.
New knob `VolumePaletteStrength` `[0,1]`, default 0 = off.

Implementation (CPU):
- `LightingFxData.VolumePaletteStrength` (double, default 0) + runtime-only
  `VolumePalette` (uint[] LUT, default null — not serialized; a reference
  field on the value type).
- `VolumePaletteBaker.Bake(ref fx, colorMap)` (Engine, IColorMap-aware) bakes
  the theme's iteration sweep into a 256-entry ARGB LUT once per frame when
  strength > 0; all 8 CPU 3D calculators call it after taking their local `fx`.
  ShadingPipeline never sees `IColorMap` — only the baked `uint[]`, keeping the
  shading kernel decoupled from the theme machinery.
- `ShadingPipeline.VolumetricInScatter` samples the LUT by optical depth
  (`1 − T`) and does an **energy-preserving hue remap** of the accumulated
  in-scatter (redistribute its own brightness across the palette hue), then
  cross-fades by `VolumePaletteStrength`. Strength 0 or null LUT → unchanged
  (bit-identical with slice C).

GPU parity for D is **DONE** (follow-on to slice E). The baked theme LUT is
uploaded as a separate `ArrayView<uint>` kernel arg (it can't ride on the
blittable `GpuShadingParams`; a length-1 dummy `GpuKernelUtils.PaletteOff` keeps
the kernel arity fixed when the feature is off). New
`GpuShadingParams.VolumePaletteStrength` gates it; `GpuKernelUtils.SamplePalette`
+ `PaletteRemapInScatter` mirror the CPU energy-preserving remap. All 8
per-fractal kernels + Render signatures thread the palette; the 8 CPU
calculators pass `fx.VolumePalette`. Strength 0 / dummy LUT → bit-identical with
slice E. UserBulb's separate GPU path stays cheap-palette (no volumetric), same
scope boundary as slice E. On-device pixel parity = CLI probe + user smoke.

### E — GPU parity (#181) — follow-up

Mirror A/B/C into the 8 per-fractal GPU volumetric kernels + `GpuShadingParams`
/ `GpuKernelUtils`. CPU path (default, `UseGpuRender = false`) ships first.
UserBulb GPU stays on `UserBulbGpuCalculator` — confirm CPU fallback when the
new knobs are active.

## 4. Cross-cutting rules

- **Bit-identity gating.** Every new knob defaults to a pass-through value
  (L2/L3 intensity 0, `VolumeAnisotropy` 0, `FogColor` white). Default renders
  stay pixel-identical.
- **`LightingFxData` is a value type.** Copy-mutate-write-back through the
  auto-property (see the CS8156 note in `Docs/Lighting-FX-Roadmap.md`).
- **Two Shade paths.** Every math change lands in *both* the byte `Shade`
  block and the generic `Shade<TDe>` block in `ShadingPipeline.cs`.
- **Commit per slice.** A, then B, then C. Push + PR after C. D and E are
  separate future sessions.

## 5. Touch points

- `Abstractions/Rendering/Lighting/LightingFxData.cs` — new fields + defaults.
- `Engine/Rendering/Lighting/ShadingPipeline.cs` — both in-scatter blocks.
- `UI.Avalonia/ViewModels/FractalParamsViewModel.Lighting.cs` — bindings.
- `UI.Avalonia/Views/FractalParamsView.axaml` — volumetric expander controls.
- `Engine/Models/LightingFxPresetData.cs` — preset round-trip (if present).
- `Server.Tests/` — bit-identity + per-slice behavior tests.
