# Relief 3D Cookbook

Copy-paste recipes for turning 2D fractals into lit relief and 3D terrain. Each
lists exact controls, explains *why* it works, and offers variations. For what
each control means, see the [Relief 3D Guide](Relief3D-Guide.md).

All controls are in the **Relief 3D** panel (Control Center → Color & Light →
*Relief 3D…*, or Fractal Params → *Open Relief 3D…*). Some recipes also use the
**Lighting & FX** dialog (reachable from the panel's *Open Lighting & FX…*
button). Colors are `AARRGGBB` hex.

> Companion pages: [User Index](_Index.md) · [Relief 3D Guide](Relief3D-Guide.md) · [Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md)

**Contents**

- [The two modes at a glance](#the-two-modes-at-a-glance)
- [Tuning workflow](#tuning-workflow)
- Screen-space relief:
  1. [Quick emboss](#1--quick-emboss)
  2. [Dramatic raking bas-relief](#2--dramatic-raking-bas-relief)
- Oblique 3D raymarch:
  3. [Full 3D landscape](#3--full-3d-landscape)
  4. [Clean orthographic relief map](#4--clean-orthographic-relief-map)
  5. [Isolated filament sculpture](#5--isolated-filament-sculpture-transparent-cutout)
  6. [Foggy valley with god-rays](#6--foggy-valley-with-god-rays)
  7. [Metallic gold plaque](#7--metallic-gold-plaque)
  8. [Marble bas-relief](#8--marble-bas-relief)
  9. [Neon crystal spikes](#9--neon-crystal-spikes)
  10. [Deep-zoom filament forest](#10--deep-zoom-filament-forest)
  11. [Newton root-map relief](#11--newton-root-map-relief)
  12. [Turntable animation](#12--turntable-animation)
- Mesh export:
  13. [3D-print-ready STL](#13--3d-print-ready-stl)
  14. [Bas-relief OBJ plaque](#14--bas-relief-obj-plaque)
- [Troubleshooting](#troubleshooting)

---

## The two modes at a glance

| | Screen-space relief | Oblique 3D raymarch |
|---|---|---|
| Speed | Fast | Heavier (GPU by default) |
| Framing | Keeps the exact 2D view | Tilted 3D camera + silhouette |
| Perspective | No | Yes (or orthographic) |
| Fog / god-rays | No | Yes (via Lighting & FX) |
| Mesh export | No | Yes |
| Enable with | *Enable raised relief + cast shadows* | *…* + *Oblique 3D raymarch* |

Start with screen-space relief to judge the height; switch on oblique raymarch
when you want a real 3D scene.

---

## Tuning workflow

1. **Pick a smooth theme.** Relief reads best on low-noise palettes — try *Gold
   Relief*, *Marble Relief*, or *Neon Relief*.
2. **Enable relief**, set **Height scale** until the terrain reads (2–3 is a good
   start), keep **Height curve = Log**.
3. **Light it low.** Drop **Light elevation** (screen-space) to ~25–35° for long
   shadows; raise **Shadow strength** to taste.
4. **Go 3D** (optional). Tick **Oblique 3D raymarch**, set **Camera elevation**
   ~35°, tick **Auto lighting defaults**.
5. **Add atmosphere** (optional). Open Lighting & FX, add fog + volume steps
   ([Recipe 6](#6--foggy-valley-with-god-rays)).
6. **Quality pass.** Raise **Anti-alias** to 2–3; turn on **Hi-res height field**
   if the window is small.

---

## 1 · Quick emboss

The 2D image gains raised relief and shadows without changing framing.

| Control | Value |
|---|---|
| Enable raised relief + cast shadows | **on** |
| Oblique 3D raymarch | off |
| Height scale | 2.0 |
| Light azimuth | 135° |
| Light elevation | 45° |
| Shadow strength | 0.5 |
| Relief strength | 0.8 |

**Why:** screen-space relief embosses the height field and drapes cast shadows
over the flat themed image — instant depth, minimal cost, same composition as the
2D view.

---

## 2 · Dramatic raking bas-relief

Long, theatrical shadows carving the filaments — a coin-relief / carved-stone
look.

| Control | Value |
|---|---|
| Enable raised relief + cast shadows | on |
| Height scale | **3.5** |
| Light azimuth | 160° |
| Light elevation | **22°** |
| Shadow strength | **0.8** |
| Relief strength | 1.0 |

**Why:** low light elevation stretches the cast shadows across the surface;
high height scale deepens the carving. Zoom into filament structure first — flat
regions have nothing to catch the light.

**Variations:**
- Rotate the light: sweep **Light azimuth** for different shadow directions.
- Softer stone: drop **Shadow strength** to 0.5, **Height scale** to 2.5.

---

## 3 · Full 3D landscape

The fractal as an orbitable terrain with real perspective and a sky silhouette.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Oblique 3D raymarch | **on** | | Camera azimuth | 20° |
| Camera elevation | **35°** | | Camera FOV | 55° |
| Frame-fill zoom | 1.0 | | Anti-alias | 2 |
| Height curve | Log | | Base ground plane | **on** |
| Auto lighting defaults | **on** | | Edge fade | 0.1 |

**Why:** a mid-low camera elevation gives depth and a silhouette; the ground
plane grounds the terrain so shadows land on a floor; auto lighting seeds AO +
soft shadows + specular. Edge fade keeps structure from streaking off the frame.

**Variations:**
- More drama: **Camera elevation** 20°, **Height scale** up.
- Flatter map: **Camera elevation** 70°.

---

## 4 · Clean orthographic relief map

A precise, distortion-free relief map — engineering/architectural feel.

| Control | Value |
|---|---|
| Oblique 3D raymarch | on |
| Orthographic camera | **on** |
| Camera elevation | 45° |
| Frame-fill zoom | 1.0 |
| Anti-alias | 3 |
| Height curve | Log |
| Base ground plane | on |

**Why:** orthographic projection removes perspective stretch (which otherwise
grows with FOV and frame-fill), so the terrain reads as a clean, measured relief
map. Higher anti-alias keeps the parallel edges crisp.

---

## 5 · Isolated filament sculpture (transparent cutout)

Keep only the fine fractal filaments as a standalone 3D object on transparent
background — ideal for compositing or a cutout export.

| Control | Value | Where |
|---|---|---|
| Oblique 3D raymarch | on | Relief 3D |
| Isolate object | **on** | Relief 3D |
| Drop low-detail (keep filaments) | on | Relief 3D |
| Drop amount | **0.6** | Relief 3D |
| Base ground plane | **off** | Relief 3D |
| Show sky backdrop | **off** | Lighting & FX (Sky) |

**Why:** detail culling removes the flat background and smooth plateaus by height
gradient, leaving the sharp filaments; turning off the sky backdrop and ground
plane leaves a transparent surround the export preserves as a cutout.

**Variations:**
- Cull by color instead: tick **Drop colours**, add the background hex (use
  **Pick** to eyedrop it), raise **Colour tolerance** until it clears.
- Keep more structure: lower **Drop amount** to 0.4.

---

## 6 · Foggy valley with god-rays

Relief terrain wrapped in volumetric fog with light shafts — atmospheric depth.

| Control | Value | Where |
|---|---|---|
| Oblique 3D raymarch | on | Relief 3D |
| Camera elevation | 28° | Relief 3D |
| Base ground plane | on | Relief 3D |
| Auto lighting defaults | on | Relief 3D |
| Fog density | 0.15 | Lighting & FX (Fog/Volumetric) |
| Volume steps | 28 | Lighting & FX |
| Shadow steps | 24 | Lighting & FX (Shadow) |
| Anisotropy | 0.7 | Lighting & FX |
| Height falloff | 1.0 | Lighting & FX |

**Why:** in raymarch mode the terrain is a full member of the 3D shading pipeline,
so all volumetric fog and god-ray controls apply. Height falloff pools the fog in
the valleys; the low camera + forward anisotropy give shafts raking across the
ridges. Every recipe in the [Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md)
works here.

---

## 7 · Metallic gold plaque

A polished gold bas-relief medallion.

| Control | Value | Where |
|---|---|---|
| Theme | **Gold Relief** | Theme combo |
| Oblique 3D raymarch | on | Relief 3D |
| Camera elevation | 40° | Relief 3D |
| Auto lighting defaults | on | Relief 3D |
| Metallic | **1.0** | Lighting & FX (Material) |
| Specular | 1.5 | Lighting & FX |
| Roughness | 0.3 | Lighting & FX |
| IBL strength | 0.5 | Lighting & FX (Sky) |

**Why:** the *Gold Relief* theme colors the height, and metallic + specular +
low roughness give the polished-metal highlight; a touch of IBL adds environment
reflection so the gold looks lit by a room, not a lamp.

**Variations:**
- Brushed gold: **Roughness** 0.6.
- Bronze: pair with a warmer HDRI environment / sky.

---

## 8 · Marble bas-relief

Soft, matte carved-marble panel.

| Control | Value | Where |
|---|---|---|
| Theme | **Marble Relief** | Theme combo |
| Oblique 3D raymarch | on | Relief 3D |
| Camera elevation | 45° | Relief 3D |
| Bicubic height sampling | **on** | Relief 3D |
| Auto lighting defaults | on | Relief 3D |
| Sub-surface | 0.4 | Lighting & FX (Material) |
| Specular | 0.3 | Lighting & FX |

**Why:** bicubic sampling smooths the terrain like polished stone; a little
sub-surface gives marble its soft internal glow; low specular keeps it matte.

---

## 9 · Neon crystal spikes

Sharp, glowing crystalline spires — sci-fi / synthwave.

| Control | Value |
|---|---|
| Theme | **Neon Relief** |
| Oblique 3D raymarch | on |
| Height scale | **4.5** |
| Height curve | **Linear** |
| Camera elevation | 25° |
| Anti-alias | 3 |

**Why:** the **Linear** height curve keeps the raw boundary spikes (instead of
taming them to terrain), so the fractal edge becomes sharp needles; the *Neon
Relief* theme lights them like glowing crystal. Higher anti-alias tames the sharp
edges' aliasing.

**Variations:**
- Add glow: Lighting & FX → Bloom strength ~0.4.
- Tie into fog: [Recipe 6](#6--foggy-valley-with-god-rays) with a colored fog.

---

## 10 · Deep-zoom filament forest

Zoomed deep into filaments, at consistent quality regardless of window size.

| Control | Value |
|---|---|
| Oblique 3D raymarch | on |
| Hi-res height field | **on** |
| Field floor (px) | **1080** |
| Height curve | Log |
| Edge fade | **0.15** |
| Anti-alias | 2 |

**Why:** at small window sizes the display-resolution height field undersamples
the fractal boundary into a spiky "needle-forest". **Hi-res height field**
computes the field at the **Field floor** resolution instead, so any window
matches the maximized look; **Edge fade** stops deep-panned structure from
streaking arms at the frame edge.

> [!NOTE]
> Hi-res field costs an extra escape-time field render below the floor, and has no
> effect at or above it (e.g. maximized or Span). Raise the floor for hero stills.

---

## 11 · Newton root-map relief

Relief on a root-finding fractal, where height is the **iteration count** to
convergence — terraced basins around each root.

| Control | Value |
|---|---|
| Type | Newton (or Nova / Halley) |
| Enable raised relief + cast shadows | on |
| Height scale | 2.5 |
| Height curve | **Sqrt** |
| Light elevation | 30° |
| Relief strength | 0.9 |

**Why:** Newton/Nova/Halley use iteration count as height, so the convergence
basins step down toward each root — Sqrt keeps those terraces readable without
over-flattening. Works in oblique raymarch too for a 3D basin landscape.

**Also applies to:** Buddhabrot (height = orbit density) and Apollonian (height =
synthesised dome per disc) — try the same knobs.

---

## 12 · Turntable animation

Rotate the terrain for a video loop.

| Control | Value |
|---|---|
| Oblique 3D raymarch | on |
| Camera elevation | 35° |
| *(animate)* Camera azimuth | sweep −180 → 180° |

**Why:** animating **Camera azimuth** over a Scene Engine timeline orbits the
camera for a clean turntable. Keep elevation fixed so the horizon stays level.
Pair with a slow light or fog animation for extra life.

---

## 13 · 3D-print-ready STL

Export the terrain as a watertight solid for a slicer.

| Control | Value | Section |
|---|---|---|
| Oblique 3D raymarch | on | — |
| Mesh → Height | **0.25** | Mesh export |
| Mesh → Smoothing | **0.5** | Mesh export |
| Mesh → Detail (grid) | 768 | Mesh export |
| Mesh → File size cap (MB) | 25 | Mesh export |
| Mesh → Underside | 0 (flat back) | Mesh export |

Click **Export mesh (OBJ / STL)…** and choose **STL**.

**Why:** printing wants a moderate relief height and ~0.5 smoothing so dendrites
merge into printable ridges rather than fragile spikes; the file-size cap clamps
the grid so the STL stays sliceable. A flat back sits stable on the bed.

> [!WARNING]
> Very high **Detail (grid)** with low **Smoothing** produces thin, fragile
> spikes that may not print. Keep smoothing ≥ 0.4 for physical prints.

---

## 14 · Bas-relief OBJ plaque

A double-sided decorative panel with a contoured back, colored for rendering.

| Control | Value | Section |
|---|---|---|
| Oblique 3D raymarch | on | — |
| Isolate object | on (optional cutout) | Relief 3D |
| Mesh → Height | 0.18 | Mesh export |
| Mesh → Smoothing | 0.6 | Mesh export |
| Mesh → Detail (grid) | 1024 | Mesh export |
| Mesh → Underside | **0.7** | Mesh export |

Export as **OBJ** (keeps per-vertex color + smooth normals).

**Why:** a low mesh height reads as a clean bas-relief from any angle; the
**Underside** contours the back with the same smoothed relief so the plaque has
depth on both faces; OBJ carries the themed vertex colors into a DCC app.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Terrain looks flat | Camera elevation too high | Lower **Camera elevation** to 25–40° (raymarch), or **Light elevation** (screen-space). |
| No shadows | Shadow strength 0 (screen-space) / no shadow steps (raymarch) | Raise **Shadow strength**, or **Shadow steps** in Lighting & FX. |
| Spiky needle-forest at small window | Display-res field undersampling | Turn on **Hi-res height field**, set **Field floor** ~1080. |
| Boundary is all spikes | Height curve = Linear | Switch to **Log** (or **Sqrt**). |
| Streaky "arms" at frame edges | Structure running off-frame | Raise **Edge fade** (0.1–0.2). |
| Relief barely visible | Height scale too low / busy theme | Raise **Height scale**; use a smooth relief theme. |
| Title shows `[RELIEF GPU OFF]` | GPU relief toggled off | `Ctrl+Shift+G` (or Control Center checkbox) to turn GPU back on. |
| Background won't go transparent | Sky backdrop / ground plane on | Turn **Show sky backdrop** OFF (Lighting & FX) and **Base ground plane** OFF with **Isolate object** on. |
| Mesh spikes won't print | High detail + low smoothing | Raise **Smoothing** to ≥ 0.4; lower **Mesh height**. |
| Slow render | High resolution × Anti-alias² + heavy FX | Tune at Anti-alias 1; raise for the final frame; keep GPU relief on. |

---

## See also

- [Relief 3D Guide](Relief3D-Guide.md) — what every control does + how it works.
- [Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md) — fog + god-rays for [Recipe 6](#6--foggy-valley-with-god-rays).
