# Distributed Rendering — Development Plan

Multi-session implementation plan for a master/worker rendering cluster on top
of the existing `Server/` + `Client/` stack. Goal: cut wall-clock time for
expensive Mandelbrot renders (Bird of Paradise, deep zooms, 8K posters,
multi-minute videos) by sharding work across LAN nodes while keeping full
fidelity and lossless output.

Anchor commit at doc creation: `c759214` on `feature/cross-platform-full`.

---

## 1. Scope & non-goals

**In scope**
- One Master Server (orchestrator + ffmpeg merge node).
- N Worker Servers (rendering nodes, formerly "slaves" — use "worker" in code
  and UI per modern convention).
- mTLS on every link (worker→master, client→master, admin→master). LAN only;
  no WAN-facing endpoints.
- Render-job lifecycle: submit → ack + RenderId → poll → fetch.
- Tiled sharding of image, poster, slideshow-frame, and video-frame jobs.
- Reuse the existing `RenderRequestDto` / `RenderResponseDto` / `ChunkDto`
  wire format wherever possible; add `Tile*` and `Job*` envelopes alongside.
- Lossless artifact transport (existing chunk-stream + SHA-256 path).
- Admin UI in `UI.Avalonia/` for cluster status, worker control, job control.

**Out of scope (this iteration)**
- WAN / Internet operation. No NAT traversal, no STUN/TURN, no relay.
- Heterogeneous-OS GPU coordination beyond what `Rendering.Skia` /
  `Rendering.Silk` already give us. Workers expose what they have; the master
  does not try to virtualise across vendors.
- WinForms UI changes (CLAUDE.md: WinForms is deprecated, Avalonia-only).
- Re-platforming the existing single-server protocol. The single-server path
  stays operational; distributed mode is additive.

---

## 2. Architecture

```
                       ┌──────────────────────────┐
                       │  Avalonia Admin UI        │
                       │  (master-attached)        │
                       └──────────┬───────────────┘
                                  │ mTLS JSON-RPC
                                  ▼
┌────────────┐   mTLS    ┌──────────────────────────┐    mTLS    ┌────────────┐
│  Client    │──submit──▶│  Master Server            │◀──register─│  Worker N  │
│  (FFClient)│◀──poll────│  - job queue              │            │  (FFServer │
│            │           │  - tile planner           │            │   extended)│
│            │           │  - merge / ffmpeg         │──tile job─▶│            │
└────────────┘           │  - artifact cache         │◀──chunks───│            │
                         └──────────────────────────┘            └────────────┘
                                  ▲
                                  │ mTLS register (push/pull)
                                  │
                            (more workers…)
```

