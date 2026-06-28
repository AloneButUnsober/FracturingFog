# D-6 Session Notes — Hardening & Polish

Phase D-6 from `DistributedRendering-DevelopmentPlan.md`. Goal: harden
the cluster surface that D-1..D-5 stood up. The §9 list:

1. Crash recovery (resume `rendering` jobs after master restart).
2. Reference-orbit caching for perturbation deep zooms.
3. Per-role rate limiting.
4. Operator doc at `Docs/User/Distributed-UserGuide.md`.
5. 8-worker / 200-job stress test in `Server.Tests`.

Each lands as its own sub-slice (D-6a..D-6e) to keep commit cadence
matching D-5a..D-5e.

---

## Session 1 — D-6a — Master crash recovery for image jobs (2026-06-28)

**Goal**: a master process that died mid-render no longer abandons its
in-flight image jobs. On restart, every non-terminal image job replays
its on-disk tiles into a fresh `ArtifactMerger` and re-enqueues the
remaining tiles into the dispatcher under their original ids. The job
status transitions back to `rendering` (or `queued` if nothing had been
delivered yet) and the artifact finalises whenever the last tile
arrives — whether that happens immediately at resume time or later from
a live worker.

**Sub-slice decision**: image only for D-6a. Video and slideshow tile
streams (frame ranges, per-slide PNGs) are recoverable but need their
own dispatcher seam — the streaming ffmpeg pipeline has to be rebuilt
from disk for video, and slideshow manifests live one finaliser step
past the per-slide store. Both modes keep the D-5e behaviour of
flipping to `failed` with reason `master-restart` until a later sub-
slice picks them up (likely D-6a2 / D-6a3 if they justify their own
commits; otherwise rolled into D-6e polish). The `FailInflightAfterRestart`
sweep stays on the `JobStore` API because the coordinator's fall-back
path uses the same status transition.

**Server-side changes**

- `Server/Cluster/JobStore.cs`:
  * New `EnumerateResumableJobs()` → `IEnumerable<ResumeRecord>` yields
    one record per job in `queued | planning | rendering | merging`.
    Skips jobs missing `status.json` or `request.json` silently — they
    cannot be resumed.
  * New `ListTilesOnDisk(jobId)` → sorted list of int tile ids whose
    `.bin` files exist under `tiles/`. Used by the coordinator to skip
    already-delivered tiles.
  * New `ResumeRecord(JobId, Status, Submit)` record at the bottom of
    the file. The submit DTO travels with the status so the coordinator
    doesn't re-read `request.json` per job during the resume loop.
- `Server/Cluster/ClusterCoordinator.cs`:
  * New `RecoverFromDisk()` → `ResumeCounts` (Considered / ResumedImage
    / FailedUnsupportedMode / Failed). Walks `Jobs.EnumerateResumableJobs`,
    dispatches image jobs to `TryResumeImageJob`, falls back to
    `FailResumeJob("master-restart")` for video / slideshow.
  * `TryResumeImageJob` rebuilds the merger from plan rects (computing
    `width` / `height` as `max(OffsetX + Width)` / `max(OffsetY + Height)`),
    sniffs each on-disk tile as PNG vs. raw BGRA, replays them through
    the merger, then enqueues the missing tile dtos via
    `Dispatcher.EnqueueJob`. If every tile is already on disk the merge
    finalises in-place via the existing `FinaliseMerge` path.
  * `IsPng` byte-sniff for the 8-byte PNG signature
    (`89 50 4E 47 0D 0A 1A 0A`) — corruption-tolerant: a tile whose
    sniff path throws is dropped from the done set and re-enqueued so
    one bad `.bin` doesn't fail the whole job.
  * `ReadPlanTileDtos` deserialises `plan.json` back into
    `IReadOnlyList<TileJobDto>` so the re-enqueued tiles carry their
    original render parameters (centre, zoom, theme, region). Uses
    `JsonRpcFraming.JsonOpts` — the camelCase property names on
    `TileJobDto` are explicit so round-trip is stable.
