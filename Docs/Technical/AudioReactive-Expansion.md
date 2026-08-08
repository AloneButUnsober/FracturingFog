# Audio-Reactive Expansion — Design & Dev Plan

Status: **in progress** (foundation phase). Owner: Bradley Brown.
Tracking issue: **#259** (parent). Child slices: **#260–#266**.

This is the delivery of the deferred roadmap bullet
[`Animation-Roadmap.md` → D.4 "Audio-reactive animations"](../Animation-Roadmap.md),
generalised well past animation `FrequencyHz`/`PhaseOffset` into a single
modulation layer every visual subsystem can subscribe to.

---

## 1. Problem — the signal exists, almost nothing consumes it

The analyzer is already strong. [`BeatAnalyzer`](../../Audio/BeatAnalyzer.cs)
does FFT spectral-flux onset detection, adaptive threshold, BPM estimation, and
per-band energy smoothing, exposing it all through
[`IBeatSource`](../../Abstractions/Audio/IBeatSource.cs):

- 5 band levels + RMS — `BandEnergy(Bass, LowMid, Mid, HighMid, High, Rms)`,
  each normalised 0..1 via dual-EMA (short / long) so it is loudness-relative.
- `Beat` / `Downbeat` events with per-event `Strength` and `BpmEstimate`.
- `EstimatedBpm`, `CurrentEnergy` (latest snapshot, replaced atomically).
- User-steerable per-band flux weights (`AudioSettings.BandWeights`).

**Today the only consumer is the slideshow.**
[`SlideshowEngine`](../../UI.Avalonia/Slideshow/SlideshowEngine.cs) counts beats
to swap theme (`BeatsPerTheme`) / region (`BeatsPerRegion`) and derives fade span
from BPM (`FadeBeatFraction`). The rich `BandEnergy` signal drives **nothing**
except a live meter in the Audio Settings dialog. Every other subsystem —
fractal parameters, the animation bus, Acid Warp, terminal / ASCII FX, the Scene
Engine, post-FX — is deaf.

## 2. Core design — one shared modulation source, a binding matrix, many consumers

Do **not** wire audio point-to-point into each feature. Introduce two
abstractions and let every subsystem depend only on those.

### 2.1 `IAudioModulationSource` — pull-model, derived signals

Wraps an `IBeatSource` and exposes a per-frame **pull** of ready-to-use signals.
Pull (not event) fits the render-gated animation bus: consumers sample the latest
state when they tick, never mid-render.

```
AudioModulationFrame Sample();   // cheap, allocation-free, thread-safe read
```

`AudioModulationFrame` (immutable struct):

| Field            | Range | Meaning                                                        |
|------------------|-------|----------------------------------------------------------------|
| `Bass`…`High`    | 0..1  | Band levels (straight from `BandEnergy`, already smoothed).    |
| `Rms`            | 0..1  | Overall loudness.                                              |
| `BeatPulse`      | 0..1  | Envelope: jumps to `Strength` on each `Beat`, decays by `tau`. |
| `DownbeatPulse`  | 0..1  | Same, gated to bar starts (`Downbeat`).                        |
| `BpmPhaseSaw`    | 0..1  | Tempo-locked sawtooth (free LFO synced to the music).          |
| `BpmPhaseSine`   | 0..1  | `0.5+0.5·sin` of the same phase (smooth breathe carrier).      |
| `Transient`      | bool  | True on the frame a fresh onset landed (one-shot triggers).    |
| `Bpm`            | dbl   | `EstimatedBpm`, 0 if unknown.                                  |
| `IsActive`       | bool  | Analyzer running and producing samples.                        |

**Key implementation trick — envelopes are analytic, not ticked.** On each
`Beat`/`Downbeat` the source stores `(timestampUtc, strength)` and a phase
anchor. `Sample()` computes `BeatPulse = strength · exp(-(now-t)/tau)` and
`BpmPhaseSaw = frac((now-anchor)/beatPeriod)` on read. No background timer, no
per-consumer state, threadsafe by reading volatile fields. Attack/decay `tau`
configurable (default ~180 ms decay for a musical thump).

