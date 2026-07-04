// Server/Cluster/Protocol/WorkerRegisterAckDto.cs
// Master → Worker. Success response to worker.register. Refusals come back
// as ErrorDto with codes: "unsupported-protocol", "engine-sha-mismatch",
// "thumbprint-pin-mismatch", "duplicate-worker".

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class WorkerRegisterAckDto
{
    /// <summary>Stable identifier the worker uses for the lifetime of this
    /// physical node. UUID-style base32 string. Persists across reconnects
    /// by virtue of the master pinning it to the worker's cert thumbprint.</summary>
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = "";

    /// <summary>Server-clock unix seconds at the moment of registration.
    /// Worker uses it to detect large clock skew (>60s) and log a warning;
    /// tile deadlines are computed master-side so skew does not break
    /// correctness, only diagnostics.</summary>
    [JsonPropertyName("serverUnixSeconds")]
    public long ServerUnixSeconds { get; set; }

    /// <summary>Heartbeat cadence the master expects. Worker should call
    /// worker.heartbeat at this interval; the master marks workers stale
    /// at 3× missed beats.</summary>
    [JsonPropertyName("heartbeatIntervalSeconds")]
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum seconds the master will hold a tile.next long-poll
    /// before returning wait-again. Worker uses this as its read timeout.</summary>
    [JsonPropertyName("tileNextHoldSeconds")]
    public int TileNextHoldSeconds { get; set; } = 30;
}
