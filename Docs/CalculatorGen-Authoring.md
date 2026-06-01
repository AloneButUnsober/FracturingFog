# CalculatorGen — Authoring guide

CalculatorGen is a small console tool that turns a user-supplied
fractal equation into a drop-in `IFractalCalculator` class file. The
goal: stop hand-writing one C# file per fractal type. Once the
generator ships, "promote this sandbox equation to a real calculator"
is a CLI invocation, not an LLM session.

The current generator (v0.3) emits a single C# file containing
**five fully-validated execution paths** for every input equation:

| Tier | Path           | Bound by                                            |
|-----:|----------------|-----------------------------------------------------|
|    1 | Scalar         | Reference path; `double`                            |
|    2 | AVX2 + FMA     | Four pixels per lane; `Vector256<double>`           |
|    4 | Perturbation   | Reference orbit + symbolic δ-update; deep-zoom safe |
|    5 | BLA            | Per-iter linear approx; auto-falls-back to Tier 4   |
|    6 | ILGPU GPU      | Auto-dispatched device kernel; CPU colour post-pass |

A `--selftest` flag emits a sibling validator that compares all five
paths on a fixed 64×64 grid and reports per-path drift. Verified
**0 mismatches** for `z² + c` and `z³ + c`.

A sixth "Tier 0" optimisation (imag-zero ComplexExpr flag) cuts roughly
30 % of the AVX2 prelude by skipping dead `Vector256<double>.Zero` adds
when subexpressions are provably real-valued (constants, real-only
arithmetic chains).

This document covers all the above plus the new `FractalType
.GeneratedMandelbrotZ2` dropdown entry that surfaces the generated
calculator in the Avalonia toolbar alongside the hand-tuned
`MandelbrotCalculator`.

**Developers extending the generator itself** — adding new operators,
new emitters, new precision tiers, new GPU backends — should read
[CalculatorGen-Architecture.md](CalculatorGen-Architecture.md) for the
internals: the AST pipeline, emitter contracts, template substitution,
the deep-zoom path order (perturbation / glitch detection / HP-direct
fallback), status labels, and debugging tips. This file is for
*authors* invoking the existing CLI; the architecture file is for
*modifiers* of CalculatorGen itself.

## Quick start

From the repo root:

```pwsh
dotnet build CalculatorGen\CalculatorGen.csproj -c Release
dotnet run --project CalculatorGen -c Release -- `
    --equation "z*z + c" `
    --name MandelbrotZ2 `
    --out Calculators\Generated `
    --selftest
```

This writes:

```
Calculators\Generated\MandelbrotZ2Calculator.cs
Calculators\Generated\MandelbrotZ2CalculatorSelfTest.cs
```

Rebuild the main project; the generated class is picked up by the
existing `Calculators\Generated\**` glob.

Verify the five paths agree:

```pwsh
.\bin\x64\Release\net10.0-windows\FracturingFog.exe --gentest MandelbrotZ2
Get-Content .\bin\x64\Release\net10.0-windows\gentest.out
```

Expected:

```
MandelbrotZ2CalculatorSelfTest — scalar ↔ AVX2 ↔ GPU agreement
  grid:           64×64 = 4096 pixels
  max iterations: 256
  mismatches:     0 (0.00%)
  mean |Δit|:     0.0000
  max  |Δit|:     0  (tolerance: 1)
  gpu in-set:     cpu=523  gpu=523  diff=0  → PASS
  perturbation:   cpu=523  pt=523  diff=0  → PASS
  bla:            cpu=523  bla=523  diff=0  → PASS
  qd ref orbit:   in-set=523  out=3573  → PASS
  result:         PASS
```

The self-test runs at Zoom = 1, so it does NOT exercise the deep-zoom
paths (DD/QD per-pixel HP-direct, per-pixel glitch detection). To
validate those, run the app and zoom past 1e12 — the status bar shows
the active path (`PT`, `QD-PT`, `DD-HP`, `QD-HP`). See
[CalculatorGen-Architecture.md §6](CalculatorGen-Architecture.md) for
the full label scheme.

Select the generated calc in the Avalonia toolbar:
**Type → "Mandelbrot Z² (Generated)"**.

## Equation grammar

