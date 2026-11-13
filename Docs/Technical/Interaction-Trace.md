# Interaction Trace (input to screen)

Issue #1762, slice S6 of the live / interactive responsiveness epic #1756.

The interaction trace follows a drag, pan or edit from the moment the pointer event reaches FF to
the moment its frame is on screen. It records where the time goes on each thread. The #86 trace
(`FF_GPU_PERTURB_DEBUG`) starts at `Trigger`, so it can't show the UI thread's part. It also
writes one formatted line per event straight to the file, which adds cost to the threads it
measures.

## Recording a trace

1. Start FF with the variable set. In PowerShell:

   ```powershell
   $env:FF_INTERACTION_TRACE = "1"; .\FracturingFog.exe
   ```

2. Do the interaction. Pause about a second between tests, so the analyser can tell them apart.
3. Close FF. The trace is `%TEMP%\ff_interaction_trace.csv`. It is rewritten each time FF starts,
   so copy it somewhere first if you want to keep it.
4. Analyse it:

   ```bash
   python Tools/InteractionTrace/analyze_interaction_trace.py "$TEMP/ff_interaction_trace.csv"
   ```

   Use `--json` for machine-readable output and `--gap-ms N` to change how long a pause splits
   two interactions (default 400 ms).

When the variable is off, each trace point is a static call that reads one field and returns. It
allocates nothing (a guard test checks this). When the variable is on, a record is a struct
written into a fixed ring of 131,072 records with one interlocked increment. A background
thread writes the ring to the file every 100 ms. If the ring laps before it is written, the file
says how many records were dropped.

## What is recorded

One CSV row per event: `t_us,thread,kind,seq,a,b`. `t_us` is microseconds since the trace
started, on the Stopwatch clock. `seq` is the frame job's upload sequence number, the same number
the #86 trace and the newest-wins gate use. The header names the fractal types.

