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
- **As built (#1909):** difference arithmetic rather than a symbolic expansion; see D4 in §9.

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
- **As built (#1910):** see D5 in §9. The rescaled limit is not needed for the double-δ tier.

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
  - **Measured in #1911: not so.** A tighter tolerance only shifts counts (Newton +1 everywhere);
    see D6 in §9.
- **Phoenix-type `prev`.** Carry δ for z_{n−1} as a second state (the scalar Phoenix tier already does).
  - **As built (#1912):** `TwoStatePlan`; see D7 in §9. The scalar tier was not a deep tier: it
    rebuilt z in double.

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
  - **As built:** D9 (#1914) landed with D8 (#1913). The reference is BigFloat from the start, so
    there is no OD stage; see §9.

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
- **As built (#1915): better than agreement.** A pixel on another piece switches exactly, and only
  close calls fall back; see D10 in §9.

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

**D3 as built (#1908).**
- **`AstPlan`** (`Engine/Perturbation/AstPlan.cs`) builds a plan from the equation's CalcGen AST
  (`CalcGenLowering.Lower` of the interpreter's parsed `SandboxExpression`). CalcGen's
  `AstPerturbation` / `AstDifferentiator` derive δ, the derivative, A, B and ½f″ for the BLA radius.
  The derived ASTs compile to delegates through `System.Linq.Expressions` (no Roslyn). The reference
  evaluates the step AST in BigFloat.
- **Scope:** the polynomial subset (`+ − ×`, integer powers, real / `i` constants).
- **Rebasing and glitches.**
  - Rebasing is used only when ∂f/∂z at z = 0 simplifies to 0 for every c.
  - Otherwise glitches use Pauldelbrot's test generalised to |f′(z)|² < τ·|f′(Z)|², with secondary
    references. z² + z + c (critical point −½) is the test case.
- **The plan interface gained** `CanRebase`, `IsGlitch`, `InitialDerivative`, and the reference's c in
  the δ / derivative / BLA calls.
- **User Equation** renders a polynomial on the engine from 1e11. A seed, a bailout condition or an
  orbit-aware theme keeps the double interpreter; `DeepRefusal` gives the reason.
- **Not in D3:**
  - `AstPerturbation` stays in CalculatorGen.Lib, which Engine already references; moving it buys
    nothing yet.
  - Compile & Load keeps its own QD-PT tier (with the full centre since D1) until D12 puts CalcGen's
    deep tier on the engine.
  - `FamilyDepth` keeps User Equation's explorer limit at 1e13, because it depends on the equation.
- **Found:**
  1. **Bailout radius.** User Equation's default bailout (|z|² ≥ 1024) gives different counts from
     MandelbrotCalculator's 512². The plan takes the calculator's radius.
  2. **Masking.** With BLA on, a missing ∂f/∂c in the plan's derivative step is masked, because the
     skip adds its own `+ B`. The tests also run BLA off.

**D4 as built (#1909).**
- **`R2Plan`** (`Engine/Perturbation/R2Plan.cs`) covers steps polynomial in z, c, `conj`, `re` and `im`,
  treated as real maps of R².
  - **δ by difference arithmetic.** Each node carries its value V at the reference and its exact
    difference D. Sums, `conj`, `re` and `im` are linear, so D maps as V does. A product has
    D = V₁·D₂ + D₁·(V₂ + D₂). Every term is of order δ or ε, and for a polynomial the result is exact.
    This is mathr 2021's operator-by-operator perturbation; it needs no symbolic expansion, so
    CalcGen's `AstPerturbation` is not used here.
  - **Jacobians by forward-mode tangents.** A = ∂f/∂z and B = ∂f/∂c are 2×2 matrices. The orbit's
    derivative is the 2×2 Jacobian with respect to the pixel; the sink gets column 1, ∂z/∂Re(c),
    which is User Equation's numeric-Jacobian column.
  - **BLA radius.** A second-order tangent bounds the dropped term: |H(u)| ≤ |P| + ‖[Q R]‖ for
    H(u) = P + Q cos 2θ + R sin 2θ. This is tight for z² and conj(z)². It reduces to `AstPlan`'s
    radius for a complex-analytic step.
  - **Rebasing** applies when ∂f/∂z vanishes at z = 0, checked numerically at three generic c.
  - All of these compile to `System.Linq.Expressions` delegates in SSA form (each value bound once).
- **The engine.**
  - `IRealPerturbationPlan` routes a plan to an R² pixel loop with `Bla2Table`. That table is
    `BlaTable`'s layout and merge with operator norms (mathr 2022).
  - The glitch test uses the Jacobian's Frobenius size in place of |f′|.
- **Wiring.**
  - Tricorn renders on the engine from 1e11 (`EscapeTimeCalculator`), and `FamilyDepth` puts it at 1e290.
  - `AstPlan.TryCreateAny` picks `AstPlan` for analytic formulas and `R2Plan` for conj / re / im.
  - User Equation uses `TryCreateAny`.
- **Deferred.**
  - Generated Tricorn waits for D12 (#1917), which puts CalcGen's deep tier on the engine.
  - `abs` waits for D5 (#1910).
- **Differs from the shallow path.**
  - Tricorn's deep derivative is the true ∂z/∂Re(c). `TricornKernel` tracks z² + c's ("as if
    Mandelbrot").
  - Shading from normals can therefore change at the 1e11 switch. Iteration counts agree there
    (1,536 / 1,536 in the test).
- **Found:**
  1. **Interpreter derivative at depth.** User Equation's numeric Jacobian steps c by an absolute
     1e-6, which is ~1e5 pixels at 1e11, so its dz/dc is noise at depth (#1931). The engine's agrees
     with a BigFloat finite difference.
  2. **Bad oracle view.** A level-set boundary found by bisection is a bad oracle view: pixels escape
     near maxIter, at the chaotic floor (90 / 176 equal). Misiurewicz points (z₄ = z₃, by Newton in
     BigFloat) give early escapes with structure at every depth.
- **Mutants** (`S1909ConjR2Tests`):
  - **Caught:**
    - a product's D₁·D₂ term dropped (the issue's cross-term mutant);
    - the BLA radius ×1e6;
    - the merge's `+ B` dropped, and the merge's A in the wrong order;
    - the skip's Jacobian `+ B` dropped;
    - the Jacobian step's ∂f/∂c dropped;
    - the rebase test removed.
  - **Survives:** the glitch test, as in D2 / D3. At these views its regions are sub-pixel, and the
    pixels where the reference runs out still go to secondary references.

**D5 as built (#1910).**
- **Abs folds in `R2Plan`.** `abs` of a real value and `Folded` ((|re|, |im|)) are supported.
  - **δ:** laser blaster's `diffabs` cases (as given by mathr 2021), exact and with no
    large-minus-large.
  - **Derivatives** take the kink's side (≥ 0 counts as +, as the kernels do).
  - **`abs` of a complex value** is the modulus, a square root, not a fold. It stays outside the
    plan with the reason "abs of a complex value (its modulus)".
- **BLA radius.** The radius also stays inside half the δ that moves any fold's argument U to 0:
  (|U| − ‖∇_c U‖·|ε|max) / ‖∇_z U‖.
  - A `Limit` jet carries the four first derivatives and a running minimum.
  - For Burning Ship this is mathr 2022's min(|X|, |Y|) / 2, and the test checks it against that
    rule.
  - `Level0R2` takes |ε|max for the c term.
- **Wiring.**
  - Burning Ship renders on the engine from 1e11, and `FamilyDepth` puts it at 1e290.
  - Celtic, Perpendicular Burning Ship and other abs variants are user formulas: FF has no such
    `FractalType`.
- **Deferred.**
  - Generated Burning Ship waits for D12 (#1917).
  - mathr's rescaled limit sign(X)·sign(Y)·(X·y + x·Y) is for a rescaled (float-exp) δ. The
    double-δ tier does not need it; it comes with the float-exp tier.
- **Found:**
  1. **Chaotic views.** A Burning Ship Misiurewicz view (−0.3637 − 1.0501i) is at the chaotic floor:
     32 / 176 direct counts change under a 1e-12-pixel shift. Oracle views now require every sampled
     count to be stable.
  2. **BLA tolerance at a near-floor view.** At −0.2282 − 1.1151i (1e30), BLA off reaches the floor
     (174 / 176). BLA at ε = 1e-10 lost 6 more pixels, and ε = 1e-14 is back to 174. This is the
     #1613 / #1275 tolerance trade, not the folds: a 10× tighter fold limit changed nothing.
- **Mutants:**
  - **Caught:**
    - each of the four `diffabs` cases;
    - the derivative's kink sign;
    - the fold limit removed. Only the radius test catches this: on these views the second-order
      radius binds first.

**D6 as built (#1911).**
- **Division in `R2Plan`.** The δ is (ΔP·Q − P·ΔQ) / (Q·(Q + ΔQ)). The quotient rule gives the
  tangents, the second-order jet and the fold-limit jet.
  - Against BigFloat the worst relative error is 6e-16 for |δ| from 1e-14 to 1e-2 of |Z|.
  - The naive (P + ΔP)/(Q + ΔQ) − P/Q fails that test and every Magnet view.
- **Poles.** `PoleRatio2` (min |Q + ΔQ|² / |Q|²) flags a pixel whose denominator cancels the
  reference's. Below Pauldelbrot's τ the pixel goes to another reference or to direct iteration,
  counted in `PerturbationStats.PoleGlitches`.
- **Convergence.** The engine has a second stop, `Convergence(target, ε)`, which Magnet's
  `MagnetConvergence` uses for z = 1.
  - The converged sink colours a pixel as `CalculateMagnet` does, through a shared
    `FillMagnetConverged`.
  - BLA radii are zero where the reference is within 4ε of the target, so a skip cannot step over a
    pixel's convergence.
- **Wiring.**
  - Magnet 1 / 2 render on the engine from 1e11, and `FamilyDepth` puts them at 1e290.
  - User `/` formulas use `R2Plan` through `AstPlan.TryCreateAny`.
  - Magnet's dz/dc stays 0 (the kernels track none).
  - The kernels clamp |den|² at 1e-12; the deep path divides exactly.
- **Root-finding tolerance (§4.4) — measured and not changed.**
  - Newton z³ − 1 with the tolerance tightened from |Δ|² < 1e-12 to 1e-24 shifts every pixel's
    count by exactly +1, and basins and neighbour order are unchanged (6,144 / 6,144).
    Convergence to a simple root is quadratic, so this holds at any depth.
  - Nova adds ~29 steps (linear convergence), varying smoothly with c; basins are unchanged.
  - The test is in z-space, against O(1) roots, so the pixel size does not enter it. The
    saturation claim in §4.4 is not supported. What limits deep root finders is precision (they are
    double-only to 1e13), not the tolerance.
- **Limits.**
  - A reference within double rounding of a pole computes Q = Z − (pole) as noise. For Magnet that
    step escapes (bailout 100), so it does not matter. A user formula with a removable
    singularity (numerator and denominator both 0) is not handled.
  - Magnet 1's z = 1 is superattracting (f′(1) = 0), so the generalised Pauldelbrot test fires near
    convergence. Results are exact through secondary references and direct BigFloat, but costly in
    escape-only mode (#1936).
- **Mutants:**
  - **Caught:** the naive difference, the quotient tangent, the convergence stop.
  - **Survive:**
    - the pole check. No render view triggers it, and views near a pole escape on the next step, so
      only its unit test covers it;
    - the near-target BLA cutoff. For Magnet, A → 0 at the superattracting z = 1, so the
      second-order radius already collapses there. The cutoff is kept as a guard for targets that
      are not superattracting.

**D7 as built (#1912).**
- **`TwoStatePlan`** (`Engine/Perturbation/TwoStatePlan.cs`) handles z′ = f(z, w, c), w′ = g(z, w, c),
  with z₀ = 0 and w₀ = 0 or the pixel.
  - **Families:** Phoenix (w = z_{n−1}), Spider (w = the evolving c, w₀ = c, so the pixel enters as
    δw₀ = ε) and User Equation formulas that read `prev`.
  - **δ:** R2Plan's emitter, now shared through a parameter list and a leaf resolver. Each state's δ
    is its exact difference.
  - **Derivative:** a forward-mode tangent of both states along Re c, which is what the sink
    receives. No 2×2 matrix is needed without BLA.
- **`PerturbationEngine.RenderTwoState`** keeps a BigFloat reference of both states, the generalised
  glitch and pole tests, secondary references and direct BigFloat. There is no BLA (#1938) and no
  rebase.
- **Retired:** the scalar Phoenix "PT" tier (D-3.16, from 1e10). It rebuilt z in double from a
  double reference, so it resolved no deeper than SP. Phoenix runs SP up to 1e11, then the engine.
- **Views.** A two-state map has no usable Misiurewicz points: Phoenix's map is invertible, so an
  orbit cannot land on a fixed state it did not start at.
  - The test views land on a saddle's stable manifold instead (one complex condition on c): Newton on
    ℓ_u·(S_{K+1} − S_K) = 0, continued in K so the linearisation error falls below the view's scale.
  - Orbits there approach the saddle, then leave along its unstable direction, which gives structure
    at any depth.
- **Results** (`S1912TwoStateTests`):
  - Phoenix and Spider at 1e30 / 1e80 and a User Equation `prev` formula match direct BigFloat on
    176/176. The derivative is within 1 % of a finite difference on every escaped pixel.
  - One δ step is exact to 4e-16 for both states.
  - At the 1e11 switch, 1,536 / 1,529 of 1,536 agree with SP.
- **Mutants:**
  - **Caught:** the cross-state term (the `prev` δ reading δz), Spider's δw₀, the tangent's start.
  - **Survives:** the glitch test, as in D2–D6.

**D9 as built (#1914), landed with D8.** `BigFloatMath` (`Abstractions/Math/BigFloatMath.cs`)
provides the BigFloat functions D8's reference orbits need. D8 depends on them, so they landed first,
in the same PR.
- **Functions:** √, π, ln 2, exp, ln, sin / cos, atan / atan2, and complex exp / log / sin / cos / pow.
- **Method:** argument reduction plus series, with guard bits.
  - The sources are Wikipedia pages (bibliography `#wikipedia-elementary-series`).
  - MPFR's algorithms PDF could not be read here, so it is not the source.
  - The functions make no claim of correct rounding.
- **Checks** (`S1914BigFloatMathTests`):
  - sin² + cos² = 1, exp(log x) = x and exp(a + b) = exp(a)·exp(b) hold to 1–2 bits at 200, 600
    and 2,000 bits.
  - Machin's π matches 4·atan 1 (the half-angle route) and sin(π/6) = ½.
  - Rounded to double, the functions match `System.Math`.
  - The complex identities hold.
- **Mutants caught:** one squaring dropped from exp, one ln 2 dropped from log, the wrong cos
  double-angle, one atan halving dropped. Machin's ×16 / ×4 slip was a real bug, caught on the first
  run.

**D8 as built (#1913).**
- **`PerturbFunctions`** (`Engine/Perturbation/PerturbFunctions.cs`) supplies the cancellation-free
  differences: e^V·expm1(D); log1p(D/V) + 2πik; V^p·expm1(p·L); sum-to-product for sin and cos. Complex
  expm1 and log1p are built from accurate real ones.
- **The R2Plan emitter** gains exp, log, sin, cos, sqrt and pow with a constant exponent in every
  jet mode.
  - log, sqrt and pow have a branch cut. **Limit** keeps the BLA radius off the cut and off 0;
    **Pole** flags a pixel whose argument cancels the reference's at 0.
  - The reference evaluates in BigFloat.
- **R2Plan on the dynamic plane** (z₀ = the pixel, c a constant in the AST) is used for Glynn,
  z^1.5 + c.
- **Wiring:**
  - Glynn renders on the engine from 1e11, and `FamilyDepth` puts it at 1e290.
  - User Equation transcendentals reach the engine through `TryCreateAny`.
- **Branch cuts are kept exact, not treated as glitches.** The principal log difference is
  log1p(D/V) + 2πik.
  - k comes from arg(V) + arg(1 + D/V). Within 1e-9 of ±π, where double rounds V a hair off the
    axis onto π, the sides of the cut decide instead.
  - The seeded branch-cut view (Glynn at e^{2πi/3}, z₁ = −1.2 on the cut) caught the arg-only test:
    87 / 176 pixels went to the wrong branch.
  - A signs-only rule then broke the fixed-point view (155 / 176). There |δ| ~ |Z|, and the shift
    can be needed with V + D in the right half-plane.
- **Deferred:** TranscendentalJulia (axis bailout, no `IDeepCentre`; #1940); tan, the hyperbolic
  functions and the inverse trig functions; pow with a non-constant exponent. These stay on the
  interpreter, which reports the reason.
- **Results** (`S1913TranscendentalTests`):
  - each difference matches BigFloat to 1.5e-15 for |D| from 1e-14 to 1e-1 of |V|, with 150 cut
    crossings per function;
  - `exp(z) + c` and `sin(z) + z² + c` at Misiurewicz points, 1e40 and 1e100, BLA on and off: 176/176,
    derivative exact;
  - Glynn at its repelling fixed point, 1e40 and 1e100: 176/176;
  - the branch-cut view: 176/176 escaped values;
  - at 1e11: Glynn 1,498 and `exp(z) + c` 1,535 of 1,536 against the double paths.
- **Mutants caught:** naive exp and sin differences, the cos sign, the 2πk shift removed, the
  cut / zero limit removed, the tangent using f instead of f′.

**D10 as built (#1915).**
- **Scope:** `R2Plan` takes floor, round, ceil, trunc, fract, sign, min, max, clamp and `if`; `mod`
  lowers to floor.
- **Branch switching, not branch agreement.** The plan above proposed branch agreement: flag every
  pixel that leaves the reference's piece. As built, such a pixel switches exactly.
  - **Floor and its kin:** δ′ is the jump f(V + D) − f(V), an exact integer difference. Fract's
    jump is D minus floor's.
  - **if / min / max / clamp:** δ′ = (pixel branch − reference branch) at the reference's values,
    plus the pixel branch's δ.
  - The pixel's comparison is decided on (l − r) + (dl − dr), so there is no cancellation.
- **Close calls fall back.** Only an argument within 1e-12 (relative) of a jump, at the pixel or at
  the reference, goes to another reference or to direct BigFloat (`PoleGlitches`). Two exceptions:
  - an argument exactly equal to the reference's is accepted (step 0);
  - a reference argument of exactly 0 is trusted, since z₀ = 0 and the rebase target Z₀ = 0 are
    exact zeros. Without that waiver every rebased pixel of `re z > 0` or `floor(re z)` was a close
    call, and those views ran entirely on direct BigFloat.
- **BLA:** the Limit jet keeps it inside the distance to the nearest jump.
- **Reference:** `BigFloatMath` gains exact Floor / Ceil / Trunc / Round (half to even).
- **Results** (`S1915BranchAgreementTests`): every view matches direct BigFloat at 1e40 and 1e100,
  176/176.
  - **Inside one piece:** `clamp(re z, −100, 100)` flags nothing.
  - **Across pieces:** `clamp(re z, −0.3, 0.3)`, `if re z > 0 then z² + c else z² − c` and
    `0.05·floor(2·re z)`. 114–176 of the 176 sampled pixels leave the reference's piece, yet the
    views use one reference, flag no close calls and need no direct pixels.
  - **Seeded close call:** re c = ½, so pixels straddle floor's jump below double's resolution. The
    fallback takes them.
- **Mutants:** all six caught:
  - no switch for floor;
  - the if switch without the branches' difference;
  - the pixel's outcome taken as the reference's;
  - each close-call test disabled;
  - the jump limit removed. Only a radius test catches this one, because the views' BLA radii never
    span a jump.

**D11 as built (#1916).**
- **`DeepGeneralFinder`** (`Abstractions/Explore/DeepGeneralFinder.cs`) is `GeneralFinder` past double,
  for maps with a BigFloat step (`IDeepOrbitMap`: z^d + c, Tricorn, Burning Ship).
  - **Source.** It follows Claude Heiland-Allen, "At the Helm of the Burning Ship", 2019, DOI:
    10.14236/ewic/EVA2019.74 (bibliography `#heiland-allen-eva-2019`).
    - The paper's formulas could not be extracted from the PDF.
    - The forms below are FF's reading of its prose, checked against independent oracles.
  - **Newton in R².** The orbit runs in BigFloat, the 2×2 Jacobian in double (renormalised), and the
    step is applied in BigFloat.
    - The precision rises to log₂‖J‖ + 128 bits.
    - A root is accepted at the precision floor **and** with a tiny residual |G|.
    - A tiny step alone is not enough near a Misiurewicz point, where |J| ~ λ^p. An early version
      took a period-241 non-root for a nucleus. The double `GeneralFinder` has that flaw (#1954): it
      reports a period-79 "nucleus" whose |z_79| ≈ 33.
  - **Periods.** The view disk's image ellipse z_n + J_n·u, |u| ≤ R, is tested for 0:
    |J_n⁻¹·z_n| ≤ R.
    - The paper describes this as iterating an implicitly defined ellipse, and credits it to
      "knighty" (2017).
    - Periods are then scanned up from the first candidate within a work budget.
  - **Acceptance:** `GeneralFinder`'s invariants, then a polish at the minibrot's own precision.
  - **Size:** Hunt & Ott's estimate with Jacobians, read as
    (|det ∂z_p/∂c|·|det Λ|^{1/(d−1)})^{−1/2}. For a conformal map it is the atom-domain size.
  - **Snap:** `FindNearMisiurewicz` is ported too.
- **UI.** Past 1e13, Zoom to minibrot and Snap to spiral run the deep finder (exact centre, log zoom,
  `MinibrotJump.AtDeep`), within `FamilyDepth`. A double find whose framing needs more than 1e13 is
  polished in BigFloat and taken.
- **Not covered** (#1952): User Equation (no BigFloat evaluator for the DSL in Abstractions), a
  perturbed `MultibrotRay`, and Julia morph for the general families past 1e13.
- **Results** (`S1916DeepGeneralFinderTests`):
  - **z² + c:** matches the deep z² + c finder (period, nucleus, and size to 3 decimals).
  - **Burning Ship on its real axis** (where its orbit is z² + c's): finds the same nuclei at
    n = 40 / 80.
  - **Tricorn** at 1e30 / 1e80 and **z³ + c** at 1e30: roots by an independent BigFloat
    finite-difference Newton step.
  - **Engine check:** at the framing zoom, the nucleus renders in set with escaping pixels around.
  - **Shallow:** Tricorn (p 28) and Burning Ship (p 3) agree with `GeneralFinder`.
  - **Rejected:** the spurious p 79, and a period-40 nucleus searched at period 80.
  - **Misiurewicz points:** refound at 1e40.
  - **Open:** by the ship's off-axis Misiurewicz point 0.3015 + 0.4520i, no nucleus is found within
    the work budget.
- **Mutants:**
  - **Caught:**
    - the polish skipped;
    - the Jacobian's ∂f/∂c dropped;
    - the step's scale sign;
    - the minimal-period test removed (by a direct test);
    - the residual check and the precision raise removed together.
  - **Survive, with reason:**
    - Each of the residual check and the precision raise, alone. Each is a backstop for the other.
    - The ellipse period test reverted to the norm ball. The upward scan still reaches the period,
      so the ellipse only saves periods scanned.
    - The determinant size reverted to the norm form. They agree on every oracle view: the maps
      there are conformal, or diagonal on the ship's real axis.

**D12 as built (#1917).**
- **The template's deep tier is the engine.** From `EngineZoomThreshold` (1e12, the template's own
  `PerturbZoomThreshold`) a generated calculator renders on `PerturbationEngine`. Below it the
  emitted SP / AVX2 / GPU code runs unchanged.
  - The plan comes from the calculator's own equation. `EquationSource` (a new `{{EQUATION_LITERAL}}`
    substitution) is parsed at runtime with `CalculatorGenApi.ParseEquation`, so Compile & Load and the
    built-ins share one path.
  - `TwoStatePlan.TryCreatePrev` handles prev; `AstPlan.TryCreateAny` handles the rest (polynomials,
    conj / fold / re / im / abs, quotients, the elementary and piecewise functions: D3–D10).
  - The plan is built once per class. The sink is the template's `ColorFor`, so smooth count, DE and
    the theme see the same inputs as before.
  - The centre is the exact centre when the caller gave one (`IDeepCentre`), else the 16 limbs.
- **Every built-in equation gets a plan**, including the `if` form and prev. The template's
  PT / QD-PT / SA / HP-direct code is kept, but only as the fallback for an equation no plan
  covers (`EngineRefusal` gives the reason). It was not deleted: a Compile & Load equation outside
  the operator set (for example `atan`) still needs it.
- **Explorer limit:** Generated Z2–Z5, Tricorn and Burning Ship go to `EngineDoubleDeltaMaxZoom`
  (1e290). Before this they stopped at 1e50 (Z2–Z5) or 1e13.
- **Batch / Command builder:** no new render setting, so no gap. The generated types render through
  the same calculator headless.
- **Results** (`S1917CalcGenDeepTests`):
  - Every built-in generated equation renders deep on the engine, with no refusal.
  - Below the threshold (1e8 to 0.999e12) the colour buffer is byte-identical with the engine off.
  - At Misiurewicz views, the counts match direct BigFloat iteration (S1909's oracle):
    - Z2, Z3, Z5 at 1e40 and 1e120 (past the old QD ceiling);
    - Z4, Tricorn and Burning Ship at 1e40.
- **Mutants**, all caught:
  - the threshold dropped (the engine below 1e12): the byte-identity test;
  - the engine never used: the refusal test and the 1e120 views;
  - the plan's bailout² as 4: the oracle views;
  - the centre's Hi limb only: the oracle views.

**D13 as built (#1918).**
- **`R2Plan.TearDrop()`** is (z³ / c³)·(−i) + c, with z₀ = 0, dz₀/dc = 0 and |z|² ≥ 1024: the
  calculator's own formula and bailout.
  - It is rational in c, so the quotient difference from D6 takes 1 / c³.
  - 0 is a critical point, so rebasing applies.
- **`TearDropCalculator`**: past the HP threshold it renders on `PerturbationEngine` with that plan.
  - The sink is the calculator's `FinaliseDouble`, so smooth count, DE, normals and final z / dz are
    filled as before.
  - The centre is the exact centre when given, else the 16 limbs.
  - `UseEngine = false` keeps the hand-written DD-PT / QD-PT tiers. `DisableAcceleration` still
    selects full DD / QD.
  - `LastPrecisionLabel` is new: SP, PT, DD-PT, QD-PT, DD or QD.
- **Explorer limit:** 1e290, up from 1e50.
- **Batch / Command builder:** no new render setting, so no gap.
- **Results** (`S1918TearDropDeepTests`), at two Misiurewicz views (preperiod 3, period 1):
  - **The plan against direct BigFloat iteration**, at 1e40 and 1e120: 176/176 counts equal, and
    dz/dc within 1 % of a finite difference on every pixel.
  - **The calculator against the same oracle:** 176/176 at 1e40, and at 1e120 (past the old QD
    ceiling).
  - **Parity with the old PT below its ceiling:**
    - DD-PT at 1e20: 1536/1536 counts equal on both views;
    - QD-PT at 1e40: 1536/1536 and 1535/1536;
    - dz/dc (the old closed form against the engine's tangent): within 0.1 % at 1e20. At 1e40 the
      float buffers overflow, so only the plan test checks dz/dc there.
  - **Below the HP threshold:** byte-identical with the engine off.
- **Mutants**, all caught:
  - the sink's dz/dc zeroed (the dz/dc parity);
  - the centre's Hi limb only;
  - the engine never used;
  - the plan's bailout² as 4;
  - the plan's −i as +i.

**D2b as built (#1921).**
- **Prior art.** This is the rescaling that mathr's *Deep zoom theory and practice* (2021) describes;
  bibliography `#mathr-deep-zoom-2021`, with `#kf-floatexp` for the float-exp type. FF claims no
  novelty for it.
- **The pixel scale past double.** `PerturbationEngine.Render` takes an optional `FloatExp pixelScale`.
  - Below 2^−950 (zoom ~1e286), pixel offsets are doubles in units of 2^s0, the scale's exponent.
  - References and direct pixels are built in BigFloat from those offsets.
  - The BLA tables take dcMax as 0 there. This is a negligible change to a radius above 2^−1000.
- **The scaled-δ prelude.** While |δ| < 2^−900, δ is held as d·2^s.
  - **The BLA** applies to d: A·d + B·e·2^(s0−s). Its radius test sees |δ|² underflow to 0. That
    result is correct, since every positive radius² is ≥ 2^−1074 and so above it.
  - **A single step** runs the plan's own `DeltaStep` on δ and ε rescaled to ~2^−500, then scales the
    result back by the same power of two.
    - This is exact to ~2^−500 relative wherever the step is positively homogeneous of degree 1 at
      small δ: differentiable steps, or abs folds.
    - So one rule covers PowerPlan, AstPlan and R2Plan, with no float-exp operator set.
  - **Rebasing and the end of the reference.** Where Z is as small as δ, z = Z + δ is formed in the
    scaled unit. Otherwise z = Z: δ is under 2^−900 of it.
  - **The end of the reference in the dynamic plane:** the escape step onto the escaped point, as
    Pixel does it. Without it every pixel glitched there: Glynn at 1e300 sent 497 of 1536 pixels to
    direct BigFloat iteration (5 min); with it, 1 pixel (15 s).
  - **Not done in the prelude:** the dynamic plane's glitch test. Its z ≈ Z there, so the test cannot
    fire.
  - Once |δ| ≥ 2^−900 the double loop takes over. ε, below 2^−1000 there, then contributes under
    2^−100 of δ.
- **The derivative** is held as d·2^t once |d| passes 2^600. The scaled step is
  f′(z)·d + 2^−t·∂f/∂c (`DerivStepScaled` / `JacobianStepScaled`), and it is used in the double loop
  and the direct path too.
  - The sink gets d·2^(t − derivExp2): MandelbrotCalculator's distance unit (#1783 / #1843).
  - With t = 0 and derivExp2 = 0 nothing changes, bit for bit. This is true of every frame that
    passes no pixelScale and no unit, and of every double-depth frame whose derivative stays below
    2^600.
- **EscapeTimeCalculator.**
  - `IDeepZoom` (`ZoomLog10`, `SetZoomLog10`) is new; the host, the video engine and the poster path
    hand the log zoom over past double.
  - The deep path renders with the FloatExp scale past 1e280 and takes the distance unit
    (`DistanceUnitExp2`). `DistanceBuffer`, `FinalDr` / `Di` and `DistancePixelScale` are in that
    unit.
  - A colour map gets complex-plane units unless it opts in (`IColorMapDistanceUnit`). Those units
    are bit for bit as before, since the scaling is by a power of two.
  - The two-state families (Phoenix, Spider) only scale their delivered derivative into the unit.
- **FamilyDepth.**
  - Julia, Multibrot, Tricorn, Burning Ship, Magnet 1 / 2 and Glynn: unlimited (Mandelbrot's).
  - Phoenix and Spider stay at 1e290: the two-state engine's δ is double.
  - User Equation, the CalcGen template and TearDrop also stay at 1e290: they pass a double zoom.
- **Batch / Command builder:** no new setting. `--zoom` past double already reaches the poster path's
  `ZoomLog10`, which now reaches these calculators.
- **Results** (`S1921DeepFxTests`; counts against direct BigFloat iteration, and the derivative in the
  distance unit within 1 % of a BigFloat finite difference):
  - z² + c at 1e320 and 1e600: 176/176 counts, derivative 0 off;
  - z³ + c at 1e320: 176/176, derivative 0 off;
  - Tricorn at 1e320 and 1e600: 176/176, derivative 0 off;
  - Burning Ship at 1e320 (its fold through the rescaled step): 176/176, derivative 0 off;
  - Julia c = i at 1e320 (the dynamic plane, secondary references): 176/176, derivative 0 off;
  - Magnet 1 at 1e320, escape only and with its convergence stop: 176/176, derivative 0 off;
  - Glynn at 1e300 (dynamic, z^1.5): 176/176, derivative 0 off.
  - **The prelude forced at 1e60** against the double loop: 1536/1536 counts, and the derivative
    within 1e-6, for z², Tricorn and Burning Ship.
  - **EscapeTimeCalculator** (Multibrot d = 3) at 1e200 and 1e400 through `SetZoomLog10`:
    176/176 counts. `FinalDr` / `Di` are finite and within 1 % of the finite difference. Before, at
    1e200 the derivative overflowed to ∞ (S1907 measured it).
  - The poster path hands the log zoom over.
- **Mutants:**
  - **Caught:**
    - the scaled step not rescaled;
    - the prelude BLA's ε factor dropped;
    - the derivative delivered without the unit;
    - the calculator never passing the FloatExp scale;
    - the poster dropping the log zoom;
    - the prelude's escape step, by Glynn's direct-pixel bound.
  - **Survive, with reason:**
    - The scaled ∂f/∂c weight replaced by 1 (PowerPlan, R2Plan, the double loop's BLA). Once the
      derivative is scaled, t ≥ 100, so that term is ≤ 2^−100 against f′·d ~ 2^500. It is below
      rounding unless |f′| < 2^−450.
    - The prelude's scaled rebase. It needs |Z| < 8·|δ| < 2^−897, which no test view reaches (and,
      but for Z₀ = 0, practically none would).
  - **Not tested:** the live host's and the video engine's `IDeepZoom` sync (one line each, as the
    poster's).
- **Not covered** (follow-up #1957): Phoenix / Spider (the two-state engine's δ is double), and
  User Equation, the CalcGen template and TearDrop (they take a double zoom).

**#1957 as built (after D2b).**
- **The two-state engine** (`RenderTwoState`: Phoenix, Spider, User Equation prev) now has the same
  past-double support as `Render` (#1921):
  - a FloatExp pixel scale;
  - the scaled-δ prelude, with one scale s for both δs;
  - the escape step at the reference's end;
  - the tangent held as t·2^k (`TwoStatePlan.TangentStepScaled`);
  - the sink's distance unit.
  It still has no BLA and no rebase, as before.
- **User Equation, the CalcGen template and TearDrop**:
  - take a log zoom (`IDeepZoom`) and pass the FloatExp scale and the distance unit to the engine;
  - compute DE and normals from dz/dc in that unit;
  - keep their buffers and colour maps in complex-plane units as before (`MandelbrotCalculator.MapDz`,
    now public for Compile & Load). Past ~1e38 those float dz buffers are still ±∞, as they always
    were.
  - At unit 0 (below ~1e27) every path is unchanged bit for bit.
- **User Equation's rotation** is a linear warp, so the engine applies it to the 2^s0-unit offsets
  (`linearWarp`). A non-linear warp past 1e286 is still refused.
- **The AA jitter** (#1960) moves the exact centre by the log zoom's pixel (`IDeepZoom`).
- **FamilyDepth:** every engine family is unlimited: Phoenix, Spider, TearDrop, and Generated Z2–Z5,
  Tricorn and Burning Ship. User Equation keeps its explorer limit (1e13): its deep finders are #1952.
- **Results** (`S1957DeepFxRestTests`). Counts are against direct BigFloat iteration:
  - Phoenix and Spider on the engine at 1e320, at views on a saddle's stable manifold (S1912's
    construction, its Newton rescaled past double): 176/176. dz/dc is within 1 % of a finite
    difference (0 off).
  - The two-state prelude forced at 1e40 matches the double loop on 1536/1536.
  - Through `SetZoomLog10`:
    - TearDrop, Generated z³ / Tricorn and User Equation z² + c at 1e320: 176/176;
    - Phoenix through `EscapeTimeCalculator` at 1e320: 176/176.
  - User Equation rotated 90° at 1e320 is the unrotated frame turned a quarter: 992/992.
  - The poster path hands the log zoom to all four.
  - The AA jitter moves the exact centre by j pixels at 1e400.
- **Mutants:**
  - **Caught:**
    - the two-state step not rescaled;
    - the two-state tangent delivered without the unit;
    - Phoenix through the calculator without the FloatExp scale (at 1e320; at 1e300 the 48 px pixel is
      still a normal double);
    - TearDrop, User Equation and the template without the FloatExp scale;
    - the AA jitter ignoring the log zoom;
    - User Equation's warp not declared linear.
  - **Survives, with reason:** the two-state prelude's escape step. No test view keeps a pixel's δ
    below 2^−900 until Phoenix's reference ends. The same step in the one-state prelude is what kept
    Glynn off direct iteration in D2b.

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
