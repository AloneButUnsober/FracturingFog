// Server/Cluster/Protocol/JobListDto.cs
// Admin → Master. Paged list of jobs from the on-disk store. Distinct
// from cluster.status' embedded recent-jobs block because the admin UI
// may want to filter (e.g. failed-only) or page deeper without bloating
// every status poll.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobListRequestDto
{
    /// <summary>Cap on jobs returned. Default 50; clamped server-side to
    /// [1, 500].</summary>
    [JsonPropertyName("limit")] public int? Limit { get; set; }

    /// <summary>When true, includes ready/failed/cancelled jobs alongside
    /// in-flight ones. Defaults to true.</summary>
    [JsonPropertyName("includeTerminal")] public bool? IncludeTerminal { get; set; }

    /// <summary>Filter to one state ("rendering", "ready", "failed", ...).
    /// Empty/null returns all states subject to IncludeTerminal.</summary>
    [JsonPropertyName("stateFilter")] public string? StateFilter { get; set; }
}

public sealed class JobListDto
{
    /// <summary>Total job count on disk (pre-filter). Lets the UI show
    /// "showing 50 of 312" without a second call.</summary>
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }

    /// <summary>Jobs matching the filter, newest first.</summary>
    [JsonPropertyName("jobs")] public List<JobSummaryDto> Jobs { get; set; } = new();
}
