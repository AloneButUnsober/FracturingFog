<div align="center">

# Fracturing Fog
### Real-Time High-Precision Mandelbrot Explorer

**Version 0.5.4** &nbsp;·&nbsp; Windows x64 &nbsp;·&nbsp; .NET 10 &nbsp;·&nbsp; Direct3D 11 / 12

*A complete tour of every feature, switch, and slider.*

---

</div>

## Table of Contents

1. [Overview](#1-overview)
2. [Rendering Engine](#2-rendering-engine)
3. [Precision & Deep Zoom](#3-precision--deep-zoom)
4. [Navigation & View Control](#4-navigation--view-control)
5. [Quality Presets](#5-quality-presets)
6. [Color Themes & Palettes](#6-color-themes--palettes)
7. [Color Theme Editor](#7-color-theme-editor)
8. [Regions (Coordinate Bookmarks)](#8-regions-coordinate-bookmarks)
9. [Floating Menu (Control Panel)](#9-floating-menu-control-panel)
10. [Post-Processing](#10-post-processing)
11. [Overlays & Mini Windows](#11-overlays--mini-windows)
12. [Slideshow](#12-slideshow)
13. [Video Zoom](#13-video-zoom)
14. [Screenshots & Posters](#14-screenshots--posters)
15. [Multi-Monitor & Window Modes](#15-multi-monitor--window-modes)
16. [Help System](#16-help-system)
17. [Persistence & File Locations](#17-persistence--file-locations)

---

## 1. Overview

Fracturing Fog is a Windows desktop application for exploring the Mandelbrot set in real time, from a wide view of the entire set all the way down to zooms past **10⁵⁰** — well beyond the resolving power of standard double-precision arithmetic. It combines a hardware-accelerated DirectX renderer, SIMD-vectorized CPU math, extended-precision arithmetic (double-double and quad-double), and perturbation theory with series approximation + bilinear approximation (BLA) to keep frame rates interactive even at extreme depth.

**Key pillars:**

| Pillar | What it gives you |
|---|---|
| Real-time interactivity | Pan, zoom, color-cycle with smooth feedback |
| Extreme zoom depth | Quad-double precision out to ~5 × 10⁵⁸ |
| 200+ color themes | Built-in palettes plus JSON-imported user themes |
| Full theme editor | Live-preview parameter tweaking with save/export |
| Capture suite | PNG, multi-tile poster, MP4 video, PNG sequence |
| Automation | Slideshow + animated zoom slideshow |
| Multi-monitor | Span across all displays, wallpaper-resolution capture |

---

## 2. Rendering Engine

### 2.1 Backends

| Backend | When used |
|---|---|
| **Direct3D 12** | Default when available — feature-level appropriate hardware |
| **Direct3D 11** | Fallback for older GPUs, or forced for testing |

Active renderer is shown in the title bar at startup and on the **Help → Hardware** tab. Vortice.Windows bindings (v3.8.3) provide the managed wrapper.

### 2.2 GPU Texture Streaming

- Calculated frames are uploaded as **BGRA texture data** to the renderer.
- The **previous fully-rendered frame stays visible** while the next one is being computed — no black-flash during deep-zoom (DD/QD) calculations.
- Pan-stop debounce: a 300 ms timer fires a full-quality render after a drag ends, so mid-drag frames can be fast/low-quality without sacrificing final image fidelity.

### 2.3 CPU Calculator

- **SIMD-vectorized** Mandelbrot iteration (`System.Numerics.Vector<double>`).
- Multi-threaded across logical CPU cores.
- **Early-exit for in-set pixels** when neighborhood evidence indicates the point will never escape — measurable speedup in cardioid- and bulb-heavy views.
- Cancellation-token aware: a new pan/zoom request cancels the in-flight calculation cleanly.

### 2.4 Perturbation + Series Approximation + BLA

For zooms past where naïve per-pixel iteration breaks down:

- **Reference orbit** is iterated once in high precision (DD or QD).
- **Per-pixel deltas** are then iterated in cheap doubles relative to the reference.
- **Series approximation** skips early iterations entirely using a Taylor-style polynomial of the reference orbit.
- **Bilinear Approximation (BLA)** jumps over thousands of inner iterations at a time inside regions where the orbit derivative is well-behaved.

Result: deep zooms that would take minutes per frame become interactive.

---

## 3. Precision & Deep Zoom

Fracturing Fog automatically promotes the arithmetic precision based on the active zoom level:

| Precision | Decimal digits | Effective zoom ceiling |
|---|---:|---:|
| **Double (SP)** | ~15 | ~10¹³ |
| **Double-Double (DD)** | ~31 | ~10²⁵ |
| **Quad-Double (QD)** | ~62 | ~10⁵⁸ |

- **Auto-promotion** crosses each threshold transparently — no user action required.
- The high-precision threshold (when DD engages) is set conservatively at zoom **10¹²**, leaving 1–2 guard digits before pixel-grid degradation would be visible.
- Pan/zoom coordinate math itself promotes from `double` → `DD` → `QD` at zoom > 10²⁵, so cursor anchoring stays accurate even at extreme depth.
- The status footer shows the active precision (`SP`, `DD`, or `QD`) so you always know which arithmetic mode is in play.

---

## 4. Navigation & View Control

### 4.1 Mouse

| Input | Action |
|---|---|
| **Mouse wheel** | Zoom in / out anchored at the cursor |
| **Left-click + drag** | Pan the view |
| **Double-click** | Center on point and zoom in one step |
| **Right-click** | Context menu (toolbar visibility, mini-map toggle, etc.) |

### 4.2 Keyboard

| Key | Action |
|---|---|
| **R** | Reset view to default (-0.5, 0, zoom 0.3) |
| **Esc** | Close floating dialogs |

### 4.3 Direct Coordinate Entry

The Floating Menu exposes:

- **CX / CY** — real and imaginary components of the view center. Accepts the **pipe-separated DD/QD limb format** for high-precision paste-back (e.g. `-0.7548...|1.2e-17|...`).
- **Zoom** — scalar zoom factor. Accepts scientific notation up to ~1e58 (`1e48`, `2.5e30`, etc.).
- **Iter** — maximum escape iteration count. Minimum 64. No upper cap.
- **Go** — apply typed values.
- **Flip Y** — mirror the view vertically (negate CY) for symmetry experiments.
- **Lock** — pin iteration count across all subsequent pan/zoom operations so deep regions don't black-out on auto-recompute.

---

## 5. Quality Presets

Five tiers control zoom ceiling, iteration scaling, wheel step size, and precision behavior.

| Tier | Zoom ceiling | Iter range | Wheel step | Precision |
|---|---:|---|---:|---|
| **Draft** | 1 × 10⁵ | 64 – 256 | ×1.40 | SP only |
| **Standard** | 1 × 10¹³ | 256 – 2 048 | ×1.20 | SP → DD @ 10¹² |
| **High** | 1 × 10²² | 512 – 16 384 | ×1.12 | SP → DD @ 10¹² |
| **Ultra** | 5 × 10²⁷ | 1 024 – 65 536 | ×1.08 | SP → DD @ 10¹² |
| **Extreme** | 5 × 10⁵⁸ | 2 048 – 131 072 | ×1.06 | SP → DD → QD |

Iteration count auto-scales with depth: `IterBase + ⌊log₁₀(zoom) × IterPerDecade⌋`, clamped to `[IterBase, IterMax]`.

---

## 6. Color Themes & Palettes

Fracturing Fog ships with **200+ built-in color palettes** organized into categories, plus unlimited JSON-imported user themes.

### 6.1 Categories

| Category | Examples |
|---|---|
| **3D Relief (normal-mapped)** | Phong Stone, Molten Metal, Crystal Cave, Gold Relief, Marble, Volcanic Rock, Lunar Surface, Ancient Bronze, Neon |
| **Algorithmic Phong 3D** | Bernstein, Copper Sheen, Digital Matrix, Fire, Golden Ratio, Pastelly, Psychedelic, Solar Wind, Twilight Cyclic, Vintage Sepia |
| **PBR (physically-based)** | Cesium Spectrum (Standard / Realistic / Ultra-Glow), Radio Interference, Golden Ratio Phi |
| **Escape Time** | Smooth-iter palettes — classic gradients |
| **Distance Estimation** | Reveal fine filaments and dendrites |
| **Orbit Traps & Orbit Trap Images** | Geometric or image-mapped traps |
| **Argument / Binary Decomposition** | Phase-angle-based coloring |
| **Domain Coloring** | Complex-plane phase + magnitude |
| **Field Lines** | Equipotential contour visualization |
| **Histograms** | Iteration-histogram normalized palettes |
| **Stripe Average (TIA)** | Continuous stripe averaging |
| **Potential** | Logarithmic-potential gradients |
| **Lemniscate** | Equipotential ring coloring |
| **Derivative Bailout** | Detail-enhanced bailout |
| **Chromostereopsis 3D** | Depth-via-color illusion |
| **Post-Process** | Painterly / film-grain effects |
| **JSON Imported** | User-shareable theme files |
| **Interior** | In-set coloring (cardioid/bulbs) |

### 6.2 Theme Management

| Button | Action |
|---|---|
| **Export** | Save the active theme to a standalone JSON file |
| **Import** | Load a theme JSON into your library |
| **Delete** | Remove a user-imported theme (built-ins are protected) |
| **Reload** | Re-scan disk for edited theme JSON files |
| **Edit Theme…** | Open the full **Color Theme Editor** (see § 7) |

---

## 7. Color Theme Editor

A dedicated two-column floating editor for creating and tweaking themes with **live preview** into the main render window.

### 7.1 Layout

| Left column | Right column |
|---|---|
| Target (region + base theme picker) | 3D Lighting (Phong / PBR shared params) |
| Identity (name, category, description, max zoom) | Phong 3D extras (key/fill spec, fill diff) |
| Kind (Gradient / Cycling / Phong3D / Pbr3D) | PBR 3D extras (mode, glow exp/scale, material bands) |
| Stops (color-stop list editor) | |
| Cycle (cycling-speed numeric) | |
| In-Set color override | |
| Actions (Save, Save As, Export, Cancel) | |

### 7.2 Theme Kinds

| Kind | Description |
|---|---|
| **Gradient** | Multi-stop interpolated color ramp |
| **Cycling** | Periodically repeating gradient with adjustable cycle speed |
| **Phong3D** | Diffuse + specular shading with key/fill lights and steepness/ambient controls |
| **Pbr3D** | Physically-based shading with material bands (metallic, roughness, glow) |

### 7.3 Editing Mechanics

- **Color Stop List Control** — add, remove, reorder, recolor stops. Drag to reposition along the gradient.
- **Light Source Controls** — separate widgets for key light and fill light: direction, intensity, color.
- **Material Band List** (PBR) — define per-iteration-band metallic/roughness/glow profiles.
- **In-Set Override** — checkbox + RGB picker for points that never escape; otherwise inherits from the gradient's tail.
- **Live preview** — every parameter change pipes a transient `IColorMap` to the main view immediately; closing the editor restores the committed theme.
- **Region jump** — pick a region from the target dropdown to navigate without leaving the editor.
- **Save to library** persists the theme to `%APPDATA%\FracturingFog\` and rebuilds the theme combo.

---

## 8. Regions (Coordinate Bookmarks)

Named coordinate bookmarks that capture a complete view: center (with DD/QD limb fidelity), zoom, iteration count, and optionally a preferred color theme.

### 8.1 Built-In Regions

A curated tour of classic Mandelbrot locations (cardioid valley, mini-brots, seahorse valley, elephant valley, double-spirals, deep-zoom showpieces). Built-in regions are **read-only** — they can be re-applied but not deleted.

### 8.2 User Regions

| Action | Description |
|---|---|
| **Save View** | Capture the current center/zoom/iter as a new named region |
| **Delete** | Remove a user region (built-ins are protected) |
| **Export** | Write the entire user library to a JSON file for sharing |
| **Import** | Merge a region JSON file into your library |

Stored at `%APPDATA%\FracturingFog\regions.json` with full DD precision (low-word + extra limbs) so paste-back at zoom > 10¹⁵ is bit-exact.

### 8.3 Slideshow Region Filter

A checkbox in the Floating Menu controls whether slideshow region cycling includes "extreme" (very deep-zoom) regions. Useful when you want a calmer rotation that stays at shallower zooms.

---

## 9. Floating Menu (Control Panel)

The detachable, borderless control window that hosts every parameter exposed by the renderer.

### 9.1 Sections

- **Top Buttons** — Reset, Span, Image, Poster, Slideshow, Video, Menu, Close.
- **Form Resolution** — combo to resize the main render area.
- **Coordinates** — CX, CY, Zoom, Iter, Lock-Iter, Go, Flip.
- **Quality** — preset combo (Draft / Standard / High / Ultra / Extreme).
- **Region** — combo + Save/Delete/Export/Import + "include extreme regions" checkbox.
- **Theme** — combo + Edit/Export/Import/Delete/Reload.
- **Post-Processing** — Brightness, Contrast, Adaptive (histogram eq) sliders.
- **Video TAA Tuning** — live sliders for temporal-blend alpha and deep-zoom fade start/end (active during video zoom rendering).
- **Overlays** — Show Coord Panel, Show Footer, Show Grid checkboxes.

### 9.2 Interaction

- **Borderless dark window**, drag by the title bar to reposition.
- **TopMost** — stays above the main window for one-glance access.
- **Esc** closes; reopens via the Menu button on the main toolbar.

---

## 10. Post-Processing

Three real-time post-process sliders, all applied on the CPU before the buffer is uploaded to the GPU:

| Control | Range | Behavior |
|---|---:|---|
| **Brightness** | −100 … +100 | Additive offset; 0 is neutral |
| **Contrast** | −100 … +100 | Multiplicative gain; 0 is neutral (1.0×) |
| **Adaptive (Histogram Eq)** | 0 … 100 | Histogram equalization strength — pulls hidden detail out of flat areas, off at 0, full at 100 |

Adaptive contrast is particularly powerful for revealing fine filament structure in deep-zoom shots where iteration counts cluster in a narrow band.

---

## 11. Overlays & Mini Windows

### 11.1 Grid Overlay

Cartesian complex-plane grid with major/minor divisions and labeled coordinates. Renders as a transparent sibling panel over the fractal — does not interfere with the GPU upload pipeline.

### 11.2 Mini-Map

Inset panel showing the **whole Mandelbrot set** with a marker for your current view position. Click anywhere on the mini-map to jump there.

### 11.3 Mini Depth Indicator

Per-pixel iteration-depth heat-map miniature — visualizes the "iteration cost" landscape of the current view at a glance.

### 11.4 Status Footer

Bottom bar showing live values: center coordinates, zoom, iteration count, active precision (SP/DD/QD), render time, and current operation status.

---

## 12. Slideshow

Click **Slideshow** to start an automatic guided tour:

- **Region cycle:** every 30 seconds, advance to the next region.
- **Theme cycle:** every 10 seconds within a region, change the color theme.
- **Cross-fade:** 2-second CPU-blended cross-fade between both theme changes and region transitions (per-pixel lerp between the outgoing and incoming color buffers, ~20 frames over 100 ms).
- **Watermark:** region name and theme name are rendered onto the live frame during the show.

### 12.1 Modifiers

| Modifier | Effect |
|---|---|
| **Shift+click Slideshow** | Lock the current region — only the theme cycles |
| **Slideshow Focus button** | Slow, focused viewing of the current region |
| **Skip Region** | Cancel the current region's timer and advance immediately |
| **Include Extreme Regions** checkbox | Toggle whether very-deep-zoom regions are included |

Click the **Stop** button (the toolbar button changes label and color while running) to end the slideshow at any time.

---

## 13. Video Zoom

Smoothly animated zoom from the current view to a chosen target, with optional recording.

### 13.1 Motion

Two-phase animation:

1. **Pan phase** (first 5% of duration): pan to the target CX/CY at the current zoom — avoids the "zoom-and-drift" feel where the target slides off-screen.
2. **Zoom phase** (remaining 95%): log-Zoom interpolation to the target depth with the center fixed.

Both phases use **smoothstep easing** for soft start/stop.

### 13.2 Frame Rendering

Every frame triggers a full background `Calculate()` — frame rate is **calculation-bound, not wall-clock-bound**. The loop advances by elapsed wall-clock time so total duration is honored even if individual frames are slow.

### 13.3 Recording Options

| Format | Description |
|---|---|
| **MP4 (ffmpeg)** | Real-time encoding to a temp file; you choose the output path after the zoom completes |
| **PNG Sequence** | Lossless frame-by-frame PNG dump — ideal for offline encoding at higher bitrates |
| **None** | Live playback only |

MP4 and PNG can record simultaneously.

### 13.4 Video Slideshow

A continuous loop variant: zoom in → pause → zoom out → next region → repeat. Each leg is 30 s by default with a 7 s pause between videos. Stops independently from the single-shot Video feature.

### 13.5 Live TAA Tuning

While a video zoom is running, three sliders in the Floating Menu let you live-tune the temporal anti-aliasing:

- **TAA Alpha** — temporal blend strength between successive frames.
- **Fade Start** — zoom at which the deep-zoom artifact fade begins.
- **Fade End** — zoom at which the fade reaches full strength.

### 13.6 Per-Region Iteration Override

Regions can carry a stored iteration target; video zoom raises `MaxIterations` to at least that value during the leg so deep targets don't render as in-set black just because the quality preset's iter formula would produce a smaller value.

---

## 14. Screenshots & Posters

### 14.1 Screenshot

- Saves the current view as **PNG / TIFF / BMP**.
- Automatically applies the live brightness/contrast/adaptive post-processing to the saved image.
- Generates a descriptive filename: `FracturingFog_Theme_Region_x...y...z...i..._WxH.png`.
- Adds an unobtrusive **watermark** with region + theme name and a contrast-aware text color picked from the underlying pixels.
- When **Span mode** is active, the screenshot covers the entire virtual desktop (wallpaper resolution).

### 14.2 Poster

Multi-tile composite render at print resolution — much larger than the panel can display directly. Each tile is calculated separately and stitched into one large image, suitable for printing or wallpaper.

---

## 15. Multi-Monitor & Window Modes

| Mode | Behavior |
|---|---|
| **Span** | Stretch the window across the entire virtual desktop (all monitors); toolbar/footer toggleable |
| **Full Screen** | Borderless single-monitor full-screen |
| **Mini Mode** | Shrink to minimum size, borderless, top-most — a desktop companion view |
| **On-Top** | Keep main window above all others |

Span mode is the foundation for wallpaper-resolution captures and for showing the slideshow on a multi-monitor setup.

---

## 16. Help System

Press the Help button (or the corresponding floating-menu entry) to open the **Floating Help** window — a borderless dark dialog with five tabs:

| Tab | Contents |
|---|---|
| **About** | Version, platform, runtime, renderer, credits, Wikipedia/dxdiag links |
| **Hardware** | Live system info: GPU adapters (DXGI enumeration), D3D11 feature level, displays, CPU, memory, fractal calculator state. Refreshable on demand |
| **Features** | Quick-reference summary of navigation, toolbar, panels, themes, captures, precision |
| **Mathematics** | Mandelbrot set definition, historical timeline (Fatou/Julia → Brooks-Matelski → Mandelbrot → Douady-Hubbard → Shishikura → Martin), properties, escape-time algorithm, why deep zoom is hard |
| **Mandelbrot** | Benoit Mandelbrot biography with external links (Wikipedia, MacTutor, TED, IBM Research) |

---

## 17. Persistence & File Locations

All user-modifiable data lives under your AppData folder so updates to the program never overwrite your work.

| File | Purpose |
|---|---|
| `%APPDATA%\FracturingFog\regions.json` | User-defined coordinate bookmarks |
| `%APPDATA%\FracturingFog\colorthemes.json` | User-imported / authored color themes |
| `Resources\*.bmp`, `*.ico` | Built-in icons and toolbar images |

Both region and theme JSON files are **human-readable** (indented `System.Text.Json` output) — no third-party serializer dependency, easy to diff and share.

---

<div align="center">

### Credits

UI & Engine · **Bradley Brown**
Renderer · [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT)
Video Encoding · [ffmpeg](https://ffmpeg.org) (LGPL build)

*Fracturing Fog · Real-time high-precision Mandelbrot exploration · © 2026*

</div>
