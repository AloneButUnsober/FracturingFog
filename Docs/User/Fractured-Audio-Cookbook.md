# Fractured Audio Cookbook

Recipes for making a fractal move with music, organised by what you want to
see. Each recipe gives the knob, the signal, the mode, the curve, the range and
the smoothing, plus what to expect. For what each control means, see the
[Avalonia User Guide → Audio drive](Avalonia-UserGuide.md); for the beat-driven
slideshow (a different feature), see the
[Slideshow Audio-Reactive Guide](Slideshow-AudioReactive-Guide.md).

> Companion pages: [User Index](_Index.md) · [Avalonia User Guide](Avalonia-UserGuide.md) · [Double-Slit Guide](DoubleSlit-Guide.md) · design notes: [Audio-Reactive Expansion](../Technical/AudioReactive-Expansion.md)

**Where the controls are.** Pick the audio source (system loopback, a file,
the microphone or the Fractal Synth) in **Audio Settings**. The binding rows
are in two places that edit the same bindings: the **Fractured Waves**
expander of the Fractal Params panel, and *Drive Fractal Parameters with
Audio* in Audio Settings. The **Recipes** picker above the rows applies a whole
recipe in one click; recipes marked ★ below ship as built-ins (#1434).

**Contents**

1. [Signal character](#1-signal-character)
2. [Shaping primer](#2-shaping-primer)
3. [Recipes by outcome](#3-recipes-by-outcome)
4. [One recipe per family](#4-one-recipe-per-family)
5. [Pitfalls](#5-pitfalls)
6. [Headless (batch video)](#6-headless-batch-video)

---

## 1. Signal character

Every signal is normalised to 0..1 before shaping.

| Signal | What it is | Feels like | Good for |
|---|---|---|---|
| **Bass** | 20-150 Hz band level (kick, sub) | Slow, heavy swell | Size, power, warp depth |
| **LowMid** | 150-400 Hz | Body of the mix; warmer, busier than Bass | Gentle breathing |
| **Mid** | 400-1500 Hz | Busy; follows the melody and vocals | Colour or detail that "talks" |
| **HighMid** | 1500-4000 Hz | Busy and bright; snares, attack of notes | Shimmer, texture |
| **High** | 4000-12000 Hz | Sparkle: hats, cymbals, air | Fine flicker, small offsets |
| **Rms** | Overall loudness | Smooth; tracks the song's energy | Speed in Rate mode, "the louder the more" |
| **BeatPulse** | Jumps on every detected beat, then decays (1/e after 0.18 s) | A punch on each beat | Hits, flashes, nudges |
| **DownbeatPulse** | Same envelope, once every 4 detected beats | One accent per bar | Big moves, section accents |
| **BpmPhaseSaw** | Ramps 0 → 1 once per beat, locked to the tempo | A steady tick | Wrapping targets only (angles, Acid Fog flow) |
| **BpmPhaseSine** | 0.5 + 0.5·sin of the same phase: one smooth cycle per beat | Smooth, tempo-locked breathing | Back-and-forth between two values |

Two things worth knowing:

- **Pulses often peak below 1.** The pulse height is the strength of the
  detected onset (0..1), so on much music it stays under the top of the range. Raise
  **Gain** (1.5-3) when a beat recipe should hit the full range.
- **"Downbeat" means every fourth beat.** It assumes 4/4 and counts from
  whenever detection started, so it may not land on the bar's first beat.

## 2. Shaping primer

The pipeline per binding is: signal → **Curve** → **Invert** → **Gain** /
**Bias** → clamp 0..1 → **Attack** / **Release** smoothing → **Min..Max**.

| Control | What it does | Use it when |
|---|---|---|
| **Curve: Linear** | Passes the signal through | Default |
| **Curve: Exp** (x²) | Suppresses quiet parts, keeps peaks | Only loud moments should move it |
| **Curve: Log** (√x) | Lifts quiet detail | Quiet music should still move it |
| **Curve: Smoothstep** | Ease-in-out, no hard corners | Swells that should start and land softly |
| **Gain** | Scales the shaped signal | Pulses or bands that never reach the top |
| **Bias** | Adds a floor | The knob should never fully drop |
| **Invert** | 1 − signal | Duck on the beat; stillness when loud |
| **Mode: Position** | The drive picks the value between Min and Max | The knob follows the music and falls back |
| **Mode: Rate** | The drive picks a speed, Min/s..Max/s, added up over time | Endless motion that never jumps back |
| **Edge: Clamp / Wrap / PingPong** (Rate only) | Stop at the end / jump to the other end / turn around | Wrap on looping knobs, PingPong on others |
| **Attack / Release** (s) | Time for the drive to rise / fall (0 = instant) | Short attack + longer release turns jitter into swells |

Smoothing works on the drive *after* Invert. On an inverted binding the beat
makes the drive **fall**, so **Release** sets how fast the duck hits and
**Attack** how fast it recovers.

## 3. Recipes by outcome

Ranges are starting points; tune them with the music you use.

### 3.1 Acid Fog: endless flow pushed by the music ★

*Acid Fog - Endless flow.* `AcidWarpFlow`, **Rate**, Edge **Wrap**, **Rms**,
Min/s 0.05, Max/s 1.5, Release 0.8. The patterns morph into each other
non-stop; louder passages flow faster, quiet ones still drift. Needs **Morph**
on: the recipe turns it on, and so does enabling the row in the Fractured
Waves expander.

### 3.2 Acid Fog: a nudge forward on every beat ★

*Acid Fog - Beat nudge.* `AcidWarpFlow`, **Rate**, Edge **Wrap**,
**BeatPulse**, 0..3 patterns/s. Each beat pushes the flow on and it eases off
before the next one. Raise Gain if the beats are soft.

### 3.3 Breathe between two patterns ★

*Acid Fog - Breathe between patterns.* `AcidWarpFlow`, **Position**,
**BpmPhaseSine**, k..k+1 (the built-in uses 0..1). Rocks between pattern k and
the next once per beat.

### 3.4 Bass-swelled domain warp ★

*Bass-swelled domain warp.* `DomainWarpStrength`, **Position**, **Bass**,
0..0.4, **Smoothstep**, Attack 0.05, Release 0.4. The image swirls with the
kick and settles. Needs the **Domain warp** toggle on (the recipe sets it), and
only works at shallow zoom.

### 3.5 Kick punch

`BulbPower` (Mandelbulb), **Position**, **BeatPulse**, 7..9, Gain 2,
Release 0.15. The bulb's lobes jump on the kick and snap back.
Gentler: ★ *Bulb breathes with the bass* (Bass, Smoothstep, attack 0.05 /
release 0.5).

### 3.6 Downbeat accent ★

*Downbeat slice.* The 4D slice knobs (`QJuliaSliceW`, `QMandelSliceW`,
`BicomplexSliceW`, `CoquaternionSliceW`), **Position**, **DownbeatPulse**,
−0.3..0.3, Release 0.6. Once a bar the shape sweeps through a new slice and
glides back.

### 3.7 Spin with the music ★

*Spin with the music.* Angle knobs (`UserEquationRotationDegrees`,
`DualOrbitTrapAngle`, `BilliardRaysAngle`), **Rate**, Edge **Wrap**, **Rms**,
5..120 degrees/s, Release 0.5. Always turning, faster when loud; never jumps
back. Tempo-locked spin instead: **BpmPhaseSaw** in **Position**, 0..360, on a
wrapping angle (one turn per beat).

### 3.8 Fringes collapse on the beat ★

*Fringes collapse on the beat.* `DualOrbitWhichPath` / `SlitCascadeWhichPath`,
**Position**, **BeatPulse**, 0..1, Release 0.15. The double-slit fringes flash
flat on each beat and come back. Raise Gain for a full collapse. The
*Double Slit - Beat Collapse* region is the same idea saved on a region
([Double-Slit Guide](DoubleSlit-Guide.md)).

### 3.9 Calm-passage stillness ★

*Calm passages hold still.* `DualOrbitInterferenceK`, **Position**,
**BeatPulse**, **Invert**, 10..30, Release 0.3. With no beat the knob rests
at the top of the range; each beat ducks it. Swap BeatPulse for **Rms** to
make the whole loud section duck and the quiet one rest.

### 3.10 Sparkle on the hats

`DualOrbitPhaseK` (dual-orbit, recolour only), **Position**, **High**,
−0.5..0.5, Attack 0, Release 0.2. The palette phase flickers with the
cymbals; cheap because it only recolours.

## 4. One recipe per family

Cheap knobs first: they re-render fast, so the motion stays smooth. *E* marks a
knob the app rates expensive (see [Pitfalls](#5-pitfalls)).

| Family | Knob | Recipe |
|---|---|---|
| Julia, Burning Ship, Tricorn, Magnet, Multibrot, Phoenix, Glynn, Spider | `DomainWarpStrength` | §3.4 (plain Mandelbrot has no audio knobs) |
| Multibrot | `MultibrotExponent` | Position, DownbeatPulse, 2..4: the power steps up on the bar |
| Spider | `SpiderCDecay` | Position, Mid, 0.2..0.8, Smoothstep, release 0.3 |
| Newton / Nova / Secant | `NewtonRelaxation` | Position, Bass, 0.8..1.4, Smoothstep, attack 0.05 / release 0.4 |
| Transcendental | `TranscendentalLambdaIm` | Rate, PingPong, Rms, 0.02..0.3/s |
| Talbot carpet | `TalbotSliceZeta` | Rate, PingPong, Rms, 0.01..0.2/s: walks down the carpet and back |
| Slit cascade | `SlitCascadeWhichPath` | §3.8 |
| Lyapunov | `LyapunovWarmup` *E* | Position, DownbeatPulse, 200..800 (slow) |
| Logistic map | `LogisticSeed` *E* | Position, BpmPhaseSine, 0.3..0.7 |
| IFS | `IFSIterations` *E* | Position, Rms, 1,000,000..5,000,000: density follows loudness |
| L-System | `LSystemDepth` *E* | Position, DownbeatPulse, 3..6 |
| Strange attractors | `AttractorA` | Position, Bass, a ± 0.1 around the preset value, release 0.5 |
| Plasma | `PlasmaRoughness` | Position, HighMid, 0.3..0.8 |
| Acid Fog | `AcidWarpFlow` *E*, `AcidWarpFrequency` | §3.1-3.3; or Frequency, Rate, PingPong, Rms, 0.1..1/s |
| Flame | `FlameVibrancy` | Position, Rms, 0.3..1.0, release 0.4 |
| Apollonian | `ApollonianMinPixelRadius` | Position, Invert, High, 0.5..2: more circles when it's bright |
| Billiards | `BilliardDiskRadius` | Position, Bass, 0.4..0.6, Smoothstep |
| Billiard rays | `BilliardRaysAngle` | §3.7 |
| Math Plane | `MathPlaneT` | Position, BpmPhaseSine, 0..1: morphs the map once per beat |
| Precision field | `PrecisionLowTier` *E* | Not a good audio target (discrete tiers) |
| DLA | `DlaParticles` *E* | Position, Rms, 5,000..20,000 (slow) |
| Random tiling | `RandomTileRelief` | Position, Bass, 0.2..1.2 |
| Dual-orbit (pair) | `DualOrbitInterferenceK`, `DualOrbitWhichPath` | §3.8, §3.9 |
| Dual Buddhabrot | `DualBuddhaGainZ` | Position, Bass, 0.5..1.5 |
| Indra's Pearls | `IndrasMaskitMuRe` *E* | Rate, PingPong, Rms, 0.01..0.1/s |
| Mandelbulb / User Bulb | `BulbPower` | §3.5 |
| Mandelbox | `MandelboxScale` | Position, Bass, −2.0..−1.6, Smoothstep, release 0.5 |
| KIFS | `KifsOffsetX` | Position, Mid, −0.2..0.2 around the preset value |
| Quaternion Julia / Mandelbrot, Bicomplex, Coquaternion | slice W | §3.6 |
| Kleinian | `KleinianSphereScale` | Position, Bass, 0.9..1.1 |
| Mirror spheres | `MirrorSpheresReflectance` | Position, Rms, 0.5..1.0 |
| Dual-orbit volume | `DualOrbitVolumeSY` *E* | Position, BpmPhaseSine, −0.2..0.2 |
| User equation | `UserEquationRotationDegrees` | §3.7 |

The 3D families (Mandelbulb onwards) re-render slower than the 2D ones, so
favour slow signals (Bass, Rms, Downbeat) and longer release there.

## 5. Pitfalls

- **Saw on a non-wrapping knob gives a hard cut every beat.** BpmPhaseSaw
  jumps from 1 back to 0; only use it on knobs that loop (angles, Acid Fog
  flow). Same for Rate + **Wrap**: on a knob that doesn't loop, use
  **PingPong** or **Clamp** instead.
- **Expensive knobs get dropped first.** When more knobs are animating than
  the *animated-param ceiling* allows (App Settings; auto by default), the
  most expensive ones skip updates for that frame. If a binding seems to
  stall, drive fewer knobs or pick a cheaper one.
- **Pulses alias at a slow render rate.** Live, a binding updates only when
  the previous frame has finished (at most every 50 ms). When a frame takes
  longer than a beat pulse (about 0.2 s), pulses can be missed or caught at
  their tail. Use Bass / Rms with release on slow fractals. In a batch video
  the same applies at the video's frame rate: 10 fps samples every 0.1 s.
- **Rate mode slows down on slow frames (live).** Each update adds at most
  0.1 s of motion, so when frames take longer than that the knob moves more
  slowly than Min/s..Max/s. Batch videos are exact (one frame = 1/fps).
- **Acid Fog flow needs Morph.** `AcidWarpFlow` only shows with **Morph** on.
  A flow recipe, or enabling the row in the Fractured Waves expander, turns it
  on; enabling the row in Audio Settings does not, so check Morph there.
- **Domain warp needs its toggle and a shallow zoom.** `DomainWarpStrength`
  does nothing with the Domain-warp toggle off or past zoom 1e6.
- **No audio, no motion.** Without an active audio source the knobs stay at
  their own values.

## 6. Headless (batch video)

Bindings saved on a region play in a batch video from a music file (#1432),
and a recipe can be named on the command line (#1434):

```bash
FracturingFog --batch --mode video --fractal AcidWarp --x 0 --y 0 --zoom 1 --audio music.wav --audio-preset "Acid Fog - Endless flow" --seconds 20 --video-motion hold --out flow.mp4
```

`--audio-bind PARAM=SIGNAL,min=…,max=…,curve=…,mode=…,edge=…,attack=…,release=…`
adds or replaces one binding, so any recipe in this cookbook can be written
out in full. Order: the region's bindings, then `--audio-preset`, then
`--audio-bind`. An uncompressed WAV needs nothing else; other formats, and
muxing the music into the video, need ffmpeg.
