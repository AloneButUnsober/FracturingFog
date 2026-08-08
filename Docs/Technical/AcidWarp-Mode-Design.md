# Acid Warp Mode — Design & Implementation Plan

Tracking document for adding an **Acid Warp** mode to Fracturing Fog, plus six
"ideas to steal" harvested from the Acid Warp source that FF does not already
do. Acid Warp is the DOS palette-cycling demo by **Noah Spurrier** (1992),
later ported to SDL/Emscripten by **Boris Gjenero** (dreamlayers).

Reference source: <https://github.com/dreamlayers/acidwarp> (`gen_img.c`,
`palinit.c`, `rolnfade.c`, `acidwarp.c`).

Status legend: ☐ not started · ◐ in progress · ☑ shipped

> **Design R&D provenance.** This plan was produced design-only. No engine code
> was written when it was filed — only this doc and the tracking issues.

## Implementation status (2026-08-08)

Branch `feat/acidwarp-mode` (off `main`, not pushed). Full suite 1111/1111.

| Issue | State | Notes |
|---|---|---|
| #247 AW-1 calculator | ☑ shipped | 20 clean-room patterns; 7 tests |
| #248 AW-2 wiring + params UI | ☑ shipped | full FractalType wiring + picker + region |
| #249 IDEA-1 live palette cycling | ☑ shipped | LUT rotation + "Cycle" toolbar toggle |
| #250 AW-4 Spurrier intro | ☑ shipped | once-per-process gate |
| #251 IDEA-6 auto-VJ | ☑ shipped | shuffle+classic-first playlist + `AcidWarpAmbientDirector` (hold/lock/pause/next) wired into MainViewModel with fade-to-black advance; toolbar Auto-VJ / Lock / Next (Acid Fog only); awaits on-device visual sign-off |
| #252 IDEA-2 XOR post-transform | ☑ shipped | colour-index moiré on any field |
| #253 IDEA-3 domain-warp | ☑ shipped | warp inside Acid Warp + **cross-fractal warp on the EscapeTimeCalculator family** (Julia/Burning Ship/Tricorn/Multibrot/Magnet/Glynn/Phoenix/Spider), toggle + strength + frequency, animatable; Mandelbrot's dedicated deep-zoom SIMD calc intentionally excluded |
| #254 IDEA-4 sparkle | ☑ shipped | every-Nth LUT boost |
| #255 IDEA-5 seamless toggle | ☑ shipped | opt-in close-the-loop |

