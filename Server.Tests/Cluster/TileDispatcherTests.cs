// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
        Assert.True(d.AcceptDelivery("J1", t!.TileId, "w1"));
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

    // ── D-3b work-stealing ─────────────────────────────────────────────

    private static List<TileJobDto> TenTiles(string jobId)
    {
        var list = new List<TileJobDto>();
        for (int i = 0; i < 10; i++)
            list.Add(new() { JobId = jobId, TileId = i, Render = new RenderRequestDto { Width = 16, Height = 16 } });
        return list;
    }

    [Fact]
    public async Task Work_Stealing_Returns_Duplicate_When_Pending_Empty_And_Near_End()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var d = new TileDispatcher
        {
            NowUtc = () => now,
            StealMinAge = TimeSpan.FromSeconds(1),
            StealMinTotalTiles = 4,
            StealRemainingFraction = 0.10,
        };
        d.EnqueueJob("J1", TenTiles("J1"));

        // First worker drains the whole queue (1 in-flight at a time
        // — for the test we just claim all 10).
        var claimed = new List<TileJobDto>();
        for (int i = 0; i < 10; i++)
        {
            var t = await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(t);
            claimed.Add(t!);
        }

        // Complete 9 of the 10 — last one (TileId=9) is the straggler.
        for (int i = 0; i < 9; i++) Assert.True(d.AcceptDelivery("J1", i, "wA"));
        Assert.Equal(1, d.InFlightCount("J1"));
        Assert.Equal(0, d.PendingCount("J1"));

        // Age the in-flight tile past the steal-min-age window.
        now = now.AddSeconds(2);

        // Idle worker arrives — should steal a duplicate of tile 9.
        var stolen = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.NotNull(stolen);
        Assert.Equal(9, stolen!.TileId);
        // In-flight count stays 1 — work-stealing does not consume the
        // original slot; the duplicate races for first delivery.
        Assert.Equal(1, d.InFlightCount("J1"));
    }

    [Fact]
    public async Task Work_Stealing_Skipped_For_Tiny_Jobs()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var d = new TileDispatcher
        {
            NowUtc = () => now,
            StealMinAge = TimeSpan.FromSeconds(1),
            StealMinTotalTiles = 4,
        };
        // Only 3 tiles — under StealMinTotalTiles, no stealing.
        d.EnqueueJob("J1", new List<TileJobDto>
        {
            new() { JobId = "J1", TileId = 0, Render = new RenderRequestDto { Width = 16, Height = 16 } },
            new() { JobId = "J1", TileId = 1, Render = new RenderRequestDto { Width = 16, Height = 16 } },
            new() { JobId = "J1", TileId = 2, Render = new RenderRequestDto { Width = 16, Height = 16 } },
        });
        for (int i = 0; i < 3; i++)
            await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(d.AcceptDelivery("J1", 0, "wA"));
        Assert.True(d.AcceptDelivery("J1", 1, "wA"));
        now = now.AddSeconds(2);

        var stolen = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(stolen);
    }

    [Fact]
    public async Task Work_Stealing_Honors_Min_Age()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var d = new TileDispatcher
        {
            NowUtc = () => now,
            StealMinAge = TimeSpan.FromSeconds(5),
            StealMinTotalTiles = 4,
        };
        d.EnqueueJob("J1", TenTiles("J1"));
        for (int i = 0; i < 10; i++)
            await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        for (int i = 0; i < 9; i++) Assert.True(d.AcceptDelivery("J1", i, "wA"));

        // Only 1 second past assignment — under 5 s min age.
        now = now.AddSeconds(1);
        var notStolen = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(notStolen);

        // Past the window — steal allowed.
        now = now.AddSeconds(5);
        var stolen = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.NotNull(stolen);
        Assert.Equal(9, stolen!.TileId);
    }

    [Fact]
    public async Task Work_Stealing_Does_Not_Steal_From_Self()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var d = new TileDispatcher
        {
            NowUtc = () => now,
            StealMinAge = TimeSpan.FromMilliseconds(100),
            StealMinTotalTiles = 4,
        };
        d.EnqueueJob("J1", TenTiles("J1"));
        for (int i = 0; i < 10; i++)
            await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        for (int i = 0; i < 9; i++) Assert.True(d.AcceptDelivery("J1", i, "wA"));

        now = now.AddSeconds(1);
        // Same worker asking again must not steal its own tile.
        var t = await d.ClaimNextAsync("wA", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(t);
    }

    [Fact]
    public async Task Work_Stealing_Does_Not_Re_Hand_Same_Tile_To_Same_Stealer()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var d = new TileDispatcher
        {
            NowUtc = () => now,
            StealMinAge = TimeSpan.FromMilliseconds(100),
            StealMinTotalTiles = 4,
        };
        d.EnqueueJob("J1", TenTiles("J1"));
        for (int i = 0; i < 10; i++)
            await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        for (int i = 0; i < 9; i++) Assert.True(d.AcceptDelivery("J1", i, "wA"));

        now = now.AddSeconds(1);
        var first  = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.NotNull(first);
        var second = await d.ClaimNextAsync("wB", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(second);
    }

    // ── D-4b — ReturnPending (backpressure path) ───────────────────────

    [Fact]
    public async Task ReturnPending_Puts_Tile_Back_Without_Bumping_Attempt()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J1", ThreeTiles("J1"));

        var t = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(t);
        Assert.Equal(1, t!.Attempt);
        Assert.Equal(1, d.InFlightCount("J1"));

        Assert.True(d.ReturnPending("J1", t));
        Assert.Equal(0, d.InFlightCount("J1"));

        var again = await d.ClaimNextAsync("w1", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(again);
        // Attempt unchanged — backpressure is not the worker's fault.
        Assert.Equal(1, again!.Attempt);
    }

    [Fact]
    public async Task ReturnPending_Signals_Waiting_Worker()
    {
        var d = new TileDispatcher();
        d.EnqueueJob("J1", ThreeTiles("J1"));

        // Worker A claims tile 0, then the master returns it for backpressure.
        var first = await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        // Drain remaining pending so the next claim has to wait for our requeue.
        var second = await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        var third  = await d.ClaimNextAsync("wA", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(second); Assert.NotNull(third);

        // Worker B is waiting on tile.next — should wake when we ReturnPending.
        var waitTask = d.ClaimNextAsync("wB", TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(50);
        Assert.True(d.ReturnPending("J1", first!));

        var got = await waitTask;
        Assert.NotNull(got);
        Assert.Equal(first.TileId, got!.TileId);
    }

    [Fact]
    public void ReturnPending_Unknown_Job_Returns_False()
    {
        var d = new TileDispatcher();
        Assert.False(d.ReturnPending("missing", new TileJobDto { JobId = "missing", TileId = 0 }));
    }
}
