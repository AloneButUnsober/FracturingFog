# Theoretical & Frontier Fractal R&D

**Status:** living research document. **Owner:** ABUDev. **Started:** 2026-09-15.

This is the standing tracking doc for *research-frontier* fractal mathematics — types and
techniques that are **under-rendered** (the math is known but rendering is hard), **theoretical**
(little concrete reference imagery, so implementation is a validation challenge), or represent an
**envelope** worth pushing past what Fracturing Fog already does.

It is deliberately separate from [Fractal-Expansion-Roadmap.md](../Fractal-Expansion-Roadmap.md),
which tracked the *mainstream* expansion set (Magnet, Glynn, Halley, Mandelbox, KIFS, quaternion,
Apollonian, DLA, Flame, …) — all **19 slices of that roadmap are shipped**. This doc picks up where
that one stops: the stuff nobody renders well, or renders at all.

Canonical task list lives in the **GitHub issues**, per project convention
([CLAUDE.md](../../CLAUDE.md)). This doc is the design/bibliography backing; issues are the truth for
status. Link both ways.

- Epic (best-bets slice): **#850**. Children: Lyapunov **#851**, Magnet convergence colour **#852**,
  split-complex/coquaternion **#853**, transcendental Julia **#854**, Kleinian generalization design
  doc **#855**.
