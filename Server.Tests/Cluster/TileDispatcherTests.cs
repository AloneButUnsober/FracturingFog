using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class TileDispatcherTests
{
    private static List<TileJobDto> ThreeTiles(string jobId) => new()
    {
        new() { JobId = jobId, TileId = 0, Render = new RenderRequestDto { Width = 16, Height = 16 } },
        new() { JobId = jobId, TileId = 1, Render = new RenderRequestDto { Width = 16, Height = 16 } },
        new() { JobId = jobId, TileId = 2, Render = new RenderRequestDto { Width = 16, Height = 16 } },
    };

    [Fact]
    public async Task ClaimNext_Returns_Pending_Tile_Immediately()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J1", ThreeTiles("J1"));

        var t = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(t);
        Assert.Equal(0, t!.TileId);
        Assert.Equal(1, t.Attempt);
    }

    [Fact]
    public async Task ClaimNext_Returns_Null_When_No_Jobs_And_Hold_Expires()
    {
        var d = new TileDispatcher();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = await d.ClaimNextAsync("w1", TimeSpan.FromMilliseconds(80), CancellationToken.None);
        sw.Stop();
        Assert.Null(t);
        Assert.True(sw.ElapsedMilliseconds >= 60,
            $"expected to wait the hold; only {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ClaimNext_Wakes_When_Job_Enqueued_During_Wait()
    {
        var d = new TileDispatcher();
        var claimTask = d.ClaimNextAsync("w1", TimeSpan.FromSeconds(5), CancellationToken.None);

        await Task.Delay(50);
        d.EnqueueJob("J1", ThreeTiles("J1"));

        var t = await claimTask;
        Assert.NotNull(t);
        Assert.Equal("J1", t!.JobId);
    }

    [Fact]
    public async Task AcceptDelivery_Removes_InFlight_And_Records_Completion()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J1", ThreeTiles("J1"));
        var t = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(t);

        Assert.Equal(1, d.InFlightCount("J1"));
        Assert.True(d.AcceptDelivery("J1", t!.TileId));
        Assert.Equal(0, d.InFlightCount("J1"));
        Assert.Equal(1, d.CompletedCount("J1"));
    }

    [Fact]
    public async Task RecordFailure_Requeues_With_Incremented_Attempt()
    {
        var d = new TileDispatcher { MaxAttempts = 3 };
        // Single-tile job: failure must requeue the same tile (no other
        // tile to round-robin into).
        d.EnqueueJob("J1", new List<TileJobDto>
        {
            new() { JobId = "J1", TileId = 0, Render = new RenderRequestDto { Width = 16, Height = 16 } },
        });
        var t = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(t);

        Assert.True(d.RecordFailure("J1", t!.TileId));

        var retry = await d.ClaimNextAsync("w2", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(retry);
        Assert.Equal(t.TileId, retry!.TileId);
        Assert.Equal(2, retry.Attempt);
    }

    [Fact]
    public async Task RecordFailure_Returns_False_When_Budget_Exhausted()
    {
        var d = new TileDispatcher { MaxAttempts = 2 };
        d.EnqueueJob("J1", ThreeTiles("J1").Take(1).ToList());

        var t1 = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(d.RecordFailure("J1", t1!.TileId));   // attempt 1 → requeued as 2

        var t2 = await d.ClaimNextAsync("w2", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(2, t2!.Attempt);
        Assert.False(d.RecordFailure("J1", t2.TileId));   // attempt 2 was last
    }

    [Fact]
    public async Task RetireJob_Stops_Returning_Its_Tiles()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J1", ThreeTiles("J1"));
        d.RetireJob("J1");

        var t = await d.ClaimNextAsync("w1", TimeSpan.FromMilliseconds(80), CancellationToken.None);
        Assert.Null(t);
    }

    [Fact]
    public async Task ClaimNext_Honours_Cancellation()
    {
        var d = new TileDispatcher();
        var cts = new CancellationTokenSource();
        var task = d.ClaimNextAsync("w1", TimeSpan.FromSeconds(10), cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        var t = await task;
        Assert.Null(t);
    }
}
