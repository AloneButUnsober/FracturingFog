# Coloring: iter + final-z combination themes (Design Plan)

Tracking issue: [#69](https://github.com/AloneButUnsober/FracturingFog/issues/69)
Slices: #358 (P1 base + iter+real / iter+imag), #359 (P2 ratio + 4-way composite)

> Status legend: **DONE** already shipped · **TODO** this plan.

## 1. Background — what #69 asked for

Issue #69 requested eight coloring types. Audit of the codebase shows **four
already ship**; only the *iter + final-z arithmetic combination* family is
missing. This plan covers only the missing four.

| #69 item | Status | Location |
|---|---|---|
| Binary Decomposition | **DONE** | `Engine/Models/ColorSchemes/BinaryDecompositionThemes.cs` |
| Biomorphs | **DONE** | `OrbitTrapBiomorphMap`, `Engine/Models/ColorSchemes/OrbitTrapThemes.cs` |
| Potential | **DONE** | `Engine/Models/ColorSchemes/PotentialThemes.cs` (Douady–Hubbard) |
| Color Decomposition | **DONE** | `Engine/Models/ColorSchemes/ArgumentDecompositionThemes.cs` |
| iter + real | **TODO** | this plan (slice #71) |
| iter + imag | **TODO** | this plan (slice #71) |
| iter + real/imag | **TODO** | this plan (slice #72) |
| iter + real + imag + real/imag | **TODO** | this plan (slice #72) |

These are the Ultra Fractal "iter + real", "iter + imag", "iter + real/imag",
"real+imag+iter" coloring family: the smooth iteration count blended with the
real/imaginary parts of the escape value `z` (and their ratio).

## 2. Why this is cheap — data already plumbed

No new calculator path is required. The escape value `z` and derivative
`dz/dc` already reach every color map through the nine-parameter
`IColorMap.Map` overload:

```csharp
// Engine/Interefaces/IColorMap.cs
int Map(float smooth, float distance, int iterations, float nx, float ny,
        float finalZr, float finalZi, float dzdcR, float dzdcI)
```

`finalZr` / `finalZi` are the real/imaginary parts of `z` at the escape
iteration. They are populated on **both** render paths:

- Scalar / SIMD fast path — `MandelbrotCalculator.cs:1719`
- HP / perturbation deep-zoom path — `FillAuxAndColorHP`, `MandelbrotCalculator.cs:2211`

Because the perturbation path reconstructs full `z = reference + delta` and
writes true `finalZr/finalZi`, these themes are **valid at all zoom depths** —
no `MaxRecommendedZoom` cap (mirrors `BinaryDecomposition*`, which sets none).

For in-set pixels all four extra parameters are `0` → paint `InSetColor`.

## 3. Normalization — the one real design problem

After escape, `z` is unbounded: `|z|` ranges from the bailout radius up to
roughly `bailout²` for a step that overshoots. Raw `finalZr` / `finalZi` /
`finalZr/finalZi` therefore cannot index a `[0,1)` gradient directly, and the
ratio has a pole where `finalZi → 0`.

Compression rules (shared helper, applied consistently across all four themes):

- **real channel** `R = finalZr`: compress with sign-preserving arctangent
  `r01 = 0.5 + atan(R) / π` → `[0,1)`. Bounded, smooth, sign-symmetric.
- **imag channel** `I = finalZi`: same, `i01 = 0.5 + atan(I) / π`.
- **ratio channel** `finalZr / finalZi`: use the *angle*, not the raw quotient,
  to dodge the pole: `q01 = 0.5 + atan2(finalZr, finalZi) / (2π)` → `[0,1)`.
  (atan2 encodes the ratio continuously with no division-by-zero.)
- **iteration channel**: existing smooth iteration count, cyclically mapped
  `t = frac(smooth / period)` with a tunable `period` (default `MaxIterations`,
  exposed per the tunable-params convention).

### Combination model

Each theme forms a single scalar index `u ∈ [0,1)` then samples the theme
gradient (reuse `GradientColorMap` machinery where possible):

| Theme | Index `u` |
|---|---|
| iter + real | `frac(t + wR · r01)` |
| iter + imag | `frac(t + wI · i01)` |
| iter + real/imag | `frac(t + wQ · q01)` |
| iter + real + imag + real/imag | `frac(t + wR·r01 + wI·i01 + wQ·q01)` |

Weights `wR, wI, wQ` are tunable `FractalParameters` fields (default `1.0`),
following the tunable-params preference (expose hardcoded constants as params +
`FractalParamsView` controls). `period` likewise tunable.

Rationale for additive-frac over multiplicative: keeps the iteration bands
readable (the fractal's structure stays legible) while the final-z channel adds
the cross-hatched / marbled secondary texture that is the whole point of the UF
family.

## 4. Class design

New file `Engine/Models/ColorSchemes/IterFinalZThemes.cs`:

```
IterFinalZBaseMap : GradientColorMap          // shared compression + frac index
 ├─ IterPlusRealMap        "Iter + Real"
 ├─ IterPlusImagMap        "Iter + Imag"
 ├─ IterPlusRatioMap       "Iter + Real/Imag"
 └─ IterRealImagRatioMap   "Iter + Real + Imag + Ratio"
```

- Base holds the `atan`/`atan2` helpers + `Combine(t, r01, i01, q01)` and the
  weight/period fields. Subclasses supply only which channels feed `Combine`.
- Override the 9-param `Map`; the 3/5-param overloads fall through to base
  (they lack final-z, so degrade to pure iteration — acceptable fallback).
- `Features = UsesSmooth | UsesFinalZ | GradientBased`.
- `Category => "Decomposition"` (sits beside Binary/Argument decomposition).
- No `MaxRecommendedZoom` override (valid at all depths — see §2).
- In-set (`finalZr==0 && finalZi==0 && iterations>=MaxIterations`) → `InSetColor`.

### Registration

Add the four instances to the registry list in
`Engine/Models/ColorPalettes.cs` (near the decomposition block). Reflection
picks up static `Name`/`Category`/`Description`/`Features`. No UI code change —
Color Theme Editor + slideshow enumerate the registry.

### Legacy names

ASCII-only names already (no Unicode). No `LegacyNameAliases` entry needed
unless a name is later renamed.

## 5. Tests (`Server.Tests`)

Per slice:

1. **Registry presence** — each new `Name` resolves and is enumerable.
2. **In-set sentinel** — `Map(0,0,maxIter,0,0,0,0,0,0) == InSetColor`.
3. **Bounded index** — fuzz `finalZr/finalZi` across `[-1e6, 1e6]` incl.
   `finalZi == 0`; assert no NaN/Inf and output alpha `== 0xFF`.
4. **Ratio pole** — `finalZi == 0, finalZr != 0` produces a finite color
   (atan2 path, not a divide).
5. **Determinism** — same inputs → same ARGB.
6. **Deep-zoom parity smoke** — render a tile via scalar and HP paths at a
   fixed deep center; assert both populate finalZ and produce non-trivial
   (non-uniform) output (guards against a path zeroing the channel).

Target: green in the existing suite, no new zoom cap regressions.

## 6. Slice plan (issues)

- **#358 — P1**: `IterFinalZBaseMap` + `IterPlusRealMap` + `IterPlusImagMap`,
  registration, tunable weights/period, tests 1–5. Ships two usable themes and
  the shared base.
- **#359 — P2** (depends on #358): `IterPlusRatioMap` +
  `IterRealImagRatioMap` (atan2 ratio channel + 4-way composite), tests + the
  deep-zoom parity smoke (test 6).

Both slices Mandelbrot-family escape-time only (where finalZ is defined). Other
escape-time types inherit automatically if they populate the finalZ buffers;
verify per-type before advertising.

## 7. Out of scope (broader gaps — separate issues, not #69)

Noted during the #69 audit, deliberately **not** in this plan:

- External-angle / Böttcher external-ray coloring (binary expansion of θ).
- Environment / matcap reflection mapping on the DE normal.
- Temporal palette cycling as a first-class coloring mode.

File separately if pursued.
