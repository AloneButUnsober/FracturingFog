// Server/Protocol/ErrorDto.cs
// Error payload returned in MessageEnvelope.Error when a request fails.
// Codes are short kebab-case strings the client can match on.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Protocol;

public sealed class ErrorDto
{
    /// <summary>"bad-request", "forbidden-fractal", "limit-exceeded",
    /// "timeout", "busy", "unknown-region", "unknown-theme",
    /// "ffmpeg-missing", "render-failed", "internal".</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "internal";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
