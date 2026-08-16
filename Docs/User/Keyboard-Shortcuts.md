# Keyboard Shortcuts

Every keyboard binding in the Fracturing Fog Avalonia shell.

---

## Global (any fractal, any mode)

| Key | Action |
|---|---|
| `M` | Toggle Floating Menu |
| `T` | Toggle Color Theme Editor |
| `R` | Reset view to default for the active fractal |
| `V` | Save current view as a new region |
| `Esc` | Exit Span mode / stop running slideshow / stop video zoom / close active sub-dialog |

---

## Overlays & Toggles (any fractal, any mode)

These single keys flip an on-screen overlay or open a companion window. They are
ignored while a text box has focus (see [Focus Behavior](#focus-behavior)).

| Key | Action |
|---|---|
| `G` | Toggle the coordinate **grid** overlay |
| `K` | Toggle the **watermark** overlay |
| `H` | Toggle the **performance HUD** (frame time, iteration budget, precision tier) |
| `Shift + H` | **Reset** the performance HUD's rolling averages — do this before timing a fresh region or video capture |
| `X` | Toggle the **Post-FX HUD** overlay (live brightness / contrast / adaptive readout) |
| `P` | Open the **Fractal Parameters** dialog |
| `F1` | Open the **Help** window |

---

## Performance & Deep-Zoom Diagnostics

Advanced switches for tuning the Mandelbrot render path. The GPU toggle is an
everyday speed control; the `Ctrl + Shift` combos are diagnostic — they turn off
one accelerator at a time so you can tell which math stage is responsible for a
visual artifact deep in a zoom. While a diagnostic is off, the window title gains
a suffix such as `[ACCEL OFF]` or `[SA OFF]` so you never forget it is engaged.

| Combo | Action |
|---|---|
| `Ctrl + G` | Toggle **GPU compute** for the single-precision Mandelbrot path, using whichever compute backend the session attached (D3D11 on Windows, or Vulkan/SPIR-V under `--renderer vulkan`). Falls back to CPU automatically on backends that cannot engage it; the Control Center checkbox stays in sync. |
| `Ctrl + Shift + A` | Toggle Mandelbrot **acceleration** (perturbation / BLA fast path). Off = plain per-pixel iteration. |
| `Ctrl + Shift + S` | Toggle **Series Approximation** — the polynomial skip that fast-forwards the first thousands of iterations near the reference orbit. |
| `Ctrl + Shift + D` | Toggle **double-double (DD) BLA** precision on the bilinear-approximation step. |

> [!TIP]
> Chasing pixelation or smearing that only appears past a very deep zoom? Turn
> the accelerators off one at a time with the `Ctrl + Shift` combos and watch
> whether the artifact disappears. Whichever toggle "fixes" the image names the
> stage that needs attention. For the theory behind these stages see
> [Deep-Zoom & Perturbation](../Deep-Zoom-Perturbation.md).

> [!NOTE]
> Bare `A` and `S` are the WASD pan / zoom keys during 2-D and 3-D navigation, so
> the diagnostic toggles deliberately live on the `Ctrl + Shift` layer to stay
> out of their way.

---

## 2-D Navigation (Mandelbrot, Julia, Burning Ship, …)

| Key | Action |
|---|---|
| `W` | Zoom in (centred) |
| `S` | Zoom out (centred) |
| `A` | Pan left |
| `D` | Pan right |
| `Q` | Pan up |
| `E` | Pan down |
| `Shift + W` / `Shift + S` | Quarter-step zoom in / out (fine) |
| `Shift +` pan key | Quarter-step pan (precise nudge) |

---

## 3-D Navigation (Mandelbulb, User Bulb 3D)

| Key | Action |
|---|---|
| `W` | Move camera closer (distance −) |
| `S` | Move camera farther (distance +) |
| `Shift + W` / `Shift + S` | Quarter-step camera distance (fine) |
| `A` | Pan left (screen-space) |
| `D` | Pan right |
| `Q` | Pan up |
| `E` | Pan down |
| `↑` | Tilt camera up (phi +) |
| `↓` | Tilt camera down (phi −) |
| `←` | Orbit camera left (theta −) |
| `→` | Orbit camera right (theta +) |
| `PgUp` | Light azimuth − |
| `PgDn` | Light azimuth + |
| `Home` | Light elevation − |
| `End` | Light elevation + |

---

## Mouse — 2-D

| Input | Action |
|---|---|
| Wheel up | Zoom in at cursor |
| Wheel down | Zoom out at cursor |
| Left-click drag | Pan (fast pass mid-drag; full re-render 300 ms after release) |
| Double-click | Center on point + zoom in one step |
| **Right-click drag** | **Highlight-to-zoom** — marquee box; release centers + zooms to fill rectangle |

---

## Mouse — 3-D

| Input | Action |
|---|---|
| Wheel up | Zoom in (camera closer) |
| Wheel down | Zoom out (camera farther) |
| Left-click drag | Pan in screen space |
| Right-click drag X | Orbit theta (azimuth) |
| Right-click drag Y | Orbit phi (elevation, **inverted** for natural ""tilt up"" feel) |

---

## Focus Behavior

Keyboard pan / zoom / camera keys are **ignored** while any text box has keyboard focus (CX, CY, Zoom, Iter, the equation editor, the search box in the theme combo, …).

Clicking the render surface restores focus to the canvas — including after a toolbar click. (v0.6.2 fixed a regression where toolbar interaction permanently stole focus.)

If keystrokes feel ""dead,"" click the rendering area once before pressing them.

---

## Slideshow VCR (mouse only)

The transport bar at the bottom of MainWindow exposes:

| Button | Action |
|---|---|
| ◀◀ | Previous region |
| ◀ | Previous theme within region |
| ▮▮ | Pause / Resume |
| ▶ | Next theme within region |
| ▶▶ | Next region |

Visible only while the slideshow is running.

---

## Sub-dialog Hotkeys

### Color Theme Editor

| Key | Action |
|---|---|
| `Ctrl+S` | Save to library |
| `Ctrl+E` | Export JSON |
| `Esc` | Close (cancels live preview if Live Preview is on) |

### User Equation / Sandbox / User Bulb editor

| Key | Action |
|---|---|
| `Ctrl+S` | Save under current name |
| `Ctrl+Shift+S` | Save As |
| `F5` | Force recompile |
| `Esc` | Close |

### Floating Menu

| Key | Action |
|---|---|
| `Esc` | Close (does not exit the program) |
| `Enter` (inside CX/CY/Zoom/Iter) | Apply (same as Go button) |

### Floating Help

| Key | Action |
|---|---|
| `Esc` | Close |

### Server Admin / Client dialog

| Key | Action |
|---|---|
| `Esc` | Close |

---

## Right-Click Sort Menus

| Combo | Right-click options |
|---|---|
| Region (toolbar OR menu) | Default · By Fractal Type → \<type\> |
| Theme (toolbar OR menu) | Default · All A–Z · per-kind filter (Cycling / Phong3D / Pbr3D / …) |

The selected sort persists until changed (per-shell, not on disk).

---

## Modifier Combinations

| Action | Combo |
|---|---|
| Lock current region in slideshow | `Shift` + click Slideshow button |
| Quarter-step pan | `Shift` + pan key |
| Quarter-step zoom / 3-D distance | `Shift + W` / `Shift + S` |
| Custom — TODO | (none reserved) |

---

*Keyboard Shortcuts · Fracturing Fog v0.6.x · © 2026*
