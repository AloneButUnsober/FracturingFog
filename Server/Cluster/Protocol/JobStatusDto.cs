// Server/Cluster/Protocol/JobStatusDto.cs
// Master → Client, response to job.status. State machine + progress.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobStatusRequestDto
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
}

public sealed class JobStatusDto
{
    /// <summary>queued | planning | rendering | merging | ready | failed | cancelled</summary>
    [JsonPropertyName("jobState")]
    public string JobState { get; set; } = "";

    [JsonPropertyName("tilesTotal")]    public int    TilesTotal    { get; set; }
    [JsonPropertyName("tilesDone")]     public int    TilesDone     { get; set; }
    [JsonPropertyName("tilesInFlight")] public int    TilesInFlight { get; set; }

    [JsonPropertyName("progressPercent")] public double ProgressPercent { get; set; }
    [JsonPropertyName("elapsedMs")]       public long   ElapsedMs       { get; set; }

    /// <summary>Null when the planner hasn't yet recorded enough tiles to
    /// estimate, or after the job reaches a terminal state.</summary>
    [JsonPropertyName("etaMs")]
    public long? EtaMs { get; set; }

    /// <summary>True when an artifact is on disk and job.fetch will
    /// succeed. Only set when JobState=="ready".</summary>
    [JsonPropertyName("artifactReady")]
    public bool ArtifactReady { get; set; }

    [JsonPropertyName("artifactBytes")]
    public long ArtifactBytes { get; set; }

    /// <summary>Populated when JobState=="failed". Caller may show or log
    /// it; do not parse for routing.</summary>
    [JsonPropertyName("failReason")]
    public string? FailReason { get; set; }
}