| Construct        | Example                | Notes                            |
|------------------|------------------------|----------------------------------|
| Variable z       | `z`                    | The iterating complex value      |
| Variable c       | `c`                    | Pixel coordinate (complex)       |
| Real literal     | `2`, `0.5`, `1e-3`     | Treated as `(n, 0)` complex      |
| Addition         | `z + c`                | Complex                          |
| Subtraction      | `z - c`                | Complex                          |
| Multiplication   | `z * z`, `2 * c`       | Complex                          |
| Integer power    | `z^2`, `z^3`, `c^2`    | Exponent must be 0..16           |
| Parentheses      | `(z + c) * (z - c)`    | Standard precedence              |
| Unary minus      | `-z`, `-(z*z + c)`     | Complex negation                 |

**Not yet supported** (use Sandbox or hand-write):

- Division (`/`)
- Transcendentals (`sin`, `cos`, `exp`, `log`, …)
- Absolute-value folds (`|z.real|` → Burning Ship)
- Conditional branches
- Magnitude / argument decomposition

The polynomial restriction lets the symbolic differentiator and
perturbation expansion stay closed-form one-liners with no CAS
dependency.

## What the generator emits

One `.cs` file per equation containing:

1. **Class declaration** — sealed, implements `IFractalCalculator` and
   `IDisposable`. Holds GPU accelerator handles released on dispose.
2. **Header comment** — original equation, auto-derived `∂p/∂z`,
   `∂p/∂c`, the dz/dc per-iteration update, **and** the symbolic
   δ-expansion `p(Z+δ, C+ε) − p(Z, C)` used by the perturbation path.
3. **`Calculate` driver** — picks the active path in this order:
   1. GPU if `UseGpu = true` and ILGPU init succeeds.
   2. Perturbation if `UsePerturbation = true` and `Zoom ≥
      PerturbZoomThreshold`. BLA layers on top via `UseBla = true`.
   3. AVX2 + scalar tail (default).
4. **`IteratePixelScalar`** — reference implementation, one pixel,
   `double` arithmetic. Emits both the `z_{n+1}` body and the
   `dz_{n+1}/dc` body each iteration.
5. **`IteratePixelScalarRaw`** — iteration-count-only variant (no
   colour writeback). Used by the self-test.
6. **`IterateLaneAvx2`** — `Vector256<double>` over four pixels per
   lane. Complex multiply via `Fma.MultiplyAdd` /
   `MultiplyAddNegated`. Per-lane bailout via `BlendVariable` so
   escaped lanes freeze.
7. **`IterateLaneAvx2Raw`** — iteration-count-only AVX2 variant for
   the self-test.
8. **`Kernel`** — ILGPU device kernel; one work item per pixel. Uses
   the same scalar arithmetic body as path 4 (ILGPU accepts the
   `double` ops directly).
9. **`TryRenderGpu`** — lazy `Context` + `Accelerator` + kernel
   load, allocates an `ArrayView<RawPixel>`, dispatches the kernel,
   reads back, runs the **CPU colour post-pass** in `Parallel.For`
   so the full `IColorMap` (incl. distance-estimate / normal
   themes) is honoured.
10. **`TryRenderPerturbation`** — reference orbit at view centre
    (plain double precision); per-pixel δ-iteration loop using the
    symbolic Taylor expansion. Auto-aborts and returns false when
    the reference orbit itself escapes (which means the centre is
    outside the set).
11. **BLA inner branch** — when `UseBla = true`, computes per-iter
    `A_n = ∂p/∂z(Z_n, C)` and `B_n = ∂p/∂c(Z_n, C)` arrays during
    reference-orbit construction. In the per-pixel loop, attempts
    the linear step `δ_{n+1} = A·δ + B·ε` first; falls back to the
    full polynomial expansion when `|δ| ≥ BlaRelative · |Z_n|`.
12. **`ColorFor`** — surface-normal + distance-estimate computation
    feeding the 9-argument `IColorMap.Map` overload. Themes that
    ignore normals/DE inherit the 3-arg fallback through the
    default interface implementations.

## Reading the generated code

Take `z*z + c`. The scalar inner update (post imag-zero opt):

