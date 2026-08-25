<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<!-- SPDX-FileCopyrightText: 2026 Bradley Brown -->

# Lights — User Guide & Reference

Every 3D scene in Fracturing Fog is lit by **three lights** plus ambient fill and
the sky (IBL). Out of the box the three are simple **directional** lights — sun-like
beams that shine from a fixed direction with no falloff. This guide covers the
richer light *types* the lighting system also offers: **point**, **spot**, and
**area** lights, each of which changes how a surface is lit and how its shadows
fall.

> Companion pages: [User Index](_Index.md) · [Relief 3D Guide](Relief3D-Guide.md) · [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) · [User Bulb 3D Guide](UserBulb-Guide.md)

> [!NOTE]
> **"My scene looks exactly the same as before."** That is by design. Every light
> defaults to **directional**, and directional lighting is byte-for-byte identical
> to how the app has always rendered. Point / spot / area lights and their shadows
> only appear once you dial them up — you pay no cost for effects you don't use.

---

## 1. Where the controls live

1. Open the **Fractal Params** panel.
2. Under **Lighting & FX**, click **"Open Lighting & FX…"**. (The same dialog is
   reachable from the **Relief 3D** dialog.)
3. The lighting controls span a few expanders:
   - **Lights** — the three lights' direction (θ/φ), **Intensity** (0 = off), and
     **Colour**, plus **Ambient**.
   - **Light Types (Point / Spot)** — per-light **Type**, world **Position**,
     **Range**, spot **Cone**, and **Area soft** (the area-light control).
   - **Shadow** — **Shadow steps** (0 = shadows off), **softness**, and the
     per-light **shadow mask**.

### Which fractals are lit

Lights apply to every **3D renderer**:

- The **3D ray-marched fractals** — Mandelbulb, Mandelbox, Menger Sponge,
  Sierpinski, Quaternion Julia, Quaternion Mandelbrot, Kleinian, Bicomplex, and
  UserBulb.
- **2D escape-time fractals rendered through Relief 3D** (Mandelbrot, Julia,
  Burning Ship, Tricorn, …). Relief 3D is a lit heightfield, so a flat Mandelbrot
  region can cast real shadows once you enable it.

Flat 2D fractals rendered *without* Relief 3D are coloured by the palette alone and
ignore lights.

---

## 2. The three lights

| Light   | Default state                | Typical role |
|---------|------------------------------|--------------|
| Light 1 | **On** (intensity 1.0, white) | Key light — the main source |
| Light 2 | Off (cool blue-white)         | Fill — softens the shadow side |
| Light 3 | Off (warm amber)              | Rim / back light — separates subject from background |

Turn a light on by raising its **Intensity** above 0 (range 0–4). Each light has:

- **Direction (θ, φ)** — θ is the compass angle around the vertical axis, φ is the
  elevation (0 = straight up, 90° = horizon, 180° = straight down). For **spot**
  lights this same direction is the cone's aim.
- **Colour** — any colour. The key light is usually white; a cool fill + warm rim
  is the classic three-point look.
- **Intensity** — brightness multiplier. 0 switches the light off entirely.

---

## 3. Light types

Set a light's **Type** in the *Light Types (Point / Spot)* expander. The extra
controls (Position, Range, Cone) appear only for the types that use them.

### Directional (default)

A light infinitely far away — every surface is hit from the **same direction**,
with **no falloff**. This is the sun. It has no position; only its direction, colour,
and intensity matter. Directional lighting is the legacy default and renders
identically to older versions.

### Point

An omni-directional bulb at a **world position**. A surface is lit from the
direction toward the bulb, and brightness falls off with distance
(**inverse-square** — twice as far is a quarter as bright).

- **Position (X, Y, Z)** — where the bulb sits, in world units.
- **Range** — a soft cutoff distance. `0` = pure inverse-square (no cutoff);
  a positive value smoothly fades the light to nothing by that distance (handy to
  keep a bulb from bleeding across the whole scene).

### Spot

A point light restricted to a **cone**, like a stage spotlight. It has a position
and range (as above) plus a cone aimed along the light's **direction (θ, φ)**.

- **Cone inner (°)** — full intensity inside this half-angle.
- **Cone outer (°)** — zero intensity beyond it. The inner→outer band is the soft
  edge (penumbra) of the pool of light.

### Area (soft shadows)

Real lights have **size**, and a light with size casts a **soft-edged shadow** — a
sharp umbra that fades into a fuzzy penumbra. The **Area soft (°)** control gives
any light (directional, point, or spot) an *angular size*: how large the emitter
looks from the surface.

