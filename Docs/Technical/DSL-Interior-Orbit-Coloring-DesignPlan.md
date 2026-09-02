# DSL Interior Orbit Colouring (Design Plan)

Tracking issue: [#583](https://github.com/AloneButUnsober/FracturingFog/issues/583)
Slices: [#584](https://github.com/AloneButUnsober/FracturingFog/issues/584) (P1 core), [#585](https://github.com/AloneButUnsober/FracturingFog/issues/585) (P2 UI)
Companion (independent): [#586](https://github.com/AloneButUnsober/FracturingFog/issues/586) fractional Escape r

> Status: **P1 DONE** · **P2 DONE** · **#586 DONE** — all in `feat/dsl-interior-orbit-coloring-583`.

## 1. Problem

Reproducing a Fragmentarium forum map in FF — `z = abs(z)*abs(tan(z)) - c^2`,
Julia `c = 0.168 - 0.744i`, seed `z0 = c` — the base structure and exterior
filaments came out right, but the large **bounded** region rendered as one flat
black fill under every available theme. Raising quality/iterations did nothing.

Root cause is **not** the DSL, the equation, or a maths limit. It is a
colouring-model gap:

- `UserEquationCalculator` already samples the orbit every iteration for
  orbit-aware themes (`IOrbitAwareColorMap.InitOrbit`/`Sample`) and colours
  escaped / converged pixels via `MapWithOrbit`.
- **In-set (non-escaping) pixels discarded the accumulator and painted a flat
  `InSetColor`** (`Engine/Calculators/UserEquationCalculator.cs`, in-set branch).

For maps whose interesting region is bounded (transcendental Julia maps, Newton /
Magnet basins) that flattens everything. Fragmentarium never classifies
interior/exterior — it colours *every* pixel by an orbit accumulator over a
fixed iteration budget, so bounded orbits produce lace. The data FF needs is
already captured (the full-orbit `acc` for in-set pixels) — it was just thrown
away.

## 2. Fix

Route in-set pixels of an orbit-aware theme through the accumulated orbit,
opt-in and byte-identical by default.

### P1 — core (#584)

1. `Engine/Interefaces/IColorMap.cs` — new default method on
   `IOrbitAwareColorMap`:
   ```csharp
   int MapInteriorWithOrbit(int iterations, in OrbitAccumulator acc)
       => MapWithOrbit(0f, 0f, iterations, 0f, 0f, in acc);
   ```
   Orbit-trap `MapWithOrbit` colours purely from `acc.TrapMin` (ignores
   `smooth`), so the smooth=0 default already yields correct interior lace;
   stripe / TIA themes use their accumulated sums. Themes wanting a distinct
   interior look override it.
2. `Abstractions/Models/FractalParameters.cs` — `bool UserEquationColorInterior`
   (default `false`), copied in `Clone()`.
3. `Engine/Calculators/UserEquationCalculator.cs` — capture
   `colorInterior = UserEquationColorInterior && orbitMap != null` once per
   render; in the in-set branch paint `orbitMap.MapInteriorWithOrbit(maxIt, acc)`
   (alpha-scaled by `InteriorAlpha` for #382 parity) when set, else the flat
   `inSet`. Default off ⇒ byte-identical.

Tests: `Server.Tests/DslInteriorOrbitColoringTests.cs` — flag-off flat interior;
flag-on orbit theme colours interior non-uniformly + opaque; flag-on non-orbit
theme is a no-op; `InteriorAlpha` scales the interior colour; `Clone` round-trip.

### P2 — UI (#585)

- `UserEquationView.axaml` — "Colour interior (orbit)" checkbox next to Escape r.
- `UserEquationViewModel.ColorInterior` over `_params.UserEquationColorInterior`,
  re-renders on change. Persistence rides `FractalParameters`.

### #586 — fractional Escape r

`UserEquationView.axaml` Escape-r `NumericUpDown`: `FormatString="F0"` →
`"F2"`, `Increment="8"` → `"0.5"`. The VM property and `FractalParameters.
EscapeRadius` were already `double`; only the editor forced integers.

## 3. How to use (the forum map)

Julia, `c = 0.168 - 0.744i`, seed `z0 = c`,
equation `fold(z)*fold(tan(z)) - c^2`, a small fractional Escape r (e.g. 1.5–3),
theme = **Orbit Trap** (or Stripe / TIA), **Colour interior (orbit)** on. The
bounded region fills with orbit lace instead of flat black.

## 4. Out of scope (separate issues if pursued)

- Interior orbit colouring on the built-in `EscapeTimeCalculator` kinds
  (Julia/BurningShip/…) — that path has no orbit sampling at all; larger.
- `SandboxCalculator` DSL-path parity.
- Attracting-cycle interior themes (`IInteriorAwareColorMap`) are Mandelbrot-only
  and assume hyperbolic cycles; transcendental maps have none, so they are not
  the vehicle here.
