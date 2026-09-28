# Relief 3D consistency

Epic #1025. Slices #1026 (height modes), #1027 (tolerance anchor), #1028 (canonical field), #1029 (distance height source).

The same Relief 3D settings should give the same relief whatever is in view, wherever the camera sits, and whatever the window size. Before this epic, three independent mechanisms broke that.

## The three causes

| Cause | Where | Symptom | Slice |
|---|---|---|---|
| Per-frame auto-normalisation | `HeightfieldRaymarch2D.BuildPrepass` baseline (60th percentile of non-zero heights) and `sy = 0.35·amp·HeightScale / maxH` | Height drifts with content; one needle flattens the view | #1026 |
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
