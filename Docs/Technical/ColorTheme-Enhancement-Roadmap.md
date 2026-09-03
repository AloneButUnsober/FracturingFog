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

> **Audit 2026-07-17 (feature/ui-overhaul).** The opaque-ARGB assumption is
> confirmed baked at **two hard sites in `ColorUtils.cs`** — `PackArgb`
> (`0xFF000000 | …`) and `MapNormalized` (returns `0xFF000000 | …`) — **plus the
> two 3D pack points** (`GradientPhong3DBase`, `PbrGradient3DBase`). The same
> `0xFF`/opaque force recurs across **~104 files** (grep `0xFF000000` /
> `PixelFormat` / `Bgra8888`): every `ColorSchemes3D/*` and `ColorSchemes/*`
> theme, every calculator, all three GPU renderers (`Rendering.D3D`,
> `Rendering.Silk`, `Rendering.Skia`), and the whole export/capture/video chain
> (`ImageExport`, `PosterRenderer`, `BatchRenderer`, `SceneVideoRenderer`,
> `PngSequenceWriter`, `FractalOverlayCompositor`). Alpha is not a "carry one
> lane" change — it is a pipeline-wide compositing contract change. Real risk is
> **subtle premultiply / over-composite bugs** at any consumer that blends onto
> a background (overlay compositor, watermark, video frame accumulation) plus
> PNG-vs-video alpha-handling divergence. **Prerequisite before any F10 code: a
> `--colorprobe` golden gate** so a composited alpha result can be regression-
> checked across the option matrix. Estimate revised 3 → **5+ ideal-days**.

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

> **Audit 2026-07-17 — route (a) is wrong; it cannot deband.** Banding is born
> at exactly one place: `MapNormalized` (`ColorUtils.cs:449-453`) lerps the LUT
> to a **float** RGB, then `(int)rgb.GetElement(0)` **truncates** to byte. That
> truncation is the only quantise step, and the float sub-byte precision exists
> **only there**. The buffer handed to the post-FX upload pass
> (`FractalRenderHost.UploadProcessedBuffer`) is **already 8-bit** — Map already
> truncated. Ordered dither on an already-quantised integer is a no-op:
> `floor(V + threshold)` with `V ∈ ℤ` and `threshold ∈ [0,1)` returns `V`. So a
> post-pass over the rendered ARGB adds patterned noise but **recovers zero band
> detail** — the information is gone before the post-pass runs. Route (a) is
> dropped.
>
> To actually kill bands the dither threshold must be added to the **float**
> value *before* the `(int)` cast, i.e. inside the colour path. Coordinate
> delivery without breaking `IColorMap.Map`'s signature:
>
> - **(b1) thread-static dither offset** — render loops set a `[ThreadStatic]`
>   `GradientColorMap.DitherOffset = bayer8x8[x&7, y&7] − 0.5f` immediately
>   before each `Map` call; `MapNormalized` adds it pre-cast. No interface
>   change, thread-safe (each worker sets its own before use), CPU-only.
>   Contained: `MapNormalized` + the two 3D pack points + the CPU render loops.
> - **(b2) explicit x/y overload** — new `Map(..., int x, int y)`; invasive
>   across every calculator + the interface. Rejected (blast radius ≈ F10).
> - **GPU parity is separate.** On the GPU path `Map` is never called — colour
>   is packed in HLSL. GPU dither needs a `cg_dither(col, uv)` in the HLSL
>   prelude wired into every GPU final-pack site (`Rendering.D3D`,
>   `Rendering.Silk`, ColorGen HLSL emitter). Deep-zoom banding is worst on GPU,
>   so a *complete* F11 spans CPU **and** GPU; they can ship in that order.
>
> Revised plan: **F11a = CPU deband via (b1)** (contained, real payoff, off by
> default), **F11b = GPU HLSL dither** (separate, wider). A `--colorprobe` gate
> should assert histogram-spread on a flat gradient and SSIM parity.

### F12 — Randomize / seed palette generator

- **What:** one-click random palette in the Theme Editor (golden-ratio hue
  walk, or IQ random cosine coefficients) with a reproducible seed.