### 2.2 `AudioModulationBinding` — the modulation matrix row

One assignable mapping: pick a signal, shape it, land it in a target's range.

```
AudioSignalKind Source;   // Bass | … | Rms | BeatPulse | DownbeatPulse
                          //        | BpmPhaseSaw | BpmPhaseSine
double Gain, Bias;        // out = clamp01(Bias + Gain · shaped(signal))
AudioResponseCurve Curve; // Linear | Exp | Log | Smoothstep
bool  Invert;             // 1 - x
double OutMin, OutMax;    // final map into the target parameter's range
```

`double Evaluate(in AudioModulationFrame f)` → the target value. Targets reuse
the `Min`/`Max` already declared in
[`FractalAnimatableParamsMap`](../../Abstractions/Animation/FractalAnimatableParamsMap.cs)
so no range is redefined. A binding is data — savable in regions / scenes /
presets and editable in a small modulation-matrix UI.

Both types live in **`Abstractions/Audio/`** (UI.Avalonia references Abstractions,
never Engine). The `IAudioModulationSource` impl lives in the **`Audio`** project
next to `BeatAnalyzer`; [`AudioCaptureDriver`](../../Audio/AudioCaptureDriver.cs)
exposes it alongside `BeatSource`, and the bootstrap hands it to the shell the
same way it hands over `IBeatSource` today
([`AvaloniaShellBootstrap`](../../Hosting/AvaloniaShellBootstrap.cs) →
`StartAudioReactive`).

## 3. Injection points — why each is cheap

### 3.1 Fractal parameters via the animation bus (biggest win)

[`ParameterAnimationBus`](../../UI.Avalonia/ViewModels/Animation/ParameterAnimationBus.cs)
already ticks a list of
[`IParameterAnimator`](../../Abstractions/Animation/IParameterAnimator.cs) at
50 ms, **gated on render completion** (skips its tick while a render is in
flight). Audio param modulation rides that gate for free — no new race with the
renderer. Add one animator:

> **`AudioModulatorAnimator`** — holds an `AudioModulationBinding` + a target
> `AnimatableParamDescriptor` + a setter closure. Each `Tick` it samples the
> source and writes `binding.Evaluate(frame)` into the param.

