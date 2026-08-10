# User Bulb 3D — Guide

Fracturing Fog's User Bulb engine is the 3D analogue of User Equation. Author a per-iteration step function in either full C# (Roslyn) or a restricted DSL (Sandbox); the engine handles raymarching, distance estimation, lighting, AO, fog, and surface normals.

This guide covers the dialog, the Vec3 / Quat APIs, distance-estimation tradeoffs, the chain editor, mesh export, the Sandbox DSL grammar, and 14 ready-to-paste example bodies.

> Companion pages: [User Index](_Index.md) · [CalcGen User Guide](CalcGen-UserGuide.md) · [Fractal Equation Design Guide (Technical)](../Technical/FractalEquation-DesignGuide.md)

![Mandelbulb 3D distance-estimated render at p = 8 with the "New 3D" Phong palette — home view, 1600 × 1000.](../Images/fractals/mandelbulb.png)

---

## A friendly tour

The 2-D fractals you know — Mandelbrot, Julia, Burning Ship — all live in the complex plane: every
point has a *real* part and an *imaginary* part. The **User Bulb** moves that idea into three
dimensions, so every point has an `x`, a `y`, and a `z`. Run a recipe over and over on every point;
the points that stay close to home form a shape in space. Light it from a direction and you get a
sculpture.

The most famous shape of this kind is Daniel White's **Mandelbulb**, which uses what mathematicians
call *triplex* algebra. It looks like this:

$$
\vec{v}_{n+1} = \vec{v}_n^{\,p} + \vec{c}
\quad\text{where}\quad
\vec{v}^{\,p} = r^p \big(\sin(p\theta)\cos(p\varphi),\; \sin(p\theta)\sin(p\varphi),\; \cos(p\theta)\big)
$$

Power `p = 8` is the classic. Fracturing Fog ships that recipe as the default, *plus* a full editor
that lets you swap it for anything you want to try. You can write it in real C#, or in a tiny DSL
that protects you from making the app crash.

| ![Mandelbulb p = 4 — soft jelly silhouette.](../Images/fractals/mandelbulb-p4.png) | ![Mandelbulb p = 8 — classic spiked sphere.](../Images/fractals/mandelbulb.png) | ![Mandelbulb p = 12 — denser quill pattern.](../Images/fractals/mandelbulb-p12.png) |
|:--:|:--:|:--:|
| **p = 4** — soft, organic, jelly-like. | **p = 8** — the canonical look. | **p = 12** — finer detail, quill-like. |

### Worked example — "Spin a Mandelbulb on its axis for a video loop"

1. Switch the toolbar **Type** dropdown to **User Bulb (3D)**.
2. Press **`R`** to reset to the default Mandelbulb.
3. The default body is already correct — no editing needed.
4. Open Floating Menu → **Params** → scroll to **Animation**. Enable the animated `t` parameter,
   period `10` seconds.
5. In the **Camera** group, set **Theta animation** = `t * 360°` so the camera rotates around once
   per period.
6. Floating Menu → **Video** → duration `10` seconds, framerate `30 fps`, output `.mp4`.
7. Click **Render**.

The result is a perfectly looping spin of the bulb — drop it on a Discord channel as an animated
banner.

### Worked example — "Try the Burning Ship in 3-D"

1. Switch the toolbar **Type** to **User Bulb (3D)**.
2. Floating Menu → **Params** → editor opens.
3. Paste this body:

```csharp
// 3-D Burning Ship: absolute-value all three components before the bulb-power step.
var w = new Vec3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
return BulbPow(w, 8.0) + c;
```

4. Hit **Compile & Load**. The render switches in ~500 ms.

You will see a much rougher, almost rocky shape — sharp masts, jagged hulls — because the
absolute-value step makes the map non-smooth.

---

## Table of Contents

