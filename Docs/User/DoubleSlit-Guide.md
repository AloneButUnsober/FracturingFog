# Double-Slit Fractals

The double-slit experiment in one line: **amplitudes add, then square.** Light (or an electron) can reach
a point by two paths; each path brings a complex amplitude, and what you see is |a + b|². The bright and
dark fringes come from the cross term
([Feynman, Leighton & Sands, *Lectures* Vol. III ch. 1](../Resources-Bibliography.md#double-slit-fractals)).

Fracturing Fog turns that rule into three kinds of picture:

| Where | What it shows |
|---|---|
| **Julibrot Pair**, field *PathInterference* / *PathInterferencePhase* | Two escaping orbits as the two slits: their phases interfere. A which-path detector and an angular-momentum twist on top. |
| **Slit Cascade** (Double-Slit Diffraction) | A recursive aperture where every slit becomes smaller slits, each lit by the parent's fringes, and its exact diffraction pattern. The light shares set the fractal dimension; vortex twists set filaments. |
| **Talbot Carpet** (Quantum Carpet) | The light just past a grating: self-images, shifted copies and, at irrational distances, a fractal. |

The physics is established; the sources are in the
[bibliography](../Resources-Bibliography.md#double-slit-fractals) and the design notes are in
[Double-Slit Fractals R&D](../Technical/DoubleSlit-Fractal-RnD.md). What Fracturing Fog adds is the
combination: interference on escape-time orbits, a cascade weighted by its own fringes, and the angular-momentum
twist on both.

---

## 1. Path interference (Julibrot Pair)

Pick **Julibrot Pair**, set the field to **PathInterference** (or **PathInterferencePhase** for the phase).
Each escaping orbit is a path with phase G + 2πiθ (its escape potential and external angle); the two paths
interfere.

| Setting | What it does |
|---|---|
| **Field** | *PathInterference* (brightness), *PathInterferencePhase* (phase), or ***PathInterferenceDomain***: both at once, colour = phase on the colour-blind-safe Phase Wheel, brightness = intensity (ignores the theme). |
| **Interference κ** | How fast the phase turns with escape potential: more fringes as κ grows. Animatable. |
| **Angle damping γ** | Fringes fade where the two orbits' external angles differ. 0 = full fringes (default). |
| **Which-path η** | The detector. 0 = nobody watches, full fringes; 1 = fully watched, the fringes vanish. Intensity field only. Animatable. |
| **Angular momentum ℓ** | Each path twists by its own external angle, like light carrying orbital angular momentum. On the phase field the rings become ℓ-armed spirals; the sign sets the handedness. Whole numbers only. |
| **Winding direction** | *Same*: both paths twist the same way (spirals on the phase field). *Opposite*: the intensity fringes fan out as radial rays. |

Close to the set, below the critical level, the external angles have branch cuts. With ℓ other than 0 they show
as seams there; the rest of the plane is clean.

## 2. Slit Cascade

A slit is replaced by N smaller slits at scale r, again and again (**depth** levels). Each child slit lets
through the share of light that its parent's two-slit fringes put at its position, so a child on a dark fringe
drops out, with everything below it. The picture is the far-field diffraction pattern, computed exactly level by
level.

| Setting | What it does |
|---|---|
| **Children per slit** | Use 3 or more: a symmetric pair gets equal shares, a plain Cantor set. |
| **Layout** | *Line*: a row of slits (a grating; the pattern is stripes). *Ring*: slits round a circle. |
| **Scale ratio r**, **Depth** | The cascade's shape. Zooming the pattern out by 1/r shows the next level. |
| **Slit separation d**, **Slit width w**, **Wavelength λ** | The fringe that decides the shares. Animate λ and the slits slide on and off dark fringes. |
| **Show** | *Intensity* (log scale), *Phase*, *Aperture* (the slits, brighter where more light passes), *HolderAlpha*, *Spectrum* (§2.1), *Domain* (§2.2). |
| **Dynamic range** | How many powers of ten of brightness the colours span. |
| **Vortex charge ℓ**, **Charge per level** | §2.2. |

### 2.1 The probabilities set the dimension

The light shares are probabilities, and they fix the fractal dimension of the aperture exactly. The **status bar**
shows D0 (the slits' outline), D1 (information), D2 (correlation) and Dq for the **Dimension order q** you pick.
Change the wavelength and watch them move: with the defaults, D1 is about 1.05 at λ = 0.6 and climbs back toward
D0 ≈ 1.49 as λ grows and the shares even out. If neighbouring slits overlap, the status bar says so (the numbers
are then only an upper estimate).

*HolderAlpha* colours each point of the aperture by how concentrated the light is there; *Spectrum* colours it by
how many slits share that concentration. These views have only a handful of distinct values (with the default ring, Depth + 1 of them),
so the theme matters: the demo uses 40 iterations so the default HSV theme gives each class its own hue, and
**Cividis** or **Viridis** keep the classes apart by lightness on a blue-to-yellow scale (colour-blind safe).

### 2.2 Angular momentum sets the filament

On a **Ring**, **Vortex charge ℓ** gives each slit a phase twist from its angle. **Charge per level**:
*Constant* (the twists add), *Alternating* (opposite twists nest: a braid), *Ramp* (deeper levels twist harder).

- With equal light through every slit (a very large wavelength and width 0), each level puts a vortex on the
  centre and they stack into one **dark core** whose charge is the sum of the levels' charges.
- A ring of N slits can only carry charges from −N/2 to N/2; larger values wrap round (6 on a ring of 5 acts like 1).
- With unequal shares (the usual case) every vortex keeps its charge but drifts off centre, and deeper levels drift
  further: the vortices spread into a constellation.
- A row of slits has no angle, so the charge does nothing on *Line*.

The *Domain* view shows it best: colour = phase, brightness = intensity, so each vortex core is a dark point
with the whole colour wheel turning round it (ℓ times for charge ℓ). The *Phase* view shows the twist alone. For any phase view (this one, *PathInterferencePhase*, the Talbot
*Phase*) pick the **Phase Wheel (Blue-Amber)** theme: one full turn of colour per turn of phase at any
iteration count, using only lightness and blue-amber, so it reads the same with red/green colour blindness. On the shipped layouts the pattern is real along one axis, so the phase colour
wraps there in a straight line: that is the physics, not a glitch.

## 3. Talbot Carpet

Across the screen: position in grating periods. Down: distance travelled, in Talbot distances. The grating
images itself at every whole distance, shifted by half a period at every half, as a few shifted copies at simple
fractions, and as a fractal at irrational distances.

| Setting | What it does |
|---|---|
| **Grating** | *Ronchi* (bars, with an **Open fraction**), *DeltaComb* (infinitely thin slits, the sharpest carpet), *SlitCascade* (one period of the Slit Cascade's row of slits, using that type's settings). |
| **Fourier modes M** | Sharper grating edges, finer carpet. Values far above the screen resolution only alias. |
| **Show** | Intensity on a log scale about the average brightness, phase, or *Domain* (both: colour = phase, brightness = intensity). |
| **Dynamic range** | How far below the average brightness the dark end reaches. |

**Tip:** carpets look best with **few iterations (16)** and a **plain theme such as Greyscale**. Fracturing Fog's
themes repeat over the iteration scale, so the default 400 iterations paint the carpet many times over. The
Talbot presets already use 16 iterations.

---

## 4. Demos (built-in regions)

Open them from the region list; each says what it shows. Built-ins never change: save a copy to keep edits.

| Demo | Type | Bound animation |
|---|---|---|
| **Double Slit - Which-Path Collapse** | Julibrot Pair | *Double slit: which-path collapse*: η 0 → 1 → 0, the fringes fade and return |
| **Double Slit - Angular Momentum Spirals** | Julibrot Pair | *Double slit: angular momentum sweep*: ℓ −4 → 4, arm count and handedness change |
| **Double Slit - Opposite Winding Rays** | Julibrot Pair | — |
| **Slit Cascade - Ring Diffraction** | Slit Cascade | *Slit cascade: wavelength breathing*: λ 0.4 ↔ 1.4 |
| **Slit Cascade - Probability Sets Dimension** | Slit Cascade | — (watch the status bar) |
| **Slit Cascade - Vortex Core** | Slit Cascade | — |
| **Talbot Carpet - Ronchi Grating** | Talbot Carpet | — |
| **Talbot Carpet - Fractal Comb** | Talbot Carpet | — |
| **Talbot Carpet - Cascade Grating** | Talbot Carpet | — |

## 5. Headless commands

Every demo renders from the command line (Windows: `FracturingFog.exe`, or `.\FracturingFog` in a console):

```bash
FracturingFog --batch --region "Double Slit - Which-Path Collapse" --out which-path.png
```

```bash
FracturingFog --batch --region "Slit Cascade - Vortex Core" --out vortex-core.png
```

```bash
FracturingFog --batch --region "Talbot Carpet - Ronchi Grating" --theme Greyscale --out carpet.png
```

Play a bound animation as a video (the view holds still, the setting moves):

```bash
FracturingFog --batch --mode video --region "Double Slit - Which-Path Collapse" --animation "Double slit: which-path collapse" --video-motion hold --seconds 10 --out C:\out\
```

Any setting can be given with `--param Key=Value` (names as in the tables, e.g. `SlitCascadeWavelength`,
`DualOrbitWhichPath`, `TalbotModes`). Choices are given as numbers in list order, e.g. the Slit Cascade views are
Intensity 0, Phase 1, Aperture 2, HolderAlpha 3, Spectrum 4:

```bash
FracturingFog --batch --fractal SlitCascade --x 0 --y 0 --zoom 1.6 --param SlitCascadeView=4 --out spectrum.png
```

Brightness and phase in one image (the *Domain* view; views are numbered in list order, Domain = 5):

```bash
FracturingFog --batch --region "Slit Cascade - Vortex Core" --param SlitCascadeView=5 --out vortex-domain.png
```

A phase view with the colour-blind-safe phase wheel:

```bash
FracturingFog --batch --region "Slit Cascade - Vortex Core" --param SlitCascadeView=1 --theme "Phase Wheel (Blue-Amber)" --out vortex-phase.png
```

The Control Center **Command** builder writes the full command for whatever is on screen.

## 6. Credits

Double slit and which-path: Feynman, Leighton & Sands. Diffraction by fractal gratings: Allain & Cloitre; Bak,
Kim, Kim, Soh & Yee. Generalized dimensions and f(α): Hentschel & Procaccia; Halsey, Jensen, Kadanoff, Procaccia &
Shraiman. Orbital angular momentum: Allen, Beijersbergen, Spreeuw & Woerdman. Devil's vortex lens: Furlan,
Giménez, Calatayud & Monsoriu. Talbot effect and quantum carpets: Berry & Klein; Berry; Berry, Marzoli & Schleich.
Full entries: [bibliography](../Resources-Bibliography.md#double-slit-fractals).
