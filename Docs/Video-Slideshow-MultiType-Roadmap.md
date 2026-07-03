# Multi-Type Video Slideshow — Feasibility &amp; Roadmap

Status: **deferred / planning**. No code shipped. Spun out of the
[Animation Roadmap](Animation-Roadmap.md) open follow-ups (2026-07-03) as
its own project because the fix is real engine work, not an animation
follow-up patch.

## Problem

The video slideshow pool is **Mandelbrot-only**. The gate lives in
[`FractalRenderHost.Video.cs`](../Engine/Rendering/FractalRenderHost.Video.cs)
`VideoSlideshowLoop` (~line 1523):

```csharp
foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
    if (r.FractalType == FractalType.Mandelbrot
        && r.QualityPreset.Tier != QualityTier.Extreme
        && r.Zoom > SlideshowMinRegionZoom)
        regions.Add(r);
```

Every leg then forces `ViewState.FractalType = FractalType.Mandelbrot`
(~line 1601) and drives an unattended deep-zoom into the region's
`CenterX/Y` + `Zoom`.

### Why Mandelbrot-only

`FractalRegion` captures a geometric target (center, zoom, iterations,
quality) but **carries no per-engine parameters** — no Julia constant, no
Newton root/relaxation, no equation source name resolved to a live
calculator, no 3D camera/raymarch state. The Mandelbrot escape-time path
is the one family whose entire visual is reconstructable from center +
zoom + iterations alone, so it's the only family that can be faithfully
re-zoomed unattended from a saved region.

For other families an unattended zoom would either render the wrong image
(missing the per-engine params) or need those params re-plumbed and a
per-family "zoom leg" motion model that doesn't exist yet.

### Downstream impact (Animation Roadmap Phase 5)

Because the pool is Mandelbrot-only, the video slideshow's per-leg
animation resolver (`BuildVideoLegAnimators`, ~line 1784) only ever
resolves animations whose `TargetFractalTypes` include Mandelbrot.
Julia/Newton/3D animations still play fine on the **Image** and
**Animation** slideshow paths — just not on the **Video** path.

## Scope of a fix

1. **Per-engine params on the region asset.** Either widen `FractalRegion`
   to snapshot the full `FractalParameters` (versioned; large) or store a
   typed per-family sub-record. Persistence-shape change → migration.
2. **Per-family zoom-leg motion model.** Mandelbrot zooms into a point.
   What does a "video leg" mean for Julia (animate the constant? orbit?),
   Newton (rotate roots?), a 3D raymarch (fly the camera?)? Each family
   needs a defined unattended motion or an explicit "static hold" leg.
3. **Perturbation / reference-cache interaction.** Deep-zoom reference
   recompute is Mandelbrot-specific today; other families zoom via the
   plain per-pixel path with different cost curves.
4. **Fallback tiers.** Families with no motion model play a static
   (Ken-Burns-style pan or hold) leg rather than being excluded.

## Recommended phasing (when picked up)

- **P1** — Region asset carries per-family params (snapshot + migration).
  Unblocks faithful reconstruction; no motion yet (static holds only).
- **P2** — Julia video legs (animate the constant along a path) — the
  cheapest striking win; Julia is 2D escape-time like Mandelbrot.
- **P3** — 3D raymarch camera-fly legs (Mandelbulb / Mandelbox / KIFS).
- **P4** — Remaining 2D families (Newton, Phoenix, …) as static/pan legs
  or per-family motion where it reads well.

## Risk

Medium-high. Touches the region persistence shape (migration), the video
engine's leg loop, and per-family render cost. Not a follow-up — a
project. Deferred until explicitly scheduled.

## See also

- [Animation Roadmap](Animation-Roadmap.md) — Phase 5 video animation
  hooks that inherit this limitation.
- [`FractalRegion.cs`](../Engine/Models/FractalRegion.cs) — the region
  asset that would need per-engine params.
