# Out-of-bounds surround colour (#615)

## The artifact

Zoom any 2D escape-time fractal *out* far enough and the whole set shrinks to a
dot, leaving a large flat **disk** on screen surrounded by a solid colour. That
surround is normally **colour stop 0** and, before #615, could not be recoloured
apart from the fractal.

### Why the circle exists

The circle boundary **is the escape (bailout) radius drawn in the plane**. In the
escape-time loop the orbit bails when `|z|² ≥ bailout²`. A pixel whose mapped
plane coordinate already sits outside that radius escapes on the first step:

- **Mandelbrot** (seed `z0 = 0`): `z1 ≈ c`, so it escapes when `|c| ≥ R`. The
  disk is `{ |c| ≥ R }`.
- **Julia** (pixel = `z0`): escapes when `|z0| ≥ R`. The disk is `{ |z0| ≥ R }`.

Both reduce to **`|pixel plane-coordinate| ≥ R`** — a disk. Inside it the orbit
develops structure; outside, every pixel escapes immediately, lands in the lowest
smooth band, and maps to colour stop 0 — the same slot the fastest-escaping
fractal-edge pixels use, which is why the surround can't be split from the
gradient without extra work.

## Phase 1 — inline flat colour (shipped)

A dedicated, opt-in colour for the surround.

- **`IColorMap.OutOfBoundsColor`** — nullable packed ARGB. `null` (default) means
  "paint the escape gradient as before" and is byte-identical to pre-#615.
- **Geometric test** `IColorMap.IsOutOfBounds(px, py, R)` = `px² + py² ≥ R²`.
  Unifies Mandelbrot (`|c|`) and Julia (`|z0|`) — both are `|pixel| ≥ R`.
- **Calculators**:
  - `UserEquationCalculator` applies the override inline at the escape write site
    (`R = √bailout²`, default 32).
  - `MandelbrotCalculator` / `EscapeTimeCalculator` wrap `Calculate` →
    `CalculateInternal` + a single path-agnostic **post-pass** over the finished
    `ColorBuffer`. Because these calculators apply no view rotation
    (`c = centre + (px − W/2)·scale`), the post-pass mapping matches every render
    path exactly. It is self-gating: zoomed in, `|c| ≈ centre ≪ R`, so the test
    never fires. OOB pixels also get their surface normal zeroed so post-FX
    (emboss / AO) doesn't re-shade the flat surround.
- **Themes / editors**: `ColorThemeData.OutOfBoundsColor` flows through all
  `DataDrivenColorThemes` runtime classes and the `Create`/`Export` round-trip;
  persisted in `ColorThemeDef` (Color Theme Editor) and `UserColorGenEntry`
  (ColorGen editor), each with a toggle + ColorPicker.

### GPU-compute parity

No shader change was needed. The GPU compute path reads its result back into the
managed `ColorBuffer` synchronously (`GpuKernel.Run` `colorDst` memcpy) before the
post-pass runs, so the CPU post-pass covers GPU-rendered frames on both the D3D
and Vulkan backends.

One gap was fixed: `EscapeTimeCalculator` dispatches the GPU shader at a fixed
escape radius (`|z|² ≥ 4`, radius 2) while its CPU kernels bail at
`BailoutRadius2` (512²). The post-pass records the radius the frame *actually*
rendered with (`_lastBailout2`; `GpuBailout2` is a shared constant) so the
surround disk is sized correctly on both paths. Mandelbrot already dispatches GPU
with `EscapeRadius2` (512²), matching its post-pass, so it needed no change.

### Scope / caveats

- The circle is a **2D escape-time** artifact only. 3D families already composite
  a background; Newton/Halley converge (no escape disk); Buddhabrot is density —
  all out of scope.
- Exotic seeds (`z0 = f(c)`) and an active domain warp make the surround edge
  approximate (the geometric disk is unwarped). Fine for a flat fill.

## Phase 2 — background / environment compositor (deferred, #623)

Instead of a flat colour, mark out-of-bounds pixels as a distinct state
(mask / reserved alpha) and paint them from a shared `BackgroundSpec`
(flat / gradient / image / HDRI-still), reusing the public
`Interior2DBackgroundCompositor` and unifying with the 3D "ray-missed →
background" concept. Build only if a flat colour proves insufficient.

## Tests

`Server.Tests/OutOfBoundsSurroundTests.cs` — the geometric predicate; per-family
(Mandelbrot / EscapeTime / UserEquation) paint that leaves every non-surround
pixel byte-identical to the null-override baseline; null-override inertness; and
the `Create → Export` colour round-trip.

## Issue map

Parent **#615**; Phase 1 slices **#616** (interface) · **#617** (calculators) ·
**#618** (data-driven themes) · **#619** (Color Theme Editor) · **#620**
(ColorGen) · **#621** (GPU parity) · **#622** (tests + docs). Phase 2 **#623**.
