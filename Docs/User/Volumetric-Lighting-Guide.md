# Volumetric Lighting — User Guide & Reference

Fracturing Fog can fill the space *around* a 3D fractal with light: hazy depth,
drifting clouds, and the bright "god-ray" shafts that fan out when a light is
partly blocked by the fractal. This is **volumetric lighting** — light
scattering off a participating medium (fog) between the camera and the surface,
rather than only off the surface itself.

This guide explains what every control does and how the system works. For
ready-made settings you can copy, see the companion
[Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md).

> Companion pages: [User Index](_Index.md) · [Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md) · [User Bulb 3D Guide](UserBulb-Guide.md) · [Volumetric Color Plan (Technical)](../Technical/Volumetric-Color-Plan.md)

> [!NOTE]
> **"How do I make god rays?"** Turn on **Volume steps** (~24), a little
> **Fog density** (~0.15), make sure **Shadow steps** is on (~24), and push
> **Anisotropy** positive (~0.6) with the key light *behind* the fractal. Full
> walk-through in the cookbook's [God Rays recipe](Volumetric-Lighting-Cookbook.md#1--classic-god-rays-crepuscular-shafts).

---

## 1. Where the controls live

1. Open the **Fractal Params** panel.
2. Under **Lighting & FX**, click **"Open Lighting & FX…"**. (The same dialog is
   reachable from the **Relief 3D** dialog.)
3. Volumetric controls are split across three expanders:
   - **Lights** — the three directional lights (Light 1/2/3) and ambient. Lights
     are what the fog scatters, so their direction, intensity, and color set the
     look of every shaft.
   - **Shadow** — soft-shadow steps. **Required for shafts** (see below).
   - **Fog / Volumetric** — the fog medium itself: density, the in-scatter march,
     cloud noise, phase, medium color, and palette mapping.

Every knob defaults to a value that does **nothing** (fog off), so a fresh scene
looks exactly as it did before you opened the dialog. You only pay render cost
for effects you dial up.

### Which fractals support it

Volumetric lighting applies to:

- The **3D ray-marched fractals** — Mandelbulb, Mandelbox, Menger Sponge,
  Sierpinski, Quaternion Julia, Quaternion Mandelbrot, Kleinian, Bicomplex,
  and UserBulb.
- **2D escape-time fractals rendered through Relief 3D** (Mandelbrot, Julia,
  Burning Ship, Tricorn, and the other zoomable-2D families). Relief 3D is a
  lit heightfield renderer and shares the same VL in-scatter walk, so a
  Mandelbrot region **can** get god-rays. See the
  [Relief 3D Cookbook](Relief3D-Cookbook.md) for the tuning differences
  between 3D-fractal VL and relief VL.

Volumetric lighting does **not** apply to 2D escape-time fractals rendered
*without* Relief 3D (the flat 2D view) — those use the color theme alone.

---

## 2. How it works (the short version)

When fog is on, each pixel's ray does a second, cheaper march *through the fog*
from the camera to the surface it hit. At each step it asks two questions:

1. **How thick is the fog here?** (`Fog density`, shaped by `Height falloff` and
   `Volume noise`.) Thicker fog dims what's behind it — this is the
   Beer–Lambert *extinction* that makes distant geometry fade.
2. **How much light reaches this point in the fog, and from where?** For every
   light that is on, the march checks whether the fractal blocks the light
   (soft-shadow), applies the **phase function** (are we looking *toward* the
   light?), and adds that light's color into the fog. This is *in-scatter* — the
   glow of lit fog, and the mechanism behind god rays.

The lit fog is then composited over the surface, tinted by the **medium color**
and optionally recolored by the active **theme palette**.

That's the whole model: **extinction** (fog hides things) + **in-scatter** (fog
glows where light reaches it). Everything in the Fog / Volumetric expander is a
handle on one of those two.