```csharp
double dr_new = (((zr + zr) * dr - (zi + zi) * di) + 1.0);
double di_new =  ((zr + zr) * di + (zi + zi) * dr);
double zr_new = ((zr * zr - zi * zi) + cr);
double zi_new = ((zr * zi + zi * zr) + ci);
```

`dr_new`/`di_new` are the symbolic dz/dc update `(z + z)·D + 1` (which
equals `2z·D + 1` = `∂p/∂z·D + ∂p/∂c` for `p = z² + c`).

The AVX2 path is the same identity in `Vector256<double>` lanes,
emitted as SSA-style temps (the JIT folds redundant copies):

```csharp
Vector256<double> d_re1 = Avx.Add(zr, zr);
Vector256<double> d_im2 = Avx.Add(zi, zi);
Vector256<double> d_re3 = Fma.MultiplyAddNegated(d_im2, di, Avx.Multiply(d_re1, dr));
Vector256<double> d_im4 = Fma.MultiplyAdd(d_re1, di, Avx.Multiply(d_im2, dr));
Vector256<double> d_re5 = Vector256.Create(1.0);
Vector256<double> d_re6 = Avx.Add(d_re3, d_re5);
Vector256<double> dr_new = d_re6;
Vector256<double> di_new = d_im4;
```

`Fma.MultiplyAddNegated(b, d, a*c)` = `−(b·d) + a·c` = real part of
`(a+bi)(c+di)`. Note `d_re5 = Create(1.0)` binds only Re (no Im
temp); the imag-zero optimisation recognises the `+ 1.0` literal as
provably real-valued and elides the dead zero-vector add that earlier
generator revisions emitted.

The perturbation comment line shows the symbolic δ-update produced
from `p(Z+δ, C+ε) − p(Z, C)`:

```text
//     δ_{n+1}  =  ε + (z + z)*δ + δ*δ
```

