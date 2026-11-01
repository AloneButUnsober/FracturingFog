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

**Status: planned (2026-11-01).** §2–4 are written in S1. Until then they are an outline of
what S1 has to pin down, not a specification.

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

## 2. Method (outline — S1 writes it out)

The method is the one Imagina uses: https://github.com/5E-324/Imagina, AGPL-3.0,
`HInfLAEvaluator.{h,cpp}`, read 2026-11-01. Its pieces:

**LA step.** A linear map over l iterations of the perturbation recurrence:

  δ_{n+l} ≈ A·δ_n + B·δc

It is valid while ‖δ_n‖ is under a z-threshold and ‖δc‖ is under a c-threshold. The thresholds use
Chebyshev norms scaled by a tolerance (2⁻²⁴ in Imagina). Steps compose by multiplying A and
accumulating B, with the thresholds tightened accordingly. This is the same family as BLA (#1346),
but its validity is tracked per step rather than as a radius in a power-of-two table.

**Period detection and stages.** Building steps over the reference, a period shows up as a dip:
the threshold of the step being built collapses relative to the one before (the orbit came back
near zero).
- That ends stage 0. Its steps run between consecutive near-zero points.
- Stage 1 is built the same way, but its "iterations" are whole stage-0 steps, so one stage-1 step can span an ancestor period.
- And so on, up to the top stage (Imagina allows up to 256).
- Each step records its length and the index of the step to continue with in the stage below.

**Evaluation.** A pixel starts at the highest stage whose first step is valid for its δc. It
applies steps while they are valid, drops a stage when they are not, and rebases as today when
the reference no longer anchors it.

**AT (approximation transform).** For pixels whose δc is under the top stage's c-threshold, a top
stage step is close to a quadratic map in rescaled coordinates: renormalisation near the nucleus.
The pixel then iterates

  z′ ← z′² + c′,  with c′ = RefC + CCoeff·δc

one top-stage period per step, until it passes the AT escape radius. It then converts back
(z = z′·InvZCoeff) and continues with the stages. This is what lets a 28.5M-iteration interior
pixel finish in about 30 AT steps.

S1 derives each of these from the recurrence on its own (not from the code), states every
threshold and the tolerance, and notes where FF's choices differ from Imagina's.

## 3. Architecture in FF (outline)

- **`LaStageTable`** (S2), next to `BlaTable` (`Engine/Math`). Built from the reference orbit, which #1622 truncates at the period. Cached per reference generation, dc radius and tolerance.
- **Evaluator** (S3–S5), on the CPU deep path. A scalar version first (`ResumePixelPTRebased`'s shape), then SIMD.
  - The rebased loop stays the fallback.
  - The #1330 lattice and #1344 mask contracts hold.
  - The derivative is carried for distance estimates and normals.
- **Selection** (S6). LA where it pays (measured), BLA otherwise, with a way back:
  - `FractalParameters` → region snapshot → `--param` → Command builder;
  - an env var for A/B.
- **GPU** (S7), after #1621.

## 4. Correctness

- **Independent invariant:** exact iteration (fixed-point BigInteger, no perturbation) on sampled pixels of the bench scenes, as `S1613RebaseResumeTests` / `S1614PeriodicReferenceTests` do.
  - LA must match exact iteration at least as often as today's BLA path.
  - Past ~10⁵ iterations, boundary pixels are chaotic and no double-precision method matches exact iteration (shown in #1614), so compare error size there, not exact matches.
- **Self-consistency only as a second line:** composed steps against direct reference iteration (S2); SIMD byte-identical to scalar (S5).
- **Mutants** (wrong stage index, skipped validity check, AT threshold off) must fail the tests. Mutants that are equivalent by construction are named in the test file, as #1614 did.

## 5. Credit

The method comes from Imagina; see `Docs/Resources-Bibliography.md#imagina`. FF re-implements it
from its description and the maths. If any code is ported, it also goes into
`THIRD-PARTY-NOTICES.md`; the AGPL-3.0 licences are compatible.
- **Author:** the repository author's real name, and whether the method was published elsewhere (fractalforums.org threads), are not verified. Those entries sit under *Citations awaiting verification* until traced.
- **Related background:** Zhuoran's rebasing and BLA (already credited), and Munafo's Mu-Ency entry on bivariate linear approximation.
