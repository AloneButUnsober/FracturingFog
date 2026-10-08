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

**Status: S1–S5 shipped (#1185–#1189, PRs #1232/#1234/#1236/#1238/#1239); S6 implemented (#1190) — 2026-10-08.** See §9.1 for what implementation changed versus this design.

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
  ≥ 0.1 px; full-precision `--x/--y` is #1233.

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
  Deep centres still need #1233 for exact headless reproduction (already
  flagged by `DetectGaps`).
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
- **Not shipped:** the score-heatmap overlay and live weight tuning ([#1240](https://github.com/AloneButUnsober/FracturingFog/issues/1240)).

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
