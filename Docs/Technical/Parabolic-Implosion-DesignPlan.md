# Parabolic Implosion — Research Design Plan

**Tracking issue:** [#911](https://github.com/AloneButUnsober/FracturingFog/issues/911)
(a spike/design issue, not an implementation issue).
Part of frontier epic [#850](https://github.com/AloneButUnsober/FracturingFog/issues/850);
the flagship far-future item of [Theoretical-Fractal-RnD.md](Theoretical-Fractal-RnD.md)
§5. **This doc also scopes §3.3 (positive-area Julia sets, Buff–Chéritat) as the
*static sibling* of the same machinery — one research thread, not two.**

**Status: SPIKE SERIES COMPLETE (S0–S6, 2026-09-20). S0 (feasibility, GO) + S1 (Tier A
naïve implosion animation, SHIPPED) + S2 (near-parabolic accuracy, GREEN) + S3
(Fatou + Écalle–Voronin horn map, GREEN) + S4 (Lavaurs map + limit theorem, GREEN) + S5
(positive-area Julia, GREEN-scoped) + S6 (precompute engine GREEN / faithful render
AMBER).** The whole faithful-limit **math chain is proven** — horn map (`|c₁| ≈ 0.06`,
`d ∝ ζ` to 0.3%; Abel to ~4e-11), Lavaurs map (`g_{α+1} = f ∘ g_α` to 3e-13, phase =
return multiplier `e^{2πiα}`, Douady re-injection) — and the **performance blocker is
resolved**: S6's precompute-coordinate engine evaluates `g_α` in 0.59 µs (~116 000×
faster), turning the faithful render from ~250 h to ~1 min at 7e-6 interp accuracy.
**Production build under way (post-spike).** The faithful feature is being built via the
**Lavaurs-limit** route ([#920](https://github.com/AloneButUnsober/FracturingFog/issues/920)):
render `J(g_α)` as the high-iteration near-parabolic Julia set on the controlled cardioid
`c(θ)`-path (`θ → 0` approaches the cusp `c = 1/4`), which is Lavaurs's theorem's own
definition of the limit (`J(f_{c(θ)}) → J(g_α)`) — tractable, validatable, and on the
existing Julia calculator. The **direct semigroup-Julia word-tree** render (S6 finding:
the imploded set is `J(⟨f, g_α⟩)`) is the theoretically-distinct alternative, **deferred**
as [#918](https://github.com/AloneButUnsober/FracturingFog/issues/918). The **naïve
implosion animation ships (S1)**; the faithful animation now ships too (a
`CardioidApproach` animation mode + a high-iteration "faithful" region/animation, #920).
Higher-`q` petals (other bulb boundaries) are the one untested math extension.

**Why a design doc first.** The faithful render depends on the Écalle–Voronin /
Lavaurs renormalization machinery — steep analytic math with near-zero prior
rendering art. The cost is the *math*, not the plumbing; scoping it wrong wastes
weeks. Same discipline that gated the Kleinian generalization
([Kleinian-Generalization-DesignPlan.md](Kleinian-Generalization-DesignPlan.md),
#855) before any code.

---

## 1. Purpose & scope

Render the **parabolic implosion**: the *discontinuous* reorganisation of the
Julia set as the parameter `c` is nudged off a **parabolic parameter** `c₀`.
Animate `c → c₀ + εe^{iθ}` and watch the set explode. The controlling analytic
objects are the **Écalle–Voronin horn maps** and **Lavaurs maps** (parabolic
renormalisation); **Lavaurs's theorem** names the limit the imploding family
converges to.

**In scope:**
- Near-parabolic Julia rendering at high precision.
- The **implosion animation** (`ε, θ`, and the Lavaurs phase `α`).
- The **Écalle–Voronin / Lavaurs faithful limit** — the true imploded set.
- **Positive-area Julia sets** (Buff–Chéritat, §3.3) as the *static sibling* —
  the same Fatou-coordinate / horn-map machinery, rendered as a set rather than
  an animation.

**Out of scope:**
- **§3.5 statistical / SLE / LQG / cascades** — a *disjoint* branch of maths
  (random conformal geometry / measure theory). No machinery transfers; do not
  fold it in.
- General (non-parabolic) renormalisation, the Mandelbrot-boundary MLC programme,
  and any deep-zoom-*accuracy* work — parabolic implosion is where perturbation
  accuracy breaks *by design* (the opposite goal).

---

## 2. Why it is the frontier (RnD §5.2)

- **It inverts the engine's usual goal.** Every FF deep-zoom asset —
  perturbation + series approximation + rebasing + DD/QD — exists to make
  perturbation theory *accurate*. Parabolic implosion is the regime where linear
  perturbation **fails by design**; the breakdown *is* the content. (The
  precision memory notes [[project_detail_depth_limit]], [[project_wave214_qdfloor]]
  are about *avoiding* this; here we *want* it.)
- **Near-zero faithful artistic renders exist** → maximum novelty.
- **Shared machinery with §3.3** (positive-area Julia) → two landmark results
  from one research core.

---

## 3. Math foundations (Rule B — cite everything)

### 3.1 Parabolic parameters and the discontinuity
A **parabolic parameter** `c₀` is one where `z² + c₀` has a fixed (or periodic)
point whose multiplier is a **root of unity** — e.g. `c = 1/4` (multiplier `+1`,
the cardioid root) or `c = −3/4` (multiplier `−1`, the 1/2-bulb root), and the
`p/q` bulb roots generally. There the Julia set is **discontinuous under
perturbation**: `J(c)` does **not** Hausdorff-converge to `J(c₀)` as `c → c₀`;
the limit is strictly *larger* (Douady's "explosion").

### 3.2 Leau–Fatou flower and Fatou coordinates
Near a parabolic fixed point with `q` petals the dynamics conjugates to the
translation `w → w + 1` in a **Fatou coordinate**. There are **attracting** and
**repelling** Fatou coordinates `Φ_att`, `Φ_rep` on the petals (the Leau–Fatou
flower). Computing these numerically is the first hard primitive.

### 3.3 Écalle–Voronin horn maps (the analytic invariant)
The **horn map** `h = Φ_att ∘ Φ_rep^{-1}`, defined on the ends of the quotient
cylinder, is the **modulus of analytic classification** of the parabolic germ —
it encodes the "extra" dynamics that surfaces under perturbation. This is the
object that makes the implosion *faithful* rather than a naïve perturbed-Julia
sequence.

### 3.4 Lavaurs maps and the limit theorem
Perturb along a controlled path so that the perturbed multiplier's argument and
the escaping-iteration count combine into a limiting **Lavaurs phase** `α`. Then
the perturbed Julia sets converge to the Julia set of the **Lavaurs map**
`g_α = f_{c₀} ∘ L_α` (Lavaurs's theorem). `J(g_α)` — the enriched limit set — is
the object to render for the *faithful* implosion, parameterised by `α`.

### 3.5 Positive-area Julia sets — the static sibling (§3.3)
Buff–Chéritat build quadratic Julia sets of **positive Lebesgue measure** by an
infinite sequence of *controlled near-parabolic perturbations* (Shishikura's
parabolic / cylinder renormalisation controlling the post-critical set). This is
**the same Fatou-coordinate / horn-map machinery** as §3.3–§3.4 above; the
deliverable is a **static set** (positive area), not an animation. Hence: build
the core once, render both.

---

## 4. Render approach — the three tiers (achievable-first-increment split)

The key design decision: **separate the visible-now increment from the deep
research**, so value lands early and the math risk is quarantined.

### Tier A — naïve implosion animation (no horn maps) — *achievable now*
Animate `c = c₀ + εe^{iθ}` and render **each frame with the existing Julia
calculator** at DD/QD precision (FF already has the precision + scene
param-animation, #632 precedent). This shows the *visual* explosion — the set
discontinuously reorganising as `c` circles `c₀`. **It is mostly plumbing.** It is
**not** the faithful limit (it is a sequence of ordinary perturbed Julia sets, not
`J(g_α)`), but it is visually compelling and ships without any new math. Label it
honestly as the *naïve* implosion.

### Tier B — near-parabolic accuracy
Characterise and guarantee orbit accuracy as the multiplier → 1 (orbits crawl
through the parabolic point). Confirm the DD/QD floor suffices; this gates Tier C.

### Tier C — faithful limit (Fatou coordinates → horn map → Lavaurs)
Implement `Φ_att`, `Φ_rep`, the Écalle–Voronin horn map, and the Lavaurs-map
composition; render `J(g_α)`, the true imploded set, parameterised by the Lavaurs
phase `α`. **The deep research.** Delivers the §3.3 positive-area set as a
byproduct (same core, static output). **Numeric constraints (validated in S2/S3, §9):**
1. **Integrate in the shifted normal-form coordinate** `w = z − z*` (`w_{n+1} = w + w²`),
   never form `z − z*` by subtraction — the raw subtraction cancels (~6 digits lost
   deep in the petal), the normal form holds ~14 digits in plain double (S2). DD/QD is
   an opt-in fallback for extreme regimes only.
2. **Fatou coordinate:** in `Z = −1/w` the germ is exactly `Z ↦ Z²/(Z−1)`, giving the
   clean asymptotic `Φ = Z − log Z + 1/(2Z) + O(1/Z³)` (pure-power tail → Richardson of
   `ψ(fⁿw) − n` converges cleanly; a bare `−1/w − log(−1/w)` leaves a `log n/n` tail).
3. **Backward map rationalised** `f⁻¹(w) = 2w/(1+√(1+4w))` (avoids the cancellation that
   otherwise caps repelling accuracy at ~1e-7); **per-petal log branch** (principal for
   the attracting petal `Re Z>0`, a `[0,2π)` branch for the repelling petal `Re Z<0`).
4. **Horn map = analytic continuation, not a common domain:** the single-parabolic
   petals do not overlap as sets (tangent disks meeting only at 0). Assemble
   `h = Φ_att ∘ Φ_rep⁻¹` by iterating a gap seed forward into the attracting petal
   (`Φ_att(fᵏw)−k`) and backward into the repelling petal (`Φ_rep(f⁻ᵏw)+k`). The EV
   modulus is exponentially small in `Im σ`, measurable only in a band `Im σ ∈ [1.2,2.2]`.
5. **The Lavaurs map** `g_α = f ∘ L_α`, `L_α = Φ_rep⁻¹ ∘ T_α ∘ Φ_att`, is the bridge that
   re-injects basin points into the repelling petal (Douady explosion). Its phase `α` is
   the **cylinder return multiplier `e^{2πiα}`** (`α = p/q` → parabolic cascade; irrational
   → rotation), so `α` is the natural render/animation parameter. Self-consistency to test:
   `g_{α+1} = f ∘ g_α` (S4: 3e-13).
6. **The faithful `J(g_α)` render must precompute, not recompute (S4).** One `g_α`
   evaluation is ~69 ms (a continued `Φ_att`, a Newton `Φ_rep⁻¹`, and `f`), so a naïve
   per-pixel escape-time render is ~250 h. Tier C **tabulates `Φ_att`, `Φ_rep` and their
   inverses on a grid over the basin and bilinearly interpolates** — this both makes the
   render tractable and gives a globally robust inverse (replacing the per-point Newton,
   which occasionally misses into the wrong petal).

### Where FF helps / where it does not
- **Helps:** DD/QD precision; scene parameter-animation (#632); the Julia
  renderer + colour themes (Tier A, and the output of Tier C is a Julia set).
- **New (a bespoke research calculator, likely its own module):** a Fatou-coordinate
  solver, a horn-map evaluator, Lavaurs-map composition.

---

## 5. Toolchain reach (Rule A)

- **CalcGen / DSL — out of reach.** This is renormalisation-*operator* machinery,
  not a per-pixel `f(z, c)` iteration; there is no DSL vocabulary for Fatou
  coordinates or horn maps. It ships as a bespoke research calculator, possibly in
  its own module. (Same conclusion as the Kleinian 3D DE.)
- **ColorGen / Color Theme — output side, in reach.** The rendered object is a
  Julia set, so existing Julia themes colour it unchanged. The novelty is
  **temporal** (the implosion), so the real reach is into the **animation / scene**
  system — a **phase input** (`θ`, or the Lavaurs `α`) driving a cyclic palette is
  the natural colour hook.

---

## 6. Sliced spike plan (each = a spike issue; validation-gated)

**These are research spikes, not implementation slices.** S0 gates everything;
the deep tiers only proceed on validation.

- **S0 — Feasibility spike. ✅ DONE — verdict GO (see §9).** Read Douady 1994 /
  Lavaurs 1989 / Shishikura 1998 / Buff–Chéritat 2012 / Milnor (flower background);
  numerically de-risked Tier A (naïve explosion) and Tier C's first primitive (the
  attracting Fatou coordinate). *(gated all others)*
- **S1 — Tier A naïve implosion animation. ✅ SHIPPED.** Scene animation of
  `c = c₀ + εe^{iθ}` on the existing Julia calculator; a **parabolic-parameter
  preset library** (three built-in Julia regions at `c = 1/4`, `−3/4`, the 1/3-bulb
  root) + three matching built-in **"Parabolic implosion (…)"** animations. Enabled
  by a small reusable infra add: an optional **centre** on the Complex polar
  (Lissajous) sweep, so `c` can circle a non-origin `c₀` (default (0,0) =
  byte-identical). Visible result, no new math. +2 tests. *(deps: S0)*
- **S2 — Near-parabolic accuracy spike (Tier B). ✅ DONE — verdict GREEN (§9).**
  Double suffices for near-parabolic integration *via the shifted normal-form
  coordinate* `w = z − z*` (`w_{n+1} = w + w²`, ~1e-14 vs QD at 10⁶ iters);
  DD/QD is an opt-in margin, not required. *(deps: S0)*
- **S3 — Fatou coordinates + horn map (Tier C core). ✅ DONE — verdict GREEN (§9).**
  `Φ_att` / `Φ_rep` computed (Abel equation to ~4e-11, real symmetry exact); the
  Écalle–Voronin horn map assembled by analytic continuation through the gap and shown
  **nontrivial** (`|c₁| ≈ 0.06`, `d ∝ ζ = e^{2πiσ}` to 0.3% over 2.3 decades, multiplier
  `μ = 1`). **The main research risk is retired for `c = 1/4`.** Numeric recipe recorded
  as Tier C constraints (§4). *(deps: S2)*
- **S4 — Lavaurs map + faithful limit render. ✅ DONE — verdict GREEN (§9).** Built
  `g_α = f ∘ L_α` on the S3 core; validated against Lavaurs's theorem — periodicity
  `g_{α+1} = f ∘ g_α` to **3e-13**, the phase `α` confirmed as the cylinder return
  multiplier `e^{2πiα}`, Douady-explosion re-injection demonstrated. **Did not render
  `J(g_α)`:** S4 found a naïve render is ~250 h → the image is an *engineering* build
  (precompute/interpolate the coordinates, §4), not spike work. The math chain is proven.
  *(deps: S3)*
- **S5 — Positive-area Julia static render (§3.3 sibling). ✅ DONE — verdict GREEN,
  scoped (§9).** Finding: positive Lebesgue measure is a *limit* property of an infinite
  near-parabolic renormalisation tower, not a single renderable `c`. So the sibling
  splits like the implosion — a **naïve near-parabolic / Siegel "fat" Julia** ships now on
  the existing calc (rendered: golden-mean Siegel set), the **certified** Buff–Chéritat
  set is a research tier (renorm tower + the S4 precompute engine). Box-dim of `∂J` rises
  1.07→1.36 along the period-doubling cascade (the fattening mechanism) but plateaus below
  2 — confirming the tower is required. *(deps: S3/S4)*
- **S6 — Faithful implosion animation (Tier C animated). ✅ DONE — verdict GREEN
  (engine) / AMBER (render) (§9).** Built + validated the **precompute-coordinate engine**
  (both Fatou coordinates on a `Z=−1/w` grid, `g_α` in 0.59 µs, ~116 000× faster,
  interp ≤ 7e-6, Lavaurs periodicity preserved to 7.8e-9) → the ~250 h render becomes
  ~1 min; the perf blocker is resolved. **Finding:** the faithful set is the Julia set of
  the **semigroup `⟨f, g_α⟩`**, so it needs a word-tree escape algorithm — a naïve
  single-orbit combined map gives bounded recurrence (0/888 escape), not the imploded set.
  That's the one open item for the faithful feature. *(deps: S4)*

**Recommended order:** S0 → **S1 (ship the visible naïve animation, pause)** →
S2 → S3 → S4 → S5 → S6. S1 is the only slice that lands without the deep core; it
buys a real result while S3 (the hard math) is de-risked. **S0–S6 are DONE. The
faithful-limit *math* chain (Fatou coordinates → horn map → Lavaurs map) is proven and
internally validated; the positive-area sibling shares the Tier-A-now / Tier-C-research
split (S5); and the *performance* blocker is resolved (S6 engine). The faithful renders
(imploded `J(g_α)` and the certified Buff–Chéritat set) are unblocked on perf and gated on
one named production task: a correct semigroup-Julia rendering algorithm (word-tree escape
with pruning) — the hardest validation tier (§7). The visible naïve implosion (S1) already
ships. Higher-`q` petals remain the one untested math extension.**

---

## 7. Risks & open questions

- **Horn-map numerics (the main risk).** Fatou-coordinate + horn-map evaluation is
  steep; getting it correct is S3's whole job. The gate is validation against the
  literature (horn-map self-consistency; Lavaurs's theorem).
- **Precision.** Near-parabolic orbits crawl; whether DD/QD suffices at the finest
  `α` is exactly what S2 answers. (If not, the OD/extended tiers are the fallback.)
- **Scope masquerade.** Tier A (S1) can look "done" — it is *not* the faithful
  implosion. Keep the naïve vs faithful distinction explicit in UI + docs.
- **Validation (Bucket II, hardest tier).** Almost no reference imagery for the
  faithful limit. Checks are internal (horn-map self-consistency) + theoretical
  (Lavaurs's theorem), not "match a plate."
- **Module boundary.** Likely its own module/calculator; decide in S0 whether it
  reuses the Julia render path or stands alone.

---

## 8. Bibliography anchors (Rule B)

All already in [Resources-Bibliography.md](../Resources-Bibliography.md#parabolic-implosion):
- **Douady 1994** — *Does a Julia set depend continuously on the polynomial?* — the
  discontinuity at parabolic parameters (the explosion).
- **Lavaurs 1989** (thesis) — Lavaurs maps / the limit of the imploding family.
- **Shishikura 1998** — parabolic renormalisation; `dim ∂M = 2`.
- **Écalle** (résurgence) — the Écalle–Voronin horn-map invariants.
- **Buff & Chéritat 2012** (*Annals*) — quadratic Julia sets of positive area,
  built via controlled near-parabolic perturbation (the §3.3 sibling).
- **Milnor**, *Dynamics in One Complex Variable* (3rd ed., 2006) — Leau–Fatou
  flower / Fatou-coordinate background.

---

## 9. Spike findings

### S0 — feasibility (2026-09-20) — verdict **GO**

The S0 spike ran two throwaway numerical experiments (deleted after write-up) to
de-risk the two unknowns that would sink the project: whether Tier A is real, and
whether the Tier C horn-map core's first numeric is tractable.

**Experiment 1 — Tier A naïve implosion (existing math only).** Rendered plain
`z² + c` Julia sets at the parabolic `c₀ = 1/4` and at `c = c₀ + εe^{iθ}` for
`ε ∈ {0.005, 0.02}` and several `θ`. Result: the parabolic "cauliflower" at `c₀`
**discontinuously reorganises** as `c` circles it — pinch points open, spiral horns
appear, the filament structure changes qualitatively between nearby frames. This is
the explosion, and it renders with the **existing Julia calculator** at ordinary
precision. **Tier A is trivially feasible** — S1 is animation plumbing + a
parabolic-parameter preset library, no new math. (Faithfulness caveat unchanged: it
is a sequence of ordinary perturbed Julia sets, not the Lavaurs limit `J(g_α)`.)

**Experiment 2 — Tier C first primitive: the attracting Fatou coordinate.** At
`c = 1/4` the parabolic fixed point is `z* = 1/2` (multiplier `f'(z*) = 2z* = 1`).
Orbits in the attracting petal approach `z*` by the parabolic `1/n` law; the
attracting Fatou coordinate is asymptotically `u(z) = −1/(z − z*)` and must satisfy
the conjugacy `u(f(z)) − u(z) → 1`. Numerically (double precision, 50-step warm-up,
4000 iterations) the increment **converged to 1 within < 0.02**. So the
Fatou-coordinate primitive — the entry point of the Écalle–Voronin machinery — is
**computable and well-conditioned** for the simplest parabolic. That is the single
most encouraging signal for Tier C: the hard part starts from a clean footing.

**Precision note.** Double precision sufficed for `c = 1/4` (one petal, multiplier
exactly 1). Deeper cases — higher-`q` bulb roots, and the fine Lavaurs phase `α` —
are expected to need DD/QD; that is exactly what **S2** (near-parabolic accuracy)
exists to characterise, and FF already has the DD/QD tiers.

**Module-boundary decision.** Two clean layers:
- **Tier A / S1** reuses the **existing Julia render path** + the scene/animation
  engine + a new **parabolic-parameter preset library**. *No new module* — it is a
  preset set plus an animation. Keep it clearly labelled *naïve* implosion.
- **Tier C (S3–S6)** is a **bespoke standalone module** (a Fatou-coordinate solver +
  Écalle–Voronin horn-map evaluator + Lavaurs-map composition), **not** an extension
  of the heuristic Julia calculator — keep the escape-time path clean. Its *output*
  is a Julia-type set, so it re-enters the normal colour/theme pipeline at the end.

**Verdict: GO.** Both the achievable-first-increment and the deep-core entry point
are validated. Proceed to **S1** (visible result, no math risk); schedule **S2 → S3**
(the real research) only when S1 has shipped and the appetite is there. S3 (horn-map
correctness) remains the project's main risk; S0 has shown its foundation is sound,
not that the whole edifice is easy.

### S2 — near-parabolic accuracy (2026-09-20) — verdict **GREEN: double suffices via the normal form**

The near-parabolic worry was that orbits *crawl* through the parabolic point — many
iterations spent where `z ≈ z*` — and that rounding would corrupt the distance-to-
fixed-point. Measured at `c = 1/4` (real axis, `z* = 1/2`, one petal, multiplier 1),
comparing **double** against a **QuadDouble** reference:

**Finding 1 — the primitive is the parabolic *normal form*, not the raw map.**
Forming `z − z*` by subtraction cancels (both near ½): at `n = 2·10⁵` the direct
`z − ½` in double had **rel. error ≈ 3.9e-10** vs QD — ~6 digits lost, worsening
deeper in the petal. But the **shifted coordinate** `w = z − z*` obeys
`w_{n+1} = w_n + w_n²` with **no subtraction** — and in plain double it tracks the QD
reference to **rel. error ≈ 1e-14 across the whole range up to n = 10⁶**, with the
`1/n` law `w_n·n → −1` reproduced to 5 significant figures. The parabolic multiplier
is *exactly 1* (neutral), so per-step error is neither amplified nor damped — it
stays at machine-epsilon rather than growing exponentially.

**Finding 2 — double is adequate for the Tier C integration core.** Because the
normal form retains ~14 digits at a million iterations, **double precision suffices**
for near-parabolic orbit integration and hence for the Fatou-coordinate / horn-map
evaluation, *provided the code works in the shifted `w` coordinate* (this is now a
Tier C design constraint). FF's DD/QD tiers are a **safety margin** for extreme
regimes — the finest Lavaurs phases `α`, `c` pressed against `∂M`, and higher-`q`
petals (where the normal form gains a `w^{q+1}` term but the neutral-multiplier
argument still holds) — available if a case demands it, but **not required for the
baseline**.

**Verdict: GREEN.** The numerics are not the risk. Tier C must (a) integrate in the
shifted normal-form coordinate `w = z − z*`, and (b) keep DD/QD as an opt-in
fallback. The remaining risk is entirely the **horn-map correctness (S3)**, not
precision. Gate to S3: open.

### S3 — Fatou coordinates + Écalle–Voronin horn map (2026-09-20) — verdict **GREEN: the core is computable and internally validated**

S3 is the flagged **main research risk**: can the Fatou-coordinate / horn-map core
actually be computed *correctly* for the simplest parabolic (`c = 1/4`, germ
`f(w) = w + w²`, one attracting + one repelling petal)? A throwaway numerical
experiment (double precision, deleted after write-up) built all three primitives and
validated them.

**Primitive 1 — attracting & repelling Fatou coordinates.** In `Z = −1/w` the germ is
*exactly* `Z ↦ Z²/(Z−1)`, so the Fatou coordinate has the clean asymptotic
`Φ(w) = Z − log Z + 1/(2Z) + O(1/Z³)` — a pure-power tail (no `log n / n` term), so
Richardson extrapolation of `ψ(fⁿw) − n` converges cleanly. `Φ_att` (forward orbit)
and `Φ_rep` (backward orbit) each satisfy the Abel/conjugacy equation
`Φ(f(w)) − Φ(w) = 1` to **3.6e-11** (independent limits, double precision). Three
non-obvious numerical constraints were required (now recorded in §4): the backward
map must be **rationalised** `f⁻¹(w) = 2w/(1+√(1+4w))` (the naïve form cancels and caps
repelling accuracy at ~1e-7); a **per-petal log branch** (principal for `Re Z>0`, a
`[0,2π)` branch for the repelling petal's `Re Z<0`, which otherwise sits on the cut);
and convergence judged by **Richardson-extrapolation stability**, not the raw tail gap
(which over-reports the error ~10³× and rejects good points).

**Primitive 2 — the horn map, via analytic continuation.** The key geometric fact for
the single parabolic: the attracting and repelling petals **do not overlap as point
sets** (disks tangent to 0 from opposite sides, meeting only at 0). The horn map
`h = Φ_att ∘ Φ_rep⁻¹` therefore lives on the *analytic continuation* through the two
gaps, not a common domain. It is assembled from a gap seed `w₀`: `Φ_rep(w₀)` continues
by iterating **backward** into the repelling petal (`Φ_rep(f⁻ᵏw₀)+k`), `Φ_att(w₀)` by
iterating **forward** into the attracting petal (`Φ_att(fᵏw₀)−k`); then `σ = Φ_rep(w₀)`,
`τ = Φ_att(w₀)`, and `(σ, τ)` is a graph point of `h`.

**The horn map is nontrivial (the deliverable).** `d = τ − σ` is 1-periodic and, at the
upper end, analytic in `ζ = e^{2πiσ} → 0`. Measured: `d ∝ ζ` with `|c₁| = 0.0598`, and
the ratio `d/ζ` is **constant to 0.3% across 2.3 decades of `|ζ|`** — i.e. `d` is the
single Écalle–Voronin Fourier mode, not numerical noise. The horn shift
`c₀ ≈ 3.5e-8 ≈ 0` gives multiplier `μ = e^{2πic₀} = 1` — correct for the real-symmetric
quadratic germ (whose formal invariant vanishes; the modulus is purely the nonzero
`c₁`). The lower end is `c₁_lower = conj(c₁_upper)` by the real symmetry
`Φ(w̄) = conj Φ(w)`, which held **exactly** (0.0). The EV modulus is exponentially
small in `Im σ`, so it is measurable only in a band `Im σ ∈ [1.2, 2.2]` — small enough
that `|ζ|` is not below the ~1e-11 Fatou noise floor, large enough that the seed still
converges in both petals.

**Validation posture.** There is no reference plate for the horn-map coefficient, so
validation is internal + theoretical (as §7 anticipated): the Abel equation, the exact
real symmetry, and — the strongest check — that `d` collapses onto a single
`ζ`-proportional mode over 2+ decades. All three passed. `|c₁| ≈ 0.06` is the measured
modulus; its *phase* is convention-dependent (the additive constant / branch choice),
`|c₁|` is not.

**What this does and does not de-risk.** It retires the **central** S3 risk — the
horn-map core is computable and correct for `c = 1/4`. It does **not** yet cover
higher-`q` bulb roots (germ gains a `w^{q+1}` term → `q` petal pairs / a `q`-fold horn
structure) nor the Lavaurs phase `α`; those are S4's job, but they now build on a
*proven* Fatou/horn foundation rather than an unknown one.

**Verdict: GREEN.** Gate to S4 (Lavaurs map + faithful limit render): open.

### S4 — Lavaurs map + limit-theorem mechanism (2026-09-20) — verdict **GREEN: the Lavaurs construction is validated; the faithful render is gated on precomputing the coordinates**

S4 builds the Lavaurs map on the S3-validated Fatou coordinates and tests it against
Lavaurs's limit theorem. Throwaway double-precision experiment (deleted after write-up).

**Construction.** The Lavaurs bridge `L_α = Φ_rep⁻¹ ∘ T_α ∘ Φ_att` maps the attracting
basin to the repelling petal by translating by the Lavaurs phase `α` on the quotient
cylinder; the **Lavaurs map** is `g_α = f ∘ L_α`. Both are assembled from the S3
primitives (forward-/backward-continued `Φ_att` / `Φ_rep`) plus a Newton inverse of
`Φ_rep`.

**Validation 1 — Lavaurs periodicity (the strong, non-circular check).** Because
`Φ_rep⁻¹(σ+1) = f(Φ_rep⁻¹(σ))`, the family must satisfy `g_{α+1} = f ∘ g_α`. Measured
across `α ∈ {0.2, 0.5, 0.75}` and several basin points: `max|g_{α+1}(w) − f(g_α(w))| =
2.9e-13`. This identity holds only if the whole construction (both Fatou coordinates,
the inverse, the composition) is correct — it is the numeric signature of Lavaurs's
theorem's structure.

**Validation 2 — the phase is the return multiplier (Lavaurs's mechanism).** The bridge
is a pure translation by `α` on the cylinder; the first-return map that closes the loop
is `R_α = h ∘ T_α` with `h` the S3 horn map. In the end coordinate `ζ = e^{2πiσ}` its
multiplier is `E_α'(0) = e^{2πic₀} · e^{2πiα} = e^{2πiα}` (S3 gave the horn multiplier
`μ = e^{2πic₀} = 1`). So **the Lavaurs phase becomes the multiplier of the created return
dynamics**: `α = p/q` → root of unity → a *new* parabolic → an implosion cascade;
`α` irrational → an irrational rotation (Siegel/Cremer) on the cylinder. This is exactly
the tuning knob Lavaurs's theorem predicts, and it wires S4 directly to the S3 invariant.

**Validation 3 — the Douady explosion mechanism.** Under `f` alone an attracting-basin
point converges to the parabolic point (`f²⁰⁰(w) → 0`); `g_α` **re-injects** it into the
repelling petal (`g_{0.5}(w) = 0.059`, `|·| ≫ 0`), so it no longer converges. This
re-activation of the basin is precisely why `J(g_α)` is strictly larger than
`J(f_{c₀})` — the discontinuous enlargement the whole project renders.

**The render-cost wall (key engineering finding).** A single `g_α` evaluation costs
~**69 ms** (each involves a forward-continued `Φ_att`, a Newton `Φ_rep⁻¹`, and `f`). A
naïve escape-time render of `J(g_α)` — `512² px × ~50 iterations` — would take
**~250 hours**. **The faithful Tier C render is therefore not a per-pixel-recompute job:
it must precompute/tabulate `Φ_att`, `Φ_rep` (and their inverses) on a grid over the
basin and bilinearly interpolate** (now Tier C constraint §4.6). This also subsumes the
isolated Newton-inversion misses seen in the sanity check (5/6 converged): a tabulated
inverse is both faster and globally robust.

**What this does and does not deliver.** It validates the Lavaurs **map + limit-theorem
mechanism** (periodicity to 3e-13, phase→multiplier, basin re-injection) — the math of
the faithful limit is correct and computable. It does **not** produce the `J(g_α)`
image: that is production work (the precomputed-coordinate engine), correctly deferred to
the Tier C build (S5/S6) rather than forced into a spike. Higher-`q` petals (a `q`-fold
bridge) remain untested but inherit the now-proven single-petal machinery.

**Verdict: GREEN.** The Lavaurs construction and Lavaurs's-theorem mechanism are
validated; the remaining Tier C work is an **engineering** build (precompute-and-
interpolate the coordinates), not an open math risk. Gate to S5 / S6: open.

### S5 — Positive-area Julia sets (Buff–Chéritat, §3.3 sibling) (2026-09-20) — verdict **GREEN, scoped: the sibling splits Tier-A-now / Tier-C-research, exactly like the implosion**

S5 asked whether the S3/S4 core renders a Buff–Chéritat positive-area Julia set. The
honest answer the spike surfaced: **positive Lebesgue measure of a Julia set is a *limit*
property of an infinite near-parabolic renormalisation tower, not a property of any
single renderable parameter `c`.** So the §3.3 sibling splits the same way the implosion
does:

- **Tier-A analogue (achievable now, existing calc):** a **near-parabolic / Siegel "fat"
  Julia** — a deep-iteration `z² + c` render at a parameter driven toward the positive-area
  regime (a golden-mean Siegel `c`, or a near-parabolic cauliflower). Visually the
  area-filling fractal; ships on the existing Julia path, no new math — exactly like S1's
  naïve implosion. *(Rendered as the throwaway spike artifact: the golden-mean Siegel set,
  boundary a thick fractal filigree.)*
- **Tier-C analogue (research):** the **certified** Buff–Chéritat set (dim exactly 2,
  positive Lebesgue measure). Requires the inductive renormalisation tower — infinitely
  many controlled near-parabolic perturbations, each step's size governed by the cylinder
  renormalisation / Fatou-coordinate machinery (the S3/S4 core). Rendering the certified
  limit is a research computation and inherits S4's precompute-coordinate constraint.

**Numeric evidence for the fattening mechanism.** Box-counting dimension of `∂J(c)` along
the real period-doubling renormalisation cascade (each step = one more renormalisation
level), 1200² grid, 2500 iters:

| `c` | regime | box-dim `∂J` | area-frac `K` |
|---|---|---|---|
| 0 | circle (control) | 1.07 | 0.256 |
| 1/4 | period-1 parabolic | 1.09 | 0.239 |
| −3/4 | period-2 root | 1.25 | 0.171 |
| −5/4 | period-4 root | 1.36 | 0.054 |
| −1.368 | ~period-8 | 1.36 | 0.015 |
| −1.4012 | Feigenbaum accumulation | K empty (dendrite) | 0.000 |

The dimension **rises with renormalisation depth** (1.07 → 1.36) — the fattening is real
and mechanistic — but the simple real cascade **plateaus far below 2** and the interior
vanishes at the accumulation point (a dendrite, not a positive-area set). Quantitative
confirmation that **certified positive area cannot be reached by a single or simply-
parameterised `c`**; it genuinely needs the Buff–Chéritat tower (the delicate infinitely-
renormalisable *complex* parameters), consistent with Shishikura/Buff–Chéritat.

**The S3/S4 connection.** Each renormalisation step is a parabolic explosion (the S4
re-injection mechanism), and the area it contributes is controlled by the horn map /
Fatou coordinates (S3). The positive-area set is literally "the S3/S4 core, iterated to a
limit" — the doc's "build the core once, render both" holds, with the caveat that the
*limit* (not a single application) carries the positive measure.

**Verdict: GREEN, scoped.** The achievable deliverable — a near-parabolic/Siegel "fat"
Julia on the existing calculator — is validated and rendered; the certified Buff–Chéritat
positive-area set is a research tier (renorm tower + the S4 precompute engine), correctly
deferred. The static sibling is *not* a single new build but the same Tier-A-now /
Tier-C-research split as the implosion. Gate to S6 (faithful animation): open — and S6
shares the one remaining engineering task (the precompute-coordinate render engine) with
the whole faithful tier.

### S6 — Faithful implosion animation: precompute engine built + a render-algorithm finding (2026-09-20) — verdict **GREEN on the engine (perf wall resolved); AMBER on the render (the faithful set needs a semigroup-Julia algorithm)**

S6 built the engineering deliverable S4 identified — the precompute-coordinate engine —
and used it to attempt the faithful `J(g_α)` render. Throwaway experiment, deleted.

**The engine (the deliverable): built and validated.** Both Fatou coordinates are
tabulated on a regular `Z = −1/w` grid (900×720) — the natural cylinder coordinate, where
`Φ ≈ Z` is smooth so bilinear interpolation is accurate — with `Φ_rep` stored as its
analytic **continuation on the attracting-side range** (where the Lavaurs inverse
`Φ_rep⁻¹(τ+α)` actually lands, S4). Results:
- **Interp accuracy:** `|Φ_att,fast − Φ_att,direct| ≤ 7e-6` over the petal.
- **Speed:** one engine `g_α` eval is **0.59 µs** (all 500 000 test evals converged) vs
  **69 ms** direct — a **~116 000× speedup**. A 700²×200-step render drops from S4's
  **~250 h to ~1 min**.
- **Correctness preserved:** the engine reproduces the S4 Lavaurs periodicity
  `g_{α+1} = f(g_α)` to **7.8e-9** (interp-limited, not a construction error).

**So the performance wall S4 identified is resolved** — the coordinates evaluate in
constant time at render-grade accuracy.

**The render finding (a scope correction).** Feeding the engine into a naïve *single-orbit*
combined map — "apply `g_α` where the point is in the basin, else `f`" — does **not**
produce the imploded Julia set: over an 888-point petal grid, **0 points escaped in 5000
steps**; typical orbits stay bounded, cycling between basin and repelling petal. The reason
is structural, not a bug (the engine `g_α` is correct to 7.8e-9): the faithful imploded set
is the Julia set of the **semigroup** `⟨f, g_α⟩` (Lavaurs's theorem) — a point escapes iff
*some word* in `{f, g_α}` escapes, which requires exploring the **word tree**, not
following one deterministic orbit. A single orbit massively undercounts escape (hence the
all-bounded result).

**Consequence for the roadmap.** The earlier framing — "the only remaining task is the
precompute engine" — is corrected: the faithful animation needs **two** pieces, (a) the
engine [now done and validated] and (b) a correct **semigroup-Julia rendering algorithm**
(word-tree escape with pruning, or an equivalent formulation). Piece (b) is the "hardest
validation tier" §7 flagged (almost no reference imagery; checks internal + theoretical).
It is a production task, not a spike.

**Verdict: GREEN on the engine, AMBER on the faithful render.** The research chain (S0–S5)
is complete and the performance blocker is removed; the faithful-animation *feature*
remains gated on the semigroup-Julia algorithm — now the single clearly-named open item
for the flagship. No misleading render was shipped (the spike's combined-map frames are
bounded-recurrent, not the imploded set, and were discarded). The visible naïve implosion
animation (S1) already ships as the flagship's user-facing deliverable.

---

## 10. Change log

- **2026-09-20** — **Faithful renderer #920 follow-up — other parabolic roots +
  iteration auto-scaling.** Generalised the faithful implosion to the period-2 (`c = −3/4`,
  `θ → 1/2`) and period-3 (`1/3`-bulb root, `θ → 1/3`) parabolic roots — the same
  `CardioidApproach` mode, since the `p/q` bulb root *is* the cardioid point `c(p/q)`; each
  ships a built-in animation + a high-iteration region. **Iteration auto-scaling:** a new
  animatable `FractalParameters.EscapeIterationScale` (default 1.0, honoured in
  `FractalRenderHost` where the per-render iteration cap is set) plus a **synced iteration
  track** (Triangle, lock-step with the `θ` sweep) on each faithful animation — so shallow
  frames stay fast (×1) and the deep frames near the root get up to ×6 the iterations the
  near-parabolic crawl needs. +3 tests (period-2/3 roots land on the right `c`;
  `EscapeIterationScale` animates + clones). Suite green; WinExe build clean. Follow-ups:
  arbitrary `p/q` roots via a Farey/continued-fraction solver; the `1/(θ−p/q)` iteration
  law (the ramp is currently linear-in-phase, monotone-correct but not exact).
- **2026-09-20** — **Faithful renderer production build (post-spike) — Lavaurs-limit route
  ([#920](https://github.com/AloneButUnsober/FracturingFog/issues/920)).** Chose the
  Lavaurs-limit approach over the semigroup word-tree ([#918](https://github.com/AloneButUnsober/FracturingFog/issues/918),
  deferred): render `J(g_α)` as the high-iteration near-parabolic Julia set on the
  controlled cardioid `c(θ)`-path (`c(θ) = e^{2πiθ}/2 − e^{4πiθ}/4`, multiplier `e^{2πiθ}`;
  `θ → 0` approaches the cusp `c = 1/4`), which is Lavaurs's theorem's definition of the
  limit — on the existing Julia calculator. Prototype validated (satellite spirals bloom as
  `θ → 0`). **Shipped (MVP):** a new `CardioidApproach` **animation mode** (sweeps `θ`,
  outputs `c(θ)`), a built-in **"Parabolic implosion (faithful, c=1/4)"** animation, and a
  high-iteration **"Parabolic implosion (faithful) c = 1/4"** region bound to it. +3 tests.
  The faithful animation is the rigorous counterpart of the naïve S1 circle. (Iteration
  auto-scaling for the deepest `θ`, other bulb boundaries / higher-`q` petals: follow-ups.)
- **2026-09-20** — **S6 done — faithful implosion animation, verdict GREEN (engine) /
  AMBER (render) (§9). SPIKE SERIES S0–S6 COMPLETE.** Built + validated the
  **precompute-coordinate engine** S4 called for: both Fatou coordinates tabulated on a
  `Z=−1/w` grid (900×720), `g_α` evaluated in **0.59 µs** (~116 000× faster than the 69 ms
  direct), interp ≤ 7e-6, Lavaurs periodicity preserved to 7.8e-9 → the ~250 h faithful
  render becomes **~1 min**; the perf blocker is resolved. **Scope-correcting finding:** a
  naïve single-orbit combined map (`g_α` on the basin, `f` elsewhere) yields bounded
  recurrence (0/888 petal points escape in 5000 steps), **not** the imploded set — because
  the faithful set is the Julia set of the **semigroup `⟨f, g_α⟩`** (escape = *some word*
  in `{f,g_α}` escapes → needs a word-tree algorithm). So the faithful feature needs two
  pieces: the engine [done] + a semigroup-Julia renderer [named, open]. No misleading
  render shipped; the visible naïve implosion (S1) is the flagship's user-facing
  deliverable.
- **2026-09-20** — **S5 done — positive-area Julia (§3.3 sibling), verdict GREEN, scoped
  (§9).** Finding: positive Lebesgue measure is a *limit* property of an infinite
  near-parabolic renormalisation tower, not a single renderable `c`. The sibling splits
  like the implosion — a **naïve near-parabolic / Siegel "fat" Julia** ships now on the
  existing calc (rendered the golden-mean Siegel set as the artifact), the **certified**
  Buff–Chéritat set (dim 2, positive area) is a research tier (renorm tower + the S4
  precompute engine). Box-dim of `∂J` measured rising 1.07→1.36 along the period-doubling
  cascade (the fattening mechanism) but plateauing below 2 — quantitatively confirming the
  tower is required (the real cascade dendrites out; certified positive area needs the
  delicate complex infinitely-renormalisable parameters). S3/S4 core = the per-level
  cylinder-renormalisation control.
- **2026-09-20** — **S4 done — Lavaurs map + limit-theorem mechanism, verdict GREEN
  (§9).** Built `g_α = f ∘ L_α` (`L_α = Φ_rep⁻¹ ∘ T_α ∘ Φ_att`) on the S3 core and
  validated against Lavaurs's theorem: **periodicity `g_{α+1} = f ∘ g_α` to 3e-13**, the
  phase `α` confirmed as the cylinder **return multiplier `e^{2πiα}`** (`p/q` → parabolic
  cascade, irrational → rotation), and the **Douady-explosion re-injection** demonstrated
  (`f`-orbit → parabolic point vs `g_α` kicks it back into the repelling petal). **The
  whole faithful-limit math chain is now proven.** S4 surfaced the **render-cost wall**:
  one `g_α` eval ≈ 69 ms → a naïve `J(g_α)` render ≈ 250 h → Tier C must
  **precompute/interpolate the Fatou coordinates + inverses on a grid** (new constraint
  §4.6; also fixes the occasional Newton-inverse miss). The `J(g_α)` image is deferred to
  the Tier C engineering build (S5/S6); it is no longer a math risk.
- **2026-09-20** — **S3 done — Fatou coordinates + Écalle–Voronin horn map, verdict
  GREEN (§9).** Built and validated the Tier C core for `c = 1/4` (germ `w+w²`, one
  petal): `Φ_att`/`Φ_rep` satisfy the Abel equation to ~4e-11, real symmetry exact.
  The horn map, assembled by **analytic continuation through the (non-overlapping)
  petal gap**, is **nontrivial** — `d = τ − σ ∝ ζ = e^{2πiσ}` with `|c₁| ≈ 0.06`, the
  ratio constant to 0.3% over 2.3 decades of `|ζ|`, multiplier `μ = 1`. **The
  project's flagged main research risk (horn-map correctness) is retired for the
  simplest parabolic.** Numeric recipe (exact `Z↦Z²/(Z−1)` asymptotic, rationalised
  `f⁻¹`, per-petal log branch, iterate-into-petal continuation, `Im σ∈[1.2,2.2]`
  measurement band) recorded as Tier C constraints (§4). Next: S4 (Lavaurs map +
  faithful limit render), which adds the two unknowns S3 deferred — the Lavaurs phase
  `α` and higher-`q` petals — on top of this proven foundation.
- **2026-09-20** — **S2 done — near-parabolic accuracy, verdict GREEN (§9).**
  Measured double vs QuadDouble at `c = 1/4`: the parabolic **normal form**
  `w = z − z*`, `w_{n+1} = w + w²` holds ~1e-14 rel. error vs QD across n up to 10⁶
  (neutral multiplier → no error growth; `1/n` law reproduced), whereas forming
  `z − z*` by subtraction loses ~6 digits to cancellation. Conclusion: **double
  suffices** for Tier C's near-parabolic integration *provided it uses the shifted
  `w` coordinate* (now a Tier C design constraint, §4); DD/QD is an opt-in margin.
  The numerics are not the risk — horn-map correctness (S3) is.
- **2026-09-20** — **S1 shipped — Tier A naïve implosion animation.** Added an
  optional **centre** to the Complex polar (Lissajous) sweep (`AnimationTrack.CenterX/Y`
  → `c = centre + r·e^{iθ}`; default (0,0) byte-identical) so the Julia `c` can circle
  a parabolic `c₀ ≠ 0`. Three built-in parabolic Julia **regions** (`c = 1/4`,
  `−3/4`, 1/3-bulb root) + three matching built-in **"Parabolic implosion (…)"**
  animations that circle `c` around `c₀`. The Julia set discontinuously reorganises
  (connected ↔ dust) as `c` crosses `∂M` — the naïve explosion, on the existing
  Julia path, no new math. +2 tests, suite 2879 green.
- **2026-09-20** — **S0 feasibility spike done — verdict GO (§9).** De-risked Tier A
  (the naïve explosion renders with plain `z² + c`; visible discontinuous
  reorganisation as `c` circles `c₀ = 1/4`) and the Tier C entry primitive (the
  attracting Fatou coordinate at `c = 1/4`: `u(f(z)) − u(z) → 1` within < 0.02 in
  double precision). Module boundary fixed (S1 = existing Julia path + preset library,
  no new module; Tier C = bespoke standalone Fatou/horn-map module). Next: S1.
- **2026-09-19** — Doc created. Scopes RnD §5 parabolic implosion **and** §3.3
  positive-area Julia (the static sibling) as **one** research thread, on the
  finding that they share the Écalle–Voronin / parabolic-renormalisation core (and
  that §3.5 statistical/SLE is disjoint — deliberately excluded). Three-tier render:
  Tier A naïve animation / Tier B near-parabolic accuracy / Tier C faithful
  horn-map limit. Spike-gated S0–S6; **S1 is the achievable first increment** (naïve
  implosion animation on the existing Julia calculator, ships without the deep
  core). Toolchain: DSL out, animation/scene in. Spike/design issue
  [#911](https://github.com/AloneButUnsober/FracturingFog/issues/911) filed and
  cross-linked.
