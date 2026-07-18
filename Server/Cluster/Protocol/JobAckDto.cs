// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/JobAckDto.cs
// Master → Client, response to job.submit. Carries the assigned JobId
// and the master's tile-plan summary so the caller knows up-front how
// big the work is.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobAckDto
{
    /// <summary>Cluster-unique job id. Crockford base32, 26 chars.</summary>
    [JsonPropertyName("jobId")]
    public string JobId { get; set; } = "";

    /// <summary>Number of tiles the planner produced. 1 for a job that
    /// didn't shard (slideshow per-slide, video frame, very small image).</summary>
    [JsonPropertyName("tileCount")]
    public int TileCount { get; set; }

    /// <summary>Server's best estimate of the final artifact size in
    /// bytes — clients use it to size receive buffers and gate fetch
    /// against disk free. 0 = unknown.</summary>
    [JsonPropertyName("estimatedBytes")]
    public long EstimatedBytes { get; set; }
}
