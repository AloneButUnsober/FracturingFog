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
☑ --colorprobe gate  →  ☑ F11a (CPU deband)  →  ◐ F11b (GPU dither, code done / UNVERIFIED on-device)
→  ☑ runtime toggle (Deband checkbox + strength)  →  ◐ F10 (alpha: F10.1 LUT foundation done)
```

## Commit ledger (this arc, newest last)

| Commit | What |
|---|---|
| `31c0070` | docs: re-plan Phase D after pipeline audit |
| `82720b8` | feat: `--colorprobe` golden gate (`Engine/Diagnostics/ColorProbe.cs` + Program.cs dispatch) |
| `b2dc48e` | docs: mark `--colorprobe` shipped in Phase D plan |
| `8d5ce45` | feat: F11a CPU ordered dither (Bayer 8×8, pre-quantise, default-off) |
| `5e9afc4` | feat: F11b GPU HLSL ordered dither in cg_pack_bgra (default-off, UNVERIFIED on-device) |
| `ad3de2c` | feat: Deband runtime toggle (Post-FX checkbox + strength slider → GradientColorMap statics) |
| _(this)_ | feat: F10.1 per-stop alpha foundation (LUT 4th lane, default-255 byte-exact) |

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

## F10.5 live alpha preview + the "export loses alpha" report (2026-07-18)

- **F10.5 (commit `7787af7`)** — the on-screen present is opaque (swap-chain
  ignores the alpha channel; `UploadProcessedBuffer` force-opaques the buffer),
  so a theme's authored stop-alpha was invisible while editing. New default-off
  **Alpha preview** checkbox (Post-FX floating menu) composites the render over a
  checkerboard using the authored coverage byte (read from the source calc buffer
  so it survives the post-FX force-opaque). Display-only — runs after the
  save-snapshot, so exports keep straight alpha.
- **"Saved images don't show the transparency" is NOT a bug.** The real export
  path carries alpha end to end (verified: `--colorprobe alphaposter` →
  min 1..255, ~87% translucent, identical with and without a watermark; both the
  poster path and `SaveLastFrameToPng` preserve it). Straight-alpha PNGs keep RGB
  **byte-identical** to an opaque theme, so an alpha-unaware viewer (Windows
  Photos, Explorer thumbnails) renders translucent and opaque exports the same.
  The transparency is in the file. Owner chose to **keep straight alpha** and
  verify with the diagnostics below rather than bake a background.
- **Diagnostics (commit `9498c53`):** `--colorprobe alphawm` (TRUE gate,
  watermarked export), `alphaposter [out]` (TRUE gate, real PosterRenderer render;
  writes a `_checker.png` preview), `alphascan <file.png>` (inspect any PNG:
  alpha min..max + `_checker.png`). If `alphascan` on a genuine user export ever
  reports `255..255`, THAT is a real bug — start at theme persistence.

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

## F11b — GPU HLSL dither (CODE DONE, UNVERIFIED on-device)

Landed in `Rendering.D3D/MandelbrotGpuKernel.cs` — the ONLY procedural GPU
palette/quantise point. (`Rendering.Silk`'s `SilkGLRenderer` just blits the
CPU-coloured BGRA buffer, so F11a already debands it; the D3D compute kernel is
the only path that evaluates a palette and quantises on the GPU.)

- `cg_pack_bgra(float3 c, uint px, uint py)`: adds a centred 8×8 Bayer offset
  (`cg_bayer8`, the exact twin of `GradientColorMap.Bayer8`) to each channel
  **before** the round, clamped to [0,255]. Gated by the cbuffer field
  `gDitherStrength` (repurposed `_pad0`, layout unchanged at 64 B).
- All three `cg_pack_bgra` call sites (in-set / escape / bulb-skip) now pass the
  shader's `x`,`y`.
- `Params.DitherStrength` is set in `Run` from the SAME statics as F11a
  (`GradientColorMap.DitherEnabled ? .DitherStrength : 0`), so one runtime knob
  drives CPU + GPU together. Default-off → `gDitherStrength=0` → plain round,
  byte-identical to before.

**MUST DO before sign-off:** the HLSL is compiled at *runtime* by D3DCompiler on
a real GPU — the C# build does NOT exercise it. Nobody has run a GPU render with
`DitherEnabled=true` yet. Verify: (1) the color-path shader still *compiles*
(watch for a D3DCompile error on the `cg_bayer8` literal / `clamp` overloads),
(2) default-off output is unchanged, (3) enabled output visibly debands a deep
gradient without artefacts. This is Windows + D3D11 only.

## Runtime toggle (WIRED)

"Deband" now lives in the Post-FX group of the floating menu (a checkbox + a
0–100 strength slider), following the live-gamma chain:

- `FloatingMenuView.axaml` — Deband row (Post-FX grid) → `FloatingMenuViewModel`
  `BandDither` / `BandDitherStrength` (+ `*Silent` setters + toggle/slide events).
- `ShellViewModel` bridges those events → `MainViewModel.BandDither` /
  `.BandDitherStrength`, which write `FractalViewState.BandDither(Strength)` and
  call `Trigger()` (full re-render — deband acts at colorize, not post-FX).
- `FractalRenderHost.ApplyBandDitherState()` lifts the ViewState into
  `GradientColorMap.DitherEnabled` / `.DitherStrength` at the top of
  `RunFrameJobCalc` (interactive path) — the SAME statics the CPU (F11a) and GPU
  (F11b) quantise points read. One knob drives both.

Default OFF. `--colorprobe` still PASS; app launches clean (no XAML parse
error). NOT visually driven: the GUI window isn't screenshot-reachable under
this RDP session, so the on-screen deband + the GPU F11b path still need a
local visual sign-off (toggle Deband on at a shallow gradient / deep zoom and
confirm the banding smooths without artefacts, on both CPU and D3D renderers).

**Export path — DONE (commit `0c19363`).** Still export goes through
`PosterRenderer` (interactive "Image" button, batch, server, scene capture),
which builds its own calculator and never touched the deband globals — so the
toggle only reached exports by accident, via whatever the last interactive
frame left in the process-global statics (and not at all headless). Fixed:
`PosterRequest` now carries `BandDither`/`BandDitherStrength` (default off);
`RenderToFile` sets the `GradientColorMap` deband globals from the request
around the calc and restores them in a `finally`; `CreatePosterRequest` fills
both from ViewState → the "Image" export is WYSIWYG. Batch/server/scene keep
their existing (off) behaviour until they thread the fields. Default-off byte-
identical; `--colorprobe` PASS (digest `b68af584…`).

_Video is NOT this gap_ — the animation/slideshow pipeline has its own,
older "BandDither" (a **smooth-iter** spatial dither via
`MandelbrotCalculator.ApplyBandDitherRecolor`, sourced from Video Settings /
`SlideshowConfig`), a different technique from the F11 float→byte deband.
Forcing the F11 globals on during a video render would double-dither; left
alone by design.

**Persistence — SKIPPED (owner decision 2026-07-18).** The premise was false:
NO interactive post-FX persists across restart today (Brightness / Contrast /
Gamma / Adaptive all reset to ViewState defaults on launch — deliberate, post-FX
is a per-session look tied to theme/region). The `Set*Silent` restore methods
(incl. `SetBandDitherSilent`) are defined but never called — dead stubs, no
loader. Persisting only BandDither would be inconsistent with every other
slider, and doing it "right" is a new app-session post-FX store touching all
sliders (its own feature, not a colour unit). Deferred; revisit as a general
"persist interactive post-FX" feature if ever wanted.

## F10 — per-stop alpha (F10.1 foundation DONE)

Phased because the full change is a ~104-file compositing-contract shift. F10.1
lands the gradient-LUT alpha carrier only, all defaulting to A=255 so output is
byte-exact and `--colorprobe` still PASSES:

- Data model: `ColorStopData.A` + `InSetColorData.A` (both default 255; omitted
  in old JSON ⇒ property initialiser keeps 255 ⇒ back-compat).
- `ColorStopDataExtensions` carries A both ways (`Color.FromArgb(A,R,G,B)`).
- `SampleStops` outputs a linearly-interpolated `alpha` (blend-space independent,
  gamma-exempt); `BuildLut` stores it in the Vector128 **lane 3**, so the
  existing per-pixel `base+delta·frac` lerp interpolates it for free.
- `MapNormalized` emits `(aC << 24)` from lane 3 instead of forcing `0xFF000000`
  (both dither branches). Dither never touches the coverage term.
- Verify: `--colorprobe` byte-exact (opaque default) + new `--colorprobe alpha`
  proves A rides 0→255 monotone through the LUT.

### F10.2 — per-stop alpha AUTHORING UI (DONE, commit `fb0719a`)

Gives F10.1's LUT carrier a consumer — the Color Theme Editor can now author +
save per-stop opacity (nothing set A<255 before, so the foundation was
untestable). `ColorStopDef`/`InSetColorDef` gain `A` (byte=255); the
`ColorThemeDefAdapter` carries it across all four Data↔Def stop/in-set maps;
`ColorStopRowVm` exposes `A` (0..255) with an ARGB `StopColor` (ColorPicker
alpha slider works) + alpha-aware swatch; the editor row gained an Alpha
NumericUpDown. Authoring + persistence only — default A=255 ⇒ `--colorprobe`
byte-exact (digest `b68af584…`), `--colorprobe alpha` PASS.

**Authored A rides into the ARGB buffer (via F10.1) but is NOT yet surfaced
correctly:** the screen blits opaque, and PNG export still declares
`SKAlphaType.Premul` (`ImageExport.SaveBgraSkia`) while the buffer is *straight*
alpha — so an exported translucent theme would mis-colour (premul-vs-straight).
That correctness work is F10.3.

**Remaining F10 phases (each its own sign-off — NOT started):**

- **F10.3a — straight-alpha PNG encode (DONE, commit `c06367f`).**
  `SaveBgraSkia` now declares `SKAlphaType.Unpremul` (opaque case byte-
  identical: A=255 ⇒ premul==straight), and `PosterRenderer.ApplyBrightnessContrast`
  preserves the source alpha byte (was forced `0xFF`). New TRUE gate
  `--colorprobe alphapng` round-trips a hand-built A=128 BGRA buffer through
  `SavePixelsToFile` → PNG → SkiaSharp decode and asserts A survives + RGB
  unmangled (PASS). This closes the core author→export vertical slice: an F10.2
  translucent theme now exports with its alpha. **Scope: the PNG *encode* path
  only.**

- **F10.3b — multi-consumer compositing audit (DONE, commit `e96fbb1`;
  visual proof `599c9b4`).** Audited every consumer that blends onto a
  background or reloads/reencodes:
  - **`PngSequenceWriter.SavePng`** (video / PNG-sequence frames) declared
    `Premul` on the straight-alpha buffer → **fixed to `Unpremul`**. New TRUE
    gate `--colorprobe pngseq` round-trips a translucent frame through the
    writer → decode (PASS; it has its own `SavePng`, separate from ImageExport).
  - **`FractalOverlayCompositor.DrawOnto`** (in-place grid/watermark/HUD blend)
    declared `Premul` → **fixed to `Unpremul`** so SrcOver over a translucent
    fractal composites correctly.
  - **Watermark reload (`CompositeWatermarkRenderSkia`)** — audited, already
    correct: `SKBitmap.Decode` → premul surface → SrcOver over transparent
    preserves src alpha → PNG re-encode unpremultiplies → round-trips. No change.
  - All fixes are byte-identical for opaque output (A=255 ⇒ premul==straight),
    so the golden `--colorprobe` digest is unchanged (`b68af584…`).
  - **On-device visual sign-off DONE** via `--colorprobe alphaimage`: renders a
    translucent gradient (A 0→128→255) through the real `ImageExport` PNG path
    and composites it over a checkerboard. Confirmed a smooth coverage fade (not
    a blown-out hard band) → straight-alpha contract holds end to end.
  - NOTE: driving the live GUI window with computer-use was **denied by the
    owner** this session, so the sign-off was done with the headless
    `alphaimage` artifact instead of clicking the interactive "Image" button.
    The interactive button's path (`PosterRenderer` → `ImageExport` +
    `CompositeWatermarkRenderSkia`) is the same code the gates + artifact
    exercise, but a human eyeball of the actual button output is still worth
    doing when convenient.
- **F10.4 — procedural/3D/GPU pack parity. DONE (lit) + scoped.**
  Per-surface decision:
  - **3D lit bases — FIXED.** `GradientPhong3DBase.LitMap` and
    `PbrGradient3DBase.LitMapPbr` sample the gradient LUT for albedo (which
    carries authored stop alpha in the top byte) but then packed a forced
    `0xFF`, silently dropping it. Now they read `albedoA = (albedoI>>24)&0xFF`
    and pack it back as **coverage** — lighting modulates the covered RGB only,
    alpha is not a light term. Opaque stops (A=255) keep the `0xFF` top byte →
    byte-exact; golden `--colorprobe` digest unchanged (`b68af584…`). The
    in-set early-out still returns opaque black. Gate: **`--colorprobe alphalit`**
    (TRUE gate) builds a Phong3D AND a Pbr3D theme whose stops ramp A:0→255 and
    asserts the lit output carries an interpolated, monotone coverage byte with
    non-black RGB, while an all-opaque control theme still packs 255 everywhere.
    Both PASS.
  - **Procedural themes (~33 `ColorSchemes/*`, `ColorUtils.PackArgb`/`PackArgbF`)
    — LEFT OPAQUE by design.** They compute RGB from a formula (HSV cycling,
    fire ramp, etc.) with **no authored-alpha source** — there is no stop alpha
    to carry, so forcing `0xFF` is correct, not a bug.
  - **GPU `cg_pack_bgra` (D3D) — F10.4b: NO-OP by design (premise void).**
    Audited the whole GPU colour path. The forced-`0xFF` pack is **correct**, not
    a gap: there is no authored-alpha source that can ever reach it. A theme only
    hits the GPU pack when it implements `IGpuHlslPalette` (`EscapeTimeCalculator`
    does `ColorMap as IGpuHlslPalette` → `SetPalette`; a gradient theme returns
    `null` there, so `HasGpuPalette==false`, `colorDst==null`, and the CPU
    writeback colourises it via the **alpha-aware** `Map`/`MapNormalized`). Every
    one of the 21 `IGpuHlslPalette` themes is a **procedural** `ColorSchemes/*`
    scheme whose colour model is `float3`/vec3 (`rgb()`/`hsv()`/`palette()` — the
    ColorGen DSL has **no alpha primitive**; `CgType` is Scalar|Vec3 only), so
    there is no per-stop alpha to carry. The only authored-alpha carriers are
    `GradientColorMap` subclasses (F10.1 LUT 4th lane), and **none** implements
    `IGpuHlslPalette`. So a translucent gradient theme already exports with alpha
    even when GPU compute is on — its palette pass runs on the CPU. The
    float3→float4 codegen change was **rejected**: it would carry a constant
    `1.0` alpha (pure overhead, zero payoff).
    - **Guard gate: `--colorprobe gpualpha`** (TRUE gate). Reflects the engine
      assembly and asserts `IGpuHlslPalette ∩ GradientColorMap == ∅` — i.e. no
      authored-alpha theme reaches the GPU pack. PASS today (21 GPU themes, 0
      carriers). If someone later hand-writes an HLSL body on a gradient theme,
      this fails loudly (the point where the float3→float4 work would actually
      become necessary) instead of shipping silent opaque output.
    - Issue [#46](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/46)
      closed as by-design with this reasoning + the gate as the regression guard.

## Remaining Phase-D work

1. ~~**Control Center parity for the Phase-D Post-FX controls.**~~ **DONE
   (commit `d8e5d6e`, issue #47).** The Deband checkbox + strength slider and the
   Alpha-preview checkbox now live in the canonical
   `UI.Avalonia/Views/ControlCenterSections/ColorLightSectionView.axaml` Post-FX
   grid (they had been in `FloatingMenuView.axaml` only). Pure XAML add — the VM
   props (`Menu.BandDither`/`.BandDitherStrength`/`.AlphaPreview`) are shared via
   the `Menu` binding; detach parity is free (the section is one UserControl).
   **Lesson:** a control added to `FloatingMenuView.axaml` does NOT auto-appear in
   the Control Center (separate XAML, shared VM) — add both when touching Post-FX.
2. **F11b GPU deband** — still needs on-device visual sign-off (runtime-compiled
   HLSL; no GPU render with `DitherEnabled=true` has run yet). See the F11b section.
3. **F10.6 "flatten over background" export option** — optional, previously
   declined; revisit only if wanted.

## Housekeeping / constraints

- **CLAUDE.md:** all new UI work goes to `UI.Avalonia/` only; do not touch
  `MainForm.cs` / WinForms `Views/` without asking.
- Leave the pre-existing uncommitted `FracturingFogCLD.csproj` change and the
  untracked `FR.Bench/` `FR.Smoke/` `FracturedRefract/` dirs alone.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- User is red/green colourblind — use yellow `#FFCC00`, not red, for any error/
  validation UI state.
