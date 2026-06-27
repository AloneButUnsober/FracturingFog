using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class WorkerRegistryTests
{
    private static WorkerRegisterDto SampleDto(string name = "w1") => new()
    {
        WorkerName            = name,
        OsPlatform            = "win",
        CpuModel              = "Test CPU",
        LogicalCores          = 8,
        TotalRamBytes         = 16L * 1024 * 1024 * 1024,
        Gpus                  = new() { "GPU0" },
        SupportedFractalTypes = new() { "Mandelbrot", "BurningShip" },
        MaxConcurrentTiles    = 4,
        PreferredTilePixels   = 512,
        EngineBuildSha        = "abc123",
        ProtocolVersion       = "1",
    };

    [Fact]
    public void Register_Returns_Entry_With_New_Id()
    {
        var reg = new WorkerRegistry();
        var entry = reg.Register(SampleDto(), thumbprint: "AABBCC", out var err);

        Assert.Null(err);
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrEmpty(entry!.WorkerId));
        Assert.Equal("AABBCC", entry.CertThumbprint);
        Assert.Equal("w1", entry.WorkerName);
    }

    [Fact]
    public void Register_Refused_When_Thumbprint_Empty()
    {
        var reg = new WorkerRegistry();
        var entry = reg.Register(SampleDto(), thumbprint: "", out var err);

        Assert.Null(entry);
        Assert.Equal("thumbprint-missing", err);
    }

    [Fact]
    public void Resume_With_Matching_Thumbprint_Returns_Same_Entry()
    {
        var reg = new WorkerRegistry();
        var first = reg.Register(SampleDto(), "TT", out _);
        Assert.NotNull(first);

        var dto = SampleDto("w1-renamed");
        dto.ResumeWorkerId = first!.WorkerId;
        var second = reg.Register(dto, "TT", out var err);

        Assert.Null(err);
        Assert.NotNull(second);
        Assert.Equal(first.WorkerId, second!.WorkerId);
        Assert.Equal("w1-renamed", second.WorkerName);   // re-register updates display fields
    }

    [Fact]
    public void Resume_With_Different_Thumbprint_Refused()
    {
        var reg = new WorkerRegistry();
        var first = reg.Register(SampleDto(), "TT", out _);

        var dto = SampleDto();
        dto.ResumeWorkerId = first!.WorkerId;
        var second = reg.Register(dto, "DIFFERENT", out var err);

        Assert.Null(second);
        Assert.Equal("thumbprint-pin-mismatch", err);
    }

    [Fact]
    public void Lookup_Refuses_Mismatched_Thumbprint()
    {
        var reg = new WorkerRegistry();
        var entry = reg.Register(SampleDto(), "TT", out _)!;

        Assert.Null(reg.Lookup(entry.WorkerId, "WRONG", out var err));
        Assert.Equal("thumbprint-pin-mismatch", err);
    }

    [Fact]
    public void Lookup_Refuses_Unknown_Worker()
    {
        var reg = new WorkerRegistry();
        Assert.Null(reg.Lookup("nope", "TT", out var err));
        Assert.Equal("unknown-worker", err);
    }

    [Fact]
    public void Heartbeat_Records_Live_State()
    {
        var reg = new WorkerRegistry();
        var entry = reg.Register(SampleDto(), "TT", out _)!;

        reg.Heartbeat(entry, new HeartbeatDto
        {
            WorkerId = entry.WorkerId,
            TilesInFlight = 3,
            CpuPercent = 42.5,
            FreeRamBytes = 1024,
            Note = "fine",
        });

        Assert.Equal(3, entry.TilesInFlight);
        Assert.Equal(42.5, entry.CpuPercent);
        Assert.Equal(1024, entry.FreeRamBytes);
        Assert.Equal("fine", entry.LastNote);
    }

    [Fact]
    public void Stale_Sweep_Evicts_After_Three_Missed_Intervals()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var reg = new WorkerRegistry
        {
            HeartbeatIntervalSeconds = 5,
            NowUtc = () => now,
        };
        var entry = reg.Register(SampleDto(), "TT", out _)!;

        // 14s later — still inside the 3× window (15s). Not stale.
        now = now.AddSeconds(14);
        Assert.Empty(reg.SweepStale());
        Assert.Equal(1, reg.LiveCount());

        // 16s past registration — outside the window. Should evict.
        now = now.AddSeconds(2);
        var evicted = reg.SweepStale();
        Assert.Single(evicted);
        Assert.Equal(entry.WorkerId, evicted[0]);
        Assert.Equal(0, reg.LiveCount());
    }

    [Fact]
    public void Heartbeat_Refreshes_Liveness()
    {
        DateTime now = new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var reg = new WorkerRegistry
        {
            HeartbeatIntervalSeconds = 5,
            NowUtc = () => now,
        };
        var entry = reg.Register(SampleDto(), "TT", out _)!;

        // 10s later — heartbeat keeps the entry alive.
        now = now.AddSeconds(10);
        reg.Heartbeat(entry, new HeartbeatDto { WorkerId = entry.WorkerId });

        // Another 14s past the heartbeat (24s past register) — still alive.
        now = now.AddSeconds(14);
        Assert.Empty(reg.SweepStale());
        Assert.Equal(1, reg.LiveCount());
    }

    [Fact]
    public async Task Concurrent_Register_Produces_Distinct_Ids()
    {
        var reg = new WorkerRegistry();
        const int N = 64;
        var tasks = Enumerable.Range(0, N).Select(i => Task.Run(() =>
            reg.Register(SampleDto($"w{i}"), thumbprint: $"T{i:X4}", out _)!
        )).ToArray();

        var entries = await Task.WhenAll(tasks);
        var ids = new HashSet<string>(entries.Select(e => e.WorkerId));
        Assert.Equal(N, ids.Count);  // every registration got a unique id
    }

    [Fact]
    public void Truncates_Oversized_String_Fields()
    {
        var reg = new WorkerRegistry();
        var dto = SampleDto();
        dto.WorkerName = new string('A', 1024);
        dto.CpuModel   = new string('B', 1024);

        var entry = reg.Register(dto, "TT", out _)!;
        Assert.Equal(64, entry.WorkerName.Length);    // 64-char cap per dev plan §3
        Assert.Equal(128, entry.CpuModel.Length);     // 128-char cap
    }

    [Fact]
    public void Clamps_Max_Concurrent_Tiles_To_Positive()
    {
        var reg = new WorkerRegistry();
        var dto = SampleDto();
        dto.MaxConcurrentTiles = 0;

        var entry = reg.Register(dto, "TT", out _)!;
        Assert.Equal(1, entry.MaxConcurrentTiles);    // 0 → 1 (minimum)
    }
}
