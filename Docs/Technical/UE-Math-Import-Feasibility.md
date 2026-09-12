<!--
SPDX-License-Identifier: AGPL-3.0-or-later
SPDX-FileCopyrightText: 2026 Bradley Brown
-->

# User Equation — LaTeX / MathML *import* feasibility (#757)

Spike for the reverse direction of the LaTeX/MathML preview feature (#753).
The preview **exports** the DSL engine's interpreted equation as LaTeX (#754)
and MathML (#756). This asks: can the User Equation dialog **accept** a pasted
LaTeX or MathML expression and turn it into the CalcGen DSL (or reject it with a
clear reason)?

**Verdict: GO — MathML-first, LaTeX best-effort.** Structured MathML maps cleanly
onto the DSL AST for the supported subset; LaTeX is doable for a constrained
subset but its irregular surface syntax makes it a lower-confidence, later pass.

---

## 1. What we import *into* — the DSL target

The importer's output is a CalcGen DSL string, fed to the existing
`EquationParser.Parse`. The full accepted vocabulary (from `EquationLexer`):

```
z c n(iter) prev  pi e i  + - * /  ^<int>
sqr conj fold abs re im sign floor round ceil trunc fract
sin cos tan sinh cosh tanh exp log sqrt arg
asin acos atan asinh acosh atanh
min(a,b) max(a,b) mod(a,b) atan2(y,x) pow(a,b) clamp(x,lo,hi)
if <cond> then <expr> else <expr>   (cond: re/im/abs/arg  > < >= <= == !=  const)
```

This is the **bound**: anything outside it (arbitrary variables, integrals,
sums, matrices, derivatives, unsupported functions) cannot be represented and
must be rejected with a specific message. The set is small and fully known
(`AstNodes.cs`), which is what makes import tractable at all.

## 2. Architecture — reuse the AST, don't emit DSL text directly

The clean design mirrors the export path in reverse and reuses existing infra:

```
LaTeX / MathML  --parse-->  AstNode tree  --AstPrinter.Print-->  DSL string
                                           --EquationParser.Parse--> validate
```

- Build an **`AstNode`** (the same type the whole engine already speaks), then
  hand it to the existing **`AstPrinter.Print`** to get canonical DSL text.
  This guarantees the emitted DSL is well-formed and re-parseable, and it means
  the importer never has to know DSL *surface* syntax — only how to build nodes.
- **Round-trip acceptance test:** `import(export(ast)) ≈ ast`. We already have
  both exporters (`AstLatexPrinter` #754, `AstMathmlPrinter` #756), so every
  equation the preview can render becomes a free import test case:
  `Parse(dsl) → AstLatexPrinter → import → AstPrinter` should equal
  `Parse(dsl) → AstPrinter`. High-value, cheap coverage.

This makes MathML import a **tree-to-tree transform** — the low-risk part.

## 3. MathML import (P1 — recommended)

Presentation MathML is structured XML; parse with `System.Xml.Linq` and walk:

| MathML | → AstNode |
|---|---|
| `<mi>z\|c\|n\|i</mi>` | `ZRef` / `CRef` / `IterRef` / `ImagUnit` |
| `<mn>k</mn>` | `RealConst(k)` |
| `<mo>+ − ⋅ /</mo>` in `<mrow>` | `Add` / `Sub` / `Mul` / `Div` |
| `<msup>b k</msup>` | `Pow(b,k)` if `k` int else `PowC` |
| `<msup><mi>e</mi> x</msup>` | `Exp(x)` |
| `<mfrac>` | `Div` |
| `<msqrt>` | `Sqrt` |
| `<mover>…<mo>¯</mo></mover>` | `Conj` |
| `<mrow><mo>\|</mo>…<mo>\|</mo></mrow>` | `AbsOp` (or `CondAbs2` in a condition) |
| `<mi>sin\|cos\|ln\|…</mi>` + args | `Sin` / `Cos` / `Log` / … |
| `<msub><mi>z</mi>…n−1</msub>` | `PrevRef` |
| `<mtable>`+`<mo>{</mo>` | `If` (recognise the cases shape) |

**Boundary / reject:** unknown `<mi>` names, `<msub>` other than `z_{n-1}`,
`<munderover>`/sums/integrals, tables that aren't a 2-row cases block, any
element with no DSL analogue → reject naming the offending construct.

Effort: **moderate**, high confidence. XML is unambiguous; the mapping is finite.

## 4. LaTeX import (P2 — best-effort, later)

LaTeX math is irregular and, in general, unbounded. A constrained subset is
feasible but needs a small tokenizer + Pratt parser handling:

- `\frac{}{}`, `\sqrt{}`, `^{}`, `_{}`, `\overline{}`, `\left|…\right|`,
  `\cdot`, `\operatorname{}`, `\sin`/`\cos`/`\ln`/`\arg`/`\arcsin…`, `e^{}`.
- Delimiter matching (`\left`/`\right`, nested braces).

Hard problems (documented, not solved here):

- **Implicit multiplication** — `2z`, `zc`, `z(z+c)` all mean `*`, but `z(…)` is
  never function application (no user-defined functions). Requires an insert-`*`
  pass with care around function names (`\sin(x)` is application).
- **`i`** — imaginary unit vs a stray index; we resolve to `ImagUnit` always
  (the DSL has no other `i`).
- **Non-integer / symbolic exponents** — `z^{2.5}`, `z^{c}` → `PowC` (`pow`),
  not the integer `^` operator.
- **Bare `\text{}` / spacing macros / `\displaystyle`** — strip.
- **Anything genuinely unbounded** (sums, integrals, `\begin{matrix}`, unknown
  macros) → reject.

Effort: **higher**, lower confidence (surface-syntax edge cases). Recommend
shipping MathML first, then LaTeX as a follow-up if demand warrants.

## 5. Ambiguities & policy (both directions)

| Case | Policy |
|---|---|
| Variable not in {z,c,n,i,e,pi,prev} | Reject, name it |
| `abs` inside a condition | `CondAbs2` = \|x\|² (matches CalcGen condition semantics) — note the export side already does this |
| `abs` in expression position | `AbsOp` = \|x\| |
| `sqrt` | maps to `Sqrt` node; note the DSL *itself* desugars `sqrt` to `exp(0.5·log)` on parse, so a round-trip normalises — acceptance tests must compare post-parse, not raw |
| Whitespace / grouping-only `<mrow>` / `\left(` | collapse, no semantic effect |

## 6. Where it plugs into the UI

- A **"Paste LaTeX / MathML…"** action on the DSL tab of the User Equation
  dialog: detect `<math` prefix → MathML path; else LaTeX path. On success,
  set `DslSource` (the existing validate-on-type + preview pipeline takes over,
  including the #754/#755/#756 preview so the user immediately sees the
  interpreted result to confirm the import was faithful). On failure, surface
  the reason in the status bar (same channel as parse errors).
- The importer lib belongs beside the parser (`CalculatorGen/Parser/`), UI-free,
  so it's unit-testable and reusable by the CLI/batch layer.

## 7. Recommendation & scoped follow-ups

**GO.** Land in two slices, MathML first:

1. **P1 — MathML import** (#764) — `MathmlToAst` + `Paste MathML` UI + round-trip
   tests against `AstMathmlPrinter`. High confidence, bounded.
2. **P2 — LaTeX import** (#765) — constrained-subset parser + `Paste LaTeX` +
   round-trip against `AstLatexPrinter`. Best-effort; ship after P1 proves the
   plumbing.

Both reuse `AstNode` + `AstPrinter` + `EquationParser`, so the risky part is only
the front-end parse, and the export printers give free test coverage.

Non-goals: full LaTeX support, unbounded constructs, variables outside the DSL
vocabulary. These are rejected with a specific message, never silently dropped.
