# CalculatorGen — Architecture & Internals

Developer-facing reference. Reading order: this file → the existing
[CalculatorGen-Authoring.md](CalculatorGen-Authoring.md) (user-facing
quickstart + grammar). This file covers how the generator is structured,
where the seams are, and what to touch when you need to add a new
emitter, a new execution path, or a new fractal type.

If you are *using* CalculatorGen to generate a calculator, the
authoring doc is the right place. If you are *modifying* CalculatorGen
itself — adding ops, new SIMD widths, new precision tiers, a new GPU
backend, glitch heuristics, anything that changes what the generated
code looks like — this is the right place.

---

## 1. End-to-end flow

```
    CLI invocation
         │
         ▼
    Equation string  ──►  Parser  ──►  AstNode root
                                         │
                                         ▼
            ┌────────────────────  Differentiator  ─────────────────┐
            │                                                       │
   AstNode dz/dc-update                                AstNode ∂p/∂z, ∂p/∂c
            │                                                       │
            └────────────────────┬──────────────────────────────────┘
                                 ▼
                         Perturbation builder
                                 │
                                 ▼
                          AstNode δ-update
                                 │
            ┌────────────────────┼────────────────────┐
            ▼                    ▼                    ▼
        Emitters            Emitters             Emitters
   (scalar, AVX2,        (perturbation,         (DD-direct,
    Q D, derivative,        DD-direct,           QD-direct)
    BLA-A, BLA-B)          QD-direct)
            │                    │                    │
            └──────────┬─────────┴──────────┬─────────┘
                       ▼                    ▼
              Body strings            Body strings
                       │                    │
                       └──────────┬─────────┘
                                  ▼
                         Template substitution
                                  │
                                  ▼
                            Output .cs file
```

Each box is one source file under `CalculatorGen/`:

| Stage | File |
|-------|------|
| Lexer | `Parser/EquationLexer.cs` |
| Parser | `Parser/EquationParser.cs` |
| AST node types | `Parser/AstNodes.cs` |
| Differentiator | `Parser/AstDifferentiator.cs` |
| Simplifier | `Parser/AstSimplifier.cs` |
| Substitution | `Parser/AstSubstitute.cs` |
| Distributive expansion | `Parser/AstExpander.cs` |
| Perturbation Taylor builder | `Parser/AstPerturbation.cs` |
| Common emitter base | `Emitters/EmitterBase.cs` and `EmitterCommon.cs` |
| Body emitters | `Emitters/*Emitter.cs` |
| Template substitution + CLI | `Program.cs` |
| Templates | `Templates/Calculator.template.cs`, `Templates/SelfTest.template.cs` |

Treat the parser → AST → emitter pipeline as a one-way street. The AST
is the canonical representation; emitters never mutate it.

---

## 2. The AST

`AstNodes.cs` defines a small set of nodes — every supported equation
parses into a tree of these:

| Node           | Meaning                                |
|----------------|----------------------------------------|
| `ZRef`         | The current iterate z                  |
| `CRef`         | The view-centre c                      |
| `DRef`         | The derivative dz/dc                   |
| `DeltaRef`     | Per-pixel δ (perturbation context)     |
| `EpsRef`       | Per-pixel ε (perturbation context)     |
| `RealConst(v)` | A real constant                        |
| `Pow(base, n)` | Integer-power exponent                 |
| `Mul(a, b)`    | Complex multiplication                 |
| `Add(a, b)`    | Complex addition                       |
| `Sub(a, b)`    | Complex subtraction                    |
| `Neg(a)`       | Complex negation                       |

The parser builds the AST from the right-hand side of `z_{n+1} = …`.
Anything the grammar can't express becomes a parse error — there is no
silent fallback. Add a new operator? Add a node here, teach the parser
to lex/parse it, then teach every emitter to emit it. Missing-arm
exceptions in emitters are intentional; they surface the gap
immediately during the next CalculatorGen build instead of producing
silently-wrong code.

### Differentiation

`AstDifferentiator.cs` implements partial-derivative rules over the
AST. Three entry points:

- `DpDz(root)` — ∂p/∂z. Used by the BLA `A` coefficient and the
  derivative-update recurrence.
- `DpDc(root)` — ∂p/∂c. Used by the BLA `B` coefficient.
- `BuildDerivativeUpdate(root)` — full per-iter rule
  `dz/dc_{n+1} = (∂p/∂z)·dz/dc_n + (∂p/∂c)`. Drives the scalar /
  AVX2 / GPU derivative path AND the perturbation derivative path.

