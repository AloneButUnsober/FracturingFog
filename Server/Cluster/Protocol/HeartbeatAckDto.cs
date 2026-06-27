// Server/Cluster/Protocol/HeartbeatAckDto.cs
// Master → Worker. Cheap ack with optional control flags. Errors come
// back as ErrorDto with codes: "unknown-worker", "thumbprint-pin-mismatch".

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class HeartbeatAckDto
{
    /// <summary>Server-clock unix seconds. Worker uses it to keep its
    /// notion of master-time fresh for diagnostics.</summary>
    [JsonPropertyName("serverUnixSeconds")]
    public long ServerUnixSeconds { get; set; }

    /// <summary>True when the master wants the worker to stop accepting
    /// new tiles (admin quiesce). Worker MUST cease issuing tile.next
    /// long-polls until a subsequent heartbeat ack clears the flag.</summary>
    [JsonPropertyName("quiesce")]
    public bool Quiesce { get; set; }
}
