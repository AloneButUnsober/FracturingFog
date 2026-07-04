# Scene Engine — User Guide

> **Companion pages:** [User Index](_Index.md) · developer-facing counterpart:
> [Scene Engine Architecture](../Technical/SceneEngine-Architecture.md) ·
> project roadmap: [Scene Engine Roadmap](../Scene-Engine-Roadmap.md).

The **Scene Engine** is the cinematic layer of Fracturing Fog. Where a *region*
saves a single frozen view and an *animation* wiggles the numbers behind one
view, a **Scene** strings several views together into a timeline, flies a camera
around the 3-D fractals, fades between shots, ramps the exposure across the whole
clip, and — when you are ready — renders the result to a video file.

If you have ever wanted the app to *make a little film* instead of just showing
you a picture, this is the page for you.

---

## What is a Scene, in plain words?

Think of a Scene as a **shot list for a short film**:

- A **Scene** is the whole film.
- A **Shot** is one continuous clip inside it — one fractal, one palette, for a
  set number of seconds.
- A **Camera track** is the path the camera flies along *during* a 3-D shot.
- A **Transition** is how one shot hands over to the next — a hard cut, a
  cross-fade, a wipe.
- A **Global track** is a change applied to the *whole* Scene on top of every
  shot — for example, fading the exposure up from black over the opening.

You author and preview a Scene inside the app in real time, then export it to an
MP4 (or a folder of PNG frames) at whatever quality your machine can manage,
taking as long as it needs.

![Placeholder: the Scene Editor with a two-shot timeline and a camera keyframe row.](../Images/_placeholders/scene-editor.png)

> [!NOTE]
> Scenes reference your existing assets *by name* — they do not copy them. A
> Scene that uses the region "Seahorse Deep" simply remembers the name; if you
> later re-paint that region, the Scene picks up the new look automatically. If
> you delete or rename the region, the shot quietly falls back to a default
> rather than crashing.

---

## The two ways a Scene is shown: preview vs. export

This is the single most important idea on the page, so it comes first.

| | **Preview (realtime)** | **Export (offline)** |
|---|---|---|
| **Goal** | stay smooth while you author | look as good as possible |
| **Speed** | keeps up with the clock, dropping quality if it must | renders each frame to completion, however long that takes |
| **Transitions** | **cuts only** — shots hard-cut between each other | full cross-fades, wipes, and morphs |
| **Motion blur** | none | optional, silky |
| **Output** | the live window | an MP4 / PNG sequence |

