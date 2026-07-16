// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Protocol/ChunkDto.cs
// Payload of a "chunk" envelope. Sent after a streamed render response so
// neither side has to hold the full rendered artifact in RAM. Each chunk
// is read out of FileStream into a 1 MB pooled buffer, base64-encoded
// into a fresh string for the wire, then released. The receiver
// reassembles by concatenation (Seq must arrive in order, 0..Total-1).

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Protocol;

public sealed class ChunkDto
{
    [JsonPropertyName("seq")]   public int Seq   { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("bytesBase64")]
    public string BytesBase64 { get; set; } = "";

    /// <summary>Base64-encoded SHA-256 of the decoded chunk bytes. TLS
    /// already authenticates the stream, so this is defense in depth
    /// against an in-process bug (e.g. ArrayPool reuse error, file
    /// truncation, mid-stream disk swap) silently corrupting a chunk
    /// before encode. Optional for older clients — when present the
    /// receiver MUST verify.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
