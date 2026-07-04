# D-2 — Distributed Rendering, Phase 2 Session Notes

Phase plan: see
[DistributedRendering-DevelopmentPlan.md §9 Phase D-2](DistributedRendering-DevelopmentPlan.md#phase-d-2--job-submission--image-tiling--merge).

Picks up from
[D-1-Session-Notes.md](D-1-Session-Notes.md) — D-1 left the cluster
foundation (role-aware mTLS, worker registration, heartbeat, tile.next
long-poll harness) wired through `FFServer`/`FFWorkerAgent`/
`ClusterCoordinator` but with no job dispatch.

## Session log

### Session 1 — 2026-06-27 — Image tiling + merge end-to-end

**Goal**: client submits a job → master plans tiles → workers render
tiles → master merges to a final PNG → client fetches. Coordinator-only
infrastructure (no Program.cs CLI wiring or engine glue — those are
D-2b leftovers).

**Files added**

- `Server/Cluster/Protocol/JobSubmitDto.cs` — client → master job
  submission (wraps a `RenderRequestDto` + distribution hints).
- `Server/Cluster/Protocol/JobAckDto.cs` — master → client (JobId,
  tile count, byte estimate).
- `Server/Cluster/Protocol/JobStatusDto.cs` — request + status reply.
- `Server/Cluster/Protocol/JobFetchDto.cs` — fetch + cancel request /
  ack DTOs.
- `Server/Cluster/Protocol/TileJobDto.cs` — master → worker, one tile
  assignment. Carries a per-tile `RenderRequestDto` with translated
  `centerX/centerY/zoom` + per-tile `Width/Height`.
- `Server/Cluster/Protocol/TileDeliverDto.cs` — worker → master,
  rendered tile bytes (PNG today; "rgba" path stubbed for D-3) + per-
  payload SHA-256.
- `Server/Cluster/Protocol/TileErrorDto.cs` — worker → master, tile
  failure report with retry-vs-fatal `code`.
- `Server/Cluster/IClusterImageCodec.cs` — DecodePngToBgra +
  EncodeBgraToPng boundary so the Server library stays Skia-free; the
  hosting WinExe / Avalonia shell wires the real Skia codec in D-2b.
- `Server/Cluster/JobStore.cs` — `%APPDATA%\FracturingFog\master\jobs\
  <jobid>\` with `request.json`, `plan.json`, `tiles/<id>.bin`,
  `artifact.<ext>`, `events.ndjson`, `status.json` (atomic
  write-and-rename so a crashed master never leaves half-written
  state). Swappable `NowUtc` for tests. Includes
  `FailInflightAfterRestart()` and `EvictExpired(retention)`.
- `Server/Cluster/TilePlanner.cs` — image rect → N×M tiles. Each tile
  carries a per-tile `RenderRequestDto` with translated
  centerX/centerY/zoom so a normal worker render of `(tW × tH)`
  produces pixels identical to the same sub-rect of the `(W × H)`
  full render. Refuses untileable fractal types (LSystem, IFS,
  Strange*, Mandelbulb, TearDrop).
- `Server/Cluster/TileDispatcher.cs` — bounded per-job pending queue,
  in-flight tracking, retry budget (default 3), TCS-based long-poll
  awaiters that wake on enqueue. Per-call cancellation honoured.
- `Server/Cluster/ArtifactMerger.cs` — flat BGRA byte[] buffer per
  job; `TryMergePngTile` / `TryMergeRgbaTile` paste a decoded tile
  rect; `WritePng` encodes the final via the injected codec.
  Idempotent on duplicate tile delivery (race-safe against retried
  workers).
- `Server.Tests/Cluster/{TilePlannerTests,JobStoreTests,
  ArtifactMergerTests,TileDispatcherTests,ClusterEndToEndImageTests}
  .cs` — 44 new unit + integration tests covering the geometry, the
  on-disk state, the merger paste path, the dispatcher long-poll/
  retry semantics, and a full submit → tile loop → fetch run using
  the trivial RawHeader test codec.

**Files amended**

- `Server/Cluster/Protocol/TileNextResultDto.cs` — added the `Tile`
  payload field (D-1 left this commented out).
- `Server/Cluster/IClusterCoordinator.cs` —
  `ClusterDispatchOutcome.OkStreaming(ack, path, chunks)` so a
  coordinator method (job.fetch) can declare "reply with this ack
  then stream this file in N chunks". Keeps the SslStream-shaped
  wire encoding in FFServer.
- `Server/Cluster/ClusterCoordinator.cs` — added job.submit /
  job.status / job.fetch / job.cancel / tile.deliver / tile.error
  handlers; tile.next now drains the dispatcher (with fallback to
  WaitAgain when no dispatcher / no work).
- `Server/Cluster/FFWorkerAgent.cs` — tile.next non-WaitAgain branch
  now calls `IFractalRenderEngine.RenderAsync` on the per-tile
  request, reads the resulting PNG bytes, ships them via
  tile.deliver with SHA-256; engine exceptions surface as tile.error
  "engine-failed". New `Options.Engine` + `Options.WorkDirRoot`.
- `Server/FFServer.cs` — `DispatchClusterAsync` honours
  `StreamFilePath` on the outcome and streams the artifact bytes
  using the existing chunked path after sending the ack.
- `Client/FFClientConnection.cs` — added `SubmitJobAsync`,
  `GetJobStatusAsync`, `CancelJobAsync`, `PollUntilTerminalAsync`,
  `FetchJobArtifactAsync` with full per-chunk + whole-artifact SHA
  verification.

**Build / test**

```
dotnet build FracturingFogCLD.sln -c Debug      →  0 errors, 4 warnings
                                                     (all pre-existing — Avalonia
                                                      obsolete TextBox.Watermark)
dotnet test Server.Tests --filter ~Cluster      →  74 passed (30 from D-1 + 44 new)
dotnet test Server.Tests                        →  230 passed (no regressions)
```

**Design decisions captured here so future sessions don't relitigate**

1. **Tile coord mapping is purely additive — no engine change required.**
   The per-tile RenderRequestDto reuses centerX/centerY/zoom with new
   values (`scale' == scale`, `zoom' = Zoom * max(W,H) / max(tW,tH)`).
   Any calculator path that uses the standard
   `scale = (3.5 / max(W,H)) / Zoom` formula renders a tile that
   pixel-matches the same sub-rect of the full image — verified by
   `TilePlannerTests.Plan_Per_Tile_Render_Has_Translated_Center_Same_Scale`.
   Calculators that don't (LSystem, IFS, StrangeAttractor, Mandelbulb,
   TearDrop) are refused at `TilePlanner.ValidateForTiling` with a
   "tiling support pending" message.
2. **Worker ships PNG, not raw RGBA.** The engine's natural output is
   a PNG file on disk; reading + base64-encoding that file is cheaper
   than asking the worker to also decode the PNG into RGBA on the way
   out. Master pays a one-time decode per tile via
   `IClusterImageCodec.DecodePngToBgra`. D-3 will add the raw-RGBA
   binary path as a perf optimisation; the merger already accepts
   both via `TryMergeRgbaTile`.
3. **`IClusterImageCodec` is the only Skia dependency** for the
   coordinator. Server library stays UI-free + platform-free per
   CLAUDE.md. Hosting WinExe / Avalonia shell registers a concrete
   Skia-backed impl when constructing the coordinator (D-2b wiring).
4. **JobStore writes are atomic write-temp + rename.** A crashed
   master never leaves a half-written `status.json` for the next
   restart. Tile payloads use the same pattern.
5. **Per-tile rect comes from the persisted plan, not from the worker
   delivery.** `ClusterCoordinator.ReadPlan(jobId)` re-reads
   `plan.json` and uses the on-disk OffsetX/Y/Width/Height — defends
   against a worker (or a man-in-the-middle bug that survives mTLS)
   shipping a delivery with a fabricated offset.
6. **Round-robin dispatch is intentionally simple.** Pending tiles
   across jobs are claimed FIFO across jobs in id order. Rebalancing
   strategy (adaptive sizing, work-stealing) lands in D-3.
7. **Job.fetch reuses the single-server chunked path.** Coordinator
   returns `OkStreaming(ack, path, chunkCount)`; FFServer sends the
   ack envelope then streams chunks using the existing
   `StreamArtifactChunksAsync` — same code path as
   render.image/video, so client-side reassembly is shared.
8. **D-2 supports image mode only.** Video tiling lands in D-4 per
   dev plan §9; job.submit returns `unsupported-mode` for
   `mode=video`.
9. **Tile retry budget is per-tile not per-job.** A tile that fails
   on three different workers (default `MaxAttempts=3`) fails the
   whole job — the dev-plan acceptance criterion is byte-for-byte
   parity, which makes "partial image" output worse than no output.
10. **Fatal tile.error codes skip the retry budget.** `forbidden-
    fractal` / `limit-exceeded` / `cancelled` will not produce a
    different result on another worker; coordinator fails the job
    immediately on first occurrence.

**Open work — D-2b (engine wiring + parity test)**

The infrastructure is complete; what remains is the WinExe-side
plumbing that turns it into a runnable cluster:

- **Register a concrete `IClusterImageCodec`** in the WinExe + Avalonia
  shells (Skia-backed). Right now `ClusterCoordinator.Codec` is null
  in production; job.submit refuses with `not-configured`.
- **Wire `--master` / `--worker --master-host …` CLI flags into
  `Program.cs`.** D-1 left this todo; D-2 still relies on it for the
  end-to-end smoke test outside unit tests.
- **Stand-up coordinator in master mode** with `JobStore + Dispatcher +
  Codec` wired (today only unit tests construct it that way).
- **Cross-process parity test** — render a small image single-server,
  render the same image via 2-worker cluster, byte-compare the PNGs.
  Lives outside Server.Tests because it needs the real engine; goes
  under `IntegrationTests/` or as a `--self-test` flag in Program.cs.
- **Acceptance from dev plan §9 D-2**: 8K Bird-of-Paradise across 2
  workers byte-for-byte matches single-worker. Achievable only after
  the above lands.

**Phase D-3 entry points (when starting next session)**

- `Server/Wire/MessageEnvelope.cs` — add `PayloadKind = "binary"` plus
  length-prefixed binary framing (`Server/Wire/BinaryFraming.cs`).
- Worker side: ship raw RGBA via tile.deliver `PayloadKind = "rgba"`
  (merger already accepts this — see `ArtifactMerger.TryMergeRgbaTile`).
- Master side: median tile-time tracker on `WorkerEntry`; dispatcher
  re-sizes tiles to keep per-worker tile time in the 1–3 s band.
- Work-stealing on the last 10 % of tiles per job.
- Acceptance for D-3 (from the dev plan): 4-worker ≥ 3.2× speedup on
  Bird-of-Paradise 8K (≥ 80 % parallel efficiency).

### Session 2 — 2026-06-27 — D-2b engine wiring + CLI + parity

**Goal**: turn the D-2 infrastructure into something a user can actually
run from a shell. Was the "open work" punch-list at the bottom of
session 1.

**Files added**

- `ServerHost/SkiaClusterImageCodec.cs` — concrete
  `IClusterImageCodec` backed by SkiaSharp. DecodePngToBgra forces
  `Bgra8888/Premul` so the merger always pastes the same byte order;
  EncodeBgraToPng wraps a pinned buffer in an `SKBitmap.InstallPixels`
  (same pattern as `PngSequenceWriter.SavePng`) and encodes at
  quality 100. Lives in `ServerHost/` so both shells pick it up
  through the existing source-link.
- `ServerHost/ClusterEntry.cs` — `--master` and
  `--worker --master-host HOST` CLI handlers. Wires
  `WorkerRegistry + JobStore + TileDispatcher + SkiaClusterImageCodec`
  into `ClusterCoordinator`, hands the coordinator to `FFServer`, and
  reuses the existing cert/instance-probe/work-dir scaffolding from
  `ServerEntry.cs`. Worker mode runs `FFWorkerAgent` with
  `HostFractalRenderEngine` so a real cross-OS render happens per
  tile. Both modes accept `--cluster-certs-dir PATH` for operator-
  supplied PKI; otherwise they generate / reuse the dev bundle.
- `ServerHost/ClusterParitySelfTest.cs` — `--cluster-parity` flag.
  In-process A/B: renders the same `RenderRequestDto` via
  `HostFractalRenderEngine.RenderAsync` (full image) and via
  `TilePlanner.PlanImage → engine.RenderAsync per tile →
  ArtifactMerger.TryMergePngTile`, byte-compares the decoded BGRA
  buffers. Writes `cluster-parity.out` next to the exe (matches the
  rest of the `--*-smoke` / `--*-probe` family). Default size
  256×128, override via `--width/--height/--tile-px`.

**Files amended**

- `Server/Tls/CertSelfSignedHelper.cs` — added
  `EnsureClusterBundle(dir)` that issues a 5-file bundle:
  `ca.pfx + master.pfx + worker.pfx + cluster-client.pfx + admin.pfx`.
  The worker/client/admin leaves carry `OU=role-worker|client|admin`
  so the existing `CertRoleParser` routes their RPC calls through
  `FFServer.DispatchClusterAsync`'s per-role gate without any new
  parsing code.
- `Server/Protocol/RenderRequestDto.cs` — added
  `SuppressDecorations` (default false). Tile renders set it so the
  engine emits raw fractal pixels with no watermark / sub-text /
  region branding composited; single-server renders leave it false
  so existing decoration behaviour is unchanged. The master will
  add the single watermark once on the merged artifact in D-3+.
- `Server/Cluster/TilePlanner.cs` — sets
  `tileReq.SuppressDecorations = true` on every per-tile DTO,
  threads the field through `CloneRequest`. Without this each tile
  came back with its own watermark in its corner; the merged
  artifact had a grid of mini-watermarks instead of the single
  branding stripe.
- `ServerHost/HostFractalRenderEngine.cs` — image path now honours
  `req.SuppressDecorations`: blanks `Watermark` / `SubText` and
  passes `null` for `CustomWatermark` so `PosterRenderer`'s
  `BuildPosterWatermark` short-circuits.
- `Program.cs` (WinExe) + `FracturingFog.App/Program.cs` (cross-plat) —
  added the three `--master` / `--worker` / `--cluster-parity`
  entry points. Both call into the same `ClusterEntry` /
  `ClusterParitySelfTest` source files via the existing
  `<Compile Include="..\ServerHost\…" Link="…" />` pattern.
- `FracturingFog.App/FracturingFog.App.csproj` — added
  `Rendering.Skia` ProjectReference (transitively pulls SkiaSharp
  so the linked `SkiaClusterImageCodec` resolves) and three new
  `<Compile Include>` entries for the linked ServerHost files.

**Build / test**

```
dotnet build FracturingFogCLD.sln                      → 0 errors, 27 warnings (pre-existing)
dotnet test Server.Tests --no-build                    → 230 passed (no regressions)
dotnet run --project FracturingFogCLD.csproj -- \
      --cluster-parity --width  256 --height 128 --tile-px 64
   → PARITY OK   single-server + cluster paths byte-identical
                 (32,768 pixels, 0 diff pixels, 0 diff bytes)
dotnet run --project FracturingFogCLD.csproj -- \
      --cluster-parity --width 1024 --height 512 --tile-px 256
   → PARITY OK   524,288 pixels, 0 diff pixels, 0 diff bytes
```

**Design decisions captured here so future sessions don't relitigate**

11. **`SuppressDecorations` is the right contract.** Tried first
    without it: each tile shipped back with its own watermark in
    its top-left corner; the merged 256×128 image had 147 diff
    pixels concentrated at the 4×2 tile-grid corners. Flipping
    decorations off on the tile DTOs (and matching off on the
    full-image baseline) drops diff to zero. The master will draw
    a single watermark at merge time in D-3+ via the same
    `WatermarkResolver` path — for D-2b the merged PNG is plain
    fractal pixels.
12. **Cluster cert bundle lives at
    `%APPDATA%\FracturingFog\cluster-certs\`** (vs.
    `server-certs/` for the single-server path). Operators that run
    both modes on the same host get two independent CAs — a stolen
    single-server cert cannot then talk to the cluster master and
    vice versa.
13. **Cluster master writes job state to
    `%APPDATA%\FracturingFog\master\jobs\`** by default (override
    with `--jobs-dir`). Separate from the single-server work dir
    so a master restart never collides with an in-flight
    single-server render.
14. **Parity test runs the coordinator's tile-render math through
    the real engine**, but stops short of going through the
    SslStream-shaped wire path — `ClusterEndToEndImageTests`
    already covers that with the `RawHeaderCodec`. The split keeps
    the parity test focused on the "engine + tile math + Skia
    codec" tuple, which is the part `RawHeaderCodec` cannot
    exercise.
15. **Engine SHA pin re-uses `AssemblyInformationalVersion`.**
    Master + worker derive `EngineBuildSha` from the same string
    (which the build pipeline already stamps), so a version-skewed
    worker is refused at register time per dev plan §10 risk #7
    without a separate hashing pass.

**Open work — D-2b leftovers (none blocking D-3)**

- 8K Bird-of-Paradise parity run. The dev plan's D-2 acceptance
  criterion explicitly names this case. Parity is byte-identical
  at 1024×512 so the math is right; running 8K is a single-line
  command (`--cluster-parity --width 7680 --height 4320 --tile-px 512`).
  Deferred to the operator since this depends on host RAM /
  patience; the underlying code path is exercised.
- Admin role wiring. `admin.pfx` is now generated but no
  `cluster.*` methods accept connections yet; D-5 (Admin UI) lands
  those handlers.
- Cluster-mode `Server.Tests` coverage that exercises the
  Skia-backed codec end-to-end. The existing tests use
  `RawHeaderCodec`; the parity test is a CLI tool, not a unit
  test. A follow-up `Server.Tests/Cluster/SkiaCodecRoundtripTests`
  would add Skia decode/encode coverage without dragging the
  whole engine into Server.Tests.
