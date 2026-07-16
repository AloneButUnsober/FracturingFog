// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/TileErrorDto.cs
// Worker → Master, body of tile.error. Reports a tile the worker failed
// to render (engine threw, timed out, guard refused). Master decides
// whether to retry on a different worker.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class TileErrorDto
{
    [JsonPropertyName("workerId")] public string WorkerId { get; set; } = "";
    [JsonPropertyName("jobId")]    public string JobId    { get; set; } = "";
    [JsonPropertyName("tileId")]   public int    TileId   { get; set; }

    /// <summary>"engine-failed" | "timeout" | "forbidden-fractal" |
    /// "limit-exceeded" | "cancelled". Master uses for retry policy
    /// (cancelled / forbidden = do not retry, others = retry up to
    /// RetryBudget on a different worker).</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    /// <summary>Free-text. Echoed into the cluster event log.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class TileErrorAckDto
{
    /// <summary>Master observed the error. Worker MUST NOT retry the
    /// tile itself — the master reassigns.</summary>
    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }
}
