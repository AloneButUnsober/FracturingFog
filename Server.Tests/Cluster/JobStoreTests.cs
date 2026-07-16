// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JobStore _store;

    public JobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ff-jobstore-test-{Guid.NewGuid():N}");
        _store = new JobStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (JobSubmitDto, TilePlanner.Plan) BuildSubmit(int w = 1024, int h = 512)
    {
        var req = new RenderRequestDto
        {
            Mode = "image", FractalType = "Mandelbrot",
            Width = w, Height = h,
            CenterX = -0.75, CenterY = 0, Zoom = 1.0,
        };
        var plan = TilePlanner.PlanImage(req, 512);
        return (new JobSubmitDto { Request = req, TilePixelsHint = 512 }, plan);
    }

    [Fact]
    public void NewJobId_Produces_Distinct_Crockford_Strings()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < 64; i++) Assert.True(seen.Add(JobStore.NewJobId()));
        foreach (var id in seen)
        {
            Assert.InRange(id.Length, 25, 27);
            foreach (var c in id)
                Assert.Contains(c, "0123456789ABCDEFGHJKMNPQRSTVWXYZ");
        }
    }

    [Fact]
    public void Create_Persists_Request_Plan_Status_And_Event()
    {
        var id = JobStore.NewJobId();
        var (submit, plan) = BuildSubmit();
        _store.Create(id, submit, plan);

        Assert.True(File.Exists(Path.Combine(_store.JobDir(id), "request.json")));
        Assert.True(File.Exists(Path.Combine(_store.JobDir(id), "plan.json")));
        Assert.True(File.Exists(Path.Combine(_store.JobDir(id), "status.json")));
        Assert.True(File.Exists(Path.Combine(_store.JobDir(id), "events.ndjson")));

        var st = _store.ReadStatus(id);
        Assert.NotNull(st);
        Assert.Equal("queued", st!.JobState);
        Assert.Equal(plan.TileCount, st.TilesTotal);
        Assert.Equal(0, st.TilesDone);
    }

    [Fact]
    public void UpdateStatus_Atomically_Rewrites_Status_File()
    {
        var id = JobStore.NewJobId();
        var (s, p) = BuildSubmit();
        _store.Create(id, s, p);

        _store.UpdateStatus(id, st => { st.JobState = "rendering"; st.TilesDone = 1; });
        var read = _store.ReadStatus(id);
        Assert.Equal("rendering", read!.JobState);
        Assert.Equal(1, read.TilesDone);
        Assert.False(File.Exists(Path.Combine(_store.JobDir(id), "status.json.tmp")));
    }

    [Fact]
    public void WriteTileBytes_Round_Trips_Through_Disk()
    {
        var id = JobStore.NewJobId();
        var (s, p) = BuildSubmit();
        _store.Create(id, s, p);

        byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
        _store.WriteTileBytes(id, 0, payload);

        Assert.True(_store.TryReadTileBytes(id, 0, out var got));
        Assert.Equal(payload, got);
    }

    [Fact]
    public void FailInflightAfterRestart_Marks_Stuck_Jobs_Failed()
    {
        var id1 = JobStore.NewJobId();
        var id2 = JobStore.NewJobId();
        var id3 = JobStore.NewJobId();
        var (s, p) = BuildSubmit();
        _store.Create(id1, s, p);
        _store.Create(id2, s, p);
        _store.Create(id3, s, p);
        _store.UpdateStatus(id1, st => st.JobState = "rendering");
        _store.UpdateStatus(id2, st => st.JobState = "ready");
        _store.UpdateStatus(id3, st => st.JobState = "merging");

        int n = _store.FailInflightAfterRestart();
        Assert.Equal(2, n);
        Assert.Equal("failed", _store.ReadStatus(id1)!.JobState);
        Assert.Equal("ready",  _store.ReadStatus(id2)!.JobState);
        Assert.Equal("failed", _store.ReadStatus(id3)!.JobState);
        Assert.Equal("master-restart", _store.ReadStatus(id1)!.FailReason);
    }

    [Fact]
    public void EvictExpired_Removes_Old_Terminal_Jobs_Only()
    {
        // Use a swappable clock so we can simulate the retention window
        // elapsing without sleeping.
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        string clockRoot = Path.Combine(_root, "clocked");
        var clocked = new JobStore(clockRoot) { NowUtc = () => now };

        var idA = JobStore.NewJobId();
        var idB = JobStore.NewJobId();
        var (s, p) = BuildSubmit();
        clocked.Create(idA, s, p);
        clocked.Create(idB, s, p);
        clocked.UpdateStatus(idA, st => st.JobState = "ready");
        clocked.UpdateStatus(idB, st => st.JobState = "rendering");

        // Advance the clock past the retention window.
        now = now.AddHours(2);
        int n = clocked.EvictExpired(TimeSpan.FromHours(1));
        Assert.Equal(1, n);
        Assert.False(clocked.Exists(idA));
        Assert.True (clocked.Exists(idB));
    }

    [Fact]
    public void ListJobIds_Returns_Only_Subdirs()
    {
        var id = JobStore.NewJobId();
        var (s, p) = BuildSubmit();
        _store.Create(id, s, p);
        File.WriteAllText(Path.Combine(_root, "not-a-job.txt"), "x");

        var ids = _store.ListJobIds().ToList();
        Assert.Contains(id, ids);
        Assert.DoesNotContain("not-a-job.txt", ids);
    }
}