- **Surfaces:** UI only (Theme Editor) — emits ordinary stops or a ColorGen `cosine` program.
- **Data model:** none persisted beyond the generated stops (optionally store seed in `Description`).
- **Injection:** `UI.Avalonia` Theme Editor + ColorGen editor "Randomize" button.
- **Back-compat:** additive UI.
- **Test:** same seed ⇒ same palette.

### F13 — Data-driven Orbit Trap theme kind

- **Tracking:** [#589](https://github.com/AloneButUnsober/FracturingFog/issues/589). Status: ☑ P1 core + P2 editor UI shipped.
- **What:** let the Color Theme Editor (and JSON) author **orbit-trap** themes, not
  just the ~30 hardcoded C# trap classes. Pick a trap shape (point/ring/cross/
  hexagon/hyperbola/…), supply a gradient, tune the response curve.
- **Surfaces:** JSON data-driven themes + editor UI (P2).
- **Data model:** `ColorThemeKind.OrbitTrap`; new `OrbitTrapShape` enum;
  `ColorThemeData.TrapShape` / `TrapScale` (default 2) / `TrapPower` (default 0.35).
- **Algorithm / injection:** `DataDrivenOrbitTrap : OrbitTrapPowerBaseMap,
  INamedColorMap, IThemePostFx` — **delegates `InitOrbit`/`Sample` to a built-in
  shape instance** (a factory maps the shape enum → the existing concrete
  `OrbitTrap*Map`), so no SDF is duplicated; `MapWithOrbit` (inherited) maps
  `acc.TrapMin` through *this theme's* stops with the tunable `TrapScale`/
  `TrapPower`. Wire into `DataDrivenColorThemes.Create` + `Export` (an `OrbitTrap`
  case **before** the `GradientColorMap` catch-all). All the F1-F9 gradient
  machinery (stops/interp/transfer/OkLab/gamma) reuses directly — orbit-trap maps
  already extend `GradientColorMap`.
- **Back-compat:** additive kind; existing themes unaffected. Orbit-aware ⇒ carry
  a finite `MaxRecommendedZoom` (degrades at deep zoom).
- **Runtime path:** only calculators with an orbit-sampling path feed it today
  (`MandelbrotCalculator`, `UserEquationCalculator`); `EscapeTimeCalculator`
  kinds have none (out of scope, tracked separately).
- **Test:** create-from-data renders non-uniform lace; each shape resolves;
  round-trip Export→Create.
- **P2 (UI) — SHIPPED:** "Orbit Trap" kind radio + an Orbit Trap section (shape
  dropdown, TrapScale / TrapPower, "Colour interior (orbit)" checkbox) in the
  Avalonia Color Theme Editor. Threaded through the UI-neutral `ColorThemeDef`
  (new `OrbitTrapShapeDef` + `TrapShape`/`TrapScale`/`TrapPower`/`ColorInterior`
  fields) and `ColorThemeDefAdapter` (Def↔Data). Live preview + save/export
  reuse the existing `BuildColorMap`→`ToData`→`DataDrivenColorThemes.Create`
  path (which already handles the OrbitTrap kind). 🎲 Random is Kind-aware for
  OrbitTrap (random shape + scale/power + interior). Compiled-binding build
  validates every new binding; enum-parity guard test locks the Def↔Engine cast.

### F14 — Interior orbit colouring in the editor

