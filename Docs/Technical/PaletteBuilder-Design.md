# PaletteBuilder — Perceptual, Colorblind-First Color Assistant

Design document for growing PaletteBuilder from a palette extractor/editor into a
*great* artist assistant for fractal color — perceptual, **colorblind-first**,
fractal-aware, and advisory. Backs roadmap slice **S10** (issue #392) of the
[3D Rendering Roadmap](3D-Rendering-Roadmap.md); this doc is the canonical design,
#392 is the canonical task list — keep them in step.

Status legend: ☐ not started · ◐ in progress · ☑ shipped

---

## 1. Positioning — the home of FF's art idiom

FF has two idioms (see the 3D roadmap §1): the *engineering* idiom is the
parity-twin slice; the *art* idiom is the color-theme / palette system. **PaletteBuilder
is the home of the art idiom.** Making FF great — not just deep-zooming — means
making this tool a genuinely great color assistant.

The lane discipline mirrors the mesh-export one: PaletteBuilder is a **color/palette
assistant**, not a general image editor. It extracts *from* images and designs
color — it does not become Photoshop (no layers, brushes, freeform paint), a DAM,
or a material node editor.

Almost no creative color tool is built **colorblind-first**. That is a real,
unclaimed gap — and it matters personally to this project (the maintainer is
red/green colorblind; FF's own UI already uses `#FFCC00`-not-red for exactly this
reason). Owning colorblind-first color authoring is both a differentiator and a
principle.

## 2. The unifying insight — luminance is load-bearing twice

**In 3D, form reads from shading: luminance *is* apparent relief/depth.**
**In colorblind vision, luminance is the channel that survives when hue collapses.**

So one discipline — *perceptually-uniform, luminance-structured* ramps — serves
both at once:

- A hue-only ramp flattens relief **and** fails colorblind readers.
- A luminance-monotonic ramp makes relief pop **and** stays legible under
  deuteranopia / protanopia / full monochromacy.

This is not two features. It is one philosophy, and it should be the spine of the
whole tool. Everything below is downstream of it.

## 3. Reuse thesis (same as the render roadmap)

PaletteBuilder should be *assembled from what FF already computes*, not rebuilt:

| Assistant needs | FF already has |
|---|---|
| perceptual color math | OkLab in the extraction lib (`Imaging/PaletteExtraction/GradientInterpolation.cs`, `ColorSpaces`) |
| live fractal preview | the render engine (2D + relief + bulb) |
| "where do colors land on THIS fractal" | the iteration histogram (`HistogramEqualizer`, HE port #145) |
| compact GPU-friendly ramps | the ColorGen cosine-palette idiom |
| colorblind-safe UI precedent | the `#FFCC00`-not-red convention already in FF's UI |
| perceptual ramp interpolation | OkLab/Lab interpolation already in the extraction lib, **not yet wired to render** |

The render path today is "Linear sRGB only, hardcoded" (see
[ColorTheme Enhancement Roadmap](ColorTheme-Enhancement-Roadmap.md)); PaletteBuilder
is the natural place perceptual color *lives* and from which perceptually-even
ramps flow into the render.

## 4. Slices

### S10.1 — Perceptual core ◐ (LANDED — PR #670)
Author, interpolate and measure ΔE in **OKLCH / OkLab**, not sRGB; emit
perceptually-even ramps to the render. Ship the **viridis / cividis** family
(cividis is CVD-optimized) and a generator for uniform, CVD-safe ramps. The
viridis lesson from scientific viz: perceptually-uniform + monotonic-luminance
ramps are simply better.
- **Reuse:** existing OkLab extraction math.
- **Contract:** sRGB↔OkLab↔OKLCH round-trips get epsilon-stable tests.
- **Landed:** `Engine/Imaging/PerceptualRamp.cs` — OkLab ⇄ OKLCH, OkLab ΔE
  (`DeltaEOk`), perceptually-even multi-stop sampling (`SampleOkLab`, interpolates IN
  OkLab), the viridis / cividis built-ins (`Viridis`/`Cividis`, sampled in OkLab), a
  luminance-monotonic (CVD-safe) ramp generator (`UniformLuminanceRamp`), and `Emit`
  (N sRGB stops → the render). Placed in Engine (not the PaletteBuilder-only extraction
  lib) so perceptually-even ramps reach the render **and** the headless tests. Shipped
  the **Cividis** render theme (`CividisColorMap`, registered in `ColorPalette.BuiltIns`)
  — the colourblind-first sibling of Viridis. +8 `PerceptualRampTests` (round-trips,
  ΔE, monotonic luminance, endpoints). UI + the remaining slices (S10.2 CVD suite next)
  build on this core.

### S10.2 — CVD-first suite ◐ (the differentiator; core LANDED — PR #671)
- **Live CVD simulation** — deutan / protan / tritan / monochromacy, side-by-side,
  on the palette **and** the fractal preview. Use **Machado 2009** (or
  Brettel–Viénot) — the accepted models.
- **Confusability linter** — flag stop pairs whose ΔE *in CVD-simulated space* is
  too low ("these collapse under deuteranopia — nudge?").
- **Luminance-lock mode** — enforce monotonic lightness so the ramp survives full
  monochromacy (and reads as 3D form — §2).
- **CVD-safe generators** — Okabe-Ito 8-color for categorical/banded coloring; for
  continuous fractal ramps, maximize CVD-simulated ΔE along the sweep.
- **Redundant-encoding hints** — for banded coloring, pair hue with luminance so
  meaning never rides on hue alone.
- **Contract:** CVD sim + ΔE are deterministic → assert in tests (the color analog
  of the render parity twin).
- **Landed (core):** `Engine/Imaging/CvdAnalysis.cs` — `CvdSimulation.Simulate`
  (Machado 2009 matrices in linear RGB, severity-lerp from identity; monochromacy =
  Rec.709 luminance grey) + `PaletteLint`: `Confusables` (ΔE in CVD-simulated OkLab
  below a threshold, per type, worst-first), `IsLuminanceMonotonic` (the luminance-lock
  check; the generator itself is S10.1's `UniformLuminanceRamp`), and the **Okabe-Ito**
  8-colour CVD-safe categorical set. +7 `CvdAnalysisTests` (severity-0 identity,
  monochromacy grey, determinism, red/green deutan collapse vs normal, confusables flag
  red/green but not black/white, Okabe-Ito clears a JND, luminance-monotonic detection).
  **Remaining:** live side-by-side CVD preview UI (on palette + fractal); the
  continuous CVD-ΔE-maximising ramp generator; redundant-encoding hints — the UI + the
  advisor surfacing (S10.6). Deterministic core is in; suite 2270/2270.

### S10.3 — Fractal-aware preview ◐ (core LANDED — PR #673)
- Palette live **on the real fractal** (2D + 3D), not a gradient bar.
- **Histogram-aware stop mapping** — show where stops land on *this view's*
  iteration density (reuse `HistogramEqualizer`); let the artist redistribute to
  match. "Your palette spends 40% of its range on iterations this view never hits
  — redistribute?"
- **Seamless-cycle guarantee** — for palette cycling (`CyclingGradientColorMap`),
  endpoint = startpoint in perceptual space; preview the cycle.
- **Reuse:** render engine + iteration histogram.
- **Landed (core):** `Engine/Imaging/PaletteHistogram.cs` — `Build` (histogram over the
  view's palette-parameter t), `WastedFraction` (the "this view never hits X% of your
  palette" metric), `EqualizeStopPositions` (CDF-inverse redistribution that packs stops
  where the pixels are — the render's histogram-EQ idiom, #145, on the t-domain), and
  `CycleSeamDeltaE` / `IsSeamlessCycle` (the cycle-join gap in OkLab ΔE, reusing S10.1).
  +5 `PaletteHistogramTests`. **Remaining:** wire it live on the real fractal preview
  (feed the view's per-pixel t; the "redistribute?" + cycle preview UI) — the UI slice.

### S10.4 — Harmony + generation in perceptual space ◐ (core LANDED — PR #674)
Adobe-Color harmony rules (complementary / triadic / analogous / split) computed
in **OKLCH**; an **IQ cosine-palette** editor (`a+b·cos(2π(c·t+d))`, matches the
ColorGen idiom) with live coefficients; **chroma.js**-style bezier-through-Lab +
lightness correction for high-quality ramps.
- **Landed (core):** `Engine/Imaging/ColorHarmony.cs` — `ColorHarmony` (OKLCH hue
  rotation keeping L+C; `Harmony` sets: complementary / triadic / analogous /
  split-complementary / tetradic, base first), `CosinePalette` (IQ `a+b·cos(2π(c·t+d))`
  per channel + `Rainbow` default + `Emit`), and `BezierRamp` (De Casteljau **in OkLab**
  through control colours, optional **lightness correction** that re-times the curve so
  OkLab lightness rises evenly / monotonically end-to-end). +6 `ColorHarmonyTests`.
  **Remaining:** the live-coefficient editors + harmony picker UI (the PaletteBuilder UI
  slice, deferred with the other S10 UI tails).

### S10.5 — Extraction upgrades ◐ (core LANDED — PR #676)
Perceptual **k-means in OkLab**; extract an *ordered ramp* (by lightness), not just
a swatch set; dominant + accent detection.
- **Landed (core):** `Engine/Imaging/PaletteExtractionCore.cs` — `PaletteExtractionCore`
  clusters an sRGB colour bag with **Lloyd's k-means in OkLab** (k-means++ seeded, so
  distances are perceptual and degenerate bags don't collapse), then `Extract(samples,
  k, seed)` returns an `ExtractedPalette`: the survivors as a **lightness-ordered ramp**
  (ascending OkLab L — a ramp is a curve, not a swatch bag), plus **`Dominant`** (the
  heaviest cluster — the workhorse) and **`Accent`** (the most chromatic non-dominant
  cluster — the pop colour). Cluster swatches are the mean sRGB of the assigned samples;
  empty clusters are dropped (so `k` is an upper bound). Reuses `PerceptualRamp` for the
  OkLab primitives. Placed in Engine (not the PaletteBuilder extraction lib, which the
  render + headless tests can't reach) — an extracted ramp flows into the render. +7
  `PaletteExtractionCoreTests` (separation, lightness order, dominant/accent, determinism,
  k-clamp, empty bag). **Remaining:** wire the core into the PaletteBuilder extraction UI
  (feed it the sampled image pixels; surface dominant/accent) with the other S10 UI tails.

### S10.6 — Color advisor ◐ (the artist-*assistant* framing; core LANDED — PR #675)
The parity-twin discipline applied to color: automated checks that *guide*, not
just tools that sit there. Surface CVD-collapse, **shadow-crush** ("the low third
of this ramp crushes to black under 3D shading"), histogram-waste and cycle-seam
breaks as gentle, dismissible `#FFCC00` advisories. A **linter for color**.
- **Landed (core):** `Engine/Imaging/ColorAdvisor.cs` — `ColorAdvisor.Review(stops,
  viewHistogram?, cycling)` composes the S10.1–S10.4 cores into a list of typed
  `ColorAdvice` (`Kind` ∈ CvdCollapse / ShadowCrush / HistogramWaste / CycleSeam +
  `Severity` + a plain-text message the UI paints `#FFCC00`): CVD-collapse via
  `PaletteLint.Confusables` (S10.2), histogram-waste via `PaletteHistogram.WastedFraction`
  + cycle-seam via `IsSeamlessCycle` (S10.3), and a new `ShadowCrushes` check (attenuate
  the low-third colours in linear light and flag when their OkLab spread collapses — the
  dark tail losing separation under 3D shading). +6 `ColorAdvisorTests` (each check fires
  correctly; a clean palette yields no advice). **Remaining:** surface the advisories in
  the PaletteBuilder UI (with the other S10 UI tails).

## 5. Enhancing the 3D artist experience

Color in 3D collides with light, shadow, fog and material — PaletteBuilder should
design for that, not for flat iteration coloring alone.

### S10.7 — Preview under 3D lighting ◐ (core LANDED — PR #677)
A ramp that sings flat can muddy under shading (shadow crushes the low end, spec
blows the high end). Show the palette's **shaded gamut** — full-shadow → lit →
specular. Author in **linear** and preview through the tonemap (ties to roadmap
**S2**).
- **Landed (core):** `Engine/Imaging/ShadedGamut.cs` — `ShadedGamut.Sweep(rgb, steps,
  transform, exposureEv, ambient, specularStrength)` shades one albedo across the
  lighting sweep **in linear light** (diffuse ambient→1 over the first 70%, additive
  white specular over the last 30%) and previews it **through the render's view
  transform** (`ViewTransformOps`, S2) before the single sRGB encode — so a tonemap
  rolls the highlights off while `ViewTransform.None` hard-clips them. `Analyze`
  returns a `ShadedSwatch` with the sweep plus two warnings: **`CrushesInShadow`** (the
  shadow end lands within a JND of black — detail vanishes in shade) and
  **`BlowsInSpecular`** (the specular end washes to white — hue lost in highlights);
  `AnalyzeRamp` does a whole ramp (the shaded-gamut grid). Reuses `ViewTransformOps`
  (S2 tonemap + transfer) and `PerceptualRamp` (OkLab ΔE). +7 `ShadedGamutTests`
  (rise shadow→specular, crush/blow classification, AgX rolls off what None clips,
  exposure brightens, determinism). **Remaining:** draw the shaded-gamut grid in the
  PaletteBuilder preview (with the other S10 UI tails).

### S10.8 — Fog / volumetric palette preview ◐ (core LANDED — PR #678)
The palette now colors the **fog** via optical-depth remap (shipped in #185).
Preview the ramp as god-rays / haze and offer a fog-optimized sub-ramp.
- **Landed (core):** `Engine/Imaging/FogPalettePreview.cs` — mirrors the render's
  #180/#185 optical-depth fog remap (`ShadingPipeline.VolumetricInScatterSegment`):
  `FogSweep(fogStops, steps, bg, density, maxDepth)` composites the ramp over a backdrop
  across optical depth **in linear light** — τ = s·maxDepth, T = exp(−τ) (Beer–Lambert),
  in-scatter = ramp sampled at (1−T) (the render's fog key), `out = bg·T + inscatter·(1−T)`
  — so a thin sample is nearly pure backdrop and a thick one nearly pure fog (the
  god-ray/haze read). `FogInscatter` exposes the optical-depth-keyed sample directly.
  `WashesOut` flags a ramp that produces no visible haze gradient (composited sweep's
  max OkLab ΔE below threshold — too close to the backdrop, or too flat). `FogOptimizedSubRamp`
  derives a fog sub-ramp: fog in-scatter *adds* light, so the ramp's dark reach
  (OkLab L < a floor) contributes nothing and is culled, and the survivors are re-emitted
  luminance-ASCENDING (thicker fog reads as *more*); an all-dark source is lifted toward
  white so the result is never empty. Reuses `PerceptualRamp` (OkLab sampling + ΔE) and
  the same sRGB↔linear transfer the render composites in. +6 `FogPalettePreviewTests`.
  **Remaining:** draw the god-ray/haze strip + offer the sub-ramp in the PaletteBuilder
  UI (with the other S10 UI tails).

### S10.9 — Relief = luminance is form ☐
Restate §2 as a 3D tool: a luminance-monotonic ramp makes relief read as raised
3D. Show the relief preview; warn when a ramp flattens it — the *same*
luminance-lock that helps colorblind readers.

### S10.10 — "Looks" (scene color scripts) ☐
Pair a ramp with **material presets** (gold = warm ramp + low roughness + metallic)
and key-light / sky tint, saved as one unit — a scene color script. The on-brand
way the palette tool reaches into 3D **without becoming a material editor**.
Forward-hook: drives emission / transmission color when roadmap **S5** lands.

## 6. Non-goals (not a worse Photoshop)

| Tempting | Verdict | Why |
|---|---|---|
| General image editor (layers / brushes / filters) | **Skip** | Extract *from* images; don't edit them |
| Full DAM / asset browser | **Skip** | Not a color assistant's job |
| Freeform vector / paint canvas | **Skip** | Off-idiom |
| Node **material editor** | **Careful** | Pair palette *with* material presets (S10.10); don't build a material node graph (that is the S9 / Blender line) |

## 7. Sequencing

1. **S10.1** perceptual core — underpins everything.
2. **S10.2** CVD-first suite — the differentiator; land early.
3. **S10.3** fractal-aware preview — reuses render + histogram.
4. **S10.6** advisor + **S10.7–S10.10** 3D items — ride on top.

Independent of the render-pipeline slices; couples to **S2** (linear / tonemap)
for the shaded-gamut preview (S10.7).

## 8. Strategy in one line

**Author color in perceptual space, structured by luminance — the one discipline
that makes relief read as 3D form and keeps every ramp legible to colorblind eyes
— previewed live on the real fractal under real lighting, with a color advisor that
catches CVD-collapse, shadow-crush and histogram-waste before the artist does.**

---

## References

- [3D Rendering Roadmap](3D-Rendering-Roadmap.md) — S10 slice + S2 (linear/tonemap) coupling.
- [ColorTheme Enhancement Roadmap](ColorTheme-Enhancement-Roadmap.md) — current color-system architecture; the "linear sRGB only" gap S10.1 addresses.
- [Volumetric Color Plan](Volumetric-Color-Plan.md) — the fog palette (slice D, #185) S10.8 previews.
- Parent issue #389; slice issue #392.
