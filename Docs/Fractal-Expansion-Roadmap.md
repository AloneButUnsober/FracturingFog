# Fractal Expansion Roadmap

Future work plan. Each family below is **not yet rendered** by Fracturing Fog as of v0.6.x. Grouped by engine reuse — items in same phase share infrastructure.

Existing extension points to reuse:
- `IFractalKernel` struct-generic kernel (see `FRACTAL_EXPANSION_PROMPTS.md` Prompt 0)
- `FractalParameters` record for per-family params
- CalcGen 5-path pipeline (scalar / AVX2 / Pert / BLA / ILGPU) — auto-applies once kernel registered
- 3D pipeline: `UserBulb` raymarcher + `Vec3` / `Quat` triplex algebra
- Theme system gates interior-cycle themes per FractalType; smooth-iter + distance themes work on any escape-time kernel

---

## Phase A — 2D escape-time, kernel-only (cheapest wins)

Reuse existing `IFractalKernel` + per-pixel iteration loop. Each = one struct file + Type combo entry + Help → Mathematics tab. No new pipeline.

### A.1 Magnet 1
- Formula: `z = ((z² + c - 1) / (2z + c - 2))²`
- Rational map; bailout 10² (slower grow), needs divide-safe Step
- Risk: pole at `2z + c - 2 = 0` — clamp denom magnitude in Step
- Params: none beyond c
- Deliver: `MagnetOneKernel.cs`, Type combo, Math help tab, 1 region preset

### A.2 Magnet 2
- Formula: `z = ((z³ + 3(c-1)z + (c-1)(c-2)) / (3z² + 3(c-2)z + c² - 3c + 3))²`
- Same risk as A.1; bigger expression
- Deliver: `MagnetTwoKernel.cs`, Type combo, Math tab

### A.3 Halley basins
- Iterate Halley's method on user polynomial (default `z³ - 1`)
- Step: `z -= 2f·f' / (2f'² - f·f'')`
- Reuses Newton's `NewtonPolyCoeffs` param + derivative cache
- Deliver: `HalleyKernel.cs`, polynomial param picker shared with Newton, Math tab

### A.4 Secant basins
- Two-point recurrence `z_{n+1} = z_n - f(z_n)·(z_n - z_{n-1}) / (f(z_n) - f(z_{n-1}))`
- Needs prev-z slot — model after `PhoenixKernel` which already keeps prev
- Deliver: `SecantKernel.cs`, Math tab

### A.5 Glynn
- Formula: `z = z^1.5 + c` with `c ≈ -0.2`
- Fractional power → use `Complex.Pow` (log/exp)
- Deliver: `GlynnKernel.cs`, default region at canonical Glynn coords

### A.6 Spider
- Two-state recurrence: `z = z² + c; c = c/2 + z`
- c mutates per iteration — `IFractalKernel.Step` needs `ref cx, ref cy` overload, OR new sub-interface `IMutatingKernel`
- Architecture cost: small — add overload; existing kernels ignore
- Deliver: `SpiderKernel.cs`, kernel-interface extension, Math tab

### A.7 Logistic / Feigenbaum bifurcation
- Not escape-time. 1D map `x_{n+1} = r·x_n·(1 - x_n)`
- Render: x-axis = r (3.0 – 4.0), y-axis = settled-x density
- Needs own renderer path — histogram accumulator like Buddhabrot
- Reuse: Buddhabrot's accumulation buffer + log-density tone-map
- Deliver: `LogisticRenderer.cs` (sits alongside `BuddhabrotRenderer`), Type combo, Math tab
- Themes: density-histogram only; gate interior themes off

**Phase A exit:** 7 new 2D families, no new pipeline beyond kernel-interface mutating-c overload + bifurcation density renderer.

---

## Phase B — 3D escape-time, new fold families (Mandelbox + KIFS)

Reuse `UserBulb` raymarcher (DE-based sphere tracing, normal estimation, lighting). Add new fold-style DE functions.

### B.1 Mandelbox
- Box-fold + sphere-fold + scale: `z = scale · sphereFold(boxFold(z)) + c`
- Tunables: scale (default 2.0; ≈ -1.5 / 2.0 / 3.0 are classics), fixedRadius (1), minRadius (0.5)
- DE: `length(z) / |dz|` with dz tracked through folds
- ILGPU: straightforward — folds are conditional negate/scale, no transcendentals
- Deliver: `MandelboxKernel.cs` (3D), MandelboxParams struct, 4 region presets (-1.5, 2, 3, +negative scale), Math tab
- Risk: scale near critical values blows up — clamp iters, raise bailout

