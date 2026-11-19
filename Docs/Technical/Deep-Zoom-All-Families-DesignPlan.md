# Deep zoom for every family that can support it — design plan (epic #1253)

Status: **research / design**, 2026-11-19. Tracking issue: #1253, widened from "Multibrot / Burning Ship /
Tricorn past 1e13" to an epic. Slices are listed in §9 and filed as #1906–#1918, linked from #1253.

Builds on the Mandelbrot deep-zoom engine:
- `Docs/Deep-Zoom-Perturbation.md`;
- `Docs/Technical/Arbitrary-Precision-DeepZoom.md` (epic #1428: the exact centre, float-exp δ and the
  BigFloat reference orbit);
- `Docs/Technical/Period-Synchronised-LA-DesignPlan.md` (epic #1623).

FF claims no novelty for perturbation, rebasing, BLA or automatic δ derivation. §10 lists the prior art.

## 1. Question and answer in brief

**The question.**
1. Which fractal families can be zoomed past double precision (~1e13), and how far?
2. Can a user's own equation (User Equation, interpreted or compiled with CalcGen) zoom with arbitrary
   precision as Mandelbrot does?

**Answer.**
- **Escape-time families built from + − × of z and c, with integer powers.** These are Multibrot,
  Julia, Tricorn and Burning Ship (via `abs`), the Generated families, Spider and Phoenix, and any user
  polynomial. All can zoom without limit, the same way Mandelbrot does:
  - a high-precision reference orbit;
  - a per-pixel δ in double, then float-exp;
  - BLA for speed.

  Tricorn and Burning Ship need the real two-variable (R²) form. Burning Ship also needs the `diffabs`
  case analysis. Both are published and in use elsewhere (§10).
- **Rational formulas** (Magnet 1 / 2, Nova, Newton-type maps, user `/`) can use perturbation through
  a cancellation-free quotient difference. Glitches appear where the denominator nears 0, so these
  pixels fall back to direct evaluation. Feasible, with more glitch handling.
- **Transcendental formulas** (sin, cos, exp, log, non-integer powers) can use perturbation through
  cancellation-free difference identities, for example exp(Z+δ) − exp(Z) = exp(Z)·expm1(δ). They also
  need BigFloat versions of those functions for the reference orbit past OD. Feasible, more work, and
  slower. I found no published description of perturbing these in a fractal renderer (§10); FF claims
  only the engineering.
- **Discontinuous functions** (floor, round, fract, mod, sign, comparisons in `if`) have no δ
  expansion across a jump. They can still zoom deep, but only by direct per-pixel high-precision
  evaluation. That is ~100–1000× slower, so a cap or a warning applies, not perturbation.
- **Not applicable:** splat / Monte-Carlo, attractor, L-system, IFS, flame, generator, wave-optics and
  ray-traced families (§4.8). Their depth limit is sample density or geometry, not a number format.
- **User Equation / CalcGen: yes, through one shared engine (§5).** Every user equation becomes a δ
  recurrence derived from its AST; CalcGen's `AstPerturbation` already does this for polynomials. The
  δ recurrence runs on the same tiered engine Mandelbrot uses: DD → QD → OD → float-exp δ → BigFloat
  reference. The interpreted path and CalcGen's compiled path then share one deep tier, so neither has
  to carry its own.

## 2. Where each family stands today (read from the code, 2026-11-19)

| Family | Calculator | Deepest tier now | Limit |
|---|---|---|---|---|
| Mandelbrot | `MandelbrotCalculator` | DD / QD / OD PT, LA / BLA / SA, float-exp δ, BigFloat ref (AP) | none (#1428) |
| TearDrop | `TearDropCalculator` | DD-PT, QD-PT, DD / QD full | ~5e58 (QD) |
| Julia, Multibrot, Burning Ship, Tricorn, Magnet 1 / 2, Glynn, Spider | `EscapeTimeCalculator` | SP only ("Zoom cap ≈ 1e15") | ~1e13–1e15 |
| Phoenix | `EscapeTimeCalculator` | scalar PT from 1e10 | ~1e15 |
| Generated Z2–Z5 | CalcGen template | PT (DD ref), QD-PT, SA, BLA; DD / QD direct | ~1e58 (QD ref, double δ) |
| Generated Tricorn / Burning Ship | CalcGen template | DD / QD direct only (Conj / Folded turn PT off) | QD-direct speed |
| User Equation, interpreted | `UserEquationCalculator` | double iteration; DD / QD only to place the pixel (Hi limb taken) | ~1e13 |
| User Equation, Compile & Load | CalcGen hot-load (`_dynamicAltCalculator`) | as Generated: PT for holomorphic polynomials, else DD / QD direct | ~1e58 |
| Newton / Nova, Halley, Secant, TranscendentalJulia, Logistic, Lyapunov | own calculators | double | ~1e13 |
| Bicomplex / Coquaternion, Julibrot / JulibrotPair | own calculators | double | ~1e13 |

**Plumbing limits that apply to every family but Mandelbrot:**
- **Centre.** `IFractalCalculator` carries `CenterX` / `CenterY` / `Zoom` as doubles.
  `FractalRenderHost.SyncAltStateFromMandel` copies only those. Some calculators (User Equation, CalcGen)
  have their own `CenterXLo…X3`; none takes the OD limbs or the exact centre.
- **Finders and explorer.** `AutoExplorer.MaxZoomFor` returns `DoubleMaxZoom` (1e13) for every family
  but Mandelbrot. `GeneralFinder` and `MultibrotRay` run in double.
- **CalcGen's perturbation gate.** `CalculatorGenApi.Generate` turns perturbation, BLA and SA off for
  `Conj`, `Folded`, `Div`, transcendentals, `re` / `im` / `abs` / `clamp`, `pow` with a complex
  exponent, inverse trig, the piecewise functions, `If`, `prev` and `n`. `AstPerturbation.BuildDeltaUpdate`
  is the exact Taylor expansion, so it covers polynomials only.
- **BigFloat** has `Add`, `Sub`, `Mul` and `Div`, with no `Sqrt`, `Exp`, `Log` or trig. A BigFloat
  reference orbit is possible today only for rational formulas.

## 3. What a family needs to zoom deep

1. **The exact centre:** all 16 limbs, plus the `ExactCentre` past OD, through the calculator interface,
   the host, input, batch, region store and cluster. Mandelbrot already has all of these (#1320,
   #1833, #1860, #1884).
2. **A reference orbit at the view's precision:** DD / QD / OD, then BigFloat. It is evaluated with the
   formula's own operations at that precision.
3. **A δ recurrence that runs in double or float-exp without catastrophic cancellation.**
   - Every term must be of order δ or ε (ε = dc, the pixel's parameter offset).
   - A term that computes a large value and then subtracts it fails, because large-minus-large loses
     the small part.
   - This is the rule Ultra Fractal states for hand-written perturbation equations (§10).
4. **Glitch handling.**
   - **Rebasing** (Zhuoran) applies when the reference restarts at the formula's critical point. The
     mathr 2022 post implemented it only for formulas with a single critical point at 0.
   - **Otherwise:** Pauldelbrot's glitch test, plus a new reference or direct evaluation for the
     glitched pixels. TearDrop already does the latter.
5. **Acceleration (optional, needed for speed).**
   - **BLA** generalises to non-conformal formulas with 2×2 real Jacobians and an operator-norm radius.
     For `abs` folds the radius uses min(|X|, |Y|) (mathr 2022, §10).
   - **SA and Imagina-style LA** are derived for analytic maps; they stay Mandelbrot / polynomial-only
     until shown otherwise.
6. **Finders (optional):** an OD / BigFloat reference and perturbed Newton for the family's nuclei. Only
   meaningful where the family has minibrots (§6).
7. **Batch and Command builder parity:** already carried by the region limbs. Verify each tier is
   picked headless (CLAUDE.md batch rule).

## 4. Feasibility by class

### 4.1 Holomorphic polynomials in z and c: Multibrot, Generated Z2–Z5, Julia, user polynomials

**Feasible, same as Mandelbrot.**
- **δ recurrence.** `AstPerturbation` already builds it: the binomial / Taylor expansion, ε added last.
- **Julia.** c is fixed and the pixel sets z₀, so ε = 0 and δ₀ = the pixel offset.
- **BLA.** A = ∂f/∂z(Z), B = ∂f/∂c(Z), both complex; CalcGen emits both bodies.
- **SA.** Exists for z^d + c (`AstSaDetector`).
- **Rebasing.** Valid for Mandelbrot-type (z₀ = 0 = the critical point). **Not valid for Julia**: there
  z₀ ≠ the critical point, so Julia uses glitch detection plus a secondary reference (§3, item 4).
- **Precision ceiling.** Rises by moving the engine, not the formula, onto OD / float-exp / BigFloat.

### 4.2 Anti-holomorphic polynomials: Tricorn / Mandelbar, conj in user formulas

**Feasible.**
- **Real form.** Write the map in R² (x, y) as real polynomials. The δ recurrence is the real Taylor
  expansion: exact, and every term is of order δ.
- **BLA.** Uses 2×2 real A and B matrices with the operator norm (mathr 2022).
- **Rebasing.** Valid (critical point 0).
- **CalcGen work.** The perturbation builder needs an R² mode: lower `conj` and `re` / `im` to real
  components before differentiating.

### 4.3 Abs folds: Burning Ship, Perpendicular / Celtic variants, `abs` in user formulas

**Feasible, published.**
- **δ form.** |X + x| − |X| for the reference component X and pixel δ component x, via laser blaster's
  `diffabs` case analysis as given in mathr 2021:
  `diffabs(c, d) = c ≥ 0 ? (c + d ≥ 0 ? d : −(2c + d)) : (c + d > 0 ? 2c + d : −d)`.
- **Rescaled limit.** When the rescaled term underflows, the same post gives
  sign(X)·sign(Y)·(X·y + x·Y).
- **BLA.** Valid only while no component changes sign: radius from min(|X|, |Y|), halved for safety
  (mathr 2022).
- **Rebasing.** Valid (critical point 0).
- **Unsolved case.** mathr 2021 notes that "deep needle" locations are hard to detect automatically in
  KF. FF should expect the same.

### 4.4 Rational maps: Magnet 1 / 2, Nova / Newton-type, Halley, Secant, `/` in user formulas

**Feasible, with more glitch handling.**
- **δ form.** For f = P / Q, the cancellation-free difference is
  (P+ΔP)/(Q+ΔQ) − P/Q = (ΔP·Q − P·ΔQ) / (Q·(Q + ΔQ)).
  ΔP and ΔQ are the polynomial δ-expansions of numerator and denominator, already of order δ.
- **Glitches.** Pixels where |Q + ΔQ| is small relative to |Q| are flagged and evaluated directly.
- **BLA.** Applies with A = f′(Z) and the radius shrunk near poles.
- **Rebasing.** Applies only if the formula's critical point is z₀ (Magnet: no). Otherwise use the
  glitch test plus a secondary reference.
- **Root-finding families (Newton / Nova / Halley / Secant).** The convergence test |z_{n+1} − z_n| < tol
  must scale with the pixel size. At depth the basin boundary is what is drawn, and the absolute
  tolerance saturates.
- **Phoenix-type `prev`.** Carry δ for z_{n−1} as a second state (the scalar Phoenix tier already does).

### 4.5 Transcendental formulas: TranscendentalJulia, Glynn (z^1.5), `sin` / `exp` / `log` / `pow` in user formulas

**Feasible, the most work.** Each elementary function gets a difference form with no large-minus-large
step:

| f | f(Z+δ) − f(Z) |
|---|---|
| exp | exp(Z) · expm1(δ) |
| sin | 2 · cos(Z + δ/2) · sin(δ/2) |
| cos | −2 · sin(Z + δ/2) · sin(δ/2) |
| log | log1p(δ / Z) |
| Z^p, real or complex p | Z^p · expm1(p · log1p(δ / Z)) |
| sinh / cosh / tanh, inverse trig | from exp / log by the same rule |

These are standard identities (sum-to-product; expm1 / log1p). The engine needs accurate **complex**
expm1 and log1p for small arguments in double and float-exp.

**Branch cuts.** log and non-integer powers have branch cuts. A pixel whose Z + δ crosses the
reference's cut is a glitch → direct evaluation.

**Arbitrary precision.** The BigFloat reference orbit needs BigFloat exp, log, sin, cos and atan.
- **Method:** argument reduction plus series.
- **Source:** the slice that writes them picks and verifies its source (MPFR's documentation is the
  likely reference; MPFR itself stays out, §10).
- **Until then:** transcendental formulas stop at OD (~1e110, as Mandelbrot's OD tier).

**BLA.** Applies (A = f′(Z) analytic). The radius needs the second derivative's size, which the
`AstDifferentiator` can give symbolically.

### 4.6 Discontinuous and conditional formulas: floor, round, ceil, trunc, fract, mod, sign, min / max, `if`

**No perturbation across a jump.**
- **Why not.** Perturbation holds only while every pixel takes the reference's branch.
- **Branch-agreement mode.** It could still pay off in a view inside one branch region (most of a
  zoomed view). A pixel that disagrees with the reference's branch is a glitch → direct evaluation.
- **Effort.** Worth one slice once the engine exists, since it is just the glitch path. Until then
  these formulas zoom deep via direct per-pixel DD / QD / OD only (CalcGen already has DD / QD direct)
  and carry a slow-render warning.

### 4.7 Other 2D / 4D iterations: Bicomplex, Coquaternion, Julibrot / JulibrotPair, Lyapunov, Logistic

- **Bicomplex / Coquaternion.** Polynomial maps on R⁴ → the 4.2 approach with 4×4 Jacobians. Feasible,
  low demand. **Parked.**
- **Julibrot / dual-orbit.** Two coupled orbits, each Mandelbrot- or Julia-type. Feasible on the
  engine once 4.1 lands. **Parked.**
- **Lyapunov / Logistic.** The real logistic map perturbs exactly (polynomial). But the Lyapunov
  exponent averages log|f′| over thousands of iterations, so per-pixel cost dominates, and deep
  Lyapunov zooms are rarely interesting. **Parked.**

### 4.8 Not applicable (depth is not limited by number precision)

- **Splat / Monte-Carlo, attractors, generators:** Buddhabrot family, Flame, IFS, L-system, strange
  attractors, Plasma, Acid Warp, DLA, Random Tile.
- **Wave optics and ray / billiard tracers:** Slit Cascade, Talbot Carpet, Chaotic Billiard, Billiard
  Rays, Mirror Spheres.
- **Limit sets:** Kleinian, Apollonian, Indra's Pearls (Möbius iterations, geometry-bound).
- **Math Plane / Math Plane 3D:** function plots.
- **3D raymarchers:** Mandelbulb, Mandelbox, KIFS, quaternion, User Bulb. Depth is camera precision in
  a distance-estimate march; deep 3D zoom is a separate research topic, not in this epic.

## 5. User Equation and CalcGen: one deep engine

### 5.1 The proposal

Today there are three deep paths:
- the hand-written Mandelbrot engine;
- the TearDrop engine;
- CalcGen's template (PT / QD-PT / DD-QD direct), which every generated or compiled-and-loaded
  equation copies.

The interpreted User Equation has none. The proposal is one **generic perturbation engine** in
`Engine/` (working name `PerturbationEngine`). It takes a **formula plan** and owns everything that
isn't formula-specific:

| The engine owns | The formula plan supplies |
|---|---|
| the precision tiers and labels (SP / DD / QD / OD / FX / AP) | the step as an AST (from the equation language) |
| the exact centre and the pixel scale | its δ-update AST |
| reference-orbit building and caching (incl. the cluster blob) | its Jacobian ASTs (A, B; complex or 2×2) |
| glitch detection, rebasing where the plan allows, secondary references | its critical point (rebasing allowed?) |
| BLA tables (2×2 or complex) | its branch / glitch predicates (abs signs, denominators, cuts, `if`) |
| periodicity, solid regions, adaptive AA, cancellation, the GPU hand-off later | its escape test and derivative (DE) |

**Built-in families become plans.** Multibrot, Julia, Tricorn, Burning Ship, Magnet, Phoenix, Spider,
TearDrop and Generated Z2–Z5 become plans with fixed equations. Mandelbrot keeps its hand-tuned engine
(SIMD rows, LA / AT, GPU kernels). The generic engine is checked against it on z² + c (§8).

**How a plan executes:**
- **Interpreted User Equation.** The δ-update and Jacobian ASTs compile like the step (typed Expression
  trees, #1110) to double and float-exp delegates. The reference orbit runs through a generic
  evaluator over the numeric types (DD / QD / OD / BigFloat). No Roslyn, so the hot-load sandbox rules
  (#27, #1259) are unaffected.
- **CalcGen.** Its emitters can keep generating the shallow SIMD paths. Its deep tier calls the
  engine with emitted δ delegates instead of the template's own PT / QD code. This retires the
  template's deep paths once parity holds.

### 5.2 Extending the δ builder beyond polynomials

`AstPerturbation` moves to the shared equation-language layer (as `CalcGenLowering` did for parsing,
#1087) and grows rules, in this order:

1. **R² lowering** for `conj`, `re`, `im` (§4.2).
2. **`abs` / Folded via `diffabs`** (§4.3).
3. **Division via the quotient difference** (§4.4).
4. **Elementary functions via the difference table** (§4.5), plus their derivatives for BLA (the
   `AstDifferentiator` already has the dz/dc rules, #215).
5. **`prev` and `n`:** a δ state per `prev`. `n` is a plain scalar in δ terms.
6. **Branch-agreement mode** for the piecewise operators (§4.6).

Each rule comes with a gate. A formula that uses an operator with no rule yet runs on the direct
high-precision path, as today, so nothing regresses.

### 5.3 How deep, and how fast

- **Depth.** Unlimited for the classes in §4.1–4.4 (BigFloat has + − × ÷). Unlimited for §4.5 after
  the BigFloat elementary-function slice, OD (~1e110) before it.
- **Speed.**
  - Interpreted δ delegates will be slower than emitted C#, as the shallow paths are today. D3
    measures the ratio.
  - CalcGen-emitted plans should approach the hand-written Mandelbrot non-SIMD rows.
  - Transcendental δ steps cost one complex expm1 / log1p / sin per term per iteration: an order of
    magnitude over a polynomial step.

  To be measured per slice, not promised.

## 6. Finders and the explorer

- **Families with minibrots** (Multibrot, Tricorn, Burning Ship, polynomial user maps). `GeneralFinder`
  gets an OD / BigFloat reference orbit with perturbed Newton, as S2 / S3 did for z² + c. `MultibrotRay`
  gets a perturbed trace (#1237's approach). For Burning Ship the published generalisation of
  minibrot finding is Heiland-Allen's EVA 2019 paper (§10, under verification).
- **Julia-type and the rest** have no nuclei to seek. Their explorer limit becomes the render limit
  (`AutoExplorer.MaxZoomFor` per family, from the engine's capability).

## 7. Plumbing

1. **`IDeepCentre`** (capability interface): the 16 limbs, `CenterExact`, `SetDepth`. Implemented by
   every engine-backed calculator.
   - **Done in D1 (#1906):** `Engine/Interefaces/IDeepCentre.cs`, implemented by TearDrop, User
     Equation and the CalcGen template (built-ins and Compile & Load). The host's shared sync, the
     video engine and the poster path now use it instead of per-type limb copies. Before, only the
     main live frame and the poster copied QD limbs. The preview sidecars, slideshow cross-fade,
     video, Generated Tricorn / Burning Ship and Compile & Load got the Hi limb only. Explorer
     caps come from `FamilyDepth.MaxZoomFor`.
   - `FractalRenderHost.SyncAltStateFromMandel` and the alt preview sidecars copy all of it.
   - Input (`ViewCamera`) is already exact.
2. **Batch.** `BuildFractalParameters` / `PosterRenderer.BuildCaptureCalculator` pass the limbs and
   the exact centre to engine-backed calculators. `--zoom` past 1e13 is accepted for those families.
   Command-builder fidelity gaps are reported per family until then.
3. **Cluster.** The reference-orbit blob (#1884) generalises to any plan (formula hash in the header).
4. **Region store.** Already carries all limbs and the exact centre (#1320, #1833).

## 8. Validation

Each slice is checked against an **independent** oracle:
- **Direct per-pixel BigFloat iteration** of the formula on a small grid (as `S1783` / `S1842` do for
  Mandelbrot): counts equal, smooth values and distances within tolerance. The oracle iterates the
  formula directly; it never sees δ, so it is independent of the perturbation path.
- **Shallow parity** with the existing double calculators below 1e13, byte-identical where the old path
  is exact.
- **Engine vs `MandelbrotCalculator`** on z² + c at 1e30 / 1e60 / 1e320: same counts.
- **Glitch fallbacks** (abs sign flips, poles, branch cuts, `if` branches): seeded views where they
  must trigger, with the glitched pixels matching the oracle.
- **Mutation checks** per rule (drop the `diffabs` case, the quotient term, an expm1) must fail the
  oracle.
- **Finders:** deep nuclei re-checked in-set by the real calculator, not by the finder's own iteration.

## 9. Slices (each an issue under #1253)

| # | Issue | Slice | Depends on |
|---|---|---|
| D1 | #1906 | `IDeepCentre` plumbing: calculator, host, sidecars, batch, cluster; capability-driven zoom caps | — |
| D2 | #1907 | Generic `PerturbationEngine` core: tiers DD → OD → FX → AP, reference orbit, rebasing, glitch / secondary reference, BLA (complex). Plans: Multibrot, Julia. Parity vs `MandelbrotCalculator` on z² + c | D1 |
| D3 | #1908 | Shared δ builder (move `AstPerturbation`); interpreted User Equation and Compile & Load polynomial plans on the engine | D2 |
| D4 | #1909 | R² lowering + 2×2 BLA: Tricorn, conj in user formulas, Bicomplex later | D3 |
| D5 | #1910 | `abs` / `diffabs` + sign-aware BLA radius: Burning Ship, Generated Burning Ship, user `abs` | D4 |
| D6 | #1911 | Rational: quotient difference, pole glitches. Magnet 1 / 2, user `/`; scaled convergence test for Newton / Nova / Halley / Secant | D3 |
| D7 | #1912 | `prev` / multi-state: Phoenix, Spider on the engine (retire the scalar Phoenix tier) | D3 |
| D8 | #1913 | Elementary functions in double / float-exp: complex expm1 / log1p, the difference table, branch-cut glitches. TranscendentalJulia, Glynn, user transcendentals to OD | D3 |
| D9 | #1914 | BigFloat elementary functions (exp, log, sin, cos, atan): transcendental plans past OD | D8 |
| D10 | #1915 | Branch-agreement mode for piecewise / `if` formulas | D3 |
| D11 | #1916 | Finders: OD / BigFloat `GeneralFinder` + perturbed `MultibrotRay`; per-family explorer caps | D2, D4, D5 |
| D12 | #1917 | CalcGen deep tier on the engine; retire the template's own PT / QD paths after parity | D3 |
| D13 | #1918 | TearDrop on the engine (past its QD ceiling) | D6 |
| — | — | Parked: GPU deep kernels for plans; LA / AT for non-Mandelbrot plans; Bicomplex / Coquaternion / Julibrot; Lyapunov | — |

**D2 as built (#1907).**
- `Engine/Perturbation/`: `PerturbationEngine`, `IPerturbationPlan`, `PowerPlan` (z^d + c). Julia and
  Multibrot render on it from 1e11 in `EscapeTimeCalculator`.
- **Tiers:** a BigFloat reference stored as doubles, with a double δ, to ~1e290. Float-exp δ, a log
  zoom, and the derivative's distance unit (dz/dc overflows past ~1e308) are #1921 (D2b).
- **Measured findings:**
  1. **The reference ends before its escaped point.** A pixel that stepped onto it carried |δ| ≈ 512
     while its own z was small, and was 20 % off direct iteration in a view with the minibrot
     off-centre. Mandelbrot's reference ends the same way. A Julia pixel may still take the one step
     onto it when that step escapes.
  2. **BLA ε defaults to 1e-10** (`PerturbationEngine.DefaultBlaEpsilon`, as #1613 / #1791). At 1e-6,
     z² + c was 2.8 % off MandelbrotCalculator, and Julia's derivative was off on ~10 % of pixels.
  3. **Secondary references (Julia)** take the glitched pixel that lasted longest, ties by index, so a
     frame is deterministic.

**Order of value:** D1 → D2 → D3 gives deep zoom to Multibrot, Julia, Generated Z2–Z5 and every
polynomial user equation. D4 / D5 add Tricorn and Burning Ship, which is the original #1253. D6–D10
widen the user equation language.

## 10. Prior art and credits

All entries are in `Docs/Resources-Bibliography.md`; the new ones are added with this document.

- **Perturbation + series approximation:** K. I. Martin, SuperFractalThing (2013).
- **Glitch detection:** Pauldelbrot (2014).
- **Rebasing and BLA:** Zhuoran (fractalforums.org, 2021).
- **Imagina's LA:** used for Mandelbrot only here.
- **mathr.co.uk, "Deep zoom theory and practice" (2021-05-14).**
  - Abs variations via `diffabs`, credited there to laser blaster's Fractal Forums post "Perturbation
    Formula for Burning Ship".
  - The rescaled form, hybrids perturbed operator by operator, and generating code for them.
  - The automatic derivation for KF / Fraktaler's operator set (abs, negation, integer power,
    multiplier) is prior art for §5.2 rules 1–2. FF does not claim it.
- **mathr.co.uk, "Deep zoom theory and practice (again)" (2022-02-21).**
  - BLA for non-conformal formulas: the operator norm of the 2×2 Jacobian.
  - Burning Ship's radius with min(|X|, |Y|).
  - Rebasing implemented only for a single critical point at 0.
- **Ultra Fractal "Perturbation equations":** user-written perturbation sections (`perturbinit` /
  `perturbloop`), derived by hand. Every term must reduce to the order of dz or dc.
- **Kalles Fraktaler / Fraktaler 3:** deep zoom for hybrid escape-time 2D formulas.
- **Transcendental difference forms (§4.5):** standard identities. I found no published description of
  their use in a perturbation renderer (search: mathr posts, Ultra Fractal docs, fractalforums
  results; not exhaustive), so FF claims only the engineering.
- **Under *Citations awaiting verification*:**
  - laser blaster's forum post (year and full name not confirmed);
  - Heiland-Allen, "At the helm of the burning ship", EVA London 2019 (DOI given by Fractalshades,
    not yet read).

## 11. Open questions

1. **Julia secondary references.** Choose the new reference per glitched blob (KF style) or per tile?
   Measure in D2.
2. **Interpreted δ speed.** Is it enough, or should the interpreted path auto-route through Compile &
   Load past 1e13? Decide after D3's numbers.
3. **Generic LA / AT.** Can Imagina-style LA stages generalise to polynomial plans? Not assumed; a
   research item after D5.
4. **Deep-needle detection** for Burning Ship (mathr 2021 calls it unresolved in KF).
