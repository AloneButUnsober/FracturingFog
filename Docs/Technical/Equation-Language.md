# Equation Language — Specification

Status: **#937 complete** (#1086 – #1090). This is the one 2D equation
language behind the User Equation editor, the Sandbox editor, the
z0-seed and bailout-condition fields, and (since #1087) CalcGen's Compile &
Load / Generate, which lower this language's AST into CalcGen's.

| Issue | Slice |
|---|---|
| #937 | Umbrella: one syntax, one editor |
| #1085 | `if … then … else` on the interpreter, `norm()` |
| #1086 | **This spec**, one parser and one AST |
| #1087 | CalcGen lowering from this AST |
| #1088 | Saved-source migration (`abs` and `-x^y` decisions below) |
| #1089 | Single User Equation editor |
| #1090 | Documentation sweep |

**Language version 2** (#1088): `-x^y` means `-(x^y)`, and `abs` means \|x\|
everywhere. Saved text is upgraded automatically; see
[Versions and migration](#versions-and-migration).

## Where it lives

| What | Where |
|---|---|
| Syntax (shared with User Bulb 3D, #1101) | `Equations/EquationFrontEnd.cs` |
| 2D builder, AST, interpreter | `Equations/SandboxExpression.cs` |
| Entry point | `Equations/EquationLanguage.cs` |
| Saved-text upgrade | `Equations/EquationMigration.cs` |
| Lowering to CalcGen | `CalculatorGen/Language/CalcGenLowering.cs` |

The language lives in the dependency-free **FracturingFog.Equations** project
(#1088). Abstractions (the equation stores) and CalculatorGen.Lib (the lowering)
both reference it. The namespace stays `FracturingFog.Models`.

**One parser for both languages (#1101).** `EquationFrontEnd<T>` owns the syntax:
tokens, comments, precedence (current and version 1, with the migration's edit
recorder), statements, `let`, `if … then … else`, the ternary, member access
where the language allows it, and the syntax errors. It builds each engine's own
AST through an `IEquationBuilder<T>`:
- the 2D builder (in `SandboxExpression`) supplies complex values, the slots
  `z c n prev iter`, the constants `pi e i`, the function table and the
  version-1 condition rule;
- the 3D builder (in `Engine/Models/SandboxBulbExpression.cs`) supplies real /
  vec / quat values, `.x .y .z .w`, the bulb function table, params and chain
  scope.

There is no intermediate tree, so errors come in the same order as before and the
trees are unchanged; `ParserBehaviourSnapshotTests` pins both languages' trees,
migration edits and error messages against a baseline taken from the old
hand-written parsers.

**Entry point (`EquationLanguage`):**
- `Parse`
- `TryParse`
- `ToSExpression`, which prints the AST for tests and diagnostics

**Tables:**
- `SandboxExpression.FunctionNames`
- `SandboxExpression.FunctionArity`
- `SandboxExpression.BuiltInIdentifiers`

The netstandard2.0 source generator (`CalculatorGen.SourceGen`) does not compile
`Language/`.

## Grammar

```text
program   := block
block     := "return" expr ";"?
           | "if" "(" expr ")" "return" expr ";"? block       ; guard
           | "if" "(" expr ")" IDENT "=" expr ";"? block      ; seed
           | TYPE? IDENT "=" expr ";"? block                  ; declare / reassign
           | expr ";"?
expr      := "let" IDENT "=" expr "in" expr
           | "if" or_expr "then" expr "else" expr             ; CalcGen form
           | ternary
ternary   := or_expr ("?" expr ":" expr)?                     ; right-assoc
or_expr   := and_expr ("||" and_expr)*
and_expr  := not_expr ("&&" not_expr)*
not_expr  := "!" not_expr | cmp_expr
cmp_expr  := add_expr (("<"|">"|"<="|">="|"=="|"!=") add_expr)?   ; non-assoc
add_expr  := mul_expr (("+"|"-") mul_expr)*
mul_expr  := unary (("*"|"/") unary)*
unary     := "-" unary | "+" unary | pow_expr
pow_expr  := primary ("^" unary)?                             ; right-assoc
primary   := NUMBER | IDENT | IDENT "(" args ")" | "(" expr ")"
TYPE      := var | Complex | double | int | float | long | decimal  (ignored)
```

**Comments and terminators:**
- `//` comments run to the end of the line, and `/* */` comments are blocks.
- A single trailing `;` is allowed.

**Statements:**
- The statement forms desugar to `let` / ternary.
- Assignment is shadowing, not mutation: the language is pure, has no loops, and
  always terminates.

### Precedence (tightest first)

| Level | Operators | Associativity |
|---|---|---|
| 1 | `^` | right (its exponent may carry a sign: `z^-2`) |
| 2 | unary `-` `+` | prefix |
| 3 | `*` `/` | left |
| 4 | `+` `-` | left |
| 5 | `<` `>` `<=` `>=` `==` `!=` | none (one per operand pair) |
| 6 | `!` | prefix |
| 7 | `&&` | left |
| 8 | `\|\|` | left |
| 9 | `?:`, `if … then … else`, `let … in` | right |

> **`-x^y` means `-(x^y)`** (#1088), the conventional reading: `-z^2 + c` is
> `−z² + c`. Language version 1 read it as `(-x)^y`; saved text was rewritten to
> `(-x)^y` so it renders the same.

## Values, identifiers, constants

Every value is either real or complex; real values stay real where possible.

| Name | Meaning |
|---|---|
| `z` | The current iterate |
| `c` | The pixel / parameter |
| `n`, `iter` | Iteration index (real) |
| `prev` | The previous iterate z<sub>n−1</sub> (0 before the first step) |
| `pi`, `e`, `i` | Constants (case-insensitive: `PI`, `E`, `I`) |

Let and statement locals shadow everything, including the constants.

## Functions

| Arity | Functions |
|---|---|
| 1 | `sin cos tan sinh cosh tanh exp log sqrt sqr abs norm conj re im arg asin acos atan asinh acosh atanh floor sign fold fract round ceil trunc` |
| 2 | `pow atan2 min max mod` |
| 3 | `clamp` |

**Function names** are case-insensitive (`CLAMP(z, -1, 1)`).

**Meanings:**
- `abs(x)` = \|x\| and `norm(x)` = \|x\|².
- `fold(x)` = (\|Re\|, \|Im\|).
- `mod` is centred and per component.
- `atan2`, `min`, `max` and `clamp` are real-valued.
- The per-component functions (`floor`, `round`, …) apply to Re and Im independently.

### `abs` and `norm`

`abs(x)` is the magnitude \|x\| everywhere, conditions included. `norm(x)` is
the squared magnitude \|x\|², the cheap bailout-style threshold
(`if norm(z) > 4 …` is \|z\| > 2).

Language version 1 read a comparison operand that was directly `abs(x)`,
inside an `if … then` condition, as \|x\|². That was CalcGen's old condition
shorthand, adopted by #1085. #1088 retired the rule and rewrote saved text to
`norm(x)`.

**User Bulb 3D** uses its own value types (real / vec3 / quat). There, `abs` is
**componentwise** on vectors, as in GLSL; it is the idiom every fold is written
in. `length` is the magnitude, and `norm` (#1100) is the squared length. The
3D parser is being aligned to this grammar's surface rules: see
[UserBulb-Language-Alignment.md](UserBulb-Language-Alignment.md) (#1091).

## Errors

Every parse error names the construct and its position in CalcGen's format:

```text
Unknown function 'sni' at line 1, col 7. Did you mean 'sin'?
Unknown identifier 'prve' at line 2, col 5. Did you mean 'prev'?
Expected 'else' after the 'then' branch at line 1, col 20.
```

**Did-you-mean:** suggests the closest built-in, function or in-scope local
within edit distance 2. The editor's error-span and quick-fix code parses this
format.

## Engines

| Engine | Accepts | Notes |
|---|---|---|
| Interpreter (live view, poster, batch, Sandbox, seed and bailout) | The whole language | `double` precision; exact `dz/dc` for holomorphic trees |
| CalcGen (Compile & Load / Generate) | The whole language, lowered by `CalcGenLowering` (#1087), except the refusals below | SIMD, deep zoom (DD/QD), perturbation / SA where the maths allows |

The compile-time source generator (`[GeneratedCalculator("…")]` built-ins)
still uses CalcGen's own parser. That dialect is a subset of this language and
lowers to the identical CalcGen tree (`CalcGenLoweringTests`).

### Lowering to CalcGen (#1087)

**Bindings and conditions:**
- **`let` and statement bindings** are inlined; a bound value is shared by
  reference.
- **Expanded-size cap:** the expanded tree is capped at
  `CalcGenLowering.MaxNodes` (4000) terms, because the distance-estimate
  derivative grows with it. Over the cap, CalcGen refuses with a reason and the
  interpreter still renders the equation.
- **`?:`, `if … then … else`, `&&`, `||`, `!` and comparisons** become CalcGen
  `If` over `Cmp` / `CondAnd` / `CondOr` / `CondNot`.
  - Scalar code emits C# `&&` / `||` / `!`; SIMD code combines the per-lane
    masks with `&` / `|` / `~`.
  - A comparison used as a value is `If(cond, 1, 0)`.
  - A value used as a condition is `norm(x) != 0`.

**Powers:** `^` with a literal integer exponent from 0 to 64 is CalcGen's
integer `Pow`; any other exponent, and `pow()`, is `PowC`.

**Real and complex values:** the interpreter reads a value through *AsReal*: the
value itself when real, its **magnitude** when complex. The lowering tracks each
value's kind, so:
- **Comparisons, `min` / `max` / `clamp` / `atan2` and the `mod` period** read a
  complex operand through `abs()`.
- **`mod`** is the interpreter's centred, per-component modulo
  `x − p·floor(x/p + ½)`.
- **Meaning change:** CalcGen's old `Mod` was a truncated real remainder, and
  its old `min` / `max` / `clamp` / `atan2` read the real part of a complex
  operand. Compile & Load now matches the live view for these.
- **Refused when the kind depends on the value:** for example `log` / `sqrt` /
  `pow` / `asin` / `acos` / `acosh` / `atanh` of a real, or a `?:` mixing
  kinds, when such a value is read as a real. Wrap it in `re()`, `im()`,
  `abs()` or `norm()` to say which you mean.

### Parity: what "the same rendered result" means

The two engines differ in smooth colouring, the bailout loop's floating-point
association, SIMD rounding and DD/QD precision. Bit-exact output is therefore
not the target. Instead, at shallow zoom with the same bailout radius:
- the **in-set mask** agrees on ≥ 98 % of pixels;
- the **escape iteration count** agrees exactly on ≥ 95 % of the pixels both
  engines escape.

This is checked on a corpus covering every construct, on both the AVX2 and the
scalar CalcGen paths (`CalcGenLoweringTests`). Deep zoom differs by design,
because the interpreter is `double`-only.

## Verification

- **`EquationLanguageGoldenTests`:** a frozen fingerprint captured from the
  pre-refactor parser. It covers parse success and the exact bits of `EvalStep`
  and `EvalStepD` over a (z, c, n, prev) grid for every corpus: the cookbook,
  both parity corpora and grammar coverage.
- **`EquationLanguageGrammarTests`:** parse trees for the precedence table
  above, plus error messages and positions.

## Versions and migration

| Version | Saved | `-x^y` | Condition `abs(x)` in `if … then` |
|---|---|---|---|
| 1 | before #1088 (no version recorded) | `(-x)^y` | \|x\|² |
| 2 (current) | #1088 on | `-(x^y)` | \|x\| (\|x\|² is `norm`) |

`EquationMigration.UpgradeFromVersion1` reads text with the version-1 rules
(`SandboxExpression.ParseLegacy`), which records the edits that keep its
meaning:
- a signed base before `^` is wrapped: `-x^y` → `(-x)^y`;
- that condition `abs` is renamed to `norm`.

**Self-check:** the version-1 tree of the original must equal the version-2 tree
of the result, or the text is left unchanged and the reason reported. Text that
doesn't parse is left as is: C#-style sources awaiting translation, and typos.

**Where it runs:**

| Data | How |
|---|---|
| `userequations.json`, `sandboxequations.json` | Each entry carries `LanguageVersion`; entries without it are version 1. The store upgrades them on `Load`, after a timestamped `*.equation-language-v2.bak` snapshot (`UserDataBackup`), and stamps them. A second load is a no-op. Entries made in code are always the current version. |
| Imported equations and region-export bundles | Carry the file's version; legacy entries are upgraded before they are saved. |
| Persisted hot-loaded calculators (`UserCalculators/*.meta.txt`) | A `<Class>.lang` marker records the version. An unmarked `.meta.txt` is upgraded on startup (the old text kept as a `.bak`), then marked. |
| Regions and scenes | Reference equations by name, so they follow the store. |
| CalcGen's own parser (`EquationParser`: built-in `[GeneratedCalculator]` equations, importers) | Uses the same rules. The one built-in `if abs(z) > 4 …` became `if norm(z) > 4 …`. |

C#-style sources are translated at use time. The translator writes `(x)^k`, so
`-Complex.Pow(z, 2)` now reads as its C# meaning, `-(z²)`; version 1 misread it
as `(-z)²`.

**Verification:**
- `EquationMigrationTests`: rewrite cases, and meaning preserved against the
  version-1 parser on a grid; store, import and persisted upgrades, with
  backups, idempotence, and current-version text left untouched.
- `EquationLanguageGoldenTests`: every corpus source, upgraded and then read
  with the version-2 rules, still matches the fingerprint frozen from the
  pre-#1086 parser.

## One source, one flag (#1088 part B)

A User Equation is **one source** in this language plus a **"use CalcGen"
flag**. That replaces the old pair of sources (C#-style tab, DSL tab) and the
active-tab index.

| Where | Field |
|---|---|
| Saved entry (`UserEquationEntry`) | `Source`, `UseCalcGen` |
| Live / batch parameters (`FractalParameters`) | `UserEquationSource`, `UserEquationUseCalcGen` |
| Promoted type (`RegisteredFractal`) | `Source`, `UseCalcGen` |

- **The flag never changes the image.** The interpreter renders the source either
  way (live, poster, batch, relief twins). The flag routes the editor and
  Compile & Load / Generate. Batch and the Command builder need nothing new: an
  equation travels by region name (`--region`), and the region recall
  (`FractalRegion.ApplyHeadlessParams` → `UserEquationEntry.ApplyTo`) now carries
  the source and the flag together.
- **Old files:** an entry's legacy `"Kind": 1` (DSL tab) reads as
  `UseCalcGen: true`, and `0` or a missing field as `false`. `Kind` is never
  written again. Old parameter state was never persisted (regions store the
  equation name only), so nothing else needs mapping.
- **C#-style sources** stay accepted. They are translated when compiled, and
  the startup translation (`UserEquationDslMigration`) still rewrites the
  translatable ones on non-CalcGen entries.
- **The editor** (#1089) has one source box and a CalcGen toggle bound to the
  flag (`UserEquationViewModel.UseCalcGen`).

Tests: `UserEquationSingleSource1088Tests` (legacy `Kind` mapping and rewrite,
region recall, image independence, clone and promotion) and
`UserEquationEditor1089Tests` (the editor).

## Samples in the docs (#1090)

Equation samples in the Markdown docs are tagged so a test can parse them:
a ```` ```equation ```` block is one equation (statements allowed), and an
```` ```equations ```` block lists one equation per line. Notes go in `//`
comments, which are part of the language. User Bulb 3D samples use
```` ```bulb ```` / ```` ```bulbs ```` and parse with the bulb language (#1100).
`DocsEquationSamplesTests` parses
every tagged sample, and every `--- Title ---` snippet in the in-app help
(`HelpTextBundle`), with this language.
