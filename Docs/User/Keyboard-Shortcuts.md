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

## 2-D Navigation (Mandelbrot, Julia, Burning Ship, …)

| Key | Action |
|---|---|
| `W` | Zoom in (centred) |
| `S` | Zoom out (centred) |
| `A` | Pan left |
| `D` | Pan right |
| `Q` | Pan up |
| `E` | Pan down |
| `Shift +` pan key | Quarter-step (precise nudge) |

---

## 3-D Navigation (Mandelbulb, User Bulb 3D)

| Key | Action |
|---|---|
| `W` | Move camera closer (distance −) |
| `S` | Move camera farther (distance +) |
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
| Quarter-step pan / zoom | `Shift` + pan key |
| Custom — TODO | (none reserved) |

---

*Keyboard Shortcuts · Fracturing Fog v0.6.x · © 2026*
