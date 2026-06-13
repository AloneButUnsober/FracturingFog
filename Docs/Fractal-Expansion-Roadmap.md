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
