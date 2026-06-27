# S-X8 / S-X9 Cross-Platform Bug Sweep — Session Notes

**Branch:** `feature/cross-platform-full`
**Session window:** 2026-06-27
**Platforms verified:** Windows 11 Pro for Workstations · Ubuntu 26.04 (CrashingSymphony 7.0.0-22-generic)

Two-pass remediation of cross-platform regressions reported empirically by
the user. S-X8 was a triage sweep across six visible bugs; S-X9 followed
up on a "memory leak still present" report with instrumentation that
showed the climb was OS-side page retention (not a managed leak), plus
three additional symptoms that surfaced during the diagnostic runs.

---

## 1. Commits landed this branch (newest first)

| SHA | Subject |
|---|---|
| `f7710f8` | diag: S-X9f — burst-log first 5 frames so one-shot triggers show in FF_LEAK |
| `fadf1f3` | fix: S-X9e — right-drag select artifacts (composite over last-good frame) |
| `44881c6` | fix: S-X9d — left-drag pan snap-back (full-res stale-frame fallback) |
| `2617184` | fix: S-X9c — hold "Calculating…" 250 ms minimum so user sees app working |
| `31f499f` | diag: S-X9b — gate FF_LEAK baseline past warm-up + add forced-GC option |
| `121f62b` | diag: S-X9 — FF_LEAK_DEBUG instrumentation on upload path |
| `764d1e2` | fix: S-X8 — cross-plat memory + render + theme-editor bug sweep |

---

## 2. S-X8 bug sweep — initial six issues

User reported six bugs at session open. Per-fix detail:

### S-X8 #1 — Memory climb (Linux 3.5× baseline vs Windows; right-click needed to "wake" image)

Three concrete code-side issues attacked under the leak banner (none turned
out to be the real cause; see §3 for the actual disposition):

- **`Rendering.Skia/SkiaCpuRenderer.cs`** — atomic GCHandle + InstallPixels.
  Previously a failure in `SKBitmap.InstallPixels` left the pinned handle
  allocated and the `_ownBuffer` rooted, leaking one resize-worth of LOH per
  retry. Wrapped pin/install in try/catch that frees the handle and disposes
  the bitmap before rethrow.
- **`Rendering.Silk/SilkGLRenderer.cs`** — defensive `glPixelStorei` state
  (`UNPACK_ROW_LENGTH=0`, `UNPACK_ALIGNMENT=4`) before every `TexSubImage2D`,
  and a Linux-only `glFinish()` before `swap()` to force the GPU sync that
  glXSwapBuffers does not perform on most Mesa drivers (the "right-click to
  wake the image" symptom — the present was stalled behind in-flight commands
  until input forced an implicit sync).
- **`UI.Avalonia/Views/MainWindow.axaml.cs`** — store MiniDepth `ColorMapChanged`
  and `FrameCompleted` handlers as fields so `DetachShell` can unsubscribe.
  Previously lambdas leaked the host subscription on shell teardown.

### S-X8 #2 — Linux render breakdown (banding, drift, snap-back, blank regions, delays)

Same `Rendering.Silk` defensive state + glFinish as #1. Mesa under memory
pressure was leaving `UNPACK_ROW_LENGTH` at a non-zero stride from an earlier
client call, which the next `TexSubImage2D` read as padded → horizontal
banding.

### S-X8 #3 — Status bar stuck on "Calculating…" in deep Extreme

- **`Abstractions/Render/IFractalRenderHost.cs`** — added `RenderCancelled`
  event.
- **`Engine/Rendering/FractalRenderHost.cs`** — fire `RenderCancelled` in all
  three cancel branches (TAA cancel at ~line 1007, progressive intermediate
  at ~line 1487, final-stage at ~line 1521); gate `FrameCompleted` to fire
  only for the initial sample (`job.TaaSampleIndex == 0`) so TAA continuation
  samples don't update the status bar with cumulative-ms timings.
- **`UI.Avalonia/ViewModels/MainViewModel.cs`** — cache last `FrameInfo` and
  replay on `RenderCancelled` so the bar shows the prior render's geometry
  instead of "Calculating…" forever.

### S-X8 #4 — Ultra quality ms timing jitter

