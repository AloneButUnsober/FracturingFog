# Fractal QR codes

Epic #1305. Slices: S1 #1396 encoder + verifier, S2 #1397 compositor, S3 #1398 batch parity,
S4 #1399 Control Center, S5 #1400 halftone, S6 #1401 view search, S7 #1402 QArt shaping.
3D-look follow-up: epic #1408; phase A #1409 Bevel style (done), B #1410 relief extrusion (done), C #1411 printable mesh (done).
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
| `QrBevel.cs` | #1409: Bevel style. `QrBevelLighting.Resolve` takes the theme's lights (`IThemeLights` on the Phong / PBR / orbit-trap 3D bases) or explicit angles. The compositor steers, builds the heightfield, shades, then bounds every pixel. |

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

## Bevel style (#1409)

1. **Band from the distance to the other shade.** A 3-4 chamfer distance from each
   pixel to the nearest pixel of the opposite shade sets its band: the core band from
   (0.5 − dot/2) modules in, so every sampled centre is covered, widening smoothly to
   the edge band. Measuring to the other shade rather than to the module border keeps
   a run of same-shade modules one continuous face. The first version steered the
   Steer way (core square, edge band) and then clamped, which left a bright square in
   every light module.
2. **Heightfield.** Raised = dark modules, with a smoothstep ramp `BevelWidth` wide at
   the block edge. The floor is height 0 and the quiet zone is floor.
3. **Shade relative to flat** (Blinn-Phong per light, minus the flat-normal response),
   so plateaus and floor keep their steered colour and only slopes change. A soft
   horizon walk toward the key light gives the cast shadow.
4. **Bound.** Each pixel is clamped (luma only) into its band. At an edge, data modules
   may widen to the Dots soft band; function modules and the quiet zone only to the
   edge band.
5. **Module mean.** Each module's mean is held at Steer's worst case (light ≥ 160,
   dark ≤ 104) by a uniform remap, because the small and blur passes average a module
   and a long shadow can cover most of a light one.

Not `HeightfieldRelief2D`: that pass treats height ≤ 0 as unlit interior, which here
is the whole floor that the shadows must land on.

Theme lights are complex-plane convention (y up), the same orientation as the saved
image. A rotated (portrait) poster save turns them 90° clockwise with the picture;
batch renders never rotate.

On the synthetic busy test image, plain Steer itself fails the small pass at some
square sizes (640, 800). Bevel tracks Steer there, so the robustness tests use the
#1305 size (900 × 640) and real renders.

## Extrude in Relief 3D (#1410)

`QrRelief.cs`. `PosterRenderer.RenderToFile` attaches a `QrReliefState` to the request when
`Qr.Relief` is set, the output is unrotated and the relief raymarch is on. The steps:

1. **Prepare** (in `ApplyReliefIfEnabled`, before the trace). The code and mask are chosen
   the flat way, by a decode-verified composite (`QrExport.Apply`). That composite becomes
   the **albedo**. Steer and Bevel use the Bevel bands with no lights
   (`QrBevelLighting.None`), so no core square shows on a lit face. The camera elevation is
   raised to `ReliefMinElevationDeg`, on a clone of the parameters.
2. **Inject** (`HeightfieldRaymarch2D.Render(..., heightInjection:)`). The hook edits the
   prepared field **after** the prepass, because its despike filters and tone curve would
   round the blocks off. It works on a copy, since the prepass is a shared cache. Under the
   code and quiet zone, the terrain is compressed toward its median (`ReliefTerrain`) and
   eases back over two modules outside. Dark modules rise by `ReliefHeight × module size`
   (world units, ÷ sy) with the Bevel smoothstep ramp. The peak, slope bound and skip mip
   are re-derived. `sy` and the camera aim (normRef) stay the fractal's, so the framing
   does not move. Both the CPU trace and the GPU kernel consume the edited field. That is
   the parity: the test plugs the kernel's CPU twin in at the seam and compares it with
   the CPU trace (mean drift < 1.5).
3. **Frame fit.** The outer corners of the quiet zone are projected at plinth height through
   the trace camera (`ReliefAovBuffers.TraceCamera`, set on both paths). If one leaves the
   frame (2% margin), the camera zoom is scaled by the overflow and the scene traced again,
   at most twice. A tilted perspective camera otherwise cuts the near quiet zone, and the
   detector needs it.