### B.2 Kaleidoscopic IFS (KIFS) — Menger sponge + Sierpinski tetra
- Repeated fold + scale-from-pivot. Menger: 3 axis-folds + scale-3 from (1,1,1). Sierp tetra: 4 vertex reflections + scale-2.
- DE: `(length(z) - constant) / scale^n`
- Tunables: iteration count, scale, pivot, rotation matrix
- Two presets share single `KifsKernel` parameterized by fold table
- Deliver: `KifsKernel.cs`, 2 built-in fold-table presets (Menger / Sierp), Params dialog tab, Math tab

### B.3 Hybrid 3D (Mandelbulber-style chain)
- Reuse User Bulb's **chain editor** — already lets user stack multiple Step functions
- Action: register Mandelbox-fold + KIFS-fold + Mandelbulb-power as chain primitives in `UserBulbChainPrimitives.cs`
- Users compose hybrids without code; promoted entries become Type-combo fractals
- Deliver: 3 chain primitives + 2 worked-example chains shipped as built-ins
- Low risk — leverages existing system

### B.4 Kleinian / inversive limit sets
- Iterated Möbius transforms (sphere inversions) in 3D
- Complex math — defer behind B.1–B.3
- Deliver: `KleinianKernel.cs`, params = inversion-sphere list (radius + center per sphere), 1 preset (Apollonian-style packing)

**Phase B exit:** 4 new 3D families + Mandelbulber-class composability.

---

## Phase C — 4D / hypercomplex

New algebra layer. Mandelbulb's triplex won't suffice — need true 4D number types.

### C.1 Quaternion Julia
- `q_{n+1} = q_n² + c`, q ∈ ℍ
- Reuse existing `Quat` from User Bulb 4D mode
- Render: raymarch a 3D slice (fix one quaternion component as "slice w")
- UI: Params dialog adds slice-w slider — live re-render
- Deliver: `QuatJuliaKernel.cs`, slice-w param, 3 classic c presets (-0.2+0.4i+0.4j+0.4k, etc.), Math tab

### C.2 Quaternion Mandelbrot
- Same algebra, vary c per pixel
- Deliver: `QuatMandelbrotKernel.cs`, slice picker, Math tab
- Share raymarch infra with C.1

### C.3 Bicomplex / hypercomplex Mandelbrot
- Tessarine algebra (commutative, zero-divisors present)
- New `Bicomplex` struct in `Abstractions/Numerics/`
- Deliver: `Bicomplex.cs`, `BicomplexMandelbrotKernel.cs`, slice picker, Math tab
- Lower priority — visually similar to quat in many slices

**Phase C exit:** True 4D fractals with interactive slice control.

---

## Phase D — Geometric / stochastic (own renderers)

These don't fit escape-time. Each needs dedicated renderer.

### D.1 Apollonian gasket
- Circle packing via Descartes Circle Theorem
- Recursive: from 3 mutually tangent circles, generate 4th, recurse
- Renderer: tree traversal until circle radius < pixel
- Deliver: `ApollonianRenderer.cs`, depth-limit param, color-by-depth, 2D only, Math tab

### D.2 DLA (diffusion-limited aggregation)
- Stochastic. Seed pixel; random-walk particles stick on contact
- Renderer: simulate N particles, accumulate hit map
- Determinism: seedable PRNG; cache result per (seed, N, region) — slow to compute
- Deliver: `DlaRenderer.cs`, particle-count + seed params, color-by-arrival-time, Math tab
- Risk: zoom non-trivial — pre-render at fixed grid; pan/zoom = blit cached buffer until re-sim
- Don't support: pan/zoom-aware live re-sim (deferred)

### D.3 Plasma / diamond-square
- Procedural noise terrain — not strictly fractal but renders as one
- Renderer: square-step + diamond-step over 2D grid, recursive midpoint displacement
- Deliver: `PlasmaRenderer.cs`, roughness + seed params, 2D, Math tab
- Themes: full gradient palette works directly

