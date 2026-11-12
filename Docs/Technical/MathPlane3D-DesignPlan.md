# Math Plane 3D — Design Plan

**Tracking issue:** [#1556](https://github.com/AloneButUnsober/FracturingFog/issues/1556)
(follow-up to the 2D Math Plane epic [#1547](https://github.com/AloneButUnsober/FracturingFog/issues/1547)).
**Status:** design doc. No code lands until this doc is reviewed. The slices are
filed as issues (§9), and the issues are the canonical task list.

**Related docs:** [MathPlane-DesignPlan.md](MathPlane-DesignPlan.md) (the 2D render mode;
§8 lists this follow-up); [MathPlane-ScriptedScenes-Plan.md](MathPlane-ScriptedScenes-Plan.md);
[3D-Rendering-Roadmap.md](3D-Rendering-Roadmap.md) (the raymarch families, mesh export S9);
[Resources-Bibliography.md](../Resources-Bibliography.md).

---

## 1. Purpose

The 2D Math Plane explains the ideas behind FF's 2D fractals: vectors, curves, a grid under
a 2×2 map, orbits. FF's 3D fractals (Mandelbulb, Mandelbox, quaternion Julia sets, KIFS)
rest on the same ideas one dimension up: points and vectors in space, 3×3 maps (rotations,
scalings, the folds of a KIFS), and surfaces. **Math Plane 3D** draws that space:

- axes and grid planes, points, vectors, segments, polylines and space curves;
- surfaces z = f(x, y), parametric surfaces (x, y, z)(u, v), and later implicit surfaces
  F(x, y, z) = 0;
- a 3×3 matrix map played from the identity with the same `t` (linear and polar
  interpolation), with det as the volume of the unit cube's image and the real eigenvectors;
- an orbit camera, so the scene can be turned with the mouse, keyed in the Scene Engine and
  dollied in video.

The look follows 3Blue1Brown's *Three-dimensional linear transformations* (Essence of linear
algebra, chapter 5): the grid of space morphing under a matrix while the origin stays fixed
([bibliography](../Resources-Bibliography.md#sanderson-3d-transformations)).

Non-goals:
- A CAS, a CAD tool or a general 3D modeller.
- Raymarched rendering: this is a mesh path (lines and triangles with depth).
- Complex maps and the fractal bridge (orbit, cobweb, backdrop) in 3D. They are 2D ideas and
  stay in the 2D mode.
- Scripted 3D scenes. They are possible later (the scripted-scenes engine is
  dimension-agnostic, §8), not in this epic.

## 2. What exists to build on

| Need | Already in FF | Where |
|---|---|---|
| Scene as one text param, batch via `--param` | 2D `MathPlaneScene` parser, region snapshot | `Abstractions/Models/MathPlaneScene.cs`, `Engine/Models/FractalRegion.cs` |
| Expressions in x, y, t | `PlotExpression` (equation language) | `Equations/PlotExpression.cs` |
| Orbit camera triple, keyframes, Scene Engine | `CameraParamBinding` (11 families), `CameraTrack` | `Abstractions/Render/CameraParamBinding.cs` |
| Generic camera in regions / batch | the snapshot's `Cam3DFamily` + `Cam3DDistance / Cam3DTheta / Cam3DPhi` block, filled per family | `Engine/Models/FractalRegion.cs` (`Snapshot`) |
| 3D video motion (camera dolly via distance / zoom) | `VideoMotionKind.Dolly` for `Raymarch3D` | `Abstractions/Models/VideoMotionPlan.cs` |
| Implicit-surface meshing | Marching cubes / dual contouring (S9) | `Engine/Export/DualContourMesher.cs`, bibliography `#lorensen-cline-marching-cubes` |
| Mesh files | glTF/GLB, STL, PLY writers and validator (S9) | `Engine/Export/GltfMeshWriter.cs`, `MeshValidator.cs` |
| 2×2 linear / polar `t` interpolation | `MathPlaneMap` (S5) | `Abstractions/Models/MathPlaneMap.cs` |

**There is no triangle rasterizer in FF.** Every GPU path is a compute kernel, and the 2D
Math Plane draws through SkiaSharp, which has no depth buffer. That decides §3.3.

## 3. Architecture

### 3.1 A new fractal type

**`FractalType.MathPlane3D`**, separate from the 2D `MathPlane`, with its own params:

- `MathPlane3DScene` (text);
- the matrix `MathPlane3DM11 … M33`;
- `MathPlane3DT` and `MathPlane3DInterpolation`;
- the camera triple `MathPlane3DCameraDistance`, `MathPlane3DCameraTheta` and `MathPlane3DCameraPhi`;
- `MathPlane3DTarget{X,Y,Z}`, the point the camera orbits.

Why a separate type rather than a 3D switch inside the 2D mode:
- **Camera and motion reuse.** Registering the camera triple in `CameraParamBinding` brings
  orbit input, `CameraTrack` keys, Scene Engine camera shots, the generic `Cam3D*` batch
  params and the video dolly at once, as for every 3D family. A 2D view has none of these.
- **No risk to the 2D mode.** Its params, scenes, demos and tests stay untouched.

`FractalMotionCapabilities` classes it with the 3D families (dolly). Its "zoom" follows the same
contract: the camera distance is divided by `Zoom`.

### 3.2 Scene text

Same rules as the 2D format: version line, `;` or newlines, quoted expressions, errors with
statement and column. The 2D parser's tokenizer, name rules and number parsing are shared.

```
v1
axes on|off        grid xy|xz|yz|off ...    ticks on|off    labels ...
det on|off         eigen on|off             lengths on|off
point    NAME x y z                        [options]
vector   NAME x y z [from x0 y0 z0]        [options]
segment  NAME x1 y1 z1 x2 y2 z2            [options]
polyline NAME x1 y1 z1 x2 y2 z2 ...        [options]
curve    NAME "x(t)" "y(t)" "z(t)" tmin tmax              [options]   space curve
surface  NAME explicit   "f(x, y)" [xmin xmax ymin ymax]  [options]   z = f(x, y)
surface  NAME parametric "x(u,v)" "y(u,v)" "z(u,v)" umin umax vmin vmax  [options]
surface  NAME implicit   "F(x, y, z)" [box]               [options]   later slice (§9)
options: #RRGGBB  #RRGGBBAA (translucent)  hidden  nolabel  mesh N (surface resolution)
```

### 3.3 Renderer: a CPU z-buffer (decision)

**Decision: a CPU software rasterizer with a depth buffer**, not a GPU mesh path. Reasons:
- **No pipeline to reuse.** FF has no triangle pipeline (§2), so the GPU option is a new D3D11
  and Vulkan draw path with its own shaders.
- **The scale is small.** A Math Plane scene is a few thousand to ~10⁵ triangles. CPU
  rasterizing that is milliseconds at 1080p, row-parallel like the other CPU calculators.
- **It runs everywhere.** It is identical headless, in batch and on Linux without a GPU,
  matching the 2D mode's "one renderer" rule (bit-identical batch and live frames).

**Passes:**
1. **Opaque triangles** (surfaces, filled det parallelepiped faces, vector arrowheads) are
   rasterized into a colour buffer and a z-buffer. Shading is flat or Gouraud with one
   directional light plus ambient, and the colour comes from the object. The z-buffer is
   Catmull's (and Straßer's, independently) hidden-surface method
   ([bibliography](../Resources-Bibliography.md#catmull-zbuffer)).
2. **Translucent triangles** (grid planes, surfaces with alpha `#RRGGBBAA`) are sorted back
   to front by centroid depth and blended over the opaque result. They are depth-tested
   against it but don't write depth. This is the painter's ordering
   ([Newell, Newell & Sancha](../Resources-Bibliography.md#newell-depth-sort)), applied only
   to the translucent pass.
   - **Known limit:** intersecting translucent triangles can sort wrongly. Newell et al.
     split such faces; FF accepts the artefact instead (it is small at the default
     resolutions) and documents it.
3. **Lines** (axes, grid lines, curves, segments, vector shafts) are clipped to the near
   plane, projected, and drawn with Skia anti-aliasing. Each is split into short pieces, and
   a piece is drawn only where its mid-depth passes the z-buffer test, with a small bias so
   lines lying on a surface stay visible.
4. **Points, handles and labels** are drawn in screen space. A label is hidden when its
   anchor fails the depth test (an option keeps all labels visible).

**Projection:** perspective, with the vertical field of view fixed at 45°. A `projection
orthographic` statement is cheap to add and helps reading coordinates.

*As built (T1, #1713):*
- **Where it lives:** `Engine/Calculators/MathPlane3DCalculator.cs` holds the renderer;
  `Abstractions/Models/MathPlane3DScene.cs` holds the scene, which reuses the 2D parser's
  tokenizer, numbers, names and labels.
- **Camera:** z up; eye = target + (distance / Zoom)·(sinφ cosθ, sinφ sinθ, cosφ), with a 45°
  vertical FOV.
- **Rasterizer:** 1/z depth (nearer = larger); pixel centres; the top-left fill rule, so a
  closed fan covers each pixel exactly once; strict depth test.
- **Arrowheads:** flat-Lambert cones, the only triangles in T1. Triangles crossing the near
  plane were skipped in T1; T2 clips them.
- **Lines:** clipped to the near plane, cut into ~3 px pieces, depth-tested at each piece's
  mid-1/z with a 0.2 % relative bias.
- **Labels and tick numbers:** shown only where their anchor passes the depth test.
- **Grid:** the extent covers the objects (×1.2, nice-rounded); with no objects it follows the
  camera distance.
- **Registration** follows the 3D families:
  - `CameraParamBinding`, the `Cam3D*` snapshot block and `FractalViewState.Is3D` (right-drag
    orbits as in the other 3D types);
  - the `Raymarch3D` motion class (video dolly);
  - the animatable camera, `PosterRenderer` and the render host.
- **Excluded from DE mesh export** (`MeshExportCapabilityTests`): its surfaces export through
  T6.
- **Matrix params:** `t` and the matrix arrive with T3, not as stubs in T1.

### 3.4 Maps and `t`

- **Linear:** `(1 − t)·I + t·M`, as in 2D.
- **Polar (det M > 0):** factor `M = R·S` with R a rotation and S symmetric positive definite.
  - R comes from Higham's Newton iteration `X ← ½(X + X⁻ᵀ)`, which converges quadratically
    ([bibliography](../Resources-Bibliography.md#higham-polar-decomposition)); then S = Rᵀ·M.
  - R is interpolated from the identity by quaternion slerp
    ([Shoemake](../Resources-Bibliography.md#shoemake-slerp)), and S linearly, so a pure
    rotation turns instead of shrinking half-way, as it does in 2D.
  - det M ≤ 0 has no rotation-only factor, so polar falls back to linear and the section
    says so (same rule as 2D).
- **Read-outs:**
  - det as the signed volume of the unit cube's image, drawn as a shaded parallelepiped;
  - the real eigenvectors (one to three) as lines through the origin, with eigenvalues;
  - vector lengths after the map.

*As built (T3, #1715):*
- **Params** (region snapshot, so `--param` and the Command builder carry them):
  `MathPlane3DMapOn`, `MathPlane3DM11` … `MathPlane3DM33` (default: the shear j ↦ i + j),
  `MathPlane3DT` and `MathPlane3DInterpolation` (the 2D enum). `t` and the nine entries are
  animatable.
- **Scene text:** `det on|off`, `eigen on|off`, `lengths on|off`.
- **Maths** (`Abstractions/Models/MathPlane3DMap.cs`): `Mat3`, `MathPlaneQuat`.
  - Polar: Higham's unscaled Newton iteration (up to 100 steps, stops when a step moves X by
    less than 1e-15 of its norm), S symmetrised. R(t) is slerped from the identity, and the
    frame is R(t)·((1 − t) I + t S).
  - The quaternion of R is FF's inversion of the quaternion-to-matrix formula, branching on the
    largest diagonal term so half-turns stay accurate.
- **Eigenpairs:**
  - **Roots:** bisection on the characteristic cubic between its turning points, within ±(‖M‖_F + 1).
    A triple root is taken exactly at tr/3, and a double root where a turning point touches zero.
  - **Eigenvectors:** the largest cross product of two rows of M − λI. When M − λI has rank 1,
    two orthonormal vectors span its plane; a multiple of I reports "every vector".
- **Renderer:**
  - the grid size comes from the unmapped scene, so it holds still while `t` animates;
  - the original grid planes stay, the axes dim, and the mapped grid is drawn in the 2D map
    blue;
  - objects, surface vertices (normals recomputed from the mapped triangles), curves, fills and
    arrowheads are mapped;
  - det is the parallelepiped (translucent faces in the translucent pass, depth-tested edges)
    with "det = …" at its far corner;
  - eigen lines are dashed through the origin, with λ labels;
  - lengths are |M v| under each vector's name;
  - polar with det ≤ 0 draws linear and says so in the corner.

### 3.5 Surfaces

- **Explicit z = f(x, y):** sampled on a regular grid, `mesh N` cells a side (default 64).
  Non-finite samples drop their cells, so poles cut the surface instead of spiking it.
- **Parametric (u, v):** the same grid in (u, v).
- **Implicit F = 0:** marching cubes on a box grid
  ([Lorensen & Cline](../Resources-Bibliography.md#lorensen-cline-marching-cubes), already
  used for implicit curves). This is a later slice.

*As built (T5, #1717):*
- **Text:** `surface NAME implicit "F(x, y, z)" [xmin xmax ymin ymax zmin zmax]`, default box
  [-3, 3]³, and `mesh N` (2-256, default 64) cells a side. F reads only x, y and z. Here z is
  a real input (`PlotExpression.EvalXYZ`), not the 2D language's complex x + iy.
- **Mesher:** `Engine/Calculators/MathPlane3DImplicitMesher.cs` does marching cubes with the
  edge and triangle tables of
  [Bourke's write-up](../Resources-Bibliography.md#bourke-polygonise). It shares them with the
  S9 exporter, which already used them uncredited; the bibliography entry is added here.
  - A corner is inside where F < 0, and triangles face F > 0, so a closed surface faces outward.
  - Each edge's vertex is made once and shared, so a closed surface inside the box is watertight
    (checked with `MeshValidator`). A surface that leaves the box is cut open at its faces.
- **Poles,** as in 2D: every crossing is found by bisection (10 halvings, then a linear step).
  - A crossing whose final |F| is not below 0.05 of the larger end value is a pole, and every
    cell using that edge is dropped. `1/x` therefore meshes nothing, and `1/x - 1` meshes only
    x = 1.
  - A cell with a non-finite corner is dropped too.
- **Render:** index triangles go into the same opaque or translucent pass as the other surfaces.
  - Under a map, the vertices are mapped one by one.
  - Gouraud normals are the area-weighted sums of the mapped triangles' normals round each vertex.
  - The label sits on the highest vertex. The grid is sized by the largest |coordinate| of the
    vertices.
- **Cost:**
  - `mesh N` costs (N+1)³ evaluations, run in parallel: 64 is about 0.3 M, and 256 about 17 M.
    A unit-sphere still took 0.18 s at 64 and 1.8 s at 256 (900×600, Debug build, author's machine). The triangle cap is 4 M.
  - A mesh is kept while its formula, box and resolution are unchanged. Colour, name and
    visibility edits, other objects and orbiting therefore never re-mesh it.
  - A render cancelled mid-mesh commits nothing, so the next render meshes again.
- **UI:** a "+ Implicit" button adds the unit sphere. The selected object's box is edited in the
  numbers field, as six numbers.
- **Batch:** the scene text carries the statement, so `--batch` and the Command builder need no
  new flags. A round-trip test renders both sides identical.
- **Under a map:** a surface is mapped vertex by vertex, which is exact for a linear map.
  Normals are recomputed from the mapped triangles.

*As built (T2, #1714):*
- **Text:** `surface NAME explicit "f(x, y)" [xmin xmax ymin ymax]` (default domain [-3, 3]²),
  `surface NAME parametric "x(u,v)" "y(u,v)" "z(u,v)" umin umax vmin vmax`,
  `curve NAME "x(t)" "y(t)" "z(t)" tmin tmax`, the option `mesh N` (2–256, default 64) on
  surfaces, `shading smooth|flat` and `fill xy|xz|yz|off`.
- **u and v** are opt-in plot variables (`PlotExpression.TryParse(..., surface: true)`), so the
  2D language keeps `u` and `v` free as `let` names.
- **Sampling:** a cell is dropped when a vertex is not finite, or when one coordinate spans more
  across the cell than the central 90 % range of that coordinate's samples (a jump across a
  pole). The grid size counts a surface by that central range, so a pole's walls don't inflate
  it. Space curves are 512 uniform steps in t, broken at non-finite samples.
- **Shading:** two-sided Lambert ([bibliography](../Resources-Bibliography.md#lambert-cosine-law))
  plus a 0.35 ambient floor, light over the camera's shoulder. Smooth is Gouraud
  ([bibliography](../Resources-Bibliography.md#gouraud-shading)): vertex normals averaged from the
  drawn cells, colours interpolated linearly in screen space.
- **Near plane:** triangles are clipped to it (Sutherland–Hodgman against one plane,
  [bibliography](../Resources-Bibliography.md#sutherland-hodgman-clipping)), which lifts T1's
  "skipped" limit.
- **Translucent pass:** `#RRGGBBAA` surfaces and filled grid planes, sorted farthest-first by
  centroid depth (stable), blended straight-alpha over the colour buffer where they pass the
  opaque depth, no depth write. Lines and points are split by the nearest translucent depth:
  those behind it are drawn before the blend (tinted), the rest after it. Labels come last.
- **Known limits:** intersecting translucent triangles can sort wrongly (no face splitting); the
  depth AOV for stereo holds the opaque surfaces only.

### 3.6 Camera and input

- **Camera:** an orbit camera around the target, with distance / Zoom, theta (azimuth) and
  phi (elevation) as in the other 3D families.
- **Input:**
  - left-drag on empty space orbits and the wheel zooms, as in the 3D families;
  - left-drag on a handle moves the point in the plane facing the camera through it;
  - Shift snaps to the grid, and Alt constrains the move to the nearest axis.

### 3.7 Batch, video and Scene Engine

- **Stills:** everything is in the region snapshot (`--param MathPlane3D…`). The camera
  rides the snapshot's generic `Cam3DFamily` + `Cam3DDistance / Cam3DTheta / Cam3DPhi`
  block, filled for this family the way each 3D family fills it. No new flags.
- **Video:** the existing 3D dolly and `--orbit`. `t` is animatable, so a matrix map plays
  through `--animation` or the Scene Engine.
- **Scene Engine:** camera keys on shots work through `CameraParamBinding`.
- **Command builder:** the live seed carries the snapshot. The parse round-trip test covers it.

### 3.8 Mesh export

Surfaces can be exported through the S9 writers:
- glTF/GLB with the object colour as the base colour, or STL;
- run through `MeshValidator`;
- export only: FF is a mesh exporter, not an editor (S9 framing).

*As built (T6, #1718):*
- **What is exported:** `Engine/Export/MathPlane3DMeshExporter.cs` exports every visible surface
  (explicit, parametric and implicit) after the map at the current t, as the view shows it.
  - Points, vectors, segments, curves, grids and fills are left out.
  - Explicit and parametric surfaces are the drawn cells of `BuildSurface`: two triangles a cell,
    wound as the render winds them, over shared grid vertices. A full grid is therefore one connected,
    consistently oriented sheet: 2n² triangles, with only its 4n rim edges open.
  - Implicit surfaces are the T5 marching-cubes mesh.
  - A map with det < 0 mirrors space, so the winding is reversed and a closed surface still faces
    outward.
- **Formats:**
  - .glb / .gltf go through `GltfMeshWriter`, with area-weighted vertex normals. They are written Y up,
    as the [glTF 2.0 spec](../Resources-Bibliography.md#khronos-gltf-2) defines: (x, y, z) is stored as
    (x, z, -y).
  - Colour: one colour across the surfaces becomes the material's base colour; several become
    per-vertex COLOR_0 with a white base. Alpha is dropped, as in S9.
  - .stl is [binary STL](../Resources-Bibliography.md#loc-stl-binary) with z up, through the S9 writer
    (`WriteStlBinary`, now shared). It carries geometry only.
  - 3MF / OBJ / PLY are not offered. The issue asked for glTF and STL.
- **Check:** `MeshValidator` runs on the result. The UI and batch show one line, for example
  "2 surfaces, 16144 triangles, open (has edges)". A scene without visible surfaces is an error, and no
  file is written.
- **UI:** an "Export" group in the Control Center section holds **Export mesh...**. It is enabled when a
  visible surface exists. A save dialog (.glb / .gltf / .stl) opens and the export runs off the UI
  thread on a copy of the parameters. The result line is grey, and problems are yellow.
- **Batch:** `--math-plane-3d-mesh PATH` (image mode) writes the mesh from the same parameters as the
  still. It is new, because there was no S9 mesh flag to reuse; only `--qr-mesh` exists, and that is
  QR-specific.
  - Another fractal type, or a scene without surfaces, exits 2.
  - The Command builder emits the flag when a snapshot carries a path. The live seed has none, since an
    export path is not view state.

## 4. UI (Control Center only)

A **Math Plane 3D** section, structured like the 2D one:
- **Switch:** a Switch button when another type is showing.
- **Objects:** an objects list with add buttons (+ Point, Vector, Segment, Polyline, Curve,
  Surface).
- **Map:** a 3×3 matrix grid with presets (rotate about x / y / z, shear, scale, project onto a
  plane), plus `t`, Play / Loop and the Linear / Polar choice.
- **Display:** check boxes for det, eigenvectors and lengths, and the grid-plane toggles.
- **Camera:** read-outs and a Reset view button.
- **Scene box:** the scene text, with errors in yellow.
- **Demos:** later.

*As built (T4, #1716):*
- **Where it lives:**
  - `UI.Avalonia/ViewModels/MathPlane3DSectionViewModel.cs` and
    `Views/ControlCenterSections/MathPlane3DSectionView.axaml`: a nav entry of its own,
    "Math Plane 3D", which detaches to its own window (`WindowRole.DetachedMathPlane3DPanel`
    = 27);
  - the pure edits are in `Abstractions/Models/MathPlane3DSceneEdit.cs`.
- **Owned by the shell**, like the 2D section, so canvas editing works before the Control Center
  opens. Both editors share the input controller's single edit slot through
  `PointerEditHandlerChain`: each ignores views of the other type, and the handler that claimed
  a press gets that drag.
- **Camera code:** `MathPlane3DCamera` and `Projected3D` moved from the calculator to
  `Abstractions/Models/MathPlane3DCamera.cs` (same namespace), so the editor projects and
  un-projects exactly as the renderer does. `MathPlane3DCamera.RayDirection` is the inverse
  projection.
- **Every edit is a param change:**
  - scene text, map params and camera params, so regions, `--batch` and the Command builder
    follow with nothing new to wire;
  - an edit is written only if the scene it produces parses again, and the parser's message
    shows in yellow otherwise.
- **Input:**
  - **Handles:** points, vector tips and tails, segment ends and polyline vertices. They are
    hit-tested where they are drawn (through the map), within 9 px, the nearer one winning a
    tie.
  - **Dragging a handle:** the pointer's ray meets the camera-facing plane through the handle's
    drawn place (the view depth is kept), and the point goes back through M⁻¹. A singular
    map doesn't move it.
  - **Shift** snaps to the 2D editor's minor step for the visible width at that depth.
  - **Alt** keeps the largest change from the drag's start The axis is chosen once, when the pointer leaves a 4 px dead zone, and kept while Alt is held (re-choosing it every move made the handle hop between the three axis lines). The read-out shows `[x only]`. The Win32 render window reads Alt with `GetKeyState`, since mouse messages carry no Alt flag.
  - **Left-drag on empty space orbits** with the controller's right-drag rule: π per width or
    height, drag down looks up, φ kept in (0.01, π − 0.01). With "Drag objects" off, every
    left-drag orbits. The wheel and right-drag are unchanged.
  - **Double-click** selects a handle; it never recentres, which has no meaning in 3D.
  - **Delete** removes the selected object.
- **Presets:**
  - quarter turns about x, y and z;
  - shear, scale 2 and stretch;
  - reflect in x-y;
  - project onto x-y.

## 5. Tests (independent invariants)

- **Projection:** a point's pixel equals the pinhole formula computed independently from the
  camera triple. The origin projects to the image centre when it is the target.
- **Occlusion:** a near opaque triangle hides a far one wherever they overlap, whatever the
  draw order (shuffle the triangle list: identical pixels).
- **Translucent:** a translucent plane over an opaque one blends to the expected colour (±1),
  and back-to-front order makes two stacked translucent planes order-independent of input.
- **Lines:** a segment behind a surface is hidden and one in front is drawn. A line lying on the
  surface stays visible (bias).
- **Polar decomposition:**
  - R is orthonormal with det R = 1 and S is symmetric positive definite, both to 1e-12, for
    random M with det > 0;
  - R·S reconstructs M;
  - for a pure rotation, S = I;
  - slerp keeps |q| = 1 and hits both endpoints;
  - the angle grows linearly in t.
- **det:** the parallelepiped's volume, as the triple product of the three mapped unit edges,
  equals det M from the cofactor expansion.
- **Eigenvectors:** M·v = λ·v for every reported pair.
- **Batch:** live, `--param` and Command-builder frames are pixel-identical. Camera keys
  round-trip through `CameraParamBinding`.
- **Mutation checks:** the depth test sense flipped, the translucent sort reversed, a missing
  bias, and slerp replaced by matrix lerp must each fail tests.

## 6. Risks

| Risk | Mitigation |
|---|---|
| CPU raster cost at 4K batch with dense surfaces | `mesh N` caps resolution; row-parallel raster; measured in the slice with a budget test |
| Translucent sort artefacts on intersecting planes | Documented limit; grid planes drawn as lines by default, filled only when asked |
| Line z-fighting on surfaces | Depth bias scaled by pixel footprint; tested |
| Polar decomposition near-singular M | Iteration capped and det ≤ 0 falls back to linear (as 2D); near-singular det > 0 is checked by the orthonormality test |
| Users expect raymarched quality | It is a diagram tool; the 3D fractals keep the raymarchers |

## 7. Prior art and credit

| Source | What FF takes |
|---|---|
| 3Blue1Brown, *Three-dimensional linear transformations* (Essence of linear algebra ch. 5) | The look: space's grid morphing under a 3×3 matrix |
| Catmull 1974, z-buffer (Straßer 1974 independently) | Hidden-surface method for the opaque pass |
| Newell, Newell & Sancha 1972, depth sort | Back-to-front order for the translucent pass |
| Higham 1986, polar decomposition | M = R·S for the polar interpolation |
| Shoemake 1985, quaternion slerp | Interpolating R from the identity |
| Lorensen & Cline 1987, marching cubes | Implicit surfaces (later slice); already credited |

All are added to (or already in) the bibliography in this PR. Straßer's thesis title is not
verified and is listed under *Citations awaiting verification*.

## 8. Later (not in this epic)

- **Scripted 3D scenes:** the step / with engine (MathPlaneScript) is dimension-agnostic in
  its timeline and fold. A 3D action set (`show`, `move` in 3D, `t`, `matrix`, `camera` as
  orbit keys) is a follow-up once the renderer exists.
- **Vector fields** (arrows on a lattice) and flow lines.
- **Planes and lines** as objects (`plane NAME a b c d`, `line NAME point direction`) for
  linear-system pictures.

## 9. Slices

Dependencies are stated per issue; no auto-blocking is configured.

| Slice | Issue | Content | Depends on |
|---|---|---|---|
| T1 | #1713 | `FractalType.MathPlane3D`, params + snapshot + `CameraParamBinding`, scene text (axes, grid planes, ticks, labels, point, vector, segment, polyline), CPU raster core (z-buffer, depth-tested lines, labels), perspective camera, batch round-trip | — |
| T2 | #1714 | Surfaces (explicit, parametric) and space curves; shading; translucent pass; `mesh N` | T1 |
| T3 | #1715 | 3×3 matrix map + `t`, linear and polar (Higham + Shoemake), det parallelepiped, eigenvectors, lengths | T1 |
| T4 | #1716 | Control Center section, orbit input, handle dragging in 3D | T1 (T3 for the map UI) |
| T5 | #1717 | Implicit surfaces F(x, y, z) = 0 via marching cubes | T2 |
| T6 | #1718 | Mesh export of surfaces (glTF/GLB, STL) through the S9 writers + validator | T2 |
| T7 | #1719 | Demos ("math behind 3D fractals": rotations, a KIFS fold as a reflection, scale, det as volume), help, user guide | T1–T4 |

*As built (T7, #1719):*
- **Demos:** `Abstractions/Models/MathPlane3DPresets.cs` holds nine read-only demos, each a scene, a map,
  t, interpolation, camera and zoom. `Apply` sets every Math Plane 3D param, and `ParamArgs()` is the
  `--param` list, with the camera on the shared `Cam3DDistance / Cam3DTheta / Cam3DPhi` keys.
  - **Rotations:**
    - about z at t = 1/2 twice: polar keeps |i| = 1 at 45°, while linear shrinks it to 0.71 (det 1/2);
    - about x and about y.
  - **The KIFS fold as a reflection:** FF's Sierpinski fold, the plane x + y = 0, det −1. A test
    checks it against the fold code itself.
  - **Uniform scale:** the Mandelbox step, scale 2, det 8.
  - **det as volume:** det 3.
  - **Surfaces:** the saddle z = x² − y², and a parametric torus (tube 0.6, ring 2).
- **UI:** a **Demos** group at the top of the Control Center section (list, Load, the idea). Loading a
  demo stops Play, applies the demo, sets the zoom and renders once. The built-ins never change.
- **Help:** `HelpTextBundle.MathPlane3DText`, registered in both help trees next to Math Plane.
- **Guide:** `Docs/User/MathPlane3D-Guide.md`, linked from `_Index.md`. A test checks that it names
  every demo.
- **Tests:** each demo:
  - parses and renders headless;
  - renders the same through its `--param` list and through the Command builder (no gaps);
  - is checked against an independent fact: its det, a rotation's orthonormality and fixed axis, the
    halfway lengths, the fold against `SierpDE`, the saddle's equation, the torus's tube radius.
