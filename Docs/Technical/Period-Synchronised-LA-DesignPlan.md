# Period-synchronised linear approximation — Design Plan

**Tracking epic:** [#1623](https://github.com/AloneButUnsober/FracturingFog/issues/1623) (part 2 of
[#1614](https://github.com/AloneButUnsober/FracturingFog/issues/1614)).

**Slices:**
- S1 [#1624](https://github.com/AloneButUnsober/FracturingFog/issues/1624): method spec + proof of concept
- S2 [#1625](https://github.com/AloneButUnsober/FracturingFog/issues/1625): stage table
- S3 [#1626](https://github.com/AloneButUnsober/FracturingFog/issues/1626): scalar evaluator
- S4 [#1627](https://github.com/AloneButUnsober/FracturingFog/issues/1627): AT
- S5 [#1628](https://github.com/AloneButUnsober/FracturingFog/issues/1628): SIMD
- S6 [#1629](https://github.com/AloneButUnsober/FracturingFog/issues/1629): default on + parity
- S7 [#1630](https://github.com/AloneButUnsober/FracturingFog/issues/1630): GPU

The issues are the canonical task list. This doc is the maths and architecture context; link both ways.

**Status: S1 done (2026-11-03); S2 done (#1625).** §2 is the method, derived and checked against Imagina's code;
§4.1 is the proof of concept (`--laprobe`) and the decision to go on with S2. §3 is still a plan
where S1 did not settle it.

---

## 1. Why

rith's FF 0.7.1 benchmark (a fractalforums.org direct message; see the bibliography) timed FF
against Imagina on the fractadyne bench-kit. On high-period deep minibrots FF was about 10× slower.
#1622 removed the image-size-independent part, the reference orbit run to the iteration cap: the
28.5M-iteration scene went from 61 s to 8.3 s at 960×540. What remains is per pixel.

`--deepcostprobe` (i5-13420H, 960×540, after #1622):

| scene | iterations / px | plain steps / 4-px batch | BLA skips / batch | avg skip | pixels |
|---|---|---|---|---|---|
| p953 1.3e53 | 13.6k | 328 | 180 | 69 | 0.46 s |
| p15,248 2.1e57 | 198k | 1,082 | 1,957 | 105 | 1.8 s |
| p121,984 1.7e66 | 1.56M | 1,267 | 2,239 | 707 | 2.3 s |
| p951,094 2.1e68 | 12.7M | 2,563 | 8,319 | 1,561 | 7.8 s |

What the measurements rule out:
- **Scheduling:** 9.4–9.9 of 12 threads are busy (`CPU … threads busy`).
- **The BLA level walk:** a binary search over the levels gave byte-identical output and no speed-up, so it was not kept.
- **Cost per operation:** each step or skip costs about 48 ns of CPU. That is ordinary for a masked AVX step plus a BLA attempt on this hyperthreaded laptop chip.

The cause is the number of operations. The bench minibrots form a chain:

> p953 → p15,248 → p121,984 → p951,094

The reference orbit of the deepest passes near zero at every multiple of each ancestor's period.
BLA's validity radius is ε·|Z_n|, so it collapses at those points, and no skip can cross one. A
pixel that iterates 12.7M times therefore pays thousands of operations: about 1,561 iterations per
skip, against a period of 951,094.

## 2. Method

The method is the one Imagina uses: https://github.com/5E-324/Imagina, AGPL-3.0,
`HInfLAEvaluator.{h,cpp}` (commit `cd046377`, read 2026-11-03). Each rule below is derived from
the perturbation recurrence first. §2.6 then lists where FF's proof of concept (`--laprobe`,
`Probes/LaProbe.cs`) agrees with Imagina's code and where it departs.

### 2.1 Notation

- Reference orbit Z_n at the centre c₀: Z₀ = 0, Z_{n+1} = Z_n² + c₀, for n = 0..R. R is the period p when the reference returns within a pixel of 0 (#1614); otherwise it is the cap or the escape.
- A pixel is c = c₀ + δc with z_n = Z_n + δ_n. Then exactly:

  δ_{n+1} = 2·Z_n·δ_n + δ_n² + δc

- ‖x‖ is the Chebyshev norm max(|Re x|, |Im x|), within √2 of |x|. τ = 2⁻²⁴ is the validity tolerance; d = 2⁻¹⁰ is the period-dip factor.
- **Rebasing (Zhuoran).** Z₀ = 0, so setting δ ← z and n_ref ← 0 is exact whenever it is done. FF already rebases when |z| < |δ| (#1274); the LA path keeps that rule.

### 2.2 One LA step

A step anchored at reference index s covers L iterations. Its **first iteration is kept exact:**

  w = δ_s·(2·Z_s + δ_s),  so  δ_{s+1} = w + δc.

The remaining L − 1 iterations drop the quadratic term, δ_{k+1} ≈ 2·Z_k·δ_k + δc. By induction, for 1 ≤ k ≤ L:

  δ_{s+k} = A_k·w + B_k·δc,  A₁ = B₁ = 1,  A_{k+1} = 2·Z_{s+k}·A_k,  B_{k+1} = 2·Z_{s+k}·B_k + 1.

The step stores (Ref = Z_s, A = A_L, B = B_L, L). Applying it is two complex multiply-adds:

  δ_{s+L} = A·w + B·δc

**Validity.** Dropping δ² at iteration s + k is a relative error |δ_{s+k}| / (2|Z_{s+k}|) in that iteration. Requiring |δ_{s+k}| ≤ τ·|Z_{s+k}| keeps it under τ/2. It suffices that each term of δ_{s+k} stays under τ·|Z_{s+k}| on its own:

  ‖w‖ < Thr = min_k τ·‖Z_{s+k}‖ / ‖A_k‖,  ‖δc‖ < ThrC = min_k τ·‖Z_{s+k}‖ / ‖B_k‖,  over 1 ≤ k < L.

Two checks per step: one on w, which is computed anyway, and one on ‖δc‖, a per-pixel constant. This is the same family as BLA (#1346), which applies δ ← A·δ + B·δc over power-of-two runs with a validity radius on δ.

The difference is the exact first iteration. BLA linearises every iteration, so a run cannot contain a point where |Z_k| is tiny: the validity radius ε·|Z_k| collapses there. That is why no BLA skip crosses an ancestor's near-zero point (§1). An LA step starts *at* such a point and handles it exactly, so the near-zero points are where steps begin, not barriers inside them.

**Error.** The error per iteration is under τ/2, relative, and grows at most linearly over a step's L − 1 linear iterations. Composed steps (§2.3) add their bounds. τ = 2⁻²⁴ is Imagina's value. It is an engineering choice, not a proven bound: the escape count is what matters, and §4 measures it against exact iteration.

### 2.3 Periods, stages and composition

**Period dip.** While a step is being extended, its running threshold τ‖Z_{s+k}‖/‖A_k‖ falls slowly as |A_k| grows. When Z_{s+k} comes close to zero it drops sharply. The step ends just before index s + k if the new threshold is below d = 2⁻¹⁰ times the previous one; the next step then starts at that near-zero point, with its exact first iteration.

**Stage 0** is built over the reference. Steps are cut at each dip, and also at a maximum length:
- the first detected period P₀, if P₀ ≤ 64;
- otherwise round(P₀^(1/r)) with r = round(log₂(P₀)/4), about 16 to 32 iterations.

With no dip at all, one step covers the whole reference when R ≤ 64; otherwise the same cap applies, using R in place of P₀. The cap keeps steps short enough that the stages above form a balanced tree.

**Composition.** Stage k + 1 is built from consecutive stage-k steps a (from s, length L_a), then b (anchored at t = s + L_a). Inside a stage-(k + 1) step, b's exact first iteration is itself linearised, w_b ≈ 2·Z_t·δ_t. Substituting δ_t = A_a·w + B_a·δc:

  A = A_b·2Z_t·A_a,  B = A_b·2Z_t·B_a + B_b,  L = L_a + L_b

Validity adds three bounds to a's:
- linearising at t: ‖A_a·w‖ and ‖B_a·δc‖ < τ‖Z_t‖;
- b's input bound: ‖2Z_t·A_a·w‖ < Thr_b and ‖2Z_t·B_a·δc‖ < Thr_b;
- b's own δc bound: ‖δc‖ < ThrC_b.

So:
- Thr = min(Thr_a, τ‖Z_t‖/‖A_a‖, Thr_b/‖2Z_t·A_a‖)
- ThrC = min(ThrC_a, τ‖Z_t‖/‖B_a‖, Thr_b/‖2Z_t·B_a‖, ThrC_b)

A dip in the linearisation term τ‖Z_t‖/‖A_a‖ means Z_t is near zero *at the coarser scale*. That ends the stage-(k + 1) step, and the next one starts at b with b's own first iteration exact.

**Why stages follow the minibrot chain.** A stage-(k + 1) step crosses stage-k anchors linearly. That is valid only for pixels whose δ stays far below those anchors' |Z_t|. Those are the pixels deep inside the descendant minibrot, which are exactly the ones paying millions of iterations.

So each stage spans the next ancestor period, as far as the length cap allows. On the p951,094 scene the top steps of stages 0–3 are 31 (the fixed stage-0 length), 953, 30,496 and 951,094 (§4.1). 30,496 is 953 × 32: stage 2's first dip was at 951,094, past the ×64 cap (953 × 64), so that stage was cut at the fixed length round(√998) = 32 stage-1 steps, and stage 3 then found the 951,094 period. On p121,984 the same rule gives 953 × 11 = 10,483. The 15,248 dip is not deep enough at d = 2⁻¹⁰ to end a stage on its own there. A stage with no dip has one step over the whole reference and ends the hierarchy. The same fixed-length cap as stage 0 applies, counted in stage-k steps. Imagina allows 256 stages.

**Next index.** Each step records where it begins one stage down: for stage k > 0, the index of its first stage-(k − 1) step; for stage 0, the reference index s. A pixel that cannot take a step continues one stage down from there.

### 2.4 Evaluation

For one pixel:
1. **AT**, if usable (§2.5).
2. **Stages**, from the top stage down at index j = 0.
   - Skip a stage if ‖δc‖ ≥ ThrC of its first step.
   - At step j: compute w = δ(2Ref + δ). If ‖w‖ ≥ Thr or ‖δc‖ ≥ ThrC, drop one stage, continuing at that step's next index.
   - Otherwise δ ← A·w + B·δc, n += L, z = δ + Ref_{j+1}.
   - **Rebase** (j ← 0, δ ← z) at the end of the stage's steps, or when ‖δ‖ > ‖z‖.
3. **Plain perturbation** from the stage-0 next index: FF's step, escape test (|z| ≥ 512), and rebasing at |z| < |δ| or at the end of the reference.

LA steps cannot pass the escape. Each keeps ‖δ‖ ≤ τ‖Z‖ at every iteration it covers, and |Z| ≤ 2, so the escape is always found by the plain phase.

**Climbing back (FF).** j = 0 is reference index 0 in every stage, so after any rebase a pixel can restart at the top stage. Imagina keeps a pixel in the stage it fell to. Climbing back matters for pixels that rebase often; §4.1 measures both (`FF_LAPROBE_CLIMB`).

### 2.5 The approximation transform (AT)

Take the first step of a stage: anchored at Z₀ = 0, length L, coefficients (A, B), next anchor Z_L. From z₀ = 0 + δ₀, w = δ₀², so

  z_L = Z_L + A·z₀² + B·δc.

Rescale z′ = A·z. Then

  z′_L = z′₀² + c′,  c′ = A·Z_L + (A·B)·δc,

which is exactly the Mandelbrot iteration in renormalised coordinates. This is the standard renormalisation near a nucleus: a minibrot is a small, distorted copy of the whole set. A pixel with ‖δc‖ < ThrC can therefore iterate

  z′ ← z′² + c′

one step per L iterations. It stops when |z′|² > Esc² = |A|²·Thr, the edge of the step's validity, where ‖w‖ = ‖z₀²‖ reaches Thr. It then converts back (z = z′/A, δ ← z, j ← 0) and continues with the stages. An interior pixel finishes in maxIter/L AT steps.

- **Which stage:** the highest stage whose first step gives a usable AT, from the top down.
- **Usable when:**
  - Esc² > 4, so the renormalised escape test outruns the |z′| ≤ 2 bulk;
  - radius²·|A·B|²·2³² > |A·Z_L|², with the radius the view's half diagonal in δc. That is, the image's c′ extent is not negligible next to c′'s offset, so the view really sits in the renormalised copy.
- **Range caps:** Esc² and ThrC are capped at 2²⁵⁶ (and 2²⁵⁶/‖A·B‖ for ThrC), as Imagina does.

### 2.6 FF's code against Imagina's

**Agrees:**
- the step and its thresholds (2.2), with Imagina's `LAInfo::Step`, including τ = 2⁻²⁴;
- the stage-0 and stage-k builders, including the 64 / log₁₆ length caps and the next indices (`CreateLAFromOrbit`, `CreateNewLAStage`);
- the stage-to-stage drop;
- the AT's coefficients, escape radius, δc threshold and usability test (`CreateAT`, `CreateATFromLA`, `EvaluateAT`).

**Departs** (marked `FF:` in `LaStageTable.cs` / `LaProbe.cs`):
1. **Composite δc threshold.** It also includes b's own ThrC_b; Imagina's `Composite` keeps only Thr_b for the δc term. The derivation needs both, though in practice b's term rarely binds.
2. **δc checked on every step**, not only on the stage's first step.
3. **Climbing back** to the top stage after a rebase (2.4), switchable.
4. **The plain phase uses FF's rules:** bailout 512 and rebasing at |z| < |δ|. Imagina uses |z|² > 4096 and rebases at |δ|² · 64 > |z|².
5. **Double precision only.** The bench scenes' δc (~10⁻⁷¹) and coefficients sit well inside double range. Imagina switches to a float-exp type below 2⁻⁸⁹⁶; FF needs the same for its 1e100+ views (S3).
6. **The reference stops at |Z_p| < one pixel** (#1614), FF's existing rule, not Imagina's period search.
7. **The period test.** FF uses the threshold dip of §2.3 (d = 2⁻¹⁰). In Imagina's source that is the `#else` branch of `Stage0DetectPeriod` / `DetectPeriod` / `Step` / `Composite`. Imagina's own build uses the other one: `HInfLAEvaluator.h` defines `PERIOD_DETECTION_METHOD2`, a minimum-|Z| test.
   - Each step tracks MinMag, the smallest ‖Z‖ it has covered (starting at 4).
   - A period ends a step when ‖Z‖ falls below MinMag · 2⁻⁶ at stage 0, or below MinMag · 2⁻³ at higher stages.

   §2.6 as first written (PR #1651) said FF's dip test agreed with Imagina's. That was wrong, corrected in #1625. The two tests have not been compared on the bench scenes: whether they cut the same stages, and which gives fewer steps per pixel, is for S3 to measure.

### 2.7 Function-by-function mapping

A third party can check FF's code against Imagina's `HInfLAEvaluator.{h,cpp}` (commit `cd046377`) with this table. "Translation" means the same branches and arithmetic in C#, renamed and restructured but not independent work. The changes column refers to §2.6.

| FF | Imagina | Relation | Changes |
|---|---|---|---|
| `LaStageTable.La.Start` | `LAInfo(complex z)` constructor | translation | — |
| `LaStageTable.La.Step` | `LAInfo::Step` | translation | 7; the step length is carried in the step |
| `LaStageTable.La.Compose` | `LAInfo::Composite` | translation | 1, 7 |
| `LaStageTable.La.DetectPeriod` | `LAInfo::DetectPeriod` (`#else` branch) | translation | 7 |
| `LaStageTable.Builder.FixedLen` | inline `round(pow(x, 1 / round(log2(x) / 4)))` in both builders | translation | — |
| `LaStageTable.Builder.Stage0` | `ReferenceGenerationContext::CreateLAFromOrbit` | translation | no float-exp / double-stage flags |
| `LaStageTable.Builder.NextStage` | `ReferenceGenerationContext::CreateNewLAStage` | translation | the same |
| `LaStageTable.Build` | the stage loop of `GenerateApproximationData` | translation | no `ConvertStageToDouble`; the AT is built in the probe |
| — | `CreateLAFromOrbit_MT` (multithreaded builder) | not translated | |
| `LaStageTable.ToleranceFor`, `LaStep`, the table layout | — | FF's own | |
| `MandelbrotCalculator.EnsureLaTable` (cache) | — | FF's own | |
| `LaProbe.BuildAt` | `CreateATFromLA` + `LAInfo::CreateAT` + `ATInfo::Usable` | translation | adds a finiteness check |
| `LaProbe.Evaluate`, AT part | the AT block of `HInfLAEvaluationTask::Evaluate` + `EvaluateAT` | translation, scalar | no derivatives |
| `LaProbe.Evaluate`, stage loop | `EvaluateLA` | translation, scalar | 2, 3; no derivatives, no Julia sampling |
| `LaProbe.Evaluate`, plain loop | `EvaluatePT` | adapted | 4 |
| `LaProbe.Reference`, `Exact`, `Run` | — | FF's own (BigInteger reference, exact check, report) | 6 |

## 3. Architecture in FF

What S1 settled is in **bold**; the rest is still the plan.

- **`LaStageTable`** (S2, done: #1625), `Engine/Math/LaStageTable.cs`, next to `BlaTable`.
  - Built from FF's own double reference values Z₀..Z_R: R is the period when the reference is periodic (#1614; the arrays run one further, to Z_{p+1}), otherwise the orbit length.
  - `MandelbrotCalculator.EnsureLaTable` builds it after the BLA table when `BuildLaStages` is on (`FF_LA_STAGES=1`). It is off by default until S3 reads it.
  - **Cache key:** the reference generation and τ. The thresholds don't depend on the δc radius, so unlike BLA there is no radius key; the AT (S4) will need one.
  - **τ follows the BLA tolerance:** τ = 2⁻²⁴·ε/10⁻⁶, clamped to [2⁻⁵², 2⁻⁸], from the ε the CPU rows use (tightened in the QD band, #1613). The Fast…Exact presets keep their meaning.
  - **Size and build time** (i5-13420H):

    | scene | stages | steps | memory | build |
    |---|---|---|---|---|
    | p15,248 | 3 | 516 | 36 KiB | 5 ms |
    | p121,984 | 4 | 4,113 | 289 KiB | 3–49 ms |
    | p951,094 | 4 | ~32,000 | 2.2 MiB | 13–21 ms |

    At 160×90, FF's BLA table takes 63 ms and the orbit 1.3 s.
  - `--laprobe` now runs on this table and reproduces the S1 numbers exactly.
- **Evaluator** (S3–S5), on the CPU deep path.
  - A scalar version first, in the shape of `ResumePixelPTRebased`, then SIMD.
  - The rebased loop stays the fallback. The #1330 lattice and #1344 mask contracts hold. The derivative is carried for distance estimates and normals.
  - **Climb back to the top stage after a rebase:** −39 % steps on p951,094, −29 % on p121,984 (§4.1).
  - **The plain tail costs about 300–480 steps per pixel on every scene.** That is the last stretch before escape, after the steps stop being valid. On the shallow scenes it is most of the work. S3 should try FF's BLA table for that tail before plain steps.
- **AT** (S4) works where a stage's escape radius exceeds 2: p448, p953 and p15,248 (all pixels used it).
  - On p121,984 and p951,094 the top stage's escape radius is 0.35 and 0.46, so no stage gives a usable AT. Those pixels pay LA steps instead, about 3,800 per pixel on p951,094.
  - S4 starts by measuring whether a looser τ for the AT step, or an AT built from the period-953 copy with a float-exp c′, recovers it.
- **Precision.** The probe runs in double (§2.6, item 5). FF's deep views go to 1e100 (#1428 later), where δc ≈ 10⁻¹⁰² still fits a double, but coefficients over long steps can leave its range. S3 checks the coefficient range per stage and falls back to the rebased loop (or float-exp) where they do.
- **Selection** (S6). LA where it pays (measured), BLA otherwise, with a way back:
  - `FractalParameters` → region snapshot → `--param` → Command builder;
  - an env var for A/B.
- **GPU** (S7). #1621 is done, so the GPU deep kernels already take the periodic reference that the stages are built over.

## 4. Correctness

- **Independent invariant:** exact iteration (fixed-point BigInteger, no perturbation) on sampled pixels of the bench scenes, as `S1613RebaseResumeTests` / `S1614PeriodicReferenceTests` do.
  - LA must match exact iteration at least as often as today's BLA path.
  - Past ~10⁵ iterations, boundary pixels are chaotic and no double-precision method matches exact iteration (shown in #1614), so compare error size there, not exact matches.
- **Composed steps against direct perturbation iteration** (S2, `S1625LaStageTableTests`): the full recurrence with δ² kept, at a quarter of each step's thresholds, for steps of every stage. The worst relative error is 6.1·10⁻⁸ against τ = 6.0·10⁻⁸; the test bound is 8τ.
- **Self-consistency only as a second line:** SIMD byte-identical to scalar (S5).
- **Mutants** (wrong stage index, skipped validity check, AT threshold off) must fail the tests. Mutants that are equivalent by construction are named in the test file, as #1614 did.

### 4.1 Proof of concept (S1, `--laprobe`, 2026-11-03)

Setup:
- i5-13420H, 160×90, Release.
- LA is scalar double (`System.Numerics.Complex`, `Parallel.For` over rows), climbing back after a rebase.
- FF is today's default deep path (SA + BLA + rebasing, SIMD ×4), on the same pixel grid.
- Exact samples: 12 pixels per scene, fixed-point BigInteger at 320 bits, seed 1624.
- Both use the same reference length: FF's own detected period.

| scene | stages (top step) | AT | LA steps / px (AT + LA + plain) | FF steps / 4-px batch (plain + BLA) | exact: LA | exact: FF |
|---|---|---|---|---|---|---|
| p448 5.2e33 | 2 (448) | 448 | 7 + 14 + 268 = 289 | 300 + 40 = 340 | 12/12 | 12/12 |
| path 1e33 | 3 (2,817) | unused | 0 + 31 + 424 = 455 | 241 + 2 = 243 | 12/12 | 12/12 |
| p953 1.3e53 | 2 (953) | 953 | 8 + 19 + 309 = 336 | 427 + 115 = 542 | 12/12 | 11/12 |
| p15,248 2.1e57 | 3 (15,248) | 953 | 111 + 19 + 338 = 468 | 1,145 + 1,153 = 2,298 | 12/12 | 12/12 |
| p121,984 1.7e66 | 4 (121,984) | none | 0 + 530 + 337 = 867 | 1,556 + 2,652 = 4,208 | 8/12, mean \|err\| 12 | 3/12, mean \|err\| 1,639 |
| p951,094 2.1e68 | 4 (951,094) | none | 0 + 3,778 + 339 = 4,116 | 3,489 + 11,458 = 14,948 | 4/12, mean \|err\| 1,636 | 4/12, mean \|err\| 3,846 |

How to read the columns:
- **"Steps"** counts loop iterations. One LA step is about two complex multiply-adds plus two norm checks; one FF step advances 4 pixels in lockstep, so the batch count is what each of those pixels pays.
- **Interior samples** match exactly in both.
- **On the two chaotic scenes,** LA's errors are smaller than FF's, not just as small.

Other findings:
- **Stages follow the chain.** The top-stage steps are 953 / 15,248 / 121,984 / 951,094 (each scene's own period), with fixed-length steps between (31 at stage 0; 10,483 = 953 × 11 and 30,496 = 953 × 32 where the next dip was past the ×64 cap, §2.3).
- **Climbing back** (2.4), steps per pixel with climb / without (`FF_LAPROBE_CLIMB=0`): p121,984 867 / 1,217, p951,094 4,116 / 6,735, the others within 10 %.
- **Times** are indicative only (scalar `Complex`, JIT and scheduling noise at this size):
  - pixel stage on p951,094: LA 66 ms against FF's 250 ms;
  - p121,984: 15–93 ms against 218 ms;
  - p15,248: about 280 ms against 153 ms, where AT and the plain tail dominate and FF's SIMD wins.
- **Against FF's frame,** iteration counts differ on 1–8 % of pixels for the four shallow scenes, and on 70–80 % for the two chaotic ones (as FF against acceleration off, #1614). In/out disagrees on 1 pixel in 14,400.

**Decision: go on with S2.** On the scenes that motivated the epic, LA cuts steps per pixel 3.6× (p951,094) and 4.9× (p121,984) before any SIMD, with no loss of accuracy against exact iteration. The open questions move to S3 and S4: the plain tail, and the missing AT on the deepest scenes.

## 5. Credit

The method and the code come from Imagina; see `Docs/Resources-Bibliography.md#imagina`.
- **Code.** `Engine/Math/LaStageTable.cs` and `Probes/LaProbe.cs` contain C# translations of Imagina's functions (§2.7). They are not independent implementations.
  - Both files carry an SPDX copyright line for the upstream author.
  - `THIRD-PARTY-NOTICES.md` §3 lists them as code derived from another project.
  - Imagina and Fracturing Fog are both AGPL-3.0.
- **Maths.** §2 derives each rule from the recurrence independently, so the translation can be checked, not trusted.
- **Author:** the GitHub profile of 5E-324 gives the name "Yu, Zhuoran", and Imagina's commits are authored as "Zhuoran" (read 2026-11-03). Two things are not verified and sit under *Citations awaiting verification*: whether this is the Zhuoran of the fractalforums.org rebasing and BLA thread, and whether the method was published apart from the code.
- **Related background:** Zhuoran's rebasing and BLA (already credited), and Munafo's Mu-Ency entry on bivariate linear approximation.
