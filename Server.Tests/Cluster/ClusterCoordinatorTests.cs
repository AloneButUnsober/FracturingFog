using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class ClusterCoordinatorTests : IDisposable
{
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly ClusterCoordinator _coord;

    public ClusterCoordinatorTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(),
            $"ff-cluster-test-{Guid.NewGuid():N}");
        _log = new ClusterLogger(_logDir);
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _coord = new ClusterCoordinator(_registry, _log)
        {
            // No engine-sha pin — tests use the empty-skip path.
            EngineBuildSha = "",
            TileNextHold = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private static JsonElement ToParams(object payload)
        => JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts);

    private static WorkerRegisterDto MinimalDto() => new()
    {
        WorkerName = "test-worker",
        OsPlatform = "win",
        LogicalCores = 4,
        ProtocolVersion = "1",
        EngineBuildSha = "irrelevant-when-master-skip",
    };

    [Fact]
    public async Task Register_Returns_WorkerId_And_Heartbeat_Cadence()
    {
        var outcome = await _coord.HandleAsync(
            "worker.register", ToParams(MinimalDto()),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Null(outcome.ErrorCode);
        var ack = Assert.IsType<WorkerRegisterAckDto>(outcome.Result);
        Assert.False(string.IsNullOrEmpty(ack.WorkerId));
        Assert.Equal(5, ack.HeartbeatIntervalSeconds);
    }

    [Fact]
    public async Task Register_Refuses_Unsupported_Protocol_Version()
    {
        var dto = MinimalDto();
        dto.ProtocolVersion = "99";

        var outcome = await _coord.HandleAsync(
            "worker.register", ToParams(dto),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Equal("unsupported-protocol", outcome.ErrorCode);
    }

    [Fact]
    public async Task Register_Refuses_Engine_Sha_Mismatch_When_Master_Pin_Set()
    {
        var coord = new ClusterCoordinator(new WorkerRegistry(), _log)
        {
            EngineBuildSha = "master-sha",
        };
        var dto = MinimalDto();
        dto.EngineBuildSha = "worker-sha";

        var outcome = await coord.HandleAsync(
            "worker.register", ToParams(dto),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Equal("engine-sha-mismatch", outcome.ErrorCode);
    }

    [Fact]
    public async Task Heartbeat_From_Unknown_Worker_Refused()
    {
        var outcome = await _coord.HandleAsync(
            "worker.heartbeat",
            ToParams(new HeartbeatDto { WorkerId = "not-registered" }),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Equal("unknown-worker", outcome.ErrorCode);
    }

    [Fact]
    public async Task Heartbeat_With_Wrong_Thumbprint_Refused()
    {
        var regOutcome = await _coord.HandleAsync(
            "worker.register", ToParams(MinimalDto()),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);
        var ack = (WorkerRegisterAckDto)regOutcome.Result!;

        var outcome = await _coord.HandleAsync(
            "worker.heartbeat",
            ToParams(new HeartbeatDto { WorkerId = ack.WorkerId }),
            CertRole.Worker, thumbprint: "DIFFERENT", CancellationToken.None);

        Assert.True(outcome.Handled);
        Assert.Equal("thumbprint-pin-mismatch", outcome.ErrorCode);
    }

    [Fact]
    public async Task Heartbeat_Updates_Registry_State()
    {
        var regOutcome = await _coord.HandleAsync(
            "worker.register", ToParams(MinimalDto()),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);
        var ack = (WorkerRegisterAckDto)regOutcome.Result!;

        await _coord.HandleAsync("worker.heartbeat",
            ToParams(new HeartbeatDto
            {
                WorkerId = ack.WorkerId,
                TilesInFlight = 2,
                CpuPercent = 77,
                FreeRamBytes = 4096,
            }),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        var snap = _registry.Snapshot();
        Assert.Single(snap);
        Assert.Equal(2, snap[0].TilesInFlight);
        Assert.Equal(77, snap[0].CpuPercent);
    }

    [Fact]
    public async Task TileNext_Holds_Then_Returns_WaitAgain()
    {
        var regOutcome = await _coord.HandleAsync(
            "worker.register", ToParams(MinimalDto()),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);
        var ack = (WorkerRegisterAckDto)regOutcome.Result!;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await _coord.HandleAsync(
            "tile.next",
            ToParams(new HeartbeatDto { WorkerId = ack.WorkerId }),
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);
        sw.Stop();

        Assert.True(outcome.Handled);
        Assert.Null(outcome.ErrorCode);
        var result = Assert.IsType<TileNextResultDto>(outcome.Result);
        Assert.True(result.WaitAgain);
        Assert.False(result.Shutdown);
        // Coordinator was set to 50ms hold; allow slack for CI jitter.
        Assert.True(sw.ElapsedMilliseconds >= 40,
            $"tile.next returned too fast: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Unknown_Cluster_Method_Returns_NotHandled()
    {
        var outcome = await _coord.HandleAsync(
            "worker.nonsense", null,
            CertRole.Worker, thumbprint: "TT", CancellationToken.None);

        Assert.False(outcome.Handled);
    }
}
