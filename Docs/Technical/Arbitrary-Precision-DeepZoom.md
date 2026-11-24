# Arbitrary-precision deep zoom — Design Notes

Epic #1428: lift the 1e100 zoom cap (option C, arbitrary precision). The slice issues are
the canonical task list; this doc backs them.

**Status: complete (2026-11-18). All slices are merged; the close-out is §34, and the open
follow-ups are listed there.**

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
| C7a period detection + zoom to minibrot past 1e100 | #1866 | §22 (merged, PR #1867) |
| C7b Snap to spiral (Misiurewicz) past 1e100 | #1868 | §23 (merged, PR #1869) |
| C7c angle → landing past 1e100 (deep ray trace) | #1870 | §24 (merged, PR #1871) |
| C7e location → angles past 1e100 | #1873 | §25 (merged, PR #1874) |
| C7d .kfr import + Julia morph past 1e100 | #1876 | §26 (merged, PR #1877) |
| C7f auto-explore + loop video past 1e100 | #1878 | §27 (merged, PR #1879) |
| C6a GPU float-exp LA kernel past 1e100 (to 1e290) | #1880 | §28 (merged, PR #1881) |
| C6b GPU float-exp pixel scale past 1e290 | #1882 | §29 (merged, PR #1883) |
| C6c server engine + cluster shared-orbit blob past 1e100 | #1884 | §30 (merged, PR #1885) |
| C5g LA approximation transform (AT) past double | #1886 | §31 (merged, PR #1888) |
| C5h float-exp LA tail in double where exact | #1889 | §32 (merged, PR #1890) |
| GPU float-exp LA kernel on minibrot views (bug) | #1887 | §33 (merged, PR #1891) |
| C5i LA stages in double | (no issue) | tried, not shipped: no gain (§34) |
| SA rescaling | (no issue) | measured, not built (§34) |
| Close-out | #1428 | §34 |

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
  - **The host engine:** renders with its double zoom. Rendering past 1e100 is C4–C6. *(C6c, §30: the engine now takes the exact centre and the log zoom.)*
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
  - **C6:** GPU, and the cluster's shared-orbit blob. *(Done: §28–§30.)*
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
- **The FX pixel loop** looks skips up by reference index, as `ResumePixelPTRebased` does, and applies δ' = A·δ + B·dc, d' = A·d + B (the + B since #1782; it was dropped before).
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
- **Not yet.** The AT in floatexp, SIMD (LA4) and SA rescaling. *(The AT: C5g, §31.)*

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

- **`--aa-samples 1|4|9|16` (#2016).** Past 1e100 a still renders in Unbounded: 2×2 supersampling plus 8 TAA samples. The flag replaces the preset's supersample count at any depth, and `1` also skips temporal AA (one sample per pixel). Benchmark kits that compare renderers at one sample per pixel asked for it (rith's 0.7.2 bench, bibliography `#rith-ff-071-benchmark`). At 320×180, 1e120, 2,000 iterations: 3.8 s → 0.65 s wall.

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
  - **Handoff:** a minibrot found by the OD finder in a shallower view whose framing lies past 1e100 is no longer refused ("beyond the deepest zoom"). `DeepNucleusFinder.CarryOn` runs Newton in BigFloat from its OD nucleus, on the same view disk the OD search ran on.
    - The disk must be the view's, not one sized from the minibrot. OD places the nucleus only to about \|c\|·1e-125, and such a minibrot can be far smaller.
    - Smoke test (2026-11-17), the saved region "Mandelbrot - 1E100 Test" at zoom 1e100: the period-57,563 minibrot there has size about 1.6e-200. A disk of 10 sizes around the OD nucleus missed it, so the jump fell back to the refusal.
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
  - **That region** (zoom 1e100, 1920×1080 view): `NucleusFinder` finds period 57,563, `Plan` refuses it, and `CarryOn` carries it on. The result has the same period, is framed past 1e100 in the Unbounded tier, and stays within OD's grain of the OD nucleus.

## 23. Snap to spiral past 1e100 (C7b, #1868)

C7 was re-sliced once the ray and angle work was sized:
- **C7b:** Snap to spiral (this section).
- **C7c:** external rays and angles. `ExternalRay.Land`, `LocationAngles` and the angle landings trace rays at every bit of depth.
- **C7d:** auto-explore, Julia morph and `.kfr`.

- **`DeepMisiurewiczFinder`** (`Abstractions/Explore`) is `MisiurewiczFinder` in BigFloat and floatexp.
  - **Reference orbit** at the exact click point.
    - The orbit is kept in BigFloat, because the differences Z_a − Z_b cancel to almost 0 near the point and must be exact.
    - Double copies of it are kept as well.
    - dZ/dc is in CFx.
  - **Candidates:** each (k, p) gets the first-order distance |Z_{k+p} − Z_k| / |dZ_{k+p} − dZ_k|.
    - A double difference whose lower bound already rules a pair out skips the exact BigFloat difference.
    - Only the smallest k per p is kept, and the simplest pairs are tried first.
  - **Newton:**
    - Deflated Newton runs perturbed in CFx. Each difference is the exact one plus (ε_a − ε_b).
    - It is then polished in BigFloat, with Δc = 1 / (G′/G) computed in CFx.
    - The precision is `PrecisionForZoom(reach + 30 decades)`, so the snapped point still holds while the view zooms on into it.
  - **Acceptance:** within the accept radius of the click, and (k, p) minimal there: no smaller preperiod and no divisor period is also a root.
  - **The multiplier** λ = Π 2zᵢ is in CFx.
- **Control Center:** past 1e100 the click becomes an exact world point (`ViewCamera.ExactWorldFromScreen`), with the reach as the log of 10 pixels. The view snaps to the exact point.
  - The repeat factor, turn and Julia preview are shown as before.
  - The loop video is not offered past 1e100 (C7d), because its batch path keeps double zooms and the OD centre. The angles are not shown either (C7c).
- **Validation** (`S1868DeepMisiurewiczFinderTests`).
  - **M(4,1) near −0.1011 + 0.9563i**, refined independently in BigFloat at 1600 bits. Clicks 3.6 px off it at 1e150 and 1e320 snap to M(4,1).
    - The point matches to 2⁻⁶⁶ of the reach, far past OD's grain.
    - λ = 2·z₄ to 1e-12.
  - **The tip of the period-125 antenna minibrot** (~1e-149), clicked 1% of a size inside it, snaps to a point with k + p > 125.
    - Independently, in BigFloat: z_{k+p} = z_k there.
    - No smaller preperiod or divisor period holds.
    - It is within reach of the click.
  - **Agreement with `MisiurewiczFinder`** at 1e60, where OD holds: the same (k, p), the same point and the same λ.
  - **Refusals:**
    - M(4,1) refined as M(5,1) reports LowerPreperiod 4.
    - M(4,1) refined as M(4,2) reports LowerPeriod 1.
    - Refining towards an anchor 5 reaches away fails (an anchor 0.5 reach away succeeds).
    - A click 1000 reaches away finds nothing.
  - **Mutation checks: 6 of 7 caught.** The one that survived drops the deflation terms in the perturbed proposal step only.
    - The BigFloat polish deflates on its own, so it would still refuse a wrong root.
    - No tested click lies near an unwanted root that the proposal could slide onto.

## 24. Angle → landing past 1e100 (C7c, #1870)

C7 was re-sliced once more:
- **C7c:** angle → landing (this section).
- **C7e:** location → angles. This is `LocationAngles`' root walk and outward traces in BigFloat, verified with this slice's `Land`.

- **`DeepExternalRay`** (`Abstractions/Explore`) is `ExternalRay` in BigFloat and floatexp.
  - **`Tracer`:** each bit rebases the reference to the current ray point as an exact centre and takes its orbit in BigFloat, stored as double. The substeps solve for Δ in CFx. The reference precision follows the ray: a bit that moved it by 2^-m keeps it to m + 128 bits.
  - **`Land`, periodic angle:** `DeepNucleusFinder.Find` runs with a growing reach. The result is accepted within RootReach (2) atom sizes of the seed.
  - **`Land`, preperiodic angle:** `DeepMisiurewiczFinder.Refine` runs over the divisors of the period, with the reach at ×4 … ×4096 of the trace step, as ExternalRay does.
    - **Adaptive:** once a period moves the ray by 1e-8 of its first cycle, the landing is tried. If it fails, the trace walks one more period and tries again.
    - **Why:** a long preperiod (a point tuned deep into a minibrot) needs the seed much closer than the 1e-8 stop gives. A wider reach instead lands on neighbouring M(l+1, q) points of the wrong period, measured at n = 30.
- **Control Center (Go):** the OD `ExternalRay.Land` runs first. The deep trace takes over when:
  - the OD landing fails;
  - a component landing has a framing past 1e100;
  - or a Misiurewicz landing's context zoom is past 1e100.

  It then jumps to the exact minibrot framing, or to the exact point at the log context radius.
- **Validation** (`S1870DeepExternalRayTests`).
  - **A periodic angle past OD:** `.(0 1¹²⁴)`. OD fails on it. The deep trace lands on the period-125 antenna nucleus, checked against the independent BigFloat nucleus to 1e-6 of a size, and the size to 1e-6 (~1e-149, 4 s).
  - **Agreement with OD:** at n = 30 the deep and OD landings agree.
  - **A tuned preperiodic angle:** M(4,1)'s `.001(010)`, tuned into the period-60 antenna minibrot (0 → 0 1⁵⁹, 1 → 1 0⁵⁹). This angle has preperiod 180 and period 180, and the OD trace fails on it.
    - It lands on M(181, 60).
    - That point is at nucleus + Size·M(4,1) to 2% of the size: the copy of M(4,1) in the minibrot, by tuning theory.
    - Independently in BigFloat: z_{k+p} = z_k there, and no smaller preperiod or divisor period holds (~1e-70, 8 s).
  - **Refusal:** angle 0 is refused (the cusp).
  - **Mutation checks: 4 of 5 caught.** The survivor drops the RootReach check on a periodic landing.
    - That check guards against Newton reaching a neighbouring period-p component, as in ExternalRay.
    - No tested angle lands next to one.
  - **Measured, not in the suite:**
    - The same tuned angle at n = 125 lands M(376, 125) at ~1e-146, but takes 46 s. The repeated landing attempts dominate.
    - A precision raise in `Refine` by log₂ max |dz/dc| was tried here and dropped: the n = 60 and n = 125 landings succeed without it.
    - At n = 30 the OD trace fails both the tuned angle and the antenna tip angle `.1 0²⁹ (0 1²⁹)`. That tip is a real-axis point among many Misiurewicz points where Newton creeps, and the deep trace fails it too.

## 25. Location → angles past 1e100 (C7e, #1873)

- **`DeepLocationAngles`** (`Abstractions/Explore`) is `LocationAngles` in BigFloat and floatexp.
  - **`Root`:** the interior coordinate b walks 0 → 1. Newton runs on (z₀, c), perturbed about the nucleus orbit (BigFloat, stored as double), with the offsets in CFx.
  - **Probe rings** sit around the root (component) or the point (Misiurewicz). Each probe is escape-tested in CFx about the root's own orbit, and the shallowest deep-enough probes are traced outward.
  - **`TraceOut`** takes the probe's exact orbit in BigFloat to its escape. It then runs `DeepExternalRay.SolveDepth` on the offset in CFx, reading one digit per depth.
  - **Verification:** each candidate is landed with `DeepExternalRay.Land`. It is kept only if it lands back within 1% of the component's size, or within 1e-6 of the scale of the point.
- **Control Center:**
  - After a deep Zoom to minibrot (and the OD hand-off), the angle pair and the angled internal address are shown.
  - After a deep Snap to spiral, the point's angles are shown.
  - The status says this can take a while.
  - The loop video stays OD-only (C7d).
- **Validation** (`S1873DeepLocationAnglesTests`). The expected answers come from theory, not from FF.
  - **Antenna minibrot, period 125** (~1e-149):
    - The angles are exactly `.(0 1¹²⁴)` and `.(1 0¹²⁴)`.
    - The root offset is Size/4 to 5% of the size: the main cardioid's cusp, carried by the atom map.
    - Takes 17 s.
  - **Agreement with OD:** at period 30 the result equals `LocationAngles`.
  - **M(4,1)'s copy in the period-10 minibrot** gets exactly its three tuned angles, ascending. Takes 10 s.
  - **Measured, not in the suite:**
    - The same three tuned angles come back at period 30 (~1e-34, 48 s) and period 60 (~1e-70, 145 s).
    - Verification lands each candidate, so the time is #1872's.
  - **Reach** (smoke test, 2026-11-18). Four saved minibrots past 1e100 (periods 7,805, 22,320, 57,563 and about 1,100) searched, then reported "could not be verified".
    - A probe must escape no earlier than `ProbeMinDepth` (4) periods within the `RingMaxDepth` (4,000) iteration ring orbit. Past period 1,000 no probe qualifies.
    - Verifying a candidate lands a ray of about 10 bits per period digit, at a cost that grows with its square.
    - `LocationAngles.MaxAnglePeriod` (= 4000 / 4) and `WithinReach` now stop both the OD and the deep searches at once past it, for components (the period) and Misiurewicz points (preperiod + period).
    - The Control Center says so instead of searching.
    - Angles past that reach are a research item (#1875).
  - **Mutation checks: 3 of 5 caught.** Two survived:
    - **Preperiod split off by one:** this mutant is equivalent. `ExternalAngle.FromBits` reduces an angle to its canonical form, and the extra preperiod digit of a correct trace is the one the cycle has there.
    - **Dropping the landing-distance check:** this is a guard. Every candidate traced in the tests is genuine.

## 26. .kfr import and Julia morph past 1e100 (C7d, #1876)

Auto-explore and the loop video are render-path work, so they moved to C7f.

- **`KfrLocation`:**
  - Zoom is read through `ZoomLog.TryParse` (`KfZoomLog10`; `KfZoom` saturates past double).
  - `FfZoomLog10(w, h)` is the log of #1615's conversion. `FfZoom` is unchanged in double's range and saturated past it.
  - `CentreExact()` returns the exact centre from Re / Im when they hold more digits than OD can. It is conjugated for `ImagPointsUp: 1`, as `Centre()` is.
- **Control Center import:** the 1e100 refusal is gone. The import sets the exact centre, the log zoom past double, and the tier `QualityFor` gives (Unbounded past 1e100).
- **`--batch --kfr` (`ResolveKfr`):** when neither --x nor --y is given, the file's exact centre becomes `CenterXText` / `CenterYText`, so `CliExactCentre` reads it. The log zoom becomes `ZoomLog10`.
- **`MinibrotJump.AtDeep`** plans a view change to an exact centre at a log zoom. `PlanDeep` is now built on it.
- **`JuliaMorph.PlanDeep`** takes a step toward a `DeepNucleusResult`.
  - The new log zoom is (1−α)·log z₀ + α·log z_Q, centred on the exact nucleus.
  - It has no depth limit. NotDeeper is reported as before.
- **Control Center morph:**
  - Past 1e100 a click maps to the exact target with a log reach. `DeepNucleusFinder.FindMinibrot` searches there, and `PlanDeep` takes the step.
  - Below 1e100, a step that would pass 1e100 (TooDeep) carries its minibrot on with `CarryOn` on the search disk and takes the deep step, instead of refusing.
- **Validation** (`S1876KfrAndMorphDeepTests`). Mutation checks: 6 of 6 caught.
  - **`.kfr` at 1.5E150 with a 340-digit centre:** the log zoom and FF's log zoom are right, `FfZoom` matches the #1615 formula in double's range, and the exact centre is read.
  - **`.kfr` at 2.4E1000:** both zooms saturate and the logs are right.
  - **`ImagPointsUp: 1`:** gives the conjugate.
  - **A short centre** gives no exact centre.
  - **`--batch --kfr` at 3E500:** `CliExactCentre` is the file's centre, and `ZoomLog10` is FF's log zoom for the batch frame.
  - **Deep morph step:** from a 1e120 view toward the period-250 antenna minibrot (~1e-299, found by `DeepNucleusFinder`):
    - it lands at the log midpoint on the exact nucleus, in the Unbounded tier, with the iteration hint;
    - with α = 1 it lands at the minibrot's own zoom;
    - from a view deeper than the minibrot it reports NotDeeper.

## 27. Auto-explore and the Misiurewicz loop past 1e100 (C7f, #1878)

This is the last explore slice. Every explore tool now works past 1e100. The remaining limits are angle search past period 1,000 (#1875) and slow deep preperiodic landings (#1872).

- **`ExploreView`** carries `Exact` and `ZoomLog10` (`ZoomLog10Value` gives the log at any depth).
- **`AutoExplorer.Children`:**
  - Past `DeepZoomLog10 = 100`, each child's exact centre is the parent's plus the grid or jitter offset. The offset is the fraction × the extent (computed from the log, in floatexp) and is added in BigFloat at `PrecisionForZoom(child + 10)`.
  - The RNG draws come in the same order, so a seed reproduces.
  - Shallower children are unchanged.
- **`AutoExplorer` level stop and `Select`:**
  - The level stop compares log zooms past 1e100.
  - `Select` measures separation exactly between exact views.
- **`MaxZoomFor(Mandelbrot)`** is the deepest tier, which has no limit.
- **`ExploreProbe`** gives the request `CenterExact` and `ZoomLog10`, takes iterations from the log, and calls `SetDepth` on its calculator.
- **Control Center explore:**
  - Past 1e100 it starts from the exact view.
  - Going to a candidate applies its exact centre and log zoom.
  - Candidate labels show the log zoom.
- **`--batch --explore`** starts from the exact `--x` / `--y` and log `--zoom` (`ResolveDepth`), for stills and video alike.
  - It renders the explored view exactly. The explored view feeds `deepExact` and `targetZoomLog10` in video.
  - It prints log zooms.
  - `--explore-regions` writes the exact centre (`SetCentreExact`) and `ZoomLog10`.
- **`MisiurewiczLoopRenderer.RenderFrame`:**
  - Past double the square's zoom is log + log10(|λ|^(cycles·t)) + log10(max(W,H) / side). This also covers a loop that starts in double's range and passes 1e308 mid-loop.
  - In double's range it is the old product, byte-identical.
- **Batch loop frames** carry the exact point and the start's log zoom.
- **Control Center loop:** a deep Snap to spiral now offers the loop video. Loop video seeds `--x` / `--y` with the exact decimal.
- **Validation** (`S1878DeepExploreLoopTests`). Mutation checks: 5 of 5 caught.
  - **Children:** the children of an exact view at 1e150 (the 340-digit point) are at the grid offsets, computed independently in BigFloat, to 2⁻⁴⁰ of a pixel. Shallow children are unchanged.
  - **Probe:** the probe's smooth field at 1e150 equals a live-style calculator's (limbs, Zoom, `SetDepth`). It differs without the exact centre.
  - **Real run:** a 2-level run from 1e150 ends at 1e151.2 with exact views, reproduces for its seed, and keeps a beam of 2.
  - **Loop:** a loop frame at 1e320.25, t = ½, equals a direct render at the expected log zoom, turned and cropped.

## 28. The GPU float-exp LA kernel past 1e100 (C6a, #1880)

The first GPU slice. Before it, every frame past 1e100 rendered on the CPU.

- **What held the route at 1e100.** Only the constant did (`MaxGpuLaFloatExpZoom`). The float-exp LA kernel (#1664, opt-in `FF_GPU_LA_FX=1`) has float-exp numbers, the pixel scale goes up as float-exp (`GpuLaFxParams.Make` splits the double scale), and the reference orbit is O(1) (the AP orbit past 1e110, C4). The CPU keeps the double-δc rows up to `FloatExpZoomLog10` (1e290, C5a).
- **The limit is now 1e290.** Past it the CPU float-exp rows run (the double scale underflows). The route says so: `zoom > 1e290`. A float-exp scale for the kernel is a later slice. *(C6b, §29: it now runs there too.)*
- **The float-exp LA table on the GPU.** Deep, on long orbits, A and B (∏2Z, the dc response) overflow the double LA table. The CPU frame then drops LA (it renders `AP-PT`), and so would the GPU route. The kernel now takes the float-exp table (`LaStageTableFx`, C5c) there, without the AT, as the CPU float-exp rows do.
  - `LaStageTableFx.GpuStepsFloatExp` / `GpuInts` use the double table's layout.
  - `GpuFloatExp.Encode(CFx)` gives one shared exponent. It writes the same words as the double encoder wherever double holds the value, and `FloatExp.Huge` (the builders' x / 0) becomes +∞ as the double would.
  - `LastFrameUsedGpuLaTableFx` reports it.
- **D3D11 and Vulkan** share the host code.
  - `--gpulaprobe` with `FF_GPULA_EXACT=1` renders the exact view at 1e150 and 1e285 on both.
  - On the GT 710 at 480×270, the iteration counts differ from the CPU frame's on 0.23–0.29 % of pixels. That is the fp32 mantissa, inside #1664's budget.
  - Speed is about the CPU's: ×0.77–1.17.
- **Unchanged:** the fp64 and plain kernels and the GPU orbit kernel keep their 1e50 limit.
- **Validation** (`S1880GpuLaFloatExpDeepTests`, Vulkan; skipped without a device). Mutation checks: 6 of 6 caught.
  - **The view:** the dense 340-digit centre near M(4,1), at 1e150 (double table) and 1e285 (float-exp table). The frame runs on the float-exp LA kernel.
  - **Truth:** 32 sampled pixels iterated directly in BigFloat. The GPU frame is exact on all 32 at both depths (the test allows one off), and the CPU frame is exact on all of them, which checks the oracle.
  - **Against the CPU frame:** 0.46 % / 0.08 % of the pixels' counts differ, with no in/out flips.
  - **Past 1e290:** the frame stays on the CPU (`FX`), with the route's reason.
  - **Encoding:** a FloatExp encodes as its double does, bit for bit. Past double it keeps its exponent, and Huge encodes as +∞.

## 29. The GPU past 1e290: a float-exp pixel scale (C6b, #1882)

Past 1e290 the pixel scale underflows double, so every frame rendered on the CPU float-exp rows (C5a). The float-exp LA kernel already holds the scale as a float mantissa and an int exponent (`gScaleM` · 2^`gScaleE`), and its δ, dc and LA coefficients are float-exp. Only its host API took a double scale.

- **`IGpuKernel.RunPerturbLaFloatExp`** gains `scaleExp2`: the scale is `scale` · 2^`scaleExp2`.
  - `GpuLaFxParams.Make` packs `ScaleE` = ILogB(scale) + scaleExp2. It writes the same words as the double scale wherever double holds it.
  - It refuses past `MaxScaleExponent` (2^±500,000, zoom ~1e150,000). That keeps the exponent far from int overflow and from the kernels' zero exponent.
  - D3D11 and Vulkan pass it through.
- **`TryRunGpuFloatExpPixels`** (`MandelbrotCalculator.GpuFx.cs`) runs before the CPU float-exp rows.
  - **Route:** the C6a float-exp LA route (`FF_GPU_LA_FX=1`, float-exp the device's deep kernel, LA wanted), on the float-exp LA table without the AT, as the CPU rows run it.
  - **Offsets:** dc = (x + offset) · scale, with the AA pixel jitter (#1851) in the offset, in pixels.
  - **Derivative:** the kernel writes dz/dc · 2^gScaleE (#1660). The host turns it back into double by 2^-gScaleE. Past double's range that gives ±∞, as the CPU rows' `FloatExp.ToDouble` does (#1843).
  - **Fallback:** where it declines (switch off, LA off, pan reuse, a tile cap, a table that isn't finite) the CPU rows run, and the route says why. A dispatch failure takes the usual GPU-failure path.
  - **Labels:** `LastFrameUsedGpuFloatExpPixels`. The precision label reads `… LA FX`.
- **Fixed in passing:** `LastFrameUsedGpuLaTableFx` (C6a) was never cleared between frames. The "deep-zoom perturbation engaged" log printed 2e+308 past double; it now prints the log zoom.
- **Speed** (`--gpulaprobe`, `FF_GPULA_EXACT=1`, 480×270, the 340-digit view):

  | Zoom | GT 710, Vulkan | Intel UHD, D3D11 |
  |---|---|---|
  | 1e320 | 84 ms vs 770 ms (×9.1) | 75 ms vs 343 ms (×4.6) |
  | 1e600 | ×1.45 | ×0.90 |

  Iteration counts differ from the CPU's on 0.32–0.33 % of pixels at 1e320 and 0 % at 1e600. At 1e600 the view is near-uniform, so the timing there is mostly fixed cost.
- **Validation** (`S1882GpuFloatExpPixelsTests`, Vulkan; skipped without a device). Mutation checks: 7 of 7 caught.
  - **Cbuffer:** a split scale packs as its double, keeps its exponent past double, and is refused past the range.
  - **Views:** S1842's views: c = i at 1e320, 1e600 and 1e1000, and the centre whose offsets OD can't hold, at 1e600.
    - The frame runs on the kernel.
    - Against direct BigFloat iteration, the GPU is exact on every sampled pixel (64 / 64 / 36 / 64). The CPU float-exp frame is exact on them too, which checks the oracle.
    - Against the CPU frame, no pixel's count differs.
    - Smooth values sit in [n − 3.17, n − 2.17].
  - **Jitter:** a pixel jitter moves the GPU frame as it moves the CPU's (0 pixels differ; 117 of 576 differ from the unjittered frame).
  - **Fallback:** with LA off the frame stays on the CPU, with the reason.
  - **C6a's test:** `S1880`'s past-1e290 test now checks the switch: on, the kernel runs; off, the CPU rows run with the reason.

## 30. The server engine and the cluster's shared orbit past 1e100 (C6c, #1884)

This is the last C6 slice, so the GPU and cluster parts of the epic are done.

- **The server engine dropped the depth.** `HostFractalRenderEngine`, the server's only `IFractalRenderEngine`, never passed the request's exact centre or log zoom to the poster request. C2b and C3b carry both on the DTO and through the tile planner. As a result, a server or cluster tile past OD rendered the OD-rounded centre, and past double it rendered at a saturated zoom.
  - **Image renders** now set `CenterExact` (`req.CentreExact()`) and `ZoomLog10`. A region's exact centre already reached the request (#1833).
  - **Video frames** take the OD limbs X4–X7 (D-6b2; the frame renderer dropped those too), the exact centre, and, past double, a log10 zoom interpolated as the batch video does (C10). Within double the frames are unchanged.
- **The shared reference orbit past 1e110.**
  - D-6b / D-6b2 ship DD / QD / OD orbits up to 1e115. Past `ApReferenceZoomLog10` (110) every tile built the BigFloat orbit itself (C4), O(maxIter · prec²) once per tile. Between 1e110 and 1e115 the master shipped an OD orbit the tile's tier-3 cache could not use.
  - **Master.** `ComputeReferenceOrbitAPPublic` builds the tier-3 orbit once, as the frame of the full image would. The full image's pixel size sets the period tolerance (#1614 / #1854), and a tile's is the same.
  - **Wire format.** `ReferenceOrbitBlobCodec` format version 2 is the OD layout plus an AP extension: the precision, the period data, the useful-zoom bound, and the exact centre as decimal text at its bits. A version-1 decoder refuses it, so an old worker builds the orbit itself.
  - **Glue.** `ServerHost/SharedOrbitAp.cs` converts between the engine's `OrbitAP` and the blob; the Server library stays Engine-free.
  - **Worker.** `SeedReferenceOrbitAP` seeds the tier-3 cache. The calculator's own cache check (exact centre, precision, cap, period tolerance) decides whether to use it, and a seed for another view is rebuilt, never used.
  - **Planner.** It ships the orbit past 1e110 at any depth, on the log zoom. `MandelbrotCalculator.DefaultApReferenceZoomLog10` and `TilePlanner.SharedRefOrbitApZoomLog10` mirror each other.
- **Validation** (`S1884SharedOrbitApTests`; the host files are linked into Server.Tests). Mutation checks: 12 of 12 caught.
  - **The orbit:** the master's orbit is the tile's own, limb for limb, with the same period data, at 1e150 (the dense 340-digit centre) and 1e320.
  - **The blob:** it round-trips every field, including a period FloatExp, and is refused when truncated. OD blobs still decode.
  - **A seeded tile:** it renders byte-identically to an unseeded one without building an orbit. A seed one pixel off is rebuilt, with the same pixels.
  - **The period** (the period-2 nucleus c = −1, where Z₂ = 0 exactly).
    - At the nucleus the period ships and is used.
    - Just off it, where |Z₂| lies between the image's pixel and a 1-pixel frame's, the master detects no period, as the tile doesn't.
    - Deep minibrot nuclei don't show their period at the zoom's precision: the rounding, amplified, exceeds the pixel.
  - **The engine:**
    - It renders an exact request at 1e150 and 1e320 as the poster path does. Without the exact centre the frame differs.
    - A seeded tile request renders as an unseeded one, and a poisoned seed changes it, which shows the seed reaches the calculator.
    - A video frame at 1e320 is the direct render.

## 31. The LA approximation transform past double (C5g, #1886)

This is the first of the deferred C5 speed items.

- **Why it fits past double.** The AT (#1627, Imagina's) iterates z′ ← z′² + c′ in renormalised coordinates, z′ = A·z and c′ = RefC + C·δc.
  - z′ stays below Esc (≤ 2^128), and c′ is about the size of the view's extent in c′. So the AT iterates in plain double at any depth.
  - Only the conversions need FloatExp: C·δc on the way in, and z = z′/A and dz/dc = (dz′/dc′)·C/A on the way out.
- **`LaAtFx`** (`Engine/Math/LaAtFx.cs`) is `LaAt`'s `Create` / `Usable` / `Select` over `LaStageTableFx`.
  - A, B, C = A·B, 1/A and ThrC are in FloatExp. RefC and Esc² must be finite doubles, or the AT is not usable.
  - Like `LaAt.cs`, it derives from Imagina.
- **`EvaluateLaFx`** runs the AT block first, as `EvaluateLa` does. `RunLaAt`'s iteration is now `RunLaAtCore`, shared by the double AT and the float-exp AT, including the #1804 periodicity check. The FX rows select the AT for the view's half diagonal, and the work is reported in `LastLaAtWork`.
- **GPU.** `GpuLaAt.FromFx` carries the float-exp AT to the float-exp LA kernel: C6a's float-exp-table route and C6b's float-exp-pixel route. Neither passed an AT before.
  - **Fixed in passing (pre-existing, #1664):** the kernel compares |z′|² in float. Esc² runs up to 2^256, and the cbuffer cast it to float as +∞, so the kernel's AT never escaped: a pixel that leaves the AT ran to the cap and read as in-set. `GpuLaFxParams.Make` now clamps it to FLT_MAX. Leaving the AT there is safe, because the stages go on from z′/A.
  - **Not frame-tested:** the kernel renders antenna-minibrot views wrong from about 1e77, AT or not: every pixel comes back maxIter. That bug predates C6 and is #1887. The GPU part is checked at the cbuffer instead. *(Fixed in #1887, §33, which also replaced this clamp: an AT whose Esc² is past FLT_MAX is not passed to the kernel at all.)*
- **Speed.** The CPU frame's rows, AT on against off, on S1864's antenna minibrots framed at their home view (48×36 and 320×180), at period 250 (~1e299) and 415 (~1e498):
  - ×1.0–1.1 at 320×180 (223 ms vs 251 ms; 361 ms vs 385 ms). At 48×36 the timing is noise-level (×1.0–3.2 across runs).
  - The top stage already steps by the period, so the AT saves no steps. It makes each in-set pixel's steps double rather than FloatExp. Most of the frame is escaping pixels' lower-stage steps.
  - The remaining speed items, SIMD for the FX rows and SA rescaling, target those.
- **Validation** (`S1886LaAtFloatExpTests`). Mutation checks: 9 of 10 caught.
  - **Where double holds** (periods 40 and 80), `LaAtFx` is `LaAt`: the same stage and Len, every bound and coefficient within 1e-12, and the same cbuffer AT words. Its float Esc² is finite.
  - **Past double** (periods 250 and 415):
    - The AT runs on every pixel.
    - In / out is the AT-off frame's, and so is every count.
    - On 16 sampled pixels the counts match direct BigFloat iteration as often as AT off does (16 / 16).
  - **The minibrot's cusp ×10⁶:** no stage's AT is usable (the view is small next to c′'s offset), so the frame is the AT-off frame.
  - **Surviving mutant:** removing the AT's δc-bound check. On these views every pixel is inside the bound, even zoomed out ×10⁴. LaAtFx's ThrC is checked equal to LaAt's.
- **Credits.** `LaStageTableFx.cs` and `MandelbrotCalculator.LaFx.cs` (C5c) are line-for-line ports of Imagina-derived code. They shipped without the THIRD-PARTY-NOTICES §3 entry, the bibliography mention and the upstream SPDX line; all three are added here, with `LaAtFx.cs`.

## 32. The float-exp tail in double (C5h, #1889)

This is the "SIMD" speed item. Measuring first changed what it does.

- **Measured.** S1886's p250 minibrot at 1e286–1e287 (320×180), rendered on the double rows and then forced onto the FX rows (`FloatExpZoomLog10 = 200`):
  - The FX rows took 648–700 ms against 93–102 ms for the same steps, about 7×.
  - The plain tail was 93 % of the steps (8.9M plain against 0.6M LA).
  - SIMD on the double rows had already measured no gain (#1628). The lever was the FloatExp tail itself.
- **Why double can be exact.** A FloatExp `+` or `*` is one correctly rounded double operation on the mantissas, with exact power-of-two exponent bookkeeping. `+` returns the larger term when the gap passes 60 bits, as double's rounding does. So where every operand and result is a normal double, a FloatExp step and a double step give the same bits.
- **`TailInDouble`** (`MandelbrotCalculator.LaFx.cs`) runs the float-exp LA evaluator's plain tail in double under three conditions:
  - ‖δ‖ ∈ [2^-500, 2^500], which keeps |δ|² normal for the rebase test. After a rebase δ is |z|, so it is O(1).
  - Each part of δ is exactly its double.
  - dc is a normal double, or else stays below δ's last bit (‖dc‖·2^62 < ‖δ‖). Past about 1e170 this always holds once ‖δ‖ ≥ 2^-500.
- **The derivative** runs as dd·2^s, where the +1 becomes +2^-s; it is renormalised by powers of two outside [2^-400, 2^400].
- **Leaving the double tail.** It hands back in FloatExp at the range's edge, on escape, on a rebase that climbs back, or at the cap.
- **Credit.** The method is FF's own. The idea of keeping an exponent beside a double is Kalles Fraktaler 2+'s floatexp (`#kf-floatexp`), as FloatExp itself is.
- **Switch.** On by default; `FF_FX_TAIL_DOUBLE=0` turns it off. `LastFxDoubleSteps` and `LastFxDoubleTailEvents` (climbs, renormalisations) report its work.
- **Not covered:** the LA-off FX loop (`ComputePixelPTFx`, which looks up BLA every step), the orbit-aware FX path (C5d), and the LA stage steps themselves.
- **Speed** (the rows, best of 2, 320×180):

  | View | FX tail | Double tail | Speed-up |
  |---|---|---|---|
  | p415, 1e498 | 365–374 ms | 118 ms | ×3.1 |
  | p250, 1e299 | 226–232 ms | 77–78 ms | ×2.9 |
  | p250 zoomed out, 1e286 | 676–700 ms | 223–232 ms | ×3.0 |
  | the cusp ×10⁶ (LA steps only) | | | none |

  The remaining FX cost is the LA stage steps, still in FloatExp.
- **Validation** (`S1889FxTailInDoubleTests`). Frames are byte-identical with the double tail on and off: iteration, smooth, colour, distance and both normal buffers. Twelve scenes:
  - the p250 / p415 minibrots, p250 zoomed out 10^13;
  - c = i at 1e400 and 1e1000;
  - the OD-tail centre at 1e600;
  - a pixel jitter and a sub-rect tile;
  - the dense M(4,1) centre at 1e320 and 1e600;
  - p250 and M(4,1) with climbing off, which rebase in place and renormalise the derivative (1,238 times on p250).
  - Wherever there are plain steps, at least 90 % run in double (100.0 % on all but c = i).
- **Mutation checks:** 3 of 9 caught: the +1 left unscaled, a rebase keeping Z, and the write-back without the scale. The other 6 are equivalent on every reachable view (checked, argued):
  - climbing back from a rebase: the rebased δ = z is far above every stage's bound, so the stages hand straight back to the tail at index 0, which is what the non-climb branch does;
  - an unreported escape: the next FloatExp step finds it;
  - the 2^-500 floor lowered to 2^-1000, the per-part exactness check, and dropping dc: dc is below δ's last bit at every depth the FX rows run at;
  - no renormalisation: no stint in double moves the derivative by 2^±1023.

## 33. The GPU float-exp LA kernel on minibrot views (#1887)

- **Symptom.** From about 1e77, the float-exp LA kernel (#1664) returned the cap for every pixel on antenna-minibrot views, AT or not. Through 1e72.9 it was exact.
  - The bug predates C6. The C6 tests (S1880 / S1882) used Misiurewicz views, whose reference orbit never comes near 0.
- **How it was found.** A C# emulation of the kernel's float arithmetic (HLSL semantics: `FxNorm`'s exponent-bits normalisation, `Pow2` from the exponent field) on one pixel of the p66 view at 1e78 reproduced it: 6,600 iterations against the CPU's 524. Its step trace showed the cause.
- **Cause.** Near a nucleus the reference orbit passes within |Z_p| below FLT_MIN (1.2e-38) of 0 at every period: Z₆₆ ≈ 5.8e-40 on that view.
  - The kernel kept each LA step's Ref, and the tail's Z_n, as plain floats. Those were denormal, and GPUs flush denormals to 0.
  - `FxNorm` doesn't normalise a denormal, so w = δ·(2·Ref + δ) lost its 2·Ref·δ term, as did the rebase test. The pixel never escaped.
- **Fix.**
  - **Ref:** a float-exp complex in each step, so `GpuStepsFloatExp` is 13 words per step (was 12) in both tables.
  - **Tail:** reads Z_n from its own float-exp buffer (`gRefFx`, 3 words per entry, packed by `LaKernelSource.PackRefFloatExp`). The plain float-exp kernel keeps its float2 buffer.
  - **Arithmetic:** z = Z + δ, the rebase test (`FxNormLess`, |z|² < |δ|²) and every product stay float-exp. z goes to float only for the escape test and the output.
  - **Backends:** D3D11 and Vulkan upload the new buffer.
- **Also: no AT past float's range.** With the views rendering, the AT frames from p90 up had about 1.3 % of pixels off by up to 95 %. There the AT's Esc² exceeds FLT_MAX, and #1886 clamped it, so the kernel left the AT earlier than the CPU does.
  - `GpuLaFxParams.AtUsable` now passes no AT when Esc² doesn't fit a float; the stages alone are right.
  - `LastFrameUsedGpuLaAtFx` reports what the kernel actually ran.
- **Validation** (`S1887GpuLaFxMinibrotTests`, Vulkan). The antenna minibrots of periods 40, 66, 90, 125, 250 and 415 (~1e46 to ~1e498, AT on and off):
  - Counts differ from the CPU frame's on at most 14 of 1,728 pixels.
  - No count is more than 1 % off (max 2e-3).
  - In / out is 0.
  - The reference packing decodes to Z within a float rounding, including below FLT_MIN.
  - S1664's packing test now uses the 13-word layout.
  - **`--gpulaprobe`**, `FF_GPULA_MINIBROT=1`, 160×90, GT 710: D3D11 and Vulkan both 0.4–0.9 % iteration difference at p66, p125 and p250, in / out ≤ 2 of 14,400.
- **Mutation checks:** 5 of 7 caught. The survivors are the tail's Z_n as float and the exact rebase in the tail. On these views the tail never meets a Z_n below FLT_MIN, because the LA steps carry the period; both are defensive.

## 34. Close-out (2026-11-18)

### What the epic delivers

The 1e100 ceiling is gone. A Mandelbrot view renders, explores, saves, batches and clusters at any depth. The tested depths reach 1e1000, and float-exp exponents allow zooms up to about 2^±500,000. A view carries:
- **Exact centre:** `ExactCentre` / `BigFloat`, at `PrecisionForZoom` bits (C1, C2a, C2b).
- **Zoom:** as its log10 past double (C3, C3b).

| Area | What works past 1e100 | Slices |
|---|---|---|
| Reference orbit | BigFloat (tier 3, "AP-PT") from 1e110, at the exact centre; periodic detection past 1e154; arrays grow as built | C4, C5f, C8a |
| CPU pixels | Double δ to 1e290 (exact against BigFloat iteration). Float-exp pixels beyond, with BLA, LA stages, the AT and the plain tail in double where exact. Orbit-aware palettes. AA jitter by pixel | C5a–C5e, C5g, C5h |
| GPU | Opt-in float-exp LA kernel (`FF_GPU_LA_FX=1`) to any depth (float-exp pixel scale), D3D11 and Vulkan; correct on minibrot views (#1887) | C6a, C6b, #1887 |
| Server / cluster | The engine takes the exact centre and log zoom (images and video); the master ships the BigFloat orbit once per job | C6c |
| Quality / batch | Unbounded tier; `--batch` and the Command builder carry the exact centre and log zoom (stills, video, slideshow, loop, explore) | C9, C10, C8b |
| Explore tools | Period detection and zoom to minibrot, Snap to spiral, angle → landing and location → angles, .kfr import, Julia morph, auto-explore, the Misiurewicz loop video | C7a–C7f |
| Validation | Deep visual-regression cases (1e150, 1e320, 1e498); exact-iteration oracles in every slice's tests | C11 and each slice |

### What was measured and not shipped

- **C5i, the LA stages in double.** Tried twice: as in C5h, then with cheaper checks. Frames were byte-identical, but the rows ran ~5 % *slower* at 320×180 (6 interleaved runs each). The profile shows why:
  - after C5h the FX tail runs at parity with the double rows' SIMD tail;
  - all of the remaining float-exp gap (~4.9× in the stages) is the stage tries;
  - running the tries in double costs at least as much in guards and conversions.

  A different design (per-table doubles and a per-pixel range certificate) is not attempted. Notes are on #1428.
- **SA rescaling.** SA runs only with LA off (Auto turns LA on from 1e30), and BLA covers the same stretch. Raising SA's 1e100 coefficient cap (a proxy for rescaling) gained ~2 ms on a ~13 ms LA-off spiral frame and nothing on minibrots. Not built.
- **SIMD for the FX rows.** Superseded by C5h. Profiling showed the FloatExp cost, not vectorisation, was the lever, and the existing double-row SIMD (#1628) already measured no gain.

### Speed at depth (CPU, i5-13420H)

- **Past 1e290:** the float-exp rows cost ~1.4–2.3× the double rows on the same steps, down from ~7× before C5h.
- **Where it went:** C5h gave ×2.9–3.1 on minibrot frames, C5g ×1.0–1.1.
- **GPU (GT 710):** about the CPU's speed on these views, and ×9.1 on Misiurewicz views past 1e290 (C6b). It stays opt-in.

### Lessons recorded

- **Test deep GPU paths on minibrot views as well as Misiurewicz views.** #1887 hid behind spiral-only tests: near a nucleus the reference passes below FLT_MIN.
- **Measure speed at 320×180 or more,** with on/off runs interleaved (≥ 6 each) and the minimum taken. Smaller frames and sequential runs gave a false ×1.1, and the machine drifts ~20 % over a session.
- **Profile without new downloads:** EventPipe's sample profiler through environment variables, analysed with the TraceEvent package already in the NuGet cache (§32–§34 notes on #1428).

### Open follow-ups, carried forward

| Issue | Topic |
|---|---|
| #1872 | Deep ray landing on preperiodic angles with long preperiods is slow (46 s at ~1e-146). *×2.8–3 (16 s), §37.* |
| #1875 | External angles of minibrots past period 1,000 (the method's reach at any depth) |
| #1843 | Normals and the final derivative overflow in `FillAuxAndColorHP` past ~1e38–1e150 (the derivative is ±∞ past double); the distance is fixed to ~1e308 by #1783 (§35). *Fixed at any depth, §36.* |
| #1783 | The float distance estimate underflows past ~1e38 (CPU and GPU). *Fixed, §35.* |
| #1852 | Flaky: S1427's cancelled deep-orbit build test is wall-clock timed. *Fixed (PR #1893): a pre-cancelled token and an orbit-capacity bound.* |

## 35. The distance estimate past ~1e38 (#1783)

- **Symptom.** The exterior distance estimate |z| ln|z| / |dz/dc| was stored as a float in complex-plane units, about the pixel size. It went subnormal past zoom ~1e38 and flushed to 0 past ~1e45, CPU and GPU alike. Past ~1e150 the double `dMag = √(dr² + di²)` also overflowed (|dz/dc| past ~1e154), so the estimate was 0 even where the derivative was a finite double.
- **Who read it.**
  - **As distance / pixel scale**, flat past ~1e45: the Distance Estimation palettes (`DistanceEstimationBaseMap`), ColorGen themes using `dist / pxScale`, and the Distance relief source (`IDistanceFieldSource.DistancePixelScale`).
  - **As a raw value:** many built-in themes, e.g. HSV's lightness `1 − min(dist·0.08, 1)` and `exp(−in_dist·k)` in Bernstein, CopperSheen and others. At any depth past ~1e5 those see a distance of effectively 0, and that is their established look.
- **Fix.** A per-frame power-of-two distance unit, 2^-`DistanceUnitExp2` complex-plane units (`MandelbrotCalculator.DistanceUnit`).
  - The unit is 0 while the pixel is at least 2^-100 (zoom ~1e27), so shallow frames are unchanged bit for bit.
  - Past that, the unit is the exponent that puts the pixel scale in [1, 2): exact from the double scale where it is normal, from log10 past ~1e308.
  - `DistanceBuffer` and `DistancePixelScale` (relief) are in the unit. dz/dc is scaled by 2^-e before it is squared (`DeDist`).
  - **Colour maps opt in** (`IColorMapDistanceUnit.TakesDistanceUnit`) to receive the distance and `PixelScale` in the unit.
    - The DE palettes opt in.
    - A ColorGen theme opts in exactly when it reads `pxScale`.
    - Every other map still gets complex-plane units, the same values bit for bit (from the same double), so HSV and the raw-distance built-ins render as before. The first attempt gave every map the unit, and the 1e150 visual-regression case (HSV) went darker; that is how this split was found.
  - The recolour and histogram-equalisation paths convert the buffer per map in the same way.
  - `LastPixelScale` stays in complex-plane units.
- **Reach.** Up to ~1e308. Past that the double derivative itself is ±∞ (the FX rows' `FloatExp.ToDouble`); that is #1843.
- **Validation** (`S1783DeepDistanceUnitTests`).
  - At 1e40, 1e60, 1e150 and 1e280 (QD, OD and AP tiers, LA on), z and dz/dc iterated directly in BigFloat at sampled pixels give the distance in pixels. `DistanceBuffer / DistancePixelScale` matches it to 2.7e-6 (worst), and every value is a normal float.
  - The Distance – Glow palette at 1e150 gives each sampled pixel the colour of the direct distance: 0 of 64 differ, 30 colours. Before the fix the frame was flat.
  - The opt-in split at 1e150: HSV and a raw-`dist` ColorGen theme get complex-plane units and match a reference fed the direct distance in those units. A `dist / pxScale` ColorGen theme gets the unit and shows 33 colours. 0 differ in each case.
  - The visual regression is unchanged (45/45), the 1e150 HSV case included.
  - The unit is exact, and the shallow frame keeps complex-plane units.
  - `S1660`'s GPU-against-CPU distance check, previously limited to zooms below 1e37, now runs at 3E47 as well.

## 36. Normals and the final derivative at depth (#1843)

- **Symptom.** `FillAuxAndColorHP` took dz/dc in complex-plane units, ~1/pixel:
  - `FillNormal` squared Re(z·conj(dz/dc)), which went to ∞ past ~1e150, so the normals were 0 and Phong / PBR shading went flat;
  - the float `FinalDr` / `FinalDi` buffers overflowed past ~1e38;
  - past ~1e308 the double derivative itself was ±∞ (`FloatExp.ToDouble` on the float-exp rows, and the GPU float-exp kernel's write-back), so the normals and the distance (§35) were lost there too.
- **Fix.** dz/dc enters the per-pixel fill in the distance unit of §35: dz/dc · 2^-`DistanceUnitExp2`, O(1 / pixel) there.
  - **Plain double paths:** `ToDistanceUnit` before `FillAuxAndColorHPUnit` / `FinalizeOrbitPixelUnit`.
  - **Float-exp rows:** a FloatExp overload scales before the double conversion (`DistanceUnitOf`).
  - **GPU float-exp kernel:** its 2^scaleE output goes straight into the unit (· 2^-scaleE-e).
  - **Normals:** a power-of-two scale of dz/dc leaves their direction unchanged. Where the old products were finite they are the same bits.
  - **Buffers:** `FinalDr` / `FinalDi` hold dz/dc in the unit, finite at any depth.
  - **Colour maps** get dz/dc as they get the distance: in the unit if they opt in (`IColorMapDistanceUnit`), else in complex-plane units, bit for bit as before (±∞ past ~1e38). The recolour and histogram paths convert per map (`MapDz`).
- **Validation** (`S1843DeepNormalsTests`).
  - **Against direct iteration:** z and dz/dc iterated directly in BigFloat at 1e150 and 1e280 (double δ) and 1e320 and 1e600 (float-exp rows).
    - The normals agree within 1e-4 rad (worst 1e-6).
    - FinalDr/Di equal dz/dc · 2^-e, finite, within 1e-4 (LA's derivative: 2e-5 worst at 1e150, ≤ 3e-7 elsewhere).
    - The distance in pixels agrees within 1e-4, past 1e308 included.
  - **GPU float-exp kernel** (Vulkan) at 1e320 and 1e600: normals and distance match the CPU frame on 575 pixels, and every value is finite. Before, they were ∞ / 0.
  - **Units per map:** a ColorGen theme that doesn't read `pxScale` still sees |dz/dc| as a float ∞ at 1e150; one that does sees the unit.

## 37. Deep ray landing speed (#1872)

- **Measured** with `DeepExternalRay.Land` on S1870's angle: M(4,1)'s .001(010) tuned into the period-n antenna minibrot.
  - **n = 60** (M(181, 60), ~1e-70): 8.0 s. The trace was 72 % (its BigFloat reference orbit 30 %, its CFx Newton substeps 27 %) and `Refine` 28 %.
  - **n = 125** (M(376, 125), ~1e-146): 46 s, with the reference orbit at 42 %.
- **Shipped** (`DeepRayTuning`, all on by default):
  - **The substeps in double** where every value is a normal double (|Δ| and |dz/dc| within 2^±900); CFx past that. It mirrors CFx's operations, a product as (ac − bd, ad + bc) and a quotient as a product with the reciprocal, with 1/dc scaled by a power of two first.
  - **BigFloat rounding without temporaries.** `Round` decides ties-to-even from the guard bit and the trailing-zero count instead of building the discarded field; `ToDouble` rounds from the top 64 bits and a sticky flag. Both are exact rewrites, checked field for field and bit for bit against the old algorithms in `S1872BigFloatRoundingTests`, ties and subnormals included. This speeds every BigFloat path, not only the rays.
  - **Skip exhausted divisors.** In `TryMisiurewicz`, a divisor whose Newton ran out of steps without leaving its reach is skipped in the wider reach windows (`Refine`'s `ranOutOfSteps`). It would only creep the same way again.
- **Result:**

  | Case | Before | After | Speed-up |
  |---|---|---|---|
  | n = 60 | 8.0 s | 2.7 s | ×2.95 |
  | n = 125 | 46 s | 16.3 s | ×2.8 |
  | n = 40 (S1872) | 3.8 s (plain) | 1.1 s | ×3.5 |

  The landing points are identical to the plain path's, bit for bit (`S1872DeepRayLandingSpeedTests`, preperiodic and periodic).
- **Why it is still O(bits²).** The landing is tried once per period after the ray converges, and at n = 125 it takes 8 tries (bits 1,125 … 3,750). On the tuned views the right (k, p) Newton creeps. Its step stays ~1/270 of the distance to the root (a tight cluster of roots around a tuned copy), and only a seed that the trace has brought into the quadratic region lands. Each failed try costs another period of trace.
- **Measured and not shipped:**
  - **Rebasing every few bits.** An older ray point's orbit escapes far faster (|Z| about squares per extra iteration), so perturbing a later bit from it loses all precision. Every 8 bits broke the n = 60 landing.
  - **A tapered orbit precision.** Each step can be given only the bits its error needs after the Π|2Z| amplification. No gain: the tuned orbit passes near 0 every n iterations, and those small |Z_m| need nearly full precision for every earlier step.
  - **Jumping when Newton creeps**, extrapolating 2×, 4×, … along the step. Neither the deflated residual |G / Π f| (it falls all the way to the reach edge) nor the plain |G| (it rises: the seed sits beside lower-preperiod roots) guides it to the root.
  - **Aitken-extrapolated seeds** from the ray's last three period points. They landed no earlier.

## 38. Where the time goes past 1e100 (#2015)

**Measured:** `--apcostprobe 320 180` (`FF_APCOST_KFR=<dir>`) on the fractadyne bench-kit's ladder scenes (`#fractadyne-bench-kit`). CPU, one sample per pixel, i5-13420H. It followed rith's 0.7.2 report (`#rith-ff-071-benchmark`), where FF was 3–550× slower than Fractadyne past 1e100.

| scene | iter cap | frame | reference orbit (length, µs / iter) | tables | pixels | threads busy |
|---|---|---|---|---|---|---|
| spiral path 1e100 | 10,293 | 127 ms | 65 ms (4,355, 15.0) | 14 ms | 47 ms | 3.3 |
| spiral path 1e300 | 26,673 | 161 ms | 60 ms (9,476, 6.4) | 0 | 101 ms | 6.9 |
| spiral path 1e1000 | 84,003 | 0.84 s | 0.64 s (28,590, 22.4) | 0 | 0.20 s | 3.3 |
| spiral path 1e3000 | 247,803 | 7.4 s | 7.3 s (83,295, 87.8) | 0 | 95 ms | 1.1 |
| spiral path 1e10000 (cap 20,000) | 20,000 | 10.5 s | 10.4 s (20,000, 521.7) | 0 | 82 ms | 1.0 |
| minibrot p14,416 @ 1e1000 | 432,480 | 10.8 s | 10.4 s (432,480, 24.0) | 0 | 0.47 s | 1.1 |
| minibrot p41,722 @ 1e3000 | 1,251,660 | 112 s | 112 s (1,251,660, 89.4) | 0 | 0.48 s | 1.0 |

**From about 1e1000 the reference orbit is 76–99 % of the frame**, and it runs on one thread. Two separate causes:

- **#2032, the period:** on the minibrots the orbit runs to the full cap (30 periods) with period 0.
  - The centres lie in the minibrot's attracting cycle: |Z_p|² ≈ 2^-3451 at p, 2p, 3p, 4p.
  - The stop test needs |Z_p|² < pixel² ≈ 2^-6659, so it never fires.
  - Stopping at the period would cut the orbit about 30×.
- **#2033, the cost per iteration:** it grows about n^1.35 with precision, from 22 µs at ~3.4k bits to 522 µs at ~33k bits.
  - Each iteration is three `BigFloat.Mul` on immutable `BigInteger`, plus adds, rounding and the OD store.
  - Candidates, to be measured: a 2-multiply step, allocation-free limbs, more threads.

## 39. The period of an attracting-cycle minibrot centre (#2032)

**The problem (§38):** the AP reference orbit stopped at the period only when |Z_n|² < pixel² (#1614 / #1854). The bench-kit minibrot centres sit in the attracting cycle: |Z_p|² ≈ 2^-3451 at p14,416 @ 1e1000, against pixel² ≈ 2^-6659. So the orbit ran to the cap, 30 periods.

**The new rule:** `ComputeReferenceOrbitAP` tracks dZ_n/dc in FloatExp (Z'_{n+1} = 2·Z_n·Z'_n + 1, cheap next to the BigFloat step). It also stops at n when both of these hold:
- **The Newton distance |Z_n / Z'_n| is below the pixel size.** A period-n nucleus is then within a pixel of the reference. The wrap at n is the usual exact rebase (δ := Z_n + δ, back to Z_0), and it keeps each pixel's offset, because the nearest pixels' |δ_n| ≈ |dc|·|Z'_n| is at least about |Z_n|.
- **|Z_n|⁴ < pixel².** Late in an escaping orbit |Z'_n| is huge, so the Newton distance drops below a pixel with |Z_n| near 1. Without this guard the kit's spirals at 1e300..1e3000 reported "periods" just before escaping.

The cached orbit's validity check compares the stored |Z_p / Z'_p|² against the next frame's pixel², as before.

**Measured** with `--apcostprobe 320 180`, `FF_APCOST_EXACT=1` (i5-13420H):

| scene | frame before | frame after | orbit length | pixels whose iteration differs from the full-length orbit |
|---|---|---|---|---|
| minibrot p14,416 @ 1e1000 | 10.8 s | 0.89 s | 432,480 → 14,417 | 3 / 57,600 (in / out 0) |
| minibrot p41,722 @ 1e3000 | 112 s | 3.9 s | 1,251,660 → 41,723 | 0 |
| spirals 1e100 / 1e300 / 1e1000 / 1e3000 | — | unchanged | unchanged (period 0) | 0 |

The 3 moved pixels are within #1614's bound (≤ 3 %, in / out identical). The cost per iteration (§38, #2033) is unchanged.

**Tests (`S2032ApPeriodTests`):** they embed two kit locations, Apache-2.0 (THIRD-PARTY-NOTICES §6):
- the minibrot stops at p = 14,416 (orbit length p + 1) and stays within #1614's bound against the full orbit;
- an escaping spiral keeps its full orbit and renders byte-identically.

Mutants: dropping the Newton test fails the first test, and dropping the |Z|⁴ guard fails the second.

## 40. The BigFloat step on three threads (#2033)

**Measured** with `--bigfloatopsprobe`: the cost of each operation of one reference-orbit step, in µs, on full-precision random operands, i5-13420H.

| bits (zoom) | Mul | square | Add | ToOD | step, serial | step, parallel |
|---|---|---|---|---|---|---|
| 1,100 (~1e300) | 3.5 | 1.0 | 2.4 | — | 6.9 | 4.0 (×1.75) |
| 3,400 (~1e1000) | 6.5 | 4.5 | 0.9 | 3.4 | 22.5 | 12.4 (×1.82) |
| 10,000 (~1e3000) | 37 | 28 | 1.9 | 6.1 | 86 | 40.5 (×2.13) |
| 33,300 (~1e10000) | 193 | 128 | 5.5 | 15.4 | 504 | 222 (×2.27) |

- **The multiplies dominate:** two squares and one multiply are 70–95 % of a serial step. The two OD splits come to ~30 % at 3.4k bits and ~6 % at 33k.
- **The ToOD figure at 1,100 bits wasn't reproducible** (15–22 µs, more than at 3,400). Probably JIT tiering in the probe's first size.

**Change:** `BigFloatStepWorkers` (Abstractions/Math) runs the step's independent work on three threads:
- the calling thread: zr², then zi's OD split;
- helper 1: zi², then zr's OD split;
- helper 2: zr·zi.

These are the same operations on the same operands, so the orbit is **bit-identical** to the serial step.

The helpers spin on a generation counter, because a step (4–220 µs) is far shorter than a thread-pool wake-up. They block on an event after 1 ms idle, and are created per orbit build and joined at its end. `ComputeReferenceOrbitAP` uses them from `BigFloatStepWorkers.MinBits` = 1,024 bits. `MandelbrotCalculator.ParallelApOrbit = false` gives the serial step; it's a test hook.

**Orbit cost** (`--apcostprobe 320 180`, µs per iteration, before → after):

| scene | per iteration | frame |
|---|---|---|
| spiral 1e1000 | 22.6 → 12.2 | 0.75 → 0.45 s |
| spiral 1e3000 | 88 → 40.4 | 7.4 → 3.5 s |
| minibrot p41,722 @ 1e3000 | 94 → 44 | 4.4 → 2.2 s |
| minibrot p14,416 @ 1e1000 | 46 → 38 | 0.91 → 0.79 s |

- **Near-zero Z costs extra:** the minibrots cost more per iteration than the spirals at the same depth. Their Z_n passes near 0, so `Add(Z², c)` aligns across the exponent gap (about 1,700 bits at p14,416) before rounding.
- **Not done:**
  - the 2-multiply form (it rounds differently, so it isn't bit-identical; ~14 % at 33k bits);
  - allocation-free limb arithmetic;
  - a comparison against a reference big-number library.

**Tests (`S2033ParallelApOrbitTests`):**
- every stored orbit point (bit for bit) and every output buffer match with `ParallelApOrbit` on and off, on the S2032 bench-kit minibrot and spiral;
- the workers' products and OD splits equal the serial calls at 1.1k / 3.4k / 10k bits.

Mutant: the helper squaring zr instead of zi fails all five.

