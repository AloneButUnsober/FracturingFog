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
