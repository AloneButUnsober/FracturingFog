// Server/Cluster/Protocol/ClusterStatusDto.cs
// Admin → Master, response to cluster.status. Snapshot of every connected
// worker plus a short list of recent jobs so the admin UI can render the
// dashboard without a second round-trip per row.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class ClusterStatusRequestDto
{
    /// <summary>Cap on the number of recent jobs returned. Default 50 if
    /// omitted; clamped server-side to [1, 500] so a misconfigured admin
    /// UI cannot pull the entire job history in one call.</summary>
    [JsonPropertyName("recentJobLimit")]
    public int? RecentJobLimit { get; set; }
}

public sealed class ClusterStatusDto
{
    /// <summary>Wall-clock unix seconds at the moment the snapshot was
    /// built. Admin UI uses it to detect staleness when poll cadence
    /// drifts.</summary>
    [JsonPropertyName("serverUnixSeconds")]
    public long ServerUnixSeconds { get; set; }

    /// <summary>Heartbeat cadence the master expects from workers — the
    /// admin UI uses 3× this value to draw the "stale" threshold.</summary>
    [JsonPropertyName("heartbeatIntervalSeconds")]
    public int HeartbeatIntervalSeconds { get; set; }

    /// <summary>Count of workers currently within the live window
    /// (LastHeartbeatUtc within 3× HeartbeatIntervalSeconds).</summary>
    [JsonPropertyName("liveWorkerCount")]
    public int LiveWorkerCount { get; set; }

    /// <summary>Per-worker snapshot. Includes both live and stale entries
    /// (UI filters by LiveBy field if it wants live-only).</summary>
    [JsonPropertyName("workers")]
    public List<WorkerSummaryDto> Workers { get; set; } = new();

    /// <summary>Most recent N jobs, newest first. Pulled from the
    /// JobStore — covers everything still on disk, not just in-flight.</summary>
    [JsonPropertyName("jobs")]
    public List<JobSummaryDto> Jobs { get; set; } = new();
}

public sealed class WorkerSummaryDto
{
    [JsonPropertyName("workerId")]            public string WorkerId { get; set; } = "";
    [JsonPropertyName("workerName")]          public string WorkerName { get; set; } = "";
    [JsonPropertyName("osPlatform")]          public string OsPlatform { get; set; } = "";
    [JsonPropertyName("cpuModel")]            public string CpuModel { get; set; } = "";
    [JsonPropertyName("logicalCores")]        public int    LogicalCores { get; set; }
    [JsonPropertyName("totalRamBytes")]       public long   TotalRamBytes { get; set; }
    [JsonPropertyName("gpus")]                public List<string> Gpus { get; set; } = new();
    [JsonPropertyName("maxConcurrentTiles")]  public int    MaxConcurrentTiles { get; set; }
    [JsonPropertyName("preferredTilePixels")] public int    PreferredTilePixels { get; set; }

    /// <summary>Live load from the most recent heartbeat. -1 cpuPercent
    /// means the worker can't sample it on its platform.</summary>
    [JsonPropertyName("tilesInFlight")] public int    TilesInFlight { get; set; }
    [JsonPropertyName("cpuPercent")]    public double CpuPercent { get; set; }
    [JsonPropertyName("freeRamBytes")]  public long   FreeRamBytes { get; set; }
    [JsonPropertyName("lastNote")]      public string? LastNote { get; set; }

    /// <summary>Master-clock unix seconds of the most recent successful
    /// heartbeat. Admin UI computes age against ServerUnixSeconds.</summary>
    [JsonPropertyName("lastHeartbeatUnixSeconds")]
    public long LastHeartbeatUnixSeconds { get; set; }

    [JsonPropertyName("registeredAtUnixSeconds")]
    public long RegisteredAtUnixSeconds { get; set; }

    [JsonPropertyName("quiesced")] public bool Quiesced { get; set; }

    /// <summary>EMA ms-per-kilopixel reported by the registry. 0 means no
    /// samples yet.</summary>
    [JsonPropertyName("emaMsPerKilopixel")]
    public double EmaMsPerKilopixel { get; set; }

    [JsonPropertyName("tileSamples")] public int TileSamples { get; set; }
}

public sealed class JobSummaryDto
{
    [JsonPropertyName("jobId")]      public string JobId { get; set; } = "";
    [JsonPropertyName("jobState")]   public string JobState { get; set; } = "";

    [JsonPropertyName("tilesTotal")]    public int TilesTotal { get; set; }
    [JsonPropertyName("tilesDone")]     public int TilesDone { get; set; }
    [JsonPropertyName("tilesInFlight")] public int TilesInFlight { get; set; }

    [JsonPropertyName("totalFrames")]   public int TotalFrames { get; set; }
    [JsonPropertyName("framesDone")]    public int FramesDone { get; set; }
    [JsonPropertyName("encodedFrames")] public int EncodedFrames { get; set; }

    [JsonPropertyName("progressPercent")] public double ProgressPercent { get; set; }

    [JsonPropertyName("createdUnixMs")]    public long CreatedUnixMs { get; set; }
    [JsonPropertyName("lastUpdateUnixMs")] public long LastUpdateUnixMs { get; set; }

    [JsonPropertyName("artifactBytes")] public long ArtifactBytes { get; set; }
    [JsonPropertyName("artifactExt")]   public string? ArtifactExt { get; set; }

    [JsonPropertyName("failReason")]    public string? FailReason { get; set; }

    /// <summary>Mode (image|video|slideshow) carried out of the persisted
    /// plan so the UI can group rows without a per-job round-trip for the
    /// request body.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
}
