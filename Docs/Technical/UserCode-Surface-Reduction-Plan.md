# User-Code Surface Reduction Plan (#27)

Status: **active** — Phase 0 complete (0a/0b/0c); Phase 1 complete (1a/1b/1c);
Phase 2 complete (2a/2b/2c; the original hard-delete 2d folded into Phase 3);
Phase 3 complete (3a/3b/3c/3d) — **both raw-C# calculators now run DSL-only, no
Roslyn fallback anywhere**; Phase 5a complete (widen equation translation +
persist saved equations to DSL, backup-guarded); Phase 5b complete (statement
blocks in the equation DSL); Phase 4 complete (ColorGen runs on the interpreter,
**no Roslyn on the theme render path**). Phase 6 (CalcGen DSL parity, #215)
outstanding.
Tracking issues: #27 (umbrella) + per-phase children (see [Tracking](#tracking)).

## Problem

Three runtime surfaces accept user-authored text, compile it to a live .NET
assembly via Roslyn, and load it **in-process with full trust**:

| Surface | Fractal / feature | Compile site | Input today |
|---|---|---|---|
| User Equation (2D) | `UserEquation` | `UserEquationCalculator.WrapUserSource` | **raw C#** |
| User Bulb (3D) | `UserBulb` (Roslyn compiler) | `UserBulbCalculator.WrapUserSource{,Quat,Chain}` | **raw C#** |
| ColorGen theme | custom color themes | `ColorGenHotLoad.TryCompileAndLoad` | validated DSL → emitted C# → Roslyn |

The two "raw C#" surfaces string-interpolate user text directly into a class
template and compile it with `RoslynRefs.GatherAllTpaRefs()` — **every BCL
assembly referenced**. A user body may contain any statement
(`System.IO.File.Delete`, `Process.Start`, P/Invoke, reflection). This is
arbitrary code execution in the host process.

ColorGen's DSL input is already validated (lexer restricts identifiers to
`[A-Za-z_][A-Za-z0-9_]*`; function names come from a fixed emitter switch), so
there is **no injection path** through it. Its residual concern is
architectural: it still round-trips through Roslyn codegen + a collectible
`AssemblyLoadContext` at runtime — a heavy, RCE-adjacent mechanism to keep on
the theme hot path.

## Risk assessment

- **Mechanism:** raw-C# paths are a textbook RCE primitive. No sandbox; full
  process privileges.
- **Network vector: already closed.** `Server/Guard/FractalTypeAllowlist.cs`
  refuses `UserEquation`, `Sandbox`, `UserBulb` over RPC. Keep it as
  defense-in-depth even after the surfaces are made safe.
- **Residual real risk = untrusted content files.** A shared region / preset /
  scene JSON (or a `.cs` under `%LOCALAPPDATA%/FracturingFog/UserCalculators/`
  auto-loaded at startup) can carry a hostile `UserBulbSource` /
  `UserEquationSource`. Opening the file triggers a **lazy compile on render** —
  no explicit "Compile" click required — so code runs on open.
- **Exposure today: low-to-medium** (desktop app, content is user-supplied),
  **rising** if region / scene / theme sharing becomes a product feature.

## Safe machinery already in the codebase

