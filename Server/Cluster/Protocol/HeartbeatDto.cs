// Server/Cluster/Protocol/HeartbeatDto.cs
// Worker → Master. Sent every ~5 s on the cluster session. Carries the
// live load so the master's tile planner can avoid piling work onto a
// saturated worker.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class HeartbeatDto
{
    /// <summary>WorkerId issued by the master at register time. Master
    /// rejects heartbeats whose id does not match the cert thumbprint pin.</summary>
    [JsonPropertyName("workerId")]
    public string WorkerId { get; set; } = "";

    /// <summary>Tiles currently being rendered on this worker.</summary>
    [JsonPropertyName("tilesInFlight")]
    public int TilesInFlight { get; set; }

    /// <summary>CPU utilisation 0..100 averaged over the last heartbeat
    /// window. -1 if the worker cannot sample it on its platform.</summary>
    [JsonPropertyName("cpuPercent")]
    public double CpuPercent { get; set; } = -1;

    /// <summary>Free RAM bytes at the moment of sampling.</summary>
    [JsonPropertyName("freeRamBytes")]
    public long FreeRamBytes { get; set; }

    /// <summary>Optional message the worker wants surfaced in the admin UI
    /// (e.g. "thermal throttling"). Truncated to 256 chars master-side.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
