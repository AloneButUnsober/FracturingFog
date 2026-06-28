# D-4 — Distributed Rendering, Phase 4 Session Notes

Phase plan: see
[DistributedRendering-DevelopmentPlan.md §9 Phase D-4](DistributedRendering-DevelopmentPlan.md#phase-d-4--video--slideshow-distribution).

Picks up from
[D-3-Session-Notes.md](D-3-Session-Notes.md) — D-3a / D-3b closed the
image-tiling pipeline (raw RGBA binary trailer, adaptive sizing, work
stealing). D-4 distributes video and slideshow renders.

## Session log

### Session 1 — 2026-06-27 — D-4a frame-range planner + video job submit

**Goal**: extend the cluster pipeline to accept video jobs. Land the
frame-range planner, route mode="video" through `job.submit`, deliver
per-frame PNGs as a single batched binary trailer per tile, and persist
frames on disk under `<jobid>/frames/`. Stop short of the ffmpeg
encode pass — that's D-4b — but transition the job state machine
through `merging` → `ready` with a stub frames-manifest artifact so
clients can verify completion.

**Files added**

- `Server/Cluster/Protocol/FrameRangeDto.cs` — optional payload on
  `TileJobDto` carrying `[StartFrame, EndFrame)`, `TotalFrames`, `Fps`,
  and the pre-computed `LogStartZoom` + `LogZoomDelta` so the worker
  computes per-frame zoom with the same smoothstep BatchRenderer uses.
- `Server/Cluster/FramePlanner.cs` — `PlanVideo(RenderRequestDto, …)`
  returns a `TilePlanner.Plan` with `Mode="video"` and one
  `TileJobDto` per contiguous frame range. Defaults to 30 frames per
  tile (one second of 30 fps video). Validates total frame count
  against `[MinTotalFrames=2, MaxTotalFrames=18000]` and even-snaps
  output dims to match the encoder requirement.
- `Server/Cluster/FramesPayloadCodec.cs` — pack/unpack the binary
  trailer for `tile.deliver PayloadKind="frames"`. Wire shape: 4-byte
  `FRMS` magic + i32 version + i32 frame count, then per-frame
  `[i32 frameIndex][i32 pngLen][pngLen bytes]`. SHA-256 lives on the
  outer DTO; per-frame integrity rides on TLS + the trailer-wide hash.
- `Server.Tests/Cluster/FramePlannerTests.cs` — 14 tests covering
  total-frame computation, tile range splitting, last-tile remainder,
  log-zoom constants, forward/reverse zoom, even-snap, refusal of
  non-video mode + frame-count bounds, smoothstep-vs-BatchRenderer
  zoom parity.
- `Server.Tests/Cluster/FramesPayloadCodecTests.cs` — 5 tests:
  round-trip preserves all frames, zero-frame payload, magic-mismatch,
  truncated-payload and trailing-garbage rejection.
- `Server.Tests/Cluster/ClusterEndToEndVideoTests.cs` — 4 tests:
  `job.submit` plans frame-range tiles + persists `TotalFrames` on
  status.json; full lifecycle drives `tile.next` → `tile.deliver`
  (frames) → `job.status` → `job.fetch` on the manifest stub;
  rejects frame-count mismatch; rejects frame index outside the
  tile's range.

**Files amended**

- `Server/Cluster/Protocol/TileJobDto.cs` — optional `FrameRange`
  field; image tiles leave it null and the existing geometry path
  stays unchanged.
- `Server/Cluster/Protocol/TileDeliverDto.cs` — `PayloadKind` doc
  notes the new `"frames"` value.
- `Server/Cluster/TilePlanner.cs` — `Plan` gains `Mode` (default
  `"image"`) and `TotalFrames` so the persisted `plan.json` carries
  enough state for the coordinator's video-side handlers.
- `Server/Cluster/JobStore.cs` — `WriteFrameBytes`, `FrameExists`,
  `CountFrames`, `FramesDir`, `FrameFileName` helpers. Filenames
  follow ffmpeg's `image2` demuxer convention
  (`frame_NNNNNN.png`, 1-based) so D-4b's encode pass can point
  ffmpeg at the directory directly. `PersistedStatus` gains
  `TotalFrames` + `FramesDone` (0 for image jobs).
- `Server/Cluster/ClusterCoordinator.cs` —
  `HandleJobSubmitAsync` now branches on `mode`. Video routes to
  `FramePlanner.PlanVideo` and skips `ArtifactMerger` (no in-memory
  buffer — frames stream to disk). `HandleTileDeliverAsync` early-
  exits to a new `HandleFramesDeliverAsync` when
  `PayloadKind="frames"`. The frames handler validates the FRMS
  trailer against the persisted frame-range plan (count, index
  bounds), persists per-frame files via `JobStore.WriteFrameBytes`,
  feeds the EMA with `(W*H*frameCount, ms)` so video tiles are
  comparable to image tiles for adaptive sizing, and calls
  `FinaliseVideoFrames` once all tiles deliver. The stub finaliser
  writes a `frames-manifest.json` artifact and transitions to
  `ready`; D-4b will replace it with a real ffmpeg encode pass.
- `Server/Cluster/FFWorkerAgent.cs` —
  `ExecuteAndDeliverAsync` detects `tile.FrameRange != null` and
  routes to `ExecuteAndDeliverFramesAsync`, which loops the range,
  computes per-frame zoom via the same smoothstep math
  `BatchRenderer.RenderVideo` uses, renders each frame with the
  engine (per-frame workdir, deleted after read), packs into one
  FRMS trailer, and delivers with `PayloadKind="frames"`.
- `Server.Tests/Cluster/ClusterEndToEndImageTests.cs` —
  `Submit_Refuses_Video_Mode_In_D2` removed (the master accepts
  video now); replaced with `Submit_Refuses_Unknown_Mode` that
  pins the bad-mode error for an arbitrary unknown string.

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug     → 0 errors, 28 warnings (all pre-existing)
dotnet test Server.Tests --no-build            → 274 passed (249 from D-3 + 25 new)
dotnet run --project FracturingFogCLD.csproj -- --cluster-parity \
      --width 256 --height 128 --tile-px 64
  → png-path  PARITY OK   32,768 px, 0 diff
    rgba-path PARITY OK   32,768 px, 0 diff
```

**Design decisions captured here so future sessions don't relitigate**

32. **Frame-range as the tile unit, not per-frame.** Per-frame
    dispatch would push JSON-RPC chatter to one call per frame
    (600 calls for a 20s/30fps clip). Per-tile dispatch with 30
    frames per tile keeps the chatter at ~20 calls for the same
    clip and rides the existing dispatcher / steal infrastructure
    unchanged. Worker still wakes between frames so per-tile
    deadlines stay tight.
33. **FRMS trailer over per-frame envelopes.** Same reasoning as
    D-3a (raw-RGBA single trailer): one envelope per tile beats
    N envelopes per tile on framing cost, JSON parsing cost,
    and ordering complexity. SHA-256 covers the whole trailer;
    per-frame integrity rides on TLS plus the trailer hash.
34. **Master pre-computes `logStartZoom` + `logZoomDelta`.**
    Alternative was shipping raw `startZoom`/`endZoom` and having
    the worker take logs per frame — but the master is the only
    place that knows whether `--video-reverse` was set (the dev
    plan's reverse swap happens at plan time). Shipping the log
    constants avoids re-encoding reverse semantics on the worker.
35. **Per-tile workdir per frame, deleted after read.** Engines
    write PNG to disk by convention, and a single workdir for all
    frames would leave 30+ files on disk between renders. Per-
    frame subdirs keep cleanup deterministic and let a cancelled
    tile leave nothing behind beyond the parent dir (also cleaned
    up). Cost is one `mkdir`+`rmdir` per frame — negligible vs.
    the render itself.
36. **`Mode` + `TotalFrames` on `TilePlanner.Plan`, not a new
    `VideoPlan` type.** Two parallel plan types would double the
    persistence + coordinator branching for no clean separation
    (the dispatcher and JobStore operate on tiles, not modes). One
    plan type with optional fields keeps the dispatch path uniform
    and the diff against D-3 small.
37. **`PreferredTilePixels` doubles as `TilePixelsHint` for
    frames-per-tile in video mode.** The submit DTO only carries
    one hint field; using the same field for both kinds keeps the
    protocol simple and the planner picks the right knob via
    `mode`. Callers that want to override default frames per tile
    set the same field they'd use for image tiles.
38. **Per-frame EMA records `pixels = W * H * frameCount`.** A
    video tile that renders 30 frames at 320×180 represents the
    same work signal as an image tile of 1.7M pixels — feeding
    the EMA with the product keeps the planner's
    ms-per-kilopixel signal comparable across modes.
39. **`FinaliseVideoFrames` ships a stub manifest in D-4a.** The
    job state machine wants a terminal `ready` so the existing
    polling / fetch path keeps working in tests. A stub
    `frames-manifest.json` artifact (one file listing per frame
    on disk + total bytes) is enough to assert progress; D-4b
    will swap this for an ffmpeg encode pass that emits
    `.mp4`/`.mkv` and leaves the rest of the state machine
    untouched.
40. **`HandleTileDeliverAsync` early-exits to the frames handler
    *before* the image plan lookup.** The image plan reader pulls
    rect metadata that doesn't exist on a video tile; routing
    by `PayloadKind` first keeps the two paths independent and
    leaves the D-2/D-3 image fast path untouched.

**Open work — D-4b (ffmpeg sequential ingest + backpressure)**

- Replace `FinaliseVideoFrames`'s stub manifest write with an
  actual `FfmpegEncoder` invocation pointed at `JobStore.FramesDir`.
  Produce `.mp4` (h264hq default) or `.mkv` (ffv1 for `Lossless="ffv1"`).
- Stream encode in parallel with frame delivery: as soon as
  `frame_000001.png` is on disk, ffmpeg can start consuming.
  Master holds `tile.next` long-polls when the encoder is behind
  by more than `MaxFrameQueueDepth` (default 64) to avoid workers
  racing ahead of sequential ingest.
- Surface encode progress through `job.status` (new
  `EncodedFrames` counter alongside `FramesDone`).

**Phase D-4c entry points (after D-4b)**

- Slideshow per-slide sharding: each slide is one tile, one slide
  job, distributed independently. Reuse the image artifact merger
  unchanged.
- Slideshow assembly: master concatenates per-slide PNGs (or
  per-slide MP4s) into the final deliverable.

### Session 2 — 2026-06-27 — D-4b ffmpeg streaming ingest + backpressure

**Goal**: replace D-4a's `frames-manifest.json` stub with a real
streaming ffmpeg encoder. Workers continue to deliver per-tile PNG
batches; the master pipes them into a long-running ffmpeg subprocess
(`image2pipe / vcodec=png`) as soon as each frame lands on disk. A
backpressure gate on `tile.next` prevents fast workers from racing
ahead of sequential encoder ingest.

**Files added**

- `Server/Cluster/VideoFramePipeline.cs` — per-video-job streaming
  encoder. Spawns ffmpeg with image2pipe stdin and a reader task
  that loops `nextFrame=1..TotalFrames`, waits for
  `frame_NNNNNN.png` on disk, reads + pipes to stdin, increments the
  encoded counter. `IsBehind(maxDepth)` drives the master's
  backpressure gate. Bundles its own ffmpeg binary lookup
  (mirroring `Engine/Imaging/FfmpegEncoder.FindFfmpeg`) so the
  Server assembly doesn't need an Engine reference to use the
  pipeline. Includes `PresetFromLossless` + `DefaultExtensionFor`
  helpers translating `RenderRequestDto.Lossless` ↔ output container.
- `Server.Tests/Cluster/VideoFramePipelineTests.cs` — 7 tests:
  preset / extension mapping (3), `TryStart` returns null when
  ffmpeg missing (1), streaming round-trip produces a real MP4
  with `EncodedFrames == totalFrames` (1, ffmpeg-gated),
  `IsBehind` tracks delivered-minus-encoded (1, ffmpeg-gated),
  null-on-no-ffmpeg path (1). Round-trip tests use a hand-rolled
  PNG generator (`TinyPng`) so the test project stays UI-/imaging-
  stack-free.

**Files amended**

- `Server/Cluster/TileDispatcher.cs` — new `ReturnPending(jobId, tile)`
  pops a freshly claimed tile back to the pending queue without
  incrementing `Attempt`. Used by the coordinator's backpressure gate:
  the worker that was about to receive a tile gets WaitAgain instead,
  and the tile remains available for whoever asks next (often the
  same worker once the encoder catches up). Burning an attempt would
  be wrong — backpressure is not the worker's fault.
- `Server/Cluster/Protocol/JobStatusDto.cs` — exposes `TotalFrames`,
  `FramesDone`, `EncodedFrames` on the wire. Clients can show a real
  encode-progress bar alongside delivery progress.
- `Server/Cluster/JobStore.cs` — `PersistedStatus` gains
  `EncodedFrames` (mirrors the live pipeline counter at every
  tile-deliver tick + on every status read).
- `Server/Cluster/ClusterCoordinator.cs`:
  - New `MaxFrameQueueDepth { get; init; } = 64` (per dev-plan §7.9).
  - Per-job `_videoPipelines` + `_videoCts` maps.
  - `HandleJobSubmitAsync` spawns a `VideoFramePipeline` when the
    request maps to a real lossless preset AND ffmpeg is on disk;
    otherwise falls back to the D-4a manifest stub at finalise time.
  - `HandleTileNextAsync` checks `pipeline.IsBehind(MaxFrameQueueDepth)`
    after `Dispatcher.ClaimNextAsync`. If behind: `ReturnPending` the
    tile, log `tile-backpressure`, return `WaitAgain`.
  - `HandleFramesDeliverAsync` calls `pipe.NotifyFramesDelivered(N)`
    so the pipeline's delivered counter advances; persisted
    `EncodedFrames` is read back from the pipeline.
  - New `FinaliseVideoFramesWithEncoderAsync` awaits
    `pipe.Completion` (off-thread; ffmpeg drain may take seconds for
    ffv1 / qp0 H.264). Failure surfaces ffmpeg's stderr tail as
    `FailReason`. Success sets the artifact to the produced
    `.mp4` / `.mkv` (no more manifest).
  - `HandleJobCancelAsync` + tile-error-fail path call
    `DisposeVideoPipelineAsync` so a cancelled / failed video job
    doesn't leak the ffmpeg child.
  - `HandleJobStatusAsync` reads the live `pipe.EncodedFrames`
    instead of the last-persisted value so the wire counter is
    fresh per poll.
- `Server.Tests/Cluster/TileDispatcherTests.cs` — 3 new tests for
  `ReturnPending`: bounces the tile without bumping `Attempt`,
  signals a waiting worker via the `SignalAll` path, refuses an
  unknown job.

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug     → 0 errors, 29 warnings (all pre-existing + new xUnit1051 lints)
dotnet test Server.Tests --no-build            → 284 passed (274 from D-4a + 10 new)
dotnet run --project FracturingFogCLD.csproj -- --cluster-parity \
      --width 256 --height 128 --tile-px 64
  → png-path  PARITY OK   32,768 px, 0 diff
    rgba-path PARITY OK   32,768 px, 0 diff
```

(Existing D-4a video-end-to-end tests still pass: they submit jobs
with `Lossless="none"` so the coordinator skips pipeline creation
and lands on the manifest stub, exactly as before.)

**Design decisions captured here so future sessions don't relitigate**

41. **`image2pipe / vcodec=png` over an on-disk image2 demuxer.**
    Disk-mode ffmpeg (`-i frame_%06d.png`) requires every frame on
    disk before start; image2pipe over stdin lets ffmpeg consume
    `frame_000001.png` while `frame_000060.png` is still being
    rendered. This is the §7.9 win — overlapped encode + render
    cuts wall clock by the encode duration on lossless presets
    (seconds for h264-qp0 / ffv1 on a 20s clip).
42. **Master polls the frames dir; doesn't subscribe to filesystem
    events.** `FileSystemWatcher` works on Win but is unreliable on
    network shares and has latency quirks. A 20 ms polling interval
    on a single integer-counter directory listing burns negligible
    CPU and stays portable to Linux master deployments.
43. **Backpressure via `Dispatcher.ReturnPending`, not a pause
    flag.** Pause-flag adds a per-job state field and a cross-call
    invariant (set on slow, clear on caught-up). ReturnPending
    re-uses the existing pending queue + signal infrastructure: the
    tile goes back, the worker gets WaitAgain, and the next poll
    re-evaluates the gate cleanly. The dispatcher stays job-
    agnostic; backpressure lives in the coordinator where the
    pipeline state already is.
44. **`Attempt` count is preserved on ReturnPending.** Reusing
    `RecordFailure` would have bumped the retry budget and starved
    legitimate retries. Backpressure is master-side scheduling, not
    a tile-execution failure — same tile, same attempt, just
    later.
45. **Ffmpeg discovery lives in Server, not pulled from Engine.**
    Server's `csproj` only references `Abstractions`. Pulling
    Engine for `FfmpegEncoder.FindFfmpeg` would drag SkiaSharp,
    the calculator stack, and the render path into the cluster
    master — bigger surface area, slower build, no real benefit.
    The discovery code is ~25 lines; duplicating is cheaper than
    re-architecting the project graph for it.
46. **`Lossless="none"` → manifest stub, not h264 default.** The
    test fleet (D-4a end-to-end tests) submits with default
    `Lossless="none"` and asserts a `frames-manifest.json` artifact;
    keeping that path live preserves all existing tests AND
    matches user intent (a request that asked for "no lossless
    encode" shouldn't silently encode anyway). Encode-or-stub is
    a single check: `PresetFromLossless != null && IsAvailable()`.
47. **`FinaliseVideoFramesWithEncoderAsync` runs off-thread.**
    Awaiting `pipe.Completion` synchronously inside the
    `tile.deliver` handler would block the JSON-RPC dispatcher
    thread for the encoder's drain duration. Fire-and-forget via
    `Task.Run` keeps the wire path snappy; the state machine
    transition to `ready` happens whenever ffmpeg finishes.
48. **Live `EncodedFrames` read in `HandleJobStatusAsync`.**
    Persisted-only would lag by up to one tile-deliver interval
    (30 frames at typical settings → up to 1 s stale). Reading
    the pipeline counter at poll time gives clients a tight
    real-time view at zero persistence cost.

**Open work — D-4c (slideshow per-slide sharding)**

- Each slide is an independent render job: one tile per slide,
  dispatched through the existing image pipeline. Reuse
  `ArtifactMerger` unchanged; the merge step concatenates
  per-slide PNGs into the final slideshow PDF / per-slide MP4
  stream as required by the slideshow renderer.
- Slideshow assembly likely lives in a new `SlideshowAssembler`
  alongside `ArtifactMerger`; the coordinator branches on
  `Mode="slideshow"` in `HandleJobSubmitAsync` like video does
  today.

**D-4 exit criteria (after D-4c)**

- ffprobe parity test: a 20s 1080p30 ffv1 zoom across 2 workers
  must match a single-worker render frame-for-frame
  (`ffprobe -show_streams` + per-frame SHA-256).
- Extend `ClusterScaleSelfTest` with `--mode video` so the harness
  drives multi-worker video as well as image renders.
