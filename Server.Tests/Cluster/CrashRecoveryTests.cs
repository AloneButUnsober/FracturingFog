// Server.Tests/Cluster/CrashRecoveryTests.cs
// D-6a — ClusterCoordinator.RecoverFromDisk replays inflight image jobs
// after a simulated master restart and falls back to fail-on-restart
// for video / slideshow whose tile streams cannot be replayed yet.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class CrashRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly TileDispatcher _disp;
    private readonly RawHeaderCodec _codec = new();
    private readonly ClusterCoordinator _coord;

    public CrashRecoveryTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), $"ff-recover-{Guid.NewGuid():N}");
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
            Codec      = _codec,
            EngineBuildSha = "",
            TileNextHold = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SeedImageJob(int width, int height, int target, string state, int[] tilesOnDisk)
    {
        var req = new RenderRequestDto
        {
            Mode = "image", FractalType = "Mandelbrot",
            Width = width, Height = height,
            CenterX = -0.75, CenterY = 0, Zoom = 1.0,
        };
        var plan = TilePlanner.PlanImage(req, target);
        var submit = new JobSubmitDto { Request = req, TilePixelsHint = target };
        string id = JobStore.NewJobId();
        _jobs.Create(id, submit, plan);
        _jobs.UpdateStatus(id, s => s.JobState = state);
        foreach (int tileId in tilesOnDisk)
        {
            var t = plan.Tiles[tileId];
            // Raw BGRA: TryMergeRgbaTile validates length == w*h*4. Bytes
            // are arbitrary (recovery just pastes them into the merger).
            int len = t.Render.Width * t.Render.Height * 4;
            var bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = (byte)((tileId + 1) & 0xFF);
            _jobs.WriteTileBytes(id, tileId, bytes);
        }
        return id;
    }

    [Fact]
    public void Empty_Store_Is_NoOp()
    {
        var counts = _coord.RecoverFromDisk();
        Assert.Equal(0, counts.Considered);
        Assert.Equal(0, counts.ResumedImage);
    }

    [Fact]
    public void Terminal_Jobs_Are_Not_Touched()
    {
        string id = SeedImageJob(64, 64, 64, "ready", Array.Empty<int>());
        _jobs.UpdateStatus(id, s => { s.ArtifactExt = "png"; s.ArtifactBytes = 1; });

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(0, counts.Considered);
        Assert.Equal("ready", _jobs.ReadStatus(id)!.JobState);
        Assert.False(_disp.KnowsJob(id));
    }

    [Fact]
    public void Image_Job_With_No_Tiles_Done_Re_Enqueues_All_Pending()
    {
        // 128×64 with 64-px target → 2 tiles, neither delivered.
        string id = SeedImageJob(128, 64, 64, "rendering", Array.Empty<int>());

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(1, counts.Considered);
        Assert.Equal(1, counts.ResumedImage);
        Assert.True(_disp.KnowsJob(id));
        Assert.Equal(2, _disp.PendingCount(id));

        var st = _jobs.ReadStatus(id)!;
        Assert.Equal("queued", st.JobState);
        Assert.Equal(0, st.TilesDone);
        Assert.Equal(0, st.TilesInFlight);
        Assert.Null(st.FailReason);
    }

    [Fact]
    public void Image_Job_With_Some_Tiles_Done_Re_Enqueues_Remainder()
    {
        // 192×64 with 64-px target → 3 tiles; tile 1 already on disk.
        string id = SeedImageJob(192, 64, 64, "rendering", new[] { 1 });

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(1, counts.ResumedImage);
        Assert.Equal(2, _disp.PendingCount(id));

        var st = _jobs.ReadStatus(id)!;
        Assert.Equal("rendering", st.JobState);
        Assert.Equal(1, st.TilesDone);
    }

    [Fact]
    public void Image_Job_With_All_Tiles_Done_Finalises_Immediately()
    {
        // 128×64 with 64-px target → 2 tiles; both on disk.
        string id = SeedImageJob(128, 64, 64, "rendering", new[] { 0, 1 });

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(1, counts.ResumedImage);
        Assert.False(_disp.KnowsJob(id));   // dispatcher retired on finalise

        var st = _jobs.ReadStatus(id)!;
        Assert.Equal("ready", st.JobState);
        Assert.Equal("png", st.ArtifactExt);
        Assert.True(st.ArtifactBytes > 0);
        Assert.True(File.Exists(_jobs.ArtifactPath(id, "png")));
    }

    [Fact]
    public void Video_Job_Falls_Back_To_Failed()
    {
        // Hand-build a minimal video plan; FramePlanner needs more setup
        // than belongs in this test and we only care that the resume
        // path refuses video.
        var tiles = new List<TileJobDto>
        {
            new() { TileId = 0, ImageWidth = 64, ImageHeight = 64,
                    Render = new RenderRequestDto { Width = 64, Height = 64 } },
        };
        var plan = new TilePlanner.Plan
        {
            Mode = "video",
            ImageWidth = 64, ImageHeight = 64,
            TileTargetPixels = 64, Columns = 1, Rows = 1,
            TotalFrames = 1,
            Tiles = tiles,
        };
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto { Mode = "video", FractalType = "Mandelbrot", Width = 64, Height = 64 },
        };
        string id = JobStore.NewJobId();
        _jobs.Create(id, submit, plan);
        _jobs.UpdateStatus(id, s => s.JobState = "rendering");

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(1, counts.Considered);
        Assert.Equal(0, counts.ResumedImage);
        Assert.Equal(1, counts.FailedUnsupportedMode);

        var st = _jobs.ReadStatus(id)!;
        Assert.Equal("failed", st.JobState);
        Assert.Equal("master-restart", st.FailReason);
    }

    [Fact]
    public void Corrupt_On_Disk_Tile_Bytes_Re_Enqueue_That_Tile()
    {
        // 128×64 → 2 tiles. Tile 0 has wrong-length bytes (corrupt).
        var req = new RenderRequestDto
        {
            Mode = "image", FractalType = "Mandelbrot",
            Width = 128, Height = 64,
            CenterX = -0.75, CenterY = 0, Zoom = 1.0,
        };
        var plan = TilePlanner.PlanImage(req, 64);
        var submit = new JobSubmitDto { Request = req, TilePixelsHint = 64 };
        string id = JobStore.NewJobId();
        _jobs.Create(id, submit, plan);
        _jobs.UpdateStatus(id, s => s.JobState = "rendering");
        // Tile 0: bytes that are neither PNG nor a valid raw BGRA length.
        _jobs.WriteTileBytes(id, 0, new byte[] { 1, 2, 3, 4 });
        // Tile 1: valid raw BGRA (length = 64*64*4).
        var good = new byte[64 * 64 * 4];
        _jobs.WriteTileBytes(id, 1, good);

        var counts = _coord.RecoverFromDisk();
        Assert.Equal(1, counts.ResumedImage);

        var st = _jobs.ReadStatus(id)!;
        Assert.Equal(1, st.TilesDone);   // only the good one replayed
        Assert.Equal(1, _disp.PendingCount(id));   // bad one re-enqueued
    }
}
