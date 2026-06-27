// Server/Cluster/Protocol/JobSubmitDto.cs
// Client → Master, body of job.submit. Wraps a RenderRequestDto with
// distribution-only hints. The render fields themselves stay in
// RenderRequestDto so the single-server protocol can keep using the
// same DTO unchanged.

using System.Text.Json.Serialization;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobSubmitDto
{
    /// <summary>The render the client wants done — same DTO used by
    /// render.image / render.video in the single-server protocol.</summary>
    [JsonPropertyName("request")]
    public RenderRequestDto Request { get; set; } = new();

    /// <summary>Operator priority hint, 0..100. Higher is more urgent.
    /// Master uses it for queue ordering only; never as a scheduling
    /// guarantee. Default 50.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;

    /// <summary>If &gt; 0, master tries to size tiles around this many
    /// pixels per side. 0 = let the planner pick from worker hints.</summary>
    [JsonPropertyName("tilePixelsHint")]
    public int TilePixelsHint { get; set; }
}
