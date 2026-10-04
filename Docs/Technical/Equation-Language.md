# Equation Language — Specification

Status: **Phases 1–2 of #937** (#1086, #1087). This is the one 2D equation
language behind the User Equation editor (both tabs), the Sandbox editor, the
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

## Where it lives

| What | Where |
|---|---|
| Parser, AST, interpreter | `CalculatorGen/Language/SandboxExpression.cs` |
| Entry point | `CalculatorGen/Language/EquationLanguage.cs` |

Both compile into **CalculatorGen.Lib**, below Engine, so CalcGen and Engine
share one tree. The namespace stays `FracturingFog.Models`.

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
mul_expr  := pow_expr (("*"|"/") pow_expr)*
pow_expr  := unary ("^" pow_expr)?                            ; right-assoc
unary     := "-" unary | "+" unary | primary
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
| 1 | unary `-` `+` | prefix |
| 2 | `^` | right |
| 3 | `*` `/` | left |
| 4 | `+` `-` | left |
| 5 | `<` `>` `<=` `>=` `==` `!=` | none (one per operand pair) |
| 6 | `!` | prefix |
| 7 | `&&` | left |
| 8 | `\|\|` | left |
| 9 | `?:`, `if … then … else`, `let … in` | right |

> **`-x^y` today means `(-x)^y`.** Unary minus binds tighter than `^`, which is
> what both engines have always done. The conventional reading, `-(x^y)`, will
> arrive with #1088, together with a backed-up rewrite of saved sources
> (`-x^y` → `(-x)^y`) so no artwork changes. Until then, write `-(x^y)` when you
> mean it.

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

### `abs` inside an `if … then` condition

CalcGen reads a condition operand `abs(x)` as **\|x\|²**. So that the same text
renders the same on both engines (#1085), a comparison operand that **is**
`abs(x)` in an `if … then` condition evaluates as `norm(x)`. Everywhere else,
including `?:`, `abs` is \|x\|. Write `norm(x)` to be explicit. #1088 migrates
saved sources and retires this rule.

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
