# Color Theme Enhancement Roadmap

Tracking document for closing the feature gap between Fracturing Fog's colour
system and comparable fractal / procedural-colour tools (Ultra Fractal,
Apophysis / Chaotica, Kalles Fraktaler, Mandelbulber, matplotlib colormaps,
IQ-style shader palettes).

Status legend: ☐ not started · ◐ in progress · ☑ shipped

---

## 1. Current architecture (as-built)

The colour system has three surfaces. Any enhancement must state which it
touches.

| Surface | Code | Interpolation today |
|---|---|---|
| **JSON data-driven themes** | `ColorThemeData` → `DataDrivenColorThemes` → `GradientColorMap` | Linear sRGB only, hardcoded |
| **ColorGen DSL** (CPU + HLSL) | `ColorGenEmitter`, `ColorGenHlslPrelude`, `ColorMap.template.cs` | `palette()` = linear sRGB cyclic lerp |
| **Palette-extraction preview / PDF** | `Imaging/PaletteExtraction/GradientInterpolation.cs` | sRGB / Lab / OkLab — **NOT wired to render** |

Key injection points referenced throughout this doc:

- `Engine/Models/ColorUtils.cs`
  - `GradientColorMap.SampleStops()` — per-stop segment lerp (feeds the 256-entry LUT).
  - `GradientColorMap.BuildLut()` — builds the LUT once per theme instance.
  - `GradientColorMap.Map()` — computes `t = smooth / maxIter`, clamps, → `MapNormalized`.
  - `CyclingGradientColorMap.Map()` — `t = (smooth * CycleSpeed) % 1`.
  - `MapNormalized()` — final LUT sample + lerp → packed ARGB.
- `Engine/Models/ColorThemeData.cs` — JSON DTO. All new theme fields land here (nullable = back-compat).
- `Engine/Models/DataDrivenColorThemes.cs` — plumbs DTO fields into the runtime maps.
- `Abstractions/Models/ColorStopData.cs` — per-stop DTO (position + RGB).
- `ColorGen/Parser/ColorGenAst.cs` — `CgFunctions.Table` (builtin registry).
- `ColorGen/Emitters/ColorGenEmitter.cs` — CPU C# emission (`EmitCall`).
- `ColorGen/Emitters/ColorGenHlslPrelude.cs` — GPU HLSL helper prelude.
- `ColorGen/Templates/ColorMap.template.cs` — `Cg3` runtime struct (CPU palette/mix/gamma helpers).
- `Imaging/PaletteExtraction/GradientInterpolation.cs` + `ColorSpaces` — existing OkLab math (reusable).

Two structural facts constrain the specs:

1. **The LUT is built once per theme instance** (256 entries, byte precision).
   Interpolation-space and curve changes that only affect LUT *construction*
   are effectively free at render time — the per-pixel hot path is unchanged.
2. **`Map()` has no pixel coordinates.** Anything needing screen x/y
   (spatial dithering) requires a signature change across the `IColorMap`
   surface — that is why dithering is ranked high-risk.

---

## 2. Feature specs

Each spec: what, surfaces, data model, algorithm, injection points, back-compat, test.

### F1 — Stop interpolation space (linear / OkLab / Lab / HSV-arc)

- **What:** choose the colour space the gradient blends in. OkLab kills muddy
  mid-tones between distant hues; HSV-arc gives rainbow sweeps.
- **Surfaces:** JSON themes (all four kinds inherit `GradientColorMap`).
- **Data model:** `ColorThemeData.InterpolationSpace` (enum `Srgb|OkLab|Lab|Hsv`, default `Srgb`).
- **Algorithm:** in `SampleStops`, convert the two bracketing stops into the
  chosen space, lerp, convert back. Reuse `ColorSpaces.RgbToOkLab/OkLabToRgb`
  from `GradientInterpolation`. HSV-arc lerps hue along the shorter arc.
