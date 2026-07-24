# Fractal Equation Design Guide

> Companion pages: [Technical Index](_Index.md) · [CalculatorGen Authoring](CalculatorGen-Authoring.md) · [CalculatorGen Architecture](CalculatorGen-Architecture.md) · [User-facing CalcGen Guide](../User/CalcGen-UserGuide.md) · [User Bulb 3D Guide (user-facing)](../User/UserBulb-Guide.md) · [Resources & Bibliography](../Resources-Bibliography.md)

---

## 0. Extending to three dimensions — quaternion algebra

The 2-D CalcGen pipeline is the right backbone for escape-time work in the complex plane, but the
User Bulb engine extends the same idea to ℝ³ via two non-equivalent algebras: **triplex** (the
Mandelbulb's spherical-coordinate trick) and **quaternion** (a four-dimensional skew field where the
classical Julia/Mandelbrot recurrence is well-defined).

### 0.1 Quaternion basics

Quaternions are numbers of the form

$$
q = a + b\,\mathbf{i} + c\,\mathbf{j} + d\,\mathbf{k}
$$

with the Hamilton multiplication rules

$$
\mathbf{i}^2 = \mathbf{j}^2 = \mathbf{k}^2 = \mathbf{i}\mathbf{j}\mathbf{k} = -1
$$

Multiplication is associative but **not commutative**. Conjugation is

$$
\overline{q} = a - b\,\mathbf{i} - c\,\mathbf{j} - d\,\mathbf{k},
\qquad
|q|^2 = q\overline{q} = a^2 + b^2 + c^2 + d^2
$$

For two quaternions `p` and `q`, the product expands explicitly as

$$
\begin{aligned}
p\,q
&= (a_p a_q - b_p b_q - c_p c_q - d_p d_q) \\
&\quad + (a_p b_q + b_p a_q + c_p d_q - d_p c_q)\,\mathbf{i} \\
&\quad + (a_p c_q - b_p d_q + c_p a_q + d_p b_q)\,\mathbf{j} \\
&\quad + (a_p d_q + b_p c_q - c_p b_q + d_p a_q)\,\mathbf{k}
\end{aligned}
$$

A 3-D slice through the 4-D quaternion Julia/Mandelbrot set fixes one of the four components (the
shell uses `d = 0` by default) and varies the other three across screen pixels and the ray-marcher.

### 0.2 The quaternion Mandelbrot recurrence

The natural lift of `z² + c` to ℍ is

$$
q_{n+1} = q_n^{\,2} + c
$$

where `q² = q*q` per the multiplication table above. The User Bulb body equivalent in DSL form:

```cg
let q2 = quat_mul(q, q);
return q2 + c;
```

In C# (Roslyn body) form:

```csharp
var q2 = new Quat(
    q.A * q.A - q.B * q.B - q.C * q.C - q.D * q.D,
    2 * q.A * q.B,
    2 * q.A * q.C,
    2 * q.A * q.D);
return q2 + c;
```

(The cross-terms cancel for `q * q` because `p = q` — many terms simplify pairwise.)

### 0.3 Distance estimation for quaternion fractals