Because `FractalAnimatableParamsMap` already enumerates every animatable field
per `FractalType` with `Min`/`Max`/`Cost`, this instantly reaches Julia `c`,
`BulbPower`, `MandelboxScale`, `DomainWarpStrength` (#253), quaternion slices,
etc. — across every fractal family — with clamp ranges and cost gating already
defined. The bus `Ceiling` policy drops `Expensive` tracks first, so a
beat-slammed IFS/DLA/Plasma-seed re-sim is already protected.

### 3.2 Fractal "breathing" / movement

The evocative bit — making the fractal itself pulse, warp, breathe with the
music. All feasible on top of §3.1 plus one new view-scale modulator:

- **Zoom-pulse breathe** — view scale ×(1 + `Bass`·k). Needs a small modulator
  on `FractalView` scale (not a `FractalParameters` field). The signature
  "breathing" look.
- **Domain-warp breathe** — `DomainWarpStrength` (#253) ← `Bass`; swirl pulses
  on the kick. Already animatable, cheap, shallow-zoom only.
- **c-orbit / power pump** — `JuliaC`, `BulbPower`, `QJuliaSliceW` ← `BpmPhaseSine`;
  morph locked to tempo.
- **Rotation kick** — view rotation / `UserEquationRotationDegrees` stepped on
  `DownbeatPulse`.
- **Iteration / bailout pump** — detail blooms with `Rms` (moderate cost —
  throttle).
- **Camera shake** — small view-offset jitter on `Transient` (high-band).

### 3.3 Acid Warp

[`AcidWarpAmbientDirector`](../../Abstractions/Models/AcidWarpAmbientDirector.cs)
already exposes `RequestNext()` + a tick model. Feed a `Downbeat` into
`RequestNext()` → pattern advances on the bar. Palette-cycle rate ← `Bpm`. Warp
frequency / center via the animator (`_acidWarpList` entries already exist). The
existing auto-VJ (#251) becomes a **beat-locked** VJ. See
[`AcidWarp-Mode-Design.md`](AcidWarp-Mode-Design.md).

### 3.4 Terminal / ASCII FX

[`AsciiFxSettings`](../../Abstractions/Render/AsciiFxSettings.cs) is a large FX
set, much of it already time-driven (`…Hz`, `TimeSeconds`, strength scalars).
Audio just writes those scalars each frame:

- `Bass` → `BreatheGammaAmp` (field pulses with the kick)
- `Transient` → `Glitch` / `GlitchIntensity` burst
- `Rms` → `BloomStrength`
- `Bpm` → `HueCycleDegPerSec`, `RampScrollSpeed` (synced cycling)
- `BeatPulse` → `MatrixRainSpeed` surge

No effect code changes — only the settings feed.

### 3.5 Scene Engine

[`SceneEngine-Architecture.md`](SceneEngine-Architecture.md) already lists an
**audio-reactive track** as a remaining slot. Add audio as a track *source*
alongside keyframes/easing: a scene param reads a band/binding instead of a
curve.

### 3.6 Post-FX / colour

Palette rotation phase and bloom strength ← `Rms`/`BeatPulse`, same binding
pattern.

## 4. Design constraints / gotchas

- **Export determinism (⚠ the one hard part).** MP4 / scene export must sample
  audio at **scene time**, not wall clock, or renders are unreproducible. The
  `File` source is already supported end-to-end; the export path must run the
  analyzer over the file timeline offline and seek the modulation source to each
  frame's timestamp. `AudioModulationFrame` therefore also needs a
  `SampleAt(double seconds)` form for the offline pass. Live view uses the
  wall-clock `Sample()`.
- **Smoothing.** Band levels are already dual-EMA smoothed. Beat/downbeat
  envelopes carry their own attack/decay so params never snap.
- **Cost gating.** Never beat-slam `Expensive` params. Reuse the bus `Ceiling` +
  `AnimatableParamCost`.
- **Headless / no-backend.** `IsActive=false` must make every binding a no-op
  that leaves the base param untouched (analyzer-only / Noop backend on
  Linux/macOS today).
- **Determinism of tests.** The modulation source must be drivable from a fake
  `IBeatSource` so envelope decay / phase / binding curves are unit-testable
  without real audio.

## 5. Delivery order (matches the recommended slicing)

| Phase | Slice | Issue | Depends | Effort |
|-------|-------|-------|---------|--------|
| **1** | Foundation — `IAudioModulationSource`, `AudioModulationFrame`, `AudioModulationBinding`, driver wiring, tests | #260 | — | med |
| **2** | ASCII / terminal FX bindings (quick win, proves the layer) | #261 | #260 | small |
| **3** | Acid Warp beat-lock (advance-on-beat + palette rate) | #262 | #260 | small |
| **4** | `AudioModulatorAnimator` + modulation-matrix UI → fractal params | #263 | #260 | med |
| **5** | Fractal breathing — view-scale / zoom-pulse / camera-shake modulator | #264 | #260, #263 | med |
| **6** | Scene Engine audio track | #265 | #260 | med |
| **7** | Deterministic audio→MP4 / scene export (`SampleAt`) | #266 | #260, #264 | hard |

Phase 1 lands the foundation with zero UI and full unit coverage; phases 2–3 are
quick wins on top; the fractal-param work and export determinism come last.

## 6. Cross-references

- [`Animation-Roadmap.md`](../Animation-Roadmap.md) — D.4 is the parked bullet
  this plan delivers; D.5 (animation→MP4) overlaps Phase 7.
- [`SceneEngine-Architecture.md`](SceneEngine-Architecture.md) — audio track slot
  (Phase 6).
- [`AcidWarp-Mode-Design.md`](AcidWarp-Mode-Design.md) — auto-VJ director reused
  in Phase 3.
- [`Slideshow-AudioReactive-Guide.md`](../User/Slideshow-AudioReactive-Guide.md)
  — the existing (only) audio consumer; unchanged by this work.
