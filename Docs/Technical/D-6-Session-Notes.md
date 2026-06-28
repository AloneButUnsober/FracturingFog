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
