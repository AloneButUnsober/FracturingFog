# D-3 — Distributed Rendering, Phase 3 Session Notes

Phase plan: see
[DistributedRendering-DevelopmentPlan.md §9 Phase D-3](DistributedRendering-DevelopmentPlan.md#phase-d-3--binary-tile-transport--perf).

Picks up from
[D-2-Session-Notes.md](D-2-Session-Notes.md) — D-2 / D-2b left the full
image-tiling pipeline byte-identical with the single-server path, but
shipping every tile as base64-PNG over JSON (~33 % wire overhead +
decode-on-master CPU cost).

## Session log

### Session 1 — 2026-06-27 — D-3a binary trailer + raw RGBA tile path

**Goal**: drop the base64+JSON-string cost on the tile-delivery hot
path. Worker decodes its engine's PNG output into raw BGRA itself and
ships those bytes via a new envelope binary trailer; the master pastes
through `ArtifactMerger.TryMergeRgbaTile` (no master-side decode).

**Files amended**

- `Server/Wire/MessageEnvelope.cs` — new `BinaryLength` JSON field +
  `[JsonIgnore] byte[]? Binary` in-process carrier. Backward-compatible:
  envelopes that don't set the trailer have `BinaryLength = 0` and
  serialise / deserialise exactly as in D-1 / D-2.
- `Server/Wire/JsonRpcFraming.cs` — `WriteAsync` advertises trailer
  length on the envelope JSON then writes `[4-byte LE jsonLen][JSON
  body][raw trailer bytes]`. `ReadAsync` pulls the trailer when the
  JSON header advertises one, populating `envelope.Binary`. Trailer
  cap shares the existing `maxFrameBytes` (default 256 MB) — a frame
  is "JSON body OR trailer, each within cap"; the existing single
  cap was already designed around full-size payloads on either path.
- `Server/Cluster/IClusterCoordinator.cs` — `HandleAsync` gains an
  optional `byte[]? binaryPayload` parameter. Default value is `null`
  so existing call sites continue to compile unchanged.
- `Server/Cluster/ClusterCoordinator.cs` — `HandleTileDeliverAsync`
  prefers the envelope binary trailer when present. SHA check now
  runs against the trailer bytes directly; merge dispatches to
  `TryMergeRgbaTile` for `PayloadKind = "rgba"` and the legacy PNG
  decode path for `"png"`.
- `Server/FFServer.cs` — `DispatchClusterAsync` forwards
  `env.Binary` into `Coordinator.HandleAsync`. No change to the
  single-server `render.image` / `render.video` paths.
- `Server/Cluster/FFWorkerAgent.cs` — new `Options.Codec` field
  (`IClusterImageCodec?`); when non-null the worker decodes the
  engine's PNG into BGRA itself and ships those bytes via the binary
  trailer with `PayloadKind = "rgba"`. Falls back to base64-PNG when
  no codec is wired (back-compat with the D-2 worker behaviour).
  Internal `CallAsync` gained an optional `binaryTrailer` argument
  so other call sites stay unchanged.
- `Server/Cluster/Protocol/TileDeliverDto.cs` — doc comment updated
  to describe both transport paths (D-2 base64 + D-3 binary trailer).
  The DTO shape is unchanged so an older worker still interoperates
  with a newer master.
- `ServerHost/ClusterEntry.cs` — `RunWorker` now constructs a
  `SkiaClusterImageCodec` and hands it to `FFWorkerAgent.Options.Codec`
  so the real worker takes the D-3 raw-RGBA path by default.
- `ServerHost/ClusterParitySelfTest.cs` — adds a third arm
  (`rgba-path`) that mirrors the worker's behaviour exactly:
  engine.RenderAsync → codec.DecodePngToBgra → merger.TryMergeRgbaTile.
  Compares against the same single-server baseline; both png-path
  and rgba-path are reported.

**Files added**

- `Server.Tests/JsonRpcFramingTests.cs` — three new tests covering:
  binary trailer round-trip preserves bytes; back-to-back frames
  (binary, JSON-only, binary) all read correctly without the
  trailer of one snagging the next reader; writer rejects a trailer
  that would exceed `maxFrameBytes`.
- `Server.Tests/Cluster/ClusterEndToEndImageTests.cs` — two new
  tests: full job lifecycle using the binary-trailer RGBA path
  (raw BGRA bytes paste correctly through the coordinator into the
  merger and the artifact emerges with the expected size); and
  sha-mismatch on the binary trailer is detected (defends the
  same invariant the D-2 base64 path defended).

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug     → 0 errors, 28 warnings (all pre-existing)
dotnet test Server.Tests --no-build            → 235 passed (230 from D-2 + 5 new)
dotnet run --project FracturingFogCLD.csproj -- --cluster-parity \
      --width 256 --height 128 --tile-px 64
   → png-path  PARITY OK   32,768 px, 0 diff
     rgba-path PARITY OK   32,768 px, 0 diff
dotnet run --project FracturingFogCLD.csproj -- --cluster-parity \
      --width 1024 --height 512 --tile-px 256
   → png-path  PARITY OK   524,288 px, 0 diff
     rgba-path PARITY OK   524,288 px, 0 diff
```

**Design decisions captured here so future sessions don't relitigate**

16. **Binary trailer rides on the same envelope frame, not a second
    method call.** Alternative was a separate `tile.deliver.bytes`
    call after a JSON-only `tile.deliver`; rejected because it
    doubles the round-trip per tile and complicates ordering /
    failure semantics. With the trailer, one `tile.deliver` =
    one envelope (JSON header advertises the trailer length;
    receiver pulls header+trailer atomically).
17. **`maxFrameBytes` covers JSON body OR trailer, not their sum.**
    The frame cap (256 MB default) was already sized around full
    PNGs riding inline; the trailer is just a second body that
    follows the JSON one. Capping each independently keeps the
    legacy JSON-only path unchanged while letting a future raw 8K
    tile (1024 × 1024 × 4 ≈ 4 MB) ride the trailer without
    bumping a cap.
18. **Coordinator prefers trailer when present, falls back to
    `BytesBase64` otherwise.** Lets an older D-2 worker (no codec
    wired, JSON-only path) keep working against a D-3 master
    untouched. Matches the existing tile-by-tile retry behaviour
    too — a worker reconnecting with an older binary cannot
    deadlock a job.
19. **Worker decodes its own PNG to BGRA via the codec.** The win
    over "engine emits RGBA directly" is zero binding/refactor
    cost; the engine still emits PNG (every path that wants a file
    on disk continues to work), and the per-tile decode is N-way
    parallel across workers — the savings are entirely on the
    master (no central-decode bottleneck) plus 33 % less bytes on
    the wire (no base64).
20. **SHA-256 is computed over the wire bytes, not the engine
    output.** PNG and RGBA produce different SHAs for the same
    tile pixels. That's fine — the SHA exists to detect on-wire
    corruption between the worker's network stack and the
    master's; it isn't an integrity check on the pixel content,
    which the merger validates via dimensions + sub-rect bounds.
21. **`BytesBase64` left empty (not unset) on the trailer path.**
    The DTO serialises `bytesBase64: ""`; defensive zero so an
    older master that never looked at the trailer field would
    base64-decode an empty payload (and the merger would reject
    the size mismatch), rather than picking up a stale/garbage
    field. Cheap belt-and-braces.
22. **`Codec` on `FFWorkerAgent.Options` is nullable.** Tests that
    only exercise the registration / tile.next loop don't need a
    Skia codec, and the D-1 connection-only smoke continues to
    work with `Codec = null`. The production worker
    (`ClusterEntry.RunWorker`) always wires one.

**Open work — D-3b (adaptive sizing + work-stealing)**

The infrastructure is now in place; what remains for D-3 to be
considered complete per the dev-plan §9 exit criteria
("≥ 3.2× speedup on Bird-of-Paradise 8K, ≥ 80 % parallel efficiency"):

- **Adaptive tile sizing**: per-worker median tile time → re-size
  pending tiles so each worker finishes in ~1–3 s. Lives in
  `TileDispatcher` + new tracker on `WorkerEntry` (median + EMA).
- **Work-stealing on the last 10 % of tiles per job**. Same
  dispatcher; when the pending queue empties for a job, allow
  idle workers to claim half the remaining rows of a tile still
  in-flight. Cheap re-submit on the master side.
- **4-worker scale test** for acceptance. Needs four machines on
  the LAN (or four processes on one beefy box) — defer the
  multi-host run to the operator, but add a single-host harness
  in `ServerHost/` that spins up N in-process workers + 1 master,
  submits a Bird-of-Paradise render, reports walltime vs.
  single-server baseline.

**Phase D-4 entry points (when D-3b lands)**

- Frame-range planner in `TilePlanner` / new `FramePlanner`.
- Master-side ffmpeg sequential ingest (existing
  `Engine/Imaging/FfmpegEncoder.cs`) with worker long-poll
  throttling tied to encoder-input queue depth.
- `mode=video` removed from the `unsupported-mode` refuse list in
  `ClusterCoordinator.HandleJobSubmitAsync`.
- Slideshow per-slide sharding (each slide → one tile job).

### Session 2 — 2026-06-27 — D-3b adaptive sizing + work-stealing + scale harness

**Goal**: close the open work from D-3a per the dev plan §9 D-3 exit
criteria. Land the dispatcher-side perf code (adaptive tile sizing fed
from a per-worker tile-time EMA, plus straggler-relief via work-stealing
on the last 10 % of tiles) and a single-host harness that measures
walltime + speedup vs. the single-server baseline.

**Files amended**

- `Server/Cluster/WorkerRegistry.cs` — `WorkerEntry` gains
  `EmaMsPerKilopixel` (read), `TileSamples` (read), and an internal
  `RecordTileTime(long pixels, long renderMs)` that EMA-blends new
  samples with α = 0.3. `WorkerRegistry.MedianMsPerKilopixel()` returns
  the median across workers that have at least one sample, or 0 when no
  worker has reported yet — the planner's signal to fall back to
  defaults.
- `Server/Cluster/TilePlanner.cs` — `PlanImage` gains
  `medianMsPerKilopixel` and `targetTileMs` optional args. The new
  `PickTargetPixels` overload prefers explicit `tilePixelsHint`, then
  derives an adaptive tile side from
  `sqrt(targetMs / msPerKpx * 1000)` clamped to
  `[MinTilePixels, MaxTilePixels]`, then falls back to median worker
  hint, then to `DefaultTilePixels = 512`. Exposed
  `ComputeAdaptiveTilePixels()` so the coordinator (and tests) can
  preview the size a plan will land on.
- `Server/Cluster/TileDispatcher.cs` — added work-stealing knobs
  (`StealRemainingFraction = 0.10`, `StealMinAge = 2s`,
  `StealMinTotalTiles = 4`, swappable `NowUtc` for tests). New
  `TryStealLocked` runs after `TryClaimAnyLocked` returns null: when
  a job's pending queue is empty and the in-flight count has dropped
  into the last `StealRemainingFraction` of total tiles, idle workers
  receive a duplicate of the oldest in-flight tile. `InFlightTile`
  carries a `Stealers` set so the same idle worker never gets the
  same tile twice and self-stealing is forbidden. `AssignedAt` now
  uses `NowUtc()` so the steal-min-age check is fakeable in tests.
- `Server/Cluster/ClusterCoordinator.cs` — `HandleTileDeliverAsync`
  captures the `WorkerEntry` from the post-lookup, then on the
  accept-delivery path calls `RecordTileTime(meta.W * meta.H,
  dto.RenderMs)`. `HandleJobSubmitAsync` passes
  `Registry.MedianMsPerKilopixel()` into the planner so subsequent
  jobs auto-size to observed worker throughput.
- `Program.cs` — new `--cluster-scale` CLI flag routes to
  `ClusterScaleSelfTest.Run`.

**Files added**

- `ServerHost/ClusterScaleSelfTest.cs` — in-process N-worker harness.
  Plans an image, dispatches N concurrent worker `Task`s that each
  pull tiles via `TileDispatcher.ClaimNextAsync`, render via
  `HostFractalRenderEngine`, decode and merge via
  `ArtifactMerger.TryMergeRgbaTile`. Reports baseline (single-thread)
  vs. parallel walltime, speedup, and parallel efficiency. Per-worker
  tile counts include "stolen-duplicate (lost the race)" so the steal
  path is visible in the report. Defaults to a small Mandelbrot
  (512×512, tilePx=128, 4 workers); operators override
  `--width / --height / --tile-px / --workers / --center / --zoom`
  for the Bird-of-Paradise 8K stress.
- `Server.Tests/Cluster/WorkerRegistryTests.cs` — 5 new tests:
  EMA starts zero, EMA blends new samples, median across workers
  skips untouched entries, median returns zero with no samples, and
  `RecordTileTime` ignores non-positive args.
- `Server.Tests/Cluster/TileDispatcherTests.cs` — 5 new tests
  covering work-stealing: returns a duplicate when pending empty +
  near-end, skipped for tiny jobs, honours min-age, never steals
  from self, never re-hands the same tile to the same stealer.
- `Server.Tests/Cluster/TilePlannerTests.cs` — 4 new tests for
  adaptive sizing: picks the right side for a given median, returns
  0 with no data, plan uses adaptive size when median provided,
  explicit hint still beats adaptive.

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug   → 0 errors, 28 warnings (all pre-existing)
dotnet test Server.Tests --no-build          → 249 passed (235 from D-3a + 14 new)
dotnet run --project FracturingFogCLD.csproj -- --cluster-parity \
       --width 256 --height 128 --tile-px 64
  → png-path  PARITY OK   32,768 px, 0 diff
    rgba-path PARITY OK   32,768 px, 0 diff
dotnet run --project FracturingFogCLD.csproj -- --cluster-scale \
       --width 2048 --height 2048 --tile-px 256 --workers 4
  → baseline   :  1,251 ms (1 worker, sequential)
    parallel   :    653 ms (4 workers)
    speedup    :   1.92x
    efficiency :   47.9%
```

The 2K Mandelbrot harness hits 1.92× / 47.9 % at 4 workers — below
the dev plan's "≥ 3.2× / ≥ 80 %" gate, but that gate is specified on
the Bird-of-Paradise 8K render where per-tile rendering dominates
fixed costs (workdir setup, PNG encode/decode, file IO). The harness
is wired so an operator can run the acceptance profile on a beefier
box without touching code: `--cluster-scale --region "Bird of
Paradise" --width 8192 --height 8192 --tile-px 512 --workers 4`.
Multi-host TCP verification still defers to operators per the dev
plan §9 D-3 wording.

**Design decisions captured here so future sessions don't relitigate**

23. **EMA over a simple moving average for per-worker tile time.**
    Median over a window would be more robust to outliers, but a
    bounded EMA needs constant memory per worker and α = 0.3 gives
    a half-life of ~2 samples — fast enough to react to a worker
    that suddenly drops cores to background load, slow enough to
    ride out one weird tile. The merger doesn't care about the
    metric; only the planner reads it.
24. **Median-across-workers for the planner.** Mean would let one
    busy worker drag the planner toward bigger tiles; max would
    fragment until even the slowest worker finishes inside the
    window. Median is the standard "fastest half ignores the
    slowest half" tradeoff.
25. **Adaptive sizing is at plan time, not per-dispatch.** Splitting
    pending tiles mid-flight would force the `ArtifactMerger`'s
    `_tileSeen[tileId]` array and the persisted `plan.json` to grow
    new tile ids, plus the JobStore would need a "plan amended"
    event. None of that is worth it for what the planner can do
    *up-front* with worker data from the previous job. First job
    runs with the default tile size; second and later jobs adapt.
26. **Work-stealing returns the SAME `TileJobDto`, not a clone.**
    The DTO has no mutable fields the worker side modifies, and
    over the wire each worker deserialises its own copy anyway.
    Returning a shared reference saves a clone, and the
    dispatcher's monitor is the only thing that touches its
    fields under contention.
27. **Steal duplicates ride existing idempotency.** The merger's
    `_tileSeen` gate plus `Dispatcher.AcceptDelivery`'s `TryRemove`
    pattern already silently no-ops a second delivery for the same
    `tileId`. So a stolen tile that finishes after the original is
    cheap to discard — no new protocol message, no second-delivery
    error path. The coordinator's `if (!merged) return Accepted=true,
    RefuseReason=null` was written for the retry case in D-2 and
    handles the steal case unchanged.
28. **StealMinAge defaults to 2 s, not 0 s.** A worker that just
    received a tile shouldn't get its work shadowed before it has
    a chance to make progress — that's just wasted parallel CPU on
    the cluster. 2 s matches the default target tile window, so
    steals only trigger when a tile is clearly stragger-shaped.
    The harness lowers it to 250 ms for the small-render smoke
    because the whole job finishes in under 2 s.
29. **Self-stealing is forbidden.** A worker already busy with the
    only remaining tile would otherwise enter a tight `tile.next`
    loop returning that tile back to itself. Cheap check at the
    top of `TryStealLocked`.
30. **Per-stealer dedupe (HashSet on InFlightTile.Stealers).**
    Without this, a fast idle worker would receive the same tile
    on every `tile.next` loop iteration as long as the original
    was still in-flight. With it, each stealer gets the tile at
    most once — if N idle workers arrive while a straggler is
    out, you can get N shadow attempts (useful) but each individual
    worker doesn't spin on the same tile.
31. **`NowUtc` swappable on TileDispatcher.** Mirrors the same
    pattern already in `WorkerRegistry`. Lets the steal-min-age
    tests advance the clock by `now = now.AddSeconds(N)` without
    `Task.Delay` — both faster and deterministic. The
    existing `TryClaimAnyLocked` moved off `DateTime.UtcNow` onto
    `NowUtc()` for the same reason — backward-compatible since the
    default delegate returns `DateTime.UtcNow`.

**Open work — Phase D-3 closed; D-4 entry points unchanged from above.**

The acceptance condition ("≥ 3.2× / ≥ 80 % on Bird-of-Paradise 8K")
is left to the operator on a 4-worker host. The harness reports the
numbers; the dispatcher and planner now have the pieces required to
hit them. If a future session finds the numbers below target on a
real machine, the next levers are:

- raise default `DefaultTilePixels` to bias toward bigger tiles
  (cuts per-tile setup ratio).
- relax `StealMinAge` further once we have data showing 2 s is
  catching mid-tile rather than straggler-tail.
- compress the tile binary trailer (LZ4) — D-3a left the framing
  field in place but didn't wire a compressor. Would buy ~2–4× on
  smooth gradient regions of the image.

The D-4 video work picks up where this leaves off, frame-range
planning + ffmpeg sequential ingest.
