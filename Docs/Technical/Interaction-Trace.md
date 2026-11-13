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
| `PointerMove` | render surface, UI thread | OS-to-handler delay, ms (-1 unknown) | points coalesced into the event |
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
`--gap-ms`. It follows each one for up to 1.5 s, so that its last frames land. For each
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
- **Input to screen.** For each move, the time until the first landed frame that comes from a
  Trigger started after the move. With the OS stamp added, this is OS-to-screen. It does not
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

## Files

- `Abstractions/Render/InteractionTrace.cs`: switch, record kinds and the ring writer.
- `Engine/Rendering/FractalRenderHost.cs`: Trigger, calc thread, upload, Present, landed and
  dropped.
- `Abstractions/Render/InteractiveTriggerPacer.cs`: pacer decisions.
- `UI.Avalonia/Input/AvaloniaInputAdapter.cs`: pointer, wheel and key arrival and handler time.
- `UI.Avalonia/ViewModels/MainViewModel.cs`: render hints, and it starts the lag probe.
- `UI.Avalonia/Services/UiLagProbe.cs`: UI-thread lag probe.
- `Tools/InteractionTrace/analyze_interaction_trace.py`: analyser; see the
  [README](../../Tools/InteractionTrace/README.md).
- `Server.Tests/InteractionTraceTests.cs`: allocation guard, ring round-trip and lap, and the
  host-drag invariants.
