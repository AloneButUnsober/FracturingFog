# Math Plane: the math behind the fractals

**Math Plane** is a render mode that draws a Cartesian plane. You can place points, vectors and curves on it,
transform it with a matrix or a complex map, and draw orbits and cobwebs over a fractal. It is built to explain
the ideas behind the fractals FF renders. It is not a general computer-algebra system.

The look of the grid morphs follows 3Blue1Brown's *Essence of linear algebra* (Grant Sanderson). The phase
portraits follow Elias Wegert's *Visual Complex Functions*. Sources are listed in the
[bibliography](../Resources-Bibliography.md); the design is in
[MathPlane-DesignPlan.md](../Technical/MathPlane-DesignPlan.md).

Open it from **Control Center → Math Plane**. Press **Switch to Math Plane** if another fractal is showing.

---

## 1. Demos

The **Demos** box at the top loads a built-in example. Each one shows a single idea, and its description
appears under the list. Loading a demo replaces the scene, the map, c, the backdrop and the view. The built-in
demos never change: to keep an edited demo, save it as a region.

| Demo | The idea |
|---|---|
| Multiply by 1 + i | Multiplying by a complex number turns and scales: 45° and √2. Its matrix is [[1, −1], [1, 1]], with det = 2. |
| z^2 doubles angles | Squaring squares the length and doubles the angle (30° and 1.2 become 60° and 1.44). |
| Phase portrait of z^2 + c | Points coloured by the angle of f(z). Every colour meets at each zero of f. |
| Jacobian: f'(z) is the local map | Near z, a complex map is multiplication by f'(z): a small square turns and scales. |
| Orbit of c inside M | For c in the main cardioid, the orbit of 0 spirals into a fixed point. |
| Orbit of c outside M | For c = 0.5 + 0.5i, the orbit leaves \|z\| ≤ 2 at step 5. |
| Period 2: c = -1 | The orbit goes 0, −1, 0, −1 …, so c is in the period-2 bulb. |
| Period 3: the rabbit | The orbit closes a triangle, drawn over the Julia set of the same c. |
| Cobweb: fixed point / period 2 / period 4 / chaos | The real map x → x² + c at c = −0.5, −1, −1.3 and −1.9: period doubling along the real axis of the Mandelbrot set. |
| Shear, Rotation by 90 degrees, Projection | Three 2×2 matrices, with det and eigenvectors. |

## 2. Objects

Use **+ Point**, **+ Vector**, **+ Segment**, **+ Polyline**, **+ Function**, **+ Parametric**, **+ Polar**,
**+ Implicit** and **+ Region** to add objects.

**On the canvas:**
- Drag a handle to move it. Hold **Shift** to snap to the grid.
- Double-click an empty spot to add a point there.
- **Delete** removes the selected object.

**In the list:** select an object to edit its name, colour, numbers or expression.

Expressions use the equation language, with these variables:

| Variable | Used by |
|---|---|
| `x` | functions y = f(x) |
| `t` | parametric curves (x(t), y(t)) |
| `theta` | polar curves r(θ) |
| `x, y` | implicit curves F(x, y) = 0 and regions (shaded where the condition holds) |

## 3. Linear map

The **Linear map** group sets the 2×2 matrix M (its columns are where î and ĵ land), with presets.

- **t** runs from the identity (t = 0) to M (t = 1). **Play** animates t, and **Loop** plays back and forth.
- **Interpolation:**
  - *Linear* is the usual grid morph.
  - *Polar* rotates and stretches separately, so a rotation turns instead of shrinking half-way. It needs det > 0.
- **det** shades the image of the unit square and prints its area.
- **Eigenvectors** draws the real eigenvector lines, with their eigenvalues.
- **Lengths** prints each vector's length after the map.

## 4. Complex map

The **Complex map** group applies f(z) to the plane: z → (1 − t)·z + t·f(z).

- **Map:** pick a preset (z², z² + c, 1/z, exp z, i z) or type f in z and c. c is set in this group, and it is
  shared with the orbit.
