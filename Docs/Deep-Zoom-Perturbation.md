# Deep-Zoom Perturbation & Navigation — Model, Tools, Findings

Single reference for how deep-zoom Mandelbrot rendering and interactive
navigation work here, why they behave the way they do at extreme depth, and the
headless tools that measure it. **Read this before touching the deep-zoom render
or input path — it exists so the multi-session investigation behind it never has
to be repeated.** Related plan items: SM-2, SM-5..SM-11 in `Open-Work-Plan.md`.
Memory: `project_detail_depth_limit`, `project_viewcamera_input`,
`project_wave214_qdfloor`.

## 1. Coordinate & precision tiers

- View centre is carried at **octuple-double (OD, ~124 digits)** ALWAYS, in
  `FractalViewState` (16 limbs) via `DeepComplex` / `ViewCamera`. Never
  reintroduce a plain-double centre with a promotion threshold — that was the
  historic "anchor drift" bug (frozen ~1e-16 world error blooming ∝ zoom).
  See `project_viewcamera_input`.
- Precision types: `DD` (Hi/Lo, ~31 digits), `QD` (X0..X3, ~62), `OD` (X0..X7,
  ~124), all in `Abstractions/Math`. `FromCenterOffset(centre, pixelOffset,
  scale)` builds a pixel's coordinate; **OD's must use the OD+double full-cascade
  add** (`(centre + offHi) + offLo`), not OD+OD — the sloppy OD+OD add loses a
  deep offset past ~1e64 (SM-6).
- Thresholds (`MandelbrotCalculator`): `QDZoomThreshold=1e25` (QD reference
  orbit), `ODZoomThreshold=1e50` (OD reference orbit). One scale formula
  everywhere: `scale = 3.5 / (max(pxW, pxH) · zoom)`. Input and render MUST agree
  on it (`ViewCamera.PlaneExtent = 3.5`).

## 2. How a deep frame is rendered (perturbation)

One **reference orbit** is computed at the view centre in QD/OD and stored to 8
limbs per iteration (`_refZr/_refZrLo/..X7`). Each pixel iterates only its
**delta** δ = z − Z (perturbation): `δ_{n+1} = 2·Z_n·δ_n + δ_n² + dc`, where
`dc = pixelOffset·scale`. The full value for the escape test is `z = Z_n + δ_n`.

- **δ stays in double.** δ is a small *deviation*, so double's exponent range
  handles it at any depth; only the reference and centre need many digits.
- **Rebasing (SM-2, `ComputePixelPTRebased`, default on).** When δ grows past the
  full value (`|z| < |δ|`) or the reference is exhausted, restart the reference
  (`Z[0]=0`, `δ := z`, `m := 0`). Keeps the δ-chain glitch-free to arbitrary
  depth without the slow per-pixel QD/OD fallback. The SIMD PT path
  (`ComputeRowPT4/8`) carries most pixels; `ComputePixelPTRebased` is the scalar
  fallback for lanes that glitch (typically a few % at extreme zoom).
- **SA (series approximation) + BLA** skip early/at-scale iterations. Toggle off
  with `DisableAcceleration` / `DisableSeriesApproximation` for A/B.

## 3. The detail-depth floor (SM-7) — NOT a bug

Perturbation resolves a pixel only while its offset δ (∝ 1/zoom), amplified by
`∏|2·Zₙ|` over the reference orbit, reaches O(1). If the centre's orbit **escapes
at iteration N**, amplification is finite — `Σ log₁₀|2·Zₙ|` (frozen at |Z|>2)
decades — so **zoom beyond ~10^that collapses the whole viewport to one escape
value (flat frame)**. This is a property of the *location*, not precision: to go
deeper, recentre on a point whose orbit stays bounded longer.

- Exposed as `MandelbrotCalculator.MaxUsefulZoomLog10` (+∞ if the orbit stays
  bounded to maxIter), computed free in every reference-orbit build.
- Surfaced in the **perf-HUD render-context block** (`ShowPerfHud`) as
  `max-detail zoom: ~1eNN` plus a yellow warning when the live zoom exceeds it.
  NOT on the status bar (a long wrapping string there bounced the panel — SM-8).
- If the user reports "controls break past 1eNN," FIRST check `zoom` vs
  `maxUseful` in the overlay. `zoom > maxUseful` ⇒ flat dead-zone, expected.

## 4. Navigation (input) is provably exact — do not re-investigate

All 2D pan / zoom / focus / box-zoom goes through `ViewCamera` + `DeepComplex`
(OD centre). `--inputprobe` (headless, to 1e70) and the input-math check in
`--navrepro` show the controller places the new centre to **9.5e-15 px** of the
ideal OD value at 4.65e64 — i.e. machine-exact, path-independent. **The input
layer is not the source of any deep-zoom navigation complaint.** Native input
(`NativeMouseForwarder`) feeds device-px coords AND device-px client dims
(`GetClientSize`), and the render buffer is device-px, so there is no HiDPI
scale mismatch (it cancels).

## 5. The deep-zoom navigation symptom (SM-11) — reference-dependent RENDER

**Symptom:** past ~1e63–1e64, double-click / outline-zoom "get close but not
exact" and click-drag pan makes the image "jump around" as the mouse moves, even
though the grabbed point stays roughly put.