All three call `AstSimplifier.Simplify` afterwards so the emitted code
isn't littered with `0 · x` and `1 · x`.

### Perturbation Taylor expansion

`AstPerturbation.BuildDeltaUpdate(root)` builds the AST for

```
    δ_{n+1}  =  p(Z + δ, C + ε)  −  p(Z, C)
```

by repeated partial differentiation. For polynomial `p`, the Taylor
series terminates after a finite number of terms — the builder loops
over `(k, m)` where `k + m ≤ maxOrder` (effectively the polynomial
degree), differentiates `k` times in Z and `m` times in C, multiplies
by `δ^k · ε^m / (k! m!)`, and sums. **Simplification inside the loop
matters**: without `AstSimplifier.Simplify` after each `Diff`, the
intermediate trees explode and the builder hangs on degree-3+ inputs.

The output AST uses `DeltaRef` and `EpsRef` nodes where the emitters
later bind `dr/di` and `er/ei` (or DD-typed variants).

---

## 3. Emitters

Every emitter derives from `EmitterBase` (`Emitters/EmitterCommon.cs`).
The base recursively walks the AST and dispatches to abstract methods:

```csharp
protected abstract string ZRe { get; }
protected abstract string ZIm { get; }
protected abstract string CRe { get; }
protected abstract string CIm { get; }
protected abstract string DRe { get; }
protected abstract string DIm { get; }
// + DeltaRe, DeltaIm, EpsRe, EpsIm in EmitterBase for perturbation

protected abstract ComplexExpr Const(double v);
protected abstract ComplexExpr OpAdd(ComplexExpr a, ComplexExpr b);
protected abstract ComplexExpr OpSub(ComplexExpr a, ComplexExpr b);
protected abstract ComplexExpr OpMul(ComplexExpr a, ComplexExpr b);
protected abstract ComplexExpr OpNeg(ComplexExpr a);
```

Each emitter overrides the bindings (variable names) and op
implementations (how `+`, `-`, `*` look in its target precision /
representation). The base handles the AST walk so emitters never see
recursion themselves.

### `ComplexExpr` and the imag-zero flag

`ComplexExpr(Re, Im, ImZero)` carries a real string, an imag string,
and a flag indicating that the imaginary part is provably zero. When
both inputs of `Mul/Add/Sub` have `ImZero = true`, the resulting Im
collapses to literal `"0.0"` and `ImZero` propagates. Constants, the
real-only branches of derivative chains, and `Pow(z, k)` for real `z`
all start `ImZero = true`. The savings are biggest in the AVX2 path,
where each suppressed Im term skips a `Vector256<double>` op.

### The emitter zoo

| Emitter | Output | Used in |
|---------|--------|---------|
| `ScalarEmitter` | `double <p>r_new = …; double <p>i_new = …;` | Scalar z-update, scalar d-update (`SCALAR_Z_BODY`, `SCALAR_D_BODY`), reference orbit's z-update |
| `Avx2Emitter` | `Vector256<double> ...` (SSA temps) | AVX2 z-update, AVX2 d-update (`AVX2_Z_BODY`, `AVX2_D_BODY`) |
| `PerturbationEmitter` | `double dr_new = …; double di_new = …;` | Per-pixel δ-update inside perturbation loop (`PERTURB_DELTA_BODY`) |
| `PerturbDerivEmitter` | `double drv_new = …; double div_new = …;` | dz/dc derivative inside perturbation loop (`PERTURB_DERIV_BODY`); also reused inside the HP-direct helpers |
| `QdEmitter` | `QD zr_q_new = …; QD zi_q_new = …;` | Reference orbit's QD z-update (`QD_Z_BODY`) |
| `DdDirectEmitter` | `DD zr_dd_new = …; DD zi_dd_new = …;` | DD direct z-update inside `ComputePixelDdDirect` (`DD_DIRECT_BODY`) |
| `QdDirectEmitter` | `QD zr_q_new = …; QD zi_q_new = …;` | QD direct z-update inside `ComputePixelQdDirect` (`QD_DIRECT_BODY`) |
| `DdEmitter` *(legacy, kept)* | `DD dr_dd_new = …;` | Currently unused after the perturbation path reverted to plain-double δ. Retained because the emitter is self-contained and may be wired back in if a future BLA-in-DD variant lands. |

