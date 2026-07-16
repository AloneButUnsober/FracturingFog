// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/JobFetchDto.cs
// Client → Master, body of job.fetch. The master replies first with a
// JobFetchAckDto (declaring size + hash + chunkCount + extension), then
// streams the artifact bytes in ChunkDto envelopes — same chunked path
// as render.image/video in the single-server protocol so existing
// client-side reassembly code can be reused.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobFetchRequestDto
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
}

public sealed class JobFetchAckDto
{
    [JsonPropertyName("jobId")]      public string JobId      { get; set; } = "";

    /// <summary>"png" | "mp4" | "mkv" — extension without the dot. Drives
    /// the client's save-as default.</summary>
    [JsonPropertyName("artifactExt")]  public string ArtifactExt  { get; set; } = "png";

    [JsonPropertyName("totalBytes")]  public long   TotalBytes  { get; set; }
    [JsonPropertyName("chunkCount")]  public int    ChunkCount  { get; set; }

    /// <summary>Base64 SHA-256 of the whole artifact. Client recomputes
    /// after reassembly; mismatch means a bug between disk-read and
    /// JSON-encode that TLS authentication cannot catch.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

public sealed class JobCancelRequestDto
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
}

public sealed class JobCancelAckDto
{
    [JsonPropertyName("jobId")]    public string JobId    { get; set; } = "";
    [JsonPropertyName("previousState")] public string PreviousState { get; set; } = "";

    /// <summary>True when the master observed the cancel and transitioned
    /// the job to "cancelled". False when the job was already in a
    /// terminal state (ready/failed/cancelled) and the cancel is a no-op.</summary>
    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }
}
