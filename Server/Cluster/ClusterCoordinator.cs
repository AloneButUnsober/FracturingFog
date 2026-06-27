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

    public WorkerRegistry  Registry   { get; }
    public JobStore?       Jobs       { get; init; }
    public TileDispatcher? Dispatcher { get; init; }
    public IClusterImageCodec? Codec { get; init; }

    private readonly ClusterLogger _log;
    private readonly ConcurrentDictionary<string, ArtifactMerger> _mergers =
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
        if (Dispatcher is null || Jobs is null || Codec is null)
            return Err("not-configured", "master has no dispatcher/jobs/codec");

        TileDeliverDto? dto;
        try { dto = rawParams?.Deserialize<TileDeliverDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex) { return Err("bad-request", $"invalid params: {ex.Message}"); }
        if (dto is null) return Err("bad-request", "missing params");

        string normThumb = ServerCertLoader.NormalizeThumbprint(thumbprint);
        if (Registry.Lookup(dto.WorkerId, normThumb, out string? err) is null)
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

        Dispatcher.AcceptDelivery(dto.JobId, dto.TileId);

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

        if (!string.Equals(dto.Request.Mode, "image", StringComparison.OrdinalIgnoreCase))
            return Err("unsupported-mode",
                $"D-2 supports image mode only; got '{dto.Request.Mode}'. Video lands in D-4.");

        if (!TilePlanner.ValidateForTiling(dto.Request.FractalType, out string? whyNot))
            return Err("untileable-fractal", whyNot!);

        TilePlanner.Plan plan;
        try
        {
            var workerHints = new List<int>();
            foreach (var w in Registry.Snapshot())
                if (w.PreferredTilePixels > 0) workerHints.Add(w.PreferredTilePixels);
            plan = TilePlanner.PlanImage(dto.Request, dto.TilePixelsHint, workerHints);
        }
        catch (Exception ex) { return Err("plan-failed", ex.Message); }

        string jobId = JobStore.NewJobId();
        foreach (var t in plan.Tiles) t.JobId = jobId;

        try { Jobs.Create(jobId, dto, plan); }
        catch (Exception ex) { return Err("internal", $"persist failed: {ex.Message}"); }

        if (Codec is null)
            return Err("not-configured", "master has no image codec wired");

        var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, Codec);
        _mergers[jobId] = merger;

        Dispatcher.EnqueueJob(jobId, plan.Tiles);
        Jobs.UpdateStatus(jobId, s => s.JobState = "planning");
        Jobs.AppendEvent(jobId, "submitted", new Dictionary<string, object?>
        {
            ["tiles"] = plan.TileCount,
            ["w"]     = plan.ImageWidth,
            ["h"]     = plan.ImageHeight,
        });

        _log.Event("job-submitted", new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["tiles"] = plan.TileCount,
            ["w"]     = plan.ImageWidth,
            ["h"]     = plan.ImageHeight,
        });

        // Best-effort artifact-size estimate: 0.25 bytes/pixel for PNG of
        // a typical Mandelbrot image. Overestimate is fine; the client
        // uses it only to gate disk free.
        long estBytes = (long)plan.ImageWidth * plan.ImageHeight / 4;

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
            _log.Event("job-cancelled", new Dictionary<string, object?>
            {
                ["jobId"] = dto.JobId,
                ["prev"]  = st.JobState,
            });
        }
        return Ok(ack);
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