### Avx2Emitter SSA detail

`Avx2Emitter` is the only emitter that *names temporaries* — the other
emitters emit one expression tree per body. When the same body is
emitted twice in the same method (D-body + Z-body sharing a scope), the
SSA names would collide. That's why `Program.cs` constructs two
`Avx2Emitter` instances with distinct `tempPrefix` strings (`"z_"` and
`"d_"`). Add a new place that calls `Avx2Emitter` from the same scope?
Pick a third unique prefix or you'll get `CS0128 'local re3 already
defined'`.

---

## 4. Template substitution

`Program.cs` loads `Templates/Calculator.template.cs` (embedded
resource) and runs `Replace` for each `{{PLACEHOLDER}}`. Current
placeholders:

| Placeholder | Comes from | Lives in template |
|-------------|-----------|-------------------|
| `{{CLASS_NAME}}` | `--name` | Class declaration, self-test references |
| `{{EQUATION_SOURCE}}` | `--equation` | File-header comment |
| `{{DPDZ_TEXT}}` / `{{DPDC_TEXT}}` / `{{DERIV_TEXT}}` | `AstPrinter` | Documentation comment |
| `{{PERTURB_DELTA_TEXT}}` | `AstPrinter` | Documentation comment |
| `{{TIMESTAMP}}` | `DateTime.UtcNow` | Header |
| `{{SCALAR_Z_BODY}}` / `{{SCALAR_D_BODY}}` | `ScalarEmitter` | Inside `IteratePixelScalar`, `IteratePixelScalarRaw`, GPU kernel, reference-orbit fallback |
| `{{AVX2_Z_BODY}}` / `{{AVX2_D_BODY}}` | `Avx2Emitter` | Inside `IterateLaneAvx2`, `IterateLaneAvx2Raw` |
| `{{PERTURB_DELTA_BODY}}` | `PerturbationEmitter` | Inside `TryRenderPerturbation`'s per-pixel loop |
| `{{PERTURB_DERIV_BODY}}` | `PerturbDerivEmitter` | Inside the perturbation loop + both HP-direct helpers |
| `{{QD_Z_BODY}}` | `QdEmitter` | Inside the QD reference-orbit loop |
| `{{DD_DIRECT_BODY}}` | `DdDirectEmitter` | Inside `ComputePixelDdDirect` |
| `{{QD_DIRECT_BODY}}` | `QdDirectEmitter` | Inside `ComputePixelQdDirect` |

Adding a new placeholder: introduce it in `Calculator.template.cs`,
build the body via an existing or new emitter in `Program.cs`, and call
`.Replace("{{NAME}}", body)` before the file is written. Forgetting the
`Replace` step is silent: the placeholder text ends up in the generated
file and the next `dotnet build` fails on parse error with `{{NAME}}`
in the error string. That's the desired loud failure.

---

## 5. Execution paths inside a generated calculator

The generated `Calculate(CancellationToken)` method picks a path based
on `UseGpu`, `UsePerturbation`, `Zoom`, and the thresholds. Tiers from
fastest (shallowest) to slowest (deepest):

```
                         Zoom ranges
   Zoom < ~1e12                   Zoom in [1e12, ~1e25]            Zoom ≥ 1e25
        │                                  │                              │
        ▼                                  ▼                              ▼
  AVX2 lane (4 px)                Perturbation:                   Perturbation:
  Scalar fallback                  • Reference orbit (QD if         • QD ref orbit
        │                            Zoom ≥ ExtendedRefZ)           • Per-pixel δ in
        │                          • Per-pixel δ in double            double
        │                          • Per-pixel glitch / ref-         • Per-pixel HP fallback
        │                            exhausted → DD-direct             promotes DD → QD
        │                                  │                              │
        └────────── GPU dispatch (opt-in, lazy init) ────────────────────┘
                              │
                              ▼
                         CPU post-pass colour
```

### Path order in `Calculate()`

1. **GPU** (`UseGpu` and `TryRenderGpu` succeeds) — `LastPrecisionLabel
   = "GPU"`.
2. **Perturbation** (`UsePerturbation` and `Zoom >=
   PerturbZoomThreshold`). `TryRenderPerturbation` returns `true` on
   success → `"PT"` (double reference orbit) or `"QD-PT"` (QD reference
   orbit). Returns `false` only when the orbit escapes at **iter 0**
   — i.e. the view centre is firmly exterior to the set, no
   perturbation possible.
