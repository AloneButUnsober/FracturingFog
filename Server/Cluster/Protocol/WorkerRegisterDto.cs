// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/WorkerRegisterDto.cs
// Worker → Master. First call on a freshly opened cluster session.
// Announces capabilities and identity. Master pins the cert thumbprint at
// first sight (or verifies the pin matches on reconnect) and returns a
// stable WorkerId via WorkerRegisterAckDto.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class WorkerRegisterDto
{
    /// <summary>Operator-set name. Shown in admin UI; does not need to be
    /// unique. Truncated to 64 chars by the master.</summary>
    [JsonPropertyName("workerName")]
    public string WorkerName { get; set; } = "";

    /// <summary>"win" | "linux" | "macos".</summary>
    [JsonPropertyName("osPlatform")]
    public string OsPlatform { get; set; } = "";

    /// <summary>Free-form CPU brand string (e.g. "AMD Ryzen 9 7950X").</summary>
    [JsonPropertyName("cpuModel")]
    public string CpuModel { get; set; } = "";

    [JsonPropertyName("logicalCores")]
    public int LogicalCores { get; set; }

    [JsonPropertyName("totalRamBytes")]
    public long TotalRamBytes { get; set; }

    /// <summary>Free-form GPU adapter names (one per discovered adapter).
    /// Empty list signals a CPU-only worker.</summary>
    [JsonPropertyName("gpus")]
    public List<string> Gpus { get; set; } = new();

    /// <summary>Fractal types the worker's engine can render. Master uses
    /// this for tile assignment — workers without a given type are skipped
    /// when sharding a job of that type.</summary>
    [JsonPropertyName("supportedFractalTypes")]
    public List<string> SupportedFractalTypes { get; set; } = new();

    /// <summary>How many tiles the worker is willing to run in parallel.
    /// Default 1. Master never exceeds this regardless of queue pressure.</summary>
    [JsonPropertyName("maxConcurrentTiles")]
    public int MaxConcurrentTiles { get; set; } = 1;

    /// <summary>Worker's preferred tile edge length in pixels. Master uses
    /// it as a hint when sizing tiles; actual tile size may differ.</summary>
    [JsonPropertyName("preferredTilePixels")]
    public int PreferredTilePixels { get; set; } = 512;

    /// <summary>Build SHA of the engine assembly. Master refuses workers
    /// whose engine SHA does not match its own — protects against tile
    /// output divergence across version skew (risk #7 in the dev plan).</summary>
    [JsonPropertyName("engineBuildSha")]
    public string EngineBuildSha { get; set; } = "";

    /// <summary>Wire protocol version the worker speaks. Currently "1".
    /// Master refuses anything it does not know.</summary>
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "1";

    /// <summary>If the worker has a persisted WorkerId from a previous
    /// session, it re-presents it here so the master can resume the same
    /// identity. Master still verifies the cert thumbprint pin matches.
    /// Null on first-ever registration — master mints a fresh id.</summary>
    [JsonPropertyName("resumeWorkerId")]
    public string? ResumeWorkerId { get; set; }
}
