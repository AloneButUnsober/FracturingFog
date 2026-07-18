// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/WorkerControlDto.cs
// Admin → Master. Per-worker control surface: quiesce (drain), resume
// (clear quiesce flag), kill (evict from registry). Routed under the
// cluster.* namespace so it falls under the admin-only role gate; the
// worker.* prefix is reserved for worker→master traffic.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class WorkerQuiesceRequestDto
{
    [JsonPropertyName("workerId")] public string WorkerId { get; set; } = "";

    /// <summary>True to drain (no new tiles dispatched, in-flight finish),
    /// false to resume normal dispatch. Mirrors WorkerEntry.Quiesced.</summary>
    [JsonPropertyName("quiesced")] public bool Quiesced { get; set; }
}

public sealed class WorkerQuiesceAckDto
{
    [JsonPropertyName("workerId")]      public string WorkerId { get; set; } = "";
    [JsonPropertyName("previousState")] public bool   PreviousState { get; set; }
    [JsonPropertyName("currentState")]  public bool   CurrentState { get; set; }
}

public sealed class WorkerKillRequestDto
{
    [JsonPropertyName("workerId")] public string WorkerId { get; set; } = "";
}

public sealed class WorkerKillAckDto
{
    [JsonPropertyName("workerId")] public string WorkerId { get; set; } = "";

    /// <summary>True if the worker was in the registry and has been
    /// removed; false if the id was already unknown (idempotent).</summary>
    [JsonPropertyName("removed")] public bool Removed { get; set; }
}
