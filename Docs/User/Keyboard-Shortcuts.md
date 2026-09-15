# Keyboard Shortcuts

Every keyboard binding in the Fracturing Fog Avalonia shell.

---

## Global (any fractal, any mode)

| Key | Action |
|---|---|
| `M` | Open the **Control Center** (the main menu) |
| `T` | Toggle Color Theme Editor |
| `R` | Reset view to default for the active fractal |
| `V` | Save current view as a new region |
| `Backspace` | **Back** — pop the most recent view off the navigation history and return to it |
| `Esc` | Exit Span mode / stop running slideshow / stop video zoom / close active sub-dialog |

> [!NOTE]
> `M` opens the Control Center — the single main menu. The old Floating Menu
> window was retired; the toolbar **Menu** button, `M`, and a right-click on the
> render surface all open the Control Center now.

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
| `Ctrl + H` | Toggle the **Post-FX HUD** — a second, discoverable binding for the same overlay as `X` (mnemonic: *H = HUD*) |
| `P` | Open the **Fractal Parameters** dialog |
| `F1` | Open the **Help** window |

---

## Accessibility — UI Scale

Resize the whole interface — every dialog, menu, and companion window — up or
down along a fixed ladder. The choice is remembered between sessions.

| Combo | Action |
|---|---|
| `Ctrl` + `+` | **Increase** the global UI scale one step (`Ctrl` + `=` also works, and the numeric-keypad `+`) |
| `Ctrl` + `-` | **Decrease** the global UI scale one step |
| `Ctrl` + `0` | **Reset** the UI scale to 100% |

> [!NOTE]
> These work from **any** window, including the main render window, so you can
> resize the dialogs without first clicking into one. The render surface itself
> is deliberately **not** scaled — only the surrounding UI chrome — so the
> fractal view keeps its exact pixel framing.

---

## Composition & Export Preview

Aids for framing a shot the way the poster / wallpaper export will crop it
(see the [poster / wallpaper export](Capture-Guide.md) flow).

| Combo | Action |
|---|---|
| `Ctrl + Shift + P` | Open the **1:1 poster / wallpaper preview** — renders the current view through the export path in its own window so you see exactly what will be written. |
| `Ctrl + Shift + F` | Toggle the **export-aspect frame guide** — an overlay at the wallpaper (multi-monitor union) aspect, so you can compose inside the shape the export will use. |

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
| `Ctrl + Shift + G` | Toggle the **GPU relief-raymarch** path (default ON). Off forces the CPU sphere-trace — the parity oracle. The title gains `[RELIEF GPU OFF]` while it is off. |

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
| `Alt` + double-click | **Relief DOF focus pick** — sets the depth-of-field focal plane from the clicked pixel's depth. Only on the Relief 3D raymarch with perspective + depth of field; a normal recenter otherwise. |

---

## Mouse — 3-D

| Input | Action |
|---|---|
| Wheel up | Zoom in (camera closer) |
| Wheel down | Zoom out (camera farther) |
| Left-click drag | Pan in screen space |
| Right-click drag X | Orbit theta (azimuth) |
| Right-click drag Y | Orbit phi (elevation, **inverted** for natural ""tilt up"" feel) |
| **Middle-click drag** | **Marquee zoom** — the 3-D equivalent of the 2-D right-drag box; release recenters the camera target and zooms to fill the rectangle (right-drag stays camera-orbit in 3-D) |

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

### Control Center (main menu)

| Key | Action |
|---|---|
| `Esc` | Close the pop-out / detached section window (does not exit the program) |
| `Enter` (inside the Explore section's CX/CY/Zoom/Iter fields) | Apply (same as the **Go** button) |

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
| Region (toolbar OR Control Center → Explore) | Default · By Fractal Type → \<type\> |
| Theme (toolbar OR Control Center) | Default · All A–Z · per-kind filter (Cycling / Phong3D / Pbr3D / …) |

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
