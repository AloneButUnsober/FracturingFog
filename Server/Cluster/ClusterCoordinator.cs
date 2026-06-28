// Server/Cluster/ClusterCoordinator.cs
// Master-side implementation of IClusterCoordinator. Owns the
// WorkerRegistry, ClusterLogger, JobStore, TileDispatcher and the per-
// job ArtifactMerger map; routes individual methods to small per-method
// handlers.
//
// Phase D-1: worker.register, worker.heartbeat, tile.next (always
// returns wait-again).
// Phase D-2 adds: job.submit, job.status, job.fetch, job.cancel,
// tile.deliver, tile.error — plus real tile dispatch through tile.next.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;

namespace FracturingFog.Server.Cluster;

public sealed class ClusterCoordinator : IClusterCoordinator
{
    /// <summary>Engine build SHA the master itself was compiled against.
    /// Workers must match exactly; mismatch is refused at register time
    /// per risk #7 in the dev plan. Caller sets this from its own engine
    /// assembly InformationalVersion.</summary>
    public string EngineBuildSha { get; init; } = "";

    /// <summary>Wire protocol versions the master accepts. Currently just
    /// "1"; bump when introducing a breaking envelope/method change.</summary>
    public IReadOnlyCollection<string> AcceptedProtocolVersions { get; init; } = new[] { "1" };

    /// <summary>How long tile.next holds the long-poll before returning
    /// wait-again. Lower values = more polling churn; higher values = a
    /// laggier reaction to admin worker.kill / shutdown. 30 s matches
    /// the value advertised in WorkerRegisterAckDto.</summary>
    public TimeSpan TileNextHold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Chunk size used by job.fetch streaming. Mirrors the
    /// single-server render chunk size so client-side reassembly logic
    /// can be shared.</summary>
    public int FetchChunkBytes { get; init; } = 1 * 1024 * 1024;

    /// <summary>D-4b — backpressure gate. tile.next holds a video tile
    /// when the per-job streaming ffmpeg encoder has fallen this many
    /// frames behind the wire delivery. Default 64 matches the dev-plan
    /// §7.9 recommendation. Higher = more frames buffered on disk
    /// (faster workers, more memory pressure); lower = stricter pacing
    /// (tighter wall-clock between deliver and encode).</summary>
    public int MaxFrameQueueDepth { get; init; } = 64;

    /// <summary>D-5e — cap on concurrent non-terminal jobs. 0 = unlimited.
    /// job.submit beyond this returns "queue-full". Live-mutable through
    /// cluster.config.set so the admin UI can dial it without a master
    /// restart.</summary>
    public int ClusterMaxJobs { get; set; } = 0;

    /// <summary>D-5e — retention window for terminal jobs. Drives the
    /// <see cref="JobStore.EvictExpired"/> sweep that ClusterEntry runs on
    /// a minute timer. 0 = never evict. Live-mutable.</summary>
    public int ClusterArtifactRetentionMinutes { get; set; } = 60;

    /// <summary>D-5e — default per-tile pixel side handed to
    /// <see cref="TilePlanner.PlanImage"/> when the client supplies no
    /// hint and the registry has no learned EMA / worker hints. 0 = use
    /// <see cref="TilePlanner.DefaultTilePixels"/>. Live-mutable.</summary>
    public int ClusterTileTargetPixels { get; set; } = 0;

    /// <summary>D-5e — invoked after a successful cluster.config.set so the
    /// host (ClusterEntry) can persist the new values to server-config.json.
    /// Null = in-memory only; admin UI changes vanish on master restart.</summary>
    public Action<ClusterConfigDto>? PersistConfig { get; init; }

    public WorkerRegistry  Registry   { get; }
    public JobStore?       Jobs       { get; init; }
    public TileDispatcher? Dispatcher { get; init; }
    public IClusterImageCodec? Codec { get; init; }

    /// <summary>D-6b — host-side hook that computes a Mandelbrot DD
    /// reference orbit. Wired by ClusterEntry from the Engine assembly so
    /// the Server library stays UI- and Engine-free. Null = orbit caching
    /// disabled (every tile recomputes its orbit, legacy D-2..D-5
    /// behaviour). When set, image jobs that
    /// <see cref="TilePlanner.QualifiesForSharedReferenceOrbit"/> get one
    /// orbit computed up-front and the blob attached to every tile.
    ///
    /// Args: centreX, centreXLo, centreY, centreYLo, maxIter.
    /// Returns: encoded blob bytes + orbit's maxIter cap. Null return =
    /// provider declined (compute failed, out-of-range centre, etc.); the
    /// job falls back to per-tile compute.</summary>
    public Func<double, double, double, double, int, (byte[] Blob, int MaxIter)?>? ReferenceOrbitProvider { get; init; }

    private readonly ClusterLogger _log;
    private readonly ConcurrentDictionary<string, ArtifactMerger> _mergers =
        new(StringComparer.Ordinal);

    /// <summary>D-4b — per-video-job streaming encoder pipelines. Created
    /// on job.submit when (a) RenderRequest.Lossless maps to a real
    /// codec preset AND (b) ffmpeg is on disk. Absent entries mean the
    /// video job falls back to the D-4a frames-manifest stub.</summary>
    private readonly ConcurrentDictionary<string, VideoFramePipeline> _videoPipelines =
        new(StringComparer.Ordinal);

    /// <summary>D-4b — token sources backing each pipeline. Cancelled on
    /// job cancel / coordinator shutdown so the encoder subprocess dies
    /// promptly instead of waiting on a dead frame source.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _videoCts =
        new(StringComparer.Ordinal);

    /// <summary>D-4c — slideshow jobs use neither the ArtifactMerger
    /// (no sub-rect math; each tile is a complete PNG) nor a
    /// VideoFramePipeline (no encode pass; final artifact is a manifest).
    /// This set marks slideshow jobs so tile.deliver can route to the
    /// per-slide write path instead of falling through merger lookup.
    /// The value is the JobSubmitDto kept alive for the finaliser, which
    /// consults it for per-slide display-ms + region/theme labels.</summary>
    private readonly ConcurrentDictionary<string, JobSubmitDto> _slideshowJobs =
        new(StringComparer.Ordinal);

    public ClusterCoordinator(WorkerRegistry registry, ClusterLogger log)
    {
        Registry = registry;
        _log     = log;
    }

    public Task<ClusterDispatchOutcome> HandleAsync(
        string method, JsonElement? @params, CertRole role, string thumbprint, CancellationToken ct,
        byte[]? binaryPayload = null)
        => method switch
        {
            "worker.register"  => HandleRegisterAsync(@params, thumbprint),
            "worker.heartbeat" => HandleHeartbeatAsync(@params, thumbprint),
            "tile.next"        => HandleTileNextAsync(@params, thumbprint, ct),
            "tile.deliver"     => HandleTileDeliverAsync(@params, thumbprint, binaryPayload),
            "tile.error"       => HandleTileErrorAsync(@params, thumbprint),
            "job.submit"       => HandleJobSubmitAsync(@params),
            "job.status"       => HandleJobStatusAsync(@params),
            "job.fetch"        => HandleJobFetchAsync(@params),
            "job.cancel"       => HandleJobCancelAsync(@params),
            // D-5a — admin RPCs. Live under cluster.* so the role gate in
            // FFServer (cluster.* → admin only) covers them; per-worker
            // mutations that the dev plan names worker.quiesce/kill move
            // here for the same reason (worker.* is reserved for worker→
            // master traffic).
            "cluster.status"         => HandleClusterStatusAsync(@params),
            "cluster.quiesceWorker"  => HandleClusterQuiesceWorkerAsync(@params),
            "cluster.killWorker"     => HandleClusterKillWorkerAsync(@params),
            "cluster.listJobs"       => HandleClusterListJobsAsync(@params),
            // D-5c — per-job tile map for JobDetailView (rect + state +
            // workerId per tile). Distinct from job.status (counters-only,
            // client-facing, polled at 1 Hz) because the payload scales
            // with TileCount; admin UI polls this at 2 s on the open job.
            "cluster.jobTileMap"     => HandleClusterJobTileMapAsync(@params),
            // D-5e — live-tunable cluster knobs surfaced by MasterConfigView.
            "cluster.config.get"     => HandleClusterConfigGetAsync(@params),
            "cluster.config.set"     => HandleClusterConfigSetAsync(@params),
            _                  => Task.FromResult(ClusterDispatchOutcome.NotHandled),
        };

    // ── Worker registration / heartbeat ─────────────────────────────────