- **Area soft (°)** — the emitter's angular radius. `0` = a pinpoint source with a
  crisp shadow. Larger = a bigger, softer source with a wider penumbra. Reference
  points: the real sun's disc is about **0.25°**; a soft studio panel reads as
  roughly **5–15°**.

Area softness is a property of the **shadow**, so it only has a visible effect when
**shadows are on** (Shadow steps > 0). It caps how sharp a shadow can be: a shadow
can never be crisper than the light's size physically permits, so raising *Area
soft* only ever *softens*, never sharpens.

> [!NOTE]
> The area-light softness is computed analytically — it does **not** add render
> noise, so (unlike some renderers) you do not need to raise denoise settings to
> use it.

---

## 4. Shadows

Shadows are what make point / spot / area lights read as physical. In the
**Shadow** expander:

- **Shadow steps** — `0` = no shadows (fastest). ~24 is a good soft-shadow budget.
- **Softness** — the global shadow-edge sharpness. Higher = crisper. (An **Area
  soft** radius on a light overrides this toward *softer* for that light.)
- **Shadow mask** — which lights cast shadows. Default: the key light only (cheap);
  enable more for multi-shadow scenes.

Lights also drive **volumetric god-rays** when fog is on — see the
[Volumetric Lighting Guide](Volumetric-Lighting-Guide.md). Point / spot / area
lights light the fog with the same falloff and softness they apply to surfaces.

---

## 5. Animating lights

- **Light orbit speed** (in the animation controls) slowly orbits Light 1 around
  the scene; Lights 2 and 3 follow at 0.7× and 1.3× so the three desync into a
  choreographed sweep. `0` = static.

---

## 6. Batch / command-line

Every light property is reachable from the `--batch` command line. `N` is the light
number (1–3):

| Flag                        | Meaning |
|-----------------------------|---------|
| `--lightN-type T`           | `directional` \| `point` \| `spot` |
| `--lightN-intensity F`      | Brightness 0–4 (0 = off) |
| `--lightN-dir "theta,phi"`  | Aim in radians (directional / spot cone axis) |
| `--lightN-pos "x,y,z"`      | World position (point / spot) |
| `--lightN-range F`          | Soft cutoff 0–100 (0 = pure inverse-square) |
| `--lightN-cone "inner,outer"` | Spot cone half-angles in degrees |
| `--lightN-color "#RRGGBB"`  | Light colour (hex; `#AARRGGBB` also accepted) |
| `--lightN-area F`           | Area light: emitter angular radius 0–90° (0 = sharp) |

> [!IMPORTANT]
> Any `--lightN-*` flag implies **`--relief-raymarch`** — the lit 3D path — because
> that is where these lights render. For a 2D fractal, the flag turns on Relief 3D
> for you.

**Example — a warm spotlight with a soft edge and soft shadows:**

```bash
FracturingFog --batch --fractal Mandelbulb --zoom 1 \
  --light1-type spot --light1-intensity 2 \
  --light1-pos "0,4,0" --light1-dir "0,3.14" \
  --light1-cone "18,32" --light1-color "#FFD8A0" \
  --light1-area 6 \
  --out spotlight.png
```

---

## 7. Performance notes

- **Directional** lighting is free — byte-identical to a scene with no light types
  set.
- **Point / spot** lighting is a cheap per-sample calculation. On the **Relief 3D**
  path and the 8 GPU 3D-fractal renderers, directional scenes stay on the GPU;
  positional (point/spot) scenes are correct everywhere and run on the GPU for
  relief, on the CPU for the 3D-fractal families (a GPU upgrade for those is
  planned).
- **Area** softness needs shadows on. It currently renders on the CPU trace for the
  lit 3D paths (a GPU upgrade is planned); for still images and video this is
  transparent, only slower than a punctual light.

---

## 8. Troubleshooting

| Symptom | Fix |
|---------|-----|
| A point/spot light does nothing | Its **Intensity** is 0, or the **Position** is outside the scene / beyond **Range**. Raise intensity; move it closer; set Range to 0 to disable the cutoff. |
| Spot lights nothing | The cone is aimed away from the surface — the cone axis is the light's **direction (θ, φ)**, not its position. Re-aim θ/φ. |
| "Area soft" seems to do nothing | Shadows are off. Raise **Shadow steps** above 0 — area softness only affects the shadow penumbra. |
| Batch command renders flat / unlit | You used a `--lightN-*` flag on a 2D fractal — it turned on Relief 3D, which is what you want; check **Relief** height/quality if it looks wrong. |