3. **Whole-frame HP-direct** (`Zoom >= DdDeltaZoomThreshold`) — runs
   only when perturbation returned false. Per pixel iterates `z =
   p(z, c)` in DD or QD. Label `"DD-HP"` / `"QD-HP"`.
4. **AVX2 / scalar** — shallow-zoom fallback. `"SP"`.

### Reference orbit truncation

The reference orbit (`TryRenderPerturbation`'s outer loop) iterates `Z`
in QD (`useExtendedRef` true) or double (`useExtendedRef` false) and
stores high limbs in `refZr[]` / `refZi[]` plus lo limbs for the
QD case. Three exit conditions:

- Orbit runs to `maxIt` cleanly → `refOrbitLen == maxIt`, `refZr[maxIt]`
  stamped.
- Orbit escapes at iter `n > 0` → `refOrbitLen = n; break;`. Slots
  `[0..n-1]` valid. Per-pixel iter caps at `refOrbitLen`.
- Orbit escapes at iter 0 → `return false;` (whole-frame HP-direct
  fallback).

The "escape at iter > 0" case is the common one near the Mandelbrot
set's boundary at extreme zoom. Pixels that *would* have escaped before
`refOrbitLen` get the fast perturbation path. Pixels that need more
iters fall to per-pixel HP-direct (the `refExhausted` branch).

### Glitch detection

Inside the perturbation loop:

```csharp
if (zr == Zr && zi == Zi && (dr != 0.0 || di != 0.0)) {
    glitched = true;
    break;
}
```

`zr = Zr + dr` in double — when `|dr|` is below `Zr`'s ULP, the
addition rounds back to `Zr`. The pixel's δ has been swallowed; further
iteration tracks the reference orbit, not this pixel. The pixel falls
to HP-direct (DD below `QdDirectZoomThreshold`, QD above).

### HP-direct helpers

`ComputePixelDdDirect(DD cr, DD ci, int maxIt)` and
`ComputePixelQdDirect(QD cr, QD ci, int maxIt)` are the per-pixel
escape-time loops in extended precision. They iterate `z = z² + c` (or
whatever the equation is, via the `*_DIRECT_BODY` placeholders) and
track `dz/dc` alongside in plain double for the colour map's distance
estimate and surface normal. The `cr`/`ci` arguments come from
`DD.FromCenterOffset(center, pixelOffset, scale)` /
`QD.FromCenterOffset(…)` — these build the per-pixel c value to full
extended precision even when `scale` is far below double precision
relative to the centre.

---

## 6. Status labels

`LastPrecisionLabel` is set during `Calculate()` to advertise which
path actually ran. The render host surfaces it to the status bar:

| Label | Meaning |
|-------|---------|
| `SP`     | AVX2 / scalar — shallow zoom only |
| `GPU`    | ILGPU kernel + CPU colour post-pass |
| `PT`     | Double-precision reference orbit + double δ |
| `QD-PT`  | QD reference orbit + double δ (most common at deep zoom) |
| `DD-HP`  | Whole-frame DD direct iteration (centre is exterior) |
| `QD-HP`  | Whole-frame QD direct iteration (centre exterior, very deep) |

The render host's old `IsHighPrecisionActive` flag is meaningful only
for `MandelbrotCalculator`; for generated calculators the host queries
`LastPrecisionLabel` and maps `"DD"` / `"QD"` to "[DD]" status. See
`FractalRenderHost.cs:Trigger` near the alt-calc branch.

---

## 7. Adding things

### Adding a polynomial operator

1. Add an AST node in `AstNodes.cs`.
2. Teach the lexer / parser to emit it.
3. Add a `Diff` rule in `AstDifferentiator.cs`.
4. Add cases in `AstSimplifier.cs` / `AstSubstitute.cs` / `AstExpander.cs`
   as needed.
5. Add a method to every emitter that handles the new node.

Forgetting step 5 is loud — `EmitterBase`'s walker throws on the
unknown node type, and the next CalculatorGen build fails.

### Adding a new precision tier

1. Create an emitter that overrides the variable bindings to your
   precision-typed locals (`Foo zr_f`, etc.) and the op implementations
   so the emitted string uses your type's `+ - *`.
2. Add the body emit in `Program.cs` and a new `{{FOO_BODY}}` placeholder.
3. Drop the body into a new method in the template. Wire `Calculate()`
   to dispatch to it.
4. Update the status label scheme so the path is observable.

The DD-direct / QD-direct pair was added this way — both emitters are
tiny (the AST walk and op semantics come for free from
`EmitterBase`).

### Adding a new fractal equation

You almost certainly don't need to modify the generator — just invoke
the CLI with a new `--equation` / `--name`. If your equation parses,
the generator handles the rest. The dispatcher wiring (toolbar entry +
render host case) is per-calculator boilerplate; the existing
`MandelbrotZ2Calculator` is the template to copy.

---

## 8. Debugging

### Self-test first

`MandelbrotZ2CalculatorSelfTest.Run(out var report)` (and equivalents
for every `--selftest` build) compares all paths on a 64×64 grid and
returns a report. **Any new generator change must keep this passing.**
If the AVX2 path drifts from scalar by more than 1 ULP per pixel, or
GPU disagrees with scalar by more than 4 in-set pixels at this grid,
something broke.

Run the self-test from the CLI:

```pwsh
.\bin\x64\Release\net10.0-windows\FracturingFog.exe --gentest MandelbrotZ2
```

The deep-zoom paths (DD-HP, QD-HP, perturbation glitches) are not
exercised by the existing 64×64 self-test (it uses Zoom = 1). To
validate them, run the app and zoom in past `1e12` — the status bar
shows the active path so you can confirm you're hitting the code under
test.

### Inspecting generated bodies

`dotnet run --project CalculatorGen … --dry-run` prints the rendered
calculator to stdout instead of writing it. Pipe through `Select-Object
-First 500` or similar to read the prelude. Bodies expand inline so
you can see exactly what the AST → emitter pipeline produced.

### Common failure modes

| Symptom | Likely cause |
|---------|--------------|
| `CS0128 'local re3 already defined'` | Two `Avx2Emitter` instances sharing a `tempPrefix` in the same scope |
| Pixelation past ~1e12 | Glitch detection not firing, or HP-direct helpers not engaged. Check `LastPrecisionLabel`. |
| Whole frame black or single-coloured at deep zoom | `refOrbitLen` truncation case treated as in-set instead of falling to HP-direct. Verify the `refExhausted` branch fires. |
| Render extremely slow past `1e23` | Wholesale DD δ-loop running instead of double δ + per-pixel HP. Check `Calculate()` path order. |
| Frame is correct but smooth-count banding is visible at deep zoom | The smooth count formula loses precision in `float` once `Zoom` is past ~1e15. The dz/dc-derived distance estimate and surface normals carry the per-pixel signal — verify the colour map consumes those (9-arg `Map`). |
| `{{PLACEHOLDER_TEXT}}` ends up in generated file | A new placeholder added to the template without a matching `Replace` call in `Program.cs`. |

---

## 9. What's NOT generated (yet)

Inventory of legacy `MandelbrotCalculator` features that the generator
doesn't yet emit. None are blocking — generated calculators render
correctly without them — but each is a known optimisation that would
narrow the perf gap with the hand-tuned legacy path:

- **AVX-512 perturbation** (8 pixels per lane). Generator emits AVX2
  (4-wide). Legacy uses AVX-512 at deep zoom where supported.
- **Reference orbit caching**. Generator rebuilds the orbit every
  frame. Legacy keeps the last orbit when the centre + maxIt are
  unchanged, saving 20-30 % on pan.
- **Series approximation prelude**. Legacy skips early iterations via
  a Taylor-series approximation around z=0 before handing off to BLA /
  perturbation. Adds another 1.5-2× on deep zoom.
- **Auto-recenter via rebase**. When the reference orbit escapes
  prematurely, legacy can pick a fresh interior centre nearby. The
  generator's current behaviour is to truncate `refOrbitLen` and fall
  pixels past the truncation to HP-direct — correct but slower than a
  rebase when the truncation is severe.
- **Float-precision-resistant smooth count**. The standard
  `it + 1 - log2(log2(|z|))` collapses in float past ~1e15.
  Generator currently relies on the distance estimate to carry
  per-pixel signal; the smooth-count fractional itself stops varying.
  A DD-precision smooth computed and narrowed to float at the end
  would close this gap.

Pick any of these as a starting point if you want to make a generated
calculator outrun the legacy hand-tuned one. The architecture in this
document is designed to take them — each one adds an emitter and a
placeholder, not a rewrite.
