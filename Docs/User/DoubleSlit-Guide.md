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
| **Field** | *PathInterference* (brightness), *PathInterferencePhase* (phase), or ***PathInterferenceDomain***: both at once, colour = phase on the colour-blind-safe Phase Wheel, brightness = intensity (ignores the theme). ***PathInterferenceDetections***: the intensity recorded one particle at a time (§2.3). |
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
| **Show** | *Intensity* (log scale), *Phase*, *Aperture* (the slits, brighter where more light passes), *HolderAlpha*, *Spectrum* (§2.1), *Domain* (§2.2), *Detections* (§2.3). |
| **Dynamic range** | How many powers of ten of brightness the colours span. |
| **White light** | *Intensity* view only (§3.1): *TrueSpectrum* or *BlueAmber*. |
| **Vortex charge ℓ**, **Charge per level** | §2.2. |
| **Which-path η**, **Which-path levels** | §2.4: watch which slit, level by level. |

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

**Contour lines (#1598).** In any *Domain* view, **Contour lines** draws thin darker lines so the picture
reads without telling colours apart: *Phase* puts a line every 1/n of a turn of phase (**Phase lines per
turn**, default 8), *Modulus* a line wherever the amplitude halves or doubles, *Both* draws the two. At a
vortex of charge ℓ, n·|ℓ| phase lines meet, so you can read the charge by counting lines: the Vortex Core
preset has charge 3 at the centre, so 24 lines meet there at n = 8 (zoom in: the core itself is dark). The same setting is
on the Julibrot Pair *PathInterferenceDomain* field and the Talbot *Domain* view. It recolours without
recomputing. These are the contours of Wegert's "enhanced" phase portraits.

### 2.3 Particle build-up: the image is the probability

The *Detections* view (Slit Cascade and Talbot Carpet *Show*, Julibrot Pair field *PathInterferenceDetections*)
records the pattern the way the single-electron double-slit experiment does (Tonomura et al., 1989): particles
arrive one at a time, each lands on one spot, and the chance of landing on a pixel is proportional to the
intensity there. A few hundred dots look random; tens of thousands draw the fringes. The picture is the
probability, revealed by sampling. Grey dots on black, so it reads the same for every colour vision; it ignores
the colour theme.

| Setting | What it does |
|---|---|
| **Detections** | How many particles have landed (up to 50 million). Raising it only adds dots: the first 1,000 are the same whatever the total, so animating it shows the pattern building up. Animatable. |
| **Random seed** | Same seed, same dots, live and in exported images. |
| **Dot radius** | 0 = one pixel per detection; larger draws bigger dots (useful at high resolution). |
| **Exposure per dot** | Brightness of one detection; where dots pile up the brightness saturates like film (1 − e^(−exposure × dots)). Lower it for large counts. |

The screen is the detector: dots land only inside the current view, and pixels with no light (the Julibrot
Pair interior) never get one. Demo: **Slit Cascade - Particle Build-Up**, with its bound animation
*Slit cascade: particle build-up* (0 → 60,000 detections over 20 seconds).

### 2.4 Which-path, level by level

Watching which slit the light went through removes the interference between the slits (Feynman's "watching the
electrons"). In the cascade you can watch at some levels and not others. **Which-path η** sets how strongly the
chosen levels are watched: 0 = not at all (the default; the picture is unchanged), 1 = fully, so that level's
children add as intensities and *its* fringes wash out while every other level keeps its own. Between 0 and 1 the
fringes fade partway.

**Which-path levels** picks the levels: *Constant* (all), *Ramp* (deeper levels more, the deepest gets the full
η), *DeepOnly* (levels from **Cut level L** down), *ShallowOnly* (the levels above L), *Chosen* (a **Level mask**:
1 = level 0, 2 = level 1, 4 = level 2 and so on, added up). Level 0 is the outermost, widest-spaced set of slits:
it makes the finest fringes, and deeper levels make coarser ones. So *ShallowOnly* with L = 1 blurs the fine
ripples and leaves the broad pattern, and *DeepOnly* does the opposite. With η = 1 everywhere only the smooth
glow of a single smallest slit is left.

- The result is exact, and as fast as the plain view: each level's fringe factor |F|² becomes
  (1 − η)·|F|² + η.
- A partly watched light has no single phase. *Phase* and *Domain* show the phase of the part that still
  interferes: partly watched levels keep theirs, fully watched levels add none. So *Phase* only changes when a
  level reaches η = 1.
- The aperture views (*Aperture*, *HolderAlpha*, *Spectrum*) show the slits, which watching doesn't change.
- It works with white light and *Detections* too: the dots land on the washed-out pattern. η is animatable.

```bash
FracturingFog --batch --region "Slit Cascade - Ring Diffraction" --param SlitCascadeWhichPath=1 --param SlitCascadeWhichPathPattern=ShallowOnly --param SlitCascadeWhichPathLevel=1 --out ring-watched.png
```

## 3. Talbot Carpet

Across the screen: position in grating periods. Down: distance travelled, in Talbot distances. The grating
images itself at every whole distance, shifted by half a period at every half, as a few shifted copies at simple
fractions, and as a fractal at irrational distances.

| Setting | What it does |
|---|---|
| **Grating** | *Ronchi* (bars, with an **Open fraction**), *DeltaComb* (infinitely thin slits, the sharpest carpet), *SlitCascade* (one period of the Slit Cascade's row of slits, using that type's settings). |
| **Fourier modes M** | Sharper grating edges, finer carpet. Values far above the screen resolution only alias. |
| **Show** | Intensity on a log scale about the average brightness, phase, *Domain* (both: colour = phase, brightness = intensity), or *Detections* (particle build-up, §2.3). |
| **White light** | *Intensity* only: the carpet in white light, *TrueSpectrum* or *BlueAmber* (§3.1). |
| **Dynamic range** | How far below the average brightness the dark end reaches. |
| **Contour lines** | *Domain* only: phase and/or modulus lines (§2.2). The carpet's phase is itself a fractal, so at the full view the lines crowd into speckle; zoom in, or use fewer Fourier modes, to follow them. |

**Tip:** carpets look best with **few iterations (16)** and a **plain theme such as Greyscale**. Fracturing Fog's
themes repeat over the iteration scale, so the default 400 iterations paint the carpet many times over. The
Talbot presets already use 16 iterations.

---

### 3.1 White light

In white light each wavelength diffracts by a different amount, so the fringes come out coloured. Set **White
light** on the *Intensity* view of either type and Fracturing Fog sums the pattern over **Wavelengths** samples
from 380 to 720 nm (equal energy at each). The settings' wavelength (Slit Cascade **Wavelength**) and distance
(Talbot ζ) are taken to mean 550 nm; shorter wavelengths spread less, longer ones more:

- **Slit Cascade:** each wavelength re-weights the children (the fringe that sets the shares moves) *and* scales
  the far field, so colours separate away from the centre.
- **Talbot Carpet:** the Talbot distance z_T = 2d²/λ depends on the wavelength, so each colour images itself at a
  different depth. The grating plane (ζ = 0) is the same for every colour and stays white; the carpet below it
  breaks into colour.

Two colourings, same physics:

- **TrueSpectrum:** the real colours (CIE 1931 colour matching). Mostly a red↔green axis, so hard to read with
  red/green colour blindness.
- **BlueAmber:** how strongly coloured each point is, shown on the blue → amber axis of the Phase Wheel: light
  weighted toward short wavelengths is blue, toward long wavelengths amber. No red↔green, so it reads the same for
  red/green colour-blind viewers. A mix of red and violet (no overall lean) shows grey.

Brightness is the same log scale as the plain view (weighted by the eye's sensitivity), so **Wavelengths = 1** gives
exactly the monochrome picture. Cost grows with the number of wavelengths: keep it low while exploring and raise it
for stills. The colour theme is not used. The Slit Cascade centre is not white: with these settings shorter
wavelengths concentrate more light there (cyan-blue centre); that follows from the model, not a colour error.

```bash
FracturingFog --batch --region "Talbot Carpet - Ronchi Grating" --param TalbotWhiteLight=2 --param WhiteLightSamples=31 --out carpet-white.png
```

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
| **Slit Cascade - Particle Build-Up** | Slit Cascade | *Slit cascade: particle build-up*: 0 → 60,000 detections |
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

The particle build-up as a video. A video's first frame shows the region's own settings and the animation
takes over from the second, so start the count at 0 to match the ramp:

```bash
FracturingFog --batch --mode video --region "Slit Cascade - Particle Build-Up" --animation "Slit cascade: particle build-up" --param DetectionCount=0 --video-motion hold --seconds 20 --out C:\out\
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

The same with phase and modulus contour lines (Off = 0, Phase = 1, Modulus = 2, Both = 3). The Talbot and
Julibrot Pair settings are `TalbotDomainContours` / `TalbotContourCount` and
`DualOrbitInterferenceContours` / `DualOrbitInterferenceContourCount`:

```bash
FracturingFog --batch --region "Slit Cascade - Vortex Core" --param SlitCascadeView=5 --param SlitCascadeDomainContours=3 --param SlitCascadeContourCount=8 --out vortex-contours.png
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
Giménez, Calatayud & Monsoriu. Talbot effect and quantum carpets: Berry & Klein; Berry; Berry, Marzoli & Schleich. Single-particle build-up:
Tonomura, Endo, Matsuda, Kawasaki & Ezawa. Phase-portrait contours: Wegert. White light: CIE colour-matching fit
by Wyman, Sloan & Shirley; Oklab by Ottosson; polychromatic self-imaging by Packross, Eschbach & Bryngdahl.
Full entries: [bibliography](../Resources-Bibliography.md#double-slit-fractals).