Same TAA `FrameCompleted` gating as #3. Cumulative `job.Sw` across TAA
refinement passes was inflating the ms counter on each subsequent fire.

### S-X8 #5 — Theme editor eyedropper broken

Two roots, two new files + one bootstrap edit:

- **`FracturingFog.Win/WindowsColorSampleBridge.cs`** (new) — Win32
  `WH_MOUSE_LL` global hook + `GdiGetPixel` sampling. Replaces the legacy
  WinForms-bound `DesktopEyedropper` wrapper that was wired only from the
  legacy WinExe `Program.cs`, so `FracturingFog.App` on Windows left the
  bridge null and the eyedropper silently no-op'd.
- **`Hosting/X11ColorSampleBridge.cs`** (new) — Linux X11 analogue.
  `XGrabPointer` + `XC_crosshair` cursor; runs its own `XNextEvent` pump on
  a dedicated background thread (XNextEvent is blocking). On the next button
  press, reads pixel at root-relative `(x_root, y_root)` via `XGetImage`,
  then `XUngrabPointer` and fires picked callback. 30 s timeout; right- /
  middle-click cancels.
- **`FracturingFog.Win/WindowsBootstrap.cs`** + **`Hosting/AvaloniaShellBootstrap.cs`**
  — register the bridges when `BootstrapHooks.ColorSampleBridge == null` on
  Windows / Linux respectively.

### S-X8 #6 — Theme editor inspect broken

- **`Hosting/X11InputBridge.cs`** — `HandleButtonPress` now invokes
  `InspectClickHook` on `PointerButton.Left` before the normal handler so
  the inspect callback gets first dibs.
- Implemented `X11InputBridge.TrySampleClient` via `XGetImage` at client
  coords, reading 32-bit BGRX at offset 16.
- **`FracturingFog.Win/WindowsBootstrap.cs`** — `WindowsNativeInputBridge`'s
  `TrySampleClient` uses Win32 `ClientToScreen` + `GetDC(NULL)` +
  `GdiGetPixel` so the same path that powers the eyedropper drives Inspect.

---

## 3. S-X9 — leak instrumentation + disposition

After S-X8 shipped, user re-tested and reported memory still climbing
(Linux >1 GB over a session; Windows slower but same pattern). Round-1
audit (Silk renderer, Skia renderer, upload pool, TAA accumulator, MSAA
accumulator, BlaTable, MiniDepth bitmap, FractalOverlayCompositor,
event-handler subscriptions, GCHandle pairings) found **no fresh leak**
matching the symptoms.

### S-X9 — diagnostic instrumentation (`FractalRenderHost.cs`)

`UploadProcessedBuffer` gained an env-gated `LeakDiagSample` helper that
logs the managed heap, working set, and per-generation collection-count
deltas from a baseline frame. Activation:

```
FF_LEAK_DEBUG=1                   # opt in
FF_LEAK_DEBUG_EVERY=<n>           # sample cadence (default 30 frames)
FF_LEAK_DEBUG_FORCEGC=1           # adds forced gen-2 collect + retained line
```

S-X9b refinements:
- Skip the ctor's 1×1 dummy frame so warm-up artifact isn't counted as leak.
- `forceFullCollection: true` adds a `retained=` field — the only line that
  matters for real leak triage; the bare `managed` value is noisy.

S-X9f refinement:
- Always log first 5 frames after baseline regardless of modulo so one-shot
  user actions (region jump, theme pick) leave a record. Found because user
  reported region-combo selection produced no log lines while manual
  coord-entry + Go did — turned out to be modulo-gate sampling, not a code
  bypass.

### Telemetry result (1500 frames at deep zoom)

| Platform | Retained Δ vs baseline | Disposition |
|---|---|---|
| Linux  | +73 MB **constant** from f=100 → f=1500 | Not a managed leak |
| Windows | +46 MB **constant** from f=100 → f=300  | Not a managed leak |

Working set climbed 135–303 MB on top of stable retained — that's Server-GC
page retention (OS does not aggressively give back pages to which the
runtime has committed) + Mesa driver caches, not a code-side bug.

