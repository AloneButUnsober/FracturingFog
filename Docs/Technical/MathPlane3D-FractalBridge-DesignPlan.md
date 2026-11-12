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

*As built (B2, #1732):*
- **Model:** an orbit is a **curve** with `MathPlaneCurveKind.Fractal`.
  - Sources hold the type, then KEY=VALUE. Coords hold the start point in scene axes. `Mesh` holds the
    step cap (0 means the family's iteration count; at most 1000).
  - It reuses curve drawing, the map and labels.
  - Its start point is a handle (`MathPlane3DSceneEdit.HandleCount` is 1), so dragging it re-runs the
    orbit.
  - Settings are parsed and checked as for B1. Bad keys become the render's scene error, and `--batch`
    refuses them.
- **The step functions are exact CPU twins** (`Engine/Calculators/MathPlane3DOrbit.cs`), not shared
  code. The calculators' DE loops are left untouched, so no render path changed and no visual baseline
  moved. The parameters are derived as `RaymarchMeshSampler.For` derives them.
- **Agreement is tested against the calculators' own code.** Neither test can pass with a wrong copy:
  - Mandelbulb, Mandelbox and the quaternion Julia and Mandelbrot: the closest the orbit passes to the
    origin equals the calculator's orbit-trap estimator **bit for bit** (`IOrbitTrapEstimator`, the same
    loop), on 300 random points each. The traps cover z₀ to the escaping point (Mandelbulb), or every
    post-step point (the others).
  - KIFS, every fold kind, plus Menger with a negative z offset so its corner mirror fires: the DE read
    off the orbit's last point equals `KifsCalculator`'s DE bit for bit. That is (r − 2)·s⁻ⁿ, r·s⁻ⁿ,
    r·s^−(it+1), or r/|dr| for Mandelbox-rot, whose orbit also carries dr.
  - This replaces §3.2's "step count to escape" check. The Mandelbulb's escape count is private to its
    DE, and the traps compare every point rather than one count.
- **Escape semantics follow each loop exactly.**
  - The Mandelbulb tests |z| > 2 before stepping, so an orbit that runs out of iterations can end
    outside without having "escaped".
  - The others test after the step.
- **Display ball:** the radius is min(escape radius, 1.5 × `SuggestedRange`).
  - The polyline is clipped to it (`MathPlane3DOrbit.Clip`, a segment-sphere intersection), and a
    segment that passes through the ball keeps its chord.
  - Each step is a dot (the start larger), and a ring marks an escape.
  - Bounds come from the clipped pieces, so the grid keeps to the fractal's scale.
- **Measured:** an orbit is at most a few hundred steps of a few flops each, so it is rebuilt on every
  scene parse without a cache.
- **UI:** a **+ Orbit** button. A selected orbit shows Type (the five orbit types), Settings, Numbers
  (x y z) and Steps.

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

*As built (B3, #1733):*
- **The type:** `Engine/Rendering/Raymarch3DCamera.cs`. `Raymarch3DCamera` holds the eye, the forward,
  right and up vectors, fovScale, pan and the frame's width and height. It has:
  - `RayDirection(x, y)`;
  - `PointAt(x, y, distance)`;
  - `Project(world) → (x, y, ray distance)`, which returns null behind the eye.

  Coordinates are continuous pixel positions: the centre of pixel (i, j) is (i + ½, j + ½).
- **The pixel ray formula is shared.** All ten raymarchers cast the ray of pixel (x, y) the same way:
  u = (2(x + ½)/W − 1)·fovScale·aspect + panU, v = (1 − 2(y + ½)/H)·fovScale + panV, and
  dir = normalize(F + u·R + v·U).
- **One source of truth for the camera.** Each calculator sets `LastCamera`
  (`IRaymarch3DCameraSource`) from the `Froxel3DView` it already builds every frame, right after
  `ScreenSpacePost.Froxel3DViewOf`. That view holds the eye after the distance clamp and the stereo
  offset, fovScale after the zoom lens, and the pan.
  - The GPU kernels' parameters are filled from the same local variables in the same method, so the CPU
    and GPU paths share the camera by construction.
  - Not tested on a GPU here: CI has none, and the test host's calculators may route to either path.
- **Which calculators:**
  - Mandelbulb, Mandelbox, KIFS, quaternion Julia and Mandelbrot, bicomplex, coquaternion, Kleinian,
    Julibrot (DualOrbitVolume) and User Bulb.
  - The User Bulb has no pan; its pan is in its orbit target.
  - Mirror Spheres (a ray tracer) and Math Plane 3D (its own camera) are not included.
- **Measured differences from §3.3's table:**
  - The Mandelbulb has **no zoom lens**: zooming moves its eye in (distance / zoom), at a fixed 60°.
  - The eight other DE types narrow their field of view inside the clamp.
- **The test is independent of the camera code.** Each renderer's own published depth (ray distance to
  its march hit) is laid along `LastCamera`'s ray for the same pixel, and the point must be on the
  fractal.
  - It runs for all 9 DE raymarchers in 4 cases: plain, pan, zoom (lens), and stereo eye.
  - Every hit point's |DE| is ≤ 0.0015, the march's own hit epsilon.
  - The `Project` round trip is within 1e-13 px.
  - The test fails if the pan sign, the aspect, the v direction, the half-pixel centre or the
    normalisation is wrong.

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

*As built (B4, #1734):*
- **Renderer:** `Engine/Rendering/MathOverlay3DRenderer.cs` draws onto the finished frame, with B3's
  `LastCamera`. It draws:
  - the axes, out to min(escape radius, 1.5 × range), which is B2's display ball;
  - the bailout sphere's silhouette, drawn only when the escape radius is that ball. That is the tangent
    circle, centre E·r²/|E|² and radius r·√(1 − r²/|E|²);
  - B2's object-space orbit, clipped to the ball, plus the point and a read-out.
- **Depth test:** each line is cut into pieces about two pixels long. A piece whose ray distance is
  beyond the frame's depth at its pixel, plus 1e-3 + 1 % of the depth, is drawn dimmed (alpha 0xA8) and
  dashed. A thin dark halo keeps every line readable over the texture.
  - Consecutive pieces of the same kind share one contour, because Skia restarts a dash at every
    `MoveTo`. Drawing each piece as its own contour made hidden lines solid; this was caught visually,
    and a test now checks it.
- **Depth on request (no change to the user's settings):**
  - `IDepthAovSource.DepthRequested` is set by the host in `SyncAltStateFromMandel` while the overlay is
    on a 3D view.
  - Each calculator then sets the runtime-only `LightingFxData.OverlayDepth` on its own copy of the
    block. `FromFx` does not map it, so it is never saved, and `WantsDepthOutput` honours it.
  - Capturing depth leaves the image byte-identical, as the Fake-stereo capture already did (tested on
    every raymarcher). Like stereo, it turns off masked adaptive AA (`PixelLocal3D`) while on.
- **Host:**
  - At a fresh upload, the frame's camera and a copy of its depth are captured, only while the overlay
    is on. Hover repaints, which do not recompute, draw on that captured frame.
  - `PickSurface3D` maps a client pixel to the frame and returns the depth along the camera ray
    (`MathOverlay3DRenderer.Pick`).
  - The same View controls as 2D drive it: Follow pointer, Pin last point, steps, and the pin text as
    `x y z` on a 3D view. Turning the overlay on in 3D triggers one render, so the frame has depth.
- **Not drawn:**
  - on side-by-side stereo frames: the composed frame is wider than the camera's;
  - depth-dimming under thin-lens DoF: no depth exists then, and a yellow note says so.
  - The marker, the axis labels and the read-out are always drawn at full strength.
- **Batch:** a pinned 3D overlay is reported by `BatchCommandBuilder.DetectGaps` until B5 (#1735).
  Hover-only needs nothing.

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

*As built (B5, #1735):*
- **Flag:** `--math-overlay-3d X,Y,Z[,STEPS]` (`BatchFlags.MathOverlay3D`, parsed by
  `MathOverlay3DSpec.TryParse`). The catalog entry is image-mode only and conflicts with `--math-overlay`,
  both ways. The batch refuses it:
  - on a 2D fractal type (exit 2);
  - together with `--math-overlay`;
  - in video, slideshow and scene modes (#1746). That is an error rather than a silent drop.
- **Poster path:** `PosterRequest.MathOverlay3D`.
  - While it is set on a 3D type, `RenderComposedBuffer` sets `DepthRequested` on the capture calculator.
    It then keeps the camera and a copy of the depth from the **first, unjittered** sample, before the AA
    samples pan the camera by sub-pixel offsets.
  - `ApplyMathOverlay3D` draws it pinned, last in `RenderDisplayBuffer`, on the finished display buffer,
    as the live host does after its post chain. The letterbox path renders the inner rect through the same
    function, so the overlay lands on the fractal, not on the bars.
  - A stereo side-by-side frame is not drawn on, as live.
  - `MathOverlay3DReport` says what happened, and the batch prints a note when the overlay was not drawn
    or had no depth (thin-lens DoF).
- **Live export:** `FractalRenderHost.CreatePosterRequest` carries `CurrentMathOverlay3DPin()`, so a
  pinned 3D overlay is part of an exported image. Hover is not.
- **Command builder:** emits `--math-overlay-3d` from the live 3D pin, and B4's gap is gone. A 3D overlay
  with no pin is now reported as hover-only, as in 2D.
- **Tests:**
  - for Mandelbulb, Mandelbox, KIFS and Kleinian, a poster equals the host's own calculator frame with
    the live draw call applied (pixels identical);
  - with AA, the drawn overlay equals one drawn with the unjittered camera.

### 3.6 Demos and guide (B6, #1736)

- **New Math Plane 3D demos:**
  - a Mandelbulb mesh with an escaping orbit;
  - a Mandelbox orbit showing fold, then scale;
  - a KIFS fold step on a point beside the KIFS mesh;
  - a quaternion Julia slice with its orbit.
- **Guide:** sections for the fractal object, the orbit object and the 3D overlay in
  `Docs/User/MathPlane3D-Guide.md`, plus updated help text.

*As built (B6, #1736):*
- **Four demos** were added to `MathPlane3DPresets.All`. Each has an orbit, and three also have a
  translucent fractal mesh of the same type and settings. The Mandelbox demo shows the fold box and the unit
  sphere instead: the s = 2 Mandelbox's mesh is a solid block filling its 12-wide meshing cube (measured), which hid the orbit in the visual
  check.
  - "Mandelbulb: an escaping orbit": escapes at step 4;
  - "Mandelbox: fold, then scale": step 1 only scales, step 2 box-folds x and y first;
  - "KIFS fold step beside the mesh": Sierpinski, one x + y mirror fold, then 2z − (1, 1, 1);
  - "Quaternion Julia slice and its orbit": escapes at step 7, with w dropped.
- **Coordinates:** the idea texts give their numbers in the fractal's own axes (y up). The guide says how
  they map to the scene's z-up axes.
- **Tests:**
  - The generic demo tests cover them: parse, headless render, the `--param` list, the Command builder, and
    the guide naming them.
  - `MathPlane3DBridgeDemoTests` checks each stated number against the scene's own orbit object. The
    numbers are worked by hand from each family's step, and each test checks that the mesh builds.
- **Docs:**
  - guide rows, an axes note and a section pointing to the 3D math overlay;
  - help text for the demos, the overlay, O and `--math-overlay-3d`.
  - There are no new sources: the families' steps are credited from B2.

## 4. Batch parity

- **B1 and B2** are scene statements, so the region snapshot and `--param MathPlane3DScene=...` carry
  them. The Command builder needs nothing new; B1 and B2 each add a round-trip test.
- **B4** was live-only until **B5** landed. That gap was reported in `DetectGaps` until B5 replaced it
  with `--math-overlay-3d`, which is for stills only for now (#1746).

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
