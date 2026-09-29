# Relief 3D consistency

Epic #1025. Slices #1026 (height modes), #1027 (surface hit test, field downsample, tolerance anchor), #1028 (canonical field), #1029 (distance height source).

The same Relief 3D settings should give the same relief whatever is in view, wherever the camera sits, and whatever the window size. Before this epic, three independent mechanisms broke that.

## The three causes

| Cause | Where | Symptom | Slice |
|---|---|---|---|
| Per-frame auto-normalisation | `HeightfieldRaymarch2D.BuildPrepass` baseline (60th percentile of non-zero heights) and `sy = 0.35·amp·HeightScale / maxH` | Height drifts with content; one needle flattens the view | #1026 |
| Hit test on the Lipschitz-scaled distance | `d < epsT` in the sphere trace, where `d = (y − h)·invLip` | Fine fields (big windows) render a flat plate; relief height depends on window size and content | #1027 |
| Distance-dependent hit tolerance | `epsT = eps0 + pixelAngle·t` (`BuildObliqueCamera`) | Far filaments merge into the floor; "narrowing to the back" | #1027 |
| Resolution-dependent pipeline | Field floor only raises small windows; `ResolutionRamp` amplitude pull-down (up to 72%) and median/blur; `pixelAngle = tanHalf / h` | Small and large windows look different | #1028 |

A fourth, smaller one: iteration counts rise as you zoom deeper, so even a fixed normalisation drifts with zoom depth. #1029 offers a zoom-invariant distance-estimate height source.

## S1: height normalisation modes (#1026)

`FractalParameters.Relief2DHeightMode` (`ReliefHeightMode`):

- **Peak** (default): unchanged, byte-identical. The reference is the processed field's peak (`maxH`) and the baseline is measured every frame.
- **Robust:** the reference is the 99.5th percentile of the non-zero processed cells (`RobustReference`, 1024-bin histogram, bin upper edge). A lone needle sits above the reference instead of setting it.
- **Fixed:** `Relief2DHeightBaseline` (≥ 0) replaces the measured baseline. `Relief2DHeightRef` (> 0) replaces the reference, and also anchors `Relief2DHeightGamma`'s curve. Unset values (baseline < 0, ref ≤ 0) fall back to the per-frame measurement.

`sy = 0.35·amp·HeightScale / reference`. The camera aims off the reference (`BuildObliqueCamera(..., aimH)`) rather than the content's peak, so framing does not follow the tallest needle. The AABB top still uses the true `maxH`, so the march stays bounded.

**Lock current height:** `HeightfieldRaymarch2D.MeasureHeightNormalization` returns the (baseline, reference) the current mode uses on a field. `FractalRenderHost.TryMeasureReliefHeight` runs it on the live field (`_reliefHeight`). The dialog stores the result and switches to Fixed. Because the baseline is stored as the exact float the prepass subtracted and the reference as the exact peak or percentile, the locked frame is byte-identical to the one it was measured from (tested). The exception is a non-default Height gamma: its curve then anchors at the reference rather than the post-filter peak.

**GPU:** the GPU kernel consumes the same prepass field, `sy` and camera (`ReliefUniforms.Build(..., aimH)`), so no shader change is needed. The `--reliefgpuraymarch` gate still passes.

**Batch:**
- `--relief-height-mode peak|robust|fixed`
- `--relief-height-ref F` (> 0)
- `--relief-height-baseline F` (≥ 0)

Either of the fixed values selects Fixed, and combining one with another mode is an error. The Command builder seed emits them, region save/load carries them (`Relief3DSettings.HeightMode/HeightRef/HeightBaseline`), and they are reset by Defaults.

**Limits:**
- The values are in tone-curve units, so changing the height curve, or zooming much deeper, calls for a new lock.
- In Fixed mode a genuinely tall new feature stays tall. That also steepens the field's global slope bound, so the march takes smaller steps.
- Screen-space emboss (`HeightfieldRelief2D`) and mesh export keep their own normalisation.

