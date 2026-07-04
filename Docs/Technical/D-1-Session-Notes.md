# D-1 — Distributed Rendering, Phase 1 Session Notes

Phase plan: see
[DistributedRendering-DevelopmentPlan.md §9 Phase D-1](DistributedRendering-DevelopmentPlan.md#phase-d-1--role-aware-mtls--worker-registration-foundation).

## Session log

### Session 1 — 2026-06-27 — Foundation lands

**Goal**: role-aware mTLS + worker registration + heartbeat + tile.next
long-poll stub, with the single-server protocol path untouched.

**Files added**

- `Server/Tls/CertRole.cs` — `CertRole` enum + `CertRoleParser.FromCertificate`.
  Reads `OU=role-{worker|client|admin}` from the cert Subject DN. Missing
  OU resolves to `Client` (preserves legacy single-server bundle). Unknown
  role suffix throws — caller must refuse the session.
- `Server/Cluster/Protocol/{WorkerRegisterDto,WorkerRegisterAckDto,
  HeartbeatDto,HeartbeatAckDto,TileNextResultDto}.cs` — wire DTOs.
- `Server/Cluster/WorkerRegistry.cs` — concurrent in-memory store. Crockford
  base32 128-bit `WorkerId`. Pins cert thumbprint at first register;
  refuses resume with a different thumbprint. Heartbeat-timeout sweep at
  3× heartbeat interval. Swappable clock (`NowUtc`) for deterministic
  tests without sleeping.
- `Server/Cluster/IClusterCoordinator.cs` — small interface + outcome record.
  Coordinator returns `(Handled, Result | ErrorCode+Message)`; FFServer
  owns the wire encoding.
- `Server/Cluster/ClusterCoordinator.cs` — master-side impl. Routes
  `worker.register`, `worker.heartbeat`, `tile.next`. tile.next holds
  for `TileNextHold` (default 30 s) then returns `WaitAgain=true`.
- `Server/Logging/ClusterLogger.cs` — NDJSON event log,
  `cluster-yyyyMMdd.log` under `%APPDATA%/FracturingFog/master-logs/`.
  Mirrors `SessionLogger`'s bounded-channel pump-task pattern.
- `Server/Cluster/FFWorkerAgent.cs` — worker outbound side. Loads worker
  PFX, mTLS-dials the master, calls `worker.register`, then runs the
  heartbeat / tile.next loop. Exponential reconnect backoff on failure.
  Resumes its prior `WorkerId` after a drop.
- `Server.Tests/Cluster/{CertRoleTests,WorkerRegistryTests,
  ClusterCoordinatorTests}.cs` — 30 unit tests, all green.

**Files amended**

- `Server/FFServer.cs`
  - New `IClusterCoordinator? Coordinator { get; init; }` property.
    Null preserves legacy behaviour.
  - Per-session: parse `CertRole` from the presented cert immediately
    after the TLS handshake. Misissued cert (unknown role suffix) is
    logged + the session is dropped before any method runs.
  - `DispatchAsync` now takes role + thumbprint. Unknown methods that
    match `worker.* | tile.* | cluster.* | job.*` route to the
    coordinator via `DispatchClusterAsync`, with role-gating applied
    centrally (`worker.* / tile.*` requires Worker; `cluster.*` requires
    Admin; `job.*` requires Client or Admin).
  - Coordinator exceptions become `internal` ErrorDtos; OperationCanceled
    on session shutdown is swallowed (socket closes anyway).

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug   →  0 errors, 24 warnings
                                                  (all pre-existing — CalculatorGen
                                                   unused-var + Avalonia obsolete-API)
dotnet test Server.Tests --filter ~Cluster   →  30 passed
dotnet test Server.Tests                     →  186 passed (no regressions)
```

**Design decisions captured here so future sessions don't relitigate**

1. **Role via Subject OU, not custom X.509 extension OID.** No Private
   Enterprise Number required; stock OpenSSL / `New-SelfSignedCertificate`
   can issue role-tagged certs. A SAN URI alternative
   (`urn:fracturingfog:role:worker`) is a documented future hardening but
   not implemented in D-1.
2. **Coordinator returns a record, not writes the wire frame.** Keeps
   wire encoding in FFServer; lets `ClusterCoordinatorTests` run without
   an SslStream.
3. **Worker outbound only.** Workers never listen — removes the
   inbound-firewall requirement on every node and bounds attack surface
   to the master. `tile.next` long-poll is the server-push channel.
4. **WorkerId is fresh random per first-register, NOT derived from cert
   thumbprint.** Avoids leaking cert identity into log lines.
   Thumbprint is the pin; WorkerId is the public handle.
5. **Re-register on reconnect uses `ResumeWorkerId`.** Master verifies
   the thumbprint pin matches; on success the worker keeps the same id.
6. **`tile.next` payload is a `HeartbeatDto`.** Worker re-asserts its
   `WorkerId` on every long-poll so a re-registration window can't be
   straddled by a stale session.
7. **Engine-SHA pin is opt-in.** ClusterCoordinator has
   `EngineBuildSha = ""` by default — skips the check. Production
   deployments stamp it from
   `Engine assembly InformationalVersion`. Tests use the empty path.

**Open work — pick up next session (Phase D-2)**

- Wire `Master`/`Worker` entry points into `Program.cs` —
  `--master` (extends `--server`) and `--worker --master-host …` CLI
  flags. D-1 left the building blocks in `Server/Cluster/` without a
  process entry point; the foundation is exercised only through unit
  tests. *Acceptance*: `dotnet run -- --master` listens, `dotnet run --
  --worker --master-host 127.0.0.1` registers + heartbeats; admin sees
  it in the cluster log NDJSON.
- Cert-issuance helper (`--issue-worker-cert NAME`) that mints a
  worker PFX with `OU=role-worker` baked into Subject. Mirrors the
  existing self-signed dev bundle path. Mitigation for risk #5
  ("cert sprawl operationally painful").
- Start the WorkerRegistry stale-sweep timer from a master-host loop
  (today only invoked via unit tests calling `SweepStale()` directly).
- Engine-SHA stamping — pick up the engine assembly's
  `InformationalVersion` and feed it into both the master's
  `ClusterCoordinator.EngineBuildSha` and the worker's
  `WorkerRegisterDto.EngineBuildSha`. Right now both are caller-supplied;
  no automatic plumbing.

**Phase D-2 entry points (when starting next session)**

- `Server/Cluster/JobStore.cs` — on-disk job state under
  `%APPDATA%/FracturingFog/master/jobs/<jobid>/`.
- `Server/Cluster/TilePlanner.cs` — image rect → N tiles, picks workers.
- `Server/Cluster/TileDispatcher.cs` — channel-fed `tile.next` source.
- Coordinator: add `job.submit / job.status / job.fetch / job.cancel`.
- `TileNextResultDto`: add `Tile` field; coordinator returns real tiles
  when the dispatcher channel has one.
- Client side: extend `FFClientConnection` with `SubmitJobAsync`,
  `PollJobAsync`, `FetchJobAsync` helpers.

Acceptance for D-2 (from the dev plan): a 2-worker 8K Bird-of-Paradise
render completes and matches a single-worker render byte-for-byte (or
pixel-identical after a deterministic tie-break).