- **2D:** `SandboxExpression` (`Engine/Models/SandboxExpression.cs`) — no-BCL
  interpreted DSL: `+ - * / ^`, comparisons, ternary, `let..in`, functions
  `sin cos tan sinh cosh tanh exp log sqrt abs conj re im arg pow`, constants
  `z c n pi e i`. Lexer throws on any unknown identifier. Driven by
  `SandboxCalculator` (mirrors `UserEquationCalculator`'s pixel loop).
- **3D:** `SandboxBulbExpression` + `UserBulbSandboxEmitter` — rich vec/quat DSL
  (`vec qvec triplex length dot cross normalize rot boxfold spherefold mod smin
  clamp qmul qpow qexp…qcoth` + scalar math). Vec3 **and** Quat modes; CPU
  interpret **and** GPU emit.
- **ColorGen:** `CgProgram` AST + `ColorGenEmitter` — rich color DSL
  (`hsv hsl oklab oklch palette cosine brightness contrast gamma mix hash` +
  scalar math). Today it *emits C# and compiles*; it has no interpreter yet.

The reduction is therefore mostly **make the safe path the only path** + close
DSL feature gaps so no existing artwork regresses.

## Trust boundary (Phase 0 core concept)

Not every compile is untrusted. Define `UserCodeOrigin`:

- **Interactive** — user is in the editor and clicked Compile. Trusted.
- **BuiltIn** — app-shipped preset from `UserBulbStore` / built-in themes.
  Trusted (ships in the binary; all current built-in bulb presets are Roslyn
  C#, so they must keep working).
- **ExternalFile** — source arrived by loading a region / scene / preset /
  theme file, or the startup persisted-calculator scan. **Untrusted.**

Policy (`UserCodeSecurityPolicy`, env-overridable):

| Origin | Roslyn raw-C# compile | DSL interpret |
|---|---|---|
| Interactive | allow | allow |
| BuiltIn | allow | allow |
| ExternalFile | **deny** (default) → error, no execution | allow |

Env `FF_ROSLYN_USERCODE = allow-all | trusted-only(default) | deny-all` is the
global override / kill-switch. `trusted-only` closes the file-borne RCE while
leaving interactive editing and shipped presets untouched → **no regression.**

## Phases

Each phase = one child issue, its own branch, **one commit per sub-phase**, one
PR at phase completion. Ship in order.

### Phase 0 — trust-boundary gate (immediate de-risk, non-regressing)
- **0a** `UserCodeSecurityPolicy` + `UserCodeOrigin` + single chokepoint
  `UserCodeGate.EnsureRoslynAllowed(origin)`. Both raw-C# calculators route
  their Roslyn compile through it. Default `trusted-only`; calculators default
  to `Interactive` origin (no behavior change yet). Unit tests for the policy
  matrix. Commit.
- **0b** Thread `UserCodeOrigin.ExternalFile` from the region / scene / preset
  load paths and the startup persisted-`.cs` scan into the compile calls. Add a
  test that a hostile external source is refused (no assembly emitted) under the
  default policy. Commit.
- **0c** User-facing surfacing: when a compile is denied, `LastError` explains
  the block and points at the DSL. Yellow (`#FFCC00`) advisory in the editor —
  **not red** (red/green colorblind users). Commit.
  *Done: the deny reason flows `calc.LastError → CompileUser{Equation,Bulb} →
  vm.ShowError`, rendered by the editor's existing `Classes.error` → `#FFCC00`
  status style (never red). Contract pinned by a test.*

### Phase 1 — 2D DSL parity + fold UserEquation onto the DSL — **complete**
- **1a** ✅ Closed the `SandboxExpression` math gap vs `Complex`/`Math`: added
  `asin acos atan asinh acosh atanh` (real inside the principal real domain,
  complex continuation outside — the inverse-hyperbolic complex branches use
  log/sqrt identities since BCL `Complex` lacks them), per-component
  `floor sign`, real-valued `atan2 min max clamp`, and centered per-component
  `mod` (matches `Vec3.Mod` so 2D and 3D share one meaning). 28 unit tests.
- **1b** ✅ `UserEquationCalculator` is DSL-first: `EquationPreprocessor`
  (reused via a new Engine → `CalculatorGen.Lib` reference) translates the C#
  `Complex.*` source; when it translates + parses it runs on `SandboxExpression`
  (no Roslyn, no BCL, no assembly load). Roslyn stays only as a fallback for
  sources with no DSL form, still gated by origin — so an untrusted equation the
  DSL can express now *executes safely* instead of being refused, and trusted
  editing never regresses. Untranslatable sources surface a crisp error and stay
  editable. `UsingDsl` exposes which path ran.
  *Interim note:* translatable equations now run interpreted rather than
  JIT-native (a per-step slowdown), accepted as the direction toward Phase 3.
- **1c** ✅ Parity harness (`UserEquationDslParityTests`): a 16-equation corpus
  proves the translated DSL step equals the identical C# expression evaluated
  natively (= the old Roslyn semantics) within relative 1e-10 over a (z, c, n)
  grid; a render-level check confirms `UserEquationCalculator` (DSL) and
  `SandboxCalculator` emit bitwise-identical pixel buffers.

### Phase 2 — 3D DSL parity + make the bulb DSL primary — **complete**
- **2a** ✅ Gap-audited `SandboxBulbExpression` vs every built-in preset idiom:
  all math has a DSL form (`new Vec3`→`vec`, `Vec3.Fn`/`Math.Fn`→lowercase
  builtins, `.X`→`.x`, `^`/`triplex`/`boxfold`/`spherefold`/`rot`/`abs*`
  present); imperative `var`/`if` map to `let..in`/ternary. The one gap was
  comments — added `//` and `/* */` skipping. `SandboxBulbDslAuditTests` makes
  it executable (DSL == native `Vec3` math over a grid).
- **2b** ✅ Migrated all built-in `UserBulbStore` presets and chain primitives
  from raw C# to DSL and pinned each to `Compiler = Sandbox` in its `Settings`
  snapshot. `MigrateBuiltinsToDsl()` upgrades a pre-2b `userbulbs.json` only
  when the stored source still exactly matches the shipped C# (or the new DSL
  awaiting a pin), so a user's own edit is preserved (read-only built-in
  contract). ("Cosh × Sin bulb" used `Vec3*Vec3` — no operator, never compiled
  under Roslyn — the DSL Hadamard repairs it.)