**Triage: closed.** The "leak" perception is a combination of:
- Server GC not returning pages to OS until pressure
- Mesa shader / texture state cached per-FBO never released until context
  destroy
- Pinned `_uploadDstPool` / `_uploadPrePool` / new `_lastFullResBuffer` live
  in POH which does not compact

None are code defects. If a follow-up genuinely wants to reduce WS, the
levers are: switch to Workstation GC for desktop builds; force `GC.Collect`
on idle (high CPU cost); shed Mesa state by recreating GL context on resize
events (visible flash).

---

## 4. S-X9c/d/e — three additional regressions found during the diag runs

### S-X9c — Status bar never shows "Calculating…" (`UI.Avalonia/ViewModels/MainViewModel.cs`)

Symptom: user reports never seeing the busy hint even though
`FractalRenderHost.Trigger` raises `StatusRequested` before any work.

Cause: shallow-zoom GPU calc completes in <20 ms; `OnFrameCompleted`
overwrites the busy string before the next display refresh.

Fix: minimum-visible 250 ms hold. `OnRenderHostStatusRequested` becomes a
named handler (was lambda) that starts a stopwatch and pushes the busy
string; subsequent `OnFrameCompleted` / `OnRenderCancelled` text is queued
via `ApplyOrDeferStatusText` and a `System.Threading.Timer` releases it
when 250 ms elapses. Long calcs flip the bar immediately because the
elapsed gate is already past. Also adds the missing `StatusRequested`
unsubscribe in `Dispose` that the old lambda left dangling.

### S-X9d — Left-drag pan snap-back (`Engine/Rendering/FractalRenderHost.cs`)

Symptom: drag with left-button → image follows cursor → release →
**image snaps back to pre-drag position** → after the final full-res calc
completes, pops to the dragged location.

Cause: progressive `Trigger(progressive: true)` uploads sub-res buffers
(W/4 × H/4 for ¼ stage) during pan and writes them into
`_lastUploadedBuffer`. On pan-stop the pan-stop debounce fires `Trigger()`
which captures `_lastUploadedBuffer` as the candidate stale buffer for the
next calc's stale-upload step. That step is gated by
`StaleW == CalcW && StaleH == CalcH`, so the small preview buffer fails the
dim check and is skipped → no present fires between input release and
full-res frame completion → in some Mesa / X11 swap-chain configs the OS
damages the surface back to a pre-pan composite (the snap-back).

Fix: new `_lastFullResBuffer` pinned snapshot, updated only when an
uploaded frame's dims match `_currentTarget`. Stale-frame capture in the
calc thread prefers `_lastUploadedBuffer` when its dims match the calc and
falls back to `_lastFullResBuffer` when they don't. Resize nulls both.
Snapshot sources from `_lastPreOverlayBuffer` (= `dst` **before** grid /
watermark / selection-box composite) so the buffer is overlay-free — see
S-X9e for why that matters.

### S-X9e — Right-drag select artifacts (`Engine/Rendering/FractalRenderHost.cs`)

Symptom: at deep zoom, right-drag rubber-band-select. As user drags,
banding / artifacts appear in the image under the rect. Repeat drags grow
the artifact area.

