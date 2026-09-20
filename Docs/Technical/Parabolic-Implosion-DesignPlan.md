# Parabolic Implosion — Research Design Plan

**Tracking issue:** [#911](https://github.com/AloneButUnsober/FracturingFog/issues/911)
(a spike/design issue, not an implementation issue).
Part of frontier epic [#850](https://github.com/AloneButUnsober/FracturingFog/issues/850);
the flagship far-future item of [Theoretical-Fractal-RnD.md](Theoretical-Fractal-RnD.md)
§5. **This doc also scopes §3.3 (positive-area Julia sets, Buff–Chéritat) as the
*static sibling* of the same machinery — one research thread, not two.**

**Status: RESEARCH DESIGN PLAN — spike-gated. S0 (feasibility, verdict GO) + S1
(Tier A naïve implosion animation) SHIPPED (2026-09-20).** The visible naïve
implosion now ships (built-in parabolic Julia presets + "Parabolic implosion"
animations); **the deep tiers (S2 near-parabolic accuracy → S3 horn maps → S4
Lavaurs limit) remain research, gated on validation against the literature — not
scheduled.** This is the north-star plan, not a build schedule. Per RnD §5/§6.5, the
faithful-limit work always proceeds spike-by-spike, never direct implementation.

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
byproduct (same core, static output).

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
- **S2 — Near-parabolic accuracy spike (Tier B).** Accuracy characterisation near
  multiplier = 1; validate the DD/QD orbit-integration floor. *(deps: S0)*
- **S3 — Fatou coordinates + horn map (Tier C core).** Implement `Φ_att` / `Φ_rep`
  + the Écalle–Voronin horn map for the simplest case (`c = 1/4`, one petal).
  Validate against the known horn-map structure. **The main research risk lives
  here.** *(deps: S2)*
- **S4 — Lavaurs map + faithful limit render.** Compose `L_α`; render `J(g_α)`;
  validate against Lavaurs's limit theorem. *(deps: S3)*
- **S5 — Positive-area Julia static render (§3.3 sibling).** Use the S3/S4 core to
  render a Buff–Chéritat positive-area Julia set (static). *(deps: S3/S4)*
- **S6 — Faithful implosion animation (Tier C animated).** Animate `α` / `θ`
  through the horn-map machinery — the faithful counterpart of S1. *(deps: S4)*

**Recommended order:** S0 → **S1 (ship the visible naïve animation, pause)** →
S2 → S3 → S4 → S5 → S6. S1 is the only slice that lands without the deep core; it
buys a real result while S3 (the hard math) is de-risked.

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

## 9. S0 feasibility findings (2026-09-20) — verdict **GO**

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

---

## 10. Change log

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
