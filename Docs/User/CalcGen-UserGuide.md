# CalcGen — User Guide

CalcGen ("CalculatorGen") turns a single line of fractal math into a
fully-validated calculator with five execution paths (scalar, AVX2,
perturbation, BLA, ILGPU GPU). The User Equation editor exposes two
buttons:

| Button                    | What it does                                                   |
|---------------------------|----------------------------------------------------------------|
| **Compile & Load**        | Roslyn-compiles the equation and swaps it onto the live render |
| **Generate via CalcGen**  | Writes `Calculators/Generated/{Name}Calculator.cs` (rebuild)   |

This guide covers the equation grammar, every supported operator and
function, gating rules, dozens of ready-to-paste examples, and the
in-app workflow. It is for **authors**. The deep-architecture doc lives
at [CalculatorGen-Architecture.md](../Technical/CalculatorGen-Architecture.md).

> Companion pages: [User Index](_Index.md) · [User Bulb 3D Guide](UserBulb-Guide.md) · [Fractal Equation Design Guide (Technical)](../Technical/FractalEquation-DesignGuide.md)

![PLACEHOLDER — User Equation editor with the Mandelbrot recipe and live preview](../Images/_placeholders/placeholder.svg)

---

## A friendly tour

A *fractal equation* in Fracturing Fog is the **single line of math the calculator runs once per
pixel, per iteration**. The classic Mandelbrot equation reads:

$$
z_{n+1} = z_n^2 + c
$$

…where `z` starts at zero and `c` is the pixel coordinate on the complex plane. Repeat that line
hundreds or thousands of times for every pixel, ask whether `|z|` ever grows past `2`, and you
have produced a Mandelbrot picture.

The User Equation editor lets you change that single line to *anything you want*.

### Worked example — "Make a Tricorn"

The Tricorn fractal replaces `z²` with the conjugate of `z`, squared:

$$
z_{n+1} = \overline{z_n}^{\,2} + c
$$

1. Toolbar **Type** = **User Equation**.
2. Floating Menu → **Params** → editor opens.
3. Paste:

   ```csharp
   var zb = Complex.Conjugate(z);
   return zb * zb + c;
   ```

4. **Compile & Load**. The render switches in milliseconds.

You have a Tricorn. Save it under the name `Tricorn` so it appears in your future *Type* dropdown
right alongside the built-ins.

### Worked example — "Mandelbrot, but cubic"

$$
z_{n+1} = z_n^3 + c
$$

```csharp
return z * z * z + c;
```

That is a *Multibrot* of order 3 — two cardioids meeting at the origin instead of one. Six lobes
around the boundary instead of two.

![Cubic Multibrot (z³ + c) — two-fold symmetry instead of the Mandelbrot cardioid.](../Images/examples/multibrot-cubic.png)

### Worked example — "Mix two recipes"

$$
z_{n+1} = z_n^2 + c + \tfrac{1}{4}\overline{z_n}
$$

```csharp
return z * z + c + 0.25 * Complex.Conjugate(z);
```

You have invented a hybrid fractal — half Mandelbrot, quarter Tricorn. Names are cheap; call it
`Mandri-corn`, save it, and it ships in the *Type* dropdown next launch.

---

## 1. Quick start

1. Switch fractal type to **User Equation** (toolbar).
2. **Params** → opens the User Equation editor.
3. Type an equation, click **Compile & Load**. The render updates.
4. Optional: **Save…** to keep the equation in your library
   (`%APPDATA%\FracturingFog\userequations.json`).
5. Optional: **Generate via CalcGen** to write a permanent C# file
   under `Calculators/Generated/` for the next build.

The editor accepts either C# style (`return z*z + c;` — the
preprocessor strips the `return ` / `;`) or bare CalcGen DSL
(`z*z + c`).

---

## 2. Grammar at a glance

```
z_{n+1} = <expression>
```

