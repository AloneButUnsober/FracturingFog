// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/Cluster/RoleAwareRateLimiterTests.cs
// D-6c — covers the per-role per-method rate limiter that sits on the
// FFServer dispatch path. Unit-level: drives RoleAwareRateLimiter directly
// without an SslStream. Also exercises the coordinator's admin-call audit
// event so the "log every admin call" half of the dev-plan §6.6 ask is
// regression-guarded.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Guard;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class RoleAwareRateLimiterTests
{
    [Fact]
    public void Admin_Always_Allowed_Even_With_Tight_Buckets()
    {
        // perMinute=60 + burst=1 — first call empties both buckets for any
        // other role; admin must still be allowed across many calls.
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 60, clientBurst: 1,
            workerTileNextPerMinute: 60, workerTileNextBurst: 1);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Admin, "10.0.0.1", "cluster.status"));
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Admin, "10.0.0.1", "job.submit"));
        }
    }

    [Fact]
    public void Worker_TileNext_Bucket_Exhausts_Then_Refuses()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 0, clientBurst: 0,
            workerTileNextPerMinute: 60, workerTileNextBurst: 3);

        const string thumb = "AABBCC";
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, thumb, "tile.next"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, thumb, "tile.next"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, thumb, "tile.next"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Worker, thumb, "tile.next"));
    }

    [Fact]
    public void Worker_NonTileNext_Methods_Bypass_Limiter()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 0, clientBurst: 0,
            workerTileNextPerMinute: 60, workerTileNextBurst: 1);

        const string thumb = "AABBCC";
        // First tile.next consumes the only token.
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, thumb, "tile.next"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Worker, thumb, "tile.next"));

        // Other worker methods must not draw from the tile.next bucket.
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Worker, thumb, "worker.heartbeat"));
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Worker, thumb, "tile.deliver"));
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Worker, thumb, "tile.error"));
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Worker, thumb, "worker.register"));
        }
    }

    [Fact]
    public void Worker_Buckets_Are_Per_Thumbprint()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 0, clientBurst: 0,
            workerTileNextPerMinute: 60, workerTileNextBurst: 2);

        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));

        // A second worker (different thumbprint, possibly same IP) gets a
        // fresh bucket. This is the load-bearing isolation guard against
        // one runaway worker starving its peers behind a shared NAT.
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, "BBB", "tile.next"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Worker, "BBB", "tile.next"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Worker, "BBB", "tile.next"));
    }

    [Fact]
    public void Client_Bucket_Exhausts_Then_Refuses_Per_IP()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 60, clientBurst: 2,
            workerTileNextPerMinute: 0, workerTileNextBurst: 0);

        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Client, "10.0.0.1", "job.submit"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));

        // Separate IP has its own bucket.
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Client, "10.0.0.2", "job.status"));
        Assert.Equal(RoleLimiterDecision.Allow,       lim.TryAccept(CertRole.Client, "10.0.0.2", "job.status"));
        Assert.Equal(RoleLimiterDecision.RefusedRate, lim.TryAccept(CertRole.Client, "10.0.0.2", "job.status"));
    }

    [Fact]
    public void Disabled_PerMinute_Allows_All()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 0, clientBurst: 1,
            workerTileNextPerMinute: 0, workerTileNextBurst: 1);

        Assert.False(lim.ClientEnabled);
        Assert.False(lim.WorkerTileNextEnabled);

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));
        }
    }

    // ── D-6c1 — live reconfigure ────────────────────────────────────────

    [Fact]
    public void Reconfigure_From_Disabled_To_Enabled_Starts_Refusing()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 0, clientBurst: 1,
            workerTileNextPerMinute: 0, workerTileNextBurst: 1);
        Assert.False(lim.ClientEnabled);

        // Was disabled; tighten to 60/min burst=1. First call drains the
        // single token, second refuses.
        lim.Reconfigure(
            clientPerMinute: 60, clientBurst: 1,
            workerTileNextPerMinute: 0, workerTileNextBurst: 1);
        Assert.True(lim.ClientEnabled);

        Assert.Equal(RoleLimiterDecision.Allow,
            lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
        Assert.Equal(RoleLimiterDecision.RefusedRate,
            lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
    }

    [Fact]
    public void Reconfigure_From_Enabled_To_Disabled_Stops_Refusing()
    {
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 60, clientBurst: 1,
            workerTileNextPerMinute: 0, workerTileNextBurst: 1);
        Assert.Equal(RoleLimiterDecision.Allow,
            lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
        Assert.Equal(RoleLimiterDecision.RefusedRate,
            lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));

        // Operator dials per-minute to 0; the limiter goes back to
        // unconditional Allow for the client role on the next call.
        lim.Reconfigure(
            clientPerMinute: 0, clientBurst: 1,
            workerTileNextPerMinute: 0, workerTileNextBurst: 1);
        Assert.False(lim.ClientEnabled);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
        }
    }

    [Fact]
    public void Reconfigure_Worker_Bucket_Independent_Of_Client_Bucket()
    {
        // Tighten only the worker bucket; the client bucket retains its
        // original rate. This guards against an accidental cross-wiring
        // in the Reconfigure forwarder.
        var lim = new RoleAwareRateLimiter(
            clientPerMinute: 60, clientBurst: 5,
            workerTileNextPerMinute: 60, workerTileNextBurst: 5);

        lim.Reconfigure(
            clientPerMinute: 60, clientBurst: 5,
            workerTileNextPerMinute: 60, workerTileNextBurst: 1);

        Assert.Equal(RoleLimiterDecision.Allow,
            lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));
        Assert.Equal(RoleLimiterDecision.RefusedRate,
            lim.TryAccept(CertRole.Worker, "AAA", "tile.next"));

        // Client side still has its original burst of 5.
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(RoleLimiterDecision.Allow,
                lim.TryAccept(CertRole.Client, "10.0.0.1", "job.status"));
        }
    }
}

