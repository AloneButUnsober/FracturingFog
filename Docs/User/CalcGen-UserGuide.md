# User Equation & the Fracturing Fog DSL — Guide + Cookbook

This is the complete authoring reference for **your own fractal formulas** in
Fracturing Fog. It documents the equation language (the *DSL*) in full — every
operator, function, and constant, the mathematical grammar, how an equation is
built and iterated, which features are available on which rendering path — and
closes with an exhaustive cookbook of ready-to-paste recipes.

It is written for **authors**, not implementers. If you want the generator
internals (AST, simplifier, Taylor/BLA expansion, distance-estimate derivation),
read the technical companions:

> Companion pages: [User Index](_Index.md) ·
> [Fractal Equation Design Guide (technical)](../Technical/FractalEquation-DesignGuide.md) ·
> [CalculatorGen Authoring (technical)](../Technical/CalculatorGen-Authoring.md) ·
> [User Bulb 3D Guide](UserBulb-Guide.md) · [ColorGen User Guide](ColorGen-UserGuide.md)

---

## Table of contents

1. [What "your own equation" means](#1-what-your-own-equation-means)
2. [Two engines, one language](#2-two-engines-one-language)
3. [The editor at a glance](#3-the-editor-at-a-glance)
4. [Mathematical grammar — how an equation is built](#4-mathematical-grammar--how-an-equation-is-built)
5. [Language reference — operators](#5-language-reference--operators)
6. [Language reference — the function catalogue](#6-language-reference--the-function-catalogue)
7. [Constants, variables, and state](#7-constants-variables-and-state)
8. [Structure: `let`, statement blocks, and conditionals](#8-structure-let-statement-blocks-and-conditionals)
9. [Holomorphic vs non-holomorphic — why it matters](#9-holomorphic-vs-non-holomorphic--why-it-matters)
10. [Execution paths and what gates them](#10-execution-paths-and-what-gates-them)
11. [Orbit controls — escape radius, z₀ seeding (Julia), convergence](#11-orbit-controls--escape-radius-z-seeding-julia-convergence)
12. [Migrating C# equations to the DSL](#12-migrating-c-equations-to-the-dsl)
13. [The Cookbook](#13-the-cookbook)
14. [Editor workflow](#14-editor-workflow)
15. [Command-line generation](#15-command-line-generation)
16. [Troubleshooting](#16-troubleshooting)
17. [Reference card](#17-reference-card)
18. [See also](#18-see-also)

---

## 1. What "your own equation" means

A *fractal equation* in Fracturing Fog is the **single line of math the app runs
once per pixel, per iteration**. The classic Mandelbrot recipe is:

$$
z_{n+1} = z_n^{2} + c
$$

`z` starts at zero, `c` is the pixel's coordinate on the complex plane. Repeat
that line hundreds or thousands of times for every pixel, ask whether `|z|` ever
runs away past a bailout radius, and colour each pixel by how fast it escaped.
That is the whole idea behind every escape-time fractal.

The **User Equation** type lets you replace that one line with *anything the DSL
can express*. In the DSL, the Mandelbrot recipe is simply:

```text
z*z + c
```

You are always writing the **right-hand side** of `z_{n+1} = …`. You never write
`z_{n+1} =` yourself — the app supplies the loop, the escape test, the precision
management, the colouring, and the deep-zoom machinery. You supply the math.

> [!NOTE]
> The DSL is deliberately small and **pure**: it has numbers, the two fractal
> variables, arithmetic, a fixed catalogue of mathematical functions, and simple
> structure (`let`, conditionals). It has **no** file access, no loops, no
> reflection, nothing from the .NET runtime. That is what makes an equation safe
> to save, share, and open from someone else's region file.

---

## 2. Two engines, one language

The same equation can be run by **two different engines**, and it is worth
understanding the split because it decides how deep you can zoom and how fast the
render is.

| | **Live interpreter** | **CalcGen compiler** |
|---|---|---|
| Where | The editor's live preview + the `User Equation` fractal type | The **DSL** tab → **Compile & Load** / **Generate via CalcGen** |
| How it runs | Interprets the equation directly, one pixel at a time | Generates typed C#, compiles it, and hot-loads a real calculator |
| Speed | Good for authoring; scalar only | Fast: scalar **+ AVX2 SIMD + GPU** |
| Deep zoom | Shallow (roughly to the limit of `double`) | **Deep** — perturbation, BLA, Series Approximation, DD/QD high precision |
| Surface normals / distance estimate | **Yes** — exact analytic `dz/dc` for holomorphic maps (numeric for the rest) | **Yes**, where the math allows |
| Language | The DSL (this document) | The DSL (this document) |

Both engines speak **the same DSL**, with two small dialect differences noted
throughout (the live interpreter additionally understands `let … in`, `?:`,
`&& || !`, and multi-statement blocks; CalcGen understands `if … then … else`
and integer-only `^`). Everything in the [function catalogue](#6-language-reference--the-function-catalogue)
works in both.

The practical workflow: **author in the live editor** (instant feedback), then,
when you want speed or a deep dive, **Compile & Load** through CalcGen.

> [!NOTE]
> Older versions accepted raw C# that was compiled with full .NET access. That
> path has been retired for safety. The `User Equation` tab still *accepts*
> C#-style text (`return z*z + c;`) and automatically translates it to the DSL
> for you (see [§12](#12-migrating-c-equations-to-the-dsl)), but the DSL is now
> the real language underneath. New equations should be written directly in DSL.

---

## 3. The editor at a glance

Open it with toolbar **Type → User Equation**, then **Params**. The editor has
two input tabs and a live analysis panel.

| Control | What it does |
|---|---|
| **User Equation** tab | Accepts DSL *or* C#-style (`return z*z + c;`). Runs live on the interpreter and auto-renders as you type. C# is translated to DSL automatically. |
| **DSL** tab | Bare DSL (`z*z + c`). Feeds the CalcGen buttons. |
| **Compile & Load** | CalcGen-compiles the DSL and swaps it onto the live render (unlocks SIMD / GPU / deep zoom / normals). |
| **Compile + Save** | The same, and stores the equation in your library. |
| **Generate via CalcGen** | Writes `Calculators/Generated/{Name}Calculator.cs` for a permanent, build-time calculator. |
| **Validate for CalcGen** | Parses without rendering — tells you whether the DSL compiles and what paths it will support. |
| **Equation Guide** | Opens this document. |
| **Rotation°** | Rotates the sampling plane for display only; the equation is unchanged. |
| **Escape r** | The bailout radius the orbit must exceed to count as escaped (`0` = automatic default). Lower it for transcendental maps whose orbits stay small — see [§11](#11-orbit-controls--escape-radius-z-seeding-julia-convergence). |
| **z₀ seed** | A DSL expression for the *starting* value of the orbit (blank = `0`). Set it to `c` for a **Julia** set, or to a critical point for other families. |
| **Converge-if** | A boolean DSL condition; when it becomes true the orbit stops early and the pixel is coloured as **converged** (for Newton / Magnet / Nova maps). Blank = escape-only. |

The **live analysis panel** mirrors what CalcGen deduces from your equation:

| Readout | Meaning |
|---|---|
| **AST** | The parsed equation, normalised and pretty-printed. |
| **dz/dz**, **dz/dc** | The symbolic derivatives used for normals and distance estimate. |
| **SA** | Series-Approximation degree (0 = unavailable). |
| **Perturbation** | Whether deep-zoom perturbation is available. |
| **DE / normals** | Whether the distance estimate / surface normals are available. |
| **Flags** | The feature flags (`conj`, `div`, `trans`, `cond`, …) the equation tripped. |

Use the panel as a live gating check: if **DE / normals** reads *off* and you
wanted lit relief, you know a construct in your equation disabled it — see
[§9](#9-holomorphic-vs-non-holomorphic--why-it-matters) and
[§10](#10-execution-paths-and-what-gates-them).

---

## 4. Mathematical grammar — how an equation is built

### 4.1 The complex plane

Every value in the DSL is a **complex number** `a + b·i`, where `a` is the real
part and `b` the imaginary part. A plain number like `2` or `0.5` is the complex
value `(2, 0)` — a *real* value, which the engine tracks specially so it can skip
the dead imaginary half of the arithmetic. The pixel coordinate `c` and the
iterate `z` are full complex numbers.

`i` is the imaginary unit `(0, 1)`. So `3 + 4*i` is the complex number with real
part 3 and imaginary part 4, and `i*z` rotates `z` by 90°.

### 4.2 The iteration

An equation defines one **step**. The host wraps it in the loop:

```text
z ← 0            (or a seed)
repeat:
    z ← <your equation>          # c is the pixel; z is the running value
    if |z| > bailout: escaped
```

Because the equation is re-evaluated every iteration, the *same* short formula
produces unlimited detail — the feedback is where the fractal comes from. The
[Design Guide §1–2](../Technical/FractalEquation-DesignGuide.md#1-what-an-escape-time-fractal-actually-is)
explains the dynamics in depth.

### 4.3 Real vs complex values

Some functions return a **real** value lifted back into the complex plane as
`(value, 0)`:

- `re(z)`, `im(z)`, `abs(z)`, `arg(z)` — extract a real scalar.
- `min`, `max`, `mod`, `clamp`, `atan2` — operate on real parts.
- `floor`, `ceil`, `round`, `trunc`, `fract`, `sign` — act per component.

Everything else is fully complex. Mixing is fine: `z*z + abs(z) + c` adds the
complex `z*z`, the real-lifted `abs(z)`, and the complex `c`.

### 4.4 Precedence and associativity

From loosest to tightest binding:

| Level | Operators | Associativity |
|---|---|---|
| 1 | `+`  `-` | left |
| 2 | `*`  `/` | left |
| 3 | `^` (power) | CalcGen: applies to the preceding factor, integer exponent. Live: right-associative, any exponent |
| 4 | unary `-` | prefix |
| 5 | atoms: numbers, `z`, `c`, `(…)`, `f(…)` | — |

So `2*z^2` is `2*(z^2)`, and `-z*z` is `-(z*z)` is `(-z)*z` — all the same by
sign rules, but be explicit with parentheses when in doubt. `a - b - c` is
`(a - b) - c`.

> [!NOTE]
> **Power differs between the two engines.** CalcGen's `^` takes an **integer
> exponent 0–64** and applies to the immediately preceding factor (`z^3`,
> `c^2`); for anything else — negative, fractional, or complex exponents — use
> the `pow(base, exp)` function. The live interpreter's `^` is right-associative
> and accepts any exponent (`z^2.5`, `z^-3`). For portability across both
> engines, prefer `pow()` whenever the exponent is not a small non-negative
> integer.

### 4.5 Building an equation, step by step

Start from the Mandelbrot baseline and layer ideas:

```text
z*z + c                       # 1. Mandelbrot
z*z*z + c                     # 2. raise the power → Multibrot
z*z*z - z + c                 # 3. add a linear term → a new critical structure
z*z*z - z + 0.5*conj(z) + c   # 4. mix in a conjugate → break the symmetry
```

Each change is a hypothesis; **Compile & Load** (or just watch the live render)
is the experiment. Keep the ones that surprise you and **Save** them with a
descriptive name.

---

## 5. Language reference — operators

| Operator | Example | Meaning |
|---|---|---|
| `+` | `z + c` | Complex addition |
| `-` | `z - c`, `-z` | Complex subtraction; unary negation |
| `*` | `z*z`, `2*c` | Complex multiplication |
| `/` | `z / (z + 1)` | Complex division (see gating in [§10](#10-execution-paths-and-what-gates-them)) |
| `^` | `z^2`, `z^3` | Power. CalcGen: integer 0–64. Live: any exponent, right-assoc. Prefer `pow()` for non-integer. |
| `(` `)` | `(z + c)*(z - c)` | Grouping |

Comparisons — only inside a condition (`if …` in CalcGen, `?:`/`&&`/`||` live):

| Operator | Meaning |
|---|---|
| `<`  `<=`  `>`  `>=` | ordered comparison of real scalars |
| `==`  `!=` | equality / inequality |

Boolean operators (**live interpreter only**): `&&` (and), `||` (or), `!` (not).

Ternary (**live interpreter only**): `cond ? a : b`.

> [!WARNING]
> A bare `=` is **not** an operator in an expression — use `==` to compare. In
> the live interpreter, `=` only appears in a statement block or a `let` binding
> (`let k = … in …`). In CalcGen, use `if cond then … else …`.

---

## 6. Language reference — the function catalogue

Every function below works in **both** engines unless noted. Argument counts are
fixed; names are case-insensitive.

### 6.1 Powers and roots

| Function | Signature | Meaning & notes |
|---|---|---|
| `sqr(x)` | 1-arg | `x*x`. A convenience; identical to writing `x*x` (and still deep-zoomable). |
| `pow(x, y)` | 2-arg | General power `x^y`. If both operands are real, real `Math.Pow` (so `pow(-2, 3) = -8`); otherwise the principal complex power, **zero-guarded** so `pow(0, 0) = 1` and `pow(0, k) = 0` (no `NaN` at the `z = 0` seed). Use for **negative or fractional** exponents. |
| `sqrt(x)` | 1-arg | Principal square root. In CalcGen it desugars to `exp(0.5*log(x))`, matching `Complex.Sqrt`'s branch. |

```text
sqr(z) + c                    # = z*z + c
pow(z, 3) + c                 # cubic Multibrot, general-power form
pow(z, -2) + c                # negative power — a "Donut"/inverse map (finite at z=0)
z*pow(z, -3) + c*pow(c, -2)   # "Movie Reel" — mixed inverse powers
pow(z, 2.5) + c               # fractional power (live: z^2.5 also works)
sqrt(z*z - 1) + c             # square-root shell
```

> [!NOTE]
> **Why `pow(z, -3)` instead of `1/z^3`?** At the Mandelbrot seed `z = 0`, the
> form `1/z^3` is `1/0 = NaN` and blanks the image; `pow(0, -3)` is defined as
> `0` by the zero-guard, so negative-power maps render correctly. Always express
> negative powers with `pow()`.

### 6.2 Exponential, logarithm, trigonometry

| Function | Meaning |
|---|---|
| `exp(x)` | `e^x` (complex exponential) |
| `log(x)` | Natural log, principal branch (pole at 0) |
| `sin(x)` `cos(x)` `tan(x)` | Circular trig (`tan` = `sin/cos`) |
| `sinh(x)` `cosh(x)` `tanh(x)` | Hyperbolic (built from `exp`) |

```text
exp(z) + c                    # exponential map
sin(z) + c                    # sinusoidal
log(z*z) + c                  # logarithmic shell
sin(z)*cos(c) + c             # mixed trig "petals"
tanh(z*z) + c                 # hyperbolic
sin(pi*z) + c                 # constants in action
```

### 6.3 Inverse trigonometry and hyperbolics

| Function | Meaning |
|---|---|
| `asin(x)` `acos(x)` `atan(x)` | Inverse circular functions (principal branch) |
| `asinh(x)` `acosh(x)` `atanh(x)` | Inverse hyperbolic functions |

These are holomorphic. **Surface normals and the distance estimate work for
them** — their analytic derivative rules are built in (e.g.
`d/dz asin(u) = u'/√(1 − u²)`). They still run on the shallow direct paths (no
deep-zoom perturbation), which is where their parity holds.

```text
atan(z) + c                   # bounded inverse-tangent map
asinh(z*z) + c                # chain rule exercised in the normals
z*z + asin(c) + c             # inverse-trig term with full distance estimate
```

> [!NOTE]
> `asin`, `acos`, `atanh` are **bounded** maps — orbits stay small and may never
> exceed the bailout, so a solid "all inside the set" image can be the *correct*
> result, not a blank. Drive them with an escaping term (e.g. a `z*z`) if you
> want classic escape banding.

### 6.4 Component and rounding functions (per-component)

Applied to the real and imaginary parts independently: `f(a + b·i) = f(a) + f(b)·i`.

| Function | Meaning |
|---|---|
| `floor(x)` | Round toward −∞, each component |
| `ceil(x)` | Round toward +∞ |
| `round(x)` | Round half-to-even |
| `trunc(x)` | Round toward zero |
| `fract(x)` | Fractional part `x − floor(x)` |
| `sign(x)` | −1 / 0 / +1 per component |
| `fold(x)` | `(|Re|, |Im|)` — the Burning-Ship absolute-value fold |

```text
fold(z)*fold(z) + c           # Burning Ship
z*z + fract(z) + c            # domain-warped / tiled Mandelbrot
z*z + 0.1*floor(z*4) + c      # quantised feedback ("pixelated" bands)
```

### 6.5 Real-valued extractors and reducers

These return a real scalar lifted to `(value, 0)`.

| Function | Meaning |
|---|---|
| `re(x)` | Real part |
| `im(x)` | Imaginary part |
| `abs(x)` | Magnitude `|x| = √(Re² + Im²)` |
| `arg(x)` | Principal argument (angle) in `(−π, π]` |
| `conj(x)` | Complex conjugate `(Re, −Im)` — this one stays complex |
| `min(a, b)` `max(a, b)` | Min / max of the real parts |
| `mod(x, p)` | Real modulo, centered, per component |
| `clamp(x, lo, hi)` | Clamp the real part to `[lo, hi]` |
| `atan2(y, x)` | Two-argument arctangent of the real parts |

```text
conj(z)*conj(z) + c           # Tricorn
z*z + 0.1*arg(z) + c          # spiral biased by orbit angle
min(z*z, max(z, -1.0)) + c    # clamp-like feedback
z*z + mod(z, 1.0) + c         # periodic wrap
clamp(z, -2.0, 2.0) + c       # bounded feedback
```

> [!WARNING]
> **`abs` has two meanings depending on where it appears.** As a value in an
> expression, `abs(x)` is the magnitude `|x|`. **Inside an `if` condition**, the
> shorthand `abs(x)` means the *squared* magnitude `|x|²` — it saves a square
> root and matches the bailout-threshold form (`if abs(z) > 4 …` is really
> `|z|² > 4`). If you need the true magnitude inside a condition, compare against
> the squared threshold, or use `re`/`im` explicitly.

---

## 7. Constants, variables, and state

| Name | Meaning | Availability |
|---|---|---|
| `z` | Current iterate (complex) | both engines |
| `c` | Pixel coordinate (complex) | both engines |
| `n` / `iter` | Current iteration index (real scalar) | both engines |
| `prev` | The previous iterate `z_{n-1}` (Phoenix coupling) | both engines |
| `pi` | π | both |
| `e` | Euler's number | both |
| `i` | Imaginary unit `(0, 1)` | both |

```text
z*z + c + 0.5*prev            # Phoenix (CalcGen)
z*z + c + 0.001*n             # slow iteration drift
i*z + c                       # 90° rotation via the imaginary unit
sin(pi*z) + e*c               # π and e as literals
```

> [!NOTE]
> `prev` and `iter` now run on **both** engines — the live interpreter carries
> the extra `z_{n-1}` state slot, so a Phoenix equation (`z*z + c + 0.5*prev`)
> renders in the editor without a Compile & Load. The one behavioural nicety of
> the compiled path is that the live interpreter also tracks `prev` through the
> **analytic derivative** (exact `dz/dc`), where CalcGen's symbolic
> differentiator treats `prev` as opaque and turns the distance estimate off.

---

## 8. Structure: `let`, statement blocks, and conditionals

### 8.1 Conditionals

**CalcGen** uses an `if … then … else` *expression* — both branches must produce
a value:

```text
if abs(z) < 1 then z*z + c else z*z - c
if re(z) > 0 then z*z + c else conj(z)*conj(z) + c
if im(z) > 0 then z^3 + c else z^2 + c
```

Each side of the comparison is a **single** condition term: `re(…)`, `im(…)`,
`abs(…)` (remember: `|x|²` here), `arg(…)`, or a numeric literal. You cannot
combine terms inside a CalcGen condition (`re(z)*im(z) > 0` is not valid there,
and there is no `&&`/`||`). For compound conditions, use the live interpreter's
ternary (next section).

The **live interpreter** additionally supports C-style ternary and boolean
operators:

```text
abs(z) < 1 ? z*z + c : z*z - c
(re(z) > 0 && im(z) > 0) ? z^3 + c : z*z + c
```

### 8.2 `let` bindings (live interpreter)

Name a sub-expression and reuse it:

```text
let w = z*z in w*w + c        # z^4 + c, computed once
let r = abs(z) in z*z + r*c   # magnitude-weighted forcing
```

### 8.3 Statement blocks (live interpreter)

A saved C#-style equation can be a short sequence of statements — declarations,
reassignments, an early guard, and a final `return`. Each desugars to a `let`;
reassignment shadows the previous binding. There are still no loops, no braces,
and no side effects.

```text
var w = z*z;
var d = w - 1;
return w*w + d + c;
```

```text
if (n == 0) z = c;            // seed on the first iteration
return z*z + c;
```

Blocks are how legacy multi-line C# equations keep working; when authoring fresh,
a single DSL expression (optionally with `let`) is usually clearer.

---

## 9. Holomorphic vs non-holomorphic — why it matters

A function is **holomorphic** (complex-differentiable) if it bends the plane
smoothly without folding or reflecting it. Holomorphicity is not academic here —
it decides which *rendering features* your equation can use:

- **Holomorphic** (polynomials, `exp`, `log`, `sin/cos/tan`, `sinh/cosh/tanh`,
  `sqrt`, inverse trig, division): the app can track `dz/dc` analytically, so
  **surface normals and the distance estimate work**, and (for polynomials) the
  deep-zoom accelerators are available.
- **Non-holomorphic** (`conj`, `fold`, `re`, `im`, `abs`, `arg`, `atan2`, `min`,
  `max`, `mod`, `clamp`, the per-component rounding functions): these fold or
  reflect the plane. They render beautifully, but the derivative chain becomes
  meaningless, so the **distance estimate / normals turn off** for equations that
  use them.

The one deliberate exception the app makes: **`pow` and the per-component
functions are transcendental/kinked**, so they disable the deep-zoom accelerators
and the distance estimate, while the **inverse-trig functions keep the distance
estimate** (their derivatives are known) even though they too disable deep-zoom
perturbation.

The live analysis panel's **DE / normals** and **Perturbation** readouts tell
you exactly what your current equation supports.

> [!NOTE]
> The **live interpreter now computes surface normals too.** For a holomorphic
> map it carries an **exact** `dz/dc` alongside the orbit (forward-mode
> automatic differentiation), so lit relief in the editor preview matches the
> compiled engine to machine precision. A non-holomorphic map (anything from the
> second bullet above) falls back to a numerical estimate of the normal, exactly
> as before — the picture is unchanged, only holomorphic maps got sharper.

---

## 10. Execution paths and what gates them

CalcGen emits every path and the runtime picks the best for the current view.
Some constructs switch specific paths off:

| Construct in your equation | Scalar | AVX2 | Perturbation | BLA / SA | DE (normals) | DD/QD deep | GPU |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| Polynomial in `z` (+ `c`) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Division `/` | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ | ✓ |
| `conj` / `fold` | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ |
| `exp` `log` `sin` `cos` `tan` `sinh` `cosh` `tanh` `sqrt` | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ (degraded) | ✓ |
| `asin` `acos` `atan` `asinh` `acosh` `atanh` | ✓ | ✓ | ✗ | ✗ | **✓** | ✓ (degraded) | ✓ |
| `pow` | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ (degraded) | ✓ |
| `re` `im` `abs` `arg` `atan2` `min` `max` `mod` `clamp` | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ |
| `floor` `round` `ceil` `trunc` `fract` `sign` | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ |
| `if … then … else` | ✓ | ✓ | ✗ | ✗ | ✓ per branch | ✗ | ✓ |
| `prev` (Phoenix) | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ |
| `n` / `iter` | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ | ✓ |

Rules of thumb:

- **Stay polynomial in `z` (+ `c`)** to keep every accelerator — that is the
  only shape that gets perturbation + BLA + Series Approximation, so it is the
  one you can dive into past 10¹⁵.
- **Transcendental holomorphic** functions keep the distance estimate (so you can
  light them as relief) but drop deep-zoom perturbation.
- **Inverse trig** is the sweet spot for lit non-polynomial art: normals on,
  perturbation off.
- The status bar shows the active path (`SP` / `AVX2` / `PT` / `DD-HP` / `QD-PT`).

---

## 11. Orbit controls — escape radius, z₀ seeding (Julia), convergence

An equation is only the **step**. Three editor knobs control the *orbit* around
that step, and together they unlock whole families the bare step cannot reach —
Julia sets, Newton/Magnet/Nova convergence maps, and transcendental maps that
otherwise render solid. They live next to the equation box (see
[§3](#3-the-editor-at-a-glance)) and each maps to a `FractalParameters` field for
the command line and saved regions.

### 11.1 Escape radius (`Escape r`)

The orbit "escapes" when `|z|` exceeds this radius; escape speed is what the
colour map bands. The default is deliberately large so quadratic maps get smooth
gradients, but **transcendental maps have a tiny dynamic range** — `log`, `sin`,
and their compositions keep `|z|` small, so at the big default *nothing escapes*
and the frame reads as one solid interior colour. Dropping the radius makes the
structure appear.

| Value | Effect |
|---|---|
| `0` | Automatic default (the legacy contract — large radius). |
| `2` | The classic Mandelbrot escape contract; a good first try for **any transcendental map that renders blank**. |
| `4`–`32` | Middle ground; raise it for smoother banding once something is escaping. |

> [!TIP]
> **"It compiles but renders solid / blank."** Nine times in ten this is the
> escape radius, not the equation. Set `Escape r` to `2` and re-render before
> touching anything else. `sin`, `log`, `exp∘`-nested and `1/z`-style maps almost
> always need a small radius.

### 11.2 z₀ seed — Julia sets and critical-point seeding (`z₀ seed`)

By default the orbit starts at `z₀ = 0`. The seed box is a DSL expression for a
different start, evaluated once per pixel with `c` bound to the pixel coordinate
(so `z = 0`, `n = 0` at seed time). Two big uses:

**Julia sets.** A Julia set fixes `c` to a constant and lets the *starting point*
sweep the plane. Fracturing Fog's `c` is always the pixel, so you make a Julia by
**seeding `z₀ = c`** (start at the pixel) and writing the constant into the
equation in place of `c`:

```text
Equation:  z*z + (-0.8 + 0.156*i)     ← constant instead of c
z₀ seed:   c                          ← start the orbit at the pixel
```

That is the classic Douady rabbit. Any map becomes a Julia the same way: seed
`c`, replace the `+ c` term with the Julia constant.

**Critical-point seeding.** For non-quadratic families the interesting parameter
image starts the orbit at the map's *critical point* (where the derivative
vanishes), not at `0`. Put that point in the seed box (e.g. `0` for `z²+c`, or a
solved critical value for a cubic).

> [!NOTE]
> A blank seed means `z₀ = 0` — every existing equation is unchanged. The seed is
> its own tiny DSL expression, so `c`, `0.5*c`, `1 - c`, or a literal all work.

### 11.3 Convergence bailout — Newton / Magnet / Nova (`Converge-if`)

Escape-time maps colour by how fast `|z|` runs to infinity. **Root-finding maps
converge instead** — Newton's method walks each pixel toward one of several
roots and *stops moving*. With only an escape test those pixels never bail and
the whole image reads as interior. The `Converge-if` box is a boolean DSL
condition over `z / prev / c / n / iter`; when it becomes true the orbit stops
and the pixel is coloured by **how quickly it converged** (raw iteration count).

The canonical trio needs all three controls together — a seed at the pixel, the
step, and a convergence test on successive iterates:

```text
# Newton for z³ − 1
Equation:    z - (z*z*z - 1)/(3*z*z)
z₀ seed:     c
Converge-if: abs(z - prev) < 0.0001
```

`prev` (the previous iterate, [§7](#7-constants-variables-and-state)) is what
makes `abs(z - prev) < ε` mean "the step barely moved — we've landed on a root".
Magnet and Nova maps follow the same shape with their own step function.

> [!NOTE]
> Convergence colouring bands by iteration, so it pairs well with a cyclic
> palette. Root-*basin* colouring (a distinct hue per root) is not wired yet;
> today all roots share the convergence-speed ramp.

### 11.4 Reproducing a fractalforums.org map (local smoke test)

A worked end-to-end example, the map `z ← log(sin(|1/z|)) + c` from
fractalforums.org. It is transcendental **and** singular at `z = 0`, so it needs
two of the controls above; it is the canonical "why is my screen blank" case.

**Mandelbrot form** (parameter plane):

```text
Equation:  log(sin(abs(1/z))) + c
z₀ seed:   c        ← must NOT start at 0: 1/z is a pole there → all-NaN
Escape r:  2        ← log∘sin stays small; at the default nothing escapes
```

**Julia form** (the forum's `c = (0, −1.55)`):

```text
Equation:  log(sin(abs(1/z))) - 1.55*i
z₀ seed:   c
Escape r:  2
```

Both render structure immediately with those two knobs set; either knob left at
its default reproduces the original bug report (blank at default radius, all-NaN
if seeded at `0`). `abs` is non-holomorphic, so the distance estimate stays off
and the normals come from the numeric path — expected for this map.

> [!TIP]
> **Smoke-test drill for any pasted forum equation:** (1) paste the step into the
> **DSL** tab; (2) if it renders solid, set `Escape r = 2`; (3) if it uses `1/z`,
> `1/…`, or `log`/`sqrt` near the origin, set `z₀ seed = c` to move off the pole;
> (4) if it is a Newton/root map, add `Converge-if: abs(z - prev) < 0.0001` with
> `z₀ seed = c`. Those four steps recover the large majority of forum maps.

---

## 12. Migrating C# equations to the DSL

The `User Equation` tab still accepts C#-style text and translates it
automatically; the table below is the exact mapping so you can convert by hand or
understand what the translator did. **Write new equations in DSL directly.**

| C# form | DSL form |
|---|---|
| `return z*z + c;` | `z*z + c` |
| `Complex.Conjugate(z)` | `conj(z)` |
| `Complex.Pow(z, 3)` | `z^3` (or `pow(z, 3)`) |
| `Complex.Pow(z, -3)` | `pow(z, -3)` |
| `Complex.Pow(z, expr)` | `pow(z, expr)` |
| `Complex.Sqrt(z)` | `sqrt(z)` |
| `Complex.Divide(a, b)` | `(a)/(b)` |
| `Complex.Sin/Cos/Tan/Exp/Log(z)` | `sin/cos/tan/exp/log(z)` |
| `Complex.ImaginaryOne` | `i` |
| `new Complex(a, b)` | `(a + (b)*i)` |
| `new Complex(a, 0)` | `a` |
| `z.Real` | `re(z)` |
| `z.Imaginary` | `im(z)` |
| `z.Magnitude` | `abs(z)` (or `sqrt(z*conj(z))`) |
| `z.Phase` | `arg(z)` |
| `Math.Abs(x)` | `abs(x)` |
| `Math.PI` / `Math.E` | `pi` / `e` |
| `Complex.Zero` / `Complex.One` | `0` / `1` |

Worked conversions:

```text
// Tricorn — C#:  var zb = Complex.Conjugate(z); return zb*zb + c;
conj(z)*conj(z) + c

// Hybrid — C#:  return z*z + c + 0.25*Complex.Conjugate(z);
z*z + c + 0.25*conj(z)

// Newton-ish — C#:  return z - Complex.Divide(z*z*z - 1, 3*z*z);
z - (z*z*z - 1)/(3*z*z)
```

> [!NOTE]
> Saved equations from older versions are migrated to DSL automatically on first
> launch, with a timestamped backup of your library kept alongside. Your edits
> are preserved; only exact unmodified legacy sources are rewritten.

---

## 13. The Cookbook

Each recipe is a complete equation you can paste into the **DSL** tab. Grouped by
family; every one is DSL (no C#).

### 13.1 The Mandelbrot / Multibrot family (deep-zoomable)

```text
z*z + c                       # classic Mandelbrot
z^3 + c                       # cubic Multibrot
z^4 + c
z^5 + c
z^6 + c
z^3 - z + c                   # cubic with a linear term
z*z + 0.5*z + c               # quadratic with linear coupling
(z^4 + z^2)/2 + c             # mixed-degree (division → shallow only)
i*z*z + c                     # complex leading coefficient
0.5*z*z + 0.3*i*z + c         # mixed real/imaginary coefficients
```

### 13.2 Anti-holomorphic — Burning Ship, Tricorn, hybrids

```text
fold(z)*fold(z) + c           # Burning Ship
conj(z)*conj(z) + c           # Tricorn (Mandelbar)
conj(fold(z))^2 + c           # Burning-Tricorn hybrid
fold(z)^3 + c                 # cubic Burning Ship
z*z + c + 0.25*conj(z)        # Mandelbrot / Tricorn blend
```

### 13.3 Rational maps (division; shallow-but-deep via DD/QD)

```text
(z*z - 1)/(z + 1) + c         # Mandelbrot-on-a-shell
z - (z*z*z - 1)/(3*z*z)       # Newton-shaped iteration
(z*z + c)/(1 + 0.1*z)         # damped feedback with a c-independent pole
1/(z*z) + c                   # inverse-square (prefer pow(z,-2)+c)
```

### 13.4 Powers via `pow` (negative / fractional)

```text
pow(z, -2) + c                # "Donut" inverse map (finite at z=0)
z*pow(z, -3) + c*pow(c, -2)   # "Movie Reel"
pow(z, 2.5) + c               # fractional Multibrot
pow(z, 3) + pow(z, -1) + c    # mixed positive/negative powers
```

### 13.5 Transcendental — exp / log / trig (lit relief works)

```text
exp(z) + c
sin(z) + c
cos(z)*z + c
log(z*z) + c
sin(z)*cos(c) + c
0.5*z + sin(z) + c            # damped oscillator
tan(z) + c
sinh(z) + c
cosh(z) - 0.5*c
tanh(z*z) + c
sqrt(z) + c
sqrt(z*z - 1) + c
sin(pi*z) + c
e*z*z + c
```

### 13.6 Inverse trig / hyperbolic (normals on)

```text
atan(z) + c                   # bounded; drive harder for banding
z*z + asin(c) + c             # escaping term + inverse-trig, with normals
asinh(z*z) + c
z*z + 0.3*atan(z) + c         # gentle inverse-tangent forcing
acosh(z) + c
```

### 13.7 Phoenix family (`prev`; Compile & Load)

```text
z*z + c + 0.5*prev            # classic Phoenix
z*z - 0.5*prev + c            # negative feedback
z^3 + 0.4*prev + c            # cubic Phoenix
z*z + 0.3*prev - 0.1*prev*prev + c   # two-tap Phoenix
```

### 13.8 Component / rounding (domain warps, quantisation)

```text
z*z + fract(z) + c            # tiled / Kali-style warp
z*z + 0.1*floor(z*4) + c      # quantised bands
z*z + 0.2*sign(re(z)) + c     # sign-driven asymmetry
z*z + round(z) - z + c        # snap-to-lattice feedback
```

### 13.9 Argument / angle driven

```text
z*z + 0.1*arg(z) + c          # spiral bias by orbit angle
z*z + 0.05*atan2(z, c) + c    # branch by relative angle
if arg(z) > 0 then z*z + c else z*z - c   # phase-split map
```

### 13.10 Real reducers — min / max / mod / clamp

```text
min(z*z, max(z, -1.0)) + c    # clamped feedback
z*z + mod(z, 1.0) + c         # periodic wrap
max(z*z, sqr(z)) + c          # hybrid step
clamp(z, -2.0, 2.0) + c       # bounded orbit
z*z + clamp(re(z), -1.0, 1.0)*i + c   # clamp only the real drive
```

### 13.11 Conditional / piecewise

```text
if abs(z) < 1 then z*z + c else z*z - c
if re(z) > 0 then z*z + c else conj(z)*conj(z) + c
if im(z) > 0 then z^3 + c else z^2 + c
```

Live-interpreter ternary equivalents — and compound conditions the CalcGen `if`
cannot express (a product of terms, `&&`/`||`):

```text
abs(z) < 1 ? z*z + c : z*z - c
(re(z) > 0 && im(z) > 0) ? z^3 + c : z*z + c
re(z)*im(z) > 0 ? z^3 + c : z^2 + c
```

### 13.12 Iteration-aware (`n` / `iter`)

```text
z*z + c + 0.001*n             # slow drift (breaks scale invariance — by design)
sin(z + 0.01*n) + c           # phase that winds with depth
(1 - n*0.001)*(z*z) + n*0.001*(z^3) + c   # crossfade quadratic→cubic
if (n mod 4) < 2 then z*z + c else z*z - c   # alternate every few iters
```

### 13.13 `let` and blocks (live interpreter)

```text
let w = z*z in w*w + c        # z^4, computed once
let r = abs(z) in z*z + 0.2*r*c
```

```text
var w = z*z;
var d = w*w - w;
return d + c;
```

### 13.14 Julia sets (`z₀ seed = c`)

Each pairs an **equation** (a fixed constant where `+ c` used to be) with the
**z₀ seed** control set to `c` — see [§11.2](#112-z-seed--julia-sets-and-critical-point-seeding).

```text
z*z + (-0.8 + 0.156*i)        # Douady rabbit           (seed: c)
z*z + (-0.70176 - 0.3842*i)   # spiral Julia            (seed: c)
z*z + (0.285 + 0.01*i)        # dendrite                (seed: c)
z*z*z + (-0.4 + 0.6*i)        # cubic Julia             (seed: c)
sin(z) + (1 + 0.2*i)          # transcendental Julia    (seed: c, Escape r: 4)
```

### 13.15 Convergence maps — Newton / Nova (`Converge-if` + `z₀ seed = c`)

Each needs **z₀ seed = `c`** and a **Converge-if** condition — see
[§11.3](#113-convergence-bailout--newton--magnet--nova).

```text
# Newton z³ − 1        Converge-if: abs(z - prev) < 0.0001
z - (z*z*z - 1)/(3*z*z)

# Newton z⁴ − 1        Converge-if: abs(z - prev) < 0.0001
z - (z*z*z*z - 1)/(4*z*z*z)

# Nova (relaxed Newton + c)   Converge-if: abs(z - prev) < 0.0001   seed: c
z - (z*z*z - 1)/(3*z*z) + c
```

### 13.16 Transcendental-singular (forum maps; small `Escape r` + seed)

Maps with a pole at the origin or a tiny dynamic range — the "renders blank by
default" family. Set **Escape r = 2** and **z₀ seed = c**; full walkthrough in
[§11.4](#114-reproducing-a-fractalforumsorg-map-local-smoke-test).

```text
log(sin(abs(1/z))) + c        # forum map, Mandelbrot form   (seed: c, Escape r: 2)
log(sin(abs(1/z))) - 1.55*i   # forum map, Julia c=(0,−1.55) (seed: c, Escape r: 2)
sin(1/z) + c                  # simpler pole map             (seed: c, Escape r: 2)
1/(z*z) + c                   # inverse-square               (seed: c, Escape r: 2)
```

---

## 14. Editor workflow

### Authoring loop

1. **Type → User Equation**, then **Params**.
2. Type in the **User Equation** tab (DSL or C#-style) — it renders live.
3. When you like it, switch to the **DSL** tab, click **Validate for CalcGen** to
   confirm it compiles and see which paths it supports, then **Compile & Load**
   for the fast/deep engine.
4. **Compile + Save** (or **Save…**) to store it in your library
   (`%APPDATA%\FracturingFog\userequations.json`).
5. **Promote to fractal list** to surface it in the *Type* dropdown next launch.

### Compile & Load vs Generate via CalcGen

| Path | When to use |
|---|---|
| **Compile & Load** | Iterative authoring; preview an idea with the full engine in one click. |
| **Generate via CalcGen** | Promote a keeper into a permanent C# calculator that builds into the app. |

Generated files land at `Calculators/Generated/{Name}Calculator.cs` (plus a
self-test); the next `dotnet build` compiles them in and the calculator appears
under **Type → "{Name} (Generated)"**.

### Compile feedback

The status line shows `✓ Compiled` (a green/neutral confirmation) or the parse
error with `line, col`. Live re-compile debounces about half a second after you
stop typing. Advisory messages render in yellow (`#FFCC00`), never red.

---

## 15. Command-line generation

```pwsh
dotnet build CalculatorGen\CalculatorGen.csproj -c Release
dotnet run --project CalculatorGen -c Release -- `
    --equation "z*z + c" `
    --name MandelbrotZ2 `
    --out Calculators\Generated `
    --selftest
```

Flags:

- `--equation "..."` — the RHS only (no `z_{n+1} =`).
- `--name <Name>` — base class name (`Calculator` suffix added automatically).
- `--out <dir>` — output directory (default: current directory).
- `--selftest` — also emit `{Name}CalculatorSelfTest.cs`.
- `--bailout <R>` — bailout radius (default 512; the generator squares it, so
  `--bailout 512` means "escape when `|z|² ≥ 262144`"). Raise it for smoother
  transcendental gradients; drop it to `2` for the classic Mandelbrot contract.
  This is the compile-time twin of the editor's **Escape r** control — the same
  radius that keeps transcendental maps from rendering solid
  ([§11.1](#111-escape-radius-escape-r)).

---

## 16. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Unknown identifier 'X'` | Typo. The allowed names are the operators, the functions in [§6](#6-language-reference--the-function-catalogue), the constants `pi e i`, and the variables `z c n/iter prev`. The lexer suggests the closest match. |
| `Unexpected '='` | Use `==` to compare. `=` only appears in a `let`/statement-block binding. |
| `Exponent … must be a non-negative integer ≤ 64` | CalcGen's `^` caps at 64 and takes integer exponents. Use `pow(x, y)` for anything else. |
| `Expected ',' …` after a function name | Multi-arg functions (`pow`, `atan2`, `min`, `max`, `mod`, `clamp`) need their arguments comma-separated. |
| `Equation is empty` | The editor is blank after stripping `return`/`;`. |
| **DE / normals** reads *off* when you wanted relief | A non-holomorphic construct (`conj`, `fold`, `re/im/abs/arg`, `min/max/mod/clamp`, per-component, `pow`, `prev`, `n`) disabled it — see [§9](#9-holomorphic-vs-non-holomorphic--why-it-matters). |
| Deep zoom drops to scalar / DD | A non-polynomial construct disabled perturbation; only `z^d + c`-shaped maps deep-zoom with acceleration. |
| A bounded map (`asin`, `atanh`) renders all-inside | That is correct — those orbits never escape. Add an escaping term (`z*z`). |
| Negative-power map is blank | Use `pow(z, -k)` (zero-guarded), not `1/z^k` (NaN at `z = 0`). |
| A **transcendental** map (`sin`/`log`/`exp∘…`) renders one solid colour | Escape radius too large for its small dynamic range. Set **Escape r = 2** — see [§11.1](#111-escape-radius-escape-r). |
| A `1/z`-style map is all one colour / all interior | The orbit starts at the pole `z₀ = 0` (`1/0`). Set **z₀ seed = c** to start at the pixel — see [§11.2](#112-z-seed--julia-sets-and-critical-point-seeding). |
| A Newton / root-finding map reads as solid interior | Root maps *converge*, they don't escape. Add a **Converge-if** condition and seed `z₀ = c` — see [§11.3](#113-convergence-bailout--newton--magnet--nova). |
| A Julia set renders as the Mandelbrot instead | Put the Julia constant in the equation (replace `+ c`) and set **z₀ seed = c** — see [§11.2](#112-z-seed--julia-sets-and-critical-point-seeding). |

---

## 17. Reference card

```text
Variables / state   z   c   n (or iter)   prev            (all both engines)
Orbit controls      Escape r (small for transcendental)   z0 seed (c = Julia)
                    Converge-if (Newton/Nova: abs(z-prev) < eps)
Constants           pi   e   i                     (i = imaginary unit (0,1))
Operators           +  -  *  /  ^          (^ = int 0..64 in CalcGen; any exp live)
Compare (in cond)   <  <=  >  >=  ==  !=
Boolean (live)      &&  ||  !
Conditional         CalcGen: if <cmp> then <expr> else <expr>
                    Live:    <cmp> ? <expr> : <expr>
Binding (live)      let <name> = <expr> in <expr>   |   statement blocks

Powers / roots      sqr(x)  pow(x,y)  sqrt(x)
Exp / log / trig    exp log sin cos tan sinh cosh tanh
Inverse trig        asin acos atan asinh acosh atanh          (normals ON)
Per-component       floor round ceil trunc fract sign  fold
Real extractors     re(x) im(x) abs(x) arg(x)  conj(x)
Real reducers       min(a,b) max(a,b) mod(x,p) clamp(x,lo,hi) atan2(y,x)
```

Reminders:

- `abs` = magnitude `|x|` in an expression, but `|x|²` inside an `if` condition.
- Prefer `pow()` over `^` whenever the exponent is not a small non-negative
  integer, so the equation behaves the same on both engines.
- Stay polynomial in `z` (+ `c`) to keep deep zoom + normals.

---

## 18. See also

- [Fractal Equation Design Guide (technical)](../Technical/FractalEquation-DesignGuide.md) — the math of each family, deep-zoom tables, and per-family modification cookbook.
- [CalculatorGen Authoring (technical)](../Technical/CalculatorGen-Authoring.md) — generator internals.
- [ColorGen User Guide](ColorGen-UserGuide.md) — the sibling DSL for algorithmic colour themes.
- [User Bulb 3D Guide](UserBulb-Guide.md) — the 3-D analogue (Vec3 / Quat raymarched DSL).
- [Colour Theme Editor Guide](ColorThemeEditor-Guide.md) — paint what your equation produces.
- [Relief 3D Guide](Relief3D-Guide.md) — turn a distance-estimate-capable equation into lit relief.
