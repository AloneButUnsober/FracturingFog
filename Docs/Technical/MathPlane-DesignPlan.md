# Math Plane (interactive Cartesian plotter) — Design Plan

**Tracking issue:** [#1547](https://github.com/AloneButUnsober/FracturingFog/issues/1547).
**Status:** design doc. No code lands until this doc is reviewed. The slices are
filed as issues (§9), and the issues are the canonical task list.

**Related docs:** [Equation-Language.md](Equation-Language.md) (the shared equation
front end this plan binds to); [Indras-Pearls-2D-DesignPlan.md](Indras-Pearls-2D-DesignPlan.md)
(another 2D primitive plotter); [Resources-Bibliography.md](../Resources-Bibliography.md).

---

## 1. Purpose

A new render mode, **Math Plane**: a pan/zoom Cartesian plane where the user plots
points and vectors, types equations, and applies maps to them, then watches the
result animate. The model is the explanatory style of 3Blue1Brown's videos, most
directly the grid-under-a-linear-map view of *Essence of linear algebra* ch. 3
[(Sanderson 2016)](../Resources-Bibliography.md#sanderson-linear-transformations).
Those videos are made with Manim, a *scripted* animation engine
[(Manim)](../Resources-Bibliography.md#manim). Math Plane is **live editing first**,
and scripted scenes come later (§8).

**Lane: "the math behind fractals".** This is not a general computer algebra system
or graphing calculator. Desmos and GeoGebra already cover that ground
[(prior art)](../Resources-Bibliography.md#desmos-geogebra). Every feature here should
help explain something FF renders: complex multiplication as rotate-and-scale, `z²`
doubling angles, orbits of `z² + c`, fixed points and their stability, cobweb
diagrams of real maps, linear maps as the local picture of a map (Jacobian), and the
plane under a complex map. What FF adds is the **bridge** (S7): the same plane laid
over a live fractal, with the orbit of the pixel under the cursor drawn on it.

User decisions (2026-10-29): stay in the "math behind fractals" lane, not a CAS;
interactive/live first, full scene support later; a **render mode inside FF**, not a
standalone app; **3D is a later follow-up**, started only after the 2D version ships.

### In scope (2D epic)

- Plane with adaptive axes, grid and tick labels; pan/zoom.
- Objects: points, vectors (arrow from origin or from a point), segments, polylines.
- Curves: explicit `y = f(x)`, parametric `(x(t), y(t))`, polar `r = f(θ)`,
  implicit `F(x, y) = 0` (and inequality shading).
- Maps applied to the plane and its objects: a 2×2 matrix, or a complex map `f(z)`.
  An animation parameter `t ∈ [0, 1]` interpolates identity → map.
- Linear-algebra read-outs: basis vectors î ĵ, determinant as signed area,
  real eigenvectors, rotation angle and scale (magnitude) of a vector.
- Complex read-outs: `|z|`, `arg z`, product of two vectors as complex numbers.
- Fractal bridge: orbit plot, cobweb diagram, fractal backdrop, overlay on any 2D view.
- Full `--batch` / Command builder parity (project rule), presets and help.

### Out of scope

- Symbolic algebra (simplify, solve, differentiate symbolically), statistics,
  sliders-for-everything. Desmos/GeoGebra territory.
- 3D (later epic, §8).
- Timeline/keyframed multi-step scenes beyond the single `t` parameter (later, §8).

---

## 2. What ships today (precedents to build on)

| Need | Already in FF | Where |
|---|---|---|
| Cartesian grid overlay | CPU grid + labels drawn into the BGRA buffer with SkiaSharp | `Engine/Rendering/FractalOverlayCompositor.cs` (`DrawGrid`) |
| Equation parsing | One front end, per-language builders (`IEquationBuilder<T>`) | `Equations/EquationFrontEnd.cs`, `Equations/SandboxExpression.cs` (#1101) |
| Fast evaluation | Typed Expression-tree compiler | #1110 |
| Real-space 2D primitive render as a FractalType | Billiard Rays: plane mapping, y-up world, deterministic raster | `Engine/Calculators/BilliardRaysCalculator.cs` |
| Recursive 2D primitives with zoom/pan | Apollonian / Indra's Pearls | `Engine/Calculators/ApollonianCalculator.cs` |
| Drag-to-edit objects in world space | Billiard inset: drag disks, wheel resize, double-click add | `UI.Avalonia/Controls/BilliardRayInsetControl.cs` (#1504) |
| Variable-length object list in a region | Custom disk layout as one string param | `FractalParameters.BilliardCustomDisks` → `RegionFractalParams` |
| Animating a parameter → video | Animatable-params map + keyframes + video export | `Abstractions/Animation/FractalAnimatableParamsMap.cs` |
| Orbit of a pixel | Billiard ray-path inset traces one pixel's trajectory | `BilliardRayInsetViewModel` (#1447) |

The grid overlay is the closest thing to a plotter FF has. It is a decoration on
other views; Math Plane makes the plane the subject.

---

## 3. Architecture

### 3.1 A FractalType that rasterises vectors

`FractalType.MathPlane` with a `MathPlaneCalculator : IFractalCalculator`
(`SupportsZoomPan = true`). `Calculate` draws the scene into `ColorBuffer` with
SkiaSharp, the same way `FractalOverlayCompositor` already wraps the BGRA buffer in
an `SKCanvas`. Why a raster calculator and not an Avalonia overlay:

- **Batch parity for free.** The headless renderer, poster export, video and
  slideshow all already drive `IFractalCalculator`. An Avalonia-only overlay would be
  a live-only feature, which the batch rule forbids.
- **The native surface hides Avalonia overlays.** `GpuSurfaceControl` is a native
  HWND that the OS composites above Avalonia controls (see the compositor header), so
  the overlay must be in the texture anyway.
- **Determinism.** Skia CPU raster at fixed AA settings is reproducible for a given
  scene, so live, batch and export match (same contract as Billiard Rays).

Cost is low. A typical scene is a few thousand line segments, and Skia draws that in
a few milliseconds at 1080p. No GPU kernel is needed for the 2D epic. The exception
is the per-pixel backgrounds (implicit-curve shading, phase portrait), which are
per-pixel loops (§3.4).

View mapping: the standard 2D plane mapping as Billiard Rays uses it, **y-up**, with
"one unit" a real coordinate unit so `(1, 0)` is î.

### 3.2 Scene model

```
MathScene
  Objects : list of MathObject   (id, kind, label, colour, visible, style)
    Point(x, y)           Vector(x, y, from?)     Segment(a, b)
    Curve(kind = Explicit | Parametric | Polar | Implicit, source text, domain)
  Map : None | Matrix(a, b, c, d) | Complex(source text, e.g. "z^2 + c")
  T   : double in [0, 1]   (animation / interpolation parameter)
  Display flags : axes, grid, labels, basis, determinant, eigenvectors, backdrop
```

Persisted as **one versioned text param** `MathPlaneScene` on `FractalParameters`,
the same route as `BilliardCustomDisks`. That puts it in the region snapshot
(`RegionFractalParams`), so `--param MathPlaneScene=...` works for batch, and region
recall/#961 coverage come with it. Scalars that should animate (`MathPlaneT`, matrix
entries, a picked `c`) are **separate** numeric params so the existing
`FractalAnimatableParamsMap` keyframes them. Video export of a transform then works
before any scene engine work.

Format: a small line-based text form (one object per line, `point A 1 2`,
`curve f explicit "sin(x)" -10 10`). It is readable in a `--param` value and diffable.
The parser rejects unknown keys with a position, and stays separate from the equation
language. Escaping rules for quotes are fixed in S1 with a round-trip test.

### 3.3 Expressions: a third binder on the shared front end

`EquationFrontEnd` owns syntax; each language supplies an `IEquationBuilder<T>`.
Math Plane adds a **plot builder** with real variables `x, y, t, theta` and
complex `z, c`, the existing function table (sin, cos, exp, log, abs, sqrt, pow …),
and the existing *Did you mean* diagnostics. No new grammar, so the 2D and bulb golden fingerprints
(`ParserBehaviourSnapshotTests`) stay untouched.

*As built (S3, #1550):* `Equations/PlotExpression.cs` builds the 2D language's own
AST (`SbxNode`) and evaluates it with the 2D interpreter, so the function
semantics are exactly the 2D language's. A result whose imaginary part is not
negligible is NaN, a gap in the curve. The typed Expression-tree compile (#1110)
was planned but not used. A curve costs a few thousand evaluations per view
(adaptive sampling), and the interpreter handles that well within a frame. It
stays an option if per-pixel plot work (S4 shading, S6 phase portrait) turns
out slow. Only ASCII `theta` is accepted (no `θ`), following the ASCII-names rule.

### 3.4 Curve algorithms

- **Explicit / parametric / polar:** adaptive sampling. Bisect a parameter interval
  where the bend angle or screen distance between neighbours exceeds a tolerance, up
  to a depth cap. Break the polyline at non-finite values and at jumps larger than the
  view height (asymptotes of `tan x`, `1/x`) so no vertical connector is drawn.
- **Implicit `F(x, y) = 0`:** marching squares on a screen-resolution lattice
  [(marching squares, the 2D form of Lorensen & Cline's marching cubes)](../Resources-Bibliography.md#lorensen-cline-marching-cubes).
  Saddle cells are resolved by the cell-centre value. Inequalities (`F < 0`) shade by
  per-pixel sign, the same pass.
- **Phase portrait (S6):** per-pixel colour by `arg f(z)` (and optionally `|f(z)|`
  contours), in the style of Wegert's phase portraits
  [(Wegert 2012)](../Resources-Bibliography.md#wegert-visual-complex-functions).
  Per-pixel loops run row-parallel on CPU, with a fixed order per row for determinism.
  *As built (S6, #1553):* the scene's `phase on|contours` colours each pixel z by
  `arg f(z)` of the scene's `map "f(z)"`, whether or not the map is applied to the
  grid. The wheel blends six Okabe–Ito colours, with no red–green axis; it is FF's own
  construction. `contours` steps the brightness where |f| crosses a power of 2. A zero,
  pole or non-finite value shows the plain background.

### 3.5 Maps and the animation parameter

- **Matrix `M`:** draw the grid lines, basis vectors and objects at
  `(1 − t)·I + t·M`. This is the straight-line interpolation 3B1B-style grid morphs
  use. A pure rotation interpolates poorly that way (the grid shrinks mid-way), so
  offer a second mode, **polar interpolation**: split `M = R·S` (polar decomposition),
  interpolate the angle of `R` and the entries of `S`. The user picks the mode.
  *As built (S5, #1552):* for 2×2 with det M > 0 the rotation angle is
  `atan2(c − b, a + d)` and `S = R(−θ) M`. det M ≤ 0 has no rotation-only
  factor, so polar falls back to linear, and the section says so. A pure rotation keeps
  det = 1 in polar; in general det along the polar path is
  det((1 − t) I + t S), which need not be monotone.
- **Complex `f(z)`:** grid lines are sampled as polylines (adaptive, as above) and
  each sample is mapped by `(1 − t)·z + t·f(z)`. Read-outs at a hovered point:
  `f'(z)` as a local rotate-and-scale (the bridge back to linear maps).
  *As built (S6, #1553):* `MathPlaneMapMode = Complex` applies the scene's `map "f(z)"`
  (plot language in `z` and `c`; `c = MathPlaneCRe + i·MathPlaneCIm`, both animatable).
  Grid lines come from a square domain round the view (half the view diagonal each
  way) and are sampled with the curve sampler in the image, so bends are refined and
  poles break the line. Segments and polyline edges are sampled the same way; curves
  are refined in the image; implicit curves are traced in the domain and mapped.
  Regions stay where they are: a non-linear map has no inverse to test pixels
  against. det and eigenvectors are matrix read-outs and show a note instead.
  `f'(z)` is a central difference along the real axis. The pinned read-out is the
  scene's `probe x y`: a small square at z and its true image, with `|f'|` and
  `arg f'`. The section also shows `f'(z)` at the pointer, read as a point of the
  plane (as the phase portrait is). Dragging under a complex map finds the preimage
  by Newton's method from the dragged handle, so a drag stays on one branch.
- Read-outs: `det M` (signed area of the unit square's image, shaded), real
  eigenvectors as lines through the origin, and `|v|`, `arg v` for the selected
  vector. Labels are drawn in screen space, so they do not shear.

### 3.6 The fractal bridge (what makes it FF)

- **Orbit plot:** for a picked `c` (draggable point), draw `z₀ = 0, z₁, z₂, …`
  under `z → z² + c`, or the active complex map, as connected points coloured by
  step, with the escape circle `|z| = 2`. Period-n cycles become visible as closed
  polygons.
- **Cobweb diagram:** for a real map `x → f(x)` (default `x² + c`, c real), draw `y = f(x)`,
  `y = x`, and the cobweb staircase. This is the standard picture behind the
  period-doubling route the real axis of the Mandelbrot set shows
  [(cobweb plot)](../Resources-Bibliography.md#cobweb-plot).
- **Fractal backdrop:** Math Plane can draw any 2D escape-time family's render under
  its objects (the backdrop calculator renders first, then the scene draws on top),
  so the orbit of `c` is drawn on the Mandelbrot set itself.
- *As built (S7 part 1, #1554):* the orbit (scene `orbit N`), cobweb (scene
  `cobweb x0 [N] ["f(x)"]`) and backdrop (param `MathPlaneBackdrop`) are drawn in the
  plain plane; a matrix or complex map does not move them. The orbit uses the scene's
  `map` when it has one, else `z² + c`, with `c = MathPlaneCRe + i·MathPlaneCIm`
  (draggable on the canvas while the orbit is on). Its read-out gives the step where
  it leaves `|z| ≤ 2`, or the period it settles to. The cobweb's map reads `x` and `c`
  (the real part of the plane's c). The backdrop is one of the `EscapeTimeCalculator`
  families (Mandelbrot, Julia, Burning Ship, Tricorn, Multibrot, Phoenix, Magnet 1/2,
  Glynn, Spider), rendered on the CPU at the scene's view: that grid is y-down with
  pitch `3.5 / max(w, h) / zoom` and samples pixel corners, so the zoom is converted,
  the centre moved half a pixel, and the rows flipped. Julia draws the Julia set of
  the plane's c, so dragging c moves the set and the critical orbit together. The
  other families' own params ride in the MathPlane region block. When both are on,
  the phase portrait covers the backdrop.
- **Overlay on other views:** the reverse direction. On a normal 2D fractal view, a
  "Math overlay" toggle draws the axes plus the orbit of the hovered/pinned pixel,
  through `FractalOverlayCompositor`. This extends the existing grid overlay rather
  than adding a second compositor.

### 3.7 Axis label precision (shared with the grid overlay)

User request (2026-10-29): label precision should be configurable, or adjust
itself when needed. Today's grid overlay (`FractalOverlayCompositor.FormatCoord`)
picks decimals from the **magnitude of the label value** (`6 − ⌊log₁₀|v|⌋`, capped
at 15). Three problems follow:

- **Too long at normal zoom.** At step 0.5, the label 1.5 prints as `1.500000`.
- **Not tied to the step.** Neighbouring labels differ in the step's last digit,
  and nothing guarantees that digit is shown.
- **Breaks at deep zoom.** The grid uses only the double `CenterX` / `CenterY`, so
  once the step falls below about 1e-15 × |centre| the tick values collapse to the
  same double and every label reads the same.

One **shared label formatter** (Abstractions, used by the overlay and Math Plane)
with three modes:

1. **Auto (default).** Decimals come from the **grid step**:
   `max(0, −⌊log₁₀ step⌋)`. That is the fewest digits that keep neighbouring
   labels distinct; for nice steps of 1, 2 or 5 × 10ᵏ no extra digit is needed.
   Trailing zeros are trimmed, and the origin prints as `0`.
2. **Fixed.** A user-set number of decimals (0–30), or significant figures in
   scientific form (`1.25e-8`).
3. **Offset (automatic fallback, also selectable).** Used when Auto would need
   more than a length cap (default 12 characters), or when the step is below
   about 1e-12 × |centre|. One **anchor label** in a corner shows the view centre
   at full precision, from the multi-limb centre via `CoordNotation`, never a
   `(decimal)double`. Each tick then shows only its offset from the anchor, in
   scientific form (`+3e-20`). Tick positions are computed as offsets from the
   centre, never by summing absolute doubles, so the grid keeps working at any zoom.

Where the setting lives:
- **Math Plane:** a scene setting (`labels = auto | fixed:N | sci:N | offset`), so
  it is in `MathPlaneScene` and batch reproduces it.
- **Grid overlay on other views:** a display preference (the overlay is live-only;
  it is not in batch output today). If S7 makes the overlay renderable headless, the
  setting moves with it.

Tracked as its own issue (#1559), because it fixes the existing overlay
independent of Math Plane. S1 uses the formatter from that issue.

---

## 4. UI (Control Center only)

- New Control Center section `ControlCenterSections/MathPlaneSection` (never
  `FloatingMenuView`): an object list (add / delete / reorder / visibility),
  a property editor for the selected object, the map editor (matrix grid or
  `f(z)` text with live validation, debounced 1200 ms like the equation editor), and
  a `t` slider with Play/Loop.
- Canvas input on the render surface: drag points and vector tips (snap to grid with
  Shift), double-click to add a point, Del to remove. World conversion as in
  `BilliardRayInsetControl`.
- Colours default to an Okabe–Ito-style set that stays distinguishable for red/green
  colour-blind users. Errors use the yellow `#FFCC00` convention, not red.

---

## 5. Batch / Command builder parity

Every slice keeps the headless path in step (CLAUDE.md rule):

1. **Render path.** `MathPlaneCalculator` runs through `BatchRenderer` /
   `PosterRenderer` unchanged. Verify each slice with a real `--batch` still.
2. **Expressible.** The scene → `--param MathPlaneScene=...`; scalars →
   `--param MathPlaneT=0.5` etc. via the region snapshot. No dedicated flags expected.
3. **Command builder.** `BuildLiveSeed` emits the scene param, with a parse
   round-trip test (quoting and newlines are the risk).
4. **Gaps.** Anything live-only during a slice (e.g. hover read-outs) is reported in
   `BatchCommandBuilder.DetectGaps` and filed as an issue.

---

## 6. Tests (independent invariants, not self-consistency)

- Axes: tick values are "nice" (1, 2, 5 × 10ᵏ) and every tick lies inside the view.
- Labels (Auto): neighbouring labels are always distinct strings, and dropping one
  more decimal would make two of them equal (minimal). Offset mode: anchor +
  parsed offset equals the exact tick value computed independently in `decimal` /
  BigInteger from the multi-limb centre, at zooms down to 1e-60.
- Explicit curve: every drawn vertex satisfies `|y − f(x)| <` 1 px, and no segment
  crosses a known pole (`tan x` at `π/2`).
- Implicit curve: every emitted vertex has `|F| ≈ 0` against an analytic F (circle:
  `|x² + y² − r²| < ε`); vertex count scales with circumference.
- Matrix map: at `t = 1` the image of î is column 1 of `M`; shaded area = `|det M|`
  within raster error; eigenvector lines satisfy `Mv ∥ v`.
- Polar interpolation: `|det|` is monotone in `t` for a pure rotation (no mid-way
  collapse); at `t = 1` exactly `M`.
- Complex map: at `t = 1` each mapped grid sample equals `f(z)` evaluated
  independently with `System.Numerics.Complex`.
- Orbit: points equal an independent `Complex` loop of `z² + c`; a period-3 `c`
  closes a triangle.
- Scene text: parse(serialise(scene)) == scene for random scenes, including quotes,
  unicode-free labels (ASCII names rule) and empty lists.
- Batch: a `--batch` still of a fixed scene equals the live frame (pixel compare).

---

## 7. Risks

- **Scope creep toward a CAS.** Each new object kind must answer "which fractal idea
  does this explain?" in its issue.
- **Interpolation semantics.** Linear interpolation of matrices is what viewers
  expect from 3B1B-style grids but misleads for rotations; both modes ship, with
  linear as the default.
- **Scene param size.** A long object list in one string param is fine for regions
  but can be awkward on a command line. Mitigation: `--param MathPlaneScene=@file.txt`
  if the param parser lacks a file form (check in S1; file an issue if needed).
- **Text rendering in batch.** Labels depend on font availability headless. Use the
  same font fallback the watermark uses.

---

## 8. Later follow-ups (not in the 2D epic)

- **3D Math Plane** (#1556). Starts only after the 2D slices ship. 3D axes and
  grid planes, vectors, 3×3 matrix maps with the same `t` interpolation,
  surfaces `z = f(x, y)` and parametric surfaces, orbit camera. It is a mesh
  path, not raymarching: tessellate and draw lines/triangles with depth. Open
  questions are depth sorting for translucent planes, 3D label placement, and
  whether it reuses the mesh export (S9) path.
- **Scripted scenes** (#1557). A sequence of steps (show object, apply map,
  wait, fade) driven by the Scene Engine / animation timeline, for Manim-style
  narrated sequences rendered to video. Today's single animatable `t` covers one
  transform.

---

## 9. Slices

Dependencies are stated per issue; no auto-blocking is configured.

| Slice | Issue | Content | Depends on |
|---|---|---|---|
| Labels | #1559 | Shared axis-label formatter (Auto / Fixed / Offset); fixes the existing grid overlay | — |
| S1 | #1548 | `FractalType.MathPlane`, calculator, scene param + text format, axes/grid/labels, points/vectors/segments, batch round-trip | Labels |
| S2 | #1549 | Control Center section + canvas editing (drag, add, delete, snap) | S1 |
| S3 | #1550 | Plot builder on `EquationFrontEnd`; explicit / parametric / polar curves, adaptive sampling | S1 |
| S4 | #1551 | Implicit curves (marching squares) + inequality shading | S3 |
| S5 | #1552 | Matrix maps, `t` animation (linear + polar), basis, det, eigenvectors, vector read-outs | S1 (S2 for editing UI) |
| S6 | #1553 | Complex maps on the grid, `f'(z)` read-out, phase-portrait background | S3, S5 |
| S7 | #1554 | Fractal bridge: orbit plot, cobweb, fractal backdrop, Math overlay on 2D views | S3, S5 |
| S8 | #1555 | Presets ("math behind fractals" demos), help text, user guide page | S1–S7 |
| Later | #1556 | 3D Math Plane | all 2D slices |
| Later | #1557 | Scripted multi-step scenes | S5, S8 |
