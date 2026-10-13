# Interesting-Location Finder — Design Plan

**Tracking epic:** [#1184](https://github.com/AloneButUnsober/FracturingFog/issues/1184).
Slices: S1 [#1185](https://github.com/AloneButUnsober/FracturingFog/issues/1185) ·
S2 [#1186](https://github.com/AloneButUnsober/FracturingFog/issues/1186) ·
S3 [#1187](https://github.com/AloneButUnsober/FracturingFog/issues/1187) ·
S4 [#1188](https://github.com/AloneButUnsober/FracturingFog/issues/1188) ·
S5 [#1189](https://github.com/AloneButUnsober/FracturingFog/issues/1189) ·
S6 [#1190](https://github.com/AloneButUnsober/FracturingFog/issues/1190) ·
S7 [#1191](https://github.com/AloneButUnsober/FracturingFog/issues/1191).
The issues are the canonical task list; this doc is the maths + architecture context. Link both ways.

**Status: S1–S6 shipped (#1185–#1190, PRs #1232/#1234/#1236/#1238/#1239/#1241); S7 implemented (#1191) — 2026-10-08.** See §9.1 for what implementation changed versus this design.

---

## 1. Purpose

Exploring by hand means zooming at random until something catches the eye. Most of the
plane is either solid interior or smooth exterior. The goal is for the application to
*assist* in finding **interesting locations**: regions with dense filaments, spirals,
branch points, minibrots and embedded Julia sets — the places a human would screenshot.

There are two complementary tracks:

| Track | Works on | Idea |
|---|---|---|
| **Exact (theory)** | z²+c first; then Multibrot, holomorphic User Equations, Burning Ship / Tricorn (S7) | Interesting structure sits at mathematically special parameters: **nuclei** of hyperbolic components (minibrots) and **Misiurewicz points** (spiral / branch hubs). Locate them exactly with Newton's method; navigate by **external angles** (the "left/right" paths); compose shapes with **Julia morphing**. |
| **Heuristic** | Every 2D family | Score a cheap probe render for "detail" (boundary density, iteration entropy, fractal dimension) and search downward toward high scores. |

The tracks combine: the heuristic finds a promising area, then the exact track snaps to a
crisp centre inside it.

## 2. What already exists in FF

- **Deep precision:** `Abstractions/Math/DeepComplex.cs` (octuple-double, ~124 digits) is
  the view-centre representation; `Abstractions/ViewState/ViewCamera.cs` drives navigation.
- **Perturbation reference orbits:** `Engine/Calculators/MandelbrotCalculator.cs`,
  `Engine/Calculators/Gpu/MandelbrotRefOrbitGpu.cs`.
- **Interior period detection** for colouring: the Atom Domains / Multiplier interior
  themes (`Engine/Models/ColorSchemes/InteriorThemes.cs`). These detect a *pixel's* cycle,
  not the component nucleus, but prove the cycle-detection plumbing.
- **Symbolic derivatives:** `CalculatorGen/Parser/AstDifferentiator.cs` exposes `DpDz` and
  `DpDc` — exactly what Newton needs for user equations (S7).
- **UI home:** Control Center Explore section
  (`UI.Avalonia/Views/ControlCenterSections/ExploreSectionView.axaml`).
- **Precedent for UI-free maths in Abstractions:** `Abstractions/Animation/ParabolicImplosionMath.cs`.

Nothing yet finds nuclei, Misiurewicz points or external angles, or scores views.

## 3. Exact track (f_c(z) = z² + c)

Notation: f_c(z) = z² + c, z₀ = 0, zₙ = fⁿ_c(0). Derivative with respect to c:
dz₀ = 0, dzₙ₊₁ = 2zₙ·dzₙ + 1.

### 3.1 Period detection — S1 #1185

Question: *which minibrot (hyperbolic component) has its nucleus inside this view, and what
is its period?* Answer the lowest period p for which the view contains a root of fᵖ_c(0).

**Ball method.** Cover the view by a disk with centre c₀ and radius ρ. Track a ball around
the orbit of c₀ that contains the orbits of every c in the disk:

```
z ← z² + c₀
r ← 2|z|·r + r² + ρ          // |(z+e)² + c₀ + δ − (z² + c₀)| ≤ 2|z||e| + |e|² + |δ|
```

The first n ≥ 1 with |zₙ| < rₙ is the candidate period p (the ball contains 0, so some c in the
disk has fⁿ_c(0) = 0). Stop with "none" when r exceeds the escape radius or n > pMax.

**Box method** (Munafo): iterate the four view corners; p is the first n where the image
quadrilateral winds around 0. Optional cross-check.

Ball arithmetic over-approximates, so p is a *candidate*. S2's Newton confirms it.

### 3.2 Nucleus by Newton's method — S2 #1186

Solve F(c) = fᵖ_c(0) = 0, with F′(c) = dzₚ:

```
loop until |Δc| < tol:
    z = 0; dz = 0
    repeat p times: dz = 2·z·dz + 1; z = z² + c
    Δc = z / dz;  c = c − Δc
```

Convergence is quadratic near the root. Seed with the view centre (or S1's disk centre).
Use DeepComplex for c and z: the nucleus is needed to about the zoom's number of digits plus
margin, so OD supports roughly 1e-115. Beyond that, cap with a clear message. A
*perturbed* Newton (iterate δc against a reference) is the follow-up for deeper zooms.

Cost: O(p) per step, ~5–20 steps. At p = 10⁵ this is ~10⁶ OD complex ops — acceptable
async, but needs cancellation and progress.

**Acceptance invariants** (independent, not self-consistency):
1. |fᵖ(0)| below tolerance at the result.
2. **Minimal period:** no proper divisor d of p has |f^d(0)| ≈ 0. Otherwise Newton found a
   lower-period root that also solves fᵖ = 0.
3. The result lies inside the original view. If Newton wandered off, refuse to navigate.

### 3.3 Atom size and framing — S2 #1186

A closed-form estimate of the minibrot's size *and orientation*, evaluated at the nucleus:

```
z = 0; l = 1; b = 1
for q = 1 .. p−1:  z = z² + c;  l = 2·z·l;  b = b + 1/l
size = 1 / (b · l²)          // complex: |size| ≈ radius, arg(size) ≈ orientation
```

Framing: centre = nucleus, view half-height ≈ k·|size| (k tunable, ~2–4), optional camera
rotation by −arg(size) so every minibrot appears "upright" like the main set.

**Bonus:** a nucleus is the ideal perturbation reference point. Its orbit is periodic, so it
never escapes, which removes the main reference-glitch cause. Offering it to the reference
orbit selection is a follow-up hook, not S2 scope.

### 3.4 Misiurewicz points — S3 #1187

A **Misiurewicz point** M(k,p) is a c whose critical orbit is *strictly preperiodic*:
f^(k+p)(0) = f^k(0), with k ≥ 1 the preperiod and p the period, both minimal.
Examples: c = i (0 → i → −1+i → −i → −1+i …: k = 2, p = 2), c = −2 (0 → −2 → 2 → 2: k = 2, p = 1).

**Why they are interesting (Tan Lei, 1990):** near a Misiurewicz point, the Mandelbrot set
is asymptotically self-similar and looks like the Julia set J_c at that point. These are the
spiral centres and branch hubs of filaments. Zooming into one by the multiplier
λ = (fᵖ)′(z_k) = Π 2zᵢ (over the cycle) repeats the picture, rotated by arg λ.

**Finding (k, p) from a click:** iterate the clicked c and look for the near-return
minimising |z_(k+p) − z_k| over a bounded (k, p) range. Present the top few candidates.

**Newton:** G(c) = f^(k+p)(0) − f^k(0), G′(c) = dz_(k+p) − dz_k. Plain Newton on G also
converges to roots of lower preperiod and to component centres whose period divides p.
Heiland-Allen's remedy: divide G by the factors for those unwanted roots before taking the
Newton step (equivalently, subtract their contributions to G′/G). Re-verify the minimal
(k, p) after convergence.

**Self-similar loop video** (S3 bonus, may split out): zoom by |λ| per cycle while rotating
by arg λ → a seamless, infinitely looping zoom. Render-affecting → needs a batch flag.

### 3.5 External angles, internal addresses and "left/right" paths — S4 #1188

The exterior of the Mandelbrot set is conformally a disk (Douady–Hubbard). **Parameter
rays** at angle θ ∈ [0, 1) come in from infinity; rational θ rays land on the set:

- **periodic θ** (purely repeating binary, e.g. 1/7 = .(001)) lands on the **root** of a
  hyperbolic component of that period;
- **preperiodic θ** (e.g. 1/6 = .0(01)) lands on a **Misiurewicz point** (1/6 → c = i).

Angle doubling θ ↦ 2θ mod 1 is the dynamics on the circle, so the n-th binary digit of θ
records which half of the circle (θ < ½ or θ ≥ ½) the angle lies in after n−1 doublings. That
is an L/R itinerary. **That is the "left/right zoom path" folklore made precise.** A
sequence of L/R choices is an angle; an angle is a location.

Navigation aids:

- **Angle → location:** trace the ray inward. For decreasing radii r_m (S steps per binary
  digit), Newton-solve fⁿ(c) = r_m·e^(2πi·2ⁿθ) starting from the previous point. Then finish
  with the S2/S3 Newton at the landing point.
- **Location → angle:** trace outward from just outside the component root and read the
  digits from the half-plane each iterate falls into.
- **Internal addresses** (`1 → 2 → 4 → …`, Lau–Schleicher) are a more human-readable
  coordinate: the chain of lowest-period components on the way from the main cardioid.
  The angled form converts to and from angle pairs (Lavaurs' algorithm / kneading
  sequences). **Douady–Hubbard tuning** composes addresses, so one minibrot's address can
  be nested inside another's.

### 3.6 Julia morphing and shape stacking — S5 #1189

Empirical technique, well documented by fractal artists (Heiland-Allen calls it "Julia
morphing"): near a minibrot M of period P there is an **embedded Julia set**, a copy of
J_c for c at M, appearing at roughly the **log-scale midpoint** between the view depth where
M was chosen and M's own size (scale ≈ √(current · |size_M|)). If you zoom toward M
*through* an off-centre target T rather than straight at it, the structure around T is
doubled. Repeating with a new target each time **stacks** shapes: trees, X-forms, doubled
spirals, "layers".

Automatable per step:

1. User clicks T (or presets: left / right / tip / spiral side).
2. S1 period detection on a small disk around T → Q; S2 Newton → nucleus N_Q, atom size s_Q.
3. New view: centre N_Q, scale = current^(1−α) · |s_Q|^α with α ≈ 0.5 (tunable; calibrate
   against reference images in S5).
4. Append (T, Q, N_Q, scale) to the morph path; undo/redo.

A morph path doubles as zoom-video keyframes. Periods grow with each step (roughly
doubling plus the base period), so S2's cost and precision ceiling bound how many layers
are practical. Expect 5–15 at OD precision.

## 4. Heuristic track — S6 #1190

Theory-free, so it works for any 2D family: Burning Ship, Newton/Nova/Halley basins,
Lyapunov, IFS, User Equation and so on.

**Interest score** on a small probe render (e.g. 96×96, the family's normal calculator,
lowered maxIter):

| Term | Measures | Notes |
|---|---|---|
| Boundary fraction | Share of pixels with DE < k·pixel, or a large smooth-iteration gradient | Filament density |
| Band entropy | Shannon entropy of the smooth-iteration histogram | Rewards many bands, penalises flat regions |
| Box-count dimension | Slope of log N(boundary boxes) vs log(1/s), s = 2…32 | Preference band ~1.5–1.9; 1 = smooth curve, 2 = noise |
| Penalties | Inside fraction above threshold, trivially-escaped fraction, maxIter saturation, precision floor | Avoid blobs, emptiness and undersampled views |

All weights and thresholds are exposed as tunable parameters, per the project preference.

**Search:** beam search with width B. Each node spawns K children (3×3 grid of sub-views
plus seeded jitter) at zoom factor Z, up to depth D. Keep the top B by score at each level.
Deterministic from a seed. "Surprise me" = random start from the family's home view plus
descent. "Descend" = start from the current view. For Mandelbrot-family views, optionally
finish with an S2/S3 snap.

Outputs are regions (centre, scale, family params), so they plug into the region list and
slideshows directly. An optional score-heatmap overlay helps tune the weights.

## 5. Generalisation — S7 #1191

| Family | Exact track? | How |
|---|---|---|
| Multibrot z^d + c | Yes | dz ← d·z^(d−1)·dz + 1; atom size generalises; external angles in base d |
| User Equation (holomorphic) | Nucleus + Misiurewicz | `AstDifferentiator.DpDz` / `DpDc` supply the derivatives. Start at a critical point (solve f′(z) = 0 by Newton) instead of 0; with several, let the user pick. No angles. |
| Burning Ship / Tricorn | Nucleus (+ Misiurewicz) | Real 2-D map; Newton in R² with the 2×2 Jacobian in (cx, cy). The abs-sign pattern is constant inside the basin (Kalles Fraktaler's approach). Tricorn is anti-holomorphic: use the second iterate. |
| Non-holomorphic User Equation, IFS, Kleinian, scattering, Newton-family basins | No | S6 heuristic only; exact-track buttons hidden |

Newton-family fractals (Newton/Nova/Halley) are a different case: their interesting areas
are where root basins meet. S6 handles them directly. Their *parameter planes* (free
critical point) contain Mandelbrot copies, which is a possible future exact-track target.

## 6. Architecture and integration

```
Abstractions/Explore/          UI-free, unit-testable, DeepComplex-based
    PeriodDetector             S1
    NucleusFinder, AtomSize    S2
    MisiurewiczFinder          S3
    ExternalRay, InternalAddress  S4
    MorphPlanner               S5
Engine/Explore/                needs calculators
    InterestScorer, AutoExplorer  S6
UI.Avalonia  Control Center Explore section: "Find" group
    Detect period · Zoom to minibrot · Snap to spiral (click mode)
    Angle/address bar · Julia morph (click mode + step list) · Surprise me / Descend
```

- **Navigation** goes through `ViewCamera` (centre DeepComplex + scale + rotation). Never
  bypass it.
- **Threading:** every finder is async with a `CancellationToken` and progress; the UI
  stays responsive. A new view request cancels a running search.
- **Precision ceiling:** the OD limit (~1e-115) is surfaced to the user, not hidden.
- **Clean-room:** implement from the published maths (sources below). Do **not** copy code
  from mandelbrot-numerics or Kalles Fraktaler; their licences are incompatible.

### Batch parity

Per the project rule, every render-affecting feature must be reproducible with `--batch`
and the Command builder.

- **Finders only navigate** (S1–S4). Their result is a view, already expressible as
  `--x/--y/--zoom`, and the Command builder's live seed captures it. S2 adds a round-trip
  test at OD precision.
- **Content generators** need flags in the same change: auto-explore (`--explore
  seed=…,depth=…,beam=…`, S6), morph-path video (`--morph-path`, S5) and the Misiurewicz
  loop (`--video-motion misiurewicz-loop`, S3 bonus). Each needs the `BatchFlags` const,
  `BatchOptions` parse/validate, `BatchRenderer` apply, `BatchFlagCatalog` entry,
  `BatchCommandBuilder` emit and a round-trip test, or else a `DetectGaps` banner plus an
  issue.

## 7. Validation strategy

Check must-hold invariants, not self-consistency:

| Check | Fixture / invariant |
|---|---|
| Period detection | c = −1 → 2; −1.7549 → 3; main-cardioid view → 1; a deep minibrot from `Resources/regions.json` → its known period |
| Nucleus | \|fᵖ(0)\| ≈ 0, **minimal** p, result inside the view; −1, −1.7548776662466927, −0.1225611668766536 + 0.7448617666197442i |
| Misiurewicz | f^(k+p)(0) = f^k(0) with minimal (k, p); c = i (2, 2), c = −2 (2, 1), c ≈ −0.10109636 + 0.95628651i |
| External rays | 1/3, 2/3 → root −0.75; 1/7, 2/7 → period-3 components; 1/6 → i; angle → c → angle is the identity |
| Interest score | All-inside and all-outside views score ≈ 0; seahorse and elephant valleys outscore the cardioid interior; same seed → same path |
| Batch | A finder- or morph-produced view renders the same live and headless |

## 8. Risks

| Risk | Mitigation |
|---|---|
| Newton converges to the wrong root (lower period, outside the view) | Seed from S1; enforce the minimal-period and inside-view invariants; refuse rather than mis-navigate |
| High periods (10⁴–10⁶) are slow in OD | Async + cancel + progress; perturbed Newton as a follow-up |
| Precision ceiling at ~1e-115 | Surface it; perturbed Newton follow-up |
| "Interesting" is subjective | Tunable weights, heatmap overlay, several candidates rather than one answer |
| Julia-morph scale constant is empirical | Calibrate α in S5 against reference images; keep it tunable |
| Ball method over-approximates p | Treat p as a candidate; S2 confirms |

## 9. Slices

| Slice | Issue | Depends on | Delivers |
|---|---|---|---|
| S1 | #1185 | — | Period detection + readout |
| S2 | #1186 | S1 | Nucleus Newton + atom size → "Zoom to minibrot" |
| S3 | #1187 | S2 | Misiurewicz snap (+ loop-video bonus) |
| S4 | #1188 | S2 | External angles, internal addresses, L/R navigation |
| S5 | #1189 | S2 (S4 optional) | Julia-morph assistant + morph-path video |
| S6 | #1190 | — | Interest score + auto-explore for all 2D families |
| S7 | #1191 | S2, S3 | Multibrot, User Equation, Burning Ship / Tricorn |

Critical path: S1 → S2 → {S3, S4, S5}. S6 runs in parallel. S7 comes last.

### 9.1 Implementation notes (S1–S2)

Measured against all 158 built-in Mandelbrot regions (`Resources/regions.json`):

- **The ball period is a lower bound, not "the minibrot in view".** On a whole
  view the ball scan over-approximates: for 63 of 158 regions its period-p₀
  nucleus lies outside the view. After its first hit the ball saturates (every
  later n also "contains 0"), so later candidates carry no information. The real
  lowest period found in view is often several times p₀ (116 → 261,
  53 → 466, 351 → 427). The S1 button is labelled accordingly.
- **S2 therefore scans periods upward from p₀** (`NucleusFinder.FindMinibrot`):
  Newton from the view centre for each p, the first root inside the view wins.
  The scan runs as **perturbed Newton in double** against one OD reference orbit
  at the centre (εₙ₊₁ = 2Zₙεₙ + εₙ² + δ), so each try is O(p) double ops at any
  depth. A proposal is then polished and verified in OD (minimal period, inside
  view) before it is accepted. Result: 157/158 regions found, every one
  re-confirmed at its period by the ball scan on a disk 1/100 the minibrot's
  size. Worst found case ≈ 1.1 s; zoom 1e43 works. The one miss ("Bird of
  Paradise", p₀ = 86 570) is stopped by a work budget (~5 s) instead of
  scanning for minutes. Quadtree localisation by sub-disk ball scans was tried
  first and abandoned: sub-disks inherit the same false positive.
- **OD's `+` is the sloppy variant and floors at ~1e-65 under cancellation.**
  z² + c → 0 at a nucleus is exactly that case, so Newton stalled.
  `Abstractions/Explore/OdExact` adds b limb by limb through OD's full-cascade
  `OD + double`; S1's deep path uses it too.
- **Iterations:** a minibrot framed at its own size needs ~100 × period
  iterations; 10 × leaves it a black smear (checked live). The jump sets this as
  a region-style first-render hint, capped at the Extreme tier's 131 072, and
  promotes (never demotes) the quality tier for the depth.
- **Batch:** the jump is plain centre / zoom / quality / iterations. Deep
  centres exposed a pre-existing gap — `--x/--y` are double only — now reported
  by `BatchCommandBuilder.DetectGaps` once the dropped limbs shift the render by
  ≥ 0.1 px. #1233 has since closed the gap: `--x/--y` take octuple-double
  limbs `Hi|Lo|…`, the builder emits them for deep views, and the gap entry is gone.

### 9.2 Implementation notes (S3)

- **Candidates from one orbit.** At the clicked point, the first-order distance
  to a root of z_{k+p} − z_k is |Z_{k+p} − Z_k| / |dZ_{k+p} − dZ_k|, for every
  (k, p) at once. Because (k′ ≥ k, p) and (k, multiples of p) are roots too,
  only the smallest k per p is kept, and candidates are tried by k + p.
- **Exact reference differences.** The near-cancelling differences
  Z_a − Z_b must come from the OD orbit (`OdExact.Sub`), not from double copies
  of Z. With double copies, ~1e-16 absolute noise hid every candidate at depth
  and blocked convergence even on shallow points. The perturbed Newton
  therefore uses (Z_a − Z_b exact) + (ε_a − ε_b).
- **Deflation + verification** as designed. Newton is deflated against lower
  preperiods (i = 0 removes hyperbolic centres) and divisor periods. The OD
  polish then re-checks (k, p) minimality independently.
- **Click-mode** is a one-shot `IFractalInputController.PointPickHandler`
  (cross cursor while armed). Snap keeps the zoom and only recentres.
- **Measured.** Snapping at the centres of the 158 built-in regions (48 px
  reach) found a hub for 106; every hit's OD residual was ≤ 1e-117. Worst case
  was ~5.5 s. The seahorse spiral snaps to the published −0.77568377 + 0.13646737i
  as M(24,1), λ ≈ 1.039∠172.6°.
- **Deferred:** the J_c preview and the self-similar loop video are tracked
  separately as #1235.

### 9.3 Implementation notes (S4)

- **`ExternalAngle`** keeps θ exactly as binary `.pre(per)` (canonical, shortest
  form) and converts to and from p/q with BigInteger arithmetic. The kneading
  sequence and internal address (Lau–Schleicher ρ) are computed with exact
  rational comparisons.
- **`ExternalRay.Land`** traces inward in double (start radius 2¹⁶, 8 substeps
  per bit, Newton on f^{k+1}(0) = r·e^{2πi·2ᵏθ}), then hands the end point to
  the verified OD Newton of S2 (periodic) or S3 (preperiodic).
  - **Periodic rays creep into a parabolic root**, so per-period movement says
    nothing about the component's size. They are traced to the bit budget, and
    the nucleus is accepted only within 2 atom sizes of the root (main cardioid
    0.25, period-2 bulb 0.5); a neighbouring component's nucleus never is.
    An early "barely moving" stop left seeds several sizes away.
  - **A preperiodic angle's landing point can have a smaller period** than
    the angle (e.g. `.010(001)` → M(4,1)). S3's Refine deflates divisor periods
    out, so landing tries every divisor of the angle period, smallest first.
- **Measured on random angles:** periodic angles land reliably to about 16–20
  bits and preperiodic ones to 32+, with **zero wrong landings**. Longer
  periodic angles fail cleanly at double precision. OD tracing, location →
  angle, and address → angle are #1237.

### 9.4 Implementation notes (S5)

- **`JuliaMorph.Plan`**: target = a minibrot near the click, found by S2's
  verified `FindMinibrot` in a 48 px disk. New centre = its nucleus; new zoom
  = z₀^(1−α)·(1/|size|)^α with α = ½ (tunable). A target that is not deeper
  than the view is refused, which is typically the click landing on a big bulb
  of the current minibrot. Tier promotion and the 100 × period iteration hint
  are shared with S2 through `MinibrotJump.At`.
- **Checked live** from *Lakes and Rivers*: two clicks gave periods 669 → 1139
  (zoom 1.3e8 → 9.5e12). Each layer shows the point symmetry about the new
  centre that marks an embedded Julia set. Test: a deterministic 3-step chain
  stays nested, zoom strictly increases, and S1 confirms every period.
- **No new batch flag.** A morph path is navigation ending at one view, and a
  plain zoom video into that view passes through every layer. So the morph
  video is the existing Video zoom / `--video-motion zoom --x --y --zoom`.
  Deep centres reproduce exactly since #1233 (`--x/--y` limb form).
- **Undo** = nav Back plus restoring that layer's iteration hint (Back clears
  the hint; without it the restored layer smears).

### 9.5 Implementation notes (S6)

- **Placement.** `InterestScorer` and `AutoExplorer` live in `Abstractions/Explore/`
  (pure, UI- and Engine-free, unit-tested with synthetic fields), not
  `Engine/Explore/` as planned. Only the probe renderer, `Engine/Explore/ExploreProbe`,
  needs the Engine: it builds the poster/batch calculator for the family
  (`PosterRenderer.BuildCaptureCalculator`, or `MandelbrotCalculator` with the full
  octuple-double centre). The UI reaches it through `Shell.CreateExploreProbe`, wired
  by the host.
- **Field.** The smooth-iteration buffer when the family has one (in-set = 0 gives the
  inside mask); otherwise rendered luminance. Relief, lighting and post-FX are not
  applied.
- **Score.** Boundary is a preference band (rises to 1 at 30 % boundary pixels and
  falls past 60 %, where views turn into pixel noise). Entropy is over 1-iteration
  bands (1/32 luminance bands). Dimension is a Gaussian around 1.7 (σ 0.35), trusted
  once 24+ boundary pixels exist. The penalties multiply: inside > 60 %, one escaped
  band > 60 %, near-maxIter escapes > 2 %. All of these are fields on `InterestWeights`.
- **Search.** Square probes (96 px), a 3×3 grid of cell centres plus 2 seeded jitter
  children per node, step 4×, beam 3, depth 6. Kept views are at least half a child
  width apart, and the search stops when a level's best child scores < 0.05. The
  depth is capped at 1e100 for Mandelbrot and 1e13 for everything else.
  "Surprise me" uses a random seed and 16 first-level jitter children from the home
  view. The final S2/S3 snap is left to the existing Find buttons.
- **Supported:** every 2D family except the Buddhabrot family, Flame, Plasma, Acid
  Warp, DLA and Random Tile (stochastic or not pannable).
- **Validated:** all-inside, uniform-escape and flat-colour fields score 0. Box
  dimension is 1 for a line and 2 for a filled square. Seahorse valley (0.79) and
  elephant valley (0.59) beat the cardioid interior and empty space (both 0). The same
  seed gives the same path. A 5-level descent from home lands where independent plain
  escape-time iteration counts span 28 to 447. Burning Ship, Tricorn, Newton, Lyapunov
  and Julia all descend to views scoring > 0.8. Checked live in the app: Surprise me
  (Mandelbrot), Descend (Burning Ship), finalist buttons, Backspace.
- **Batch:** `--explore SPEC` (image and video) and `--explore-regions OUT.json` (the
  finalists as an importable plain region list). These are catalog flags, so the
  Command panel offers them. The live seed reproduces an explored view by coordinates.
- **Heatmap + live weights** ([#1240](https://github.com/AloneButUnsober/FracturingFog/issues/1240)).
  - **Scoring:** Explore ▸ Find ▸ Auto-explore ▸ **Heatmap** splits the view into 10 ×
    (aspect) cells. Each cell gets a 48 px probe render of its own; four render in
    parallel. Each cell is scored by `InterestScorer`.
  - **Drawing:** the render host blends the result into the live frame
    (`FractalOverlayCompositor.CompositeHeatmap`, under the grid). It is diagnostic
    only and never exported. The native GPU surface occludes Avalonia overlays, so the
    heatmap is composited into the frame like the grid. It is drawn only while the view
    is the one it was scored for. A 400 ms poll re-probes once the view has changed and
    then held still.
  - **Colours:** a colour-blind-safe ramp, deep blue → grey → yellow (cividis-like).
    Luminance rises strictly, so the order reads without hue; the test checks this.
    Each cell is labelled with its value when the cell is ≥ 56 px wide.
  - **Terms:** total score, boundary, entropy, dimension, or "penalty keep"
    (total ÷ weighted base).
  - **Live weights:** sliders set the boundary, entropy and dimension weights and the
    preferred dimension, with Reset. The probes are cached, so a slider re-scores at
    once without re-rendering. The same weights drive **Surprise me** / **Descend**.
  - **Batch parity:** the `--explore` spec gained `wb`, `we`, `wd` and `dc`; only values
    that differ from the defaults are emitted, with a round-trip test. The heatmap
    itself is not part of any export, so it has no flag.
  - **Checked live:** in seahorse valley at zoom 40, interior cells read 0.00, filament
    cells 0.7–0.96 and smooth exterior about 0.07. Dragging the boundary weight to 0
    re-scored at once and updated the shown spec (`wb=0`).

### 9.3b Implementation notes (S4b, #1237: deep rays, location → angle, address → angle)

- **Deep rays.** The inward trace is perturbed. Each bit rebases an OD reference to the
  ray point and computes its exact orbit Z_n. The substeps solve for a double offset
  with ε ← 2Zε + ε² + Δ. The double trace had lost periodic rays after about 16–20 bits.
  A periodic ray creeps into its parabolic root, so it is walked
  max(64, 10·p) bits; at 4p bits it was still about 10 atom sizes away. Preperiodic
  traces stop once a period moves the ray by less than 1e-8 of its first cycle.
  - **Validated:** 3 random periodic angles each of 24, 32, 48 and 64 bits land, and
    S1 re-confirms the period every time. Components reach |size| 1e-40, in 0.5–4 s.
- **Location → angle** (`LocationAngles`).
  - **Root:** the interior coordinate b runs from 0 to 1, with Newton on
    (z₀, c): f^p(z₀) = z₀, (f^p)′(z₀) = b, perturbed about the nucleus orbit. At b = 1
    the root is a double root, so Newton is linear there and the solve is loosened to
    1e-9 relative.
  - **Probes:** the exterior near a root is a thin cusp. So a dense ring (128 directions
    × 4 radii) is escape-tested by perturbation about the root's orbit. The shallowest
    probes still at least 4p deep are traced outward first, since the outward trace
    costs depth² and deepest-first took minutes.
  - **Outward trace:** Newton on c raises r to the escape radius at depth k. The digit
    b_{k−1} is chosen so that (φ + b)/2 matches arg f^k(0); then k drops by 1.
  - **Verification:** every candidate angle is landed again and kept only if it returns
    to the same component or point.
  - **Validated:** the round trips (angle → land → angles) give exact pairs for 1/3,
    the rabbit, the airplane, 2/5 and the period-4 bulb. Random period-12, 16 and 20
    components recover pairs that contain the original angle and share an address.
    Misiurewicz points recover the original angle, including all three rays of an
    M(4,1).
- **Address → angle** (`AddressAngles`), combinatorial and exact.
  - Main k/m bulb: the m-cycle with rotation k/m has digits
    [(i + j·k) mod m ≥ m − k]; the bulb's angles bound its shortest gap.
  - Sub-wakes: tuning (0 → w₋, 1 → w₊).
  - Each step s → q goes into a k/m sub-wake with s·m ≥ q. It lands on the satellite
    when s·m = q, otherwise on the period-q angles in the sub-wake whose own internal
    address (S4's ρ function) is the prefix → q, paired consecutively. Steps are
    searched depth-first with backtracking, because a plain address leaves conjugates
    open.
  - **Finding those angles.** The address fixes the kneading sequence:
    ν₁ = 1, s_k-periodic up to s_{k+1}, flipped at s_{k+1} (Lau–Schleicher).
    A periodic angle's kneading is its doubling itinerary relative to
    (θ/2, (θ+1)/2). So θ's digits are built depth-first, and a branch is pruned once
    its known digits leave the sub-wake or decide some ν_j wrongly, using exact
    integer interval arithmetic.
    - The first version enumerated every period-q angle in the sub-wake. Along an
      antenna that grows about 8× per step: a smoke-test address
      (1 → 4 → … → 46 → 53, a period-53 minibrot at zoom 7.5e13) hit the enumeration
      cap at 25 → 29 and wrongly reported "not an internal address". The pruned
      search resolves it in about 2.5 s.
  - **Angled output.** The angles line after Zoom to minibrot / Snap shows the
    *angled* address, with every step's sublimb chosen to hold the found angle
    (`AngledAddressOf`). Pasting it back lands on that exact component; a plain address
    resolves to the first conjugate (here the mirror image in the 1/4 limb). The live
    description under the Angle box no longer cuts periodic addresses at 12 terms
    without marking it.
  - **Copy buttons** (added to the design after the smoke test). Under the angles line
    sit **Copy angle**, which copies the found point's lower angle, and **Copy address**,
    which copies the angled address (components only, since a Misiurewicz point has no
    internal address). Both go to the clipboard through `Shell.RequestCopyToClipboard`.
    The line itself stays selectable. Either value pasted into Angle + Go returns to the
    same place, which makes a found location shareable as text.
  - Two simpler rules failed:
    - "lowest period in the sub-wake": the period-4 bulb's wake holds the period-3
      airplane;
    - pairing by lowest-period wakes: wakes of different periods nest arbitrarily.
  - **Validated:** textbook pairs. Addresses from random angles up to period 28 round-trip.
    Resolved angles land on a component whose period S1 confirms, with both angles on
    the same component.
- **Batch:** navigation only, so no flag. A `--angle` flag was optional in the issue and
  is not added.

### 9.5b Implementation notes (S3b, #1235: Misiurewicz loop + J_c)

- **Loop** (`MisiurewiczLoop`): one loop at time t ∈ [0, 1) zooms |λ|^{n·t} and
  turns the camera by −n·t·arg λ. The palette is turned back by n·t·p iterations,
  because the escape count grows by exactly p per cycle. Both facts were checked
  numerically: smooth(M+δ) − smooth(M+λδ) = 2.000 at c = i, with δ from 1e-3 to
  1e-7; using conj λ instead gives random values. The video samples
  t = f / frames, so playback wraps.
- **Rotation in image space** (`MisiurewiczLoopRenderer`). The renderers have no
  plane rotation, so each frame renders a square of side ≥ the frame diagonal at
  the same pixel scale, through `PosterRenderer.RenderToPixels`, so any family and
  relief work. The square is then rotated and cropped bilinearly. The renderers map
  screen y to +Im (`ViewCamera.WorldFromScreen`): assuming Im-up made the loop turn
  the wrong way, which the "wrong-turn" control in the tests caught.
- **Colour.** `ShiftedColorMap` hands the theme smooth − shift. That works for
  every theme; the built-in palette-rotation static is ignored by HSV, the default.
  The iteration cap grows by the same shift.
- **Validated** (4×4 block means over the central half, so the test measures
  geometry rather than per-pixel aliasing):

  | Point | Seam | Mid-loop | Wrong-turn control | No-palette-shift control |
  |---|---|---|---|---|
  | c = i, M(2,2), \|λ\| = 4√2, 45° | 0.004 | 0.044 | 0.107 | 0.080 |
  | Seahorse M(24,1), \|λ\| = 1.04, 172.6°, 19 cycles | 0.03 to 0.04 | 0.41 | 0.40 | 0.50 |
  | −2, M(2,1) (seam only; the exterior is uniform) | 0.001 | | | |

  For the seahorse the remaining seam is a slight hue drift in the core,
  compounded over 19 cycles; the geometry still closes.
- **Batch:** `--video-motion loop` plus `--loop-multiplier MAG,DEG`,
  `--loop-period P` and `--loop-cycles N`. It is batch-only: the shared
  `VideoMotionMode` enum used by live video and slideshows is unchanged.
  `BatchCommandBuilder.LoopVideoArgs` emits the flags, with a round-trip test, and
  **Loop video** loads live command + loop into the Command panel, whose **Run**
  renders through the same code.
- **J_c preview:** the host renders a 240×135 Julia set at the snapped c
  (`Shell.RenderJuliaPreview`, live theme), z² + c only.

### 9.6 Implementation notes (S7)

- **One engine, not three.** Every family is an `IOrbitMap`: a real 2-D map whose step
  returns z′ and the 2×2 Jacobians A = ∂z′/∂z and B = ∂z′/∂c. `GeneralFinder` runs Newton
  in R² on that map, carrying J = ∂z_n/∂c. The maps:
  - `MultibrotMap(d)`: analytic. It covers Multibrot and the Generated z³/z⁴/z⁵.
  - `BurningShipMap`: the Jacobian of the active fold piece.
  - `TricornMap`: anti-holomorphic, handled directly in R², with no second-iterate trick.
  - `UserEquationMap`: the Sandbox DSL step plus the optional z₀ seed, with central
    finite differences. That also covers non-holomorphic equations.
  An equation that depends on `n` / `iter` / `prev` is detected by evaluation and refused.
- **Nucleus acceptance.** All three conditions must hold:
  - z_p(c) = z₀(c), converged inside the view;
  - the period is minimal: no proper divisor q has a root within a scale-aware threshold
    (10⁻⁶ of the reach, floored at 64 ulp);
  - the cycle through z₀ is **attracting** (spectral radius of the cycle Jacobian < 1).
  The last test makes the method sound for any start point, so it replaces the planned
  "solve f′(z) = 0 and let the user pick": z₀ is then attracted to the cycle, so c lies in
  a hyperbolic component of the rendered set. With a critical z₀ the radius is 0.
  Picking another critical point shipped in #1242 (§9.7).
- **Search.** Periods are scanned upward from 1 (lowest period first, as in S2) within a
  work budget. Newton starts at the view centre. Once the centre's orbit escapes too early
  for the period, it starts instead from the point of a 9×9 grid whose orbit survives
  longest. Trying atom-domain periods first, or always starting from the deepest grid
  point, favoured tiny high-period minibrots, so both were dropped. Newton runs through
  escaping orbits and stops only on overflow (|z|² > 10²⁰⁰).
- **Framing.** These families have no closed-form atom size. Instead, the body radius is
  measured by escape: along 12 rays, double until a point escapes, then bisect, and take
  the median. Medians resist rays that run on through an attached bulb; the max over-read
  the cardioid as 1.05. Frame zoom = 0.8 · 0.72 / radius, where 0.72 is the measured median
  of the Mandelbrot main cardioid. A minibrot therefore frames like the home view.
- **Misiurewicz.** Every (k, p) is ranked by the length of a Newton step from the click,
  and the shortest are refined, lowest k + p first. Instead of deflating, the root is
  re-classified to its minimal (k, p). A periodic root (k = 0) is rejected, and the cycle
  must be repelling. A rotation angle is reported only when the cycle Jacobian is conformal.
- **Julia morph** gained a family-agnostic `JuliaMorph.Plan(nucleus, period,
  minibrotZoom, …, maxZoom)` overload.
- **Follow-ups:** base-d angles and the critical-point picker shipped in #1242 (§9.7).
  Deep zoom past 1e13 waits on the families themselves.
- **Validated:**
  - Hand-solved nuclei:
    - z²: −1, the airplane and the rabbit;
    - z³: ±i;
    - z⁴: −1 and e^{±iπ/3};
    - Tricorn: −1 and e^{±iπ/3};
    - Burning Ship: (−1, 0) and (½, −√3/2). The mirror point is correctly rejected,
      because the ship is not symmetric.
  - User Equation `z^2 + c`, `z^3 + c`, the Burning Ship written in DSL, and a z₀ = c seed
    all reproduce the analytic nuclei.
  - Misiurewicz points:
    - i = M(2, 2) of z² with multiplier 4√2;
    - M(2, 1) of z³ at c² = ω − 1;
    - −2 = M(2, 1) for Burning Ship and Tricorn, with multiplier 4.
  - Independent render check: for Multibrot, Burning Ship, Tricorn, their Generated
    variants and User Equation, the found minibrot's nucleus and its 3×3 neighbours render
    in-set in the family's real calculator, and the frame shows exterior.
- **Batch:** navigation only, so no new flag is needed. The Command builder reproduces the
  result by coordinates (double precision suffices below 1e13).

### 9.7 Implementation notes (#1242: base-d angles, critical points)

- **Base-d angles (`BaseAngle`).** For z^d + c the circle dynamics is θ ↦ dθ, so an angle
  is stored exactly as base-d digits `.pre(per)`, with base ≤ 36 (digits 0–9, a–z).
  - A fraction p/q is split as q = q₁·r, where r is coprime to d and q₁ divides d^a. The
    pre part is then a digits long, and the period is the order of d mod r.
  - The canonical form has the minimal period, trailing pre digits folded into the period,
    and a (d−1)-recurring tail rewritten as a terminating angle.
  - In base 2 it reproduces `ExternalAngle` exactly (the test compares them).
- **Rays (`MultibrotRay`).** The same scheme as S4, with f^{k+1}_c(0) ≈ Φ(c)^{d^k}:
  - each digit takes 8 substeps over r = R^{(1/d)^{(j+½)/8}};
  - Newton runs on c with dz/dc = d·z^{d−1}·dz + 1;
  - the trace is plain double, because these families render in double, and it stops once
    the ray stalls at that limit.
- **Landing.**
  - A periodic ray lands on a parabolic point of a period-p component. For d > 2 that is the
    root or one of the d − 2 co-roots: the multiplier map of a component of M_d is
    (d − 1)-to-1, so each component has d − 1 such points. `GeneralFinder.FindNucleus`
    takes the trace end to the nucleus with a growing reach, accepted within 2.5 body radii.
  - A preperiodic ray (preperiod l, period p) lands on M(l + 1, p′) with p′ | p. The new
    `GeneralFinder.FindMisiurewicz(k, p)` refines it: Newton, re-classification to the
    minimal (k, p), and a repelling cycle.
- **Validated (independent facts):**
  - In base 2 the landings equal S4's OD `ExternalRay` (1/3, 1/7, 2/7, 1/15; 1/6, 1/4, 5/12).
  - z³ + c:
    - the period-2 rays 1/8, 1/4, 3/8 land on the nucleus i, and 5/8, 3/4, 7/8 on −i (c³ + c = 0);
    - 0 and 1/2 land at the cusps ±2/(3√3);
    - 1/6, 2/3, 5/6 and 1/3 land on the M(2,1) points 3^{1/4}·e^{iφ}, with c² = ω − 1.
  - z⁴ + c: the twelve period-2 rays land four apiece on the roots of c³ = −1 (a root plus
    two co-roots each).
  - The (d − 1)-fold rotation (θ + 1/(d−1) ↔ e^{2πi/(d−1)}·c) and conjugation (−θ ↔ c̄)
    symmetries hold for d = 4 and 5.
- **Critical points (`CriticalPoints`).**
  - f′ = ∂f/∂z is exact, from the DSL's forward-mode dual numbers (`EvalStepDz`, with c
    held constant). That keeps full relative precision where f itself is swamped by c
    (z⁸ + c near 0).
  - The dual rule for a^b at a = 0 used the log form, 0·∞ = NaN. With a constant real b ≥ 1
    it now returns b·a^{b−1}·a′.
  - Roots of f′ are found by Newton from 0 and 7 rings of 12 starts, with f″ from a central
    difference of f′. The step is scaled by the multiplicity m, estimated from successive
    steps contracting by (m − 1)/m.
  - Each root is checked: |f′| must be under 10⁻⁶ of its value a little way off, and the
    order is measured.
  - A root that moves when c changes, at two test offsets, is flagged as c-dependent. It is
    listed but cannot be applied, because no constant seed reproduces it.
  - Refused for non-holomorphic equations, whose Jacobian vanishes on curves, not points.
- **Validated (critical points):**
  - Hand-solved roots: z^d + c → 0 with multiplicity d − 1; z³ − 3z + c → ±1;
    z⁴ − 2z² + c → 0, ±1; z³ + 3z + c → ±i; z³ + cz → ±√(−c/3), flagged as c-dependent;
    exp(z) + c → none.
  - Dynamical check: with z₀ = ±1 on z³ − 3z + c, the nuclei the general finder accepts
    (c = ±3) are superattracting (multiplier 0), as they must be when the cycle passes
    through a critical point.
- **UI.** Explore ▸ Find:
  - On a Multibrot, the Angle box reads base-d angles.
  - On a User Equation, **Critical points** fills a list and **Use as z0** sets the seed. It
    goes through the equation editor when that is open, so its seed box follows.
- **Batch parity.**
  - The seed changes the render, so it joins the region snapshot (`RegionFractalParams.UserEquationSeed`),
    which makes it `--param UserEquationSeed=…`, with a round-trip test. A blank seed is not
    emitted, so a saved equation's own seed stands.
  - The equation source itself was never in the Command builder. `DetectGaps` now reports
    it, and #1249 tracks the fix.
- **Not done:** deep zoom past 1e13 for these families. They render in double with no
  perturbation tier, so an OD reference orbit in the finders would gain nothing. It becomes
  relevant once a family gains perturbation rendering.

## 10. Sources

- R. Munafo, *Mu-Ency — The Encyclopedia of the Mandelbrot Set*: period detection (box
  method), nuclei, Misiurewicz points, external angles.
- A. Douady, J. H. Hubbard, *Étude dynamique des polynômes complexes* (Orsay notes,
  1984–85): external rays, landing theorems, tuning.
- Tan Lei, "Similarity between the Mandelbrot set and Julia sets", *Comm. Math. Phys.* 134
  (1990): local similarity at Misiurewicz points.
- P. Lavaurs (1986): combinatorial algorithm pairing the periodic external angles that land
  at the same component root.
- E. Lau, D. Schleicher, "Internal addresses in the Mandelbrot set and irreducibility of
  polynomials" (1994).
- C. Heiland-Allen (mathr.co.uk): write-ups on Newton's method for nuclei and Misiurewicz
  points, atom domain size estimation, external ray tracing, and Julia morphing. Use for
  the maths only (clean-room).
- Kalles Fraktaler 2 / its documentation: Newton-Raphson zooming and Jacobian Newton for
  Burning Ship (reference behaviour only).
