# Regions Guide — Coordinate Bookmarks

A region in Fracturing Fog is a saved view: center coordinates with full DD/QD limb fidelity, zoom factor, iteration count, fractal type, and optional preferred theme + bound saved-equation name.

> Companion pages: [User Index](_Index.md) · [Avalonia User Guide](Avalonia-UserGuide.md) · [Slideshow Guide](Slideshow-AudioReactive-Guide.md)

![PLACEHOLDER — Region combo dropdown with built-ins above the divider and user saves below](../Images/_placeholders/placeholder.svg)

---

## A friendly tour

Think of regions as **bookmarks for the fractal**. You wander, you find a view you love, you press
**`V`** to bookmark it, and from that moment on it appears in the *Region* dropdown forever. Pick
it from any other session and you snap back to the exact same spot — same coordinates, same zoom,
same iteration count, same fractal family.

Two kinds of regions live in that dropdown:

| Kind        | Where they live                                                              | Editable? |
|-------------|------------------------------------------------------------------------------|:---------:|
| **Built-in** | Baked into the app — *Classic Full View*, *Seahorse Valley*, *Elephant Valley*, *Mini Mandelbrot*, *Period-3 Bulb*, etc. | no |
| **Yours**    | Anything you save with `V`. Stored as plain JSON under `%APPDATA%\FracturingFog\regions.json`. | yes |

> [!TIP]
> Right-click the Region dropdown to sort: **Default** (built-ins first, then yours), or
> **By Fractal Type** (Mandelbrot bookmarks together, Julia bookmarks together, etc.). The setting
> persists per dropdown — toolbar and Floating Menu remember independently.

### Worked example — "Bookmark something deep, come back tomorrow"

1. Pan and zoom freely until you find a swirl that grabs you. Pay no attention to coordinates.
2. Press **`V`**. A name prompt opens, pre-filled with something sensible like
   *"Mandelbrot zoom 1.4e12"*.
3. Change the name to anything you like — *"the swirl above the spike"* works.
4. Hit OK.

Tomorrow:

5. Launch the app. Open the **Region** dropdown.
6. Pick your bookmark. The view snaps to the exact saved coordinates, with the same iteration
   count and fractal family.

### Worked example — "Share a region with a friend"

1. Open Floating Menu → Region Navigation → **Exp…** (Export).
2. Pick a `.json` filename and save it. The file is text — a few KB.
3. Email / DM / Discord that file to your friend.
4. They open Floating Menu → Region Navigation → **Imp…** (Import) and pick the file. Done — the
   region appears in their Region dropdown like any other.

> [!IMPORTANT]
> Regions remember the fractal type too. If you bookmark a Julia view, picking that bookmark snaps
> you straight into Julia — you do not need to switch the *Type* dropdown first.

### Worked example — "Paste a precise coordinate from a friend"

When deep-zoom enthusiasts swap coordinates online, they paste pipe-separated *limbs* like:

```text
-0.7548776661778 | 1.2e-17 | 0 | 0
```

Those four numbers are the high-precision representation of one axis. Fracturing Fog reads them
natively:

1. Open Floating Menu → Region Navigation.
2. Click into the **CX** textbox and paste the line above. Repeat for **CY** with the imaginary half.
3. Type your zoom into **Zoom** and your iteration count into **Iter**.
4. Click **Go**. The view jumps.

Now press **`V`** to bookmark it so you do not have to paste again next time.

---

## Table of Contents

