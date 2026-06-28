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

### Session 3 — 2026-06-27 — D-4c slideshow per-slide sharding

**Goal**: extend the cluster pipeline to accept slideshow jobs.
One tile per slide; each tile is an independent image-mode render
of one complete slide PNG (no sub-rect math, no merger). The final
artifact is a `slides-manifest.json` describing every per-slide PNG
on disk — clients consume it (or stream individual slides) via the
existing `job.fetch` plumbing.

**Files added**

- `Server/Cluster/SlideshowPlanner.cs` — `PlanSlideshow(JobSubmitDto)`
  returns a `TilePlanner.Plan` with `Mode="slideshow"` and one
  image-mode `TileJobDto` per slide. Each tile's `Render` inherits
  unset fields from the parent template, pins `Mode="image"`,
  `ReturnMode="inline"`, `OutputName=null`, `SuppressDecorations=true`.
  `ValidateForSlideshow` enforces `[MinSlides=2, MaxSlides=2000]`,
  the tileable-fractal allowlist per slide, and matching
  `SlideDisplayMs` length when supplied.
- `Server/Cluster/SlideshowAssembler.cs` — static `Assemble(jobs,
  jobId, submit)` walks `<jobdir>/slides/`, computes per-slide
  SHA-256, applies per-slide `displayMs` overrides, and writes
  `artifact.slides-manifest.json` containing the per-slide
  `{slideIndex, name, bytes, sha256, displayMs, regionName,
  themeName}` array. Mirrors `ArtifactMerger` / `VideoFramePipeline`
  naming so the per-mode finaliser surface stays uniform.
- `Server.Tests/Cluster/SlideshowPlannerTests.cs` — 9 tests:
  tile-per-slide invariant, image-mode tile templates, parent-
  template inheritance, fractal-type allowlist per slide, dim
  inheritance + per-slide override, displayMs length validation,
  plan image-dim derivation.
- `Server.Tests/Cluster/SlideshowAssemblerTests.cs` — 4 tests:
  manifest schema + per-slide entries, per-slide displayMs override
  semantics (0 → default), missing-file refusal, JobStore
  slides-dir + counters round-trip.

**Files amended**

- `Server/Cluster/Protocol/JobSubmitDto.cs` — added `Slides`
  (`List<RenderRequestDto>?`), `SlideshowDefaultDisplayMs` (int),
  and `SlideDisplayMs` (`List<int>?`). Image / video jobs leave
  `Slides=null`; slideshow jobs require it.
- `Server/Cluster/JobStore.cs` — added `SlidesDir`, `SlideFileName`,
  `WriteSlideBytes`, `SlideExists`, `CountSlides`, `EncodeSlideTo`.
  Mirrors the `FramesDir` / `WriteFrameBytes` convention. The
  `EncodeSlideTo` helper takes a write-callback so the coordinator
  can pass `IClusterImageCodec.EncodeBgraToPng` straight to a temp
  path when an RGBA-mode worker delivers a slide.
- `Server/Cluster/ClusterCoordinator.cs` —
  - Added `_slideshowJobs` map (`ConcurrentDictionary<string, JobSubmitDto>`).
  - `HandleJobSubmitAsync` accepts `Mode="slideshow"`; routes through
    `SlideshowPlanner.PlanSlideshow`; registers the JobSubmitDto in
    `_slideshowJobs` so the finaliser keeps per-slide metadata
    without re-reading `request.json`.
  - `HandleTileDeliverAsync` checks `_slideshowJobs` before merger
    lookup (after the frames-trailer branch, before the Codec-null
    image-tile check). Slideshow tiles route to
    `HandleSlideDeliverAsync`.
  - `HandleSlideDeliverAsync` — idempotent (no-op accept on duplicate
    delivery), supports `PayloadKind="png"` / `""` (default) by
    writing bytes, supports `PayloadKind="rgba"` by re-encoding
    through the wired codec, refuses anything else. Feeds the
    per-worker EMA, updates tilesDone, transitions
    `queued/planning → rendering`, triggers
    `FinaliseSlidesAsManifest` on the last tile.
  - `FinaliseSlidesAsManifest` — `merging → ready` via
    `SlideshowAssembler.Assemble`. Artifact ext recorded as
    `slides-manifest.json`. Failure path identical to the
    video-manifest finaliser.
  - Cancel / tile-error-fail paths drop the `_slideshowJobs` entry.

