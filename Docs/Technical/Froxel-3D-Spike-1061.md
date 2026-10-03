# Froxel / environment volumetrics for 3D fractals — spike (#1061)

Issue: [#1061](https://github.com/AloneButUnsober/FracturingFog/issues/1061) (split from
#1050, epic #943). Status: **quick fix shipped; full froxel port planned as slices below.**

## The problem

On the 3D distance-estimation raymarchers, only rays that **hit** the fractal get fog or
volumetric in-scatter. A ray that **misses** — the space around the object, which is most of
the frame — returned a flat colour: `ColorMap.InSetColor` (usually black) or the sky gradient /
HDRI when *Show sky backdrop* is on. So a fogged scene showed a fogged object floating in a
black void, with no god-rays in the air around it.

## What shipped in #1061 (quick fix)

**Fog the background** (`LightingFxData.FogBackground` + `FogBackgroundDistance`, default off):

- **What it does:** a miss pixel is fogged as if its ray had travelled `FogBackgroundDistance`. The default is 12, the raymarchers' escape distance.
- **Same fog as the surface:** classic Beer–Lambert, `1 − exp(−distance × FogDensity)`, blended toward the sky gradient along the ray and tinted by `FogColor`. A hit at the escape distance and a miss therefore get the same colour, so the silhouette blends into the haze.
- **One CPU path:** `ShadingPipeline.MissColor` / `ApplyBackgroundFog`. All 18 CPU miss sites in the 10 calculators now go through it.
- **GPU:** `GpuKernelUtils.MissColor` uses a fraction precomputed CPU-side (`GpuShadingParams.BackgroundFogF`). A parity test checks GPU against CPU.
- **Batch:** `--fog-background`, and `--fog-background-distance N` (which implies the switch). Both are in the catalog, the Command builder and `LightingFidelity`'s covered set.

It gives the environment a lit haze, but **no light shafts**: the haze is uniform along each
background ray.

## What exists today (relief only)

| Piece | Where | Reusable for 3D? |
|---|---|---|
| Froxel grid + populate + integrate (multi-light, HG phase, noise, temporal history, reprojection) | `FroxelVolumePass`, `FroxelGrid`, `FroxelHistory` | **Yes.** Camera-agnostic: it works in grid space plus a medium. |
| Grid framing + medium build + composite by world depth | `FroxelCameraVolume` | **Partly.** Takes `HeightfieldRaymarch2D.ReliefCamera` and frames near/far from the terrain slab (`Bx/By/Bz`). |
| Composite over 8-bit beauty and over HDR beauty | `FroxelVolumePass.CompositeWorldDepth{,Hdr}` | **Yes.** Needs a world-depth buffer. |
| GPU twin (D3D + Vulkan) | `IFroxelVolumeKernel`, `FroxelGpuUniforms`, `FroxelGpuKernel`, `FroxelVolumeVulkanKernel` | **Yes, once uniforms are built from a generic camera.** `FroxelGpuUniforms` is built from `ReliefCamera`. |
| 3D depth AOV | 3D calculators' `DepthBuffer` (ray distance, `+Inf` = miss), published when `ScreenSpacePost.WantsDepthOutput` | **Yes.** It is what the composite needs. The froxel would add one more depth consumer. |

**Findings:**

1. **The populate has no occlusion.** Froxels are lit without shadow tests, so the relief froxel
   gives volumetric *haze with light colour and phase*, not shafts. Shafts in relief come from the
   separate per-pixel in-scatter march, which needs `ShadowSteps > 0`. A 3D froxel without
   occlusion would therefore look like the quick fix plus anisotropic glow around lights. It
   needs per-froxel shadowing to be worth the cost.
2. **The camera coupling is thin.** `ReliefCamera` is a pinhole or ortho basis: position, F/R/U,
   tanHalf. The 3D orbit cameras build exactly this basis inline in each calculator. Only the
   near/far framing is relief-specific (the terrain slab).
3. **3D occlusion is cheap enough to try.** At Balanced, 24×24×48 ≈ 27.6k froxels; a DE shadow
   march of 16–32 steps per froxel per light is ~0.4–0.9 M DE evaluations. That is small next to a
   1080p primary march (~2 M rays × ~100 steps), and the volume is low-frequency, so temporal
   history (already implemented) can amortise it further.

## Proposed slices

| Slice | What | Notes |
|---|---|---|
| **F1 — Generic froxel camera** (#1067) | `FroxelCamera` (pos, F/R/U, tanHalf, ortho, near/far) used by `FroxelCameraVolume` / `FroxelGpuUniforms`; `ReliefCamera` adapts to it. | Pure refactor; relief byte-identical (probe + tests). |
| **F2 — 3D froxel post-pass (CPU)** (#1068) | For the 3D calculators: build `FroxelCamera` from the orbit camera, near = 0.05, far = escape distance; populate (no occlusion yet); composite over the beauty by the depth AOV (`+Inf` → far). Opt-in `LightingFxData.FroxelVolumetrics3D`. | Arms the depth AOV like SSAO/stereo do. Batch: preset field + flag. |
| **F3 — DE-shadowed populate (shafts)** (#1069) | Per-froxel shadow march toward each light through the fractal's DE (a delegate / `IDistanceEstimator`, as the per-surface march uses). | This is what makes god-rays around the object; measure cost, then tune steps. |
| **F4 — GPU 3D froxel** (#1070) | Feed the existing D3D / Vulkan kernels from `FroxelCamera`; the GPU 3D paths provide depth; occlusion needs a DE in-kernel (per-fractal), so start with unshadowed. | Parity probe like `FroxelGpuProbe`. |
| **F5 — UI + scenes** (#1071) | Lighting & FX controls; scene shot presets pick it up automatically (#1059). | |

**Order:** F1 → F2 → F3 (judge the look), then F4. The quick fix stays as the cheap option.