- **Tracking:** [#590](https://github.com/AloneButUnsober/FracturingFog/issues/590). Status: ☑ runtime + editor UI + native-path interior shipped.
- **What:** expose the [#583](https://github.com/AloneButUnsober/FracturingFog/issues/583)
  interior-orbit colouring (bounded pixels coloured by the accumulated orbit) to
  editor / JSON-authored themes — an interior toggle on the data-driven Orbit Trap
  kind, mirroring the User-Equation `UserEquationColorInterior` flag.
- **Surfaces:** JSON + editor UI. **Data model:** `ColorThemeData.ColorInterior`
  (bool, default false ⇒ flat interior). **Injection:** the calculators route the
  in-set branch through `MapInteriorWithOrbit` when the theme requests it (the DSL
  path already does for its own flag — generalise the gate to the theme).
- **Back-compat:** default false = byte-identical.
- **SHIPPED (runtime):** `IOrbitAwareColorMap.WantsInteriorColor` default member
  (false); `DataDrivenOrbitTrap` re-lists `IOrbitAwareColorMap` so its public
  `WantsInteriorColor` re-implements the DIM (GOTCHA: a derived class must re-list
  the interface or the base's default wins); `ColorThemeData.ColorInterior` +
  Export round-trip; `UserEquationCalculator` gate is now
  `UserEquationColorInterior || orbitMap.WantsInteriorColor`. Also fixed an F13
  gap: `MandelbrotCalculator`'s orbit dispatch is a concrete-type switch that
  never caught data-driven orbit maps → added an `IOrbitAwareColorMap` interface
  fallback so they sample on the native path (were rendering as a plain gradient).
- **Editor UI — SHIPPED** with F13-P2: the "Colour interior (orbit)" checkbox
  lives in the editor's Orbit Trap section (`ColorThemeDef.ColorInterior` →
  adapter → `ColorThemeData.ColorInterior` → `DataDrivenOrbitTrap`).
- **Native-path interior — SHIPPED.** `MandelbrotCalculator.CalculateOrbitAware`
  reads `WantsInteriorColor` once and threads it into `ComputePixelOrbit`: the
  **bulb early-out is skipped** when on (so the accumulator actually fills), the
  in-set branch routes through `MapInteriorWithOrbit` (opaque — `StampInteriorAlpha`
  applies `InteriorAlpha` afterwards, no double-scale), and the trap/stripe/TIA
  buffers are populated for in-set pixels (0f = byte-identical when off). The
  **periodicity early-out is kept**: by the time it fires the orbit has settled, so
  the trap minimum is already captured (stripe/TIA interior averages are then a
  close approximation). NOTE (perf): with interior on, cardioid/bulb pixels now
  iterate instead of early-outing — it is opt-in, so default renders are unchanged.
  CAVEAT: the **recolor-without-recompute** paths (`RecolorFromBuffers` /
  histogram-EQ / band-dither) rebuild colour via `Map` (not `MapWithOrbit`) and so
  already don't reproduce ANY orbit theme (exterior or interior) — a pre-existing
  limitation, unchanged here; the fresh render is correct.

### F15 — ColorGen: orbit-accumulator inputs (route a)

- **Tracking:** [#591](https://github.com/AloneButUnsober/FracturingFog/issues/591). Status: ◐ MVP shipped (trapMin / stripeAvg / tiaAvg, interpreter/CPU); GPU + C# export + more accumulators/shapes deferred.
- **What:** ColorGen inputs are escape-final only, so true orbit traps / Stripe /
  SAC / TIA / curvature / Lyapunov / Gaussian / exp-smoothing **cannot** be
  written in it (only single-final-point fakes). Expose the engine's already-
  computed `OrbitAccumulator` fields as new read-only inputs — `trapMin`,
  `trapMin2`, `stripeAvg`, `tiaAvg`, `curvature`, `lyapunov`, `gaussian`,
  `expSmooth` — plus a small fixed menu of engine-sampled trap shapes.
- **Chosen route (a):** engine computes; ColorGen consumes. A program that
  references any orbit input flags the theme orbit-aware so the calculator runs
  the sampling path and binds the values. (Route b — per-iteration ColorGen
  callback — rejected: interpreter-per-iter cost + hard HLSL translation.)
- **Surfaces:** ColorGen CPU + HLSL (both emitters + prelude — parity required).
- **Injection:** `ColorGenAst` input table, `ColorGenEmitter`, `ColorGenHlslPrelude`/
  `ColorGenHlslEmitter`, and the calculator binding that fills the inputs from
  `acc`. **Note:** trap *shape* stays engine-fixed (the menu); arbitrary
  user-defined shapes would need route b.
- **Back-compat:** additive inputs; existing programs unaffected. Larger than a
  one-file change (CPU+GPU parity + accumulator wiring).
- **MVP SHIPPED (interpreter/CPU):** ColorGen inputs `trapMin` (origin point-trap
  min |z_n|), `stripeAvg` (classic SAC, density 7), `tiaAvg` (triangle-inequality
  average) added to `CgInputs.Scalars` + an `OrbitScalars` set. A program that
  references any of them is parsed to a new `InterpretedOrbitColorMap`
  (`: InterpretedColorMap, IOrbitAwareColorMap`) — the calculator's orbit-aware
  path (Mandelbrot native via the interface fallback; User-Equation via
  `as IOrbitAwareColorMap`) samples per iteration and binds the values at escape;
  `MapWithOrbit` reuses the same interpreter body. **Normal ColorGen themes are
  untouched** — only orbit-referencing programs pay the per-iteration path (two-
  type split). The orbit map advertises **no GPU palette** (escape-only HLSL can't
  produce these) so it renders on CPU; **`Generate via ColorGen` (C# export)
  rejects** orbit programs with a clear message (interpreter-only for now).
  `MapInteriorWithOrbit` inherits the default, so an orbit ColorGen theme also
  colours the interior when the calculator gate (F14) is on.
- **ACCUMULATOR MENU EXPANDED (interpreter/CPU):** added `trapCross` (nearest-axis
  trap), `curvature` (mean |Δarg|), `lyapunov` (mean log|2z|), `gaussian` (mean
  dist to nearest Gaussian integer) and `expSmooth` (mean e^{−|z|}) — raw means,
  the DSL scales them. `InterpretedOrbitColorMap` now takes the set of referenced
  orbit inputs and **computes only those per iteration** (per-input flags — the
  transcendentals aren't free), reusing the built-in themes' exact Sample maths.
- **SHAPE TRAPS ADDED (interpreter/CPU):** `trapRing`, `trapHyperbola`,
  `trapHexagon` — each an independent trap channel (`OrbitAccumulator.TrapMin3/4/5`)
  reusing the built-in shape SDFs, so a program can combine several distinct
  shape traps at once. Point + cross + ring + hyperbola + hexagon ship.
- **C# EXPORT SHIPPED:** `Generate via ColorGen` no longer rejects orbit
  programs — a new embedded `ColorMapOrbit.template.cs` emits an orbit-aware class
  (`IOrbitAwareColorMap`: `InitOrbit` + `Sample` + `MapWithOrbit`, no GPU palette)
  with a baked `const bool F_<input>` gate per referenced accumulator.
  `ColorGenApi.GenerateOrbit()` emits the DSL body into both `MapWithOrbit` (bound
  from the accumulator) and the escape-final `Map` (orbit inputs 0). A parity test
  Roslyn-compiles the export and asserts `MapWithOrbit` is bit-identical to the
  interpreter.
- **SELECTABLE TRAP-SHAPE MENU SHIPPED ([#611](https://github.com/AloneButUnsober/FracturingFog/issues/611)):**
  rather than adding 14 more fixed-shape inputs, a single **`trap`** input reads the
  slot-1 orbit-trap minimum and the theme picks its shape from the **same 19-shape
  list as the Color Theme Editor** (`OrbitTrapShape`: Point … PolarRose). The
  ColorGen editor gains a "Trap shape" ComboBox; the choice persists on the saved
  theme (`UserColorGenStore.TrapShape`) and threads through `GenerateOptions` →
  `InterpretedColorMap.TryCreate` → `InterpretedOrbitColorMap`. Non-Point shapes
  **delegate to the built-in `OrbitTrap*Map` SDFs** (the exact `DataDrivenOrbitTrap`
  factory — one source of truth), so no shape maths is duplicated. **Point is the
  default ⇒ `trap` == `trapMin`, byte-identical to pre-#611 themes.** CPU/interpreter
  only for now (the 14 non-legacy shapes have no HLSL SDF) — a `trap` theme
  advertises no GPU palette; the 5 legacy fixed-shape inputs keep their GPU path.
- **REMAINING:** GPU/HLSL orbit support (the big, cross-cutting one — see **F16**);
  GPU parity for the full `trap` shape set (needs the 19 SDFs in HLSL) + C# export
  of a `trap` theme (currently rejected with a clear message — bake shape delegation
  later).

### F16 — ColorGen orbit inputs on the GPU (HLSL) — COMPLETE (shippable scope)

- **Tracking:** [#603](https://github.com/AloneButUnsober/FracturingFog/issues/603). Status: ☑ COMPLETE for its shippable scope — shallow-escape orbit kernel on **both** backends, all 11 accumulators, C# export, on-device parity confirmed (GT 710), **enabled by default**. The three formerly-listed "remaining" tails are re-scoped as deferred / non-goals (see **DEFERRED** below) — none is a parity gap.
- **What:** make orbit-accumulator ColorGen themes render on the GPU. Today an
  orbit theme advertises **no GPU palette** (`HlslPaletteBody = ""`) so the render
  falls to the CPU; the GPU escape-time kernel only has escape-final state at the
  colour-write splice, not the whole orbit.
- **Why deferred / big:** the accumulators must be computed **inside the fractal
  iteration loop**, not in `EvalPalette` — so this reaches into the kernel, not
  just the palette. It spans:
  - `ColorGenHlslEmitter` — map the 11 orbit input names to new `EvalPalette`
    params (they currently throw / are CPU-only), and emit an HLSL Sample prelude.
  - `MandelbrotKernelSource` — accumulate the referenced traps/means per iteration
    in **all three** loops (`HlslEntry` shallow escape, `BuildPerturb`, and the SA
    perturbation variant — the last two reconstruct `z = Z[m] + δ`, so the orbit
    is available but the plumbing differs), extend the `EvalPalette` signature +
    the three colour splices (`InSet` / `Escape` / `BulbSkip`) to pass them.
  - Per-input `#define`/const gates so a theme only pays for the accumulators it
    reads (mirror the CPU `F_<input>` flags) — otherwise every GPU render eats the
    per-iteration transcendentals.
  - Both backends: `Rendering.D3D` (FXC/DXC) and `Rendering.Vulkan` (DXC→SPIR-V),
    plus `Rendering.Silk` if it grows a DSL-palette path.
  - The two-type split stays: only `InterpretedOrbitColorMap` (and the generated
    orbit class) would advertise a non-empty `HlslPaletteBody`; normal themes are
    untouched.
- **Hazards:** float-vs-double drift on the accumulators (curvature / lyapunov use
  transcendentals), the `tia` / `curvature` predecessor-state machine inside a GPU
  loop, and deep-zoom perturbation parity. Some accumulators (curvature's segment
  history) may be GPU-costly enough to keep CPU-only.
- **Test:** extend the existing multikernel/golden CPU-vs-GPU parity harness to the
  orbit inputs (`--colorprobe`-style), tolerance-compared (not bit-exact — float).
- **SLICE 1 SHIPPED (default off):** the shallow-escape kernel + wiring, all 11
  accumulators, gated behind `InterpretedOrbitColorMap.GpuEnabled` (env
  `FF_GPU_ORBIT`, default off → production byte-identical, CPU). What landed:
  - `IGpuOrbitPalette : IGpuHlslPalette` + `[Flags] GpuOrbitInputs` +
    `GpuOrbitInputOrder` (canonical order shared by mask/kernel).
  - `MandelbrotKernelSource.BuildColorOrbit(helpers, body, mask)` + `HlslEntryOrbit` —
    a shallow-escape CSMain that declares only the mask'd accumulators, samples them
    per iteration (pre-update RAW z, `it > 0` — matches CPU `Sample`), extends the
    `EvalPalette` signature with the 11 orbit params, and passes the accumulated
    means at the escape write. Shared source ⇒ **both** D3D (FXC) + Vulkan (DXC).
  - `InterpretedOrbitColorMap` implements `IGpuOrbitPalette`: when `GpuEnabled` it
    emits the HLSL body/prelude + the referenced-input mask (else `None` ⇒ CPU).
  - Both backends' `SetPalette` branch to `BuildColorOrbit` for a non-None mask.
  - `MandelbrotCalculator.TryRunGpuOrbit` — routes an orbit theme to the GPU when
    enabled + shallow (`Zoom ≤ MaxGpuZoom`) + **exterior** (interior stays CPU: GPU
    in-set uses the `isInSet=1` path, not `MapInteriorWithOrbit`); else CPU.
  - Tests: opt-in behaviour, mask = referenced inputs, per-input kernel gating, and
    **dxc compiles the generated orbit kernel to SPIR-V** for all 11 accumulators.
- **ON-DEVICE PARITY CONFIRMED:** `--vulkanorbitprobe` (Rendering.Vulkan.Smoke,
  `OrbitColorParityProbe`) drives the **production** path both ways —
  MandelbrotCalculator CPU orbit (double) vs `TryRunGpuOrbit` → `BuildColorOrbit`
  kernel (float) — and gates exterior pixels on meanDiff (≤4 channels) + disagree
  fraction (≤6%, ±8/ch). PASS on a **GeForce GT 710** across trapMin / stripeAvg /
  trapHexagon / curvature+lyapunov / gaussian+expSmooth: meanDiff **1.7–2.5**
  channels, disagree **1.4–3.9 %**. In-set excluded (interior-on-GPU is later).
- **ENABLED BY DEFAULT:** `InterpretedOrbitColorMap.GpuEnabled` now defaults on
  (set `FF_GPU_ORBIT=0` to force CPU). Landed alongside GPU compute default-on on
  the D3D11 backend (`AvaloniaShellBootstrap` sets `UseGpuCompute = true` when the
  kernel factory is present) — both fall back to CPU on any failure and toggle
  live with **Ctrl+G**.
- **DEFERRED / NON-GOALS (not parity gaps).** The three tails once listed as
  "slice-1 remaining" were re-examined against the actual CPU capability and each
  is either net-new (beyond what the CPU does), unreachable, or a non-goal — so
  building them buys nothing today. Key finding: **no perturbation-orbit path
  exists on the CPU either.** Every orbit `Sample` call site
  (`MandelbrotCalculator.ComputePixelOrbit`, `UserEquationCalculator`,
  `SandboxCalculator`) iterates in **direct `double`**, at any zoom — orbit themes
  past ~1e13 are already imprecise-by-design on the CPU, exactly like everything
  else on the direct path. There is nothing to be "in lockstep" with.
  - **Deep-zoom perturbation orbit (`BuildPerturb` / `BuildPerturbSA`)** —
    *net-new capability the CPU lacks*, not a gap. `CalculateOrbitAware` never
    perturbs; it runs the direct-double loop and accumulates there. Adding orbit
    accumulation to the GPU perturbation kernels would be a speculative feature
    with **no CPU reference to validate against**, and its only payoff (perf at
    extreme zoom) is exactly where the GPU perturbation path already aborts to CPU
    on weak FP64 (GT 710 / lavapipe). Orbit at extreme zoom stays on the CPU
    direct path (shallow-GPU gate `Zoom ≤ MaxGpuZoom` in `TryRunGpuOrbit`).
  - **Interior orbit on GPU** — *unreachable for ColorGen themes.* A ColorGen
    `InterpretedOrbitColorMap` always has `WantsInteriorColor == false` (no DSL
    syntax sets it — the F14 interior toggle lives on
    `FractalParameters.UserEquationColorInterior`, i.e. the User-Equation path).
    The calculator that *does* colour the interior from the orbit
    (`UserEquationCalculator`) has **no GPU compute path at all**. So wiring the
    GPU in-set branch to `MapInteriorWithOrbit` would colour nothing that isn't
    already covered; the `if (map.WantsInteriorColor) return false;` guard in
    `TryRunGpuOrbit` stays as correct defence-in-depth. (If a ColorGen interior
    toggle is ever added, revisit — the shallow orbit kernel already fills the
    accumulators for in-set pixels, so it would be a small follow-up then.)
  - **`Rendering.Silk` DSL-palette path** — Silk has no DSL/`IGpuHlslPalette`
    palette path, so there is nothing to extend. Explicit non-goal in #603 ("only
    if it grows a DSL-palette path"). If Silk ever gains one, the shared
    `MandelbrotKernelSource.BuildColorOrbit` drops straight in.
  - [#607](https://github.com/AloneButUnsober/FracturingFog/issues/607) tracks the
    speculative deep-zoom-orbit enhancement so it is not lost, gated behind a
    strong-FP64 GPU **and** a CPU perturbation-orbit reference to compare against
    (the real prerequisite — none exists today).

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
| **F13** | Data-driven Orbit Trap kind | High | Low-Med | 2 | ★★★★☆ |
| **F14** | Interior orbit colouring (editor) | Med | Low | 1 | ★★★★☆ |
| **F15** | ColorGen orbit-accumulator inputs | High | Med | 3+ | ★★★☆☆ |

Rationale for the two high-risk items:

- **F10 (alpha)** touches the opaque-ARGB assumption baked into the LUT, the
  packer, `IColorMap.InSetColor`, and every export/capture/video consumer.
  Wide blast radius, easy to ship subtle premultiply bugs. **Audit confirmed
  ~104 files carry the assumption; estimate raised to 5+ days (see F10 note).**
- **F11 (dither)** must add the dither threshold **before** the float→byte
  truncate in `MapNormalized` — the post-pass route (a) was found unable to
  deband (8-bit in = 8-bit out; see F11 note). The contained route is a
  thread-static offset set by the CPU render loops (F11a); GPU HLSL dither
  (F11b) is a separate, wider follow-up. F11a reranks toward the middle;
  full CPU+GPU stays high.

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

**Phase B — small-surface curves & knobs:** ☑ SHIPPED (2026-07-17)
☑ F2 · ☑ F3 · ☑ F7

- **F2** — `ColorThemeData.InterpolationCurve` (`Linear`/`Cosine`/`Cubic`/`Step`).
  Cosine/Step remap the segment parameter in `SampleStops`; `Cubic` is a
  Catmull-Rom spline through the 4 neighbouring stops in sRGB (`SampleCubic`).
  LUT-baked → zero per-pixel cost.
- **F3** — `TransferFunction` (`Linear`/`Sqrt`/`Cubic`/`Log`/`Sine`) +
  `TransferStrength`. Shared `GradientColorMap.ApplyTransfer(t)` called from
  `GradientColorMap.Map` and `CyclingGradientColorMap.Map`. Every curve fixes
  f(0)=0/f(1)=1 so cycling seams stay continuous. **Not applied to 3D albedo**
  (would move PBR material bands); the `InterpCurve` LUT effect still applies to
  3D albedo.
- **F7** — `ColorStopData.Midpoint` (+ `ColorStop.Midpoint` runtime field, +
  interop). Power-bias remap in `SampleStops` (`ApplyMidpoint`); 0/out-of-range
  ⇒ 0.5 = linear, so legacy stops are unaffected.
- Round-trip via `Export*` accessors; `FromColorStop` normalises legacy 0→0.5.
- Verified: Engine + WinExe build clean; 12/12 runtime invariant checks pass
  (transfer endpoints, step/cosine/cubic curves, midpoint bias, OkLab≠sRGB,
  ping-pong seam continuity).
- **Not yet done (follow-up):** editor UI, user worked-examples.

**Phase C — DSL depth + post-FX:** ☑ SHIPPED (2026-07-17)
☑ F9 · ☑ F6 · ☑ F12

- **F9** (commit 92c4178) — ColorGen `oklab(L,a,b)`, `oklch(L,C,h)` (h in
  radians), `mix_oklab(va,vb,t)`. Ottosson matrices in the `Cg3` template block
  + always-emitted HLSL prelude (`sign*pow(abs,1/3)` for the missing `cbrt`).
  Additive builtins, generic parser path. Verified: generated theme
  Roslyn-compiles vs Engine + HLSL prelude self-contained with parity call
  sites.
- **F6** — palette gamma, split in two:
  - *Part 1, theme-baked* (commit b9a13da): `ColorThemeData.PaletteGamma`
    (float, default 1) baked into the gradient LUT at build (`out=in^(1/gamma)`,
    all four kinds), editor Gamma slider, Export round-trip. Zero per-pixel
    cost. Verified: probe 0.5→63 / 1.0→127 / 2.0→180 at mid-grey, round-trips.
  - *Part 2, live host slider* (commit d7b9e36): `ViewState.Gamma` [-100,100] +
    a Post-FX **Gamma** slider (FloatingMenu → Main → RepaintWithPostFx). 256-
    entry byte gamma LUT in the upload pass; SIMD fast path kept for the no-
    gamma case, scalar path when gamma is active. `2^(slider/100)` exponent.
    No lock / no theme default (themes use PaletteGamma); the two gammas
    compound.
- **F12** (commit 5604a85) — editor "Random" button: golden-ratio hue walk,
  5 stops, jittered S/V, seed recorded in Description. UI-only, additive.

**Editor UI (cross-cutting):** all Phase A/B fields wired into the Avalonia
Color Theme Editor in commit e33d05a (interp space/curve/transfer + strength,
offset/density/wrap, per-stop midpoint), plus the F6/F12 controls above.

**Phase D — structural (gate behind explicit sign-off):**
☑ `--colorprobe` gate · ☐ F11a (CPU deband) · ☐ F11b (GPU dither) · ☐ F10 (alpha)

Pipeline audit **done 2026-07-17** (see the F10 / F11 notes above); it overturned
two assumptions the original phasing rested on, so the plan is re-sequenced:

1. **Prerequisite — `--colorprobe` golden gate. ☑ SHIPPED 2026-07-17.** Mirrors
   `--kifsprobe` / `--inputprobe` but is a true GATE (non-zero exit on drift, for
   CI). `Engine/Diagnostics/ColorProbe.cs`: sweeps the 21-config Gradient+Cycling
   option matrix (F1-F9/F12) through `DataDrivenColorThemes.Create` →
   `IColorMap.Map`, SHA-256 over sampled ARGB, compares to an embedded golden
   digest. `--colorprobe` (gate) / `--colorprobe regen` (re-pin after an intended
   change) / `--colorprobe verbose`. Per-config table dumped to `colorprobe.out`
   to localise drift. 3D (needs normals) + ColorGen (separate codegen) out of
   scope — the shared quantise point (`MapNormalized`) is fully exercised via
   Gradient/Cycling. Nothing structural lands without this, because both
   remaining features change pixel values in ways only a golden diff catches.
2. **F11a — CPU deband (lowest risk, real payoff).** Add the ordered-dither
   threshold *before* the `(int)` truncate in `MapNormalized`, coord delivered
   by a `[ThreadStatic]` offset the CPU render loops set per pixel. No
   `IColorMap` change. Off by default; per-theme `DitherStrength` + global
   toggle. **The roadmap's original "cheap post-pass over the ARGB buffer"
   route is rejected — it is provably a no-op on already-8-bit data.**
3. **F11b — GPU HLSL dither (separate, wider).** `cg_dither(col, uv)` in the
   HLSL prelude wired into every GPU final-pack site. Deep-zoom banding is worst
   on the GPU path, so F11 is only "done" once this ships, but it is a distinct
   unit of work behind its own sign-off.
4. **F10 — per-stop alpha (highest blast radius, do last).** ~104 files carry
   the opaque-ARGB assumption; it is a compositing-contract change, not a
   lane-add. Needs the `--colorprobe` gate plus a premultiply audit of every
   export/capture/video consumer first.

**Phase E — orbit-aware colouring surfaces (new, 2026-09):**
☑ F13 (data-driven Orbit Trap kind — P1 + editor-UI P2 shipped) · ◐ F14 (interior colouring — runtime + editor UI shipped; native-path interior in flight) · ☑ F15 (ColorGen orbit inputs — interpreter/CPU + native/DSL calculators + C# export shipped) · ☑ F16 (ColorGen orbit inputs on the GPU — shallow kernel both backends, C# export, on-device parity, default-on; deep-zoom/interior/Silk tails deferred as non-goals)

These extend the colour system past the gradient/palette family into **orbit-aware**
colouring authorable outside hardcoded C#. F13 is the keystone (reuses the whole
gradient stack via `OrbitTrapPowerBaseMap`); F14 rides on F13 + [#583](https://github.com/AloneButUnsober/FracturingFog/issues/583);
F15 is the larger CPU+GPU-parity ColorGen extension. Motivated by user testing:
orbit traps render well but are unauthorable in the editor/ColorGen, and
finalZ/interior colourings were missing on the DSL path (see also
[#588](https://github.com/AloneButUnsober/FracturingFog/issues/588)).

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
