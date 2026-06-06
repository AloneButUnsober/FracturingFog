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
at [`CalculatorGen-Architecture.md`](CalculatorGen-Architecture.md).

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
| Addition         | `z + c`                  | Complex                          |
| Subtraction      | `z - c`                  | Complex                          |
| Multiplication   | `z*z`, `2*c`             | Complex                          |
| Division         | `z / (z + 1)`            | Complex; gates BLA / SA off      |
| Integer power    | `z^2`, `z^3`             | Exponent 0..16                   |
| Parentheses      | `(z + c) * (z - c)`      | Standard precedence              |
| Unary minus      | `-z`, `-(z*z + c)`       | Complex negation                 |
| Conjugate        | `conj(z)`                | (zr, -zi) — anti-holomorphic     |
| Component fold   | `fold(z)`                | (|zr|, |zi|) — Burning Ship      |
| Square shortcut  | `sqr(z)`                 | Same as z*z                      |
| Real / imag      | `re(z)`, `im(z)`         | Real scalar lifted as (n, 0)     |
| Magnitude        | `abs(z)`                 | \|z\| as (n, 0)                    |
| Transcendental   | `sin(z) cos(z) exp(z) log(z)` | Holomorphic                |
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
* `--bailout <R>` — bailout radius; default 512.

---

## 7. Troubleshooting

| Symptom                                   | Likely cause / fix                                  |
|-------------------------------------------|------------------------------------------------------|
| `Unknown identifier 'X'`                  | Typo. Allowed: z, c, conj, fold, sqr, sin, cos, exp, log, if/then/else, re, im, abs, prev, iter/n. |
| `Unexpected character …`                  | Stray punctuation. `=` alone is not allowed; use `==`.|
| `Exponent must be 0..16`                  | Use `z*z*z…` or break into factored form.            |
| `Equation is empty`                       | Editor text is blank after preprocessor strip.       |
| Deep-zoom drops to scalar                 | Construct disables perturbation; see table §3.       |
| Black output past 1e13                    | DD/QD HpDirect off (Conj/Fold/Prev gated). Use polynomial form. |
| Hot-load error after edit                 | Roslyn diagnostic with line/col; fix and re-load.    |

---

## 8. Reference card

```
Operators       + - * / ^   (^ = integer power 0..16)
Comparisons     < <= > >= == !=
Conditional     if <cmp> then <expr> else <expr>
Unary           -expr  conj(...) fold(...) abs(...) sqr(...)
Lifts           re(z), im(z), abs(z)          (real scalar → (n, 0))
Transcendentals sin cos exp log
State           z   c   prev   iter (or n)
```

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