- `ServerHost/ClusterEntry.cs`:
  * `RunMaster` calls `coord.RecoverFromDisk()` after wiring the
    coordinator and before `server.RunAsync`. Counts are printed when
    any job was considered so the operator sees a one-line summary
    instead of silent "ghost" jobs in the dashboard.

**Tests**

- New `Server.Tests/Cluster/CrashRecoveryTests.cs` — 7 cases:
  * empty store is a no-op
  * terminal jobs (`ready`) are untouched and the dispatcher does not
    learn about them
  * image job with no tiles on disk → all tiles re-enqueued, status
    goes back to `queued`
  * image job with some tiles on disk → only the remainder re-enqueued,
    status stays `rendering`, `TilesDone` reflects the replay count
  * image job with every tile on disk → finalises in-place, dispatcher
    retires the job, artifact PNG is written
  * video job in flight → flipped to `failed` with `master-restart`
    reason
  * corrupt on-disk tile (wrong byte length, fails the merger) →
    dropped from done set and re-enqueued; the good neighbour still
    counts as replayed

- Test suite: **323 passed, 0 failed** (was 316; +7 recovery tests).
  No existing test churn — the new `RecoverFromDisk` entry point is
  opt-in and the legacy `FailInflightAfterRestart` API is unchanged
  (the JobStore test still passes against it).

**Design decisions**

#89. Resume is opt-in on the coordinator, not implicit in JobStore.
  Reason: tests + the legacy `FailInflightAfterRestart` path want a
  store that doesn't side-effect on construction; making the JobStore
  itself walk and replay on startup would force every test harness to
  either pre-clean its temp dir or accept replay surprises. Keeping
  the entry point on `ClusterCoordinator.RecoverFromDisk()` puts the
  decision at the host layer where lifecycle already lives.

#90. PNG vs. RGBA distinguished by magic-byte sniff, not by a stored
  payload-kind tag. Reason: changing `tiles/{id}.bin` to
  `tiles/{id}.{kind}` would break every pre-D-6a job on disk and
  force a migration. The PNG signature is 8 bytes, deterministic, and
  cannot collide with the raw BGRA representation of any tile (a
  64×64 RGBA buffer is 16 KB, never starts with `89 50 4E 47`). The
  cost of a wrong sniff is a single decode exception that the
  recovery path catches and re-enqueues the tile for — same as the
  corrupt-bytes case.

#91. Width / height recomputed from the plan's tile rects rather than
  stored on `PersistedStatus`. Reason: the original
  `JobSubmitDto.Request.Width/Height` are the *requested* dims, which
  the planner pads up if the tile target doesn't divide evenly — so
  the merger must be sized to the plan's actual rect extent, not the
  request. Computing `max(OffsetX + Width)` over the plan's tile list
  is O(tiles) and runs once per resume, well under a millisecond even
  for a 10K-tile poster.

#92. Corrupt-tile recovery drops the bad tile from the done set, not
  the whole job. Reason: a half-written `.bin` from a master killed
  mid-write is the expected failure mode this whole feature exists
  to handle; failing the parent job because the dispatcher would
  otherwise need to re-render one tile defeats the purpose. The
  dispatcher's retry budget covers a worker re-rendering the
  replacement, and the merger is idempotent — a duplicate delivery
  after resume is a no-op.