- **Injection:** `GradientColorMap` gains a `protected GradientInterpolationSpace Space` (default Srgb); `SampleStops` branches on it; `DataDriven*` ctors set it from the DTO. LUT already caches the result → zero per-pixel cost.
- **Gap to close first:** `GradientInterpolation.Mix` currently fakes `Lab` by
  delegating to OkLab (see comment at `GradientInterpolation.cs:66`). Either
  implement a real Lab inverse in `ColorSpaces` or drop `Lab` from the enum for v1.
- **Back-compat:** null/absent ⇒ `Srgb` ⇒ byte-identical to today.
- **Test:** golden-image compare; unit test that OkLab midpoint of `#000`↔`#fff` differs from sRGB midpoint.

### F2 — Stop interpolation curve (linear / cosine / cubic / step)

- **What:** shape of the blend within a segment. Cosine = smooth ease at both
  stops (classic demo-scene look); cubic (Catmull-Rom / monotone) = spline
  through stops; step = hard bands.
- **Surfaces:** JSON themes.
- **Data model:** `ColorThemeData.InterpolationCurve` (enum `Linear|Cosine|CubicMonotone|Step`, default `Linear`).
- **Algorithm:** remap the segment parameter `u`:
  - cosine: `u' = 0.5 - 0.5*cos(pi*u)`
  - step: `u' = 0` (hold low stop)
  - cubic: needs the neighbouring stops (4-point Catmull-Rom), so compute in `BuildLut` across the global stop list, not per-segment.
- **Injection:** `SampleStops` for linear/cosine/step; `BuildLut` for cubic (it already walks all 256 samples).
- **Back-compat:** null/absent ⇒ `Linear`.
- **Test:** unit test each curve's `u=0.5` output; cubic C1-continuity spot check.

### F3 — Transfer function on the mapping scalar

- **What:** remap `t` before palette lookup (Ultra Fractal "transfer function").
  Compresses/expands where colour detail lands. `log` spreads deep detail,
  `sqrt` lifts shadows, `pow` is a general knob.
- **Surfaces:** JSON themes (Gradient + Cycling + 3D albedo).
- **Data model:** `ColorThemeData.TransferFunction` (enum `Linear|Sqrt|Cubic|Log|Sine`, default `Linear`) + `TransferStrength` (float, default 1.0).
- **Algorithm:** apply to `t` in `GradientColorMap.Map` / `CyclingGradientColorMap.Map` before `MapNormalized`:
  - sqrt `t^0.5`, cubic `t^3`, log `log(1+k·t)/log(1+k)`, sine `0.5-0.5cos(pi·t)`.
  `TransferStrength` blends identity↔curve.
- **Injection:** the two `Map` overrides in `ColorUtils.cs`. Also mirror into the
  ColorGen template's `in_t` if desired (optional — DSL users can already write it).
- **Back-compat:** null/absent ⇒ `Linear`, strength ignored.
- **Test:** unit test monotonicity + endpoints (`f(0)=0, f(1)=1`) for every curve.

### F4 — Colour offset / phase + density (cycling)

- **What:** rotate the palette along the iteration axis (offset/phase) and
  scale how many cycles fit (density). Ultra Fractal "rotation" + "density".
- **Surfaces:** JSON Cycling / Phong3D / Pbr3D.
- **Data model:** `ColorThemeData.ColorOffset` (float `[0,1)`, default 0) + `ColorDensity` (float, default 1.0).
- **Algorithm:** `t = ((smooth * CycleSpeed * ColorDensity) + ColorOffset) mod 1`.
  (Density multiplies frequency; offset is an additive phase — distinct knobs, both cheap.)
- **Injection:** `CyclingGradientColorMap.Map` (single line change) + `DataDriven*` ctors.
- **Back-compat:** offset 0 + density 1 ⇒ identical.
- **Note:** exposes as animatable params later (ties into the scene-engine global-track work).
- **Test:** offset 0.5 shifts LUT index by 128; density 2 doubles band count.

### F5 — Repeat / wrap mode (repeat / ping-pong / clamp)

- **What:** ping-pong (mirror) removes the hard seam where a cycling palette
  wraps 1→0. Clamp holds the endpoints.
