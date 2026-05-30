// Server/Wire/MessageEnvelope.cs
// Top-level JSON shape sent over the wire. Every frame is one envelope,
// either Request (Method + Id + Params) or Response (Id + Result | Error).

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
}