| Construct        | Example                  | Notes                            |
|------------------|--------------------------|----------------------------------|
| Variable z       | `z`                      | Current iterate (complex)        |
| Variable c       | `c`                      | Pixel coordinate (complex)       |
| Real literal     | `2`, `0.5`, `1.5e-3`     | Treated as `(n, 0)` complex      |
| Imaginary unit   | `i`                      | Literal `(0, 1)`. Complex constant — holomorphic. Differentiator returns 0, chain rule still hands back `i` via Mul (`d(i·z)/dz = i`). DE / perturbation / BLA / SA all stay on. |
| Addition         | `z + c`                  | Complex                          |
| Subtraction      | `z - c`                  | Complex                          |
| Multiplication   | `z*z`, `2*c`             | Complex                          |
| Division         | `z / (z + 1)`            | Complex; gates BLA / SA off      |
| Integer power    | `z^2`, `z^3`             | Exponent 0..64 (`z*z…` for higher) |
| Parentheses      | `(z + c) * (z - c)`      | Standard precedence              |
| Unary minus      | `-z`, `-(z*z + c)`       | Complex negation                 |
| Conjugate        | `conj(z)`                | (zr, -zi) — anti-holomorphic     |
| Component fold   | `fold(z)`                | (\|zr\|, \|zi\|) — Burning Ship  |
| Square shortcut  | `sqr(z)`                 | Same as z*z                      |
| Real / imag      | `re(z)`, `im(z)`         | Real scalar lifted as (n, 0)     |
| Magnitude        | `abs(z)`                 | \|z\| as (n, 0)                    |
| Transcendental   | `sin(z) cos(z) tan(z) exp(z) log(z)` | Holomorphic              |
| Hyperbolic       | `sinh(z) cosh(z) tanh(z)`| Desugared via exp; holomorphic   |
| Square root      | `sqrt(z)`                | Desugared as `exp(0.5*log(z))`   |
| Argument         | `arg(z)`                 | Real angle in (-π, π], lifted to (arg, 0). Non-holomorphic — disables DE / perturbation / BLA / SA. |
| Atan2            | `atan2(y, x)`            | Binary; same gating as `arg`. Per-lane scalar fallback on AVX2 (4× `Math.Atan2` per body), full vector on Scalar/DD/QD. |
| Min / Max        | `min(a, b)`, `max(a, b)` | Real-valued, lifted to (result, 0). Imag parts discarded. Vectorised on AVX2 via Vector256.Min/Max. Same gating as `arg`. |
| Modulo           | `mod(a, b)`              | Real-valued C# `%` on the real parts, lifted. Per-lane scalar on AVX2. Same gating as `arg`. |
| Constants        | `pi`, `e`                | Real literals (Math.PI / Math.E) |
| Previous iter    | `prev`                   | z_{n-1} — Phoenix coupling       |
| Iter index       | `iter` (or `n`)          | Real scalar; current index       |
| Conditional      | `if cond then a else b`  | Cond compares real scalars       |
| Comparisons      | `< <= > >= == !=`        | Yield 1 / 0 inside `if`          |

Whitespace separates tokens; the lexer tolerates either single- or
multi-line input.

---

## 3. Five execution paths and what gates them

The generator emits **every** path; runtime picks the best for the
view. Some constructs disable specific paths automatically:

| Construct used        | Scalar | AVX2 | Perturbation | BLA / SA | DE (normals) | DD/QD HpDirect | GPU |
|-----------------------|:------:|:----:|:------------:|:--------:|:------------:|:--------------:|:---:|
| Polynomial in z (+ c) | ✓      | ✓    | ✓            | ✓        | ✓            | ✓              | ✓   |
| Division              | ✓      | ✓    | ✗            | ✗        | ✓            | ✗              | ✓   |
| `conj` / `fold`       | ✓      | ✓    | ✗            | ✗        | ✗            | ✗              | ✓   |
| Transcendentals       | ✓      | ✓    | ✗            | ✗        | ✓            | ✓ (degraded)   | ✓   |
| `if`/`else` branches  | ✓      | ✓    | ✗            | ✗        | ✓ per side   | ✗              | ✓   |
| `prev`                | ✓      | ✓    | ✗            | ✗        | ✗            | ✓              | ✓   |
| `iter` / `n`          | ✓      | ✓    | ✗            | ✓        | ✓            | ✓              | ✓   |

