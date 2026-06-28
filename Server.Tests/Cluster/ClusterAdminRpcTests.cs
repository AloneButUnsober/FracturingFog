// Server.Tests/Cluster/ClusterAdminRpcTests.cs
// D-5a — covers the admin-only cluster.* RPCs: cluster.status,
// cluster.quiesceWorker, cluster.killWorker, cluster.listJobs. Skips the
// FFServer role gate (FFServer tests cover that separately); these
// exercise the coordinator handler logic directly.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class ClusterAdminRpcTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly TileDispatcher _disp;
    private readonly ClusterCoordinator _coord;

    public ClusterAdminRpcTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), $"ff-admin-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logDir);
        _log      = new ClusterLogger(_logDir);
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _jobs     = new JobStore(Path.Combine(_root, "jobs"));
        _disp     = new TileDispatcher();
        _coord    = new ClusterCoordinator(_registry, _log)
        {
            Jobs       = _jobs,
            Dispatcher = _disp,
            EngineBuildSha = "",
            TileNextHold = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement ToParams(object payload)
        => JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts);

    private async Task<string> RegisterWorkerAsync(string name, string thumb)
    {
        var dto = new WorkerRegisterDto
        {
            WorkerName = name,
            OsPlatform = "test",
            LogicalCores = 4,
            TotalRamBytes = 16L * 1024 * 1024 * 1024,
            ProtocolVersion = "1",
            EngineBuildSha = "",
            PreferredTilePixels = 256,
            MaxConcurrentTiles = 2,
        };
        var outcome = await _coord.HandleAsync("worker.register",
            ToParams(dto), CertRole.Worker, thumb, CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var ack = Assert.IsType<WorkerRegisterAckDto>(outcome.Result);
        return ack.WorkerId;
    }

    private void SeedJob(string mode, string state, int tilesTotal, int tilesDone)
    {
        // Build a minimal plan that matches what TilePlanner emits for the
        // mode under test — we only need the cached counters, no actual
        // tile geometry, so a hand-rolled Plan is fine. Plan.TileCount is
        // derived from Tiles.Count so fill the list with placeholder DTOs.
        var tiles = new System.Collections.Generic.List<TileJobDto>(tilesTotal);
        for (int i = 0; i < tilesTotal; i++)
            tiles.Add(new TileJobDto
            {
                TileId      = i,
                ImageWidth  = 64,
                ImageHeight = 64,
                Render      = new RenderRequestDto { Width = 64, Height = 64 },
            });
        var plan = new TilePlanner.Plan
        {
            Mode             = mode,
            ImageWidth       = 64,
            ImageHeight      = 64,
            TileTargetPixels = 64,
            Columns          = 1,
            Rows             = tilesTotal,
            TotalFrames      = mode == "video" ? tilesTotal : 0,
            Tiles            = tiles,
        };
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = mode,
                FractalType = "Mandelbrot",
                Width  = 64,
                Height = 64,
            },
        };
        string id = JobStore.NewJobId();
        _jobs.Create(id, submit, plan);
        _jobs.UpdateStatus(id, s =>
        {
            s.JobState  = state;
            s.TilesDone = tilesDone;
        });
    }

    // ── cluster.status ──────────────────────────────────────────────────

    [Fact]
    public async Task ClusterStatus_Includes_Workers_And_Recent_Jobs()
    {
        await RegisterWorkerAsync("worker-A", "AA");
        await RegisterWorkerAsync("worker-B", "BB");
        SeedJob("image", "ready",     4, 4);
        SeedJob("video", "rendering", 3, 1);

        var outcome = await _coord.HandleAsync("cluster.status",
            ToParams(new ClusterStatusRequestDto()),
            CertRole.Admin, thumbprint: "X", CancellationToken.None);

        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<ClusterStatusDto>(outcome.Result);

        Assert.Equal(5, resp.HeartbeatIntervalSeconds);
        Assert.Equal(2, resp.LiveWorkerCount);
        Assert.Equal(2, resp.Workers.Count);
        Assert.Contains(resp.Workers, w => w.WorkerName == "worker-A");
        Assert.Contains(resp.Workers, w => w.WorkerName == "worker-B");

        Assert.Equal(2, resp.Jobs.Count);
        Assert.Contains(resp.Jobs, j => j.Mode == "image" && j.JobState == "ready");
        Assert.Contains(resp.Jobs, j => j.Mode == "video" && j.JobState == "rendering");
    }

    [Fact]
    public async Task ClusterStatus_Empty_State_Returns_Zero_Workers_Zero_Jobs()
    {
        var outcome = await _coord.HandleAsync("cluster.status",
            ToParams(new ClusterStatusRequestDto()),
            CertRole.Admin, thumbprint: "X", CancellationToken.None);

        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<ClusterStatusDto>(outcome.Result);
        Assert.Empty(resp.Workers);
        Assert.Empty(resp.Jobs);
    }

    // ── cluster.quiesceWorker ───────────────────────────────────────────

    [Fact]
    public async Task QuiesceWorker_Sets_Then_Clears_Flag()
    {
        string id = await RegisterWorkerAsync("worker-Q", "QQ");

        var set = await _coord.HandleAsync("cluster.quiesceWorker",
            ToParams(new WorkerQuiesceRequestDto { WorkerId = id, Quiesced = true }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(set.ErrorCode);
        var setAck = Assert.IsType<WorkerQuiesceAckDto>(set.Result);
        Assert.False(setAck.PreviousState);
        Assert.True(setAck.CurrentState);
        Assert.True(_registry.Snapshot().Single().Quiesced);

        var clear = await _coord.HandleAsync("cluster.quiesceWorker",
            ToParams(new WorkerQuiesceRequestDto { WorkerId = id, Quiesced = false }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(clear.ErrorCode);
        var clearAck = Assert.IsType<WorkerQuiesceAckDto>(clear.Result);
        Assert.True(clearAck.PreviousState);
        Assert.False(clearAck.CurrentState);
        Assert.False(_registry.Snapshot().Single().Quiesced);
    }

    [Fact]
    public async Task QuiesceWorker_Unknown_Worker_Refused()
    {
        var outcome = await _coord.HandleAsync("cluster.quiesceWorker",
            ToParams(new WorkerQuiesceRequestDto { WorkerId = "not-real", Quiesced = true }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Equal("unknown-worker", outcome.ErrorCode);
    }

    // ── cluster.killWorker ──────────────────────────────────────────────

    [Fact]
    public async Task KillWorker_Removes_Entry_And_Is_Idempotent()
    {
        string id = await RegisterWorkerAsync("worker-K", "KK");

        var first = await _coord.HandleAsync("cluster.killWorker",
            ToParams(new WorkerKillRequestDto { WorkerId = id }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(first.ErrorCode);
        var firstAck = Assert.IsType<WorkerKillAckDto>(first.Result);
        Assert.True(firstAck.Removed);
        Assert.Empty(_registry.Snapshot());

        // Second kill: idempotent — Removed=false but no error.
        var second = await _coord.HandleAsync("cluster.killWorker",
            ToParams(new WorkerKillRequestDto { WorkerId = id }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(second.ErrorCode);
        var secondAck = Assert.IsType<WorkerKillAckDto>(second.Result);
        Assert.False(secondAck.Removed);
    }

    // ── cluster.listJobs ────────────────────────────────────────────────

    [Fact]
    public async Task ListJobs_Returns_Newest_First_With_Total_Count()
    {
        SeedJob("image", "ready",     4, 4);
        // Slight delay so CreatedUnixMs distinguishes the rows ordering.
        await Task.Delay(5);
        SeedJob("video", "rendering", 3, 1);
        await Task.Delay(5);
        SeedJob("image", "failed",    2, 0);

        var outcome = await _coord.HandleAsync("cluster.listJobs",
            ToParams(new JobListRequestDto { Limit = 10, IncludeTerminal = true }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobListDto>(outcome.Result);
        Assert.Equal(3, resp.TotalCount);
        Assert.Equal(3, resp.Jobs.Count);
        // Newest first.
        Assert.Equal("failed",    resp.Jobs[0].JobState);
        Assert.Equal("rendering", resp.Jobs[1].JobState);
        Assert.Equal("ready",     resp.Jobs[2].JobState);
    }

    [Fact]
    public async Task ListJobs_Excludes_Terminal_When_Requested()
    {
        SeedJob("image", "ready",     4, 4);
        SeedJob("video", "rendering", 3, 1);
        SeedJob("image", "failed",    2, 0);
        SeedJob("image", "cancelled", 2, 0);

        var outcome = await _coord.HandleAsync("cluster.listJobs",
            ToParams(new JobListRequestDto { IncludeTerminal = false }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobListDto>(outcome.Result);
        Assert.Equal(4, resp.TotalCount);
        Assert.Single(resp.Jobs);
        Assert.Equal("rendering", resp.Jobs[0].JobState);
    }

    [Fact]
    public async Task ListJobs_StateFilter_Matches_Exact_State()
    {
        SeedJob("image", "ready",  4, 4);
        SeedJob("image", "ready",  4, 4);
        SeedJob("image", "failed", 2, 0);

        var outcome = await _coord.HandleAsync("cluster.listJobs",
            ToParams(new JobListRequestDto { StateFilter = "failed" }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobListDto>(outcome.Result);
        Assert.Single(resp.Jobs);
        Assert.Equal("failed", resp.Jobs[0].JobState);
    }

    [Fact]
    public async Task ListJobs_Limit_Caps_Returned_Rows()
    {
        for (int i = 0; i < 5; i++) SeedJob("image", "ready", 1, 1);

        var outcome = await _coord.HandleAsync("cluster.listJobs",
            ToParams(new JobListRequestDto { Limit = 2 }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobListDto>(outcome.Result);
        Assert.Equal(5, resp.TotalCount);
        Assert.Equal(2, resp.Jobs.Count);
    }

    // ── cluster.jobTileMap (D-5c) ───────────────────────────────────────

    [Fact]
    public async Task JobTileMap_Unknown_Job_Returns_Error()
    {
        var outcome = await _coord.HandleAsync("cluster.jobTileMap",
            ToParams(new JobTileMapRequestDto { JobId = "nope" }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Equal("unknown-job", outcome.ErrorCode);
    }

    [Fact]
    public async Task JobTileMap_Image_Job_Returns_Rect_Per_Tile_With_Image_Dims()
    {
        string jobId = SeedJobReturningId("image", "rendering", tilesTotal: 4, tilesDone: 0);

        var outcome = await _coord.HandleAsync("cluster.jobTileMap",
            ToParams(new JobTileMapRequestDto { JobId = jobId }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);

        var resp = Assert.IsType<JobTileMapDto>(outcome.Result);
        Assert.Equal(jobId, resp.JobId);
        Assert.Equal("image", resp.Mode);
        Assert.Equal(64, resp.ImageWidth);
        Assert.Equal(64, resp.ImageHeight);
        Assert.Equal(4, resp.Tiles.Count);
        Assert.All(resp.Tiles, t => Assert.Equal("pending", t.State));
        Assert.All(resp.Tiles, t => Assert.Null(t.WorkerId));
    }

    [Fact]
    public async Task JobTileMap_InFlight_And_Completed_Tiles_Carry_WorkerId()
    {
        string jobId = SeedJobReturningId("image", "rendering", tilesTotal: 3, tilesDone: 0);

        // Push tiles into the dispatcher so SnapshotTileStates has data.
        var tiles = new System.Collections.Generic.List<TileJobDto>();
        for (int i = 0; i < 3; i++)
            tiles.Add(new TileJobDto
            {
                JobId  = jobId,
                TileId = i,
                Render = new RenderRequestDto { Width = 16, Height = 16 },
            });
        _disp.EnqueueJob(jobId, tiles);

        // Two tiles claimed by worker-A; first one delivered.
        var t0 = await _disp.ClaimNextAsync("worker-A", TimeSpan.FromSeconds(1), CancellationToken.None);
        var t1 = await _disp.ClaimNextAsync("worker-A", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(t0);
        Assert.NotNull(t1);
        Assert.True(_disp.AcceptDelivery(jobId, t0!.TileId, "worker-A"));

        var outcome = await _coord.HandleAsync("cluster.jobTileMap",
            ToParams(new JobTileMapRequestDto { JobId = jobId }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobTileMapDto>(outcome.Result);
        Assert.Equal(3, resp.Tiles.Count);

        var completed = resp.Tiles.Single(t => t.State == "completed");
        Assert.Equal("worker-A", completed.WorkerId);

        var inflight = resp.Tiles.Where(t => t.State == "inflight").ToList();
        Assert.Single(inflight);
        Assert.Equal("worker-A", inflight[0].WorkerId);

        var pending = resp.Tiles.Where(t => t.State == "pending").ToList();
        Assert.Single(pending);
        Assert.Null(pending[0].WorkerId);
    }

    [Fact]
    public async Task JobTileMap_Terminal_Job_Synthesises_Completed_From_TilesTotal()
    {
        string jobId = SeedJobReturningId("image", "ready", tilesTotal: 5, tilesDone: 5);

        var outcome = await _coord.HandleAsync("cluster.jobTileMap",
            ToParams(new JobTileMapRequestDto { JobId = jobId }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        var resp = Assert.IsType<JobTileMapDto>(outcome.Result);
        Assert.Equal("ready", resp.JobState);
        Assert.Equal(5, resp.Tiles.Count);
        // No dispatcher state for a job we never enqueued + terminal status
        // → handler falls through to the retired branch and synthesises
        // "completed" for every tile so the UI shows a finished grid.
        Assert.All(resp.Tiles, t => Assert.Equal("completed", t.State));
        Assert.All(resp.Tiles, t => Assert.Null(t.WorkerId));
    }

    [Fact]
    public async Task JobTileMap_Video_Job_Emits_Counters_Without_Rects()
    {
        string jobId = SeedJobReturningId("video", "rendering", tilesTotal: 4, tilesDone: 0);

        var outcome = await _coord.HandleAsync("cluster.jobTileMap",
            ToParams(new JobTileMapRequestDto { JobId = jobId }),
            CertRole.Admin, "X", CancellationToken.None);
        Assert.Null(outcome.ErrorCode);

        var resp = Assert.IsType<JobTileMapDto>(outcome.Result);
        Assert.Equal("video", resp.Mode);
        // Video tiles have no spatial layout — handler reports image dims
        // off the plan but skips per-tile rects.
        Assert.Equal(64, resp.ImageWidth);
        Assert.Equal(64, resp.ImageHeight);
        Assert.Equal(4, resp.Tiles.Count);
        Assert.All(resp.Tiles, t =>
        {
            Assert.Equal(0, t.Width);
            Assert.Equal(0, t.Height);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private string SeedJobReturningId(string mode, string state, int tilesTotal, int tilesDone)
    {
        var tiles = new System.Collections.Generic.List<TileJobDto>(tilesTotal);
        for (int i = 0; i < tilesTotal; i++)
            tiles.Add(new TileJobDto
            {
                TileId      = i,
                ImageWidth  = 64,
                ImageHeight = 64,
                OffsetX     = (i % 2) * 32,
                OffsetY     = (i / 2) * 32,
                Render      = new RenderRequestDto { Width = 32, Height = 32 },
            });
        var plan = new TilePlanner.Plan
        {
            Mode             = mode,
            ImageWidth       = 64,
            ImageHeight      = 64,
            TileTargetPixels = 1024,
            Columns          = 2,
            Rows             = (tilesTotal + 1) / 2,
            TotalFrames      = mode == "video" ? tilesTotal : 0,
            Tiles            = tiles,
        };
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = mode,
                FractalType = "Mandelbrot",
                Width  = 64,
                Height = 64,
            },
        };
        string id = JobStore.NewJobId();
        _jobs.Create(id, submit, plan);
        _jobs.UpdateStatus(id, s =>
        {
            s.JobState  = state;
            s.TilesDone = tilesDone;
        });
        return id;
    }
}
