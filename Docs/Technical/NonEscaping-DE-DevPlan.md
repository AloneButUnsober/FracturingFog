# Non-Escaping DE + User-Supplied DE — Dev Plan

> Companion pages: [Technical Index](_Index.md) · [User Bulb Sandbox Dev Plan](UserBulbSandbox-DevPlan.md) · [User Bulb 3D Dev Plan](UserBulb3D-DevelopmentPlan.md) · [Fractal Equation Design Guide](FractalEquation-DesignGuide.md) · [User-facing User Bulb Guide](../User/UserBulb-Guide.md)

Tracking issues: parent **#279**, slices **#280** (NonEscaping runner), **#281** (dr/de DSL slots + DE body), **#282** (Amoser complex-sine preset), **#283** (Expr-compile CPU kernel). File ↔ issue links are two-way per project convention.

---

## Motivation

Origin: [fractalforums.org — Amoser's complex sine formula](https://fractalforums.org/index.php?topic=5591.0) (login-walled; formula + frag quoted below from user paste).

The map:

```
z = vec3(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) * Scale + C
```

Two coupled complex sines (real parts on x/z, shared imaginary on y) → quasi-conformal Kleinian-like dynamics with double-periodic (lattice) symmetry. Renders as pseudo-Kleinian spiky halls / sphere-grid hybrids.

**The step map is already fully expressible in the Sandbox-Bulb DSL** (`vec(...)*Scale + c`, with `Scale` a `UserBulbParam`; folds via `absx/absy/absz`; rotation via `rot`; Regulator/domain-inversion via `let`). **The DE is not.** That gap is this plan.

## Why current FF cannot reproduce it

FF `UserBulbCalculator` DE is **escape-based**, every path:

| Path | Form | Requires |
|---|---|---|
| Numerical Jacobian (default) | `0.5·r/|J|`, 4 orbits/iter, computed **after** loop | bounded `dz/dc` |
| Analytic running-derivative | `0.5·log(r)·r/dr` | power-law growth + escape |
| Quat-exact | `2·q·dq` recurrence | detected `q²+c` |

The Amoser DE is **non-escaping** and structurally different:

```glsl
float dr = 1.0;
float de = 1e20;
for (int i = 0; i < Iterations; i++) {
    if (abs(z.y) > 8.) break;              // stability clamp, NOT escape
    // ... compute sx,cx,sz,shy,chy ...
    z = vec3(sx*chy, cx*cz*shy, sz*chy) * Scale + juliaC;
    float stretch = max(0.9, 0.75 * sqrt(chy*chy + shy*shy));  // analytic |derivative|
    dr = abs(Scale) * signFactor * stretch * dr + offset;      // running derivative bound
    de = min(de, 1.0 / dr);                // running MIN, INSIDE the loop
}
return DEMultiplier * de;
```

Differences that make FF unable to match by tuning alone:

1. **No escape.** Stability clamp (`|z.y|>8`), not a bailout. FF treats Bailout as escape → wrong semantic.
2. **No `r` magnitude, no `log`.** Distance is purely `1/dr`.
3. **Running `min(1/dr)` accumulated inside the loop.** FF returns one value after the loop; there is no running-min accumulator.
4. **Map-specific analytic `stretch`.** `0.75·sqrt(chy²+shy²)` is the derivative magnitude of the hyperbolic part — hand-authored, not FF's numerical Jacobian.
5. **Seeding.** Mandelbrot: `z=pos, c=pos, offset=1`. Julia: `z=pos, c=JuliaC, offset=0`. The `offset` feeds the `dr` recurrence, not the map.

Conclusion: **not a settings problem — an engine extension.** (Verified: the user swept Iterations/Bailout/MaxSteps/Epsilon with no convergence to the forum look.)

## Bonus: it is also ~4× cheaper

The Amoser DE is **single-trajectory** (1 orbit + scalar `dr`). FF forces a **4-trajectory** numerical Jacobian on this map. Implementing the non-escaping DE is therefore both the correctness fix *and* a ~4× per-step speedup — a large part of the user's "slow near the surface" complaint on low-end hardware. (Their reference preset runs `MaxRaySteps = 1397` precisely because each step is cheap.)

---

## Design

### New DE mode: `NonEscaping`

Extend `UserBulbDEModeKind` (`Abstractions/Models/FractalParameters.cs`, currently `Auto | Analytic | Numerical`) with `NonEscaping`. A dedicated runner in the `UserBulbCalculator` DE dispatch:

- seed `z = pos`; Mandelbrot `c = pos`, `offset = 1`; Julia `c = JuliaC`, `offset = 0`;
- **stability clamp** (configurable component + threshold, default `|z.y|>8`) rather than escape bailout;
- single trajectory; track scalar `dr`;
- `de = min(de, 1/dr)` inside the loop;
- return `DEMultiplier * de`.

`DEMultiplier` (their `FudgeFactor`), stability axis/threshold → new `FractalParameters.UserBulb*` fields + `FractalParamsView` controls (per the tunable-params convention).

### `dr` recurrence — two tiers

**Tier 1 (ship first): user-supplied `dr` DSL body.** Add read/write state slots `dr` and `de` to `SandboxBulbExpression`, and a second per-iteration DSL body (the "DE body") authored + persisted alongside the step. Their recurrence is then trivially DSL:

```
let ez = exp(z.z) in
let stretch = max(StretchMax, StretchScale * sqrt(0.5*(ez*ez + 1/(ez*ez)))) in
dr = drScale * stretch * dr + drOffset
```

All their sliders (`Scale`, `StretchScale/Max`, `drScale/Offset`, `DEMultiplier`, `InvertC`, `OffsetVec`) are **already supported** as `UserBulbParams` — no new param plumbing.

Persistence: extend the userbulbs.json bulb record with an optional `DeBody` string. Absent → falls back to numerical/analytic as today.

**Security:** interpreter path only, no BCL surface, no source compile. The #27 raw-C#-removal invariant holds unchanged.

**Tier 2 (follow-up): auto-analytic `dr`.** Differentiate the step AST for a running scalar-derivative bound, so users need not hand-author `dr`. Precedent: FF already carries analytic-DE AST work (2D inverse-trig, #215/#220). 3D trig/hyperbolic chain rules are the hard part; the scalar-`|dz|` approximation is the tractable target. Not required for parity — Tier 1 authors it by hand exactly as the forum does.

### Reference preset: Amoser complex-sine

Ship a curated UserBulb preset (step body + DE body + default params from the frag `#preset Default`: `Scale=1`, folds on, `StretchScale=0.81`, `StretchMax=1.04`, `Regulator=true`, camera `Eye=(0,-12,0) Up=(0,0,1)`). Do **not** overwrite user copies (per the no-save-over-examples rule).

### Perf: Expr-compile CPU kernel (`#282`, orthogonal)

`Sbx3Node → System.Linq.Expressions → .Compile()` JIT delegate. No Roslyn, no source, whitelisted node set → #27-safe. ~5–20× over the interpreter, stacks on the 4× from single-trajectory DE. Benefits all bulbs, not just non-escaping.

---

## Slice plan

| # | Slice | Depends | Effort |
|---|---|---|---|
| #280 | `NonEscaping` DE runner: seed-at-point, stability clamp, running `min(1/dr)`, `DEMultiplier`, mode enum + UI | — | M |
| #281 | `dr`/`de` DSL state slots + persisted DE body; wire into #280 runner | #280 | M |
| #282 | Amoser complex-sine curated preset (step + DE body + params + camera) | #280, #281 | S |
| #283 | Expr-compile CPU kernel (`Sbx3Node` → compiled lambda) | — | M |

Later / not in parent scope: auto-analytic `dr` (AST diff); difference-DE (`ε·r/|z−z'|`) for other non-escaping maps; domain-inversion (`c/dot(c,c)+InvertC`) as a first-class pre-step.

## Open questions

- **DE body vs chain reuse.** Can the existing `SandboxBulbChain` carry a step tagged as the dr-update, or is a separate DE-body field cleaner? Lean separate field — the dr-update reads `dr`/`de` state the chain steps don't.
- **Stability clamp axis.** Frag hardcodes `z.y` (or `z.z` in the elevation-swapped .frag). Expose axis + threshold as params.
- **GPU parity.** `UserBulbSandboxGpuCompiler` would need the same non-escaping runner + `dr`/`de` slots to keep the GPU path usable for these maps. Defer until CPU lands; note as a fast-follow.
