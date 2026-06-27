// Server/Cluster/Protocol/TileDeliverDto.cs
// Worker → Master, body of tile.deliver. Carries the rendered tile
// pixels back to the master so they can be merged into the final image.
//
// D-2: single inline message per tile (PNG bytes, base64) on the JSON
// path — BytesBase64 populated, no envelope trailer.
// D-3: raw bytes ride the envelope's binary trailer instead (PayloadKind
// = "rgba" or "png"; BytesBase64 left empty). Server prefers the
// trailer when present and falls back to BytesBase64 for older workers.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class TileDeliverDto
{
    [JsonPropertyName("workerId")] public string WorkerId { get; set; } = "";
    [JsonPropertyName("jobId")]    public string JobId    { get; set; } = "";
    [JsonPropertyName("tileId")]   public int    TileId   { get; set; }

    /// <summary>"png" | "rgba". D-2 ships PNG (worker calls its existing
    /// encoder so engine wiring stays minimal). D-3 will add "rgba"
    /// for raw uncompressed transport — saves encode/decode CPU at the
    /// price of more bytes on the wire.</summary>
    [JsonPropertyName("payloadKind")]
    public string PayloadKind { get; set; } = "png";

    /// <summary>Tile width × height in pixels — must match the
    /// TileJobDto the master sent. Master refuses delivery if not.</summary>
    [JsonPropertyName("width")]  public int Width  { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    /// <summary>Base64-encoded payload. For PNG, this is the raw .png
    /// file bytes. For RGBA (D-3), 4 bytes per pixel, top-to-bottom,
    /// row-major.</summary>
    [JsonPropertyName("bytesBase64")]
    public string BytesBase64 { get; set; } = "";

    /// <summary>Base64 SHA-256 of the decoded bytes. Master verifies
    /// before merging — mismatch fails the tile (retried elsewhere).</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Wall-clock ms the worker spent rendering this tile.
    /// Master collects for adaptive sizing in D-3.</summary>
    [JsonPropertyName("renderMs")]
    public long RenderMs { get; set; }
}

public sealed class TileDeliverAckDto
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }

    /// <summary>Why the master refused the tile. Codes:
    /// "unknown-job", "tile-not-in-flight", "size-mismatch",
    /// "sha-mismatch", "job-cancelled". Worker decides whether to retry
    /// the call (it generally should not — master will reassign).</summary>
    [JsonPropertyName("refuseReason")]
    public string? RefuseReason { get; set; }
}