## S2: surface hit test, field downsample, tolerance anchor (#1027)

S2 was planned as "measure the hit tolerance from the look-at point". Measuring that showed the tolerance was not the main problem.

**The flat-plate bug.** The sphere trace stepped by the height DE `d = (y − h)·invLip` and stopped when `d < epsT`. `invLip` is one global Lipschitz factor taken from the field's steepest cell. A Mandelbrot boundary is a near-vertical cliff, so on a fine field (about 1000 px or more on the short axis: a maximised window, or the #143 hi-res floor field) `invLip` is tiny. `d` was then below `epsT` at the very first sample, and every ray stopped where it entered the terrain box. The relief rendered as a flat plate at the box top with the fractal only in the normals and colour. Coarse fields have gentler cells, so small windows rendered real relief. That is much of "small and large windows look different", and why Height scale seemed to do little. #143's "the hi-res field renders smoother" result was this plate.

**Fix, in three parts, shared by the CPU trace, the GPU parity twin and the HLSL kernel (D3D + Vulkan):**
- **Hit test on the vertical gap:** hit when `d < epsT·invLip`, i.e. when the vertical gap `y − h` is within the tolerance, in world units. Stepping still uses the conservative `d`.
- **Empty-space skip on by default and ported to the CPU trace.** `Relief2DEmptySkip` (#170) was GPU-only and off. With a strict hit test, rays crossing the air above a steep field would otherwise crawl at `d` and run out of steps. The CPU trace now uses the same coarse max-height grid (`ReliefHeightMip.EmptySkipDist`, shared with the twin). The bicubic path skips it, because the grid's halo bounds the bilinear sample only.
- **Field downsample to the output:** real geometry exposed a new problem. A field finer than the output holds filaments narrower than a pixel, which trace as a hair-thin needle forest. When the field is more than 1.25× finer than the output, the prepass area-averages the tone-curved field down to about one cell per output pixel (`FieldTargetDims`, `AreaDownsample`) and runs the rest of the chain there, including the small-window filters. Computing high and averaging down is an anti-aliased field, so the #143 hi-res field now gives the same shape as a display-res field, only cleaner. `MeasureHeightNormalization` takes the output size so a lock matches the render.

**Tolerance anchor** (`Relief2DDetailAnchor`, `--relief-detail-anchor camera|uniform`):
- **Camera** (default) grows the tolerance with distance.
- **Uniform** measures it once, at the terrain's nearest point (camera to terrain box), and holds it for everything farther. It triples the step budget.

The look-at point turned out to be farther than most visible terrain, so a cap there changed nothing; the nearest point does. With the fix in, Uniform cut the far-terrain depth error against a 5× resolution reference by about 80% in the test view. It is off by default because it costs more steps. The GPU kernel reads the cap from the former `gPad3` slot (`gConeCapT`), so the cbuffer layout is unchanged.

**Tests and gates:**
- `ReliefFineFieldHitTests` recomputes each ray's box entry from the camera alone. It fails at 100% "stopped at the box top" on the old hit test.
- `ReliefDetailAnchorTests`.
- `--heightfieldhires` now requires the display-res and floor-field renders to agree (same coverage, floor no rougher).
- `--reliefgpuraymarch` (WARP) and `--vulkanrelief` (GT 710) run with Uniform on.

**Defaults: restored behind an opt-in (follow-up PR).** The first version turned this on for everyone. At app defaults the real surface of an iteration-count field is a wall of tall, thin needles around the set, with a pit where the set's interior is. Much of the earlier relief tuning had been done against the plate, so it looked like a regression. The trace is now opt-in:
- `Relief2DTrueHeight` (dialog "Real height (experimental)", batch `--relief-true-height`). Off = byte-identical to before #1027, verified pixel-for-pixel on four views with batch renders. On = the #1034 trace exactly.
- The GPU kernel reads the switch from the former 4a pad slot (`gTrueHeight`); layout unchanged.
- `Relief2DEmptySkip` is back to default off and runs whenever it is on *or* true height is on.
- Fixed height mode no longer exposes the raw reference and baseline as editable numbers. The reference is in height-curve units (typically 1–10), so a value like 0.1 made the terrain about 50× taller. The lock is now shown read-only, and Height scale adjusts it.

Next: terrain shaping so real height reads as terrain (#1035), and distance-estimate height (#1029).

**Open:**
- Soft shadows, AO and reflections also march the height DE against small epsilons and may over-occlude on fine fields: #1033.
- Per-distance filtering (a mip chosen by pixel footprint along the ray) would anti-alias far terrain better than one output-sized grid. That belongs in #1028.

## Terrain shaping for real height (#1035)

At app defaults the real surface of the smooth-iteration field is a thin, noisy wall of needles around the set. Under `Relief2DTrueHeight` (only; the default plate path is byte-identical, verified by batch pixel-diff):

- **Terrain smoothing:** `Relief2DTerrainSmoothing`, default 0.02 of the traced grid's short axis, 0..0.05; batch `--relief-terrain-smoothing`; dialog 0–5%. Three box passes (≈ Gaussian) after the tone curve and downsample, before the detail shaping, so Detail exaggeration can re-sharpen on purpose. It is view-relative, so it looks the same at any window size. The default comes from a sweep (0 / 0.5 / 1 / 2 / 3.5%) over whole-set, seahorse and elephant views at 640 and 1280 px: 1% still bristled and 3.5% went blobby.
- **Small-window tamers off:** `ResolutionRamp`'s amplitude pull-down (up to 72%) and the adaptive median/blur were needle mitigations tuned against the plate. They made the height depend on the window size, so real height skips them and terrain smoothing takes their place. 480 and 960 px now agree on silhouette coverage within 3%.
- **Needle knee:** anything above the Robust (99.5th percentile) or Fixed (locked) reference rolls off softly toward reference × 1.2 (`KneeSpan`). Peak never exceeds its reference, so it is a no-op. A lock taken from Robust reproduces the frame exactly, and needles that enter a locked view are capped too. This applies to the plate path as well, but only in Robust / Fixed mode.

**Tests** (`ReliefTerrainShapingTests`): depth-AOV roughness drops by more than 40% with smoothing; window-size agreement; Robust's top (hit heights reconstructed from the camera) ≤ 1.22 × full height while Peak reaches full height; the plate path ignores smoothing; and parity.

## S3: canonical field for real height (#1028)

**Measured first.** Five window sizes (480×270 to 2560×1440) over whole-set and seahorse views, with the field sized the way the host sizes it. Terrain hit heights were reconstructed from the depth AOV and the camera, and compared on a common grid of screen positions.
- **Plate path (default):** already window-invariant (mean height gap ≤ 0.3% of full height). Every supported field is raised to the floor, so every window gets the plate. Nothing to do; it stays byte-identical.
- **Real height (#1038):** the field was downsampled to the output size, so smaller windows averaged the peaks away. The height reference moved by up to 25% (smoothing 2%) or 48% (smoothing 0). Mean height gap vs 1440p on the seahorse: 2.1% at 270p, 1.5% at 540p, 1.0% at 720p.
- **Large windows:** a window above the floor traced its own display-res field. Area-downsampling a finer field lowers the peaks, so the seahorse reference was 1.64 at 1080p, 1.55 at 1440p and 1.39 at 2160p. A 4K window's relief came out about 18% taller.

**Fix** (`Relief2DCanonicalField`, default on, Real height only):
- **One grid:** `HeightfieldRaymarch2D.TraceGrid` shapes and traces the terrain on a grid whose short axis is the field floor (`CanonicalDims`). A finer field is area-downsampled to it; a coarser one (preview, hi-res field off) is kept, never upsampled.
- **Same field for large windows:** `FractalRenderHost.HiResReliefFieldDims`, shared by the host capture and the poster/batch path, now brings a window above the floor *down* to the floor too. Every window computes the same field, not a downsample of its own.
- **Anti-aliasing:** when the grid is finer than the output, a blur of about one output pixel (`round((f − 1)/2)` cells, f = grid cells per output pixel) stands in for the #1027 downsample-to-output. It merges into the terrain smoothing, which at the default 2% is already wider, so it changes nothing at default settings.
- **Off:** the #1038 behaviour exactly (`FieldTargetDims`, the display-res field above the floor). Batch `--relief-no-canonical-field`.

**Result:** at default smoothing the reference is identical at every window size, and the mean height gap vs 1440p falls to 0.6% at 270p and ≤ 0.3% from 540p. At smoothing 0 the small windows still differ (2–3%). That is the anti-alias blur, a sharpness change by design.

**Left as is:**
- **Cone tolerance** still uses the output's pixel angle. It is pixel-footprint anti-aliasing, and the residual 0.6% at 270p did not justify changing it in three kernels.
- **Per-distance mip filtering** is not needed once the grid is canonical and smoothed.

**Cost:** a small window now traces the floor-size grid instead of an output-size one (270p: about 0.35 s vs 0.2 s on the CPU trace; the prepass is cached). A window above the floor computes one extra floor-size field and traces a coarser grid, which came out slightly faster.

**Tests** (`ReliefCanonicalFieldTests`):
- 480×270 and 1280×720 hit heights agree within 0.8% of full height, and output-size shaping differs by more;
- the Robust reference is equal at 270p, 720p, 1080p and 1440p;
- field sizing, `TraceGrid`, and the poster's large-output field;
- the plate path ignores the setting;
- batch, builder, region and dialog parity.

## S4: distance-estimate height source (#1029)

`ReliefHeightSource.Distance` builds the height from the exterior distance estimate the calculators already fill (`DistanceBuffer`, complex-plane units, 0 in the set).

**How it works:**
- **Interface:** `IDistanceFieldSource` exposes the buffer plus `DistancePixelScale`. Mandelbrot and EscapeTime implement it.
- **Height formula:** `ReliefHeightField.Build` computes h = 10·exp(−d / (falloff·span)), where span = pixelScale·min(w,h), i.e. view units. Default `Relief2DDistanceFalloff` is 0.02.
- **Plateau:** in-set pixels take the full nominal height.  
  The constant 10 is independent of iteration counts, so the tone curve behaves the same at any zoom depth.
- **Fallback to Smooth** when a calculator has no distance field, or when most escaped pixels have no estimate (float underflow at very deep zooms).
- **Wiring:** all capture sites pass the source's buffer and scale: the host hi-res twin, the preview, the display-res field and the poster/batch. The poster's own below-floor hi-res field used to return raw counts, so it now applies the source as well.
- **Trap/Blend checks:** checks that meant "needs the orbit-trap field" now test Trap/Blend explicitly rather than `!= Smooth`.

**Batch:** `--relief-height-source smooth|trap|blend|distance` (the source had no batch flag before), `--relief-height-blend`, `--relief-distance-falloff`. Also region save/load, Command builder seed, dialog falloff slider and Defaults reset.

**Assessment at app defaults:** it is a different look (plateau and ridges, no interior pit), not a replacement for Smooth with terrain smoothing. It reads best on filament views such as the elephant valley. On dense views the in-set plateau (usually black) dominates, e.g. at the seahorse. It stays opt-in.

**Tests** (`ReliefDistanceSourceTests`): plateau, monotonic falloff and the exact e⁻¹ point; fallbacks; the same landscape at 240×135 and 480×270 (2×2 block means); the poster hi-res field carries the source; batch/builder/region/dialog parity.
