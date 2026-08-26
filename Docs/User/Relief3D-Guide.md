# Relief 3D — User Guide & Reference

Relief 3D turns a flat 2D fractal into terrain. It reads the fractal's own data
(how fast each point escapes, how many iterations it took, how dense the orbit is)
as a **height field**, then lights that height as raised relief — or renders it as
a full 3D landscape you can orbit, fog, and export as a printable mesh.

Two modes, from cheapest to richest:

1. **Screen-space relief** — embosses the flat image and drapes horizon cast
   shadows over it. Fast, keeps the exact framing of the 2D view.
2. **Oblique 3D raymarch** — a true 3D render of the height field from a tilted
   camera: real perspective, a silhouette against the sky, volumetric fog and
   god-rays, and mesh export.

For ready-made settings, see the companion
[Relief 3D Cookbook](Relief3D-Cookbook.md).

> Companion pages: [User Index](_Index.md) · [Relief 3D Cookbook](Relief3D-Cookbook.md) · [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) · [Heightfield Relief Spike (Technical)](../Technical/Heightfield-Relief-Spike.md)

> [!NOTE]
> **Fastest path to a 3D-looking fractal:** open Relief 3D, tick **Enable raised
> relief + cast shadows**, and lower the **Light elevation** to ~25° for long
> dramatic shadows. That's screen-space relief. For a true orbitable landscape,
> also tick **Oblique 3D raymarch**.

---

## 1. Where the controls live

Relief 3D has its own standalone panel:

- **Control Center → Color & Light → "Relief 3D…"** — opens the panel. It's
  independent of Fractal Params and stays open when you close Params.
- **Fractal Params → "Open Relief 3D…"** — the same panel, shown whenever the
  current fractal supports relief.

The panel shares the **Lighting & FX** dialog (soft shadows, AO, PBR, IBL/HDRI,
reflections, volumetric fog). Its **"Open Lighting & FX…"** button opens it
without leaving Relief 3D.

### Which fractals support it

Relief 3D applies to **2D fractal families**, each with a natural height source:

| Family | Height comes from |
|---|---|
| Mandelbrot, Julia, Multibrot, Phoenix, Glynn, Spider, Burning Ship, Tricorn, Magnet 1 & 2, and the generated Mandelbrot Z²–Z⁵ / Tricorn / Burning Ship variants | **Escape potential** (smooth iteration count) |
| Newton, Nova, Halley (root-finding) | **Iteration count** to convergence |
| Buddhabrot family | **Orbit density** |
| Apollonian gasket | **Synthesised dome** (sphere-cap height per disc) |

3D ray-marched fractals (Mandelbulb, Mandelbox, Quaternion, Kleinian, …) are
**already** 3D and don't use Relief 3D — they have their own camera and lighting.

---

## 2. How it works

The fractal's per-pixel value becomes a height `h(x, y)`. A **height curve**
shapes it first (see below), and a **height scale** exaggerates it. From there:

- **Screen-space relief** shades that height with a single light and marches
  along the light direction across the height field to drop **cast shadows** —
  all in the plane of the flat image. The result is blended over the themed color
  by **Relief strength**.
