# Color Theme Editor — Complete Guide

The Color Theme Editor is Fracturing Fog's modeless live-preview palette authoring tool. This guide covers every control, the persistence model, and 20 worked examples.

---

## Table of Contents

1. [Opening the Editor](#1-opening-the-editor)
2. [Layout](#2-layout)
3. [Identity Section](#3-identity-section)
4. [Kind Selector](#4-kind-selector)
5. [Color Stops](#5-color-stops)
6. [Cycle Speed](#6-cycle-speed)
7. [3D Lighting (Phong + PBR)](#7-3d-lighting-phong--pbr)
8. [Phong3D Extras](#8-phong3d-extras)
9. [Pbr3D Extras](#9-pbr3d-extras)
10. [In-Set Color](#10-in-set-color)
11. [Post-FX Defaults](#11-post-fx-defaults)
12. [Live Preview + Actions](#12-live-preview--actions)
13. [Image Palette Helper](#13-image-palette-helper)
14. [JSON Schema](#14-json-schema)
15. [Worked Examples](#15-worked-examples)
16. [Tips + Troubleshooting](#16-tips--troubleshooting)

---

## 1. Opening the Editor

Three entry points:

- Toolbar **Edit Theme** button.
- Floating Menu → Color Themes → **Edit Theme…** button.
- Hotkey `T`.

The editor seeds from the currently-selected theme. Cancel-without-Save restores the previously active theme on close — your live preview edits don't pollute the library.

---

## 2. Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ Target  : Region ▾   Base theme ▾                                │
│ Identity: Name __________  Kind ▾  Category ▾                    │
│           Description ___________  Max zoom _____                │
├──────────────────┬───────────────────────────────────────────────┤
│ Color Stops      │ 3D Lighting                                   │
│  [pos] [swatch]  │  Steepness ___   Ambient ___                  │
│  [pos] [swatch]  │  Key   Dir XYZ  Diffuse RGB  Spec RGB  Shine  │
│  [pos] [swatch]  │  Fill  Dir XYZ  Diffuse RGB  Spec RGB  Shine  │
│  [+ Add] [- Del] │  ☐ Rim light                                  │
│                  │                                               │
│ Cycle            │ Phong3D extras                                │
│  Speed ___       │  (key spec extra, fill diff extra)            │
│                  │                                               │
│ In-Set           │ Pbr3D extras                                  │
│  ☐ Override RGB  │  Lighting mode ▾  Glow exp / scl              │
│                  │  Material bands [start end metal rough] …     │
│ Post-FX defaults │                                               │
│  Brightness ___  │                                               │
│  Contrast   ___  │                                               │
│  Adaptive   ___  │                                               │
│                  │                                               │
│ Actions          │                                               │
│  ☑ Live preview  │                                               │
│  [Apply] [Blank] │                                               │
│  [Revert]        │                                               │
│  [Save] [Export] │                                               │
│  [Save C#]       │                                               │
│  [From Image…]   │                                               │
└──────────────────┴───────────────────────────────────────────────┘
```

Sections collapse when irrelevant — Phong3D / PBR3D extras hide for Gradient + Cycling kinds.

---

## 3. Identity Section

| Field | Purpose |
|---|---|
| Name | Library key. Must be unique within `colorthemes.json`. |
| Kind | Gradient / Cycling / Phong3D / Pbr3D |
| Category | Free-form grouping label (used by the right-click sort menu) |
| Description | Optional free-text annotation |
| Max zoom | Optional metadata — UI does not enforce; future-proofing for adaptive theme switching |

**Save behavior:** if the typed Name already exists in the library, the editor prompts to confirm overwrite. Cancel returns to the editor with the existing entry untouched.

---

## 4. Kind Selector

| Kind | When to use |
|---|---|
| **Gradient** | Single-pass linear ramp across the iter range. Best for escape-time + distance-estimation maps where the iteration count is roughly monotonic. |
| **Cycling** | Repeats the gradient N times along the iter axis. Best for revealing fine band structure in shallow + medium zooms. |
| **Phong3D** | Cycling gradient + Blinn-Phong directional lighting computed from a synthesized surface normal (the Z component is built from the iter derivative). Gives a relief-mapped look. |
| **Pbr3D** | Cycling gradient + Cook-Torrance physically-based shading + per-band metallic/roughness. Gives a real-material look (chrome, brushed steel, ceramic, glass). |

Changing Kind shows / hides the relevant lighting sections.

---

## 5. Color Stops

| Control | Behavior |
|---|---|
| Position [0, 1] | Normalised stop along the gradient. 0 = low iter, 1 = high iter. |
| Swatch | Click for the color picker. RGB / hex entry below the swatch also works. |
| Add | Insert a new stop above the selected one. |
| Delete | Remove the selected stop. Editor enforces ≥ 2 stops. |
| Reorder | Drag the position handle to reposition. |

Stops interpolate linearly between adjacent positions. Outside [0, 1] the outer stops clamp.

**Tip:** for smooth gradients, space stops by perceptual distance, not numeric distance. Two close stops in dark regions look smoother than the same stops evenly placed across the full range.

---

## 6. Cycle Speed

Active for Cycling / Phong3D / Pbr3D kinds.

`Speed = 0.02` ≈ one full gradient cycle every 50 smoothed-iter units. Higher speed = more bands, tighter rings. Typical range 0.005 – 0.2.

For Phong3D + Pbr3D, the cycle controls the **base albedo** banding only — the lighting overlays on top, so a moderate cycle (0.02–0.05) usually reads better than a frantic high cycle.

---

## 7. 3D Lighting (Phong + PBR)

Shared parameters between Phong3D and Pbr3D kinds:

| Parameter | Purpose | Typical range |
|---|---|---:|
| Steepness | Z-scale on the synthesized normal. Higher = more relief depth. | 0.5 – 4.0 |
| Ambient | Base illumination before lighting. 0 = pitch-black shadows. | 0.0 – 0.3 |

### Lights

Each light carries:

| Field | Description |
|---|---|
| Direction (X, Y, Z) | Normalised direction vector. (0, 0, 1) = straight at camera. |
| Diffuse RGB | Color of the diffuse contribution. Bright key, dim fill is typical. |
| Specular RGB | Color of the highlight. White / warm for key, cool for fill. |
| Shininess | Specular exponent. 1 = matte, 256 = chrome. |

**Key Light** is the strong, often warm primary. **Fill Light** is the dim, often cool sim of sky / bounce. **Rim Light** (optional) is a back-light that highlights silhouette edges.

A balanced 3-point setup: Key 1.0 / Fill 0.4 / Rim 0.3.

---

## 8. Phong3D Extras

Phong3D layers a second specular pass for the Fill light only, producing a softer secondary highlight you can use for atmospheric accent (e.g., a cyan halo opposite the warm key spec).

| Field | Purpose |
|---|---|
| Fill diff extra | Extra diffuse contribution beyond the shared lighting block. |
| Key spec extra | Extra specular bump on top of the shared key. |

Default both to 0 unless you want the explicit accent.

---

## 9. Pbr3D Extras

Pbr3D uses a Cook-Torrance BRDF with a GGX normal-distribution function and Smith geometric occlusion. Material parameters drive the BRDF:

| Field | Purpose | Range |
|---|---|---:|
| Lighting mode | `PBRRealistic` (filmic curve) / `PBRBright` (pre-multiplied radiance for a punchier sci-fi look) | enum |
| Glow exp / scale | Additive emission near escape (t → 1). | 0–10 / 0–5 |
| Material bands | List of `(start t, end t, metallic, roughness)` tuples. Lets you make the cardioid matte ceramic while the filaments turn chrome. | per-band |

Material bands are evaluated in declared order — the first band whose `(start, end)` contains the current t wins. Out-of-range t falls back to a default (metallic 0, roughness 0.5).

---

## 10. In-Set Color

When the **Override** checkbox is ticked, the picker sets the opaque RGB color for points that never escape (the cardioid + bulbs).

Default behavior (override off) draws the in-set with opaque black. Useful overrides:
- Dark navy `(8, 12, 32)` to keep contrast without harshness.
- Very-dark hue-matched color taken from the gradient's tail — keeps the in-set from popping visually.
- Transparent (alpha) — currently not honored; in-set is always opaque.

---

## 11. Post-FX Defaults

Per-theme defaults for the three Post-FX sliders.

When you select this theme from a combo, the renderer snaps each slider to the corresponding default — **unless** that slider's Lock checkbox is ticked. Locking lets you preserve a global brightness preference across theme browsing.

| Field | Range | Default |
|---|---:|---:|
| Brightness | −100 … +100 | 0 |
| Contrast | −100 … +100 | 0 |
| Adaptive | 0 … 100 | 0 |

---

## 12. Live Preview + Actions

| Action | Behavior |
|---|---|
| Live preview | Edits push to the main render via 150 ms debounce. Drag freely; the calculator re-runs once per debounced commit. |
| Apply | Force a push regardless of live-preview state. |
| New Blank | Discard edits; start from a fresh Gradient (2 stops, black → white). |
| Revert | Reload from the last source theme name. |
| Save to Library | Validate Name + ≥ 2 stops, upsert into `colorthemes.json`. Prompts to confirm overwrite if the name already exists. |
| Export JSON… | Write a single-theme JSON array to disk. |
| Save C#… | Write a compilable `ColorThemeData` C# class via `ColorThemeCsExporter`. Drop the file into `Models/ColorSchemes/Generated/` and rebuild to ship as built-in. |
| From Image… | Open the Image Palette helper. |

---

## 13. Image Palette Helper

Sample any PNG / JPG / BMP, get a 5-stop palette via k-means clustering in CIELAB color space, loaded straight into the Color Stops list.

| Control | Purpose |
|---|---|
| Source image | Click to browse. Loads + downsamples for analysis. |
| Cluster count | 2 – 16 centroids. Default 5. |
| Sort | By hue / by lightness / by frequency. |
| Sample bias | Center / Edges / Uniform — bias which pixels feed the kmeans. |
| Use as palette | Load the centroids as Color Stops. |

Tip: pick a source image whose mood matches your fractal target. A sunset photo for a warm Mandelbrot, a CT scan for a sci-fi Mandelbulb.

---

## 14. JSON Schema

Each entry in `colorthemes.json` is a single `ColorThemeData` object. Field omission follows `WhenWritingNull` — null fields disappear entirely.

```json
{
  "name": "My Theme",
  "kind": "Phong3D",
  "category": "Custom",
  "description": "Warm dusk shading",
  "maxZoom": 1e20,
  "stops": [
    { "position": 0.0, "r": 8,   "g": 4,   "b": 16  },
    { "position": 0.4, "r": 255, "g": 80,  "b": 16  },
    { "position": 1.0, "r": 255, "g": 230, "b": 180 }
  ],
  "cycleSpeed": 0.03,
  "lighting": {
    "steepness": 1.6,
    "ambient": 0.12,
    "keyLight": {
      "dir": { "x": 0.4, "y": 0.6, "z": 0.7 },
      "diffuse": { "r": 1.0, "g": 0.9, "b": 0.8 },
      "specular": { "r": 1.0, "g": 1.0, "b": 1.0 },
      "shininess": 64
    },
    "fillLight": { ... },
    "rim": null
  },
  "inSet": null,
  "postFxDefaults": {
    "brightness": 0,
    "contrast": 10,
    "adaptive": 0
  }
}
```

Pbr3D entries add a `pbrExtras` block with `lightingMode`, `glowExp`, `glowScale`, and a `materialBands` array.

---

## 15. Worked Examples

### Example 1 — Warm Sunset Gradient

- Kind: Gradient
- Stops: (0.0, #0A0613) → (0.3, #4B1B30) → (0.6, #C8521C) → (0.85, #FFA13C) → (1.0, #FFE9A8)
- In-set: override #050308.

### Example 2 — Cool Glacier Cycling

- Kind: Cycling
- Cycle Speed: 0.025
- Stops: (0.0, #050E2B) → (0.4, #1E5780) → (0.7, #C0E6FF) → (1.0, #FFFFFF)

### Example 3 — Twilight Spiral (3-stop)

- Kind: Cycling
- Cycle Speed: 0.04
- Stops: (0.0, #1A0033) → (0.5, #FF5599) → (1.0, #FFFF99)

### Example 4 — Solar Flare Phong3D

- Kind: Phong3D
- Cycle Speed: 0.03
- Stops: (0.0, #100000) → (0.45, #C04000) → (0.85, #FFC050) → (1.0, #FFFFE0)
- Steepness 2.2, Ambient 0.08
- Key Light dir (0.5, 0.6, 0.6), diffuse (1.0, 0.85, 0.55), spec (1.0, 1.0, 1.0), shine 96
- Fill Light dir (-0.4, -0.3, 0.5), diffuse (0.2, 0.3, 0.6), spec (0.4, 0.5, 0.8), shine 32

### Example 5 — Brushed Copper Pbr3D

- Kind: Pbr3D
- Cycle Speed: 0.02
- Stops: (0.0, #110804) → (0.5, #C57033) → (1.0, #FFD79A)
- Lighting mode: PBRRealistic
- Material bands: [(0.0, 0.5, metallic 1.0, roughness 0.4), (0.5, 1.0, metallic 1.0, roughness 0.18)]
- Glow exp 6.0, scale 0.4

### Example 6 — Chrome Filament

- Kind: Pbr3D
- Stops: (0.0, #050505) → (1.0, #FFFFFF)
- Material bands: [(0.0, 1.0, metallic 1.0, roughness 0.05)]

### Example 7 — Domain Coloring Argument

- Kind: Gradient with HSV-cycling color stops (12 stops around the hue wheel)
- In-set: override #000000.

### Example 8 — Phosphor CRT

- Stops: (0.0, #000000) → (0.5, #003B00) → (1.0, #6CFFB1)
- Post-FX defaults: contrast +20, brightness -10.

### Example 9 — Bauhaus Primary

- Kind: Cycling
- Cycle Speed: 0.05
- Stops: (0.0, #E63946) → (0.5, #F1FA8C) → (1.0, #1D3557)

### Example 10 — Inferno-style

- Stops: (0.0, #000004) → (0.25, #420A68) → (0.5, #932667) → (0.75, #DD513A) → (1.0, #FCFFA4)

### Example 11 — Pastel Cotton Candy

- Stops: (0.0, #FFC4D6) → (0.5, #C4B5FF) → (1.0, #B8F0FF)
- Cycle Speed: 0.03

### Example 12 — Topo Map

- Kind: Cycling
- Cycle Speed: 0.08
- Stops: (0.0, #2E512E) → (0.4, #5A8C39) → (0.7, #C9B780) → (1.0, #FFFFFF)
- Repeats every ~12 iter units; reads like elevation rings.

### Example 13 — Iridescent Oil Slick

- Kind: Cycling
- Cycle Speed: 0.06
- Stops: 6 stops cycling through magenta → blue → green → yellow → red → magenta.

### Example 14 — Tron Grid

- Kind: Pbr3D
- Stops: (0.0, #00111A) → (0.5, #00B0E0) → (1.0, #80FFFF)
- Material bands: [(0.0, 1.0, metallic 0.6, roughness 0.25)]
- Glow exp 4.0, scale 1.2

### Example 15 — Vintage Sepia

- Stops: (0.0, #2A1505) → (0.6, #A07550) → (1.0, #F0E0C0)
- Post-FX defaults: contrast +5.

### Example 16 — Ice Cave

- Kind: Phong3D
- Cycle Speed: 0.025
- Stops: (0.0, #051F3B) → (0.5, #7EB5E0) → (1.0, #FFFFFF)
- Steepness 1.8, Ambient 0.15

### Example 17 — Volcanic Glass

- Kind: Pbr3D
- Stops: (0.0, #050505) → (0.7, #4B0010) → (1.0, #FF7050)
- Material bands: [(0.0, 0.7, metallic 0.0, roughness 0.6), (0.7, 1.0, metallic 0.0, roughness 0.05)]
- Glow exp 3.0, scale 0.8

### Example 18 — Watercolor

- Stops: (0.0, #F4E9DC) → (0.5, #B8D8E5) → (1.0, #D4A5A5)
- Post-FX defaults: brightness +10.

### Example 19 — Ultra-Violet

- Kind: Cycling
- Cycle Speed: 0.07
- Stops: 4 stops (#000033 → #6633CC → #CC33FF → #FFFFFF) repeated.
- Pair with Adaptive = 80 for psychedelic deep-zooms.

### Example 20 — Mono Ink

- Stops: (0.0, #FFFFFF) → (0.5, #777777) → (1.0, #000000)
- Cycle Speed: 0.04
- Useful for distance-estimation maps where shape, not color, is the goal.

---

## 16. Tips + Troubleshooting

**Live preview lagging.** Lower Cycle Speed temporarily, or untick Live preview and use Apply between edits.

**Banding visible at deep zoom.** Increase iteration count (Floating Menu → Iter), or raise Quality. The palette only colors what the calculator computes; bands appear when iter is too low for the depth.

**Phong3D looks plastic.** Drop Specular RGB and raise Roughness — actually, switch to Pbr3D and use the material band system. Phong's specular model lacks the energy conservation that makes PBR materials look ""real.""

**Pbr3D too dark.** Raise Ambient to 0.15–0.2, switch Lighting mode to PBRBright, or push the glow scale.

**In-set color washes out the rest.** Untick the override; the default opaque-black tends to read better. If you must override, pick a desaturated low-luma color.

**Save button silently overwrites.** As of v0.6.2, the overwrite confirmation prompt prevents accidental clobbering. If you're not seeing the prompt, the typed name doesn't match any existing user theme — Save proceeds as an insert.

**My theme disappears after reload.** Built-in themes don't write to `colorthemes.json`; only user-saved themes do. Use Save to Library, not Export JSON, for persistence.

**Image Palette returns washed-out colors.** Bias toward Edges or pick a more saturated source image. K-means clusters in CIELAB; pure-grey source images give a grey palette.

---

*Color Theme Editor Guide · Fracturing Fog · © 2026*