- **Phase portrait:** colours every point z by the angle of f(z).
  - The colour wheel uses colour-blind-safe Okabe–Ito colours.
  - **Contours** steps the brightness where |f| crosses a power of 2.
  - Zeros and poles are where every colour meets.
- **Probe:** a small square at z and its image under the map, with f'(z) as a scale |f'| and a turn arg f'.
- **Pointer:** the pointer read-out also shows f'(z) at the pointer.
- **Not mapped:** regions stay in place, and det and eigenvectors apply to matrices only.

## 5. Fractal bridge

| Setting | What it does |
|---|---|
| **Backdrop** | Draws an escape-time fractal under the scene, at the same view. *Julia* draws the Julia set of c, so dragging c moves it. |
| **Orbit of 0** | Draws 0, z₁, z₂ … under the scene's map (z² + c if there is none), with the escape circle \|z\| = 2. Drag the white **c** handle. The read-out gives the step at which the orbit escapes, or the period it settles to. |
| **Cobweb** | Draws y = f(x), the line y = x, and the staircase from x0. f reads x and c, where c is the real part of the plane's c. |

These stay in the plain plane: a map does not move them.

## 6. Math overlay on other views

Use **Control Center → View → Math overlay** on a normal Mandelbrot or Julia view. It draws the axes and the
orbit of one point.
- On a **Mandelbrot** view, the point is c.
- On a **Julia** view, the point is z0.
- Other families get the axes only.

**Follow pointer** tracks the pixel under the pointer. **Pin last point** freezes it. A pinned orbit is
included when you export an image.

## 7. Scene text

Everything in sections 1–5 is stored as one line of text, which you can see and edit in the **Scene** box.
Statements are separated by `;` or newlines.

```
v1
axes on|off   grid on|off   ticks on|off   labels auto|offset|fixed:N|sci:N
det on|off    eigen on|off  lengths on|off
point NAME x y            vector NAME x y [from x0 y0]
segment NAME x1 y1 x2 y2  polyline NAME x1 y1 x2 y2 ...
curve NAME explicit "f(x)" [xmin xmax]
curve NAME parametric "x(t)" "y(t)" tmin tmax
curve NAME polar "r(theta)" [thetamin thetamax]
curve NAME implicit "F(x, y)"
region NAME "condition(x, y)"
map "f(z)"   phase off|on|contours   probe x y
orbit N      cobweb x0 [N] ["f(x)"]
options after an object: #RRGGBB  hidden  nolabel
```

## 8. Batch

Each setting is a `--param`. Put the scene in a file, so that cmd and Windows PowerShell keep its double
quotes:

```bash
FracturingFog --batch --fractal MathPlane --x -0.5 --y 0 --zoom 0.6 --param MathPlaneScene=@scene.txt --param MathPlaneBackdrop=1 --param MathPlaneCRe=-1 --param MathPlaneCIm=0 --out orbit.png
```

| Key | Meaning |
|---|---|
| `MathPlaneScene` | the scene text (or `@file`) |
| `MathPlaneMapMode` | 0 none, 1 matrix, 2 complex |
| `MathPlaneM11 … MathPlaneM22` | the matrix |
| `MathPlaneT` | map progress, 0 to 1 |
| `MathPlaneInterpolation` | 0 linear, 1 polar |
| `MathPlaneCRe`, `MathPlaneCIm` | c |
| `MathPlaneBackdrop` | 0 none, 1 Mandelbrot, 2 Julia, 3 Burning Ship, 4 Tricorn, 5 Multibrot, 6 Phoenix, 7 Magnet 1, 8 Magnet 2, 9 Glynn, 10 Spider |

**Math overlay:** for the overlay on a 2D view, add `--math-overlay RE,IM[,STEPS]`.

**y axis:** Math Plane's y axis points up, so `--y` is minus the world y of the view centre.

**Command builder:** the Control Center **Command** builder writes the full command for the current view.
