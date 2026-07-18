# Colour Theme — Phase D Handoff

Pickup notes for continuing the colour-theme enhancement work in a fresh
session. Durable facts also live in the auto-memory
(`project_colortheme_gaps`); this file is the actionable session state.

- **Branch:** `feature/ui-overhaul` (main branch is `main`).
- **Full plan:** `Docs/Technical/ColorTheme-Enhancement-Roadmap.md` — read the
  F10 / F11 spec notes and the "Phase D" phasing section first.
- **Last updated:** 2026-07-17.

## Where things stand

Phases A / B / C are **shipped** (F1-F9, F12) including the Avalonia editor UI
and the live host gamma slider. Phase D is **planned + audited but only the
prerequisite gate is built**. Remaining Phase D order:

```
☑ --colorprobe gate  →  ☑ F11a (CPU deband)  →  ☐ F11b (GPU dither)  →  ☐ F10 (alpha)
```

## Commit ledger (this arc, newest last)

| Commit | What |
|---|---|
| `31c0070` | docs: re-plan Phase D after pipeline audit |
| `82720b8` | feat: `--colorprobe` golden gate (`Engine/Diagnostics/ColorProbe.cs` + Program.cs dispatch) |
| `b2dc48e` | docs: mark `--colorprobe` shipped in Phase D plan |
| _(this)_ | feat: F11a CPU ordered dither (Bayer 8×8, pre-quantise, default-off) |

## Audit findings that changed the plan (do NOT re-derive)

1. **F11 "post-pass dither over the rendered ARGB buffer" CANNOT deband.**
   Banding is born at one spot — `GradientColorMap.MapNormalized`
   (`Engine/Models/ColorUtils.cs:449-453`): the LUT lerp yields a **float** RGB,
   then `(int)rgb.GetElement(0)` **truncates** to byte. Float sub-byte precision
   exists ONLY there. The buffer reaching the post-FX upload pass
   (`FractalRenderHost.UploadProcessedBuffer`) is already 8-bit, so ordered
   dither on it is a no-op (`floor(V + threshold) = V` for integer V). Dither
   MUST be added to the float value **before** the `(int)` cast.
2. **F10 alpha touches ~104 files.** The opaque-ARGB force (`0xFF000000 | …`)
   recurs across every theme, every calculator, all three GPU renderers, and the
   whole export/capture/video chain. It is a compositing-contract change, not a
   "add a 4th LUT lane". Do it LAST, behind a premultiply audit.

## The gate: `--colorprobe`

`Engine/Diagnostics/ColorProbe.cs`, dispatched in `Program.cs` just above the
`--kifsprobe` block. Unlike the diagnostic probes it is a **true gate** — exit
code **1** on drift, **0** on match — so CI can block a colour regression.

```
dotnet run --project FracturingFogCLD.csproj -- --colorprobe          # gate (exit 1 on drift)
dotnet run --project FracturingFogCLD.csproj -- --colorprobe regen     # reprint digest to re-pin
dotnet run --project FracturingFogCLD.csproj -- --colorprobe verbose   # gate + dump table to stdout
```

- Sweeps a fixed **21-config Gradient+Cycling matrix** (F1-F9/F12) through
  `DataDrivenColorThemes.Create` → `IColorMap.Map`, SHA-256 over the sampled
  ARGB, compares to the embedded `GoldenDigest`.
- Current golden: `b68af584c34804f02db6e07b4fdec31748ea254211efeb3e85274218ff3bfbdb`.
- Per-config table always written to `colorprobe.out` next to the exe (gitignored
  bin) so a drift localises without a rebuild.
- **Scope:** Gradient + Cycling only. 3D (needs surface normals) and ColorGen
  (separate codegen path) are out of scope; the shared quantise point
  (`MapNormalized`) is fully exercised via Gradient/Cycling.
- **REGEN RULE:** editing `BaseStops`, reordering `BuildMatrix`, or ANY intended
  colour-output change flips the digest → run `--colorprobe regen` and paste the
  new value into `GoldenDigest`. An unexpected drift is a real regression.

## F11a — CPU deband (SHIPPED, route b1)

Mechanism landed in `GradientColorMap` (`Engine/Models/ColorUtils.cs`):

- Static `DitherEnabled` (master switch) + static `DitherStrength` (global amp,
  the host lifts the active theme's strength here) + per-theme data-model
  `PaletteDitherStrength`/`ExportDitherStrength` for JSON round-trip.
- `[ThreadStatic] _ditherOffset`; `SetDitherForPixel(x,y)` seeds it from a
  centred 8×8 Bayer table (`(raw+0.5)/64 − 0.5`), no-op when disabled.
- `MapNormalized` adds the offset to each float channel **before** the `(int)`
  cast, clamped to `[0,255]` — gated so the OFF path is the exact original
  truncate. The two 3D pack points (`GradientPhong3DBase`, `PbrGradient3DBase`)
  read the shared `CurrentDitherOffset` before their byte cast.
- Wired into the CPU scalar loops in `EscapeTimeCalculator` (the row loop +
  `FillAuxAndColor`, which is the colorize exit for the SIMD kernels too).

Verify: `--colorprobe` still **PASS** (byte-exact, digest unchanged) and
`--colorprobe dither` proves the enabled path spreads the step and is
mean-preserving (revealed the truncate was biasing R 80.5→81).

**STILL UNWIRED (do next as part of F11 integration):** nothing flips
`DitherEnabled` at runtime yet — no host/UI toggle and no still-render CLI knob.
The video path has its *own, separate* pre-quantise dither on the CDF/iter value
(`_videoBandDitherEnabled`, `RenderRequest.BandDither`) — do NOT confuse it with
this LUT-quantise dither. Add an Avalonia toggle (+ optional strength slider,
per `feedback_tunable_params`) and have the render host set
`GradientColorMap.DitherEnabled` / `.DitherStrength` from it. SIMD *vector*
color maps (`IVectorColorMap`, the fixed HSV/Fire/etc. palettes) are procedural,
not LUT-banded, so they are out of scope.

## Next task: F11b — GPU HLSL dither

Same idea on the GPU render path (`Rendering.Silk` / `Rendering.Skia` shaders),
where deep-zoom banding is worst. Add the ordered offset before the shader's
float→8-bit write. F11 is only "done" once F11b ships. F10 (alpha) is the last,
widest unit — separate sign-off, behind a premultiply audit (~104 files).

## Housekeeping / constraints

- **CLAUDE.md:** all new UI work goes to `UI.Avalonia/` only; do not touch
  `MainForm.cs` / WinForms `Views/` without asking.
- Leave the pre-existing uncommitted `FracturingFogCLD.csproj` change and the
  untracked `FR.Bench/` `FR.Smoke/` `FracturedRefract/` dirs alone.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- User is red/green colourblind — use yellow `#FFCC00`, not red, for any error/
  validation UI state.
