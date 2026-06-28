// Client/FFAdminConnection.cs
// Admin-cert wrapper over FFClientConnection. Connect with admin.pfx (OU=role-admin)
// and the master's role gate (FFServer.AcceptsRole) lets the cluster.* methods
// through. Identical TLS plumbing as FFClientConnection — this type just exposes
// the admin-only surface so callers don't accidentally reach a Client-only
// helper that would be refused with "forbidden".

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster.Protocol;

namespace FracturingFog.Client;

public sealed class FFAdminConnection : IAsyncDisposable
{
    private readonly FFClientConnection _inner;

    private FFAdminConnection(FFClientConnection inner) { _inner = inner; }

    public static async Task<FFAdminConnection> ConnectAsync(
        FFClientConnection.ConnectOptions opts, CancellationToken ct)
    {
        var inner = await FFClientConnection.ConnectAsync(opts, ct).ConfigureAwait(false);
        return new FFAdminConnection(inner);
    }

    /// <summary>cluster.status — snapshot of every connected worker plus the N
    /// most-recent jobs. Caller supplies the recent-job cap; the master clamps
    /// to [1, 500].</summary>
    public Task<ClusterStatusDto> GetClusterStatusAsync(int? recentJobLimit, CancellationToken ct)
        => _inner.CallAsync<ClusterStatusDto>(
            "cluster.status",
            new ClusterStatusRequestDto { RecentJobLimit = recentJobLimit },
            ct);

    /// <summary>cluster.quiesceWorker — set/clear a worker's drain flag.
    /// Ack reports previous + current state so the UI can resolve a
    /// concurrent toggle without an extra cluster.status round-trip.</summary>
    public Task<WorkerQuiesceAckDto> SetWorkerQuiescedAsync(
        string workerId, bool quiesced, CancellationToken ct)
        => _inner.CallAsync<WorkerQuiesceAckDto>(
            "cluster.quiesceWorker",
            new WorkerQuiesceRequestDto { WorkerId = workerId, Quiesced = quiesced },
            ct);

    /// <summary>cluster.killWorker — evict a worker from the registry.
    /// Idempotent: second call on an already-removed id returns
    /// <c>Removed=false</c> with no error.</summary>
    public Task<WorkerKillAckDto> KillWorkerAsync(string workerId, CancellationToken ct)
        => _inner.CallAsync<WorkerKillAckDto>(
            "cluster.killWorker",
            new WorkerKillRequestDto { WorkerId = workerId },
            ct);

    /// <summary>cluster.listJobs — paged job summaries from the on-disk store.
    /// Distinct from cluster.status' embedded recent-jobs block because the
    /// admin job list view may filter (e.g. failed-only) and page deeper.</summary>
    public Task<JobListDto> ListJobsAsync(
        int? limit, bool? includeTerminal, string? stateFilter, CancellationToken ct)
        => _inner.CallAsync<JobListDto>(
            "cluster.listJobs",
            new JobListRequestDto
            {
                Limit           = limit,
                IncludeTerminal = includeTerminal,
                StateFilter     = stateFilter,
            },
            ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