### D.4 Flame fractals (Apophysis-style)
- IFS + per-map non-linear "variation" + log-density tone-map
- Reuse existing IFS pipeline; extend `AffineMap` → `FlameMap` adding variation enum (linear, sinusoidal, spherical, swirl, horseshoe, polar, handkerchief, heart, disc, spiral, hyperbolic, diamond, ex, julia, bent, waves, fisheye, popcorn, exponential, power, cosine, rings, fan, blob, pdj, fan2, rings2, eyefish, bubble, cylinder, perspective, noise, juliaN, juliascope, blur, gaussian, radial-blur, pie, ngon, curl, rectangles, arch, tangent, square, rays, blade, secant, twintrian, cross — Apophysis stock set, 49 variations)
- Initial cut: 8 most-used variations (linear, sinusoidal, spherical, swirl, polar, heart, disc, julia)
- Renderer: chaos game + log-density accumulation + gamma tone-map
- Deliver: `FlameRenderer.cs`, `FlameMap.cs`, variation enum + lookup, 6 built-in flame presets, Math tab, theme integration via density palette
- Big phase. Plan as 3 slices: core chaos-game → variation library → tone-map + palette.

### D.5 Pythagoras tree, dragon curve, Hilbert curve
- All L-System representable. **Confirm before building dedicated path** — likely just add new L-System presets.
- Deliver: 5 new L-System presets in built-in library, no code change beyond preset JSON

**Phase D exit:** Stochastic + geometric coverage; flame fractals = biggest user-visible feature.

---

## Implementation order (recommendation)

| Order | Phase | Why this slot |
|---|---|---|
| 1 | A.1 + A.2 Magnet 1/2 | Single-file kernels, classic missing pair |
| 2 | A.7 Logistic | Reuses Buddhabrot accumulator, big visual variety |
| 3 | A.3–A.6 Halley/Secant/Glynn/Spider | Batch the small kernels |
| 4 | B.1 Mandelbox | Highest user-impact 3D add; well-trodden |
| 5 | B.3 Hybrid chain primitives | Leverages B.1 + existing User Bulb |
| 6 | B.2 KIFS | Menger / Sierp recognizable shapes |
| 7 | C.1 Quat Julia | Reuses Quat from User Bulb 4D |
| 8 | D.5 L-System presets | Cheap shipping win |
| 9 | D.3 Plasma | Simple renderer, broad appeal |
| 10 | D.4 Flame fractals | Largest phase; 3 sub-slices |
| 11 | C.2 Quat Mandelbrot | Pairs with C.1 |
| 12 | D.1 Apollonian | Niche but iconic |
| 13 | B.4 Kleinian | Complex; do after flame infra settles |
| 14 | C.3 Bicomplex | Lowest visual differentiation vs C.1/C.2 |
| 15 | D.2 DLA | Stochastic — caching complexity |

---

## Cross-cutting work

### Theme compatibility matrix
Each new family needs entry in theme-gating table. Default rules:
- Smooth-iter + distance themes: enable on all escape-time families (A.1–A.6, B.1–B.4, C.1–C.3)
- Interior cycle themes: Mandelbrot-only (existing rule, no change)
- Density-histogram themes: enable on A.7, D.2, D.4
- 3D Phong / PBR themes: enable on B.* + C.* only when DE + normal estimation present

### Help → Mathematics tabs
Each new family = one new sub-tab. Current count: 18. Post-roadmap: ~33. Reorder Math tab as 2-level (Family group → specific tab) once > 25.

### Region presets
Each family ships ≥ 1 built-in region. Total new built-ins: ~25.

### CalcGen reach
Phase A families that fit `f(z, c)` shape (A.1, A.2, A.5, A.6) should auto-flow through CalcGen 5-path generator. Verify per-kernel; bailout-radius + cardioid-skip differ.

### Persistence
No new JSON files. Existing `regions.json` + `colorthemes.json` cover everything.

### CLI / batch
Each FractalType added to enum becomes immediately addressable from `--batch --fractal=...`. Audit batch help text after each phase.

### Server protocol
User-code families stay blocked. New built-ins (A–D) are server-safe — whitelist in `Server/FractalGuard.cs` per phase.

---

## Out of scope (do not implement)

- Mandelbar — = Tricorn, already shipped
- Cubic / Quartic Mandelbrot — covered by Multibrot exponent param
- Strange attractor variants (Lorenz, Rossler, Clifford, De Jong, Pickover) — already shipped under StrangeAttractor
- Buddhabrot variants (Nebulabrot, anti-Buddhabrot) — extend existing Buddhabrot, not new family
- Pickover stalks, orbit traps — coloring, not fractals; live in theme system
- Cantor set, Koch — too trivial as standalone; L-System covers

---

## Risk register

| Risk | Mitigation |
|---|---|
| Magnet pole instability | Denominator-magnitude clamp; bailout 10² not 4 |
| Mandelbox scale critical points | Iter clamp + diagnostic overlay in dev |
| Flame fractal scope creep | 3-slice plan: core / variations / tone-map. Ship slice 1 even if 2/3 deferred. |
| DLA pan/zoom UX | Render to fixed grid; live re-sim deferred |
| 4D slice UI complexity | Single slice-w slider in Params dialog; defer arbitrary-plane slicing |
| Theme combinatorial explosion | Auto-gate via family-tag set on each theme + family |
| Math help tab proliferation | Two-level grouping after count > 25 |