The status bar shows the active path: `SP / AVX2 / PT / DD-HP / QD-PT`,
etc. Hot zoom (~1e13+) without perturbation drops to DD/QD HpDirect
automatically.

---

## 4. Examples

### 4.1 The Mandelbrot family

#### Classic
```
z*z + c
```

#### Multibrot — fixed integer powers
```
z^3 + c
z^4 + c
z^5 + c
z^6 + c
```

#### Cubic with two terms
```
z^3 - z + c
```

#### Quadratic with linear coupling
```
z*z + 0.5*z + c
```

#### Mixed-degree polynomial
```
(z^4 + z^2)/2 + c
```

#### Rational — Mandelbrot-on-a-shell
```
(z*z - 1) / (z + 1) + c
```
(Division turns perturbation/BLA off; deep zoom still works via DD/QD
HpDirect.)

### 4.2 Julia-like (constant c via region)

Set `c` constant through the User Equation rotation+region; equation
itself is the same as Mandelbrot. The Julia behaviour comes from how
the host iterates with c held fixed.

### 4.3 Burning Ship and Tricorn

#### Burning Ship — components folded each step
```
fold(z)*fold(z) + c
```

#### Tricorn — conjugate each step
```
conj(z)*conj(z) + c
```

#### Burning-Tricorn hybrid
```
conj(fold(z))^2 + c
```

### 4.4 Phoenix family

`prev` carries `z_{n-1}` so two-step recurrences become first-class.

#### Classic Phoenix
```
z*z + c + 0.5*prev
```

#### Phoenix with negative feedback
```
z*z - 0.5*prev + c
```

#### Cubic Phoenix
```
z^3 + 0.4*prev + c
```

#### Two-tap Phoenix
```
z*z + 0.3*prev - 0.1*prev*prev + c
```

### 4.5 Transcendental and trigonometric

The transcendental wedge — sin/cos/exp/log — disables perturbation/BLA
but keeps distance estimate (the chain rule still has a closed form).

#### Exponential
```
exp(z) + c
```

#### Sinusoidal
```
sin(z) + c
```

#### Logarithmic shell
```
log(z*z) + c
```

#### Mixed trig
```
sin(z)*cos(c) + c
```

#### Damped oscillator
```
0.5*z + sin(z) + c
```

#### Tangent
```
tan(z) + c
```

#### Hyperbolic family (desugared via exp internally)
```
sinh(z) + c
cosh(z) - 0.5*c
tanh(z*z) + c
```

#### Square root (principal branch)
```
sqrt(z) + c
sqrt(z*z - 1) + c
```

#### Constants pi / e
```
sin(pi*z) + c
e*z*z + c
```

### 4.10 Argument-driven (arg / atan2)

`arg(z)` is the principal angle of `z` in (-π, π], lifted back to complex
as `(arg, 0)`. Non-holomorphic — same gating as `conj`: distance estimate,
perturbation, BLA, and SA all turn off when the equation contains `arg`.

`arg` is also accepted as a real-scalar condition term inside `if`:
`if arg(z) > 0 then z*z + c else z*z - c` branches by orbit phase. The
cond grammar position behaves like `re(...)` / `im(...)` / `abs(...)` —
the arg's sub-expression can be anything (z, c, or a composite like
`arg(z*z + c)`). Conditions don't feed the differentiator chain so
piecewise-by-arg keeps the distance estimate valid inside each branch
(boundary locus where the cond flips is measure-zero).

#### Spiral by angle
```
z*z + 0.1*arg(z) + c
```

#### Branch by quadrant via atan2
```
z*z + 0.05*atan2(z, c) + c
```