Why the split? A cross-fade needs *two* fractals drawn at once. For two live 3-D
raymarchers that is more work than a mid-range machine can do without stuttering,
and Fracturing Fog refuses to bog down your computer to keep the promise (see
[Resource use](#a-note-on-resource-use-and-speed-tiers) below). So **realtime
preview cuts between shots**, and the pretty transitions appear **only in the
exported video**, where the renderer can take its time and draw both sides.

> [!IMPORTANT]
> If you preview a Scene and the cross-fades look like hard cuts, nothing is
> broken — that is by design. Export the Scene and the fades will be there.

---

## Opening the Scene Editor

There are two doors into the editor:

1. **Floating menu → Edit Scene…** Press **`M`** to raise the floating menu,
   then click **Edit Scene…**. This opens the editor on a blank Scene (or the
   last one you were editing).
2. **Asset Manager → Scenes.** Open the Asset Manager, expand the **Scenes**
   node, and click any Scene row. Built-in demo Scenes live here too.

Either way you land in the same editor.

---

## A five-minute first Scene

Let us build a Scene from scratch — a single flying orbit of a Mandelbulb.

1. Press **`M`** → **Edit Scene…**. Click **New** to start with an empty Scene.
2. Type a **Name** — say `My First Orbit`.
3. Click **Add Shot**. A shot row appears.
4. In the shot, set **Fractal type** to **Mandelbulb** and **Duration** to
   `20` seconds. Leave **Region** blank — a blank region just renders the
   fractal type with its default look, which is perfect for a first try.
5. Because Mandelbulb is a 3-D type, a **Camera** section appears under the
   shot. Add four camera keys:

   | Time (s) | Distance | θ (theta) | φ (phi) | Ease |
   |----------|----------|-----------|---------|------|
   | 0        | 2.6      | 0.0       | 0.35    | None |
   | 5        | 2.3      | 1.57      | 0.9     | None |
   | 10       | 2.3      | 3.14      | 0.9     | None |
   | 20       | 2.6      | 6.28      | 0.35    | None |

   That sweeps the camera one full circle (θ goes `0 → 6.28`, which is `2π`,
   one turn), rising up over the top (φ climbs then settles) and pushing in a
   little at the far side.
6. Click **Play**. The live window flies the orbit and loops. Click **Stop** to
   halt it.
7. Click **Save**. Your Scene is now in the library, ready to export.

That is the whole loop. Everything below is refinement.

> [!TIP]
> Not sure what numbers to type? Load the built-in **"Mandelbulb Orbit"** Scene
> and read its camera keys — it is exactly this orbit, hand-tuned. Copy it,
> rename it, and tweak.

---

## Understanding the camera: distance, θ, φ

The 3-D fractals are viewed with an **orbit camera** — imagine the fractal
floating at the centre of a globe and the camera riding on the globe's surface,
always looking inward at the centre. Three numbers place the camera:

- **Distance** — how far the camera sits from the centre. Smaller = closer =
  the fractal fills more of the frame (a *dolly in*). Larger = further away.
- **θ (theta)** — the **azimuth**: how far *around* the globe, measured in
  **radians**. `0` is the front, `1.57` (≈ π/2) is a quarter-turn, `3.14` (≈ π)
  is the back, `6.28` (≈ 2π) is all the way around to the front again.
- **φ (phi)** — the **elevation**: how far *up or down*. Small values look from
  near the equator; larger values climb toward the top pole.

> [!NOTE]
> **Radians, not degrees.** One full turn is `2π ≈ 6.283`. Handy values:
> quarter-turn `1.571`, half-turn `3.142`, three-quarter `4.712`, full turn
> `6.283`. Want two full turns? Use `12.566`. The camera follows your numbers
> *literally* — asking for `θ = 12.566` really does orbit twice.

A **camera track** is just a list of these poses at different times, and the
engine smoothly flies the camera through them.

### Why an orbit can look like a spin (and how to fix it)

If you sweep only θ at a fixed distance and elevation, a centred, roughly
symmetric fractal looks like it is *spinning in place* rather than the camera
flying around it — there is nothing else in the frame to tell your eye which is
moving. The cure is to also change **φ** (rise up and over) and **distance**
(loom in) as you orbit. The parallax that creates reads unmistakably as *the
camera flying around a solid object*. Every built-in orbit does this; copy the
pattern.

---

## Path shape: Linear, Catmull-Rom, Bezier

Each camera track has one **interpolation** setting that decides how the camera
flies *between* your keys:

| Setting | Feel | When to use |
|---------|------|-------------|
| **Linear** | constant speed, a slight jolt of direction at each key | mechanical, deliberate moves |
| **Catmull-Rom** *(default)* | smooth curved path that still passes exactly through every key | almost everything — the natural cinematic choice |
| **Bezier** | eases to a gentle stop at every key, then accelerates away | shot-to-shot moves that pause on each pose |

Catmull-Rom is the default and the right answer most of the time. It gives you a
flowing path with no visible "corners" at the keyframes.

> [!TIP]
> Catmull-Rom can *overshoot* slightly on a sharp change of direction — the
> camera bulges a hair past a key before curving back. Usually this looks
> great (it is how a real crane move behaves). If you need the camera to hit a
> pose dead-on with no overshoot, add an extra key just before it, or switch
> that track to **Linear**.

---

## Per-key easing: accelerate and decelerate

Interpolation shapes the *path in space*; **easing** shapes the *speed along it*.
Every camera key carries an **Ease** setting that controls how the camera
accelerates out of that key toward the next one:

| Ease | Behaviour |
|------|-----------|
| **None** *(default)* | steady speed across the segment |
| **Ease In** | slow start, speeding up (accelerate out of the pose) |
| **Ease Out** | fast start, slowing down (decelerate into the next pose) |
| **Ease In-Out** | slow at both ends, quick in the middle — the classic "settle" |

Because easing and path-shape are independent, you can combine them freely — a
Catmull-Rom path with **Ease In-Out** keys glides *and* breathes.

> [!TIP]
> A push-in that starts slow, accelerates, then eases to a stop feels far more
> expensive than a constant dolly. Set the first key to **Ease In** and the
> last key to **Ease Out** (or use **Ease In-Out** on both).

---

## Transitions between shots

When a Scene has more than one shot, each shot (after the first) has a
**Transition** that decides how it arrives:

| Transition | What it does | Realtime preview | Exported video |
|------------|--------------|------------------|----------------|
| **Cut** | instant switch on the next frame | cut | cut |
| **Cross-fade** | one shot dissolves into the next | *cut* | dissolve |
| **Light-sweep** | a soft-edged wipe sweeps left→right | *cut* | wipe |
| **Param-morph** | the fractal's shape morphs from one shot into the next | *cut* | shape morph* |

`*` **Param-morph** only *morphs the shape* when both shots are the **same**
fractal type (so there is a shape to morph between). If the two shots are
different types, it automatically falls back to a cross-fade in the export.

Each non-cut transition also has a **length in seconds** — how long the fade or
wipe takes. It overlaps into the tail of the previous shot.

> [!IMPORTANT]
> Remember: **every transition except Cut looks like a Cut in the live preview.**
> The fades and wipes are drawn only when you **Export**. This is not a bug — it
> is how Fracturing Fog protects your machine (see below).

---

## Per-shot tone-map

Each shot can pin its own **HDR tone-map operator** — the curve that squashes
the bright, high-dynamic-range render down to something your screen can show:

| Operator | Character |
|----------|-----------|
| *(inherit)* | use whatever the shot's region already uses (default) |
| **None** | clip highlights hard — punchy, can blow out |
| **Reinhard** | gentle, rolls off highlights softly |
| **Reinhard Extended** | Reinhard with a white point you can push |
| **ACES** | filmic, cinematic contrast — a common "movie" look |

Leave it on *inherit* unless you specifically want one shot to look different
from its region's normal grade. This is a per-shot *look decision*, so it lives
next to the region/theme pickers, not on a keyframe track.

---

## Global tracks: changing the whole Scene at once

A **global track** keyframes one look-setting across the **entire Scene**, on
top of whatever every shot is doing. It is sampled at *global* Scene time, so it
sweeps continuously even across shot boundaries. Available targets:

| Target | What it controls |
|--------|------------------|
| **Exposure** | overall brightness before tone-mapping (1 = neutral, <1 darker, >1 brighter) |
| **Bloom strength** | how much the bright parts glow |
| **Bloom threshold** | how bright a pixel must be before it glows (lower = more glow) |
| **Vignette** | darkening toward the frame edges (0 = none) |
| **Chromatic aberration** | coloured-fringe lens look (0 = off) |

Global tracks use the **same** keyframe/easing/interpolation vocabulary as the
camera track — a list of `(time, value, ease)` keys with a path shape (default
**Linear**, because a look-ramp usually wants a steady, predictable sweep).

The built-in **"Exposure Ramp"** Scene is the reference example: a Mandelbulb
orbit that fades up out of near-black, over-exposes to a bright bloom, then falls
back toward black — driven entirely by one Exposure global track.

> [!NOTE]
> **Where do I edit global tracks?** The graphical global-track row is still on
> the polish list. Today you author them either by starting from the built-in
> "Exposure Ramp" Scene, or by hand-editing the Scene's JSON in the Asset
> Manager (global tracks round-trip as readable text — see
> [Hand-editing scenes.json](#hand-editing-scenesjson)).

---

## Playing, previewing, saving

The editor's buttons:

- **New** — start a blank Scene.
- **Load** — pull an existing Scene from the library into the editor.
- **Revert** — throw away unsaved edits, reloading the saved version.
- **Save** — write the Scene to your library (`scenes.json`).
- **Delete** — remove the Scene from the library.
- **Preview** *(per shot)* — apply just that one shot's region + theme +
  animation to the live view, so you can frame it. This is a single static
  framing, not sequenced playback.
- **Play** — run the *whole* Scene live on the main window, cut-sequenced, and
  loop it. **Stop** halts it (so does closing the editor).
- **Export…** — render the Scene to a video (see next section).

---

## Exporting a Scene to video

Click **Export…** in the editor. You are offered a group of output knobs:

| Knob | Meaning | Typical |
|------|---------|---------|
| **Width / Height** | output resolution in pixels | `1920 × 1080` |
| **FPS** | frames per second | `30` (or `60` for silky motion) |
| **Motion-blur sub-frames** | extra samples averaged per frame for motion blur (1 = off) | `1`, or `8`–`16` for blur |
| **Encode** | video format preset | `h264hq` |

Choose an output file, and the app renders every frame to completion on a
background thread — this is the *offline* path, so it is slower than realtime and
that is expected. When it finishes you get the video, with all the cross-fades,
wipes, morphs, and motion blur that the live preview could not show.

> [!NOTE]
> **No ffmpeg?** Video encoding uses ffmpeg. If it is not installed, the export
> still succeeds — it keeps the rendered **PNG frame sequence** in the output
> folder and tells you so. Install ffmpeg and you can encode the folder later,
> or the app will encode automatically next time.

### Motion blur, explained

With **motion-blur sub-frames** set above 1, each output frame is rendered
several times at slightly different moments across the frame's duration and the
results are averaged — exactly how a real camera's shutter smears fast motion.
It is only affordable in the offline export (never in realtime), and it makes
camera moves look dramatically more expensive. The **shutter fraction** (exposed
in the headless CLI, default `0.5` ≈ a 180° film shutter) controls how far
across each frame the samples are spread.

> [!TIP]
> Motion blur costs render time linearly: 8 sub-frames means roughly 8× the work
> per frame. Author and preview with it off; turn it up only for the final
> export.

---

## Rendering Scenes from the command line

Everything the **Export…** button does is also available headless, for batch
jobs and scripting:

```powershell
dotnet run --project FracturingFogCLD.csproj -c Release -- `
  --batch --mode scene --scene "Mandelbulb Orbit" `
  --fps 30 --motion-blur 8 --shutter 0.5 `
  --width 1920 --height 1080 --encode h264hq `
  --out Videos\mandelbulb-orbit.mp4
```

| Flag | Meaning |
|------|---------|
| `--scene NAME` | the saved Scene to render (from `scenes.json`) |
| `--fps N` | output frame rate (default `30`) |
| `--motion-blur N` | accumulation motion-blur sub-frames, `1`–`64` (1 = off) |
| `--shutter F` | open-shutter fraction `0 < F ≤ 1` (default `0.5`) |
| `--encode TYPE` | `h264hq` (default) · `h264` (lossless) · `ffv1` (lossless MKV) |
| `--width` / `--height` | output resolution |
| `--out PATH` | output file (or folder) |
| `--keep-frames` | keep the intermediate PNG sequence after encoding |

This is the same engine the GUI export uses, so the output is identical.

---

## Using the example Scenes

Fracturing Fog ships four hand-authored example Scenes alongside this guide, each
demonstrating one feature. They live in
[`Docs/Examples/Scenes/`](../Examples/Scenes/_Index.md) as importable files.

| Example file | Shows off |
|--------------|-----------|
| `push-in-ease-demo.json` | a dolly-in with **Ease In-Out** keys |
| `elevation-reveal.json` | a **φ (elevation)** sweep rising up over a Kleinian |
| `transition-showcase.json` | **Light-sweep** + **Cross-fade** between shots (export to see them) |
| `bloom-breath.json` | two **global tracks** — a bloom swell and a closing vignette |

To use one:

1. Open the **Asset Manager → Scenes** node.
2. Use **Import…** and pick the example `.json` file, **or** hand-merge its
   contents into your `scenes.json` (see below).
3. The Scene now appears in the editor's **Load** list. Load it, **Play** it,
   and read its shots and keys to learn the pattern.

See the [examples index](../Examples/Scenes/_Index.md) for a walkthrough of each.

---

## Hand-editing scenes.json

Scenes are stored as human-readable JSON at:

```
%APPDATA%\FracturingFog\scenes.json
```

(On Windows that expands to something like
`C:\Users\<you>\AppData\Roaming\FracturingFog\scenes.json`.) The file is a list
of Scenes; every enum is written as a readable string (`"Mandelbulb"`,
`"CatmullRom"`, `"EaseInOut"`), so you can safely edit it by hand. A minimal
one-shot orbit looks like this:

```json
[
  {
    "Name": "Hand-Edited Orbit",
    "Category": "User",
    "Description": "A single Mandelbulb orbit, authored by hand.",
    "Tags": [ "demo", "3D" ],
    "Shots": [
      {
        "Name": "Orbit",
        "RegionName": "",
        "FractalType": "Mandelbulb",
        "DurationSeconds": 20.0,
        "Transition": "Cut",
        "TransitionSeconds": 1.0,
        "Camera": {
          "Interpolation": "CatmullRom",
          "Keys": [
            { "Time": 0.0,  "State": { "Distance": 2.6, "Theta": 0.0,  "Phi": 0.35 }, "Ease": "None" },
            { "Time": 10.0, "State": { "Distance": 2.3, "Theta": 3.14, "Phi": 0.9  }, "Ease": "None" },
            { "Time": 20.0, "State": { "Distance": 2.6, "Theta": 6.28, "Phi": 0.35 }, "Ease": "None" }
          ]
        }
      }
    ]
  }
]
```

> [!WARNING]
> Edit `scenes.json` while the app is **closed**, or your changes may be
> overwritten when the app next saves. Keep a backup copy before large hand
> edits — a malformed file is skipped on load (you lose the custom Scenes in it,
> but the app still starts and the built-in demos still appear).

---

## A note on resource use and speed tiers

Fracturing Fog is built to **never crash your computer to draw a fractal**. It
aims to leave headroom — roughly a 90 % ceiling on processor and memory — and it
watches itself while previewing. On a laptop it turns quality down to keep the
window responsive; on a powerful desktop it deliberately uses more of the machine
to look its best. Same target, opposite intent.

For Scene authoring, the two consequences you will actually feel are:

1. **Realtime preview cuts between shots** (it will not run two 3-D fractals at
   once) — the fades appear in export.
2. **Preview may soften** under load (lower resolution, fewer effects) to hold a
   smooth framerate. The **export** always renders at full quality regardless of
   your machine, just more slowly.

None of this needs configuration to author a Scene. If you want to bias the
preview toward smoothness or fidelity, that lives in the app's performance
settings, not the Scene Editor.

---

## Troubleshooting

| Symptom | Likely cause & fix |
|---------|--------------------|
| Cross-fades look like cuts in preview | Working as intended — export to see them. |
| No camera section on a shot | The shot's fractal type is 2-D. Only the eight 3-D raymarch types (Mandelbulb, Mandelbox, KIFS, Quaternion Julia/Mandelbrot, Kleinian, Bicomplex, User Bulb) have an orbit camera. |
| Orbit looks like the object spinning in place | Add **φ** (elevation) and **distance** changes to your keys, not just θ. See [Why an orbit can look like a spin](#why-an-orbit-can-look-like-a-spin-and-how-to-fix-it). |
| "This scene has no shots with a positive duration to play." | Every shot has a duration of 0 (or less). Give at least one shot a positive **Duration**. |
| Camera whips around unexpectedly | θ interpolates *literally*. A jump from `6.0` back to `0.1` unwinds a whole turn. Keep θ monotonic (always increasing) for a clean orbit. |
| Export produced PNGs but no MP4 | ffmpeg is not installed. Install it, then re-encode the frame folder (or the app encodes next time). |
| A Scene's shot renders the "wrong" fractal | The named region was renamed/deleted, so the shot fell back to a default. Re-point the shot's **Region** in the editor. |

---

## See also

- [Regions Guide](Regions-Guide.md) — the saved views shots point at.
- [Slideshow + Audio-Reactive Guide](Slideshow-AudioReactive-Guide.md) — the
  cross-fade machinery Scenes build on.
- [User Bulb 3D Guide](UserBulb-Guide.md) — the 3-D fractals the camera flies
  around.
- [Capture Guide](Capture-Guide.md) — single-image and video export basics.
- [Scene Engine Architecture](../Technical/SceneEngine-Architecture.md) — how it
  all works under the hood.
- [Scene Engine Roadmap](../Scene-Engine-Roadmap.md) — what shipped and what is
  still to come.
