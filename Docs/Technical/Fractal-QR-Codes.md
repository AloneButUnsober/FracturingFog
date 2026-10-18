# Fractal QR codes

Epic #1305. Slices: S1 #1396 encoder + verifier, S2 #1397 compositor, S3 #1398 batch parity,
S4 #1399 Control Center, S5 #1400 halftone, S6 #1401 view search, S7 #1402 QArt shaping.
User documentation: [Capture Guide §2a](../User/Capture-Guide.md#2a-qr-code-a-fractal-that-scans).

A render is exported as a square image that is also a valid QR code. Everything lives in
`Engine/Imaging/Qr/`. The option types are in Abstractions (`Abstractions/Imaging/QrCodeOptions.cs`,
`QrSearchOptions.cs`) so the batch parser and the UI, which does not reference Engine, carry them.

## Pipeline

```
PosterRenderer.RenderToFile
  └ WritePoster: rotate → QrExport.Apply(buffer) → watermark + save → QrExport.VerifySaved(file)
```

The QR goes onto the final display buffer after rotation, so the module grid is square in
the saved file. It goes on before the watermark. The verify reads the **saved file**, so the
watermark and the encoder are part of what is checked. `PosterResult.Qr` (a `QrExportReport`)
carries the code, the layout, the verify result and the shaping stats.

| File | Role |
|---|---|
| `QrEncoder.cs` | ISO/IEC 18004 Model 2, byte mode, v1–40, L/M/Q/H, 8 masks. Exposes the function-pattern mask, forced masks, the raw codeword layer (`BuildDataCodewords` / `EncodeCodewords`), and the S7 helpers (`ChooseVersion`, `BuildShapeableData`, `CodewordBitModules`, `MapBlocks`). |
| `QrVerifier.cs` | ZXing.Net, **decode only**. Passes: full, small (3 px/module), blur (¼-module box), JPEG q70. A code scans only if every pass returns the exact payload. |
| `QrCompositor.cs` | Layout (centred, 4-module quiet zone) and the styles. Moves BT.601 luma only. `Steer` remaps luma into a band, backing off chroma when the gamut clamp would push luma out of it. |
| `QrExport.cs` | Entry point: Validate / Preflight / Apply / VerifySaved. Chooses the mask by decoding. |
| `QrViewSearch.cs` | S6: seeded random beam over centre (DeepComplex) + zoom, scored on probes from `PosterRenderer.RenderDisplayPixels`. |
| `QrQArt.cs` | S7: GF(2) elimination over the post-terminator data bits. |

## Invariants the tests hold

- **Decoded payload == input payload, by an independent decoder.** The encoder is ours and
  ZXing only decodes, so a pass is two implementations agreeing. The 160-case version × level
  sweep uses ZXing's matrix `Decoder`, which skips image detection, so detection luck cannot
  hide an encoder bug. Image-level tests use the full reader.
- Reed–Solomon matches the published "HELLO WORLD" 1-M vector.
- A QArt solution satisfies every claimed equation exactly, and the shaped symbol still decodes.
- `--qr-search` is deterministic per seed and never ends worse than its start view. Its
  `replay` line re-renders byte-identically.

## Gotchas found on the way

- **Fake finders.** ZXing's `FinderPatternFinder` accepts 1:1:{2..5}:1:1 crosses: the
  centre width is tolerated because the module size is estimated from the run. A cross in
  the data region can be confirmed twice before the scan reaches the real bottom-left finder,
  and then the scan stops. On perfect renders at full capacity about 1 in 8 masks failed
  detection. The spec's N3 penalty is 1-D, so `QrEncoder.PseudoFinderPenalty` adds a 2-D term.
  Large versions have lookalikes under every mask, so `QrExport.Apply` tries masks in penalty
  order and keeps the first composite that passes all verify passes.
- **Halftone must be 4-tone.** A pure two-tone halftone gives 8 of the 9 sub-cells to the
  image, so blur and shrinking pull a module's average to the wrong side. The outer cells keep
  the image's two tones *within the module's half* of the luma range.
- **Dots needs a soft surround.** The "untouched" area around each dot gets a far-end-only
  luma compression, so a blurred dot plus surround stays on its side.
- **View search drifts to flat framings.** Minimising the luma change rewards featureless
  mid-tone areas, which are cheap to steer but show no fractal. `QrSearchOptions.MinDetail`
  (default 0.8 × the start view's edge energy) rejects them. Gains are modest by nature: QR
  data is pseudo-random, so the best of a few hundred view × mask samples sits only a few σ
  below chance.
- **Mean, not median.** On two-level images the median sits on one level, and `< median`
  then calls nothing dark.
- **QArt needs room.** The payload's own data modules are fixed and cannot be shaped. Free
  bits = data capacity − header − payload − terminator. Raising the version and lowering ECC
  is what makes shaping visible (51% → 84% at v8-L for a 40-character URL, against ~40 free
  bits at the minimal version).

## Batch parity

Flags `--qr-payload --qr-ecc --qr-style --qr-module-px --qr-dot --qr-invert --qr-no-verify
--qr-version --qr-shape --qr-mask --qr-search`, in `BatchFlagGroup.Qr`, image mode only. The
group is a Command-composer *look* group, so **Seed from live view** carries the Control
Center settings. Exit code 8 = saved but does not scan. Preflight failures (payload does not
fit, image too small) exit 2 before rendering.