(`atan2(y, x)` is real-valued. On AVX2 the binary form falls back to
per-lane scalar `Math.Atan2` — kept 4-wide so the surrounding pipeline
stays vectorised, at the cost of 4× scalar atan2 calls per body. Scalar
and DD/QD paths handle the full form directly. `arg(z)` is the unary
shortcut when you only need atan2 of a complex value's components.)

### 4.12 Imaginary unit (`i`)

`i` is the imaginary unit literal — `(0, 1)` as a complex constant. It
behaves like any other complex constant in the grammar: holomorphic, so
the distance estimate, perturbation, BLA, and SA paths all stay enabled.
Use it to inject a complex coefficient without juggling `re()` / `im()`
decomposition.

The C# editor's `Complex.ImaginaryOne` and `new Complex(a, b)` both
translate to the DSL form automatically — there is no longer an "i has
no DSL representation" diagnostic.

#### Multiply by i (90° rotation)
```
i*z + c
```

#### Complex coefficient on the quadratic term
```
i*z*z + c
```

#### Mixed real + imaginary coefficients
```
0.5*z*z + 0.3*i*z + c
```

#### Decompose a hand-written complex constant
`new Complex(0.4, -0.2)` in the C# editor becomes
```
((0.4) + (-0.2)*i)
```
in the DSL — same semantics, with both halves explicit.

### 4.11 Real binary ops (min / max / mod)

`min(a, b)`, `max(a, b)`, `mod(a, b)` all act on the real parts of their
operands (imag is dropped) and lift back to complex as `(result, 0)`.
Non-holomorphic — same gating as `arg` / `atan2`. `min` / `max` use
`Vector256.Min` / `Vector256.Max` intrinsics on the AVX2 path so they
stay vectorised; `mod` falls back to per-lane scalar `%`.

#### Clamp by min/max
```
min(z*z, max(z, -1.0)) + c
```

#### Periodic wrap via mod
```
z*z + mod(z, 1.0) + c
```

#### Hybrid step
```
max(z*z, sqr(z)) + c
```

### 4.6 Newton-like patterns

(Newton fractals proper use the dedicated NewtonCalculator, but
CalcGen can do Newton-shaped iterations for novelty.)

```
z - (z^3 - 1)/(3*z*z)
```

### 4.7 Conditional / piecewise

`if` evaluates a real-scalar predicate; both branches must produce
complex values.

#### Branch by magnitude
```
if abs(z) < 1 then z*z + c else z*z - c
```

#### Branch by component sign
```
if re(z) > 0 then z*z + c else conj(z)*conj(z) + c
```

#### Quadrant switch
```
if re(z)*im(z) > 0 then z^3 + c else z^2 + c
```

### 4.8 Iteration-aware (`iter` / `n`)

`iter` is the current iteration index as a real scalar. Use sparingly —
the resulting fractal can be unusual (it loses scale invariance).

```
z*z + c + 0.001*iter
sin(z + 0.01*iter) + c
```

### 4.9 Time-evolved hybrids

#### Smooth crossfade between two systems via iter
```
(1 - iter*0.001)*(z*z) + iter*0.001*(z^3) + c
```

#### Branch every fourth iteration
```
if (iter % 4) < 2 then z*z + c else z*z - c
```
(`%` on the integer iter is interpreted as real-scalar mod.)

---

## 5. The editor workflow

### Saved equations

* **Save…** prompts for a name; the entry persists to
  `%APPDATA%\FracturingFog\userequations.json`.
* **Promote to fractal list** — surfaces the saved equation as a
  first-class entry in the fractal-type dropdown
  (`RegisteredFractalCatalog`).
* **Delete** — only the selected named entry; the current editor source
  is untouched.

### Rotation

The User Equation host post-rotates the iteration plane by **Rotation°**
before sampling. `+90 / -90 / Reset` provide single-click rotations.
This is purely a display rotation; the equation itself is unchanged.

### Compile feedback

The status line under the editor shows either `✓ Compiled` (green) or
the parse / Roslyn error (red) with `line, col`. Live re-compile
debounces 500 ms after typing stops.

### Compile & Load vs Generate

| Path                    | When to use                                        |
|-------------------------|----------------------------------------------------|
| **Compile & Load**      | Iterative authoring; preview an idea in one click  |
| **Generate via CalcGen**| Promote a keeper into a permanent calculator file  |

Generated `.cs` lands at
`Calculators/Generated/{Name}Calculator.cs` plus a sibling self-test.
The next `dotnet build` picks it up; the new calculator shows under
**Type → "{Name} (Generated)"**.

---

## 6. CLI

```pwsh
dotnet build CalculatorGen\CalculatorGen.csproj -c Release
dotnet run --project CalculatorGen -c Release -- `
    --equation "z*z + c" `
    --name MandelbrotZ2 `
    --out Calculators\Generated `
    --selftest
```

Flags:

* `--equation "..."` — required, the RHS only (no `z_{n+1} =`).
* `--name <Name>` — required, base class name (`"Calculator"` suffix
  added automatically).
* `--out <dir>` — output directory (default cwd).
* `--selftest` — emit `{Name}CalculatorSelfTest.cs`.
* `--bailout <R>` — bailout radius; default 512. The generator squares it, so
  `--bailout 512` means "escape when `|z|² ≥ 262144`". Raise it for smooth
  transcendental gradients; lower it (e.g. `--bailout 2`) to reproduce the
  classic Mandelbrot escape contract.

---

## Cookbook — end-to-end recipes

Short, complete walk-throughs that string the pieces above into a finished
result. Each one starts from a blank editor and ends with something on disk.

### Recipe 1 — Preview an idea, then keep it

Goal: audition a new equation and, if you like it, make it a permanent entry in
the *Type* dropdown.

1. Toolbar **Type** → **User Equation**, then **Params** to open the editor.
2. Type the equation and click **Compile & Load**. The render swaps in under a
   second; the status line shows `✓ Compiled` or a red `line, col` error.
3. Nudge coefficients and re-load until you like the shape. (Live re-compile
   also fires ~500 ms after you stop typing.)
4. **Save…** and give it a name — it persists to
   `%APPDATA%\FracturingFog\userequations.json`.
5. **Promote to fractal list** so it appears in the *Type* dropdown next launch,
   alongside the built-ins.

> [!TIP]
> Steps 1–4 never touch disk beyond the JSON library, so this loop is safe to
> repeat as fast as you can type. Save the *why* in the name — `phoenix-0p57`
> beats `test3`.

### Recipe 2 — Freeze a keeper into a compiled calculator

Goal: turn a saved equation into a first-class C# calculator that builds into the
app (faster than the Roslyn hot-load path, and it survives a clean rebuild).

From the editor, click **Generate via CalcGen**, or from a shell:

```pwsh
dotnet build CalculatorGen\CalculatorGen.csproj -c Release
dotnet run --project CalculatorGen -c Release -- `
    --equation "z*z*z - 0.5*z + c" `
    --name TwistedCubic `
    --out Calculators\Generated `
    --selftest
```

This writes `Calculators/Generated/TwistedCubicCalculator.cs` plus a sibling
self-test. The next `dotnet build` of the app compiles it in, and it shows up
under **Type → "TwistedCubic (Generated)"**.

### Recipe 3 — Render a generated family headlessly

Goal: batch-render an image or zoom video with no UI.

Headless `--batch` addresses fractals by their **built-in** `FractalType` name,
so the generated families that ship pre-wired are the ones you can drive from the
CLI: `GeneratedMandelbrotZ2`, `GeneratedMandelbrotZ3`, `GeneratedMandelbrotZ4`,
`GeneratedMandelbrotZ5`, `GeneratedTricorn`, and `GeneratedBurningShip`.

```pwsh
FracturingFog.exe --batch --mode image ^
    --fractal GeneratedMandelbrotZ3 ^
    --x -0.5 --y 0 --zoom 1.0 ^
    --theme HSV --width 3840 --height 2160 ^
    --out C:\out\multibrot-cubic.png
```

The watermark is composited **on by default** for parity with the interactive
**Save** button; add `--no-watermark` for a clean plate. See
[Capture-Guide → Batch CLI](Capture-Guide.md#8-batch-cli).

> [!IMPORTANT]
> A calculator you author with a *custom* name (Recipe 1 / Recipe 2) is not
> reachable through `--batch --fractal` — that switch only accepts the fixed
> `FractalType` names above. To batch-render your own equation, either express it
> as one of the generated families, or render it interactively and use the
> **Image** / **Video** buttons.

### Recipe 4 — Slow the escape for smoother transcendental gradients

Goal: an `exp` / `sin` equation whose boundary bands look coarse.

Transcendentals blow up fast, so the default bailout clips the gradient. Generate
the calculator with a larger bailout radius so orbits are allowed to travel
further before they count as escaped:

```pwsh
dotnet run --project CalculatorGen -c Release -- `
    --equation "sin(z) + c" `
    --name SineField `
    --bailout 4096 `
    --out Calculators\Generated --selftest
```

Pair the wider bailout with the app's smooth-iter colouring (automatic) for a
continuous ramp instead of visible iso-iter steps.

### Recipe 5 — Keep an equation deep-zoomable

Goal: a custom fractal you can still dive into past 10¹⁵.

Stay **polynomial in `z` (+ `c`)**. Perturbation, BLA, and Series Approximation
all stay on for `z^d + c` and its coupled/Multibrot/Phoenix-free variants; the
moment you add `conj`, `fold`, `/`, a transcendental, `if`, `prev`, or `iter`
you drop to scalar / DD-QD past the perturbation threshold (see the gating table
in [§3](#3-five-execution-paths-and-what-gates-them)). The design-side reasoning
and a per-family deep-zoom table live in
[Fractal Equation Design Guide → §13](../Technical/FractalEquation-DesignGuide.md#13-equation-modification-cookbook).

---

## 7. Troubleshooting

| Symptom                                   | Likely cause / fix                                  |
|-------------------------------------------|------------------------------------------------------|
| `Unknown identifier 'X'`                  | Typo. Allowed: z, c, conj, fold, sqr, sin, cos, tan, sinh, cosh, tanh, sqrt, exp, log, arg, atan2, min, max, mod, pi, e, i, if/then/else, re, im, abs, prev, iter/n. |
| `Unexpected character …`                  | Stray punctuation. `=` alone is not allowed; use `==`.|
| `Exponent … must be a non-negative integer ≤ 64` | Power `^` caps at 64. Use `z*z*z…` or factor for higher. |
| `Equation is empty`                       | Editor text is blank after preprocessor strip.       |
| Deep-zoom drops to scalar                 | Construct disables perturbation; see table §3.       |
| Black output past 1e13                    | DD/QD HpDirect off (Conj/Fold/Prev gated). Use polynomial form. |
| Hot-load error after edit                 | Roslyn diagnostic with line/col; fix and re-load.    |

---

## 8. Reference card

```
Operators       + - * / ^   (^ = integer power 0..64)
Comparisons     < <= > >= == !=
Conditional     if <cmp> then <expr> else <expr>
Unary           -expr  conj(...) fold(...) abs(...) sqr(...)
Lifts           re(z), im(z), abs(z)          (real scalar → (n, 0))
Cond terms      re(...), im(...), abs(...), arg(...)   (inside if cmp)
Transcendentals sin cos tan sinh cosh tanh exp log sqrt
Argument        arg(x)            atan2(y, x)
Real binary     min(a, b)  max(a, b)  mod(a, b)
Constants       pi  e  i              (i = imaginary unit, (0, 1))
State           z   c   prev   iter (or n)
```

Note: `tan / sinh / cosh / tanh / sqrt` are desugared at parse time
(`tan→sin/cos`, hyperbolics via `exp`, `sqrt→exp(0.5*log)`), so they
inherit the same gating as `sin/cos/exp/log` — perturbation / BLA off,
distance estimate preserved.

Use this guide as the source of truth for the User Equation editor.
The Sandbox calculator accepts a restricted DSL with no .NET BCL access
(safe to share) but loses every execution path except the scalar
interpreter — prefer CalcGen for anything you plan to keep.

---

## 9. See Also

- [CalculatorGen-Architecture.md](CalculatorGen-Architecture.md) — generator internals (AST, simplifier, Taylor expander, BLA validity)
- [Avalonia-UserGuide.md](Avalonia-UserGuide.md) — UI walkthrough including the User Equation editor
- [ColorGen-UserGuide.md](ColorGen-UserGuide.md) — sibling DSL for algorithmic color themes
- [UserBulb-Guide.md](UserBulb-Guide.md) — 3D analogue (Vec3 / Quat Roslyn-compiled raymarched calculator)
- [Architecture-Overview.md](Architecture-Overview.md) — where the CalculatorGen project sits in the solution
