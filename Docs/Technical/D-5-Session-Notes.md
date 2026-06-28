# D-5 Session Notes — Admin UI

Phase D-5 from `DistributedRendering-DevelopmentPlan.md`. Goal: drive an
end-to-end cluster render from `UI.Avalonia/` with no CLI, plus recover
cleanly when a worker dies mid-job.

Carved into D-5a..D-5e because §8 of the dev plan lists five view trees
plus the connection wrapper; each sub-slice is one commit.

---

## Session 1 — D-5a — Admin RPCs on the master (2026-06-28)

**Goal**: light up the master-side admin surface so the upcoming
`UI.Avalonia/` views have something to call. Workers, jobs, control.

**New protocol methods** (all under the `cluster.*` namespace so the
existing FFServer role gate — `cluster.* → admin only` — covers them; the
dev plan's literal `worker.quiesce` / `worker.kill` move here because
`worker.*` is reserved for worker→master traffic and routes to
`CertRole.Worker`):

- `cluster.status` → `ClusterStatusDto` — snapshot of every connected
  worker plus the N most-recent jobs from the on-disk store. Used to back
  the dashboard view in D-5b.
- `cluster.quiesceWorker { workerId, quiesced }` → `WorkerQuiesceAckDto`.
  Sets/clears the worker's quiesce flag (the dispatcher already honours
  it from D-3b work-stealing). Reports previous + current state for
  optimistic-concurrency awareness in the UI.
- `cluster.killWorker { workerId }` → `WorkerKillAckDto`. Removes the
  entry from the registry; subsequent heartbeats from that worker fail
  with `unknown-worker` and the worker tears down. Idempotent.
- `cluster.listJobs { limit?, includeTerminal?, stateFilter? }` →
  `JobListDto`. Newest-first paged job summaries from disk. Distinct
  from the dashboard's embedded `Jobs` block because the JobListView in
  D-5c may filter (e.g. failed-only) and paginate deeper.

**Files**

- New: `Server/Cluster/Protocol/ClusterStatusDto.cs` (request/response +
  `WorkerSummaryDto` + `JobSummaryDto`).
- New: `Server/Cluster/Protocol/WorkerControlDto.cs` (quiesce + kill
  request/ack pairs).
- New: `Server/Cluster/Protocol/JobListDto.cs` (list request +
  response).
- Amended: `Server/Cluster/JobStore.cs` — `PersistedStatus.Mode` field
  added; `Create` caches `plan.Mode` so admin summaries don't crack
  `plan.json` per row.
- Amended: `Server/Cluster/ClusterCoordinator.cs` — switch routes for the
  four new methods; `HandleClusterStatusAsync`, `HandleClusterQuiesceWorkerAsync`,
  `HandleClusterKillWorkerAsync`, `HandleClusterListJobsAsync`; shared
  `BuildWorkerSummary` + `BuildRecentJobSummaries` helpers.
- New: `Server.Tests/Cluster/ClusterAdminRpcTests.cs` — 9 tests covering
  the success + idempotency + filter paths.

**Design decisions**

#63. `cluster.*` not `worker.*` for admin per-worker mutations. Reason:
  FFServer's existing role gate (`worker.* → CertRole.Worker`) refuses
  admin role on `worker.*` methods. Two options were on the table —
  per-method ACL override in FFServer, or moving the methods into the
  `cluster.*` namespace where the gate already permits admin. Picked the
  latter: smaller blast radius, no FFServer changes, the dev plan's
  literal method names were illustrative not contractual.

#64. `Mode` cached in `PersistedStatus`. Reason: the admin job-list view
  groups by image/video/slideshow and would otherwise need a per-row
  `plan.json` read on every refresh. One extra string in `status.json`
  is cheap; the cracking was not.

#65. `cluster.killWorker` is idempotent. Reason: the admin UI may double-
  click; we'd rather report `removed=false` than refuse with `unknown-
  worker`. The user-visible action (worker gone from registry) is the
  same either way.

#66. `cluster.listJobs` reads status from disk per call rather than
  keeping an in-memory index. Reason: with the existing `JobArtifactRetentionMinutes`
  sweep the on-disk job count stays bounded; a parallel index would
  double bookkeeping and create drift bugs. Reconsider when the master
  starts serving >1000 jobs.

#67. `WorkerSummaryDto` includes `EmaMsPerKilopixel` + `TileSamples`
  even though no current view binds them. Reason: the WorkerDetailView
  in D-5d shows per-worker throughput history; surfacing it now keeps
  the wire shape stable so adding the binding later doesn't churn
  the contract.

**Build + test**

- `Server` project: 0 warnings, 0 errors.
- `FracturingFog.App` (cross-plat): 24 warnings (pre-existing Avalonia
  + codegen), 0 errors.
- `FracturingFogCLD` (legacy WinExe): 0 warnings, 0 errors.
- Test suite: **306 passed, 0 failed** (was 297; +9 admin RPC tests).
- Filtered run `--filter "FullyQualifiedName~ClusterAdminRpcTests"`:
  9 passed in 325 ms.

**Next session** opens D-5b: `FFAdminConnection` (admin-cert wrapper
over `FFClientConnection`) + the `ClusterDashboardView` workers grid.
Yellow `#FFCC00` for stale/quiesced/problem states per the user's
colourblind note in CLAUDE.md.