| Kind | Where | `a` | `b` |
|---|---|---|---|
| `PointerMove` | render surface, UI thread | OS-to-handler delay, ms (-1 unknown) | points coalesced into the event (1 on the Windows native surface) |
| `InputOther` | press / release / wheel / key | OS-to-handler delay, ms | input kind (1 press, 2 release, 3 wheel, 4 key) |
| `InputHandled` | handler returned | handler time, µs | input kind (0 move) |
| `RenderHint` | `MainViewModel` | 0 full, 1 fast (progressive), 2 interactive (Math Plane editor) | |
| `PacerRequest` | `InteractiveTriggerPacer` | 0 started, 1 forced, 2 collapsed into the pending request | |
| `PacerFire` | pacer starts the pending request | 0 on a landed frame, 1 by its watchdog | |
| `TriggerEnter` | `FractalRenderHost.Trigger` | | fractal type |
| `TriggerExit` | Trigger returned | `ApplyView` µs | flags: 1 kept in flight, 2 cancelled a frame in flight, 4 progressive, 8 3D, 16 on the UI thread |
| `CalcStart` / `CalcEnd` | calc thread | stage (0 final) | `CalcEnd`: 1 cancelled |
| `UploadBarrier` | calc thread waited for the previous upload | wait, µs | |
| `UploadQueued` / `UploadStart` / `UploadEnd` | upload hand-off to the thread pool | stage | |
| `PostPipeline` | final frame's post-FX, overlays and HUD | µs | |
| `Present` | `UpdateTexture` + `Render` (a vsync'd Present blocks here) | µs | stage; -1 re-present, -2 resize, -3 external buffer |
| `Landed` | the host's `FrameLanded` | stage | |
| `Dropped` | calculated but not shown | 1 cancelled, 2 final superseded, 3 preview superseded | |
| `UiLag` | UI-thread probe | post-to-run delay, µs | |
| `Resize` | host resize | width | height |

**Where the render surface's input arrives.** On Windows the swap chain is a native child HWND
that receives every mouse message itself, so the Avalonia `InputSponge` never sees them.
`FracturingFog.Win/NativeMouseForwarder.cs` subclasses that HWND and forwards the messages to the
input controller. The trace marks them there, using `GetMessageTime` for the OS stamp. On the
other platforms `AvaloniaInputAdapter` marks them, using the Avalonia event's time stamp.

**OS-to-handler delay.** Avalonia stamps a pointer event with the OS message time. On Windows
that is `GetMessageTime`, milliseconds since boot. The trace subtracts it from
`Environment.TickCount`, which counts on the same clock, and both move in steps of about 15.6 ms.
So the delay is coarse, but it shows when the UI thread is too busy to read its message queue.
Other platforms stamp from other clocks. A difference outside 0–10 s is recorded as -1.

**Coalesced points.** This is `PointerEventArgs.GetIntermediatePoints(...).Count`: how many raw
moves were merged into the one event FF handled.

**UI lag probe.** `UI.Avalonia/Services/UiLagProbe.cs` posts a stamped callback to the UI thread
every 10 ms, with one in flight at a time, and records how long it waited. It also tells the
trace which thread is the UI thread.

## What the analyser reports

The analyser splits the trace into interactions: runs of input with no pause longer than
`--gap-ms`. Input here means presses and releases, wheel steps, keys, render requests and window
resizes. A bare pointer move doesn't count, because hovering over the image moves the mouse
without rendering. It follows each one for up to 1.5 s, so that its last frames land. For each
interaction it gives the p50 / p95 / max of:

- **Input.** Moves per second, coalesced points, OS-to-handler delay, handler time by input kind,
  and UI lag. UI lag while idle is reported separately as a baseline.
- **Pacer.** Requests that started a render, were forced or were collapsed, and fires on landing
  or by the watchdog.
- **Trigger.** Count and rate, time in `Trigger`, `ApplyView` time, how many ran on the UI
  thread, and frames kept or cancelled in flight.
- **Calc thread.** Queue wait from Trigger to calc start, upload-barrier wait, calculation time
  for previews and finals, cancelled frames, and the share of the drag the calc thread was busy.
- **Upload.** Thread-pool delay, post pipeline, Present time by stage, total upload time, and the
  share of the drag Present was busy.
- **Screen.** Frames landed and per second, the gaps between them, and frames dropped by reason.
- **Input to screen.** For each move made with a button held (or, when there are none, each
  render request), the time until the first landed frame that comes from a Trigger started after
  it. With the OS stamp added, this is OS-to-screen. It does not
  include the display's own scan-out.

It then prints verdicts that tie the numbers to the epic's slices:

| Verdict | Fires when |
|---|---|
| S5/S6: UI thread busy | OS-to-handler p95 is longer than one display refresh (16.7 ms) |
| S5: slow handler | the move handler p95 is over 4 ms |
| S5: UI lag | posted work waits over 8 ms (p95) |
| S5: Trigger on the UI thread | Trigger p95 is over 4 ms and it ran on the UI thread |
| S2: barrier wait | the calc thread spent over 10% of the drag waiting for the previous upload |
| S2: serial pipeline | Present is busy over 50% of the drag and the calc thread under 50%. The verdict estimates the frame rate if they overlapped: 1000 / max(calc, Present). |
| S3: 2D cancels | a 2D drag had frames cancelled mid-calculation |
| S4: Math Plane 3D | a Math Plane 3D frame's median calculation is longer than one refresh |
| S7: post pipeline | the post pipeline is over 25% of a final frame |
| S7: pool delay | uploads waited over 4 ms (p95) for a thread-pool thread |
| S8 resize | the interaction had window resizes. The verdict gives the UI lag, the frames calculated at the old size and shown after the swap chain was resized, and the uploads still running when the swap chain was resized. |

`--selftest` checks the analysis against a small hand-built trace with known answers. CI runs it
in the tools workflow.

## First reading: the host test with a vsync stub

`InteractionTraceTests` drags through the real `FractalRenderHost`, paced by the S1 pacer, with a
renderer stub whose `Render` blocks the way a vsync'd Present does. Its trace, run through the
analyser on the development machine:

| | 2D Mandelbrot pan, 320×200 | Mandelbulb, progressive, 480×320 |
|---|---|---|
| Triggers / frames landed during input | 22 / 20 (33.5/s) | 21 / 19 (31.8/s) |
| Calculation, median | 2.5 ms final | 4.5 ms preview |
| Present, median | 25.3 ms | 25.3 ms |
| Present busy / calc busy | 90% / 14% | 92% / 32% |
| Handler to screen, p50 / p95 | 43 / 60 ms | 45 / 60 ms |

Since S1, the pacer starts the next render only when a frame lands, and a frame lands only after
its Present. So calculation and Present run one after the other, never at the same time. That is
the S2 headroom (#1758): overlapping the next calculation with the current Present. It needs a
second upload buffer and a pacer release at upload start, not at landing.

## First live reading (2026-11-13, user's machine, about 1280×744, 60 Hz)

This trace was recorded before the native mouse forwarder was traced, so it has no pointer-move
records. The input side is read from the render hints instead.

| Interaction | Frames shown / s | Longest gap | Calc median | Present median | Handler to screen p50 / p95 | UI lag p95 |
|---|---|---|---|---|---|---|
| 2D pan | 59 | 37 ms | 14.6 ms final | 0.4 ms | 25 / 33 ms | 0.5 ms |
| 2D wheel zoom (2 bursts) | 4–10 | **1071 ms** | 15–23 ms | 0.4 ms | 46–208 / 101–1052 ms | 0.6 ms |
| 2D deep pan (previews) | 52 | 42 ms | 15.5 ms preview | 0.4 ms | 29 / 37 ms | 0.4 ms |
| Mandelbulb orbit | 46–48 | 28 ms | 19 ms preview, 240 ms final | 0.5 ms | 31 / 41 ms | 0.5 ms |
| Quaternion Julia orbit | 45–53 | 55 ms (307 once) | 19 ms preview, 63 ms final | 0.4 ms | 31 / 41 ms | 0.5 ms |
| Math Plane 3D orbit | 62–64 | 33 ms | 2.2 ms | 12.7 ms (vsync wait) | 25 / 32 ms | 0.4 ms |
| Math Plane 2D drag | 60–62 | 84 ms | 1.1 ms | 13.8 ms (vsync wait) | 25 / 32 ms | 0.4 ms |
| Window resize (9 drags, 116 steps) | 0–20 | 256 ms | 13–27 ms | — | — | 35–66 ms (max 195) |
| 2D pan, perf HUD on | 60 | 52 ms | 4.1 ms preview | 9.6 ms preview | 25 / 33 ms | 0.8 ms |

What it says about the remaining slices:
- **Drags and pans are at the display rate,** limited by the calculation or by vsync. Latency is
  about 1.5 refreshes from handler to screen. The UI thread is idle during drags (lag p95
  under 1 ms), so S5 (#1761) has little to win for drags.
- **S2 (#1758):** this only gains where Present waits for vsync (Math Plane at 60–64/s) or where
  calculation is about one refresh long. The 2D and 3D drags here are calc-bound, at 14–19 ms
  per frame, so S2 alone won't raise them.
- **S3 (#1759):** few 2D frames were cancelled while dragging, because the S1 pacer already keeps
  the view still under a frame. Wheel zoom is a different story: it isn't paced, and it cancels
  frame after frame (#1769).
- **S4 (#1760):** Math Plane 3D already runs at the refresh rate (2.2 ms calc).
- **S7 (#1763):** the post pipeline is 0.7–1.0 ms. That is small next to a 15 ms calculation.
- **Resize (#1768):** blocks the UI thread up to 195 ms a step. 41 of 116 frames calculated at
  the old size were shown after the swap chain was resized (the flashing). 6 uploads were reading
  the calculator's buffers when a resize swapped them (the likely cause of the banding).

## Second live reading: window resize (#1768)

The second trace was recorded on 2026-11-13 with native mouse tracing. While a window edge was
dragged (110 resize steps):
- 24 frames calculated at the old size were shown after the swap chain had been resized;
- 14 uploads were still reading the calculators' buffers when a resize swapped them;
- the UI thread stalled for up to 135 ms.

The first trace showed the same pattern: 41 of 116 frames, 6 uploads, and stalls of up to 195 ms.

Before the fix, `FractalRenderHost.Resize` did four things in this order:
1. resized the swap chain and presented it;
2. cancelled the frame in flight;
3. waited for the calc thread only;
4. reallocated the buffers.

Each window size step also posted its own `Resize` to the UI thread, so stale steps queued up
behind a slow one. With #1768:
- a frame job (calc thread) and its upload (thread pool) hold a shared buffer gate. `Resize`
  cancels the frame, then takes the gate exclusively, so it waits for both before it resizes the
  swap chain or any buffer. The calc thread waits for the upload barrier before it takes the
  gate; otherwise it would deadlock against a waiting resize.
- the bootstrap keeps one resize post pending at a time, and that post applies the newest size.

`ResizeTearTests` resizes the real host in a burst between sizes of different aspect, with a
vsync-blocking renderer stub. It checks:
- every image after a swap-chain resize has the new size, apart from the one re-present that
  bridges the resize;
- the plain view's images are byte-equal to a fresh host's settled frames;
- the last image is the settled frame of the last size.

Before the fix, the three cases showed 47, 23 and 45 old-size images; after it, 0.

## Third finding: every calculator allocated up front (#1772)

The trace from the #1768 fix build showed that the swap-chain resize stopped racing, but each
resize call still blocked the UI thread: p50 28.5 ms, p95 122 ms. 21 ms (p50) and 94 ms (p95) of
that came after the swap-chain resize, where `ResizeLocked` resized all ~50 alt calculators.

A probe measured the cost at 1280×744:
- the host constructor and each `Resize` allocated **634 MB**, although only one calculator
  draws the view;
- working set climbed to about 1 GB over three construct-and-resize runs.

Since #1772, each alt calculator is made the first time it is used (`LazyCalc`), at the current
size, with the current colour map and 2D GPU state. Resize, the colour-map broadcast and the GPU
switch reach only the calculators made so far.

`ApplyView` used to sync the escape-time and TearDrop calculators on every view, and that alone
made both of them at startup. It now syncs them only when they exist or are about to draw the
view.

`LazyCalculatorTests` checks four things:
- a Mandelbrot start makes no alt calculator;
- the constructor and a resize allocate 81 MB instead of 634 MB;
- a type first used after a resize and a colour change renders byte-equal to a fresh host;
- a calculator that was made, then recoloured and resized, renders byte-equal to a fresh host too.

## Files

- `Abstractions/Render/InteractionTrace.cs`: switch, record kinds and the ring writer.
- `Engine/Rendering/FractalRenderHost.cs`: Trigger, calc thread, upload, Present, landed and
  dropped.
- `Abstractions/Render/InteractiveTriggerPacer.cs`: pacer decisions.
- `FracturingFog.Win/NativeMouseForwarder.cs`: mouse arrival on the Windows swap-chain HWND
  (`GetMessageTime`) and handler time.
- `UI.Avalonia/Input/AvaloniaInputAdapter.cs`: pointer, wheel and key arrival and handler time
  (non-Windows surfaces, and keys).
- `UI.Avalonia/ViewModels/MainViewModel.cs`: render hints, and it starts the lag probe.
- `UI.Avalonia/Services/UiLagProbe.cs`: UI-thread lag probe.
- `Tools/InteractionTrace/analyze_interaction_trace.py`: analyser; see the
  [README](../../Tools/InteractionTrace/README.md).
- `Server.Tests/InteractionTraceTests.cs`: allocation guard, ring round-trip and lap, and the
  host-drag invariants.
- `Server.Tests/ResizeTearTests.cs`: resize bursts against the real host (#1768).
- `Server.Tests/LazyCalculatorTests.cs`: lazy calculator allocation (#1772).