#93. `FailInflightAfterRestart` survives instead of being removed.
  Reason: video / slideshow modes still need the fail-on-restart
  fallback (their tile streams aren't replayable yet) and the
  `JobStore` test already asserts the legacy behaviour. Removing the
  API would force a parallel coordinator-layer helper for the same
  effect with no upside. When the video / slideshow resume slices
  land the API can shrink or move; for now it stays.

#94. ResumeRecord carries `Submit` even though `TryResumeImageJob`
  doesn't read it directly. Reason: the video / slideshow resume
  paths (D-6 follow-ups) will need the submit DTO for the streaming
  encoder + slide manifest re-spawn respectively. Surfacing it now
  keeps the wire shape stable; the cost is a per-job
  `JsonSerializer.Deserialize<JobSubmitDto>` on startup, which is
  bounded by the on-disk job count (capped by the retention sweep).

**Build + test**

- Solution build (Debug): 0 errors, pre-existing AVLN5001
  `TextBox.Watermark` obsoletes + codegen CS0219 warnings only (24
  warnings unchanged from D-5e).
- Test suite: **323 passed, 0 failed** (was 316; +7 in new
  `CrashRecoveryTests`).
- Filtered run `--filter "FullyQualifiedName~CrashRecoveryTests"`:
  7 passed in 258 ms.

**Next session** opens D-6b: reference-orbit caching for perturbation
deep zooms. §7.7 of the dev plan: compute the reference orbit once on
the master, attach it to `TileJobDto.ReferenceOrbitBlob`, and have the
worker skip the per-tile re-derivation. Wire-shape change is additive
(new optional blob field); the engine seam is the
`SeriesApproximation` calculator path identified in §4 image-tiling
caveats.

---

## Session 2 — D-6b — Shared reference orbit for image tiles (2026-06-28)

**Goal**: Mandelbrot image jobs at zoom ≥ 1e8 (perturbation engaged) and
≤ 1e25 (DD precision sufficient) compute one DD reference orbit on the
master and ship it to every tile, so each worker seeds its calculator
instead of re-deriving the same orbit per tile. The shipped tile carries
the full image's centre + dims + the tile's pixel offset; the calculator
derives per-pixel dc from the IMAGE coordinate system so every tile of
the same image shares the orbit bit-for-bit. Out-of-range jobs (low
zoom, QD+ zoom, non-Mandelbrot, video, slideshow) fall through to the
existing per-tile compute — wire change is purely additive.

**Sub-slice decision**: v1 ships DD precision only (limbs=2 in the blob
header). QD (zoom > 1e25) and OD (zoom > 1e50) orbit shipping is a
follow-up: the wire format reserves the limbs byte for that growth.
SIMD PT4/PT8 fall back to scalar when sub-rect mode engages — the dc-
origin shift adapts cleanly to the scalar path's `(SubRectOffset + x -
halfW_image) * scale` formula, but retrofitting the SIMD inner loops to
the new origin would touch a lot of carefully-tuned code for a perf
follow-up that's only worthwhile under measurement. SIMD adaptation
lands in D-6b1.

**Server-side changes**

- `Server/Protocol/RenderRequestDto.cs`:
  * New optional fields: `ImageWidth`, `ImageHeight`, `SubRectOffsetX`,
    `SubRectOffsetY`, `RefOrbitBlobBase64`, `RefOrbitMaxIter`. All zero/
    null on legacy single-server requests — geometry unchanged.
- `Server/Cluster/ReferenceOrbitBlobCodec.cs` (new):
  * Binary blob format: 44-byte header (magic 0xD6, format v1, limbs=2,
    escaped flag, refLen, maxIter, centreX/XLo/Y/YLo) + Zr Hi / Zi Hi /
    Zr Lo / Zi Lo arrays sized `refLen + 1`. Base64-wrapped on the wire
    so it fits the existing JSON-RPC envelope (binary trailer support is
    a perf path for D-6b1 if blob size justifies it — DD orbits at 1M
    iters are ~32 MB which is borderline).
- `Server/Cluster/TilePlanner.cs`:
  * New `QualifiesForSharedReferenceOrbit(submitRequest)` — gates on
    Mandelbrot + zoom in `[SharedRefOrbitMinZoom (1e8), SharedRefOrbitMaxZoom
    (1e25)]`.
  * New `AttachSharedReferenceOrbit(plan, submitRequest, blob, maxIter)` —
    rewrites every tile into image-frame mode: tile.Render.CenterX/Y set
    to the IMAGE centre, tile.Render.Zoom set to the IMAGE zoom, tile.
    Render.ImageWidth/Height set to the image dims, tile.Render.SubRectOffsetX/Y
    set from the tile's plan offset, blob attached.
- `Server/Cluster/ClusterCoordinator.cs`:
  * New `ReferenceOrbitProvider` init-only delegate (`Func<centreX,
    centreXLo, centreY, centreYLo, maxIter, (byte[], int)?>?`). Server
    library stays Engine-free — the host wires the Engine-side compute.
  * `HandleJobSubmitAsync`: after `Jobs.Create` would have run, calls
    the provider when qualifying conditions hold, attaches the blob via
    `TilePlanner.AttachSharedReferenceOrbit`, persists the modified
    plan. Provider failure or null return leaves the plan untouched and
    logs `ref-orbit-attach-failed`; success logs `ref-orbit-attached`.

**Engine-side changes**

- `Engine/Calculators/MandelbrotCalculator.cs`:
  * New public properties: `ImageWidth`, `ImageHeight`, `SubRectOffsetX`,
    `SubRectOffsetY`. Defaults zero; the calculator's `EffectiveImageWidth/
    Height` getter falls back to `Width/Height` so legacy renders are
    bit-identical (the new dc formula collapses to the legacy
    `(x - Width*0.5) * scale` by arithmetic when offsets are zero).
  * `CalculateHighPrecision` — scale derives from the effective image
    dims; dcMaxAbs uses the effective image corner. SIMD PT4 / PT8
    forced off when sub-rect mode active.
  * `ComputeRowPTScalar` — halfW/H measured from the effective image,
    per-pixel offset adds `SubRectOffsetX/Y` so dc reflects the pixel's
    position in image coordinates (not tile-local). HP fallback inside
    the row (DD/QD/OD `FromCenterOffset` calls) gets the same shift.
  * New `OrbitDD` record + public static `ComputeReferenceOrbitDDPublic`
    — mirrors the private `ComputeReferenceOrbit(DD,DD,int)` math so the
    master computes orbits without inheriting the calculator's instance
    state.
  * New instance method `SeedReferenceOrbitDD(orbit)` — pre-fills the
    `_refZr/Zi/Lo`, `_refCx*/Cy*`, `_refOrbitLen`, `_refCachedMaxIter/
    Escaped` and bumps `_refOrbitGen`. The calculator's next
    `ComputeReferenceOrbit` sees centerSame == true and short-circuits.
- `Engine/Imaging/PosterRenderer.cs`:
  * `PosterRequest` gains `ImageWidth/Height/SubRectOffsetX/Y` and
    `SeededOrbit`. Mandelbrot branch forwards to the calculator and
    calls `SeedReferenceOrbitDD` when an orbit is supplied.

**Host-side changes**

- `ServerHost/HostFractalRenderEngine.cs`:
  * When `req.RefOrbitBlobBase64` is present on a Mandelbrot tile,
    decodes via `ReferenceOrbitBlobCodec.Decode`, validates centre +
    maxIter match the request, wraps as `MandelbrotCalculator.OrbitDD`,
    and forwards via `PosterRequest.SeededOrbit`. Mismatch → seed
    skipped (the calculator's centerSame check would refuse it anyway;
    avoiding one Decode allocation per refused tile). Decode failure →
    logged warning + per-tile recompute (never a hard failure on a
    perf-path).
  * Forwards `ImageWidth/Height/SubRectOffsetX/Y` from the request DTO
    to `PosterRequest`.
- `ServerHost/ClusterEntry.cs`:
  * Wires `ReferenceOrbitProvider` on the new coordinator: compute via
    `MandelbrotCalculator.ComputeReferenceOrbitDDPublic`, encode via
    `ReferenceOrbitBlobCodec.EncodeDD`, return `(blob, maxIter)`. Engine
    + Server.Cluster references are local to the host assembly.

**Tests**

- `Server.Tests/Cluster/ReferenceOrbitBlobTests.cs` (new) — 6 cases:
  * codec round-trip preserves header + centre limbs + Hi/Lo arrays
  * decode of a blob with wrong magic byte throws
  * decode of a truncated blob (header-only) throws
  * `QualifiesForSharedReferenceOrbit` accepts Mandelbrot at 1e10,
    refuses low-zoom (1.0), past-DD-ceiling zoom (1e26), and Julia
  * `AttachSharedReferenceOrbit` rewrites both tiles of a 256×128 plan
    into image-frame mode with the blob attached
  * **pixel-parity**: 64×64 full Mandelbrot render at zoom 1e9 vs. four
    32×32 sub-rect tiles seeded with the master-computed orbit — every
    pixel matches bit-for-bit. This is the load-bearing correctness
    guard for the dc-origin shift in `ComputeRowPTScalar`.

- `Server.Tests/FracturingFog.Server.Tests.csproj` — gains a project
  reference to `Engine/FracturingFog.Engine.csproj` so the pixel-parity
  test can drive `MandelbrotCalculator` directly. Test project was
  previously Server+Client+Abstractions only.

- Test suite: **329 passed, 0 failed** (was 323; +6 in new
  `ReferenceOrbitBlobTests`). No existing test churn.

**Design decisions**

#95. Sub-rect rendering instead of per-tile dc-shift overlays. The
  alternative — keeping tile.Render.CenterX = tile centre and shipping a
  separate `RefCenterX/Y` for the orbit — would have required threading a
  new pair of "where is the orbit centred" fields through every PT inner
  loop AND careful Hi/Lo subtraction to preserve DD precision in the dc
  shift. Sub-rect mode is mathematically equivalent (the dc origin
  collapses to `(x - imageHalfW) * scale + tileWorldOffset` in either
  formulation) but the calculator already takes CenterX as the orbit
  anchor, so reusing that field means zero new precision plumbing.

#96. SIMD PT4/PT8 forced to scalar when sub-rect active. The SIMD
  inner loops hardcode `Width/halfW` into vector setup; retrofitting
  them with sub-rect math would touch dense AVX2/AVX-512 code paths that
  the project hand-tuned over many sessions. The scalar path is the
  single source of truth for the dc-origin shift in v1; SIMD adaptation
  (~3× speedup on the PT inner loop) lands as D-6b1 once a perf
  measurement justifies the surgery.

#97. v1 ships DD-precision orbits only (limbs = 2 in the blob header).
  QD (zoom > 1e25) and OD (zoom > 1e50) require shipping additional
  limb arrays — 4× / 8× the wire bytes and 4× / 8× the calculator
  cache-seed surface. The wire format reserves the `limbs` header byte
  for that growth; the codec refuses non-DD blobs in v1 with a clear
  error so a future blob shipped from a QD-capable master against a
  DD-only worker fails closed instead of producing wrong pixels.

#98. ReferenceOrbitProvider is a delegate on the coordinator, not a
  trait on TilePlanner. The Server library stays Engine-free — the
  planner doesn't know how to compute an orbit, only how to rewrite a
  plan when given a blob. The compute lives in `ClusterEntry` which is
  already the assembly that pulls Engine references.

#99. Blob is base64 inside the JSON-RPC envelope rather than a binary
  trailer. Binary trailer support exists for `tile.deliver`; reusing it
  for `tile.next`'s response would let blobs >1 MB ride the same path.
  DD orbits at typical maxIter (1K–10K) are 32–320 KB — base64's 33%
  overhead is tolerable. When 1M+ iter renders become the workload
  driver (deep-zoom video), D-6b1 swaps to a binary trailer.

#100. Sub-rect mode is invariant under `SubRectOffsetX = 0 && SubRectOffsetY
  = 0 && ImageWidth <= Width && ImageHeight <= Height`. That guard collapses
  the new dc formula to the legacy `(x - Width*0.5) * scale` exactly, so
  the change is a strict superset of the prior behaviour and existing
  pixel-parity tests for single-server renders remain valid without
  needing an explicit "sub-rect disabled" toggle.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (35 total — AVLN5001 + codegen CS0219, unchanged from D-6a).
- Test suite: **329 passed, 0 failed** (+6 since D-6a's 323).
- Filtered `--filter "FullyQualifiedName~ReferenceOrbitBlobTests"`:
  6 passed in 311 ms (includes the pixel-parity render).

**Next session** opens D-6c: per-role rate limiting (§9 D-6 item 3).
Extends `EndpointRateLimiter` to a per-role policy: client-role uses
the existing per-IP per-minute, worker-role lower bound on `tile.next`
long-poll churn, admin-role unlimited but every call logged. D-6b1
(SIMD PT4/PT8 sub-rect adaptation) is a perf follow-up — schedule
behind D-6c → D-6d (doc) → D-6e (stress test) per the plan §9 ordering.
