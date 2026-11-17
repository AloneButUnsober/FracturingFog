# Arbitrary-precision deep zoom — Design Notes

Epic #1428: lift the 1e100 zoom cap (option C, arbitrary precision). The slice issues are
the canonical task list; this doc backs them.

| slice | issue | status |
|---|---|---|
| C1 BigFloat type | #1830 | §1–§5 (merged, PR #1831) |
| C2a exact centre in view state + region JSON | #1832 | §6 (merged, PR #1834) |
| C2b exact centre in history, location strings, video, RPC, cluster | #1833 | §7 |
| C3–C11 | filed when started (see #1428) | |

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