i.e. `ε + 2Zδ + δ²` (exact, not a Taylor truncation — for polynomials
the expansion terminates at the polynomial's total degree).

For `z³ + c`:

```text
//     δ_{n+1}  =  ε + ((z + z)*z + z*z)*δ + 0.5*(2*z + z + z + z + z)*δ*δ + δ*δ*δ
```

i.e. `ε + 3Z²δ + 3Zδ² + δ³`. (The unsimplified coefficients are
artefacts of how the multinomial-from-Taylor expansion is built;
emitting collected-form polynomials is on the cleanup list but the
JIT folds them either way.)

## Architecture

```
CalculatorGen/
├── Parser/
│   ├── AstNodes.cs           — ZRef, CRef, DRef, DeltaRef, EpsRef,
│   │                            RealConst, Neg, Add/Sub/Mul, Pow
│   ├── EquationLexer.cs      — string  → List<Token>
│   ├── EquationParser.cs     — List<Token> → AstNode (recursive descent)
│   ├── AstSimplifier.cs      — peephole: 0+x, 1*x, x^0/^1, const folding
│   ├── AstDifferentiator.cs  — symbolic ∂/∂z, ∂/∂c, dz/dc update builder
│   ├── AstSubstitute.cs      — z → expr, c → expr (used by perturbation)
│   ├── AstExpander.cs        — distributive expansion (reserved)
│   ├── AstPerturbation.cs    — Taylor-series builder for p(Z+δ, C+ε) − p(Z, C)
│   └── AstPrinter.cs         — AST → source string (for header comment)
├── Emitters/
│   ├── EmitterCommon.cs       — abstract EmitterBase; ComplexExpr w/ ImZero
│   ├── ScalarEmitter.cs       — emits double expressions
│   ├── Avx2Emitter.cs         — emits Vector256<double> w/ FMA, SSA temps
│   └── PerturbationEmitter.cs — emits scalar code w/ Z/C/δ/ε bindings
├── Templates/
│   ├── Calculator.template.cs — IFractalCalculator skeleton. Placeholders:
│   │                              {{CLASS_NAME}}, {{EQUATION_SOURCE}},
│   │                              {{DPDZ_TEXT}}, {{DPDC_TEXT}},
│   │                              {{DERIV_TEXT}}, {{TIMESTAMP}},
│   │                              {{SCALAR_Z_BODY}}, {{SCALAR_D_BODY}},
│   │                              {{AVX2_Z_BODY}},   {{AVX2_D_BODY}},
│   │                              {{PERTURB_DELTA_BODY}}, {{PERTURB_DELTA_TEXT}},
│   │                              {{BLA_A_BODY}},    {{BLA_B_BODY}}
│   └── SelfTest.template.cs   — scalar↔AVX2↔GPU↔perturb↔BLA grid validator
└── Program.cs                  — CLI; parses, diffs, simplifies, emits,
                                  writes file(s)
```

Both arithmetic emitters subclass `EmitterBase`, which provides the AST
walker and per-primitive virtual hooks (`Const`, `OpAdd`, `OpSub`,
`OpMul`, `OpNeg`). The `PerturbationEmitter` is a third subclass with
different variable bindings (`Zr/Zi` for reference orbit, `dr/di` for
δ, `er/ei` for ε). To add a new target (e.g. AVX-512), add an emitter
subclass and override those five primitives + provide an `EmitBody`
entry point.

## Imag-zero optimisation (Tier 0)

`ComplexExpr` carries an `ImZero` boolean. `Const(double)` sets it
true; ZRef/CRef/DRef/DeltaRef/EpsRef set it false (those values are
arbitrary complex at runtime). Add / Sub / Mul / Neg propagate:

| Op  | a.ImZero | b.ImZero | Result |
|-----|----------|----------|--------|
| Add | true     | true     | (a.Re + b.Re,  0,  ImZero=true)         |
| Add | true     | false    | (a.Re + b.Re,  b.Im,  ImZero=false)     |
| Add | false    | true     | (a.Re + b.Re,  a.Im,  ImZero=false)     |
| Add | false    | false    | (a.Re + b.Re,  a.Im + b.Im,  false)     |
| Mul | true     | true     | (a.Re · b.Re,  0,  ImZero=true)         |
| Mul | true     | false    | (a.Re · b.Re,  a.Re · b.Im,  false)     |
| Mul | false    | true     | (a.Re · b.Re,  a.Im · b.Re,  false)     |
| Mul | false    | false    | full complex multiply (4 muls / 2 adds) |

This collapses `(2 · zr - 0 · zi, 2 · zi + 0 · zr)` → `(2 · zr, 2 · zi)`
and saves the dead `Vector256<double>.Zero` SSA binds in the AVX2
prelude. Empirically: ~30 % shorter prelude on a derivative-tracking
calculator (cuts ~5 SSA temps per iteration on `z² + c`).

## Symbolic differentiation (Tier 1 / Phase B)

`AstDifferentiator.Diff(node, Var)` implements:

- `ZRef` → 1 if Var = Z, else 0
- `CRef` → 1 if Var = C, else 0
- `DRef`/`DeltaRef`/`EpsRef` → 0 (opaque)
- Constants → 0
- Sum rule, product rule, chain rule on Pow:
  `(u^n)' = n · u^(n-1) · u'`

Plus `BuildDerivativeUpdate(stepFn)` which builds
`dz_{n+1}/dc = (∂p/∂z) · D + (∂p/∂c)` where `D = DRef` is the
symbolic placeholder for the current dz/dc value. The emitter binds
DRef to `(dr, di)` registers at runtime; the result feeds the
9-arg `IColorMap.Map` for Inigo Quilez normals (`z · conj(dz/dc)`,
normalised) and Milnor/Hubbard distance estimate
(`½ · |z| · log|z| / |dz/dc|`).

## Perturbation expansion (Tier 4)

`AstPerturbation.BuildDeltaUpdate(stepFn)` produces the symbolic
δ-update via Taylor series:

```text
δ_{n+1}  =  p(Z + δ, C + ε)  −  p(Z, C)

         =  Σ_{k+m≥1}  (1 / k! m!) · (∂^{k+m} p / ∂z^k ∂c^m)|_{Z,C} · δ^k · ε^m
```

The expansion terminates at total polynomial degree (≤ 32 in the
current grammar), so this is **exact** — no Taylor truncation error.
Implementation iterates `(k, m)` pairs, differentiating the step
function k times w.r.t. z and m times w.r.t. c, simplifying after
each derivative (otherwise the intermediate tree grows exponentially
in the unsimplified form and the run hangs). Constant-zero partials
short-circuit the term.

The emitter (`PerturbationEmitter`) walks the resulting AST with
bindings:

| AST node  | Variable |
|-----------|----------|
| `ZRef`    | `Zr, Zi` — reference orbit iterate at step n |
| `CRef`    | `Cr, Ci` — view centre coordinate            |
| `DeltaRef`| `dr, di` — per-pixel δ                       |
| `EpsRef`  | `er, ei` — per-pixel ε = c - C               |

The runtime `TryRenderPerturbation`:

1. Computes the reference orbit at the view centre using plain
   double-precision iteration. Bails to AVX2 path (returns false)
   if the reference itself escapes.
2. For each pixel: walks the same number of iterations, but updates
   δ via the symbolic expansion. Stores final `(Zr+δr, Zi+δi)` for
   colour mapping.
3. Falls back to the AVX2 path if the reference orbit can't be
   built or `Zoom < PerturbZoomThreshold`.

## BLA (Tier 5)

Bilinear approximation: at each reference iteration n,

```text
δ_{n+1}  ≈  A_n · δ_n  +  B_n · ε

A_n = ∂p/∂z(Z_n, C)
B_n = ∂p/∂c(Z_n, C)
```

Both `A_n` and `B_n` are emitted by the *scalar* emitter from the
already-derived `∂p/∂z` and `∂p/∂c` ASTs (no new emitter needed).
They're computed alongside the reference orbit and cached into
`blaAr[]`, `blaAi[]`, `blaBr[]`, `blaBi[]`, plus a validity radius
`blaR[n] = BlaRelative · |Z_n|` (BlaRelative defaults to 1e-3).

The pixel loop attempts the linear step first when `UseBla = true`;
when `|δ|² ≥ blaR[n]²` it falls back to the full polynomial step.
For `z² + c` the linear step is `(A·δ + B·ε)` = `(2Z·δ + ε)`; the
quadratic δ² term is dropped under the validity threshold.

The current implementation is **iteration-filter BLA**: it speeds up
individual iterations when the linearisation holds, but does not
yet skip ranges of iterations via composed multi-level BLA tables.
A future enhancement (sketched in [Future Work](#future-work)) builds
power-of-two compositions
`A^{(K)}_n = ∏ A_{n+k}`, `B^{(K)}_n = Σ … · B_{n+k}` for skip-K steps.

## ILGPU (Tier 6)

The generated calculator emits a `Kernel` method:

```csharp
private static void Kernel(Index1D idx, ArrayView<RawPixel> output, GpuParams p)
{
    int x = idx % p.Width;
    int y = idx / p.Width;
    if (y >= p.Height) return;
    double cr = p.CenterX + (x - p.Width  * 0.5) * p.Scale;
    double ci = p.CenterY + (y - p.Height * 0.5) * p.Scale;
    double zr = 0.0, zi = 0.0;
    double dr = 0.0, di = 0.0;
    int it = 0;
    int maxIt = p.MaxIter;
    for (; it < maxIt; it++)
    {
        double r2 = zr * zr + zi * zi;
        if (r2 >= p.Bailout2) break;
        // {{SCALAR_D_BODY}} and {{SCALAR_Z_BODY}} substituted here
        ...
    }
    output[idx] = new RawPixel { Iter = it, Zr = zr, Zi = zi, Dr = dr, Di = di };
}
```

`RawPixel` is a value type with `int Iter; double Zr, Zi, Dr, Di;`
captured per pixel. The CPU post-pass reads the buffer back and runs
`ColorFor` in `Parallel.For` — so the full `IColorMap` is active even
in GPU mode (the kernel itself contains no colour code, which is what
lets it stay device-compatible).

Lifecycle:

- `TryInitGpu` lazily creates `Context.Create(b => b.Default())` and
  picks the preferred device via `GetPreferredDevice(preferCPU: false)`.
  Stores the kernel delegate.
- On init failure, sets `_gpuInitFailed = true` and stashes the
  exception message in `LastGpuError`. Subsequent calls short-circuit
  to the AVX2 fallback.
- `Dispose()` releases the accelerator and context.

The kernel is double-precision. On consumer NVIDIA cards (RTX series)
that's ~1/32 the FP32 throughput, so the GPU is most useful for very
high iteration counts or large output buffers where the iteration
loop dominates.

## Self-test (`--selftest`)

`--selftest` emits `<Name>SelfTest.cs` alongside the calculator. The
test runs four cross-checks on a fixed 64×64 grid at the standard
Mandelbrot view (centre −0.75, span 3.5, 256 max iterations):

1. **Scalar ↔ AVX2** per-pixel iteration-count drift.
   Tolerance: `maxAbsDiff ≤ 1` (one ULP shift in the bailout-radius
   compare).
2. **GPU smoke**: render full grid with `UseGpu = true`, count
   in-set pixels (those that hit `MaxIterations`), compare to the
   scalar count. Tolerance ≤ 4 boundary pixels. Init failure → SKIP
   (not a hard failure).
3. **Perturbation smoke**: same comparison with `UsePerturbation =
   true, PerturbZoomThreshold = 0`. Tolerance ≤ 4.
4. **BLA smoke**: `UseBla = true` on top of perturbation. Tolerance
   ≤ 8 (BLA may admit a few extra boundary pixels under linear
   approximation).

Hook into the main `Program.cs` so `FracturingFog.exe --gentest
MandelbrotZ2` runs it. Because the binary is a WinExe, output is
also mirrored to `gentest.out` next to the exe so a redirected shell
can read it.

## Toolbar wiring

The generated calculator is exposed via `FractalType
.GeneratedMandelbrotZ2`. Wiring touchpoints (all already in place
for the MandelbrotZ2 demo, repeat the pattern for further calcs):

| File                                          | Hook                              |
|-----------------------------------------------|-----------------------------------|
| `Abstractions/Models/Enums.cs`                | New `FractalType` enum value      |
| `Abstractions/ViewState/FractalViewState.cs`  | Default centre/zoom in `SnapToFractalDefault` |
| `Rendering/FractalRenderHost.cs`              | Field + ctor + colour map setter + Resize + `SelectAltCalculator` dispatch |
| `UI.Avalonia/ViewModels/MainViewModel.cs`     | Entry in `BuiltInFractalLabels`   |

The Avalonia dropdown shows the entry as **"Mandelbrot Z² (Generated)"**.
The Server allowlist (`Server/Guard/FractalTypeAllowlist.cs`) blocks
only user-authored types (UserEquation / Sandbox / UserBulb); generated
calcs are compiled into the binary and therefore safe to expose over
the network — no allowlist change needed.

## Extending the generator

### Add an arithmetic target (e.g. AVX-512)

1. Add `Emitters/Avx512Emitter.cs` mirroring `Avx2Emitter.cs` but with
   `Vector512<double>`, `Avx512F.Add`, `Avx512F.Multiply`,
   `Avx512F.FusedMultiplyAdd`. Eight pixels per lane.
2. Add `{{AVX512_BODY}}` placeholder in `Calculator.template.cs` and
   update the template to dispatch when `Avx512F.IsSupported`.
3. Wire it through `Program.cs` (one extra `.Replace` call).
4. Add an AVX-512 leg to the self-test template.

### Add an operator (e.g. abs-fold for Burning Ship)

1. Add `Abs(AstNode Operand)` to `AstNodes.cs`.
2. Teach `EquationLexer.cs` to emit `|` tokens (or a `abs(...)` call
   form).
3. Add `OpAbs` virtual to `EmitterBase`; override in scalar (`Math.Abs`)
   and AVX2 (`Avx.AndNot(signMask, vec)` with `signMask =
   Vector256.Create(-0.0)`).
4. Update `AstDifferentiator` (Abs is not analytically differentiable
   along the axis — return Sign(x) by convention, document the cusp).
5. Update `AstPerturbation` — Abs makes the iteration anti-holomorphic,
   so the Taylor expansion picks up axis-discontinuity terms. Burning
   Ship perturbation typically uses signed components rather than
   straight Abs to preserve the polynomial chain.

### Add a calculator-discovery hook

For multiple generated calcs, replace the per-calc enum value pattern
with a registry:

1. Add a single `FractalType.Generated` enum value.
2. Add a `GeneratedCalculatorRegistry.Register(name, factory)` static
   class. The generator emits a `[ModuleInitializer]`-decorated method
   into each calc file that self-registers on assembly load.
3. `FractalTypeEntry` carries the registered name when the entry is
   `Generated`. `FractalRenderHost.SelectAltCalculator` resolves
   via the registry.

This is sketched but not implemented — the current demo wires
`GeneratedMandelbrotZ2` directly because it's the only generated
calc shipped.

## Trade-offs and caveats

- **Polynomial subset is small but powerful.** Covers Mandelbrot
  `z²`, all Multibrot powers (`z^n`), Mandelbar via a `Conj` node
  (not yet wired), and any polynomial perturbation thereof.
- **No `^` on subexpressions.** `(z+c)^2` requires expanding manually
  to `(z+c) * (z+c)` in your equation, by design. Keeps the
  differentiator's life easy.
- **FMA rounding ≠ separate-mul-add rounding.** Scalar and AVX2
  outputs may differ by 1 ULP at the per-iteration level, producing
  ≤1 iteration count divergence near boundary pixels. This is the
  same trade-off `MandelbrotCalculator.cs` already makes; documented
  in the self-test tolerance.
- **Perturbation expansion is unsimplified.** The Taylor builder
  produces e.g. `0.5*(2*z + z + z + z + z)*δ*δ` for the
  `½·∂²p/∂z²·δ²` term of `z³+c` instead of `3z·δ²`. The JIT folds
  these but the source is noisier than necessary. Adding term-
  collection to the simplifier is a future cleanup.
- **BLA is iteration-filter only.** No multi-level skip table yet —
  the linear step doesn't yet skip iterations, only avoids the
  polynomial expansion within a single iter. The composition algebra
  is sketched in *Future Work* below.
- **GPU is double-precision.** Best for high-iter / large-buffer
  renders where the iteration loop dominates. Consumer NVIDIA cards
  (RTX) run double at ~1/32 of single-precision throughput; data-
  centre cards (A100/H100) run at full speed.
- **CPU colour pass on GPU path.** Trade-off: keeps the GPU kernel
  device-portable (no IColorMap virtual call on device) at the cost
  of a 36 MB readback for a 1080p render. Acceptable on PCIe Gen3+.
- **One accelerator per generated calc.** If you ship several
  generated calcs each instantiates its own `Context` /
  `Accelerator`. A shared static pool is a future refactor.

## Future work

- **Multi-level BLA composition.** Build `A^{(K)}_n`, `B^{(K)}_n`
  for K = 2, 4, 8, … log2(maxIt). Validity radius shrinks with K.
  Per-pixel: walk the largest level whose radius covers the current
  δ. Real Yang-style skip BLA — expected 10-100× speedup for deep
  zooms.
- **Series Approximation (SA).** Symbolic Taylor expansion of δ as a
  power series in ε, truncated to order 8-16. The AST already lets
  us extract the per-order coefficients; the runtime caches them
  per skip block and applies the series for any pixel whose ε falls
  in the validity disk.
- **Double-double SIMD path.** `Vector256<double>` lanes that hold
  hi+lo of a DD number; complex multiply becomes DD-multiply + DD-
  add chains (~40 flops per lane per iter). Unlocks zoom past 1e15
  without perturbation.
- **Burning Ship / Tricorn via `Abs` and `Conj`.** Add the AST nodes
  + emitter hooks; the perturbation expansion needs an extension
  rule for the anti-holomorphic case.
- **Registry-driven UI.** Drop the per-calc enum values in favour of
  a single `FractalType.Generated` + name-based registry as sketched
  above.

## Reference reading

- Inigo Quilez — *Rendering the Mandelbrot Set*
  https://iquilezles.org/articles/mandelbrot/
  (Normal-vector + distance-estimator formulas used by the
  template's `ColorFor` helper.)
- Claude Heiland-Allen — *Perturbation and BLA for the Mandelbrot
  set*. The polynomial-AST approach for Tier 4/5 follows his
  derivation.
- Zhuoran Yang — *Multi-level BLA tables*. The composition algebra
  that the iteration-skip future-work entry references.
- ILGPU documentation — `Context.Create`, `Accelerator`,
  `AutoGroupedStreamKernel`, the JIT path used by the GPU emitter.
