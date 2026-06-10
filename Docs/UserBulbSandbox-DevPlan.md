# User Bulb Sandbox — Remaining Deferred Items (Dev Plan)

Sandbox-Bulb DSL (Stage 1) shipped in the `feature/gpu-compute` branch: parser, interpreter, chain support, AnalyticDE pattern recognition (`triplex(z, K) + c`), UI compiler toggle, smoke tests. See [UserBulb-Guide.md](UserBulb-Guide.md) for the user-facing surface.

This doc covers the items deliberately deferred at Stage 1 — what remains, why each is wanted, and the recommended execution order.

---

## Phase-1 results (Stage 1 baseline)

```
Roslyn z²+c                  32 ms / 4799 hits   (numerical-DE baseline)
Sandbox z²+c (numerical DE) 435 ms / 4799 hits   (parity)
Sandbox triplex(z,8)+c      134 ms / 4756 hits   (analytic DE engaged → 3.2× faster)
Sandbox chain abs → triplex 285 ms / 4764 hits   (multi-step works)
```

Sandbox interpreter ≈ 10–15× slower than Roslyn (expected). Analytic-DE detection recovers most of that for power-map fractals; chains stay numerical for now.

---

## Locked design decisions (carried from Stage 1)

1. **Compiler axis is orthogonal to algebra axis.** Roslyn vs Sandbox is a separate enum from Vec3 vs Quat. Any compiler can target any algebra mode once support lands.
2. **Sandbox is real-only.** No complex tag in `SbxVal3`. Cross-product, triplex, quaternion are special-cased calls.
3. **Adapter delegate.** Both compilers expose `Func<Vec3,Vec3,int,double[],Vec3>` (or `Func<Quat,Quat,int,double[],Quat>` once Quat lands). The raymarch loop stays compiler-agnostic.
4. **Per-thread env scratch.** Interpreter never allocates on the hot path. ThreadLocal env array sized by `EnvSize` at parse time.
5. **AnalyticDE pattern is detected on the AST, not the source text.** Source-text patterns stay for Roslyn (regex). Sandbox uses `DetectSandbox(Sbx3Node)`.

---

## Remaining items, in execution order

### 1. AnalyticDE: explicit Square-triplex shape on Sbx AST  (S, ~2 h)

`DetectSandbox` currently matches only `triplex(z, K) + c`. The canonical hand-written squared-bulb formula

```
vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c
```

should also detect as `AnalyticDEKind.Square` (power=2). Roslyn already does this via regex (see `UserBulbAnalyticDE.Detect`). Goal: parity for users who paste the explicit form.

**Files**
- [Calculators/UserBulbAnalyticDE.cs](../Calculators/UserBulbAnalyticDE.cs) — extend `DetectSandbox` with a `TryMatchExplicitSquare(Sbx3Node)` helper.

**Strategy**
- Walk `Sbx3Binary{Op:"+"}`, find the `vec(...)` side and the `c` slot side.
- For the `vec` side: three component expressions must each match one of the three canonical sub-trees. Allow either operand order for commutative `*` (`2*z.x*z.y` ≡ `z.x*z.y*2` ≡ `z.y*2*z.x`).
- Helper: small AST equality / "matches `a*b`" / "matches `±a*a`" predicates.

**Test**
- Self-test case: parse the canonical formula, assert `DetectSandbox` returns `Square` with power=2.
- Render-time confirmation: smoke run should approach Roslyn perf (currently 32 ms; analytic Sandbox would land near the 134 ms triplex number once the per-iter interpreter cost is the dominant remaining work).

---

### 2. UI dialogue updates  (S, ~3 h)

Surface compiler/algebra interactions in the UserBulb dialog so users do not hit "Sandbox compiler is Vec3-only" errors blindly.

**Files**
- [UI.Avalonia/Views/UserBulbView.axaml](../UI.Avalonia/Views/UserBulbView.axaml) — tooltips, conditional enables, analytic badge.
- [UI.Avalonia/ViewModels/UserBulbViewModel.cs](../UI.Avalonia/ViewModels/UserBulbViewModel.cs) — `IsSandbox`, `AnalyticEngaged` properties + a `SandboxAllowsQuat` gate.

