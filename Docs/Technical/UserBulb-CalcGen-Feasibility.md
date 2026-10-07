# User Bulb 3D: a CalcGen-like compile path? (#1104)

Status: **study complete — recommendation: partial.** Do not build a CalcGen
equivalent for User Bulb: no toggle, no persisted calculators, no DD/QD. Instead,
take three targeted runtime slices that the measurements below point at: #1110,
#1111, #1112. Related: #1091 (language scope), #1100–#1102, #1105.

## What exists today

| Path | How an equation runs |
|---|---|
| CPU | `SandboxBulbExpression` parse → `SandboxBulbCompiler` builds an Expression-tree delegate (#283, no Roslyn). It is still **dynamically typed**: every value is an `SbxVal3` (kind + four doubles), every operator is a call into `SbxVal3.Add/Mul/…`, and every function goes through one `SbxFuncEval.Apply` switch taking four `SbxVal3` arguments. |
| GPU | `UserBulbSandboxEmitter` infers each node's kind statically and emits **typed** C# (`Vec3`, `Quat`, `double`). Roslyn compiles it and ILGPU loads it as a kernel (`UserBulbSandboxGpuCompiler`). Routes (as measured below): Quat mode with any DE; Vec3 mode only with an analytic-power pattern. Everything else fell back to the CPU. **Since #1112 (2026-10-06):** Vec3 numerical + Julia and the exact quaternion DE run on the GPU too; only the scalar KIFS and non-escaping DEs stay on the CPU, and the status bar names the reason. |
| DE | **Analytic:** a single trajectory with a running derivative, for recognised patterns (triplex power, square triplex, quaternion square #115). **Numerical:** four trajectories (base + three offsets) for a Jacobian. |

## Measurements

Machine: 12 logical CPUs, x64, .NET 10, Release build. GPUs: NVIDIA GeForce GT 710
(CUDA, the preferred device) and Intel UHD (OpenCL). The GT 710 is a 2014
entry-level card, so the GPU column is a floor, not typical. Reproduce with
`UserBulbBenchmark1104` (a skipped manual test; see its header).

**Per-step cost** (ns per step, single thread, Vec3 mode):

| Equation | Tree-walk | Expression tree (today's CPU) | Typed C# via Roslyn (emitter) | Typed vs today |
|---|---:|---:|---:|---:|
| `z^8 + c` | 126.6 | 74.3 | 77.6 | 1.0× |
| square triplex `vec(z.x*z.x - …) + c` | 201.5 | 57.5 | 34.7 | 1.7× |
| Mandelbox (`boxfold`, `spherefold`) | 87.1 | 58.2 | 62.7 | 0.9× |
| Menger fold (KIFS, chained `let`) | 492.8 | 390.8 | 522.7 | **0.7×** |
| `z^4 + sin(z)*0.5 + c` | 438.5 | 394.3 | 84.7 | **4.7×** |
| Amoser sine (`sin`/`cos`/`cosh`/`sinh` per axis) | 990.2 | 867.2 | 37.0 | **23.4×** |

**End-to-end frame** (ms, median of 5, 640×480, Iterations 8, MaxSteps 96, temporal reuse off):

| Case | CPU | GPU |
|---|---:|---:|
| `z^8 + c`, analytic DE | 237 | 329 |
| `z^8 + c`, numerical DE | 1347 | 324 |
| Mandelbox, numerical | 8230 | 8410 (no Vec3 numerical GPU route → CPU) |
| `z^4 + sin(z)*0.5 + c`, numerical | 8039 | 8211 (CPU fallback) |
| Quat Julia `z*z + c`, numerical | 1264 | 700 |
| Quat Julia `z*z + c`, analytic (exact, #115) | **33** | 635 (the GPU has no exact quaternion DE; it runs numerical) |
| Quat `qsin(z*z) + c`, numerical | 2179 | 3266 |

The exact quaternion render is a real image: the same ~80 k covered pixels as the
numerical one, with more distinct shades (9 492 vs 4 237).

**Re-measured after #1112 (2026-10-06).** Same benchmark, but over Remote Desktop, where
the GT 710 can't create a CUDA context (#1195). So the GPU column is User Bulb's ILGPU
**CPU-accelerator** fallback, not the card. The GPU frames now also run the full family
shading (#1173-A), so the GPU column isn't comparable with the table above:

| Case | CPU | GPU route (CPU accelerator) |
|---|---:|---:|
| `z^8 + c`, analytic DE | 243 | 306 |
| `z^8 + c`, numerical DE | 1248 | 1659 |
| Mandelbox, numerical | 8138 | **929** (new GPU route) |
| `z^4 + sin(z)*0.5 + c`, numerical | 8323 | **3497** (new GPU route) |
| Quat Julia `z*z + c`, numerical | 1238 | 348 |
| Quat Julia `z*z + c`, analytic (exact) | 34 | 117 (now the exact DE, not numerical) |
| Quat `qsin(z*z) + c`, numerical | 2170 | 1148 |

Even without a GPU, the JIT'd kernel beats the CPU path's delegate-based DE where the DE
is expensive (Mandelbox 8.8×, `z^4 + sin` 2.4×). Cheap DEs (analytic `z^8`, the exact
quaternion) stay faster on the CPU path, which a real GPU should reverse.

## The questions from #1104

### 1. CPU speed — does generated typed code beat the interpreter?

**Only for transcendental-heavy maps, and there by a lot (5–23×).** For
polynomial and fold maps, today's Expression-tree compile already matches typed
C# (0.9–1.7×). The gap is the dynamic typing: every elementwise `sin` passes
through `Apply`'s switch with 160 bytes of `SbxVal3` arguments and a kind check;
typed code calls `Math.Sin` directly.

**A Roslyn-compiled CPU calculator is not needed to close that gap.** The
emitter's static kind inference can drive a **typed Expression-tree compile**
instead: still no Roslyn, no assembly load, compiles in milliseconds, and keeps
the current dynamic compile as the fallback when kinds can't be inferred. That is
**#1110**.

SIMD across rays was not pursued. Rays take different step counts, so lanes would
need masking. Each step is 35–80 ns of dependent scalar maths, and the ray loop is
already parallel across cores. The GPU is the data-parallel route.

**The emitter makes `let` slower (Menger 0.7×).** It inlines a `let` by pasting
the value's text at every use, so chained folds (`let v1 = … v … v … in let v2 = …
v1 … v1 …`) recompute their inputs repeatedly. The same text goes into the GPU
kernels, so KIFS and chain equations are slower there too. Emitting each `let` as
a local is **#1111**.

### 2. Distance estimate — a general analytic DE?

**Forward-mode (dual-number) differentiation of the vec3/quat tree is feasible but
not a speed win.** A true Jacobian for a 3-vector map needs three tangent
directions, so a dual-number step costs about the same as today's four-trajectory
numerical DE. What it would buy is accuracy: no `Jacobian h` to tune, and crisper
meshes. The non-smooth folds (`abs`, `boxfold`, the KIFS swaps) still have
piecewise derivatives on their fold planes either way.

**The speed win comes from single-trajectory running-derivative DEs, and those are
pattern-specific** (they need conformal or power-law structure). The measurements
show how much a recognised pattern is worth:
- `z^8 + c`: analytic 237 ms vs numerical 1347 ms (5.7×);
- the exact quaternion square: 33 ms vs 1264 ms.

Extending the pattern matcher (`UserBulbAnalyticDE`) is the right lever. A
general dual-number DE stays deferred as a quality option.

### 3. Precision — DD for 3D?

**No.** Deep zoom in 2D works because perturbation theory turns one
high-precision reference orbit into cheap low-precision per-pixel deltas. There is
no standard equivalent for raymarched 3D sets: every ray sample needs its own
orbit. The practical limits are the surface `Epsilon` (0.0015 by default) and the
DE's own accuracy, not `double`'s 1e-16. A DD path would cost roughly 10–20× per
step for detail that the raymarcher's step size already hides.

### 4. Reuse

High. Everything a typed path needs exists already:
- the emitter's kind inference (made exact against the interpreter in #1105);
- the GPU compiler;
- `BulbEmitterParityTests` as the correctness gate for any new compile path.

`CalculatorGenHotLoad`-style persisted calculators aren't needed, because nothing
here produces an artefact the user manages.

### 5. UX — a CalcGen toggle in the bulb editor?

**No.** The 2D toggle exists because CalcGen is a separate, slower-to-build engine
with different capabilities (DD/QD, perturbation) that the user chooses. Every
User Bulb acceleration above is automatic and has the same meaning as the
interpreter:
- the analytic-DE badge;
- the GPU route;
- a typed CPU compile.

There is no decision to hand to the user. A small, useful addition is a status
hint for **why the GPU isn't used** (e.g. "Vec3 numerical DE: CPU only"), folded
into #1112.

### 6. Batch and the Command builder

**No change.** All three slices are runtime paths inside `UserBulbCalculator`,
which `--batch` already uses. There is no persisted artefact and no new parameter,
so headless renders stay reproducible.

## Recommendation and slices

| Slice | What | Expected effect (from the tables) |
|---|---|---|
| **#1110** typed CPU compile | Compile the bulb AST to a **typed** Expression tree using the emitter's kind inference. Keep the dynamic compile as the fallback. Gate: `BulbEmitterParityTests`-style interpreter parity. | Transcendental maps 5–20× faster per step on the CPU; polynomial and fold maps unchanged. |
| **#1111** emitter `let` locals | Emit `let` and statement locals as C# locals (a statement body) instead of pasting their text at every use. | Fixes the 0.7× regression on KIFS / chained folds, for CPU-typed code and GPU kernels alike. |
| **#1112** GPU DE coverage | Add a Vec3 numerical-DE GPU kernel (Quat mode has one since Wave 4.6), plus Vec3 Julia. Give the exact quaternion DE (#115) a GPU form, or route that case to the CPU, which is faster today (33 vs 635 ms). Show why the GPU isn't used. | The slowest renders measured (Mandelbox / `z^4 + sin` numerical, ~8 s) get a GPU route at all. Where a numerical-DE GPU route exists today, this GT 710 ran 1.8–4.2× faster than the CPU (and slower for `qsin`), so a modern card should do much better. |

**Not recommended:**
- a CalcGen toggle or persisted bulb calculators;
- DD/QD for 3D;
- SIMD across rays;
- a general forward-mode DE as a speed measure (it stays a deferred quality option).
