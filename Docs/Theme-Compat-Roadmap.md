# Theme / Fractal Compatibility Roadmap

Companion to `Fractal-Expansion-Roadmap.md`. Tracks work to eliminate broken
Slideshow / Video Slideshow renders caused by incompatible
(theme, fractal-type, zoom) combinations, and to expose intelligent
theme filtering / sorting to the user.

## Motivation

Not all `IColorMap` themes work with every `FractalType` or zoom depth.
Today the slideshow filter is **zoom only** — `ColorPalette.GetPaletteNamesForZoom`
([Engine/Models/ColorPalettes.cs:669](../Engine/Models/ColorPalettes.cs)).
The fractal type is ignored, which produces the following failure modes:

- **Orbit-trap themes** are wired only into `MandelbrotCalculator`. On
  every other fractal the default `IColorMap.Map(...)` fallback runs,
  collapsing the image to a flat smooth-count gradient (often a uniform
  black or one-band render at deep zoom).
- **Interior themes** (CyclePeriod, AtomDomains, Multiplier, Argument,
  FakeDE) need Brent cycle detection — only `MandelbrotCalculator`
  exposes that. Elsewhere they paint the interior with whatever the
  default 3-param `Map` returns at `iter = maxIter`, typically solid
  black.
- **Phong3D / Pbr3D / Lambert / Slope** need surface normals. IFS,
  LSystem, Attractor, DLA, Apollonian, Flame, Plasma do not supply
  `nx, ny` (they call the 3-param overload). 3D themes look flat or
  monochromatic on these fractals.
- **Distance-field themes** need calculator-supplied DE. Only the DE
  fractals (Mandelbulb, Kleinian, Mandelbox, Quaternion*, KIFS,
  Bicomplex, and the holomorphic 2D set) supply it.

## Calculator capability matrix

| Path | Calculators | Data supplied |
|---|---|---|
| 3-param only | IFS, LSystem, Attractor, DLA, Apollonian, Flame, Plasma | smooth |
| 5-param (DE normals) | Mandelbulb, Kleinian, Mandelbox, QuaternionJulia, QuaternionMandelbrot, KIFS, Bicomplex, UserBulb, Sandbox, UserEquation, TearDrop | smooth + nx,ny |
| 9-param + orbit + interior | MandelbrotCalculator (+ EscapeTimeCalculator family) | smooth + nx,ny + finalZ + dz/dc + orbit accumulator + interior cycle |

Source: grep of `ColorMap.Map(` call sites in `Engine/Calculators/*.cs`.

## Design summary

Single source of truth: a `FractalCapabilities` flags enum (lives next
to `FractalType` in `Abstractions/Models/Enums.cs`) plus a static
`FractalCapabilityMap.For(FractalType)` lookup. A theme's *required*
capabilities are derived from its `ColorMapFeatures` flags + the
`IOrbitAwareColorMap` / `IInteriorAwareColorMap` marker interfaces.
Compatibility is a single bitmask test: `(required & ~supplied) == 0`.

```csharp
[Flags] public enum FractalCapabilities {
    None              = 0,
    SuppliesNormals   = 1 << 0,
    SuppliesDE        = 1 << 1,
    SuppliesOrbit     = 1 << 2,
    SuppliesInterior  = 1 << 3,
    SuppliesFinalZ    = 1 << 4,
    SuppliesDerivative= 1 << 5,
    SuppliesHistogram = 1 << 6,
}
```

`ColorPalette.GetPaletteNamesFor(FractalType, double zoom)` combines
the existing zoom cap with the new compatibility predicate. Slideshow
and Video Slideshow call this instead of `GetPaletteNamesForZoom`. The
predicate falls back to the unfiltered zoom list if the intersection
goes empty (no zero-pool failure mode).

## Ship phases

### P1 — `FractalCapabilities` table — **status: done**

- Add `FractalCapabilities` flags enum in `Abstractions/Models/Enums.cs`.
- Add `FractalCapabilityMap` static class with `For(FractalType)` switch
  covering every value in `FractalType`.
- No call sites yet — pure data.

**Files touched:** `Abstractions/Models/Enums.cs` (+ new file possible).
**Risk:** zero. Pure addition.

### P2 — Compat filter + slideshow call-site swap — **status: done**

- `ColorPalette.IsCompatible(IColorMap, FractalType)` — derives
  required caps from features + interface tags, single bitmask test.
- `ColorPalette.GetPaletteNamesFor(FractalType ft, double zoom)` —
  combines zoom cap with compat predicate; falls back to
  `GetPaletteNamesForZoom(zoom)` if intersection is empty.
- Swap [Slideshow.cs:299](../Slideshow.cs) and
  [VideoZoom.cs:1616](../VideoZoom.cs) to call the new helper.

