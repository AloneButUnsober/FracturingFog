# Volumetric Lighting Cookbook

Copy-paste recipes for the Fracturing Fog volumetric system. Each recipe lists
exact control values, explains *why* it works, and offers variations. For what
each control means, see the [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md).

> Companion pages: [User Index](_Index.md) · [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) · [User Bulb 3D Guide](UserBulb-Guide.md)

All controls are in **Lighting & FX** (Fractal Params → *Open Lighting & FX…*),
across the **Lights**, **Shadow**, and **Fog / Volumetric** expanders. Colors are
`AARRGGBB` hex; keep alpha `FF`.

**Contents**

- [The three rules of god rays](#the-three-rules-of-god-rays)
- [Tuning workflow](#tuning-workflow)
- Recipes:
  1. [Classic god rays (crepuscular shafts)](#1--classic-god-rays-crepuscular-shafts)
  2. [Soft depth haze](#2--soft-depth-haze-atmospheric-perspective)
  3. [Ground mist / valley fog](#3--ground-mist--valley-fog)
  4. [Static volumetric clouds](#4--static-volumetric-clouds)
  5. [Drifting clouds (animated)](#5--drifting-clouds-animated)
  6. [Cathedral shafts (warm, colored)](#6--cathedral-shafts-warm-colored)
  7. [Colored medium — amber haze & teal mist](#7--colored-medium--amber-haze--teal-mist)
  8. [Three-light stage lighting](#8--three-light-stage-lighting)
  9. [Back-scatter rim halo](#9--back-scatter-rim-halo)
  10. [Stormcloud with internal banding](#10--stormcloud-with-internal-god-ray-banding)
  11. [Underwater shafts + caustics](#11--underwater-shafts--caustics)
  12. [Nebula / cosmic volumetrics](#12--nebula--cosmic-volumetrics)
  13. [Psychedelic dream fog](#13--psychedelic-dream-fog)
  14. [Horror: single hard shaft](#14--horror-single-hard-shaft)
  15. [Sweeping searchlight (animated)](#15--sweeping-searchlight-animated)
- [Advanced combinations](#advanced-combinations)
- [Troubleshooting](#troubleshooting)

---

## The three rules of god rays

God rays (crepuscular rays, light shafts) are just fog that is bright where light
reaches it and dark where the fractal blocks the light. Three things must all be
true or you get flat haze instead of shafts:

1. **Fog to light up** — `Fog density > 0` and `Volume steps > 0`.
2. **Something to cast the shadow** — `Shadow steps > 0`. Without shadows nothing
   carves the dark gaps between shafts. *This is the #1 reason shafts don't
   appear.*
3. **A reason to glow toward the light** — `Anisotropy > 0`, and the **light
   positioned behind/beside the fractal** relative to the camera, so the fractal
   occludes it and the shafts fan out toward you.

Everything else (clouds, color, palette) is styling on top of those three.

> [!NOTE]
> **These recipes are tuned for the 3-D fractals** (Mandelbulb, Mandelbox, Quaternion,
> KIFS, …), whose geometry fills the frame at unit scale. On **Relief 3D** (2-D
> heightfield terrain) the same knobs work but the numbers differ — the fog is
> bounded to the terrain's height band and the air path is shorter, so you need
> higher `Fog density`, and shafts show against the sky only in the band just above
> the ridge. The default GPU relief path also uses the **key light only** for fog,
> and **Palette map** doesn't apply to relief yet. See the relief-specific caveats
> in the [Relief 3D Cookbook → Foggy valley with god-rays](Relief3D-Cookbook.md#6--foggy-valley-with-god-rays).

---

## Tuning workflow

Dial in this order — each step builds on the last:

1. **Density first.** Set `Volume steps` to 16 and raise `Fog density` until the
   depth reads right (~0.1–0.2 for most scenes). Ignore shafts for now.
2. **Add shafts.** Turn `Shadow steps` to 24. Reposition **Light 1** (θ/φ) so it
   sits behind the fractal from the camera's view — shafts appear in the gaps.
3. **Shape them.** Raise `Anisotropy` to 0.5–0.8 for forward punch; adjust
   `Softness k` for edge hardness.
4. **Color** (optional). Raise Light 2/3 intensity for colored fill, or set a
   `Fog color`, or turn up `Palette map`.
5. **Clouds** (optional). Raise `Volume noise`, set `Noise scale`, add
   `Self-shadow` for internal structure.
6. **Quality pass.** Raise `Volume steps` to 32–48 for the final render.

---

## 1 · Classic god rays (crepuscular shafts)

Bright white shafts fanning through gaps in the fractal — the signature look.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | **0.15** | | Volume steps | **28** |
| Height falloff | 0 | | Anisotropy | **0.7** |
| Shadow steps | **24** | | Softness k | 8 |
| Light 1 intensity | 1.2 | | Fog color | FFFFFFFF |

**Why:** density gives the fog something to light; shadow steps let the fractal
carve dark lanes; positive anisotropy concentrates the glow toward the light so
the lit lanes read as beams. **Position Light 1 behind the fractal** (try θ ≈ 3.9,
φ ≈ 1.2) and orbit the camera until the silhouette breaks the light into shafts.

**Variations:**
- Softer, dustier shafts: `Anisotropy` 0.4, `Softness k` 3.
- Laser-sharp shafts: `Anisotropy` 0.85, `Softness k` 24.
- Add floating dust: `Volume noise` 0.2, `Noise scale` 1.5.

---

## 2 · Soft depth haze (atmospheric perspective)

No shafts — just distance fading to atmosphere, so far parts of the fractal
recede. Cheap and subtle.

| Control | Value |
|---|---|
| Fog density | **0.08** |
| Volume steps | 0 *(flat exp-fog is enough)* |
| Shadow steps | 0 |
| Anisotropy | 0 |

**Why:** with `Volume steps = 0` you get pure Beer–Lambert extinction — distant
geometry blends toward the sky gradient with almost no cost. Set the **Sky top /
bottom colors** (Sky / Environment expander) to control the haze tint.

**Variations:**
- Warmer haze: sky bottom color to a warm grey (`FFB9A88C`).
- Add faint glow: `Volume steps` 12, `Anisotropy` 0.3 — barely-there shafts.

---

## 3 · Ground mist / valley fog

Fog pools low and clears overhead — mist clinging to the base of the fractal.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | **0.4** | | Height falloff | **2.0** |
| Volume steps | 24 | | Anisotropy | 0.5 |
| Shadow steps | 24 | | Volume noise | 0.3 |
| Noise scale | 0.6 | | Noise speed | 0.05 |

**Why:** `Height falloff` scales density by `exp(-2·y)`, so fog is dense near
`y = 0` and thin above. The gentle noise + slow drift keeps the mist alive.
Aim the light low (φ near 1.4–1.6, near the horizon) to rake shafts across the
mist.

**Variations:**
- Thicker soup at the base: `Fog density` 0.7, `Height falloff` 3.0.
- Higher fog line: lower `Height falloff` to 1.0.

---

## 4 · Static volumetric clouds

The fog breaks into cloud masses surrounding the fractal.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | **0.5** | | Volume noise | **0.85** |
| Volume steps | **32** | | Noise scale | **0.5** |
| Shadow steps | 24 | | Noise octaves | **5** |
| Anisotropy | 0.6 | | Noise speed | 0 |

**Why:** high `Volume noise` swings density between empty space and dense clumps;
low `Noise scale` makes big soft masses; 5 octaves adds wispy edges. Shadow steps
+ anisotropy still give shafts *between* the clouds.

**Variations:**
- Wispier: `Noise scale` 1.5, `Noise octaves` 6.
- Denser cumulus: `Fog density` 0.8, `Volume noise` 1.0.
- Add internal shadow depth: see [Recipe 10](#10--stormcloud-with-internal-god-ray-banding).

---

## 5 · Drifting clouds (animated)

Recipe 4, but the clouds move — ideal for video / slideshow.

| Control | Value |
|---|---|
| *(start from Recipe 4)* | |
| Noise speed | **0.3** |
| Light orbit speed | 0.1 *(optional — sweeps the shafts too)* |

**Why:** `Noise speed` advects the FBM lookup over scene time so the clouds roll.
Use the **Start/Stop** button next to the field to freeze on a good frame without
losing the rate. Pair with a slow **Light orbit** for shafts that sweep as the
clouds drift.

**Tip:** keep `Noise speed` small (0.05–0.4). Fast drift looks like boiling, not
weather.

---

## 6 · Cathedral shafts (warm, colored)

Warm, sacred window-light shafts — think sun through stained glass.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | 0.18 | | Volume steps | 32 |
| Shadow steps | **28** | | Anisotropy | **0.75** |
| Softness k | 12 | | Fog color | **FFFFE0B0** (warm) |
| Volume noise | 0.15 | | Noise scale | 1.2 |

**Why:** hard-ish shadows (high softness k) + strong forward anisotropy make
crisp, defined beams; the warm `Fog color` tints the whole medium golden even
under a white key light. A little noise adds drifting dust motes in the beams.

**Variations:**
- Cooler, moonlit chapel: `Fog color` `FFB8CCFF`, lower Light 1 intensity to 0.7.
- Dustier air: `Volume noise` 0.3, `Noise scale` 2.0.

---

## 7 · Colored medium — amber haze & teal mist

Fog with its own color, independent of the (white) lights. Two presets:

**Amber haze**

| Control | Value |
|---|---|
| Fog density | 0.2 |
| Volume steps | 24 |
| Shadow steps | 24 |
| Anisotropy | 0.5 |
| Fog color | **FFFFCC00** |

**Teal mist**

| Control | Value |
|---|---|
| Fog density | 0.25 |
| Volume steps | 24 |
| Shadow steps | 24 |
| Anisotropy | 0.4 |
| Fog color | **FF66CCFF** |

**Why:** `Fog color` multiplies the accumulated in-scatter, so the whole medium
takes that hue while the lights stay neutral. This is the cleanest way to set a
single fog mood. Compare with Recipe 8, where the *lights* (not the medium)
provide color.

---

## 8 · Three-light stage lighting

Colored fog from multiple directions — white key, cool blue fill, warm amber rim.
Uses the **pre-colored** Lights 2 and 3, so no color editing needed.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Light 1 intensity | 1.0 *(white key)* | | Light 2 intensity | **0.8** *(blue fill)* |
| Light 3 intensity | **0.6** *(amber rim)* | | Fog density | 0.2 |
| Volume steps | 32 | | Shadow steps | 24 |
| Anisotropy | 0.6 | | Fog color | FFFFFFFF |

**Why:** each light with intensity > 0 injects its **own color** into the fog.
Light 2 defaults to cool blue (`B0C8FF`), Light 3 to warm amber (`FFC890`), so the
fog picks up blue on one side and amber on the other — cinematic color contrast
with a neutral medium.

> Only **Light 1** casts shadow shafts by default; Lights 2/3 add colored *glow*
> without carved shafts. That's usually the desired look (one hero shaft, colored
> ambience). To give all three shafts, set `ShadowLightMask = 0x7` in a saved
> scene/preset.

**Variations:**
- Push the contrast: Light 2 intensity 1.2, Light 3 1.0.
- Custom light colors: set them in the Control Center's **Color & Light** section
  or a saved scene.

---

## 9 · Back-scatter rim halo

The glow appears when the light is *behind the camera* — a soft halo rimming the
fractal instead of shafts toward the light. Ethereal, backlit-cloud feel.

| Control | Value |
|---|---|
| Fog density | 0.3 |
| Volume steps | 28 |
| Shadow steps | 16 |
| Anisotropy | **−0.5** |
| Volume noise | 0.4 |

**Why:** negative anisotropy peaks the phase function *away* from the light, so
fog glows brightest where the light comes from behind you. Combine with clouds
for a "sun behind the storm" edge glow.

**Variations:**
- Stronger halo: `Anisotropy` −0.75.
- Mix forward + back by animating anisotropy across a scene keyframe track.

---

## 10 · Stormcloud with internal god-ray banding

Dense clouds that self-shadow, producing dark-to-light banding *inside* the cloud
body — the depth that flat clouds lack.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | **0.7** | | Volume noise | **1.0** |
| Volume steps | **40** | | Noise scale | 0.5 |
| Shadow steps | 24 | | Noise octaves | 5 |
| Anisotropy | 0.6 | | Self-shadow | **2.5** |
| Noise speed | 0.15 | | Self-shadow steps | **8** |

**Why:** `Self-shadow` marches each cloud sample toward the light through the FBM
density, darkening the far side of each clump — you see light bleed through the
near edge and fall off into the body. Costs extra, so add it last and keep
`Self-shadow steps` modest.

**Variations:**
- Backlit thunderhead: combine with `Anisotropy` −0.4 (Recipe 9).
- Lighter cumulus: `Fog density` 0.5, `Self-shadow` 1.5.

---

## 11 · Underwater shafts + caustics

Light shafts stabbing down through water, with rippling caustic highlights on
upward faces. Combines volumetrics with the caustics FX.

| Control | Value | Expander |
|---|---|---|
| Fog density | 0.3 | Fog / Volumetric |
| Volume steps | 32 | Fog / Volumetric |
| Height falloff | 0.5 | Fog / Volumetric |
| Anisotropy | 0.7 | Fog / Volumetric |
| Fog color | **FF2E6E7E** (deep teal) | Fog / Volumetric |
| Shadow steps | 24 | Shadow |
| Caustics strength | 0.8 | *(Reflection/Edge/… — caustics)* |
| Caustics speed | 0.4 | Lights |

**Why:** teal fog color + downward key light + forward anisotropy = classic
underwater shafts; the animated caustics add the dancing surface-refraction
highlights. Aim Light 1 near-vertical (φ small).

**Variations:**
- Murkier depths: `Fog density` 0.6, `Fog color` `FF1E4A55`.
- Add drifting particulate: `Volume noise` 0.3, `Noise speed` 0.1.

---

## 12 · Nebula / cosmic volumetrics

The fog takes the fractal's own color theme — glowing gas clouds in the palette
of the surface. This is the **palette map** (layer D) at work.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | **0.5** | | Volume noise | **0.9** |
| Volume steps | **36** | | Noise scale | 0.4 |
| Shadow steps | 16 | | Noise octaves | 6 |
| Anisotropy | 0.4 | | Palette map | **0.7** |
| Noise speed | 0.1 | | Fog color | FFFFFFFF |

**Why:** `Palette map` recolors the lit fog by the active 3D color theme's
gradient, keyed by fog depth — thick regions pull one end of the ramp, thin
regions the other. With cloudy noise this reads as a nebula whose colors match
the fractal. **Pick an expressive color theme first**; the fog inherits it.

**Variations:**
- Fog fully in the theme palette: `Palette map` 1.0.
- Blend palette with a base tint: `Palette map` 0.4 + a `Fog color`.

---

## 13 · Psychedelic dream fog

Maximum palette map — surreal, non-realistic color that binds fog and fractal
into one shifting palette.

| Control | Value |
|---|---|
| Fog density | 0.4 |
| Volume steps | 32 |
| Shadow steps | 12 |
| Anisotropy | 0.3 |
| Volume noise | 0.6 |
| Noise speed | 0.25 |
| Palette map | **1.0** |

**Why:** at full strength the fog is entirely theme-colored; animated noise makes
the palette swirl. Deliberately not physical — pair with a vivid, high-contrast
color theme for the strongest effect.

---

## 14 · Horror: single hard shaft

One stark, hard-edged beam in near-blackness — dread, interrogation-lamp energy.

| Control | Value | | Control | Value |
|---|---|---|---|---|
| Fog density | 0.25 | | Volume steps | 32 |
| Shadow steps | **32** | | Softness k | **40** (very hard) |
| Anisotropy | **0.85** | | Ambient | **0.03** (Lights) |
| Light 1 intensity | 1.5 | | Fog color | FFDDE6FF (cold) |

**Why:** high softness k + high anisotropy make one tight, sharp shaft; crushing
the **Ambient** to near-zero drops everything else into darkness so the single
beam dominates. Position Light 1 to rake across the fractal at a steep angle.

---

## 15 · Sweeping searchlight (animated)

A shaft that sweeps across the scene over time — great for video loops.

| Control | Value |
|---|---|
| Fog density | 0.2 |
| Volume steps | 28 |
| Shadow steps | 24 |
| Anisotropy | 0.75 |
| Light orbit speed | **0.4** |

**Why:** `Light orbit speed` rotates Light 1 around world-Y (Lights 2/3 follow at
0.7× and 1.3×, desynced), so the shafts swing through the frame. Use **Start/Stop**
to hold a frame. Keep the rate low for a stately sweep.

**Variations:**
- Multi-color sweep: raise Light 2/3 intensity (Recipe 8) so blue and amber
  shafts chase each other at different speeds.

---

## Advanced combinations

- **Volumetric + Relief 3D** — volumetrics work with the Relief 3D raymarch;
  fog wraps the raised relief for dramatic depth.
- **Color layers stack** — light color (Recipe 8) × fog color (Recipe 7) ×
  palette (Recipe 12) all compose. E.g. blue fill light + amber fog color +
  low palette map = a complex, film-graded atmosphere.
- **Scene Engine keyframes** — animate `Anisotropy` from −0.5 → +0.8 across a
  shot to swing from back-halo to forward shafts as the camera moves; or ramp
  `Palette map` 0 → 1 to dissolve realistic fog into a dream palette.
- **Slideshow** — volumetric settings are part of the saved scene, so they carry
  through slideshow cross-fades.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Fog is flat, no shafts | `Shadow steps = 0` | Raise Shadow steps to 24+. |
| Still no shafts | Light not occluded by the fractal | Move Light 1 (θ/φ) behind the fractal from the camera view. |
| Shafts too weak / even | `Anisotropy` near 0 | Raise to 0.5–0.8. |
| Whole image washed white | `Fog density` too high | Lower toward 0.1–0.3; check Light 1 intensity. |
| Clouds look like static/noise | `Noise scale` too high | Lower to 0.4–0.8 for soft masses. |
| Clouds "boil" in video | `Noise speed` too high | Lower to 0.05–0.3. |
| Colored fog looks muddy | Light color *and* fog color both strong | Pick one color source (lights *or* fog color), leave the other neutral. |
| Fog color ignored | Value is white `FFFFFFFF` | Set a non-white `Fog color`, or it's a ×1 no-op. |
| Palette map does nothing | Strength 0, or theme has a flat gradient | Raise Palette map; pick an expressive color theme. |
| No fog at all on UserBulb GPU | UserBulb GPU path skips volumetrics | Render UserBulb on the CPU (turn off *Use GPU render*). |
| Render is slow | High `Volume steps × Shadow steps` + self-shadow | Tune at 12–16 steps; rely on adaptive LOD; use GPU where supported. |

---

## See also

- [Volumetric Lighting Guide](Volumetric-Lighting-Guide.md) — what every control does + the pipeline.
- [Volumetric Color Plan](../Technical/Volumetric-Color-Plan.md) — design notes.
