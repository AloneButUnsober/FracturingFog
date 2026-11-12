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
demos never change: to keep an edited demo, save it to **My scenes**.

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
| Script: multiply by 1 + i | A script: show a vector, then play the map z → (1 + i) z. Every vector turns 45° and grows by √2. |
| Script: period doubling | A script: the cobweb builds up, then c slides from −0.5 to −1.9 through a 2-cycle and a 4-cycle to chaos. |
| Script: c leaves the set | A script: the orbit of 0 stays bounded for c inside M, then escapes as c moves out past the edge. |
| Script: the rabbit orbit | A script: the rabbit's orbit is revealed step by step, then the camera moves in on its 3-cycle. |

The **Script:** demos are timed sequences: load one, then press **Play** in the Script group (see
[Scripts](#scripts)).

### My scenes

The **My scenes** box, under Demos, keeps your own scenes.

- **Save:** type a name and press **Save**. It stores the scene text, the map, c, the backdrop and the view.
  Saving under a name you already used overwrites that scene. A built-in demo's name is refused, so the demos
  stay as they are.
- **Load:** pick a scene and press **Load**. Like a demo, it replaces every Math Plane setting.
- **Rename / Delete:** pick a scene, then type the new name and press **Rename**, or press **Delete**.
- **Share:** **Export…** saves the selected scene to a `.json` file; **Export all…** saves every one.
  **Import…** adds the scenes from such a file:
  - **Name clashes:** a name you already use, or a demo's name, gets a number added ("Orbit (2)"), so
    nothing is overwritten.
  - **Unreadable entries:** they are skipped, and the rest still import.
  - **Damaged files:** files saved in another encoding, or with comments or trailing commas, are repaired
    on the way in.

Saved scenes are kept in `mathplanescenes.json` in FF's data folder. A saved region also keeps every Math
Plane setting, if you want the scene in the region list instead.

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

**Shortcut:** press **O** in the render window to pin the point under the pointer. You don't have to move the
pointer to the button, which would drag the point along the way. Press O again to re-pin somewhere else.

### On 3D fractal views

The same toggle works on the 3D raymarched fractals. It draws:
- **Axes:** x, y and z, in the fractal's own axes, where y is up.
- **The bailout sphere:** shown as its outline, when its radius is in view.
- **The orbit of a point:** under the fractal's own step (the same arithmetic as its renderer), for Mandelbulb,
  Mandelbox, Kifs, QuaternionJulia and QuaternionMandelbrot. The quaternion orbits are 4D, so w is dropped.
  Other 3D types get the axes only.

Lines are drawn with the renderer's own camera:
- **In front of the fractal:** solid.
- **Behind its surface, or inside it:** dimmed and dashed, so an orbit inside the set stays readable.

Choosing the point:
- **Follow pointer** uses the point on the fractal's surface under the pointer. The sky has none.
- **Pin last point** freezes that point.
- You can type a pin as `x y z`.

A pinned 3D overlay is part of the picture:
- **Exports** draw it with the export's own camera and depth.
- **`--batch`:** `--math-overlay-3d X,Y,Z[,STEPS]` draws it on a 3D still, and the Command builder emits it
  from the pin. For example:
  `--batch --fractal Mandelbulb --x 0 --y 0 --zoom 1 --math-overlay-3d 0.6,0.4,0.5,20 --out bulb.png`
- It cannot be combined with the 2D `--math-overlay`, and a 2D fractal type is refused.

Where it isn't drawn yet:
- **Video and slideshow frames:** `--math-overlay-3d` is for stills only, and a video or slideshow command
  with it is refused (#1746).
- **Stereo:** the overlay is not drawn on side-by-side stereo frames.
- **Thin-lens depth of field:** no depth is available, so hidden parts are not dimmed. A yellow note says so.

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
step DURATION [ease none|in|out|inout] ACTION ...    with [DURATION] [ease ...] ACTION ...
  actions: wait | show NAME... | hide NAME... | t V | matrix A B C D | c RE IM | move NAME X Y
           draw NAME | orbit N | cobweb N | phase on|contours|off | caption "TEXT" | camera X Y [ZOOM]
options after an object: #RRGGBB  hidden  nolabel
```

### Scripts

`step` and `with` lines turn a scene into a timed sequence, like the animations in 3Blue1Brown's videos.
The step model (steps one after another, groups played together, each with a duration and an easing)
follows Manim, the engine those videos are made with.

```
step DURATION [ease none|in|out|inout] ACTION ...    starts when the previous step ends
with [DURATION] [ease ...] ACTION ...                runs alongside the step before it
```

- **Timing:** a `with` without a duration or ease takes its step's. A step lasts as long as its longest
  member, and the script's length is the end of its last step.
- **Easing:** `inout` (smooth start and stop) is the default. `none` is linear. `in` starts slowly and
  `out` stops slowly.
- **Values:** each step moves a value from wherever the earlier steps left it to its target.
- **Overlaps:** two steps changing the same thing at the same time are an error.

| Action | What it does over the step |
|---|---|
| `wait` | Nothing: a pause. |
| `show NAME...` / `hide NAME...` | Fades objects in or out. Mark an object `hidden` to start it invisible. |
| `move NAME X Y` | Moves a point (or a vector's tip, keeping its tail; or a whole segment or polyline) to (X, Y). |
| `draw NAME` | Draws a curve, segment, polyline or vector along its length. Meant for objects marked `hidden`. |
| `t V` | Moves the map's progress t to V. |
| `matrix A B C D` | Moves the matrix to [[A, B], [C, D]]. It shows only when the map mode is Matrix. |
| `c RE IM` | Moves c, which also moves the orbit, the Julia backdrop and `z^2 + c`. |
| `orbit N` / `cobweb N` | Reveals the orbit's or cobweb's steps up to N, one at a time, starting from the scene's own `orbit` / `cobweb` line. A cobweb needs its `cobweb x0 ...` line. |
| `phase on`, `phase contours`, `phase off` | Fades the phase portrait in or out. The scene needs a `map`. |
| `caption "TEXT"` | Shows a caption at the bottom, cross-fading from the previous one. `caption ""` fades it out, and `\n` starts a new line. |
| `camera X Y [ZOOM]` | Pans the view by (X, Y) and zooms by ZOOM (default 1), on top of your own view. `camera 0 0` goes back. |

**Playing a script:** when the scene has steps, a **Script** group appears under My scenes.
- **Controls:** Play / Pause, Restart and Loop, or drag the time slider. Its ticks mark where steps start and
  end.
- **Step list:** each step with its time span. The steps running now are bold, and clicking one jumps to its
  start.
- **Editing:** dragging objects on the canvas works at 0 s only, so press **Restart** first.
- **Time setting:** the moment drawn is the setting `MathPlaneScriptTime` (seconds). At 0 you see the scene
  as written.

**Making a video:**
- **Batch:** `--mode video --video-motion script` renders the script, one frame per 1 / fps (see
  [Batch](#8-batch)). The Script group's **Video command** button loads that command into the Command panel.
- **Scene Editor:** a shot on a scripted region plays the script on the shot's own time.

**Example:**

```
v1
vector i 1 0; vector j 0 1
vector z 2 1 #009E73 hidden
step 1 caption "The basis"
step 1 show z
with caption "z = 2 + i"
step 2 camera 1 0.5 1.5
step 1 caption ""
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
| `MathPlaneScriptTime` | the moment of the scene's script drawn, in seconds (0 = the scene as written) |

**Math overlay:** for the overlay on a 2D view, add `--math-overlay RE,IM[,STEPS]`.

**Script video:** a scene with `step` lines renders as a video of its script.

```bash
FracturingFog --batch --mode video --fractal MathPlane --x 0 --y 0 --zoom 0.35 --param MathPlaneScene=@scene.txt --video-motion script --fps 30 --out script.mp4
```

- **Timing:** frame k shows the script at k / fps, and the last frame is the script's end. Leave out
  `--seconds` to get the script's own length.
- **Auto:** `--video-motion auto` picks `script` for a scene with steps.
- **From the app:** the Script group's **Video command** button loads this command into the Command panel.

**In the Scene Editor:** save the scripted scene as a region and use it in a shot.
- **Play script:** the shot plays the script on its own time, live and in export. Turn it off to hold
  the region's saved script time.
- **Length:** a new shot on such a region takes the script's length.

**y axis:** Math Plane's y axis points up, so `--y` is minus the world y of the view centre.

**Command builder:** the Control Center **Command** builder writes the full command for the current view.
