# User Bulb Sandbox — Dev Plan

Sandbox-Bulb DSL on the `feature/gpu-compute` branch. Stages 3A + 3B shipped 2026-06-10. Stage 3C (interpreter perf) remains, plus chain-mode GPU.

---

## Status snapshot (latest smoke, 80×60 grid, 96 iters)

| Path | ms | Hits | Notes |
|---|---|---|---|
| Roslyn `z²+c` | 33–36 | 4799 | baseline |
| Sandbox `triplex(z,8)+c` | 34 | 4756 | analytic — at Roslyn parity |
| Sandbox `z^8+c` | 39 | 4756 | operator-form detect (#16) |
| Sandbox explicit `vec(...)+c` | 209–337 | 4799 | Square pattern detect (#9) |
| Sandbox chain `abs→triplex` | **96** | 4764 | analytic via chain detect (#21) |
| Sandbox chain `sin→triplex` | None | — | Lipschitz guard rejects (#21) |
| Sandbox-Quat `qmul(z,z)+c` | 102–149 | 4800 | Quat support (#10) |
| Emitter→Roslyn parity | exact | 4756 | E2E test passes (#14) |

---

## Locked design decisions

1. **Compiler axis orthogonal to algebra axis.** `UserBulbCompilerKind` (Roslyn / Sandbox) × `UserBulbAxisModeKind` (Vec3 / Quat). Both compilers cover both algebras.
2. **`SbxVal3` is a tagged union {Real | Vec | Quat}** with a W field always present. Vec3 ops leave W=0.
3. **Adapter delegate.** Both compilers expose `Func<Vec3,Vec3,int,double[],Vec3>` (or Quat form). Raymarch is compiler-agnostic.
4. **Per-thread env scratch.** `ThreadLocal<SbxVal3[]>` sized by `EnvSize`. No allocation on hot path.
5. **AnalyticDE pattern detection on the AST**, not source text. Source-text regex stays for Roslyn; Sandbox uses `DetectSandbox(Sbx3Node)` / `DetectSandboxChain(SandboxBulbChain)`.

---

## Stage 1 — DSL foundation (SHIPPED)

- Parser + AST (`Sbx3Node` hierarchy) for vec/triplex/fold ops/let/ternary/comparisons.
- Interpreter with per-thread env array.
- Chain support (`SandboxBulbChain`) with shared scope across steps.
- AnalyticDE pattern detection for `triplex(z, K) + c`.
- UI compiler toggle (Roslyn ↔ Sandbox).
- Self-tests for parity, chain, analytic.

Files: [Models/SandboxBulbExpression.cs](../Models/SandboxBulbExpression.cs), [Models/SandboxBulbChain.cs](../Models/SandboxBulbChain.cs), [Calculators/UserBulbAnalyticDE.cs](../Calculators/UserBulbAnalyticDE.cs), [Calculators/UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs).

## Stage 2 — Coverage, perf, polish (SHIPPED)

| # | Item | Files |
|---|---|---|
| #9 | Square pattern detect on AST | [UserBulbAnalyticDE.cs](../Calculators/UserBulbAnalyticDE.cs) |
| #10 | Quat support — `qmul`, `qconj`, `qpow`, `qvec`, `.w`, Quat eval path | [SandboxBulbExpression.cs](../Models/SandboxBulbExpression.cs), [Quat.cs](../Models/Quat.cs), [UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs) |
| #11 | User guide Sandbox chapter | [UserBulb-Guide.md](UserBulb-Guide.md) §19 |
| #12 | UI badge + tooltips + compiler-aware gating | [UserBulbView.axaml](../UI.Avalonia/Views/UserBulbView.axaml), [UserBulbViewModel.cs](../UI.Avalonia/ViewModels/UserBulbViewModel.cs) |
| #13 | ILGPU emitter foundation — `Sbx3Node → C#` walker with per-subtree kind inference | [UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs) |
| #14 | Emitter→Roslyn E2E parity test | [UserBulbSelfTest.cs](../UserBulbSelfTest.cs) |
| #15 | `qpow` emit — literal int unfolded, runtime `Quat.Pow` fallback | [UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs), [Quat.cs](../Models/Quat.cs) |
| #16 | `z^N + c` operator-form AnalyticDE detect | [UserBulbAnalyticDE.cs](../Calculators/UserBulbAnalyticDE.cs) |
| #17 | Inline let emit (no IIFE — no delegate dispatch) | [UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs) |
| #18 | Reject `sin/cos/...` on Quat (interpreter + emitter) | [SandboxBulbExpression.cs](../Models/SandboxBulbExpression.cs), [UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs) |
| #19 | Parser error spans → UI highlight with focus guard | [SandboxBulbExpression.cs](../Models/SandboxBulbExpression.cs), [UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs), [UserBulbViewModel.cs](../UI.Avalonia/ViewModels/UserBulbViewModel.cs), [UserBulbView.axaml.cs](../UI.Avalonia/Views/UserBulbView.axaml.cs) |
| #20 | Translator cache (`ConcurrentDictionary`, cap 32) | [UserBulbIlgpuTranslator.cs](../Calculators/UserBulbIlgpuTranslator.cs) |
| #21 | Chain AnalyticDE detect — last-step pattern + Lipschitz-≤1 fold prefix | [UserBulbAnalyticDE.cs](../Calculators/UserBulbAnalyticDE.cs), [SandboxBulbChain.cs](../Models/SandboxBulbChain.cs) |
| #22 | Help anchor jump to Sandbox section when compiler=Sandbox | [UserBulbViewModel.cs](../UI.Avalonia/ViewModels/UserBulbViewModel.cs) |

---

## Stage 3 — ILGPU JIT

### 3A — Sandbox → Roslyn → ILGPU kernel (SHIPPED 2026-06-10)

Stage 3A end-to-end runtime path: Sandbox DSL → AST → emitter (`gpuTarget: true`) → Roslyn in-memory asm → ILGPU `LoadAutoGroupedStreamKernel`. Wired into [UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs) GPU gate ahead of the legacy [UserBulbGpuCalculator.cs](../Calculators/UserBulbGpuCalculator.cs) power-N path.

| File | Role |
|---|---|
| [Models/Vec3GpuOps.cs](../Models/Vec3GpuOps.cs) | Device-safe mirrors of `Vec3.Pow`/`Rot`/`BoxFold`/`SphereFold`/`Mod`/`Normalized` using scalar `if`-clamps (no `Math.Clamp` Throw). |
| [Calculators/UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs) | New `Emit(..., gpuTarget: bool)` overload routes `Vec3.*` → `Vec3GpuOps.*` and `Math.Clamp` → `Vec3GpuOps.Clamp` when true. (3B: Quat axis also supported — runtime `qpow` → `QuatGpuOps.Pow`.) |
| [Calculators/UserBulbSandboxGpuCompiler.cs](../Calculators/UserBulbSandboxGpuCompiler.cs) | Bridge. Parses, emits, wraps in kernel source (mirrors `BulbKernel` shape), Roslyn-compiles, JITs via ILGPU. Caches kernel by `(source, paramNames, axisMode)`. fp64 fallback: catches `CapabilityNotSupportedException` and re-JITs on CPU accelerator. |
| [Calculators/UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs) | GPU gate now routes to `UserBulbSandboxGpuCompiler` when `Compiler=Sandbox` and chain-less; falls through to `UserBulbGpuCalculator` on any failure. |

**Smoke (`dotnet run -- --ubspike`):** T1/T2/T3/T4 all pass on this box (Intel UHD OpenCL → CPU-accel fp64 fallback). T5 added in 3B (see below).

| Tier | What | Result |
|---|---|---|
| T1 | Minimal kernel in byte[]-loaded asm, no external types | **SUCCESS** |
| T2 | Kernel + cross-asm `Vec3` reference | **SUCCESS** |
| T3 | `triplex(z, 8) + c` emitter body in DE-loop kernel + spike-inlined `TriplexPowSafe` | **SUCCESS** (parity vs CPU `Vec3.Pow` matches) |
| T4 | Full `UserBulbSandboxGpuCompiler.TryCompile` + `Render` on `triplex(z, 8) + c` | **SUCCESS** (hit=326, bg=698 on 32×32 with the fp64 fallback) |

**Limitations carried forward (after 3B):**

- Chain mode (multi-step DSL) not yet GPU-compiled — chain path stays CPU.
- Quat-mode Julia + numerical-Jacobian DE not yet on GPU — current quat GPU path is analytic-DE only (matches the CPU `qpow(z, K) + c` shape). Julia + 5-trajectory DE stays CPU.
- fp64 fallback to CPU accelerator works but is slower than CUDA/OpenCL; on fp64-capable devices the preferred accelerator is used directly.

### 3B — Sandbox-Quat GPU path (SHIPPED 2026-06-10)

Extends 3A's vec-mode kernel with a Quat-mode variant. Emitter routes Quat constants, Hamilton `*`, `.Conjugate`, `.Length`, and `qpow` through device-safe paths. `QuatGpuOps.Pow` is the throw-free mirror of `Quat.Pow` (runtime exponent — literal int still inlines to chained `*` from Stage 2). Kernel branches on `quatMode` in `BuildKernelSource`: `Step` takes `Quat z/c`, `SandboxDE` projects via `GpuRenderParams.QuatSliceW`.

| File | Role |
|---|---|
| [Models/QuatGpuOps.cs](../Models/QuatGpuOps.cs) | `Pow(Quat, double)` mirror — rounds + clamps to `[0, MaxIter]`, loops Hamilton-multiply. No throw on non-integer/negative/non-finite. |
| [Calculators/UserBulbSandboxEmitter.cs](../Calculators/UserBulbSandboxEmitter.cs) | Drops `gpuTarget && quatMode` early-return. Runtime-exponent `qpow` routes to `QuatGpuOps.Pow` on GPU. |
| [Calculators/UserBulbSandboxGpuCompiler.cs](../Calculators/UserBulbSandboxGpuCompiler.cs) | `BuildKernelSource(stepBody, paramNames, quatMode)`. Quat branch builds `Quat Step(Quat z, Quat c, …)` + `Quat`-typed analytic DE loop using `p.QuatSliceW` as `c.W`. |
| [Calculators/UserBulbGpuCalculator.cs](../Calculators/UserBulbGpuCalculator.cs) | `GpuRenderParams.QuatSliceW` added. |
| [Calculators/UserBulbCalculator.cs](../Calculators/UserBulbCalculator.cs) | GPU gate drops `!quatMode` guard at top, gates only the legacy Roslyn-translator branch on `!quatMode`. Populates `gp.QuatSliceW`. |

**T5 smoke:** `qpow(z, 2) + c` in Quat mode, 32×32, DEIter=12 → hit=284, bg=740 on the fp64-fallback CPU accelerator.

### 3A spike — VIABILITY CONFIRMED (2026-06-10, retained for context)

Three tiered probes run via `dotnet run -- --ubspike` ([UserBulbSandboxGpuSpike.cs](../Calculators/UserBulbSandboxGpuSpike.cs)) against the CPU accelerator (the OpenCL/Intel UHD device on this box has no fp64).

| Tier | What | Result |
|---|---|---|
| T1 | Minimal kernel in byte[]-loaded asm, no external types | **SUCCESS** |
| T2 | Kernel + cross-asm `Vec3` reference | **SUCCESS** |
| T3 | `triplex(z, 8) + c` emitter body wrapped in a DE-loop kernel + device-safe `TriplexPow` | **SUCCESS** (1024/1024 finite, 719 inSet / 305 outSet, center pixel parity vs CPU `Vec3.Pow` = 0/0) |

**Confirmed.** Runtime `Assembly.Load(byte[])` outputs are JIT-acceptable to ILGPU. Cross-asm type refs resolve. No need for on-disk staging or for sinking `Vec3` into the runtime asm.

**Blockers found and fixed in-spike.**

- `Vec3.Pow` calls `Math.Clamp`, which lowers to a `Throw` opcode ILGPU rejects (`Not supported IL instruction of type 'Throw'`). Workaround in spike: hand-rolled `TriplexPowSafe` that clamps via two scalar compares. Real fix: add `Models/Vec3GpuOps.cs` with device-safe mirrors of `Vec3.Pow`, `Vec3.Rot`, `Vec3.SphereFold`, `Vec3.BoxFold`, etc., and route the emitter to call those when targeting GPU.
- Intel UHD OpenCL has no fp64. Existing `UserBulbGpuCalculator` shares this risk; out of scope for 3A. Pivot if it bites: float32 kernel variant.

### 3C. Interpreter perf  (M, ~6 h)

CPU Sandbox interpreter is currently ~10–15× slower than Roslyn for non-analytic sources. Options:

- Opcode-flat dispatch table (replace virtual `Eval` dispatch with switch on a packed opcode array).
- Struct-based ops (avoid `Sbx3Binary`/`Sbx3Call` heap allocation cost on tight loops — already small but `Eval` is virtual).
- IL emit (compile AST to `DynamicMethod`).

Lower priority once 3A lands — GPU wins dominate for big renders.

---

## Out of scope (intentional)

- Removing the Roslyn compile path.
- DSL extensions beyond the existing function table.
- Cross-compiling the DSL to WebGL / CUDA-direct / SPIR-V (ILGPU is the only backend).
- Standalone `Docs/UserBulbSandbox-DSL-Reference.md` (section 19 of [UserBulb-Guide.md](UserBulb-Guide.md) covers the grammar fully — split only if the section grows).

---

## Test rubric (applies to every item)

1. Build clean.
2. `dotnet run -- --ubtest` self-test passes, all existing scenarios green.
3. New scenario added under [UserBulbSelfTest.cs](../UserBulbSelfTest.cs) for the new path.
4. Manual smoke render in Avalonia shell (`dotnet run`) — confirm no visual regression on a saved preset.
5. Performance: no item slows `triplex(z, 8) + c` Sandbox path below the 34 ms baseline.
