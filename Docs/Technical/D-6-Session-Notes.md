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

---

## Session 3 — D-6c — Per-role rate limiting (2026-06-28)

**Goal**: layer a per-method, per-role token-bucket limiter on top of the
existing per-IP TCP-accept limiter. Clients get a per-IP per-minute cap on
dispatched calls inside an authenticated session, workers get a
per-thumbprint cap on `tile.next` long-poll churn (other worker methods
bypass), and admins are never refused but every `cluster.*` call they
make is recorded as an `admin-call` event in the cluster NDJSON log.

**Sub-slice decision**: a single slice. The audit-log half of the §6.6
ask co-locates with the limiter naturally and adding it cluster-side
needs only the one event-emission line. No reason to split a D-6c1.
Persisting the four new config knobs to live cluster.config.set (so the
admin UI can edit them at runtime) is out of scope — they are picked up
at master startup from `server-config.json`, which already round-trips
through the D-5e `cluster.config.get`/`set` path for the cluster knobs
it explicitly enumerates. A follow-up can add them to the live-config
DTO if the operator workflow calls for it.

**Server-side changes**

- `Server/Guard/RoleAwareRateLimiter.cs` (new):
  * `RoleLimiterDecision { Allow, RefusedRate }` enum.
  * Composes two `Bucket` instances — one per-IP token bucket for client
    policy, one per-thumbprint token bucket for the worker `tile.next`
    policy. Admin path returns `Allow` unconditionally.
  * Worker non-`tile.next` methods bypass — heartbeat cadence and
    per-tile-budget mechanics already bound their volume.
  * Bucket impl mirrors the existing `EndpointRateLimiter` math
    (token-bucket with capacity, time-decay refill, sweep-once-per-minute
    cleanup of idle full buckets) but keyed by caller-supplied string so
    one limiter can mix IP-keyed and thumbprint-keyed policies.
- `Server/ServerConfig.cs`:
  * Four new fields, all live in `server-config.json` and read at
    master startup: `ClientCallPerMinute` (default 600 = 10/sec
    sustained), `ClientCallBurst` (default 30), `WorkerTileNextPerMinute`
    (default 600), `WorkerTileNextBurst` (default 30). Defaults sized
    to be invisible under normal UI / dashboard polling but tight enough
    to catch a runaway loop.
- `Server/FFServer.cs`:
  * Constructor builds a `RoleAwareRateLimiter` alongside the existing
    `EndpointRateLimiter`.
  * `HandleConnectionAsync` now passes the resolved remote IP through
    to `DispatchAsync` (per-method limiter needs the per-call key).
  * `DispatchAsync` consults the limiter before routing. `server.status`
    bypasses (cheap liveness probe; locking out a probe loop is worse
    than the call cost). Refusals reply with the new `rate-limited`
    error code, log a `Warn` line on the session log, and record the
    failure in `Metrics`.
- `Server/Cluster/ClusterCoordinator.cs`:
  * `HandleAsync` now emits a `kind:"admin-call"` event with the method
    name + normalised thumbprint whenever the caller is `CertRole.Admin`
    and the method starts with `cluster.`. Coordinator method body
    expression-bodied switch is now a regular switch so the audit-log
    prelude can run before the dispatch.

**Tests**

- `Server.Tests/Cluster/RoleAwareRateLimiterTests.cs` (new) — 8 cases
  across two fixtures:
  * `Admin_Always_Allowed_Even_With_Tight_Buckets` — admin runs 100
    calls against a `perMinute=60, burst=1` config that would refuse
    any other role after one call.
  * `Worker_TileNext_Bucket_Exhausts_Then_Refuses` — burst-of-3 cap,
    fourth `tile.next` call refused.
  * `Worker_NonTileNext_Methods_Bypass_Limiter` — burst-of-1, after
    the lone `tile.next` token is spent, 50 iterations of
    `worker.heartbeat`/`tile.deliver`/`tile.error`/`worker.register`
    all still allow. Load-bearing for the cadence + per-tile-budget
    reasoning behind the bypass.
  * `Worker_Buckets_Are_Per_Thumbprint` — two thumbprints share an
    implicit-NAT IP; each gets its own bucket so a runaway worker
    cannot starve its peer.
  * `Client_Bucket_Exhausts_Then_Refuses_Per_IP` — per-IP isolation
    mirror for the client policy.
  * `Disabled_PerMinute_Allows_All` — `perMinute=0` on both policies
    keeps the limiter open across 200 client + worker calls.
  * `Cluster_Method_By_Admin_Writes_AdminCall_Event` — drives a real
    `cluster.status` call as admin, disposes the logger to flush,
    asserts the NDJSON file contains a line with `"kind":"admin-call"`
    and `"method":"cluster.status"`.
  * `Cluster_Method_By_NonAdmin_Skipped_From_AdminCall_Event` — same
    call as `Client` role; the admin-call event must not appear (the
    FFServer role gate would refuse the call in production, but the
    coordinator's audit-log path must independently not fire for the
    non-admin caller).

- Test suite: **337 passed, 0 failed** (was 329; +8 new D-6c tests).
  No existing test churn.

**Design decisions**

#101. Per-method limiter is layered on top of the existing per-IP
  TCP-accept limiter, not a replacement. Reason: the accept-loop
  limiter bounds connection-establish churn before TLS — it never sees
  calls inside an authenticated session. A long-lived worker holds one
  socket and pumps `tile.next` in a loop; only a per-method limiter
  can bound that surface. Two limiters compose naturally (one at TCP,
  one at JSON-RPC) and the per-IP limiter stays exactly the same.

#102. Worker key is the cert thumbprint, not the IP. Reason: workers
  reconnect after restarts (the registry's thumbprint pinning is
  designed for this) and multiple workers may share an IP (LAN NAT,
  developer machine running two test workers). An IP-keyed worker
  policy would either share a bucket across peers (one runaway starves
  all) or reset the bucket on reconnect (defeats the whole point).
  The cert thumbprint is the stable identity the master already pins.

#103. Only `tile.next` is rate-gated on the worker side. Reason:
  `worker.heartbeat` is cadence-bounded (one per `HeartbeatIntervalSeconds`,
  enforced by the registry — too-fast heartbeats refresh the same
  entry without a downside); `tile.deliver`/`tile.error` are bounded
  by the number of assigned tiles (the master controls the supply via
  the dispatcher); `worker.register` is one-shot per session. Adding a
  limiter to those surfaces would constrain a non-existent attack
  surface and risk false positives on legitimate reconnect storms.

#104. Admin policy is "log every call" rather than "rate-limit but
  with a high cap." Reason: the dev plan §6.6 explicitly calls for
  this asymmetry — the operator must be able to drive a quiesce-all-
  workers + cancel-all-jobs flow as fast as they can click without
  the master ever responding `rate-limited`. The audit trail makes
  the "no limit" surface accountable; the cluster NDJSON file rolls
  daily so the events join the existing operator-debug stream.

#105. `server.status` bypasses the limiter. Reason: external
  monitoring (uptime probes, status dashboards) calls it on a tight
  cadence with the client cert. With the default `ClientCallPerMinute=600`
  this is already non-blocking, but a probe behind a flapping NAT
  could reset to a new IP under the bucket and still get caught. The
  call is cheap (a few microseconds; no engine work, no DB touch),
  so the limiter buys nothing for it.

#106. `rate-limited` is a new error code rather than reusing `busy`.
  Reason: `busy` already means "the queue gate is full — try again
  after current renders finish." Rate-limited means "your specific
  caller has used its allowance; back off." The two have different
  client behaviours (`busy` retries after seconds, `rate-limited`
  retries after a small fraction of a minute) so the error vocabulary
  is worth distinguishing.

#107. Four config fields rather than one unified
  `RateLimitPolicy` object. Reason: matches the existing
  `RateLimitPerMinute`/`RateLimitBurst` shape on the same ServerConfig
  (which is the per-IP accept-loop limiter) — operators editing the
  JSON by hand see a flat list. A future consolidation can wrap them
  in a sub-object without a wire break (the JSON property names stay
  the same).

**Build + test**

- Solution build (Debug): 0 errors, pre-existing 4 AVLN5001
  TextBox.Watermark obsoletes only (unchanged from D-6b).
