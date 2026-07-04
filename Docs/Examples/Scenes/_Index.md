# Scene Engine — Example Scenes

> **Companion pages:** [Scene Engine User Guide](../../User/SceneEngine-UserGuide.md) ·
> [Scene Engine Architecture](../../Technical/SceneEngine-Architecture.md).

Four hand-authored example Scenes, each isolating **one** Scene Engine feature so
you can load it, play it, and read exactly which keys produce the effect. They
are deliberately **region-free** — every shot renders a fractal type with its
default look (empty `RegionName`), exactly like the built-in demo Scenes — so
they work on any install regardless of what is in your region library.

| File | Feature | Fractal(s) | Length |
|------|---------|-----------|--------|
| [`push-in-ease-demo.json`](push-in-ease-demo.json) | Per-key **easing** + a **dolly** (distance) push-in | Mandelbulb | 12 s |
| [`elevation-reveal.json`](elevation-reveal.json) | The **φ (elevation)** axis — rising up and over | Mandelbulb | 14 s |
| [`transition-showcase.json`](transition-showcase.json) | **Light-sweep** + **Cross-fade** transitions | Mandelbulb + Mandelbox | 24 s |
| [`bloom-breath.json`](bloom-breath.json) | Two **global tracks** — bloom swell + closing vignette | Mandelbulb | 18 s |

---

## How to load an example

**Import (recommended).** Open **Asset Manager → Scenes → Import…** and pick the
`.json` file. The Scene appears under the name shown in the tables below, ready
in the editor's **Load** list.

**Hand-merge.** Each file is a JSON array holding one Scene, in exactly the shape
of your `scenes.json`. With the app **closed**, paste the Scene object into the
array in `%APPDATA%\FracturingFog\scenes.json`. (Keep a backup first.)

**Render headless.** Once imported, render any example straight to video:

```powershell
dotnet run --project FracturingFogCLD.csproj -c Release -- `
  --batch --mode scene --scene "Example — Transition Showcase" `
  --fps 30 --width 1600 --height 900 --encode h264hq `
  --out Videos\transition-showcase.mp4
```

---

## 1. Push-In (Ease) — `push-in-ease-demo.json`

**What to watch:** the camera barely turns; it *pushes in* from distance 3.4 to
1.7. The move starts slow, speeds up, then eases to a stop — because the opening
key carries **Ease In-Out**.

**The teaching point:** *interpolation* shapes the path, *easing* shapes the
speed along it. Here the path is nearly a straight dolly (`Linear`); all the
character comes from the ease. Change the first key's `Ease` to `None` and replay
— the push becomes mechanical and constant. That difference is the whole lesson.

> [!TIP]
> Export this one with `--motion-blur 12`. A push-in is exactly the kind of
> steady move accumulation motion blur flatters.

---

## 2. Elevation Reveal — `elevation-reveal.json`

**What to watch:** the camera starts low (φ = 0.05, near the equator) and climbs
up and over the top (φ = 1.45) across a half-turn (θ 0 → 3.14).

**The teaching point:** φ is the axis most authors forget. A pure θ sweep at
fixed φ reads as the object spinning in place; adding the vertical rise gives the
parallax that reads as a real fly-around. The keys use **Ease In** on the way up
and **Ease Out** into the top, so the reveal breathes. `CatmullRom` keeps the arc
smooth through the middle key.

**Try this:** flatten it — set every φ to `0.4` — and replay. The move collapses
into a featureless spin. Then restore the φ ramp to feel the difference.

---

## 3. Transition Showcase — `transition-showcase.json`

**What to watch (in the exported video):** shot 1 (Mandelbulb) cuts in; shot 2
(Mandelbox) arrives on a 2 s **left→right light-sweep wipe**; shot 3 (Mandelbulb)
arrives on a 1.5 s **cross-fade**. Each shot orbits, so there is motion under the
transitions.

> [!IMPORTANT]
> **Play** hard-cuts between all three shots — realtime never composites two live
> 3-D fractals (it would breach the resource cap). The wipe and the fade render
> **only on export**. So: *play* it to check framing, then *export* it to see the
> transitions. This is the single most common "is it broken?" moment with Scenes;
> it is working as designed.

**The teaching point:** the authored transition kind is always stored; the build
renders as much of it as it safely can. See
[the architecture note on the cut model](../../Technical/SceneEngine-Architecture.md#the-cut-model).

**Try this:** change shot 3's `Transition` from `Crossfade` to `ParamMorph`. Both
shots 3 and 1 are Mandelbulb — but since both render *default* params there is
nothing to morph, so it falls back to a cross-fade. To see a real shape morph you
need two same-type shots with **different** params, which means region-backed
shots (point each at a different saved region of the same fractal type).

---

## 4. Bloom Breath — `bloom-breath.json`

**What to watch:** one Mandelbulb orbit, but the whole clip *breathes*. A
**Bloom-strength** global track swells the glow to a bright peak at the halfway
point and back; a **Vignette** global track holds open, then darkens the frame
edges over the final five seconds.

**The teaching point:** global tracks are sampled at **global scene time** and
applied on top of every shot — they are the scene-wide grade, not a per-shot
setting. Two tracks target two different scalars (`BloomStrength`, `Vignette`);
if two targeted the *same* scalar, the later one in the list would win. Unlike
the transitions in example 3, global tracks are visible in **both** realtime Play
and export.

**Try this:** add a third track targeting `Exposure` that dips to `0.3` at the
end for a fade-to-dark finale — or start from the built-in **"Exposure Ramp"**
Scene, which does exactly that. The available targets are `Exposure`,
`BloomStrength`, `BloomThreshold`, `Vignette`, `ChromaticAberration`.

---

## Adapting these to your own fractals

Every example uses **Mandelbulb** (orbit distance ≈ 2.6) or **Mandelbox**
(distance ≈ 8.0) because those framings are known-good. If you retarget a shot to
another 3-D type (KIFS, Quaternion Julia/Mandelbrot, Kleinian, Bicomplex, User
Bulb), you will likely need to adjust the camera **distance** so the fractal
fills the frame — start from that type's built-in default distance and nudge. The
θ and φ values carry over unchanged (they are the same orbit angles for every
type).
