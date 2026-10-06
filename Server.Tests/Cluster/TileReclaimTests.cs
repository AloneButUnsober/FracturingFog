// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

// #1160 — a worker that goes away mid-tile hands its tiles back. Dispatcher
// level: reclaim / re-claim / late-original-delivery / exhaustion / stale
// errors. Coordinator level: worker A's session closes mid-tile, worker B
// finishes the job; A's late tile.error neither requeues B's tile nor fails it.
public sealed class TileReclaimTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static List<TileJobDto> Tiles(string jobId, int n)
    {
        var l = new List<TileJobDto>();
        for (int i = 0; i < n; i++)
            l.Add(new() { JobId = jobId, TileId = i, Render = new RenderRequestDto { Width = 16, Height = 16 } });
        return l;
    }

    // ---- dispatcher ------------------------------------------------------

    [Fact]
    public async Task Reclaim_RequeuesTheLostWorkersTiles_ForAnotherWorker()
    {
        var d = new TileDispatcher { MaxAttempts = 3 };
        d.EnqueueJob("J", Tiles("J", 1));
        var t = await d.ClaimNextAsync("A", Short, CancellationToken.None);
        Assert.NotNull(t);

        var r = Assert.Single(d.ReclaimWorker("A"));
        Assert.True(r.Requeued);
        Assert.Equal(2, r.Attempt);
        Assert.Equal(0, d.InFlightCount("J"));

        var t2 = await d.ClaimNextAsync("B", Short, CancellationToken.None);
        Assert.NotNull(t2);
        Assert.Equal(0, t2!.TileId);
        Assert.Equal(2, t2.Attempt);
        Assert.Empty(d.ReclaimWorker("A"));   // A holds nothing now
    }

    [Fact]
    public async Task LateOriginalDelivery_CompletesTheTile_AndTheRequeuedCopyIsSkipped()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J", Tiles("J", 1));
        await d.ClaimNextAsync("A", Short, CancellationToken.None);
        d.ReclaimWorker("A");                                // back in Pending
        Assert.True(d.AcceptDelivery("J", 0, "A"));          // A's delivery still lands: completes
        Assert.Equal(1, d.CompletedCount("J"));
        Assert.Null(await d.ClaimNextAsync("B", Short, CancellationToken.None));   // stale copy skipped
        Assert.False(d.AcceptDelivery("J", 0, "B"));         // duplicate
    }

    [Fact]
    public async Task LateOriginalDelivery_AfterReassignment_StillCompletes_OnceOnly()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J", Tiles("J", 1));
        await d.ClaimNextAsync("A", Short, CancellationToken.None);
        d.ReclaimWorker("A");
        await d.ClaimNextAsync("B", Short, CancellationToken.None);   // B re-renders it
        Assert.True(d.AcceptDelivery("J", 0, "A"));   // A first: wins
        Assert.False(d.AcceptDelivery("J", 0, "B"));  // B's is the duplicate
        Assert.Equal(1, d.CompletedCount("J"));
        Assert.Equal(0, d.InFlightCount("J"));
    }

    [Fact]
    public async Task Reclaim_CountsAsAnAttempt_SoAPoisonTileExhausts()
    {
        var d = new TileDispatcher { MaxAttempts = 2 };
        d.EnqueueJob("J", Tiles("J", 1));
        await d.ClaimNextAsync("A", Short, CancellationToken.None);
        Assert.True(Assert.Single(d.ReclaimWorker("A")).Requeued);     // attempt 1 → 2
        await d.ClaimNextAsync("B", Short, CancellationToken.None);
        var last = Assert.Single(d.ReclaimWorker("B"));
        Assert.False(last.Requeued);                                     // attempt 2 was the last
        Assert.Null(await d.ClaimNextAsync("C", Short, CancellationToken.None));
    }

    [Fact]
    public async Task FailureReport_IsOwnerChecked()
    {
        var d = new TileDispatcher { MaxAttempts = 3 };
        d.EnqueueJob("J", Tiles("J", 1));
        await d.ClaimNextAsync("A", Short, CancellationToken.None);
        d.ReclaimWorker("A");
        await d.ClaimNextAsync("B", Short, CancellationToken.None);
        // A's late error must not touch B's tile.
        Assert.Equal(TileDispatcher.FailureResult.NotOwned, d.RecordFailure("J", 0, "A", allowRetry: true));
        Assert.Equal(1, d.InFlightCount("J"));
        // The owner's own error requeues; a fatal one exhausts.
        Assert.Equal(TileDispatcher.FailureResult.Requeued, d.RecordFailure("J", 0, "B", allowRetry: true));
        await d.ClaimNextAsync("C", Short, CancellationToken.None);
        Assert.Equal(TileDispatcher.FailureResult.Exhausted, d.RecordFailure("J", 0, "C", allowRetry: false));
    }

    // ---- coordinator -----------------------------------------------------

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ff-reclaim-{Guid.NewGuid():N}");
    private ClusterLogger? _log;

    public void Dispose()
    {
        _log?.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static JsonElement P(object o) => JsonSerializer.SerializeToElement(o, JsonRpcFraming.JsonOpts);

    private (ClusterCoordinator coord, JobStore jobs) Coordinator()
    {
        Directory.CreateDirectory(_root);
        _log = new ClusterLogger(Path.Combine(_root, "logs"));
        var jobs = new JobStore(Path.Combine(_root, "jobs"));
        var coord = new ClusterCoordinator(new WorkerRegistry { HeartbeatIntervalSeconds = 5 }, _log)
        {
            Jobs = jobs,
            Dispatcher = new TileDispatcher { MaxAttempts = 3 },
            Codec = new RawHeaderCodec(),
            EngineBuildSha = "",
            TileNextHold = TimeSpan.FromMilliseconds(200),
        };
        return (coord, jobs);
    }

    private static async Task<string> Register(ClusterCoordinator c, string thumb, string name)
    {
        var o = await c.HandleAsync("worker.register", P(new WorkerRegisterDto
        {
            WorkerName = name, OsPlatform = "test", LogicalCores = 1, ProtocolVersion = "1",
            EngineBuildSha = "", PreferredTilePixels = 64, MaxConcurrentTiles = 1,
        }), CertRole.Worker, thumb, CancellationToken.None);
        return Assert.IsType<WorkerRegisterAckDto>(o.Result).WorkerId;
    }

    private static async Task<TileJobDto?> Next(ClusterCoordinator c, string id, string thumb)
    {
        var o = await c.HandleAsync("tile.next", P(new HeartbeatDto { WorkerId = id }), CertRole.Worker, thumb, CancellationToken.None);
        return ((TileNextResultDto)o.Result!).Tile;
    }

    private static Task Deliver(ClusterCoordinator c, string id, string thumb, TileJobDto t)
    {
        byte[] payload = RawHeaderCodec.BuildTile(t.Render.Width, t.Render.Height, fillR: 1, fillG: 2, fillB: 3);
        return c.HandleAsync("tile.deliver", P(new TileDeliverDto
        {
            WorkerId = id, JobId = t.JobId, TileId = t.TileId, PayloadKind = "png",
            Width = t.Render.Width, Height = t.Render.Height,
            BytesBase64 = Convert.ToBase64String(payload),
            Sha256 = Convert.ToBase64String(SHA256.HashData(payload)), RenderMs = 1,
        }), CertRole.Worker, thumb, CancellationToken.None);
    }

    private static async Task<string> Submit(ClusterCoordinator c)
    {
        var o = await c.HandleAsync("job.submit", P(new JobSubmitDto
        {
            Request = new RenderRequestDto { Mode = "image", FractalType = "Mandelbrot", Width = 64, Height = 64, CenterX = -0.5, CenterY = 0, Zoom = 1 },
            TilePixelsHint = 64,
        }), CertRole.Client, "client", CancellationToken.None);
        Assert.True(o.ErrorCode == null, $"job.submit: {o.ErrorCode} {o.ErrorMessage}");
        var ack = Assert.IsType<JobAckDto>(o.Result);
        Assert.Equal(1, ack.TileCount);
        return ack.JobId;
    }

    [Fact]
    public async Task WorkerSessionClosedMidTile_AnotherWorkerFinishesTheJob()
    {
        var (c, jobs) = Coordinator();
        string a = await Register(c, "THUMB-A", "a"), b = await Register(c, "THUMB-B", "b");
        string job = await Submit(c);

        var tA = await Next(c, a, "THUMB-A");
        Assert.NotNull(tA);
        Assert.Null(await Next(c, b, "THUMB-B"));     // nothing for B while A holds it

        c.OnWorkerSessionClosed("THUMB-A");            // A crashed mid-tile
        var tB = await Next(c, b, "THUMB-B");
        Assert.NotNull(tB);
        Assert.Equal(tA!.TileId, tB!.TileId);
        Assert.Equal(2, tB.Attempt);

        // A's late error report (it was gone) must not fail or requeue B's tile.
        await c.HandleAsync("tile.error", P(new TileErrorDto { WorkerId = a, JobId = job, TileId = tA.TileId, Code = "engine-failed", Message = "late" }),
            CertRole.Worker, "THUMB-A", CancellationToken.None);
        Assert.NotEqual("failed", jobs.ReadStatus(job)!.JobState);

        await Deliver(c, b, "THUMB-B", tB);
        Assert.Equal("ready", jobs.ReadStatus(job)!.JobState);
    }

    [Fact]
    public async Task UnrelatedWorkerSessionClosing_DoesNotTouchOtherTiles()
    {
        var (c, jobs) = Coordinator();
        string a = await Register(c, "THUMB-A", "a");
        await Register(c, "THUMB-B", "b");
        string job = await Submit(c);
        var tA = await Next(c, a, "THUMB-A");
        c.OnWorkerSessionClosed("THUMB-B");
        await Deliver(c, a, "THUMB-A", tA!);
        Assert.Equal("ready", jobs.ReadStatus(job)!.JobState);
    }
}
