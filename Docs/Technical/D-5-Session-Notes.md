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
