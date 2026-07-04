// Server/Wire/MessageEnvelope.cs
// Top-level JSON shape sent over the wire. Every frame is one envelope,
// either Request (Method + Id + Params) or Response (Id + Result | Error).
//
// D-3 adds the optional binary trailer: when an envelope advertises
// binaryLength > 0, the receiver reads that many raw bytes immediately
// after the JSON frame and surfaces them as the in-process Binary
// property. Avoids base64 + JSON-string overhead on the tile-delivery
// hot path (raw RGBA can be 1 MB+ per tile; base64 grows that 33 %).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Server.Wire;

public sealed class MessageEnvelope
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "request";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; set; }

    /// <summary>Length in bytes of the raw binary trailer that follows
    /// this envelope's JSON frame. 0 / unset means the envelope is
    /// JSON-only (backward compatible with D-1 / D-2 readers).</summary>
    [JsonPropertyName("binaryLength")]
    public int BinaryLength { get; set; }

    /// <summary>In-process carrier for the binary trailer. NOT serialised
    /// (see [JsonIgnore]); the wire bytes travel separately, after the
    /// JSON frame. Writer sets <see cref="BinaryLength"/> from Binary.Length
    /// automatically; reader populates Binary from the trailing bytes.</summary>
    [JsonIgnore]
    public byte[]? Binary { get; set; }
}