**Files touched:** `Engine/Models/ColorPalettes.cs`, `Slideshow.cs`,
`VideoZoom.cs`.
**Risk:** low. Fallback path preserves "never empty pool" invariant.

### P3 — Ring-buffer randomization — **status: done**

- Replace immediate-repeat `lastThemeIdx` int with a bounded
  `Queue<int>` of depth `min(8, pool.Count - 1)`.
- Same for region picks (alternative: keep `regionsUsed[]` sweep — its
  exhaustive policy is already non-repeating).
- Bounded retry count (`tries < 24`) so an exhausted pool never spins.
- Optional `SlideshowSeed` in `SlideshowSettings` for repeatable demos.

**Files touched:** `Slideshow.cs`, `VideoZoom.cs`,
`Engine/Models/SlideshowSettings.cs` (optional seed).
**Risk:** low. O(1) bounded retries, no allocations per pick.

### P4 — UI: ByFractalCompat sort mode — **status: done**

- Add `ByFractalCompat` value to
  [Views/Controls.cs ColorComboSortMode](../Views/Controls.cs):190.
- Add `FractalType CompatFor { get; set; }` on `ColorComboSortState`.
- Context menu item "Compatible with current fractal" toggling the
  mode; updates `CompatFor` from the active fractal type.
- (Optional) Default mode still shows everything but visually demotes
  (italic / dim suffix) names that are incompatible — non-blocking.

**Files touched:** `Views/Controls.cs` plus the Avalonia equivalent
(`UI.Avalonia/Views/`) once located.
**Risk:** low. Pure UI; no calculator changes.

### P5 — Sandbox / UserEquation orbit-aware — **status: done**

- Wire orbit accumulator (`OrbitAccumulator`) into the scalar iteration
  loops of `SandboxCalculator` ([Engine/Calculators/SandboxCalculator.cs](../Engine/Calculators/SandboxCalculator.cs))
  and `UserEquationCalculator` ([Engine/Calculators/UserEquationCalculator.cs](../Engine/Calculators/UserEquationCalculator.cs)).
- Dispatch to `IOrbitAwareColorMap.MapWithOrbit` at escape (mirror
  `MandelbrotCalculator`'s orbit path).
- Bump `FractalCapabilityMap.For(...)` entries for `Sandbox` and
  `UserEquation` to include `SuppliesOrbit`.
- UserBulb is **out of scope** — 3D ray-march doesn't expose a per-iteration
  z that's meaningful at the surface level.

**Files touched:** `SandboxCalculator.cs`, `UserEquationCalculator.cs`,
`Abstractions/Models/Enums.cs` (capability map).
**Risk:** medium. Extra per-iteration call cost — gate on
`ColorMap is IOrbitAwareColorMap` so non-orbit themes pay nothing.

### P6 — Per-region curated theme list — **status: done**

- Add `List<string>? CuratedThemes` field on `FractalRegion`
  ([Engine/Models/FractalRegion.cs](../Engine/Models/FractalRegion.cs)).
- Slideshow uses curated pool first; falls back to compat-filtered;
  falls back to unfiltered. Three-tier chain, never empty.
- JSON ships with `JsonIgnoreCondition.WhenWritingNull` so legacy
  regions stay clean.

**Files touched:** `FractalRegion.cs`, `Slideshow.cs`, `VideoZoom.cs`.
**Risk:** low. Optional field, fully back-compatible.

## Acceptance criteria

- Slideshow no longer renders a flat / solid-color frame because the
  random theme was incompatible with the active fractal type.
- Right-click on the Color Theme combo offers a "Compatible with
  current fractal" option that hides themes which would render flat.
- Theme repetition window inside a single slideshow session is at
  least 8 picks deep (configurable via depth constant).
- Sandbox and UserEquation fractals support orbit-trap themes after
  P5 lands.
- No new dialog / no new delay introduced anywhere in the slideshow
  hot path.

## Related code

- Theme metadata: [Engine/Interefaces/IColorMap.cs](../Engine/Interefaces/IColorMap.cs)
- Theme registry: [Engine/Models/ColorPalettes.cs](../Engine/Models/ColorPalettes.cs)
- Existing recommender (Sandbox/UserEquation only): [Engine/Models/ThemeRecommender.cs](../Engine/Models/ThemeRecommender.cs)
- Combo build: [Views/Controls.cs](../Views/Controls.cs)
- Slideshow loop: [Slideshow.cs](../Slideshow.cs)
- Video Slideshow loop: [VideoZoom.cs](../VideoZoom.cs)
- Fractal type enum: [Abstractions/Models/Enums.cs](../Abstractions/Models/Enums.cs)
