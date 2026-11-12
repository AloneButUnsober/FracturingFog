# Math Plane 3D: the math behind the 3D fractals

**Math Plane 3D** is a render mode that draws space with a z axis. It is the 3D sibling of the
[Math Plane](MathPlane-Guide.md). You can place points, vectors, curves and surfaces, and move all of
space with a 3×3 matrix. FF's 3D fractals (Mandelbulb, Mandelbox, quaternion Julia sets, KIFS) are built
from these pieces: rotations, reflections ("folds") and scalings. This mode shows each piece on its own. It
is not a CAD tool or a general 3D modeller.

The look of space under a matrix follows 3Blue1Brown's *Three-dimensional linear transformations* (Grant
Sanderson, *Essence of linear algebra*, chapter 5). Polar frames use Higham's polar decomposition and
Shoemake's quaternion slerp. Sources are listed in the [bibliography](../Resources-Bibliography.md). The
design is in [MathPlane3D-DesignPlan.md](../Technical/MathPlane3D-DesignPlan.md).

Open it from **Control Center → Math Plane 3D**. Press **Switch to Math Plane 3D** if another fractal is
showing.

---

## 1. Demos

The **Demos** box at the top loads a built-in example. Each demo shows a single idea, and its description
appears under the list. Loading a demo replaces the scene, the map, t, the camera and the zoom. The built-in
demos never change. To keep an edited demo, save the view as a region.

| Demo | The idea |
|---|---|
| Rotation about z: polar, halfway | A quarter turn about z, stopped at t = ½. Polar frames turn the rotation's angle: i and j have turned 45° and keep length 1. |
| Rotation about z: linear, halfway | The same turn at t = ½ with linear frames: I + t(R − I) is not a rotation, so i and j shrink to length 0.71 (det ½). |
| Rotation about x | j goes to k and k goes to −j. x is the only real eigenvector, and det 1 keeps volumes. |
| Rotation about y | k goes to i and i goes to −k. The matrix's columns are where i, j and k land. |
| KIFS fold: a reflection | FF's Sierpinski fold, "if x + y < 0 then (x, y) → (−y, −x)", is the reflection in the plane x + y = 0, applied to the half of space behind it. det −1: the cube turns inside out. |
| Uniform scale: the Mandelbox step | Each Mandelbox iteration ends with z → s·z + c, with s = 2 by default. Lengths double and volumes grow 8 times (det 8). |
| det is the volume of the cube's image | The unit cube's image is the parallelepiped on the matrix's columns. Its volume is \|det\| = 3. |
| Saddle z = x^2 - y^2 | The surface curves up along x and down along y. The origin is a saddle point. |
| Parametric torus | A circle of radius 0.6 swept round a circle of radius 2. u goes round the ring and v round the tube. |

The rotation, fold and scale demos start at their final frame, except the two halfway ones. Press **Play**
in the Map group to watch t run from the identity (0) to the matrix (1).

### Where these come from in the fractals

- **Rotations.** The 3D camera turns space. The rotated KIFS folds (Octahedron, Dodecahedron, Mandelbox-rot)
  rotate it between folds.
- **Folds.** A KIFS fold reflects the half of space on one side of a mirror plane onto the other side. On
  that half it is a reflection with det −1. The fold scheme is Knighty's *Kaleidoscopic IFS*
  (fractalforums.com, 2010).
- **Scale.** The Mandelbox (Tom Lowe, fractalforums.com, 2010) folds, then scales by s, then adds c. The
  KIFS scale about a corner (s = 3 for the Menger sponge, 2 for the Sierpinski tetrahedron) is the same step
  about another centre.

---

## 2. Objects

The **Objects** group adds objects:
- **+ Point**, **+ Vector**, **+ Segment** and **+ Polyline**;
- **+ Curve**: a space curve (x(t), y(t), z(t)). It starts as a helix.
- **+ Surface**: an explicit surface z = f(x, y). It starts as a gentle wave.
- **+ Implicit**: an implicit surface F(x, y, z) = 0, meshed by marching cubes. It starts as the unit
  sphere.

