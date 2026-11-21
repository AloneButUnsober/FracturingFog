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
- Gravitational & relativistic scattering (§2.6): black hole shadows epic **#1922** (S1 **#1923** builds
  the shared integrator), Hénon–Heiles **#1942**, restricted three-body **#1943**, magnetised black
  hole **#1944**, spinning binaries **#1945**, Mixmaster **#1946**, black hole + ring **#1947**,
  microlensing **#1948**.
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

### 2.5 Wada mirror spheres — chaotic scattering in 3D — **#1439, S1–S3 shipped**

**Math.** Four mutually tangent mirror spheres at the vertices of a tetrahedron; a light ray in the
inner chamber leaves through one of four openings (one per face). The four exit basins share one
fractal boundary with the Wada property: every boundary point is on the boundary of all four basins.
Laboratory demonstration and the photograph that motivates the epic: [(Sweet, Ott & Yorke 1999)](../Resources-Bibliography.md#sweet-ott-yorke-1999).
The name is from the Lakes of Wada [(Yoneyama 1917)](../Resources-Bibliography.md#yoneyama-lakes-of-wada).
Ray-traced renders of the same scatterer are prior art [(Bourke 1998)](../Resources-Bibliography.md#bourke-wada).
This is the 3D sibling of the 2D chaotic billiard (#627, #1445).

**Toolchain reach.** A new calculator (`MirrorSpheresCalculator`), no DE: ray-sphere intersection is
closed-form, so each bounce is exact, and the trace stops where the ray leaves the chamber (the paper's
basin definition; classifying by final direction instead gave smooth, non-Wada boundaries from bounces
off the balls' outer sides). *ColorGen / Theme* reach is free: the categorical outcome goes through
`IBilliardColorMap` (gate = opening, gateCount = 4), so the billiard themes, the Categorical theme kind
(#630) and billiard ColorGen programs (#633) drive it unchanged. *DSL* is out of reach (not an
iteration). *Batch*: region snapshot → `--param`. Photo mode (#1441) takes its sky / HDRI from
Lighting & FX and its mirror term from Schlick's Fresnel approximation [(Schlick 1994)](../Resources-Bibliography.md#schlick-fresnel).

**Validation (independent invariants).** Rays in the plane through three ball centres reduce exactly to
the 2D three-disk billiard; the cyclic coordinate shift permutes the openings; a tiny window around a
bisected boundary point shows all four openings (the Wada property itself).

**GPU (#1443).** An ILGPU trace (CUDA / OpenCL, the 3D families' path) writes per-pixel outcomes and the
CPU colour pass reads them, so frames match the CPU pixel for pixel. The trace stays in fp64 because the
basins are chaotic and fp32 would move boundary pixels. That makes it hardware-bound: on a GT 710
(fp64 at 1/24 rate) the 12-thread CPU is about 2× faster (1080p basins 91 vs 207 ms). So, as with
Buddhabrot #1220, Auto measures both on the first GPU frame and keeps the faster; `FF_GPU_MIRROR_SPHERES`
forces either.

**The 1999 photograph (#1444).** A built-in region reproduces the photo. `VertexUp` is a scene rotation
that puts one ball on top; the camera sits on an upper opening's outward normal (elevation acos(1/3)).
White comes from the table opening, red / blue boards from the other upper openings, and the viewing
opening and the room (`MirrorSpheresRoomColor`) are dark. The paper doesn't say which upper opening
held which board.

**Ray-path inset (#1447, 2D billiard).** The billiard image maps launch conditions, so its sine-band
"strands" are not trajectories. A live inset (Control Center → View → Ray Paths) traces the ray under
the pointer in real space through the render's own plane mapping and scene; Ctrl+click pins rays, so two
neighbours either side of a basin boundary show their divergence. Tests check the recorded paths against
the reflection law and against the render's per-pixel bounce counts. The inset is live only: it is never
drawn into a render or export, so it adds nothing to the batch. The mirror spheres could use the same
inspector later. #1503 fixed NDisk placing fewer disks than requested: its random field now spreads wider until every disk fits, and the play circle covers every disk. The params panel warns when Ring / ThreeDisk neighbours touch, which closes the exit gaps.

**Custom layouts (#1504).** The inset doubles as a layout editor: drag a disk to move it, wheel to resize,
double-click to add, right-click to remove. The first edit switches to the `Custom` geometry, seeded from the
current disks; the list is stored as `BilliardCustomDisks` (`x,y,r;...`), so it rides the region snapshot and
`--param BilliardCustomDisks=...` like any other per-family value. The inset re-traces while dragging; the render
follows on release. Exit gaps work for any layout: disks are ordered by centre angle from disk 0 and gate k is the
angular gap between disks k and k + 1, so a Custom list made from a ring builds the ring's scene. Every edit keeps
a 5% escape gap between disks and every disk within 10 units of the origin.

**Billiard Rays (#1505 tracker; S1 #1506).** A separate type, `BilliardRays`, turns the billiard on end: the
view is real space (y up, as in the inset), and instead of one launch per pixel a sweep emits many rays
(`BSweep` default: parallel beams with b swept across the play circle; also `AngleSweep`, `PointSource`,
`RandomFarField`, `PhaseSpace`). Each path is traced with the shared `BilliardScene` (so Custom layouts work)
and accumulated, length-weighted, into a density grid: segments are clipped with Liang & Barsky (1984) and
walked with the Amanatides & Woo (1987) cell traversal, so each pixel gets exactly the length inside it. The
log density is coloured like the Buddhabrot composite, disks drawn on top. Rays do not interact, so emission
order does not change a still; it matters only for the crossing slices (#1508, #1510). Rasterising by row
band in fixed ray order keeps the image identical across core counts and between live, batch and export.
Every sweep parameter rides the region snapshot / `--param`. Density plots of billiard trajectories are
standard; the crossing statistics planned in S3–S5 still need a prior-art check (#1512).
S2 (#1507) adds `BilliardRaysColorSource`: brightness stays the log density, the hue comes from either the
length-weighted mix of the rays' exit-gate colours (`Gate`: each exit basin's flow in its own colour, trapped
rays grey) or the length-weighted mean of one per-ray value (bounces, path length, entry / exit angle,
gates between entry and exit, self-intersection). Both ride the density splat, so the weights are the exact
in-pixel lengths. Billiard themes colour the gates; the gradient modes then use the default gradient.
S3 (#1508) adds crossings (`BilliardRaysMode` Paths / Crossings / Both): each ray, in sweep order, is checked
against the K rays drawn just before it (`BilliardRaysCrossingWindow`) and, counted separately, against itself.
Each crossing in view adds to a count grid and to a value: delta N (current minus previous bounce count at the
crossing), the crossing angle, or the path length along either ray from where it entered or to where it
leaves. The value is also the relief height field. The search is a uniform cell grid filled as rays arrive,
each segment listed in the cells its cell walk visits, with rays that leave the window skipped by per-cell
start pointers and compacted away; tests check it against a brute-force all-pairs search. This is the one
place emission order matters. Crossing counts grow fast: about 4.6 M at window 100 and 27 M at window 1000 for
60 k rays (0.45 s / 1.2 s release build, 800x600).
S4 (#1509) adds neighbour divergence (`Divergence` / `DivergenceOverPaths` modes): each ray is compared with
the ray emitted just before it when their launches are neighbours (same BSweep beam, consecutive AngleSweep /
PointSource rays; random sweeps have none). Their disk-hit sequences are compared, and the current ray's leg
leaving the last mirror both shared is drawn, valued by the bounce difference, a gate change, the separation of
their next hits, or the mirrors shared (`BilliardRaysDivergenceValue`). Neighbouring launches that split are
where the exit basins meet, so the legs are the real-space counterpart of the launch map's basin boundaries
(and of the uncertainty exponent, #1127). Tests re-trace each pair through the ChaoticBilliard launch map.
With more rays per beam the neighbours sit closer, so each beam crosses about the same number of boundaries;
the count grows only as the sweep resolves finer boundary structure.
S5 (#1510) adds the `Escaped` mode, the classification proposed in #1505: a drawn ray that leaves through a
gate without crossing any of the window's previous rays is *escaped*, one that crosses a previous ray is
*crossing*, one that never leaves is *trapped* (self-crossings do not count). The class belongs to the ray,
so this mode's crossing search covers the whole play circle, not just the view. Escaped rays' paths are drawn
(by gate distance, path length, bounces, grey, or hidden) and the other rays' crossings keep the S3 colouring;
the status bar reports the three counts. In practice few rays escape a reflecting array: a ray leaving a
convex mirror almost always crosses its sweep neighbour's incoming leg near the surface, so `escaped` mostly
picks out rays that strike first and shadow their neighbours, and widening the window shrinks it fast.
S6 (#1511) makes it animate and stay interactive. `BilliardRaysProgress` renders only the first fraction of
the sweep, in emission order (animatable, so a video shows the rays arriving). Progressive batches composite
after each batch, the Buddhabrot pattern, so a cancel leaves the last batch on screen; each batch equals a
one-shot render at the matching progress, and the last equals the normal render. The drag preview renders at
the preview size with every k-th ray (k = 1/scale^2, keeping the rays per pixel) and upscales it.
Cost (release build, 12 threads, 1920x1080, three beams): 20 k rays 43 ms, 200 k rays 293 ms, 1 M rays 1.17 s;
tracing alone is 62 ms of the 200 k case, so about 80 % is the length splat. The 1/4 drag preview of 200 k rays
takes 13 ms. **GPU: not now.** The splat is what a GPU would speed up, but the image is bit-identical across
core counts and between live, batch and export because each pixel sums in a fixed order; GPU atomics add in
any order, so keeping that would need fixed-point accumulation. With the preview keeping drags smooth, the
gain does not justify that yet.
S7 (#1512) adds three built-in presets (three-disk gate flow, ring crossings with relief, a custom five-disk
divergence), the user guide (`Docs/User/Billiard-Guide.md`, with batch examples), and a **prior-art check**.
Drawing billiard rays in real space is long established: ray tracing of reflecting scatterers, for example the
MPI-PKS interactive tracer of the Sweet-Ott-Yorke spheres (Glasser 2007), and the density of many rays is ordinary
accumulation rendering. Statistics of trajectory **self-crossings** are an established subject in periodic-orbit
theory (Braun, Heusler, Müller and Haake 2002; Müller 2003). Neighbour divergence is the real-space reading of
the launch map's sensitivity to initial conditions (the uncertainty exponent above). A limited search (web,
arXiv) found no prior treatment of crossings *between different rays* in sweep order, coloured by their
bounce-count difference, or of the escaped-without-crossing class. That is a note on what was searched, not a
claim of originality: FF calls them visualisation choices built on the work above, and the check can be
redone with a fuller literature search.

**Status:** S1–S5 shipped (#1440–#1444); epic #1439 complete.

---

### 2.6 Gravitational & relativistic scattering — **#1922 epic + #1942–#1948 filed**

Light rays or test particles moving through a gravitational field, launched from a grid of initial
conditions and coloured by their fate (captured by body *i*, escaped through channel *k*, still bound).
Where a small change in launch condition flips the fate, the boundaries are fractal, often Wada. This is
the same per-pixel *launch → trace → colour by exit* pattern as the chaotic billiard (#626) and the Wada
mirror spheres (§2.5), with one engineering difference: trajectories bend continuously, so each pixel
integrates an ODE instead of stepping exact reflections.

**Shared core (decided 2026-11-19).** Black hole shadows S1 (#1923) builds the integrator as shared
infrastructure: adaptive Runge–Kutta, conserved-quantity bookkeeping, exit rules ("captured by *i*",
"escaped", "undecided at max time"), per-ray channels (time to exit, winding, closest approach) and a
low-res drag preview. Every ODE entry below then adds only equations of motion and exit tests.
Microlensing (§2.6.8) is algebraic and stands apart.

**Toolchain reach (shared).** *DSL*: none of these is an `f(z, c)` iteration, so plain User Equation
can't reach them. A **"User Potential"** mode could reach the Newtonian ones (§2.6.2, §2.6.3): the user
writes `V(x, y)`, FF derives forces by symbolic differentiation on the AST from the deep-zoom epic
(#1253), and the shared integrator runs it. Relativistic metrics need a "User Metric" (Christoffel
symbols from the AST); much heavier, and not proposed yet. *ColorGen / Theme*: fates are categorical, so
they go through `IBilliardColorMap` (gate = fate) and every billiard / Categorical theme works unchanged;
the channels feed `SmoothBuffer` and `IHeightFieldSource`.

**Validation pattern.** Every entry checks conserved quantities from stored trajectory states (not from
the integrator's own error estimate), an analytic limit (zero perturbation → integrable or closed form),
and an exact symmetry that permutes fate labels.

#### 2.6.1 Binary black hole shadows — **#1922 (S1–S6 #1923–#1928)**

Two extremally charged black holes in static balance (the Majumdar–Papapetrou spacetime
[(Majumdar 1947)](../Resources-Bibliography.md#majumdar-1947) [(Papapetrou 1947)](../Resources-Bibliography.md#papapetrou-1947)).
Null geodesics from a pinhole camera fall into one hole or the other, or escape; the shadow edges hold
self-similar "eyebrow" copies of the pair, with Cantor-set structure [(Shipley & Dolan 2016)](../Resources-Bibliography.md#shipley-dolan-2016).
Channels: winding, capture time, closest approach, deflection; lensed sky for escaped rays; GPU kernel;
deep zoom limited by integration error (not perturbation; separate from #1253).
Validation: null Hamiltonian and energy conserved; one hole = extremal Reissner–Nordström with an
analytic shadow radius; equal-mass mirror symmetry; forward-back reversibility.

**S1 shipped (#1923).** `FractalType.BlackHoleShadow` + `BlackHoleShadowCalculator` on the shared
core `Engine/Scattering/ScatteringIntegrator.cs` (Dormand–Prince 5(4) [(Dormand & Prince 1980)](../Resources-Bibliography.md#dormand-prince-1980),
generic over an `IScatteringSystem` struct) and `MajumdarPapapetrou.cs` (null system, layouts, camera).
The camera is Shipley & Dolan's distant one: parallel rays from a plane at distance D, so the view is
the impact-parameter plane. Layouts Binary / Single / Triangle / Square / Custom (≤ 4 holes). Fate →
categorical colour (`IBilliardColorMap`, else Okabe-Ito per hole); escaped rays coloured by the
coordinate-time delay ∫(U² − 1)dλ, which also feeds `SmoothBuffer`. Validation added from
[(Yumoto, Nitta, Chiba & Sugiyama 2012)](../Resources-Bibliography.md#yumoto-2012): with ℓ = 20, κ = 1 the
traced main-shadow edges (1.763, −5.893, half-width 4.182) sit within 0.025 of their first-order Eq. 40
(1.788, −5.872, 4.195), against ~0.2 for the zeroth-order circle. Single-hole edge at L/E = 4m to 2e-4.
The pinhole camera named in the issue was left out: the paper's parallel congruence gives the
impact-parameter plane directly, and that is what the validation formulas use.

**S2 shipped (#1924).** Per-ray channels recorded during the trace (`MpNullSystem.Begin` /
`Observe`): winding (angle swept round the capturing hole, else the hole passed closest), coordinate-
time delay, integrator steps, closest approach (refined inside each step on the cubic Hermite segment
when the radial velocity changes sign) and deflection. `BlackHoleChannel` picks one for the palette and
the relief height; `BlackHoleShadeFates` keeps the fate hue with the channel as brightness stripes (a
billiard theme gets the half-turns as its bounce count). Validated against an independent quadrature of
the extreme Reissner–Nordström orbit equation [(Yumoto, Nitta, Chiba & Sugiyama 2012, Eqs. 3–4)](../Resources-Bibliography.md#yumoto-2012):
swept angle, deflection and periastron of single-hole rays; a far-away binary deflects like its total
mass. Fix: S1 passed the palette value as `Map`'s iteration cap, so escape-time palettes drew every
escaped ray in the interior colour (the black sky of the S1 batch renders).

**S3 shipped (#1925).** Escaped rays can look up a sky along their final direction
(`Engine/Scattering/BlackHoleSkies.cs`): `Environment` is the Lighting & FX sky, sampled exactly as the
Wada mirror spheres' photo mode does (HDRI by name or .hdr / .exr path, else the background gradient),
so a user image needs no new loader and the batch carries it through `--lighting-preset`; a missing HDRI
file is reported as a batch fidelity gap. `Checker`, `Grid` and `Stars` are analytic and seeded (star
cells hashed with the SplitMix64 finaliser [(Steele, Lea & Flood 2014)](../Resources-Bibliography.md#splitmix64)).
A `Pinhole` projection was added: the parallel congruence sends every unlensed ray the same way, so
only a pinhole shows a sky with parallax and lensing (Einstein rings squeeze the checker towards each
shadow). Validation: with every mass zero the pinhole image equals the straight projection of the sky,
computed from the camera geometry alone; the parallel image is one colour; the pinhole shadow edge sits
where the launch ray's L/E = 4m. Not done: anti-aliasing of the lensed sky near the shadow edges (the
issue's optional ray-differential footprint) — the checker aliases into moiré there (#1969).

**CPU speed (#1970).** Profiled (Release, 12 threads): the default view traced in about 1 s at 480x270
(about 17 s at 1080p), about 230 integrator steps per ray at about 225 ns each; each step evaluates the
field seven times, so a pixel costs about 100–200x a Mandelbrot pixel. Measured fixes, each checked
against a 1e-11 reference with no fate pixel changed: tolerance default 1e-7, tightened by the cube root
of the zoom past the default (a fixed 3e-8 already changed nothing at eyebrow zooms x6–x600); capture
radius 0.25 m (inside the single-hole photon sphere); no channel bookkeeping in Fate mode; opt-in solid
blocks (a 16 px block whose ring is one hole's shadow is filled). Speed-ups: 2.8x at the default view
(3.3x with solid blocks), 1.9–2.3x at eyebrow zooms (3.6–8.5x with solid blocks, where shadows fill the
frame). The escape radius barely matters (outbound steps are long). The GPU (#1926) remains the larger
lever; on fp64-weak cards an fp32 kernel with measured routing is the plan.

**S4 shipped (#1926).** ILGPU trace (CUDA / OpenCL, the mirror spheres' path; a D3D11 + Vulkan twin
is #1972): `BlackHoleShadowGpuCalculator` mirrors the CPU integrator term for term and returns fate,
steps, delay and final momentum; the CPU colour pass is unchanged, so themes, skies and the Fate /
Delay / Steps / Deflection channels work on either path (Winding / ClosestApproach / shaded fates stay
on the CPU). On the ILGPU CPU accelerator the double kernel matches the CPU exactly (every pixel's
fate, steps, delay, momentum; 11 scene variants). Measured on the GT 710 against the 12-thread CPU:
double precision 55x slower (fp64 at 1/24 rate, divergent rays), so a generated single-precision
kernel (`BlackHoleGpuPrecision.Single`, tolerance floored at 2e-5) was added: 1.7–1.8x faster than
the CPU at the default view and 3.1x at an x375 eyebrow zoom, with 0.003–0.007 % / 0.16 % of fates
moved (edge pixels only). Auto measures per device and precision (the Buddhabrot #1220 pattern);
`FF_GPU_BLACK_HOLE` forces either path.

**Navigation + S5 part 1 (#1927).** Box zoom near the shadow edges landed in solid regions: the render
used the billiard's 4 / (width · zoom) pixel pitch while every input path goes through `ViewCamera`'s
3.5 / (max(w, h) · zoom), so targets landed at 3.5/4 of their distance from the centre. The black hole
calculator (CPU and GPU) now uses the camera's scale (default zoom 0.16 → 0.14 keeps the landscape
framing); ten other 2D families share the mismatch (#1974). Deep zoom: calibrated at three bisected
boundary points on the eyebrow chain (120x68, x10–x10^9 past the default, against a 1e-13 reference),
the loosest tolerance that moved no fate pixel tracks the pixel pitch, so the tolerance is now also
capped at 0.003 x pitch; the views match the reference exactly to x10^9. Double precision runs out
near x10^10–x10^11 (there the 1e-12 and 1e-13 references disagree); the status bar says so ("past
fp64: edges approximate"). Double-double integration for boundary pixels past that is part 2.

**S5 part 2 (#1927).** `MajumdarPapapetrouDD` traces in double-double (Hida, Li & Bailey's DD, ~31
digits; a DD square root by one Newton step from the double one, FF's own): state and field in DD, step control in
double, the same Dormand–Prince stages, exits and camera. Past double precision (Fate colouring, on by
default as `BlackHoleDdEdges`), the calculator keeps its double / GPU pass and retraces only the pixels
along fate edges in DD at 0.003 x pitch, following any edge the DD trace moves (rounds until no fate
changes). Validation: DD equals double where double is accurate; DD resolves the single-hole shadow edge
(L/E = 4m) to ±2e-16, below double's spacing near 4 (the double trace already misplaces b* + 1e-14);
at x10^11 and x10^12 all-DD frames at two DD tolerances agree, and the fix-up frame equals the all-DD
frame pixel for pixel while retracing ~25% of a 48x27 view. Cost is the price of depth: a DD ray at
1e-20 takes ~3-4e5 steps (DP5's tol^(-1/5)); a higher-order integrator would cut that (not done).

**S6 (#1928) — presets, animation, help, prior art.** Six built-in regions: *Equal-Mass Pair* (the setup
of Shipley & Dolan's side-on figure: unit masses, a = 2, r_max = 50 M, θ = 90°), *Eyebrow Hierarchy*
(the neck above the gap at a = 1), *Unequal Masses*, *Triangle of Three*, *Inclined Camera* and *Lensed
Starfield* (pinhole, seeded stars). Three bound animations: closing pair (separation 0.5–3), camera swing
(inclination 20°–160°) and turning sky; camera distance became animatable. Batch video applies the
animation per frame (`--animation`); the tests check each animated frame equals the still rendered with
that frame's value through `--param`, and each preset renders the same live, via `--param` and through
the poster path. Help page *Black Hole Shadows* (Math → Scattering). Prior-art search: the closest work
is [Trettel 2025](../Resources-Bibliography.md#trettel-2025) (real-time multi-hole Majumdar–Papapetrou
rendering through a refractive-index analogy); the physics literature also has
[Alonso, Ruiz & Sánchez-Hernández 2007](../Resources-Bibliography.md#alonso-ruiz-sanchez-2007) and
[Kluitenberg, Roest & Seri 2022](../Resources-Bibliography.md#kluitenberg-roest-seri-2022). FF builds on
Shipley & Dolan and claims no new physics. Papapetrou 1947's title is now verified.

**Sky anti-aliasing (#1969).** One ray per pixel aliases the lensed sky: near a shadow edge a pixel's
neighbours end up many sky cells apart. Of the issue's two options FF took FF's adaptive AA (#1344 /
#1382): `BlackHoleShadowCalculator` implements `IAdaptiveAaCalculator`, the quality preset's 2x2 / 4x4
jittered samples re-trace only pixels that differ from a neighbour, and a masked frame traces only the
marked pixels on the CPU (the GPU route traces all; `MaskExact` is false with solid blocks or the DD edge
fix-up, which read neighbours, so those fall back to the full grid). The ray-differential footprint filter
was not built: it costs ~3x on every pixel, and next to the shadow the map from pixel to sky is too
non-linear for a linear footprint to hold. Measured (CPU, 640x360, pinhole D = 30, Release): stars, full 4x4
9.2 s vs adaptive 1.2 s (10 % of the pixel-samples, mean error 0.43 levels against full); checker, 9.0 s vs
3.6 s (28 %, 0.87). Tests: massless holes + pinhole (straight rays) against the box-filtered sky computed
from the camera alone (one sample 24.2 levels mean error, 4x4 3.4, adaptive 3.7); the lensed band against
an 8x8 supersample (60.1 to 9.5 full, 14.9 adaptive); masked frames are exact at marked pixels and
untouched elsewhere.

#### 2.6.2 Hénon–Heiles exit basins — **#1942**

The Hénon–Heiles potential `V = (x² + y²)/2 + x²y − y³/3` has three exit channels above `E = 1/6`; its
exit basins are fractal and Wada [(Aguirre, Vallejo & Sanjuán 2001)](../Resources-Bibliography.md#aguirre-vallejo-sanjuan-2001).
The cheapest entry (smooth polynomial potential) and the natural first "User Potential" preset.
Validation: energy conservation, C3v symmetry permutes exits, Wada window check (as §2.5), nothing escapes
below `E = 1/6`.

**Shipped (#1942).** `FractalType.HenonHeilesBasins` (`HenonHeilesCalculator`, physics in
`Engine/Scattering/HenonHeiles.cs` on the shared `ScatteringIntegrator`). Potential from
[Hénon & Heiles 1964](../Resources-Bibliography.md#henon-heiles-1964). Two launch spaces: Plane (pixel =
start position, speed from the energy, direction Tangential / Radial / FixedAngle) and Section (pixel =
(y, py) on x = 0, px ≥ 0). Exit = leaving the circle r = 2 (past the saddles at r = 1), labelled by the
nearest channel axis; undecided at the time limit. Channels: Exit (default), EscapeTime, TurningPoints,
SaddleDistance. Adaptive AA (mask always exact), drag preview, every setting in the region snapshot →
`--param`. Six presets ("Henon-Heiles - ...") and two bound animations (rising energy, turning launch).
Tests check the invariants above on the render path; "nothing escapes below 1/6" holds for launches
inside the well (r < 1): launches already down a channel past a saddle still roll away. The "User
Potential" DSL mode stays a follow-up.

#### 2.6.3 Restricted three-body basins — **#1943**

Planar circular restricted three-body problem in the rotating frame. Two views: escape / crash / bounded
fates, mapped for the equal-mass Copenhagen problem and the general case
[(Nagler 2004)](../Resources-Bibliography.md#nagler-2004) [(Nagler 2005)](../Resources-Bibliography.md#nagler-2005);
and Newton–Raphson basins of L1–L5 [(Zotos 2016)](../Resources-Bibliography.md#zotos-2016), which is
root-finder colouring FF already does. The NR view is in CalcGen / User Equation reach if the root-finder
path takes a real 2D system; recording that gap is part of #1943.
Validation: Jacobi constant conserved; Copenhagen reflection symmetries; L-points satisfy `∇Ω = 0` and
match an independent 1D collinear solve; no orbit enters the zero-velocity forbidden region.

**Shipped (#1943).** `FractalType.ThreeBodyBasins` (`ThreeBodyCalculator`, physics in
`Engine/Scattering/ThreeBody.cs`; the Fates view on the shared `ScatteringIntegrator`). Units: unit
separation, unit angular velocity, primaries 1 − μ at (−μ, 0) and μ at (1 − μ, 0); Ω without the constant
μ(1 − μ)/2, so C = 2Ω − v². Fates view: pixel = launch position, speed √(2Ω − C), direction Prograde /
Retrograde / Radial / FixedAngle about the centre of mass; fates crash 1 / crash 2 (within the crash radius)
/ escape (past the escape radius) / bounded at the time limit; the step is capped at 0.1 × the distance to
the nearer primary. Lagrange view: Newton–Raphson with the analytic Hessian; converged end points matched to
L1–L5 found independently (bisection on the axis, equilateral points). Adaptive AA (exact mask), drag
preview, every setting → `--param`. Six presets ("Three-Body - ..."), two bound animations (falling Jacobi,
mass ratio sweep).

Symmetry used by the tests: for μ = 1/2 the equations are invariant under the half turn (x, y, vx, vy) →
(−x, −y, −vx, −vy), which swaps the primaries and maps a prograde / retrograde / radial launch to the same
rule, so the fate map is point-symmetric with the crash labels swapped. The two mirror reflections need
time reversal and do not map forward fates to forward fates, so they are not tested on the map.

**Toolchain gap (recorded, not closed).** A User Equation can write one real 2D Newton step by hand through
`re(z)` / `im(z)` and an explicit Hessian (non-holomorphic, so interpreter only, no perturbation / DE), and
*Converge-if* stops it; but there is no built-in real-2D Newton / Jacobian helper and no "which root"
labelling for more than the final-z colouring, so the Lagrange view is a built-in calculator.

#### 2.6.4 Charged particles around a magnetised black hole — **#1944**

Schwarzschild (later Kerr) in an asymptotically uniform magnetic field, Wald's test-field solution
[(Wald 1974)](../Resources-Bibliography.md#wald-1974). Fates: captured by the horizon, escaped along the
field axis, trapped. The trapped motion changes from regular to chaotic with the parameters
[(Kopáček, Karas, Kovář & Stuchlík 2010)](../Resources-Bibliography.md#kopacek-2010); that paper uses
recurrence analysis, so the *fate-basin map* is FF's view of the system, and a prior-art search for
published basin images is part of #1944. Real GR but cheap (analytic metric and field).
Validation: H, E, L conserved; B = 0 → Schwarzschild (ISCO 6M); q = 0 decouples; equatorial mirror.

**Prior-art search result (#1944): the fate-basin map is published.** Bautista & Vega
[(2021)](../Resources-Bibliography.md#bautista-vega-2021) map capture / upstream / downstream basins for
particles kicked vertically from circular orbits in exactly this system and find them Wada; related escape
and chaotic-scattering work: [Al Zahrani 2014](../Resources-Bibliography.md#al-zahrani-2014),
[Stuchlík & Kološ](../Resources-Bibliography.md#stuchlik-kolos-2016). The earlier sentence calling the map
"FF's view" is superseded: FF reproduces their setup and adds an explorer plus a Meridional view.

**Shipped (#1944).** `FractalType.MagnetisedBlackHole` (`MagnetisedBlackHoleCalculator`, physics in
`Engine/Scattering/MagnetisedBlackHole.cs` on the shared `ScatteringIntegrator`). Units and notation of
Bautista & Vega: r_g = 2M = 1, b = qB r_g / 2m (q and B only as this product, so the issue's separate
"q = 0" and "B = 0" limits are one limit), U = f[1 + (ℓ/(ρ sinθ) − bρ sinθ)²]. Hamiltonian in
(ρ, θ, p_ρ, p_θ), H = −½, field term written through w = ℓ/(ρ sinθ) − bρ sinθ (finite on the axis for
ℓ = 0). Kick view: their launch over (ρ0, ℰ), branch ℓ±. Meridional view: (R, z) launches at fixed ℰ, ℓ and
direction. Captured at ρ < 1.01, escaped at |z| > escape height (default 100; theirs 1000), undecided at the
proper-time limit (default 1000). Six presets, two bound animations, every setting → `--param`.
Not done: Kerr (spin), the Poincaré-section density view.

#### 2.6.5 Spinning compact binaries — **#1945**

Post-Newtonian binaries with spin-orbit and spin-spin coupling; initial spin angles → outcome (merger
time, orbits before plunge). Levin reported chaos and fractal basin boundaries
[(Levin 2000)](../Resources-Bibliography.md#levin-2000). **Disputed:** published comments argue that with
radiation damping the boundaries are not truly fractal and the dynamics not formally chaotic
[(comments on Levin 2000)](../Resources-Bibliography.md#levin-2000-comments). FF renders honestly: damping
is a toggle, and the help text labels the structure "fractal-looking at finite resolution". Equations
taken from Levin 2000, not reconstructed.
Validation: conservative PN energy / angular momentum and spin magnitudes conserved; zero spin →
integrable (smooth map); damped circular merger time vs the quadrupole formula (source cited at
implementation).

**Shipped (#1945).** `FractalType.SpinningBinaryBasins` (`SpinningBinaryCalculator`, physics in
`Engine/Scattering/SpinningBinary.cs` on the shared `ScatteringIntegrator`). Levin's letter does not print
its equations; they come from [Kidder 1995](../Resources-Bibliography.md#kidder-1995) (Eqs. 3, 4a–4f, 6a–6b),
which the letter cites. Pixel = initial spin polar angles (θ1, θ2) from the initial orbital axis; start
x = r0 x̂, v = v0 ŷ; outcomes merged (r < merge radius, FF's setting: Levin gives none) / separated
(r > 1000) / still orbiting (50 orbits), or merger side. Radiation reaction is a switch. Presets reproduce
Levin's conservative and dissipative set-ups.

How the transcription was validated: each piece against Kidder's own conserved quantities. N + spin-spin
conserves E_N + E_SS to 1e-14 (exactly Hamiltonian); 1PN, 1PN + 2PN, spin-orbit and the full conservative
system conserve their energies and J only to the next order, so the tests require the drift ratio (with the
piece's term / without) to be small and to shrink with r. Probing showed Kidder's E_SO beats sign-flipped and
re-weighted variants by a growing margin, and the combined system's J residual falls like r^-1.8 (the 2.5PN
cross order Kidder's "to 2PN" statement excludes). Newtonian dynamics + Kidder's a_RR reproduces the
circular coalescence time of Peters (1964) to 0.1 % (r0 = 20, 40).

The dispute: [Hughes and Cornish](../Resources-Bibliography.md#levin-2000-comments) (PRL 85, 2000),
[Schnittman & Rasio 2001](../Resources-Bibliography.md#schnittman-rasio-2001),
[Cornish & Levin 2002](../Resources-Bibliography.md#cornish-levin-2002),
[Hartl & Buonanno 2005](../Resources-Bibliography.md#hartl-buonanno-2005). FF does not settle it; the help
says "fractal-looking at finite resolution".

#### 2.6.6 Mixmaster (Bianchi IX) universe — **#1946**

Anisotropic cosmology bouncing chaotically between Kasner epochs in a triangular potential. Cornish &
Levin used coordinate-independent fractal methods to show it is chaotic
[(Cornish & Levin 1997a)](../Resources-Bibliography.md#cornish-levin-1997)
[(Cornish & Levin 1997b)](../Resources-Bibliography.md#cornish-levin-farey-1997); a later paper reaches
the same conclusion while criticising that method ([*Mixmaster chaos*](../Resources-Bibliography.md#mixmaster-chaos-2000), authors
not yet verified). Two levels: the cheap discrete epoch map (a 1D real map, possibly User Equation
reach) and the full ODE on the shared core. The outcome definition is taken from the papers.
Validation: Hamiltonian constraint; triangular symmetry permutes corner labels; topological entropy of
the discrete map → ln 2, as the 1997 letter reports.

**Shipped (#1946).** `FractalType.MixmasterBasins` (`MixmasterCalculator`, physics in
`Engine/Scattering/Mixmaster.cs` on the shared `ScatteringIntegrator`). Equations, launches (Kasner (u, v),
their Fig. 16; anisotropy (β+, β−), their Fig. 19), exit (u > u_exit = 8) and outcome (axis collapsing most
quickly) from Cornish & Levin. Corrections to this section's earlier text: the 1997 letter's entropy is
2 ln 2 for the 2-D Farey map; ln 2 is the 1-D u-map's (PRD eq. 32, credited to Rugh), which is what the test
checks. The two papers print opposite constraint signs; FF uses the one the equations conserve (tested).

FF's own numerical choices (none stated in the parts of the papers read): run towards the singularity
(time-reversed for the papers' ω > 0); arc-length time dλ = |x′| dτ, because ω falls at every bounce and the
epochs lengthen geometrically, so τ would need astronomical values; constraint restored every 32 steps by the
smallest equal shift of the three rates (its tiny absolute error otherwise doubles relative to the kinetic
term at each bounce and acts like spurious matter); relative-only error control; "in a Kasner epoch" when
every |(ln x)″| < 10⁻³ Σ(ln x)′². Arc-length limit 1e30 leaves no undecided pixel in a 200-sample probe
(~1,400 steps per pixel). Discrete-map view not shipped as an image (the 2-D Farey map's u does not depend on
v, so a (u, v) picture is stripes); the u-map is used by the tests.

#### 2.6.7 Black hole + ring (Weyl superposition) — **#1947**

Schwarzschild plus a thin ring or annular disc, an exact static axisymmetric superposition; the matter
breaks integrability [(Semerák & Suková 2010)](../Resources-Bibliography.md#semerak-sukova-2010)
[(Semerák & Suková 2012)](../Resources-Bibliography.md#semerak-sukova-2012). Primary view is a Poincaré
section accumulated as a density render (Buddhabrot path, #1218), secondary is fate basins. Heaviest
metric of the ODE set (Bach–Weyl ring needs elliptic integrals and a line-integrated λ); measure early.
Validation: E, L, mass shell conserved; ring mass 0 → Schwarzschild, whose section has only closed
curves; equatorial mirror.

#### 2.6.8 Microlensing magnification maps — **#1948** (not fractal)

Many point lenses in the thin-lens approximation plus smooth convergence and shear; inverse ray shooting
[(Kayser, Refsdal & Stabell 1986)](../Resources-Bibliography.md#kayser-refsdal-stabell-1986) gives
networks of bright caustics. **Not a fractal in the strict sense**: caustics are piecewise-smooth curves
with cusps, no self-similarity. Included because it fits FF's accumulation renderer (Buddhabrot / Billiard
Rays) and the lensing theme of §2.6.1. Algebraic map, so independent of the shared ODE core.
Validation: single-lens analytic magnification curve; mean magnification vs `1/((1−κ)² − γ²)` (source
cited at implementation); ray conservation; seed determinism.

#### 2.6.9 Future R&D (not filed)

Both need numerical spacetime data FF does not have, so they wait until the analytic entries above have
shipped and the shared core has been measured.

- **Kerr black holes with scalar hair; boson stars.** Backwards ray tracing shows shadows unlike Kerr's,
  with novel shapes, multiple shadows of one hole and chaotic lensing, tied to the presence of several
  light rings [(Cunha, Herdeiro, Radu & Rúnarsson 2015)](../Resources-Bibliography.md#cunha-2015)
  [(Cunha et al. 2016)](../Resources-Bibliography.md#cunha-chaotic-lensing-2016). The metrics exist only
  as numerical solutions, so FF would have to ship or import metric grids and interpolate them (with
  derivatives) inside the integrator. Open questions: data source and licence, grid format, interpolation
  error vs fractal-boundary error.
- **Dynamic binary-merger lensing.** Strong-field lensing through a fully time-dependent binary spacetime;
  zooming in reveals more self-similar "pancake" shadows of the holes
  [(Bohn et al. 2015)](../Resources-Bibliography.md#bohn-2015). Needs numerical-relativity simulation
  output and 4D (time-varying) metric interpolation along each ray. Far future; the static
  Majumdar–Papapetrou case (§2.6.1) is FF's tractable stand-in.

**Sources:** §7 (gravitational & relativistic scattering block).
**Status:** filed 2026-11-19; nothing implemented. #1923 first (shared core), then any order.

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

**Status:** **SHIPPED** — `FractalType.Coquaternion` (#853). GPU kernel shipped 2026-10-06
(`CoquaternionGpuCalculator`, #1173-G); metric-aware DE + split-complex-square-confirmation are follow-ups.

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

### 3.6 Dual-orbit escape-geometry map (parameter→escape-space scattering)

> **#1130 novelty correction (2026-10-05):** the two orbits are two points of the **Julibrot** (z₀, c)
> space sharing their parameter (z₀ = 0 and z₀ = c); the c-orbit alone is a perturbed-z₀ Mandelbrot
> set, the pair is the bicomplex quadratic (Rochon 2000), and the 3D volume is a Julibrot slice
> (Fractint's julibrot type). Not an original *set*; FF's contribution is the *relational colouring*.
> See `DualOrbit-Coloring-RnD.md` §8.

**Math.** Construction from session notes 2026-09-17 (see the correction above) — treat
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

**Research context (what this relates to).** The construction is a *combination* in known territory
— a two-point reading of the Julibrot space (#1130) — and should be described that way (Rule B):
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
- **Sweet, Ott & Yorke 1999** — D. Sweet, E. Ott, J. A. Yorke. *Topology in chaotic scattering.*
  Nature 399, 315–316. The four-mirror-ball Wada experiment (§2.5).
- **Yoneyama 1917** — Kunizô Yoneyama. *Theory of continuous set of points (not finished).* Tohoku
  Mathematical Journal 12. The Lakes of Wada (§2.5).
- **Bourke 1998** — Paul Bourke. *Wada basins: rendering chaotic scattering.* Prior-art renders (§2.5).
- **Schlick 1994** — Christophe Schlick. *An inexpensive BRDF model for physically-based rendering.*
  Computer Graphics Forum 13(3). Fresnel term of the photo mode (§2.5).

- **Gravitational & relativistic scattering (§2.6)** — Shipley & Dolan 2016; Majumdar 1947; Papapetrou
  1947 (title unverified); Aguirre, Vallejo & Sanjuán 2001; Nagler 2004, 2005; Zotos 2016; Wald 1974;
  Kopáček, Karas, Kovář & Stuchlík 2010; Levin 2000 + comments; Cornish & Levin 1997 (two papers);
  *Mixmaster chaos* 2000; Semerák & Suková 2010, 2012; Kayser, Refsdal & Stabell 1986; Cunha et al.
  2015, 2016; Bohn et al. 2015; #1923 adds Yumoto, Nitta, Chiba & Sugiyama 2012, Nitta, Chiba & Sugiyama
  2011, Dormand & Prince 1980 and Hairer, Nørsett & Wanner 1993. Full entries under *Gravitational & relativistic scattering* in the
  bibliography; unverified details listed under *Citations awaiting verification*.

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
- **2026-09-26** — **S9 (#972) shipped** — new 3D type `FractalType.DualOrbitVolume`
  (`DualOrbitVolumeCalculator`, Coquaternion CPU-raymarcher clone). The user's original experiment run
  for every c-seed: world X = c.x, Z = c.y, Y (up) = s.x − `DualOrbitVolumeSXCenter`, s.y fixed
  (`DualOrbitVolumeSY`, animatable — the 4th coordinate of the (z0, s) space). Every horizontal layer is
  the filled Julia set K_s; the c = 0 column is the Mandelbrot line. Analytic DE from a = ∂u/∂c and
  b = ∂u/∂s: `DE = ½|u|ln|u| / sqrt(|a|² + (Re(ū·b)/|u|)²)`, CSG-intersected with the slab
  |Y| ≤ `DualOrbitVolumeHalfHeight` (beyond the Mandelbrot line the layers are Cantor dust to infinity;
  the caps show a Julia cross-section); 0.85 step factor (thin sheets). Colour sources: ExternalAngle
  (default; Julia ray bands), CriticalLayer (the seed-0 orbit per layer — bands mark the sweep crossing
  the Mandelbrot line), Steps. Mesh export via `RaymarchMeshSampler` (same DE as the render). Tests:
  DE conservative against brute-force membership (no set point within ½·DE), (X,Z) point symmetry,
  c = 0 column = Mandelbrot line, interior = 0, slab clip, render determinism, registration + region
  round-trip. Shape: from a fat cauliflower at s = ¼ down to the flat segment [−2, 2] at s = −2, a
  twisting fibrous blade.
- **2026-09-26** — **#939 per-orbit colouring: feasibility HIGH** (analysis posted on #939; slices
  A #978 / B #979 / C #980 / D #981). The "two discs" are M and M_c ⊂ M (for s ∉ M the Julia set is
  measure-zero dust, so a fixed seed is bounded only for s ∈ M — verified) plus two exterior level-set
  families centred on 0 (z-orbit) and −c² (c-orbit). **A (#978) shipped**: the dual-orbit field now
  honours the theme `InSetColor`, the global `InteriorAlpha` (#96/#97, inline `ScaleArgbAlpha`) and the
  #615 `OutOfBoundsColor` (surround = the field's orbit(s) escape by step 1 — seed-agnostic). Interior is
  keyed on the orbits' escape flags, not on `smooth == 0` (legacy fields can be a legitimate 0, e.g. the
  c = s control). Interior-alpha controls now shown for Dual-Orbit Escape.
- **2026-09-26** — **#939-B (#979) shipped** — `DualOrbitColorMode.PerOrbitLayers`: each orbit is its own
  layer (theme by name `DualOrbitThemeZ/C` or injected `LayerThemeZ/C`; that theme's `InSetColor` ×
  `InteriorAlpha` and #615 surround per layer, escape index ≤ 1), blended c-over-z with straight-alpha W3C
  compositing (`DualOrbitLayerBlend` COverZ / ZOverC / Screen / Multiply / Mix, per-layer opacity).
  SmoothBuffer = z layer. Unknown theme names fall back to the main theme and are reported
  (`UnresolvedLayerThemes`). Tests: blend maths vs the formulas; one visible layer reproduces Field-mode
  EscapeTimeZ / EscapeTimeC byte-for-byte (incl. surround at bailout 2); exact colours in the three region
  states. Smoke: the c layer alone shows the c-orbit's own escape structure (M_c + bands offset from the
  z family) — the "second disc". Params persistence / animation / UI = C (#980).
- **2026-09-26** — **#939-C (#980) shipped** — per-orbit layers reachable from the UI: Colour mode combo,
  z / c theme pickers (host `EnumerateThemeNames(ByFractalCompat, DualOrbitEscape)`, lazily fetched,
  "(main theme)" ↔ "", "— kind —" header rows ignored), blend combo, opacity fields (animatable — cross-fade
  the two discs), yellow warning when a stored theme is missing / incompatible. Region persistence at all
  three sites (defaults omitted). Colour-blind-safe defaults verified by hue: z = "MS Standard Blue
  (Gradient)" (blue at low smooth), c = "MS Standard Sunrise (Gradient)" (amber, bright where the far field
  lands). Field combo disabled in layer mode.
- **2026-09-26** — **#939-D (#981) shipped — #939 complete.** `DualOrbitEscapeCalculator` renders in two
  phases: `Iterate()` caches per-pixel z / c smooth counts + escaped / escaped-by-step-1 flags (+
  SmoothBuffer); `Colorize()` builds ColorBuffer from the cache with the current theme(s), interior alpha,
  blend and opacities. `Calculate()` skips `Iterate()` when a `GeometryKey` of every orbit-affecting input is
  unchanged, so colour-only param edits (which arrive as a full Trigger) recolour in place;
  `ISupportsCheapRecolor.Recolor()` = `Colorize()` (main-theme change). Release timings: 1280×720 @ 500 iter
  full 158–173 ms vs recolour 4–10 ms; 1920×1080 @ 1000 iter 641–689 ms vs 3–9 ms. Cache = 9 bytes/px
  (~19 MB at 1080p). A reflection guard classifies every `DualOrbit*` param as geometry or colour-only and
  checks reuse against a fresh render, so a new param cannot silently serve stale orbits.
- **2026-10-05** — **Dual-orbit colouring R&D → epic #1114** ([DualOrbit-Coloring-RnD.md](DualOrbit-Coloring-RnD.md)).
  Verified (independent invariants): the dual-orbit pair is exactly one orbit of the **bicomplex** quadratic
  map (split-complex form m' = m² + e² + s, e' = 2me; Rochon 2000), and D_{n+1} = D_n·(z_n + c_n). New
  pair-native fields (secant Lyapunov — live in every region; = log|μ|/p at phase lag 0, = 0 at lag ≠ 0),
  Böttcher-ratio domain colouring (real part = G_c − G_z, not GreenRatio), interior phase lag, co-moving
  traps, bivariate/copula palettes, path interference, basin entropy, FTLE/LIC, and a **Dual Buddhabrot**
  (joint-outcome channels; the c-escaped / z-bounded channel is new structure). Slices #1115–#1129;
  novelty research deferred to #1130. Supersedes #867's deposition scope.
- **2026-10-05** — **Renamed (#1154 / #1155):** `FractalType.DualOrbitEscape` → **`JulibrotPair`**
  ("Julibrot Pair") and `FractalType.DualOrbitVolume` → **`Julibrot`**, after the #1130 novelty research
  found the volume is a classic Julibrot 3D slice and the 2D type reads two Julibrot points. Same enum
  values; old names still parse (`FractalTypeNames`, `FractalTypeJsonConverter`); calculator classes and
  `DualOrbit*` parameter keys unchanged. Entries above keep the names of their time.
- **2026-10-27** — Added §2.5 Wada mirror spheres (#1439): the 3D chaotic-scattering sibling of the
  billiard, from Sweet, Ott & Yorke 1999. S1 #1440 (CPU basin mode, PR #1495) and S2 #1441 (photo
  mode, PR #1496 / #1497) shipped; S3 #1442 adds this section, the help page and colouring-reuse tests.
  Bibliography (§7): Sweet–Ott–Yorke 1999, Yoneyama 1917, Bourke 1998, Schlick 1994.
- **2026-10-27** — §2.5: S4 #1443, the mirror-spheres GPU trace (ILGPU, fp64, outcome readback). Parity is
  pixel-identical on the GT 710, but the CPU is faster there, so the GPU/CPU choice is measured (Auto).
- **2026-10-27** — §2.5: S5 #1444, the 1999 photograph as a built-in region (`VertexUp` orientation, plain
  room colour); epic #1439 complete.
- **2026-10-27** — §2.5: the 2D billiard ray-path inset #1447 (hover / pin a pixel to see its ray; live only).
- **2026-10-27** — §2.5: NDisk places every requested disk (#1503); Ring overlap warning.
- **2026-10-27** — §2.5: `Custom` billiard geometry edited in the ray-path inset (#1504).
- **2026-10-27** — §2.5: Billiard Rays S1 #1506, the top-down ray-density render (tracker #1505).
- **2026-10-27** — §2.5: Billiard Rays S2 #1507, gate-coloured density and per-ray attribute colouring.
- **2026-10-27** — §2.5: Billiard Rays S3 #1508, ray-ray crossings (delta N, angle, path lengths; heightfield).
- **2026-10-28** — §2.5: Billiard Rays S4 #1509, sweep-neighbour divergence (real-space basin boundary).
- **2026-10-28** — §2.5: Billiard Rays S5 #1510, the escaped / crossing / trapped classification.
- **2026-10-28** — §2.5: Billiard Rays S6 #1511, sweep progress, progressive batches, drag preview, cost (GPU deferred).
- **2026-10-28** — §2.5: Billiard Rays S7 #1512, presets, user guide, prior-art check (epic #1505 complete).
- **2026-10-29** — §2.5: `Polygon` billiard geometry (#1538, corners then evenly along the edges; one disk per corner = the ring); 10-decimal angle fields (#1540).
- **2026-10-29** — §2.5: `SquareLattice` / `HexLattice` billiard geometries (#1539, n sites nearest the origin, the periodic Lorentz gas of Sinai 1970 / Bunimovich & Sinai 1981) and the filled `Polygon` (grid at the edge spacing); disk cap 64 → 256.
- **2026-11-19** — Added §2.6 gravitational & relativistic scattering: black hole shadows (#1922,
  S1–S6 #1923–#1928; S1 builds the shared ODE / geodesic core), Hénon–Heiles #1942, restricted
  three-body #1943, magnetised black hole #1944, spinning binaries #1945 (fractality disputed), Mixmaster
  #1946, black hole + ring #1947, microlensing #1948 (not fractal); hairy black holes / boson stars and
  dynamic merger lensing recorded as future R&D (§2.6.9). Bibliography (§7): gravitational block.
- **2026-11-20** — §2.6.1: black hole shadows S1 #1923 (type, shared ODE core, MP trace, Yumoto et al. Eq. 40 check). Bibliography (§7): Yumoto et al. 2012, Nitta, Chiba & Sugiyama 2011, Dormand & Prince 1980, Hairer, Nørsett & Wanner 1993.
- **2026-11-20** — §2.6.1: black hole shadows S2 #1924 (winding, delay, steps, closest approach, deflection channels; shaded fates; palette cap fix).
- **2026-11-20** — §2.6.1: black hole shadows S3 #1925 (Lighting & FX / procedural skies for escaped rays, pinhole projection, HDRI gap).
- **2026-11-20** — §2.6.1: black hole shadows CPU speed #1970 (tolerance 1e-7 + cube-root zoom tie, capture 0.25 m, Fate-mode bookkeeping skip, opt-in solid blocks; 1.9–8.5x, no fate change vs a 1e-11 reference).
- **2026-11-20** — §2.6.1: black hole shadows S4 #1926 (ILGPU double + single kernels, measured Auto routing; D3D11 + Vulkan follow-up #1972).
- **2026-11-21** — §2.6.1: black hole box-zoom fix (camera pixel scale) + S5 part 1 #1927 (pitch-calibrated tolerance exact to x10^9; fp64 limit flagged); cross-family pitch issue #1974.
- **2026-11-21** — §2.6.1: black hole shadows S5 part 2 #1927 (double-double edge fix-up past fp64; exact against converged all-DD frames at x10^11-10^12).
- **2026-11-21** — §2.6.1: black hole shadows S6 #1928 (presets, bound animations, help page, prior art; Papapetrou 1947 title verified). Epic #1922 complete.
- **2026-11-21** — §2.6.1: black hole sky anti-aliasing #1969 (adaptive AA via IAdaptiveAaCalculator; 7.5x / 2.5x cheaper than full 4x4 on stars / checker).