- **Oblique 3D raymarch** builds an actual 3D surface from the height field and
  sphere-traces it from a tilted camera. This gives true perspective, a
  silhouette edge, and access to the full shading pipeline (shadows, AO, PBR,
  IBL, and **volumetric fog / god-rays** — see
  [§6](#6-lighting--fx-shared-with-the-3d-pipeline)).

### The height curve

Raw fractal boundary data spikes hard — the escape count shoots up right at the
set edge. The **Height curve** tames that into terrain:

| Curve | Effect |
|---|---|
| **Log** (default) | Compresses the spikes into rolling terrain. Best general choice. |
| **Sqrt** | Gentler compression — more relief than Log, less spike than Linear. |
| **Linear** | Raw, un-tamed height. Spiky needles at the boundary; use for sci-fi/crystal looks. |

---

## 3. Screen-space relief controls

Enabled by **"Enable raised relief + cast shadows."** Fast; keeps the 2D framing.

| Control | Range | Default | What it does |
|---|---|---|---|
| **Height scale** | 0 – 6 | — | Vertical exaggeration. Higher = deeper carving and longer shadows. |
| **Light azimuth** | 0 – 360° | — | Compass direction of the light. 0 = from the right, 90 = from the top. |
| **Light elevation** | 1 – 89° | — | Light height above the plane. **Lower = longer, more dramatic cast shadows.** |
| **Shadow strength** | 0 – 1 | — | How dark cast shadows go. 0 = no shadows; 1 = fully black. |
| **Relief strength** | 0 – 1 | — | Blend between relief lighting and the flat themed color. 1 = full relief. |

> [!NOTE]
> Screen-space relief reads best on **smooth, non-noisy themes** and when you're
> zoomed into filament structure. Busy rainbow palettes fight the shading.

---

## 4. Oblique 3D raymarch controls

Enabled by **"Oblique 3D raymarch (perspective + volumetric)."** Heavier than
screen-space relief, but a true 3D scene.

### Camera & quality

| Control | Range | What it does |
|---|---|---|
| **Camera azimuth** | −180 – 180° | Orbit the terrain around the up axis. 0 = straight on. |
| **Camera elevation** | 5 – 89° | Camera height. 90° ≈ top-down (looks flat); low = raking, dramatic silhouette. |
| **Camera FOV** | 15 – 100° | Vertical field of view. Wider = more perspective distortion. |
| **Frame-fill zoom** | 0.2 – 5 | How much of the window the terrain fills. 1.0 = auto-fit; >1 pulls in (edges may clip); <1 pulls back for margin. |
| **Anti-alias (N×N)** | 1 – 4 | Supersampling: N×N rays/pixel averaged. 1 = fastest; 2–4 = smoother edges/silhouette at N² cost. |
| **Height curve** | Log / Sqrt / Linear | Tone curve on the height field (see [§2](#the-height-curve)). |
| **Edge fade** | 0 – 0.5 | Ramps the height to the base plane near the image edges, so structure running off-frame tapers out instead of forming streaky "arms". 0 = off. **Not** the same as the Lighting & FX panel's **Edge strength** — that is an unrelated screen-space silhouette-inking post-pass (colored strokes over edges), not a heightfield taper. |
| **Field floor (px)** | 480 – 2160 | Short-axis resolution the height field is computed at, independent of window size. Only active with **Hi-res height field**, and only when the window is smaller than this. |
| **Far detail** | 0.15 – 1 | Distant-filament resolving power. On screen the raymarch keeps detail tall near the camera but lets it fall toward the floor with distance (the distance-cone fattens at low resolution); the poster keeps it tall throughout. **Drag left** to tighten the far cone so distant filaments stay tall on screen too — the poster look, live. Lower = more far detail **and** slower (more marching). 1 = off / byte-identical. |

### Filament detail — raise the structure *relative to* the slab

**Height scale** is a single uniform multiplier: it scales the whole slab **and** the
filament texture on it together, so the fractal ridge never grows *relative* to the
slab it sits on. These knobs (in the **Filament detail** section; raymarch only) fix
that — the fractal outline grows and exaggerates while the base stays put:

| Control | Range | Default | What it does |
|---|---|---|---|
| **Detail exaggeration** | 1 – 6 | 1 (off) | Local high-pass (unsharp) on the height field: raises and sharpens the filament ridges **out of** the base slab, instead of scaling both together like Height scale. This is the main "make the structure taller and more distinct" knob. |
| **Feature size (px)** | 0 – 256 | 0 = auto | What counts as "slab" vs "detail" for the exaggeration — the blur radius of the base. Larger = a coarser base, so broader features get raised too. Auto ≈ 1.5% of the field. |
| **Height gamma** | 0.2 – 4 | 1 (off) | Top-end contrast on the height (`h^gamma`). Above 1 pushes the high filament band up away from the base (peak preserved); below 1 flattens it. |

> [!TIP]
> Keep the **Height curve** on **Log** (tames spikes) and reach for **Detail
> exaggeration** to bring the structure back up — you get terrain-like relief with
> filaments that still stand proud of the slab. Then **Height scale** sets the
> overall vertical size. Batch: `--relief-detail-gain`, `--relief-detail-radius`,
> `--relief-height-gamma`.

### Toggles

| Toggle | What it does |
|---|---|
| **Hi-res height field** | Computes the field at **Field floor** resolution instead of the window size, so shrinking the window no longer turns terrain into a spiky needle-forest — every size matches the maximized look. Costs an extra field render below the floor; no effect at/above it. |
| **Orthographic camera** | Parallel projection — a clean relief-map look with no perspective stretch. |
| **Base ground plane** | Renders a floor so the fractal sits on the ground and cast shadows land on it, instead of a slab floating in the sky. |
| **Bicubic height sampling** | Catmull-Rom interpolation instead of bilinear — smoother terrain when zoomed in, at extra sample cost. |
| **Auto lighting defaults** | Fills sensible AO / soft-shadow / specular defaults for the 3D view **when those Lighting FX knobs are still zero.** Anything you set explicitly in Lighting & FX always wins. |

---

## 5. Isolate object (standalone 3D cutout)

**"Isolate object (drop background → transparent)"** keeps only the fractal
structure as a standalone 3D object over a transparent background — it exports as
a cutout. Best paired with **Show sky backdrop OFF** (Lighting & FX) and **Base
ground plane OFF**.

Two independent cull methods (use either or both):

| Control | What it does |
|---|---|
| **Drop low-detail (keep filaments)** | Removes cells whose local height gradient is below a threshold — culls flat background and smooth plateaus, leaving the fine filaments. |
| **Drop amount** | Fraction of the flattest cells to remove (a quantile). Higher = drops more background. |
| **Drop colours** | Also culls cells whose themed color matches a listed color. |
| **Drop colours (hex)** | Comma-separated RGB/ARGB hex (e.g. `FF102030, 405060`). Use **Pick** to eyedrop a color from the screen. |
| **Colour tolerance** | How close a cell color must be to a drop color to be removed. Higher = a wider range goes. |

---

## 6. Lighting & FX (shared with the 3D pipeline)

In **oblique raymarch** mode, Relief 3D is a full member of the 3D shading
pipeline. Everything in the **Lighting & FX** dialog applies:

- Soft shadows, DE-cone + screen-space AO, PBR (roughness/metallic/specular),
  sub-surface, IBL / HDRI environment, one-bounce reflections, tone-map / bloom.
- **Volumetric fog and god-rays** — set **Fog density** + **Volume steps** in
  Lighting & FX and the terrain gets real light shafts and atmosphere, including
  shafts against the sky just above the ridge. See the
  [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) and
  [Cookbook](Volumetric-Lighting-Cookbook.md), but note relief needs
  **higher fog density** than a unit-scale 3D fractal and the fog is bounded to
  the terrain's height band — the relief-specific caveats are in the
  [Relief 3D Cookbook → god-rays recipe](Relief3D-Cookbook.md#6--foggy-valley-with-god-rays).

**Auto lighting defaults** (§4) is the quick way to a good-looking 3D view: it
seeds AO/shadow/spec only where you haven't set them.

---

## 7. GPU vs CPU relief

The oblique raymarch runs on the **GPU by default**, with shading-pipeline
parity for the surface (shadows, AO, PBR, IBL, reflections) and volumetric fog.

- **Toggle:** `Ctrl+Shift+G`, or **Control Center → Color & Light → "GPU relief
  raymarch."**
- **Off** falls back to the **CPU sphere-trace** — the parity oracle used to
  verify the GPU output. The window title gains a **`[RELIEF GPU OFF]`** suffix
  so you always know which path rendered.
- The toggle is inert unless oblique raymarch mode is active.

> [!NOTE]
> **The two paths differ in the fog only.** The GPU path scatters fog from the
> **key light (Light 1) only** — Lights 2/3 still light the surface, but only
> Light 1 carves god-ray shafts and tints the haze (via **Anisotropy** and **Fog
> color**). The CPU path scatters all three lights and supports the palette-mapped
> fog (**Palette map**). For a single hero shaft the GPU path is exactly what you
> want; for multi-light colored haze or palette fog, turn GPU relief off.

Use the CPU path when you want the reference image, multi-light/palette fog, or
are debugging a GPU artifact; otherwise leave GPU on for speed.

---

## 8. Mesh export (3D print / DCC)

The oblique 3D object exports as a **watertight mesh**:

- **OBJ** — with per-vertex color and smooth normals (for rendering / DCC apps).
- **STL** — binary (for slicers / 3D printing).

Export honours the isolation cull and these knobs:

| Control | Range | What it does |
|---|---|---|
| **Height** | 0.02 – 0.6 | Relief (emboss) height of the mesh, independent of the on-screen 3D height. Lower = clean bas-relief from any angle; higher = dramatic but spikier side-on. |
| **Smoothing** | 0 – 1 | 0 = raw detail (spiky); 1 = heavy despike + merge dendrites into clean ridges. 0.5 is the tuned default. |
| **Detail (grid)** | 128 – 1536 | Grid resolution on the longer axis. Higher = finer detail **and** bigger file. |
| **File size cap (MB)** | 0 – 50 | When > 0, the grid is clamped so the estimate stays under this size. 0 = unlimited. |
| **Underside** | 0 – 1 | Contour the back with the same smoothed relief instead of a flat base. 0 = flat back; 1 = back bulges as deep as the top rises. |

The panel shows a live **Estimated size** as you tune. Click **"Export mesh
(OBJ / STL)…"** to save.

> [!NOTE]
> The mesh **Height** and **Smoothing** are separate from the on-screen relief —
> a scene tuned to look dramatic on screen (tall, spiky) often prints best with a
> lower mesh height and ~0.5 smoothing so it reads cleanly as a physical object.

---

## 9. Regions, slideshow, and animation

- **Saved per region.** Relief settings travel with a saved region, so a relief
  scene recalls exactly. Relief also carries through **slideshow cross-fades**.
- **Lock Relief 3D** (Control Center) — **off**: relief follows the region (on for
  relief regions, off for plain ones). **On**: relief stays as-is regardless of
  the region's saved setting — handy for auditioning relief across many regions.
- **Gamma** (Control Center, −100 … +100, default 0) is a post-FX brightness
  control that compounds with the theme's palette gamma — a quick way to lift or
  deepen relief contrast without touching the lighting.
- Animate camera azimuth (turntable), light angle, or fog over a Scene Engine
  timeline for video.

---

## 10. Best-look and performance tips

- **Dedicated relief themes** — *Gold Relief*, *Marble Relief*, and *Neon Relief*
  are tuned for the height shading. Smooth, low-noise themes generally read best.
- **Long shadows** come from **low light elevation** (screen-space) or **low
  camera elevation** + a low-elevation Lighting FX key light (raymarch).
- **Tame the boundary** with the **Log** height curve; drop **Edge fade** in if
  panned structure grows streaky arms at the frame edge.
- **Shrinking the window makes needles?** Turn on **Hi-res height field** and set
  a **Field floor** (e.g. 1080) so small windows match the maximized look.
- **Field floor is a *quality* knob, not a height knob.** Use **Height scale** (and
  the **height curve**) to make filaments taller — not a low field floor, whose
  "height" is an undersampling artifact that won't survive the export.
- **Predicting the export.** A poster / wallpaper renders at far higher resolution
  (and often a wider aspect) than the window, so it shows more of the same field.
  Two aids:
  - **Poster preview — `Ctrl+Shift+P`.** Renders the current view through the exact
    export path, at the **current on-screen aspect** and a higher resolution, and
    opens it in a preview window (fit to window). This is what **Save Image** / a
    **Poster** at this aspect will look like — so you see the extra detail the small
    window hides, before you save. (A *Wallpaper* spans all monitors, so it re-frames
    to that ultrawide shape — use the frame guide below to judge that crop.)
  - **Export frame guide — `Ctrl+Shift+F`.** Overlays the export (wallpaper /
    multi-monitor) aspect as a letterbox on the live view, so you compose inside the
    frame the export will actually use. (Composition guide — the export re-frames at
    its own aspect, it is not a pixel-exact crop.)
- **Performance:** oblique raymarch cost scales with resolution × Anti-alias²,
  plus the shading FX you enable. Tune at AA = 1, raise to 2–4 for the final
  frame. The GPU path is far faster for heavy scenes.

---

## 11. Accessibility note

Relief reads through **light and shadow**, not hue — it's a strong choice if you
distinguish colors by hue with difficulty (e.g. red/green color vision
deficiency). Lean on **Height scale**, **light elevation** (shadow length), and
**Relief strength** for contrast. When adding colored fog or a themed palette,
prefer blue↔amber over red↔green, and use a yellow like `FFFFCC00` where you'd
reach for red.

---

## See also

- [Relief 3D Cookbook](Relief3D-Cookbook.md) — copy-paste recipes.
- [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) / [Cookbook](Volumetric-Lighting-Cookbook.md) — fog + god-rays on relief terrain.
- [Heightfield Relief Spike](../Technical/Heightfield-Relief-Spike.md) — the design/technical notes.
