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
☑ --colorprobe gate  →  ☐ F11a (CPU deband)  →  ☐ F11b (GPU dither)  →  ☐ F10 (alpha)
```

## Commit ledger (this arc, newest last)

| Commit | What |
|---|---|
| `31c0070` | docs: re-plan Phase D after pipeline audit |
| `82720b8` | feat: `--colorprobe` golden gate (`Engine/Diagnostics/ColorProbe.cs` + Program.cs dispatch) |
| `b2dc48e` | docs: mark `--colorprobe` shipped in Phase D plan |

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

## Next task: F11a — CPU deband

Goal: kill banding on the CPU render path by dithering **before** the
float→byte truncate, without changing the `IColorMap.Map` signature.

Planned approach (route b1 in the roadmap F11 note):

- Add a `[ThreadStatic]` dither offset on `GradientColorMap`, e.g.
  `DitherOffset` = `bayer8x8[x & 7, y & 7] − 0.5f` (ordered 8×8 Bayer).
- The CPU render loops set it per pixel immediately before each `Map` call
  (thread-safe: each worker sets its own before use).
- `MapNormalized` adds the offset to each float channel **before** the `(int)`
  cast at `ColorUtils.cs:451-453`. Also apply at the two 3D pack points
  (`GradientPhong3DBase`, `PbrGradient3DBase`) since they share the quantise.
- Off by default. Data model: global toggle + optional per-theme
  `DitherStrength`.
- **Guard with `--colorprobe`:** the gate must still PASS with dither OFF (zero
  offset → byte-identical). Only regen if the *default-off* output changes
  (it must not).

Find the CPU render loops that call `Map` (they have x/y):

```
Grep  int Map(   in Engine/Calculators/** and Rendering.Skia/**
```

F11b (GPU HLSL dither) and F10 (alpha) are separate, wider units — each behind
its own sign-off. GPU is where deep-zoom banding is worst, so F11 is only
"done" once F11b ships too.

## Housekeeping / constraints

- **CLAUDE.md:** all new UI work goes to `UI.Avalonia/` only; do not touch
  `MainForm.cs` / WinForms `Views/` without asking.
- Leave the pre-existing uncommitted `FracturingFogCLD.csproj` change and the
  untracked `FR.Bench/` `FR.Smoke/` `FracturedRefract/` dirs alone.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- User is red/green colourblind — use yellow `#FFCC00`, not red, for any error/
  validation UI state.