---

## Tracking

- Branch per phase: `feature/fractals-phase-a`, etc.
- Each family = own slice commit within phase branch (mirrors current `Cross-platform: Phase X.2 Slice N.M` convention)
- FEATURES.md "20+ fractal families" line updates per phase
- README badge counter for fractal count

---

## Completion log

Implementation landed on `feature/cross-platform-full` rather than the
per-phase branches originally planned — all 15 slices in the recommendation
table merged sequentially into one branch.

| Slice | Status | Commit | Notes |
|---|---|---|---|
| A.1 Magnet 1 | ✅ | (Phase A bundle) | |
| A.2 Magnet 2 | ✅ | (Phase A bundle) | |
| A.3 Halley | ✅ | `44ff899` | |
| A.4 Secant | ✅ | `f2a26d2` | |
| A.5 Glynn | ✅ | (Phase A bundle) | |
| A.6 Spider | ✅ | `9b87e83` | Adds `IMutatingKernel` overload for ref-c kernels |
| A.7 Logistic | ✅ | (Phase A bundle) | Density-histogram renderer alongside Buddhabrot |
| B.1 Mandelbox | ✅ | `43d57e7` | Includes AnyCPU standardisation + 3D input parity |
| B.2 KIFS | ✅ | `0f50a62` | Menger + Sierpinski folds; two follow-up fixes (`d91a6ab`, `30a77d9`) |
| B.3 Hybrid chain | ✅ | `96c310f` | UserBulb chain primitives |
| B.4 Kleinian | ✅ | `4316932` | Tetrahedral 4-sphere preset only — see deferred section |
| C.1 Quat Julia | ✅ | `5b32fd2` | |
| C.2 Quat Mandelbrot | ✅ | `89beab5` | |
| C.3 Bicomplex | ✅ | `2532e3d` | Tessarine algebra inlined in calculator (no separate `Bicomplex` struct) |
| D.1 Apollonian | ✅ | `d1261a9` | |
| D.2 DLA | ✅ | `cb81f1b` | Fixed-grid render only — see deferred section |
| D.3 Plasma | ✅ | `7ad0e32` | |
| D.4 Flame | ✅ | 5 commits (`0923d7d` → `464d3e7`) | Variation library + tone-map + per-map palette + post-affine + presets |
| D.5 L-System presets | ✅ | `7c63333` | |

Total: **19 new fractal types** landed since the roadmap was written.

---

## Deferred work (off-roadmap, lower priority)

These items are out of scope for the now-complete recommendation table but
remain useful follow-ups. Capture here so a later session can pick them up
without re-deriving context.

### Cross-cutting (per "Cross-cutting work" section above)

- **Theme compatibility matrix audit.** New families (A.1–A.7, B.1–B.4,
  C.1–C.3, D.1, D.2) were added without a sweep of the theme-gating table.
  Verify that each family's theme set matches the defaults in the existing
  rules (smooth-iter + distance on escape-time; density on A.7/D.2/D.4;
  3D Phong on B.*/C.* with DE). Concretely: enumerate `IColorMap` entries,
  walk each new `FractalType`, check the family-tag set actually gates
  correctly in the theme picker, and note any rules that need extending.
- **Region preset coverage audit.** Roadmap target was ≥ 1 built-in region
  per family. Most slices ship 1–2; spot-check the count and add missing
  presets for any family that's bare (visual: open the Regions panel,
  filter by family). Total built-in regions added this round: ~25.
- **Math help tab two-level grouping.** Roadmap called for two-level grouping
  once count > 25. Current count is ~34 sub-tabs in `HostHelpContentProvider`
  + matching WinForms FloatingHelp. Group by family (escape-time / fold /
  4D / geometric / stochastic) so the tab strip stops scrolling off the
  edge of typical window widths.
- **FEATURES.md "20+ fractal families" line.** Bump to ~38 (existing 20 +
  19 new). Same for the README badge counter.
- **CalcGen reach verification.** A.1 (Magnet 1), A.2 (Magnet 2), A.5
  (Glynn), A.6 (Spider) should auto-flow through the CalcGen 5-path
  generator if their kernel shape matches `f(z, c)`. Verify per kernel:
  bailout radius differs (10² for Magnet vs the usual 4); cardioid-skip
  likely off for these. If a path is missing, decide whether to add the
  generated calculator or document the family as scalar-only.

