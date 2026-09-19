# Kleinian 3D Generalization — Design Plan

**Tracking issue:** [#855](https://github.com/AloneButUnsober/FracturingFog/issues/855)
(part of frontier epic [#850](https://github.com/AloneButUnsober/FracturingFog/issues/850)).
**Status:** design doc — *no code lands until this doc is reviewed and the sliced
sub-issues are filed.* Do **not** extend `KleinianCalculator` ad hoc (per epic gate).

**Related docs:**
[Theoretical-Fractal-RnD.md](Theoretical-Fractal-RnD.md) §5.4 (the epic gate),
§2.3 (2D Indra's Pearls, a distinct candidate);
[Fractal-Expansion-Roadmap.md](../Fractal-Expansion-Roadmap.md) §B.4 + per-family
deferred follow-ups (the four generalization axes this doc plans);
[Resources-Bibliography.md](../Resources-Bibliography.md) (citations, Rule B).

---

## 1. Purpose & scope

The shipped renderer draws **one fixed group**. This doc plans the path to a
**general Schottky/Kleinian renderer** where the *group is data*: the user (or a
preset) supplies the generating transforms, and the renderer draws the limit set
of whatever group they generate.

**In scope** (the four axes logged in Roadmap §B.4):

1. **User-editable generator list** — inversion spheres (centre + radius) and,
   later, general Möbius/rotation generators, instead of the hard-coded
   tetrahedral 4-sphere array.
2. **Alternate Schottky configurations / presets** — Indra's-Pearls necklace and
   limit-circle groups, a 6-sphere "Klein-bottle"/octahedral packing, an
   Apollonian-extrusion stack.
3. **Full Möbius-group composition** — arbitrary inversion **+ rotation/Möbius**
   sequences, drawing the limit set of the *generated group*, not the
   inversion-only descent the current DE runs.
4. **True analytic DE** — replace the inversion-scale scalar heuristic with the
   full Jacobian-norm distance estimate for crisper cusps under deep zoom.

**Out of scope (explicitly):**

- The **2D Indra's-Pearls** limit-set renderer (RnD §2.3) — a Möbius-on-ℂ
  word-enumeration plotter, a *different* pipeline (point deposition, not
  DE sphere-tracing). Cross-referenced here because the group algebra and the
  colour-by-word-length idea are shared, but it is its own candidate/issue.
- **Parabolic implosion** (RnD §5) — unrelated flagship.
- Any GPU work beyond parity with the CPU generalization (the current GPU kernel
  hard-codes the 4-sphere preset; see §6.4 for the GPU slice ordering).

---

## 2. What ships today (read the code, not memory — RnD Rule)

`Engine/Calculators/KleinianCalculator.cs` + `Engine/Calculators/Gpu/KleinianGpuCalculator.cs`.
`FractalType.Kleinian` (Abstractions/Models/Enums.cs), 3D camera family
(Raymarch3D motion class). Registered like the other 3D raymarchers.

**The group is hard-coded** (`KleinianCalculator.Calculate`):

```
r  = √2 · s                              // s = KleinianSphereScale
c0..c3 = s·(±1, ±1, ±1)  (even parity)   // tetrahedral 4-sphere centres
```

Four inversion spheres at the even-parity cube corners; at `s = 1` they are
mutually tangent and overlap at the origin. This is a **classical Schottky
group** on four spheres — but exactly four, exactly this arrangement.

**The DE is inversion-only + a scalar heuristic** (`KleinianDE`):

```
scale := 1
for i in 0..iter:
    k := argmin_k  (|p − c_k| − r)         // deepest containing sphere
    if none contains p: break              // escaped the fundamental domain
    f := r² / |p − c_k|² ;  scale *= f      // sphere inversion + derivative scalar
    p := c_k + (p − c_k)·f
DE := (nearest signed sphere distance) / scale
```

The `scale *= r²/|p−c|²` factor is the *magnitude* of the sphere-inversion
Jacobian; the orthogonal Householder factor (unit magnitude) is dropped. This is
the standard **Knighty / Fragmentarium "pseudo-Kleinian" DE** — a good heuristic,
not the exact distance to the limit set.

**Params today** (`FractalParameters`, all `Kleinian*`): `Iterations` (16),
`SphereScale` (1.0), `MaxSteps` (160), `Epsilon` (0.0012), `CameraDistance/Theta/Phi`,
`LightTheta/Phi`. **There is no generator/group parameter at all.**

**Already good, reuse verbatim:**

- The **DE→sphere-trace→shade** pipeline (`ShadingPipeline.Shade<De>`), SSAO,
  tonemap/bloom, thin-lens DoF, stereo eye-offset, debug HUD — all group-agnostic;
  they consume a `De` struct and a normal. Generalization only changes what
  `De.Evaluate` computes.
- The **orbit-trap colour driver** (`De.OrbitTrap` / `KleinianTrap`) — "closest the
  inversion orbit passes to a sphere boundary." Generalizes directly to "closest
  to any generator's isometric sphere."
- The **GPU kernel contract** (`KleinianGpuParams` already passes 4 centres + radius
  as data) — the shape is right; it just needs the count/config to become variable.

**Design consequence:** generalization is *almost entirely* a change to the DE's
group descriptor and the loop that consumes it. The render/shade/colour stack is
reusable. This is what makes the epic tractable in slices.

---

## 3. Math foundations (Rule B — cite everything)

### 3.1 Schottky groups and inversion

A **Schottky group** is generated by inversions in a set of disjoint (or tangent)
spheres. Inversion in a sphere of centre **c**, radius **r**:

```
ι(p) = c + r²·(p − c) / |p − c|²
```

is a conformal involution swapping interior and exterior. Composing an *even*
number of inversions yields an orientation-preserving **Möbius transformation of
the sphere at infinity** — the group acts on Ŝ³ = ℝ³ ∪ {∞} as a discrete group of
conformal maps (a **Kleinian group** under the Poincaré extension to hyperbolic
space ℍ⁴; Maskit 1988, Mumford–Series–Wright 2002).

### 3.2 Limit set

The **limit set** Λ is the set of accumulation points of any orbit under the
group — equivalently the closure of the fixed points of the group's loxodromic
elements. For a Schottky group it is a Cantor-like fractal on the sphere
boundary; for tangent-sphere ("kissing") groups it fills into curves/surfaces —
the **Apollonian gasket** in 2D, its 3D analogues here. Λ is what we render.

The current renderer approximates Λ by the *sphere-inversion IFS attractor*: the
"invert through the deepest containing sphere, repeat" descent has Λ as its
attractor when the spheres are the group's **isometric spheres**. This is correct
for pure inversion generators; it is **incomplete once rotations/Möbius factors
enter** (§3.4).

### 3.3 The generalization data model — a **generator**, not a sphere

The key abstraction: a Kleinian group is generated by a list of **generators**,
each an orientation-preserving Möbius map of Ŝ³ *or* an orientation-reversing
inversion. In 3D the clean, closed, numerically-stable representation is:

- **Inversion generator:** `(centre c, radius r)` → the map ι above. (What ships,
  ×4.)
- **Sphere-pair (Schottky) generator:** a map pairing the *outside* of one sphere
  to the *inside* of another — `g = ι_B ∘ R ∘ ι_A` where `R` is an optional
  rotation. This is the honest "Schottky generator" and includes an inversion pair
  plus a twist; it is what necklace/limit-circle groups need.
- **Möbius/rotation factor:** a pure rotation `R ∈ SO(3)` about an axis, or a
  translation, composed into a generator.

All three unify as **elements of the conformal group** `Möb(Ŝ³) ≅ SO⁺(4,1)`.
The implementation representation (§4.2) picks a concrete encoding.

### 3.4 Descent vs. group-composition rendering — the real fork

Two families of algorithm, and the design must pick per-generator-kind:

- **IFS-descent DE (what ships).** Works when every generator is a pure
  inversion: "map p back through the deepest sphere until it escapes the
  fundamental domain, track the derivative." O(iter) per DE eval, cheap, the DE is
  a natural by-product. **Fails for rotation/Möbius generators** — there is no
  single "deepest containing sphere" to invert through; the fundamental domain
  descent is not a simple nearest-sphere pick.
- **Möbius-word DE (general).** The general limit-set DE walks the group's
  fundamental-domain tiling: at each step, apply the generator (or its inverse)
  that moves p toward the fundamental domain, accumulating the product of
  generator Jacobians. This is the "**Kleinian group KIFS**" formulation
  (Knighty, fractalforums; generalizes the pseudo-Kleinian DE to arbitrary
  generator sets). It subsumes the descent path (inversions are a special case)
  and is the target for axis 3.

**Design decision (see §5):** ship the general Möbius-word DE as the engine, with
the pure-inversion descent recovered as the fast path when all generators are
inversions (preserving today's output byte-for-byte at the tetrahedral preset).

**Finding (2026-09-19, revises the above for the 3-D path).** The general
Möbius-word DE is worth building *for 3-D conformal generators that keep a solid
limit set* — inversions plus **rotations/translations** (the rotation fold shipped
in S4/#877 is the first, tractable instance). But the **complex-Möbius**
2-generator families (Grandma's recipe, Maskit slice — §3.6 preset row) act on the
Riemann sphere Ĉ; their limit set is a fractal **curve** (Hausdorff dim < 2) on a
2-sphere, *not* a 3-D solid. A 3-D distance-estimator that sphere-traces a solid
cannot render a 1-D curve-on-a-sphere without a different construction (tube/curve
DE) — it is a category mismatch. Those families belong to the **2-D Indra's-Pearls
point-plotter** (§2.3 of the R&D doc), a separate render path. So the 3-D epic's S4
delivers rotation/translation-augmented inversion groups; Grandma's-recipe + the
Maskit animation are **re-homed** to the 2-D track.

### 3.5 Analytic DE (axis 4)

The current DE tracks only `|J|` (scalar). The **analytic DE** tracks the full
`3×3` (or `4×4` conformal) Jacobian **J** through the word, and estimates

```
DE ≈ (distance from p to nearest isometric-sphere boundary in final frame)
       / ‖J‖₂          (largest singular value, not the scalar product)
```

For conformal maps the Jacobian is a scalar × orthogonal, so `‖J‖₂` *equals* the
scalar the heuristic already tracks — **for pure inversions the analytic DE and
the heuristic coincide.** Cite: Hart et al. 1989 (DE ray tracing),
Hubbard–Papadopol (Jacobian DE for holomorphic maps), Knighty (Kleinian KIFS DE).

**Finding (2026-09-19, corrects the earlier "win with rotation" claim).** The
rotation fold shipped in S4 (#877) is **orthogonal**, so it does *not* break the
scalar × orthogonal structure: `J_total = (∏ scalar_i)·Q` with `Q` orthogonal, hence
`σ_max(J_total) = ∏ scalar_i` — **exactly** the scalar the heuristic accumulates.
So the analytic-Jacobian DE is **provably byte-identical** to the shipped scalar DE
for the *entire current family* (inversions + rotations), not just pure inversions.
It would differ only for a **non-conformal** generator (anisotropic fold, a true
translation-Möbius) — which this 3-D Kleinian has none of. And the near-cusp
over-step is a **numerator** issue (nearest-boundary distance overestimates the true
distance to the limit set), which the Jacobian denominator does not address. **So a
full analytic-DE toggle is a no-op here and is deferred** (revisit if non-conformal
generators are added). The practical cusp-sharpening tool is a **DE under-relaxation
factor** — shipped as S8/#881 instead (`KleinianDeFactor`: scale each march step by
`k ≤ 1` so the ray does not overshoot near cusps; `k = 1` byte-identical).

### 3.6 Preset configurations (axis 2), with their math

| Preset | Generators | Limit set character | Ref |
|--------|-----------|---------------------|-----|
| Tetrahedral 4-sphere (**ships**) | 4 inversions, even-parity cube | Cocoon between kissing spheres | current code |
| Necklace / limit-circle | Sphere-pair Schottky, ring of N | Indra's-Pearls "necklace" ring, closed loop of tangent circles | MSW 2002 ch. 6–8 |
| Klein-bottle / octahedral 6-sphere | 6 inversions, ±axis face centres | Higher-genus fundamental domain, denser cocoon | Roadmap §B.4 |
| Apollonian-extrusion | Inversion stack from a 2D Descartes packing, extruded | 3D Apollonian gasket / soft-sphere packing | Soddy 1936; Graham et al. 2003 |
| Grandma's-recipe two-generator | 2 Möbius generators, trace-parameterized | The MSW canonical family; Maskit-slice boundary groups | MSW 2002 ch. 8–9 |

The **Grandma's recipe** family (two loxodromic generators parameterized by their
traces `ta, tb, tab`) is the crown: it is *the* Indra's-Pearls parameter space,
and animating `tab` along the **Maskit slice** boundary is the marquee animation
(ties into the scene/animation engine — see §6.5 toolchain reach).

### 3.7 The #53 answer — why "Inversion iter" looks inert (post-epic, #885)

**Question (#53):** what should the user see when they change `KleinianIterations`?
**Observation (#875 smoke):** at default framing, nothing.

**Empirical measurement** (200×200, tetrahedral default, `KleinianIterations`
16 vs 64):

| Colour source | Framing | Pixels changed |
|---------------|---------|----------------|
| **Smooth** (default) | zoom 1, ε = 1.2e-3 | **0.00 %** |
| **Smooth** | zoom 40, ε = 5e-5 | **0.00 %** |
| **WordLength** | zoom 1, ε = 1.2e-3 | **96.5 %** |
| **WordLength** | zoom 40, ε = 5e-5 | **100 %** |

**Why.** The DE descent loop breaks the instant a sample escapes every sphere
(`bestK < 0`). For the tetrahedral group at any on-screen framing the descent
converges in **fewer than 16 inversions for every visible ray**, so raising the
cap to 64 yields a **byte-identical distance estimate** — hence 0.00 % change on
the Smooth shade, even deep-zoomed (the sub-16-inversion cusps that would need
more are sub-pixel or off-frame). The cap is a *real* correctness bound, not a
detail knob, and the default 16 is already past the point of visible return for
this group.

**The answer.** The iteration count is made visible by the **colour path** (S5,
#878): with **Colour by = Word length**, each pixel is banded by *how many
inversions its descent ran*, so changing the cap re-colours ~100 % of the image.
That is what the user should reach for. Delivered:
- the `KleinianIterations` tooltip now says the effect is invisible on Smooth and
  points at **Colour by = Word length** (and deep-zoom cusps);
- this section documents the measurement.

No "make Smooth respond" work is warranted: the measurement shows Smooth geometry
is iteration-independent at every visible scale, so auto-scaling the cap with zoom
(one of #885's options) would not change the Smooth render. The knob that controls
*perceived* detail on Smooth is `KleinianEpsilon` / zoom, not the inversion cap.

---

## 4. Design — data model & DE strategy

### 4.1 Layering (keeps the epic tractable)

```
KleinianGroup  (immutable descriptor: List<Generator> + config metadata)
      │  built by
KleinianPreset (enum → factory)  ── OR ──  user-edited generator list
      │  consumed by
KleinianDE (word-descent evaluator; fast inversion path + general Möbius path)
      │  fed into
[existing] ShadingPipeline.Shade<De> / OrbitTrap / GPU kernel   ← unchanged
```

The **only new hot-path code** is `KleinianGroup` + the generalized `KleinianDE`
loop. Everything below the DE is reuse.

### 4.2 `Generator` representation

Decision candidates (resolve in slice S1):

- **(A) Tagged union** `Generator { Kind (Inversion|Rotation|Mobius); … }` with a
  concrete `Evaluate(p, ref jacScale)` per kind. Simple, matches today's inversion
  case exactly, easy CPU→GPU (fixed struct, kind byte). **Recommended.**
- **(B) Uniform 4×4 conformal matrix (SO⁺(4,1) / Vahlen/Clifford 2×2 over
  quaternions).** Mathematically clean (every generator is one matrix, composition
  is matrix product), exact analytic DE falls out. Heavier, and inversions become
  less obvious. Keep as the *internal* form the analytic-DE slice (S6) may adopt;
  do not expose in the data model v1.

**Recommendation:** ship (A) for the descriptor + fast path; let the analytic-DE
slice introduce the Vahlen/quaternion 2×2 form (B) *internally* for the Jacobian
accumulation only. Bibliography: Ahlfors 1985 (Möbius via Clifford algebras),
Vahlen matrices.

### 4.3 `KleinianGroup` descriptor

```
sealed record KleinianGroup(
    IReadOnlyList<Generator> Generators,
    KleinianPreset Preset,            // provenance for UI + region persistence
    int  MaxWordLength,               // = KleinianIterations today
    bool AllInversions)               // fast-path flag (all Kind==Inversion)
```

Serializable → lives in **Abstractions** (UI has no Engine ref; the
lighting-preset precedent, see memory `project_lighting_fx_presets_580`). The
`AllInversions` flag gates the byte-identical fast path.

### 4.4 DE strategy — three-tier, opt-in, byte-identical default

1. **Tier 0 (ships, default):** tetrahedral preset → *exact current code path*.
   Guarantee: at `Preset.Tetrahedral` with default params, DE is byte-identical
   (regression-lock with a golden test).
2. **Tier 1 (inversion fast path):** any all-inversion generator list → the same
   descent loop, generalized over `Generators` instead of the hard-coded array.
   Trivial refactor of `KleinianDE`.
3. **Tier 2 (general Möbius-word):** mixed inversion + rotation/Möbius generators →
   the word-descent evaluator, Jacobian accumulated per §3.4/§3.5.

Analytic DE (§3.5) is an *orthogonal* toggle `KleinianAnalyticDe` layered on Tier
1/2 (for Tier 0 it is a no-op by the conformal-coincidence argument, so default
off = unchanged).

---

## 5. Decision: dedicated type vs param-driven vs colouring-only

**Decision: extend the existing `FractalType.Kleinian`, param-driven — do NOT add
a new `FractalType`.**

Rationale:

- The render/motion/camera registration (~19 sites, 3D family — the heavy
  checklist) is *already done* for `Kleinian`. A new type would duplicate all of
  it for zero user benefit; the group is a *parameter*, not a new fractal family.
- Back-compat: the tetrahedral preset stays the default `KleinianPreset`, so
  existing saved regions/scenes render unchanged (Tier 0).
- The generalization surface is entirely inside `FractalParameters.Kleinian*` +
  the new `KleinianGroup` descriptor + the DE. This matches the "expose hardcoded
  constants as params" project preference (memory `feedback_tunable_params`).

**Not colouring-only:** the geometry genuinely changes (new limit sets), so this
is a calculator/params change, not a theme change. *But* the colour axis is real
and planned separately (§6.5).

**Not DSL/CalcGen:** neither DSL reaches group-word enumeration or 3D
sphere-inversion DE (see §6.5). This ships as calculator + params.

---

## 6. Toolchain-reach analysis (Rule A)

### 6.1 CalcGen / DSL

**Out of reach, by construction.** CalcGen and the DSL interpreter both compile a
per-pixel `f(z, c)` complex iteration; they have no vocabulary for (a) a 3D
sphere-inversion descent, (b) group-word enumeration, or (c) a distance estimator
over a generator list. The Kleinian DE is a bespoke 3D raymarch kernel, exactly as
the shipped one already is. **No DSL/CalcGen extension is planned or sensible
here** — recorded so a future pass does not re-litigate it. (Contrast §3.6 dual-
orbit and transcendental Julia, where a DSL *bailout/axis* primitive was reusable;
here there is no analogous per-pixel hook.)

The one *adjacent* DSL idea, deferred: a **"generator script"** mini-format (a
list of `invert cx cy cz r` / `rotate axis angle` lines) as a text way to author
`KleinianGroup` — this is a *data* DSL for the group, not the CalcGen expression
DSL, and belongs to the UI slice (S3) if pursued, not to CalcGen.

### 6.2 ColorGen / Color Theme

**In reach, and genuinely additive.** Two new colour drivers, both riding existing
theme infrastructure:

- **Word-length / generator-parity colouring.** Colour a hit point by the length
  of the reducing word (how many descent steps to reach the fundamental domain) or
  the parity/identity of the last generator applied. This is a **Categorical
  `ColorThemeKind`** — the exact precedent is the chaotic-billiard categorical
  colouring (memory `project_chaotic_scattering_626`, enum-param anim hook). It
  needs a new per-hit integer AOV (`lastGenerator` / `wordLength`) written
  alongside the smooth value — one buffer, one theme kind.
- **Orbit-trap over generator boundaries.** Already exists (`De.OrbitTrap`);
  generalize `KleinianTrap` to min-distance over *all* generators' isometric
  spheres. Feeds the shipped OrbitTrap theme kind (F13) and the relief-height
  driver (memory `project_s11_orbit_trap_relief`) for free.

**ColorGen** (the palette-generator tool) is orthogonal — it produces palettes,
which apply unchanged; no ColorGen work needed. The Color *Theme* editor gets one
new categorical kind. Recorded as a colour follow-up slice (S5), not a blocker for
geometry.

### 6.3 Region / scene persistence

`KleinianGroup` (preset + generator list) must persist at the 3 region sites
(memory `project_editor_store_schema_gotcha`) and animate through the scene engine
for the Maskit-slice animation (§6.5). Generator lists are small; serialize the
descriptor. The `CameraTrackTests.ExpectedCameraTypes` 3D-family list already
includes `Kleinian` (no change — same type).

### 6.4 GPU

The GPU kernel hard-codes 4 centres. Variable-count generators on the GPU is a
*later* slice (S7): pass the generator list as a small structured buffer, branch
on kind. Ships **after** CPU parity; the CPU path is authoritative (the shipped
GPU kernel already falls back to CPU for AOV views, so a "CPU-only for non-preset
groups" interim is acceptable — same pattern as the orbit-trap GPU-twin gotcha).

### 6.5 Animation / scene engine (the marquee)

The **Grandma's-recipe two-generator family** parameterized by traces
`(ta, tb, tab)` gives a *continuous* parameter space; animating `tab` toward the
**Maskit-slice boundary** produces the classic Indra's-Pearls "group degenerating
into a limit curve" animation. The scene engine already animates enum + scalar
params (enum-param anim hook, #632 precedent). This is the highest-value *visual*
payoff and should be an explicit slice (S6/S8) — it is why the trace-parameterized
Grandma's-recipe generator (not just raw sphere lists) is worth building.

---

## 7. Sliced implementation plan (each = its own tracking issue)

Dependencies stated inline (repo has no auto-blocking). **S1–S3 = MVP** (arbitrary
inversion groups + presets, the 80% win). S4+ = the general Möbius machinery and
polish.

- **S1 — [#874](https://github.com/AloneButUnsober/FracturingFog/issues/874) — `KleinianGroup` descriptor + generalized inversion DE.**
  New serializable `KleinianGroup` / `Generator` (tagged-union, inversion kind) in
  Abstractions. Refactor `KleinianDE`/`KleinianTrap` to loop over `Generators`
  instead of the hard-coded array. **Tier 0 byte-identical golden test** at the
  tetrahedral preset. No UI yet (preset defaulted). *Deps: none.* — **MVP core.**

- **S2 — [#875](https://github.com/AloneButUnsober/FracturingFog/issues/875) — Preset library + `KleinianPreset` enum + params.**
  Tetrahedral (default, = today), octahedral-6-sphere, Apollonian-extrusion,
  necklace-N. `FractalParameters.KleinianPreset` + any per-preset scalar (ring
  count N, twist). Preset factory builds the generator list. Region persistence
  (3 sites). Smoke-render each preset. *Deps: S1.* — **MVP.**

- **S3 — [#876](https://github.com/AloneButUnsober/FracturingFog/issues/876) — Generator-list editor UI (Control Center section).**
  Add/remove/edit inversion spheres; preset picker; optional text "generator
  script" import (§6.1). Avalonia-only, Control Center section (not FloatingMenu —
  deprecated). *Deps: S1, S2.* — **MVP completes here.**

- **S4 — [#877](https://github.com/AloneButUnsober/FracturingFog/issues/877) — rotation-augmented 3D inversion groups.**
  **SHIPPED** as a **rotation fold** (`KleinianRotation`: axis + angle applied after
  each inversion step; orthogonal → DE stays valid, angle 0 = byte-identical) — the
  Fragmentarium/Knighty "rotated pseudo-Kleinian", producing twisted/spiral 3-D
  limit-set solids. Exposed as params (`KleinianRotationAngle` + axis) on every
  preset/Custom; region-persisted; GPU-gated to CPU when rotating. *Deps: S1.*
  **Scope correction (finding, 2026-09-19):** the originally-planned *Grandma's-recipe
  two-generator + general Möbius-word DE* is a **complex-Möbius (2-D) construction** —
  its limit set is a fractal *curve* on the Riemann sphere Ĉ, not a 3-D solid, so it
  does **not** fit this 3-D distance-estimator (see §3.4 note). It is **re-homed** to a
  new **2-D Indra's-Pearls renderer** track ([#888](https://github.com/AloneButUnsober/FracturingFog/issues/888), relates §2.3); the
  Maskit animation (S6) rides that, not this.

- **S5 — [#878](https://github.com/AloneButUnsober/FracturingFog/issues/878) — Colour drivers (ColorGen/Theme reach, §6.2).**
  `wordLength` / `lastGenerator` integer AOV + new **Categorical `ColorThemeKind`**;
  generalized orbit-trap over all generators. *Deps: S1 (+S4 for word-length under
  Möbius). Independent of S2/S3.*

- **S6 — [#879](https://github.com/AloneButUnsober/FracturingFog/issues/879) — Maskit-slice / trace animation hook (§6.5).**
  Animate Grandma's-recipe `tab`; scene-engine track. The marquee animation.
  **RE-HOMED (2026-09-19):** rides the **2-D Indra's-Pearls renderer** track
  ([#888](https://github.com/AloneButUnsober/FracturingFog/issues/888)), not the 3-D
  DE path (Grandma's recipe is a 2-D construction — see S4 finding + §3.4). Depends on
  #888, not #877.

- **S7 — [#880](https://github.com/AloneButUnsober/FracturingFog/issues/880) — GPU parity for variable generator lists (§6.4).**
  Structured-buffer generators in `KleinianGpuCalculator`. *Deps: S1 (+S2 presets,
  +S4 for Möbius on GPU). CPU-only interim acceptable until then.*

- **S8 — [#881](https://github.com/AloneButUnsober/FracturingFog/issues/881) — DE relaxation factor (was: analytic DE toggle).**
  **SHIPPED** as `KleinianDeFactor` — a sphere-trace under-relaxation multiplier
  (`k ≤ 1` shortens each march step so the ray does not overshoot near cusps →
  crisper cusps, more steps; `k = 1` byte-identical). **Scope correction (§3.5
  finding):** the originally-planned analytic-Jacobian DE is **provably byte-identical**
  to the shipped scalar DE for the whole current conformal family (inversions +
  orthogonal rotations → `σ_max = ∏ scalar_i`), so it is a **no-op** and **deferred**
  until a non-conformal generator exists. The relaxation factor is the tool that
  actually sharpens cusps. *Deps: S1.*

**Recommended order:** S1 → S2 → S3 (ship MVP, pause for review) → S5 (cheap colour
win, S1-only) → S4 → S6 → S8 → S7 (GPU last, largest, CPU-authoritative).

---

## 8. Risks & open questions

- **General word-descent termination.** For non-classical (non-Schottky, near-
  degenerate Maskit-boundary) groups the fundamental-domain descent may not
  terminate cleanly; needs an iteration cap + a "which generator moves p inward"
  rule that is robust. Mitigation: keep `MaxWordLength` a hard cap (as today);
  validate presets are classical Schottky before shipping them.
- **Numerical blow-up near cusps.** Tangent-sphere groups have the interesting
  content exactly at cusps where `|p − c|² → 0` (the `e2 < 1e-30` guard today).
  Analytic DE (S8) is the real fix; until then, epsilon-clamp and document the
  soft floor.
- **Descriptor v1 lock-in.** Getting `Generator` right in S1 matters — S2–S8 all
  build on it. Resolve the tagged-union vs matrix question (§4.2) *in S1 review*
  before S4 depends on it. Recommendation stands: tagged-union descriptor +
  internal matrix form for analytic DE only.
- **Preset authenticity.** "Klein-bottle 6-sphere" and "Apollonian-extrusion" need
  their generator sets validated against the literature (they must actually be
  discrete groups with the claimed limit set), not just visually plausible. Cite
  the construction per preset in code comments.
- **GPU divergence.** Variable-length generator words on the GPU cause warp
  divergence; may need a fixed max-generator-count kernel. Acceptable — S7 is last
  and CPU is authoritative.

---

## 9. Bibliography anchors (Rule B — flesh out in Resources-Bibliography.md)

- **Mumford, Series & Wright 2002** — *Indra's Pearls: The Vision of Felix Klein.*
  Cambridge UP. (Already stubbed in RnD §7.) Grandma's recipe, Maskit slice,
  necklace groups, limit-set enumeration — the primary reference for this doc.
- **Maskit 1988** — Bernard Maskit. *Kleinian Groups.* Springer Grundlehren.
  Fundamental domains, the Maskit slice, discreteness.
- **Ahlfors 1981/1985** — Lars Ahlfors. *Möbius transformations in several
  dimensions* / *Möbius transformations and Clifford numbers.* Vahlen-matrix /
  Clifford-algebra representation of `Möb(Ŝⁿ)` — basis for the analytic-DE form.
- **Hart, Sandin & Kauffman 1989** — *Ray tracing deterministic 3-D fractals.*
  SIGGRAPH. The distance-estimator ray-tracing method the renderer uses.
- **Soddy 1936 / Graham, Lagarias, Mallows, Wilks & Yan 2003** — Descartes circle
  theorem / *Apollonian circle packings.* Basis for the Apollonian-extrusion preset.
- **Knighty (fractalforums), pseudo-Kleinian / Kleinian-KIFS DE** — the practical
  distance estimator generalizing sphere-inversion descent to arbitrary generator
  sets; the current calculator already follows this for the fixed group.
- **Vahlen 1902** — K. Th. Vahlen. Clifford-matrix Möbius representation
  (historical origin of the Ahlfors form).

---

## 10. Change log

- **2026-09-19** — Doc created (issue #855). Surveyed shipped `KleinianCalculator`
  (fixed tetrahedral 4-sphere, inversion-scale heuristic DE, orbit-trap already
  present). Planned the four generalization axes (Roadmap §B.4) as a `KleinianGroup`
  descriptor + three-tier DE (byte-identical Tier 0). **Decision:** extend
  `FractalType.Kleinian` param-driven, *not* a new type. Toolchain reach: DSL/CalcGen
  out of reach (no per-pixel hook); ColorGen/Theme in reach (categorical word-length
  kind + generalized orbit-trap). Sliced S1–S8 (S1–S3 = MVP). Sub-issues filed and
  cross-linked from this doc + #855.
- **2026-09-19** — **MVP shipped** (S1 #874 descriptor + S2 #875 presets + S3 #876
  editor). **S4 #877 shipped** as a **rotation fold** (rotated pseudo-Kleinian) after a
  scope finding: the planned Grandma's-recipe + general Möbius-word DE is a
  complex-Möbius **2-D** construction (limit set = a curve on Ĉ, not a 3-D solid) and
  does not fit the 3-D distance-estimator (§3.4 finding). Grandma's recipe + the Maskit
  animation (S6 #879) **re-homed** to a new **2-D Indra's-Pearls renderer** track
  (#888). Remaining 3-D slices: S5 #878 colour, S7 #880 GPU, S8 #881 analytic DE.
- **2026-09-19** — **S5 #878 shipped** (colour-source selector: WordLength /
  LastGenerator — also the #53 visibility mechanism). **S7 #880 deferred** (no GPU
  hardware). **S8 #881 shipped** as a **DE under-relaxation factor** (`KleinianDeFactor`)
  after a finding: the analytic-Jacobian DE is provably byte-identical to the scalar DE
  for the whole conformal inversion+rotation family (§3.5), so true analytic DE is a
  no-op and deferred. **3-D epic track complete** (MVP + rotation + colour + relaxation;
  GPU deferred, analytic deferred). Grandma's/Maskit on the 2-D track (#888/#879);
  #53 answer post-epic (#885).
- **2026-09-19** — **#885 / #53 answered** (§3.7). Measured `KleinianIterations`
  16 vs 64: **0.00 %** pixel change on the Smooth shade at zoom 1 *and* zoom 40 —
  the descent converges in &lt;16 inversions for every visible ray, so the cap is
  inert on Smooth by nature. It becomes the dominant driver under **Colour by =
  Word length** (96–100 % of pixels re-band). Delivered an honest
  `KleinianIterations` tooltip pointing at the word-length colour path + this
  section; no "make Smooth respond" work (auto-scaling the cap with zoom would not
  change the iteration-independent Smooth render). Closes #53 + #885.