1. [Why Regions](#1-why-regions)
2. [Built-in vs User Regions](#2-built-in-vs-user-regions)
3. [Save Workflow](#3-save-workflow)
4. [Apply Workflow](#4-apply-workflow)
5. [Sort + Filter](#5-sort--filter)
6. [Export + Import](#6-export--import)
7. [JSON Schema](#7-json-schema)
8. [Pipe-Separated Limb Format](#8-pipe-separated-limb-format)
9. [Slideshow Integration](#9-slideshow-integration)
10. [Tips](#10-tips)

---

## 1. Why Regions

Manual pan + zoom + iter tuning to reach a memorable view takes minutes. Pasting the coordinates back later only works if you've recorded them at full precision — at zoom 10²⁵ a double-precision `(x, y)` is already inadequate.

Regions solve this by capturing every input the renderer needs, with extended-precision limbs preserved as-is, so re-applying a region is bit-exact.

---

## 2. Built-in vs User Regions

| Category | Source | Editable? |
|---|---|---|
| Built-in | Baked into `<install>\Resources\Regions\`, ships with the EXE | No |
| User | `%APPDATA%\FracturingFog\regions.json` | Yes |

The built-in tour covers cardioid valley, period bulbs (2 / 3 / 4 / 5), seahorse valley, elephant valley, double-spirals, the antenna, multiple mini-Mandelbrots, and several deep-zoom showpieces (e.g., a 1e25 location demonstrating QD math).

Applying a built-in works; deleting one does not — the Delete button only acts on user entries.

---

## 3. Save Workflow

1. Pan / zoom / type-switch to the view you want.
2. Press `V`, or click Floating Menu → Region Navigation → **Save**.
3. The name prompt opens, pre-filled with a suggested name based on the active fractal + region area.
4. Type a final name and confirm.
5. **If the name already exists in the user library**, an overwrite confirmation prompt appears (added in v0.6.2). Confirm to replace, Cancel to back out.
6. The new region appears in every region combo (toolbar + menu).

Auto-captured fields:
- Center coordinates (Hi + 3 low limbs per axis when DD/QD precision is engaged)
- Zoom
- Iterations (current or locked)
- Fractal type
- Theme name (if a non-default theme is active)
- Bound Sandbox / User Equation / User Bulb entry name (if applicable)

---

## 4. Apply Workflow

Selecting a region in any combo (toolbar OR menu):

1. Pauses any in-flight calculation.
2. Mutates the view state in place: pan/zoom anchored at the saved coordinate (full precision restored), iter count snapped to the saved value, fractal type re-selected if it differs.
3. Re-applies the recorded theme if one is stored AND the corresponding Lock checkbox in Post-FX is OFF.
4. Re-applies any bound saved equation (Sandbox / User Equation / User Bulb) by name. If the bound entry has been deleted, the engine falls back to the currently-loaded source — no error.
5. Triggers a full-quality re-render.

The Slideshow engine uses the same Apply path under the hood — there is no separate ""slideshow region"" type.

---

## 5. Sort + Filter

Right-click any Region combo (toolbar OR menu) for the sort menu:

| Item | Effect |
|---|---|
| Default | Built-ins first, then user regions, original declared order |
| By Fractal Type → \<type\> | Filter to regions whose stored type matches \<type\> |

A non-selectable `— select region —` header is injected at the top of the filtered list — picking it has no effect (the VM filters em-dash-prefixed entries from selection handling).

---

## 6. Export + Import

### Export

Floating Menu → Region Navigation → **Exp…** opens a Save File dialog. The exported JSON contains your entire user region library (built-ins are not exported — they're already in the recipient's EXE).

### Import

Floating Menu → Region Navigation → **Imp…** opens an Open File dialog and merges the loaded regions into your library.

**Name-collision handling** (per-entry prompt):

| Action | Effect |
|---|---|
| Skip | Keep your existing entry; discard the import |
| Overwrite | Replace your entry with the imported version |
| Rename | Append a numeric suffix to the imported entry's name |
| Skip All / Overwrite All | Apply the choice to remaining collisions silently |

---

## 7. JSON Schema

Each entry in `regions.json` is a single `Region` object:

```json
{
  "name": "Seahorse Valley Deep",
  "type": "Mandelbrot",

  "centerXHi": -0.7548409391432949,
  "centerXLo1": 1.2e-17,
  "centerXLo2": 0.0,
  "centerXLo3": 0.0,

  "centerYHi": 0.05716936067717272,
  "centerYLo1": -3.4e-18,
  "centerYLo2": 0.0,
  "centerYLo3": 0.0,

  "zoom": 1.2e15,
  "iterations": 8192,
  "quality": "Ultra",

  "themeName": "Inferno Cycling",

  "sandboxName": null,
  "userEquationName": null,
  "userBulbName": null,

  "notes": "Deep dive into the central seahorse — see ridges around the spiral arm",
  "isExtreme": false
}
```

Field rules:

| Field | Required? | Notes |
|---|---|---|
| `name` | Yes | Must be unique within the file |
| `type` | Yes | Mirrors `FractalType` enum (case-sensitive) |
| `centerXHi` / `centerYHi` | Yes | Standard double precision |
| `centerXLo1..3` / `centerYLo1..3` | No (default 0) | DD low limb + QD extra limbs |
| `zoom` | Yes | Double; scientific notation accepted |
| `iterations` | Yes | Integer ≥ 64 |
| `quality` | No | Suggested preset; falls back to current |
| `themeName` | No | Preferred theme |
| `sandboxName` / `userEquationName` / `userBulbName` | No | Bound saved-equation reference |
| `notes` | No | Free-form |
| `isExtreme` | No | True = filtered out when `Include extreme regions` is off |

JSON is indented (System.Text.Json) — easy to diff and share. Field omission follows `WhenWritingNull` so `null` values disappear from the file entirely.

---

## 8. Pipe-Separated Limb Format

The CX / CY textboxes accept a special **pipe-separated limb format** for paste-back of high-precision coordinates without loss:

```
-0.7548409391432949 | 1.2e-17 | 0 | 0
```

| Limb | Position | Meaning |
|---|---:|---|
| 1 | Hi | Standard double |
| 2 | Lo₁ | DD low word (~10⁻¹⁶ of Hi) |
| 3 | Lo₂ | QD second extra limb |
| 4 | Lo₃ | QD third extra limb |

Single-double paste-back drops the low limbs. Three- or four-limb paste-back round-trips DD / QD precision so a region saved at zoom 10²⁵ can be reproduced bit-exact across machines.

The Floating Menu's **Copy** button emits the limb format for CX and CY, plain values for Zoom and Iter.

---

## 9. Slideshow Integration

Slideshow Settings exposes two region-affecting controls:

| Setting | Effect |
|---|---|
| Beats per Region | How many beats / seconds before the next region applies. Set to 0 to lock the active region. |
| Include extreme regions | When off, regions with `isExtreme: true` are skipped during slideshow rotation |

Shift+click the Slideshow button is a shortcut for ""lock the current region"" — equivalent to setting Beats per Region = 0 for the current session.

---

## 10. Tips

**Use descriptive names.** ""Seahorse Valley Deep"" beats ""DeepZoom27"" two weeks later.

**Group with categories.** The `notes` field is free-form — prefix with `[Demo]`, `[Showcase]`, `[Bug]` to filter by hand later.

**Save before exploring.** Pan + zoom doesn't undo. Save the current view as a region before chasing a new direction — if you lose the spot, the region is still in the library.

**Edit JSON by hand.** The file is plain JSON. Tweaking a stored zoom, iteration count, or theme name in your editor of choice and re-launching the shell picks up the change. Reload from the Floating Menu pulls the file without a restart.

**Don't manually edit the limbs.** The Lo₁/Lo₂/Lo₃ fields are arithmetic residuals. Editing one value without the others produces a non-normalized DD/QD number that may render as visual noise.

**Pair regions with bound equations.** If your view depends on a Sandbox or User Equation entry, save the region while that entry is active so the binding is captured. Sharing the region JSON + the equation JSON gives the recipient a one-click reproduction.

**Use `isExtreme` for >10²⁰ zooms.** Set this field by hand-editing JSON for regions you don't want appearing in casual slideshow rotation — the calmer rotation makes for a better demo.

**Slideshow region filter applies to user regions too.** A user region with `isExtreme: true` will be filtered just like a built-in extreme region. Useful for keeping your library tidy without removing the entry.

---

*Regions Guide · Fracturing Fog · © 2026*