- **Surfaces:** JSON Cycling.
- **Data model:** `ColorThemeData.WrapMode` (enum `Repeat|PingPong|Clamp`, default `Repeat`).
- **Algorithm:** after computing raw cyclic `t`: pingpong = `1 - |1 - 2·frac(t/2)·? |` → use triangle wave `tri(t) = 1 - abs(1 - 2*frac(t))`... implement as `abs(((t mod 2) ) - 1)` mapped to [0,1]; clamp = `saturate`.
- **Injection:** `CyclingGradientColorMap.Map`.
- **Back-compat:** default `Repeat`.
- **Test:** ping-pong at t and (1-t) equal; continuity at seam.

### F6 — Palette gamma (post-FX default)

- **What:** per-theme gamma alongside brightness/contrast/adaptive.
- **Surfaces:** JSON themes (all) + host post-FX pipeline + UI slider.
- **Data model:** `ColorThemeData.Gamma` (`int?` on the same [-100,100] slider scale, or `float?` gamma value — pick one; slider-scale keeps JSON consistent with the other three). Extend `IThemePostFx` with `ThemeGamma`.
- **Algorithm:** `out = pow(in, 1/gamma)` on the linearised RGB in the existing post-FX stage.
- **Injection:** find the post-FX apply site (search `IThemePostFx` consumers / `Adaptive` slider handler in `UI.Avalonia`), add a gamma stage + slider + lock checkbox mirroring brightness/contrast/adaptive.
- **Back-compat:** null ⇒ no gamma stage.
- **Test:** gamma-neutral (value 1.0 / slider 0) is identity; round-trip export/import.

### F7 — Per-stop midpoint / bias

- **What:** Photoshop-style midpoint: move the 50 % blend point within a
  segment without adding a stop.
