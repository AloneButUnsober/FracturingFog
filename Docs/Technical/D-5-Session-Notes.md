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

---

## Session 2 — D-5b — FFAdminConnection + ClusterDashboardView (2026-06-28)

**Goal**: ship the admin-cert wire wrapper + the first Avalonia view that
binds against it. A running master with at least one worker should show up
in the dashboard, polled every 5 s.

**New types**

- `Client/FFAdminConnection.cs` — composes `FFClientConnection`, exposes
  only the four admin RPCs from D-5a (`GetClusterStatusAsync`,
  `SetWorkerQuiescedAsync`, `KillWorkerAsync`, `ListJobsAsync`). Same
  TLS plumbing, just an OU=role-admin cert.
- `UI.Avalonia/ViewModels/ClusterDashboardViewModel.cs` — `DispatcherTimer`
  on a 5 s cadence (mirrors `ServerAdminViewModel`); rebuilds `Workers` /
  `RecentJobs` observable collections from each `cluster.status` snapshot.
  Cert paths resolved from `%APPDATA%\FracturingFog\cluster-certs\` so no
  extra config UI is needed for the first cut.
- `UI.Avalonia/ViewModels/ClusterDashboardViewModel.cs` also carries the
  two row VMs `ClusterWorkerRowVm` + `ClusterJobRowVm` — formatting +
  derived state (stale flag, status badge, `#FFCC00` row background) lives
  there so the XAML stays declarative.
- `UI.Avalonia/Views/ClusterDashboardView.axaml` (+ `.cs`) — two stacked
  `ItemsControl` grids (workers above, recent jobs below) with hand-rolled
  fixed-column rows. Plain `DataGrid` was rejected because Avalonia's
  DataGrid template can't bind a per-row background to a string property
  without an `IValueConverter`, and the converter machinery would have
  been bigger than the markup it replaced.

**Wiring**

- `FFClientConnection.CallAsync<T>` flipped from `private` to `internal` so
  the new admin wrapper (same assembly) can share the framing/error-envelope
  plumbing instead of duplicating it.
- `ServerAdminViewModel` gains `OpenClusterDashboardCommand` +
  `OpenClusterDashboardRequested` event; the SAVM owns no knowledge of the
  cluster view — it just raises and the shell handles routing (mirrors the
  `HelpRequested` pattern used by the colour-theme editor).
- `ServerAdminView.axaml` — new "Cluster" group below the Lifecycle group
  with a single "Cluster Dashboard…" button. One-line hint reminds the
  operator that the master must run once to mint `admin.pfx`.
- `ShellViewModel` — new `ClusterDashboard` property +
  `IsClusterDashboardVisible` flag + `ShowClusterDashboard()` private
  method; subscribes `OpenClusterDashboardRequested` when the SAVM is
  lazily constructed in `ShowServerAdmin`.
- `MainWindow.axaml.cs` — new `_clusterDashboardWin` field +
  `SyncClusterDashboard()` clone of `SyncServerAdmin`, plus the matching
  `OnClosed` close. Property-change switch routes both
  `IsClusterDashboardVisible` and `ClusterDashboard` through the sync.

**Design decisions**

#68. `FFAdminConnection` is composition, not inheritance. Reason:
  inheriting would expose the parent's `RenderImageAsync` /
  `SubmitJobAsync` surface on an admin connection — those would silently
  fail with `forbidden` because the master role gate refuses `render.*` and
  `job.*` from `CertRole.Admin`. Composition makes the smaller surface a
  compile-time guarantee.