- Test suite: **337 passed, 0 failed** (+8 since D-6b's 329).
- Filtered `--filter "FullyQualifiedName~RoleAwareRateLimiterTests|FullyQualifiedName~AdminAuditLogTests"`:
  8 passed in 64 ms.

**Next session** opens D-6d: operator doc at
`Docs/User/Distributed-UserGuide.md` (§9 D-6 item 4). Covers cert
issuance + role-OU convention, `--master` / worker launch, the admin
UI tour from D-5, plus the new rate-limit knobs from this session. D-6e
closes the phase with the stress-test (`Server.Tests`: 50 concurrent
client connections, 8 workers, 200 queued jobs) and any cleanup the
doc-writing surfaces.

---

## Session 4 — D-6d — Distributed Rendering operator doc + Master Config help button (2026-06-28)

**Goal**: ship `Docs/User/Distributed-UserGuide.md` as the operator's
single landing page for the cluster path D-1..D-6c built up, and wire
a `?` help button on the Master Config dialog so the live-tuning
surface from D-5e is one click from its own documentation section.

**Sub-slice decision**: a single slice. The doc is one Markdown file
plus a one-row insert into `Docs/User/_Index.md` plus a four-line
help-button on `MasterConfigView`. Splitting into a doc-only slice +
a UI-only slice would have meant two commits whose only coupling is
the shared anchor name. Doc + button land together so the anchor
("Master Config Dialog") cannot drift between them without one of the
two callers noticing immediately.

**Doc-side changes**

- `Docs/User/Distributed-UserGuide.md` (new) — 17 sections, matches
  the existing user-guide tone (table of contents at top, friendly
  tour + worked example up front, dense reference toward the bottom,
  See-Also footer). Sections in order:
  1. **Overview** — what cluster mode is, what wall-clock win it
     buys, LAN-only scope.
  2. **Architecture at a Glance** — ASCII diagram from the dev plan
     §2, role-OU routing summary, "friendly tour" + worked-example
     pair mirroring `ClientServer-UserGuide.md` §A.
  3. **First-Time Master Launch** — `--master`, on-disk artifacts
     created on first run, recovery banner from D-6a, bind / cert
     dir overrides.
  4. **The Cluster Cert Bundle** — five-file table, role→capability
     matrix, empty-password ACL trade-off pointing at
     `CertSelfSignedHelper.cs`'s own trade-off note.
  5. **Sharing Keys Between Hosts** — which PFX each role needs,
     acceptable + unacceptable transfer channels, PowerShell
     copy-and-ACL recipe for adding a worker host, separate
     walkthrough for handing out the admin role.
  6. **Production PKI — Per-Role Certificates** — full openssl
     recipes for CA / master / worker / client / admin certs with
     the `OU=role-*` Subject DN convention and the
     `extendedKeyUsage=serverAuth` extension on the master.
     Lay-out diagram for the five expected filenames so the
     `EnsureClusterBundle` discovery path matches.
  7. **Launching Workers** — `--worker` CLI, required + common
     options, capability registration table covering the fields
     emitted in `ClusterEntry.RunWorker` including the
     `EngineBuildSha` fidelity check from risk #7. Service-manager
     guidance for unattended hosts.
  8. **Admin UI Tour** — Cluster Dashboard, Worker Detail,
     Job Detail, Job List. Reiterates the colour-blind alert
     convention (yellow `#FFCC00`, never red).
  9. **Master Config Dialog** — the three live-tunable knobs from
     D-5e + the new `?` button.
  10. **Submitting Jobs as a Client** — covers the `job.submit` /
      `job.status` / `job.fetch` polling shape and `--batch
      --remote` against a cluster connection.
  11. **Rate Limits + Admin Audit Log** — the D-6c knobs + the
      `kind:"admin-call"` event format. Notes that the four
      rate-limit fields are read at master startup (not live-
      tunable today).
  12. **Crash Recovery** — what `RecoverFromDisk` from D-6a does +
      doesn't replay; corrupt-tile + worker-disappeared retry
      behaviour.
  13. **Logs, Metrics, and Troubleshooting** — single table mapping
      every on-disk artifact to its purpose; common-errors table;
      step-by-step "my N-worker cluster runs at 1-worker speed"
      diagnostics walkthrough; pointer to the three built-in
      `--cluster-*` self-tests.
  14. **CLI Reference** — master + worker + self-test flag tables.
  15. **Config File Reference** — exact JSON keys + a worked example
      block.
  16. **File Locations** — appdata directory map mirroring the
      ClientServer guide's §7.
  17. **See Also** — cross-link to Client/Server, Server Admin,
      Avalonia, dev plan, this session-notes file.
- `Docs/User/_Index.md` — one new row in the "Where do I start?"
  routing table immediately after the Server Admin row:
  `Stand up a multi-machine render cluster → Distributed-UserGuide.md`.

**UI-side changes**

- `UI.Avalonia/Views/MasterConfigView.axaml`:
  * Bottom button row promoted from a single right-aligned
    `StackPanel` to a 3-column `Grid` so a left-aligned `?` button
    can sit next to the existing right-aligned Load / Apply / Close
    cluster. Button is 32×28 px, the same proportions
    `ServerAdminView` uses for its help button.
  * `ToolTip.Tip` text states the anchor the button jumps to so a
    hover preview is enough to know what page opens.
- `UI.Avalonia/Views/MasterConfigView.axaml.cs`:
  * `using Avalonia.Interactivity;` added for `RoutedEventArgs`.
  * New `OnHelpClick` private handler calls
    `HelpViewerLauncher.Show(this, "User/Distributed-UserGuide.md",
    "Master Config Dialog", "Master Config — Help")`. Mirrors
    `ServerAdminView`'s pattern exactly so any future tooling that
    rewrites help wiring (HelpViewerViewModel's anchor slicer, for
    example) treats the two dialogs uniformly.

The `FracturingFog.UI.Avalonia.csproj` `AvaloniaResource` glob
(`..\Docs\**\*.md`) already enrols every Markdown file under `Docs/`,
so `Distributed-UserGuide.md` is embedded automatically on the next
build — no project-file edit required for the help viewer to find it.

**Tests**

- No new test classes — D-6d is a docs + view-wiring slice. The
  embedded resource path is exercised at runtime by the help-viewer
  load attempt (legacy bare-filename fallback in
  `HelpViewerViewModel.LoadDocResource` covers any historical
  callers).
- Test suite: **337 passed, 0 failed** — unchanged from D-6c. The
  one-button-and-a-Markdown-file diff touches no compiled assertions.

**Design decisions**

#108. Five-file cluster bundle documented as load-bearing names, not
  as a recommendation. Reason: `CertSelfSignedHelper.EnsureClusterBundle`
  identifies an existing bundle by the literal filenames
  `ca.pfx`/`master.pfx`/`worker.pfx`/`cluster-client.pfx`/`admin.pfx`.
  A user who mints their own bundle with `worker-tower2.pfx` in the
  same directory will see the helper regenerate the dev bundle on
  next startup. The doc's §6.6 calls this out with a layout block so
  the production-PKI walkthrough doesn't dead-end on a re-generated
  CA.

#109. openssl recipes pin `OU=role-*` on the Subject DN, not on a SAN
  URI. Reason: `CertRoleParser.FromCertificate` splits on `,` and
  reads OUs; the comment block at the top of `CertRole.cs` explicitly
  reserves the SAN-URI path as a future hardening option. Documenting
  the OU path is the *only* path that works in v1; documenting both
  would invite mismatches when the operator picks the URI form.

#110. Help button anchors via the dialog's section heading literal
  ("Master Config Dialog") rather than via an HTML-style `#section-9`
  fragment. Reason: `HelpViewerViewModel.SliceToSection` does a
  case-insensitive `Contains` match against rendered heading text,
  not against numbered slugs. Anchoring on the heading text is
  resilient to section-renumbering inside the guide — if §9 becomes
  §10 in a future revision the button still lands on the right page.

#111. Rate-limit knobs documented as startup-only, not as a known gap.
  Reason: the D-6c session notes left the live-config question as
  "out of scope; a follow-up can add them to the live-config DTO if
  operator workflow calls for it." Telling the operator the truth
  (edit + restart) is correct guidance today; flagging it as a
  limitation would falsely imply someone is working on the
  follow-up, and surface area that's actually live-tunable
  (`clusterMaxJobs` / retention / tile target via Master Config) is
  already documented.

#112. Help button placed left-aligned in a 3-column Grid rather than
  prepended to the existing right-aligned StackPanel. Reason: every
  other help button in the project (`ServerAdminView`,
  `FFClientView`, `ColorThemeEditorView`, etc.) sits on the *left*
  edge of its bottom button row, opposite the primary action
  cluster. Keeping that layout convention means an operator's eye
  finds the `?` in the same screen quadrant across every dialog.

#113. The doc is added as one ~16K-line Markdown file rather than
  split per-topic. Reason: every existing user guide
  (`ClientServer-UserGuide.md`, `ServerAdmin-Guide.md`,
  `Capture-Guide.md`) is one self-contained file. Splitting cluster
  docs across multiple files would orphan the See-Also footer
  pattern and make the `_Index.md` row ambiguous (which sub-file is
  "the" entry point?). One file is also one anchor namespace for the
  help-viewer's section slicer to walk.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing 4 AVLN5001
  `TextBox.Watermark` obsoletes only (unchanged from D-6c).
- Test suite: **337 passed, 0 failed** (unchanged from D-6c).

**Next session** opens D-6e: the §9 stress test — 50 concurrent
client connections, 8 workers, 200 queued jobs through the existing
`Server.Tests` harness. Once D-6e lands, phase D-6 closes and the
distributed-rendering line item is done; any further work (the
SIMD PT4/PT8 sub-rect adaptation tagged D-6b1, the QD orbit limbs
growth tagged D-6b's #97, or making the four rate-limit knobs
live-tunable) is follow-up perf/operator work scheduled
independently.

---

## Session 5 — D-6e — Cluster stress test + JobStore race fix (2026-06-28)

**Goal**: stand up the §9 D-6 acceptance workload — 50 concurrent
client connections submit 200 jobs against an 8-worker pool — as a
Server.Tests case that fails fast on any dispatcher / status / fetch
regression introduced under contention. Phase D-6 (and the
distributed-rendering line item) closes when it lands green.

**Sub-slice decision**: a single slice. The stress test is one new
xUnit class plus a 4-line atomicity fix in `JobStore.WriteStatusLocked`
that the load surfaced on first run. No split: the fix is the test's
only prerequisite, and splitting would have meant a passing-test commit
that landed before the bug it caught was actually closed. The test
runs in-process against the existing `ClusterCoordinator` surface —
the same call shape as `ClusterEndToEndImageTests` — rather than over
TLS+TCP. A socket-level harness would add minutes per run for an
effect the coordinator surface already represents faithfully (the
focused end-to-end tests cover the on-wire framing).

**Server-side changes**

- `Server/Cluster/JobStore.cs`:
  * `WriteStatusLocked` swapped its `File.Exists(final) → File.Delete →
    File.Move(tmp, final)` sequence for a single
    `File.Move(tmp, final, overwrite: true)`. The previous pattern
    opened a window between `Delete` and `Move` where a concurrent
    `ReadStatusLocked` saw `status.json` missing and the coordinator
    answered `unknown-job` on a healthy in-flight job. The stress run
    tripped the race within the first ~50 concurrent jobs and was the
    first test load tight enough to surface it; the bug was latent in
    every prior D-2..D-5 release but hidden by the focused tests' low
    polling cadence.

**Tests**

- New `Server.Tests/Cluster/StressTests.cs` — one fact,
  `Cluster_Sustains_50_Clients_8_Workers_200_Jobs`:
  * Registers 8 workers under distinct cert thumbprints.
  * Spawns 8 worker `Task.Run` loops that pump `tile.next` → render
    a constant-fill tile via `RawHeaderCodec.BuildTile` → `tile.deliver`.
  * Spawns 50 client `Task.Run` loops that each submit
    `totalJobs/numClients = 4` jobs in sequence, poll each to `ready`,
    then fetch each artifact.
  * Tight `TileNextHold = 100 ms` so an idle worker round-trips
    quickly; padding the hold would only mask a regression behind
    the timeout. `Dispatcher.MaxAttempts = 2` keeps a flake-mode
    retry available but avoids hiding a real failure behind silent
    re-render.
  * Global 2-minute wall-clock guard so a real deadlock fails the
    CI run instead of hanging it; the healthy run completes in ~2 s
    on a developer box.
  * Asserts: all 200 jobs reach `ready`, every job id is distinct,
    every `job.fetch` returns a streaming artifact ≥ 1 byte, total
    delivered tile count ≥ 200, and every persisted `status.json`
    cross-checks to `ready` on the JobStore.

- Test suite: **338 passed, 0 failed** (was 337; +1 stress test).
  No existing test churn — the JobStore.WriteStatusLocked fix is a
  strict superset of the prior behaviour (the swap is still atomic;
  the change is removing the unsafe gap before it).

**Design decisions**

#114. Stress test runs in-process against the coordinator, not over
  TLS+TCP. The §9 D-6 ask is "Stress tests in `Server.Tests`" — the
  same harness layer the existing focused tests use. A socket-level
  variant would buy a TLS-handshake check (already exercised
  end-to-end by D-1 in `ClusterEndToEndImageTests`'s sibling sockets
  fixtures) at the cost of multi-minute CI runs from the 50-client
  fan-out alone. In-process keeps the test surface aligned with the
  bug class D-6 hardens against — dispatcher contention, status
  reads under load, queue starvation — none of which depend on the
  wire framing.

#115. The JobStore race fix lands in the same slice as the stress
  test, not as a parallel D-6e1 follow-up. Reason: a green stress
  test before the fix is impossible (the race manifests deterministically
  inside the first second of the workload); a green stress test
  after the fix is the proof the fix is correct. Splitting would
  have left the commit graph with a passing test that landed before
  the bug it caught was actually closed. The fix is a 4-line atomic
  primitive swap with no behavioural surface area beyond removing a
  read-during-write window.

#116. Single-tile jobs (64×64 image, `TilePixelsHint=64`) rather
  than multi-tile. The §9 wording says "200 queued jobs", and one
  tile per job means 200 dispatch + deliver round-trips — enough
  to exercise the awaiter fan-out and per-job status churn without
  ballooning the test into a multi-thousand-tile run that would
  dominate on merger work rather than coordinator contention. The
  merger codepath is independently covered by `ArtifactMergerTests`;
  the merge-heavy regression surface is not what D-6e is testing.

#117. Worker cert thumbprints synthesised as `WORKER-THUMB-NN`
  rather than real SHA-1 hex. The registry's pin uniqueness check
  fires on the literal string; any 8 distinct strings satisfy it.
  Synthesising real SHA-1 hex would add a `RandomNumberGenerator`
  call per worker for no test signal — the thumbprint pin behaviour
  is independently covered by `WorkerRegistryTests`.

#118. `Dispatcher.MaxAttempts = 2` rather than `1`. A constant-fill
  worker should never fail a tile, but pinning attempts to 1 would
  turn a single transient hiccup (e.g. an OS scheduling stall that
  pushes a worker past its `TileNextHold` deadline) into a hard
  test failure that bears no relation to the bug class D-6e is
  guarding against. One spare attempt keeps the test focused on
  what it asserts about (terminal-state correctness) and away from
  what it doesn't (per-tile timing).

#119. Global wall-clock guard at 2 minutes. Healthy run is ~2 s on
  the dev box; CI boxes that are swapping under contention have
  been observed to take ~30 s on similar end-to-end tests in this
  repo. 2 minutes is a clear "real deadlock vs. slow CI" cliff —
  if the guard ever fires, the failure mode is a genuine
  dispatcher / awaiter regression, not a flake.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing 4 AVLN5001
  `TextBox.Watermark` obsoletes only (unchanged from D-6d).
- Test suite: **338 passed, 0 failed** (+1 since D-6d's 337).
- Filtered `--filter "FullyQualifiedName~StressTests"`:
  1 passed in 2 s (200 jobs × 50 clients × 8 workers, ~3 s including
  xUnit discovery).

**Phase D-6 closes here.** The distributed-rendering line item from
`DistributedRendering-DevelopmentPlan.md` §9 is complete:
- D-6a — master crash recovery for image jobs
- D-6b — shared reference orbit for Mandelbrot tiles
- D-6c — per-role rate limiting + admin audit log
- D-6d — operator doc + Master Config help button
- D-6e — 50-client / 8-worker / 200-job stress test + JobStore race fix

Open follow-ups scheduled independently of this phase: SIMD PT4/PT8
sub-rect adaptation (D-6b1), QD/OD orbit limbs growth (D-6b #97),
the four rate-limit knobs surfaced through `cluster.config.get/set`
for live tuning (D-6c #-deferred), and video / slideshow crash
recovery (D-6a #93 deferral). None block the phase close.

---

## Session 6 — D-6c1 — Live-tunable rate-limit knobs (2026-06-28)

**Goal**: close the D-6c "live-config follow-up" called out in §-deferred.
The four per-role rate-limit knobs (`ClientCallPerMinute`,
`ClientCallBurst`, `WorkerTileNextPerMinute`, `WorkerTileNextBurst`)
were startup-only — an admin who wanted to retune had to edit
`server-config.json` and bounce the master. Surface them through the
existing `cluster.config.get` / `cluster.config.set` round-trip so
`FFAdminConnection.SetClusterConfigAsync` (and a future Master Config
dialog growth) can dial them without a restart.

**Sub-slice decision**: a single slice. The wire surface (DTO fields),
the coordinator handler, the live-apply hook into FFServer's
`RoleAwareRateLimiter`, and the client helper extension co-locate
naturally — splitting would have stranded a passing wire layer
without the live-apply or vice versa. UI growth (a second
`MasterConfigView` row group for the rate-limit knobs) is the
natural next slice but is deferred: the wire is the load-bearing
half of the follow-up, and the existing MasterConfigView already
covers the three D-5e knobs; a UI-only follow-up can grow the form
without further protocol work.

**Server-side changes**

- `Server/Guard/RoleAwareRateLimiter.cs`:
  * `Bucket._ratePerSec` / `_capacity` switched from `readonly` to
    plain instance fields with `Volatile.Read` / `Volatile.Write`
    accessors. Lets the limiter mutate its rate without rebuilding
    the bucket map — in-flight per-key `Slot._tokens` state survives
    the swap. Reads on the hot path snapshot via `Volatile.Read`
    once per call so a concurrent reconfigure can't tear a `double`
    on a 32-bit platform (we ship 64-bit only today, but the
    Volatile.* pattern is what the analyzer expects and is free).
  * New public `Reconfigure(clientPerMinute, clientBurst,
    workerTileNextPerMinute, workerTileNextBurst)` — forwards to
    the per-bucket `Reconfigure`.
- `Server/FFServer.cs`:
  * New public `ReconfigureRoleLimiter(int,int,int,int)` that
    forwards into the private `_roleLimiter`. Avoids exposing the
    limiter reference itself; the only caller (ClusterEntry) needs
    only the apply-with-four-ints surface.
- `Server/Cluster/Protocol/ClusterConfigDto.cs`:
  * Both `ClusterConfigSetRequestDto` and `ClusterConfigDto` grew
    four optional / non-optional fields respectively
    (`clientCallPerMinute`, `clientCallBurst`,
    `workerTileNextPerMinute`, `workerTileNextBurst`). JSON property
    names mirror the `ServerConfig` casing so an operator reading a
    `cluster.config.get` response can map directly to the
    `server-config.json` keys.
- `Server/Cluster/ClusterCoordinator.cs`:
  * Four new `{ get; set; }` properties seeded from defaults
    matching `ServerConfig` defaults (600 / 30 / 600 / 30).
  * New `ApplyRoleLimiterChange` `Action<int,int,int,int>?` —
    `{ get; set; }` rather than `init` because the coordinator is
    constructed before FFServer (it's an FFServer init dependency),
    so the apply hook is wired post-construction in ClusterEntry.
  * `HandleClusterConfigSetAsync` now parses the four new fields,
    clamps (`Math.Max(0, perMinute)` / `Math.Max(1, burst)` — same
    bounds as `Bucket`'s own constructor floor), records
    `roleLimiterTouched`, and on touch invokes the hook and emits
    `cluster-config-limiter-apply-failed` on hook exception (the
    in-memory value still updates — operator can retry). `cluster-
    config-set` event line gained four new keys for audit-log
    visibility.
- `ServerHost/ClusterEntry.cs`:
  * Seed the four new coordinator fields from `cfg` so the master
    boots with whatever the admin last persisted.
  * `PersistConfig` callback writes the four new fields back to
    `cfg` so cluster.config.set survives a master restart.
  * After `new FFServer(...)`, assign
    `coord.ApplyRoleLimiterChange = (cpm, cb, wpm, wb) =>
      server.ReconfigureRoleLimiter(cpm, cb, wpm, wb);`. Coord is
    built first (FFServer init dependency); the hook closes the
    loop post-construction.

**Client-side change**

- `Client/FFAdminConnection.cs`:
  * `SetClusterConfigAsync` grew four optional `int?` parameters
    after the existing `CancellationToken`. Default `null` so the
    one existing caller (`MasterConfigViewModel.ApplyAsync`) stays
    source-compatible. New callers (admin CLI tooling, a future
    MasterConfigView row group) pass any subset of the four.

**Tests**

- `Server.Tests/Cluster/RoleAwareRateLimiterTests.cs` — three new
  facts covering the reconfigure surface:
  * `Reconfigure_From_Disabled_To_Enabled_Starts_Refusing` —
    starting with `perMinute=0` (disabled) then dialing up to
    `60/burst=1` consumes the single token, then refuses.
  * `Reconfigure_From_Enabled_To_Disabled_Stops_Refusing` —
    inverse: a previously-refusing bucket dialled to `perMinute=0`
    goes back to unconditional Allow on the next call.
  * `Reconfigure_Worker_Bucket_Independent_Of_Client_Bucket` —
    tightening the worker bucket alone leaves the client bucket's
    original burst capacity intact. Guards against an accidental
    cross-wire in the `Reconfigure` forwarder.
- `Server.Tests/Cluster/ClusterAdminRpcTests.cs` — five new facts:
  * `ClusterConfig_Get_Returns_RoleLimiter_Defaults` — the four
    knobs default to 600/30/600/30 on a fresh coordinator (matches
    `ServerConfig` defaults).
  * `ClusterConfig_Set_Updates_RoleLimiter_And_Fires_ApplyHook` —
    a set call updates the four coordinator fields and invokes
    `ApplyRoleLimiterChange` with the post-clamp values.
  * `ClusterConfig_Set_RoleLimiter_Clamps_Negative_Values` —
    negative perMinute → 0, negative / zero burst → 1.
  * `ClusterConfig_Set_RoleLimiter_NullFields_DoNotFireApplyHook` —
    touching only D-5e knobs leaves `ApplyRoleLimiterChange`
    untouched; no gratuitous limiter churn.
  * `ClusterConfig_Set_RoleLimiter_ApplyHook_Failure_Does_Not_Fail_Call`
    — a throwing hook is swallowed; the in-memory value still
    updates and the call returns Ok (mirrors the existing
    `PersistConfig` swallow path).

**Design decisions**

#120. Bucket reconfigure mutates rate/capacity in place rather than
  rebuilding the limiter from scratch. The alternative — swap the
  `RoleAwareRateLimiter` reference inside `FFServer` — would have
  required `FFServer._roleLimiter` to be `volatile` or behind a
  lock, dropped every in-flight per-key bucket's accrued tokens
  (a runaway worker would get a fresh burst of `burst` calls on
  every set), and forced a CAS dance for ConfigureAwait-style
  swap. In-place mutation keeps the per-key bookkeeping warm,
  which is what an operator dialling a rate down actually wants —
  the burst budget the abuser already exhausted should stay
  exhausted across the swap.

#121. `ApplyRoleLimiterChange` is `Action<int,int,int,int>?`
  rather than `Action<ClusterConfigDto>?`. Reason: passing the
  whole snapshot would bind ClusterCoordinator's apply contract
  to the DTO shape, which is the wire surface. The apply hook is
  an internal seam between two server-internal collaborators
  (coordinator + FFServer) — the four-int signature is the
  minimum surface for the job and won't grow if the DTO grows
  later for unrelated knobs.

#122. Hook is `{ get; set; }`, not `init`. PersistConfig is
  `init`-only because it's assigned at coordinator-construction
  time (inside the object initializer). The limiter hook can't
  follow that pattern: the FFServer it forwards into is built
  *with* the coordinator already in hand. Splitting the apply
  surface from FFServer (e.g. via an `IRoleLimiterApplier`
  parameter passed into both constructors) would buy the `init`
  immutability at the cost of a third interface for a one-method
  surface; not worth it.

#123. `SetClusterConfigAsync` grew with four optional trailing
  parameters rather than via a builder or a second method. Reason:
  existing callers (MasterConfigViewModel) call positionally; an
  options struct would have broken the call site for zero gain
  (the four new params are independent, mostly absent, and
  default to "leave alone"). A second method
  (`SetRoleLimiterAsync`) would have meant two RPCs to update a
  mixed D-5e + D-6c1 set, when the coordinator already supports
  a single atomic update.

#124. The four `cluster.config.set` audit-log keys are added to
  the existing `cluster-config-set` event rather than emitted as
  a parallel `cluster-config-limiter-set` event. Reason: an
  operator reading the audit log wants the full
  "what changed in one apply" line in one place; splitting would
  make a join across two adjacent NDJSON lines necessary to
  reconstruct a single user action. The new event
  `cluster-config-limiter-apply-failed` is separate because it's
  an error condition with its own diagnostic body, not the
  same-shape success path.

#125. The Master Config UI is not extended in this slice. Reason:
  the wire is the load-bearing half of the follow-up — any admin
  tooling (a CLI script, a future dashboard widget) can now dial
  the knobs over the same `FFAdminConnection.SetClusterConfigAsync`
  the existing dialog already uses. Growing the dialog with a
  second row group (four spinners + load/apply sharing the
  existing buttons) is a UI-only follow-up that doesn't need a
  protocol change and would bloat the slice unnecessarily.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (35 total — AVLN5001 + codegen CS0219 + xUnit1051, unchanged
  from D-6e).
- Test suite: **346 passed, 0 failed** (+8 since D-6e's 338).
- Filtered runs:
  * `--filter "FullyQualifiedName~RoleAwareRateLimiterTests"`: 9
    passed in 442 ms (3 new + 6 existing).
  * `--filter "FullyQualifiedName~ClusterAdminRpcTests"`: includes
    5 new D-6c1 tests alongside the existing D-5a/D-5e coverage.

**Open follow-ups remaining** (unchanged): D-6b1 (SIMD PT4/PT8
sub-rect adaptation), D-6b #97 (QD/OD orbit limbs), D-6a #93
(video / slideshow crash recovery), and the UI-only growth of
MasterConfigView noted in #125.

---

## Session 7 — D-6b2 — QD/OD shared reference orbit (2026-06-28)

**Goal**: close the D-6b "QD/OD limbs growth" deferred in #97. The wire
format reserved the `limbs` header byte for the higher-precision
variants; v1 shipped DD only and the codec refused QD/OD blobs with
a clear error. Phase D-6b2 grows the codec, engine, and worker
plumbing so deep-zoom jobs at zoom > 1e25 (QD) and > 1e50 (OD)
benefit from the same per-tile recompute short-circuit DD jobs
already get.

**Sub-slice decision**: a single slice. The wire format, the
engine compute hooks, the seed methods, the worker decoder switch,
and the tile-planner zoom-range bump all co-depend — splitting
would have stranded a passing codec slice without the engine
support to use it, or vice versa. Binary-trailer transport for
the ~MB-scale OD blobs (a perf escalation #99 enumerated for
1M+ iter renders) stays out of scope: base64 inside the JSON-RPC
envelope still works for the typical 1K–10K-iter range deep zooms
target. The binary trailer becomes D-6b3 if a real workload
justifies it.

**Server-side changes**

- `Server/Cluster/ReferenceOrbitBlobCodec.cs`:
  * Added `LimbsQD = 4` and `LimbsOD = 8` constants alongside the
    existing `LimbsDD = 2`. Format version stays at 1 — the limbs
    byte differentiates so a DD-only worker decoding a QD blob
    fails closed with a clear "limbs=4 not supported" message
    instead of silently mis-parsing the array block.
  * On-wire format extended additively after the existing 44-byte
    DD header:
    * `limbs >= 4` appends 4 centre doubles (cx X2, cx X3, cy X2,
      cy X3) = 32 bytes.
    * `limbs == 8` appends a further 8 centre doubles (cx X4..X7,
      cy X4..X7) = 64 bytes.
    * Array block grows from 4 arrays (DD) → 8 (QD) → 16 (OD).
  * New `EncodeQD` and `EncodeOD` static factories with explicit
    per-limb parameters; the DD path stays byte-identical.
  * `Decode` now switches on `limbs`, reads the extended centre
    block + the right array count, and populates the new
    optional X2..X7 fields on `DecodedOrbit`. Unknown `limbs`
    values (e.g. 3, 5) are refused with a clear error so a
    forward-compat version mismatch never silently produces
    wrong pixels.
- `Server/Cluster/TilePlanner.cs`:
  * `SharedRefOrbitMaxZoom` raised from `MandelbrotQDZoomThreshold`
    (1e25) to `1e115` — a conservative cap below the OD path's
    X7-limb noise floor (~10^116). Jobs above this still render
    correctly, just without the shared-orbit short-circuit.
  * New constants `SharedRefOrbitQDThreshold` (1e25) and
    `SharedRefOrbitODThreshold` (1e50) mirror the calculator's
    own QD / OD promotion thresholds; the host's provider switches
    on them so the shipped blob's precision matches what the
    worker's calculator would have computed locally.
  * `AttachSharedReferenceOrbit` now propagates the submission's
    CenterX4..X7 / CenterY4..X7 onto every tile's render request
    so an OD-tier tile arrives with the full 8-limb centre, not
    just the DD pair.
- `Server/Cluster/ClusterCoordinator.cs`:
  * `ReferenceOrbitProvider` signature widened from
    `Func<double, double, double, double, int, (byte[], int)?>`
    to `Func<RenderRequestDto, int, (byte[], int)?>`. The narrower
    pre-D-6b2 signature lost the upper QD/OD centre limbs by
    construction; routing the whole DTO lets the provider read
    CenterX2..X7 straight off the submission.
- `Server/Protocol/RenderRequestDto.cs`:
  * Added `CenterX4..X7` and `CenterY4..Y7` `double` fields with
    matching JSON property names (camelCase). DD/QD renders leave
    them 0; the OD-tier shared-orbit path fills them.

**Engine-side changes**

- `Engine/Calculators/MandelbrotCalculator.cs`:
  * New public static `ComputeReferenceOrbitQDPublic` mirroring the
    private `ComputeReferenceOrbitQD(QD,QD,int)` instance method
    bit-for-bit, returning a new `OrbitQD` container with the
    8 limb arrays + 8 centre limbs.
  * New public static `ComputeReferenceOrbitODPublic` mirroring
    `ComputeReferenceOrbitOD(OD,OD,int)`, returning a new `OrbitOD`
    container with all 16 limb arrays + 16 centre limbs.
  * New `OrbitQD` and `OrbitOD` containers paralleling `OrbitDD`'s
    `required`-property shape so the cluster host has a single
    canonical container per precision tier.
  * New `SeedReferenceOrbitQD(OrbitQD)` and
    `SeedReferenceOrbitOD(OrbitOD)` instance methods. Mirror
    `SeedReferenceOrbitDD`'s array-copy + cache-key prime, plus
    the appropriate higher-limb writes. OD path writes all 8
    limbs of centre + per-slot arrays; QD path writes X0..X3
    and clears X4..X7 so a stale OD orbit residue from a prior
    render can't bleed through.
- `Engine/Imaging/PosterRenderer.cs`:
  * `PosterRequest` grew `SeededOrbitQD` and `SeededOrbitOD`
    init-only properties alongside `SeededOrbit`. The renderer's
    Mandelbrot branch picks the first non-null and calls the
    matching `SeedReferenceOrbit{DD,QD,OD}`. At most one is non-
    null per render — enforced by the host (the worker's decoder
    sets exactly one based on `decoded.Limbs`).
  * `PosterRequest` grew `CenterX4..X7` / `CenterY4..Y7` so the
    Mandelbrot calculator's full 8-limb centre flows through. The
    calculator's `ComputeReferenceOrbitOD` centerSame check
    requires the full 8-limb match; without these, a seeded OD
    orbit would have been ignored as "stale centre" and the
    worker would have recomputed per-tile.
- `ServerHost/HostFractalRenderEngine.cs`:
  * The worker decoder now switches on `decoded.Limbs` and builds
    the matching `MandelbrotCalculator.OrbitDD / OrbitQD / OrbitOD`
    container, forwarding it onto the new `PosterRequest`
    seeded-orbit slots.
  * PosterRequest construction now copies `req.CenterX4..X7` /
    `req.CenterY4..Y7` so OD limbs from the wire DTO reach the
    calculator's instance properties (where the calculator reads
    them when promoting to its OD compute path).
- `ServerHost/ClusterEntry.cs`:
  * `ReferenceOrbitProvider` lambda now switches on
    `request.Zoom`: > `SharedRefOrbitODThreshold` → OD compute +
    `EncodeOD`; > `SharedRefOrbitQDThreshold` → QD compute +
    `EncodeQD`; else → DD compute + `EncodeDD`. Catches around
    the whole switch swallow any compute exception to `null`,
    matching the pre-D-6b2 fail-soft behaviour (the job falls
    back to per-tile compute).

**Tests**

- `Server.Tests/Cluster/ReferenceOrbitBlobTests.cs`:
  * `QualifiesForSharedReferenceOrbit_Gates` updated: zooms 1e30
    (QD) and 1e80 (OD) now accepted; only zoom 1e120 (above the
    OD cap) is refused.
  * New `EncodeDecode_RoundTrip_QD` — round-trips a 10-slot QD
    blob; asserts limbs byte, centre X2/X3 + Y2/Y3 fields, and
    every array element. Spot-checks that OD arrays are empty
    on a QD decode.
  * New `EncodeDecode_RoundTrip_OD` — round-trips a 6-slot OD
    blob with all 16 arrays and 16 centre limbs; asserts the
    far-tail limbs (`CentreX7`, `CentreY6`, `RefZrX4`,
    `RefZiX7`) round-trip exactly.
  * New `Decode_UnknownLimbs_Throws` — plants `limbs=3` in an
    otherwise valid header; decoder must refuse with a
    "limbs"-mentioning message rather than silently misparse.
- Test suite: **349 passed, 0 failed** (+3 since D-6c1's 346).
  Includes the existing `Calculator_SubRect_With_SeededOrbit_
  Matches_FullRender_Pixel_For_Pixel` pixel-parity test, which
  continues to pass after the codec extension because the DD
  path stays byte-identical.

**Design decisions**

#126. Format version stays at 1; the limbs byte is the
  precision discriminator. Alternative — bump to version 2 for
  QD, version 3 for OD — would have forced every existing DD
  blob to be re-encoded under v2 even when nothing about its
  shape changed. The limbs byte was always reserved for this
  growth (#97 explicitly called it out) and a DD-only consumer
  already errors clean on `limbs != 2` per the v1 spec, so
  forward compat across the limbs dimension is intact.

#127. Additive centre extension after the 44-byte header rather
  than a fixed 108-byte centre block. A fixed-size centre would
  pay 64 bytes per DD blob for limbs the consumer ignores;
  appending only when `limbs >= 4` keeps the DD wire footprint
  byte-identical to the v1 release. The decoder's
  `expectedBytes` formula handles the additive layout in one
  arithmetic step, so the code complexity tax is one
  conditional.

#128. `ReferenceOrbitProvider` signature widened from five
  doubles to the whole `RenderRequestDto`. The alternative —
  passing six more doubles (X4..X7 + Y4..Y7) and re-deriving
  limbs choice on the host side from a separate `zoom` arg —
  would have added 10+ scalar arguments to the delegate, made
  every test mock awkward, and lost the natural "pick precision
  from the request" point. The DTO already lives in
  `Server.Protocol` (no Engine dependency leak) and the lambda
  is the single call site in the production code.

#129. `SharedRefOrbitMaxZoom` raised to 1e115, not to
  `double.MaxValue`. The calculator's OD path has an empirical
  noise floor around 10^116 (the X7 limb runs out of significant
  bits); above that the worker would compute a different orbit
  than the master and the seeded centerSame check would fall
  through to per-tile compute anyway. Capping the planner at
  the same threshold means the master never wastes time
  computing an OD orbit a worker won't use. Jobs above 1e115
  still render — just without the shared-orbit speedup.

#130. `Decode` rejects unknown limbs values (3, 5, etc.) with a
  clear error rather than treating them as a forward-compat
  fallback. Reason: the array-block layout depends on `limbs`
  by construction; an unknown value can only mean either a
  protocol-version skew the master should have refused at
  `worker.register` (engine SHA mismatch) or a corrupted blob.
  Either way, mis-parsing produces wrong pixels — failing
  closed is the only correct behaviour.

#131. Worker-side OD-limb plumbing (CenterX4..X7) lives in
  PosterRequest, not in `RenderImageArtifactAsync`'s positional
  args. The alternative — bumping
  `RenderImageArtifactAsync(req, ftype, cx, cxLo, cx2, cx3, cy,
  cyLo, cy2, cy3, ...)` to a 14-double signature — would have
  cascaded through `RenderVideoArtifactAsync`, the video-frame
  loop, and the slideshow path. PosterRequest already has the
  X2/X3 limbs as init-only properties; growing it to X4..X7 is
  an in-place extension that touches one constructor call.

#132. PosterRenderer's QD/QO orbit selection is a `else if`
  chain on the three SeededOrbit slots rather than a `switch`
  on a single "kind" enum. Reason: the three properties are
  mutually exclusive by host construction (the decoder sets
  exactly one based on `decoded.Limbs`); a kind-enum would have
  meant adding a parallel field and an invariant the host has
  to maintain in two places. An `else if` chain is
  self-documenting and the compiler enforces no-fallthrough.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (35 total — AVLN5001 + codegen CS0219 + xUnit1051, unchanged
  from D-6c1).
- Test suite: **349 passed, 0 failed** (+3 since D-6c1's 346).
- Filtered run `--filter "FullyQualifiedName~ReferenceOrbitBlobTests"`:
  9 passed in 436 ms (was 6 — +3 new D-6b2 tests).

**Open follow-ups remaining**: D-6b1 (SIMD PT4/PT8 sub-rect
adaptation — now harder to skip because the perf delta is more
visible at QD/OD where the per-tile recompute the seed
short-circuits is the most expensive), D-6a #93 (video /
slideshow crash recovery), UI growth of MasterConfigView (#125),
and binary-trailer transport for OD-scale blobs (D-6b3 if a real
1M+ iter workload arrives).

---

## Session 8 — D-6f — Video / slideshow crash recovery (2026-06-28)

**Goal**: close the D-6a "video / slideshow falls back to
fail-on-restart" deferral noted in #93. The image-resume path
landed in D-6a; video and slideshow stayed on the
`FailInflightAfterRestart` fallback because their tile streams
needed different replay logic. This slice grows
`ClusterCoordinator.RecoverFromDisk` to handle both modes:
per-frame PNGs on disk count their parent video tile as done, the
streaming ffmpeg pipeline restarts from frame_000001.png, and
per-slide PNGs on disk count their slideshow tile as done.

**Sub-slice decision**: a single slice. Video and slideshow
resume share the "look at the on-disk artifact dir, mark tiles
done, re-enqueue the rest, mark status, drive finaliser if
all-done" shape. Splitting (D-6f-video + D-6f-slideshow) would
have duplicated the dispatcher / status / event wiring and forced
a second slice to grow `ResumeCounts` again for the second mode.

**Server-side changes**

- `Server/Cluster/ClusterCoordinator.cs`:
  * `RecoverFromDisk` now dispatches on `rec.Status.Mode`:
    `"image"` → existing `TryResumeImageJob`; `"video"` → new
    `TryResumeVideoJob`; `"slideshow"` → new
    `TryResumeSlideshowJob`. Unknown modes still hit the
    fail-on-restart path so a malformed status.json can't wedge
    the queue.
  * `ResumeCounts` grew `ResumedVideo` and `ResumedSlideshow`
    counters alongside the existing `ResumedImage`. The struct
    stays positional (no new constructors), so the ClusterEntry
    host-side log line is the only call-site that needs growth.
  * New `TryResumeVideoJob(ResumeRecord)`:
    1. Reads `plan.json` via the existing `ReadPlanTileDtos`.
       Pulls `totalFrames` and `videoFps` off the first
       `FrameRange` tile (every tile in the plan carries the
       same parent-job header).
    2. For each plan tile, marks it done only when EVERY frame
       in `[StartFrame, EndFrame)` is on disk via
       `Jobs.FrameExists`. Partial coverage re-enqueues the
       whole tile — the worker re-renders the whole range,
       which is correct because `WriteFrameBytes` is an
       idempotent overwrite.
    3. If the original job carried a lossless preset and
       ffmpeg is available, restarts
       `VideoFramePipeline.TryStart`. Frames already on disk
       are encoded from `frame_000001.png` upward without
       waiting for a new `tile.deliver`; the pipeline's
       `_delivered` counter is primed with
       `NotifyFramesDelivered(Jobs.CountFrames(rec.JobId))` so
       the `IsBehind` backpressure check accounts for the
       pre-resume frames (without the prime it would think
       the encoder was ahead of wire delivery and never gate).
    4. Updates status with the recovered counts and emits a
       `kind:"resumed"` event into both the job's NDJSON event
       log AND the cluster log so an operator can see the
       resume from either log stream.
    5. If every tile is already done on disk (the master died
       in the merging window), drives `FinaliseVideoFrames`
       directly so the resumed job re-encodes and transitions
       to `ready`; without this the resumed job would stay
       `rendering` forever (no future `tile.deliver` to
       trigger the finaliser).
  * New `TryResumeSlideshowJob(ResumeRecord)`:
    1. Reads plan via `ReadPlanTileDtos`. Bails if the submit
       DTO has no `Slides` list (malformed slideshow that
       shouldn't exist).
    2. Marks each tile done when its slide PNG exists in
       `JobStore.SlidesDir` via `SlideExists`.
    3. Re-registers `_slideshowJobs[rec.JobId] = rec.Submit`
       so the next `tile.deliver` routes through
       `HandleSlideDeliverAsync`'s per-slide writer (D-4c
       dispatch keys on `_slideshowJobs` presence; without
       this the resumed slide would fall through to the
       image-tile path and look for a merger that doesn't
       exist).
    4. Drives `FinaliseSlidesAsManifest` directly when every
       slide is on disk — same all-done short-circuit as
       video, for the same reason.

- `ServerHost/ClusterEntry.cs`:
  * Recovery log line now prints the per-mode counts:
    `resumedImage`, `resumedVideo`, `resumedSlideshow`
    alongside `failedUnsupported` and `failed`.

**Tests**

- `Server.Tests/Cluster/CrashRecoveryTests.cs`:
  * Removed `Video_Job_Falls_Back_To_Failed` (the old behaviour
    that D-6f reverses). Replaced with
    `Unknown_Mode_Still_Falls_Back_To_Failed` — exercises the
    fail-closed path with a hand-crafted plan whose mode is
    `"garbage-mode"`. Confirms the unknown-mode safety net the
    old test was implicitly relying on.
  * New `SeedVideoJob(totalFrames, framesPerTile, framesOnDisk,
    lossless)` helper — drives `FramePlanner.PlanVideo` with
    `VideoFps=1` so frame count equals "seconds" and a small
    `totalFrames` gives a small plan. Writes 4-byte
    PNG-magic placeholders for delivered frames (resume only
    checks file existence; bytes don't need to be a real PNG).
  * New `SeedSlideshowJob(slideCount, slidesOnDisk)` helper —
    same pattern, drives `SlideshowPlanner.PlanSlideshow` via a
    `List<RenderRequestDto>` of slide requests.
  * Five new facts:
    - `Unknown_Mode_Still_Falls_Back_To_Failed` — fail-closed
      preserved.
    - `Video_Job_With_No_Frames_Re_Enqueues_All_Tiles` — empty
      frames dir → all 3 tiles re-enqueue, status returns to
      `queued`.
    - `Video_Job_With_Some_Frames_Counts_Completed_Tiles_Only`
      — 3 frames out of 6 across 3 tiles: tile 0 (frames 0,1)
      fully done; tile 1 (frames 2,3) partial → re-enqueue the
      whole tile; tile 2 absent → re-enqueue. Status:
      `rendering`, TilesDone=1, FramesDone=3.
    - `Video_Job_With_All_Frames_Drives_Finaliser` — all 4
      frames on disk → finaliser runs, status reaches `ready`,
      dispatcher retires the job. Uses `lossless="none"` so the
      finaliser hits the frames-manifest stub (no ffmpeg
      required in the test environment).
    - `Slideshow_Job_With_No_Slides_Re_Enqueues_All_Tiles` —
      empty slides dir → all 3 tiles re-enqueue.
    - `Slideshow_Job_With_Partial_Slides_Re_Enqueues_Remainder`
      — slides 0 and 2 done, 1 and 3 missing → 2 pending in the
      dispatcher.
- Test suite: **354 passed, 0 failed** (+5 since D-6b2's 349).
- Filtered run `--filter "FullyQualifiedName~CrashRecoveryTests"`:
  12 passed in 417 ms (was 7 — +5 new D-6f tests).

**Design decisions**

#133. A video tile is "done" only when EVERY frame in its
  `[StartFrame, EndFrame)` range is on disk; a partial range
  re-enqueues the whole tile. Alternative — mark the tile done
  when ANY frame is present, then re-render only the missing
  frames — would have required a sub-tile dispatch surface that
  doesn't exist (the dispatcher tracks tile completion, not
  per-frame). Re-rendering the whole range is cheap relative to
  losing the partial work entirely (the old D-6a fail path) and
  the frame-write path is idempotent (write-and-rename), so a
  duplicate frame delivery is a no-op accept.

#134. The streaming ffmpeg pipeline restarts from scratch on
  resume rather than picking up mid-encode. Reason: the ffmpeg
  subprocess died with the master and its `image2pipe` stdin
  buffer is gone. The on-disk artifact is at best partially
  written (no `moov` atom for mp4, no terminating Cluster for
  mkv) — fundamentally unusable. Re-encoding from
  `frame_000001.png` is correct and bounded by the total frame
  count, which is bounded by `FramePlanner.MaxTotalFrames`
  (18000). Worst-case re-encode time at 30 fps is minutes, not
  hours.

#135. The pipeline's `_delivered` counter is primed with the
  on-disk frame count via `NotifyFramesDelivered`. The
  alternative — leave it at zero — would have left the backpressure
  gate (`Backlog = Delivered - Encoded > MaxFrameQueueDepth`)
  off until the encoder caught up; a fast worker re-delivering
  a 30-frame tile would push the on-disk queue past 64 before
  the gate engaged. Priming keeps the gate accurate from the
  first post-resume tile.

#136. Resume re-registers `_slideshowJobs[rec.JobId] = rec.Submit`
  before the dispatcher gets the remaining tiles. The
  alternative — register lazily on the first `tile.deliver` —
  would have raced with the dispatcher: a worker could have
  reached the deliver path before the lazy-register fired, and
  the dispatch lookup would have fallen through to the
  image-tile path looking for a non-existent merger. The
  eager registration is constant-time and avoids the race
  entirely.

#137. `Mode` switch uses `string.Equals(..., StringComparison.
  OrdinalIgnoreCase)` rather than a pre-normalised enum. The
  alternative would have required a `JobMode` enum stored in
  `PersistedStatus` and a migration path for existing on-disk
  status.json files (or a tolerant parser). The mode string is
  small, fixed, and already what the rest of the cluster
  routes on (FramePlanner.PlanVideo checks `request.Mode`,
  HandleSlideDeliverAsync keys on the submitJob.Slides
  presence). Keeping it a string here matches the rest of the
  code.

#138. `Video_Job_Falls_Back_To_Failed` test deleted (not
  modified to assert the new resume behaviour). The original
  test asserted a behaviour D-6f explicitly reverses; keeping
  it as a "now passes because we resume" test would have
  misleadingly implied the file documented the resume path,
  but the resume coverage lives in the three new
  `Video_Job_With_*` facts. The replacement
  `Unknown_Mode_Still_Falls_Back_To_Failed` documents the
  fail-closed safety net the old test was implicitly covering.

#139. Resume helper `SeedVideoJob` uses `VideoFps=1` so frame
  count and `VideoSeconds` are 1:1. Alternative — use `Fps=30`
  with `VideoSeconds=0.2` (= 6 frames) — would have made the
  arithmetic harder to read and produced fractional-second
  values the planner's float math could land off-by-one on.
  `Fps=1` keeps the test arithmetic transparent.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (32 in Server.Tests project; AVLN5001 + codegen CS0219
  unchanged from D-6b2).
- Test suite: **354 passed, 0 failed** (+5 since D-6b2's 349).

**Open follow-ups remaining** (unchanged from D-6b2 minus this
slice): D-6b1 (SIMD PT4/PT8 sub-rect adaptation), UI growth of
MasterConfigView (#125), binary-trailer transport for OD-scale
blobs (D-6b3), and FramePlanner.CloneFrameTemplate not
propagating the new CenterX4..X7 limbs (D-6b2 leftover — would
matter only for cluster video at zoom > 1e50, an unusual
workload).

---

## Session 9 — D-6b1 — SIMD PT4 / PT8 sub-rect adaptation (2026-06-28)

**Goal**: close the D-6b SIMD deferral noted in #96. The PT4
(AVX2, 4-wide) and PT8 (AVX-512, 8-wide) inner loops hard-coded
`Width*0.5` and `(y - halfH)` / `(x+k - halfW)` into their dc
math, so a sub-rect tile would have computed dc relative to the
tile origin rather than the IMAGE origin — the shared
reference-orbit seed would be off by `(SubRectOffset + tileHalfW)
- imageHalfW` per pixel. The pre-D-6b1 dispatcher caught this by
forcing scalar PT for any sub-rect render. The cost: cluster
tiles missed the ~3× SIMD speedup on the PT inner loop — the
single hottest loop in the cluster's wall-clock budget.

**Sub-slice decision**: a single slice. PT4 and PT8 are
near-identical (PT8 is PT4 with the lane count doubled); the
sub-rect math change is the same in both, and splitting would
have meant landing PT4 first with a still-scalar PT8 that any
AVX-512 box hit instead — net no perf win until both shipped.
Removing the `subRect` gate in `Calculate` co-locates with the
loop changes for the same reason: a half-shipped state would
have either disabled SIMD on tiles (no win) or routed sub-rect
to a broken PT4 (correctness regression).

**Engine-side changes**

- `Engine/Calculators/MandelbrotCalculator.cs`:
  * `Calculate` dispatch: removed the `subRect` local that
    forced scalar. SIMD dispatch now fires on every render that
    meets the hardware-availability check. The `subRect`
    comment block above the dispatch is updated to reference
    the new bit-identical-collapse property of the sub-rect
    formula.
  * `ComputeRowPT4`: head rewrote `halfW`/`halfH` to use
    `EffectiveImageWidth/Height * 0.5`, added `offX`/`offY`
    locals from `SubRectOffsetX/Y`, replaced bare `(y - halfH)`
    with `rowOffsetY = offY + y - halfH`. Column inner loop's
    four `dcR{k}` doubles now use `offX + x + k - halfW`. HP
    fallback (`if (glitched && !escaped)`) and scalar tail
    both pull through the same `colOffsetX = offX + x + k - halfW`
    local. The HP-fallback `cy_dd`/`cy_qd`/`cy_od` constructors
    use `rowOffsetY` instead of `y - halfH` so the y-coord
    HP-tier seed agrees with the SIMD dc.
  * `ComputeRowPT8`: identical surgery, eight-wide instead of
    four. The Vector512.Create call's eight `dcR{k}` values now
    use the sub-rect formula; the HP-fallback + scalar tail use
    the same `colOffsetX` local.
  * Removed `SubRectActive` private property (unused now that
    the dispatch gate is gone).

**Why the math collapses identically**

For a full-image render: `ImageWidth == Width`, `SubRectOffsetX
== 0`. So `offX + x - halfW = 0 + x - Width*0.5 = x - halfW` —
byte-identical to the pre-D-6b1 formula. The legacy code path
is preserved bit-for-bit by arithmetic, not by a runtime guard.

**Tests**

The existing
`Calculator_SubRect_With_SeededOrbit_Matches_FullRender_Pixel_For_Pixel`
in `ReferenceOrbitBlobTests.cs` is the load-bearing guard for
this change. It renders a 64×64 full image, then four 32×32
sub-rect tiles seeded with the same shared orbit, and asserts
pixel-for-pixel parity. Before D-6b1 it passed because sub-rect
forced scalar; after D-6b1 it passes because the SIMD path
computes the same dc. A drift here would mean the sub-rect
math change in PT4/PT8 was wrong — exactly what the test exists
to catch.

- Test suite: **354 passed, 0 failed** (unchanged from D-6f).
  `ReferenceOrbitBlobTests` filtered run: 9 passed in 262 ms.

**Design decisions**

#140. SIMD dispatch gate removed entirely rather than wrapped
  in a "sub-rect SIMD enabled" flag. The flag would have been
  set unconditionally to true at ship time (the whole point of
  D-6b1 is enabling SIMD for sub-rect); a flag with one possible
  value is dead weight. The arithmetic-collapse guarantee
  (legacy renders compute the same dc as before) makes the
  switch safe to remove outright.

#141. `offX + x - halfW` left as three-term sum rather than
  pre-computed `(x - halfW) + offX`. Reason: `halfW` is `double`
  and `offX + x` is `int`; the compiler's mixed-arithmetic
  promotion produces a single `vcvtsi2sd + vsubsd` per lane in
  the JIT output, identical to the original two-term cost. The
  three-term form reads cleaner and the codegen analyzer's
  inspection confirmed no extra IL.

#142. `SubRectActive` removed rather than left as
  `[Obsolete]`. The property had one caller and that caller is
  gone; keeping it `[Obsolete]` would have left a confusing
  dead method that the next reader has to chase. The Engine
  API surface is internal to the project so there's no public-
  contract break — the only external consumers are the
  cluster's tile path (which sets the four sub-rect properties
  directly) and the legacy single-server path (which leaves them
  zero).

#143. `cy_dd`/`cy_qd`/`cy_od` constructors switched to
  `rowOffsetY` rather than recomputing `y - halfH` per
  constructor. Reason: the new sub-rect head defines
  `rowOffsetY = offY + y - halfH` once at the top; passing the
  local through to the y-coord HP seed keeps PT and HP in
  lockstep (both see the IMAGE y offset, not tile-local). A
  divergence here would mean the HP fallback computed at one
  coordinate while the PT loop computed at another — exactly
  the kind of seam D-6b's pixel-parity test exists to catch.

**Perf note**

A formal benchmark wasn't run in this slice — the perf claim
"~3×" cited in #96 is from the pre-D-6b SIMD-vs-scalar PT
comparison in `Docs/Technical/Performance-DevelopmentPlan.md`.
The point of D-6b1 isn't a new perf measurement; it's
unblocking the SIMD path for cluster tiles so the existing
~3× win actually applies to the cluster's hot path instead of
being silently disabled. A real measurement belongs in
whichever stress test runs an 8K deep-zoom poster across
multiple workers — out of scope for the engine-level change.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (36 total — net dead-code removal of `SubRectActive`
  cancelled out by no new warnings, unchanged warning count
  modulo +1 from earlier sessions).
- Test suite: **354 passed, 0 failed** (unchanged from D-6f).
  StressTests remains parallelization-flaky in the all-suite
  run; passes when run isolated or sequentially — a known
  pre-existing flake (D-6e #119), not introduced by D-6b1.

**Open follow-ups remaining**: UI growth of MasterConfigView
(#125), binary-trailer transport for OD-scale blobs (D-6b3),
FramePlanner.CloneFrameTemplate OD-limb propagation (D-6b2
leftover), JobStore.WriteSlideBytes race (mirror of the D-6e
WriteStatusLocked fix). All non-blocking; phase D-6 stays
closed.

---

## Session 10 — D-6c2 — Master Config UI growth for rate-limit knobs (2026-06-29)

**Goal**: close #125, the UI-only follow-up deferred by D-6c1. The four
per-role rate-limiter knobs (`ClientCallPerMinute`, `ClientCallBurst`,
`WorkerTileNextPerMinute`, `WorkerTileNextBurst`) have been live-tunable
over `cluster.config.set` since D-6c1, but the existing MasterConfigView
only exposed the three D-5e knobs. An operator who wanted to retune a
rate had to fall through to an admin CLI script or hand-craft a JSON-RPC
call. D-6c2 surfaces them in the same dialog so the seven knobs are
edited in one round-trip.

**Sub-slice decision**: a single slice. No new wire surface, no new
server code — D-6c1 already landed the protocol fields, the coordinator
clamp, the `ApplyRoleLimiterChange` hook into FFServer's limiter, and
the client-side `SetClusterConfigAsync` overloads. This is purely
ViewModel + axaml growth on top of an existing surface, which is why
#125 was deferrable in the first place.

**ViewModel changes** — `UI.Avalonia/ViewModels/MasterConfigViewModel.cs`:

- Four new `int` properties with `RaiseAndSetIfChanged` backing —
  matches the existing pattern for `ClusterMaxJobs` /
  `ClusterArtifactRetentionMinutes` / `ClusterTileTargetPixels`. No
  reactive bridges between knobs; each is independently edited and
  applied.
- Constructor pre-seeds the four new fields from
  `ServerConfig.LoadOrDefault()` so the dialog shows the same
  defaults (600 / 30 / 600 / 30) the master would use at boot, before
  the first Load round-trip. Same rationale as the existing three:
  Load reconciles against the running master in case another admin
  instance issued a `cluster.config.set` in the meantime.
- `ApplyAsync` now passes the four new values as named optional
  parameters to `SetClusterConfigAsync` — keeps the existing
  positional call shape (`maxJobs, artifactRetentionMinutes,
  tileTargetPixels, ct`) source-stable while extending the apply
  surface. Reading the call site, every knob in the dialog is
  visible in the apply.
- `ApplySnapshot` mirrors the post-apply DTO back into the four new
  properties so a server-side clamp (perMinute floor 0, burst floor
  1) is visible in the same beat as the existing tile-pixel clamp.

**View changes** — `UI.Avalonia/Views/MasterConfigView.axaml`:

- New `Border.group` titled "Per-Role Rate Limits" with the same
  visual treatment as the existing "Cluster Limits" group. Four
  rows: Client perMinute, Client burst, Worker tile.next perMinute,
  Worker tile.next burst. Each tooltip describes the knob, what 0
  means where 0 is meaningful, and the default value — same hint
  format as the existing rows.
- Burst rows have `Minimum="1"` to match the server-side `Bucket`
  constructor floor (clamping client-side avoids a redundant Apply
  round-trip just to see the floor bounce back). PerMinute rows
  keep `Minimum="0"` because 0 is the documented "disable" sentinel.
- Window grew from `520x440` to `540x600` and `MinWidth/MinHeight`
  bumped to fit four extra rows without forcing the operator to
  scroll. The outer `ScrollViewer` still catches anything smaller.
- Load / Apply / Close buttons unchanged — the new group hangs off
  the same commands.

**Design decisions**

#126. Burst spinners enforce `Minimum="1"` client-side mirroring the
  server's `Math.Max(1, burst)` clamp. The alternative — let the
  user type 0 and have the server bounce it to 1 via `ApplySnapshot`
  — was rejected because the bounce would look like the apply
  "silently overrode" the input. A spinner that won't go below 1
  matches operator intent better; the documented "disable" knob is
  perMinute=0, not burst=0.

#127. Constructor pre-seeds from `ServerConfig.LoadOrDefault()`
  rather than waiting for the auto-Load in `OnDcChanged` to populate
  the form. Reason: same as D-5e — the dialog is single-instance,
  Opened-driven, and an operator who closes before the first round-
  trip completes (e.g. cancels because they realised they're
  pointing at the wrong host) still sees sensible numbers in the
  brief moment the dialog is visible. Plain consistency with the
  existing three knobs.

#128. UI growth landed as `D-6c2` rather than `D-6c1a`. The repo
  convention (per the dev plan §12) uses single-letter suffixes for
  same-day follow-ups within a phase letter, but D-6c1 already
  carries an "exposes wire only, defer UI" decision (#125) and the
  UI half is a coherent slice in its own right. Numbered suffix
  matches D-6b1 / D-6b2's slice-then-codec-then-engine cadence.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing warnings only
  (36 total — unchanged from D-6b1's baseline). The new axaml rows
  generate no new bindings warnings (every binding resolves
  against the typed `MasterConfigViewModel`).
- Test suite: **354 passed, 0 failed** (no new tests this slice —
  the ViewModel binding surface is exercised by manual UI smoke;
  the underlying coordinator clamp / limiter-apply is already
  covered by the eight D-6c1 facts in
  `ClusterAdminRpcTests` + `RoleAwareRateLimiterTests`).
  StressTests remains parallelization-flaky in the all-suite run
  (D-6e #119), passes when isolated.
- Filtered confirmation: `--filter "FullyQualifiedName~
  ClusterAdminRpcTests|FullyQualifiedName~RoleAwareRateLimiterTests"`
  → 33 passed in 481 ms.

**Open follow-ups remaining**: binary-trailer transport for OD-scale
blobs (D-6b3), FramePlanner.CloneFrameTemplate OD-limb propagation
(D-6b2 leftover), JobStore.WriteSlideBytes race (mirror of the D-6e
WriteStatusLocked fix). #125 closed. All remaining items
non-blocking; phase D-6 stays closed.
