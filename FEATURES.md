<div align="center">

# Fracturing Fog
### Real-Time High-Precision Mandelbrot Explorer

**Version 0.6.x** &nbsp;·&nbsp; Windows x64 &nbsp;·&nbsp; .NET 10 &nbsp;·&nbsp; Avalonia 12 &nbsp;·&nbsp; Direct3D 11 / 12

*A complete tour of every feature, switch, and slider in the Avalonia shell.*

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
9. [Toolbar](#9-toolbar)
10. [Floating Menu](#10-floating-menu)
11. [Post-Processing + Adaptive Sweep](#11-post-processing--adaptive-sweep)
12. [Overlays & Mini Windows](#12-overlays--mini-windows)
13. [Slideshow](#13-slideshow)
14. [Video Zoom](#14-video-zoom)
15. [Screenshots & Posters](#15-screenshots--posters)
16. [Authoring (User Equation, Sandbox, User Bulb)](#16-authoring-user-equation-sandbox-user-bulb)
17. [Audio-Reactive Engine](#17-audio-reactive-engine)
18. [Multi-Monitor & Window Modes](#18-multi-monitor--window-modes)
19. [Client / Server](#19-client--server)
20. [Help System](#20-help-system)
21. [Persistence & File Locations](#21-persistence--file-locations)

---

## 1. Overview

Fracturing Fog is a Windows desktop application for exploring the Mandelbrot set and 20+ other fractal families in real time, from a wide view of the entire set all the way down to zooms past **10⁵⁸** — well beyond the resolving power of standard double-precision arithmetic. It combines a hardware-accelerated DirectX renderer, SIMD-vectorized CPU math, extended-precision arithmetic (double-double and quad-double), perturbation theory with series approximation + bilinear approximation (BLA), and a Roslyn-compiled user-equation engine + an algorithmic color-palette DSL.

The shell is **Avalonia 12** — pure MVVM, cross-platform-ready (Windows ships first; macOS / Linux follow Skia / Metal / Vulkan back-ends).

**Key pillars:**

| Pillar | What it gives you |
|---|---|
| Real-time interactivity | Pan, zoom, color-cycle with smooth feedback |
| Extreme zoom depth | Quad-double precision out to ~5 × 10⁵⁸ |
| 20+ fractal families | Mandelbrot, Julia, Burning Ship, Tricorn, Multibrot, Phoenix, Newton, Buddhabrot, IFS, L-System, Strange Attractor, Mandelbulb (3D), User Equation, Sandbox, User Bulb (3D), Tear Drop, + CalcGen Generated family (Z² / Z³ / Z⁴ / Z⁵ / Tricorn / Burning Ship) |
| 200+ color themes | Built-in palettes plus JSON-imported user themes plus algorithmic ColorGen DSL |
| Full theme editor | Live-preview parameter tweaking with save/export + From-Image kmeans extractor |
| Capture suite | PNG, multi-tile poster, MP4 video (built-in + ffmpeg lossless), PNG sequence |
| Automation | Slideshow + animated zoom slideshow + audio-reactive mode |
| Multi-monitor | Span across all displays, wallpaper-resolution capture |
| Remote rendering | Mutual-TLS render server + sealed-vault client dialog |

---

## 2. Rendering Engine

### 2.1 Backends

| Backend | When used |
|---|---|
| **Direct3D 12** | Default when available — feature-level appropriate hardware |
| **Direct3D 11** | Fallback for older GPUs, or forced for testing |

Active renderer is shown in the title bar at startup and on the **Help → Hardware** tab. Vortice.Windows bindings (v3.8.3+) provide the managed wrapper.

The swap chain is hosted inside an Avalonia `NativeControlHost` — XAML cannot overlay it on Windows, so the toolbar / status bar / VCR transport live in their own layout bands, mini-map + floating menu + help live in their own top-level windows, and the grid + watermark are CPU-composited into the BGRA buffer before swap-chain upload.

### 2.2 GPU Texture Streaming

- Calculated frames are uploaded as **BGRA texture data** to the renderer.
- The **previous fully-rendered frame stays visible** while the next one is being computed — no black-flash during deep-zoom (DD/QD) calculations.
- Pan-stop debounce: a 300 ms timer fires a full-quality render after a drag ends, so mid-drag frames can be fast/low-quality without sacrificing final image fidelity.

### 2.3 CPU Calculator

- **SIMD-vectorized** Mandelbrot iteration (`System.Numerics.Vector<double>` + AVX2+FMA where supported).
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

### 2.5 GPU ILGPU Path (generated calculators + User Bulb 3D)

CalcGen-emitted calculators (Mandelbrot Z² / Z³ / Z⁴ / Z⁵ / Tricorn / Burning Ship) ship with a lazy-init ILGPU kernel. User Bulb 3D ships a pre-baked triplex spherical power-N GPU kernel for `Vec3.Pow(z, N) + c` bodies; anything else silently falls back to CPU.

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
- The status footer shows the active precision (`SP`, `DD`, or `QD`) plus the active execution path (`AVX2`, `PT`, `BLA`, `QD-PT`) so you always know which arithmetic mode is in play.

---

## 4. Navigation & View Control

### 4.1 Mouse

| Input | Action |
|---|---|
| **Mouse wheel** | Zoom in / out anchored at the cursor |
| **Left-click + drag** | Pan the view (fast pass mid-drag; full re-render 300 ms after release) |
| **Double-click** | Center on point and zoom in one step |
| **Right-click + drag** | **Highlight-to-zoom** — marquee box; release centers + zooms to fill rectangle (new in v0.6) |
| **Right-click + drag (3D)** | Orbit camera (X = theta, Y = phi, inverted for natural ""tilt up"" feel) |

### 4.2 Keyboard

| Key | Action |
|---|---|
| **M** | Toggle Floating Menu |
| **T** | Toggle Color Theme Editor |
| **R** | Reset view to default for the active fractal |
| **V** | Save current view as a region |
| **Esc** | Exit Span mode / stop slideshow / stop video / close sub-dialog |
| **W / S** | Zoom in / out (2D) or camera closer / farther (3D) |
| **A / D / Q / E** | Pan |
| **Shift + pan key** | Quarter-step nudge |
| **Arrows (3D)** | Orbit camera |
| **PgUp / PgDn / Home / End** | Rotate light azimuth / elevation (3D) |

Keyboard pan / zoom / camera keys are **ignored** while any text box has keyboard focus. Clicking the render surface restores focus — including after a toolbar click (v0.6.2 fix).

See [Docs/Keyboard-Shortcuts.md](Docs/Keyboard-Shortcuts.md) for the complete table.

### 4.3 Direct Coordinate Entry

The Floating Menu exposes:

- **CX / CY** — real and imaginary components of the view center. Accepts the **pipe-separated DD/QD limb format** for high-precision paste-back (e.g. `-0.7548...|1.2e-17|...`).
- **Zoom** — scalar zoom factor. Accepts scientific notation up to ~1e58 (`1e48`, `2.5e30`, etc.).
- **Iter** — maximum escape iteration count. Minimum 64. No upper cap.
- **Go** — apply typed values.
- **Flip Y** — mirror the view vertically (negate every CY limb) for symmetry experiments.
- **Copy** — copy CX / CY / Zoom / Iter to the system clipboard (limb format preserved for CX/CY).
- **Lock Iterations** — pin iteration count across all subsequent pan/zoom operations so deep regions don't black-out on auto-recompute.

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

Fracturing Fog ships with **200+ built-in color palettes** organized into categories, plus unlimited JSON-imported user themes and the ColorGen DSL for algorithmic theme authoring.

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
| **ColorGen** | Algorithmic DSL output |
| **JSON Imported** | User-shareable theme files |
| **Interior** | In-set coloring (cardioid/bulbs) |

### 6.2 Theme Management

| Button | Action |
|---|---|
| **Exp…** | Save the active theme to a standalone JSON file |
| **Imp…** | Load a theme JSON into your library |
| **Delete** | Remove a user-imported theme (built-ins are protected) |
| **Reload** | Re-scan disk for edited theme JSON files |
| **Edit Theme…** | Open the full **Color Theme Editor** (see § 7) |

Right-click the Theme combo (toolbar or menu) for the sort menu: Default / All A–Z / per-kind filter (Cycling / Phong3D / Pbr3D / …).

---

## 7. Color Theme Editor

A modeless two-column floating editor for creating and tweaking themes with **live preview** into the main render window. Hotkey `T`.

### 7.1 Layout

| Left column | Right column |
|---|---|
| Target (region + base theme picker) | 3D Lighting (Phong / PBR shared params) |
| Identity (name, category, description, max zoom) | Phong 3D extras (key/fill spec, fill diff) |
| Kind (Gradient / Cycling / Phong3D / Pbr3D) | PBR 3D extras (mode, glow exp/scale, material bands) |
| Stops (color-stop list editor) | |
| Cycle (cycling-speed numeric) | |
| In-Set color override | |
| Post-FX Defaults | |
| Actions (Save / Save As / Export / Save C# / From Image…) | |

### 7.2 Theme Kinds

| Kind | Description |
|---|---|
| **Gradient** | Multi-stop interpolated color ramp |
| **Cycling** | Periodically repeating gradient with adjustable cycle speed |
| **Phong3D** | Diffuse + specular shading with key/fill lights and steepness/ambient controls |
| **Pbr3D** | Physically-based shading with material bands (metallic, roughness, glow) |

### 7.3 Editing Mechanics

- **Color Stop List Control** — add, remove, reorder, recolor stops. Drag to reposition along the gradient.
- **Light Source Controls** — separate widgets for key light and fill light: direction, intensity, color, specular, shininess. Optional Rim light.
- **Material Band List** (PBR) — define per-iteration-band metallic/roughness/glow profiles.
- **In-Set Override** — checkbox + RGB picker for points that never escape; otherwise inherits from the gradient's tail.
- **Live preview** — every parameter change pipes a transient `IColorMap` to the main view immediately; closing without Save restores the committed theme.
- **Region jump** — pick a region from the target dropdown to navigate without leaving the editor.
- **Save to library** persists the theme to `%APPDATA%\FracturingFog\colorthemes.json` and rebuilds the theme combo. **An overwrite confirmation prompt appears if the typed name matches an existing user theme** (v0.6.2+).
- **From Image…** — extract a 5-stop palette from any PNG / JPG via k-means in CIELAB.

See [Docs/ColorThemeEditor-Guide.md](Docs/ColorThemeEditor-Guide.md) for the full walkthrough + 20 worked examples.

---

## 8. Regions (Coordinate Bookmarks)

Named coordinate bookmarks that capture a complete view: center (with DD/QD limb fidelity), zoom, iteration count, fractal type, and optionally a preferred color theme + bound saved-equation name.

### 8.1 Built-In Regions

A curated tour of classic Mandelbrot locations (cardioid valley, mini-brots, seahorse valley, elephant valley, double-spirals, deep-zoom showpieces). Built-in regions are **read-only** — they can be re-applied but not deleted.

### 8.2 User Regions

| Action | Description |
|---|---|
| **Save View** (`V`) | Capture the current center/zoom/iter as a new named region. Prompts to confirm overwrite if the name exists. |
| **Delete** | Remove a user region (built-ins are protected) |
| **Exp…** | Write the entire user library to a JSON file for sharing |
| **Imp…** | Merge a region JSON file into your library (per-collision: Skip / Overwrite / Rename) |

Stored at `%APPDATA%\FracturingFog\regions.json` with full DD precision (low-word + extra limbs) so paste-back at zoom > 10¹⁵ is bit-exact.

### 8.3 Slideshow Region Filter

A checkbox in Slideshow Settings controls whether slideshow region cycling includes ""extreme"" (very deep-zoom) regions. Useful when you want a calmer rotation that stays at shallower zooms.

### 8.4 Sort + Filter

Right-click the Region combo (toolbar or menu) for the sort menu: Default / By Fractal Type → \<type\>.

See [Docs/Regions-Guide.md](Docs/Regions-Guide.md) for the full JSON schema and tips.

---

## 9. Toolbar

The top toolbar of the Avalonia MainWindow surfaces the most-used controls:

| Control | Purpose |
|---|---|
| **Type combo** | Active fractal family. 17 built-ins + `— Registered —` divider + promoted user equations |
| **Quality combo** | Draft / Standard / High / Ultra / Extreme |
| **Region combo** | Built-in tour + user regions. Right-click for sort menu |
| **Theme combo** | Active color map. Right-click for sort menu |
| **Grid** toggle | Cartesian complex-plane overlay |
| **Watermark** toggle | Region + theme + program watermark embedded in BGRA buffer |
| **Params** | Per-fractal parameters dialog |
| **Reset** | Restore default view |
| **Edit Theme** | Open Color Theme Editor (modeless) |
| **Menu** | Toggle Floating Menu |
| **Help** | Open Help window |

See [Docs/Avalonia-UserGuide.md](Docs/Avalonia-UserGuide.md) for screen captures and a step-through.

---

## 10. Floating Menu

The detachable, borderless control window that hosts every parameter not on the toolbar. Open with `M` or the toolbar **Menu** button.

### 10.1 Sections

- **View row 1** — Reset / Span / Image / Poster.
- **View row 2** — Slideshow / Video (toggle) / Close Program.
- **Toggles** — Status / Grid + Resolution combo.
- **Region Navigation** — Region combo + Save / Delete / Exp… / Imp… + CX / CY / Quality / Zoom / Iter textboxes + Lock Iterations + Go / Flip Y / Copy.
- **Color Themes** — Theme combo + Exp… / Imp… / Delete / Reload + Edit Theme.
- **Post-FX** — Brightness / Contrast / Adaptive sliders + per-slider Lock + Sweep button + sweep-duration NumericUpDown.
- **Slideshow** — Slideshow Settings…
- **Remote** — Server… / Client…

### 10.2 Interaction

- Borderless dark window, drag by the title bar.
- Top-most over the MainWindow for one-glance access.
- Span / Video / Adaptive-Sweep button labels flip while their modes are active (`Back` / `Stop` / `Stop Sweep`).
- Esc closes; the toolbar **Menu** button reopens.

---

## 11. Post-Processing + Adaptive Sweep

Three real-time post-process sliders, all applied on the CPU before the buffer is uploaded to the GPU:

| Control | Range | Behavior |
|---|---:|---|
| **Brightness** | −100 … +100 | Additive offset; 0 is neutral |
| **Contrast** | −100 … +100 | Multiplicative gain; 0 is neutral (1.0×) |
| **Adaptive (Histogram Eq)** | 0 … 100 | Histogram equalization strength — pulls hidden detail out of flat areas |

### 11.1 Lock Checkboxes

Each slider has a Lock checkbox. When ticked, theme switches do not overwrite the current value — useful for keeping a global brightness preference across theme browsing.

### 11.2 Adaptive Sweep (new)

The Sweep button animates Adaptive 0 → 100 over the configured duration with a sine ease-in/out. Re-press to cancel mid-sweep.

| Field | Range | Default |
|---|---:|---:|
| Duration (s) | 0.25 – 600 | 5.0 |

Use to demo the effect, find the sweet spot for a region, or generate a slow-build dramatic reveal in a video recording.

Adaptive contrast is particularly powerful for revealing fine filament structure in deep-zoom shots where iteration counts cluster in a narrow band.

---

## 12. Overlays & Mini Windows

### 12.1 Grid Overlay

Cartesian complex-plane grid with major/minor divisions and labeled coordinates. CPU-composited into the BGRA buffer so it survives screenshots.

### 12.2 Watermark

Region + theme + program label, CPU-composited into the BGRA buffer. Position / opacity / color configurable from Slideshow Settings → Watermark. Contrast-aware text color picks white on dark, near-black on light.

### 12.3 Mini-Map

Inset top-level window showing the **whole Mandelbrot set** with a marker for your current view position. Click anywhere on the mini-map to jump there.

### 12.4 Mini Depth Indicator

Per-pixel iteration-depth heat-map miniature — visualizes the ""iteration cost"" landscape of the current view at a glance.

### 12.5 Status Footer

Bottom bar showing live values: center coordinates, zoom, iteration count, active precision (SP/DD/QD), render time, current operation status, and a ● Server indicator (green = local render server up, grey = down, red = error).

---

## 13. Slideshow

Click **Slideshow** to start an automatic guided tour:

- **Region cycle:** every 30 seconds, advance to the next region.
- **Theme cycle:** every 10 seconds within a region, change the color theme.
- **Cross-fade:** ~3 s blend between transitions (~0.75 × beat in audio-reactive mode).
- **Watermark:** region name and theme name embedded into the live frame.
- **VCR transport bar:** ◀◀ ◀ ▮▮ ▶ ▶▶ row at the bottom of MainWindow during the slideshow.

### 13.1 Modifiers

| Modifier | Effect |
|---|---|
| **Shift+click Slideshow** | Lock the current region — only the theme cycles |
| **Skip with VCR** | ▶▶ / ◀◀ advance / rewind by region; ▶ / ◀ by theme |
| **Esc** | Stop the slideshow |
| **Include Extreme Regions** checkbox in Settings | Toggle whether very-deep-zoom regions are included |
| **Audio-reactive** | Replace fixed timers with beat-driven transitions — see § 17 |

The Slideshow button label flips to **Stop** while running.

See [Docs/Slideshow-AudioReactive-Guide.md](Docs/Slideshow-AudioReactive-Guide.md).

---

## 14. Video Zoom

Smoothly animated zoom from the current view to a chosen target, with optional recording.

### 14.1 Motion

Two-phase animation:

1. **Pan phase** (first 5% of duration): pan to the target CX/CY at the current zoom.
2. **Zoom phase** (remaining 95%): log-Zoom interpolation to the target depth with the center fixed.

Both phases use **smoothstep easing**.

### 14.2 Frame Rendering

Every frame triggers a full background `Calculate()` — frame rate is **calculation-bound, not wall-clock-bound**. The loop advances by elapsed wall-clock time so total duration is honored.

### 14.3 Recording Options

| Format | Description |
|---|---|
| **None** | Live playback only |
| **MP4 (built-in)** | Media Foundation H.264 — no external deps |
| **Lossless H.264** | libx264 -qp 0, yuv444p — needs ffmpeg |
| **Lossless FFV1** | FFV1 v3 in MKV — needs ffmpeg |
| **H.264 HQ** | libx264 -crf 18 — needs ffmpeg |
| **PNG Sequence** | Lossless per-frame dump (any mode, simultaneous with video) |

ffmpeg.exe discovery: app folder, `<install>\Tools\`, `<install>\Resources\`, PATH.

### 14.4 Video Slideshow

A continuous loop variant: zoom in → pause → zoom out → next region → repeat. Each leg is 30 s by default with a 7 s pause between videos. Stops independently from the single-shot Video feature.

### 14.5 Live TAA Tuning

While a video zoom is running, three sliders in the Floating Menu let you live-tune the temporal anti-aliasing:

- **TAA Alpha** — temporal blend strength between successive frames.
- **Fade Start** — zoom at which the deep-zoom artifact fade begins.
- **Fade End** — zoom at which the fade reaches full strength.

### 14.6 Per-Region Iteration Override

Regions can carry a stored iteration target; video zoom raises `MaxIterations` to at least that value during the leg so deep targets don't render as in-set black just because the quality preset's iter formula would produce a smaller value.

---

## 15. Screenshots & Posters

### 15.1 Screenshot (Image button)

- Saves the current view as **PNG / TIFF / BMP**.
- Automatically applies the live brightness/contrast/adaptive post-processing.
- Generates a descriptive filename: `FracturingFog_Theme_Region_x...y...z...i..._WxH.png`.
- Embeds the watermark if the Watermark toggle is on.
- When **Span mode** is active, covers the entire virtual desktop (wallpaper resolution).

### 15.2 Poster

Multi-tile composite render at print resolution. Each tile is calculated separately and stitched into one large image.

| Field | Range | Default |
|---|---|---|
| Width × Height | up to 32768 × 32768 | 7680 × 4320 (8K) |
| Tile size | 256 – 4096 | 1024 |
| Format | .png / .tif / .tiff / .bmp | .png |

Cancel any time — partial buffer is dropped. 64 MP soft cap.

### 15.3 Remote Poster

The Client dialog can also produce posters — pick a saved server connection, Mode = `image`, dial up Width / Height. The server renders + streams bytes back over TLS. For huge posters, use Return mode = `saved-path`.

See [Docs/Capture-Guide.md](Docs/Capture-Guide.md).

---

## 16. Authoring (User Equation, Sandbox, User Bulb)

Three authoring engines for one-off custom fractals.

### 16.1 User Equation (CalcGen)

- Roslyn-compiled per-pixel `Complex Step(Complex z, Complex c, int n)`.
- Full access to `System.Numerics.Complex` + `System.Math`.
- Auto-recompile 500 ms after the last keystroke.
- **CalcGen** can additionally code-generate a full 5-path calculator (scalar + AVX2 + Pert + BLA + ILGPU GPU) from a one-line equation.
- Saved to `%APPDATA%\FracturingFog\userequations.json`.
- See [Docs/CalcGen-UserGuide.md](Docs/CalcGen-UserGuide.md).

### 16.2 Sandbox

- Restricted DSL — no .NET BCL access, safe to share.
- `z*z + c`, `let x = expr in body`, ternary, `sin / cos / sqrt / exp / log / conj`, `abs / re / im / arg`.
- Saved to `%APPDATA%\FracturingFog\sandboxequations.json`.

### 16.3 User Bulb 3D

- 3D analogue of User Equation. Roslyn-compiled `Vec3 Step(Vec3 z, Vec3 c, int n, double[] p)` (or `Quat Step` for 4D mode).
- Mandelbulb-style raymarching with analytic + numerical DE.
- Animated `t` parameter; named scalar params; chain editor for multi-step recurrences.
- OBJ mesh export.
- Saved to `%APPDATA%\FracturingFog\userbulbs.json`.
- See [Docs/UserBulb-Guide.md](Docs/UserBulb-Guide.md).

All three persist with the option to **Promote to fractal list** — promoted entries appear in the toolbar Type combo as first-class fractal types.

---

## 17. Audio-Reactive Engine

Enable from Slideshow Settings → Audio or the menu **Audio Settings…** button. The slideshow engine swaps fixed-duration timers for a beat counter driven by spectral-flux onset detection.

### 17.1 Sources

| Source | Description |
|---|---|
| System Loopback | Captures default audio output |
| Audio File | MP3 / WAV / FLAC / OGG / AIFF / WMA — plays + analyzes |
| Microphone | Default capture device |
| Fractal Synth | Closed-loop fractal-derived audio (deterministic) |

### 17.2 Tunables

- **Sensitivity** 0–100 % — onset threshold.
- **Beats per Theme / Region** — default 8 / 32.
- **Synth BPM / Routing** — Fractal Synth only.
- **Beat-Detector EQ** — 5 band-weight sliders (Bass / Low-Mid / Mid / High-Mid / High).
- **Fade × beat** — cross-fade duration as fraction of beat. Default 0.75.

Settings persist to `%APPDATA%\FracturingFog\audio-settings.json`.

See [Docs/Slideshow-AudioReactive-Guide.md](Docs/Slideshow-AudioReactive-Guide.md).

---

## 18. Multi-Monitor & Window Modes

| Mode | Behavior |
|---|---|
| **Span** | Stretch the window across the entire virtual desktop (all monitors); toolbar/status auto-hide |
| **Full Screen** | Borderless single-monitor full-screen |
| **Mini Mode** | Shrink to minimum size, borderless, top-most — a desktop companion view |
| **On-Top** | Keep main window above all others |

Span mode is the foundation for wallpaper-resolution captures and for showing the slideshow on a multi-monitor setup.

---

## 19. Client / Server

Render on a workstation, drive from a laptop. Same `FracturingFog.exe` for both sides.

| Mode | Invocation |
|---|---|
| UI | `FracturingFog.exe` |
| Server | `FracturingFog.exe --server [opts]` |
| Remote batch | `FracturingFog.exe --batch --remote …` |

All traffic is mutual TLS (mTLS). The client vault is AES-GCM under a master password the user enters once per session.

The in-shell Floating Menu has two buttons:

| Button | What it does |
|---|---|
| **Server…** | Local server admin dialog — status, lifecycle (Start / Restart / Kill), limits, TLS hardening, rate limit, paths, stale sweep |
| **Client…** | Remote-render client dialog — connection vault, render presets (image + video), inline / saved-path return mode |

User-code fractal types (User Equation / Sandbox / User Bulb) are blocked at the protocol layer to prevent RCE — the server only accepts built-in calculators.

See [Docs/ClientServer-UserGuide.md](Docs/ClientServer-UserGuide.md) and [Docs/ServerAdmin-Guide.md](Docs/ServerAdmin-Guide.md).

---

## 20. Help System

Press the Help button (or the corresponding floating-menu entry) to open the **Floating Help** window — a borderless dark dialog with the following tabs:

| Tab | Contents |
|---|---|
| **About** | Version, platform, runtime, renderer, credits, clickable external doc links (Wikipedia, Avalonia, Vortice, ffmpeg, ILGPU, FFV1, perturbation theory) |
| **Hardware** | Live system info: GPU adapters (DXGI enumeration), D3D11 feature level, CPU, OS, memory, SIMD vector width. Refresh button re-fetches |
| **Features** | Cross-reference of navigation, keyboard, toolbar, menu, post-FX, capture, precision |
| **Using** | Sub-tabs: Toolbar / Regions / Slideshow + Video / Poster / Theme Editor / Audio-Reactive |
| **Authoring** | Sub-tabs: CalcGen / ColorGen |
| **Batch / CLI** | Headless CLI reference |
| **Client / Server** | Sub-tabs: Walkthrough / Server Admin |
| **Mathematics** | 18 sub-tabs covering every fractal family (Overview / Mandelbrot / Julia / Burning Ship / Tricorn / Multibrot / Phoenix / Newton / Nova / Buddhabrot / IFS / L-System / Attractor / Mandelbulb / User Equation / User Bulb 3D / Sandbox / Mandelbrot Z² Generated) |
| **Bio** | Benoit Mandelbrot biography |
| **Architecture** | Module-by-module overview for contributors |

The About-tab links are clickable buttons — the host launches each URL via the system browser.

---

## 21. Persistence & File Locations

All user-modifiable data lives under your AppData folder so updates to the program never overwrite your work.

| File | Purpose |
|---|---|
| `%APPDATA%\FracturingFog\regions.json` | User-defined coordinate bookmarks |
| `%APPDATA%\FracturingFog\colorthemes.json` | User-imported / authored color themes |
| `%APPDATA%\FracturingFog\colorgen.json` | ColorGen DSL source library |
| `%APPDATA%\FracturingFog\userequations.json` | User Equation source library |
| `%APPDATA%\FracturingFog\sandboxequations.json` | Sandbox DSL source library |
| `%APPDATA%\FracturingFog\userbulbs.json` | User Bulb 3D source + chain library |
| `%APPDATA%\FracturingFog\audio-settings.json` | Audio-reactive slideshow config |
| `%APPDATA%\FracturingFog\slideshow-settings.json` | Slideshow timing config |
| `%APPDATA%\FracturingFog\client-connections.json` | Sealed mTLS connections (AES-GCM) |
| `%APPDATA%\FracturingFog\client-render-presets.json` | Client render presets |
| `%APPDATA%\FracturingFog\server-config.json` | Local server config |
| `%APPDATA%\FracturingFog\server-certs\*.pfx` | Self-signed mTLS bundle |
| `%APPDATA%\FracturingFog\server-logs\*.log` | Server session logs |
| `%APPDATA%\FracturingFog\server-work\` | Server scratch dir (auto-purged) |
| `Resources\*.bmp`, `*.ico` | Built-in icons and toolbar images |

All JSON files are **human-readable** (indented `System.Text.Json` output) — no third-party serializer dependency, easy to diff and share.

---

<div align="center">

### Credits

UI & Engine · **Bradley Brown**
Shell · [Avalonia UI](https://avaloniaui.net) (MIT)
Renderer · [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT)
GPU compute · [ILGPU](https://www.ilgpu.net) (BSD)
Video encoding · [ffmpeg](https://ffmpeg.org) (LGPL build)

*Fracturing Fog · Real-time high-precision Mandelbrot exploration · © 2026*

</div>