### Per-family deferred follow-ups

- **B.4 Kleinian.**
  - User-editable inversion-sphere list (currently hard-coded
    tetrahedral 4-sphere).
  - Alternate Schottky configurations: necklace (Indra's Pearls "tree" /
    "limit-circle" presets), Klein-bottle 6-sphere, Apollonian-extrusion
    (gasket-style limit set built from a circle inversion stack).
  - Full Möbius-group composition path so users can compose arbitrary
    inversion+rotation sequences and view the limit set of the resulting
    group, not just the inversion-only path the current calculator runs.
  - True analytic DE (vs the inversion-scale heuristic) for crisper
    cusps under deep zoom.

- **D.2 DLA.**
  - Cached-blit pan/zoom: keep the produced grid, recompose the colour
    buffer when only `CenterX/Y/Zoom` change so the user can frame the
    aggregate without paying the simulation cost again.
  - Pan/zoom-aware live re-sim (continuous-domain DLA): expensive,
    deferred indefinitely — only worth doing if the cached-blit path
    proves insufficient for the user.
  - Multi-seed DLA: place several seed cells (e.g. tetrahedral on the
    grid corners) and let the aggregates collide. Trivial extension.
  - Sticky-coefficient mode: probability < 1 of sticking on contact,
    walks past attached cells. Lower stickiness → bulkier aggregate
    (less branched). Worthwhile parameter for visual variety.

- **C.3 Bicomplex.** Visual differentiation against quaternion-Mandelbrot
  is low on most slices (roadmap noted this up front). If a clearer
  visual identity is needed, expose the second slice axis (currently
  hard-pinned to the k component) so the user can pick which 4D axis
  routes to the slice constant. Also: split-complex / coquaternion
  variant (k² = +1, ij = -ji = k — non-commutative) reuses the same
  raymarcher with a swapped product table.

- **B.2 KIFS.** Only Menger and Sierpinski fold tables ship. Mandelbox-
  with-rotation, Octahedron, Dodecahedron folds all fit the same
  `KifsFoldKind` enum. Each is one struct field + one switch arm.

- **D.4 Flame.** Eight variations of the Apophysis stock 49 ship.
  Adding more is mechanical (extend `FlameVariation` + `ApplyVariation`).
  Slice 4 of the originally-planned 3 would add the next 8–16 most-used
  variations and ship them as preset library updates.

- **D.1 Apollonian.** The (−1, 2, 2, 3) seed packs the curvilinear
  cusps between the seed circles but does NOT generate circles inside
  the seed circles themselves — that is a property of the gasket
  definition, not an algorithm bug. If a "filled" rendering (sub-gaskets
  inside each disk) is desired, a separate seeding pass per disk would
  reproduce the integral packing recursively. Likely not worth the
  complexity vs the canonical gasket image users expect.

- **D.5 L-System.** Five new presets shipped (Pythagoras tree, Dragon,
  Hilbert, etc.). The roadmap's full preset list called for ~10. Adding
  more is preset-JSON only, no code.

### Slices intentionally NOT done (per "Out of scope")

These were ruled out in the original roadmap and remain so:

- Mandelbar (= Tricorn, already shipped)
- Cubic / Quartic Mandelbrot (covered by Multibrot exponent param)
- Strange-attractor variants (already shipped under StrangeAttractor)
- Buddhabrot variants (extend existing Buddhabrot, not new family)
- Pickover stalks, orbit traps (coloring, not fractals; live in theme)
- Cantor set, Koch curve standalone (L-System covers)

### Operational follow-ups (post-implementation)

- **No allowlist failure tests for the new types yet.** All 19 new types
  pass `AllowedTypes_AreAllowed` but there is no negative test for any
  new type — the negative path only covers `UserEquation`/`Sandbox`/
  `UserBulb`. Probably fine because the allowlist is exclusion-based,
  but a single "negative test for a non-existent fractal name" would
  catch regressions where someone accidentally blocks a built-in.
- **No headless visual-regression baseline.** The smoke renders run in
  CI as build-only; a per-type golden PNG (hashed pixel-count by colour
  bucket) would catch silent visual regressions. Worth doing once the
  test infrastructure for image-fixture comparisons lands.
- **CLAUDE.md UI-policy bypass risk.** `FractalParamsView.axaml` has
  grown to ~600 lines of per-type IsVisible-gated panels. A future
  refactor could extract per-type user controls into separate
  `*ParamsControl.axaml` files. Not urgent — current layout works, just
  reads dense.
