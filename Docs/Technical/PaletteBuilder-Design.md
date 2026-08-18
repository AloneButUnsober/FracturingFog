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

### S10.1 — Perceptual core ☐
Author, interpolate and measure ΔE in **OKLCH / OkLab**, not sRGB; emit
perceptually-even ramps to the render. Ship the **viridis / cividis** family
(cividis is CVD-optimized) and a generator for uniform, CVD-safe ramps. The
viridis lesson from scientific viz: perceptually-uniform + monotonic-luminance
ramps are simply better.
- **Reuse:** existing OkLab extraction math.
- **Contract:** sRGB↔OkLab↔OKLCH round-trips get epsilon-stable tests.

### S10.2 — CVD-first suite ☐ (the differentiator)
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

### S10.3 — Fractal-aware preview ☐
- Palette live **on the real fractal** (2D + 3D), not a gradient bar.
- **Histogram-aware stop mapping** — show where stops land on *this view's*
  iteration density (reuse `HistogramEqualizer`); let the artist redistribute to
  match. "Your palette spends 40% of its range on iterations this view never hits
  — redistribute?"
- **Seamless-cycle guarantee** — for palette cycling (`CyclingGradientColorMap`),
  endpoint = startpoint in perceptual space; preview the cycle.
- **Reuse:** render engine + iteration histogram.

### S10.4 — Harmony + generation in perceptual space ☐
Adobe-Color harmony rules (complementary / triadic / analogous / split) computed
in **OKLCH**; an **IQ cosine-palette** editor (`a+b·cos(2π(c·t+d))`, matches the
ColorGen idiom) with live coefficients; **chroma.js**-style bezier-through-Lab +
lightness correction for high-quality ramps.

### S10.5 — Extraction upgrades ☐
Perceptual **k-means in OkLab**; extract an *ordered ramp* (by lightness), not just
a swatch set; dominant + accent detection.

### S10.6 — Color advisor ☐ (the artist-*assistant* framing)
The parity-twin discipline applied to color: automated checks that *guide*, not
just tools that sit there. Surface CVD-collapse, **shadow-crush** ("the low third
of this ramp crushes to black under 3D shading"), histogram-waste and cycle-seam
breaks as gentle, dismissible `#FFCC00` advisories. A **linter for color**.

## 5. Enhancing the 3D artist experience

Color in 3D collides with light, shadow, fog and material — PaletteBuilder should
design for that, not for flat iteration coloring alone.

### S10.7 — Preview under 3D lighting ☐
A ramp that sings flat can muddy under shading (shadow crushes the low end, spec
blows the high end). Show the palette's **shaded gamut** — full-shadow → lit →
specular. Author in **linear** and preview through the tonemap (ties to roadmap
**S2**).

### S10.8 — Fog / volumetric palette preview ☐
The palette now colors the **fog** via optical-depth remap (shipped in #185).
Preview the ramp as god-rays / haze and offer a fog-optimized sub-ramp.

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

## 6b. Documentation discipline (every slice)

Docs ship with the code, not after. **Every S10 slice must update existing docs and
create new documentation where warranted:**

- **Update in step** — a slice that changes color behavior updates the docs that
  describe it (this doc, the [ColorTheme Enhancement Roadmap](ColorTheme-Enhancement-Roadmap.md),
  and any User color guide) in the same PR.
- **Create when new** — a user-facing capability (CVD simulation, color advisor,
  "looks", perceptual export) gets its own User guide — a colorblind-first authoring
  guide is itself a differentiator worth writing — indexed in
  [`_Index.md`](_Index.md) / the [User Index](../User/_Index.md) and cross-linked to
  issue #392 both ways.
- **Keep doc ↔ issue in step** — this doc is the canonical design; #392 is the
  canonical task list. A slice landing without its doc update is incomplete.

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
