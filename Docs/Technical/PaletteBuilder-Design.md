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
  **Side-by-side CVD preview UI LANDED (PR #685)** — the standalone PaletteBuilder
  "Colorblind" tab shows the selected palette as it reads to normal vision and under
  deutan / protan / tritan / full monochromacy (a swatch row per type, simulated through
  `CvdSimulation.Simulate`), recomputed on selection + live stop edits. **Remaining:** the
  same preview on the live fractal (needs the S10.3 fractal-preview wiring); the continuous
  CVD-ΔE-maximising ramp generator; redundant-encoding hints. Deterministic core is in.

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
  **Harmony + generation UI LANDED (PR #687)** — the PaletteBuilder "Harmony" tab shows
  the five OKLCH schemes around the palette's most chromatic stop, the IQ cosine rainbow,
  and a Bézier ramp threaded through the palette's own stops (lightness-corrected).
  **Remaining:** live-coefficient editing (today the cosine ramp is the fixed rainbow and
  the Bézier controls are the palette stops — a later editor slice), and applying a chosen
  scheme / generated ramp back onto the palette.

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
  k-clamp, empty bag, `Classify`). **Dominant/accent + lightness-ramp UI LANDED (PR #686)** —
  the extraction step factored out as `PaletteExtractionCore.Classify(clusters)` (dominant /
  accent / lightness order over already-weighted clusters, no re-clustering), which the
  PaletteBuilder "Key Colors" tab feeds with the palette's weighted swatches to show the
  dominant + accent swatches (hex + pixel share) and the palette re-ordered by lightness.
  **Remaining:** running perceptual k-means directly on the sampled image pixels as an
  extraction *method* (today it reuses the existing extractor's swatches) — a later slice.

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
  exposure brightens, determinism). **UI LANDED (PR #688)** — the shaded-gamut grid +
  crush/blow summary in the PaletteBuilder "3D Preview" tab.

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
  **UI LANDED (PR #688)** — the god-ray/haze strip + fog-optimised sub-ramp + wash-out
  warning in the "3D Preview" tab.

### S10.9 — Relief = luminance is form ◐ (core LANDED — PR #679)
Restate §2 as a 3D tool: a luminance-monotonic ramp makes relief read as raised
3D. Show the relief preview; warn when a ramp flattens it — the *same*
luminance-lock that helps colorblind readers.
- **Landed (core):** `Engine/Imaging/ReliefLegibility.cs` — relief extrudes a 2D
  fractal and shades it, so apparent form *is* luminance: a slope that climbs should
  brighten. `Analyze(stops, minSpread)` → a `ReliefReport` (`ReadsAsRelief`, `Flat`,
  `NonMonotonic`, `LuminanceSpread`, `Reversals`): it walks the ramp's OkLab-lightness
  sequence, counts direction reversals (ignoring sub-JND flat steps), and measures the
  end-to-end spread — a ramp reads as relief only when its lightness is monotonic AND
  spans enough range; a reversal (bright→dark→bright makes a rising slope read as
  up-then-down) or a too-flat spread flattens it. `LockLuminance(stops, count, minSpread)`
  is the repair: keep each stop's **hue + chroma** (OKLCH C, H) but overwrite lightness
  with a monotonic spine (respecting the ramp's overall dark→light / light→dark sense,
  widened to a readable range) — the artist's colour progression survives while relief
  reads as form again. This is the **same luminance-lock** that keeps a ramp legible
  under CVD (S10.2 — luminance is the channel that survives when hue collapses): one
  discipline, two payoffs. Reuses `PerceptualRamp` (OkLab / OKLCH). +6 `ReliefLegibilityTests`.
  **UI LANDED (PR #688)** — the relief verdict + flatten warning + luminance-locked
  repair ramp in the "3D Preview" tab.

### S10.10 — "Looks" (scene color scripts) ◐ (core LANDED — PR #680)
Pair a ramp with **material presets** (gold = warm ramp + low roughness + metallic)
and key-light / sky tint, saved as one unit — a scene color script. The on-brand
way the palette tool reaches into 3D **without becoming a material editor**.
Forward-hook: drives emission / transmission color when roadmap **S5** lands.
- **Landed (core):** `Engine/Imaging/SceneLooks.cs` — a `Look` record (name + ramp +
  `LookMaterial` {roughness, metallic} + `LookLighting` {key tint, sky tint} + nullable
  `EmissionTint` / `TransmissionTint` reserved for **S5**) is a tiny descriptor, *not* a
  node graph (design §6). `SceneLooks.FromRamp(name, stops)` composes a coherent look
  from any ramp: mean OKLCH chroma → material (high chroma → metallic + glossy, like gold
  / copper; flat / desaturated → rough dielectric, like stone / ash), and the lights are
  DRAWN FROM THE PALETTE — key = the ramp's brightest stop, sky = its darkest — so they
  stay on-brand. `Recolor(look, newStops)` re-skins a look with a new ramp while keeping
  its material identity (re-deriving only the palette-drawn lights). A built-in `Catalog`
  ships six hand-authored looks (Gold, Copper, Ice, Ember [authored emission], Jade,
  Obsidian). Reuses `PerceptualRamp` (OKLCH); pure + deterministic. +7 `SceneLooksTests`.
  **Look picker UI LANDED (PR #689)** — the PaletteBuilder "Looks" tab shows a look
  derived from the current palette (`FromRamp`) plus the built-in catalog, each with its
  ramp, material (roughness/metallic) and key/sky/glow tints. **Remaining:** save/recall
  of custom looks + apply-to-render (writing material / `LightingFxData`) — a host-wiring
  slice (the standalone tool has no live render to apply into).

**S10 analysis + generation cores COMPLETE (all tested):**
PerceptualRamp (S10.1) · CvdAnalysis (S10.2) · PaletteHistogram (S10.3) · ColorHarmony
(S10.4) · PaletteExtractionCore (S10.5) · ColorAdvisor (S10.6) · ShadedGamut (S10.7) ·
FogPalettePreview (S10.8) · ReliefLegibility (S10.9) · SceneLooks (S10.10). The only S10
work left is the **UI pass** — surfacing every core in the PaletteBuilder shell (deferred
throughout S10 per the "leave UI to the end" call).

### S10 UI pass ◐ (per-core surfaces done — PR #681, #685, #686, #687, #688, #689)
Surface the ten cores in the PaletteBuilder UI, one core per slice.
- **Plumbing (LANDED, PR #681):** the cores were authored in the Engine so the render +
  headless tests could reach them, but the PaletteBuilder UI references neither Engine nor
  its render machinery, and pulling Engine into the standalone tool would drag the whole
  render engine + codegen along. So the ten cores + the `ViewTransformOps` tonemap
  operators moved into a new leaf assembly **`FracturingFog.ColorCore`** (net10.0, refs
  only Abstractions for the `ViewTransform` enum; namespace stays `FracturingFog.Imaging`
  so no consuming code changed). Engine, `Server.Tests` (via Engine) and
  `UI.Avalonia` / `PaletteBuilder.Lib` now all reference this **one** copy — the WinExe
  root glob gains a `Compile Remove="ColorCore\**"`. Byte-identical: the full suite
  (2320/2320) and `--viewtransformprobe` pass unchanged with the cores compiled from the
  new assembly.
- **First core surfaced — S10.6 advisor (LANDED, PR #681):** `ImagePaletteViewModel` now
  runs `ColorAdvisor.Review` on the selected palette (recomputed on selection change and
  on live stop edits via `StopsChanged`) into an `Advisories` collection + `AdvisorySummary`;
  the standalone PaletteBuilder `MainWindow` gains an **"Advisor" tab** that lists them —
  painted `#FFCC00` with a neutral severity glyph (never red / green). CVD-first help lands
  in the UI first, on-brand.
- **Second core surfaced — S10.2 CVD side-by-side preview (LANDED, PR #685):**
  `ImagePaletteViewModel` builds a `CvdPreview` (a `CvdPreviewRow` per vision type —
  normal + deutan / protan / tritan / monochromacy — each the selected palette's stops
  simulated through `CvdSimulation.Simulate`, as bound swatch brushes), recomputed with
  the advisor on selection / stop edits. The standalone PaletteBuilder `MainWindow` gains
  a **"Colorblind" tab** stacking the rows so the artist sees the palette collapse (or
  survive) under CVD directly — the CVD-first differentiator.
- **Third core surfaced — S10.5 extraction dominant/accent (LANDED, PR #686):** the
  extraction classification factored out as `PaletteExtractionCore.Classify(clusters)`
  (so `Extract` and the UI share the exact dominant / accent / lightness-order rules).
  `ImagePaletteViewModel` runs it over the selected palette's raw **weighted** swatches
  (`Palette`, not the weight-1 `EffectivePalette`) into `DominantBrush` / `AccentBrush`
  (+ hex + pixel-share labels) and a `LightnessRamp`; the PaletteBuilder `MainWindow`
  gains a **"Key Colors" tab** showing the two picks and the lightness-ordered strip.
- **Fourth core surfaced — S10.4 harmony + generation (LANDED, PR #687):**
  `ImagePaletteViewModel` builds `HarmonySchemes` (the five OKLCH schemes around the
  palette's most chromatic stop, as `LabeledSwatchRow`s), a `CosineRamp` (the IQ cosine
  rainbow), and a `BezierPaletteRamp` (Bézier through the palette's stops in OkLab,
  lightness-corrected), recomputed with the rest on selection / stop edits. The
  PaletteBuilder `MainWindow` gains a **"Harmony" tab** stacking the scheme rows + the two
  generated ramps.
- **Fifth surface — S10.7–S10.9 3D preview trio (LANDED, PR #688):** `ImagePaletteViewModel`
  builds, recomputed with the rest, the shaded-gamut rows (`ShadedGamut.AnalyzeRamp` —
  each stop swept shadow → lit → specular + a crush/blow summary), the fog / god-ray strip
  and fog-optimised sub-ramp (`FogPalettePreview` + a wash-out warning), and the relief
  verdict + luminance-locked repair ramp (`ReliefLegibility`). The PaletteBuilder
  `MainWindow` gains a **"3D Preview" tab** with all three sections; the warning lines
  paint `#FFCC00` (never red).
- **Sixth surface — S10.10 "looks" picker (LANDED, PR #689):** `ImagePaletteViewModel`
  builds `Looks` — a look derived from the current palette (`SceneLooks.FromRamp`) plus the
  built-in `Catalog`, each wrapped as a `LookRowVm` (ramp swatches + material text + key /
  sky / glow tint brushes). The PaletteBuilder `MainWindow` gains a **"Looks" tab** listing
  them. **All ten S10 cores now have a self-contained UI surface.**
- **Remaining — the live-host-wiring track (§4a below):** the per-core *display* surfaces
  are all done; what's left couples the palette tool to the render host and is scoped
  separately.

### S10 §4a — Live-host-wiring track ◐ (scoped — sub-issues under #392)

The per-core UI surfaces above all run **inside** the palette tool on the selected
palette — no render dependency. The remaining work reaches **across the host boundary**
to the live render, and follows one architectural spine, already proven by
`IPaletteExtractionService`:

> The palette tool references neither Engine nor the render. Every render-facing
> capability is a **service interface in Abstractions**, **implemented in `Hosting`**
> (which has Engine access), and **injected into the VM** — the VM consumes the
> interface and stays render-free. New capabilities follow that exact shape.

**Keystone insight:** a palette change never changes geometry. So "preview on the live
fractal" does **not** need the tool to re-render — it needs the current view's **per-pixel
palette parameter** (the smooth-iteration / trap `t` the render already computes). Given
that buffer once, the tool re-tints locally through any candidate gradient (and CVD-sims
each pixel) with the ColorCore it already has, and builds the S10.3 histogram for free.
That turns the big rock from "inject a render engine" into "expose a `float[] t` + dims".

**Slices (dependencies stated; the repo has no auto-blocking — see the sub-issues):**

- **LW.1 (#690) — view-parameter provider (keystone). LANDED (PR #697).**
  `IPaletteViewParamService` + `PaletteViewSample(T, Width, Height)` in Abstractions;
  the pure `ViewParamNormalizer.NormalizeSmooth(smooth, maxIters, count)` (→ t∈[0,1],
  in-set/negative/non-finite → 0) in ColorCore (+4 tests); `FractalRenderHost.
  TryGetActiveSmoothField` snapshots the live Mandelbrot calc's `SmoothBuffer` +
  `MaxIterations` + dims (Mandelbrot-first; escape-time alts / relief can extend);
  `HostPaletteViewParamService` (Hosting) composes them; exposed as
  `AvaloniaShellBootstrap.PaletteViewParamService`, set once the render host exists.
  Injected like `PaletteService`. *Consumer UI deferred to LW.2 / LW.3.* *Blocks LW.2, LW.3.*
- **LW.2 (#693) — histogram-redistribute UI (S10.3). LANDED (PR #699).**
  `ImagePaletteViewModel` builds the view's palette-parameter histogram from the LW.1 `t`
  buffer (`PaletteHistogram.Build`, 32 bins) into `HistogramBarHeights` + a `ViewFitSummary`
  (`WastedFraction` → "this view never hits X% of the palette range"), and a
  `RedistributeCommand` repositions the selected palette's stops via `EqualizeStopPositions`
  (CDF-inverse, writes `EditableStops` positions). The PaletteBuilder "View Fit" tab shows
  the histogram strip, the wasted-range readout (`#FFCC00`) and the Redistribute button;
  empty when no live view. *Depends LW.1.*
- **LW.3 (#694) — palette + CVD preview on the live fractal (S10.2/S10.3). LANDED (PR #698).**
  `ImagePaletteViewModel` gained a settable `ViewParamService` (LW.1) and `LiveFractalViews`:
  when the host supplies the service, it re-tints the view's `t` buffer through the selected
  palette **in OkLab** (perceptual `SampleOkLab`) into a downsampled `WriteableBitmap` — the
  palette on the real fractal, no re-render — and the same view under deutan / protan /
  tritan / monochromacy (`CvdSimulation` per pixel). The PaletteBuilder "Live Fractal" tab
  shows the five as a `LabeledImage` grid; `AvaloniaDialogs` wires
  `AvaloniaShellBootstrap.PaletteViewParamService` into the picker (null / empty in the
  standalone tool — no live render). *Depends LW.1.*
- **LW.4 (#695) — apply scheme / ramp / look back (S10.4/S10.10).** *Split into 4a/4b/4c.*
  - **LW.4a — apply a generated ramp. LANDED (PR #701).** A harmony scheme / cosine rainbow
    / Bézier ramp is adopted as a new **selectable palette result** (`AddGeneratedRamp` →
    even-spaced `PaletteStop`s → a `PaletteResultViewModel` row, auto-selected), so the
    existing select → Apply / live-preview / advisor path applies it unchanged. "Use"
    buttons in the Harmony tab (`UseSwatchRowCommand` per scheme, `UseCosineRampCommand`,
    `UseBezierRampCommand`). No render-state risk — reuses the stops accept flow.
  - **LW.4b — Look → render. LANDED (PR #703).** `IPaletteLookApplyService` (Abstractions,
    primitives-only) → `HostPaletteLookApplyService` writes a look's `Roughness` / `Metallic`
    into `ViewState.FractalParameters.Lighting`, tints `Light1.Color` (key) + `BgTop/BgBottom`
    (sky), and re-renders via `FractalRenderHost.Trigger`. Injected as
    `AvaloniaShellBootstrap.PaletteLookApplyService` (wired into the picker by `AvaloniaDialogs`).
    The Looks tab's **"Apply to render"** button (`ApplyLookCommand`, shown only when the
    service is present) adopts the look's ramp (the LW.4a path) *and* writes its material +
    lights. Emission is omitted — `LightingFxData` has no emission field (the Look's
    `EmissionTint` stays the S5 forward-hook).
  - **LW.4c — save / recall custom looks. LANDED (PR #704).** `LookStore` (UI.Avalonia)
    persists user looks to `palette-looks.json` under `AppDataPaths.Root` (via a primitive
    DTO — Look's colour tuples don't round-trip through `System.Text.Json`). The Looks tab
    gains a name box + **"Save current as look"** (`SaveLookCommand` → `SceneLooks.FromRamp`
    over the current palette → store); saved looks list above the catalog with a **Delete**
    button (`DeleteLookCommand`, gated on `LookRowVm.CanDelete`). *Independent of LW.1.*
- **LW.5 (#691) — perceptual k-means as an extraction method. LANDED (PR #700).**
  `PerceptualKMeansExtractor : IPaletteExtractor` (in `Imaging/PaletteExtraction/`) wraps
  `PaletteExtractionCore.KMeansOkLab` — clusters the sampled image pixels in **OkLab**
  (perceptual, k-means++), a thin adapter over the tested core. Registered in both extractor
  lists — `HostPaletteExtractionService` (main-app picker) and the standalone tool's
  `PaletteExtractionService` — so "Perceptual K-Means (OkLab)" appears as a selectable
  method everywhere. *Independent; smallest.*
- **LW.6 (#692) — live cosine coefficient editing (S10.4). LANDED (PR #702).** The Harmony
  tab's cosine ramp gains four live sliders — brightness `a`, contrast `b`, frequency `c`,
  and a phase offset (added to the rainbow's per-channel d 0/⅓/⅔, so hues stay separated
  while the sweep rotates) — bound to `CosineA/B/C/Phase`; each setter calls
  `RebuildCosineRamp` so the strip (and its "Use as palette", LW.4a) update live. Seeded
  from the IQ rainbow defaults. *Bézier control-colour editing left as a follow-up.*

Recommended order: **LW.1 → LW.2, LW.3**; **LW.5, LW.6, LW.4** any time (LW.5 is the
quickest standalone win). Boundary unchanged (design §6): still a colour assistant — LW.4
writes a *material preset + light tints*, not a material node graph.

**Live-host-wiring track COMPLETE** (PRs #697, #699, #698, #700, #702, #701, #703, #704).
With it, **S10 is fully delivered**: ten analysis + generation cores, six per-core UI
surfaces, and the live-render integration (view-param provider, on-fractal palette + CVD
preview, histogram redistribute, perceptual k-means extraction, cosine editor, apply
generated ramp / look to palette + render, save/recall looks). Remaining nice-to-haves are
minor tails noted per slice (Bézier control-colour editing; emission when S5 lands).

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