/// <summary>Covers the admin-call audit-log half of D-6c. The coordinator
/// emits a "kind":"admin-call" event for every cluster.* method invoked by
/// the admin role; the cluster log file on disk receives that line.</summary>
public sealed class AdminAuditLogTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly ClusterCoordinator _coord;

    public AdminAuditLogTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), $"ff-admincall-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logDir);
        _log      = new ClusterLogger(_logDir);
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _jobs     = new JobStore(Path.Combine(_root, "jobs"));
        _coord    = new ClusterCoordinator(_registry, _log)
        {
            Jobs           = _jobs,
            EngineBuildSha = "",
            TileNextHold   = TimeSpan.FromMilliseconds(50),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cluster_Method_By_Admin_Writes_AdminCall_Event()
    {
        // Drive a real cluster.* RPC. cluster.status is the cheapest and
        // does not need a worker / job to succeed.
        var outcome = await _coord.HandleAsync(
            "cluster.status",
            JsonSerializer.SerializeToElement(new ClusterStatusRequestDto(), JsonRpcFraming.JsonOpts),
            CertRole.Admin,
            "ADMIN-THUMB-AABBCC",
            CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Null(outcome.ErrorCode);

        // Flush the channel-backed logger so the line is on disk.
        _log.Dispose();

        var logPath = Directory.EnumerateFiles(_logDir, "cluster-*.log").Single();
        var lines = File.ReadAllLines(logPath);
        Assert.Contains(lines, l => l.Contains("\"kind\":\"admin-call\"")
                                  && l.Contains("\"method\":\"cluster.status\""));
    }

    [Fact]
    public async Task Cluster_Method_By_NonAdmin_Skipped_From_AdminCall_Event()
    {
        // Worker role is refused at the FFServer role gate before reaching
        // the coordinator in production, but the coordinator must still
        // not log "admin-call" if someone bypasses (e.g. a test driver or
        // a future role-policy widening). Direct-drive Client role here —
        // cluster.status returns forbidden semantics via FFServer; the
        // coordinator just runs the handler and we assert no admin-call
        // event is logged for the non-admin caller.
        await _coord.HandleAsync(
            "cluster.status",
            JsonSerializer.SerializeToElement(new ClusterStatusRequestDto(), JsonRpcFraming.JsonOpts),
            CertRole.Client,
            "CLIENT-THUMB",
            CancellationToken.None);

        _log.Dispose();
        var logPath = Directory.EnumerateFiles(_logDir, "cluster-*.log").SingleOrDefault();
        if (logPath is null) return;  // logger may have written nothing — also fine
        var lines = File.ReadAllLines(logPath);
        Assert.DoesNotContain(lines, l => l.Contains("\"kind\":\"admin-call\""));
    }
}