**Root cause (measured, not theorised):** the render is **reference-dependent**.
Recentring (any pan/focus/box-zoom) recomputes the reference orbit; the *same*
world region then renders with slightly different iteration counts, which the
rainbow palette amplifies into apparent position drift + live-drag shimmer. The
pan preview is `Trigger(progressive)` = ¼-res **full-iteration** (not
under-iterated), so the jitter is this same effect, not preview coarseness.

**Evidence (`--navrepro` on the user's working 1.32701e63 vs broken 4.65087e64
coords, both detailed, below their 1e67 floor):**
- input-math error: 9.5e-15 px (exact) at both.
- rendered focus error (patch-match): ~2 px at 1.32e63, ~16 px at 4.65e64 —
  grows smoothly with zoom.
- best-alignment SAD ≈ 700–890 (~18 iteration-counts/pixel) — the two frames
  genuinely differ for the same world region.

**What does NOT fix it (all tested, all byte-identical / unchanged):**
- Rebasing on/off; SA/BLA on/off (`--navrepro ... norebase|acceloff|saoff`).
- **DD reference + DD δ-chain** in the rebased loop, even forced across ALL
  pixels (`--navrepro ... scalar ddref`, `rebasedPx=942079/942080`): focus-err
  stayed exactly 16 px. **So the divergence is NOT double-rounding of the
  reference orbit or the δ-chain in the 16→31-digit range.** This matches the
  earlier "DD δ byte-identical at 1e47" result — DD is not the lever.

**Open hypotheses (for whoever picks this up):**
1. **Precision floor higher than DD.** At 4.65e64 a centre-invariant δ needs
   ~65 significant digits; DD (31) is below that, QD (62) borderline, OD (124)
   sufficient. DD being *byte-identical* (not merely insufficient) argues against
   this, but a QD-δ variant is the clean next experiment — mirror
   `ComputePixelPTRebasedDD` with QD and A/B via `--navrepro`.
2. **Rebasing-decision divergence.** Two references rebase at different
   iterations (`|z| < |δ|` fires at different m), accumulating different rounding
   paths. Test: log rebase counts per pixel for the two centres.
3. **Measurement caveat.** In the `--navrepro` focus test, frame B is centred
   exactly on the clicked world point, making it frame B's *reference* pixel
   (δ=0, escapes at the reference length) while in frame A it is a perturbed
   pixel — so part of the 16 px may be a compare-against-reference artefact, not
   user-visible drift. Before investing in a fix, tighten the test: focus to a
   point OFFSET from the clicked feature so neither frame has it as centre.

## 6. Practical mitigations (independent of the precision question)

- **Reference-orbit recycling during a drag (SM-11b).** `AllowRefOrbitRecycle`
  (implemented, default off) keeps ONE reference for the whole pan, so every
  preview frame shares it → no shimmer mid-drag. Best UX win for the "pan jumps"
  complaint; residual on the committed frame only.
- The per-recenter difference is ~0.03 % of the iteration count — cosmetic
  palette shimmer, not a positional fault. A less iteration-sensitive palette or
  slight smoothing hides it.

## 7. Diagnostic tools (headless, in `Program.cs`)

Run `dotnet FracturingFog.dll <flag>`; each writes a `.out` next to the exe.

| Flag | Measures |
|------|----------|
| `--inputprobe` | Controller anchor drift vs OD truth, wheel/click/pan, to 1e70. Expect 0.00 px. |
| `--focusprobe [dim]` | End-to-end double-click focus px error + frame richness + `MaxUsefulZoomLog10` + ref-orbit escape, over a zoom sweep. |
| `--navrepro [file]` | Reproduce a USER view from a coordinate file (`Docs/Nav-Repro-Template.txt`): full-limb `cx/cy`, `zoom`, `dim`, `click`. Reports input-math error, rendered focus-err, SAD(0,0) vs SAD(min), rebased-pixel count, `maxUseful`. Path toggles: `norebase acceloff saoff ddref scalar`. |
| `--qdfloorsweep` | QD/OD coordinate-separation floor (distinct per-pixel coords vs zoom). |
| `--rebaseprobe` | Rebasing vs QD parity + speedup. |

**To capture a repro from a user:** overlay `px WxH`, menu full-limb CX/CY,
zoom, and roughly where they clicked → `Docs/Nav-Repro-Template.txt` →
`--navrepro`. The overlay's `limbs X:n/8 Y:n/8` shows centre precision at a
glance (a truncation bug would drop `n`).

## 8. Diagnostic toggles on `MandelbrotCalculator` (all default off/safe)

- `AllowPtRebasing` (default **on**) — Zhuoran rebasing; "Bypass Rebasing" UI.
- `UseDdRebaseReference` — DD reference in the rebased loop (SM-11a, tested
  insufficient; kept for the QD follow-up).
- `ForceScalarPtPath` — bypass SIMD so all pixels hit `ComputePixelPTRebased`
  (diagnostic; slow).
- `DisableAcceleration` / `DisableSeriesApproximation` / `DisableDdBla`.

## 9. Rules of thumb

1. Deep-zoom "controls broken"? Check overlay `zoom` vs `maxUseful` first
   (dead-zone), then run `--navrepro` on the coordinate. Input is exact — look at
   the render, not the input handlers.
2. Never re-add per-tier branches to input handlers or a plain-double centre.
3. New precision tier / zoom backend → extend `DeepComplex` only.
4. Keep `ComputePixelPTRebased` and `ComputePixelPTRebasedDD` in sync.
5. Don't put long/variable status text on the status bar (panel resize bounce).