4. **Resteer** (in `ApplyQr`, on the final display buffer, after grade, tonemap and
   denoise). Each pixel's primary hit (depth AOV + `HeightfieldRaymarch2D.HitPoint`) maps
   back to field coordinates, so to a module and a position in it. Luma is clamped into a
   band set by the distance to the other shade, as in Bevel. Data module edges may widen
   to the soft band, so wall shading and shadows survive. Then each module's on-screen
   mean is held at Steer's worst case.

**Plate vs true height.** The default (plate) trace is kept, so the user's terrain looks
the same. Its hit test uses the Lipschitz-scaled DE, which gives early "air hits" near
very steep walls (#1025). Moderate blocks (≤ 1 module, bevel ramp) render cleanly. True
height draws tall blocks properly, but it rescales the whole terrain into mountains that
bury the code, so it is not forced.

**Limits measured** (Seahorse Valley, 900², v4-H, 0.5 and 1 module blocks): 60° and 50°
scan 4/4, 40° and below fail. The trapezoid is clean, but ZXing's detector rejects it.
Hence the 60° default minimum.

## Printable tile (#1411)

`QrMesh.cs`. The input is the display buffer without the QR (`PosterRenderer.RenderDisplayPixels`,
or `PosterResult.PreQrPixels` when `PosterRequest.KeepPreQrPixels` is set, so batch reuses
the image render). The code and its colours come from a flat, decode-verified composite
(the unlit Bevel bands for Steer / Bevel).

- **Not `HeightfieldMeshExporter`.** Its median / close / blur chain rounds blocks off.
  The tile is instead flat-topped columns on a grid of 4 cells per module: one top quad
  and one bottom quad per cell. Wherever two neighbouring cells (or a cell and the
  outside) differ in height there is a vertical wall. A wall's sides are split at **every
  height present at that grid corner** and zipper-triangulated, so walls that meet share
  edges exactly: no T-junctions. Each wall triangle is wound against the direction from
  the taller cell to the lower one, so it faces outward.
- **Saddles.** Two raised modules touching only diagonally make a vertical edge of four
  walls, which is non-manifold (the first build had 28 such edges). `RemoveSaddles`
  raises the taller of the low diagonal pair to the lower of the high pair until no corner
  alternates. Heights only rise to values already present, so it converges. For blocks
  this fills one cell (1/16 module); with terrain it is a small height nudge.
- **Frames.** STL and 3MF are Z-up (slicer convention) via a proper rotation, so winding
  is kept. OBJ is Y-up like the other FF mesh exports. Both read unmirrored from above.
- **Checks.** `MeshValidator` must report a closed 2-manifold with positive signed
  volume. A top-down raster (`RenderTopDown`) must decode, using colours for 3MF / OBJ
  and height for STL (dark above base + terrain + half a module). Tests read the
  **written files** back (`StlMeshReader`, `ThreeMfMeshReader`) for both checks. An STL
  with terrain taller than a module is expected to fail the height scan, and does.

## Batch parity

Flags `--qr-payload --qr-ecc --qr-style --qr-module-px --qr-dot --qr-invert --qr-no-verify
--qr-version --qr-shape --qr-mask --qr-search`, plus `--qr-bevel-height --qr-bevel-width
--qr-bevel-light --qr-bevel-specular --qr-bevel-shadow` (catalog: requires `--qr-style
bevel`), plus `--qr-relief --qr-relief-height --qr-relief-terrain
--qr-relief-min-elevation` (`--qr-relief` requires `--relief-raymarch` and does not turn it
on, matching the Control Center export), plus `--qr-mesh PATH --qr-mesh-size --qr-mesh-base
--qr-mesh-height --qr-mesh-terrain` (the builder emits these only when the snapshot has a
mesh path: the mesh is a Control Center button, so the live seed carries none), in `BatchFlagGroup.Qr`, image mode only. The
group is a Command-composer *look* group, so **Seed from live view** carries the Control
Center settings. Exit code 8 = saved but does not scan. Preflight failures (payload does not
fit, image too small) exit 2 before rendering.