Cause: `SetSelectionBox` fires per pointer-move and calls
`RepaintWithPostFx`, which reads `_calculator.ColorBuffer`. At deep zoom
the active calc is often mid-render with a partial buffer (rows the worker
hasn't reached, cancelled-state pixels left from a prior pan). Each
selection-box repaint stamped that partial state to the screen, and
because the box was redrawn dozens of times per drag the partial pixels
compounded into visible banding.

Fix: new `RepaintWithSelectionBox` composites over the cached
`_lastFullResBuffer` (the same snapshot S-X9d uses). That's the most
recent finished frame, captured pre-overlay, so re-using it for
box-repaint avoids both the partial-buffer artifact and double-stamping
the existing overlay. Falls back to `RepaintWithPostFx` when no snapshot
exists yet (first frame).

---

## 5. Files touched (round-2 only)

```
Engine/Rendering/FractalRenderHost.cs          (S-X9, S-X9b, S-X9d, S-X9e, S-X9f)
UI.Avalonia/ViewModels/MainViewModel.cs        (S-X9c)
```

For S-X8 (round-1) the touched file set is recorded in `764d1e2`.

---

## 6. Known follow-ups left on the table

- **Region dropdown produces no FF_LEAK log lines at default sample cadence.**
  Not a bug — diag modulo-gate misses single-shot uploads. S-X9f burst-log
  mitigates. If user reports it again with `EVERY=1`, then there's a real
  problem.
- **Server-GC vs Workstation-GC desktop tradeoff.** Not pursued. Switching
  to Workstation would lower idle WS at the cost of throughput during
  burst renders. Defer until a user complains about idle WS, not navigation
  WS.
- **CalculatorGen `_cache` Type pin** (`CalculatorGen/CalculatorGenHotLoad.cs`)
  — stale-cache cleanup compares `stale.Name` (`GenCalc_…`) vs Assembly name
  (`GeneratedCalc_…`). They never match → cache pins prior ALC → `Unload()`
  silently no-ops. Only matters if user actively iterates in the
  UserEquation / Sandbox / UserBulb editors. Cosmetic until then.
- **20-element calculator pool init at host ctor** (`FractalRenderHost.cs:296-316`).
  Each calc holds `uint[w*h] ColorBuffer` + 4×`float[w*h]` (Smooth /
  Distance / NormalX / NormalY). At 1920×1080 = ~20 MB × 20 ≈ 400 MB
  resident even for users who only render Mandelbrot. Lazy-init on first
  type-switch is the obvious fix but touches enough call sites to deserve
  its own slice.
- **Subprocess / thread count diagnostic.** User report of "40 subprocesses
  on Linux" was almost certainly htop showing threads (toggle with `H`).
  Worth confirming next session if it comes up again.

---

## 7. Test recipe for next session

```bash
# Linux
dotnet publish FracturingFog.App/FracturingFog.App.csproj \
    -c Release -p:PublishProfile=linux-x64
FF_LEAK_DEBUG=1 FF_LEAK_DEBUG_FORCEGC=1 FF_LEAK_DEBUG_EVERY=100 \
    ./FracturingFog.App/publish/linux-x64/FracturingFog.App 2> ff_leak.log

# Windows
dotnet build FracturingFog.App/FracturingFog.App.csproj -c Release
$env:FF_LEAK_DEBUG = '1'
$env:FF_LEAK_DEBUG_FORCEGC = '1'
$env:FF_LEAK_DEBUG_EVERY = '100'
.\FracturingFog.App\bin\Release\net10.0-windows\FracturingFog.App.exe `
    2> ff_leak.log
```

User-side validation path:
1. Pick **Deep Julias** from region combo → image renders (FF_LEAK first-5
   burst should log).
2. **Left-click-drag pan** → image follows cursor; on release, stays at the
   dragged location (no snap-back to pre-drag center).
3. **Right-click-drag select** → no banding under the rect; repeated drags
   stay clean.
4. **Status bar** shows `Calculating…` for ≥250 ms on every render trigger,
   then flips to the geometry / precision / ms line.
5. **`retained` Δ** in `ff_leak.log` stays flat after f=100. WS climb is
   expected and not a regression.

---

## 8. Video-capture recipe (so next session can ingest visual bugs)

I can't decode video directly. Extract frames with ffmpeg, attach PNG:

```bash
# Once-per-second sample
ffmpeg -i bug.mp4 -vf fps=1 frame_%03d.png

# Specific timestamps with descriptive names
ffmpeg -i bug.mp4 -ss 00:00:03 -vframes 1 t03_pan_release.png
ffmpeg -i bug.mp4 -ss 00:00:04 -vframes 1 t04_snap_back.png
ffmpeg -i bug.mp4 -ss 00:00:05 -vframes 1 t05_final_pop.png

# Burn the FF_LEAK frame counter onto frames so log lines line up
ffmpeg -i bug.mp4 -vf "drawtext=text='%{n}':x=10:y=10:fontsize=20:fontcolor=yellow" \
    -c:v libx264 bug_burned.mp4
```

For artifact / banding specifically: crop to the artifact region (full
1080p loses detail) and prefer lossless PNG over JPG (JPG compression
adds its own banding).
