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
| C5a floatexp per-pixel perturbation (CPU, scalar) | #1842 | §11 |
| C5b–C11 | filed when started (see #1428) | |

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
  - Batch `--zoom` past double: C10. Batch is clamped at Extreme's 1e100 today.
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
