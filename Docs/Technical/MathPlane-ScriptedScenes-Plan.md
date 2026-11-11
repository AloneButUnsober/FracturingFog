# Math Plane scripted scenes — Design Plan

**Tracking issue:** [#1557](https://github.com/AloneButUnsober/FracturingFog/issues/1557)
(follow-up to the closed 2D epic [#1547](https://github.com/AloneButUnsober/FracturingFog/issues/1547)).
**Status:** design doc. No code lands until this doc is reviewed. The slices are
filed as issues (§9), and the issues are the canonical task list.

**Related docs:** [MathPlane-DesignPlan.md](MathPlane-DesignPlan.md) (the 2D render
mode this builds on, §8 lists this follow-up); [SceneEngine-Architecture.md](SceneEngine-Architecture.md)
(multi-shot scenes); [Resources-Bibliography.md](../Resources-Bibliography.md).

---

## 1. Purpose

The Math Plane draws one state of an idea: a grid under a map at some `t`, an
orbit for one `c`. S5 (#1552) made a single parameter, `MathPlaneT`, animatable,
so one transform can play. A 3Blue1Brown-style explanation is a *sequence*:
show the basis, apply the map, wait, move c, reveal the orbit step by step,
caption the result. This plan adds that sequence as a **script**: an ordered
list of timed steps, evaluated as a pure function of script time, played live in
the Math Plane section and rendered to video headless.

The step model follows Manim (Grant Sanderson's engine for the 3Blue1Brown
videos, and its community edition): animations played one after another, groups
played together, each with a duration ("run time") and an easing ("rate
function"). See [bibliography → Manim](../Resources-Bibliography.md#manim). FF
borrows that vocabulary only; no Manim code or file format is used, and FF's
script is far smaller (a fixed set of Math Plane actions, not a Python API).

Non-goals:
- A general animation language or a Python/DSL scripting runtime.
- Narration audio. A video can be muxed with a soundtrack outside FF; the
  existing `--audio` drives audio-reactive *parameters*, not narration.
- 3D (that is #1556).

## 2. What exists to build on

| Need | Already in FF | Where |
|---|---|---|
| All Math Plane state as one text param | `MathPlaneScene` (+ map, c, backdrop params), region snapshot → `--param` | `Abstractions/Models/MathPlaneScene.cs`, `Engine/Models/FractalRegion.cs` |
| Animatable scalar params | `FractalAnimatableParamsMap` (`MathPlaneT`, `MathPlaneCRe/CIm`, matrix) | `Abstractions/Animation/FractalAnimatableParamsMap.cs` |
| Easing curves | `CameraEase` None / EaseIn (u²) / EaseOut / EaseInOut (smoothstep) | `Abstractions/Render/CameraTrack.cs` (`CameraKey.ApplyEase`) |
| Live play of one parameter | S5 Play / Loop of `t` in the section | `UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Map.cs` |
| Deterministic headless video | `--mode video`, each frame posed at `frame / fps` (`ApplyFrameState`) | `Batch/BatchRenderer.cs` |
| Multi-shot timelines | Scene Engine shots (region + animation at shot-local time), `--mode scene` | `Abstractions/Animation/SceneData.cs`, `Engine/Export/SceneVideoRenderer.cs` |
| Live recording | Instant record (#936) records the live window | Capture section |

Procedural animations (`AnimationData`) have no keyframes (Animation roadmap
D.1 is deferred), so they cannot express "do A, then B". The script is the
first FF feature with ordered steps, which is why it lives with the Math Plane
rather than as a general keyframe system.

## 3. Architecture

### 3.1 Storage: in the scene text (decision)

**The script is part of `MathPlaneScene`**, as `step` / `with` statements, rather
than a Scene Engine track. Reasons:

- **Batch parity for free.** The scene text is already in the region snapshot,
  so a scripted scene is `--param MathPlaneScene=@file`, it is saved by regions,
  My scenes (#1637) and the demos, and the Command builder already emits it.
- **One renderer.** The calculator draws the state at script time `s`; nothing
  outside it needs to know what a step is.
- **Scene Engine still works.** A shot on a Math Plane region plays its script on
  shot-local time (§3.7), so multi-shot sequences come without a second format.

The open question in #1557 ("extend `MathPlaneScene`, or a Scene Engine track?")
is resolved in favour of the scene text.

### 3.2 Script time

A new param **`MathPlaneScriptTime`** (seconds, `≥ 0`, default `0`) selects the
moment drawn. It is in the region snapshot (so `--param MathPlaneScriptTime=2.5`
renders a still at 2.5 s) and in `FractalAnimatableParamsMap` (so procedural
animations and the bus can drive it).

- `s = 0` draws the scene exactly as authored: every step is at progress 0.
  A scene without steps ignores `s`. Existing scenes render unchanged.
- `s ≥ D` (the script duration) holds the final state.
- The state at `s` is a **pure function of (scene text, params, s)**: no state
  is carried between frames. A frame of a video is identical to the still at the
  same `s`. That is the determinism contract (#1557's batch question).

### 3.3 Statement syntax

Steps are statements in the scene text, after the objects they refer to:

```
step DURATION [ease none|in|out|inout] ACTION ARGS...
with [DURATION] [ease ...] ACTION ARGS...
```

- `step` starts when the previous step ends. A `with` starts at the same time as
  the `step` before it and runs alongside it (Manim's animation group). A `with`
  without a duration takes its step's duration and ease.
- A step lasts as long as its longest member. `D` is the end of the last step.
- `ease` defaults to `inout` (smoothstep, `CameraEase.EaseInOut`), so a step
  starts and settles gently; `none` is linear. The curves are `CameraKey.ApplyEase`, not new
  ones.
- Durations are seconds, `0 ≤ d ≤ 600`. A zero duration is a cut.

### 3.4 Actions (first set)

| Action | Effect over the step | Notes |
|---|---|---|
| `wait` | nothing | a pause |
| `show NAME...` / `hide NAME...` | fade objects in / out (alpha 0 ↔ 1) | objects start hidden with the existing `hidden` option |
| `t VALUE` | `MathPlaneT` → VALUE | the S5 morph, now one step among others |
| `matrix A B C D` | the 2×2 map → [[A, B], [C, D]] | entries interpolate linearly; a following `t` plays it |
| `c RE IM` | `c` → RE + i·IM | moves the orbit, the Julia backdrop and `z^2 + c` together |
| `move NAME X Y` | a point, or a vector's tip, → (X, Y) | segments / polylines move by their first vertex |
| `draw NAME` | reveal a curve, segment or polyline along its length | Manim's "Create"; sampled length, not parameter |
| `orbit N` | reveal orbit steps up to N | integer steps appear one by one, the newest fading in |
| `cobweb N` | reveal cobweb steps up to N | as `orbit` |
| `phase on|off` | fade the phase portrait in / out | |
| `caption "TEXT"` | fade in a screen-space caption, replacing the last | bottom-centre, wraps; `caption ""` fades it out |
| `camera X Y [ZOOM]` | pan / zoom the view (world y-up centre, zoom factor) | relative to the user's view (§3.6) |

Values interpolate from the value the earlier steps leave (or the authored value)
to the target. Each action names a target, never a delta, so the fold in §3.5 is
well defined. Unknown names and wrong argument counts are parse errors with a
column, like every other scene statement.

### 3.5 Evaluation

`MathPlaneScript.Evaluate(scene, params, s)` returns an **effective scene and
params** for the calculator:

1. Lay out the steps on the timeline (start, end per member).
2. Fold the members in start order. For each property touched, the member's
   *from* value is the property's value at its start (the authored value, or the
   end value of the last member that touched it before). Progress is
   `ease(clamp((s − start) / duration, 0, 1))`.
3. Two members that touch the same property at overlapping times are a parse
   error ("c is already animated by the step at line N"). Overlap is otherwise
   unambiguous.

The result goes through the existing draw path. Visibility becomes an alpha per
object (today it is a bool), which the calculator applies to the object's colour.

*As built (SS1, #1695):* `Abstractions/Models/MathPlaneScript.cs` holds the step
records, `Layout`, `Validate` (unknown names, `move` on a curve or region, a leading
`with`, overlapping members) and `Evaluate`. The calculator evaluates the parsed scene
at `MathPlaneScriptTime` and draws the result. Fades lower the object colour's alpha;
labels' halos, point outlines and region shading fade with it. Values that equal the
authored ones leave the object untouched, so `s = 0` is bit-identical to the scene
with its steps removed. `matrix` animates the matrix params, which show only while
`MathPlaneMapMode` is Matrix (as before). Errors name the statement and column.
The backdrop is cached only for scripted scenes, keyed on its family, the whole
parameter set except the Math Plane's own animated values (t, matrix, script time,
scene text), c, the view, size, iterations, quality and colour map; any other scene
recomputes it as before.

*As built (SS2, #1696):* `draw`, `orbit`, `cobweb`, `phase` and `caption`. `Evaluate`
also returns a `MathPlaneReveal`: how much of each object is drawn, fractional orbit
and cobweb step counts, the phase portrait's opacity and the caption pair. Details:
- **`draw`** reveals curves (explicit, parametric, polar), segments, polylines and
  vectors along their length. Curves are cut by the length of their sampled polylines;
  segments, polylines and vectors by world length. Points, regions and implicit curves
  are refused: implicit curves come from marching squares as unordered pieces, so
  "along its length" has no meaning. A draw makes its object visible when it begins
  and begins strictly after its start time, so `s = 0` stays the authored scene. A
  draw of a visible object therefore jumps to undrawn as it starts; the intended use
  is on objects written `hidden`.
- **`orbit` / `cobweb`** start from the scene's own statement (0 without an `orbit`;
  `cobweb` needs its statement for x0 and f). Whole steps up to floor(k) are solid and
  the next one fades in with the fractional part.
- **`phase`** blends the portrait over what is under it, per channel.
- **`caption`** cross-fades with the caption it replaces. It is drawn at the bottom
  centre on a dark box, word-wrapped to 80% of the width, above the orbit read-out;
  a literal `\n` breaks a line. Captions are at most 200 characters.

### 3.6 Camera

The view centre and zoom belong to the view (`FractalViewState`), not the scene.
A `camera` step is applied **inside the calculator** as a transform on top of the
view: effective centre = view centre + script offset, effective zoom = view zoom
× script factor. The user can still pan and zoom; the script moves relative to
that. Headless renders of the same view and `s` are identical.

*As built (SS3, #1697):* `camera X Y [ZOOM]` targets an offset (world units, y up)
and a zoom factor relative to the user's view (default 1, range 1e-6..1e6). The
camera starts at (0, 0, 1). The offset interpolates linearly and the zoom
geometrically (log-linear), so a push-in reads as steady speed. `Evaluate` returns
a `MathPlaneCamera`. For one frame the calculator sets its `CenterX` / `CenterY` /
`Zoom` to the effective view and restores them afterwards, so the grid, labels,
backdrop (its cache key reads the effective view), phase portrait and objects all
follow, and the view state is never written. `LastScriptCamera` reports the camera
used. Known gap: the section's pointer read-out still maps the pointer through the
user's view. Canvas editing is off mid-script anyway (SS4), but mid-script the
read-out is off by the camera.

### 3.7 Scene Engine

A Scene Engine shot on a Math Plane region gets a **Play script** option (on by
default when the region's scene has steps). Shot-local time drives
`MathPlaneScriptTime`, in `SceneVideoRenderer` (offline) and the realtime player.
The shot's default length is the script duration. Nothing else in the Scene
Engine changes.

### 3.8 Batch and video

- **Stills:** `--param MathPlaneScriptTime=S` (region snapshot). No new flag.
- **Video:** `--mode video --fractal MathPlane` with a scripted scene uses a new
  video motion **`script`** (also chosen by `--video-motion auto` when the scene
  has steps): frame `k` is drawn at `s = k / fps`, with no zoom or drift.
  `--seconds` defaults to the script duration when omitted.
- **Command builder:** the live seed already carries the scene; the script-time
  param rides the snapshot. A video seed from a scripted scene emits
  `--video-motion script`.
- **Scene:** `--mode scene` plays scripted shots as in §3.7.

## 4. UI (Control Center only)

A **Script** group in the Math Plane section, shown when the scene has steps:

- Play / Pause, Loop, a time slider (0..D) with a read-out `s / D`, and step
  markers. Scrubbing sets `MathPlaneScriptTime`.
- The current step's line is highlighted in the Scene box.
- Editing is in the scene text; script errors appear in yellow with the line
  and column, like other scene errors.
- Live recording is the existing instant record; the offline export is the
  Scene Editor or `--batch`.

*As built (SS4, #1698):* `MathPlaneSectionViewModel.Script.cs` and a Script group
under My scenes. It has:
- Play / Pause, Restart and Loop. Play runs from the current time, or from 0 when
  at the end; Loop starts over.
- A time slider (0..D) whose ticks mark every step start and end, and an `s / D`
  read-out.
- The current step is marked in a **list of the script's members** rather than in
  the Scene box. Each row shows its time span and its statement, and the members
  running now are bold. Clicking a row jumps to its start.
  - Why a list: after any canvas edit the scene text is re-serialised on one line,
    so a line highlight in the box would usually mark the whole scene.
- **Canvas editing works only at 0 s.** Mid-script the drawn scene differs from the
  written one (moved, faded or part-drawn objects), so a drag would edit something
  other than what is under the pointer. The group says so.
- Loading a demo pauses playback, and its script starts at 0.
- Playback is a `DispatcherTimer` that feeds `AdvanceScript(elapsed)`; tests drive
  `AdvanceScript` directly.

## 5. Tests (independent invariants)

- **s = 0 is the authored scene:** a scripted scene at `s = 0` renders the same
  pixels as the same scene with every `step` / `with` line removed.
- **End state:** at `s ≥ D` it renders the same pixels as a *hand-written* scene
  holding the final values (targets typed in by hand, not computed by the
  evaluator).
- **Continuity:** each property is continuous across step boundaries (no jump
  where one step ends and the next begins), checked numerically at `start ± ε`.
- **Ease endpoints:** every ease gives 0 at 0 and 1 at 1, and `inout` has zero
  slope at both ends.
- **Video = stills:** frame `k` of a headless `script` video equals the still at
  `--param MathPlaneScriptTime=k/fps`.
- **Overlap errors:** two members animating one property at once are rejected.
- **Round-trip:** scripted scenes survive the region snapshot, My scenes and the
  Command builder unchanged.
- Mutation checks on the fold (from-value taken at the wrong time) and on the
  ease (wrong curve) must fail tests.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Script language creeps toward a programming language | Fixed action set; no variables, loops or expressions in step targets |
| Long scripts re-render every frame | Same per-frame cost as S5's `t`. The fractal backdrop is the slow part; SS1 adds a backdrop cache keyed on its family, c and the view, so steps that leave those alone don't redraw it |
| Hidden objects are hard to edit | The section can show hidden objects faintly while editing (a view toggle, not a scene change) |
| Captions need fonts headless | Use the Skia text path the calculator already uses for labels |

## 7. Out of scope (possible later)

- Transform one curve into another (Manim's `Transform`): needs matching sample
  sets between curves. Possible later, after `draw`.
- Per-object colour changes, `indicate` / flash effects.
- Narration audio mux in FF.

## 8. Prior art and credit

Manim (3Blue1Brown, and Manim Community) is the model for the step vocabulary:
succession of animations, groups played together, run time and rate functions.
FF already credits Manim in the bibliography; this plan expands that entry with
the Manim Community reference manual pages for animation groups and rate
functions. The easing curves are FF's existing `CameraEase` set (u², smoothstep),
which are standard.

## 9. Slices

Dependencies are stated per issue; no auto-blocking is configured.

| Slice | Issue | Content | Depends on |
|---|---|---|---|
| SS1 | #1695 | Script core: `step` / `with` parse, timeline, fold, `MathPlaneScriptTime` param (snapshot + animatable), actions `wait`, `show`/`hide`, `t`, `matrix`, `c`, `move` | — |
| SS2 | #1696 | Reveal actions: `draw`, `orbit`, `cobweb`, `phase`, `caption` | SS1 |
| SS3 | #1697 | `camera` action (calculator-internal view transform) | SS1 |
| SS4 | #1698 | Script group in the section: play / pause / loop / scrub, step markers, line highlight | SS1 |
| SS5 | #1699 | Headless video: `--video-motion script`, `--seconds` default, Command builder seed | SS1 |
| SS6 | #1700 | Scene Engine: Math Plane shots play their script on shot-local time | SS1, SS5 |
| SS7 | #1701 | Scripted demos, help text, user-guide section | SS1–SS5 |