#69. Yellow `#FFCC00` for both stale workers AND quiesced workers, plus
  failed/cancelled jobs. Reason: the user's red-green colourblindness
  (CLAUDE.md memory note) means red wouldn't read as a warning. The
  existing convention in `ServerAdminView` for problem text (the help
  button's `#FFCC00` foreground) extends naturally to row backgrounds.
  Distinguishing stale vs. quiesced is left to the `StatusBadge` text
  ("STALE" / "QUIESCED" / "LIVE") since both states are equally
  actionable from a dashboard.

#70. Cert bundle is resolved by absolute path
  (`%APPDATA%\FracturingFog\cluster-certs\admin.pfx`) rather than reusing
  the per-machine cluster bundle generator. Reason: pulling
  `ServerHost.ClusterEntry` into `UI.Avalonia/` would drag in the headless
  render engine + the entire cluster master assembly, blowing up the UI
  project's transitive closure. The dashboard only needs file paths; the
  master is what actually mints the bundle on first run.

#71. Per-row `Background="{Binding RowBackgroundHex}"` is bound straight
  to a string ("#FFCC00" / "Transparent"). Reason: Avalonia auto-converts
  string colours to `ISolidColorBrush` at bind time. Removed an
  `IValueConverter` and a `ResourceDictionary` switch from the XAML; if the
  D-5d kill/quiesce buttons want hover states later, they can promote
  this to an `IBrush` without changing the wire shape.

#72. Dashboard window is launched from `ServerAdminView` rather than a
  top-level floating-menu button. Reason: the dev plan §8 puts the whole
  cluster admin surface (dashboard + per-worker detail + per-job detail +
  master config) behind one launch point; keeping the entry inside the
  existing server admin window means D-5d's MasterConfigView can land
  inline as a tab without re-opening the discovery question. Floating menu
  stays tidy.

**Build + test**

- Solution build (Debug): 0 errors, 35 pre-existing warnings (codegen
  CS0219 unused vars + 4 `TextBox.Watermark` obsoletes from older Avalonia
  views).
- Test suite: **306 passed, 0 failed** — unchanged from D-5a; D-5b adds no
  test files (the wire is already covered by `ClusterAdminRpcTests`, the
  wrapper is a pass-through, and the view smoke lands in D-5e).

**Next session** opens D-5c: `JobListView` (paged + filterable, backed by
`cluster.listJobs`) and `JobDetailView` (per-job tile map coloured by
worker, polled via the existing `job.status` route). Will need to surface
per-tile `WorkerId` in `JobStatusDto` — currently the status payload has
counters only.

---

## Session 3 — D-5c — JobListView + JobDetailView (2026-06-28)

**Goal**: drill-in from the dashboard. Paged + filterable job browser
(`JobListView`) and a per-job tile map coloured by worker (`JobDetailView`).
Operator should be able to click a job row from the dashboard, see the
spatial grid filling in as tiles deliver, and tell at a glance which worker
is rendering which tile.

**Wire protocol**

- New method `cluster.jobTileMap { jobId } → JobTileMapDto`. The payload
  scales with tile count, so this is a separate RPC from `job.status`
  (which stays counters-only and high-rate) rather than a fatter
  status payload. JobDetailView polls at 2 s on the open job; the
  master clamps nothing (the caller is admin-role and the payload bound
  is `TileCount * ~80 bytes`, fine for image jobs up to a few thousand
  tiles).
- `JobTileMapDto`: jobId + jobState + mode + imageW/H + per-tile
  `{ tileId, offsetX, offsetY, width, height, state, workerId? }`. Image
  mode emits real rects; video / slideshow emit empty rects (no spatial
  layout) and the UI auto-tiles into a near-square grid so the per-tile
  worker colour is still visible.

**Server-side changes**

- `TileDispatcher.AcceptDelivery` signature grew a `string workerId`
  parameter; the completed-tile map flipped from
  `ConcurrentDictionary<int, bool>` to `<int, string>` so the per-tile
  worker is remembered for the dashboard. All call sites updated
  (ClusterCoordinator's 3 routes + two ServerHost self-tests + 8 unit
  tests). `CompletedCount` is value-type-agnostic so the rest of the
  code didn't move.
- New `TileDispatcher.SnapshotTileStates(jobId)` returns a fresh
  `Dictionary<int, TileLiveState>` keyed by tileId, value carries
  state ("pending" / "inflight" / "completed") + worker id. In-flight
  loses ties to completed so a stolen-tile duplicate that already
  delivered shows as completed, not inflight.
- `ClusterCoordinator.HandleClusterJobTileMapAsync` reads
  `PersistedStatus.Mode`, walks `plan.json` for tile rects in image
  mode (`ReadTileLayout`), and merges with the dispatcher snapshot.
  Terminal jobs (ready / failed / cancelled) have an empty dispatcher
  state — handler synthesises "completed" entries from `TilesTotal`
  so the UI still shows a finished grid.
- 5 new tests in `ClusterAdminRpcTests` (unknown-job, image rects,
  in-flight + completed worker attribution, terminal synthesis,
  video counters-without-rects). Total Server.Tests: **311 passed**
  (was 306; +5).

**Client-side changes**

- `FFAdminConnection.GetJobTileMapAsync(jobId)` thin wrapper.
- `JobListViewModel` + `JobListView.axaml` — header form for state
  filter dropdown + include-terminal checkbox + limit, paginated grid
  with per-row "Open" button bound to `OpenJobDetailCommand`. 10 s
  poll cadence (browse, not monitor).
- `JobDetailViewModel` + `JobDetailView.axaml` — 2 s poll, builds
  `Tiles` (canvas-laid-out rects) + `Workers` (legend strip). Per-
  worker colour is FNV-1a hash → HSL with fixed S/L for readable
  contrast on the dark grid; pending tiles are `#3A3A3A`. Polling
  stops automatically when the job hits a terminal state so a
  finished job doesn't keep the timer hot.
- Dashboard launchers: header "All Jobs…" button → `JobListView`;
  per-row "Open" button → `JobDetailView` with that jobId. SAVM
  remains the single launch point for `ClusterDashboardView`.
- Shell wiring: `JobList` + `JobDetail` lazy-singleton VMs +
  `IsJobListVisible` / `IsJobDetailVisible` flags +
  `ShowJobList()` / `ShowJobDetail(jobId)`. `JobDetailView` is
  single-instance; re-open with a different jobId swaps the
  VM-bound id and brings the window to the front.
- MainWindow: new `_jobListWin` / `_jobDetailWin` fields,
  `SyncJobList` / `SyncJobDetail` clones of `SyncClusterDashboard`,
  plus matching `OnClosed` close.

**Design decisions**

#73. `cluster.jobTileMap` is a distinct RPC from `job.status`, not
  an extension of it. Reason: `job.status` is on the hot client-
  polling path (rendering progress bar, etc.) and runs at 1 Hz on
  every active job. The tile-map payload scales with `TileCount` —
  a 64-tile image job would add ~5 KiB per status poll for nothing
  the client renderer needs. Keeping it admin-only behind a separate
  route means the per-tile array never bloats normal traffic, and
  the master's role gate (cluster.* → admin) handles authorisation
  for free.

#74. Per-tile workerId tracked in the dispatcher (in-memory),
  not parsed back from `events.ndjson`. Reason: the events log is
  append-only audit and re-parsing per request is O(events) per
  call — fine for forensics, terrible for a 2 s poll. The
  dispatcher already tracks per-tile state; changing the value
  type of `Completed` from `bool` to `string` (workerId) cost zero
  memory in practice and gives the snapshot accessor an O(1)
  lookup. Trade-off: terminal jobs lose the per-tile worker
  attribution (the dispatcher retires the job on completion) — the
  handler synthesises "completed" rows without a workerId in that
  case. Acceptable; the operator opens the tile map mostly while
  the job is in flight.

#75. Tile-grid colour assignment uses FNV-1a hash → HSL, not a
  fixed palette. Reason: the cluster has no upper bound on worker
  count (admin may attach + detach over time), so a fixed palette
  would either be too short (collisions) or too long (low-contrast
  shades to fill the slots). Hashing means the colour is stable per
  workerId across refreshes — important so the operator can build a
  mental "worker X is the green one" map — and HSL with fixed S/L
  keeps contrast predictable against the `#0E0E0E` canvas. Yellow
  `#FFCC00` stays reserved for problem rows (matches existing
  dashboard convention for the colour-blind user) so the hash never
  generates a tile in that hue.

#76. JobDetailView is single-instance with a settable `JobId`, not
  a per-id window dictionary. Reason: the operator drills in,
  reads the grid, picks another job. Windows-per-jobId would
  proliferate and confuse — same workflow as a browser tab vs.
  a new browser. The setter clears tile + worker collections and
  kicks an immediate poll so the swap is visible without waiting
  for the next 2 s tick.

#77. Polling stops automatically on terminal job state. Reason:
  a ready / failed / cancelled job will never change tiles again;
  a 2 s timer firing forever would burn CPU + mTLS handshakes on
  the master for no gain. Refresh button stays wired so an
  operator can manually re-pull (useful right after a kill+restart
  if the dashboard cached a stale terminal).

**Build + test**

- Solution build (Debug): 0 errors, pre-existing AVLN5001
  `TextBox.Watermark` obsoletes + codegen CS0219 warnings only.
- Test suite: **311 passed, 0 failed** (was 306; +5 in
  `ClusterAdminRpcTests` for the new RPC).
- Filtered run `--filter "FullyQualifiedName~ClusterAdminRpcTests|FullyQualifiedName~TileDispatcherTests"`:
  30 passed (16 dispatcher + 14 admin — admin grew from 9 → 14
  with the new tile-map tests).

**Next session** opens D-5d: `WorkerDetailView` (quiesce / resume / kill
buttons + per-worker throughput history) and `MasterConfigView` (cluster
config tab inside `ServerAdminView`). `cluster.quiesceWorker` and
`cluster.killWorker` already exist (D-5a); the work is the UI affordance
and a per-worker drill-in window — same shell pattern as `JobDetailView`
but parameterised by `workerId` instead of `jobId`.

---

## Session 4 — D-5d — WorkerDetailView (2026-06-28)

**Goal**: per-worker drill-in from the dashboard workers grid. Capabilities
+ live telemetry + quiesce / resume / kill action buttons. Single-instance
window parameterised by `workerId`, mirroring `JobDetailView`. No new
master RPCs — `cluster.status` already returns every worker; the detail
view filters client-side.

**Sub-slice decision**: D-5d in §9 lists both `WorkerDetailView` *and*
`MasterConfigView`. Splitting them keeps each commit a single cohesive
chunk (matches D-5a/b/c cadence). `MasterConfigView` slides to D-5e —
that work needs three new `ServerConfig` cluster fields (`ClusterMaxJobs`,
`ClusterArtifactRetentionMinutes`, `ClusterTileTargetPixels`) plus
`cluster.config.get/set` RPCs, which is its own coherent slice.

**Wire protocol** (additive, no new methods)

- `WorkerSummaryDto` gains three capability fields previously only on
  `WorkerEntry`:
  `SupportedFractalTypes` (list), `EngineBuildSha` (string), `ProtocolVersion`
  (string). Promoting them to the snapshot avoids a second RPC for the
  detail view. JSON contract stays forward-compatible — older masters
  return empty/missing values and the UI renders "—".

**Server-side changes**

- `Server/Cluster/Protocol/ClusterStatusDto.cs` — three new
  `[JsonPropertyName]` properties on `WorkerSummaryDto`.
- `Server/Cluster/ClusterCoordinator.cs` — `BuildWorkerSummary` populates
  the new fields off `WorkerEntry` (already tracked since D-1).
- No new tests: the wire shape change is a pass-through of fields already
  validated by D-1 registration tests; existing `ClusterAdminRpcTests`
  continue to pass unchanged.

**Client-side changes**

- New `UI.Avalonia/ViewModels/WorkerDetailViewModel.cs` — composes
  `FFAdminConnection`, polls `cluster.status` at 5 s (same cadence as the
  dashboard; tile-level data is not the point of this view), filters by
  `WorkerId`. Exposes:
  * Identity / capabilities panel: name, OS, CPU, cores, RAM, GPUs,
    fractal types, max-concurrent-tiles, preferred tile pixels, engine
    SHA, protocol version, registered-at.
  * Telemetry panel: heartbeat age, tiles in flight, CPU%, free RAM,
    EMA ms/kilopixel, tile samples, last note.
  * Actions: `QuiesceCommand` / `ResumeCommand` → `cluster.quiesceWorker`,
    `KillCommand` → `cluster.killWorker`. Each refreshes the local view
    after the RPC so the badge flips without waiting for the 5 s tick.
  * `IsPresent` flag flips false on a successful Kill — view keeps the
    last-seen capabilities visible but marks the badge "GONE" so the
    operator doesn't lose context (the row would otherwise blank out).
- New `UI.Avalonia/Views/WorkerDetailView.axaml` + `.cs` — header with
  WorkerId + status badge (yellow `#FFCC00` background for STALE /
  QUIESCED / GONE per the colour-blind convention), two capability /
  telemetry forms, action button strip with `danger`-class Kill button,
  scroll-viewer wrap so the forms stay readable at 480 px width.
- `ClusterDashboardViewModel` — new `OpenWorkerDetailCommand`
  (`<ClusterWorkerRowVm>`) + `OpenWorkerDetailRequested` event. Routes
  through the shell the same way `OpenJobDetailRequested` does.
- `ClusterDashboardView.axaml` — workers grid grew a 10th column "Detail"
  with a per-row "Open" button (mirrors the existing Open button on the
  recent-jobs grid).
- `ShellViewModel` — lazy-singleton `WorkerDetail` property +
  `IsWorkerDetailVisible` flag + `ShowWorkerDetail(workerId)` private
  method. Re-open with a different workerId swaps the VM-bound id and
  brings the window to the front (same single-instance pattern as
  `JobDetailView`).
- `MainWindow.axaml.cs` — `_workerDetailWin` field + `SyncWorkerDetail`
  clone of `SyncJobDetail` + matching `OnClosed` close. Property-change
  switch routes both `IsWorkerDetailVisible` and `WorkerDetail`.

**Design decisions**

#78. No new `cluster.workerDetail` RPC. Reason: `cluster.status` already
  returns every connected worker plus the capability metadata the detail
  view needs (after the small `WorkerSummaryDto` extension). A dedicated
  per-worker RPC would add round-trip churn for no information not in
  the existing snapshot, and would require a parallel role-gate +
  handler + test. Filtering client-side costs O(workers) per refresh —
  even at 1000 workers, well under a millisecond.

#79. `WorkerSummaryDto` extended in place rather than introducing a
  parallel `WorkerDetailDto`. Reason: same admin caller, same trust
  level, three small string/list fields. A separate DTO would double
  the wire shape for one consumer and force two code paths in
  `BuildWorkerSummary`. The forward-compat story holds because every
  new field is optional with a sensible empty default — older masters
  serialise nothing for them and `System.Text.Json` deserialises to
  the empty/null values the UI already coerces to "—".

#80. Detail view stays on the dashboard's 5 s poll cadence rather than
  the tile-map's 2 s. Reason: per-worker telemetry changes slower than
  tile state — heartbeats arrive at the 5 s interval the master
  advertises in `cluster.status.heartbeatIntervalSeconds`, so polling
  faster than that wouldn't surface fresher data. Tile-map at 2 s is
  the one place faster matters (in-flight tile coloration), and it
  pays the higher mTLS handshake cost only on the open job.

#81. Successful Kill leaves the last-seen capabilities visible with a
  "GONE" badge instead of blanking the form. Reason: the operator
  clicked Kill on purpose; immediately erasing what the worker *was*
  removes the context needed to confirm "yes that was the right
  machine to evict". The badge plus telemetry going to "—" is the
  honest signal: identity preserved, live state cleared.

#82. `MasterConfigView` deferred to D-5e rather than bundled here.
  Reason: it requires three new `ServerConfig` fields, plumbing
  through `ClusterCoordinator.Start` at master spawn, two new
  `cluster.config.*` RPCs, and a new tab on `ServerAdminView`. That's
  a second cohesive chunk; bundling would conflate UI surface
  (D-5d) with config-knob plumbing (D-5e). D-5a/b/c established the
  one-chunk-per-commit cadence.

**Build + test**

- Solution build (Debug): 0 errors, pre-existing AVLN5001
  `TextBox.Watermark` obsoletes + codegen CS0219 warnings only (35
  total, unchanged from D-5c).
- Test suite: **311 passed, 0 failed** (unchanged from D-5c; no new
  tests since the wire path is a pass-through of already-validated
  fields and the view-model is a thin client of `cluster.status` /
  `cluster.quiesceWorker` / `cluster.killWorker` — each already
  covered by `ClusterAdminRpcTests`).

**Next session** opens D-5e: `MasterConfigView` — new `ServerConfig`
cluster fields (`ClusterMaxJobs`, `ClusterArtifactRetentionMinutes`,
`ClusterTileTargetPixels`), `cluster.config.get` / `cluster.config.set`
admin-only RPCs, and a "Cluster Config" tab inside `ServerAdminView`.
After D-5e the §9 D-5 exit criteria are met (end-to-end render via UI
with no CLI, clean recovery from a worker dying mid-job) and D-6 opens
with crash recovery + per-role rate limiting.
