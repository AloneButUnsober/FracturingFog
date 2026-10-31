# Double-Slit Fractals — R&D and Design Plan

**Epic:** [#1565](https://github.com/AloneButUnsober/FracturingFog/issues/1565) ·
**This doc:** S0 [#1566](https://github.com/AloneButUnsober/FracturingFog/issues/1566) ·
**Parent:** R&D epic #850 ·
**Builds on:** dual-orbit path interference #1126 ([Dual-Orbit Colouring R&D §3.D](DualOrbit-Coloring-RnD.md)) ·
**Session:** 2026-10-31

The double-slit experiment is the organising idea for this family: **amplitudes add, then
square**. Two paths give complex amplitudes ψ₁, ψ₂; what is seen is P = |ψ₁ + ψ₂|², and the fringes
are the cross term 2|ψ₁||ψ₂|cos Δφ
([Feynman, Leighton & Sands, *Lectures* Vol. III ch. 1](../Resources-Bibliography.md#double-slit-fractals)).
The epic turns two loose ideas into renderable maths:

1. **Probability sets the fractal dimension.** A recursive slit cascade is a weighted iterated
   function system (IFS). Its branch weights are fringe probabilities, and the weights fix the
   multifractal spectrum D_q (§5).
2. **Angular momentum sets the filament.** A vortex phase e^{iℓφ} twists fringes into |ℓ|-armed
   spirals; per level it gives coils and braids (§3.2, §6).

This doc is the canonical research context; the issues are the task list. Every slice issue links
here, and the slice table in §10 links back.

> **Colour-blind note.** The project owner is red/green colour-blind. Defaults code intensity on
> **lightness** and phase on the **blue ↔ amber/yellow** axis, never on red ↔ green.

> **Citation status.** Every source below was checked on 2026-10-31 (authors, venue, volume, pages,
> DOI) and is in [Resources-Bibliography → Double-slit fractals](../Resources-Bibliography.md#double-slit-fractals).
> H. F. Talbot's original 1836 paper is the one exception: it is under *Citations awaiting
> verification*.

---

## 1. What already ships — do not re-scope

| Piece | Where | What it does |
|---|---|---|
| Two-path interference | #1126, `DualOrbitEscapeCalculator.Interference.cs` | Each escaping orbit is a path with Φ = G + 2πiθ (log Böttcher coordinate). Normalised intensity `I = 1 + cos(κΔG) / cosh(2πγκΔθ)`. κ is the "ħ" knob (colour-only, animatable) |
| Angle-selective damping γ | same | γ > 0 washes fringes out except where the two external angles agree (Δθ = 0). Default γ = 0, because S6 branch cuts below G_s(0) show as seams |
| Multiplicative cascades | Bibliography (Mandelbrot 1974) | Canonical multifractal measures |

So the dual-orbit side of this epic (S1, S2) **extends** #1126. S3–S6 are new calculators.

---

## 2. The core identity

For N paths with complex amplitudes aₖ:

```
P = |Σ aₖ|² = Σ|aₖ|²  +  Σ_{j≠k} aⱼ a̅ₖ
     incoherent part      interference (cross) terms
```

Measuring "which path" removes the cross terms; that is the whole of the double-slit puzzle in one
line. Every slice below is this identity with a different choice of paths and phases.

---

## 3. Dual-orbit extensions (S1 #1567, S2 #1568)

Both act on the existing #1126 pair `a = e^{+x}e^{iκG_z}`, `b = e^{−x}e^{iκG_c}`
(x = πγκΔθ). Both are **colour-only**: they rebuild the scalar from the cached smooth counts and
lifted angles, and never iterate.

### 3.1 Which-path measurement η (S1)

Scale the cross term by (1 − η), η ∈ [0, 1] (the off-diagonal of a two-state density matrix):

```
I_η = (|a|² + |b|² + 2(1−η)·Re(a b̅)) / (|a|² + |b|²)
    = 1 + (1−η)·cos(κΔG) / cosh(2πγκΔθ)
```

- η = 0 is #1126 exactly (byte-identical is the test).
- η = 1 gives I ≡ 1: fringes gone, the "detector is on" frame.
- **Distinct from γ.** γ removes coherence *selectively* (by Δθ); η removes it *uniformly*. Both stay.

Parameter: `DualOrbitWhichPath` (double, 0..1, default 0, colour-only, animatable).

### 3.2 Angular-momentum winding ℓ (S2)

Give each path an extra vortex phase e^{iℓ·2πθ} (θ = that path's lifted external angle). In the
normalised form only differences survive, so the cross term becomes

```
I = 1 + cos(κΔG + 2πℓΔθ) / cosh(2πγκΔθ)
```

- **Continuity.** Δθ is wrapped to [−½, ½); a wrap changes 2πℓΔθ by 2πℓ, which leaves the cosine
  unchanged **only for integer ℓ**. So ℓ is an integer parameter (`DualOrbitAngularMomentum`).
- **Shape.** Lines of equal phase now turn |ℓ| times as Δθ goes once round, so fringes become
  |ℓ|-armed spirals around the set. ℓ → −ℓ mirrors the handedness.
- **Opposite windings** (e^{+iℓ2πθ_z}, e^{−iℓ2πθ_c}) put 2πℓ(θ_z + θ_c) in the phase, i.e. the
  *mean* angle, not Δθ. That needs the individual lifted angles, which the interference pass has
  but the symmetrised normalised form drops. S2 decides whether to ship it, after measuring the
  seams.
- **Branch cuts.** The S6 seams below G_s(0) that forced γ = 0 enter through Δθ here too. S2 must
  measure them at ℓ ≠ 0 and pick a default that renders clean. If none does, ship ℓ = 0 as default
  and document the seam.

---

## 4. Recursive-slit cascade calculator (S3 #1569)

### 4.1 Construction

A slit cascade is a weighted IFS on the aperture plane. At each level, every slit is replaced by N
children at scale r < 1:

```
fᵢ(x) = r·x + bᵢ,   i = 0..N−1,   transmitted power share pᵢ (Σ pᵢ = 1)
```

The child amplitude is √pᵢ, so pᵢ is the share of power through that branch.

**Where the weights come from (the "fringe becomes a slit" rule).** The N child offsets bᵢ are
equally spaced sample points across a window of the parent's own far-field pattern, and each
child's weight is the parent's fringe intensity there:

```
pᵢ = I_parent(bᵢ) / Σⱼ I_parent(bⱼ),
I_parent(x) = sinc²(π w x / λL) · cos²(π d x / λL)     (two-slit: width w, separation d)
```

> **Finding (2026-10-31): a symmetric slit *pair* is not enough.** With N = 2 children placed
> symmetrically, the two weights are equal by symmetry. The cascade is then a plain Cantor set
> (monofractal), and the "probability" changes nothing. **N ≥ 3 sampled children** are needed for
> the weights to differ. Children landing on a dark fringe get pᵢ = 0 and drop out:
> **dark fringes delete branches.**

### 4.2 Rendering: exact Fraunhofer product

In the far field the amplitude is the Fourier transform of the aperture. For a self-similar
aperture it factorises **exactly** into one array factor per level times the transform of the
level-0 slit, all at scaled frequencies (the structure-factor result of
[Bak et al.](../Resources-Bibliography.md#double-slit-fractals); experiment and theory of Cantor-bar
diffraction in [Allain & Cloitre](../Resources-Bibliography.md#double-slit-fractals)):

```
A(u) = S(rᴰ u) · Π_{n=0}^{D−1} F(rⁿ u),     F(u) = Σᵢ √pᵢ · e^{−2πi bᵢ·u}
I(u) = |A(u)|²
```

D = depth, S = transform of the single slit (sinc for a rectangle). The cost is **O(D·N) per pixel**,
with no aperture sampling and no FFT. The 2D version uses offsets bᵢ ∈ ℝ² and u ∈ ℝ².

**Views** (`SlitCascadeView`):

- **Screen**: I(u), the diffraction pattern (colour by intensity; phase arg A on the blue/amber axis).
- **Aperture**: the weighted measure itself, coloured by the local Hölder exponent (§5). This is the
  object whose dimension the probabilities set.

### 4.3 Parameters (first cut)

`SlitCascadeChildren` N (≥ 2; 3+ for multifractal), `SlitCascadeRatio` r, `SlitCascadeDepth` D,
`SlitCascadeSeparation` d, `SlitCascadeWidth` w, `SlitCascadeWavelength` λ, `SlitCascadeDistance` L,
`SlitCascadeLayout` (line / ring), `SlitCascadeView`. Follow the new-2D-calculator checklist from the
chaotic-scattering epic (#626).

### 4.4 Invariants to test (independent, not self-consistency)

- D = 1, N = 2: matches the closed-form two-slit pattern `sinc²(πwx/λL)·cos²(πdx/λL)`.
- Fraunhofer product = direct sum over all Nᴰ leaf slits (small D, brute force).
- Parseval: ∫|A|² du = transmitted power (Σ pᵢ = 1 per level ⇒ constant in D).

---

## 5. Probability sets dimension (S4 #1571)

The aperture measure μ from §4.1 is a self-similar multifractal measure. Its generalized
(Rényi) dimensions ([Hentschel & Procaccia](../Resources-Bibliography.md#double-slit-fractals)) and
f(α) spectrum ([Halsey et al.](../Resources-Bibliography.md#double-slit-fractals)) are closed form for
equal ratios r:

```
Σᵢ pᵢ^q = r^{τ(q)}   ⇒   τ(q) = log Σ pᵢ^q / log r,     D_q = τ(q) / (q − 1)

D₀ = log N_eff / log(1/r)          (N_eff = children with pᵢ > 0)
D₁ = Σ pᵢ log pᵢ / log r           (information dimension)
D₂ = log Σ pᵢ² / log r             (correlation dimension)
```

So λ, d and w set the fringe pattern, the fringe pattern sets pᵢ, and pᵢ sets D_q. **That is "the
probability sets the fractal dimension", made exact.**

**Local colouring.** A point with address i₁i₂…iₙ has mass p_{i₁}⋯p_{iₙ} in a cell of size rⁿ, so
its coarse Hölder exponent is

```
α = log(p_{i₁}⋯p_{iₙ}) / log(rⁿ)
```

The aperture view colours by α (or by f(α)). A HUD readout shows D₀, D₁, D₂ and a q slider for D_q.

**Invariants.**
- Uniform pᵢ ⇒ D_q = log N / log(1/r) for every q (the monofractal check).
- D_q is non-increasing in q.
- D₁ closed form vs a box-counting (mass-partition) estimate on the rendered measure, within
  tolerance.

What the *screen* view shows is the Fourier transform of μ, not μ. Allain & Cloitre's point is that
the diffraction intensity carries the dimension (power-law decay of the structure factor), so the
screen and aperture views are two readings of one D.

---

## 6. Angular-momentum filaments in the cascade (S5 #1572)

Orbital angular momentum of light: a field with phase e^{iℓφ} carries ℓħ per photon and has a dark
phase singularity on its axis
([Allen, Beijersbergen, Spreeuw & Woerdman](../Resources-Bibliography.md#double-slit-fractals)).

**Discrete vortex.** In the cascade, a level's children sit on a **ring** (`SlitCascadeLayout = ring`)
at angles φᵢ = 2πi/N, and each child gets the phase e^{iℓφᵢ}:

```
F(u) = Σᵢ √pᵢ · e^{iℓφᵢ} · e^{−2πi bᵢ·u}
```

This keeps the exact product of §4.2, because the phase is a constant per child.

> **Finding (2026-10-31): a slit *pair* cannot carry a vortex.** With N = 2, e^{iℓφᵢ} is only ±1.
> Odd ℓ gives an antiphase pair (a dark centre *line*), not a vortex. **N ≥ 3 on a ring** is needed.
> The expected topological charge of the dark core is ℓ reduced into (−N/2, N/2] (ℓ mod N). S5 must
> **verify that numerically** by a contour integral of arg A round the core before relying on it.

**Filament controls.**
- |ℓ| = arm count, sign = handedness.
- ℓₙ per level (constant / alternating / ramp) gives coils or braids. Each level's vortex appears at
  its own scale rⁿ, so the product is a nested chain of vortices.

**Prior art, closest:** the *devil's vortex lens*
([Furlan, Giménez, Calatayud & Monsoriu](../Resources-Bibliography.md#double-slit-fractals)) combines a
Cantor-function (devil's staircase) radial phase with a spiral phase and makes a self-similar chain
of vortices **along the axis**. FF's cascade nests vortices **in the transverse plane** instead.
Related idea, different geometry; S5 must credit it at the point of use.

**Invariants.** ℓ ≡ 0 is byte-identical to S3; |A| = 0 at each core; the winding number of arg A
round a core equals the expected charge.

---

## 7. Fractal Talbot carpet (S6 #1570)

A periodic grating with period d and Fourier coefficients cₙ, propagated paraxially a distance z,
has intensity

```
I(x, z) = | Σₙ cₙ · exp(2πi n x / d) · exp(−iπ λ z n² / d²) |²,      z_T = 2d² / λ
```

- At z = z_T every phase is a multiple of 2π: the grating images itself (Talbot effect).
- At z = z_T/2 the phase is πn² ≡ πn (mod 2π): the image is shifted by d/2.
- At rational z/z_T: finite superpositions of shifted copies (fractional Talbot). At irrational z/z_T
  the transverse intensity is a **fractal**; longitudinal and most oblique planes are fractal too
  ([Berry & Klein](../Resources-Bibliography.md#double-slit-fractals)). The (x, z) picture is the
  "quantum carpet" ([Berry, Marzoli & Schleich](../Resources-Bibliography.md#double-slit-fractals)).
- The quantum-box analogue: a uniform state in a box has a fractal probability density of dimension
  D + ½ ([Berry, "Quantum fractals in boxes"](../Resources-Bibliography.md#double-slit-fractals)).

**Render.** Pixel = (x, z) in a window. Per pixel a truncated sum over M modes is O(M); per row
(fixed z) an FFT is O(W log W). Sharp-edged gratings (Ronchi, δ-comb) are what produce the fractal;
the mode count M is the user-visible "how sharp" knob.

**Optional link to S3.** The S3 cascade's aperture can serve as the grating profile (its cₙ are the
§4.2 product sampled at n/d). This is a soft link, not a dependency.

**Invariants.** z = z_T reproduces the grating; z = z_T/2 reproduces it shifted by d/2; the output
converges as M grows (vs a reference run with many more modes).

---

## 8. Toolchain reach (per #850's standing rule)

| Piece | CalcGen / DSL | ColorGen / Colour Theme Editor |
|---|---|---|
| S1 η, S2 ℓ | n/a (colour-only on cached data) | A ColorGen input for the #1126 scalar with η, ℓ as inputs would let users write their own interference blends |
| S3 cascade | A per-pixel product over D levels of an N-term complex sum. That needs loops over constant-size arrays in the DSL; assess against the equation language (#937) before choosing hardcoded vs DSL | Phase input (arg A) on the blue/amber axis |
| S4 D_q | Closed form; HUD only | α / f(α) as ColorGen inputs |
| S6 Talbot | A Fourier sum over M modes: same DSL loop question as S3 | Intensity + phase inputs |

The DSL loop question is shared by S3 and S6. Whichever lands first answers it for both.

---

## 9. Novelty status (researched 2026-10-31)

| Element | Status | Source |
|---|---|---|
| Diffraction of Cantor / fractal gratings | **Established** (1986–) | Allain & Cloitre; Bak et al. |
| Fractal Talbot carpets, quantum fractals | **Established** (1996–) | Berry & Klein; Berry; Berry, Marzoli & Schleich |
| Orbital angular momentum, vortex beams | **Established** (1992–) | Allen et al. |
| Fractal (Cantor) phase + spiral phase → vortex chains | **Established** (2009) | Furlan et al. (devil's vortex lens) |
| Multifractal D_q / f(α) of self-similar measures | **Established** (1983–86) | Hentschel & Procaccia; Halsey et al. |
| Path interference on escape-time orbits | Shipped in FF #1126; novelty already covered by #1130 | Dual-Orbit R&D §8 |
| Weights of a slit cascade set *by its own parent's fringe intensity* (fringe-sampled IFS) | **No prior source found**, from a limited search; not a claim | — |
| Integer OAM winding on the external-angle difference of a Mandelbrot path pair | **No prior source found**, from a limited search; not a claim | — |

Searches run: fractal diffraction / Cantor gratings and multifractal D_q; fractal vortex lenses and
OAM; Talbot carpets; "double slit" or "interference" Mandelbrot colouring. The last found nothing
beyond FF's own #1126 and unrelated moiré work on period maps.

**How to describe it (user-facing).** "Fracturing Fog renders known fractal-optics results (Cantor
diffraction, Talbot carpets, vortex beams) and ties them to escape-time fractals through the
double-slit rule. The fringe-sampled cascade and the angular-momentum winding on dual orbits are FF's
own combinations; we found no earlier source, but haven't done an exhaustive search." Never call the
physics new.

---

## 10. Slice plan

| Slice | Issue | Needs | Amendment from this research |
|---|---|---|---|
| S0 design + citations | #1566 | — | this doc |
| S1 which-path η | #1567 | S0 | none |
| S2 OAM winding ℓ (dual orbit) | #1568 | S0 | ℓ must be an integer (§3.2); opposite-winding mode needs individual θ |
| S3 slit cascade | #1569 | S0 | N ≥ 3 sampled children, fringe-sampled weights (§4.1); exact Fraunhofer product (§4.2); add aperture view |
| S4 probability → dimension | #1571 | S3 | closed-form D_q for equal r (§5); α colouring on the aperture view |
| S5 OAM filaments (cascade) | #1572 | S3 | ring layout with N ≥ 3; charge = ℓ mod N, to verify (§6); credit the devil's vortex lens |
| S6 Talbot carpet | #1570 | S0 | none |
| S7 presets / close-out | #1573 | all | — |

Parallel after S0: {S1, S2, S3, S6}. After S3: {S4, S5}.

**Standing rules for every slice:** batch parity (`RegionFractalParams` → `--param`,
`BuildFractalParameters`, `BatchCommandBuilder` round-trip), recolour-guard classification (#981),
and citations in the Bibliography in the same PR.

---

## 11. References

All are in [Resources-Bibliography → Double-slit fractals](../Resources-Bibliography.md#double-slit-fractals).

- R. P. Feynman, R. B. Leighton, M. Sands, *The Feynman Lectures on Physics* Vol. III, ch. 1
  "Quantum Behavior", 1965.
- C. Allain, M. Cloitre, "Optical diffraction on fractals," *Phys. Rev. B* 33(5), 3566–3569, 1986.
- D. Bak, S. P. Kim, S. K. Kim, K.-S. Soh, J. H. Yee, "Fractal Diffraction Grating," arXiv:physics/9802007, 1998.
- M. V. Berry, S. Klein, "Integer, fractional and fractal Talbot effects," *J. Mod. Opt.* 43(10),
  2139–2164, 1996.
- M. V. Berry, "Quantum fractals in boxes," *J. Phys. A* 29(20), 6617–6629, 1996.
- M. Berry, I. Marzoli, W. Schleich, "Quantum carpets, carpets of light," *Physics World* 14(6),
  39–46, 2001.
- L. Allen, M. W. Beijersbergen, R. J. C. Spreeuw, J. P. Woerdman, "Orbital angular momentum of
  light and the transformation of Laguerre–Gaussian laser modes," *Phys. Rev. A* 45(11), 8185–8189,
  1992.
- W. D. Furlan, F. Giménez, A. Calatayud, J. A. Monsoriu, "Devil's vortex-lenses," *Opt. Express*
  17(24), 21891, 2009.
- H. G. E. Hentschel, I. Procaccia, "The infinite number of generalized dimensions of fractals and
  strange attractors," *Physica D* 8(3), 435–444, 1983.
- T. C. Halsey, M. H. Jensen, L. P. Kadanoff, I. Procaccia, B. I. Shraiman, "Fractal measures and
  their singularities: the characterization of strange sets," *Phys. Rev. A* 33(2), 1141–1151, 1986.
- B. Mandelbrot, "Intermittent turbulence in self-similar cascades," *J. Fluid Mech.* 62, 1974
  (already credited).
