# Random Space Filling of the Plane — Dev Plan

Tracking: **#331** (parent) · slices **#332** (P1) / **#333** (P2) / **#334** (P3) /
**#335** (P4) · origin request **#324**.

## Goal

Implement Paul Bourke's *random space filling of the plane* as a new 2D packing
fractal type, `FractalType.RandomTile`, with full color-theme / Relief3D / 3D /
volumetric support.

Reference & credit: **Paul Bourke, "Random space filling of the plane"** —
<https://paulbourke.net/fractals/randomtile/>. Full accreditation and a
bibliography entry are delivered in P4 (#335), per the #324 requirement.

## Algorithm

Fill the plane with non-overlapping shapes of **decreasing** size:

- Shape `i` radius follows a power law: `r_i = rMax / (i + k)^(1/α)`, where `α`
  (`RandomTileSizeExponent`) controls the size falloff — larger `α` yields a few
  big shapes plus many tiny ones; `k` is a small offset preventing a singular
  first radius.
- Each shape draws a random position (seeded RNG); accept if it does not overlap
  any previously placed shape (plus an optional `RandomTileGap` margin),
  otherwise retry up to `maxAttempts`, then skip.
- Stop when `r_i` falls below the sub-pixel floor (`RandomTileMinPixelRadius`)
  or the shape count (`RandomTileCount`) is reached.

Determinism: a single RNG seeded by `RandomTileSeed` drives every position draw
in a fixed order, so `(Width, Height, Seed, Count, SizeExponent, Gap)` uniquely
determines the output — mirrors `DlaCalculator`'s determinism contract.

### Overlap test — performance crux

Naive all-pairs overlap is `O(N²)`. Placement uses a **uniform spatial-hash grid**
(bucket by world cell, test only the placed shapes in the candidate's
neighbourhood) → ~`O(N)`. This is the same acceleration `DlaCalculator` already
uses for its aggregate-proximity test; not optional at high `Count`.

## Template

Forked from `Engine/Calculators/ApollonianCalculator.cs`. Apollonian is the
existing precedent for a **non-escape-time 2D packing rasterizer** that already
implements `IHeightFieldSource`:

- fills `uint[] ColorBuffer` directly (biggest-first paint so smaller shapes nest
  on top),
- synthesises geometric relief into `float[] SmoothBuffer` (sphere-cap dome per
  disk) — so **Relief3D, 3D themes and volumetric ride for free** through the
  existing `FractalRenderHost` `IHeightFieldSource` gate,
- paints via `IColorMap.Map(t, …, nx, ny)` with a per-pixel surface normal (lit
  dome path) or a flat single-colour fast path when relief is 0.

`RandomTileCalculator` reuses this paint machinery verbatim for circles.

## Parameters (`Abstractions/Models/FractalParameters.cs`)

| field | default | meaning |
|---|---|---|
| `RandomTileCount` | 4000 | max shapes to place |
| `RandomTileSizeExponent` (α) | 1.6 | size falloff exponent |
| `RandomTileSeed` | 1 | RNG seed (determinism) |
| `RandomTileGap` | 0.0 | margin between shapes (world fraction) |
| `RandomTileMinPixelRadius` | 0.75 | sub-pixel stop floor |
| `RandomTileColorByIndex` | true | palette by placement index vs. log-radius |
| `RandomTileRelief` | 1.0 | dome relief amplitude (0 = flat fast path) |
| `RandomTileShape` *(P3)* | Circle | Circle / Square / Triangle |

## Registration touchpoints

Every point where `Apollonian` is wired (use it as the checklist):

- `Abstractions/Models/Enums.cs` — `FractalType.RandomTile`.
- `Abstractions/Models/FractalParameters.cs` — fields + `Clone()`.
- `Abstractions/Models/FractalCapabilities.cs` — motion class.
- `Engine/Rendering/FractalRenderHost.cs` — backing field, init, colormap set,
  `IHeightFieldSource` support gate, dispatch, resize, params assign, getcalc.
- `Engine/Models/FractalView.cs` — short-name map.
- `UI.Avalonia/ViewModels/MainViewModel.cs` — menu label.
- `Engine/Models/FractalRegion.cs` — nullable persist fields + mapping.
- `UI.Avalonia/ViewModels/FractalParamsViewModel.cs` — backing fields,
  `IsRandomTile`, relief-capable lists, public clamped props.
- `UI.Avalonia/Views/FractalParamsView.axaml` — `IsRandomTile`-gated panel.

## Phases

- **P1 (#332)** — core calculator (circle-only, grid-reject, determinism) + full
  registration + one preset. Renders 2D. `--randomtileprobe` headless gate.
  `Server.Tests/RandomTileTests.cs`.
- **P2 (#333)** — verify SmoothBuffer feeds Relief3D + volumetric on-device;
  relief UI knob. Mostly test (relief free from P1).
- **P3 (#334)** — square/triangle shapes + prism relief profiles + shape dropdown.
- **P4 (#335)** — docs finalized + Bourke accreditation & bibliography. Closes #324.

## Risks

| risk | severity | mitigation |
|---|---|---|
| `O(N²)` overlap at high count | low | uniform spatial-hash grid (DLA pattern) |
| determinism drift | low | single seeded RNG, fixed draw order (DLA pattern) |
| placement starvation at high density | low | cap `maxAttempts`, accept partial fill |

No perturbation / deep-zoom / GPU-kernel surface — self-contained, low risk.

## Tests

`Server.Tests/RandomTileTests.cs`:

- determinism — identical seed → byte-identical `ColorBuffer`;
- monotonicity — higher `Count` → more painted pixels;
- min-radius floor honoured;
- relief — `SmoothBuffer` non-zero and feeds the height-field probe (clone
  `ApolloReliefProbeTests`).

## Bibliography

- Paul Bourke. *Random space filling of the plane.*
  <https://paulbourke.net/fractals/randomtile/>. (Finalised in P4 / #335.)
