# Arbitrary-precision deep zoom — Design Notes

Epic #1428: lift the 1e100 zoom cap (option C, arbitrary precision). The slice issues are
the canonical task list; this doc backs them.

| slice | issue | status |
|---|---|---|
| C1 BigFloat type | #1830 | §1–§5 (merged, PR #1831) |
| C2a exact centre in view state + region JSON | #1832 | §6 (merged, PR #1834) |
| C2b exact centre in history, location strings, video, RPC, cluster | #1833 | §7 (merged, PR #1835) |
| C3 log zoom: view state, camera, readouts, iterations, region JSON | #1836 | §8 (merged, PR #1837) |
| C3b log zoom through history, server request, cluster planners | #1838 | §9 (merged, PR #1839) |
| C4 arbitrary-precision reference orbit (CPU) | #1840 | §10 (merged, PR #1841) |
| C5a floatexp per-pixel perturbation (CPU, scalar) | #1842 | §11 (merged, PR #1844) |
| C5b floatexp BLA | #1845 | §12 (merged, PR #1846) |
| C5c floatexp LA stages | #1847 | §13 (merged, PR #1848) |
| C5d orbit-aware colouring path (AP orbit, floatexp) | #1849 | §14 (merged, PR #1850) |
| C5e AA jitter at depth by pixel + tier-3 recycle test | #1851 | §15 (merged, PR #1853) |
| C5f periodic-reference detection past 1e154 | #1854 | §16 (merged, PR #1855) |
| C8a reference-orbit arrays grow as built (#1828) | #1856 | §17 (merged, PR #1857) |
| C8b explore iteration plan past the tier cap (#1426) | #1858 | §18 (merged, PR #1859) |
| C9 Unbounded quality tier: zoom past 1e100 | #1861 | §19 (merged, PR #1862) |
| C10 batch + Command builder parity past 1e100 | #1860 | §20 (merged, PR #1863) |
| C11 validation + docs | #1864 | §21 (merged, PR #1865) |
| C7a period detection + zoom to minibrot past 1e100 | #1866 | §22 |
| C7b Misiurewicz snap + external rays / angles; C7c auto-explore, Julia morph, angle landings, .kfr | filed when started | |
| C6 | filed when started (see #1428) | |

## 1. Why a new number type

The ≤1e100 path keeps the centre, the reference orbit and the finders in OD (octuple-double: 8
doubles, ~424 bits). Past that, the centre needs ~log₂(zoom) bits plus guard bits for the
orbit's rounding (`BigFloat.PrecisionForZoom`): about 1,061 bits at 1e300 and 3,386 at 1e1000.
Per-pixel deltas stay low precision (perturbation, rebasing, BLA / LA). Only the centre, the
reference orbit and the Newton steps need the wide type.

## 2. `BigFloat` (#1830, `Abstractions/Math/BigFloat.cs`)

- **Value:** value = M · 2^E. M is a signed `System.Numerics.BigInteger` with at most P significant bits and no trailing zeros, so equal values have equal fields. E is a 64-bit exponent.
- **Operations:** + − × ÷, square and ScaleB.
  - Each computes the exact result in integers, then rounds once to P bits, to nearest, ties to even: the IEEE 754 rule at P bits instead of 53.
  - Division keeps a sticky bit from the remainder.
- **Conversions:**
  - from double: exact;
  - to double: correctly rounded;
  - from OD: the exact limb sum;
  - to OD: a greedy 8-limb split, exact for values of ≤ ~400 bits.
- **Decimal text:**
  - Parse is the exact rational n · 10^k, correctly rounded; for k < 0 it divides by 5^-k and scales by 2^k.
  - Format is exact integer arithmetic. At ⌈P·log₁₀2⌉ + 1 significant digits it round-trips.
- **Credit:** the method is textbook (Knuth, TAOCP Vol. 2 §4.2; IEEE 754 for the rounding rule). See the bibliography's `#knuth-taocp-2` and `#ieee-754` entries.

## 3. The far-apart add

Adding b to a with b entirely below a's last kept bit (a widened to P + 3 bits first): b can
only decide the rounding direction. a's last bit is split in two and b is folded in as ±1
unit there. The true value lies strictly between, and the discarded field then ends in a 1, so
it is never taken for an exact tie. Aligning the two would cost as many bits as the exponent
gap, which can be billions.

## 4. Managed or native (the C1 decision)

`--bigfloatprobe`, i5-13420H, one thread. One reference-orbit iteration (z ← z² + c) at the
p951,094 centre, best of 5 (the first runs are tier-0 JIT code: OD measured 11–15 µs there):

| type | bits | zoom it covers | ns / iteration | 1 M iterations | allocated / iteration |
|---|---|---|---|---|---|
| OD | 424 | 1e100 | 1,337 | 1.34 s | 0 |
| BigFloat | 397 | 1e100 | 1,410 | 1.41 s | 3.0 KiB |
| BigFloat | 1,061 | 1e300 | 3,289 | 3.29 s | 5.7 KiB |
| BigFloat | 3,386 | 1e1000 | 15,607 | 15.6 s | 16.1 KiB |
| BigFloat | 10,030 | 1e3000 | 75,130 | 75 s | 46 KiB |
| BigFloat | 33,284 | 1e10000 | 480,150 | 480 s | 151 KiB |

- At OD's own precision BigFloat costs about the same as OD.
- Each 3× in bits costs about 4.7×, so the multiply scales better than schoolbook O(n²).
- **Decision for C1: managed BigFloat, no MPFR.**
  - A 1 M-iteration orbit at 1e300 is a few seconds, about one 4K frame's pixel time on today's deepest bench scene (§4.7 of the LA plan).
  - MPFR would add a native library to package for every platform.
- **Open for C4:** a mutable limb-span kernel for the orbit loop if 1e1000+ orbits need it; it would also remove the allocations. Native MPFR stays a fallback.

## 5. Validation (`S1830BigFloatTests`)

All against independent references, none against BigFloat itself:
- **The four operations:** each equals the exact rational result rounded by a separate rational round-to-nearest-even in the test. 400 random cases per precision (64 … 1,000 bits), with exponent gaps up to 3P for the far-apart add. Ties have their own test.
- **Doubles:**
  - round-trip exactly, including subnormals;
  - `ToDouble` is the rational rounding at 53 bits;
  - 17-digit formatting matches .NET's E16 text.
- **OD:** the exact limb sum, and a 400-bit value splitting back exactly.
- **Decimal:**
  - Parse is the rational rounding of the decimal's exact value;
  - ToString round-trips at every tested precision.
- **Orbit:** a 256-bit orbit tracks a 1,024-bit fixed-point BigInteger orbit within 2^-255 over 2,000 iterations, at an attracting c (the period-3 bulb).

## 6. The exact centre (C2a, #1832)

- **`ExactCentre`** (`Abstractions/Math/ExactCentre.cs`) is a `BigFloat` (Re, Im) pair, with conversions to and from `DeepComplex` and a decimal-string pair.
- **The OD limbs stay the render contract.** Every reader up to 1e100 keeps reading the sixteen limbs, byte-identical. The exact centre is the authority past OD's precision.
- **View state.**
  - `FractalViewState.SetCenterExact` writes the pair and its OD limbs together.
  - `CenterExact` returns the pair only while the limbs are still the ones it wrote. A limb-only writer (a pan, a jitter, any code from before #1832) silently drops it, so a stale exact centre can never sit behind moved limbs.
  - `GetCenterExact(P)` gives the pair, or the limbs exactly.
- **Region JSON.**
  - `CenterExactX` / `CenterExactY` hold decimal text with BigFloat's round-trip digits, and `CenterExactBits` holds the precision it was written at. The text round-trips only at that precision: at 1,091 bits instead of 1,078 the same digits round to a different value.
  - All three are written only for a view that has an exact centre, so files for today's views are unchanged.
  - **On load** (`IJsonOnDeserialized`, so the region store, imports and server requests alike), a valid pair rewrites the limbs from itself. A damaged pair, half a pair or non-decimal text is dropped, and the limbs rule.
  - A hand-written pair without `CenterExactBits` is read at its digit count.
  - Region apply / capture / clone (`HostColorThemeService`) and `FractalRegion.CopyCentreTo` / `CopyCentreFrom` carry the pair.

## 7. The other carriers (C2b, #1833)

- **CX / CY text** (`ViewCentreText`, used by the shell's boxes, the location copy and Go).
  - A view with an exact centre shows it as plain decimal (`CoordNotation.FormatExact`, never scientific).
  - Otherwise the text is the limb formats, unchanged.
  - Typed or pasted text goes through `CoordNotation.TryParse` as before.
- **Decimals longer than 28 digits.** These used to be cut to `decimal`'s 28 digits. They are now read exactly into all eight limbs (`BigFloat` split), so even an OD-depth paste keeps its precision. Text of 28 digits or fewer takes the old path, byte-identical.
  - Only text longer than 28 digits can become an exact centre, and only when OD can't hold it.
  - Both edited parts are read at one precision (as `ExactCentre.TryParse` reads a pair): the larger of the texts' digit counts and the exact centre the view had.
  - An untouched box keeps its exact part.
- **Reading back shown text.** Text read back into another view is the text's own value at its digit count. It may differ from the shown centre past its precision (double rounding) by less than 2^-(P-2). A box nobody touched is never re-read (the shell's `LastPushed` guard).
- **History** (`NavSnapshot`) carries the exact centre, and Back restores it.
- **Video and slideshow keyframes** (`FractalRenderHost.Video`, three region legs) apply regions through `FractalRegion.CopyCentreTo`.
- **Server.**
  - `RenderRequestDto` has `centerExactX` / `centerExactY` / `centerExactBits`, omitted when null. On load (`IJsonOnDeserialized`) a valid pair rewrites the limbs and a damaged one is dropped, as for regions.
  - The tile, frame and slideshow planners and the worker's clone copy it.
  - The host engine takes it from a region when the request has no centre of its own.
- **Explore** (finders, Descend, morph) stays on OD until C7.

## 8. Log zoom (C3, #1836)

A double zoom overflows at about 1.8e308. A double log10 zoom does not, and it keeps about 1e-13 relative zoom accuracy even at 1e1000, far below a pixel.

- **`double Zoom` stays the render contract.** It is byte-identical up to 1e100. Past double's range it saturates at `double.MaxValue`.
- **View state.**
  - `FractalViewState.SetZoomLog10` writes the log and `Zoom` = 10^log together.
  - `ZoomLog10` returns that log only while `Zoom` still holds the value it wrote, else log10(`Zoom`). This is the exact centre's stamp rule (§6): any plain `Zoom` write wins.
- **`ZoomLog` helpers** (`Abstractions/ViewState/ZoomLog.cs`).
  - `Format` is exactly `ToString("G6")` while the double holds the zoom. Past it, the mantissa and exponent come from the log (`2.5E+1000`).
  - `TryParse` reads what double reads unchanged, plus scientific text past double (`1e500`), which double reads as infinity.
- **`ViewCamera`.**
  - **Zoom.** While the zoom and the product stay inside double, a zoom step is the old `clamp(z·f)`. Past that it runs on the log, with log-domain clamp bounds. Key zoom (W / S) now goes through the camera (`ZoomBy`).
  - **Exact moves.** When the view has an exact centre, or is past OD's coordinate floor (`ExactNavZoomLog10` = 120, the measured ~1e120 from `--qdfloorsweep`), pan, focus, wheel and box zoom move the exact centre with `BigFloat` adds. Before, any camera move dropped the exact centre (§6's stamp rule).
  - **The pixel offset.** It is the double pixel scale while that is a normal double. Past it, the scale is m·2^k from the log, so it holds at any depth.
  - Without an exact centre and up to 1e120, every method is the old double / OD expression.
- **Iterations.**
  - `QualityPreset.ComputeIterationsLog10` is the same formula on the log, guarded against int overflow.
  - The view-state call sites use it: the render host, video legs, region capture and the iteration lock.
- **Readouts.**
  - `RenderFrameInfo.ZoomLog10` carries the log, so the status line, the Zoom box and the location copy show zoom past double.
  - The mini depth bar takes log10 zoom.
  - The Zoom box reads `1e500`.
- **Region JSON.**
  - `ZoomLog10` is written only past double, so region files up to there are unchanged.
  - On load a valid log rewrites `Zoom` (saturated). One that is not past double, or not finite, is dropped, and `Zoom` rules.
  - Apply / capture / clone go through `CopyZoomTo` / `LogFieldFor`.
- **Not yet.**
  - History, video keyframes, the server request and the cluster planners: C3b (§9).
  - Batch `--zoom` past double: C10 (§20).
  - Rendering past 1e100: C4–C6 and the cap in C9.

## 9. Log zoom carriers (C3b, #1838)

- **History** (`NavSnapshot`) carries the log past double, and Back restores it.
  - Two deep views with the same saturated `Zoom` are no longer the same snapshot.
- **Server request.**
  - `RenderRequestDto.zoomLog10` is written only past double.
  - On load a valid log rewrites `zoom` (saturated); a damaged one is dropped. This is the region rule (§8).
  - `SetZoomLog10` sets the log only past double.
  - `SetZoomLn` sets a video frame's zoom: `Exp(ln)` while that is a double (the old expression), the log past it.
- **Image tiles.**
  - Past double, a tile's zoom is the image's log plus its size ratio.
  - **Fix (from C2b):** each tile's exact centre is now the image's plus the tile's offset. The pixel-offset maths is `ExactCentre.TranslatePixels`, which the camera shares. Before, every tile of a plain (non-shared-orbit) plan carried the image's exact centre. The worker's load then rewrote the tile's limbs from it, so every tile rendered the image centre.
  - The shared-orbit path still gives every tile the image centre and zoom (it renders subrects), now with the log.
- **Video frames.**
  - The frame planner's natural-log range ends at the target's log past double; reverse swaps it.
  - The worker sets each frame through `SetZoomLn`. Before, `Exp` overflowed to infinity.
- **Slideshow planner:** a slide takes the log together with the zoom it takes (the slide's, else the template's).
- The worker's clone copies the log.
- **Not changed.**
  - **Live video keyframes:** those legs clamp to Ultra's `ZoomMax`.
  - **The host engine:** renders with its double zoom. Rendering past 1e100 is C4–C6.
  - **Batch:** C10.

## 10. The arbitrary-precision reference orbit (C4, #1840)

- **The builder.** `MandelbrotCalculator.ComputeReferenceOrbitAP` iterates z ← z² + c in `BigFloat`. Each step is three products and three sums, with one rounding per operation.
  - Precision: `PrecisionForZoom(ZoomLog10)` bits, about 1,061 at 1e300.
  - Centre: the calculator's exact centre, or its limbs exactly when it has none.
  - **Storage.** Each Z_n goes into the existing eight-limb arrays as its greedy OD split, so every consumer (perturbation, SA seed, LA / BLA, rebasing, periodicity) reads it as it reads the OD orbit.
    - 424 stored bits are plenty: perturbation reads Z_n at double precision.
    - High internal precision is what matters. It keeps the orbit on the true centre's orbit while the δ-amplification ∏|2Z| grows past OD's precision.
  - **Unchanged from the OD builder:** period detection (#1614) and the δ-amplification sum behind `MaxUsefulZoomLog10`.
  - **Cancellation** (#1427) is checked every 64 iterations, since a step costs microseconds.
  - **Cache key:** the exact centre plus the precision. Two exact centres with the same OD limbs are different orbits.
  - **Tier 3** is labelled `AP-PT`.
- **When.** Past `ApReferenceZoomLog10`, which defaults to 110.
  - That is above today's 1e100 cap, so the live app keeps the OD orbit, byte-identical, until C9.
  - It is also below OD's measured clean floor (about 1e120).
- **Calculator depth.**
  - `CenterExact` is stamped against the limbs, like the view state (§6). A jitter or pan of the limbs drops it, and its restore brings it back.
  - `ZoomLog10` / `SetZoomLog10` and `SetDepth(exact, log)`.
  - The live view sync and poster export pass the exact centre.
- **Recycling (tier 3).**
  - With a valid exact centre, the shift is the exact difference from the orbit's centre.
  - Otherwise, after a jitter, it is the limb difference against the orbit centre's own limbs, so OD's rounding of the exact centre (about 1e-128) is never taken for a shift. A sub-pixel jitter past about 1e125 is then lost: anti-aliasing at that depth is C5.
- **Validation** (`S1840ApReferenceOrbitTests`).
  - The first steps match the exact orbit, computed in exact dyadic arithmetic, to 2^-400.
  - At 1e110 the AP orbit equals the OD orbit to 1e-20 while the orbit's own amplification stays under 90 decades.
  - **Past OD:** two exact centres 1e-250 apart, with the same OD limbs, give orbits whose difference is δc · dZ_n/dc (the derivative computed in double) to 1e-6, over the steps where that product is between 1e-40 and 1e-20. The OD orbit cannot see that difference at all.
- **Not yet.**
  - **C5:** floatexp storage of Z_n near a nucleus, where doubles underflow; floatexp δ / BLA / SA; the per-pixel OD glitch fallback past OD; anti-aliasing jitter of the exact centre.
  - **C6:** GPU, and the cluster's shared-orbit blob.
  - **C10:** batch.

## 11. Floatexp pixels past double (C5a, #1842)

- **What a probe showed.** With C4's orbit, today's double-δ perturbation is already exact up to 1e290: iteration counts match direct BigFloat iteration of each pixel at c = i, and at centres needing digits past OD (1e250, 1e290).
  - Past double's range δ, dc and the pixel scale underflow: at 1e310 52 of 64 sampled pixels were wrong, at 1e400 63 of 64.
- **`FFMath.FloatExp`.** A double mantissa in [1, 2) with a long binary exponent. Each operation is one double operation on the mantissas plus exact exponent bookkeeping.
  - This is Kalles Fraktaler 2+'s "floatexp" idea (bibliography `#kf-floatexp`). The layout here is FF's own.
- **The pixel loop.** Past `FloatExpZoomLog10` (default 290), every pixel runs `ComputePixelPTFx`: Zhuoran's rebased loop as in `ResumePixelPTRebased`, with δ, dc and the full-orbit derivative in FloatExp.
  - Z_n is read from the orbit as double, the head limb of C4's split.
  - The pixel scale is 3.5 / max(W, H) · 10^-ZoomLog10, from the log.
  - Not used here: BLA, SA, LA, SIMD, orbit recycling (the shift is below double) and the pan lattice.
  - The label gains ` FX`.
- **The derivative.** It is about 1/pixel and reaches the colouring as ±∞ past double, which gives distance 0 and normals 0.
  - The double path already loses these well before this depth: normals past about 1e150, `FinalDr/Di` past 1e38, distance past 1e40 (#1843).
- **Validation** (`S1842FloatExpPerturbationTests`).
  - FloatExp arithmetic matches BigFloat to 2^-50 over ±1e5 decades.
  - Every sampled pixel's iteration count equals direct BigFloat iteration, exactly: at 1e310, 1e400 and 1e1000 around c = i, and at 1e600 around a centre past OD.
  - The loop forced at 1e150 is exact too. At 1e290 the double path is kept and still exact.
  - **Rebasing:** a frame with a minibrot off-centre (found by `NucleusFinder`, the reference outside it), forced onto the loop, matches direct iteration on all but 4 of 256 pixels (chaotic boundary rounding). Without the |z| < |δ| rebase 30 of 256 are wrong.
    - The default double path on that frame is off by ±1 to ±370 iterations on 48 of 256 pixels: that is BLA's approximation (#1346).
- **Cost.** One floatexp step is several double steps, and every pixel iterates in full. Speed comes in later C5 parts.
- **Later C5 parts.**
  - floatexp storage of Z_n near a nucleus;
  - BLA / LA in floatexp, and SA rescaling;
  - a SIMD path, or handing over to double once δ is normal;
  - the orbit-aware colouring path, which still builds the OD orbit;
  - AA jitter of the exact centre.

## 12. Floatexp BLA (C5b, #1845)

- **Why a new table.** The double BLA table can't hold its own numbers past double. B, the dc response ∏2Z over a skip, overflows; dcMax underflows; and the validity radii, shrunk by |B₁|·dcMax / |A₁| at each merge, underflow.
- **`BlaTableFx`** (`Engine/Math/BlaFx.cs`) is `BlaTable`'s single-precision form, with A, B and r in FloatExp. It is Zhuoran's BLA, credited in `Bla.cs`.
  - **Level 0:** A = 2·Z_n, B = 1, r = ε·|Z_n|.
  - **Merge:** A = A₂A₁, B = A₂B₁ + B₂, r = min(r₁, max(0, r₂ − |B₁|·dcMax) / |A₁|).
  - **Lookup:** the longest aligned level with R² > 0 and |δ|² < R².
  - FloatExp gains `Sqrt`, division, `Min` and `Max`.
- **The FX pixel loop** looks skips up by reference index, as `ResumePixelPTRebased` does, and applies δ' = A·δ + B·dc, d' ≈ A·d.
  - The table is cached per orbit generation, ε and dcMax (within 5%), like `EnsureBlaTable`.
  - The frame's work is reported in `LastPtWork` (skips, iterations saved, plain steps).
- **Validation** (`S1845FloatExpBlaTests`).
  - Where double holds everything (c = i, 256 steps, B ≲ 1e193), every entry's A, B and R² is within 1e-12 of the double table's, and 2,000 random lookups agree.
  - Past double (dcMax 1e-450), the table holds B past 1e308 and R² below 1e-308, all finite.
  - At 1e400 and 1e1000 BLA cuts the loop's work by at least 3×, and every pixel's count stays the plain loop's, which S1842 shows is the direct BigFloat count.
- **Accuracy.** BLA is an approximation. On S1842's minibrot frame it is off on 64 of 256 pixels, close to the double path's BLA (48 of 256, #1346). That test now runs the plain loop.
- **Not yet:** LA stages in floatexp (C5c), SA rescaling, SIMD.

## 13. Floatexp LA stages (C5c, #1847)

- **Why.** LA (the Imagina-style stage evaluator, #1623) is the default deep evaluator from its Auto crossover (about 1e30). Past double the double `LaStageTable` can't hold its numbers: A and B (∏2Z and the dc response over a step) overflow, and Thr / ThrC (the δ and δc validity radii) underflow.
- **`LaStageTableFx`** (`Engine/Math/LaStageTableFx.cs`) ports `LaStageTable`'s `La` struct and `Builder` line for line.
  - A, B, Thr and ThrC are in FloatExp (with a small `CFx` complex type); Ref stays double.
  - The stage 0 / NextStage walk, both period tests, the fixed-length stages (`LaStageBits`) and `Up` are unchanged.
  - Double's x / 0 = +∞ inside the builders' min(…) becomes `FloatExp.Huge`.
- **`EvaluateLaFx`** (`MandelbrotCalculator.LaFx.cs`) is the scalar `EvaluateLa` + `LaRunStages` in FloatExp, without the AT (#1627).
  - Kept: the stages with their δ and δc tests, climbing back on a rebase, ascending at stage boundaries (#1614), and the plain floatexp tail (BLA by reference index when `LaTailBla`).
  - Kept: the periodicity check at restarts (#1804), as bit-exact FloatExp repeats.
- **Dispatch.** The FX rows use it where the double path would (`LaWanted`, with `LaActive`'s gates). It is on by default past double, and the label reads `LA FX`. Otherwise they use the floatexp BLA loop (§12).
- **Validation** (`S1847FloatExpLaTests`).
  - **Same stages as the double table.** Where double holds every number (five reference orbits, both period tests), the floatexp table builds the double table's stages: same stage count, lengths, next indices, references and `Up`, with A, B, Thr and ThrC within 1e-12.
  - **Past double.** At c = i over 4096 steps the double table is not finite, while the floatexp one is.
  - **Frames.** At 1e400 and 1e1000, LA's work (LA + plain steps) is at most a third of the plain loop's, and every pixel's count is the plain loop's, which S1842 shows is the direct BigFloat count.
  - **Same steps as the double evaluator.** On S1842's minibrot frame (where double holds), the floatexp evaluator forced on and the double one take the same steps: 85,690 LA steps each, plain steps within 0.01 %, 5 of 1024 counts different.
  - **Not separately observed:** the rebase inside a stage (|δ| > |z| after a step). Without it, a large δ fails the next step's |w| < Thr test and drops to the tail, which rebases. No frame tried here shows the difference.
  - S1845's BLA tests now set `LaMode = Off`.
- **Not yet.** The AT in floatexp, SIMD (LA4) and SA rescaling.

## 14. The orbit-aware colouring path (C5d, #1849)

- **The problem.** `CalculateOrbitAwarePerturbation` (orbit palettes: traps, stripes, curvature …) built its own reference orbit through DD / QD / OD and never reached C4's BigFloat builder.
  - Past OD's coordinate floor its image was centred on the limbs, about 1e22 pixels from the exact centre at 1e150.
  - Past double, dc and δ underflowed.
- **Reference orbit.** Past `ApReferenceZoomLog10` its dispatch now takes the AP builder at the exact centre, as the main path does. Below that, its own dispatch and period tolerance are unchanged.
- **Past `FloatExpZoomLog10`:**
  - `CalculateOrbitAwareFloatExpRows` / `ComputePixelOrbitPerturbationFx` are C5a's loop with the palette sampling z = Z[m] + δ as a double, and the (δ, m) periodicity check on exact FloatExp repeats.
  - The GPU orbit kernel is not tried there.
  - The label gets ` FX`. The path also resets the LA flag, so its label no longer inherits a stale ` LA` from a previous main-path frame.
- **Validation** (`S1849OrbitPathDeepTests`, with `CurvatureAverageMap`). The oracle iterates each sampled pixel directly in BigFloat and feeds the same palette that orbit.
  - At 1e150 around a centre past OD, at 1e400 at c = i and at 1e60, every count is the direct count.
  - Every colour is within 2 per channel of the direct colour.

## 15. AA jitter at depth, and the tier-3 recycle (C5e, #1851)

- **The problem.** An AA sample (MSAA, offline or live TAA) moved the calculator's centre limbs by its sub-pixel jitter (`CentreJitter.Apply`, through OD).
  - **Past the AP threshold:** that limb write drops the exact centre (the stamp rule, §10). The sample was then rendered at the OD-rounded centre, a different image, and averaged in.
  - **Past double:** the double pixel scale the jitter uses underflows, so the jitter was lost.
- **The fix: `MandelbrotCalculator.PixelJitter`** (in pixels).
  - Where `JittersByPixel` (past `ApReferenceZoomLog10`), `CentreJitter.Apply` sets the pixel jitter instead of moving the limbs.
  - `CentreLimbs.Restore` clears it. `IsResolvable` is true there, so TAA keeps refining.
  - **Every deep path adds it to dc:** the HP rows (LA and LA4 included) through the recycle shift; the orbit-aware rows; the FX rows (main and orbit-aware) in pixel units.
  - BLA's dcMax grows by the jitter.
  - **Without a jitter** each addition is + 0.0, so output is byte-identical.
- **A note on OD.** Its limbs carry independent exponents, so a sparse value (1 + 1e-151) fits exactly. What OD can't hold is a dense value's digits past about 2^-424 relative. Tests that need "past OD" use a dense centre.
- **Validation** (`S1851DeepJitterRecycleTests`).
  - **The centre:** about two pixels from the Misiurewicz point near −0.1011 + 0.9563i (preperiod 4, period 1, multiplier ≈ 1.33). It is computed to full precision by Newton in BigFloat on z₃ + z₄, so it has dense digits and structure at pixel scale.
  - **The oracle:** direct BigFloat iteration of the true pixel positions, comparing counts and smooth values. LA / BLA are within 1e-2 (worst measured 2.6e-3); half a pixel moves smooth by about 0.1.
  - **Jittered samples** at 1e150 and 1e400 render the jittered pixels, and the exact centre survives the sample.
  - **A one-pixel exact pan** at 1e150 is a step the dense centre's OD limbs can't hold (the limbs are equal). It recycles the orbit (tier 3, the exact shift from C4) and renders the moved view.
  - Below the threshold the jitter still moves the limbs.

## 16. Periodic-reference detection past 1e154 (C5f, #1854)

- **Why not "Z_n storage near a nucleus".** Near a nucleus the stored Z_n (double OD limbs) can underflow below 1e-308. That only changes a pixel whose own |z| is that small at the same step, i.e. a pixel within about 1e-305 × (minibrot size) of the nucleus. That is far below a pixel in any real view, and inside the minibrot anyway.
  - The |z| < |δ| rebase already handles a reference passing close to 0.
  - The BLA / LA tables give no skip where Z_n underflows (R = 0, Thr = 0), which is safe.
- **The real gap.** Periodic-reference detection (#1614) stops the orbit at the period once |Z_p|² < (pixel size)². That square underflows double once the pixel is below about 1e-154.
  - Past about 1e154, a minibrot-centred view never detected its period.
  - The BigFloat orbit then ran to maxIter: a long build, more memory (#1828), and no wrap at the period.
- **The fix.**
  - Where the double square underflows (pixel < 1e-150), the tolerance is a FloatExp from the log zoom (`PeriodTolFx`). Above that, the double test stands, unchanged.
  - The AP builder compares |Z_n|² in FloatExp from its BigFloat Z_n (`BigFloat.ToFloatExp`).
  - `PeriodicOrbitStillValid` uses the same comparison, and the orbit-aware path sets the tolerance too.
- **Validation** (`S1854PeriodicReferenceDeepTests`).
  - At the period-3 nucleus (Newton in BigFloat on z₃ = 0), at 1e150, 1e200 and 1e400, the reference stops at period 3 (length 4) and the frame is the interior.
  - The next frame reuses the orbit: the generation is unchanged.
  - 1000 pixels off the nucleus no period is detected. The attracting cycle's point near 0 stays at about |Z₃|, about 1000 pixels. A one-pixel tolerance would wrongly take period 3.
  - `ToFloatExp` is the value to 2^-52.

## 17. Orbit arrays grow as built (C8a, #1856 / #1828)

- **The problem.** C8b and C9 raise iteration caps at depth, but the reference-orbit arrays (16 doubles per step) were sized to the cap rather than the orbit. At p951,094 the cap is 28.5 M, so 3.65 GB per calculator went to an orbit that stops at its period (951,095 entries). The zeroing of that allocation couldn't be cancelled either.
- **The fix.**
  - The CPU builders (DD / QD / OD / AP) start at min(cap + 1, 65,536) entries and double as the orbit grows, up to cap + 1.
  - After a build, a capacity more than 4× the built length is trimmed back.
  - Every reader indexes only up to `_refOrbitLen`; the GPU backends check `Length ≥ refLen`. Only the length matters.
  - The GPU QD orbit and the seeded cluster orbits write `maxIter + 1` entries at once, so they keep the up-front size.
- **Measured** on p951,094: 1,048,576 entries (134 MB) instead of 28.5 M (3.65 GB); orbit build 1.57 s.
- **Validation** (`S1856OrbitGrowthTests`), in every CPU tier:
  - A periodic orbit under a 10 M cap keeps the initial 65,536.
  - c = i over 300,000 steps (Z_n exact in double) is exact at every growth boundary.
  - A short orbit after it trims back, with the values kept.
  - A cancelled first build allocates only the initial size.
- **Not yet** (#1828): storing only the limbs each tier needs (DD writes zeros to six limb arrays).

## 18. Explore iterations past the tier cap (C8b, #1858 / #1426)

- **The problem.** An explore plan (minibrot jump, angle landing, Julia-morph step) asks for `SuggestedIterations(period)`, which is period × 100. It was capped at Extreme's 131,072, so a target of period above about 1,310 rendered short (#1426).
- **The fix.**
  - The cap is now `IterationLimits.ExploreCeiling` (50,000,000), a sanity bound.
  - A plan past the tier's own cap is flagged `BeyondTierCap`, and the Finder status says it will be slow.
  - Since C8a, a higher count costs time only: the orbit's memory follows the orbit, which stops at its period.
- **Rescue memo** (#1426 item 3). The solid-frame rescue memo now keys on all sixteen centre limbs (`CentreLimbs`) plus the zoom. With only three limbs per part, two views past 1e50 that differ only in limbs 3–7 matched.
- **Batch.** The hint becomes `PreferredIterations`, which region capture saves, and `--iterations` has no upper bound. A planned count therefore reproduces headless.
- **Not yet.**
  - The deep tier's own formula and cap: C9.
  - The partial-solid rescue and a higher rescue ceiling (#1426 item 2).

## 19. The Unbounded tier: zoom past 1e100 (C9, #1861)

- **`QualityPreset.Unbounded`** (`QualityTier.Unbounded`, last in `All`, also `QualityPreset.Deepest`).
  - **Zoom range:** ZoomMax is +∞.
  - **Iterations:** the formula is the Extreme tier's formula continued: 2048 + 1024 per decade, so the step from Extreme at 1e100 gives the same count. The cap is the explore ceiling (50 M). At 1e1000 that is 1,026,048 iterations.
  - **AA:** 2×2 plus 8 TAA samples. Extreme uses 4×4 + 32, but every deep sample is a full arbitrary-precision frame.
  - It renders through C3–C5: log zoom, the BigFloat orbit from 1e110, floatexp pixels with BLA / LA from 1e290, AA jitter by pixel, and periodic references at any depth.
- **Input.** Wheel, box and key zoom promote to the deepest tier (`NaturalQualityForZoom`, the clamp in front of the promotion). Past 1e100 the tier-change status warns that frames are slow.
- **The mini depth bar.** With an infinite ZoomMax its scale runs to 25 % past the current depth, at least to 1e120.
- **Video / slideshow.** The automatic tier choice and the "all regions" filter skip Unbounded as they skip Extreme (`Tier >= Extreme`).
- **Batch.** Until C10 (#1860, §20) `--zoom` stayed enforced at 1e100 and `--x` / `--y` carried OD limbs only.
  - Meanwhile the Command builder reported a live view past 1e100, or with an exact centre, as a fidelity gap pointing at #1860, and such a command was refused rather than rendering a wrong centre. C10 removed the gap.
- **Explore tools** (minibrot jump, finders, AutoExplorer) stay at Extreme's 1e100 until C7 (§22 for the minibrot finder).
- **Validation** (`S1861UnboundedTierTests`).
  - The tier's place and lookups, and its formula's continuity with Extreme's.
  - From Extreme just under 1e100, wheel steps promote to Unbounded with the warning and carry on past 1e100, then past double's range on the log, with no clamp.
  - The batch gaps for 1e150, 1e500 and an exact centre, and none for a shallow view (since C10: none for any of them).

## 20. Batch and the Command builder past 1e100 (C10, #1860)

- **`--x` / `--y`.**
  - A decimal with more digits than a double fills the eight OD limbs (`CoordNotation`, as the live CX / CY boxes). Up to 17 digits a decimal stays a double, as before.
  - The text is kept (`BatchOptions.CenterXText` / `CenterYText`). `CliExactCentre()` reads it again through `ViewCentreText.Apply`, the live boxes' parse: past OD's digits it is the exact centre, and the limbs are its OD split.
- **`--zoom`.**
  - Read through `ZoomLog.TryParse`, so past double's range it is written `1e500`; `BatchOptions.ZoomLog10` carries the log there and `Zoom` saturates.
  - The 1e100 ceiling (#1612) is now the deepest tier's, which is +∞; `--start-zoom` and `--kfr` follow. A non-positive zoom is refused.
- **`--quality Unbounded`.** In the catalog's choice list. A view past 1e100 renders in Unbounded even without it (`QualityForDepth`, with a note), as the live wheel promotes it.
- **Render path** (`BatchRenderer`).
  - `ResolveDepth`: the exact centre from `--x` / `--y`, else the region's (when its centre is used), and the log zoom from `--zoom`, else the region's `ZoomLog10Value`.
  - Still: `PosterRequest.CenterExact` and the new `PosterRequest.ZoomLog10`. `PosterRenderer` calls `SetDepth` on both Mandelbrot calculators it builds (the frame and the relief field). `DepthZoomLog10` hands the log on only while `Zoom` is still its saturated value, so a `with` that moves the zoom drops it, the view state's stamp rule (#1836).
  - Video: the zoom runs on its natural log, the target's from its log10 (`LnZoom`). Each frame's zoom comes back through `ZoomFromLn`: `Math.Exp` as before within double, else `double.MaxValue` and the log. The deep Mandelbrot frame and the region frame call `SetDepth`. The progress line and the output file name show the zoom from the log. The Misiurewicz loop keeps double zooms.
  - Slideshow: each leg's region zoom from its log, the same per-frame log, into the region frame.
  - `FractalRegion.CopyCentreTo(MandelbrotCalculator)` now sets the region's exact centre too.
- **Live export.** `FractalRenderHost.CreatePosterRequest` carried the exact centre (C4) but not the log zoom, so an export past double rendered at double.MaxValue. It now carries the log, with the View / Letterbox aspect factor added as `log10(factor)`.
- **Command builder.** `BatchCommandSnapshot.CenterExactX` / `CenterExactY` (plain decimal, `CoordNotation.FormatExact`) replace `HasExactCentre` and are emitted as `--x` / `--y` in place of the limbs. `--zoom` is written by `ZoomText`: the round-trip double, or past double the log's scientific form to 16 digits. The C9 gap is gone.
- **Validation** (`S1860BatchDeepTests`; `S1612` / `S1615` / `S1861` updated for the lifted ceiling).
  - A deep live view (340-digit exact centre, zoom 1e500.12, Unbounded) emits a command that parses back to the same exact centre at its precision, the log within 1e-12, the tier and the iterations; no #1860 gap. A shallow zoom stays the round-trip double.
  - `--x`: 30 digits gives eight limbs whose sum is the text to half a unit in its last place, with no exact centre; 17 digits stays one double; 340 digits gives the exact centre. `--zoom 2.5e1000` and `1e150` parse; `-1` and text are refused.
  - At 1e320.3, two pixels off the Misiurewicz point: `PosterRenderer` with the exact centre and log equals, pixel for pixel, a calculator set up as the live host sets it (limbs, Zoom, `SetDepth`). The frame has structure (more than 8 colours), and dropping the exact centre or the log changes it, so both carriers are load-bearing. A `with { Zoom = … }` drops the stale log.
  - `CreatePosterRequest` at 1e500: the log as is at the window's size, `500 + log10(factor)` in View mode at twice the width, none within double.
  - A region's exact centre reaches the calculator on `CopyCentreTo`.
- **Not unit-tested** (WinExe `BatchRenderer`): `ResolveDepth` / `QualityForDepth` wiring and the video / slideshow loops. They reuse the tested pieces: `BatchOptions`, `PosterRequest`, `SetDepth`, `ZoomLog`.
  - Smoke-run by hand: `--batch` stills at 1e150 and 1e320 on the 340-digit Misiurewicz centre (no `--quality`: promoted to Unbounded with the note) render the point's branch structure centred, and a 3-frame `--mode video` from 1e300 to 1e320 renders three distinct frames labelled 1E+300 / 1E+310 / 1E+320. The slideshow leg was not run (it needs a saved region past double).

## 21. Validation and docs (C11, #1864)

- **`--qdfloorsweep` past OD** (`Probes/QdFloorSweepProbe.cs`, moved from `Program.cs`).
  - The sweep now runs from 1e40 to 1e1000.
  - It adds the past-OD coordinates: the exact centre in BigFloat at `PrecisionForZoom` plus offset·scale (AP), and the floatexp deltas (fx).
  - It runs on two centres: the old sparse 3E47 centre and the dense 340-digit Misiurewicz point.
  - Measured:
    - QD collapses at 1e66 on both centres.
    - On the sparse centre OD never collapses, because its limbs' independent exponents hold centre + offset exactly (the "dense digits" lesson of C5e).
    - On the dense centre OD is clean to 1e132 and collapses at 1e134, so `ApReferenceZoomLog10 = 110` has 22 decades of margin.
    - AP and fx stay 128 / 128 to 1e1000.
- **Minibrot-vs-M mask** (`S1864DeepMinibrotMaskTests`).
  - The real-axis antenna minibrots next to c = −2 (periods 125 / 250 / 415, sizes ~1e-149 / ~1e-299 / ~1e-498, Size > 0 so no rotation).
  - The nucleus is found by Newton in BigFloat from c = −2, and the size by the atom-domain estimate (`NucleusFinder.AtomSize`'s recurrence, in BigFloat), independent of the renderer.
  - Each is rendered as the live host sets the calculator: limbs, Zoom, `SetDepth`, the Unbounded tier, `n·100` iterations. The view is framed through c = nucleus + Size·c′ at the home framing (centre −0.5, zoom 1).
  - Its in-set mask matches the main set's mask at 100 iterations, with IoU 0.94 at all three depths.
  - With the centre rounded to OD the minibrot leaves the frame (IoU 0.10 at 1e299 and 1e498), so the exact centre carries those two views.
  - Mutation checks against the renderer: an AP threshold that never engages, a floatexp threshold that never engages, a dropped exact centre and a dropped log zoom: 4 of 4 caught.
- **Visual regression** (#1292): three cases past 1e100, 128×128 with the Unbounded tier and a fixed `--iter`.
  - `mandelbrot-deep-1e150-exact`: the Misiurewicz point past OD's dense floor.
  - `mandelbrot-deep-1e320-floatexp`: the same point past double, with floatexp pixels.
  - `mandelbrot-deep-1e498-minibrot`: the period-415 antenna minibrot, a whole copy of the set.
  - The baseline gained exactly these three entries; the 42 existing hashes are unchanged.
- **Docs.** `Docs/Deep-Zoom-Perturbation.md` §10 summarises the path past 1e100. §1 points at it, and the tools table points at the moved probe.

## 22. Period detection and zoom to minibrot past 1e100 (C7a, #1866)

C7 is sliced as C5 was:
- **C7a:** the minibrot finder (this section).
- **C7b:** Misiurewicz snap and external rays / angles.
- **C7c:** auto-explore, Julia morph, angle landings and `.kfr`.

- **`DeepNucleusFinder`** (`Abstractions/Explore`). It runs the same methods as `PeriodDetector` / `NucleusFinder`, in BigFloat and floatexp.
  - **`Detect`:** ball arithmetic.
    - The centre's orbit runs in BigFloat at `PrecisionFor(radius)`, which is `PrecisionForZoom` plus 4 decades.
    - The ball radius runs in FloatExp, because a disk past 1e308 has no double radius.
  - **`FindMinibrot`:** the periods are scanned upward from the ball's candidate.
    - One reference orbit is built at the exact centre, in BigFloat, and stored as double.
    - Newton is perturbed around it, with ε, δ and the derivative in `CFx`.
    - A proposal is polished by Newton in BigFloat. Δc = fᵖ / f′ comes from the BigFloat z and the CFx derivative.
    - After convergence the atom size is computed. If the minibrot's framing (zoom 1/|Size|) needs more bits, the precision is raised and Newton runs on. Up to four rounds are allowed.
    - A root is accepted only inside the view disk and at its minimal period, the same invariants as `NucleusFinder`.
  - **`Find`:** Newton from a seed.
  - **`AtomSize`:** the orbit runs in BigFloat, with l and b in CFx.
  - **`Frame`:** returns the exact centre nucleus + Size·(−0.5) and the log zoom log10(framing) − log10|Size|.
- **`CFx`** moved from `Engine/Math/LaStageTableFx.cs` to `Abstractions/Math/CFx.cs`, gaining subtraction, division, reciprocal and magnitude. `BigFloat.FromFloatExp` was added.
- **`MinibrotJump.PlanDeep`:**
  - The plan carries `CenterExact` and `ZoomLog10` (`Center` and `Zoom` are their OD split and saturated double).
  - The tier comes from `QualityFor`. Past every finite cap that is now the deepest tier.
  - Iterations come from `ComputeIterationsLog10`, and are at least period × 100.
  - `At` keeps its 1e100 refusal for the other callers: angle landings and Julia morph, until C7c.
- **Control Center:**
  - **Routing:** past `ZoomLog10Threshold = 100`, the view disk is built from the exact centre (or the limbs' own value) and the log radius. Detect period and Zoom to minibrot then route to the deep finder.
  - **Applying a plan:** `ApplyPlan` sets the exact centre and, past double, the log zoom.
  - **Handoff:** a minibrot found by the OD finder in a shallower view whose framing lies past 1e100 is no longer refused ("beyond the deepest zoom"). Its OD nucleus seeds `DeepNucleusFinder.Find`, which carries it on.
  - **Angles:** past 1e100 they are not shown. The angle code is OD-only until C7b.
- **Validation** (`S1866DeepNucleusFinderTests`).
  - **Independent targets:** the real-axis antenna minibrots next to −2 (S1864), with their nucleus and size computed here independently. They are viewed 3 sizes off, in a disk of 20 sizes, at ~1e149 / ~1e299 / ~1e498.
    - The ball candidate is ≤ n.
    - `FindMinibrot` returns period n, the nucleus to 1e-6 of a size, and the size to 1e-6.
    - `Frame` returns nucleus − Size/2 and −log10 Size.
  - **A complex case:** a 64×64 view at 1e150 on the 340-digit Misiurewicz point. The minibrot found there is verified in BigFloat at +128 bits, independently of the finder:
    - it is a root of fᵖ (the Newton step is under 1e-6 of the size);
    - no proper divisor is a root;
    - it is inside the view;
    - its size matches the atom-domain estimate to 1e-6, with a nonzero imaginary part.
  - **Agreement with `NucleusFinder`** at ~1e-70, where OD holds: the same period, the nucleus to 1e-6 of a size, and the size to 1e-9.
  - **Refusals:** fᵖ with 2n gives LowerPeriod (n). A disk 100 sizes away gives LeftView (Newton wanders), and so does a root 3 sizes from the seed with a disk 1 size wide (within the wander limit, outside the view). A disk at −2.1 gives Escaped.
  - **`PlanDeep`** at 1e498 gives the Unbounded tier, the framing's exact centre and log, `Zoom` = double.MaxValue, the iteration hint, and the locked case.
  - **The OD handoff:** the period-250 nucleus rounded to OD as the seed is carried by `Find` to the true nucleus.