- **Master** wraps a new `FFMaster` class that hosts both
  (a) the client-facing protocol (extends today's `FFServer`) and
  (b) the worker-facing registration + dispatch protocol.
- **Worker** is `FFServer` from today plus a small client component that
  dials the master and registers itself (`FFWorkerAgent`).
- **Client** stays mostly the same. The only change is that the
  `RenderResponseDto` may carry `JobId` + `Status` instead of bytes; the
  client polls `job.status` and finally `job.fetch`.

### Process model

- Master: long-lived, single instance, binds two TLS listeners (or one
  listener that demuxes by client cert role). Recommend two listeners on
  separate ports — simpler, lets cert role be the listener identity.
- Worker: long-lived. Outbound TCP to master keeps a session open;
  bi-directional JSON-RPC over that one socket. Worker NEVER listens — this
  removes the inbound-firewall requirement on every node and naturally
  scopes attack surface to "the master".
- Client: short-lived per UI session (today's behaviour).

### Roles & certs

Three client-cert roles, distinguished by an OID extension or by separate
issuing CAs:
- `role=worker` — may call `worker.register`, `worker.heartbeat`,
  `tile.deliver`. Cannot call `job.submit`.
- `role=client` — may call `job.submit`, `job.status`, `job.fetch`,
  `job.cancel`. Cannot call worker RPCs.
- `role=admin` — superset of client, plus `cluster.*`, `worker.kill`,
  `worker.quiesce`, `master.config.*`.

Role parsing reuses `ServerCertLoader.BuildClientValidator` — add a
post-validation hook that reads the OID and routes the dispatcher.

---

## 3. Wire protocol additions

Reuse `Server/Wire/JsonRpcFraming.cs` + `MessageEnvelope`. New methods:

### Worker → Master

| Method | Purpose |
|---|---|
| `worker.register` | Announce capabilities, hardware, supported fractal types, max tile size, GPU/CPU mix. Returns a `WorkerId` (UUID, persists across reconnects via cert thumbprint). |
| `worker.heartbeat` | 5 s cadence. Carries current load: tiles in flight, CPU/GPU %, temp, free RAM. Master times out a worker at 3× missed beats. |
| `tile.deliver` | Streams `ChunkDto`s back keyed by `(JobId, TileId)`. Reuses existing chunk path + SHA-256. |
| `tile.error` | Reports a tile that crashed, ran past deadline, or hit a guard rejection. Master retries on a different worker. |

### Master → Worker (initiated server-side over the same socket)

JSON-RPC has no built-in server push, so a worker session opens with a
half-duplex "pull-loop": after `worker.register`, the worker issues
`tile.next` and blocks (with a server-side long-poll, e.g. 30 s) until the
master returns a tile job or a timeout. Standard pattern; avoids inventing
a second framing.

| Method (worker calls) | Purpose |
|---|---|
| `tile.next` | Long-poll for the next assigned tile. Returns `TileJobDto` or a `wait-again` signal. |
| `tile.ack` | Worker confirms it accepted the tile; arms the master's per-tile deadline. |

### Client → Master

| Method | Purpose |
|---|---|
| `job.submit` | Same payload as today's `RenderRequestDto`, plus optional `priority`, `tilePreferenceHint`. Returns `JobId` immediately. |
| `job.status` | Poll. Returns `{state, progress, tilesTotal, tilesDone, tilesInFlight, etaSeconds, errors[], artifactReady}`. |
| `job.fetch` | Streams the merged artifact (existing chunk path). Server keeps the artifact for `ArtifactRetentionMinutes` (default 60). |
| `job.cancel` | Aborts pending tiles, frees workers. |

### Admin → Master

| Method | Purpose |
|---|---|
| `cluster.status` | All workers, all in-flight jobs, queue depth. |
| `worker.quiesce` / `worker.resume` | Drain a worker for maintenance. |
| `worker.kill` | Force-disconnect a misbehaving worker. |
| `master.config.get` / `master.config.set` | Live config edits, persisted. |
| `job.list` / `job.cancel` (admin) | Any job, any user. |

### New DTOs (sketch — to live in `Server/Protocol/`)

```csharp
public sealed class JobAckDto {
    public string JobId { get; set; } = "";
    public int    TileCount { get; set; }
    public long   EstimatedBytes { get; set; }
}

public sealed class JobStatusDto {
    public string JobState { get; set; } = "";  // queued|planning|rendering|merging|ready|failed|cancelled
    public int    TilesTotal { get; set; }
    public int    TilesDone { get; set; }
    public int    TilesInFlight { get; set; }
    public double ProgressPercent { get; set; }
    public long   ElapsedMs { get; set; }
    public long?  EtaMs { get; set; }
    public bool   ArtifactReady { get; set; }
    public string? FailReason { get; set; }
}

public sealed class TileJobDto {
    public string JobId { get; set; } = "";
    public int    TileId { get; set; }
    public int    OffsetX { get; set; }
    public int    OffsetY { get; set; }
    public int    Width { get; set; }
    public int    Height { get; set; }
    public RenderRequestDto Render { get; set; } = new();  // tile-scoped render request
    public int    DeadlineSeconds { get; set; }
    public int    Attempt { get; set; }
}

public sealed class WorkerRegisterDto {
    public string WorkerName { get; set; } = "";
    public string OsPlatform { get; set; } = "";
    public string CpuModel { get; set; } = "";
    public int    LogicalCores { get; set; }
    public long   TotalRamBytes { get; set; }
    public List<string> Gpus { get; set; } = new();
    public List<string> SupportedFractalTypes { get; set; } = new();
    public int    MaxConcurrentTiles { get; set; }
    public int    PreferredTilePixels { get; set; }
}
```

---

## 4. Sharding strategy per output type

### Image / poster
- Partition the output rect into N×M tiles. Default tile target ≈ 512×512 px;
  the master adapts per worker's `PreferredTilePixels`.
- Each tile is a full `RenderRequestDto` with a sub-rect — the existing
  calculators already accept centre+zoom; the master translates tile
  offsets into `(centerX, centerY, zoom)` deltas using
  `Engine/Calculators/MandelbrotCalculator` coord math.
- **Caveat**: perturbation-based deep zoom (`SeriesApproximation`) shares a
  reference orbit across the whole image. Tiles must either
  (a) recompute the ref orbit each (cheap once, wasted N×) or
  (b) the master computes the ref orbit once and ships it inside
  `TileJobDto` as a binary blob. Option (b) is correct long-term; option (a)
  is fine for v1.
- Merge: master holds a `MemoryMappedFile` for the final RGBA buffer, each
  tile writes into its rect, atomic on completion. PNG encode runs once at
  the end via `PngSequenceWriter`/Skia.

### Slideshow
- Each slide is an independent render job. Trivial map/reduce — one tile
  per slide is fine for v1; subdivide per-slide later if a single slide is
  the long pole.

### Video / video-zoom
- **Frame-level sharding is the unit**. Per-pixel tile sharding inside a
  frame is an optional v2 escalation (only useful for 8K+ frames).
- Master plans the zoom keyframe schedule via the existing
  `IVideoZoomController` and assigns frame ranges (e.g. frames 0..29 to
  worker A, 30..59 to worker B). Workers return per-frame PNGs (existing
  `PngSequenceWriter` output).
- Master ingests frames into `Engine/Imaging/FfmpegEncoder.cs` /
  `FfmpegVideoWriter.cs` — must be sequential, so the master gates encoder
  input on frame N being present before consuming frame N+1.
- Lossless presets ("ffv1", "h264hq", "h264") already exist in
  `BatchOptions`. No change.

### Audio-reactive slideshow
- Audio analysis stays on the master (the audio source is a single file).
  Frame plan is computed master-side, then sharded as per video.

---

## 5. Job lifecycle (master state machine)

```
   submit
     │
     ▼
   queued ──admin cancel──▶ cancelled
     │
     ▼
   planning            ← tile planner runs; computes N, picks workers
     │
     ▼
   rendering ─tile fail (retry budget exhausted)─▶ failed
     │
     ▼
   merging              ← all tiles delivered; ffmpeg / png compositor runs
     │
     ▼
   ready                ← artifact on disk; client may fetch
     │
   (TTL expiry)
     ▼
   evicted
```

Persistence: `%APPDATA%\FracturingFog\master\jobs\<jobid>\` holds
- `request.json` (original `RenderRequestDto`)
- `plan.json` (tile list + assignment history)
- `tiles/<tileid>.bin` (tile payload from worker, raw RGBA or PNG)
- `artifact.<ext>` (final merged output)
- `events.ndjson` (state transitions, errors, worker swaps — for the
  troubleshooting requirement)

Crash recovery v1: on master restart, jobs in `rendering` or `merging` are
marked `failed` with reason "master-restart". v2 can resume.

---

## 6. Security review (LAN-scoped)

The user explicitly asked for security suggestions. Recommendations:

1. **mTLS everywhere** — already in place. Keep `RequireTls13 = true` in the
   master config for the cluster CA; client UI cert may stay TLS 1.2+1.3 for
   compat. Default config ships TLS 1.3.
2. **Cert role separation** — distinct CA per role (workerCA, clientCA,
   adminCA). Reuses `ServerCertLoader` infrastructure; just three trust
   stores instead of one. Compromise of a worker cert cannot submit jobs.
3. **Worker cert pinning** — `ServerConfig.AllowedClientThumbprints` already
   exists. The master persists a per-worker thumbprint after first
   registration; subsequent reconnects must match.
4. **No remote code paths** — the existing `FractalTypeAllowlist` already
   refuses `UserEquation`, `UserBulb`, `Sandbox`. Keep this guard on the
   master *and* re-check on the worker. Defense in depth.
5. **Payload validators** — `RegionPayloadValidator`, `ThemePayloadValidator`,
   `WatermarkPayloadValidator` are already enforced. Make the master apply
   them once at `job.submit`, then trust the sharded `TileJobDto` (the
   master is authenticated to the worker).
6. **Rate limiting** — extend `EndpointRateLimiter` to a per-role policy.
   Client-role: existing per-IP per-minute. Worker-role: lower bound on
   `tile.next` long-poll churn. Admin-role: unlimited but log every call.
7. **Bind interface** — default master `BindAddress = 127.0.0.1` per
   existing convention. Operator must explicitly set the LAN address.
   Refuse `0.0.0.0` unless an explicit `--allow-any-interface` CLI flag is
   passed; print a stern warning on startup.
8. **mTLS revocation** — `RevocationCheckMode = "offline"` once a real CRL
   pipeline exists; "none" stays the default for the self-signed bundle.
9. **Artifact eviction** — `JobArtifactRetentionMinutes` config field.
   Cleanup loop runs at startup + every minute. Mirrors
   `WorkDirSweeper.cs`.
10. **Audit log** — `Logging/SessionLogger.cs` already writes per-session
    NDJSON. Add a parallel cluster log on the master that records
    `(jobId, workerId, tileId, event, ts)` events. Asks satisfied: "Logging
    should provide detailed render request information for troubleshooting".
11. **Sandboxed worker work-dirs** — `Server/WorkDirSweeper.cs` already
    isolates per-job. Keep `OutputName` derivation server-side; never trust
    a client `outputName` containing path separators (existing guard).
12. **DoS surface** — the existing connection gate + queue gate apply
    per-listener. The master needs a separate `MaxJobs` cap and a
    `MaxBytesQueued` cap so a flood of poster requests cannot OOM the
    merge buffer.

---

## 7. Performance suggestions

1. **Avoid base64 on the hot path**. Today's `ChunkDto.BytesBase64` adds 33 %
   overhead and CPU on both ends. For the worker→master tile transport,
   add a binary framing option: `MessageEnvelope.PayloadKind = "binary"`,
   length-prefixed raw bytes after the JSON header. JSON-RPC stays for
   control messages; the bulk path is binary. This is the single biggest
   win for large posters and video frames.
2. **Tile output format**. Workers emit raw RGBA (4 bytes/px, no encode
   cost) for image tiles; the master encodes once at merge time. PNG
   encode of a 16K poster on a worker is wasted work the master must redo.
3. **Compression**. Optional LZ4 on tile payloads — fast, lossless, often
   2–4× on smooth gradient regions of fractals. Worth a feature flag.
4. **Memory-mapped merge buffer**. Already mentioned; keeps master RAM
   bounded to one full output buffer per active job regardless of tile
   count.
5. **Adaptive tile sizing**. Track per-worker median tile time; rebalance
   tile size so each worker finishes in ~1–3 s. Avoids stragglers.
6. **Work-stealing**. If a worker's last tile is still going when the queue
   empties, allow other workers to "steal" half its remaining rows. Cheap
   re-submit on the master side.
7. **Reference-orbit caching for perturbation**. Compute once on the
   master, distribute via `TileJobDto.ReferenceOrbitBlob`. Saves the
   N-tile redundancy mentioned in §4.
8. **TCP tuning**. `NoDelay` + `KeepAlive` already on. Add `SO_RCVBUF` /
   `SO_SNDBUF` raises to 1 MB on the worker↔master sockets for sustained
   throughput.
9. **Video frame pipeline backpressure**. Workers must not race ahead of
   the master's ffmpeg consumption. Master assigns frame ranges, and
   throttles by holding `tile.next` long-polls when its frame queue depth
   exceeds a threshold (e.g. 64 frames buffered).
10. **Avoid double-decode of region JSON**. The master parses
    `regionJson` / `themeJson` once; pass a parsed binary form in
    `TileJobDto` to skip per-tile JSON re-validation on the worker.

---

## 8. Admin UI plan (`UI.Avalonia/`)

New view tree under `UI.Avalonia/Views/Cluster/`:
- `ClusterDashboardView` — workers grid (name, OS, GPU, RAM, current load,
  tiles in flight, last heartbeat, uptime). Live updates over the existing
  admin connection. Red/yellow status — colourblind: use **yellow
  (`#FFCC00`) not red** for problem states per the project's standing
  accessibility note.
- `JobListView` — active + recent jobs. Per-row: submitter, type, progress
  bar, ETA, cancel button.
- `JobDetailView` — tile map (visual grid showing per-tile status:
  pending/inflight/done/failed, coloured by worker), live events log,
  per-worker contribution bar.
- `WorkerDetailView` — capabilities, history, button row: quiesce, resume,
  kill, force-reload-config.
- `MasterConfigView` — extends the existing `ServerAdminView`/
  `ServerAdminViewModel` to add cluster-only fields
  (`MaxJobs`, `ArtifactRetentionMinutes`, `TileTargetPixels`,
  per-role rate limits).

All views are MVVM (the existing pattern); the view-model talks to
`FFAdminConnection` (a thin wrapper over `FFClientConnection` that uses
the admin cert).

---

## 9. Phased delivery — priorities & sequencing

Each phase is a separate session-sized milestone. Earlier phases unblock
later ones; do not parallelise across phases without explicit need.

### Phase D-1 — Role-aware mTLS + worker registration (foundation)
- New cert roles, OID parsing, dispatcher routing.
- `FFMaster` skeleton: extends `FFServer`, adds worker listener and an
  in-memory `WorkerRegistry`.
- `FFWorkerAgent` in `Server/Cluster/` (worker side): outbound connect,
  register, heartbeat loop, `tile.next` long-poll harness (no real work
  yet — returns "wait" forever).
- Headless smoke: one master + two workers register, heartbeat, gracefully
  disconnect.
- **Exit criteria**: `Server.Tests` covers register + heartbeat + cert role
  refusal.

### Phase D-2 — Job submission + image tiling + merge
- New `JobId` generator (cryptographically random 128-bit, base32 string).
- `job.submit`, `job.status`, `job.fetch`, `job.cancel` on the master.
- Tile planner for image renders; `TileJobDto` dispatch over `tile.next`.
- Memory-mapped merge buffer; PNG encode at completion.
- Client-side: extend `FFClientConnection` with the new methods; add a
  polling helper.
- **Exit criteria**: a `--batch --remote --cluster` render of a non-trivial
  image (e.g. 8K Bird of Paradise at default iter budget) completes
  across 2 workers and matches a single-worker render byte-for-byte (or
  pixel-identical after a deterministic tie-break).

### Phase D-3 — Binary tile transport + perf
- `PayloadKind = "binary"` framing.
- Raw RGBA worker tile output.
- Optional LZ4.
- Adaptive tile sizing, work-stealing on the last 10 % of tiles.
- **Exit criteria**: 4-worker scale test shows ≥ 3.2× speedup over
  single-worker on a Bird-of-Paradise 8K render (≥ 80 % parallel
  efficiency).

### Phase D-4 — Video + slideshow distribution
- Frame-range planner.
- Master-side ffmpeg sequential ingest with backpressure.
- Slideshow per-slide sharding.
- **Exit criteria**: 20-second 1080p30 zoom render at `lossless=ffv1`
  completes across 2 workers, output passes `ffprobe` parity vs. a
  single-worker render.

### Phase D-5 — Admin UI
- All views from §8 live and wired to the master.
- One-click quiesce + resume of a worker. Force-cancel of a stuck job.
- Live log tail panel.
- **Exit criteria**: the UI can drive an end-to-end render with no CLI
  involvement, and can cleanly recover from a worker dying mid-job (kill
  the worker process during a render; UI shows the tile retry; final
  artifact is correct).

### Phase D-6 — Hardening & polish
- Crash recovery (resume `rendering` jobs after master restart).
- Reference-orbit caching for perturbation.
- Per-role rate limiting.
- Operational doc (`Docs/User/Distributed-UserGuide.md`).
- Stress tests in `Server.Tests`: 50 concurrent client connections, 8
  workers, 200 queued jobs.

---

## 10. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | Perturbation/reference-orbit divergence across tiles produces visible seams | Med | High (fidelity is a stated goal) | Master computes single ref orbit, ships in `TileJobDto`; pixel-parity test in CI. Fall back to per-tile orbit only behind a flag. |
| 2 | Base64 tile transport saturates LAN before CPU saturates | High | Med | Phase D-3 binary framing. Do not ship D-2 to users without D-3. |
| 3 | Video frame backpressure deadlocks (master full, workers idle, ffmpeg slow) | Med | High | Bounded master buffer + worker long-poll throttle. Explicit deadlock test in D-4. |
| 4 | Mixed-OS workers (Win + Linux) produce subtly different pixels | Med | High | Pin to one calculator path; `MandelbrotCalculator` is portable C# with no JIT-FP-mode differences in our experience, but add a deterministic-output test that hashes output across OSes in CI. |
| 5 | mTLS cert sprawl operationally painful (3 CAs + per-worker pins) | High | Med | Ship a `--issue-worker-cert` CLI on the master; admin UI button "Approve pending worker" that signs + persists the thumbprint in one click. |
| 6 | Master single point of failure | High | Med | Documented as an explicit non-goal of v1 (no HA). Master state on disk + fast restart is the answer. |
| 7 | Worker quietly returns wrong-coord tiles after a code-version skew | Low | Critical | Include a `ProtocolVersion` + `EngineBuildSha` in `worker.register`; master refuses workers whose engine SHA does not match its own. |
| 8 | A misbehaving client floods `job.submit` and exhausts master disk | Med | Med | `MaxJobsPerClientCert` quota + disk-watermark check before accepting. |
| 9 | UE editor / user-authored fractals slip through to workers | Low | High | `FractalTypeAllowlist` already blocks at the protocol layer; enforce identically on both master and worker (defense in depth). Already partially in place. |
| 10 | Long-poll `tile.next` exhausts master thread pool | Med | Med | Use async + a single shared dispatcher channel; never a thread per worker. |
| 11 | Tile retries cause non-determinism (one worker's float results vs. another's) | Low | Med | Pin `<RuntimeHostConfigurationOption Include="System.Runtime.Numerics.FloatToStringRoundtrip" Value="true" />` (already default in net10); plus the deterministic-output test from risk #4. |
| 12 | The admin role being too powerful is itself a risk | Med | Med | Separate admin cert, never auto-issued. Admin UI requires explicit cert path at startup — no "remember me". |

---

## 11. File-level work breakdown (initial sketch)

This is the layout we'll land in over phases D-1 through D-6. Paths
relative to repo root. Existing files are amended; new files are marked.

```
Server/
  FFServer.cs                       (amend: role-aware dispatch hook)
  Cluster/
    FFMaster.cs                     (new) — orchestrator
    FFWorkerAgent.cs                (new) — worker outbound + long-poll
    WorkerRegistry.cs               (new) — in-memory + thumbprint pins
    JobStore.cs                     (new) — on-disk job state
    TilePlanner.cs                  (new) — image/video sharding
    TileDispatcher.cs               (new) — channel-based worker fanout
    ArtifactMerger.cs               (new) — RGBA mmap merge + PNG encode
    VideoFramePipeline.cs           (new) — sequential ffmpeg ingest
    Protocol/
      JobAckDto.cs JobStatusDto.cs TileJobDto.cs
      WorkerRegisterDto.cs HeartbeatDto.cs ClusterStatusDto.cs   (all new)
  Wire/
    MessageEnvelope.cs              (amend: PayloadKind=binary)
    BinaryFraming.cs                (new) — length-prefixed binary frames
  Tls/
    ServerCertLoader.cs             (amend: role OID extraction)
  Logging/
    ClusterLogger.cs                (new) — NDJSON cluster events

Client/
  FFClientConnection.cs             (amend: SubmitJob/PollJob/FetchJob)
  ClusterClient.cs                  (new) — high-level polling helper

UI.Avalonia/
  ViewModels/Cluster/
    ClusterDashboardViewModel.cs
    JobListViewModel.cs
    JobDetailViewModel.cs
    WorkerDetailViewModel.cs        (all new)
  Views/Cluster/
    ClusterDashboardView.axaml(+.cs)
    JobListView.axaml(+.cs)
    JobDetailView.axaml(+.cs)
    WorkerDetailView.axaml(+.cs)    (all new)
  ViewModels/ServerAdminViewModel.cs (amend: cluster config fields)
  Views/ServerAdminView.axaml        (amend: cluster config tab)

Server.Tests/
  Cluster/
    WorkerRegistrationTests.cs
    TilePlannerTests.cs
    ArtifactMergerTests.cs
    EndToEndImageTests.cs
    EndToEndVideoTests.cs
    DeterministicOutputAcrossOsTests.cs   (all new)

Docs/
  Technical/DistributedRendering-DevelopmentPlan.md   (this file)
  User/Distributed-UserGuide.md                       (Phase D-6)
```

---

## 12. Session bookkeeping

Per the project's "S-X*" session-notes convention
(`Docs/Technical/S-X10-Session-Notes.md` etc.), distributed-rendering work
takes a `D-N[suffix]` tag in commit subjects:

- `D-1` = Phase D-1, etc.
- `D-1a`, `D-1b`, … for follow-ups within the same phase.
- Session notes per phase live as
  `Docs/Technical/D-N-Session-Notes.md` (one file per phase, appended to
  across sessions).

Commit subject pattern (matches the repo's style — see `git log`):
```
feat: D-1 — worker registration + role-aware mTLS dispatch
fix:  D-2b — tile merge mmap leak under cancellation
docs: D-3 — note binary framing on-wire format
```

The first commit of each session should reference the previous
phase-notes file and the open question it picks up; the last commit
should append a short "next session" block to the phase notes. Same
pattern as `S-X8-S-X9-Session-Notes.md`.