- Kleinian generalization **requires its own design doc + tracking issue (#855)** before any code
  (see §5.4).

---

## 0. Two standing rules for this document

### Rule A — every candidate gets a *toolchain reach* analysis

Whenever an entry here is researched, analyzed, or considered for implementation, it MUST also record
**how the two authoring DSLs could be extended to reach it**:

1. **CalcGen / DSL** (`CalculatorGen/`, User Equation, Sandbox) — can the *fractal iteration* be
   expressed or the generator extended? Note the shape the map must fit (`f(z, c)`), what new
   primitives/number-types/escape-criteria would be needed, and whether it is
   scalar-only or can ride the 5-path pipeline (scalar / AVX2 / perturbation / BLA / ILGPU).
   See [CalculatorGen-Architecture.md](CalculatorGen-Architecture.md),
   [CalculatorGen-Authoring.md](CalculatorGen-Authoring.md).
2. **ColorGen / Color Theme Editor** (`ColorGen/`, `UI.Avalonia/.../ColorGenEditor*`,
   the theme editor) — can the *colouring* the type needs be authored as a theme? Note what new
   per-pixel **inputs** the calculator must surface (mirroring the billiard `gateId`/`bounceCount`
   split, or the orbit two-type split), whether a new `ColorThemeKind` (Categorical, Orbit, …) is
   implied, and whether it is interpreter-only or can generate a GPU palette.
   See [ColorTheme-Enhancement-Roadmap.md](ColorTheme-Enhancement-Roadmap.md).

A candidate with no toolchain-reach analysis is **not ready to schedule**. The point: new math should,
wherever possible, *widen the authoring surface* — not just add one more hardcoded calculator.

### Rule B — sources are load-bearing

Every formula, algorithm, and claim traces to a citation in
[Resources-Bibliography.md](../Resources-Bibliography.md). Frontier math is exactly where an
unverifiable claim does the most damage (few reference images to sanity-check against). If a source
is not yet in the bibliography, add a one-line stub there and cite it — the cost of a stub is zero.
New bibliography anchors introduced by this doc are collected in §7.

---

## 1. Reconciliation — what already exists (do not re-scope)

Frontier ideas keep colliding with things FF *already ships*. Checked against the code
2026-09-15:

| Idea | State in FF | Where |
|---|---|---|
| Magnet M1 / M2 (Pickover rational maps) | **Shipped** (escape-time colour) | `MagnetOneKernel`/`MagnetTwoKernel`, `EscapeTimeCalculator` |
| Glynn (z^1.5 + c) | **Shipped** | `GlynnKernel` |
| Logistic / Feigenbaum bifurcation | **Shipped** (density histogram) | `LogisticCalculator` |
| Halley / Secant / Nova / Spider | **Shipped** | respective kernels |
| Kleinian 3D limit set | **Shipped, fixed tetrahedral 4-sphere preset** | `KleinianCalculator` (+GPU, +orbit-trap) |
| Quaternion Julia / Mandelbrot | **Shipped** | `QuatJulia`/`QuatMandelbrot` |
| Bicomplex (tessarine) Mandelbrot | **Shipped** | `BicomplexMandelbrotCalculator` |
| Pickover stalks / orbit traps | **Shipped as colouring** (F13 OrbitTrap kind) | theme system, not a fractal |
| Escape-angle / Böttcher / binary decomposition | **Shipped as colouring** | `ArgumentDecomposition`/`FieldLines`/`BinaryDecomposition` themes |
| Precision-sensitivity field | **Shipped** | `PrecisionFieldCalculator` (#628) |

**Implication:** the genuinely-open frontier is narrower than a naïve list suggests. The live gaps are
catalogued in §2–§4. Magnet exists but is coloured as plain escape-time — a real *colouring* gap
(§2.2). Kleinian exists but only as one fixed group — a real *generalization* gap (§5.3).

---

## 2. Bucket I — Under-rendered (math known, rendering is the barrier)

These have solid published math but are rare in the wild because the renderer is not a standard
escape-time loop, or the natural colouring differs from iteration count.

### 2.1 Lyapunov (Markus–Lyapunov) fractals — **#1 best-bet, in progress**

**Math.** Fix a periodic string over {A, B} (e.g. `AABAB`). At each pixel `(a, b)` iterate the
logistic map `x_{n+1} = r_n · x_n · (1 − x_n)`, where `r_n` cycles A→a, B→b through the string. Colour
by the **Lyapunov exponent**

  λ = lim (1/N) Σ ln | r_n · (1 − 2·x_n) |.

λ < 0 → stable/periodic (the "Zircon Zity" ridged solids); λ > 0 → chaotic. The image is in the
`(a, b) ∈ [0,4]²` parameter plane, *not* dynamical z-space.

**Render approach.** Per-pixel: warm-up ~`MaxIter/2` to settle onto the attractor, then accumulate
the log-derivative sum over the plot window. Cheap, embarrassingly parallel, SP double is plenty. The
[`LogisticCalculator`](../../Engine/Calculators/LogisticCalculator.cs) is the structural template
(same map, per-column iteration) but Lyapunov is 2D-per-pixel and emits a *signed scalar*, not a
density histogram.

**Why under-rendered:** not escape-time; needs a signed-exponent colour axis (diverging palette
around λ=0), and long warm-up for convergence. Almost nobody ships an interactive one.

**Toolchain reach (Rule A):**
- *CalcGen/DSL:* poor fit for the **iteration** generator (the map is a 1D real recurrence with a
  string-scheduled parameter, not `f(z, c)` over ℂ). Ships as a dedicated calculator like Logistic.
  A *future* DSL angle: expose the {A,B}-string and the base map (`logistic`, `sine`, `Gauss`) as a
  small parameter grammar so users can author variant Lyapunov sequences without code — track
  separately, not a blocker.
- *ColorGen/Color Theme:* **this is where the interesting reach is.** Lyapunov needs a new per-pixel
  input — the **signed Lyapunov exponent λ** — surfaced to the colour map (mirrors how billiard
  surfaced `gateId`; how PrecisionField rode `SmoothBuffer`). Cleanest first cut: **map λ onto the
  existing `SmoothBuffer`** (negative→one end, positive→other) so *every* existing 2D theme + Relief
  works unchanged, exactly like PrecisionField did. Then a follow-up ColorGen input `lyapunov`
  (signed) unlocks purpose-built diverging themes (stable ridges vs chaotic sea). Register the
  signed-λ field in `FractalCapabilities` (histogram-friendly).

**Sources:** Markus & Hess 1989; Dewdney *Scientific American* 1991 (Mario Markus's images);
A. Dewdney "Leaping into Lyapunov space." See §7 for anchors.

**Status:** implementing this session. Tracking issue: **#851**.

### 2.2 Magnet fractals — proper convergence colouring — **#2 best-bet, in progress**

**Gap.** `Magnet1`/`Magnet2` render (rational Pickover maps, pole-clamped) but are coloured as **plain
escape-time**. The mathematically-honest Magnet image colours by **convergence to the fixed point
z = 1** (the renormalization-of-Ising fixed point), not by escape. The two attractors (→1 and →∞) mean
standard escape colouring throws away half the structure — the basin boundary around z=1.

**Render approach.** Add a per-iteration convergence test `|z − 1| < ε` alongside the `|z|² > bailout`
escape test. Emit a distinct outcome (converged-to-1 vs escaped vs max-iter) + a smooth convergence
count. This is the natural interior treatment the capability map already advertises
(`SuppliesInterior` is set for Magnet1/2).

**Toolchain reach (Rule A):**
- *CalcGen/DSL:* the map already fits the kernel shape; the *convergence-to-a-root* test is a new
  escape-criterion primitive. If generalized (arbitrary target, arbitrary ε) it becomes reusable for
  any rational/Newton-like map in the generator — worth a `ConvergenceBailout` concept in CalcGen.
- *ColorGen/Color Theme:* needs a **convergence** colouring path analogous to Newton basins. The
  clean design: emit "converged" as an interior outcome and route it through the existing
  interior-aware colour map (`IInteriorAwareColorMap.MapInterior`) with the smooth convergence count
  as the shade — no new theme kind required, reuses the Newton/interior machinery. A ColorGen input
  `convergedTo`/`convergenceCount` would let themes distinguish the two attractors explicitly.

**Sources:** Pickover *Computers, Pattern, Chaos and Beauty* 1990; the Magnet maps derive from the
renormalization of the Ising model partition function (Yang–Lee). See §7.

**Status:** implementing this session (colouring enhancement, not a new type). Tracking issue: **#852**.

### 2.3 Kleinian / Schottky limit sets in **2D** (Indra's Pearls)

**Math.** Limit set of a Kleinian group generated by Möbius transforms — the classic *Indra's Pearls*
imagery. Not escape-time: enumerate group words breadth-first with a depth/tiling stop, plot the
limit-set points (fixed points of long words, or forward orbits under the generators).

**Render approach.** Combinatorial word enumeration (DFS/BFS over the generator alphabet) with
repetition/backtracking avoidance; the Maskit/Riley/Grandma's-recipe parameterization picks the
group. Rare because the renderer is *combinatorial*, not iterative — no per-pixel loop.

**Toolchain reach:** neither DSL reaches this today (both assume per-pixel iteration). Would need a
new "generator-word" renderer path (closest existing analogue: the IFS chaos-game / L-system path).
Colouring: by word length / generator parity — a Categorical `ColorThemeKind` (billiard precedent).

**Sources:** Mumford, Series & Wright *Indra's Pearls* 2002. See §7.

**Status:** **design doc LANDED** (2026-09-19) — [Indras-Pearls-2D-DesignPlan.md](Indras-Pearls-2D-DesignPlan.md)
(issue **#888**), split from the 3D Kleinian epic after the finding that Grandma's-recipe/Maskit are 2D
constructions (curve on Ĉ, not a 3D solid — see [Kleinian-Generalization-DesignPlan.md](Kleinian-Generalization-DesignPlan.md)
§3.4). Decision: new 2D `FractalType.IndrasPearls` modeled on `ApollonianCalculator` (Zoomable2D,
complex-Möbius word enumeration). Sliced **S1–S6 = #892–#897** (S1–S2 MVP; **S4/#895 = the re-homed
Maskit animation, fulfilling #879**). **Relates to** the 3D Kleinian generalization (§5.3).

### 2.4 Biomorphs (Pickover) — colouring, ships as a theme not a type

**Math.** Escape test on `|Re(z)|` **OR** `|Im(z)|` separately (rather than modulus), producing
organism-like "biomorphs." Pure colouring/bailout variant on any existing escape-time family.

**Toolchain reach:** *ColorGen* territory — it is a **bailout-shape** decision, exactly the kind of
thing a ColorGen input (`finalZr`, `finalZi`, already surfaced) can express. Likely shippable as a
theme + an optional per-axis bailout toggle on `FractalParameters`, no new calculator.

**Sources:** Pickover 1986 (*Computers and the Imagination*). See §7. **Status:** open, cheap.

---

## 3. Bucket II — Theoretical (thin reference imagery, validation is the risk)

Real published theory, near-zero faithful renders. Implementation risk is *correctness* — few images
to validate against.

### 3.1 Transcendental / entire-function dynamics — `λ·sin z`, `λ·cos z`, `λ·exp z` — **#854, SHIPPED**

**Math.** Julia sets of entire transcendental maps `z → λ·f(z)`, `z₀ = pixel`. Rich Fatou/Julia
theory (Baker domains, Cantor bouquets / "hairs"), but **no escape radius** — the essential
singularity at ∞ means `|z|` is not a membership test. Bound instead on a single **axis**: `|Im z| > R`
for `sin`/`cos` (they grow like `e^{|Im z|}/2`), `Re z > R` for `exp` (`|exp| = e^{Re z}`).

**Implementation.** `TranscendentalJuliaCalculator` (dedicated 2D calc, `FractalType.TranscendentalJulia`).
Per-map non-modulus bailout on the escape axis; chain-rule derivative `d := λ·f'(z)·d` fills
DE/normal/final-z so 2D + Relief + Phong themes work; continuous escape count = log-space crossing of
the bail axis. NaN/Inf guarded (double-exponential growth). Params: `TranscendentalMap`
(Sine/Cosine/Exp), `TranscendentalLambdaRe/Im`, `TranscendentalBailout`. Smoke: `λ·sin z` at λ=1
gives the textbook 2π-periodic bounded lobes + Cantor-bouquet hairs.

**Toolchain reach — DELIVERED in the live DSL (#859):**
- *CalcGen/DSL:* **FINDING** — the non-modulus bailout primitive *already existed*. `SandboxExpression`
  supports `< > <= >= == != && ||` + ternary + `re()/im()/abs()`, and `UserEquationCalculator` (#544)
  already evaluates a per-equation **bailout condition** (`Cond` non-zero ⇒ bail). So `abs(im(z)) > 50`
  (transcendental) / `abs(z-1) < 0.001` (convergence) parse and run. **#859** made it first-class: a
  **condition-replaces-modulus** mode (the condition becomes the SOLE escape, modulus disabled,
  non-finite guard) in BOTH `UserEquationCalculator` and `SandboxCalculator`, condition-trigger routed
  through the full smooth+normal+DE exterior path, `FractalParameters.UserEquationBailoutReplacesModulus`
  + editor toggle + per-entry persistence + cookbook demonstrator presets (`sin(z)+c`, `cos(z)+c`,
  `exp(z)+c`). **CalcGen "Compile & Load" codegen** support (deep-zoom/SIMD) is deferred → **#860**.
- *ColorGen/Color Theme:* the "hairs" are thin — benefit from the shipped escape-angle / decomposition
  themes; a `fastEscaping` boolean input would let themes paint the escaping-set structure directly.

**Sources:** Devaney's work on `λ exp z`, `λ sin z` (1984–1990s); Fatou 1926 (entire maps). §7.

**Status:** **SHIPPED** — `FractalType.TranscendentalJulia` (#854, dedicated calc) + the DSL
non-modulus bailout (#859, author-your-own in the equation editor). CalcGen codegen deferred (#860).

### 3.2 Higher/hypercomplex & split algebras — coquaternion Mandelbrot — **#853, SHIPPED**

**Math.** Iterate `z² + c` in algebras beyond ℂ/ℍ/tessarine. **Coquaternion** (split-quaternion):
`i² = −1, j² = k² = +1, ij = k = −ji` (non-commutative). Because squaring is `t·t`, the off-diagonal
cross terms cancel against their anticommuting partners, so the square reduces to the clean diagonal
form — identical to the quaternion square except the real-part signs from `j² = k² = +1`
(`t²_R = t1² − t2² + t3² + t4²`, imag `= 2 t1·(t2,t3,t4)`). The non-commutative derivative
`d(t²) = t·dt + dt·t` symmetrises to the same clean diagonal. Two imaginary units squaring to `+1`
give an **indefinite metric** → hyperbolic spike/wing extensions absent from the compact
quaternion/bicomplex sets.

**GOTCHA (finding):** the pure **split-complex 2D** Mandelbrot (`j² = +1`) is **not worth rendering**
— the idempotents `e± = (1 ± j)/2` split the iteration into two independent real quadratic
recurrences, so the "set" is a filled square. The coquaternion is the structured member of the family;
`SplitComplexMandelbrotCalculator` was therefore never built.

**Implementation.** `CoquaternionMandelbrotCalculator` = a clone of the Bicomplex CPU raymarcher with
the swapped squaring/derivative table (all the camera / lighting / DoF / SSAO / tonemap / HUD infra
reused verbatim). New `FractalType.Coquaternion`, full 3D registration (render host, camera input
routing, Cam3D region persistence, motion class Raymarch3D, animatable slice-W, params UI). **CPU-only
first cut** — no GPU kernel yet; slice fixed to the k-axis. Known limitation: the DE uses the Euclidean
`|t|²` (not the true indefinite metric), so thin hyperbolic sheets speckle under raymarch — a
follow-up could carry a metric-aware DE + GPU kernel.

**Toolchain reach:**
- *CalcGen/DSL:* still wants the pluggable **algebra / product-table descriptor** (basis signature +
  product table) so CalcGen emits *any* hypercomplex variant from one path — the coquaternion clone
  reinforces the case (two near-identical calculators now differ only in the table). High-leverage
  envelope work, tracked separately.
- *ColorGen:* rides the existing 3D DE/normal themes (same as Bicomplex). No new colouring input.

**Sources:** Rochon (bicomplex dynamics) 2000s; Norton (quaternion) 1982. §7.

**Status:** **SHIPPED** — `FractalType.Coquaternion` (#853). CPU-only; GPU kernel + metric-aware DE +
split-complex-square-confirmation are follow-ups.

### 3.3 Positive-area Julia sets & near-parabolic explosions (Buff–Chéritat)

**Math.** Julia sets of positive Lebesgue measure exist (Buff–Chéritat 2012) — a landmark theorem.
Faithful rendering is essentially unattempted because the fine structure is measure-theoretic, not
geometric; the sets are built via *near-parabolic* perturbations. Directly adjacent to §5 (parabolic
implosion).

**Toolchain reach:** far beyond current DSLs (needs controlled near-parabolic perturbation +
Écalle–Voronin machinery — see §5). Documented here as a *theory pointer*, not a schedulable item.

**Sources:** Buff & Chéritat, *Annals of Math* 2012. §7. **Status:** folded into the
**parabolic-implosion research thread as its *static sibling*** —
[Parabolic-Implosion-DesignPlan.md](Parabolic-Implosion-DesignPlan.md) §3.5 / spike S5
(#911). Same Fatou-coordinate / horn-map core as §5; the deliverable is the static
positive-area set rather than the implosion animation.

### 3.4 IFS with condensation / V-variable superfractals & fractal tops

**Math.** Barnsley's *superfractals* (V-variable: random selection among a finite set of fractals) and
**fractal tops** (colour an IFS attractor by the *address map* — the deterministic itinerary — rather
than chaos-game density). Solid theory, near-zero renders because tops need the address function, not
the chaos game.

**Toolchain reach:** extends the existing IFS/Flame chaos-game path with (a) a code-tree of maps
(V-variable) and (b) a top-function accumulator. *ColorGen:* the address string is a natural
**Categorical** colouring input (billiard precedent).

**Sources:** Barnsley *Superfractals* 2006; Barnsley "Fractal tops." §7. **Status:** open, niche.

### 3.5 Statistical / random-conformal — multifractal cascades, SLE, LQG

**Math.** Multiplicative cascades / canonical multifractal measures (Mandelbrot), Schramm–Loewner
Evolution curves, Liouville Quantum Gravity surfaces. Current research math, gorgeous, almost no
*artistic* renders. Biggest "show the world something new" payoff, highest math cost.

**Toolchain reach:** entirely new renderers (measure accumulation for cascades; stochastic ODE
integration for SLE). No DSL reach. *ColorGen:* density/measure themes (Buddhabrot-like).

**Sources:** Mandelbrot (cascades) 1974; Schramm (SLE) 2000; Duplantier–Sheffield (LQG) 2011. §7.
**Status:** long-horizon research pointers.

---

### 3.6 Dual-orbit escape-geometry map (parameter→escape-space scattering) — **original construction**

**Math.** FF-original construction (session notes 2026-09-17), *not* a named-literature set — treat
validation as the risk (Bucket II). Per parameter-space sample `s=(s_x,s_y[,s_z])`, iterate **two**
orbits under one shared nonlinear map, differing only in initial condition:

- `u_{z,0}=0`, `u_{c,0}=(x_c,y_c,0)`, both `u_{n+1}=f(u_n)+s`.

Run each to escape radius `R`; capture escape locations `E_z,E_c`, escape counts `n_z,n_c`, and the
previous state (approx escape trajectory). Derived **escape-space geometry**: midpoint
`M=(E_z+E_c)/2`, separation `D=E_c−E_z`, residual `|M−s|`, dual-orbit angle `∠(E_z−s,E_c−s)`,
scattering angles `∠(s,E−s)`, `Δn=n_c−n_z`. The object rendered is a **field over parameter space**
`F(s)=(chosen derived scalar)` — the calc→field→render split is the doc's central architectural claim
(FF already does exactly this for scalar fields via `SmoothBuffer`/`TrapBuffer`).

Map `f` choices: **complex-plane** square (XY-coupled), **radial** `|u|u`, **quaternion** `q²+S`
(`S` pure-imaginary). Component-wise `(x²,y²,z²)` is the **control only** — with both z-inits `=0` the
third coordinate is degenerate (`E_{z,z}=E_{c,z}`, separation confined to XY), and the zero-init
quaternion orbit collapses to the 2D subalgebra along `ŝ`. Richness lives in the **c-orbit + quaternion**;
skip the component-wise baseline as a deliverable.

**Seed-decoupling degeneracy (load-bearing — drives S1/S3 scope).** Every square map here has
`f(0)=0`, so the seed-0 orbit satisfies `z_1 = 0²+s = s`. **If the c-seed is set equal to `s`** (the
naïve "per-pixel parameter is both the added constant and the c-init" reading), then the c-orbit is the
z-orbit advanced by exactly one step (`c_n = z_{n+1}`): the two orbits are a **one-iteration shift**, so
`E_c = E_z`, `D ≡ 0`, `Δn ≡ −1`, dual angle `≡ 0`. Every *dual*-orbit field collapses to a constant and
only single-orbit scalars survive — reproducing the **plain Mandelbrot/Julia** exterior (escape-time /
external-angle / exterior-distance). The pure-complex `c=s` case is therefore a **control**, not the
deliverable. The dual construction is non-degenerate **only when the c-seed is decoupled from `s`**, via
any of: (a) a **fixed independent c-seed** `k` (image plane = the c-seed plane → a Julia-type set of the
fixed-`s` map, seed-0 critical orbit as reference); (b) **`s_z ≠ 0`** so `(x_c,y_c,0)` is not parallel to
`s` (notes §8); (c) the **quaternion** map, where the seed-0 orbit is trapped in the 2D subalgebra along
`ŝ` while the c-orbit explores a different plane (notes §7). **S1 (#864) must specify the c-seed
decoupled** — expose `c` as an independent parameter (see §3.6 design Qs), never hard-wire `c=s`.

**Render.** First cut = select one derived scalar → `SmoothBuffer` (**PrecisionField #628 precedent** —
dual *tier* there, dual *init* here) → every 2D theme + Relief-3D height + S9 mesh export works
unchanged. Escape-*space* deposition variant (render at `E`, not at `s`) rides the Buddhabrot
accumulation buffer. Vector-field/glyph mode (arrow = `M−s`) is a **genuinely new render path** (defer).
Multiresolution tile-cache is **redundant** with FF's fast recompute + deep-zoom perturbation (skip).

**Design parameters (`c`, `s`, map) and their visual roles.**
- **`c` is an independent user parameter** (`FractalParameters`, animatable — Julia c-drift #92 precedent),
  never hard-wired to `s` (see degeneracy note). Two modes: *parameter-space* (image = `s`-plane, `c` a
  fixed global seed knob) and *Julia-space* (image = `c`-plane, `s` the knob). First cut = parameter-space
  with editable `c`.
- **Role split:** **`s` selects the dynamical regime — the *set*; `c` selects the probe within it — the
  *texture on that set*.** Varying `c` leaves the silhouette (the seed-0 escape set) fixed and re-textures
  the dual fields (`c→0` → toward degenerate/low-contrast; `c` near a fixed/periodic point → long-transient
  ridges; `c` in another exit channel → the `Δn`/angle field re-patterns). A `c`-sweep animates texture on a
  stationary body. Varying the fixed `s_z` is a **decoupling / phase dial** (flat control → rich scattering;
  a literal "turning-on" animation); `s_x` panning travels across the set.
- **Map is a first-class enum** (`ComponentWise` control / `ComplexPlane` / `Radial |u|u` / `Quaternion q²+S`
  — the notes' `IOrbitMap` + §4 pluggable-algebra envelope). Toggling is not a tweak: it changes the
  **dimensionality + symmetry class** of the output (complex = planar 2-D field / Relief terrain, cheap;
  quaternion = rounded Norton-lobe 3-D solid / mesh, showcase; radial = spherical shells). The map
  comparison is itself a research goal (notes §13).

**Render contexts — one calc, several projections (canonical-field principle).** Compute the canonical
per-sample field **once** (`s, E_z, E_c, n_z, n_c`, prev-state) and project it several ways with no
re-iterate (notes §22/§31): **(1) parameter-space field** (render at `s` → `SmoothBuffer` → themes / Relief
/ mesh; deterministic, boundary-hugging; wants **one `s`/pixel**); **(2) escape-space deposition** (render
at `E` → Buddhabrot accumulation → density cloud in *output* space; **wants many `s`**, converges — the
opposite sampling regime); **(3) vector/glyph displacement field** (`M−s`; genuinely new render path,
design-gated). These are **complementary, not redundant** — same dataset, different questions — and the
calc→field→render split makes building all three cheap-incremental. Ordering unchanged: field (S1–S3) first,
deposition (#867) after MVP, glyph (#868) gated.

**Toolchain reach.**
- **CalcGen/DSL:** the iteration *is* `f(u)+s` — it fits the existing map grammar. The novelty is the
  **dual initial condition** + **escape-space state capture**, not the map. DSL reach = an *orbit-pair
  wrapper* (run any authored map twice, differing init) + an *escape-state capture* primitive (partly
  present: previous-state, escape-angle). Radial/quaternion `f` want the pluggable algebra/product-table
  descriptor (§4). Scalar path first; AVX2 lane-divergence identical to any escape-time map.
- **ColorGen/Color Theme:** surface the derived scalars as per-pixel **inputs** (mirror the billiard
  `gateId`/`bounceCount` split): `escapeSeparation`, `midpointResidual`, `dualOrbitAngle`,
  `escapeAngleZ/C`, `deltaN`. First cut maps one onto `SmoothBuffer` (every 2D theme + Relief free);
  follow-up ColorGen inputs enable diverging scattering-angle themes. Register in `FractalCapabilities`.

**Research context (what this relates to — no proper name of its own).** The construction is a novel
*combination*, but it sits in known territory and should be described that way (Rule B):
- **Chaotic scattering** (Ott & Tél 1993) — the closest fit. A *scattering function* maps an input
  parameter → an output observable (deflection angle, dwell time); `dualOrbitAngle`, `scatteringAngle`,
  `Δn` **are** scattering observables, with fractal exit-basin boundaries + sensitive dependence. This is
  the escape-**geometry** (where/how the orbit leaves) view, vs FF's usual escape-**time**.
- **Fractal basin boundaries / exit basins** (Grebogi–Ott–Yorke 1983; Wada basins, Nusse–Yorke) — the
  `Δn` / exit-channel field is a basin-boundary fractal.
- **Critical-orbit comparison.** The `z`-orbit (seed 0) is exactly the **critical orbit** of `z²+s`
  (`f'=0 ⇒ z=0`), the orbit that governs the dynamics (Fatou); the construction measures an arbitrary
  orbit `c` *relative to the critical orbit* — a principled reference, not an arbitrary one.
- **Finite-size/finite-time Lyapunov & Lagrangian coherent structures** (Aurell et al. 1997 FSLE;
  Haller 2015 LCS) — `D=E_c−E_z` is the finite separation of two trajectories differing in initial
  condition; as a field over parameter space it is the aesthetic twin of an FTLE/FSLE map (the object
  fluid dynamics renders to find transport barriers).
- **Biomorphs / orbit traps** (Pickover) — measuring orbit *geometry* at bailout rather than modulus;
  this generalizes that to escape *location + angle*.

**Applications this render style lends itself to** (the picture — a field over parameter/initial-condition
space of an escape or separation observable — is what these fields already visualize; the calc→field→
height-field/mesh split means FF could render genuine such fields if fed real systems):
- **Fluid mixing / transport** — FTLE ridges = Lagrangian coherent structures: ocean/atmosphere mixing,
  spill/pollutant/aerosol dispersal, transport barriers (biggest sci-viz crossover).
- **Predictability / ensemble forecasting** — finite-time divergence of nearby ICs = error-growth /
  predictability-horizon maps (`Δn` ~ time-to-divergence).
- **Chaotic-scattering physics** — three-body escape/ejection, particle scattering, reaction dynamics /
  transition-state theory, billiards.
- **Multistability & tipping points** — basin-of-attraction / safe-operating-region maps: power-grid and
  structural stability, ecology regime shifts, neuroscience attractor states.
- **Astronomy** — orbital-stability / ejection maps (cluster dynamics, planetary stability).
- **Numerical analysis** — Newton/root-finding basins; `M−s` = a solver-behaviour vector field.
This **sci-viz crossover** is unique to §3.6 among the theoretical roadmap items (the pure-aesthetic
types — Kleinian, transcendental, coquaternion — have no applied twin) — a "why it matters beyond pretty
pictures" hook.

**Fit.** Reuses the #626 chaotic-scattering framework (billiard / PrecisionField / escape-angle),
`IHeightFieldSource`/`ReliefHeightField`, S9 mesh export, and the Quat calculators. It is a new
**control-map + measurement framework**, not a new engine.

**Sources:** Ott & Tél 1993 (chaotic scattering); Grebogi–Ott–Yorke 1983 + Nusse–Yorke (fractal / Wada
basin boundaries); Aurell et al. 1997 (FSLE); Haller 2015 (Lagrangian coherent structures); Pickover
(biomorphs / orbit traps); Norton 1982 (quaternion Julia rendering); Green (Buddhabrot / escape-space
deposition). §7.
**Sources:** Ott & Tél 1993 (chaotic scattering); Norton 1982 (quaternion Julia rendering); Green
(Buddhabrot / escape-space deposition). §7.
**Status:** ready to schedule — tracking issue + slices **S1–S3 (MVP, ~1 wk)**, **S4–S6 optional**.

---

## 4. Bucket III — Envelopes to push (axes, not single types)

The generalization directions the above cluster into. Each is a *capability* that unlocks a family.

| Envelope | Current FF ceiling | Push to |
|---|---|---|
| **Algebra** | ℂ, ℍ, tessarine (inlined) | split-complex, coquaternion, octonion, dual numbers — via a pluggable **product-table descriptor** (§3.2) |
| **Map class** | polynomial + a few rational (Magnet) | transcendental (§3.1), meromorphic — via a **non-modulus / convergence bailout** primitive (§2.2, §3.1) |
| **Colouring target** | escape count, orbit trap, escape angle, basins | **Lyapunov exponent** (§2.1), convergence-to-root (§2.2), address maps / fractal tops (§3.4), distance-to-limit-set |
| **Object type** | attractors + parameter sets | **limit sets** (§2.3/§5.3), parameter-space puzzles (MLC), measures (§3.5) |
| **Precision regime** | deep-zoom perturbation (accuracy) | **near-parabolic implosion**, where perturbation theory breaks *by design* (§5) |

The two highest-leverage *toolchain* investments (each unlocks multiple types):
1. **Non-modulus / convergence bailout primitive** in CalcGen/DSL → transcendental + Magnet + Newton-ish.
   **DONE in the live DSL (#859)** — condition-replaces-modulus in `UserEquation` + `Sandbox` (the
   primitive already existed via #544 + `SandboxExpression` comparisons; #859 made it first-class).
   CalcGen codegen deferred (#860).
2. **Pluggable algebra/product-table descriptor** in CalcGen → every hypercomplex variant from one path.
   (Reinforced by the #853 coquaternion clone — two calculators differ only in the product table.)

---

## 5. Flagship far-future endeavour — **Parabolic implosion**

> User's explicit long-horizon interest. This section is the north star, not a near-term slice.

### 5.1 What it is

At a **parabolic parameter** (e.g. `c = 1/4`, the root of the main cardioid, where the fixed point has
multiplier exactly 1), the Julia set is *discontinuous* under perturbation: nudge `c` off the
parabolic value and the Julia set **explodes** — it does not vary continuously. Rendering the
*implosion* means animating `c → c₀ + εe^{iθ}` and watching the set discontinuously reorganize. The
controlling objects are the **Écalle–Voronin horn maps** and **Lavaurs maps** (parabolic
renormalization); Lavaurs's theorem describes the limits.

### 5.2 Why it is a genuine frontier for FF

- Every FF deep-zoom tool is built to make perturbation theory *accurate* (perturbation + series
  approximation + rebasing, DD/QD). Parabolic implosion is the regime where linear perturbation
  **fails by design** — the interesting content is precisely the breakdown. It inverts the engine's
  usual goal ([[project_detail_depth_limit]], [[project_wave214_qdfloor]] are about *avoiding* this;
  here we *want* it).
- Near-zero faithful artistic renders exist → maximum novelty.
- Directly adjacent to positive-area Julia sets (§3.3), which are *constructed* via near-parabolic
  perturbation — a shared machinery.

### 5.3 What it would take (research spikes, not a slice yet)

1. **High-accuracy near-parabolic orbit integration** — the whole point is behaviour as the
   multiplier → 1; needs careful accuracy near the parabolic fixed point (FF has DD/QD, a real asset).
2. **Écalle–Voronin / horn-map** implementation — the analytic invariants; steep math, essentially a
   research project. Validate against Lavaurs's limit theorem.
3. **Implosion animation hook** — animate `ε, θ` and render the set family; the scene/animation engine
   already supports parameter animation (enum-param hook precedent, #632), so the *animation* plumbing
   largely exists; the *math* is the cost.

**Toolchain reach (Rule A):**
- *CalcGen/DSL:* out of reach — this is not an `f(z, c)` per-pixel loop but renormalization-operator
  machinery. It would ship as a bespoke research calculator, possibly in its own module.
- *ColorGen/Color Theme:* the *output* (a Julia set at a perturbed parameter) colours with existing
  Julia themes; the novelty is temporal (the implosion animation), so the reach is into the
  **animation/scene** system, not ColorGen. A "phase θ" input could drive a cyclic palette.

**Sources:** Douady (parabolic implosion) 1994; Lavaurs 1989 (thesis); Shishikura (parabolic
renormalization, Hausdorff dimension of ∂M = 2) 1998; Écalle (résurgence). §7.

**Status:** **far-future — design doc LANDED (2026-09-19):**
[Parabolic-Implosion-DesignPlan.md](Parabolic-Implosion-DesignPlan.md) (spike/design
issue [#911](https://github.com/AloneButUnsober/FracturingFog/issues/911)). Scopes
this **and §3.3 positive-area Julia (the *static sibling*)** as **one** research
thread — they share the Écalle–Voronin / parabolic-renormalisation core (§3.5
statistical/SLE is *disjoint* and excluded). Three-tier render (Tier A naïve
animation / Tier B near-parabolic accuracy / Tier C faithful horn-map limit),
spike-gated S0–S6; **S1 = the achievable first increment** (naïve implosion
animation on the existing Julia calculator, ships without the deep core). Still not
scheduled to build — the design doc is the north star; any code starts from S0
(feasibility spike), never direct implementation.

### 5.4 Kleinian 3D generalization (near-term epic item) — **design doc required**

Distinct from §2.3 (2D Indra's Pearls). The shipped `KleinianCalculator` is **one fixed tetrahedral
4-sphere group**. Generalizing it (user-editable inversion-sphere list, alternate Schottky
configurations, full Möbius-group composition, true analytic DE vs the inversion-scale heuristic — the
follow-ups already logged in [Fractal-Expansion-Roadmap.md](../Fractal-Expansion-Roadmap.md) §B.4) is
an epic item **gated on its own design doc + tracking issue** before any code, given the combinatorial
group-parameterization surface. Do not extend `KleinianCalculator` ad hoc.

**Design doc LANDED (2026-09-19):** [Kleinian-Generalization-DesignPlan.md](Kleinian-Generalization-DesignPlan.md)
(issue #855). Decision: extend `FractalType.Kleinian` **param-driven** (not a new type) via a
serializable `KleinianGroup` descriptor + three-tier DE (Tier 0 tetrahedral = byte-identical).
Toolchain reach — DSL/CalcGen **out of reach** (no per-pixel hook for group-word/3D-inversion DE);
ColorGen/Theme **in reach** (categorical word-length kind + generalized orbit-trap). Sliced **S1–S8**:
**#874** descriptor+inversion-DE / **#875** preset library / **#876** editor UI = **MVP**; **#877**
Möbius-word DE / **#878** colour drivers / **#879** Maskit animation / **#880** GPU parity / **#881**
analytic DE. Marquee = Grandma's-recipe trace animation along the Maskit slice (#879).

---

## 6. Working method (per candidate, before it becomes an issue)

1. Confirm it is **not already shipped** (§1 — check the code, not memory).
2. Write the **math + render approach** with citations (Rule B).
3. Write the **toolchain-reach analysis** (Rule A: CalcGen/DSL *and* ColorGen/Color Theme).
4. Decide: dedicated calculator vs DSL/generator extension vs colouring-only.
5. File the **GitHub issue(s)**; link doc ↔ issue both ways; if it is Kleinian-generalization or
   parabolic-implosion class, the issue is a **design-doc/spike**, not an implementation.
6. Follow the **new-2D-calculator registration checklist** (~13 sites; template = RandomTile /
   ChaoticBilliard) when a new `FractalType` is genuinely warranted.

---

## 7. Bibliography anchors introduced by this doc

These are added as stubs to [Resources-Bibliography.md](../Resources-Bibliography.md) under the
appropriate sections; flesh out on the next pass. Cite inline from the sections above.

- **Markus & Hess 1989** — Mario Markus, Benno Hess. *Lyapunov exponents of the logistic map with
  periodic forcing.* Computers & Graphics 13(4), 1989. Origin of the Lyapunov/"Zircon Zity" fractal.
- **Dewdney 1991** — A. K. Dewdney. *Leaping into Lyapunov space.* Scientific American, Sept 1991.
  Popularized the A/B-string Lyapunov images.
- **Pickover 1990** — Clifford A. Pickover. *Computers, Pattern, Chaos and Beauty.* St. Martin's, 1990.
  Magnet maps + biomorphs (already partly cited for attractors).
- **Pickover 1986 (biomorphs)** — C. A. Pickover. *Biomorphs: computer displays of biological forms
  generated from mathematical feedback loops.* Computers and the Imagination.
- **Mumford, Series & Wright 2002** — *Indra's Pearls: The Vision of Felix Klein.* Cambridge UP.
  Kleinian-group limit sets (Grandma's recipe, Maskit slice).
- **Devaney (transcendental)** — Robert L. Devaney. *Exploding Julia sets* / work on `λ exp z`,
  `λ sin z`, 1984–1990s. Entire-map Julia sets, Cantor bouquets.
- **Rochon (bicomplex)** — Dominic Rochon. *A generalized Mandelbrot set for bicomplex numbers.*
  Fractals, 2000. Basis for hypercomplex/split-algebra iteration.
- **Barnsley 2006** — Michael Barnsley. *Superfractals.* Cambridge UP. V-variable IFS + fractal tops.
- **Buff & Chéritat 2012** — X. Buff, A. Chéritat. *Quadratic Julia sets with positive area.* Annals
  of Mathematics 176(2), 2012.
- **Mandelbrot 1974 (cascades)** — B. Mandelbrot. *Intermittent turbulence…* J. Fluid Mech.
  Multiplicative cascades / multifractal measures.
- **Schramm 2000 (SLE)** — Oded Schramm. *Scaling limits of loop-erased random walks…* Israel J.
  Math. Schramm–Loewner Evolution.
- **Duplantier & Sheffield 2011 (LQG)** — *Liouville quantum gravity and KPZ.* Inventiones.
- **Douady 1994 / Lavaurs 1989 / Shishikura 1998** — parabolic implosion, Lavaurs maps, and the
  Hausdorff dimension of ∂M = 2 (parabolic renormalization). The parabolic-implosion literature.
- **Écalle** — Jean Écalle. *Les fonctions résurgentes.* Résurgence theory underlying horn maps.
- **Ott & Tél 1993** — Edward Ott, Tamás Tél. *Chaotic scattering: An introduction.* Chaos 3(4), 1993.
  Escape time, exit basins, angular observables — the scattering vocabulary §3.6 borrows.
- **Norton 1982** — Alan Norton. *Generation and display of geometric fractals in 3-D.* Computer
  Graphics (SIGGRAPH) 16(3), 1982. Quaternion Julia rendering — basis for §3.6's 4D→3D projection.
- **Green (Buddhabrot)** — Melinda Green. *The Buddhabrot technique*, c. 1993 (web). Escape-space
  orbit deposition — the accumulation model §3.6's escape-space render variant reuses.
- **Maskit 1988** — Bernard Maskit. *Kleinian Groups.* Springer Grundlehren 287. Fundamental domains,
  the Maskit slice, discreteness — the Kleinian-generalization design doc (§5.4).
- **Ahlfors 1981/1985** — Lars V. Ahlfors. *Möbius transformations in several dimensions* (1981) /
  *Möbius transformations and Clifford numbers* (1985). Vahlen-matrix / Clifford representation of
  `Möb(Ŝⁿ)` — basis for the Kleinian analytic-DE form (design doc §3.5/§4.2).
- **Hart, Sandin & Kauffman 1989** — J. C. Hart, D. J. Sandin, L. H. Kauffman. *Ray tracing
  deterministic 3-D fractals.* SIGGRAPH Computer Graphics 23(3). The distance-estimator ray-tracing
  method the 3D raymarchers use.
- **Soddy 1936 / Graham, Lagarias, Mallows, Wilks & Yan 2003** — F. Soddy, *The kiss precise* (Nature)
  / *Apollonian circle packings: number theory* (J. Number Theory). Descartes circle theorem — basis
  for the Kleinian Apollonian-extrusion preset.
- **Vahlen 1902** — K. Th. Vahlen. *Über Bewegungen und complexe Zahlen* (Math. Ann.). Clifford-matrix
  Möbius representation (historical origin of the Ahlfors form).
- **Grebogi, Ott & Yorke 1983** — C. Grebogi, E. Ott, J. A. Yorke. *Fractal basin boundaries,
  long-lived chaotic transients, and unstable-unstable pair bifurcation.* Phys. Rev. Lett. 50. Fractal
  basin boundaries — the §3.6 `Δn` / exit-channel structure.
- **Nusse & Yorke 1996** — H. E. Nusse, J. A. Yorke. *Wada basin boundaries and basin cells.* Physica D
  90. Wada (three-or-more-way) exit basins.
- **Aurell, Boffetta, Crisanti, Paladin & Vulpiani 1997** — *Predictability in the large: an extension
  of the concept of Lyapunov exponent.* J. Phys. A 30. Finite-size Lyapunov exponent (FSLE) — the
  finite-separation reading of `D=E_c−E_z` (§3.6).
- **Haller 2015** — George Haller. *Lagrangian coherent structures.* Annual Review of Fluid Mechanics
  47. FTLE ridges / transport barriers — the applied twin of the §3.6 separation field.

---

## 8. Change log

- **2026-09-15** — Doc created. Reconciled frontier ideas against shipped code (§1); catalogued
  Buckets I–III; set Lyapunov (§2.1) + Magnet convergence-colour (§2.2) as the two in-progress
  best-bets; flagged parabolic implosion (§5) as flagship far-future; recorded Kleinian-generalization
  design-doc requirement (§5.4). Bibliography stubs (§7) queued for `Resources-Bibliography.md`.
- **2026-09-17** — Added §3.6 dual-orbit escape-geometry map (FF-original construction, from session
  notes). Feasibility = high (rides #628 PrecisionField dual-scalar precedent + #626 scattering +
  Relief/mesh). Filed tracking issue #863 with slices #864 (S1 calc) / #865 (S2 field selector) /
  #866 (S3 quaternion) = MVP ~1 wk; #867 (S4 deposition) / #868 (S5 glyphs, design-gated) optional;
  #869 (S6 tile-cache) deferred. Bibliography stubs added (§7): Ott–Tél 1993, Norton 1982, Green.
- **2026-09-19** — Kleinian generalization (§5.4) **design doc landed**:
  [Kleinian-Generalization-DesignPlan.md](Kleinian-Generalization-DesignPlan.md) (issue #855). Decision:
  param-driven extension of `FractalType.Kleinian` via a `KleinianGroup` descriptor + three-tier DE
  (Tier 0 byte-identical). Sliced S1–S8 (#874–#881); S1–S3 = MVP (arbitrary inversion groups + presets
  + editor). Toolchain reach recorded (DSL out of reach; ColorGen/Theme categorical word-length kind in
  reach). Bibliography additions (§7): Maskit 1988, Ahlfors 1981/1985, Hart–Sandin–Kauffman 1989,
  Soddy 1936 / Graham et al. 2003, Vahlen 1902.
- **2026-09-19** — Dual-orbit escape-geometry (§3.6) fleshed out: **seed-decoupling degeneracy** note
  (`c=s` → plain Mandelbrot control; #864), **design-parameter** note (`c` independent/animatable,
  `s`=regime vs `c`=probe, `s_z` phase dial, orbit-map enum), **render-context** note (one canonical
  field → parameter-space / escape-space / glyph), and a **research-context + applications** note placing
  it in the chaotic-scattering / fractal-basin-boundary / FSLE–LCS literature (sci-viz crossover:
  fluid mixing, predictability, multistability, scattering physics). Encoded to #863/#864/#865.
- **2026-09-19** — **S1 (#864) shipped** — `DualOrbitEscapeCalculator` (complex-plane `u→u²+s`, two
  orbits from the critical seed 0 and a fixed decoupled `c`, escape geometry → SmoothBuffer). New
  `FractalType.DualOrbitEscape` (Zoomable2D, `IHeightFieldSource`), `DualOrbitField` selector
  {EscapeSeparation, MidpointResidual, DualOrbitAngle, DeltaN}, editable c-seed + labelled
  `DualOrbitCEqualsS` Mandelbrot control; new-2D-calc checklist + region persistence + params panel.
  **Validation (Bucket II): the `c=s` degeneracy is confirmed empirically** — separation ≡ 0 (D≡0, flat
  render), while the decoupled c yields rich escape-separation / angle / Δn fields around the Mandelbrot
  body (the angle field is the showcase). +11 tests, suite 2867 green. Free on arrival: 2D themes,
  ColorGen, Relief-3D height, S9 mesh (all read SmoothBuffer). Next: S2 #865 (field UI — done inline
  here), S3 #866 (quaternion `q²+S`).
- **2026-09-19** — **S3 (#866) shipped** — quaternion map variant. `DualOrbitMap` enum
  {ComplexPlane (default), Quaternion} + the Hamilton square `q²+S` (S=(0,s_x,s_y,s_z) pure-imaginary,
  reused from `QuatMandelbrotCalculator`); escape geometry on 4D coords via the Euclidean metric,
  scalar → SmoothBuffer. New params `DualOrbitCSeedZ` + `DualOrbitSZ` (the §3.6 decoupling / phase dial);
  quat-only UI rows, region persistence, animatable (c-sweep + s_z "turning-on"). The seed-0 orbit is
  trapped in the 2D subalgebra along ŝ; a decoupled c-seed off ŝ (c-seed Z ≠ 0) makes the pair diverge
  in 4D — non-degenerate. **The `c=s` shift degeneracy still holds** (c-orbit = z-orbit + 1 step ⇒ D≡0),
  confirmed by a test, so decoupling remains required. Visually distinct from the complex map (round
  Norton disk + concentric scattering halos; the angle field is again the showcase). +5 tests, suite 2872
  green. **Dual-orbit MVP (S1–S3) complete.** S4–S6 optional (escape-space deposition / glyphs /
  tile-cache).
- **2026-09-19** — **3D quaternion dual-orbit render shipped (#909, pulled forward).** Finding from the
  S3 review: the quaternion *2D parameter-space* field is a radially-symmetric **disc** (correct — the
  quaternion Mandelbrot is a solid of revolution; pure-imaginary S samples it radially), so the
  quaternion's payoff needs a 3D render. Delivered as an **opt-in dual-orbit surface-colour mode on the
  existing Quaternion Mandelbrot raymarcher** (`QMandelDualOrbitColor` + a decoupled second-orbit seed
  `QMandelDualSeed{X,Y,Z}`): render the detailed 3D quaternion solid, and colour each surface point c by
  the smooth escape count of a decoupled seed orbit under q²+c — a Julia-style probe at that parameter
  that textures the solid with the dual-orbit escape geometry. Reuses the whole raymarch / DE / lighting
  stack (GPU gated to CPU when on); default off = byte-identical. Params + Clone + region persistence +
  UI (checkbox + 3 seed fields). +5 tests, suite 2877 green. The result is a lit 3D solid whose surface
  bands shift with the seed — the 3D, detail-rich quaternion result the disc could not give. Closes #909.
  Bibliography additions (§7 + `Resources-Bibliography.md`): Grebogi–Ott–Yorke 1983, Nusse–Yorke 1996,
  Aurell et al. 1997 (FSLE), Haller 2015 (LCS).
- **2026-09-26** — **Post-MVP review of §3.6 → follow-ups S7 #970 / S8 #971 / S9 #972.** Findings
  (numerically verified): (1) the escape-location fields read E at radius ≥ the bailout while s sits at
  ≤ 2, so `EscapeSeparation` / `MidpointResidual` / `DualOrbitAngle` are **bailout-radius artifacts**
  (`|M−s| ≈ |M|`; s drops out); only Δn is intrinsic — `2^−Δn = G(c)/G(0)`, the Green's-function
  ratio. (2) Hamilton `q²+C` is rotation-covariant (rotate seed and C together → identical escape
  count), so every quaternion `q²+c` object is a surface of revolution — the concentric rings are
  forced, no seed fixes them. (3) The user's original experiment is a **1D sx sweep with fixed seeds**
  (reproduced another agent's plot of it: component-wise map, `s=(1.75·sinθ, −0.75, 0.3)`,
  `c0=(.5,.5,.5)`, R=2); the shipped s-plane is its faithful 2D extension. All variants are slices of
  one field `F(c0, s)`; the (c.x, c.y, s.x) volume with the complex-plane map is a raymarchable 3D
  fractal (every s.x layer a filled Julia set).
  **S7 (#970) shipped** — intrinsic fields: `GreenRatio` (log2 G_c/G_z = n_z − n_c, centred ±span
  octaves, `DualOrbitRatioSpan` default 8) and `ExternalAngleDelta` (level-1 Böttcher angles by backward
  lifting of arg u_k, no branch-cut product; seed 0 ⇒ the Mandelbrot parameter external angle; complex
  map only). New `DualOrbitBailout` (default 128 = byte-identical) makes the R-dependence visible. Tests
  check independent invariants: both new fields bailout-invariant (128 vs 4096), GreenRatio = analytic
  G ratio to 1e-3 octave, known parameter rays (s>¼ → 0, s<−2 → ½), conjugation antisymmetry;
  separation shown to move with R.
- **2026-09-26** — **S8 (#971) shipped** — slice-axis selector. `DualOrbitSliceAxes` {SxSy (default,
  byte-identical), CxCy, CxSx, CxSy, CySx, CySy}: the image spans any two of (c.x, c.y, s.x, s.y), the other
  two come from `DualOrbitCSeedX/Y` and new `DualOrbitSX/SY` (animatable — animating s.x replays the
  original sweep). New single-orbit fields `EscapeTimeZ` / `EscapeTimeC`: EscapeTimeC stays live where the
  critical orbit is bounded (s in M), so it is the field for the Julia plane (CxCy) and the volume
  cross-section (CxSx: dome + filaments, the original s.x sweep stacked over c.x). Tests check field
  invariants: four slices through one (c0, s) point agree; the c-plane is point-symmetric (map even in u);
  the CxSx c.x = 0 column equals the critical orbit (Mandelbrot line); fixed s is inert in SxSy.
