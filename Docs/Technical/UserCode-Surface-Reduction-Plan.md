# User-Code Surface Reduction Plan (#27)

Status: **active** — Phase 0 complete (0a/0b/0c); Phase 1 complete (1a/1b/1c);
Phase 2 next.
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

### Phase 2 — 3D DSL parity + retire UserBulb Roslyn
- **2a** Gap-audit `SandboxBulbExpression`/emitter vs every Roslyn Vec3/Quat/
  Chain idiom; add missing DSL forms or document as unsupported. Commit.
- **2b** Migrate all built-in `UserBulbStore` presets from Roslyn C# to DSL
  syntax (respecting read-only built-in equations — replace the shipped source,
  don't force it onto a user's saved copy). Commit.
- **2c** Flip `FractalParameters.UserBulbCompiler` default → `Sandbox`. Commit.
- **2d** Delete `WrapUserSource{,Quat,Chain}` + the Roslyn branch in
  `UserBulbCalculator.Compile`. Parity harness for 3D. Commit.

### Phase 3 — remove the raw-C# user-code path entirely
- **3a** Delete `UserEquationCalculator`'s raw Roslyn path (fold the type onto
  the DSL engine). Commit.
- **3b** Confirm no remaining user-text → Roslyn slot. CalcGen/ColorGen
  hot-load compile machine-generated source from a validated AST, not user text
  — audit + assert. Commit.
- **3c** Keep `FractalTypeAllowlist` as defense-in-depth. Regression sweep. Commit.

### Phase 4 — ColorGen to a full interpreted DSL
Goal: the ColorGen DSL must be able to express **every** rich color theme with
**no Roslyn codegen and no assembly load at runtime**.
- **4a** DSL richness audit: enumerate what the built-in / hardcoded C# themes
  do (multi-stop gradients, HSV/HSL/OkLab/OkLCh, cosine palettes, orbit-trap
  inputs, smooth-iteration inputs, normals, brightness/contrast/gamma) and
  ensure the DSL surface + input set covers all of it. Add any missing builtin
  functions / input slots. Commit.
- **4b** Build a `CgProgram` interpreter (`CgNode.Eval` over a scalar/`Cg3`
  value union, mirroring `SbxNode.Eval`) — pure, no BCL surface beyond `Math`,
  no codegen, no `AssemblyLoadContext`. Reuse the existing `Cg3`/`CgScalar`
  runtime helpers. Commit.
- **4c** Make the interpreter the runtime path for custom themes; retire
  `ColorGenHotLoad` (Roslyn) from the theme hot path. Keep the C#-emitter only
  as an optional "export theme to source" convenience, clearly out of the
  render path. Commit.
- **4d** Parity harness: interpret vs previously-compiled output for a theme
  corpus; assert per-pixel color equivalence. Commit.

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
