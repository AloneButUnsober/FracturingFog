# Heightfield Relief & Volumetric FX for 2D Fractals — Spike (#102)

Status: **Phase 1 SHIPPED** (approach A productionised). Prototype behind
`--heightfieldspike` (`Engine/Diagnostics/HeightfieldReliefProbe.cs`); production
post-pass in `Engine/Rendering/Lighting/HeightfieldRelief2D.cs`, applied in
`FractalRenderHost.UploadProcessedBuffer`, driven by `FractalParameters.Relief2D*`
+ the "Relief 3D (2D heightfield)" section in `EscapeTimeParamsView`. Opt-in;
covers Mandelbrot + the EscapeTimeCalculator family (Tricorn, Burning Ship,
Julia, Multibrot, Phoenix, Magnet, Glynn, Spider). Phase 2 (full raymarch +
volumetric) still open.

## Problem

2D escape-time fractals currently get "3D themes" that are **normal-map Phong
bump only** (`IColorMap.Map(..., nx, ny)` → `PhongHelper`). The per-pixel normal
lights the escape-potential *slope*, but the surface has **no actual height**, so
it cannot:

- cast shadows onto itself,
- self-occlude / show a silhouette,
- be extruded to a mesh,
- host a volume (no Z axis → no fog/in-scatter).

Two user asks share one missing primitive — **a Z axis for 2D fractals**:

- **Q4b — real relief 3D:** true cast shadows / AO / silhouette, beyond bump.
- **Q5 — volumetric fx:** fog / god-rays / clouds, which need a 3D medium.

## The height field we already have

Every escape-time render fills `EscapeTimeCalculator.SmoothBuffer` (continuous
iteration count) and `DistanceBuffer` (exterior DE). Either is a ready-made
height field:

- **Smooth count** — dense, visually smooth, good for relief. Interior = base
  plane (height 0).
- **DE** — metric distance to the boundary; better for crisp filament ridges.

The prototype recomputes a small Mandelbrot smooth field standalone (to avoid
entangling the render host); production reads `SmoothBuffer` directly.

## Approaches evaluated

### A. 2.5D screen-space heightfield relief (prototype — PROVEN)

Treat height `h(x,y)` as terrain in screen space:

1. **Hillshade** — normal from the height gradient, Lambert against the light.
2. **Horizon cast shadow** — march each pixel toward the light across the field
   (1-px DDA), tracking the max elevation angle; occluded ⇒ in shadow.

Cost `O(W·H·steps)`, no 3D camera, no DE-march. Prototype at 512² default
Mandelbrot: **83 % exterior, 10.5 % of exterior pixels in genuine cast shadow**,
relief range `[0.003, 0.60]`. The radiating dark streaks in
`heightfield-relief.ppm` are real self-shadows — impossible with bump-only Phong.

Limitations seen: single-pixel DDA gives slightly streaky/aliased shadow edges
(fix: sub-pixel step + a small blur, or a maximum-mipmap horizon map for
`O(W·H·log)`), and the relief reads subtle at low `heightScale`.

**Verdict:** cheapest honest upgrade. Ships the "real relief" win (shadows,
self-occlusion) with no new geometry or camera. **Recommended as Phase 1.**

### B. Full heightfield raymarch (true 3D)

Build an explicit 3D surface `z = h(x,y)` and raymarch it from a real camera —
reuse `ShadingPipeline` (it already does AO / soft shadow / SSAO / reflection /
tonemap for the DE raymarchers). Gives arbitrary camera angle, true perspective
relief, silhouette, and — crucially — a scene the **existing volumetric stack**
(`LightingFxData.VolumeSteps` / fog / in-scatter / cloud-noise) can fill.

Cost: a camera + a heightfield DE (`de(p) = p.z − h(p.x, p.y)`, or a proper
lower-bound estimate). Higher effort; overlaps the mesh work (#101) since a
marching-**squares** extrusion of the same field yields an exportable mesh.

**Verdict:** the honest path to Q5 (volumetric) and oblique-angle relief. Phase 2.

### C. Cheap fakes (fallback / complementary)

- **Iteration-band depth fog** — tint by iteration count as pseudo-distance.
  Trivial, partial look, no real occlusion.
- **Buddhabrot / Nebulabrot** already *are* 2D orbit-density fields → genuinely
  fog/glow-able volumetrically with **no invented geometry**. Cheapest real
  volumetric, but only for that family.

## Recommendation

1. **Phase 1 (Q4b):** productionize approach **A** — read `SmoothBuffer`, add a
   `HeightScale` + light-elevation param (`FractalParameters`, per
   [tunable-params convention]), emit relief into the 2D theme path as an opt-in
   post step. Sub-pixel shadow march + light blur to fix aliasing.
2. **Phase 2 (Q5):** approach **B** — heightfield DE + camera, reuse
   `ShadingPipeline` + `LightingFxData` volumetric knobs. Shares the extruded
   surface with the marching-squares mesh exporter (pairs with #101).
3. **Opportunistic:** wire approach **C**'s Buddhabrot-density volumetric as a
   quick, real volumetric demo while B is built.

## Dependencies

- Height quality tracks the potential/DE field — pairs with #100 (approximate DE
  for more 2D types gives crisper ridges on Tricorn / Burning Ship).
- Phase 2 volumetric reuses the raymarcher `LightingFxData` stack directly.

## Repro

```bash
dotnet run --project FracturingFogCLD.csproj -- --heightfieldspike
```

Writes `heightfield-relief.ppm` + `heightfieldspike.out` next to the exe;
asserts non-degenerate exterior, relief range, and cast-shadow coverage.