- **2c** ✅ Flipped `FractalParameters.UserBulbCompiler` default → `Sandbox` and
  made `UserBulbCalculator.Compile` DSL-first with a **trusted Roslyn
  fallback**: a DSL parse failure falls through to the gated Roslyn path only
  for a trusted origin and only when the body looks like C#; a DSL typo keeps
  its DSL error; untrusted C# is refused with the gate's block notice. So no
  user-authored C# bulb breaks, and the file-borne surface is not widened.
- **~~2d~~** (original hard-delete) → **folded into Phase 3.** Per the
  "keep trusted fallback" decision, `WrapUserSource{,Quat,Chain}` stays as the
  trusted-origin fallback and is deleted alongside the UserEquation raw path in
  Phase 3, not here.

### Phase 3 — remove the raw-C# user-code path entirely — **complete**
Covered **both** raw-C# calculators (UserEquation from Phase 1, UserBulb from
Phase 2 — both had kept a trusted-origin Roslyn fallback until here).
- **3a** ✅ Deleted `UserEquationCalculator`'s raw Roslyn path (`WrapUserSource`,
  the `CSharpCompilation` / `AssemblyLoadContext` branch, the `_compiled`
  delegate + pinned context, the origin gate). The type runs on
  `SandboxExpression` only; a source with no DSL form surfaces an editable error
  pointing at the DSL instead of executing.
- **3b** ✅ Deleted `UserBulbCalculator`'s `WrapUserSource{,Quat,Chain}` + the
  full-BCL Roslyn branch + the Phase-2c trusted fallback (`LooksLikeCSharp` /
  `ChainSourceText` probes) + the origin gate + the `GatherRefs` helper. The
  type runs on `SandboxBulbExpression` / `SandboxBulbChain` only; the persisted
  `UserBulbCompiler` selector is ignored and the now-dead "Roslyn (full C#)"
  editor dropdown was retired (view-model pins Sandbox). Added
  `UserBulbDslRenderParityTests` (the old 2d harness): every seeded built-in
  compiles on the interpreter, a representative DSL bulb renders
  deterministically + non-blank end-to-end, and a raw-C# body no longer
  compiles. (Per-step DSL-vs-native math parity stays covered by
  `SandboxBulbDslAuditTests`.)