Pick an object in the list to edit its name, formulas, numbers, colour and visibility. Bad input is shown
in yellow, and the scene is left unchanged.

Surfaces have a **mesh N** setting: N cells a side, from 2 to 256, default 64. A colour with alpha (#RRGGBBAA)
draws the surface translucent.

On the render:
- **Drag a handle** (a point, a vector's tip or tail, a segment end or a polyline vertex) to move it.
  - **Shift** snaps the handle to the grid.
  - **Alt** keeps the drag on one axis, the one you first moved along most.
- **Left-drag on empty space** orbits the camera. Untick **Drag objects on the render** to make every left-drag orbit.
- **Double-click** a handle to select it.
- **Delete** removes the selected object.

## 3. Linear map

The **Map** group holds a 3×3 matrix M. Its columns are where i, j and k land.
- **t** runs from the identity (0) to M (1). **Play** and **Loop** animate it.
- **Linear** frames are I + t(M − I).
- **Polar** frames split M into a rotation and a stretch, and turn the rotation at a steady angle. A turn
  then stays rigid all the way. M needs det > 0 for this; otherwise linear frames are drawn.
- **Presets:** quarter turns about x, y and z; shear; scale 2; stretch; reflect in x-y; project onto x-y.
- **Read-outs:**
  - **det**: the unit cube's image as a shaded parallelepiped, labelled with det.
  - **eigen**: the real eigenvectors as lines through the origin, with their eigenvalues.
  - **lengths**: vector lengths after the map.

## 4. Camera

- **Orbit** with left-drag on empty space or with right-drag.
- **Zoom** with the wheel.
- **Reset view** returns to the default orbit: distance 9, looking at the origin.

The camera is part of the parameters, so regions, the Scene Engine and video keyframes carry it.

## 5. Export

**Export mesh...** saves the visible surfaces as the view shows them, after the map at the current t.
- **.glb / .gltf** keep the surface colours. These files are Y up, as glTF defines.
- **.stl** is shape only, z up, for printing.

The mesh is checked after it is written, and the result line says whether it is closed. A scene with no
visible surface has nothing to export.

## 6. Scene text

The **Scene text** box shows the whole scene as it is stored. The same text is what
`--param MathPlane3DScene=...` takes. Statements are separated by `;` or new lines:

```
v1
axes on|off   ticks on|off   grid xy|xz|yz ... | grid off   fill xy|xz|yz ... | fill off
point    NAME x y z                       [options]
vector   NAME x y z [from x0 y0 z0]       [options]
segment  NAME x1 y1 z1 x2 y2 z2           [options]
polyline NAME x1 y1 z1 x2 y2 z2 ...       [options]
curve    NAME "x(t)" "y(t)" "z(t)" tmin tmax                     [options]
surface  NAME explicit "f(x, y)" [xmin xmax ymin ymax]            [options] [mesh N]
surface  NAME parametric "x(u,v)" "y(u,v)" "z(u,v)" umin umax vmin vmax  [options] [mesh N]
surface  NAME implicit "F(x, y, z)" [xmin xmax ymin ymax zmin zmax]       [options] [mesh N]
shading smooth|flat     det on|off     eigen on|off     lengths on|off
options: #RRGGBB  #RRGGBBAA  hidden  nolabel
```

## 7. Batch

Every setting is a `--param`, so any view renders headless. For example, the torus demo:

```bash
FracturingFog --batch --fractal MathPlane3D --x 0 --y 0 --zoom 1 --out torus.png --param "MathPlane3DScene=surface torus parametric \"(2 + 0.6*cos(v))*cos(u)\" \"(2 + 0.6*cos(v))*sin(u)\" \"0.6*sin(v)\" 0 6.283185307179586 0 6.283185307179586 #E69F00 mesh 48" --param Cam3DDistance=10
```

- `MathPlane3DScene=@file` reads the scene from a file. This avoids quoting trouble in cmd or PowerShell.
- `--math-plane-3d-mesh PATH` also writes the surfaces as a .glb, .gltf or .stl mesh.
- The Control Center's **Command** builder writes the full command for the current view.
