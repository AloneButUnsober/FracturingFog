# User Bulb 3D and the equation language: scope (#1091)

Status: **decision recorded** (#1091); **#1100 implemented** (parser surface, `norm`, migration). This follows on from #937, which unified the
**2D** equation language (spec: [Equation-Language.md](Equation-Language.md)). This
note decides how the **User Bulb 3D** language relates to it. The agreed work is filed as #1100, #1101 and #1102 ([Follow-ups](#follow-ups)).

## Decision in one paragraph

**Share the syntax, keep the 3D value types.** User Bulb keeps its own value kinds
(real / vec3 / quat), its own functions, the chain model and its AST (`Sbx3Node`).
Everything downstream reads that AST unchanged: the interpreter, the analytic-DE
pattern matcher, the C# emitter and the ILGPU GPU path.

What changes is the **front end**, which becomes the same grammar as 2D:
- precedence, including `-x^y` = `-(x^y)`;
- `if … then … else`, statements and comments;
- error format: line and column, *Did you mean*.

Saved bulbs are protected by the same version-stamp-and-rewrite migration as #1088.
`abs` stays **componentwise** on vectors, a deliberate and documented difference.

## Today

| | 2D equation language (#937) | User Bulb 3D (`SandboxBulbExpression`) |
|---|---|---|
| Parser | `Equations/SandboxExpression.cs` | `Engine/Models/SandboxBulbExpression.cs` (≈1000 lines), plus `SandboxBulbChain` |
| Value kinds | real, complex | real, vec3, quat |
| `-x^y` | `-(x^y)` (language version 2) | `(-x)^y`: `pow_expr := unary ("^" pow_expr)?`, the 2D version-1 rule |
| Unary `+` | yes | yes |
| `if … then … else` | yes | no (ternary only) |
| `let`, `?:`, `&& \|\| !`, comments | yes | yes |
| Statements (`var x = …;`, guards, `return`) | parsed natively | **not accepted in the editor.** Only the one-time startup migration (`UserBulbDslMigration` → `UserBulbSourcePreprocessor`) desugars saved C#-style bodies to `let` / ternary |
| Member access | — | `.x .y .z .w` |
| Variables | `z c n iter prev pi e i` | `z c n pi e`, plus chain step outputs, `t` and user params |
| `abs` | magnitude \|x\| (#1088) | **componentwise** on vec / quat, \|x\| on reals |
| Magnitude / squared magnitude | `abs` / `norm` | `length` / `dot(v, v)` (no `norm`) |
| Errors | `… at line L, col C. Did you mean 'sin'?` | `… at position N` (`SbxParseException` with a span), no suggestion |
| Saved-text version | `LanguageVersion` on each entry (#1088) | none |
| Downstream of the AST | interpreter, CalcGen lowering | interpreter, analytic-DE matcher (`UserBulbAnalyticDE`), C# emitter (`UserBulbSandboxEmitter`), GPU (`UserBulbSandboxGpuCompiler`) |

**What a change would touch.** Your saved bulbs, checked read-only (`userbulbs.json`, 13 entries):
- No source or chain step uses a signed base before `^`. The precedence change rewrites nothing today, but files imported later still need the safety net.
- The Menger and KIFS folds are built on componentwise `abs(z)`: the "Menger sponge step" source, and the fold steps of the "Hybrid: Menger + Mandelbulb" and "Kaleidoscopic IFS" chains.
- One entry is still C#-style ("Quaternion Drift Map - Hand Rolled Sin Exp", `Quat scaled = new Quat(…)`). The startup migration couldn't translate it, so it is left as is and doesn't render (#211). Native statements (#1100) would let it be rewritten by hand in the same shape.

The built-in examples and the bulb test corpus contain no `-x^y` either.

## The four questions

### 1. Can the operator / precedence / statement grammar be shared?

**Yes, at the syntax level.** The two grammars are already almost the same.
`SandboxBulbExpression`'s grammar is the 2D version-1 grammar plus member access.
Their parsing differs only in:
- the `^` / unary-minus rule;
- `if … then … else`;
- native statements;
- the error format.

The differences that matter are in **binding**: which identifiers and functions exist, their arity, and the value kind of each node.

**Plan:**
1. **Align the bulb parser's surface rules first** (#1100). This gives users the whole benefit (one way to write things, one error style) at low risk, because `Sbx3Node` doesn't change.
2. **Then, optionally, extract a shared front end** in `Equations/` (#1101):
   - one tokenizer and precedence parser producing a neutral syntax tree;
   - a 2D binder to `SbxNode` and a 3D binder to `Sbx3Node`;
   - member access accepted by the shared grammar and rejected by the 2D binder.

   This removes the duplicate parser, but it is a refactor with no visible gain once #1100 has landed. So it is a separate issue, and it is optional.

**Constraint for both:** `Sbx3Node` and the opcodes resolved at parse time (`SbxBinOp`, `SbxFuncId`) stay byte-for-byte the same. The analytic-DE matcher pattern-matches on that tree, and the emitter types each node from it.

### 2. Should `abs` / `norm` follow the 2D decision?

**Partly.** On **reals**, `abs` is already \|x\|, the same as 2D.

On **vectors and quaternions**, `abs` stays **componentwise**:
- That is the vector-language convention (GLSL / HLSL `abs`).
- It is what the C# and GPU emitters produce.
- It is the idiom every fold is written in: Menger, KIFS and Sierpinski all use `let v = abs(z) in …`.

Making it a magnitude would silently break those bulbs. A migration would also need a new name for the componentwise form (2D calls it `fold`), only to rename most users' folds. The magnitude of a vector stays `length(v)`.

**Add** `norm(v)`, the squared length `dot(v, v)` (and the square on reals), for symmetry with 2D.

The 2D and 3D specs both get a short "same name, different value type" note:
- 2D `abs` (complex) = magnitude;
- 3D `abs` (vector) = componentwise;
- 3D `length` = magnitude;
- `norm` = squared magnitude in both.

### 3. Does the editor adopt the #1089 layout and help structure?

**Help: yes. Layout: it already matches, with no CalcGen toggle.** The User Bulb editor is already one source box plus an optional chain; there were never two tabs. It has no CalcGen equivalent, because its accelerator is the analytic-DE badge and the GPU path, both automatic.

What it adopts (#1102):
- **Error span and quick fix:** positioned error spans with the *Did you mean* quick fix (**Ctrl+.**). It already has `ErrorSpanStart/Length`; it gains `SuggestedFix`.
- **Error colour:** errors stay `#FFCC00` (already the case).
- **Shared help:** its help panel includes the shared `HelpTextBundle.EquationGrammarText` (#1090), plus a 3D supplement for value kinds, member access, the vector functions, the chain, and `abs` on vectors.

### 4. GPU emit

The GPU path reads the AST. Because #1100 and #1101 leave `Sbx3Node` unchanged, the emitter needs no change, with one exception: `norm`, a new function, needs a mapping in the emitter and the GPU ops.

The guard that this holds:
- a **frozen golden fingerprint** of the bulb corpus, taken before any parser change, like `EquationLanguageGoldenTests` for 2D;
- the emitter output for the same corpus, checked against the interpreter. The emitter is covered today only by the `UserBulbSelfTest` CLI self-test; #1100 moves that coverage into `Server.Tests`.

## Migration (for #1100)

This is the same mechanism as #1088:
- **Legacy parse with edit recording:** `SandboxBulbExpression.ParseLegacy` records edits that preserve meaning, wrapping a signed base before `^`: `-x^y` → `(-x)^y`.
- **Self-check:** the legacy tree of the original must equal the new tree of the rewritten text, or the text is left alone.
- **Version stamp:** a `LanguageVersion` stamp on `UserBulbEntry`, and on chain steps through their entry.
- **Backup first:** `UserDataBackup.SnapshotBeforeMigration(userbulbs.json, "bulb-language-v2")`.
- **Imports:** imported bulbs and region bundles are upgraded before they are saved.

The current data needs no rewrite; the migration is a safety net for shared and imported files.

## Follow-ups

| Issue | Work | Depends on |
|---|---|---|
| #1100 | Bulb parser surface alignment: `-x^y` = `-(x^y)` with the versioned migration; `if … then … else`; native statements; `norm`; line/col errors with *Did you mean*. Golden fingerprint and emitter tests in `Server.Tests` first | — |
| #1101 | *(optional)* Shared equation front end in `Equations/`: one syntax tree, 2D and 3D binders, `Sbx3Node` unchanged | #1100 |
| #1102 | User Bulb editor and help alignment: quick fix (Ctrl+.), shared grammar help plus a 3D supplement | #1100 |

Parent: #937. Related: #211 (C#→DSL translation of saved bulbs, deferred).