For the precise math, see [§5 Technical Reference](#5-technical-reference) and the
[Volumetric Color Plan](../Technical/Volumetric-Color-Plan.md).

---

## 3. Control reference — Fog / Volumetric

Values below list the **UI range** and **default**. "Bit-identical default"
means: at that value the control changes nothing, and the render is pixel-for-
pixel what it was with the feature off.

> **Half of these controls only apply when Volume steps > 0.** With
> **Volume steps = 0** the fog uses a cheap flat-exponential fallback that
> honours only **Fog density** and the sky gradient's top/bottom colors.
> In that mode **Fog color**, **Anisotropy**, **Height falloff**, **Palette
> map**, Light 2/3 in-scatter, and non-Gradient **Sky mode** are inert — the
> slider moves but nothing changes. Turn **Volume steps** up (16+) to bring
> them to life. See Recipe 2 in the [Cookbook](Volumetric-Lighting-Cookbook.md)
> for the intended flat-fog use. Rows below marked *(needs Volume steps > 0)*
> are the affected ones.

| Control | Range | Default | What it does |
|---|---|---|---|
| **Fog density** | 0 – 2 | 0 | Master switch for fog. Beer–Lambert extinction per unit of ray distance. 0 = no fog. ~0.05 = faint haze; ~0.2 = obvious atmosphere; ~0.6+ = pea soup. Also the base density for in-scatter. |
| **Height falloff** | 0 – 4 | 0 | *(needs Volume steps > 0)* Fog thins with world height: `density × exp(-coef · y)`. 0 = uniform fog everywhere; higher = fog pools near the "ground" and clears overhead (ground mist, valley fog). |
| **Volume steps** | 0 – 64 | 0 | Number of in-scatter samples along each ray. **0 = flat exponential fog only (no shafts, no glow).** Turn this up to enable volumetric light. 16 is a usable minimum; 24–32 is the sweet spot; 48+ for hero stills. Cost scales with this × shadow steps. |
| **Volume noise** | 0 – 1 | 0 | FBM cloud modulation of density. 0 = perfectly smooth medium; toward 1 the fog breaks into cloud-like clumps and wisps. This is what turns "haze" into "clouds". |
| **Noise scale** | 0.01 – 100 | 1.0 | Cloud frequency. Low (0.2–0.5) = big soft fluffy masses; high (2–5) = fine turbulent detail. |
| **Noise speed** | −10 – 10 | 0 | Cloud drift rate. 0 = frozen. Non-zero animates the clouds (advected along a fixed vector). The **Start/Stop** button pauses without losing the rate. |
| **Noise octaves** | 1 – 6 | 3 | Layers of detail in the cloud noise. More octaves = finer wisps at more render cost. 3 is a good default; 5–6 for dramatic storm detail. |
| **Self-shadow** | 0 – 4 | 0 | Clouds cast shadows *on themselves*. 0 = evenly lit clouds; higher = dense clouds darken internally, giving god-ray banding *inside* the cloud body. Only meaningful when **Volume noise > 0**. |
| **Self-shadow steps** | 0 – 16 | 4 | Samples for the cloud self-shadow march. Higher = smoother internal shadowing, more cost. |
| **Anisotropy** | −1 – 1 | 0 | *(needs Volume steps > 0)* **Henyey-Greenstein phase.** The directional "punch" of the fog. 0 = even glow from every angle. **Positive (0.4–0.85) = forward scatter**: a bright halo when you look toward the light — the classic god-ray look. Negative = back-scatter: the halo appears when the light is *behind you*. |
| **Fog color** | AARRGGBB hex | FFFFFFFF (white) | *(needs Volume steps > 0)* The medium's own tint (scattering albedo), independent of the lights. White = no tint. `FFFFCC00` = amber haze, `FF66CCFF` = teal mist, `FF88FF88` = eerie green. Multiplies the accumulated in-scatter. |
| **Palette map** | 0 – 1 | 0 | *(needs Volume steps > 0)* Cross-fades the lit fog toward the **active 3D color theme's gradient**, keyed by fog depth. 0 = physically-based (fog is colored by the lights). 1 = the fog takes the same palette as the fractal surface. A stylised / non-realistic effect (see [§4](#4-the-four-color-layers)). |

### Related controls in other expanders

These live outside Fog / Volumetric but directly shape the volumetric look:

| Control | Expander | Why it matters for fog |
|---|---|---|
| **Shadow steps** | Shadow | **Required for shafts.** With 0, the fog glows evenly and there are no god rays — nothing carves the shadow. 24–32 gives crisp shafts. This is the single most common reason "my god rays don't show up". |
| **Softness k** | Shadow | Edge hardness of the shafts. Higher = sharper shaft edges; low/0 = soft, diffuse shafts. |
| **Light 1/2/3 θ, φ, intensity** | Lights | The lights the fog scatters. Direction (θ azimuth, φ elevation) decides where shafts point; intensity decides how bright the fog glows. See [§3.1](#31-lights-and-fog-color). |
| **Light orbit speed** | Lights | Animates Light 1 around the scene (Lights 2/3 follow, desynced). Sweeps the shafts across the frame over time. |
| **Sky top / bottom color** | Sky / Environment | The fog fades distant geometry toward the **sky gradient**, so the sky colors also tint the fog's far haze. |

### 3.1 Lights and fog color

The fog scatters **every light that is on** (intensity > 0), each in its own
color. The three lights ship pre-colored so multi-color fog works immediately:

- **Light 1** — white key light, intensity 1.0. Your main shaft source.
- **Light 2** — cool blue fill (`B0C8FF`), intensity **0** by default. Raise its
  intensity to add a cool blue glow from the opposite side.
- **Light 3** — warm amber rim (`FFC890`), intensity **0** by default. Raise it
  for a warm counter-glow.

So the fastest way to colored fog is simply **raising Light 2 and/or Light 3
intensity** — no color editing needed. To change the light *colors* themselves,
use the **Color & Light** section of the Control Center or a saved scene/preset;
the Color Theme Editor's eyedropper can also match a light to a sampled pixel.

> [!NOTE]
> **Shafts vs. glow.** Only *shadow-casting* lights carve visible shafts. By
> default the shadow mask includes **Light 1 only**, so Lights 2/3 add colored
> *glow* to the fog but do not cut their own god-ray shafts. Enabling shafts for
> Lights 2/3 requires setting the shadow light mask to include them
> (`ShadowLightMask = 0x7` in a saved scene/preset — not exposed as a dialog
> slider). For most scenes, one shaft-caster (the key light) plus colored fill
> glow is exactly what you want.

### 3.2 Aiming Light 1 (θ / φ) for shafts

The two light angles in the **Lights** expander are the raw spherical direction
*toward* the light — not "compass + height above horizon", so they read a little
unusually. Both are in **radians**.

- **L1 θ (azim)** — the compass direction (which way around the up-axis). Spinner,
  range −10 … 10.
- **L1 φ (elev)** — despite the label, this is the angle measured **down from
  straight up**, so **larger φ = lower light**. Slider, 0.01 … 3.13 (≈ 0 … π).

The direction the app builds from them is:

```
dir_toward_light = ( sinφ·cosθ ,  cosφ ,  sinφ·sinθ )
```

**φ — how low.** `cosφ` is the light's height:

| φ | Light sits… |
|---|---|
| ~0.3 | high overhead (flat, top-lit — no shafts) |
| ~1.40 (default) | fairly low |
| **1.45 – 1.55** | **just above the horizon — the god-ray sweet spot** |
| 1.571 (π/2) | dead on the horizon (`dir.y = 0`) |
| > 1.6 | below the horizon (underground — avoid) |

**Aim it low: set φ ≈ 1.5.**

**θ — behind the subject.** Shafts appear when the fractal (or relief ridge)
sits **between the camera and the light**, so the silhouette chops the light into
beams. That means the light must be on the **far side from the camera**. For a
3-D-fractal scene, orbit the camera (or nudge θ) until the light hides behind the
form. For **Relief 3D**, the camera azimuth is a known number (degrees, in the
Relief 3D panel), so you can compute θ directly — put the light 180° opposite:

```
L1 θ  ≈  (CameraAzimuthDeg × π / 180)  +  π
```

*Example* — camera azimuth 25°:  θ ≈ 25 × 0.0175 + 3.14 ≈ **3.58**
(θ ≈ −2.70 is the same direction; either is fine).

**Then nudge θ by ~0.3** either way while watching the frame — the shafts pop
when the silhouette lands between camera and light. If the whole scene just goes
flat-bright, the light is on the *camera's* side (in front); add or subtract π to
flip it behind.

| Control | Where | Value |
|---|---|---|
| L1 φ (elev) | Lights | **1.5** (low, near horizon) |
| L1 θ (azim) | Lights | far side of the subject — Relief: `camAz°·0.0175 + 3.14` |
| L1 intensity | Lights | ≥ 1.0 |
| Anisotropy | Fog / Volumetric | 0.7 – 0.85 (forward punch) |

> [!NOTE]
> Because the default shadow mask is **Light 1 only**, Light 1 is the light worth
> aiming — it is the one that carves shafts. On **Relief 3D under GPU raymarch**
> (the default) the fog scatters the key light *exclusively*, so aiming Light 1 is
> the whole job there.

---

## 4. The four color layers

Volumetric color is built from four independent layers that **compose** — you can
use any combination:

1. **Light color (A)** — every on light tints the fog it reaches. Physically
   based. *"Blue light makes blue fog."*
2. **Phase / anisotropy (B)** — how the glow concentrates toward/away from the
   light. Physically based. *Shapes* the color into shafts and halos.
3. **Medium color / Fog color (C)** — the fog's own tint, independent of lights.
   Physically based. *"Amber haze even under a white light."*
4. **Palette map (D)** — recolor the fog by the fractal's own color theme
   gradient. **Not** physical — a deliberate stylised look that ties fog and
   surface into one palette (nebulae, psychedelic, dreamlike).

Layers A–C match how standard renderers (Unreal, Unity HDRP, Frostbite) color
fog. Layer D is Fracturing Fog's own artistic extension. All four work on both
the CPU and GPU render paths (see [§6](#6-cpu-vs-gpu)).

---

## 5. Technical reference

For anyone reading the code or tuning precisely. The pipeline is
single-scattering Beer–Lambert in-scatter, implemented in
`Engine/Rendering/Lighting/ShadingPipeline.cs`
(`VolumetricInScatter` / `AddVolumeScatter`) on the CPU, mirrored in the eight
per-fractal GPU kernels under `Engine/Calculators/Gpu/`.

### 5.1 The march

For each surface pixel, reconstruct the camera origin and march `VolumeSteps`
samples from camera to surface. At sample *s* (mid-point of its slab):

```
density = FogDensity
        × exp(-FogHeightFalloff · y)          // height falloff
        × VolumetricDensityMul(pos)           // FBM cloud noise, =1 when off

for each light i with Intensity > 0:
    shadow  = SoftShadow(pos → light_i)       // if that light is shadow-masked
            × CloudSelfShadow(pos → light_i)  // FBM self-shadow, =1 when off
    scatter = density · shadow · Intensity_i · stepSize · phase(cosθ_i, g)
    inScatter_rgb += transmittance · scatter · lightColor_i

transmittance *= exp(-density · stepSize)     // Beer–Lambert extinction
```

After the walk:

```
inScatter_rgb *= FogColor / 255               // medium color (C)
inScatter_rgb  = paletteRemap(inScatter_rgb)  // palette map (D), if strength > 0
finalColor = surface · transmittance + inScatter_rgb
```

### 5.2 The phase function (Anisotropy)

Henyey-Greenstein, normalized so `g = 0` evaluates to exactly 1 (isotropic, the
bit-identical default):

```
p(cosθ) = (1 − g²) / (1 + g² − 2g·cosθ)^1.5     with cosθ = dot(viewDir, lightDir_i)
```

`g = VolumeAnisotropy`, clamped internally to ±0.99 to avoid the forward
singularity. `g > 0` peaks when the view ray points at the light (forward
god-rays); `g < 0` peaks looking away (back-scatter halo).

### 5.3 Palette map (D)

Keyed by **optical depth** `u = 1 − transmittance` (thicker fog samples deeper
into the ramp). The remap is **energy-preserving**: it keeps the in-scatter's
own brightness and redistributes it across the palette hue, then cross-fades by
`VolumePaletteStrength`. The gradient LUT is baked once per frame from the active
`IColorMap` theme (`VolumePaletteBaker`); on the GPU it is uploaded as a separate
buffer. Strength 0 (or no LUT) is a no-op.

### 5.4 Adaptive LOD

`VolumeStepsFalloff` (default 0.5, not a Fog/Volumetric slider — set via preset)
shrinks the per-pixel step count past 4 world units of depth:
`steps / (1 + (T − 4) · k)`, floored at 4. It speeds up deep-depth volumetric
scenes with little visible change. 0 disables it.

### 5.5 Bit-identity guarantee

Every volumetric knob has a pass-through default (fog density 0, anisotropy 0,
fog color white, palette strength 0, Lights 2/3 intensity 0). At defaults the
render is byte-for-byte identical to a scene with no volumetrics — so turning the
system on is always an explicit, reversible choice.

---

## 6. CPU vs GPU

Volumetric lighting runs on both paths, but feature parity depends on **which**
GPU path is engaged — there are two, and they are not the same:

- **CPU** (default) — full support for all VL effects on every 3D fractal listed
  in §1, **and** on Relief 3D of 2D fractals (see the
  [Relief 3D Cookbook](Relief3D-Cookbook.md) for relief-specific tuning).

- **GPU: 3D-fractal kernels** (`Use GPU render` on Mandelbulb, Mandelbox,
  Menger, Sierpinski, Quaternion Julia/Mandelbrot, Kleinian, Bicomplex) — full
  volumetric parity with the CPU path: light color, phase, medium color,
  palette map. **UserBulb's GPU path is cheap-shaded and skips volumetrics** —
  for volumetric UserBulb, render on the CPU.

- **GPU: Relief 3D raymarch** (the default on Relief 3D scenes) — **subset**
  parity, not full: **Light 1 only** contributes to the fog / god-rays (Lights
  2/3 still shade the surface), and **no palette-mapped fog**. To get the full
  VL feature set on a Relief 3D scene, disable the GPU relief path
  (**Ctrl+Shift+G**) and render on the CPU.

The 3D-fractal GPU and CPU paths are built to match visually; when in doubt for
a final render, compare a still on both.

---

## 7. Performance

In-scatter cost is roughly **`VolumeSteps × ShadowSteps` distance-field
evaluations per pixel**, plus cloud-noise evaluations when noise/self-shadow are
on. Tips:

- Tune the look at **low Volume steps (12–16)**, then raise for the final render.
- **Adaptive LOD** (§5.4) reclaims most of the cost on deep scenes for free.
- Cloud **self-shadow** multiplies cost by its step count — add it last.
- Keep shafts to the **key light** (default) rather than shadow-masking all three.
- The GPU path is dramatically faster for heavy volumetrics on the supported
  fractals.

---

## 8. Accessibility note

If you distinguish colors by hue with difficulty (e.g. red/green color
vision deficiency), lean on the effects that don't rely on hue contrast:
**anisotropy** (shaft shape), **density** and **height falloff** (brightness and
placement), and **value** contrast between fog and surface. When you do use
colored fog, prefer blue↔amber pairs (the built-in Light 2/3 defaults) over
red↔green, and pick a yellow such as `FFFFCC00` where you'd otherwise use red.

---

## See also

- [Volumetric Lighting Cookbook](Volumetric-Lighting-Cookbook.md) — copy-paste recipes.
- [Volumetric Color Plan](../Technical/Volumetric-Color-Plan.md) — design/dev notes and the A–E slice history.
- [Lighting + FX Roadmap](../Lighting-FX-Roadmap.md) — the broader lighting roadmap.
