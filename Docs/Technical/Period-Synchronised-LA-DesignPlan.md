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

**Status: S1 done (2026-11-03); S2 done (#1625); S3 done (#1626, opt-in); S4 done (#1627); S5 done (#1628, 4-lane path opt-in: no gain measured); S6 done (#1629, LA on by default from zoom 1e30); S7 done (#1630, GPU kernel, opt-in).** §2 is the method, derived and checked against Imagina's code;
§4.1 is the proof of concept (`--laprobe`) and the decision to go on with S2; §4.2 is the engine's evaluator; §4.3 the AT; §4.4 SIMD; §4.5 the default; §4.6 the GPU kernel. §3 is still a plan
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
- the AT's coefficients, escape radius, δc threshold and usability test (`CreateAT`, `CreateATFromLA`, `EvaluateAT`);
- the derivative through a step, d ← A·2z_s·d + B (`LAInfo::EvaluateDzdc`; #1626).

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

   §2.6 as first written (PR #1651) said FF's dip test agreed with Imagina's. That was wrong, corrected in #1625. #1626 adds Imagina's test as `LaPeriodTest.MinMagnitude` (`FF_LA_PERIOD=minmag`) and measures the two (§4.2); the dip stays the default.
8. **No step past the iteration cap** (#1626). A step longer than the iterations left is refused like an invalid one, so the pixel drops a stage and the frame stops at the cap exactly. Imagina takes the step and stops after it.
9. **The plain tail** (#1626) is FF's rebased loop: bailout 512, rebasing at |z| < |δ| or at the end of the reference, and, from a rebase, a climb back to the stages (item 3). BLA skips in the tail are optional (`LaTailBla`, off): §4.2 measured them.
10. **The AT's precision margin** (#1627). c′ = RefC + C·δc is rounded in double, so its absolute error is about 2⁻⁵³·|RefC|. When the view's c′ extent is much smaller than |RefC|, the AT works from a coarser c′ than perturbation's exact δc. Imagina accepts a view down to 2⁻¹⁶ of |RefC| (radius²·|C|²·2³² > |RefC|²). FF's default accepts it only when the view is at least |RefC| (`LaAtOffsetBits` = 0; 16 is Imagina's, `FF_LA_AT_OFFSET_BITS`). §4.3 shows the difference against exact iteration.
11. **A looser AT tolerance** (#1627, `LaAtLoosen`, `FF_LA_AT_LOOSEN`): the AT's Thr and ThrC scaled by s ≥ 1, as if the AT alone were built at s·τ. Measured and rejected (§4.3); the default s = 1 is Imagina's AT.

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
| `LaAt.Create` / `Usable` / `Select` (#1627, `Engine/Math/LaAt.cs`) | `LAInfo::CreateAT` / `ATInfo::Usable` / `CreateATFromLA` | translation | 10, 11; a finiteness check |
| `MandelbrotCalculator.EvaluateLa`, AT block (#1627) | the AT block of `HInfLAEvaluationTask::Evaluate` + `EvaluateAT` | translation, scalar | the derivative as Imagina's dz′/dc′ · C / A |
| `LaProbe.Evaluate`, AT part | the AT block of `HInfLAEvaluationTask::Evaluate` + `EvaluateAT` | translation, scalar | no derivatives |
| `LaProbe.Evaluate`, stage loop | `EvaluateLA` | translation, scalar | 2, 3; no derivatives, no Julia sampling |
| `LaProbe.Evaluate`, plain loop | `EvaluatePT` | adapted | 4 |
| `LaProbe.Reference`, `Exact`, `Run` | — | FF's own (BigInteger reference, exact check, report) | 6 |
| `LaStageTable.La` with `LaPeriodTest.MinMagnitude` (#1626) | the `PERIOD_DETECTION_METHOD2` branches of `LAInfo::Step` / `Composite` / `DetectPeriod` | translation | — |
| `MandelbrotCalculator.EvaluateLa`, stage loop (#1626) | `EvaluateLA` + `LAInfo::EvaluateDzdc` | translation, scalar | 2, 3, 8; no Julia sampling |
| `MandelbrotCalculator.EvaluateLa`, plain tail | — (FF's `ResumePixelPTRebased`) | FF's own | 4, 9 |
| `MandelbrotCalculator.ComputeRowLa`, `UseLaEvaluator`, `LastLaWork` | — | FF's own | |
| `MandelbrotCalculator.LaRunStages`, `RunLaAt` (#1628) | the same functions as the stage loop and AT block above, moved into helpers | translation (unchanged) | |
| `MandelbrotCalculator.ComputeRowLa4` / `LaTailStep4` (#1628, `MandelbrotCalculator.La4.cs`) | — (Imagina's vec4 evaluator not read) | FF's own; calls the two helpers | |
| `LaKernelSource` `CSPerturbLA` (#1630, `Rendering.D3D/LaKernelSource.cs`) | `EvaluateAT`, `EvaluateLA`, `LAInfo::EvaluateDzdc`, via `RunLaAt` / `LaRunStages` | translation (HLSL of the C# translation), as a chunked state machine | 2, 3, 8, 10; plain tail FF's own (4, 9) |

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
- **Evaluator** (S3 done: #1626; S5 done: #1628), `Engine/Calculators/MandelbrotCalculator.La.cs`, on the CPU deep path.
  - **SIMD** (#1628, `MandelbrotCalculator.La4.cs`, `LaSimd`, `FF_LA_SIMD=1`, off): the plain tail on 4 AVX2 lanes, the AT and the stages in scalar per lane (the same `RunLaAt` / `LaRunStages` code), byte-identical to the scalar evaluator. It measured no faster than scalar on the bench CPU (§4.4), so it is off; it stays for measuring on other CPUs (S6).
  - `UseLaEvaluator` (`FF_LA=1`, off by default) sends the whole frame through `ComputeRowLa` → `EvaluateLa` instead of the PT4 / PT8 rows. It implies building the table. It is inactive with acceleration off, rebasing off, the DD rebase reference, or a table with a non-finite coefficient (`LaStageTable.Finite`); those frames are today's. GPU perturbation is skipped while it is on (the GPU is S7).
  - Scalar double, per pixel, so the #1330 lattice and #1344 mask contracts hold as in `ComputeRowPeriodic`. No SA: the stages start at n = 0.
  - **The derivative rides through each step exactly,** d ← A·2z_s·d + B, so normals and distance estimates agree with plain rebasing (§4.2). The BLA rows' d ← A·d drops B.
  - **Climb back to the top stage after a rebase** (`LaClimbOnRebase`, `FF_LA_CLIMB=0` off): −39 % steps on p951,094, −29 % on p121,984 (§4.1).
  - **The plain tail** (about 250–550 steps per pixel on every scene): BLA skips there (`LaTailBla`, `FF_LA_TAIL_BLA=1`) cut it by about a fifth but cost exact matches and no time (§4.2), so they are off.
  - **Diagnostics:** `LastFrameUsedLa`, `LastLaWork` (pixels, LA steps, plain steps, BLA skips); `--laprobe` scores the engine's frame ("eng") against exact iteration, `--deepcostprobe` with `FF_DEEPCOST_LA_AB=1` times it.
- **AT** (S4 done: #1627), `Engine/Math/LaAt.cs` + the AT block of `EvaluateLa`.
  - On with the evaluator (`UseLaAt`, `FF_LA_AT=0` off). Chosen per frame after the table (`SelectLaAt`, radius = the view's half diagonal), so no cache key.
  - A pixel under the AT's δc bound iterates z′ ← z′² + c′ until |z′|² > Esc² or the cap; it converts back (z = z′/A, a rebase) and continues with the stages from the top. The derivative is dz′/dc′ · C / A. Interior pixels finish in the AT in maxIter / L steps.
  - FF's precision margin (§2.6, item 10) keeps the AT only where its rounded c′ is as fine as perturbation's δc.
  - **What it gives on the bench scenes is small** (§4.3): p448 and p953 use it on every pixel with no measurable time change (the plain tail dominates); p15,248's AT is the stage-1 one, which FF's margin rejects; the path scene's δc bound is below a pixel; p121,984 and p951,094 have none.
  - **Why the deepest scenes get none:** their top stage's escape radius is 0.35 and 0.46, and the lower stages' views are 10⁻¹¹–10⁻¹⁵ of their c′ offset. A looser AT tolerance makes the top stage usable but costs exact matches (§4.3). Per stage, 96 % of p951,094's LA steps are stage-1 (953-iteration) steps: escaping pixels leave the reference's copy at nearly every 953-anchor, so composite steps fail there. An AT from the 953 copy with a perturbed (or float-exp) c′ would also cost one exact step per 953 iterations, the same as those LA steps, so it is not pursued.
  - **Diagnostics:** `LastLaAt`, `LastLaAtWork`, `LastLaStageSteps` (LA steps per stage); `--laprobe`'s "eng" line prints them.
- **Precision.** The probe and the evaluator run in double (§2.6, item 5). FF's deep views go to 1e100 (#1428 later), where δc ≈ 10⁻¹⁰² still fits a double, but coefficients over long steps can leave its range. Since #1626 the table records whether every coefficient and threshold is finite (`LaStageTable.Finite`); when not, the frame keeps the rebased loop. A float-exp evaluator is later work.
- **Selection** (S6 done: #1629). `FractalParameters.DeepLa` (`DeepLaMode` Off / Auto / On), default Auto.
  - **Auto = the LA stages at zoom ≥ 1e30 (`MandelbrotCalculator.LaAutoMinZoom`), the BLA rows below**, measured on FF's 76 deep built-in regions and the bench kit (§4.5). A periodic reference below 1e30 was measured neutral, so it is not part of the rule.
  - An enabled GPU perturbation frame still runs on the GPU under Auto; On forces LA in its place.
  - The way back: region snapshot (`DeepLa`, omitted at Auto) → `--param DeepLa=0|1|2` → Command builder seed; the Fractal Params → Deep zoom → Evaluator box; `FF_LA=0` / `FF_LA=1` override every calculator for A/B.
  - The precision label gains ` LA` (`QD-PT LA`) when the LA rows rendered the frame.
- **GPU** (S7 done: #1630). `Rendering.D3D/LaKernelSource.cs` (`CSPerturbLA`), one HLSL source for D3D11 (FXC) and Vulkan (DXC), `IGpuKernel.RunPerturbLa`, fp64.
  - The kernel is the scalar evaluator as a state machine: AT, stages, plain tail with climbing back, the same steps in the same order. It runs PERTURB_CHUNK trips at a time so the #1340 wavefront schedule, the #1334 TDR bands and the #1394 mask work as for the SA / BLA kernel.
  - Buffers: t2 the steps (`LaStageTable.GpuSteps`, 8 doubles each), t3 the integers (`GpuInts`: per stage first step and count, per step Len and Next; uints, so the shader needs no double-to-int conversion); the AT in the cbuffer.
  - The calculator takes it when the frame wants LA (`LaMode`), GPU perturbation runs, the kernel has it (`SupportsPerturbationLa`) and `UseGpuLa` (`FF_GPU_LA=0` off); forced On now runs on the GPU too where the kernel has LA. `LastFrameUsedGpuLa`; the route reads "deep-zoom perturbation + LA stages".
  - Range: the GPU deep path stops at `MaxGpuPerturbZoom` (1e50), so on the GPU LA covers 1e30..1e50; the deeper scenes stay on the CPU LA rows.
  - **Off by default** (`UseGpuLa`, `FF_GPU_LA=1`): on the GT 710 under Vulkan it is slower than the SA / BLA kernel. D3D11 is gated off (`FF_D3D_LA=1`): the NVIDIA D3D11 driver hung on it (#1661). §4.6.
  - Not done: the fp32 / float-exp kernels (#1331) keep their path (§4.6).

## 4. Correctness

- **Independent invariant:** exact iteration (fixed-point BigInteger, no perturbation) on sampled pixels of the bench scenes, as `S1613RebaseResumeTests` / `S1614PeriodicReferenceTests` do.
  - LA must match exact iteration at least as often as today's BLA path.
  - Past ~10⁵ iterations, boundary pixels are chaotic and no double-precision method matches exact iteration (shown in #1614), so compare error size there, not exact matches.
- **Composed steps against direct perturbation iteration** (S2, `S1625LaStageTableTests`): the full recurrence with δ² kept, at a quarter of each step's thresholds, for steps of every stage. The worst relative error is 6.1·10⁻⁸ against τ = 6.0·10⁻⁸; the test bound is 8τ.
- **Self-consistency only as a second line:** SIMD byte-identical to scalar (S5).
- **Mutants** (wrong stage index, skipped validity check, AT threshold off) must fail the tests. Mutants that are equivalent by construction are named in the test file, as #1614 did.
- **The evaluator against exact iteration** (S3, `S1626LaEvaluatorTests`): see §4.2.

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

### 4.2 The engine's evaluator (S3, #1626, 2026-11-03)

Setup:
- i5-13420H, 12 threads, Release.
- LA is `UseLaEvaluator`: scalar double, per pixel, climbing back, no tail BLA, threshold-dip period test.
- FF is today's default deep path (SA + BLA + rebasing, SIMD ×4).

**Time** (`--deepcostprobe`, 960×540, best of 3, `FF_DEEPCOST_LA_AB=1`):

| scene | pixels: FF | pixels: LA | frame: FF | frame: LA | LA steps / px (LA + plain) | iteration counts differ |
|---|---|---|---|---|---|---|
| p448 5.2e33 | 220 ms | 180 ms | 229 ms | 183 ms | 23 + 555 | 0.27 % |
| path 1e33 | 235 ms | 196 ms | 227 ms | 188 ms | 24 + 636 | 0.21 % |
| p953 1.3e53 | 215 ms | 109 ms | 218 ms | 111 ms | 27 + 309 | 2.2 % |
| p15,248 2.1e57 | 1,012 ms | 183 ms | 1,012 ms | 210 ms | 131 + 338 | 2.6 % |
| p121,984 1.7e66 | 2,093 ms | 459 ms | 2,274 ms | 630 ms | 530 + 338 | 71 % |
| p951,094 2.1e68 | 10,067 ms | 2,669 ms | 10,879 ms | 4,043 ms | 3,780 + 338 | 80 % |

- Scalar LA beats SIMD FF on every scene: pixels 3.8× faster on p951,094, 4.6× on p121,984, 5.5× on p15,248, 2× on p953, about 1.2× on the two shallow scenes.
- On p951,094 the frame is now about half reference orbit (1.3–2.3 s, single-threaded).
- The 70–80 % differing counts on the two chaotic scenes are the same as FF against acceleration off (#1614); exact iteration decides (below). In / out differs at 0–8 pixels of 518,400.
- The table build adds 0–14 ms (66 ms with the BLA table on p951,094).

**Accuracy against exact iteration** (`--laprobe`, 160×90; "eng" is the engine's LA frame, FF today's frame):

| scene | samples | exact: LA | exact: FF | mean \|err\|: LA | mean \|err\|: FF |
|---|---|---|---|---|---|
| p448, path, p953, p15,248 | 12 each | 12/12 each | 12, 12, 11, 12 | 0 | 0–1 |
| p121,984 | 48 | 29/48 | 11/48 | 70 | 1,347 |
| p951,094 | 48 | 17/48 | 11/48 | 9,563 | 22,221 |

The engine's frame reproduces the S1 probe's numbers on every sample (the probe uses the AT where it can; the engine does not yet, S4), so the stage loop moved into the engine intact.

**`S1626LaEvaluatorTests`** (64×36):
- **Exact iteration:** on p953 (16 samples), p15,248 (8) and p121,984 (4), LA matches exact iteration at least as often as BLA, with no larger total error, and in / out matches on every sample. Measured: 16/16, 8/8, 4/4 for LA; 16/16, 8/8, 2/4 for BLA.
- **Smooth value and normals** against the acceleration-off frame (plain rebasing, exact derivative), where counts agree. The normal is the direction of z / (dz/dc); the float distance buffer underflows at these zooms.
  - Smooth off by > 10⁻³: LA 65 and 97 pixels on p953 and p15,248, BLA 112 and 181.
  - Normal off by > 10⁻³ rad: LA 51 and 103 of about 2,100 pixels, BLA almost all of them (its d ← A·d drops B).
- **Mutants:**

  | mutant | failing tests |
  |---|---|
  | drop to the next stage one step off | 6 of 8 |
  | validity check on w removed | 6 of 8 |
  | + B dropped from the derivative step | 2 of 8 (smooth / normals) |

  Equivalent by construction: a skipped stage keeping its index. A stage's first-step δc bound never exceeds the one below it, so stages are only skipped from the top at j = 0, where every next index is 0. The test asserts that ordering.

**Tail BLA** (`FF_LA_TAIL_BLA=1`, p953, 160×90):
- It cut the plain tail from 309 to 246 steps per pixel, with 6 BLA skips.
- It cost an exact match (11/12, one sample 39 iterations off, against 12/12) and the frame was no faster.
- The tail is where |δ| grows from τ|Z| to escape, which is where a BLA step's 10⁻⁶ tolerance shows (#1346). Off by default.

**Period tests** (§2.6, item 7; `FF_LA_PERIOD=minmag` for Imagina's minimum-|Z| test):
- **Same stages** on p448, path, p953, p15,248 and p121,984.
- **p951,094:** stage 2 is cut at 37,167 (953 × 39) instead of 30,496. LA steps per pixel fall 43 %, from 3,780 to 2,140.
  - Pixels: 1,671 ms against 2,669 ms (960×540), −37 %.
  - Exact matches, 48 samples: 22/48 against 17/48, but mean error 16,522 against 9,563.
- **p121,984:** 5 % fewer steps; exact 25/48 against 29/48, mean error 291 against 70.

Neither test is better everywhere, so the threshold dip stays the default. S6 chooses with more samples.

**Decision:** go on with S4 (the AT on the deepest scenes) and S5 (SIMD). S6 turns LA on by default, keeping the off switch.

### 4.3 The AT in the engine (S4, #1627, 2026-11-03)

Setup as §4.2.

**Where it applies** (`--laprobe`, 160×90; `S1627LaAtTests`, 64×36):

| scene | AT stage (step) | escape r | view / c′ offset | pixels using it |
|---|---|---|---|---|
| p448 | 1 (448) | 539 | 3.5·10¹⁶ | all |
| path | 2 (2,147) | 3.1·10²⁶ | 1.4·10²² | the centre only (δc bound below a pixel) |
| p953 | 1 (953) | 2.7·10⁹ | 4.3·10⁴³ | all |
| p15,248 | 1 (953) with Imagina's margin; none with FF's | 2.7·10⁹ | 3.7·10⁻⁴ | all / — |
| p121,984 | none (top stage 0.345) | | | |
| p951,094 | none (top stage 0.462) | | | |

**Time** (`--deepcostprobe`, 960×540, best of 5, AT on / off):
- p448: 177 / 177 ms pixels; p953: 109–120 / 112–127 ms. The AT replaces 6–8 LA steps per pixel; the 300–550-step plain tail dominates.
- p15,248 with Imagina's margin: 145–146 / 193–209 ms (−25 to −30 %), the only scene where the AT saves time.

**Accuracy:**
- With FF's margin, every exact-iteration check of §4.2 holds with the AT on (p953 16/16; interior samples interior; in / out identical to the stages-only frame over the frame).
- **Imagina's margin on p15,248:** 48 exact samples at 160×90 still match (48/48). But on the pixels where its frame and the stages-only frame disagree (24 at 64×36), scored against exact smooth values (fixed-point BigInteger with the final z), the stages-only frame is closer on 23 and the AT frame on 0, with mean errors 5.6 and 8.3 iterations.
  - Tightening the AT's own bounds by up to 10¹⁶ changed nothing, so the loss is not the approximation; it is c′'s rounding. The view is 1/2,700 of |c′|.
  - With the AT on, `S1626LaEvaluatorTests` failed on p15,248: exact 7/8, and smooth 254 pixels off against 97.
- **Looser AT tolerance** on the deepest scenes (12 exact samples; LA steps per pixel; `FF_LA_AT_LOOSEN`):

  | s | p121,984 | p951,094 |
  |---|---|---|
  | 1 | no AT; 8/12, mean err 12; 530 | no AT; 4/12, mean err 1,637; 3,778 |
  | 32 | no AT | AT (r 2.6); 4/12, mean err 10,472; 3,250 |
  | 128 | AT (r 3.9); 2/12, mean err 204; 329 | AT (r 5.2); 3/12, mean err 3,455; 2,659 |
  | 1,024 | AT (r 11); 2/12, mean err 1,244; 276 | AT (r 15); 3/12, mean err 6,245; 1,976 |

  Every looser AT loses exact matches or grows the error several-fold, so it stays off.
- **Steps per stage** (p121,984 / p951,094, LA steps per pixel, stages 0–3): 19 / 469 / 42 / 0 and 19 / 3,643 / 115 / 0.

**`S1627LaAtTests`:**
- **Exact iteration**, with the AT on path and p953: at least as good as BLA and as the stages alone.
- **Interior pixels** reach the cap without drift (p953: in / out identical with and without the AT, interior samples interior, interior pixels finish in the AT).
- **Normals** after the AT agree with plain rebasing (51 of 2,138 pixels off, as without it).
- **The deepest scene** has no usable AT and its top escape radius is under 2.
- **The margin test above.**
- **Mutants:**

  | mutant | failing tests |
  |---|---|
  | the AT's δc check removed | path's exact test |
  | the renormalised escape test removed | 5 tests |
  | converting back without 1/A | 4 tests |
  | Imagina's margin as the default | 3 S1626 tests |

**Decision:** keep the AT, with FF's margin, on with the evaluator. It is exact where it runs and, on these scenes, it does not hurt. The epic's pixel cost is now the plain tail (300–550 steps per pixel) on the shallow scenes and stage-1 steps on the deepest; S5 (SIMD) attacks both.

### 4.4 SIMD (S5, #1628, 2026-11-03)

Setup:
- i5-13420H: 4 P-cores with SMT + 4 E-cores, 12 threads, AVX2, no AVX-512.
- 960×540, AT on, FF's defaults otherwise.
- `--deepcostprobe` with `FF_DEEPCOST_LA_SIMD_AB=1` renders the scalar and the 4-lane frames alternately in one process, best of 4 each, so clock drift hits both. Separate runs drifted by up to 25 % on the same frame.

**Two designs:**
1. **Every lane takes its own step each iteration** — an LA step or a plain step, both computed for all four lanes and blended by mask. The step data is gathered per lane from the table (`LaStep` is 9 doubles), and the control runs per lane in scalar.
   - Measured in separate runs: 1.9–3.7× slower than scalar on every scene, and 2.2–3.3× after skipping a block when no lane needs it.
   - The per-lane control on every step costs as much as a scalar step. Replaced.
2. **Grouped by phase** (kept): the plain tail on 4 lanes as the PT4 rows do (#1613). The AT and the stages run in scalar per lane, through the scalar evaluator's own `RunLaAt` / `LaRunStages`, when a lane loads a pixel and when it climbs back from the tail. That happens about 4 times per pixel on every scene (`LastLaTailClimbs`).

**Results (design 2, pixel stage, scalar / 4-lane):**

| scene | reference read by 4 scalar loads | by AVX2 gathers (`FF_LA_GATHER=1`) |
|---|---|---|
| p448 | 168 / 161 ms (×1.04) | 200 / 218 ms (×0.92) |
| path | 192 / 184 ms (×1.05) | 231 / 234 ms (×0.99) |
| p953 | 103 / 111 ms (×0.93) | 125 / 137 ms (×0.92) |
| p15,248 | 185 / 202 ms (×0.92) | 210 / 243 ms (×0.86) |
| p121,984 | 507 / 565 ms (×0.90) | 500 / 601 ms (×0.83) |
| p951,094 | 2,692 / 3,135 ms (×0.86) | 2,743 / 3,224 ms (×0.85) |

- The frames were byte-identical in every run.
- The scalar evaluator already keeps this CPU busy: 12 threads with SMT hide most of a step's latency, so 4 lanes per thread add little. On the stage-heavy scenes the lane state's transfers to and from scalar for each climb cost more than the vector tail saves.
- Gathers are slower than four scalar loads here, so the scalar loads are the default.

**`S1628LaSimdTests`:**
- Byte-identical to scalar on all five scenes up to p121,984, with the AT on and off, at a size whose rows do not fill the lanes (61×37). Compared: every buffer the frame writes (iterations, smooth, distance, normals, final z and derivative, colours) and the work counters.
- The same through a sparse #1344 pixel mask (at the marked pixels: unmarked ones keep their buffers' contents, and those buffers are allocated uninitialised, so they hold whatever the process had there) and in a sub-rect tile of a larger image (#1330 geometry).
- The options it does not cover (tail BLA, climbing off) keep the scalar evaluator.
- **Mutants:**

  | mutant | failing tests |
  |---|---|
  | the end-of-reference rebase one index late | 3 |
  | the cap test one iteration late | 1 |
  | a non-identical multiply in the step | all 6 |

  Equivalent:
  - `>` for `>=` at the escape radius, which needs |z|² exactly 512².
  - An escaping lane reporting the stored derivative: held lanes store the value from before the update, which is what it reports.

**Decision:** the 4-lane path stays, off by default (`FF_LA_SIMD=1`), byte-identical, for S6 to measure on other CPUs (no SMT, AVX-512). S6 turns on the scalar evaluator, the one that pays (§4.2).

### 4.5 The default (S6, #1629, 2026-11-04)

Setup:
- i5-13420H, 12 threads, Release, CPU only.
- **Before** is `DeepLa` Off, the BLA rows as before #1629. **After** is the new default, Auto.
- The two run alternately in one process (`--deepcostprobe`), so clock drift hits both. Separate processes drifted by up to 25 %.

**Where LA pays: FF's 76 deep built-in regions** (`Installer/Stage/Resources/regions.json`, every Mandelbrot region at zoom ≥ 1e13, 480×270, best of 2, `FF_DEEPCOST_REGIONS`):
- **Below 1e30:** the BLA rows were faster on 48 of the 58 regions (×0.48–0.97, LA's speed relative to BLA). The default path there seldom needs its scalar rebased loop, and its 4-wide rows beat LA's scalar tail. "Deep Lightning in Space" (7.6e26) was the worst at ×0.48.
- **At zoom ≥ 1e30:** LA was faster on all but two regions, ×1.24–1.71. The two were Propellers (×0.83 / ×0.92 in two runs) and Flower Sun (×0.88 / ×1.03), both within run-to-run noise.
- **All 76 references are non-periodic.**
- **FF's own deep views at 960×540** (`FF_DEEPCOST_FFVIEWS`, the S1274 views):

  | view | LA against default |
  |---|---|
  | Deeper and Deeper 4.5e46 | ×2.8 |
  | 3E47 Test | ×1.2 |
  | E45Test04 1.1e47 | about even |
  | Deep Lightning 7.6e26 | ×0.5 |

- **A periodic reference below 1e30 is neutral.** The period-3 nucleus at 1e14, 1e22 and 1e28 runs almost entirely in the set, with a 3-iteration table: LA ≈ BLA. Every periodic scene where LA wins big is above 1e30 anyway.

**Rule: Auto = LA at zoom ≥ 1e30** (`LaAutoMinZoom`), the BLA rows below. Periodicity is not a condition.

**Before / after on the bench kit: its 17 scenes inside FF's zoom range** (1e12–1e100), from the `.kfr` files, at 1920×1080, best of 3 with the order swapped each round (`FF_DEEPCOST_KFR`):
- rith's benchmark report used 17 scenes at 3840×2160. These are the kit's 17 in-range scenes, not verified to be the same list.
- Frame times include the reference orbit.

| scene | zoom (FF) | iterations | before | after | speed-up | LA ran | iteration counts differ |
|---|---|---|---|---|---|---|---|
| 71 tip 1e12 | 5.4e11 | 1,080 | 103 ms | 102 ms | ×1.01 | no (shallow path) | 0 % |
| 47 path 1e13 | 4.9e12 | 2,859 | 1,155 ms | 1,152 ms | ×1.00 | no | 0 % |
| 40 mini p128 1e13 | 6.2e12 | 3,840 | 689 ms | 665 ms | ×1.04 | no | 0 % |
| 21 m43 spar | 2.5e27 | 30,000 | 89 ms | 85 ms | ×1.05 | no | 0 % |
| 23 nucleus p145 | 2.5e27 | 30,000 | 101 ms | 87 ms | ×1.16 | no | 0 % |
| 24 nucleus p148 | 8.7e27 | 30,000 | 90 ms | 88 ms | ×1.02 | no | 0 % |
| 72 tip 1e28 | 9.7e27 | 1,188 | 167 ms | 172 ms | ×0.98 | no | 0 % |
| 62 fraqcel | 1.1e31 | 2,000 | 1,420 ms | 1,070 ms | ×1.33 | yes | 0 % |
| 48 path 1e33 | 4.9e32 | 2,859 | 3,214 ms | 2,471 ms | ×1.30 | yes | 0.21 % |
| 41 mini p448 | 2.5e33 | 13,440 | 889 ms | 717 ms | ×1.24 * | yes | 0.28 % |
| 56 p953 | 6.6e52 | 28,590 | 2,681 ms | 1,342 ms | ×2.00 | yes | 2.3 % |
| 57 p15,248 | 1.1e57 | 457,440 | 12,309 ms | 2,354 ms | ×5.23 | yes | 2.7 % |
| 58 p121,984 | 8.3e65 | 3,659,520 | 26,286 ms | 5,707 ms | ×4.61 | yes | 70 % |
| 59 p951,094 | 1.1e68 | 28,532,820 | 109,210 ms | 31,885 ms | ×3.43 | yes | 80 % |
| 42 mini p2,066 | 1.7e96 | 61,980 | 3,014 ms | 1,570 ms | ×1.92 | yes | 2.4 % |
| 49 path 1e100 | 4.9e99 | 10,293 | 1,391 ms | 1,111 ms | ×1.25 | yes | 3.5 % |
| 73 tip 1e100 | 4.3e99 | 1,664 | 278 ms | 237 ms | ×1.17 | yes | 0 % |

\* In the full run p448 measured 1,680 / 2,225 ms (×0.75), with both paths about 2× slower than when the scene ran alone. Rerun alone, best of 4: ×1.24 at 1920×1080 and ×1.26–1.29 at 960×540. The row shows the rerun.

- **Where LA doesn't run**, the speed-up is noise: ×0.98–1.16, with identical frames.
- **Where LA runs**, it is ×1.17–5.23.
- **The two chaotic scenes** differ from the old frame on 70–80 % of pixels, as the old path differs from acceleration off (#1614). §4.2 measured LA closer to exact iteration on them.

**Parity:**
- **Live view, poster, scene video, batch and iteration rescue** all pass `DeepLa` to the calculator. `S1629LaDefaultTests` checks the poster against the live calculator for Auto and Off.
- **Defaults elsewhere:** a calculator built without `FractalParameters` (thumbnails, probes) uses Auto.
- **The visual-regression baseline (#1292) is unchanged.** Its cases render at zoom 0.5, so none reaches the deep path, let alone 1e30. A deep case would be needed to cover this path.

**Tests that pin the BLA rows** set `DeepLa` Off explicitly: S1274 (SIMD rebasing), S1613 (resumed rebasing) and S1625 (table only). S1321 now expects the ` LA` label at ≥ 1e30.

### 4.6 The GPU kernel (S7, #1630, 2026-11-04)

**The kernel** (`LaKernelSource.CSPerturbLA`, §3) runs the CPU evaluator's steps in the same order, so its frames are the CPU LA frames up to the GPU's rounding.

**Parity on the GT 710 under Vulkan** (`S1630GpuLaTests`, 64×36):

| view | AT | pixels differing from CPU LA | in / out differences |
|---|---|---|---|
| p448 | on every pixel | 0.00 % | 0 |
| path | none | 0.09 % | 0 |
| 3E47 Test | none | 0.00 % | 0 |

- **Exact iteration** (16 samples on p448): GPU LA 16/16, the GPU SA / BLA kernel 15/16 (total error 3).
- **At 960×540**, the GPU LA frame differs from CPU LA on 0.00–0.01 % of pixels and the GPU SA / BLA frame on 0–3.6 %.

**Time** (`--gpulaprobe 960 540`, best of 2; Vulkan, GeForce GT 710, fp64 at 1/24 rate):

| view | CPU BLA | CPU LA | GPU SA / BLA | GPU LA | GPU LA against GPU SA / BLA |
|---|---|---|---|---|---|
| p448 5.2e33 | 233 ms | 186 ms | 1,056 ms | 1,249 ms | ×0.85 |
| path 1e33 | 240 ms | 202 ms | 741 ms | 1,214 ms | ×0.61 |
| deeper 4.5e46 | 65 ms | 44 ms | 144 ms | 214 ms | ×0.67 |
| e45test04 1.1e47 | 189 ms | 149 ms | 381 ms | 855 ms | ×0.45 |
| 3E47 Test | 174 ms | 117 ms | 407 ms | 605 ms | ×0.67 |

- On this card every GPU deep path is 3–7× slower than the CPU, as before LA (`GPU-DeepZoom-Handoff.md`).
- The LA kernel is slower than the SA / BLA kernel there.
- Lanes of a warp sit in different phases and stages, so the steps diverge. The gathered steps are scattered reads.
- **On WARP** (the software D3D11 device) the LA kernel was ×1.9 faster than SA / BLA on p448 (32×18), but WARP runs on the CPU.
- **No GPU with fast fp64 was available to measure.**

**Decisions:**
- **`UseGpuLa` off** (`FF_GPU_LA=1` on). GPU deep perturbation is itself opt-in (`FF_GPU_PERTURB=1`).
- **D3D11 off:** `MandelbrotGpuKernel.SupportsPerturbationLa` is false unless `FF_D3D_LA=1`, #1661.
  - On the GT 710 under the NVIDIA D3D11 driver the kernel hung the device (TDR after ~7 s), even on an 8×8 frame. That happened with `continue`-based and with structured control flow.
  - The FXC-compiled kernel is correct on WARP, and the same HLSL runs on the same GPU under Vulkan.
- **Forced On** (`DeepLa` On) runs on the GPU only where the kernel is enabled; otherwise on the CPU LA rows, as in S6.

**Tests** (`S1630GpuLaTests`):
- parity with the CPU LA frame on three views, including the normals where the GPU's float derivative is finite;
- exact iteration against the GPU SA / BLA frame;
- the route under Auto (zoom 1e28 against 2.5e33), On, Off, acceleration off and `UseGpuLa` off, with the label and route text.

**Mutants** (in the HLSL):

| mutant | failing tests |
|---|---|
| next index one step off | 3 |
| validity check on w removed | 3 |
| AT converted back without 1/A | 2 |
| + B dropped from the derivative | 1 (the normals check on path) |

Not caught: removing the tail's \|z\| < \|δ\| rebase. With climbing back the tail is short, and none of the three views glitches without it.

**Found on the way, filed:**
- **#1660:** every GPU deep kernel returns z and dz/dc as `float4`, and |dz/dc| ≈ 1/pixel overflows float past ~1e35. GPU deep frames' normals and DE are inf there: 75–100 % of escaped pixels on the test views. Where finite, GPU and CPU normals agree exactly.
- **#1661:** the D3D11 hang above.

**Not done:**
- **The fp32 / float-exp kernels (#1331)** keep their path. LA's thresholds (10⁻⁶⁸ on the bench scenes) need the float-exp type, and a fp32 port should wait for a GPU on which the fp64 LA kernel pays.
- **The GPU deep range stops at 1e50** (`MaxGpuPerturbZoom`), so the deepest bench scenes stay on the CPU LA rows.

**Epic status:** S1–S7 done. LA is on by default on the CPU from 1e30 (S6), which is where the epic's speed-up is: ×2–5.2 on the deep minibrot scenes. The GPU kernel is in place, correct, and opt-in.

## 5. Credit

The method and the code come from Imagina; see `Docs/Resources-Bibliography.md#imagina`.
- **Code.** `Engine/Math/LaStageTable.cs`, `Engine/Math/LaAt.cs` (#1627), `Engine/Calculators/MandelbrotCalculator.La.cs` (#1626, #1627) and `Probes/LaProbe.cs` contain C# translations of Imagina's functions (§2.7); `Rendering.D3D/LaKernelSource.cs` (#1630) is their HLSL form. They are not independent implementations.
  - Each file carries an SPDX copyright line for the upstream author.
  - `THIRD-PARTY-NOTICES.md` §3 lists them as code derived from another project.
  - Imagina and Fracturing Fog are both AGPL-3.0.
- **Maths.** §2 derives each rule from the recurrence independently, so the translation can be checked, not trusted.
- **Author:** the GitHub profile of 5E-324 gives the name "Yu, Zhuoran", and Imagina's commits are authored as "Zhuoran" (read 2026-11-03). Two things are not verified and sit under *Citations awaiting verification*: whether this is the Zhuoran of the fractalforums.org rebasing and BLA thread, and whether the method was published apart from the code.
- **Related background:** Zhuoran's rebasing and BLA (already credited), and Munafo's Mu-Ency entry on bivariate linear approximation.