- **3c** ✅ Audited the remaining runtime Roslyn sites (`CalculatorGenHotLoad`,
  `ColorGenHotLoad`, and the Sandbox-GPU emitters): each compiles only the
  source its generator emits, and `CalculatorGenApi.Generate` /
  `ColorGenApi.Generate` gate that on `EquationParser` / `ColorGenParser` —
  restricted grammars that reject any construct outside the DSL. Raw user text
  embedded in the generated file (an `EQUATION_SOURCE` token; a per-line `//`
  comment block, never a `/* */` block) can't break out because the parse
  refuses it first. `NoRawUserTextReachesRoslynTests` asserts 12 injection-shaped
  inputs are refused by both generators while benign DSL still generates.
- **3d** ✅ Kept `FractalTypeAllowlist` as defense-in-depth (still blocks
  UserEquation / Sandbox / UserBulb over RPC — they run user step math + open
  iteration budgets even though the RCE primitive is gone). Full regression
  sweep green (Server.Tests 867/867), solution builds clean.

### Phase 5a — migrate saved user equations to the DSL — **complete**
Follow-up to Phase 3: after the raw-C# path was removed, a *saved* raw-C#
equation with no DSL form stopped running. This widens the translation surface
and persists translatable saved equations as DSL so shipped/user content keeps
working. Issue #209 (PR #210, stacked on #208).
- **5a-1** ✅ Extended `EquationPreprocessor` to translate the C# `Complex`
  member accessors it used to reject (they had DSL equivalents; only the
  syntax rewrite was missing): `x.Real → re(x)`, `x.Imaginary → im(x)`,
  `x.Phase → arg(x)`, `x.Magnitude → sqrt(x*conj(x))`. Magnitude deliberately
  avoids `abs`, whose meaning **differs** between the CalcGen DSL (|x|²) and the
  `SandboxExpression` runtime (|x|); `x*conj(x)`=|x|² in both and sqrt of that is
  |x| (the parity harness — which evaluates via `SandboxExpression` — caught the
  divergence). Operand extraction covers identifier / paren-group / call-result.
  `UserEquationDslParityTests` gains a member-access corpus (DSL == native
  `Complex` within 1e-10). **Near-misses also closed:** `Complex.Divide(a,b)` →
  `((a)/(b))` (preprocessor); `SandboxExpression` now resolves constants
  case-insensitively (`E`/`PI`), skips `//` and `/* */` comments, and tolerates
  a single trailing `;` — so saved equations using those forms translate. Bare
  Math statics (`Sin`/`Pow`/…) already worked (the call parser lowercases).
- **5a-2** ✅ `UserDataBackup.SnapshotBeforeMigration` — a timestamped
  `<name>.<stamp>.<reason>.bak` snapshot taken before a store rewrites a user
  JSON in place, distinct from `AtomicFile`'s rolling `.bak`. Retrofitted the
  existing `UserBulbStore` built-in migration to snapshot first.
- **5a-3** ✅ On startup (after `UserEquationStore.Load()`, via
  `AvaloniaShellBootstrap`), `UserEquationDslMigration.Run` converts translatable
  `Kind=UserEquation` entries to DSL text + `Kind=Dsl` — same
  translate-then-validate (`EquationPreprocessor` → `SandboxExpression.Parse`)
  the live calculator runs. `UserEquationStore.MigrateUserEquationsToDsl(translate)`
  owns the file + backup + save (translation injected because the store is
  UI-free); idempotent; untranslatable entries left editable. The migration lives
  in Engine (needs both the preprocessor and the interpreter, which Abstractions
  doesn't reference).

### Phase 5b — statement blocks in the equation DSL (planned, #212)
The saved equations still erroring after 5a are C# **statement blocks**
(`var`/`if`/multi-statement/`return`). The DSL already has `let..in` (= `var`)
and ternary (= `if`), so this is a **front-end** parser extension — desugar
`T x = e; …`, `x = e; …`, `if (c) x = e; …`, `return r;` to nested `let`/ternary
— not a capability or security change (still pure, no BCL, no loops). One hard
case out of scope: Phoenix-style maps need the previous `z` carried between
iterations, which the `(z, c, n)` step signature can't supply (needs a `prev`
slot — separate). Branches off `main` after #208 + #210 merge.