**Changes**
- **Compiler combobox**: add tooltip explaining Roslyn vs Sandbox tradeoffs (BCL / GPU vs sandboxed / interpreter perf).
- **Algebra combobox**: disable "Quat (4D)" when compiler = Sandbox (until item 4 lands). Tooltip on the disabled state: `"Quat mode not yet implemented in Sandbox compiler — switch to Roslyn."`
- **DE badge**: read-only `TextBlock` next to DE-mode combobox showing `"Analytic engaged"` (green) or `"Numerical only"` (dim grey). Driven by `AnalyticEngaged`, updated post-compile.
- **Help button**: existing `?` button routes to [UserBulb-Guide.md](UserBulb-Guide.md) — add a Sandbox section anchor (item 3).

**Colorblind note**
Error text already uses yellow `#FFCC00` (per user pref). Analytic-engaged badge: green `#64FF64` for OK, grey for off — same palette as the existing status row.

---

### 3. User Guide + in-app help docs  (M, ~6 h)

Add a Sandbox DSL chapter to the existing User Bulb guide. Mirror the style of `FractalEquation-DesignGuide.md` (table of contents + grammar + operator semantics + worked examples).

**Files**
- [Docs/UserBulb-Guide.md](UserBulb-Guide.md) — append `## 19. Sandbox DSL` section with full grammar from `SandboxBulbExpression.cs`.
- [Docs/UserBulbSandbox-DSL-Reference.md](#) — new, standalone reference (linked from the guide). Lists every builtin function, arity, vec/scalar overload rules, operator precedence, let-binding semantics.
- ViewModel `OpenHelpCommand` already routes to `UserBulb-Guide.md` — no code change needed.

**Worked examples to cover**
- Mandelbulb N=8: `triplex(z, 8) + c`
- Burning-Bulb (axis fold): chain `abs each axis → triplex`
- Mandelbox: chain `boxfold → spherefold → z * scale + c`
- Periodic space: `mod(triplex(z, 8) + c, 2)`
- Smooth blend: `let p = triplex(z, 8) in let q = z*2 in p * smin(1, n/8, 4) + q * (1 - smin(...)) + c`

---

### 4. Quat axis support in Sandbox compiler  (M, ~8 h)

Sandbox is currently Vec3-only because `SbxVal3` is a 3-component tagged union. Quat needs a 4-component variant. Two options:

**Option A — `SbxVal4` parallel type.** New tagged union {real | vec3 | quat}. New parser-side W-component literal `qvec(x,y,z,w)`. New builtins `qmul/qconj/qpow`. Parse-time mode flag drives which value tag is used for `z`/`c` slots.

- Pro: clean separation, no overhead in Vec3 mode.
- Con: ~600 LOC duplication of operators + functions.

**Option B — promote `SbxVal3` to `{real | vec4}`.** Carry a W slot always; Vec3 ops ignore W. New axis flag tells the calculator adapter whether to read W from the result.

- Pro: ~100 LOC. Vec3 ops stay identical (W=0).
- Con: Quaternion multiplication is *not* componentwise — needs a real `qmul` call site separate from `*`. Triplex still needs its own path.

**Recommendation:** Option B. Cleaner, less duplication. `*` stays Hadamard in Quat mode (matches Vec3 surprise factor — users already need explicit `qmul` to get true quaternion arithmetic). `qmul(a, b)` becomes the explicit hot path.

**Files**
- `Models/SandboxBulbExpression.cs` — widen `SbxVal3` to W field (rename to `SbxVal`?). Add `qmul`, `qconj`, `qpow`, `qvec` to the function dispatch table.
- `Models/Quat.cs` — likely already has the helpers; reuse.
- `Calculators/UserBulbCalculator.CompileSandbox` — branch on `axisMode`. Build `Func<Quat,Quat,int,double[],Quat>` adapter.
- `UserBulbSelfTest.cs` — add Quat-Sandbox smoke.

---

### 5. ILGPU translator Stage 2 — Sbx AST → kernel  (L, ~16 h, biggest)

Currently `UserBulbIlgpuTranslator` is a regex validator on Roslyn source — the user's C# body is shipped through ILGPU as-is. Sandbox source is *not* C#; it is a DSL AST. Need a code emitter that walks `Sbx3Node` and produces equivalent C# (or kernel IR) for ILGPU consumption.

**Approach**

Walk the AST → emit C# expression string using `Vec3` static methods. The existing GPU compile pipeline then takes the emitted string the same way it currently takes Roslyn user source.

**Mapping table** (sketch)

| AST node | Emitted C# |
|---|---|
| `Sbx3Const{IsVec=false}` | numeric literal |
| `Sbx3Const{IsVec=true}` | `new Vec3(x, y, z)` |
| `Sbx3Slot{SlotZ}` | `z` |
| `Sbx3Slot{SlotC}` | `c` |
| `Sbx3Slot{SlotN}` | `n` |
| `Sbx3Slot{extra}` | `__p[i]` |
| `Sbx3Binary{"+"}` | `(a + b)` |
| `Sbx3Binary{"*"}` | `Mul(a, b)` (Hadamard helper — Vec3 doesn't overload `*` for vec*vec) |
| `Sbx3Binary{"^"}` | `Vec3.Pow(a, k)` if a is vec; `Math.Pow(a, k)` otherwise |
| `Sbx3Member{.x}` | `.X` |
| `Sbx3Call{"triplex"}` | `Vec3.Pow(arg0, (double)arg1)` |
| `Sbx3Call{"sin"}` (vec arg) | `new Vec3(Math.Sin(v.X), Math.Sin(v.Y), Math.Sin(v.Z))` |
| `Sbx3Call{"sin"}` (scalar arg) | `Math.Sin(s)` |
| `Sbx3Let` | C# block `{ var slot = value; return body; }` — wrap in IIFE or inline |
| `Sbx3Ternary` | `(c ? t : e)` |

**Open problems**

- **Type inference for overloaded sin/cos/etc.** The DSL infers vec-vs-scalar at runtime via `SbxVal3.IsVec`. The emitter must do it statically — track a `bool isVec` per sub-tree during emission. Most leaves are obvious; let-binding inference needs a pass.
- **Hadamard `*` collision with Vec3 scalar-mul.** Vec3 currently exposes `Vec3 * double` for broadcast but no `Vec3 * Vec3`. Emitter must always route vec*vec through an explicit `Vec3.Hadamard(a, b)` helper (which probably needs to be added).
- **Chain emission.** Each chain step becomes a separate emitted C# method; the wrapper builds `ctx.Set("name", result)` between calls. Already wired for the Roslyn-chain path; mirror it for Sandbox.

**Files**
- New: `Calculators/UserBulbSandboxEmitter.cs` — walker.
- Modify: `Calculators/UserBulbCalculator.cs` — when Backend=GPU AND Compiler=Sandbox, call emitter, send emitted C# through existing GPU pipeline.
- Modify: `Models/Vec3.cs` — add `Hadamard(Vec3, Vec3)` if not present.
- New: `Models/Vec3GpuOps.cs` — kernel-side device-callable mirrors of `triplex/rot/boxfold/spherefold/etc.` if ILGPU can't see the existing CPU statics. (TBD; current Roslyn-GPU path already uses `Vec3.Pow` device-side so most should JIT.)

**Test**
- Self-test: emit `triplex(z, 8) + c` AST → string, assert it parses + compiles + runs on GPU, hits ≥ CPU baseline within 5%.
- Validation: emitter rejects nodes it cannot handle (unsupported builtins) instead of emitting garbage. Caller falls back to CPU interpreter on rejection.

---

## Phase summary

| Item | Size | Risk | Order | Why this order |
|---|---|---|---|---|
| 1. Explicit Square detect | S | low | 1st | Tiny, unblocks AnalyticDE parity. |
| 2. UI dialog cleanup | S | low | 2nd | Cheap UX fix, unblocks (4) gating. |
| 3. User guide doc | M | low | 3rd | Locks the Stage-1 contract before code churn. |
| 4. Quat support | M | medium | 4th | Coupled to (2) for the disable-gate flip. |
| 5. ILGPU emitter | L | high | 5th | Biggest perf wins, biggest design risk. |

---

## Test rubric (applies to every item)

1. Build clean.
2. `dotnet run -- --ubtest` self-test passes, all 4 existing scenarios still green.
3. New scenario added under [UserBulbSelfTest.cs](../UserBulbSelfTest.cs) for the new path.
4. Manual smoke render in Avalonia shell (`dotnet run`) — confirm no visual regression on a saved preset.
5. Performance: no item is allowed to slow `triplex(z, 8) + c` Sandbox path below the 134 ms Stage-1 baseline.

---

## Out of scope (intentional)

- Removing the Roslyn compile path. Roslyn stays canonical for unrestricted users.
- DSL extensions beyond the existing function table. New builtins land via their own design notes, not this plan.
- Cross-compiling the DSL to other targets (WebGL, CUDA-direct, SPIR-V). ILGPU is the only backend.