Quaternion fractals are 4-D objects sliced into 3-D. The standard distance estimator follows the
same template as the 2-D Mandelbrot case ([Quilez](../Resources-Bibliography.md#distance-estimation-normals-shading)):

$$
\mathrm{DE}(q) \approx \tfrac{1}{2}\,|q|\,\frac{\ln|q|}{|q'|}
$$

…where `q'` is the running derivative tracked alongside the iteration. For `q_{n+1} = q_n^2 + c`,
the chain rule gives

$$
q'_{n+1} = 2\,q_n\,q'_n + 1
$$

(again, non-commutative multiplication — order matters; the constant on the right is the derivative
of `+c`). The User Bulb engine carries `q'` as a quaternion derivative alongside `q` in the DE mode
combo's *analytic* setting.

### 0.4 Triplex vs quaternion — when to use which

| Property                | Triplex (`v^p` via spherical coords) | Quaternion (`q² + c`)             |
|-------------------------|--------------------------------------|------------------------------------|
| Algebra closed?         | No (singular at the poles)           | Yes — proper skew field            |
| Symmetry                | Power-`p` rotational                 | Translation under `q → q + ℝk`     |
| Famous instance         | Mandelbulb (p = 8)                   | Norton's quaternion Julia          |
| Smoothness of boundary  | Cusps at z-axis                      | Smooth except at slice singularities |
| DE robustness           | Heuristic                            | Analytic                           |
| Speed                   | Faster — fewer multiplies            | Slower — 16-term multiply          |

Both are first-class authoring modes in the User Bulb editor; the *Algebra* combo flips between them.

> [!NOTE]
> Quaternion mode is not limited to `q² + c`. The `Quat` type exposes a full analytic transcendental
> library — `Pow` (integer self-multiply and fractional `exp(exp·log q)` branches), `Exp`/`Log`/`Sqrt`/`Inverse`,
> and the complete trig, hyperbolic, and inverse families evaluated on the quaternion's principal axis — so
> `Quat.Sin(z² ) + c` or `Quat.Pow(z, 2.5) + c` are valid maps. Every op obeys an **escape contract**: undefined
> inputs return a non-finite quaternion (which the DE loop escapes as a pixel) rather than throwing, because the
> hot loop has no `try`/`catch`. The same surface is reachable from the Sandbox DSL via the `q*` functions, and
> the Sandbox path is what renders quaternion sets on the GPU. See
> [UserBulb-Guide §4](../User/UserBulb-Guide.md#4-quat-api-4d-mode) and
> [§19.5](../User/UserBulb-Guide.md#195-functions) for the full API and worked examples.

![PLACEHOLDER — Side-by-side: triplex Mandelbulb (p = 8) vs quaternion Mandelbrot slice (d = 0)](../Images/_placeholders/placeholder.svg)

---

A practical introduction to designing fractal equations for the CalcGen DSL.
By the end of this guide you should be able to:

- Read and explain any equation expressed in the CalcGen DSL.
- Predict the rough character of its output (filaments, lobes, spirals,
  symmetric vs asymmetric, smooth vs feathered).
- Modify a given equation to push the result in a chosen direction
  (more symmetry, more chaos, deeper detail, smoother boundary).
- Construct new equations from scratch for any CalcGen-supported fractal
  family, not just the classical Mandelbrot `z² + c`.

The CalcGen DSL grammar reference is in `CalcGen-UserGuide.md`. This guide
focuses on the *why* — what the equations mean, how to think about them,
how to change them.

---

## 1. What an escape-time fractal actually is

Every fractal the CalcGen engine renders is an **escape-time fractal**.
The recipe is identical for every equation in this guide:

1. For each pixel on the screen, take the complex coordinate `c` (the
   pixel's position on the complex plane).
2. Start with `z = 0` (or sometimes another seed — see §10).
3. Apply the user equation repeatedly: `z_{n+1} = f(z_n, c)`.
4. After each step, ask: has `|z|` exceeded the **bailout radius**? The
   engine uses `|z|² ≥ 1024` (or `≥ 4` for the classic Mandelbrot
   contract).
5. Either:
   - **Escaped**: the iteration count `n` at which it escaped tells us
     how fast the orbit blew up — color the pixel based on that count
     (with a smooth-iter correction for anti-aliased gradients).
   - **Bounded**: after `maxIter` steps the orbit still hasn't escaped.
     Color the pixel as "in the set" — the deep, usually-black or
     dark-themed colour.

The fractal you see is the **boundary** between escaped pixels and the
bounded set. That boundary is the structure the iteration produces.

The single function `f(z, c)` is the entire creative space. Change `f`
and you change the fractal. The DSL is the language in which you write
`f`.

### Why this produces self-similar detail

The mapping `f` rearranges the complex plane on every step. Points near
the boundary of the bounded set are arbitrarily close to points whose
orbits diverge. As you zoom in, the boundary keeps revealing finer
structure because the iteration keeps separating "stays bounded" from
"escapes" pixels at every length scale. That's the source of the
fractal dimension.

External reading on the mathematical foundations:

- [Wikipedia: Mandelbrot set](https://en.wikipedia.org/wiki/Mandelbrot_set)
- [Wikipedia: Escape-time fractal](https://en.wikipedia.org/wiki/Fractal#Common_techniques)
- [Inigo Quilez — Smooth iteration count](https://iquilezles.org/articles/msetsmooth/)
- John Milnor, *Dynamics in One Complex Variable* (book) — the
  canonical treatment of `z² + c` and its generalisations.

---

## 2. Anatomy of an iteration step

Take the simplest equation, the classical Mandelbrot:

```
z*z + c
```

Read this as `z_{n+1} = z_n² + c`. Two pieces:

| Piece  | Role                                                      |
|--------|-----------------------------------------------------------|
| `z*z`  | The **feedback** — operates on the previous iterate.      |
| `+ c`  | The **forcing** — the constant for this pixel that breaks |
|        | the orbit out of the trivial fixed point at 0.            |

Almost every escape-time fractal in this guide has the same skeleton:

```
some-function-of(z, n, prev) + something-involving(c)
```

The function of `z` decides how fast magnitudes grow. The function of
`c` decides where the boundary sits. Mix in `n` (iteration index) or
`prev` (previous iterate `z_{n-1}`) to break the pure z-feedback and
get richer dynamics.

### The smooth-iter colouring

The engine doesn't just colour by the raw escape count — that would
produce visible iso-iter bands. It computes a smooth correction:

```
smooth_n = n + 1 − log₂(log₂(|z|))
```

This gives a continuous escape value across the boundary, producing the
gradient-smoothed look. You don't need to write this — it happens
automatically in the renderer.

---

## 3. Quadratic family — the Mandelbrot baseline

### 3.1 Classical Mandelbrot
```
z*z + c
```
The most-iterated equation in mathematics. Cardioid + bulb structure.
Symmetric across the real axis. All other quadratic Julia sets are
slices of this fractal's parameter space.

### 3.2 Higher-degree Multibrot
```
z*z*z + c          (degree 3 — 2-fold rotational symmetry)
z*z*z*z + c        (degree 4 — 3-fold)
z^5 + c            (degree 5 — 4-fold)
z^d + c            (general — (d−1)-fold symmetry)
```

Each increment of the exponent adds another symmetry lobe. The body
becomes more star-shaped; the boundary acquires more arms.

External: [Wikipedia: Multibrot](https://en.wikipedia.org/wiki/Multibrot_set).

### 3.3 Quadratic with linear coupling
```
z*z + 0.5*z + c
```
Adds a linear pull-back. Distorts the cardioid; can rotate the whole
silhouette. The coefficient on `z` shifts the location of the principal
attractor away from 0. Negative coefficients pull the silhouette in
the opposite direction.

### 3.4 Mixed-degree polynomial
```
z*z*z - 0.5*z + c
```
A cubic plus a linear suppression term. Produces twisted figure-8
silhouettes. Mixed coefficients on different powers of `z` are how to
break the rigid `d`-fold symmetry of pure `z^d + c` shapes.

### 3.5 Try this: scaled feedback
```
0.7*z*z + c
```
Shrinks the feedback. The cardioid shrinks; the boundary becomes
smoother because the forcing `+c` dominates more steps. Try
coefficients between 0.1 and 2.0 to see the boundary breathe.

---

## 4. Anti-holomorphic family — Burning Ship and Tricorn

These break the holomorphic (complex-differentiable) chain by folding
real or imaginary components. The result is dramatically different
silhouettes that look more like landscapes than the round Mandelbrot.

### 4.1 Tricorn (Mandelbar)
```
conj(z)*conj(z) + c
```
`conj(z)` is `(re(z), −im(z))`. Squaring the conjugate produces 3-fold
boundary symmetry (instead of the Mandelbrot's 2-fold). The cardioid
becomes a deltoid; bulbs are arranged threefold.

External: [Wikipedia: Tricorn](https://en.wikipedia.org/wiki/Tricorn_(mathematics)).

### 4.2 Burning Ship
```
fold(z)*fold(z) + c
```
`fold(z)` is `(|re|, |im|)` — each component absolute-valued. The
silhouette looks like a sinking ship surrounded by waves at high
contrast. Discover the "antenna" feature by zooming in near the lower
edge.

External: [Wikipedia: Burning Ship](https://en.wikipedia.org/wiki/Burning_Ship_fractal).

### 4.3 Burning Tricorn hybrid
```
conj(fold(z))*conj(fold(z)) + c
```
Combines both anti-holomorphic ops. `fold(z) = (|re|, |im|)`, then
`conj` negates the imaginary part: `(|re|, −|im|)`. Squaring gives
`(|re|² − |im|², −2|re||im|)` — same magnitude envelope as Burning
Ship but with the imaginary component flipped. Produces a vertically-
mirrored ship silhouette with the Tricorn's deltoid hint in the body.

Note: `fold(conj(z))` is *not* equivalent — `conj` negates `im`, then
`fold` re-positives it, so the conjugate cancels and you get the plain
Burning Ship. The order `conj(fold(z))` matters.

### Important note: gating

Anti-holomorphic operators (`conj`, `fold`) **disable the distance
estimate**. The engine still renders the escape-time silhouette
correctly; you just lose the surface-normal shading that's available
on the holomorphic equations. Iso-iter banding is more visible. Use
the smooth-iter colour gradient to compensate.

---

## 5. Rational family — division

Division enables Newton-shaped, Cassini-oval, and rational-pole maps.

### 5.1 Rational forcing with a fixed pole
```
z*z + c/(z + 2)
```
Quadratic feedback plus a rational forcing term. The denominator
`(z + 2)` introduces a pole at `z = −2`. Pixels whose orbit grazes that
neighbourhood get the forcing amplified. Produces a Mandelbrot-like
silhouette with the left side reshaped by the amplified `c` term.

Note: this is *not* simply `(z² + c)/(z + a)` — that family is
degenerate because z₀=0 ⇒ z₁=c/a and `f(c/a) = c/a` (instant fixed
point, no fractal). Keep the polynomial feedback separate from the
rational term to avoid the algebraic cancellation.

### 5.2 Mandelbrot-on-a-shell
```
z*z + c/(1 + z*z)
```
The forcing is now a rational function of `z`. Suppresses the forcing
inside the unit disk; amplifies it outside. The silhouette becomes
more concentrated near the origin and develops a thin shell of
boundary at larger radii.

### 5.3 Feedback with a c-dependent pole pair
```
z*z + 1/(z*z + c)
```
Standard quadratic feedback plus a reciprocal whose poles sit at
`z = ±√(−c)` — they *move with c*, so every pixel sees a different
singular structure. Orbits that pass near a pole get violently
amplified. Produces a primary Mandelbrot lobe plus secondary rational
structure where the pole pair approaches the real axis.

Note: avoid bare `1/z` constructions like `(1/z + c)²`. Because the
engine seeds `z₀ = 0`, `1/0` immediately produces `Inf`, the orbit
becomes `NaN` on the next step, and NaN never satisfies the bailout
test — every pixel registers as "in set" (solid colour). Always guard
the reciprocal with a denominator that's non-zero at z=0, e.g.
`1/(z*z + c)` or `1/(z + 2)`.

### Important note: gating

Division **disables perturbation and BLA**. Deep zooms past ~1e15
become slow (the DD/QD HpDirect path engages but without SA
acceleration). Distance estimate stays available via the quotient
rule. Avoid division if you intend to zoom past ~1e10 interactively.

---

## 6. Transcendental family — sin / cos / exp / log

The CalcGen DSL exposes the holomorphic continuations of the standard
transcendentals. They produce **periodic** structures in the imaginary
axis (sin / cos) or **exponential** asymmetry (exp / log).

### 6.1 Exponential
```
exp(z) + c
```
The exponential map: `exp(a+bi) = e^a · (cos b + i sin b)`. The
silhouette is dramatically asymmetric in the real axis: right half
(positive `Re(z)`) escapes in 1–2 iterations because `|exp(z)|` is
`e^Re(z)`. Left half oscillates. Boundary is fractally feathered.

External: Devaney's papers on exponential dynamics —
[search "Devaney exponential dynamics"](https://www.google.com/search?q=Devaney+exponential+dynamics+fractal).

### 6.2 Sinusoidal
```
sin(z) + c
```
`sin(a+bi) = sin(a)·cosh(b) + i cos(a)·sinh(b)`. The cosh factor blows
up in the imaginary axis. Silhouette has narrow horizontal bands of
bounded behaviour separated by escape strips at multiples of π.

### 6.3 Logarithmic shell
```
log(1 + z) + c
```
`log` has a branch cut along the negative real axis. The `1 +` offset
shifts the cut so it doesn't pass through z₀=0 — without that shift,
`log(0) = −∞` immediately on the first iteration and every pixel
registers as escaped/in-set uniformly (no fractal). With the offset,
the silhouette develops a curved boundary that flattens toward the
asymptote.

Avoid bare `log(z) + c`: with seed z₀=0 the first step yields
`-Inf + 0i`, the orbit becomes NaN, and the renderer produces a
single solid colour. Always guard `log` arguments to be non-zero at
the seed.

### 6.4 Mixed trig — "petal" pattern
```
sin(z) + cos(c)
```
Constant `c` enters through `cos`, periodic in `Re(c)`. Result is a
quasi-periodic mosaic of bounded regions repeating with period 2π
horizontally and Cassini-like vertically.

### 6.5 Damped oscillator
```
0.5*sin(z*z) + c
```
Squares `z` inside the sin, then halves the magnitude. Produces a
dampened oscillation around the classical Mandelbrot silhouette —
fine ripples ride on top of the cardioid boundary.

### 6.6 Tangent map
```
tan(z) + c
```
`tan(z) = sin(z)/cos(z)`. Poles at `cos(z) = 0` (every odd multiple
of π/2 on the real axis). The silhouette has periodic vertical lines
of singularities; bounded regions appear as ovals between them.

### 6.7 Hyperbolic family
```
sinh(z) + c
cosh(z) + c
tanh(z) + c
```
`sinh` / `cosh` are the hyperbolic analogues — they grow exponentially
in BOTH directions of the real axis (unlike `exp` which grows only to
the right). `tanh` saturates to ±1 and produces nearly-flat bounded
regions with sharp transitions.

### 6.8 Square root principal branch
```
sqrt(z) + c
```
Desugars to `exp(0.5*log(z))`. The principal branch of √z. Bounded
set is concentrated near the origin with two "tail" structures along
the imaginary axis (where the branch cut is approached from above and
below).

### 6.9 Constants pi and e
```
sin(pi*z) + c             — sin with period 2 on the real axis
exp(z) + e                — exp shifted by Euler's constant
z*z + pi*c                — quadratic with π-scaled forcing
```
`pi` and `e` parse as their numeric values (`Math.PI`, `Math.E`)
respectively, exactly like writing `3.14159...`.

### Important note: gating

Transcendentals disable perturbation, BLA, and SA. The deep-zoom path
falls back to DD/QD HpDirect with **scalar-precision** transcendental
calls inside the QD chain (precision degrades to ~16 decimal digits
inside each `sin`/`exp` etc. call). Distance estimate stays available
— sin/cos/exp/log are all holomorphic.

---

## 7. Phoenix family — using `prev`

The Phoenix family extends `z_{n+1} = f(z_n, c)` to
`z_{n+1} = f(z_n, z_{n-1}, c)`. The previous iterate `z_{n-1}` is a
new feedback term. CalcGen exposes this as `prev`.

### 7.1 Classical Phoenix
```
z*z + c + 0.56667*prev
```
The classical Shigehiro Ushiki construction. The `0.56667` coefficient
on `prev` is the standard parameter. Try values between 0.3 and 0.8
to see the silhouette deform.

External: [Wikipedia: Phoenix fractal](https://en.wikipedia.org/wiki/Phoenix_set).

### 7.2 Phoenix with negative feedback
```
z*z + c - 0.4*prev
```
Negative `prev` coefficient. The silhouette becomes more concentrated;
arms shrink back into the body.

### 7.3 Cubic Phoenix
```
z*z*z + c + 0.5*prev
```
Phoenix coupling on a degree-3 polynomial. Combines the 3-fold
Multibrot symmetry with the prev-step feedback.

### 7.4 Two-tap Phoenix
```
z*z + 0.3*z*prev + c
```
Phoenix coupling via a product term. The previous iterate now scales
the linear `z` contribution rather than entering additively.

### Important note: gating

`prev` disables distance estimate and perturbation. Tracking
`dprev/dc` properly requires a second derivative state vector (a
future engine extension). For now, `prev` equations render at scalar
or AVX2 escape-time only.

---

## 8. Time-dependent / non-autonomous family — using `n`

Most fractal equations are **autonomous**: the step `f(z, c)` doesn't
depend on the iteration index. The CalcGen DSL exposes `n` (or its
alias `iter`) so you can make `f` depend on the step number explicitly.
This breaks the classical theory but produces interesting visual
results — the rule itself drifts over time.

### 8.1 Iter-dependent drift
```
z*z + c + 0.001*n
```
Adds a tiny constant push proportional to the iteration count. The
silhouette gradually shifts off-axis as orbits accumulate the drift.

### 8.2 Iter-modulated phase
```
sin(z*n) + exp(c + z) + z
```
The argument to `sin` is multiplied by `n`. As `n` grows the "frequency"
of `sin` increases linearly, so neighbouring pixels with slightly
different `Im(z)` land in increasingly different phases. Produces fine
filament structure that intensifies near the boundary. The `exp(c + z)`
adds an exponential right-side asymmetry; replace with `cosh(c + z)`
to mirror it.

### 8.3 Iter-decaying coefficient
```
z*z + c/(1 + 0.01*n)
```
Forcing weakens as iteration progresses. Late-iter pixels rely on
pure feedback. Boundary fattens because forcing can't push out late
orbits.

### 8.4 Quadrant by iter parity
```
if mod(n, 2) > 0.5 then z*z + c else z*z - c
```
Alternates between adding and subtracting `c` based on `n`'s parity.
Treats odd and even iterations differently. Result is a more textured
silhouette with high-frequency boundary detail.

### Important note: gating

`n` disables perturbation, BLA, SA, AND distance estimate. The
classical theory assumes the step function is autonomous; iter-dependent
equations break those assumptions. Renders at scalar / AVX2 escape-time
only. No deep zoom optimisations.

---

## 9. Piecewise / conditional family — using `if`

CalcGen's `if cond then a else b` lets you splice two different step
functions together at a boundary defined by a real-valued comparison
on the current iterate.

### 9.1 Branch by magnitude
```
if abs(z) > 1 then z*z + c else z*z*z + c
```
`abs(z)` here is `|z|²` (the DSL's squared-magnitude convention).
Quadratic step when the orbit is inside `|z|² ≤ 1`; cubic step
outside. The silhouette is the union of the classical Mandelbrot
(inside the unit disk) and a Multibrot-cubic shell (outside).

### 9.2 Branch by component sign
```
if re(z) > 0 then z*z + c else conj(z)*conj(z) + c
```
Holomorphic Mandelbrot on the right half-plane; Tricorn-like on the
left. Produces a literal left-right mirror with different boundary
characters on each side.

### 9.3 Quadrant switch
```
if im(z) > 0 then z*z + c else z*z*z + c
```
Mandelbrot above the real axis, Multibrot-cubic below. Sharp
asymmetric silhouette.

### 9.4 Branch by phase
```
if arg(z) > 0 then z*z + c else z*z - c
```
`arg(z)` is the polar angle. Upper half-plane (`arg > 0`) adds `c`;
lower half subtracts. Produces a Mandelbrot silhouette with a folded
boundary along the real axis.

### 9.5 Bailout-band switch
```
if abs(z) > 100 then z + c else z*z + c
```
Switches to a linear (slow-escape) step once the orbit gets far from
the origin. Effectively raises the bailout radius without raising it
literally — orbits beyond `|z|² = 100` linger one step longer before
being declared escaped.

### Important note: gating

`if` disables perturbation, BLA, SA (the δ-Taylor expansion has no
closed form across the branch boundary). Distance estimate stays
available **inside each branch** — there's a discontinuity along the
locus where the condition flips, which the engine doesn't currently
detect as a feature.

---

## 10. Argument-driven family — using `arg` and `atan2`

`arg(z)` returns the polar angle of `z` in `(−π, π]`. It's a real
scalar lifted back to complex as `(arg, 0)`. Lets you encode
phase-dependent dynamics.

### 10.1 Spiral by angle
```
z*z + 0.1*arg(z) + c
```
Adds a small angle-proportional drift. Produces spiral arms in the
boundary structure.

### 10.2 Phase-modulated forcing
```
z*z + c*exp(arg(z))
```
The forcing magnitude is scaled by the orbit's current phase. Pixels
whose orbits spin produce different forcing than pixels whose orbits
stay near the real axis. Strong asymmetric spiral structure.

### 10.3 Binary atan2
```
z*z + 0.05*atan2(z, c) + c
```
`atan2(y, x)` is the two-argument arctangent: `atan2(y, x) =
arg(x + iy)`. Treats `z` as the imag part and `c` as the real part of
a phasor. Produces a slowly-rotating phase term that varies smoothly
across the parameter plane.

### Important note: gating

`arg` and `atan2` are non-holomorphic. Disable distance estimate,
perturbation, BLA, SA. On AVX2 the binary `atan2` per-lane scalarises
(no SIMD intrinsic for binary atan2) — costs ~4× scalar atan2 per
body but keeps the surrounding pipeline 4-wide.

External: [Wikipedia: atan2](https://en.wikipedia.org/wiki/Atan2).

---

## 11. Real binary family — min / max / mod

These act on the real parts of their operands only. Produce piecewise-
linear envelopes, periodic wraps, and clamping behaviour.

### 11.1 Clamp by min/max
```
min(z*z, max(z, -1.0)) + c
```
Clamps the feedback to the range `[max(z, −1), z²]`. Produces a
silhouette that follows the Mandelbrot at high magnitudes but flattens
near the origin.

### 11.2 Periodic wrap via mod
```
z*z + mod(z, 1.0) + c
```
Wraps `Re(z)` to the interval `[0, 1)` and adds the residual to the
feedback. Produces a quasi-periodic horizontal texture in the
silhouette.

### 11.3 Hybrid step
```
max(z*z, sqr(z)) + c
```
Tautological in pure z (both branches equal) but a useful template
when you want to combine two different step laws and take the larger
magnitude winner.

### 11.4 Saw-tooth periodicity
```
z*z + mod(z*z + c, 2.0)
```
Adds a horizontal saw-tooth wave to the feedback. Boundary develops
periodic ledges.

### Important note: gating

`min` / `max` / `mod` are non-holomorphic. Disable distance estimate,
perturbation, BLA, SA. On AVX2 `min` / `max` use `Vector256.Min` /
`Vector256.Max` intrinsics (stay vectorised); `mod` falls back to
per-lane scalar `%`.

---

## 12. Imaginary unit family — using `i`

The DSL's `i` is the imaginary unit literal `(0, 1)`. Lets you inject
complex coefficients without the awkward `re/im` decomposition.

### 12.1 Translated Mandelbrot
```
z*z + c + i
```
Adds the imaginary unit as a literal constant translation. Equivalent
to running the classical Mandelbrot in a parameter plane shifted down
by `i` — the silhouette is identical to the standard cardioid,
translated upward by one unit in the imaginary direction.

Note: avoid the tempting `i*z + c` as a "rotated Mandelbrot". It's a
linear Möbius map with z₀=0 producing the period-4 cycle
`0 → c → (1+i)c → ic → 0 → ...`, bounded by `√2·|c|` for *every* `c`.
It never escapes anywhere on the visible plane, so the result is a
uniform "in set" colour. You need at least a quadratic in `z` to get
a fractal silhouette — see 12.2.

### 12.2 Complex coefficient on the quadratic
```
i*z*z + c
```
The quadratic feedback is rotated by 90° each step. Produces a
silhouette with twisted lobes rotated relative to the classical
Mandelbrot.

### 12.3 Mixed real + imaginary coefficients
```
0.5*z*z + 0.3*i*z + c
```
A quadratic with both real and imaginary coefficients on different
powers of `z`. The full parameter space of degree-2 polynomials.

### Important note: not gating

`i` is a holomorphic constant. Distance estimate, perturbation, BLA,
SA all stay enabled. The differentiator returns 0 for `i`, but the
chain rule still produces correct values via `Mul` (e.g.
`d(i·z)/dz = i`).

---

## Author's pre-flight checklist — seeds, guards, and degeneracies

Most "my equation renders as one solid colour" reports trace to the seed. The
engine starts every orbit at `z₀ = 0`, so the very first step evaluates
`f(0, c)`. If that produces `Inf` / `NaN`, or a trivially bounded cycle, the
whole plane collapses to a single colour and there is no fractal to see. Run
through this table before blaming the renderer.

| Construct                     | What happens at `z₀ = 0`                        | Guard / fix                                             |
|-------------------------------|-------------------------------------------------|---------------------------------------------------------|
| `1/z`, `(1/z + c)^2`          | `1/0 = Inf` → `NaN` next step; NaN never bails → everything "in set" | Guard the denominator so it is non-zero at 0: `1/(z*z + c)`, `1/(z + 2)` |
| `log(z) + c`                  | `log(0) = −∞` → `NaN`; uniform solid            | Offset the argument off the branch point: `log(1 + z) + c` |
| `sqrt(z)` at a pole chain     | Fine at 0 (`sqrt(0)=0`) but watch downstream `/` | Keep any following division guarded as above            |
| `i*z + c` (linear in `z`)     | Period-4 cycle `0 → c → (1+i)c → ic → 0` — bounded for *every* `c`, so nothing escapes | Add a genuine quadratic: `i*z*z + c` |
| `(z^2 + c)/(z + a)`           | `z₁ = c/a`, then instant fixed point `f(c/a)=c/a` | Keep polynomial feedback separate from the rational term: `z*z + c/(z + a)` |
| `conj(fold(z))` vs `fold(conj(z))` | Not equal — `conj` negates `im`, `fold` re-positives it, so `fold(conj(z))` cancels back to plain Burning Ship | Pick the order deliberately; `conj(fold(z))` is the mirrored hybrid |

> [!TIP]
> Quick test for a suspected seed problem: temporarily prepend a tiny non-zero
> shift, e.g. change `f(z,c)` to reference `z + 0.0001` in the offending term. If
> the solid colour breaks up, the culprit was a singularity at the seed and the
> real fix is one of the guards above.

### Keeping the distance estimate alive

The DE (surface-normal / "3-D relief" shading) needs the equation to stay
**holomorphic** so the chain rule that tracks `dz/dc` has a closed form. Use this
as a helper when you want the relief look:

| Keeps DE on (holomorphic)                              | Turns DE off (non-holomorphic)          |
|--------------------------------------------------------|-----------------------------------------|
| `+ - *`, integer power `^`, `sqr`, division `/`        | `conj`, `fold`                          |
| `sin cos tan sinh cosh tanh exp log sqrt`              | `arg`, `atan2`                          |
| constants `i`, `pi`, `e`                               | `min`, `max`, `mod`                     |
|                                                        | `prev`, `iter` / `n`                    |

Division and transcendentals keep DE but still cost you perturbation / BLA / SA
(see [§14](#14-gating-quick-reference)); only pure polynomial-in-`z` equations
keep *everything*. So the recipe for "custom fractal with relief shading **and**
deep zoom" is: stay polynomial.

---

## 13. Equation modification cookbook

Practical recipes for changing a working equation to push the result
in a chosen direction.

### "I want more symmetry"

| Have                         | Try                                       |
|------------------------------|-------------------------------------------|
| Asymmetric `exp`             | Replace `exp(x)` with `cosh(x)` —         |
|                              | grows in both Re directions equally.      |
| One-sided branch             | Use `fold(...)` on the relevant operand   |
|                              | to mirror the half-plane.                 |
| Single-lobe silhouette       | Increase the polynomial degree:           |
|                              | `z*z + c` → `z*z*z + c` (more lobes).     |

### "I want more chaotic detail"

| Have                         | Try                                       |
|------------------------------|-------------------------------------------|
| Smooth boundary              | Multiply transcendental argument by `n`:  |
|                              | `sin(z) + c` → `sin(z*n) + c`.            |
| Boundary too smooth          | Mix two laws via `if`:                    |
|                              | `if abs(z) > 1 then z^3 + c else z^2 + c` |
| Want feathering              | Mix `exp` + `sin`: `exp(z) + sin(c) + z`. |

### "I want slower escape (more bounded points)"

| Have                         | Try                                       |
|------------------------------|-------------------------------------------|
| Magnitude grows too fast     | Scale down the feedback: `z*z + c` →      |
|                              | `0.5*z*z + c`.                            |
| Right side escapes too fast  | `exp(c+z)` → `cosh((c+z)/2)`.             |
| Hyperbolic too steep         | Divide argument: `cosh(z) + c` →          |
|                              | `cosh(z/4) + c`.                          |

### "I want faster escape (sharper boundary)"

| Have                         | Try                                       |
|------------------------------|-------------------------------------------|
| Boundary too soft            | Higher polynomial degree.                 |
| Magnitudes bounded too long  | Add a `c` multiplication step: `z*z + c`  |
|                              | → `z*z*c + c`.                            |
| Want explosive boundary      | Use `exp(z)` somewhere in the feedback.   |

### "I want rotational structure"

| Have                         | Try                                       |
|------------------------------|-------------------------------------------|
| Pure radial silhouette       | Inject `i`: `z*z + c` → `i*z*z + c`.      |
| Want spiral arms             | Add `arg(z)`-driven term:                 |
|                              | `z*z + 0.1*arg(z) + c`.                   |
| Want time-rotating dynamics  | Multiply by `i^n` (effectively, use `n`   |
|                              | inside a phase): `sin(z*n) + c`.          |

### "I want time-varying dynamics"

Use `n` (or `iter`). Examples:

- `z*z + c + 0.001*n` — slow linear drift.
- `z*z + c/(1 + 0.01*n)` — decaying forcing.
- `sin(z*n) + c` — frequency increases each step.
- `if mod(n, 2) > 0.5 then a else b` — alternating step laws.

Accept that you lose distance estimate, perturbation, BLA, SA when
you use `n`. Use scalar / AVX2 escape-time only.

### "I want deep-zoom to keep working"

Stick to **polynomial in `z` + `c`**. Avoid `conj`, `fold`, `div`,
`prev`, `iter`, transcendentals, `if`. The classical Mandelbrot and
all its Multibrot/Phoenix/coupling variants stay deep-zoomable through
perturbation, BLA, SA, and DD/QD HpDirect.

| Family                    | Deep zoom |
|---------------------------|-----------|
| `z^d + c` (any `d`)       | ✓ all the way to QD precision |
| Polynomial in `z` + `c`   | ✓ full perturbation + generic SA |
| Phoenix (`prev`)          | ✗ scalar only past ~1e12 |
| Anti-holomorphic          | ✗ scalar only past ~1e12 |
| Transcendental            | ✗ HpDirect DD/QD; no perturbation |
| Conditional (`if`)        | ✗ scalar/AVX2 only past ~1e12 |
| `iter` / `prev`           | ✗ scalar/AVX2 escape-time only |

---

## 14. Gating quick reference

The CalcGen engine emits five execution paths for every equation:

1. **Scalar** — plain `double` per pixel. Always available.
2. **AVX2 / AVX-512 SIMD** — vector per 4 / 8 pixels. Almost always
   available.
3. **Perturbation** — δ-Taylor expansion against a reference orbit.
   Required for deep zoom past ~1e12.
4. **BLA / SA** — skip iterations via series approximation. Required
   for *fast* deep zoom.
5. **DD/QD HpDirect** — DoubleDouble / QuadDouble arithmetic per pixel.
   Fallback for deep zoom when perturbation is off.

Operators that disable specific paths:

| Operator       | Disables                                   |
|----------------|--------------------------------------------|
| `conj`, `fold` | Perturbation, BLA, SA, DE                  |
| `/` (div)      | Perturbation, BLA, SA                      |
| `sin`/`cos`/   | Perturbation, BLA, SA                      |
| `exp`/`log`/   | (DE still works — holomorphic)             |
| `tan`/`sinh`/  |                                            |
| `cosh`/`tanh`/ |                                            |
| `sqrt`         |                                            |
| `arg`,`atan2`  | Perturbation, BLA, SA, DE                  |
| `min`,`max`,`mod` | Perturbation, BLA, SA, DE               |
| `if`           | Perturbation, BLA, SA                      |
| `prev`         | Perturbation, BLA, SA, DE                  |
| `iter` / `n`   | Perturbation, BLA, SA, DE                  |
| `i`            | (none — holomorphic constant)              |
| `pi`, `e`      | (none — real constants)                    |

When everything is gated off, the renderer still produces correct
escape-time output via the scalar / AVX2 path. You only lose the
acceleration features and surface-normal shading.

---

## 15. External resources

### Tutorials and galleries

- [Paul Bourke's fractals page](http://paulbourke.net/fractals/) — long
  archive of equation families with visual examples.
- [FractalForums.org](https://fractalforums.org/) — active community,
  many novel equation discoveries.
- [Wikipedia: List of fractals by Hausdorff dimension](https://en.wikipedia.org/wiki/List_of_fractals_by_Hausdorff_dimension)
- [Wikipedia: Newton fractal](https://en.wikipedia.org/wiki/Newton_fractal)
- [Wikipedia: Buddhabrot](https://en.wikipedia.org/wiki/Buddhabrot)

### Mathematical background

- John Milnor, *Dynamics in One Complex Variable* (book) — the
  authoritative treatment of `z² + c` dynamics.
- Robert Devaney, *An Introduction to Chaotic Dynamical Systems*
  (book) — covers the exponential family and broader chaos theory.
- [Inigo Quilez articles](https://iquilezles.org/articles/) — practical
  derivations of distance estimates, smooth iteration, surface
  normals.

### Deep-zoom engines

- [Kalles Fraktaler](http://www.chillheimer.de/kallesfraktaler/) — the
  reference C++ deep-zoom Mandelbrot renderer; pioneered many of the
  perturbation/BLA/SA techniques this engine uses.
- [Ultra Fractal](http://www.ultrafractal.com/) — commercial fractal
  renderer with a full scripting language for custom equations.

### Algorithm papers

- Pauldelbrot 2014 — "Glitch detection in perturbation Mandelbrot",
  the original glitch-detection criterion. Search for the
  FractalForums thread.
- Botsch 2013 — series approximation for `z² + c`. The basis for
  CalcGen's SA emitter.
- Zhuoran 2021 — Bilinear approximation, the basis for CalcGen's BLA
  emitter.

---

## 16. Glossary

| Term            | Meaning                                                  |
|-----------------|----------------------------------------------------------|
| Autonomous map  | Step function `f(z, c)` independent of iteration index. |
| Bailout         | `\|z\|²` threshold at which the engine declares escape.  |
| Cardioid        | The heart-shaped main body of the Mandelbrot set.        |
| DE              | Distance Estimate — surface-normal shading; gives the   |
|                 | "3D relief" look.                                        |
| Holomorphic     | Complex-differentiable in the Wirtinger sense; preserves |
|                 | the chain rule that DE needs.                            |
| Julia set       | Slice of a fractal at fixed `c` and varying `z₀`. (The   |
|                 | engine uses fixed `z₀ = 0` and varying `c` — the         |
|                 | Mandelbrot parameter space.)                             |
| Non-autonomous  | Step function depends on iteration index `n` explicitly. |
| Perturbation    | δ-expansion technique that lets the engine compute one  |
|                 | reference orbit at high precision and propagate it to    |
|                 | per-pixel offsets in plain `double`.                     |
| SA              | Series Approximation — skip many iterations at once via  |
|                 | a Taylor expansion of the polynomial.                    |
| BLA             | Bilinear Approximation — a faster variant of SA that     |
|                 | handles arbitrary polynomial steps.                      |
| Smooth iter     | Continuous correction to the integer escape count, used  |
|                 | for gradient-smoothed colouring.                         |
| Wirtinger       | Pair of partial derivatives `∂/∂z`, `∂/∂z̄` used to       |
|                 | reason about complex-differentiability.                  |

---

## 17. See also

- [CalcGen-UserGuide.md](CalcGen-UserGuide.md) — the precise DSL
  grammar reference, supported operators, gating rules.
- [Avalonia-UserGuide.md](Avalonia-UserGuide.md) — UI walkthrough
  including the User Equation editor.
- [UserBulb-Guide.md](UserBulb-Guide.md) — the 3D-analogue equation
  designer (raymarched escape-time fractals using vec3/quat instead
  of complex arithmetic).