1. [Open the Editor](#1-open-the-editor)
2. [Step Signature](#2-step-signature)
3. [Vec3 API](#3-vec3-api)
4. [Quat API (4D mode)](#4-quat-api-4d-mode)
5. [Algebra Mode](#5-algebra-mode)
6. [DE Modes](#6-de-modes)
7. [Backends (CPU vs GPU)](#7-backends-cpu-vs-gpu)
8. [Camera + Lighting](#8-camera--lighting)
9. [Render Knobs](#9-render-knobs)
10. [Param Bank](#10-param-bank)
11. [Animation (global t)](#11-animation-global-t)
12. [Julia Mode](#12-julia-mode)
13. [Color Drivers](#13-color-drivers)
14. [Chain Editor](#14-chain-editor)
    - 14.1 [Hybrid primitives (Mandelbulber-style)](#141-hybrid-primitives-mandelbulber-style)
15. [Save / Load / Promote](#15-save--load--promote)
16. [Mesh Export (OBJ / STL)](#16-mesh-export-obj--stl)
    - 16.1 [Export knobs](#161-export-knobs)
    - 16.2 [Getting a detailed mesh (not a blob)](#162-getting-a-detailed-mesh-not-a-blob)
    - 16.3 [Recommended recipes](#163-recommended-recipes)
    - 16.4 [Mesh export troubleshooting](#164-mesh-export-troubleshooting)
17. [Example Gallery](#17-example-gallery)
18. [Pitfalls + Troubleshooting](#18-pitfalls--troubleshooting)
19. [Sandbox DSL Compiler](#19-sandbox-dsl-compiler)

---

## 1. Open the Editor

Fractal Type → **User Bulb (3D)** → Floating Menu → **Params** button (or click the gear icon in the toolbar).

Modeless dialog. Auto-compiles ~500 ms after the last keystroke. Errors render in red below the editor. Camera, lighting, iter count, epsilon, bailout, Jacobian h, params, animation, color driver, lighting weights, and view knobs update without recompiling — only changes to source body / algebra / chain steps / param names trigger a fresh compile.

---

## 2. Step Signature

The compiled body is wrapped as:

```csharp
// Vec3 mode
Vec3 Step(Vec3 z, Vec3 c, int n, double[] p)

// Quat mode
Quat Step(Quat z, Quat c, int n, double[] p)
```

| Param | Description |
|---|---|
| `z` | Previous iterate. Starts at Zero on iter 0. |
| `c` | Per-pixel constant. Vec3 in 3D mode; (px.X, px.Y, px.Z, SliceW) in Quat mode. Replaced with JuliaC in Julia mode. |
| `n` | 0-based iteration index. |
| `p` | Named param vector. Trailing slot reserved for global animation `t`. Each Params row exposes a bare local `double <name>`. |

Body must `return` a Vec3 (or Quat in Quat mode):

```csharp
// Single expression form
Vec3.Pow(z, 8) + c

// Multi-statement form
var v = Vec3.Pow(z, 8);
return v + c;
```

---

## 3. Vec3 API

`FracturingFog.Models.Vec3` — readonly record struct, 3 components, double precision.

```csharp
// Fields
double X, Y, Z

// Properties
double Length          // sqrt(X² + Y² + Z²)
double LengthSquared   // X² + Y² + Z²

// Operators
+ - (unary -)  scalar *  scalar /

// Constants
Vec3.Zero, Vec3.One

// Static geometric / arithmetic
Vec3.Dot(a, b) → double
Vec3.Cross(a, b) → Vec3
Vec3.Sin(v), Vec3.Cos(v), Vec3.Sinh(v), Vec3.Cosh(v), Vec3.Exp(v), Vec3.Abs(v)

// Static fractal-authoring helpers
Vec3.Pow(v, n)               // triplex spherical power (Mandelbulb)
Vec3.Rot(v, axis, angle)     // Rodrigues rotation
Vec3.BoxFold(v, limit)       // Tglad fold (Mandelbox component)
Vec3.SphereFold(v, rMin, rMax)  // inversion fold
Vec3.AbsX(v), Vec3.AbsY(v), Vec3.AbsZ(v)  // asymmetric folds
Vec3.Mod(v, period)          // tile per axis
Vec3.SMin(a, b, k)           // C¹ smooth-min DE blend (scalars)
Vec3.ToSpherical(v) → (r, theta, phi)
Vec3.FromSpherical(r, theta, phi) → Vec3

// Instance
v.Normalized()
```

Construct with `new Vec3(x, y, z)`.

---

## 4. Quat API (4D mode)

`FracturingFog.Models.Quat` — readonly record struct, 4 components `(W, X, Y, Z)`, double precision. `W` is the real part; `X, Y, Z` are the `i, j, k` imaginary axes. In Quat mode the compiled body is `Quat Step(Quat z, Quat c, int n, double[] p)` and every helper below is in scope under Roslyn (the assembly imports `FracturingFog.Models`).

> [!IMPORTANT]
> Everything in this API is **quaternion algebra**, not component-wise arithmetic. `Quat.Sin(q)` is the true quaternion sine (it treats `q` as a rotation-carrying number), **not** `sin` applied to each of the four fields. That is the whole point of Quat mode — the maps behave like a genuine 4-D analogue of the complex plane. If you want per-axis math, use the `Vec3` helpers on `q.ToVec3()` instead.

```csharp
// Fields
double W, X, Y, Z

// Properties
double Length          // sqrt(W² + X² + Y² + Z²)
double LengthSquared   // W² + X² + Y² + Z²

// Operators
+  -  (unary -)         // component-wise
q * s   s * q           // scalar scale
a * b                   // Hamilton product (quaternion multiply — NOT commutative)

// Constants
Quat.Zero               // (0, 0, 0, 0)
Quat.Identity, Quat.One // (1, 0, 0, 0)
Quat.Pi                 // (π, 0, 0, 0)
Quat.HalfPi             // (π/2, 0, 0, 0)

// Structure / conversion
q.Conjugate() → Quat            // (W, -X, -Y, -Z)
Quat.Dot(a, b) → double
Quat.FromVec3(v, w = 0) → Quat  // lifts a Vec3 into the imaginary axes
q.ToVec3() → Vec3               // drops W, keeps (X, Y, Z)
Quat.QuatAxis(q) → Quat         // unit "imaginary axis" of q (see note below)

// Algebra
Quat.Pow(q, exp) → Quat         // real exponent — see semantics below
Quat.Sqrt(q) → Quat             // = Pow(q, 0.5)
Quat.Exp(q), Quat.Log(q) → Quat // principal-axis exp / log
Quat.Inverse(q) → Quat          // q⁻¹ = conj(q) / |q|²
Quat.Scale(q, s) → Quat         // same as q * s, static form

// Trig (quaternion-valued)
Quat.Sin, Quat.Cos, Quat.Tan
Quat.Csc, Quat.Sec, Quat.Cot            // reciprocals: 1/Sin, 1/Cos, Cos/Sin

// Hyperbolic
Quat.Sinh, Quat.Cosh, Quat.Tanh
Quat.Csch, Quat.Sech, Quat.Coth

// Inverse trig / inverse hyperbolic
Quat.Asin, Quat.Acos, Quat.Atan
Quat.Asinh, Quat.Acosh, Quat.Atanh
```

Construct with `new Quat(w, x, y, z)`.

### 4.1 `Pow` semantics

`Quat.Pow(q, exp)` picks its algorithm from the exponent:

- **Non-negative integer** exponent → exact repeated Hamilton self-multiply. `Pow(q, 0) = Identity`, `Pow(0, n) = 0` for `n > 0`. This is the fast, exact path — prefer integer powers when you can.
- **Fractional or negative** exponent → the analytic form `q^exp = exp(exp · log q)`, evaluated on `q`'s principal axis. This is what makes `Sqrt`, `Asin`, `Acos`, `Asinh`, and `Acosh` work (they all route through `Sqrt`).

### 4.2 The escape contract — these ops never throw

The quaternion DE hot loop has **no** `try`/`catch` by design (it compiles once, smoke-tests the delegate with finite inputs, then trusts it). A throw whose trigger depended on a runtime value the smoke test never hit would crash the whole render. So every op here returns a **non-finite quaternion** for undefined inputs (`Pow(0, -1)`, `Log(0)`, a divide by a zero-norm quat, …) instead of throwing. The loop's `!double.IsFinite` guard turns that non-finite result into a cleanly *escaped* pixel. You therefore never need to guard denominators inside a Quat-mode body the way you do in Vec3 mode — a bad value just paints as "outside the set."

> [!NOTE]
> `Quat.QuatAxis(q)` returns the unit imaginary axis that makes `Asin`/`Acos`/`Atan` well-defined — it is the direction `q` "points" in the 3-space of `{i, j, k}`. When `q` is (numerically) a pure real number there is no natural axis, so the helper falls back to the `x`-axis by convention. If an inverse-trig map shows a hard seam along the x-axis, that fallback is the cause; rotate your input or add a small imaginary bias to avoid the pure-real degeneracy.

In Quat mode the raymarched 3-space slice comes from the camera ray's `(x, y, z)` plus the user-chosen **Slice W** coordinate. Changing Slice W explores different 3-D slices of the same 4-D set.

### 4.3 Worked snippets

```csharp
// Quaternion Julia with a transcendental twist — sine of the square, plus c.
return Quat.Sin(z * z) + c;

// Fractional-power bulb (analytic Pow path). Non-integer exponent → exp(exp·log q).
return Quat.Pow(z, 2.5) + c;

// Exponential map — bounded, so read it with Color driver = FinalMagnitude.
return Quat.Exp(z) * 0.5 + c;
```

---

## 5. Algebra Mode

| Mode | Type | Slice W | Speed |
|---|---|---|---|
| Vec3 (3D) | Vec3 | ignored | Fast |
| Quat (4D) | Quat | active | Slower |

Algebra change triggers a recompile.

---

## 6. DE Modes

Three distance-estimation modes selectable from the DE mode combo:

| Mode | When valid | Speed | Accuracy |
|---|---|---|---|
| Auto | Default; detects triplex-power patterns | Fast when matched, Numerical fallback otherwise | Best per-map |
| Analytic | Hubbard-Douady analytic DE only valid for triplex power maps | ~4× faster | Exact for matched maps; wrong for everything else |
| Numerical | Always | Slow | Works for any map |

Analytic formula:

```
DE(p) = 0.5 · ln(|z|) · |z| / dr
dr  = p · r^(p-1) · dr + 1
```

Numerical formula: 4 lockstep trajectories at `(c, c+h·êx, c+h·êy, c+h·êz)`, `dr = max(|z_px-z_base|, |z_py-z_base|, |z_pz-z_base|) / h`. Works for ANY map.

**Jac h** slider controls the finite-difference perturbation. 1e-4 default. Too small → cancellation noise; too large → soft-edged surface.

---

## 7. Backends (CPU vs GPU)

| Backend | Coverage | Speed |
|---|---|---|
| CPU | Every map, both compilers, both algebra modes, chains, KIFS. | Baseline |
| GPU | Depends on the **Compiler** (see below). Falls back to CPU for anything it can't translate. | 5–20× faster when matched |

What the GPU can render now depends on which compiler is active (§19). There are three GPU routes; the engine picks the widest one that fits and **silently falls back to CPU** on any miss:

| Route | Compiler | What it covers |
|---|---|---|
| **Sandbox — Quat mode** | Sandbox | Full quaternion bodies. Runs the analytic power-DE when an analytic pattern is detected and Julia is off; otherwise a 5-trajectory numerical-Jacobian DE. **Julia mode is supported** (holds `c` at the Julia constant). |
| **Sandbox — Vec mode** | Sandbox | Analytic-power bodies (`triplex(z, K) + c` and the square map). Vec Julia / vec numerical on GPU is out of scope for now — those stay on CPU. |
| **Legacy Roslyn** | Roslyn | The pre-baked triplex spherical power-N kernel only: `Vec3.Pow(z, N) + c` with literal integer `N`, vec-only, non-Julia. |

Chains compile on the GPU too under the Sandbox compiler (each step body is emitted and inlined). Scalar-KIFS distance fields are **CPU-only** — a body whose DE needs the KIFS scale falls back regardless of compiler.

> [!TIP]
> The big change from earlier builds: **quaternion fractals now render on the GPU**, but only through the **Sandbox** compiler. If you wrote a quat body in Roslyn and it feels CPU-slow, retype it in the Sandbox DSL (§19) — the `q*` functions map straight onto device-safe `Quat.*` kernels.

To check whether GPU translation succeeded: render at a known-fast resolution; if the frame time matches CPU at the same resolution, you fell back. The error label also surfaces the last GPU compile/JIT error when a fallback happens.

---

## 8. Camera + Lighting

### Camera (sphere around origin)

| Knob | Range | Default |
|---|---|---|
| Distance | 0.5 – 100 | 3.0 |
| Theta (azimuth) | 0 – 360° | 45° |
| Phi (elevation) | 1 – 179° | 63° |
| Reset cam | — | restores canonical view |

### Lighting (key direction)

| Knob | Range | Default |
|---|---|---|
| Light theta | 0 – 360° | 45° |
| Light phi | 1 – 179° | 60° |

L1 / L2 / L3 intensity sliders weight three directional contributions (key / fill / rim).

### Mouse

| Input | Action |
|---|---|
| Mouse wheel | Zoom (smaller/larger Distance) |
| Left-click drag | Pan in screen space |
| Right-click drag X | Orbit Theta |
| Right-click drag Y | Orbit Phi (inverted) |

---

## 9. Render Knobs

| Knob | Sane range | Notes |
|---|---|---|
| Iterations | 4 – 16 | DE inner-loop count |
| Max steps | 48 – 256 | Raymarch step cap |
| Bailout | 2 – 16 | `|z|` escape threshold |
| Epsilon | 0.0005 – 0.005 | Surface hit threshold |
| Jac h | 1e-5 – 1e-3 | Numerical-DE perturbation |
| Cull r | 1.5 – 8 | Bounding-sphere radius |

Cull radius gates ray entry — rays missing the bounding sphere render the sky color directly (zero march cost). Default 2.0 fits canonical Mandelbulb; Mandelbox needs 4–8.

---

## 10. Param Bank

Add arbitrary scalar params from the Params panel. Each row: Name, Value, Min, Max, X (remove).

In source, reference by name — the wrapper exposes each as a local `double <name>`:

```csharp
// Params: k = 2.0, twist = 0.3, freq = 4.0
return Vec3.Pow(z, k) + c + Vec3.Sin(z * freq) * twist;
```

Changing a value re-renders (no recompile). Changing a NAME / adding / removing triggers a recompile.

---

## 11. Animation (global t)

The Animation bar plays a continuously increasing clock `t` (seconds × Speed).

Reference as a bare local in your source:

```csharp
return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;
```

▶ starts (~30 Hz updates). ▮ pauses. Speed multiplies the per-tick delta. Setting `t` manually fires a render.

---

## 12. Julia Mode

Tick **Enable (fix c)** in the Julia group to replace the per-pixel `c` with a single user-supplied constant for EVERY iteration.

| Field | Vec3 mode | Quat mode |
|---|---|---|
| c.X | active | active |
| c.Y | active | active |
| c.Z | active | active |
| c.W | disabled | active |

The pixel coordinate still drives the raymarch position; only the iteration's `c` is overridden. Produces a 3D (or 4D-sliced) Julia set.

---

## 13. Color Drivers

Selects what the IColorMap receives as input per pixel.

| Driver | Input |
|---|---|
| StepDepth | Number of march steps before hit. Highlights silhouette depth. Default. |
| OrbitTrap | Min distance from orbit to user-set trap point (tx, ty, tz). Reveals tendrils. |
| EscapeAngle | atan2 of escape vector projected onto user-chosen axis. Highlights spirals. |
| FinalMagnitude | log(|z|) at escape. Smooth gradient. |
| IterComponent | Specific axis of final iterate (X/Y/Z). Anisotropic. |
| Normal | Surface normal mapped to RGB. Pure shading debug; no palette involved. |

---

## 14. Chain Editor

When the Chain panel has ≥ 1 step, the single-source editor is **ignored**. Each chain step:

| Field | Purpose |
|---|---|
| Output name | Identifier added as a local Vec3 (or Quat) available to later steps. |
| Source | A C# body returning Vec3 / Quat (same rules as single editor). |

Steps run sequentially per iteration; the LAST step's output becomes the new `z`. Earlier outputs are visible to later steps.

```
Step 1   name = pre
         source = Vec3.Rot(z, new Vec3(0,1,0), t)

Step 2   name = sq
         source = Vec3.Pow(pre, 8) + c
```

Delete every chain row to revert to the single-editor flow.

### 14.1 Hybrid primitives (Mandelbulber-style)

Two toolbar buttons make composing 3D hybrids one click:

| Button | Action |
|---|---|
| **+ Primitive ▾** | Appends one named fold/power step to the chain. Options: Mandelbox fold (box+sphere+scale), KIFS Menger fold, KIFS Sierpinski tetra fold, Mandelbulb power. |
| **Hybrid ▾** | Replaces the chain with a worked-example two-step hybrid: Mandelbox + Mandelbulb, or Menger + Mandelbulb. |

Each primitive is plain `Vec3` source you can edit after dropping it in — change the box-fold limit, the sphere-fold radii, the Mandelbulb power, or the KIFS scale to taste. Output names auto-uniquify on insertion so duplicate primitives compose cleanly.

The two built-in hybrid examples (`Hybrid: Mandelbox + Mandelbulb` and `Hybrid: Menger + Mandelbulb`) are also seeded in the saved-equation dropdown, so you can recall them by name later.

---

## 15. Save / Load / Promote

Saved bulbs persist to `%APPDATA%\FracturingFog\userbulbs.json`.

| Button | Action |
|---|---|
| Save… | Stores the current editor text under the typed name. Overwrite confirmation if name exists (v0.6.2+). |
| Delete | Removes the selected saved entry. |
| Import… | Reads a single-entry `.fbulb` JSON file. Renames on name collision. |
| Export… | Writes the selected entry to a `.fbulb` JSON. |
| Promote to fractal list | When ticked, the saved bulb appears in the main Type combo as a first-class option. |

10 default presets ship pre-seeded on first run. Delete / edit / re-save freely.

---

## 16. Mesh Export (OBJ / STL)

Click **Export mesh (OBJ)…** to sample the DE field on a uniform N³ grid inside a cube of side `2·Range` centred at origin and extract the surface with **marching cubes** (interpolated triangles, not blocky voxels). Choose `.obj` (ASCII, smooth per-vertex normals) or `.stl` (binary, faceted) in the save dialog.

The export runs on a **background thread** — the app stays responsive. A status-bar chip reads **"Exporting mesh…"** with a **Cancel** button; cancelling aborts the run and leaves any existing file untouched (no half-written stub). The export + Auto buttons disable while a run is in flight, so you can't launch two at once.

### 16.1 Export knobs

The knobs sit on their own row beneath the export button:

| Knob | Range | Default | What it does |
|---|---:|---:|---|
| **Grid** | 16 – 512 | 96 | Marching-cubes resolution per axis. Cost ~N³ DE evaluations (sampling is parallelised across CPU cores). Finer grid = more detail **and** smaller cells, which also tightens the fraction-mode Iso band. 256–384 for a detailed mesh. |
| **Range** | 0.25 – 64 | 2.0 | Object-space half-extent of the sampled cube. Must **enclose** the fractal — too small clips it; too large wastes resolution and can leave the mesh open where the surface exits the cube face. |
| **Auto** | — | — | Probes the DE along 64 rays from the centre, finds the fractal's extent, and sets **Range** to enclose it with a 20% margin. Use it first — removes the guess-and-clip loop and prevents boundary holes. |
| **Iso** | 0.005 – 2 | 0.5 | Iso-surface level. With **abs off** it is a *fraction of the cell size* (`iso = step·Iso`); the default 0.5 sits a half-cell **outside** the true surface, so at coarse grids thin filaments inflate into fat tubes and gaps fuse into a ball. Lower toward **0.1–0.25** to hug the surface and keep filament detail; raise it to bridge gaps if the mesh comes out shattered. |
| **abs** | on/off | off | When on, **Iso** is an *absolute object-space distance* (grid-independent). Set the surface level once and change Grid freely without re-tuning Iso. |
| **SS** | 1 – 4 | 1 | Supersampling. Box-averages an `s×s×s` stencil of DE samples per grid corner, antialiasing sub-cell filaments into **continuous arms** instead of broken tubes/dots. Cost is ~`s³×`, so keep it **1 at Grid ≥ 256** and reserve **2 for Grid ≤ 192**. |
| **Crease°** | 5 – 180 | 180 | Normal-smoothing threshold. 180 smooths **everything** (rounds off Mandelbox-style facets). Lower it (~**30**) to keep hard edges crisp — faces meeting at a sharper angle than this split into separate normals, so facets stay sharp while curved bulb arms still smooth. |

DE **quality** (Iterations, Jac h, DE mode) comes from the **Render** knobs above — the export DE is the same kernel as the render. All export knobs (Grid, Range, Iso, abs, SS, Crease°) **persist** with a saved bulb, along with the Render Iterations / Jac h.

### 16.2 Getting a detailed mesh (not a blob)

The exported mesh is only as good as the distance estimate:

- **DE mode → Analytic** (or Auto). The analytic running-derivative DE is exact and meshes crisply. The **numerical** Jacobian is an approximation — it renders acceptably but marching-cubes turns it into a soft blob. Analytic engages for recognised power maps (`z*z + c`, `Vec3.Pow(z, N) + c`, and the quaternion square in Quat mode).
- **Quaternion fractals → Axis Mode = Quat + Julia mode.** The *Quaternion Julia* saved preset sets these automatically; a `z*z + c` in Quat mode meshes like the built-in Quaternion Julia fractal type.
- **KIFS folds (Menger / Sierpinski / Mandelbox / kaleidoscopic) → set KIFS Scale** (3 for Menger, 2 for Sierpinski/Mandelbox). Inserting a fold primitive or loading a fold hybrid now sets this for you. Without it the numerical DE cannot cross the fold discontinuities and export yields **zero triangles**.
- **Raise Iterations** for geometry — the render default (8) is low; try 14–16 for export.
- **Drop Iso** — the single biggest lever against the "ball with tubes" look. Pair it with a higher Grid.

### 16.3 Recommended recipes

| Goal | Auto | Grid | Iso | SS | Crease° | Also |
|---|:--:|--:|--:|--:|--:|---|
| **Fast preview** | ✓ | 160 | 0.15 | 1 | 180 | — |
| **Detailed organic bulb** | ✓ | 320–384 | 0.12 | 1 | 180 | Iter 14–16, Jac h 1e-5, DE Analytic |
| **Filament close-up** | ✓ | 160–192 | 0.12 | 2 | 180 | SS antialiases thin arms |
| **Faceted hybrid** (Mandelbox + bulb) | ✓ | 256 | 0.15 | 1 | 30 | keeps box facets crisp |
| **3-D print (solid)** | ✓ | 192–256 | 0.30+ | 1 | 60 | higher Iso fuses thin filaments into a printable solid |

> **Reality check.** User Bulb is a general interpreter — for an *arbitrary* equation it can only estimate the DE numerically, so its mesh is inherently softer than a hand-written analytic calculator. For a faithful mesh of a *known* fractal (Quaternion Julia/Mandelbrot, Mandelbulb, Mandelbox, KIFS), prefer that concrete **Fractal Type**, whose exact DE meshes at full detail.

### 16.4 Mesh export troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| **Ball with tubes / cones** sticking out the ends | Iso too high for the grid — the half-cell offset inflates filaments and fuses gaps | Lower **Iso** to 0.1–0.2 and raise **Grid**; keep **SS 1** |
| **Blobby / soft**, no fine detail | Numerical DE + low iterations smooth the field | **DE mode → Analytic**, **Iterations 14–16**, **Jac h 1e-5** |
| **Broken filaments / dotty arms** | Sub-cell aliasing — arms thinner than a cell get point-sampled away | Raise **SS** to 2 (drop Grid to keep cost sane), or raise **Grid** |
| **Box facets look rounded** | Crease smoothing averages across the hard edges | Set **Crease° ~30** |
| **0 triangles exported** | Range doesn't enclose the set, or a fold needs its scale | Click **Auto**; for folds set **KIFS Scale** (3 Menger, 2 Sierpinski/Mandelbox) |
| **Mesh has holes / not watertight** | The surface reaches the cube face and marching cubes leaves it open | Click **Auto** (adds margin), or raise **Range** |
| **Shattered / disconnected shell** | Iso so low the surface falls between grid cells | Raise **Iso** slightly, or raise **Grid**; turn **abs** on for a grid-independent level |
| **Export hangs / very slow** | Grid×SS is huge (millions of DE evals × s³) | Lower **Grid** or **SS**, or hit **Cancel**; use **SS 1** at Grid ≥ 256 |
| **File enormous** | High Grid (and Crease° < 180 splits extra vertices) | Lower **Grid**; export `.stl` if you don't need smooth normals |

---

## 17. Example Gallery

Each example: source body + suggested config block. Paste into the editor (or save).

### 1. Square Triplex (default — fast 3D Mandelbrot)

```csharp
return new Vec3(
    z.X*z.X - z.Y*z.Y - z.Z*z.Z,
    2*z.X*z.Y,
    2*z.X*z.Z) + c;
```

Config: Vec3 / Auto DE / Iter 8 / Bailout 4 / Steps 96 / Eps 0.0015 / Cull 2.0.

### 2. Mandelbulb p=8 (GPU-friendly)

```csharp
return Vec3.Pow(z, 8) + c;
```

Config: Vec3 / GPU / Auto DE / Iter 8 / Bailout 2 / Eps 0.0008 / Cull 1.5.

### 3. Power-12 Ridged Bulb

```csharp
return Vec3.Pow(z, 12) + c;
```

Config: Iter 10 / Steps 160 / Eps 0.0006 / AO 4 / Fog 0.08.

### 4. Animated Breathing Bulb

```csharp
return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;
```

Animation ▶, Speed 0.5. Falls to Numerical (animated power = no analytic). SS = 1, Iter 6 / Steps 64 for live playback.

### 5. Quartic + Sin Perturbation

```csharp
return Vec3.Pow(z, 4) + Vec3.Sin(z) * 0.5 + c;
```

Power escapes + bounded sin adds texture. Color driver OrbitTrap (0,0,0) highlights folds.

### 6. Abs-Bulb p=8 (Burning-Ship 3D)

```csharp
return Vec3.Pow(Vec3.Abs(z), 8) + c;
```

Flat-top + sharp ridges.

### 7. Mandelbox

```csharp
var v = Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0);
return v * 2.0 + c;
```

Iter 12 / Bailout 16 / Eps 0.0006 / Jac h 1e-3 / Cull 6.0 / Distance 8 / AO 6 / Fog 0.15.

### 8. Quaternion Julia

```csharp
return z * z + c;
```

Algebra Quat / Slice W 0.3 / Julia ON / c=(-0.2, 0.4, -0.4, 0.0).

Swap `z * z` for any quaternion-algebra call from §4 to explore relatives: `Quat.Pow(z, 3)` (cubic), `Quat.Sin(z * z)` (transcendental), `Quat.Exp(z) * 0.5` (exponential map). For the same maps on the GPU, retype the body in the Sandbox DSL (§19.10) — that is the quat-capable GPU path.

### 9. Vec3 Julia (3D triplex with fixed c)

```csharp
return new Vec3(
    z.X*z.X - z.Y*z.Y - z.Z*z.Z,
    2*z.X*z.Y,
    2*z.X*z.Z) + c;
```

Julia ON / c=(0.30, 0.50, -0.20).

### 10. Rotated-Triplex Helix

```csharp
var sq = new Vec3(
    z.X*z.X - z.Y*z.Y - z.Z*z.Z,
    2*z.X*z.Y,
    2*z.X*z.Z);
return Vec3.Rot(sq, new Vec3(0, 1, 0), t * 0.3) + c;
```

Animation ▶ Speed 0.4.

### 11. Periodic Kaleido

```csharp
var p = Vec3.Mod(z, 2.0);
return Vec3.Pow(p, 8) + c;
```

Distance 6 / FOV 80°. Infinite lattice of mini-bulbs.

### 12. Cross-Product Ribbons

```csharp
var sq = new Vec3(
    z.X*z.X - z.Y*z.Y - z.Z*z.Z,
    2*z.X*z.Y,
    2*z.X*z.Z);
return sq + c + Vec3.Cross(z, c) * 0.5;
```

Color driver EscapeAngle / axis Y.

### 13. Smooth-Min Blend (Chain editor)

```
Step 1   name = a    source = Vec3.Pow(z, 8) + c
Step 2   name = b    source = Vec3.Pow(z, 4) + c
```

Last step's output is new z. Swap powers (8/3, 12/6) to morph.

### 14. Parametric Twist-Bulb (named params)

Params: p=8 / k=0.3 / freq=4.

```csharp
var v = Vec3.Pow(z, p);
var twist = Vec3.Sin(z * freq) * k;
return v + c + twist;
```

Drag sliders to morph live (no recompile).

---

## 18. Pitfalls + Troubleshooting

**No-escape maps look blank.** Pure sin / cos / Vec3.Sinh that orbits forever never crosses bailout → DE meaningless → flat sphere or blank. Use Color driver = OrbitTrap or FinalMagnitude to visualise bounded maps.

**z₀ = 0 fixed points.** Any f with f(0,0) = 0 gives all-in-set when c=0 is centered. Enable Julia mode (fixes c) or rephrase the source so z₀ = 0 isn't a fixed point.

**Exploding maps.** `z^z`, double-exp grow to ∞ in 1–2 iterations. Drop Iterations to 2–4 / raise Bailout to 1e6 / rescale inputs.

**NaN / Inf.** Math.Log of negatives, Math.Atan2(0,0), division by zero. Guard with `+ 1e-6` in denominators and `Math.Max(r, 1e-12)` before Log.

**DE mode mismatch.** Analytic DE on non-triplex gives wrong surfaces. Use Auto (engine falls back to Numerical for unknown shapes) or pick Numerical explicitly.

**Tight Jac h on discontinuous maps.** Mandelbox-style folds have piecewise derivatives. Raise Jac h to 1e-3 for folds.

**Cull r too small.** Mandelbox extends beyond Cull r → bounding sphere clips silhouettes. Use 4–8 for Mandelbox; canonical bulbs are happy with 2.

**GPU backend fallback.** Anything beyond plain `Vec3.Pow(z, INT) + c` silently falls back to CPU. Check perf — if GPU was expected and you don't see a speedup, the body wasn't translatable.

**Perf.** Roslyn delegate ~40 ns per call. Heavy Math.Pow / Atan2 multiplies that. Prefer `x*x*x` over `Math.Pow(x, 3)`; hoist invariants out of the body.

**Black screen, no shape.** Check the error label (green ✓ vs red error). Bump Bailout to 16. Drop Iterations to 4. Spin Camera Theta. Raise Cull r.

**Speckled normals / noisy shading.** Raise Jac h from 1e-4 → 1e-3. Drop Epsilon to 0.0005.

**""Melted"" soft edges.** Lower Epsilon to 0.0008. Raise Max steps to 192.

**Banding / tile boundaries.** DE mode may be wrong — force Numerical. Toggle re-compile (edit source and revert) to flush the temporal cache.

**Unbearably slow render.** Drop Iterations to 4, Max steps to 48 for exploration. Switch Backend → GPU for plain `Vec3.Pow` bodies. Set SS back to 1. Resize window smaller.

---

## 19. Sandbox DSL Compiler

The **Compiler** combobox (Render group) toggles between two source compilers:

| Mode | Source language | Algebra | Backend | Speed | Safety |
|---|---|---|---|---|---|
| Roslyn (default) | full C# | Vec3 + Quat | CPU + GPU (vec triplex-power only) | fastest per-iter | trusts source |
| Sandbox | small DSL | Vec3 + Quat | CPU + GPU (vec **and** quat) | ~10–15× slower per-iter on CPU | parse-only, no BCL |

> [!IMPORTANT]
> Two things changed since earlier builds and you may still see them described the old way elsewhere:
> **(1)** the Sandbox DSL is **no longer Vec3-only** — it has a full quaternion surface (§19.5), and the algebra combobox no longer forces back to Vec3 when Sandbox is selected. **(2)** Sandbox **now has a GPU backend**, and it is in fact the *only* path that renders quaternion fractals on the GPU (§7).

Pick **Roslyn** for the fastest per-iteration CPU speed or when you want the full BCL (`Math.Atan2`, `Math.Truncate`, etc.). Pick **Sandbox** when the source comes from untrusted input, when you want the editor to detect closed-form DE patterns on the AST, when you want a tighter grammar that fails fast on typos, or when you want **quaternion fractals on the GPU**.

### 19.1 Source signature (Sandbox)

The Sandbox compiler does NOT wrap your source in a `Step` method. Write an **expression** that evaluates to `Vec3`:

```dsl
// Mandelbulb N=8
triplex(z, 8) + c
```

```dsl
// Square triplex (explicit)
vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c
```

```dsl
// Let-binding for clarity
let p = triplex(z, 8) in
let q = z * 2 in
p + q + c
```

There is no `return` keyword and no semicolon. The whole source is one expression. Use `let NAME = EXPR in EXPR` for intermediate variables.

### 19.2 Identifiers

| Name | Type | Description |
|---|---|---|
| `z` | Vec3 | Previous iterate. `Vec3.Zero` on iter 0. |
| `c` | Vec3 | Per-pixel constant. Replaced with Julia c in Julia mode. |
| `n` | Real | 0-based iteration index. |
| `t` | Real | Global animation time (always available). |
| `<param-name>` | Real | Each Params row adds an identifier. |
| `pi`, `e` | Real | Constants. |

In **Quat mode** the same `z` and `c` identifiers carry all four components, and `.w` reads the real part.

Member access:

- On a **Vec3**: `.x`, `.y`, `.z`.
- On a **Quat**: `.x`, `.y`, `.z`, and `.w` (the real part).
- On a **scalar**: `.x`/`.y`/`.z` broadcast (e.g. `n.x == n`); `.w` on a non-quat is `0`.

> [!NOTE]
> A DSL value is one of three kinds at runtime — **Real**, **Vec** (3-vector), or **Quat**. You never declare the kind; it flows from what you build. `vec(...)` produces a Vec, `qvec(...)` and every `q*` function produce a Quat, arithmetic promotes to the widest operand, and the final result is projected to Vec3 (Vec3 mode) or Quat (Quat mode) automatically.

### 19.3 Grammar

```
expr     := let_expr
let_expr := 'let' IDENT '=' expr 'in' expr | ternary
ternary  := or ('?' expr ':' expr)?
or       := and ('||' and)*
and      := not ('&&' not)*
not      := '!' not | cmp
cmp      := add (('<'|'>'|'<='|'>='|'=='|'!=') add)?
add      := mul (('+'|'-') mul)*
mul      := pow (('*'|'/') pow)*
pow      := unary ('^' pow)?           ; right-assoc
unary    := '-' unary | primary
primary  := NUMBER | IDENT (member|call)* | '(' expr ')' member*
member   := '.' ('x'|'y'|'z'|'w')      ; '.w' reads a Quat's real part
call     := '(' (expr (',' expr)*)? ')'
```

### 19.4 Operators

| Op | Vec / Vec | Vec / Real | Real / Real | Quat operand |
|---|---|---|---|---|
| `+`, `-` | componentwise | broadcast | scalar | componentwise (result is Quat) |
| `*` | Hadamard (componentwise) | broadcast | scalar | Quat × Quat = **Hamilton product**; Quat × Real = broadcast scale |
| `/` | componentwise | broadcast | scalar | Quat / Real = broadcast scale |
| `^` | **triplex** Mandelbulb power | scalar Math.Pow | scalar Math.Pow | Quat ^ Real = `Quat.Pow` (§4.1) |
| unary `-` | componentwise | – | scalar | componentwise |
| `&&`, `\|\|`, `!`, comparisons | reduce to real (1.0 / 0.0) | – | scalar | operands reduced by magnitude |

Two operator rules worth memorising:

- `^` is **triplex** power when the LHS is a Vec — `z ^ 8` is shorthand for `triplex(z, 8)`. When the LHS is a Quat it is `Quat.Pow`, and when it is a Real it is `Math.Pow`.
- `*` between two **Quat** values is the non-commutative **Hamilton product** (same as `qmul(a, b)`), *not* a component-wise multiply. `a * b ≠ b * a` in general — this is exactly the behaviour that makes `z * z + c` a real quaternion Julia set.

### 19.5 Functions

Single-arg (scalar or componentwise on Vec):
`sin`, `cos`, `tan`, `sinh`, `cosh`, `tanh`, `exp`, `log`, `sqrt`, `abs`

Vec3 specific:
- `vec(x, y, z)` — construct Vec3 from three reals.
- `length(v)` — Vec → real.
- `dot(a, b)`, `cross(a, b)`, `normalize(v)`
- `triplex(v, n)` — Mandelbulb spherical power.
- `rot(v, axis, angle)` — Rodrigues rotation.
- `boxfold(v, limit)` — Mandelbox box-fold.
- `spherefold(v, rmin, rmax)` — Mandelbox sphere-fold.
- `absx(v)`, `absy(v)`, `absz(v)` — fold a single axis.
- `mod(v, period)` — periodic space.

Scalar-only:
- `pow(a, b)`, `floor(s)`, `sign(s)`, `min(a, b)`, `max(a, b)`, `clamp(x, lo, hi)`, `smin(a, b, k)`.

Quaternion-algebra (produce and consume `Quat` values — see §4):
- `qvec(x, y, z, w)` — construct a quaternion from four reals (note the order: imaginary `x, y, z` first, real `w` last).
- `qmul(a, b)` — Hamilton product (same as `a * b` on two quats).
- `qconj(q)` — conjugate `(w, -x, -y, -z)`.
- `qinv(q)` — inverse `q⁻¹`.
- `qpow(q, s)` — `Quat.Pow`: exact self-multiply for non-negative integer `s`, analytic `exp(s·log q)` otherwise.
- `qexp(q)`, `qlog(q)`, `qsqrt(q)` — quaternion exp / log / square-root.
- `qsin qcos qtan`, `qsinh qcosh qtanh` — quaternion trig + hyperbolic.
- `qasin qacos qatan`, `qasinh qacosh qatanh` — inverse trig + inverse hyperbolic.
- `qcsc qsec qcot`, `qcsch qsech qcoth` — the six reciprocals.

> [!IMPORTANT]
> The `q*` functions treat their argument as a **quaternion** (a rotation-carrying algebra element), which is different from applying the plain `sin`/`cos`/… element-wise to four numbers. Applying a *plain* transcendental such as `sin`, `cos`, `exp`, `log`, or `sqrt` to a Quat is **rejected at eval time** ("not defined componentwise on Quat") — project to a component first (`sin(q.x)`) or use the quaternion version (`qsin(q)`). The one exception is `abs`, which is a legitimate per-axis fold and *is* allowed on a Quat.

### 19.6 Chains in Sandbox

The Chain editor works identically to Roslyn mode. Each step is its own Sandbox expression. Prior step output names become Vec3 identifiers in later steps.

```dsl
step0 (output: folded)
    vec(abs(z.x), abs(z.y), abs(z.z))

step1 (output: out)
    triplex(folded, 8) + c
```

The above is a "Burning Bulb" variant — abs-fold each axis before the triplex power.

### 19.7 Analytic DE detection

When the Sandbox compiles, the AST is walked to look for closed-form patterns the engine can render with a single trajectory instead of a four-trajectory numerical Jacobian. The detected pattern is shown in the **DE detect** badge (green when engaged, grey when numerical).

Currently recognised patterns:

| Pattern | Source shape | DE algorithm | Speedup |
|---|---|---|---|
| MandelbulbN | `triplex(z, K) + c` | Hubbard-Douady power-N | ~3-4× |
| Square | `vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c` | Hubbard-Douady N=2 | ~3-4× |

Chains never engage analytic DE — they always run numerical.

### 19.8 Limitations

- **No BCL** — `Math.Atan2`, `Math.Round`, `Quaternion.*`, etc. are not in scope. Use the built-in functions table above (including the `q*` quaternion family for 4-D work).
- **No plain transcendentals on a Quat** — `sin`/`cos`/`exp`/`log`/`sqrt` are rejected on a quaternion value (they are only defined component-wise, which is geometrically meaningless for a quaternion). Use `qsin`/`qcos`/… instead, or project to a component first. `abs` is the lone exception.
- **CPU interpreter is slower per-iter** — interpreter dispatch is roughly 10–15× slower than Roslyn-compiled. Mitigated when an analytic-DE pattern is detected, and side-stepped entirely on the GPU route (the DSL is emitted to a compiled device kernel).
- **GPU coverage is not total** — the Sandbox GPU path renders quat bodies (analytic or numerical, Julia included) and vec analytic-power bodies, but **not** vec-Julia, vec-numerical, or scalar-KIFS DE — those fall back to CPU (see §7).

> [!NOTE]
> Earlier editions of this guide listed "No Quat mode" and "No GPU backend" here. Both are obsolete: the Sandbox DSL gained the full quaternion surface and a GPU emitter. The **Compiler** combobox item may still read "Vec3 only, CPU" in some builds — that label is stale; the algebra and backend comboboxes are the source of truth.

### 19.9 When to use Sandbox vs Roslyn

| Decide for | Choose |
|---|---|
| Maximum framerate at any cost | **Roslyn** |
| GPU acceleration | **Roslyn** (Sandbox CPU-only) |
| Untrusted source / shared presets | **Sandbox** |
| Want compile-time error spans | **Sandbox** (parser positions are surfaced) |
| Pre-recognised Mandelbulb N=K | either (Roslyn faster on iter, Sandbox detects pattern too) |
| 4-D / quaternion fractals on CPU | either — Roslyn (`Quat.*`) or Sandbox (`q*`) |
| 4-D / quaternion fractals **on GPU** | **Sandbox** (the only quat-capable GPU path) |

### 19.10 Quaternion cookbook (Sandbox DSL)

Set **Algebra → Quat (4D)** and **Compiler → Sandbox**, then paste any of these into the editor. Tick **Backend → GPU** for the speed-up; all of them are GPU-translatable. Use **Slice W** to slide through the 4-D set and **Julia → Enable (fix c)** where noted.

```dsl
// 1. Classic quaternion Julia — the "hello world" of 4-D fractals.
//    z * z is the Hamilton product; z^2 would mean the same thing here.
//    Julia ON, c = (-0.2, 0.4, -0.4, 0.0), Slice W ≈ 0.3.
z * z + c
```

```dsl
// 2. Cubic quaternion Mandelbrot — integer power uses the exact fast path.
qpow(z, 3) + c
```

```dsl
// 3. Transcendental quaternion — sine of the square. qsin is true quaternion
//    sine, NOT sin applied to four numbers. Read bounded maps like this with
//    Color driver = FinalMagnitude or OrbitTrap.
qsin(z * z) + c
```

```dsl
// 4. Fractional power — analytic exp(s·log q) branch. Non-integer exponent.
qpow(z, 2.5) + c
```

```dsl
// 5. Build a quaternion by hand and fold it. qvec order is (x, y, z, w).
let q = qvec(z.x, z.y, z.z, z.w * 0.5) in
qmul(q, q) + c
```

```dsl
// 6. Exponential map, damped so it stays in frame.
qexp(z) * 0.5 + c
```

> [!TIP]
> `z * z` and `qmul(z, z)` compile to the same kernel — pick whichever reads better. When you mix reals and quaternions (`qpow(z, 3)`, `qexp(z) * 0.5`) the real operands broadcast automatically, so you rarely need `qvec` unless you are assembling a quaternion from separate scalar parts.

---

*User Bulb 3D Guide · Fracturing Fog · © 2026*