### User Bulb C#→DSL translator (deferred, #211)
No C#→DSL translator exists for `Vec3`/`Quat` bulb bodies (Phase 2b only swapped
built-in strings). A bulb analogue of `EquationPreprocessor` + a backup-guarded
startup migration would give saved user bulbs the same DSL conversion equations
got. Deferred; soft-depends on Phase 5b (shared statement-block support).

### Phase 4 — ColorGen to a full interpreted DSL — **complete (#204)**
Goal: the ColorGen DSL runs with **no Roslyn codegen and no assembly load at
runtime**. Branch `feat/usercode-phase4-204` off `main`.
- **4a** ✅ DSL richness audit: the ColorGen DSL already covers every rich-theme
  idiom — multi-stop `palette`, `hsv/hsl/oklab/oklch`, IQ `cosine`,
  `brightness/contrast/gamma`, `mix`/`mix_oklab`, the full scalar-math set, and
  all 15 render inputs (`smooth dist iter maxIter t nx ny zr zi dzr dzi arg mag
  isInSet pxScale`) + constants `pi tau e phi`. No missing builtins; the audit
  reduces to "the interpreter must cover every `ColorGenEmitter` case", which the
  parity harness enforces.
- **4b** ✅ `InterpretedColorMap` (Engine) parses a source to a `CgProgram` once
  and walks the typed AST per pixel over a scalar/`CgRgb` value union, mirroring
  `ColorGenEmitter`'s per-node C# exactly, against a compiled `CgRgb`/`CgMath`
  runtime (`Engine/Models/ColorGenRuntime.cs`) ported verbatim from the template's
  nested `Cg3`/`CgScalar`. Pure, no `Math` beyond the template's, no codegen, no
  `AssemblyLoadContext`. Let-bindings use per-thread slot scratch (no per-pixel
  alloc). Engine gains a `ColorGen.Lib` ProjectReference (leaf; mirrors
  `CalculatorGen.Lib`; no cycle).
- **4c** ✅ The interactive ColorGen editor's "Compile & Load"
  (`AvaloniaShellBootstrap.OpenColorGenEditor` → `HotLoadRequested`) now calls
  `InterpretedColorMap.TryCreate` instead of `ColorGenHotLoad.TryCompileAndLoad`
  — **no Roslyn on the theme runtime path**. `InterpretedColorMap` still
  implements `IGpuHlslPalette` (HLSL via `ColorGenHlslEmitter` — text, not Roslyn)
  so GPU SP palettes are unaffected, and `IColorMapHandlesInSet` so `iter`/
  `isInSet` keep their meaning. `ColorGenApi.Generate` stays for the "Generate to
  file" export (writes `.cs` for a future build — off the render path);
  `ColorGenHotLoad` is retired from the hot path (kept for the parity test + the
  export/CLI).
- **4d** ✅ `ColorGenInterpreterParityTests`: a 10-theme corpus spanning the whole
  DSL surface is both Roslyn-compiled and interpreted; over a grid of sample
  inputs (exterior + in-set, tilted normals, non-trivial final state) the two
  `Map()` results are **bit-identical**. Suite 916/916.

## Correctness guarantee (requirement 3 — "any and all fractal math")

Every phase that retires a Roslyn path first ships the DSL features that path
depended on, then proves equivalence with a render-level parity harness gated in
CI. Existing self-tests (`UserBulbSelfTest.cs` exercises Sandbox vs Roslyn
per-mode) fold into those harnesses. No equation, bulb, or theme regresses
because the safe path replaces the unsafe one only after parity is demonstrated.

## Tracking

- Umbrella: #27.
- Children: one issue per phase (0–4). Each PR body carries an explicit
  `Closes #<n>` line per the repo's auto-close convention (one number per line;
  no ranges).
