# User Bulb Sandbox — Dev Plan

Sandbox-Bulb DSL on the `feature/gpu-compute` branch. Two stages shipped, one stage remaining. This doc tracks status and the remaining ILGPU JIT work.

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

## Stage 3 — ILGPU JIT (REMAINING)

### 3A. Roslyn-compile emitted C# → ILGPU kernel  (L, ~16 h, high risk)

The emitter from Stage 2 (#13) produces valid C# expressions but nothing consumes them on the GPU side yet. `UserBulbGpuCalculator` hardcodes the triplex formula and is gated by analytic-DE pattern match — Sandbox sources matching Square or MandelbulbN already ride that path correctly. Stage 3 makes the kernel actually JIT-consume the emitted text so non-analytic Sandbox sources can also render on GPU.

**Approach**

1. Take emitter output (currently a C# expression string).
2. Wrap in a static method via Roslyn scripting (matches existing `UserBulbCalculator.WrapUserSource` shape).
3. Register the compiled method with ILGPU as a kernel-callable function.

**Open problems**

- **ILGPU constraint surface.** No closures, no boxing, struct-only params, no virtual calls, no exception flow. Existing `Vec3`/`Quat` types appear ILGPU-safe (readonly record struct, no reference fields) but each builtin call site needs validation. Spike: emit `triplex(z, 8) + c` through the full pipeline and check ILGPU acceptance.
- **Vec3 helpers on device.** `Vec3.Pow`, `Vec3.BoxFold`, `Vec3.SphereFold` etc. need to compile under ILGPU's restricted JIT. Some may rely on `Math.*` paths that ILGPU lowers automatically; others (especially anything using `MathF` / SIMD intrinsics) may fail. Audit per call site.
- **`Quat.Pow` loop.** Iterative Hamilton self-multiply — ILGPU may handle a bounded `for` loop but rejects unbounded or recursive forms. The literal-int unfolded path (#15) sidesteps this on the emit side; runtime `Quat.Pow` is the risk.
- **Source caching.** Like `UserBulbIlgpuTranslator` (#20), the compiled kernel should be cached per source string.

**Files**

- New: `Calculators/UserBulbSandboxGpuCompiler.cs` — Roslyn → ILGPU bridge.
- Modify: `Calculators/UserBulbCalculator.cs` — when Backend=GPU AND Compiler=Sandbox, call the bridge before falling back to the hardcoded analytic kernel.
- Possibly: `Models/Vec3GpuOps.cs` — device-safe mirrors of any `Vec3.*` static that ILGPU rejects.

**Test plan**

1. Spike: emit `triplex(z, 8) + c` → compile via Roslyn → register via ILGPU → render. Accept on success.
2. Self-test: emitter→Roslyn→ILGPU pixel parity vs CPU Sandbox interpreter for triplex, chain abs→triplex, qmul(z,z)+c.
3. Perf: target ≥10× CPU Sandbox for the triplex case.

**Why spike-first**: The constraint surface is unknown until the bridge attempts to compile a real kernel. A spike (≤4 h) determines whether the approach is viable. If ILGPU refuses to JIT Roslyn-emitted delegates the design pivots to direct SPIR-V emission or a separate compiler tier.

### 3B. Sandbox-Quat GPU path  (M, ~4 h, blocked by 3A)

Existing GPU kernel `!quatMode` gated. Once 3A lands, extend kernel to Quat mode with the 5-trajectory numerical Jacobian (matches CPU `UserBulbQuatDE`), or analytic for `qmul(z,z)+c` (the Quat-square case).

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
