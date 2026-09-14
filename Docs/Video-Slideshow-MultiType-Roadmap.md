# Multi-Type Video Slideshow — Feasibility &amp; Roadmap

Status: **P1 shipped** (2026-07-22, [#91]); **P2 shipped** (2026-09-14, [#92]).
**P3 shipped** (2026-09-14, [#93]). P4 planned. Spun out of the
[Animation Roadmap](Animation-Roadmap.md) open follow-ups (2026-07-03) as its own
project because the fix is real engine work, not an animation follow-up patch.

Tracking issues: [#91] (P1, done) · [#92] (P2, done) · [#93] (P3, done) ·
[#94] (P4).

[#91]: https://github.com/AloneButUnsober/FracturingFog/issues/91
[#92]: https://github.com/AloneButUnsober/FracturingFog/issues/92
[#93]: https://github.com/AloneButUnsober/FracturingFog/issues/93
[#94]: https://github.com/AloneButUnsober/FracturingFog/issues/94
[#801]: https://github.com/AloneButUnsober/FracturingFog/issues/801

## Problem (original)

The video slideshow pool was **Mandelbrot-only**. The gate lived in
[`FractalRenderHost.Video.cs`](../Engine/Rendering/FractalRenderHost.Video.cs)
`VideoSlideshowLoop` (the region filter, and a per-leg
`ViewState.FractalType = FractalType.Mandelbrot` force).

Note: **single-shot** video zoom was never Mandelbrot-only — it dispatches to
the alt calculator via `SelectAltCalculator(ViewState.FractalType)` +
`SyncAltCalculatorForVideoFrame`, driven by the live `ViewState.FractalParameters`.
Only the unattended **slideshow** pool was gated.

### Why it was Mandelbrot-only

`FractalRegion` captured a geometric target (center, zoom, iterations,
quality) plus a few extras (UserBulb source+camera, UserEquation/Sandbox by
name, lighting override) but **carried no core per-family 2D parameters** — no
Julia constant, no Newton exponent/relaxation, no Phoenix/Glynn constant, no
Spider decay, no Apollonian knobs. Built-in non-Mandelbrot regions worked only
because their defaults happen to render correctly (their descriptions literally
say "Set X before recall").

For a family with custom params an unattended zoom would render the wrong image
(missing the per-engine params). Escape-time 2D families are otherwise
reconstructable from center + zoom + iterations, so once the params round-trip,
they re-zoom faithfully.

## P1 — shipped ([#91])

1. **Capability classifier.** `FractalMotionClass { Zoomable2D, Raymarch3D,
   NonSpatial }` + `FractalMotionCapabilities` in
   [`Abstractions/Models/FractalCapabilities.cs`](../Abstractions/Models/FractalCapabilities.cs).
   `SupportsVideoZoomLeg(type)` = 2D-zoomable **and** not user-code.
   (Named `FractalMotionCapabilities` — `FractalCapabilities` is already the
   `[Flags]` per-pixel-data bitmask in `Enums.cs`.)
2. **Per-family param snapshot.** `RegionFractalParams` on
   [`FractalRegion`](../Engine/Models/FractalRegion.cs) — a JSON-lean, nullable
   snapshot of the 2D scalars (Julia/Phoenix/Glynn constants, Multibrot power,
   Spider decay, Newton exponent/relaxation, Secant offset, Apollonian knobs).
   `Snapshot(type, params)` captures only what a family needs (null when
   defaults suffice); `ApplyTo(params)` overlays them. Omitted from JSON when
   null, so Mandelbrot + legacy regions stay clean. Wired into
   `HostColorThemeService` save (`BuildGeometryFromLiveState`) + recall
   (`LoadRegionFractalParams`).
3. **Slideshow gate replaced.** `VideoSlideshowLoop` now admits any
   `SupportsVideoZoomLeg` region, honours `region.FractalType`, restores
   `region.Params`, and routes the leg pre-render through the alt calculator.

Result: Julia/Multibrot/Phoenix/Glynn/Spider/Newton/Halley/Secant/Nova/
Magnet/Tricorn/BurningShip/TearDrop/Apollonian/generated regions play real
zoom legs in the video slideshow.

### P1 limitations (deliberate)

- `NewtonPolyCoeffs` custom polynomials are not snapshotted (default-exponent
  path only).
- Deep-zoom on the alt path is double-precision (`EscapeTimeCalculator` sets the
  hi limbs only → ~1e13 cap). Fine for these shallow regions.
- A zoomable-2D region with **null Params** inherits whatever family fields are
  live (no built-in non-Mandelbrot regions hit this; user regions always
  snapshot).

## P2 — shipped ([#92])

Julia (and its cheap 2D escape-time cousins Phoenix, Glynn) get a **motion model
beyond point-zoom**: the complex constant sweeps along an eased path during the
leg while the set keeps its recognisable shape.

1. **`ConstantPathLegAnimator`**
   ([`Abstractions/Animation/ConstantPathLegAnimator.cs`](../Abstractions/Animation/ConstantPathLegAnimator.cs))
   — an `IParameterAnimator` with a finite, leg-length timeline (integrates its
   own elapsed time from the per-frame `dt`), offset-*relative* so `u = 0`
   reproduces the authored constant exactly (matches the pre-rendered leg-start
   frame). Shapes: `Orbit` (full circle, returns to base), `Line` (out-and-back),
   `Arc` (partial sweep). Smootherstep easing → zero velocity/acceleration at the
   ends, no visible jerk. Amplitude scales gently with `|c|`, clamped to a
   tasteful band, so a near-origin constant isn't flung across the plane.
2. **`ConstantDriftResolver`**
   ([`Abstractions/Animation/ConstantDriftResolver.cs`](../Abstractions/Animation/ConstantDriftResolver.cs))
   — maps `Julia → JuliaC`, `Phoenix → PhoenixP`, `Glynn → GlynnC` (else null)
   and builds a default drift centred on the constant the leg already restored,
   with per-leg variety (shape/angle) from the video RNG. Deterministic orbit
   when no RNG is supplied (tests).
3. **Reuse of the Phase-5 hooks.** `VideoSlideshowLoop` calls
   `MaybeAddDefaultConstantDrift(region, legSeconds)` right after
   `BuildVideoLegAnimators`: an **authored** animation targeting the constant
   wins (no double-drive); otherwise the default drift is synthesised. Adding an
   animator makes `_videoLegAnimators` non-empty, which already disables TAA
   reprojection + the leg-locked histogram CDF for the leg — both assume only
   pan/zoom moves between frames, so animating `c` without this would ghost.

Gated by `EnableAnimations` **and** the new `AutoConstantDrift` flag
(`VideoZoomRequest` / `SlideshowConfig`, default on; Slideshow Settings toggle
"Drift the constant on Julia / Phoenix / Glynn legs"). Off ⇒ static point-zoom,
so the proven path is unchanged.

**Per-leg variance ([#801], shipped 2026-09-14).** Two opt-in knobs on the
default drift, under `AutoConstantDrift` (default off):
- **Vary start position** — each leg begins at a small bounded random offset
  (≤ amplitude) from the authored constant. `ConstantPathLegAnimator.StartValue`
  + `ApplyStartValue()` let `MaybeAddDefaultConstantDrift` write the leg's start
  constant into the live params *before* the leg pre-render, so the fade target
  matches frame 0 (no jump).
- **Vary speed** — a per-leg traversal multiplier (~0.75×–1.75×): Orbit loops,
  Line oscillations, Arc sweep scale. Integer speeds still close an orbit;
  fractional ones end off-base (the cross-fade covers it).

Flags: `VaryConstantStart` / `VaryConstantSpeed` (`VideoZoomRequest` /
`SlideshowConfig`; Slideshow Settings sub-toggles).

### P2 limitations (deliberate)

- Default drift is **Julia / Phoenix / Glynn** only (families with one natural
  complex constant). Multibrot power, Newton exponent, etc. are not swept.
- The drift is a *default* synth when no authored animation drives the constant;
  authored `c`-tracks still win and play as before.
- `Arc` ends off the authored constant; the leg cross-fade covers the seam.

## P3 — shipped ([#93])

Raymarched-3D families (Mandelbulb, Mandelbox, KIFS, Quaternion Julia +
Mandelbrot, Kleinian, Bicomplex) play **camera-fly** legs: the camera dollies
from a wide establishing shot to the region's authored framing.

**Key realisation — the dolly is free.** Every 3D calculator derives its camera
distance as `CameraDistance / Zoom`. So the existing `VideoLoop` zoom
interpolation (log-lerp, smoothstep-eased) *is* a log-distance camera dolly — no
new motion engine needed. P3 just had to snapshot/restore the camera and feed
the leg the right zoom endpoints.

1. **3D camera snapshot.** `RegionFractalParams` gains a generic camera block —
   `Cam3DFamily` (the int `FractalType` discriminator), `Cam3DDistance/Theta/Phi`
   and `Cam3DSliceW` (Quaternion/Bicomplex). `Snapshot()` captures it for the six
   non-user-code raymarch families; `ApplyTo()` routes it back to the right
   per-family fields via the discriminator (so `ApplyTo` stays type-free).
   Round-trips both interactive save/recall (`HostColorThemeService`) and each
   slideshow leg. UserBulb keeps its own dedicated camera fields (user code).
2. **Capability.** `FractalMotionCapabilities.SupportsVideoCameraLeg` = Raymarch3D
   and not user code; `SupportsVideoLeg` = zoom-leg ∪ camera-leg.
3. **Pool + leg.** `VideoSlideshowLoop` admits `SupportsVideoCameraLeg` regions
   (exempt from the 2D min-plane-zoom floor). A camera leg pins the plane center,
   skips constant-rate scaling, and lerps Zoom from
   `authored / EstablishingFactor` (camera wide) to the authored framing
   (`max(region.Zoom, 1.0)`), reversed for reverse runs. Pre-render + per-frame
   already route through the 3D alt calculators (`SelectAltCalculatorByType`).

### P3 limitations (deliberate)

- **Dolly only** — no camera orbit (theta/phi are restored to the authored angles
  and held). An orbit sweep is a natural follow-up.
- UserBulb/Sandbox/UserEquation stay out of the pool (user code — RCE gate).
- Establishing factor + authored-zoom floor are fixed constants (6× / 1.0), not
  yet per-run options.

## Remaining scope

- **P4** ([#94]) — Non-spatial families (Plasma, Flame, DLA, Logistic, IFS,
  L-System, attractors, Buddhabrot) via static-hold / param-sweep legs. Admits
  `NonSpatial`.

## Security

`UserEquation` / `Sandbox` / `UserBulb` execute user-authored code and are
excluded from the slideshow pool by `FractalMotionCapabilities.IsUserCode`
regardless of motion class; they remain gated by
[`Server/Guard/FractalTypeAllowlist.cs`](../Server/Guard/FractalTypeAllowlist.cs)
on any networked path (RCE risk). Do not admit them in P3/P4.

## See also

- [Animation Roadmap](Animation-Roadmap.md) — Phase 5 video animation hooks
  reused by P2.
- [`FractalRegion.cs`](../Engine/Models/FractalRegion.cs) — the region asset +
  `RegionFractalParams`.
- [`FractalCapabilities.cs`](../Abstractions/Models/FractalCapabilities.cs) —
  the motion-class classifier.