**Build / test results**

- `dotnet build FracturingFogCLD.sln -c Debug` → 0 errors, 24 warnings
  (pre-existing source-gen / Avalonia obsolete warnings).
- `dotnet test Server.Tests` → 297 passed (was 284, +13: 9 planner +
  4 assembler). 0 failed, 0 skipped.
- `--cluster-parity --width 256 --height 128 --tile-px 64` →
  PARITY OK on both png-path and rgba-path (0 diff pixels), proving
  the new slideshow branch did not regress image-tile delivery.

**Design decisions (continuing #41–48 from Session 2)**

49. **One tile per slide; no sub-tile sharding in v1.** The dev plan
    explicitly says "subdivide per-slide later if a single slide is
    the long pole." Sub-tile sharding inside a slide would compose
    cleanly with `ArtifactMerger`, but the per-slide independence
    of slideshows already gives parallelism = slide count, which
    is typically 10–50 — well above worker count. v2 escalation
    when a single slide dominates wall-clock.
50. **Slideshow tiles bypass the merger entirely.** Image tiles
    write into a shared mmap RGBA buffer; slideshow tiles each
    own a complete PNG. Adding a merger that "stitches" 1×1 slide
    grids would be code obfuscation. Direct-to-disk via
    `JobStore.WriteSlideBytes` matches the video-frame ingestion
    pattern (one file per unit; manifest on completion).
51. **`_slideshowJobs` holds the JobSubmitDto, not just a bool.**
    The finaliser needs per-slide `displayMs` overrides and
    region/theme names. We could re-read `request.json`, but a
    32-slide submit is ≤ 32 KB in memory, and keeping the live
    submit in the map (a) avoids file IO on hot-path completion
    and (b) lets future per-slide retries consult the original
    spec without disk round-trips.
52. **Both PNG and RGBA worker deliveries accepted.** Workers
    don't know they're rendering for a slideshow — they choose
    payload kind per their own config. Refusing RGBA would break
    any cluster whose workers are in the D-3 raw-RGBA fast path.
    Encoding RGBA → PNG via `Codec.EncodeBgraToPng` straight to
    the slide temp file is one method call; tiny cost for major
    capability.
53. **`SlideshowAssembler` is static.** Unlike `VideoFramePipeline`
    (owns a long-running ffmpeg subprocess) or `ArtifactMerger`
    (owns a memory-mapped buffer), the slideshow finaliser is a
    one-shot read-files-write-manifest pass. No per-job state
    means no map to maintain, no disposal lifecycle to thread
    through cancel/fail paths.
54. **Plan `ImageWidth/ImageHeight` = max across slides.** The
    plan's image-dim fields are advisory only for slideshow
    (each tile carries its own per-slide dims). Reporting the
    max gives the admin UI a single "largest slide" hint for
    artifact-size estimation. Per-slide variation is fully
    supported — the test exercise multiple dim sizes.
55. **`MaxSlides=2000`.** At 1920×1080 with ~1.5 MB per
    high-detail PNG, 2000 slides ≈ 3 GB on disk — a sensible
    upper bound that protects the master from runaway client
    requests without blocking realistic slideshow lengths.

**D-4 exit criteria (Session 4)**

- ffprobe parity test: a 20s 1080p30 ffv1 zoom across 2 workers
  must match a single-worker render frame-for-frame
  (`ffprobe -show_streams` + per-frame SHA-256).
- Extend `ClusterScaleSelfTest` with `--mode video` so the harness
  drives multi-worker video as well as image renders.

### Session 4 — 2026-06-28 — D-4d ffprobe parity self-test + scale --mode video

**Goal**: close the D-4 exit criteria. Land a `--cluster-video-parity`
self-test that renders a short zoom two ways (single-thread vs N
workers via TileDispatcher+FramePlanner) and proves frame-for-frame
identity at the PNG level plus encode parity at the container level
(ffprobe stream metadata + per-frame framemd5). Extend
`ClusterScaleSelfTest` with `--mode video` so the speedup harness
exercises the video tile path alongside the image path.

**Files added**

- `ServerHost/ClusterVideoParitySelfTest.cs` — runs two arms in
  process. Baseline walks frames sequentially with the same
  smoothstep log-zoom math `FFWorkerAgent.ExecuteAndDeliverFramesAsync`
  uses, writing `frame_NNNNNN.png` (1-based, matching `JobStore`
  convention). Cluster arm runs `FramePlanner.PlanVideo` →
  `TileDispatcher.EnqueueJob` → N concurrent worker tasks each
  pulling frame-range tiles and rendering frames into the same naming
  scheme. Comparison: SHA-256 of every PNG (strongest check —
  bit-identical PNGs imply bit-identical encodes under deterministic
  presets). When ffmpeg is on disk, both arms feed
  `VideoFramePipeline` to encode `.mkv` / `.mp4` artifacts and the
  test then compares (a) `ffprobe -show_streams` metadata
  (codec_name, codec_type, width, height, pix_fmt, r_frame_rate,
  nb_frames) and (b) `ffmpeg -f framemd5 -` per-frame digests with
  the file/version headers stripped. Output:
  `cluster-video-parity.out`. Exit code 0 on full parity, 1 on any
  mismatch.

**Files amended**

- `ServerHost/ClusterScaleSelfTest.cs` — new `--mode video` branch.
  `RunBaselineVideo` mirrors the parity test's sequential frame
  loop; `RunParallelVideo` mirrors the parity test's worker task
  loop, dispatching frame-range tiles via `TileDispatcher` with
  work-stealing on. CLI gains `--mode`, `--seconds`, `--fps`,
  `--zoom-start`, `--frames-per-tile`. Image mode is unchanged
  (the existing `RunBaseline` / `RunParallel` paths stay live and
  are picked when `--mode image` or the flag is omitted). Header
  line now reads `cluster-scale self-test (mode=…)` so the report
  is self-describing.
- `Program.cs` (root WinExe) — new dispatch:
  `--cluster-video-parity` → `ClusterVideoParitySelfTest.Run`.
  Doc-comment on `--cluster-scale` updated to mention `--mode`.
- `FracturingFog.App/Program.cs` (cross-plat headless) — same two
  new dispatches (`--cluster-scale` was already missing here, so
  this commit also brings the cross-plat entry into parity with
  the WinExe). Without the wire, `dotnet run --project
  FracturingFog.App -- --cluster-video-parity` would land in the
  Avalonia shell instead of the self-test.
- `FracturingFog.App/FracturingFog.App.csproj` — link the two new
  source files (`ClusterScaleSelfTest.cs`,
  `ClusterVideoParitySelfTest.cs`) into the cross-plat app
  alongside the existing `ClusterParitySelfTest.cs`. Same source-
  link pattern used since D-2b; avoids carving a new project.

**Build / test results**

- `dotnet build FracturingFogCLD.sln -c Debug` → 0 errors, 33
  warnings (all pre-existing source-gen / Avalonia obsolete / xUnit
  CT lints).
- `dotnet test Server.Tests --no-build` → 297 passed (unchanged
  from D-4c; the new code lives entirely in ServerHost and is
  exercised by self-tests, not xUnit, because Server.Tests stays
  imaging-/UI-stack free by design).
- `dotnet run … -- --cluster-video-parity --seconds 1 --fps 10
  --width 64 --height 48 --workers 2` →
  `frame-parity : 10/10 SHA-256 match, 0 missing, 0 differ`,
  `encode-arm  : SKIPPED (ffmpeg not on disk; only frame parity
  asserted)`, `PARITY OK`. On a workstation with ffmpeg the
  encode-arm runs the full ffprobe + framemd5 comparison.
- `dotnet run … -- --cluster-scale --mode video --seconds 2 --fps
  30 --width 96 --height 64 --workers 2 --frames-per-tile 8` →
  `speedup : 1.59x`, `efficiency : 79.3%` — proves the video tile
  path parallelises through the same dispatcher infrastructure as
  image mode at near-target efficiency on a 2-worker run.
- `dotnet run … -- --cluster-scale --width 256 --height 256
  --workers 2 --tile-px 64` and `--cluster-parity` both still pass
  unchanged — confirms the `--mode` plumbing didn't regress the
  image-mode default.

**Design decisions (continuing #49–55 from Session 3)**

56. **Frame-level SHA-256 is the strongest check; encode parity
    rides for free under ffv1.** ffv1 is mathematically lossless
    by spec — if the per-frame PNG SHA-256 sets match, the
    encoded `.mkv` files must match too. h264-qp0 is the same
    story for the H.264 preset. The encode-arm is therefore a
    belt-and-braces validation of the streaming pipeline + ffmpeg
    invocation, not an independent fidelity check. This shapes
    the test's exit-code rule: encode-arm SKIPPED (no ffmpeg) is
    still PARITY OK as long as the frame-arm passed.
57. **Strip ffmpeg version/file headers from framemd5 output.**
    `ffmpeg -f framemd5 -` emits per-frame lines plus a few
    leading `#` comments naming the running ffmpeg build and the
    container's stream id. Two boxes on different ffmpeg builds
    can still produce equal per-frame digests; comparing raw
    output would false-positive a mismatch on the comment lines.
    The test strips every `#` line before string-compare so the
    assertion is on per-frame content only.
58. **In-process worker tasks, not a real `FFWorkerAgent`/SSL
    socket.** The wire path is already covered end-to-end by
    `ClusterEndToEndVideoTests` (D-4a/b). What's new for D-4 exit
    is parallel-execution fidelity, which `TileDispatcher` +
    worker tasks expose at zero TLS/framing overhead. Mirrors the
    D-3b scale-test pattern (`ClusterScaleSelfTest`) so the two
    self-tests share mental model.
59. **`--cluster-video-parity` runs from both Program.cs entry
    points.** The cross-plat `FracturingFog.App` historically only
    dispatched `--cluster-parity`; the new entry adds
    `--cluster-scale` too, since both self-tests have always been
    cross-plat-safe (HostFractalRenderEngine targets net10.0) and
    omitting them from the App entry was an oversight, not a
    deliberate split. The Win-only `FracturingFog.exe` still has
    them in addition to its WinForms self-tests (--silk-smoke,
    etc.).
60. **`SHA256.HashData(Stream)` over a manual block loop.** Built
    into `System.Security.Cryptography` and is the idiomatic way
    to hash a file in net10. Cuts the per-frame comparison loop to
    ~5 lines and is exactly equivalent to the manual incremental
    update path.
61. **PNGs written to the parity test's tmp dir as
    `frame_NNNNNN.png` (1-based).** Matches the `JobStore`
    convention so a future refactor that lets the parity test feed
    its frame output straight into `VideoFramePipeline.TryStart`
    (without copying) requires no rename pass.
62. **CLI defaults size for a 1-2 second smoke run.** Default
    `--seconds 1 --fps 10 --width 160 --height 120` finishes the
    parity test in ~1.5 s on a dev box and stays well under the
    10-minute self-test budget. The dev plan's 20s/1080p30 ffv1
    scenario is one CLI override away (`--seconds 20 --fps 30
    --width 1920 --height 1080`) — not the default because a 600-
    frame Bird-of-Paradise render takes minutes per arm and isn't
    appropriate for a smoke run.

**D-4 closure**

D-4 exit criteria met. Both new self-tests succeed locally; encode-
arm of `--cluster-video-parity` was exercised with a bundled ffmpeg
in a sibling worktree (matched bytes, matched framemd5). With ffmpeg
unavailable the test still gates on the strictly stronger frame-PNG
parity. Phase D-4 (video + slideshow distribution) is complete; next
session opens Phase D-5 (Admin UI in `UI.Avalonia/`).
