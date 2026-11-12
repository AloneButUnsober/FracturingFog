# Math Plane 3D fractal bridge: design plan

Epic: #1730. Slices: #1731–#1736 (table in §7). These issues are the canonical task list. This document
holds the design and the reasons behind it.

## 1. Purpose

The 2D Math Plane has a **fractal bridge** in two parts:
- **Inside Math Plane:** a fractal backdrop, the orbit of 0 under z² + c, and the cobweb.
- **On the 2D fractal views:** the **Math overlay** (#1554). It draws the axes and the orbit of a hovered
  or pinned point, live, in poster export and through `--math-overlay`.

Math Plane 3D (#1556, T1–T7) draws space, 3×3 maps, surfaces and demos of the steps the 3D fractals use. It
deliberately left the bridge out: §1 of [MathPlane3D-DesignPlan.md](MathPlane3D-DesignPlan.md) lists "the
fractal bridge (orbit, cobweb, backdrop) in 3D" as a non-goal. This epic reverses that non-goal, with the
same two directions as in 2D:

1. **The fractal inside Math Plane 3D:**
   - a 3D fractal as a scene object, drawn as a mesh (B1);
   - the orbit of a point under the fractal's own step (B2).

   Folds, scale and power steps then act on points next to vectors, maps and det.
2. **The math on the 3D fractal views:** a depth-tested overlay with axes, the bailout sphere and a pinned
   point's orbit, drawn with each raymarcher's exact camera (B3–B5).

Non-goals:
- Raymarching inside Math Plane 3D. The fractal is a mesh there, which is the Math Plane 3D path; the full
  raymarched image is the fractal view's job.
- Editing fractal parameters from the Math Plane 3D canvas.
- A cobweb in 3D. The cobweb is a 1D idea.

## 2. What exists to build on

- **Distance estimates as meshes.**
  - `Engine/Export/RaymarchMeshSampler.For(type, p)` returns the object-space DE that the S9 mesh export
    meshes, the same DE as the render. Its types are Mandelbulb, Mandelbox, Kifs, QuaternionJulia,
    QuaternionMandelbrot, BicomplexMandelbrot, Coquaternion, Kleinian and Julibrot.
  - `SuggestedRange` gives the bounding cube.
  - Marching cubes ([Lorensen & Cline](../Resources-Bibliography.md#lorensen-cline-marching-cubes), with
    Bourke's tables) is already shared by S9 and T5.
- **A mesh cache.** T5's implicit-surface cache keys a mesh on its geometry and commits only complete
  builds (`MathPlane3DCalculator`). A fractal mesh fits the same cache.
- **Cameras.**
  - Every 3D raymarcher passes its exact camera to `ScreenSpacePost.Froxel3DViewOf` (#1079): eye, forward,
    right and up, the field-of-view scale after the zoom lens, pan and distance. It returns a
    `Froxel3DView(FroxelCamera, PanU, PanV)`.
  - The calculators that do this are Mandelbulb, Mandelbox, KIFS, quaternion Julia and Mandelbrot,
    bicomplex, coquaternion, Kleinian, User Bulb and Julibrot (DualOrbitVolume).
- **Depth.**
  - After a frame, the raymarchers publish `DepthBuffer` (#1009): the per-pixel ray distance, with
    `DepthMiss` for sky, at output size.
  - It is published only when depth output is wanted (`ScreenSpacePost.WantsDepthOutput`), and it is
    invalid under thin-lens DOF.
- **The 2D overlay.**
  - `Abstractions/ViewState/MathOverlay.cs` holds the geometry and the orbit.
  - `Engine/Rendering/MathOverlayRenderer.cs` draws it from the live compositor, poster export and
    `--batch`.
  - `--math-overlay RE,IM[,STEPS]` is its flag.
- **Orbit code.** The Mandelbulb, Mandelbox and quaternion Julia calculators already track orbit traps, so
  each one has the step in its loop. Most steps are inline in the DE loops (for example
  `KifsCalculator.SierpDE`), not shared functions.

## 3. Design

### 3.1 Fractal object (B1, #1731)

```
fractal NAME TYPE [KEY=VALUE ...] [options] [mesh N]
```

- **TYPE:** one of the `RaymarchMeshSampler.For` types.
- **KEY=VALUE:** that family's region-snapshot keys (`RegionFractalParams`), the same keys `--param` takes,
  for example `BulbPower=8`. Unknown keys are parse errors.
  - The scene text holds every input, so regions, `--batch` and the Command builder need nothing new.
- **Meshing:**
  - marching cubes over `SuggestedRange` on `mesh N` cells a side (default 64; cap as for surfaces);
  - cached by (TYPE, keys, N);
  - cancellable, committed only when complete.
- **Axes:**
  - The raymarchers' object space is y up; Math Plane 3D is z up.
  - The object is placed with the rotation (x, y, z) → (x, −z, y), a proper rotation (det 1), so the
    fractal stands as it does in its own view.
  - This is stated in the guide and tested.
- **Everything else as for surfaces:** the map, colour and alpha, flat or smooth shading, T6 mesh export,
  and a "+ Fractal" button with a type picker in the Control Center.
- **Cost:** a DE mesh at 64³ is about 0.27 M DE evaluations, done once per geometry.
  - B1 measures it on the slowest types (Kleinian, Julibrot) and states the numbers in the as-built note.

*As built (B1, #1731):*
- **Model:** a fractal is a **surface** with the new `MathPlaneCurveKind.Fractal`. Its `Sources` hold
  the type name, then the `KEY=VALUE` tokens.
  - It is meshed into the same indexed-mesh type as T5's implicit surfaces
    (`Engine/Calculators/MathPlane3DFractalMesher.cs`).
  - Rendering, labels, bounds, translucency, the cache, cancellation and T6 mesh export therefore follow
    the implicit path.
  - Statement: `fractal NAME TYPE [KEY=VALUE ...] [options] [mesh N]`. The type is case-insensitive, and
    the canonical name is written back.
- **Where the keys are checked (a change from §3.1):**
  - The scene parser lives in Abstractions and cannot see `RegionFractalParams`. So the parser checks
    only the syntax: KEY=VALUE, no spaces, quotes, `;` or a leading `@` / `#`, no repeated key, no
    `Cam3D*` / `MathPlane*` key.
  - The Engine checks the keys with `RegionFractalParams.FromKeyValues` when meshing. An unknown key or a
    bad value becomes the render's scene error, shown as the message only (`MathPlaneSceneError`
    Statement 0).
  - `--batch` refuses it before rendering.
  - As with `--param`, a key of another family is accepted and changes nothing. A per-family key list
    does not exist; the region snapshot omits keys at their default values.
- **Mesh:** `UserBulbMeshExporter.BuildMarchingCubes` (now `internal`) with the export's defaults: iso
  half a cell, faces capped. A test checks that it equals the STL that `ExportMarchingCubes` writes,
  through the rotation. The core's minimum grid is 8, so `mesh N` below 8 meshes at 8.
- **Measured** (Debug build, author's machine, default settings):
  - mesh 64 builds in 0.01-0.04 s for every type;
  - mesh 128 builds in 0.10-0.25 s.

  So the default of 64 stays for every type. Mandelbox scale 2 is a solid box of half-size 6 with a
  pitted skin, as its raymarched view is.
- **A fix in the S9 core:** a corner whose distance estimate is NaN broke the mesh. The Mandelbulb's
  estimate is 0 · log 0 at r = 0, which is inside the set.
  - Such a corner read as "outside". Marching cubes then wrapped a one-cell cavity round it, and the edge
    interpolation made its vertices NaN. Those NaN vertices also went into S9 STL / OBJ / glTF files.
  - A non-finite sample now takes the mean of its finite axis neighbours (or +∞ with none), and an edge
    with a non-finite end uses its midpoint.
- **Not done here:** User Bulb (no `RaymarchMeshSampler` entry; §6).

### 3.2 Orbit / fold-step object (B2, #1732)

```
orbit NAME TYPE x y z [steps N] [KEY=VALUE ...] [options]
```

- Draws z₀ = (x, y, z), z₁, …, z_N under one iteration of the family, as a polyline with markers.
- The orbit stops at escape (|z| > the family's bailout), and the escape is marked.
- **The step functions are the calculators' own.** Where a loop is inline, B2 moves its step into a shared
  static function that the calculator then calls.
  - Pixels must not change, which the visual-regression baseline (#1292) checks.
  - Where a refactor would touch a hot GPU-parity path, B2 keeps an exact CPU twin instead, with a parity
    test.
- **Families:**
  - **Mandelbulb:** White and Nylander's triplex power, plus c.
  - **Mandelbox:** Lowe's box fold, sphere fold, then scale s, plus c.
  - **KIFS:** Knighty's fold sequence of the chosen fold kind, then the scale about the offset.
  - **Quaternion Julia and Mandelbrot:** q² + c in 4D. The scene is 3D, so w is dropped, and the label
    says "(w dropped)".
- **Coordinates:** the orbit uses the fractal's own axes, placed with the same rotation as §3.1. A fractal
  object and an orbit of the same TYPE and keys therefore line up.

### 3.3 Exact camera for each raymarcher (B3, #1733)

The two cameras differ, and this is the hard part of the epic:

| | Raymarchers | Math Plane 3D |
|---|---|---|
| Up | y | z |
| Vertical FOV | 60° (Mandelbulb, Mandelbox, KIFS, quaternion Julia and Mandelbrot in code; B3 confirms the rest) | 45° |
| Distance | clamped outside the set; inside the clamp a *zoom lens* narrows the FOV | eye = target + distance / Zoom |
| Pan | CenterX / CenterY shift the image plane | target point |

- B3 adds one projection type, for example `Raymarch3DCamera.Project(world) → (px, py, rayDistance)`.
  It is built from exactly the values each calculator passes to `Froxel3DViewOf`, so there is one source
  of truth.
- Each calculator exposes it after a frame, for example `LastCamera`, next to `DepthBuffer`.
- The stereo eye offset and the aspect ratio are included.
- The independent check: the calculator's own ray for a pixel, projected back, lands on that pixel.

### 3.4 Math overlay on 3D views (B4, #1734)

- **What it draws:** View → Math overlay on a 3D view shows the axes, the bailout sphere (as a wire ring)
  and the B2 orbit of the hovered or pinned point.
- **Picking:** the hovered pixel's depth gives the surface point; Shift-click pins it.
- **Depth test:**
  - Each overlay sample is compared with `DepthBuffer` (ray distance, with a small bias).
  - Hidden parts are drawn dimmed and dashed rather than removed, because an orbit inside the set would
    otherwise vanish.
  - The overlay asks for depth output while it is on.
- **Thin-lens DOF:** depth is invalid then. The overlay draws without the depth test and shows a yellow
  `#FFCC00` note.
- **Stereo:** each eye uses its own camera.
- **Post effects:** relief and froxel fog do not change the camera. The overlay is drawn after them, on
  the final image, as the 2D overlay is.
- **Colours:** Okabe-Ito, never a red/green distinction.

### 3.5 Poster and batch (B5, #1735)

- **Flag decision:** a separate flag, `--math-overlay-3d X,Y,Z[,STEPS]`, rather than a third coordinate on
  `--math-overlay`.
  - The 2D flag's RE,IM mean c or z₀ of a complex map, a different thing.
  - A separate flag keeps the catalog's modes and conflicts simple: it is 3D types only, and it conflicts
    with `--math-overlay`.
- Poster export and the batch still draw it the same way as the live compositor.
- Video and slideshow loops draw it on every frame, or report it as a fidelity gap
  (`BatchCommandBuilder.DetectGaps`) with a follow-up issue.
- The Command builder emits it from the live pin.

### 3.6 Demos and guide (B6, #1736)

- **New Math Plane 3D demos:**
  - a Mandelbulb mesh with an escaping orbit;
  - a Mandelbox orbit showing fold, then scale;
  - a KIFS fold step on a point beside the KIFS mesh;
  - a quaternion Julia slice with its orbit.
- **Guide:** sections for the fractal object, the orbit object and the 3D overlay in
  `Docs/User/MathPlane3D-Guide.md`, plus updated help text.

## 4. Batch parity

- **B1 and B2** are scene statements, so the region snapshot and `--param MathPlane3DScene=...` carry
  them. The Command builder needs nothing new; B1 and B2 each add a round-trip test.
- **B4** is live-only until **B5** lands. That gap is reported in `DetectGaps` from B4 onward, never
  dropped silently.

## 5. Tests (independent invariants)

- **B1:**
  - the mesh equals the S9 export's mesh after the stated rotation;
  - vertices lie within one cell of the DE's zero set;
  - the cache and cancellation behave as for T5.
- **B2:**
  - the orbit's step count to escape equals the calculator's own iteration count for the same point;
  - KIFS steps equal the calculator's fold code on random points;
  - the visual baseline is unchanged after refactors.
- **B3:** the calculator's ray for a pixel, projected back, lands on that pixel within 0.5 px. This covers
  the zoom lens, pan and stereo.
- **B4:**
  - a synthetic depth buffer hides exactly the segments behind it;
  - with the overlay off, the frame is unchanged.
- **B5:** the batch still equals the live compositor's output for the same view (CPU, pixels identical).
- **B6:** every demo renders headless and round-trips through `--param` and the Command builder.

## 6. Risks and open questions

- **GPU paths.** B3 must describe the camera the GPU kernel actually uses, which should be the same
  uniforms. B3 tests both paths where a GPU is present; on CI the GPU tests skip.
- **Mesh cost on slow DEs** (Kleinian, Julibrot) may need a lower default `mesh N` for those types. This
  is decided in B1 by measurement.
- **Quaternion types** are 4D. Dropping w is the simplest honest projection. A user-chosen slice or
  projection is a possible follow-up, not in this epic.
- **User Bulb** has a camera (B3) but no `RaymarchMeshSampler` entry (B1). Its mesh export has its own
  path, so B1 may add it later.

## 7. Slices

| Slice | Issue | Content | Depends on |
|---|---|---|---|
| B1 | #1731 | `fractal` object: DE mesh in the scene, cache, axes rotation, + Fractal button | — |
| B2 | #1732 | `orbit` object: the families' own steps, shared with or twinned to the calculators | — (B1 for demos) |
| B3 | #1733 | Exact camera description for each raymarcher (`Project`, `LastCamera`) | — |
| B4 | #1734 | Math overlay on live 3D views: axes, bailout sphere, orbit, depth test, pick and pin | B2, B3 |
| B5 | #1735 | Poster and `--batch` (`--math-overlay-3d`), Command builder, video/slideshow | B4 |
| B6 | #1736 | Demos, guide, help | B1, B2 (B4) |

B1, B2 and B3 can run in parallel.

## 8. Sources

No new sources. The fractal formulas and folds are already credited in the
[bibliography](../Resources-Bibliography.md):
- Daniel White's Mandelbulb, with Paul Nylander's notes;
- Tom Lowe's Mandelbox;
- Knighty's *Kaleidoscopic (escape time) IFS*;
- Hart, Sandin and Kauffman's *Ray tracing deterministic 3-D fractals* (quaternion Julia sets).

Marching cubes: [Lorensen & Cline](../Resources-Bibliography.md#lorensen-cline-marching-cubes) and
[Bourke](../Resources-Bibliography.md#bourke-polygonise). The grid-of-space look of Math Plane 3D:
[3Blue1Brown](../Resources-Bibliography.md#sanderson-3d-transformations). Any further source a slice
uses is credited in that slice's PR.