- **Surfaces:** JSON themes.
- **Data model:** `ColorStopData.Midpoint` (float `[0,1]`, default 0.5) — the bias applied to the segment *ending* at this stop (or starting; pick and document).
- **Algorithm:** remap segment `u` by a bias/gain curve: `u' = u^(log(0.5)/log(mid))` (power bias) so `u=mid → 0.5`.
- **Injection:** `SampleStops` (needs the stop's midpoint; `ColorStop` value type must carry it — add field to `ColorStop` in `ColorUtils.cs` and `ColorStopData`).
- **Back-compat:** default 0.5 ⇒ linear.
- **Test:** `mid=0.25` puts halfway colour at u=0.25.

### F8 — ColorGen: cosine palette (IQ) + interpolation-mode variants

- **What:** `cosine(t, a, b, c, d)` (Inigo Quilez 4-vector cosine palette) and
  `palette_cos` / `palette_oklab` variants of the existing stop palette.
- **Surfaces:** ColorGen CPU + HLSL.
- **Data model:** new builtins in `CgFunctions.Table`:
  - `cosine` — 4 Vec3 args (a,b,c,d) + scalar t → Vec3: `a + b*cos(tau*(c*t + d))`.
  - `palette_cos`, `palette_oklab` — same variadic signature as `palette`.
- **Injection:**
  - `ColorGenAst.cs` — register signatures.
  - `ColorGenParser.cs` — same variadic validation branch as `palette`.
  - `ColorGenEmitter.cs` — emit `Cg3.CosinePalette(...)` / `Cg3.PaletteCos(...)`.
  - `ColorMap.template.cs` — add the `Cg3` helper methods.
  - `ColorGenHlslPrelude.cs` + `ColorGenHlslEmitter.cs` — GPU parity helpers (track arity like `palette`).
- **Back-compat:** purely additive; existing programs unaffected.
- **Test:** CPU/GPU parity harness (the existing multikernel/golden compare pattern); `cosine` matches the IQ reference at sampled t.

### F9 — ColorGen: OkLab / OkLCh colour space

- **What:** `oklab(L,a,b)`, `oklch(L,C,h)` constructors + `mix_oklab(va,vb,t)`.
- **Surfaces:** ColorGen CPU + HLSL.
- **Data model:** builtins in `CgFunctions.Table` (Vec3 ctors + polymorphic mix).
- **Injection:** same five sites as F8. Port the OkLab matrices from
  `Imaging/PaletteExtraction/ColorSpaces` into the `Cg3` helper block and the
  HLSL prelude.
- **Back-compat:** additive.
- **Test:** `oklab`→RGB round-trips against `ColorSpaces`; CPU/GPU parity.

### F10 — Per-stop alpha / transparency

- **What:** RGBA stops; honour interior transparency (docs currently say
  "transparent not honored").
- **Surfaces:** JSON themes + the whole compositing path (`Map` returns packed
  ARGB with A=0xFF everywhere today; in-set is always opaque).
- **Data model:** `ColorStopData.A` (byte, default 255) + `InSetColorData.A`.
- **Algorithm:** carry alpha through the LUT (LUT would need a 4th lane) and
  stop forcing `0xFF` in `MapNormalized` / `PackArgb`.
- **Injection:** `ColorUtils.cs` LUT + pack, `IColorMap.InSetColor`, and every
  downstream consumer that assumes opaque (image capture, video, compositor).
- **Back-compat:** default 255 preserves opacity, but **the render/export
  pipeline must be audited** for premultiply / opaque assumptions — this is why
  it is high-risk.
- **Test:** alpha=128 stop composites over a known background; PNG export keeps alpha.

### F11 — Dithering (anti-banding)

- **What:** ordered/blue-noise dither on the final 8-bit quantise to kill
  visible bands in smooth gradients and deep zooms.
- **Surfaces:** JSON + ColorGen output (final pack), host.
- **Data model:** global toggle + optional per-theme `DitherStrength`.
- **Algorithm:** add a threshold from a 8×8 Bayer / blue-noise texture at
  `(x mod 8, y mod 8)` before rounding to byte.
- **Injection:** **requires pixel coordinates in the colour path.** `IColorMap.Map`
  has none — either (a) dither in a post-pass over the rendered ARGB buffer
  (cleaner; no interface change; where the post-FX stage already iterates
  pixels), or (b) thread x/y through `Map` (invasive). Prefer (a).
- **Back-compat:** off by default.
- **Test:** flat gradient histogram shows dither noise; SSIM vs undithered stays high.

### F12 — Randomize / seed palette generator

- **What:** one-click random palette in the Theme Editor (golden-ratio hue
  walk, or IQ random cosine coefficients) with a reproducible seed.
- **Surfaces:** UI only (Theme Editor) — emits ordinary stops or a ColorGen `cosine` program.
- **Data model:** none persisted beyond the generated stops (optionally store seed in `Description`).
- **Injection:** `UI.Avalonia` Theme Editor + ColorGen editor "Randomize" button.
- **Back-compat:** additive UI.
- **Test:** same seed ⇒ same palette.

---

## 3. Risk vs ROI ranking

ROI = user-visible payoff × breadth of themes affected. Risk = blast radius ×
math/precision/compat hazard. Effort in ideal-days, rough.

| ID | Feature | ROI | Risk | Effort | Score (ROI−Risk) |
|---|---|---|---|---|---|
| **F1** | Stop interpolation space (OkLab) | High | Low | 1.5 | ★★★★★ |
| **F8** | ColorGen cosine / IQ palette | High | Low | 2 | ★★★★★ |
| **F4** | Colour offset/phase + density | High | Low | 1 | ★★★★★ |
| **F3** | Transfer function | High | Low-Med | 1.5 | ★★★★☆ |
| **F2** | Interpolation curve (cosine/step) | Med-High | Low | 1.5 | ★★★★☆ |
| **F5** | Wrap mode (ping-pong) | Med | Low | 0.5 | ★★★★☆ |
| **F6** | Palette gamma post-FX | Med | Low-Med | 1 | ★★★☆☆ |
| **F9** | ColorGen OkLab/OkLCh | Med | Low-Med | 2 | ★★★☆☆ |
| **F7** | Per-stop midpoint/bias | Med | Low | 1 | ★★★☆☆ |
| **F12** | Randomize/seed generator | Med | Low | 1 | ★★★☆☆ |
| **F10** | Per-stop alpha | Med | **High** | 3+ | ★★☆☆☆ |
| **F11** | Dithering | Med | **High** | 3+ | ★★☆☆☆ |

Rationale for the two high-risk items:

- **F10 (alpha)** touches the opaque-ARGB assumption baked into the LUT, the
  packer, `IColorMap.InSetColor`, and every export/capture/video consumer.
  Wide blast radius, easy to ship subtle premultiply bugs.
- **F11 (dither)** either changes the `IColorMap.Map` signature (invasive) or
  needs a new pixel-space post-pass. The post-pass route de-risks it — reranks
  toward the middle if scoped that way.

---

## 4. Suggested phasing

**Phase A — cheap high-ROI, no interface changes (target first):** ☑ SHIPPED (2026-07-17)
☑ F1 · ☑ F4 · ☑ F5 · ☑ F8

Phase A landed the render-engine + DSL plumbing (no editor UI yet):
- **F1** — `ColorThemeData.InterpolationSpace` (`Srgb`/`OkLab`/`Hsv`); OkLab+HSV
  blend implemented self-contained in `GradientColorSpaces` (Engine, no
  PaletteExtraction dependency). Applied at LUT-build in `GradientColorMap.SampleStops` → zero per-pixel cost. `Lab` deferred (needs real inverse).
- **F4/F5** — `ColorOffset` / `ColorDensity` / `WrapMode` on `ColorThemeData`;
  shared `GradientColorMap.CyclicT(smooth, cycleSpeed)` helper consumed by
  `CyclingGradientColorMap` **and** both 3D lit bases (`GradientPhong3DBase`,
  `PbrGradient3DBase`) so every cycling kind honours them. Defaults collapse to
  the historical `((smooth*speed) mod 1)` — byte-identical.
- **F8** — ColorGen `cosine(t, a, b, c, d)` IQ palette builtin: AST signature,
  CPU emitter (`Cg3.Cosine`), HLSL emitter (native vector ops, no prelude
  helper), template runtime helper. Verified: codegen exit 0 + generated C#
  Roslyn-compiles against Engine + HLSL `cos()` emitted.
- Round-trip: all four JSON fields export via `GradientColorMap.Export*`
  accessors so user themes persist the options.
- **Not yet done (follow-up):** editor UI controls in `UI.Avalonia/`, user-doc
  worked examples, and a golden-image `--colorprobe` gate.

Rationale: F1/F4/F5 are additive nullable DTO fields + LUT-build or one-line
`Map` edits, byte-identical when defaulted. F8 is purely additive ColorGen
builtins. All four are independently shippable and each visibly upgrades output.

**Phase B — small-surface curves & knobs:**
☐ F2 · ☐ F3 · ☐ F7

Interpolation curve, transfer function, per-stop midpoint. Same DTO+LUT pattern;
F2 cubic and F7 add fields to the `ColorStop` value type.

**Phase C — DSL depth + post-FX:**
☐ F9 · ☐ F6 · ☐ F12

OkLab in ColorGen (port matrices to CPU+HLSL), gamma post-FX stage + slider,
randomize UI.

**Phase D — structural (gate behind explicit sign-off):**
☐ F10 · ☐ F11

Alpha and dithering. Do the pipeline audit first; prefer the post-pass dither
route. Consider a `--colorprobe` headless gate (mirrors existing `--kifsprobe`
/ `--inputprobe` pattern) to golden-compare colour output across the whole
matrix of options before these land.

---

## 5. Cross-cutting work

- **Editor UI:** each JSON field needs a control in the Avalonia Color Theme
  Editor (`UI.Avalonia/`). Per project rules, all new UI goes to Avalonia only;
  WinForms editor stays frozen.
- **JSON schema doc:** update `Docs/User/ColorThemeEditor-Guide.md` §14 and
  `Docs/User/ColorGen-UserGuide.md` §2.6 / reference card as features land.
- **GPU parity:** every ColorGen builtin (F8/F9) must emit both CPU C# and HLSL
  and pass the existing CPU/GPU golden-compare harness.
- **Tunable-params preference:** matches the project's standing preference to
  expose hardcoded constants as fields — these specs follow it.
- **Colourblind default:** any new error/validation UI in the editor uses
  `#FFCC00`, not red.

---

*Color Theme Enhancement Roadmap · Fracturing Fog · created 2026-07-17*