Remaining app-integration work (wants on-device visual verification): the
auto-VJ ambient loop (#251) is wired (MainViewModel ambient loop +
`AcidWarpAmbientDirector`), pending only on-device visual sign-off of the fade;
and the cross-fractal domain warp (#253) — shipped on the EscapeTimeCalculator
family (see below) — wants a visual pass on the swirl.

---

## 0. Licensing gate (READ FIRST)

- **Fracturing Fog is `AGPL-3.0-or-later`** (see the SPDX header on every
  engine source file, e.g. `Engine/Calculators/PlasmaCalculator.cs:1`).
- **Acidwarp is GPL-licensed.** A GPL-2.0-**only** upstream is *not* license-
  compatible with AGPL-3. Do **not** copy acidwarp source, lookup tables, or
  palette data into this repo.
- **Reimplement from the mathematics.** The pattern equations (distance, angle,
  sine interference, XOR fields) are not copyrightable — express them fresh in
  C#. This is a clean-room port of the *ideas*, not the code.
- **Attribution.** Credit Noah Spurrier (original concept, 1992) and Boris
  Gjenero (modern port) in the About box / mode help text. This is courtesy,
  not a license obligation, once the code is clean-room.

---

## 1. Why this is a small change (architecture fit)

Acid Warp is two mechanisms, and FF already has both surfaces:

| Acid Warp mechanism | FF equivalent that already exists |
|---|---|
| Closed-form per-pixel function → 8-bit **palette index** | Scalar field → `IColorMap` LUT (`Engine/Models/ColorUtils.cs`) |
| Animate by **rotating the color LUT** (no re-render) | `ColorOffset` phase term already in the LUT sample (`ColorUtils.cs:130`, applied at `:288` `raw = smooth * cycleSpeed * ColorDensity + ColorOffset`) |
| Non-fractal procedural field, pan/zoom is a no-op | **`Plasma`** already ships exactly this shape (`FractalType.Plasma`, `PlasmaCalculator` — "the generated field IS the image", `Enums.cs:167`) |

So the mode is: **a new procedural field calculator** (sibling of
`PlasmaCalculator`) feeding FF's **existing** color pipeline, animated by
**time-driving the existing `ColorOffset`**. No new render backend. No new
color pipeline.

Key structural facts (from `ColorTheme-Enhancement-Roadmap.md §1`):

1. The **LUT is built once per theme instance** (256 entries). Rotating the
   phase (`ColorOffset`) is free at render time — it does not rebuild the LUT.
2. Acid Warp's "cheap" claim holds in FF: compute the field **once**, then only
   the per-frame recolor runs. FF already has a cheap-recolor precedent
   (Buddhabrot #194/#197).

---

## 2. The mode — implementation slices

### AW-1 — `AcidWarpCalculator`: clean-room procedural pattern field  ☐
- **What.** New `IFractalCalculator` mirroring `PlasmaCalculator` (one-shot
  fill `ColorBuffer`, `SupportsZoom = false`). Selects one of ~40 closed-form
  patterns and writes a **normalized `[0,1)` field** sampled through the active
  `IColorMap`.
- **Pattern set (clean-room from the equations).** `dx = x - cx`, `dy = y - cy`;
  `dist = hypot(dx,dy)`, `angle = atan2(dy,dx)`:
  - Radial: `dist`; `sin(dist*4)`; `dist + sin(dist*4)`.
  - Angular: `angle`; `sin(angle*7)`.
  - Spiral: `angle + sin(dist)` and cosine-x/cosine-y blends.
  - Multi-center "peacock": sum of `sin(dist_k)` from 1–3 offset centers.
  - Wave interference: sums of multi-frequency `sin`/`cos` in x and y.
  - Bitwise: `xor(angle, dist)`, `xor(dx, dy)` (see IDEA-2).
  - Stochastic: neighbor-blended jitter fields.
  - Modern FF computes these in float directly — skip the DOS integer LUTs
    (`lut_sin`/`lut_dist`/`lut_angle`).
- **Output range.** Normalize to `[0,1)` and wrap (acidwarp reserves index 0 and
  uses 1..255; FF's LUT is 0..1 float, so wrap with `x - floor(x)`).
- **Injection.** New file `Engine/Calculators/AcidWarpCalculator.cs`. Pattern id
  + tunables (`AcidWarpPattern`, center offsets, frequencies) as
  `FractalParameters` fields (per the tunable-params convention).
- **Test.** Golden-hash a handful of patterns at a fixed size; assert field is
  in `[0,1)` and deterministic per pattern id.
- **Depends on:** nothing.

### AW-2 — `FractalType.AcidWarp` wiring + UI  ☐
- Add `AcidWarp` to `Abstractions/Models/Enums.cs`; register the calculator on
  the same path `Plasma` uses (`FractalRenderHost` selection).
- Expose pattern picker + tunables in `FractalParamsView`.
- Add to `Server/Guard/FractalTypeAllowlist.cs` (defense-in-depth).
- ASCII-only names (per the no-Unicode-names rule).
- **Depends on:** AW-1.

### AW-3 — Animated palette cycling as a first-class motion effect  ☐  *(= IDEA-1)*
- See §3 IDEA-1. This is Acid Warp's *core* and also a standalone win on every
  fractal. The mode requires it; ship it general.
- **Depends on:** nothing (but the mode leans on it).

### AW-4 — Classic Spurrier intro on first launch (per process)  ☐
- **Requirement (from the user).** The **first** time Acid Warp mode is entered
  in a given app launch, show Noah Spurrier's original into-screen *before* the
  shuffle begins.
- **Design.** Process-scoped `static bool _acidWarpIntroShown` (lifetime = the
  process; **not** persisted to disk, **not** per-mode-entry). First entry →
  force the canonical original pattern + its palette as playlist item 0
  (acidwarp's `DRAW_LOGO` / "logo only fades to black"), then hand off to the
  normal shuffle (AW-5). Reimplement that specific startup pattern + palette
  from the DOS math.
- **Naming.** ASCII, e.g. `"Acid Warp Classic (Spurrier 1992)"`.
- **Test.** Two mode-entries in one process → first yields the classic id,
  second yields a shuffled id. New process resets.
- **Depends on:** AW-1, AW-2.

### AW-5 — Auto-VJ ambient loop  ☑  *(= IDEA-6)*
- See §3 IDEA-6. Shuffle playlist + timed auto-advance + fade-to-black
  crossfade + lock-field/cycle-color. Reuses slideshow + Scene transitions.
- **Depends on:** AW-2, AW-3.

---

## 3. Ideas to steal (all six)

Each spec: **what · surfaces · algorithm · injection · back-compat · test.**
These are ranked by artful payoff. IDEA-1 and IDEA-6 double as mode slices
AW-3 / AW-5; IDEA-2..5 are independently useful on the existing fractals.

### IDEA-1 — Animate COLOR, not CAMERA (first-class palette cycling)  ☐
- **What.** FF's entire motion vocabulary today animates *geometry* (zoom / pan
  / scene camera). Acid Warp's signature is the opposite: **hold the frame,
  move the palette.** Make time-driven palette cycling a first-class,
  video/slideshow-exportable effect on **any** fractal.
- **Surfaces.** `CyclingGradientColorMap` / `GradientColorMap` phase term; Scene
  Engine param-animation; video exporter; slideshow.
- **Algorithm.** Drive `ColorOffset` (`ColorUtils.cs:130`, consumed at `:288`)
  from wall-clock: `ColorOffset += rate * dt`, wrapped by `CycleWrap`. Field /
  fractal computed once; only the recolor runs per frame → near-free.
- **Injection.** An animation driver that ticks `ColorOffset` (Scene Engine
  track or a dedicated "palette cycle" clock). Wire into export so the effect
  bakes into MP4 / slideshow.
- **Back-compat.** Rate default 0 = current static behavior. Opt-in.
- **Test.** Frame N and N+period render identically when the period aligns to
  the LUT length; recolor path does not re-invoke the calculator.

### IDEA-2 — XOR / bitwise index-field patterns & post-transform  ☐
- **What.** Acid Warp's `xor(angle,dist)` / `xor(dx,dy)` produce plaid / moiré
  fields with a demoscene / 8-bit aesthetic FF has **no analog** for.
- **Surfaces.** AW pattern set (AW-1) **and** an optional color-index
  post-transform usable on any field.
- **Algorithm.** Quantize the field (or coordinates) to integers, XOR, renormalize
  to `[0,1)`. As a post-transform: `t' = ((floor(t*N)) ^ mask) / N`.
- **Injection.** Pattern cases in `AcidWarpCalculator`; optional transform hook
  ahead of the LUT sample.
- **Back-compat.** Off by default; only active for AW patterns / when the
  transform is selected.
- **Test.** Golden-hash a known XOR pattern; verify bit-exactness across runs.

### IDEA-3 — Multi-center sine superposition + domain-warp modulation  ☑
- **What.** Sum of `sin(dist_k)` from N offset centers = **coherent** wave
  interference ("peacock"). FF's `Plasma` is *incoherent* noise — this is a
  different, orderly beauty. Bonus: reuse the same field as a **domain-warp**
  layer that displaces the sampling coordinates of an existing fractal.
- **Surfaces.** AW pattern set (AW-1); optional pre-sample coordinate warp on
  the 2D fractal path.
- **Algorithm.** `f(x,y) = Σ_k A_k · sin(ω_k · |p - c_k| + φ_k)`; as a warp,
  offset `(x,y)` by `ε·∇f` before the fractal samples.
- **Injection.** Pattern cases; a warp stage guarded behind a toggle + strength.
- **Back-compat.** Warp strength default 0 = no-op.
- **Test.** Zero strength ⇒ byte-identical to the un-warped fractal.
- **Shipped.** Multi-centre "peacock" patterns live in `AcidWarpCalculator`
  (cases 8/9). The **cross-fractal warp** is `FractalDomainWarp.Apply` — the same
  two-tap sine field lifted to a shared helper — injected as a pre-sample
  coordinate stage in `EscapeTimeCalculator`'s scalar cores (Julia, Burning Ship,
  Tricorn, Multibrot, Magnet 1/2, Glynn, Phoenix, Spider). Tunables
  `DomainWarpEnabled` / `DomainWarpStrength` / `DomainWarpFrequency` on
  `FractalParameters`; `DomainWarpStrength` is animatable (breathing swirl). An
  active warp forces the scalar path (SIMD builds one `cy` per row, which a
  per-pixel warp breaks) and skips GPU; it is gated below
  `EscapeTimeCalculator.MaxWarpZoom` (1e6). **Mandelbrot is excluded** — it runs
  on the dedicated deep-zoom SIMD/perturbation calculator, and warping that
  vectorised path is out of scope (the original deep-zoom deferral). Off /
  strength 0 stays byte-identical (`FractalDomainWarpTests`).

### IDEA-4 — Sparkle palette post-fx  ☐
- **What.** Acid Warp's `add_sparkles_to_palette` brightens every Nth palette
  entry → cheap glitter / lightning. FF has no such modifier.
- **Surfaces.** `GradientColorMap.BuildLut()` (bake into the 256-entry LUT — free
  at render time).
- **Algorithm.** For every Nth LUT entry, `rgb = min(rgb + boost, 1)`; `N` and
  `boost` tunable. Optionally phase-shift the sparkle set over time (rides
  IDEA-1) for twinkle.
- **Injection.** A post-step in `BuildLut`; new nullable `ColorThemeData` fields
  (`SparkleStride`, `SparkleBoost`) → back-compat by nullability.
- **Back-compat.** Absent / 0 = no sparkle. Hooks the Random theme generator
  (#83) as an optional experimental knob.
- **Test.** Stride 0 ⇒ LUT unchanged; stride N ⇒ exactly ⌈256/N⌉ entries lifted.

### IDEA-5 — Seamless-under-rotation palette discipline (TOGGLEABLE)  ☐
- **What.** Acid Warp palettes are built to **tile with no visible seam** when
  the phase rotates. FF has `CycleWrap` but the Random theme generator (#83)
  does not *guarantee* first-stop ≈ last-stop, so cycling can flash a seam.
- **Creative-choice requirement (from the user).** Ship this as an **opt-in
  toggle**, not a forced rule — a hard seam is sometimes the desired look.
  - Toggle **ON** ("Seamless cycling") → force / nudge the palette so
    `stop[0] ≈ stop[last]` (close the loop), so IDEA-1 cycling never seams.
  - Toggle **OFF** (default = today's behavior) → palette left as authored;
    seams allowed.
- **Surfaces.** `ColorThemeData` (new nullable `SeamlessCycle` bool); Random
  theme generator (#83); Color Theme Editor UI checkbox.
- **Algorithm.** When ON, append/adjust a terminal stop equal to the first (or
  blend the two ends within a tolerance) before `BuildLut`.
- **Back-compat.** Null / false = unchanged. Purely additive.
- **Test.** ON ⇒ `|LUT[0] - LUT[255]|` within tolerance; OFF ⇒ LUT identical to
  today.

### IDEA-6 — Auto-VJ ambient loop  ☑  *(= AW-5)*
- **What.** Acid Warp's signature loop = **lock geometry, cycle color,
  auto-advance on a timer with a fade-to-black crossfade**, drawing from a
  **shuffled, non-repeating** playlist (`makeShuffledList`). FF's slideshow
  advances *frames*; it has no "hold one image, cycle its palette, then fade to
  the next" ambient mode.
- **Surfaces.** Slideshow engine; Scene Engine transitions (fade); IDEA-1 for
  the color motion.
- **Algorithm.** Fisher–Yates shuffle over the pattern/preset pool → play in
  order (no repeats until exhausted); per item: hold + palette-cycle for
  `image_time`, then crossfade (fade-to-black) to the next. Controls: lock,
  pause, next.
- **Injection.** New slideshow "Acid Warp / ambient" playback profile.
- **Back-compat.** New mode; existing slideshow untouched.
- **Test.** Shuffle emits every id once before repeating; auto-advance fires on
  the timer; lock freezes advancement while color keeps cycling.

---

## 4. Dependency graph

```text
AW-1 (calculator) ──┬─> AW-2 (type + UI) ──┬─> AW-4 (Spurrier intro)
                    │                       └─> AW-5 / IDEA-6 (auto-VJ) ─┐
IDEA-1 / AW-3 (animate color) ─────────────────────────────────────────┘
IDEA-2 (xor fields)      ── needs AW-1 for the pattern slot
IDEA-3 (interference/warp) ─ needs AW-1 for the pattern; warp layer standalone
IDEA-4 (sparkle pfx)     ── standalone (hooks #83)
IDEA-5 (seamless toggle) ── standalone (hooks #83, pairs with IDEA-1)
```

Suggested order: **AW-1 → IDEA-1/AW-3 → AW-2 → AW-5/IDEA-6 → AW-4**, then the
standalone color wins **IDEA-4, IDEA-5** any time, **IDEA-2 / IDEA-3** after
AW-1.

---

## 5. Issue map

Filed under `AloneButUnsober/MandelbrotExplorer`. Tracking issue:
[#246](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/246).
Each slice/idea is one issue (per the repo's issue-first convention).

| Item | Issue |
|---|---|
| Tracking | [#246](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/246) |
| AW-1 calculator | [#247](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/247) |
| AW-2 type + UI | [#248](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/248) |
| AW-3 / IDEA-1 animate color | [#249](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/249) |
| AW-4 Spurrier intro | [#250](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/250) |
| AW-5 / IDEA-6 auto-VJ | [#251](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/251) |
| IDEA-2 xor fields | [#252](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/252) |
| IDEA-3 interference / warp | [#253](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/253) |
| IDEA-4 sparkle pfx | [#254](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/254) |
| IDEA-5 seamless toggle | [#255](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/255) |

To auto-close at merge, each PR needs its own explicit `Closes #N` line per
issue — ranges / mentions don't count (see the repo's PR-issue-autoclose note).
