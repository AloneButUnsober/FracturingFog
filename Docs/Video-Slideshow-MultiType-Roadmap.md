# Multi-Type Video Slideshow — Feasibility &amp; Roadmap

Status: **P1 shipped** (2026-07-22, [#91]). P2–P4 planned. Spun out of the
[Animation Roadmap](Animation-Roadmap.md) open follow-ups (2026-07-03) as
its own project because the fix is real engine work, not an animation
follow-up patch.

Tracking issues: [#91] (P1, done) · [#92] (P2) · [#93] (P3) · [#94] (P4).

[#91]: https://github.com/AloneButUnsober/MandelbrotExplorer/issues/91
[#92]: https://github.com/AloneButUnsober/MandelbrotExplorer/issues/92
[#93]: https://github.com/AloneButUnsober/MandelbrotExplorer/issues/93
[#94]: https://github.com/AloneButUnsober/MandelbrotExplorer/issues/94

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

- No **motion model** beyond point-zoom yet (a Julia leg zooms into a point; it
  does not sweep the constant — that's P2).
- `NewtonPolyCoeffs` custom polynomials are not snapshotted (default-exponent
  path only).
- Deep-zoom on the alt path is double-precision (`EscapeTimeCalculator` sets the
  hi limbs only → ~1e13 cap). Fine for these shallow regions.
- A zoomable-2D region with **null Params** inherits whatever family fields are
  live (no built-in non-Mandelbrot regions hit this; user regions always
  snapshot).

## Remaining scope

- **P2** ([#92]) — Julia constant-path animated legs (animate `c` along a path).
  Cheapest striking win; reuses the Phase-5 leg-animator hooks.
- **P3** ([#93]) — 3D raymarch camera-fly legs (Mandelbulb / Mandelbox / KIFS /
  Quaternion / Bicomplex / Kleinian). Needs a 3D-camera snapshot on the region
  and a camera-path motion model. Admits `Raymarch3D`.
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