    private Task<ClusterDispatchOutcome> HandleRegisterAsync(JsonElement? rawParams, string thumbprint)
    {
        WorkerRegisterDto? dto;
        try { dto = rawParams?.Deserialize<WorkerRegisterDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        if (!IsAcceptedProtocol(dto.ProtocolVersion))
            return Err("unsupported-protocol",
                $"protocol '{dto.ProtocolVersion}' not accepted (master speaks: {string.Join(",", AcceptedProtocolVersions)})");

        if (!string.IsNullOrEmpty(EngineBuildSha)
            && !string.Equals(EngineBuildSha, dto.EngineBuildSha, StringComparison.Ordinal))
            return Err("engine-sha-mismatch",
                $"worker engineBuildSha='{dto.EngineBuildSha}' does not match master='{EngineBuildSha}'");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        var entry = Registry.Register(dto, normThumb, out string? regErr);
        if (entry is null)
        {
            _log.Event("worker-register-refused", new Dictionary<string, object?>
            {
                ["thumbprint"] = normThumb,
                ["workerName"] = dto.WorkerName,
                ["error"]      = regErr,
            });
            return Err(regErr ?? "register-failed", "worker registration refused");
        }

        _log.Event("worker-register", new Dictionary<string, object?>
        {
            ["workerId"]    = entry.WorkerId,
            ["thumbprint"]  = normThumb,
            ["workerName"]  = entry.WorkerName,
            ["os"]          = entry.OsPlatform,
            ["cpu"]         = entry.CpuModel,
            ["cores"]       = entry.LogicalCores,
            ["ramBytes"]    = entry.TotalRamBytes,
            ["gpuCount"]    = entry.Gpus.Count,
            ["maxTiles"]    = entry.MaxConcurrentTiles,
            ["resume"]      = !string.IsNullOrEmpty(dto.ResumeWorkerId),
        });

        return Ok(new WorkerRegisterAckDto
        {
            WorkerId                 = entry.WorkerId,
            ServerUnixSeconds        = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            HeartbeatIntervalSeconds = Registry.HeartbeatIntervalSeconds,
            TileNextHoldSeconds      = (int)TileNextHold.TotalSeconds,
        });
    }

    private Task<ClusterDispatchOutcome> HandleHeartbeatAsync(JsonElement? rawParams, string thumbprint)
    {
        HeartbeatDto? dto;
        try { dto = rawParams?.Deserialize<HeartbeatDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        var entry = Registry.Lookup(dto.WorkerId, normThumb, out string? err);
        if (entry is null)
        {
            _log.Event("heartbeat-refused", new Dictionary<string, object?>
            {
                ["workerId"]   = dto.WorkerId,
                ["thumbprint"] = normThumb,
                ["error"]      = err,
            });
            return Err(err ?? "unknown-worker", "heartbeat refused");
        }

        Registry.Heartbeat(entry, dto);

        return Ok(new HeartbeatAckDto
        {
            ServerUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Quiesce           = entry.Quiesced,
        });
    }

    // ── tile.next ───────────────────────────────────────────────────────

    private async Task<ClusterDispatchOutcome> HandleTileNextAsync(
        JsonElement? rawParams, string thumbprint, CancellationToken ct)
    {
        HeartbeatDto? dto;
        try { dto = rawParams?.Deserialize<HeartbeatDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return ClusterDispatchOutcome.Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return ClusterDispatchOutcome.Err("bad-request", "missing params");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        var entry = Registry.Lookup(dto.WorkerId, normThumb, out string? err);
        if (entry is null)
            return ClusterDispatchOutcome.Err(err ?? "unknown-worker", "tile.next refused");

        // No dispatcher wired (D-1 / unit tests): keep the old behaviour
        // — sleep for the hold then return WaitAgain.
        if (Dispatcher is null)
        {
            try { await Task.Delay(TileNextHold, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return ClusterDispatchOutcome.Ok(new TileNextResultDto
            {
                WaitAgain = true,
                Shutdown  = ct.IsCancellationRequested,
            });
        }

        // Real dispatch path. Long-poll until a tile arrives, the hold
        // expires, or the session is cancelled.
        var tile = await Dispatcher.ClaimNextAsync(entry.WorkerId, TileNextHold, ct).ConfigureAwait(false);
        if (tile is null)
            return ClusterDispatchOutcome.Ok(new TileNextResultDto
            {
                WaitAgain = true,
                Shutdown  = ct.IsCancellationRequested,
            });

        // D-4b — backpressure: if this is a video tile and the streaming
        // encoder for its job is behind by more than MaxFrameQueueDepth
        // frames, hold the worker back. ReturnPending re-enqueues the
        // tile without burning a retry attempt; the worker gets
        // WaitAgain and re-polls when the encoder catches up.
        if (tile.FrameRange != null
            && _videoPipelines.TryGetValue(tile.JobId, out var gatePipe)
            && gatePipe.IsBehind(MaxFrameQueueDepth))
        {
            Dispatcher.ReturnPending(tile.JobId, tile);
            _log.Event("tile-backpressure", new Dictionary<string, object?>
            {
                ["jobId"]    = tile.JobId,
                ["tileId"]   = tile.TileId,
                ["workerId"] = entry.WorkerId,
                ["backlog"]  = gatePipe.Backlog,
                ["maxDepth"] = MaxFrameQueueDepth,
            });
            return ClusterDispatchOutcome.Ok(new TileNextResultDto
            {
                WaitAgain = true,
                Shutdown  = false,
            });
        }

        // Bookkeeping: the job goes to "rendering" the first time any
        // tile leaves the queue.
        if (Jobs != null && Jobs.Exists(tile.JobId))
        {
            try
            {
                Jobs.UpdateStatus(tile.JobId, s =>
                {
                    if (s.JobState is "queued" or "planning") s.JobState = "rendering";
                    s.TilesInFlight = Dispatcher.InFlightCount(tile.JobId);
                });
            }
            catch { /* job dir may have been evicted; ignore */ }
        }

        _log.Event("tile-assign", new Dictionary<string, object?>
        {
            ["jobId"]   = tile.JobId,
            ["tileId"]  = tile.TileId,
            ["workerId"]= entry.WorkerId,
            ["attempt"] = tile.Attempt,
        });

        return ClusterDispatchOutcome.Ok(new TileNextResultDto
        {
            WaitAgain = false,
            Shutdown  = false,
            Tile      = tile,
        });
    }

    // ── tile.deliver / tile.error ───────────────────────────────────────

    private Task<ClusterDispatchOutcome> HandleTileDeliverAsync(
        JsonElement? rawParams, string thumbprint, byte[]? binaryPayload)
    {
        if (Dispatcher is null || Jobs is null)
            return Err("not-configured", "master has no dispatcher/jobs");

        TileDeliverDto? dto;
        try { dto = rawParams?.Deserialize<TileDeliverDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        var workerEntry = Registry.Lookup(dto.WorkerId, normThumb, out string? err);
        if (workerEntry is null)
            return Err(err ?? "unknown-worker", "tile.deliver refused");

        if (!Dispatcher.KnowsJob(dto.JobId))
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "unknown-job" });

        // D-3: prefer the binary trailer when present. Worker advertises
        // PayloadKind="rgba" (raw BGRA) or "png" via binary trailer to
        // avoid base64+JSON-string overhead on the hot path. Legacy
        // bytesBase64 path is kept for back-compat.
        byte[] decoded;
        if (binaryPayload != null)
        {
            decoded = binaryPayload;
        }
        else
        {
            try { decoded = Convert.FromBase64String(dto.BytesBase64); }
            catch (Exception ex) { return Err("bad-request", $"bytesBase64: {ex.Message}"); }
        }

        // SHA-256 check first — TLS already authenticates the stream, so
        // a mismatch means the worker hashed pre-encode and we decoded
        // post-encode something else (e.g. base64 padding corruption).
        string actualSha = Convert.ToBase64String(SHA256.HashData(decoded));
        if (!string.Equals(actualSha, dto.Sha256, StringComparison.Ordinal))
        {
            _log.Event("tile-sha-mismatch", new Dictionary<string, object?>
            {
                ["jobId"]    = dto.JobId,
                ["tileId"]   = dto.TileId,
                ["workerId"] = dto.WorkerId,
                ["got"]      = actualSha,
                ["expected"] = dto.Sha256,
            });
            Dispatcher.RecordFailure(dto.JobId, dto.TileId);
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "sha-mismatch" });
        }

        // D-4 — frames trailer: video tile carries a packed batch of PNGs.
        // Routed to its own handler so the image-tile fast path stays
        // exactly as in D-3.
        if (string.Equals(dto.PayloadKind, "frames", StringComparison.Ordinal))
            return HandleFramesDeliverAsync(dto, workerEntry, decoded);

        // D-4c — slideshow jobs deliver one whole PNG per tile (no
        // sub-rect math, no merger). Route past the image-tile path
        // before it tries to look up a merger that doesn't exist.
        if (_slideshowJobs.TryGetValue(dto.JobId, out var slideSubmit))
            return HandleSlideDeliverAsync(dto, workerEntry, decoded, slideSubmit);

        if (Codec is null)
            return Err("not-configured", "master has no image codec wired");

        // Look up the merger and tile rect.
        if (!_mergers.TryGetValue(dto.JobId, out var merger))
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "unknown-job" });

        // Find this tile's rect from the persisted plan to avoid trusting
        // worker-supplied offsets.
        var plan = ReadPlan(dto.JobId);
        if (plan is null || dto.TileId < 0 || dto.TileId >= plan.Count)
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "tile-not-in-flight" });
        var meta = plan[dto.TileId];

        if (meta.Width != dto.Width || meta.Height != dto.Height)
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "size-mismatch" });

        try
        {
            bool merged = dto.PayloadKind switch
            {
                "rgba" => merger.TryMergeRgbaTile(dto.TileId, meta.OffsetX, meta.OffsetY, meta.Width, meta.Height, decoded),
                _      => merger.TryMergePngTile (dto.TileId, meta.OffsetX, meta.OffsetY, meta.Width, meta.Height, decoded),
            };
            if (!merged)
                return Ok(new TileDeliverAckDto { Accepted = true, RefuseReason = null });  // dup is OK
        }
        catch (Exception ex)
        {
            _log.Event("tile-merge-failed", new Dictionary<string, object?>
            {
                ["jobId"]   = dto.JobId,
                ["tileId"]  = dto.TileId,
                ["error"]   = ex.Message,
            });
            Dispatcher.RecordFailure(dto.JobId, dto.TileId);
            return Err("merge-failed", ex.Message);
        }

        // Persist the raw payload too — useful for crash recovery + debug.
        try { Jobs.WriteTileBytes(dto.JobId, dto.TileId, decoded); } catch { }

        Dispatcher.AcceptDelivery(dto.JobId, dto.TileId, dto.WorkerId);

        // D-3b — feed the EMA so future jobs adapt tile size to this
        // worker's measured throughput. Stolen-tile duplicates land here
        // too: both deliveries record a sample, which is fine — the
        // straggler being late is exactly the signal we want averaged
        // into ms-per-kilopixel.
        try { workerEntry.RecordTileTime((long)meta.Width * meta.Height, dto.RenderMs); }
        catch { }

        int done = Dispatcher.CompletedCount(dto.JobId);
        int total = plan.Count;
        Jobs.UpdateStatus(dto.JobId, s =>
        {
            s.TilesDone     = done;
            s.TilesInFlight = Dispatcher.InFlightCount(dto.JobId);
        });
        Jobs.AppendEvent(dto.JobId, "tile-delivered", new Dictionary<string, object?>
        {
            ["tileId"] = dto.TileId,
            ["worker"] = dto.WorkerId,
            ["ms"]     = dto.RenderMs,
        });

        if (done == total) FinaliseMerge(dto.JobId, merger);

        return Ok(new TileDeliverAckDto { Accepted = true });
    }

    /// <summary>D-4 frames-payload tile delivery. Parses the FRMS trailer
    /// (worker-packed concatenated PNGs), validates frames against the
    /// persisted plan's frame range for this tile, and writes each frame
    /// to the job's frames/ folder. D-4b will attach an ffmpeg encode
    /// step when all tiles have delivered.</summary>
    private Task<ClusterDispatchOutcome> HandleFramesDeliverAsync(
        TileDeliverDto dto, WorkerEntry workerEntry, byte[] decoded)
    {
        var planFrames = ReadFramePlan(dto.JobId);
        if (planFrames is null || dto.TileId < 0 || dto.TileId >= planFrames.Count)
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "tile-not-in-flight" });

        var range = planFrames[dto.TileId];
        if (range is null)
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "not-a-video-tile" });

        List<FramesPayloadCodec.Frame> frames;
        try { frames = FramesPayloadCodec.Unpack(decoded); }
        catch (Exception ex) { return Err("bad-request", $"frames trailer: {ex.Message}"); }

        int expectedCount = range.EndFrame - range.StartFrame;
        if (frames.Count != expectedCount)
        {
            _log.Event("frames-count-mismatch", new Dictionary<string, object?>
            {
                ["jobId"]    = dto.JobId,
                ["tileId"]   = dto.TileId,
                ["got"]      = frames.Count,
                ["expected"] = expectedCount,
            });
            Dispatcher!.RecordFailure(dto.JobId, dto.TileId);
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "frame-count-mismatch" });
        }

        foreach (var f in frames)
        {
            if (f.FrameIndex < range.StartFrame || f.FrameIndex >= range.EndFrame)
            {
                _log.Event("frame-out-of-range", new Dictionary<string, object?>
                {
                    ["jobId"]   = dto.JobId,
                    ["tileId"]  = dto.TileId,
                    ["frame"]   = f.FrameIndex,
                    ["range"]   = $"[{range.StartFrame},{range.EndFrame})",
                });
                Dispatcher!.RecordFailure(dto.JobId, dto.TileId);
                return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "frame-out-of-range" });
            }
        }

        // Idempotent: if every frame already lives on disk, the worker
        // is delivering a duplicate (stealer race). Accept silently.
        bool alreadyAllOnDisk = true;
        for (int f = range.StartFrame; f < range.EndFrame; f++)
            if (!Jobs!.FrameExists(dto.JobId, f)) { alreadyAllOnDisk = false; break; }

        if (!alreadyAllOnDisk)
        {
            try
            {
                foreach (var f in frames)
                    Jobs!.WriteFrameBytes(dto.JobId, f.FrameIndex, f.Png);
            }
            catch (Exception ex)
            {
                _log.Event("frames-write-failed", new Dictionary<string, object?>
                {
                    ["jobId"]  = dto.JobId,
                    ["tileId"] = dto.TileId,
                    ["error"]  = ex.Message,
                });
                Dispatcher!.RecordFailure(dto.JobId, dto.TileId);
                return Err("frames-write-failed", ex.Message);
            }
        }

        // Accept the tile — duplicate-delivery first-wins handled inside
        // the dispatcher (returns false for already-completed tile, which
        // is fine here too).
        Dispatcher!.AcceptDelivery(dto.JobId, dto.TileId, dto.WorkerId);

        // Per-frame-pixel rate feeds the same EMA as image tiles. Treat
        // pixels as (W * H * frame count) so a frame-range tile's measured
        // ms-per-kilopixel is comparable to an image tile's.
        try
        {
            long px = (long)dto.Width * dto.Height * frames.Count;
            workerEntry.RecordTileTime(px, dto.RenderMs);
        }
        catch { }

        // D-4b — let the streaming encoder know N more frames just landed
        // on disk. Pipeline is null when the job falls back to the D-4a
        // frames-manifest stub (no lossless preset or no ffmpeg on box).
        int encodedNow = 0;
        if (_videoPipelines.TryGetValue(dto.JobId, out var pipe))
        {
            pipe.NotifyFramesDelivered(frames.Count);
            encodedNow = pipe.EncodedFrames;
        }

        int done = Dispatcher.CompletedCount(dto.JobId);
        int total = planFrames.Count;
        int framesOnDisk = Jobs!.CountFrames(dto.JobId);
        Jobs.UpdateStatus(dto.JobId, s =>
        {
            s.TilesDone     = done;
            s.TilesInFlight = Dispatcher.InFlightCount(dto.JobId);
            s.FramesDone    = framesOnDisk;
            s.EncodedFrames = encodedNow;
            if (s.JobState is "queued" or "planning") s.JobState = "rendering";
        });
        Jobs.AppendEvent(dto.JobId, "frames-delivered", new Dictionary<string, object?>
        {
            ["tileId"]  = dto.TileId,
            ["worker"]  = dto.WorkerId,
            ["frames"]  = frames.Count,
            ["ms"]      = dto.RenderMs,
            ["encoded"] = encodedNow,
        });

        if (done == total) FinaliseVideoFrames(dto.JobId, framesOnDisk);

        return Ok(new TileDeliverAckDto { Accepted = true });
    }

    /// <summary>D-4c — slideshow tile delivery. The worker rendered a
    /// whole slide PNG; treat the tile id as the 0-based slide index,
    /// write the PNG into <see cref="JobStore.SlidesDir"/>, update
    /// counters. On the last slide trigger the manifest assembler.
    /// Mirrors <see cref="HandleFramesDeliverAsync"/> for video except
    /// (a) one file per tile (b) no encoder gate (c) no FrameRange.</summary>
    private Task<ClusterDispatchOutcome> HandleSlideDeliverAsync(
        TileDeliverDto dto, WorkerEntry workerEntry, byte[] decoded,
        JobSubmitDto submit)
    {
        if (submit.Slides is null || dto.TileId < 0 || dto.TileId >= submit.Slides.Count)
            return Ok(new TileDeliverAckDto { Accepted = false, RefuseReason = "tile-not-in-flight" });

        // Idempotent: if the slide PNG is already on disk a duplicate
        // delivery (stealer race) is a no-op accept.
        bool already = Jobs!.SlideExists(dto.JobId, dto.TileId);
        if (!already)
        {
            // Slideshow tiles arrive as PNG (default) or RGBA — the
            // worker chose its delivery format per its global config and
            // doesn't know the parent job is a slideshow. PNG: store as-
            // is. RGBA: encode through the registered codec straight to
            // disk so the artifact dir is uniformly PNGs.
            try
            {
                bool isRgba = string.Equals(dto.PayloadKind, "rgba", StringComparison.Ordinal);
                if (isRgba)
                {
                    if (Codec is null)
                        return Ok(new TileDeliverAckDto
                        {
                            Accepted     = false,
                            RefuseReason = "not-configured",
                        });
                    Jobs.EncodeSlideTo(dto.JobId, dto.TileId,
                        tmp => Codec.EncodeBgraToPng(decoded, dto.Width, dto.Height, tmp));
                }
                else if (string.IsNullOrEmpty(dto.PayloadKind)
                         || string.Equals(dto.PayloadKind, "png", StringComparison.Ordinal))
                {
                    Jobs.WriteSlideBytes(dto.JobId, dto.TileId, decoded);
                }
                else
                {
                    return Ok(new TileDeliverAckDto
                    {
                        Accepted     = false,
                        RefuseReason = "wrong-payload-kind",
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Event("slide-write-failed", new Dictionary<string, object?>
                {
                    ["jobId"]  = dto.JobId,
                    ["tileId"] = dto.TileId,
                    ["error"]  = ex.Message,
                });
                Dispatcher!.RecordFailure(dto.JobId, dto.TileId);
                return Err("slide-write-failed", ex.Message);
            }
        }

        Dispatcher!.AcceptDelivery(dto.JobId, dto.TileId, dto.WorkerId);

        // Per-tile EMA feed: each slide is its own full render so pixels
        // == W * H of the delivered tile.
        try { workerEntry.RecordTileTime((long)dto.Width * dto.Height, dto.RenderMs); }
        catch { }

        int done = Dispatcher.CompletedCount(dto.JobId);
        int total = submit.Slides.Count;
        int slidesOnDisk = Jobs.CountSlides(dto.JobId);
        Jobs.UpdateStatus(dto.JobId, s =>
        {
            s.TilesDone     = done;
            s.TilesInFlight = Dispatcher.InFlightCount(dto.JobId);
            if (s.JobState is "queued" or "planning") s.JobState = "rendering";
        });
        Jobs.AppendEvent(dto.JobId, "slide-delivered", new Dictionary<string, object?>
        {
            ["tileId"] = dto.TileId,
            ["worker"] = dto.WorkerId,
            ["bytes"]  = decoded.Length,
            ["ms"]     = dto.RenderMs,
        });

        if (done == total) FinaliseSlidesAsManifest(dto.JobId, submit, slidesOnDisk);

        return Ok(new TileDeliverAckDto { Accepted = true });
    }

    /// <summary>D-4c — finaliser for slideshow jobs. Hands off to
    /// <see cref="SlideshowAssembler"/> which writes a slides-manifest.json
    /// next to the per-slide PNGs. The manifest is the fetched artifact;
    /// the per-slide PNGs stay on disk so the client can fetch them
    /// individually if the renderer prefers streaming the slides.</summary>
    private void FinaliseSlidesAsManifest(string jobId, JobSubmitDto submit, int slidesOnDisk)
    {
        try
        {
            Jobs!.UpdateStatus(jobId, s => s.JobState = "merging");

            var result = SlideshowAssembler.Assemble(Jobs, jobId, submit);

            Jobs.UpdateStatus(jobId, s =>
            {
                s.JobState       = "ready";
                s.ArtifactExt    = SlideshowAssembler.ArtifactExt;
                s.ArtifactBytes  = result.ArtifactBytes;
                s.ArtifactSha256 = result.ArtifactSha256;
                s.TilesInFlight  = 0;
            });
            Jobs.AppendEvent(jobId, "slides-ready", new Dictionary<string, object?>
            {
                ["slides"]      = result.SlideCount,
                ["slideBytes"]  = result.SlideTotalBytes,
                ["manifestBytes"] = result.ArtifactBytes,
            });
            _log.Event("job-slides-ready", new Dictionary<string, object?>
            {
                ["jobId"]      = jobId,
                ["slides"]     = result.SlideCount,
                ["slideBytes"] = result.SlideTotalBytes,
            });
            Dispatcher!.RetireJob(jobId);
        }
        catch (Exception ex)
        {
            Jobs?.UpdateStatus(jobId, s => { s.JobState = "failed"; s.FailReason = "slides-finalise-failed: " + ex.Message; });
            _log.Event("job-failed", new Dictionary<string, object?>
            {
                ["jobId"] = jobId,
                ["where"] = "finalise-slides",
                ["error"] = ex.Message,
            });
        }
        finally
        {
            _slideshowJobs.TryRemove(jobId, out _);
        }
    }

    /// <summary>D-4 finaliser for video jobs. When a streaming encoder
    /// pipeline is wired (D-4b), wait for ffmpeg to consume the last
    /// frame, drain stdin, and exit; the artifact is the produced
    /// .mp4 / .mkv. Otherwise (no lossless preset, or no ffmpeg on the
    /// master) fall back to the D-4a frames-manifest stub so the state
    /// machine still reaches "ready" and clients can inspect frame
    /// counts.</summary>
    private void FinaliseVideoFrames(string jobId, int framesOnDisk)
    {
        if (_videoPipelines.TryGetValue(jobId, out var pipe))
        {
            _ = Task.Run(() => FinaliseVideoFramesWithEncoderAsync(jobId, framesOnDisk, pipe));
            return;
        }
        FinaliseVideoFramesAsManifest(jobId, framesOnDisk);
    }

    /// <summary>D-4b — fence the streaming encoder, mark the encoded
    /// artifact ready. Runs off-thread because <see cref="VideoFramePipeline.Completion"/>
    /// only resolves after ffmpeg drains stdin and exits, which can be
    /// seconds for ffv1 / qp0 H.264.</summary>
    private async Task FinaliseVideoFramesWithEncoderAsync(
        string jobId, int framesOnDisk, VideoFramePipeline pipe)
    {
        try
        {
            Jobs!.UpdateStatus(jobId, s => s.JobState = "merging");

            var (ok, log) = await pipe.Completion.ConfigureAwait(false);
            if (!ok)
            {
                string tail = log.Length > 2000 ? log[^2000..] : log;
                Jobs.UpdateStatus(jobId, s =>
                {
                    s.JobState   = "failed";
                    s.FailReason = "ffmpeg-encode-failed: " + tail;
                });
                _log.Event("job-failed", new Dictionary<string, object?>
                {
                    ["jobId"] = jobId,
                    ["where"] = "ffmpeg-encode",
                    ["error"] = tail,
                });
                Dispatcher!.RetireJob(jobId);
                return;
            }

            string outPath = pipe.ArtifactPath;
            if (!File.Exists(outPath))
            {
                Jobs.UpdateStatus(jobId, s =>
                {
                    s.JobState   = "failed";
                    s.FailReason = "ffmpeg-output-missing: " + outPath;
                });
                Dispatcher!.RetireJob(jobId);
                return;
            }

            long size = new FileInfo(outPath).Length;
            string sha = ComputeFileSha256Base64(outPath);
            int encoded = pipe.EncodedFrames;
            Jobs.UpdateStatus(jobId, s =>
            {
                s.JobState       = "ready";
                s.ArtifactExt    = pipe.ArtifactExt;
                s.ArtifactBytes  = size;
                s.ArtifactSha256 = sha;
                s.TilesInFlight  = 0;
                s.EncodedFrames  = encoded;
            });
            Jobs.AppendEvent(jobId, "video-ready", new Dictionary<string, object?>
            {
                ["frames"]  = framesOnDisk,
                ["encoded"] = encoded,
                ["bytes"]   = size,
                ["ext"]     = pipe.ArtifactExt,
            });
            _log.Event("job-video-ready", new Dictionary<string, object?>
            {
                ["jobId"]  = jobId,
                ["bytes"]  = size,
                ["frames"] = framesOnDisk,
                ["ext"]    = pipe.ArtifactExt,
            });
            Dispatcher!.RetireJob(jobId);
        }
        catch (Exception ex)
        {
            Jobs?.UpdateStatus(jobId, s => { s.JobState = "failed"; s.FailReason = "video-finalise-failed: " + ex.Message; });
            _log.Event("job-failed", new Dictionary<string, object?>
            {
                ["jobId"] = jobId,
                ["where"] = "finalise-video",
                ["error"] = ex.Message,
            });
        }
        finally
        {
            await DisposeVideoPipelineAsync(jobId).ConfigureAwait(false);
        }
    }

    /// <summary>D-4a fallback path — writes a frames-manifest.json
    /// artifact describing the per-frame PNGs on disk and transitions
    /// the job to ready. Used when no lossless preset was requested or
    /// when ffmpeg is unavailable on the master.</summary>
    private void FinaliseVideoFramesAsManifest(string jobId, int framesOnDisk)
    {
        try
        {
            Jobs!.UpdateStatus(jobId, s => s.JobState = "merging");

            string framesDir = Jobs.FramesDir(jobId);
            long totalBytes = 0;
            var entries = new List<object>();
            foreach (var path in Directory.EnumerateFiles(framesDir, "frame_*.png"))
            {
                var fi = new FileInfo(path);
                totalBytes += fi.Length;
                entries.Add(new { name = fi.Name, bytes = fi.Length });
            }

            string manifestPath = Jobs.ArtifactPath(jobId, "frames-manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                jobId,
                frames     = framesOnDisk,
                totalBytes,
                entries,
            }, new JsonSerializerOptions { WriteIndented = true }));

            long manifestSize = new FileInfo(manifestPath).Length;
            string sha = ComputeFileSha256Base64(manifestPath);
            Jobs.UpdateStatus(jobId, s =>
            {
                s.JobState       = "ready";
                s.ArtifactExt    = "frames-manifest.json";
                s.ArtifactBytes  = manifestSize;
                s.ArtifactSha256 = sha;
                s.TilesInFlight  = 0;
            });
            Jobs.AppendEvent(jobId, "frames-ready", new Dictionary<string, object?>
            {
                ["frames"]     = framesOnDisk,
                ["totalBytes"] = totalBytes,
            });
            _log.Event("job-frames-ready", new Dictionary<string, object?>
            {
                ["jobId"]      = jobId,
                ["frames"]     = framesOnDisk,
                ["totalBytes"] = totalBytes,
            });
            Dispatcher!.RetireJob(jobId);
        }
        catch (Exception ex)
        {
            Jobs?.UpdateStatus(jobId, s => { s.JobState = "failed"; s.FailReason = "frames-finalise-failed: " + ex.Message; });
            _log.Event("job-failed", new Dictionary<string, object?>
            {
                ["jobId"] = jobId,
                ["where"] = "finalise-frames",
                ["error"] = ex.Message,
            });
        }
    }

    private async Task DisposeVideoPipelineAsync(string jobId)
    {
        if (_videoPipelines.TryRemove(jobId, out var p))
            try { await p.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_videoCts.TryRemove(jobId, out var cts))
            try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    private void FinaliseMerge(string jobId, ArtifactMerger merger)
    {
        try
        {
            Jobs!.UpdateStatus(jobId, s => s.JobState = "merging");
            string outPath = Jobs.ArtifactPath(jobId, "png");
            merger.WritePng(outPath);
            long size = new FileInfo(outPath).Length;
            string sha = ComputeFileSha256Base64(outPath);
            Jobs.UpdateStatus(jobId, s =>
            {
                s.JobState       = "ready";
                s.ArtifactExt    = "png";
                s.ArtifactBytes  = size;
                s.ArtifactSha256 = sha;
                s.TilesInFlight  = 0;
            });
            Jobs.AppendEvent(jobId, "ready", new Dictionary<string, object?>
            {
                ["bytes"] = size,
                ["sha"]   = sha,
            });
            _log.Event("job-ready", new Dictionary<string, object?>
            {
                ["jobId"] = jobId,
                ["bytes"] = size,
            });
            Dispatcher!.RetireJob(jobId);
            _mergers.TryRemove(jobId, out _);
            merger.Dispose();
        }
        catch (Exception ex)
        {
            Jobs?.UpdateStatus(jobId, s => { s.JobState = "failed"; s.FailReason = "merge-failed: " + ex.Message; });
            _log.Event("job-failed", new Dictionary<string, object?>
            {
                ["jobId"] = jobId,
                ["where"] = "finalise-merge",
                ["error"] = ex.Message,
            });
        }
    }

    private Task<ClusterDispatchOutcome> HandleTileErrorAsync(JsonElement? rawParams, string thumbprint)
    {
        if (Dispatcher is null || Jobs is null)
            return Err("not-configured", "master has no dispatcher/jobs");

        TileErrorDto? dto;
        try { dto = rawParams?.Deserialize<TileErrorDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        if (Registry.Lookup(dto.WorkerId, normThumb, out string? err) is null)
            return Err(err ?? "unknown-worker", "tile.error refused");

        bool fatal = dto.Code is "forbidden-fractal" or "limit-exceeded" or "cancelled";
        bool requeued = !fatal && Dispatcher.RecordFailure(dto.JobId, dto.TileId);

        Jobs.AppendEvent(dto.JobId, "tile-error", new Dictionary<string, object?>
        {
            ["tileId"]   = dto.TileId,
            ["worker"]   = dto.WorkerId,
            ["code"]     = dto.Code,
            ["message"]  = dto.Message,
            ["requeued"] = requeued,
        });

        if (!requeued)
        {
            Jobs.UpdateStatus(dto.JobId, s =>
            {
                s.JobState   = "failed";
                s.FailReason = $"tile {dto.TileId}: [{dto.Code}] {dto.Message}";
            });
            Dispatcher.RetireJob(dto.JobId);
            if (_mergers.TryRemove(dto.JobId, out var m)) m.Dispose();
            // D-4b — fail-the-job teardown for the streaming encoder.
            _ = DisposeVideoPipelineAsync(dto.JobId);
            // D-4c — drop slideshow registration so a re-submitted job
            // with a colliding id (impossible in practice, defensive)
            // doesn't inherit stale state.
            _slideshowJobs.TryRemove(dto.JobId, out _);
            _log.Event("job-failed", new Dictionary<string, object?>
            {
                ["jobId"]  = dto.JobId,
                ["where"]  = "tile-error",
                ["error"]  = $"{dto.Code}: {dto.Message}",
            });
        }
        return Ok(new TileErrorAckDto { Acknowledged = true });
    }

    // ── job.* ───────────────────────────────────────────────────────────

    private Task<ClusterDispatchOutcome> HandleJobSubmitAsync(JsonElement? rawParams)
    {
        if (Jobs is null || Dispatcher is null)
            return Err("not-configured", "master has no jobs/dispatcher");

        JobSubmitDto? dto;
        try { dto = rawParams?.Deserialize<JobSubmitDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        // D-5e — admin-configurable cap on concurrent non-terminal jobs.
        // 0 = unlimited. Counts off the on-disk store so a restart doesn't
        // forget queued/rendering work that's still around. Cheap: the
        // store is bounded by the artifact-retention sweep.
        if (ClusterMaxJobs > 0)
        {
            int active = 0;
            foreach (var id in Jobs.ListJobIds())
            {
                var s = Jobs.ReadStatus(id);
                if (s is null) continue;
                if (s.JobState is "ready" or "failed" or "cancelled") continue;
                active++;
            }
            if (active >= ClusterMaxJobs)
                return Err("queue-full",
                    $"cluster has {active} active jobs, cap is {ClusterMaxJobs}");
        }

        bool isVideo     = string.Equals(dto.Request.Mode, "video",     StringComparison.OrdinalIgnoreCase);
        bool isImage     = string.Equals(dto.Request.Mode, "image",     StringComparison.OrdinalIgnoreCase);
        bool isSlideshow = string.Equals(dto.Request.Mode, "slideshow", StringComparison.OrdinalIgnoreCase);
        if (!isVideo && !isImage && !isSlideshow)
            return Err("unsupported-mode",
                $"cluster supports image|video|slideshow mode; got '{dto.Request.Mode}'");

        // Both image and video tiles render a single image of the same
        // fractal type — same allowlist applies. FramePlanner.ValidateForVideo
        // forwards to TilePlanner so this single check covers both modes.
        // Slideshow validates per-slide inside SlideshowPlanner.PlanSlideshow
        // because each slide may carry its own FractalType.
        if (!isSlideshow
            && !TilePlanner.ValidateForTiling(dto.Request.FractalType, out string? whyNot))
            return Err("untileable-fractal", whyNot!);

        TilePlanner.Plan plan;
        try
        {
            if (isVideo)
            {
                plan = FramePlanner.PlanVideo(dto.Request, dto.TilePixelsHint);
            }
            else if (isSlideshow)
            {
                plan = SlideshowPlanner.PlanSlideshow(dto);
            }
            else
            {
                var workerHints = new List<int>();
                foreach (var w in Registry.Snapshot())
                    if (w.PreferredTilePixels > 0) workerHints.Add(w.PreferredTilePixels);
                // D-3b — feed the registry's learned per-worker EMA into the
                // planner. First job sees median=0 (no data); planner falls
                // back to PreferredTilePixels. Subsequent jobs auto-size.
                double medianMsPerKpx = Registry.MedianMsPerKilopixel();
                // D-5e — when the client gave no per-job hint AND the admin
                // configured a cluster-wide default, prefer that over the
                // built-in TilePlanner.DefaultTilePixels. Lets operators
                // dial the planner without touching code.
                int hint = dto.TilePixelsHint;
                if (hint <= 0 && ClusterTileTargetPixels > 0) hint = ClusterTileTargetPixels;
                plan = TilePlanner.PlanImage(dto.Request, hint, workerHints, medianMsPerKpx);
            }
        }
        catch (Exception ex) { return Err("plan-failed", ex.Message); }

        string jobId = JobStore.NewJobId();
        foreach (var t in plan.Tiles) t.JobId = jobId;

        // D-6b — shared reference-orbit attach. Image jobs that meet the
        // Mandelbrot + zoom gate AND have a provider wired get one orbit
        // computed up-front; every tile carries the blob + image-frame
        // geometry so the worker seeds its calculator. Compute failure or
        // a null return leaves the plan untouched (per-tile recompute,
        // identical to pre-D-6b behaviour).
        if (isImage && ReferenceOrbitProvider != null
            && TilePlanner.QualifiesForSharedReferenceOrbit(dto.Request))
        {
            try
            {
                int orbitMaxIter = dto.Request.Iterations is int ri && ri > 0 ? ri : 1000;
                var result = ReferenceOrbitProvider(
                    dto.Request.CenterX!.Value, dto.Request.CenterXLo,
                    dto.Request.CenterY!.Value, dto.Request.CenterYLo,
                    orbitMaxIter);
                if (result is { Blob: var blob, MaxIter: var capUsed })
                {
                    TilePlanner.AttachSharedReferenceOrbit(plan, dto.Request, blob, capUsed);
                    _log.Event("ref-orbit-attached", new Dictionary<string, object?>
                    {
                        ["jobId"] = jobId,
                        ["blobBytes"] = blob.Length,
                        ["orbitMaxIter"] = capUsed,
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Event("ref-orbit-attach-failed", new Dictionary<string, object?>
                {
                    ["jobId"] = jobId,
                    ["err"] = $"{ex.GetType().Name}: {ex.Message}",
                });
            }
        }

        try { Jobs.Create(jobId, dto, plan); }
        catch (Exception ex) { return Err("internal", $"persist failed: {ex.Message}"); }

        if (isImage)
        {
            if (Codec is null)
                return Err("not-configured", "master has no image codec wired");
            var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, Codec);
            _mergers[jobId] = merger;
        }
        else if (isSlideshow)
        {
            // D-4c — register the slideshow so tile.deliver routes to the
            // per-slide path. The JobSubmitDto is retained here (not just
            // on disk) so the finaliser can build the slides-manifest
            // without re-deserialising request.json.
            _slideshowJobs[jobId] = dto;
        }
        else
        {
            // D-4b — video jobs that asked for a lossless preset spin up
            // the streaming ffmpeg encoder. The reader task starts watching
            // FramesDir immediately; it'll block on Task.Delay until the
            // first frame_000001.png lands. Pipelines are torn down by
            // FinaliseVideoFramesWithEncoderAsync or HandleJobCancelAsync.
            var preset = VideoFramePipeline.PresetFromLossless(dto.Request.Lossless);
            if (preset != null && VideoFramePipeline.IsAvailable())
            {
                var cts = new CancellationTokenSource();
                string framesDir = Jobs.FramesDir(jobId);
                string artifactBase = Path.Combine(Jobs.JobDir(jobId), "artifact");
                var pipe = VideoFramePipeline.TryStart(
                    framesDir, plan.TotalFrames, dto.Request.VideoFps,
                    preset.Value, artifactBase, cts.Token);
                if (pipe != null)
                {
                    _videoPipelines[jobId] = pipe;
                    _videoCts[jobId]       = cts;
                }
                else
                {
                    cts.Dispose();
                }
            }
            // No-preset / no-ffmpeg / spawn-failed → fall back to the
            // D-4a frames-manifest stub at finalise time.
        }

        Dispatcher.EnqueueJob(jobId, plan.Tiles);
        Jobs.UpdateStatus(jobId, s =>
        {
            s.JobState = "planning";
            // For video, track totalFrames so per-frame progress shows up
            // alongside per-tile progress.
            if (plan.Mode == "video") s.TotalFrames = plan.TotalFrames;
        });
        Jobs.AppendEvent(jobId, "submitted", new Dictionary<string, object?>
        {
            ["tiles"] = plan.TileCount,
            ["w"]     = plan.ImageWidth,
            ["h"]     = plan.ImageHeight,
            ["mode"]  = plan.Mode,
            ["frames"]= plan.TotalFrames,
        });

        _log.Event("job-submitted", new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["tiles"] = plan.TileCount,
            ["w"]     = plan.ImageWidth,
            ["h"]     = plan.ImageHeight,
            ["mode"]  = plan.Mode,
            ["frames"]= plan.TotalFrames,
        });

        // Best-effort artifact-size estimate. Image: ~0.25 bytes/pixel for
        // PNG of a typical Mandelbrot. Video: same per-frame * frame count
        // (rough — encoder gets it down further, but the estimate is only
        // used by the client to gate free disk).
        long estBytes = (long)plan.ImageWidth * plan.ImageHeight / 4
                      * Math.Max(1, plan.TotalFrames);

        return Ok(new JobAckDto
        {
            JobId          = jobId,
            TileCount      = plan.TileCount,
            EstimatedBytes = estBytes,
        });
    }

    private Task<ClusterDispatchOutcome> HandleJobStatusAsync(JsonElement? rawParams)
    {
        if (Jobs is null) return Err("not-configured", "master has no jobs");

        JobStatusRequestDto? dto;
        try { dto = rawParams?.Deserialize<JobStatusRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.JobId))
            return Err("bad-request", "jobId required");

        var st = Jobs.ReadStatus(dto.JobId);
        if (st is null) return Err("unknown-job", $"job '{dto.JobId}' not found");

        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - st.CreatedUnixMs;
        double pct = st.TilesTotal == 0 ? 0
                   : 100.0 * st.TilesDone / st.TilesTotal;

        // ETA: extrapolate from current rate. Skip when too few tiles done.
        long? eta = null;
        if (st.TilesDone > 2 && st.TilesDone < st.TilesTotal && elapsed > 0)
        {
            double msPerTile = (double)elapsed / st.TilesDone;
            eta = (long)(msPerTile * (st.TilesTotal - st.TilesDone));
        }

        // D-4b — pipeline may have consumed more frames since the last
        // tile.deliver wrote status. Read live so the encoded counter
        // reflects current progress, not the last delivery.
        int encoded = st.EncodedFrames;
        if (_videoPipelines.TryGetValue(dto.JobId, out var livePipe))
            encoded = livePipe.EncodedFrames;

        return Ok(new JobStatusDto
        {
            JobState         = st.JobState,
            TilesTotal       = st.TilesTotal,
            TilesDone        = st.TilesDone,
            TilesInFlight    = st.TilesInFlight,
            ProgressPercent  = pct,
            ElapsedMs        = elapsed,
            EtaMs            = eta,
            ArtifactReady    = st.JobState == "ready",
            ArtifactBytes    = st.ArtifactBytes,
            FailReason       = st.FailReason,
            TotalFrames      = st.TotalFrames,
            FramesDone       = st.FramesDone,
            EncodedFrames    = encoded,
        });
    }

    private Task<ClusterDispatchOutcome> HandleJobFetchAsync(JsonElement? rawParams)
    {
        if (Jobs is null) return Err("not-configured", "master has no jobs");

        JobFetchRequestDto? dto;
        try { dto = rawParams?.Deserialize<JobFetchRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.JobId))
            return Err("bad-request", "jobId required");

        var st = Jobs.ReadStatus(dto.JobId);
        if (st is null) return Err("unknown-job", $"job '{dto.JobId}' not found");
        if (st.JobState != "ready")
            return Err("not-ready", $"job state is '{st.JobState}'");

        string ext = st.ArtifactExt ?? "png";
        string path = Jobs.ArtifactPath(dto.JobId, ext);
        if (!File.Exists(path))
            return Err("artifact-missing", $"artifact for '{dto.JobId}' is gone from disk");

        long size = new FileInfo(path).Length;
        int chunks = (int)((size + FetchChunkBytes - 1) / FetchChunkBytes);
        var ack = new JobFetchAckDto
        {
            JobId       = dto.JobId,
            ArtifactExt = ext,
            TotalBytes  = size,
            ChunkCount  = chunks,
            Sha256      = st.ArtifactSha256 ?? ComputeFileSha256Base64(path),
        };
        return Task.FromResult(ClusterDispatchOutcome.OkStreaming(ack, path, chunks));
    }

    private Task<ClusterDispatchOutcome> HandleJobCancelAsync(JsonElement? rawParams)
    {
        if (Jobs is null || Dispatcher is null)
            return Err("not-configured", "master has no jobs/dispatcher");

        JobCancelRequestDto? dto;
        try { dto = rawParams?.Deserialize<JobCancelRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.JobId))
            return Err("bad-request", "jobId required");

        var st = Jobs.ReadStatus(dto.JobId);
        if (st is null) return Err("unknown-job", $"job '{dto.JobId}' not found");

        bool terminal = st.JobState is "ready" or "failed" or "cancelled";
        var ack = new JobCancelAckDto
        {
            JobId         = dto.JobId,
            PreviousState = st.JobState,
            Cancelled     = !terminal,
        };
        if (!terminal)
        {
            Jobs.UpdateStatus(dto.JobId, s =>
            {
                s.JobState   = "cancelled";
                s.FailReason = "client-cancel";
            });
            Jobs.AppendEvent(dto.JobId, "cancelled", null);
            Dispatcher.RetireJob(dto.JobId);
            if (_mergers.TryRemove(dto.JobId, out var m)) m.Dispose();
            // D-4b — kill the encoder subprocess so a cancelled video
            // job doesn't leave an ffmpeg child running until the master
            // exits. Fire-and-forget; dispose is best-effort.
            _ = DisposeVideoPipelineAsync(dto.JobId);
            // D-4c — drop slideshow registration.
            _slideshowJobs.TryRemove(dto.JobId, out _);
            _log.Event("job-cancelled", new Dictionary<string, object?>
            {
                ["jobId"] = dto.JobId,
                ["prev"]  = st.JobState,
            });
        }
        return Ok(ack);
    }

    // ── cluster.* admin RPCs (D-5a) ─────────────────────────────────────

    private Task<ClusterDispatchOutcome> HandleClusterStatusAsync(JsonElement? rawParams)
    {
        ClusterStatusRequestDto? req = null;
        if (rawParams.HasValue && rawParams.Value.ValueKind != JsonValueKind.Null)
        {
            try { req = rawParams.Value.Deserialize<ClusterStatusRequestDto>(JsonRpcFraming.JsonOpts); }
            catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        }
        int recentLimit = Math.Clamp(req?.RecentJobLimit ?? 50, 1, 500);

        var resp = new ClusterStatusDto
        {
            ServerUnixSeconds        = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            HeartbeatIntervalSeconds = Registry.HeartbeatIntervalSeconds,
            LiveWorkerCount          = Registry.LiveCount(),
        };

        foreach (var w in Registry.Snapshot())
            resp.Workers.Add(BuildWorkerSummary(w));

        if (Jobs != null)
        {
            foreach (var summary in BuildRecentJobSummaries(recentLimit, includeTerminal: true, stateFilter: null))
                resp.Jobs.Add(summary);
        }

        return Ok(resp);
    }

    private Task<ClusterDispatchOutcome> HandleClusterQuiesceWorkerAsync(JsonElement? rawParams)
    {
        WorkerQuiesceRequestDto? dto;
        try { dto = rawParams?.Deserialize<WorkerQuiesceRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.WorkerId))
            return Err("bad-request", "workerId required");

        WorkerEntry? entry = null;
        foreach (var w in Registry.Snapshot())
            if (string.Equals(w.WorkerId, dto.WorkerId, StringComparison.Ordinal)) { entry = w; break; }
        if (entry is null)
            return Err("unknown-worker", $"worker '{dto.WorkerId}' not in registry");

        bool prev = entry.Quiesced;
        entry.SetQuiesce(dto.Quiesced);

        _log.Event("worker-quiesce", new Dictionary<string, object?>
        {
            ["workerId"] = dto.WorkerId,
            ["prev"]     = prev,
            ["current"]  = dto.Quiesced,
        });

        return Ok(new WorkerQuiesceAckDto
        {
            WorkerId      = dto.WorkerId,
            PreviousState = prev,
            CurrentState  = dto.Quiesced,
        });
    }

    private Task<ClusterDispatchOutcome> HandleClusterKillWorkerAsync(JsonElement? rawParams)
    {
        WorkerKillRequestDto? dto;
        try { dto = rawParams?.Deserialize<WorkerKillRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.WorkerId))
            return Err("bad-request", "workerId required");

        bool removed = Registry.Remove(dto.WorkerId);
        if (removed)
        {
            _log.Event("worker-kill", new Dictionary<string, object?>
            {
                ["workerId"] = dto.WorkerId,
            });
        }

        return Ok(new WorkerKillAckDto
        {
            WorkerId = dto.WorkerId,
            Removed  = removed,
        });
    }

    private Task<ClusterDispatchOutcome> HandleClusterListJobsAsync(JsonElement? rawParams)
    {
        if (Jobs is null) return Err("not-configured", "master has no jobs");

        JobListRequestDto? req = null;
        if (rawParams.HasValue && rawParams.Value.ValueKind != JsonValueKind.Null)
        {
            try { req = rawParams.Value.Deserialize<JobListRequestDto>(JsonRpcFraming.JsonOpts); }
            catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        }
        int limit = Math.Clamp(req?.Limit ?? 50, 1, 500);
        bool includeTerminal = req?.IncludeTerminal ?? true;
        string? stateFilter = string.IsNullOrEmpty(req?.StateFilter) ? null : req!.StateFilter;

        int total = 0;
        foreach (var _ in Jobs.ListJobIds()) total++;

        var resp = new JobListDto { TotalCount = total };
        foreach (var summary in BuildRecentJobSummaries(limit, includeTerminal, stateFilter))
            resp.Jobs.Add(summary);
        return Ok(resp);
    }

    private Task<ClusterDispatchOutcome> HandleClusterJobTileMapAsync(JsonElement? rawParams)
    {
        if (Jobs is null || Dispatcher is null)
            return Err("not-configured", "master has no jobs/dispatcher");

        JobTileMapRequestDto? dto;
        try { dto = rawParams?.Deserialize<JobTileMapRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null || string.IsNullOrEmpty(dto.JobId))
            return Err("bad-request", "jobId required");

        var st = Jobs.ReadStatus(dto.JobId);
        if (st is null) return Err("unknown-job", $"job '{dto.JobId}' not found");

        var resp = new JobTileMapDto
        {
            JobId         = dto.JobId,
            JobState      = st.JobState,
            Mode          = st.Mode,
            TilesTotal    = st.TilesTotal,
            TilesDone     = st.TilesDone,
            TilesInFlight = st.TilesInFlight,
        };

        // Plan.json carries tile rects (image), frame ranges (video) or
        // slide indices (slideshow). Read once and walk it; per-tile state
        // pulled from the dispatcher snapshot.
        var (rects, imgW, imgH) = ReadTileLayout(dto.JobId, st.Mode);
        resp.ImageWidth  = imgW;
        resp.ImageHeight = imgH;

        var liveStates = Dispatcher.SnapshotTileStates(dto.JobId);
        // Terminal jobs (ready / failed / cancelled): the dispatcher has
        // retired the job and SnapshotTileStates returns empty. Synthesise
        // completed entries from TilesTotal so the UI still has something
        // to render — workerId is gone, but the rect + state is enough to
        // show a "finished" view.
        bool retired = liveStates.Count == 0
                    && st.JobState is "ready" or "failed" or "cancelled";

        int total = rects.Count > 0 ? rects.Count : st.TilesTotal;
        for (int i = 0; i < total; i++)
        {
            var rect = i < rects.Count ? rects[i] : default;
            string state;
            string? workerId;
            if (liveStates.TryGetValue(i, out var live))
            {
                state    = live.State;
                workerId = live.WorkerId;
            }
            else if (retired)
            {
                state    = st.JobState == "ready" ? "completed" : st.JobState;
                workerId = null;
            }
            else
            {
                state    = "pending";
                workerId = null;
            }
            resp.Tiles.Add(new TileMapEntryDto
            {
                TileId   = i,
                OffsetX  = rect.OffsetX,
                OffsetY  = rect.OffsetY,
                Width    = rect.Width,
                Height   = rect.Height,
                State    = state,
                WorkerId = workerId,
            });
        }
        return Ok(resp);
    }

    // ── cluster.config.* (D-5e) ─────────────────────────────────────────

    private Task<ClusterDispatchOutcome> HandleClusterConfigGetAsync(JsonElement? rawParams)
    {
        _ = rawParams; // request is parameter-less; ignore the body
        return Ok(SnapshotConfig());
    }

    private Task<ClusterDispatchOutcome> HandleClusterConfigSetAsync(JsonElement? rawParams)
    {
        ClusterConfigSetRequestDto? dto;
        try { dto = rawParams?.Deserialize<ClusterConfigSetRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        // Validate then commit atomically. Negative values are nonsense for
        // every knob; clamp instead of refusing so a slider that drifts to
        // -1 doesn't surface an error to the operator.
        if (dto.ClusterMaxJobs is int j)
            ClusterMaxJobs = Math.Max(0, j);
        if (dto.ClusterArtifactRetentionMinutes is int r)
            ClusterArtifactRetentionMinutes = Math.Max(0, r);
        if (dto.ClusterTileTargetPixels is int t)
        {
            // 0 = "use TilePlanner.DefaultTilePixels"; any positive value
            // gets clamped into the planner's accepted range so an admin
            // can't break tiling by setting 5 or 99999.
            ClusterTileTargetPixels = t <= 0 ? 0
                : Math.Clamp(t, TilePlanner.MinTilePixels, TilePlanner.MaxTilePixels);
        }

        var snap = SnapshotConfig();
        try { PersistConfig?.Invoke(snap); }
        catch (Exception ex)
        {
            // Persist failure does not undo the in-memory change — the
            // operator can hit Apply again once the underlying issue
            // (file permission, disk full) is fixed. Surface the reason
            // through the cluster log so it's visible without scraping
            // server-config.json by hand.
            _log.Event("cluster-config-persist-failed", new Dictionary<string, object?>
            {
                ["error"] = ex.Message,
            });
        }
        _log.Event("cluster-config-set", new Dictionary<string, object?>
        {
            ["maxJobs"]            = snap.ClusterMaxJobs,
            ["retentionMinutes"]   = snap.ClusterArtifactRetentionMinutes,
            ["tileTargetPixels"]   = snap.ClusterTileTargetPixels,
        });
        return Ok(snap);
    }

    /// <summary>D-5e — returns the live config as the same shape that
    /// cluster.config.get and cluster.config.set respond with.</summary>
    public ClusterConfigDto SnapshotConfig() => new()
    {
        ClusterMaxJobs                  = ClusterMaxJobs,
        ClusterArtifactRetentionMinutes = ClusterArtifactRetentionMinutes,
        ClusterTileTargetPixels         = ClusterTileTargetPixels,
    };

    /// <summary>D-5c — read tile geometry off plan.json for the given mode.
    /// Image mode returns one rect per tile + the full image dims; video
    /// and slideshow return empty rects + 0/0 dims because their tile
    /// layout has no spatial component (video = frame ranges, slideshow =
    /// per-slide PNGs). Failures (missing plan, malformed JSON) collapse
    /// to empty so the handler can still emit a counters-only response.</summary>
    private (IReadOnlyList<TileMeta> Rects, int ImageWidth, int ImageHeight) ReadTileLayout(
        string jobId, string mode)
    {
        var empty = (Rects: (IReadOnlyList<TileMeta>)Array.Empty<TileMeta>(), 0, 0);
        try
        {
            string planPath = Path.Combine(Jobs!.JobDir(jobId), "plan.json");
            if (!File.Exists(planPath)) return empty;
            using var fs = File.OpenRead(planPath);
            using var doc = JsonDocument.Parse(fs);
            int imgW = doc.RootElement.TryGetProperty("ImageWidth",  out var w) ? w.GetInt32() : 0;
            int imgH = doc.RootElement.TryGetProperty("ImageHeight", out var h) ? h.GetInt32() : 0;

            // Image mode: every tile carries an offset/render rect — read.
            // Video / slideshow: tile rects are not meaningful for a 2D
            // grid; return counters only.
            if (!string.Equals(mode, "image", StringComparison.OrdinalIgnoreCase))
                return (Array.Empty<TileMeta>(), imgW, imgH);

            if (!doc.RootElement.TryGetProperty("Tiles", out var tilesEl))
                return (Array.Empty<TileMeta>(), imgW, imgH);
            var list = new List<TileMeta>(tilesEl.GetArrayLength());
            foreach (var el in tilesEl.EnumerateArray())
            {
                list.Add(new TileMeta(
                    el.GetProperty("tileId").GetInt32(),
                    el.GetProperty("offsetX").GetInt32(),
                    el.GetProperty("offsetY").GetInt32(),
                    el.GetProperty("render").GetProperty("width").GetInt32(),
                    el.GetProperty("render").GetProperty("height").GetInt32()));
            }
            return (list, imgW, imgH);
        }
        catch { return empty; }
    }

    private WorkerSummaryDto BuildWorkerSummary(WorkerEntry w)
    {
        return new WorkerSummaryDto
        {
            WorkerId                 = w.WorkerId,
            WorkerName               = w.WorkerName,
            OsPlatform               = w.OsPlatform,
            CpuModel                 = w.CpuModel,
            LogicalCores             = w.LogicalCores,
            TotalRamBytes            = w.TotalRamBytes,
            Gpus                     = new List<string>(w.Gpus),
            MaxConcurrentTiles       = w.MaxConcurrentTiles,
            PreferredTilePixels      = w.PreferredTilePixels,
            TilesInFlight            = w.TilesInFlight,
            CpuPercent               = w.CpuPercent,
            FreeRamBytes             = w.FreeRamBytes,
            LastNote                 = w.LastNote,
            LastHeartbeatUnixSeconds = new DateTimeOffset(w.LastHeartbeatUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
            RegisteredAtUnixSeconds  = new DateTimeOffset(w.RegisteredAtUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
            Quiesced                 = w.Quiesced,
            EmaMsPerKilopixel        = w.EmaMsPerKilopixel,
            TileSamples              = w.TileSamples,
            // D-5d — capability metadata for WorkerDetailView.
            SupportedFractalTypes    = new List<string>(w.SupportedFractalTypes),
            EngineBuildSha           = w.EngineBuildSha,
            ProtocolVersion          = w.ProtocolVersion,
        };
    }

    /// <summary>D-5a — pull job summaries from the on-disk store, newest
    /// first, capped at <paramref name="limit"/>. Honours
    /// <paramref name="includeTerminal"/> and <paramref name="stateFilter"/>.
    /// Reads status.json per job; cheap relative to plan.json and avoids
    /// a second I/O for the (per-status-cached) Mode field.</summary>
    private IEnumerable<JobSummaryDto> BuildRecentJobSummaries(
        int limit, bool includeTerminal, string? stateFilter)
    {
        if (Jobs is null) yield break;

        // Build (jobId, status) pairs, sort by createdUnixMs desc, then
        // filter + cap. JobStore.ListJobIds is dir-order; we re-sort.
        var pairs = new List<(string Id, PersistedStatus St)>();
        foreach (var id in Jobs.ListJobIds())
        {
            PersistedStatus? st = null;
            try { st = Jobs.ReadStatus(id); } catch { /* skip races */ }
            if (st is null) continue;
            if (!includeTerminal && st.JobState is "ready" or "failed" or "cancelled")
                continue;
            if (stateFilter != null && !string.Equals(st.JobState, stateFilter, StringComparison.Ordinal))
                continue;
            pairs.Add((id, st));
        }
        pairs.Sort((a, b) => b.St.CreatedUnixMs.CompareTo(a.St.CreatedUnixMs));

        int taken = 0;
        foreach (var (id, st) in pairs)
        {
            if (taken++ >= limit) yield break;
            double pct = st.TilesTotal == 0 ? 0
                       : 100.0 * st.TilesDone / st.TilesTotal;
            yield return new JobSummaryDto
            {
                JobId            = id,
                JobState         = st.JobState,
                Mode             = st.Mode,
                TilesTotal       = st.TilesTotal,
                TilesDone        = st.TilesDone,
                TilesInFlight    = st.TilesInFlight,
                TotalFrames      = st.TotalFrames,
                FramesDone       = st.FramesDone,
                EncodedFrames    = st.EncodedFrames,
                ProgressPercent  = pct,
                CreatedUnixMs    = st.CreatedUnixMs,
                LastUpdateUnixMs = st.LastUpdateUnixMs,
                ArtifactBytes    = st.ArtifactBytes,
                ArtifactExt      = st.ArtifactExt,
                FailReason       = st.FailReason,
            };
        }
    }

    // ── crash recovery (D-6a) ───────────────────────────────────────────

    /// <summary>D-6a — rebuild dispatcher + merger state for any non-
    /// terminal job left on disk by a previous master process. Image jobs
    /// resume: every tile already on disk is replayed into a fresh
    /// merger, the remaining tiles re-enqueue into the dispatcher under
    /// their original ids, and status returns to <c>queued</c> (or
    /// <c>rendering</c> if some tiles were already done). Video and
    /// slideshow jobs fall back to <see cref="JobStore.FailInflightAfterRestart"/>
    /// behaviour — their tile streams (frame ranges, slide indices) are
    /// not yet rebuildable. Caller invokes once before the master starts
    /// accepting connections; returns counts for logging.</summary>
    public ResumeCounts RecoverFromDisk()
    {
        var counts = new ResumeCounts();
        if (Jobs is null || Dispatcher is null) return counts;

        foreach (var rec in Jobs.EnumerateResumableJobs())
        {
            counts.Considered++;
            try
            {
                if (string.Equals(rec.Status.Mode, "image", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryResumeImageJob(rec)) counts.ResumedImage++;
                    else { FailResumeJob(rec.JobId, "resume-image-failed"); counts.Failed++; }
                }
                else
                {
                    FailResumeJob(rec.JobId, "master-restart");
                    counts.FailedUnsupportedMode++;
                }
            }
            catch (Exception ex)
            {
                FailResumeJob(rec.JobId, "resume-error: " + ex.Message);
                counts.Failed++;
                _log.Event("job-resume-failed", new Dictionary<string, object?>
                {
                    ["jobId"] = rec.JobId,
                    ["error"] = ex.Message,
                });
            }
        }
        return counts;
    }

    /// <summary>D-6a — counts returned by <see cref="RecoverFromDisk"/>.
    /// Host logs these so the operator can see at a glance whether the
    /// restart preserved any inflight work.</summary>
    public struct ResumeCounts
    {
        public int Considered;
        public int ResumedImage;
        public int FailedUnsupportedMode;
        public int Failed;
    }

    private bool TryResumeImageJob(ResumeRecord rec)
    {
        if (Codec is null) return false;
        var plan = ReadPlan(rec.JobId);
        if (plan is null || plan.Count == 0) return false;

        int width  = plan[0].OffsetX + plan[0].Width;
        int height = plan[0].OffsetY + plan[0].Height;
        foreach (var t in plan)
        {
            if (t.OffsetX + t.Width  > width)  width  = t.OffsetX + t.Width;
            if (t.OffsetY + t.Height > height) height = t.OffsetY + t.Height;
        }

        var merger = new ArtifactMerger(width, height, plan.Count, Codec);

        var onDisk = Jobs!.ListTilesOnDisk(rec.JobId);
        var doneSet = new HashSet<int>(onDisk);
        int replayed = 0;
        foreach (var tileId in onDisk)
        {
            if (tileId < 0 || tileId >= plan.Count) continue;
            var meta = plan[tileId];
            if (!Jobs.TryReadTileBytes(rec.JobId, tileId, out var bytes)) continue;
            try
            {
                bool merged = IsPng(bytes)
                    ? merger.TryMergePngTile (tileId, meta.OffsetX, meta.OffsetY, meta.Width, meta.Height, bytes)
                    : merger.TryMergeRgbaTile(tileId, meta.OffsetX, meta.OffsetY, meta.Width, meta.Height, bytes);
                if (merged) replayed++;
            }
            catch
            {
                // Corrupt on-disk tile: drop it from the done set so the
                // dispatcher re-renders it. Better than failing the whole
                // job because one .bin got truncated mid-write.
                doneSet.Remove(tileId);
            }
        }

        var remaining = new List<TileJobDto>(plan.Count - doneSet.Count);
        var planTiles = ReadPlanTileDtos(rec.JobId);
        if (planTiles is null) return false;
        foreach (var t in planTiles)
            if (!doneSet.Contains(t.TileId)) remaining.Add(t);

        _mergers[rec.JobId] = merger;

        if (remaining.Count > 0)
        {
            Dispatcher!.EnqueueJob(rec.JobId, remaining);
        }

        Jobs.UpdateStatus(rec.JobId, s =>
        {
            s.TilesDone     = replayed;
            s.TilesInFlight = 0;
            s.JobState      = replayed > 0 ? "rendering" : "queued";
            s.FailReason    = null;
        });
        Jobs.AppendEvent(rec.JobId, "resumed", new Dictionary<string, object?>
        {
            ["replayed"]  = replayed,
            ["remaining"] = remaining.Count,
            ["total"]     = plan.Count,
        });
        _log.Event("job-resumed", new Dictionary<string, object?>
        {
            ["jobId"]     = rec.JobId,
            ["replayed"]  = replayed,
            ["remaining"] = remaining.Count,
            ["total"]     = plan.Count,
        });

        if (replayed == plan.Count)
            FinaliseMerge(rec.JobId, merger);
        return true;
    }

    private void FailResumeJob(string jobId, string reason)
    {
        try
        {
            Jobs!.UpdateStatus(jobId, s =>
            {
                s.JobState   = "failed";
                s.FailReason = reason;
            });
            Jobs.AppendEvent(jobId, "failed-on-restart", new Dictionary<string, object?>
            {
                ["reason"] = reason,
            });
        }
        catch { /* status file may be corrupt; best effort */ }
        _log.Event("job-resume-skipped", new Dictionary<string, object?>
        {
            ["jobId"]  = jobId,
            ["reason"] = reason,
        });
    }

    private static bool IsPng(byte[] bytes)
    {
        if (bytes.Length < 8) return false;
        return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
    }

    /// <summary>D-6a — read plan.json and return the full TileJobDto list
    /// so resumed tiles can be re-enqueued with their original render
    /// parameters (centre, zoom, theme, region). Returns null on any
    /// parse failure — caller treats that as "cannot resume".</summary>
    private IReadOnlyList<TileJobDto>? ReadPlanTileDtos(string jobId)
    {
        try
        {
            string planPath = Path.Combine(Jobs!.JobDir(jobId), "plan.json");
            if (!File.Exists(planPath)) return null;
            using var fs = File.OpenRead(planPath);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("Tiles", out var tilesEl)) return null;
            var list = new List<TileJobDto>(tilesEl.GetArrayLength());
            foreach (var el in tilesEl.EnumerateArray())
            {
                var dto = el.Deserialize<TileJobDto>(JsonRpcFraming.JsonOpts);
                if (dto is null) return null;
                dto.JobId = jobId;
                list.Add(dto);
            }
            return list;
        }
        catch { return null; }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private IReadOnlyList<TileMeta>? ReadPlan(string jobId)
    {
        try
        {
            string planPath = Path.Combine(Jobs!.JobDir(jobId), "plan.json");
            if (!File.Exists(planPath)) return null;
            using var fs = File.OpenRead(planPath);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("Tiles", out var tilesEl)) return null;
            var list = new List<TileMeta>(tilesEl.GetArrayLength());
            foreach (var el in tilesEl.EnumerateArray())
            {
                list.Add(new TileMeta(
                    el.GetProperty("tileId").GetInt32(),
                    el.GetProperty("offsetX").GetInt32(),
                    el.GetProperty("offsetY").GetInt32(),
                    el.GetProperty("render").GetProperty("width").GetInt32(),
                    el.GetProperty("render").GetProperty("height").GetInt32()));
            }
            return list;
        }
        catch { return null; }
    }

    private readonly record struct TileMeta(int TileId, int OffsetX, int OffsetY, int Width, int Height);

    /// <summary>D-4 — for video jobs, parse plan.json and return per-tile
    /// frame ranges. Null entries indicate image-mode tiles in a mixed
    /// or malformed plan (shouldn't occur — plan.Mode gates this).</summary>
    private IReadOnlyList<FrameRangeDto?>? ReadFramePlan(string jobId)
    {
        try
        {
            string planPath = Path.Combine(Jobs!.JobDir(jobId), "plan.json");
            if (!File.Exists(planPath)) return null;
            using var fs = File.OpenRead(planPath);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("Tiles", out var tilesEl)) return null;
            var list = new List<FrameRangeDto?>(tilesEl.GetArrayLength());
            foreach (var el in tilesEl.EnumerateArray())
            {
                if (!el.TryGetProperty("frameRange", out var fr) || fr.ValueKind == JsonValueKind.Null)
                {
                    list.Add(null);
                    continue;
                }
                list.Add(new FrameRangeDto
                {
                    StartFrame   = fr.GetProperty("startFrame").GetInt32(),
                    EndFrame     = fr.GetProperty("endFrame").GetInt32(),
                    TotalFrames  = fr.GetProperty("totalFrames").GetInt32(),
                    Fps          = fr.GetProperty("fps").GetInt32(),
                    LogStartZoom = fr.GetProperty("logStartZoom").GetDouble(),
                    LogZoomDelta = fr.GetProperty("logZoomDelta").GetDouble(),
                });
            }
            return list;
        }
        catch { return null; }
    }

    private static string ComputeFileSha256Base64(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(fs));
    }

    private bool IsAcceptedProtocol(string v)
    {
        foreach (var accepted in AcceptedProtocolVersions)
            if (string.Equals(accepted, v, StringComparison.Ordinal)) return true;
        return false;
    }

    private static Task<ClusterDispatchOutcome> Ok(object result)
        => Task.FromResult(ClusterDispatchOutcome.Ok(result));

    private static Task<ClusterDispatchOutcome> Err(string code, string msg)
        => Task.FromResult(ClusterDispatchOutcome.Err(code, msg));
}
