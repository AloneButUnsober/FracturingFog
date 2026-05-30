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
}
