// Server/Cluster/Protocol/TileJobDto.cs
// Master → Worker, payload returned by tile.next when there's actually
// work to do. Carries one tile of one job: the sub-rect to render and
// the per-tile RenderRequestDto the worker should hand to its engine.
//
// Pixel geometry: the master pre-computes Render so the worker treats it
// as a normal render of (Render.Width × Render.Height). The OffsetX/Y
// fields exist purely so the worker can identify the tile when it
// streams pixels back via tile.deliver — the worker never re-derives
// world coordinates from them.

using System.Text.Json.Serialization;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class TileJobDto
{
    [JsonPropertyName("jobId")]  public string JobId  { get; set; } = "";

    /// <summary>Tile index within the job's plan. 0-based, dense
    /// (no holes), matches the TilePlanner's plan output order.</summary>
    [JsonPropertyName("tileId")]
    public int TileId { get; set; }

    /// <summary>0-based pixel offset of this tile's top-left corner in
    /// the final merged image, measured from the image's top-left.</summary>
    [JsonPropertyName("offsetX")] public int OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public int OffsetY { get; set; }

    /// <summary>Final-image dimensions. The worker does NOT use these
    /// directly — the per-tile Render.Width/Render.Height fields drive
    /// its engine. They're carried so the worker logs are useful
    /// ("tile 12/64 of 8192×8192 image").</summary>
    [JsonPropertyName("imageWidth")]  public int ImageWidth  { get; set; }
    [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }

    /// <summary>The per-tile render. Width/Height are the tile pixel
    /// dimensions; CenterX/CenterY/Zoom are the master-translated
    /// coordinates so a normal render of this DTO produces the pixels
    /// that belong in the final image at (OffsetX, OffsetY).</summary>
    [JsonPropertyName("render")]
    public RenderRequestDto Render { get; set; } = new();

    /// <summary>Per-tile deadline in seconds. Master retries on a
    /// different worker if the tile is not delivered in time.</summary>
    [JsonPropertyName("deadlineSeconds")]
    public int DeadlineSeconds { get; set; } = 120;

    /// <summary>Which delivery attempt this is, 1-based. Master uses it
    /// for log correlation; workers may log it but otherwise ignore.</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; } = 1;

    /// <summary>D-4 — when non-null, this tile owns a contiguous range of
    /// video frames instead of a 2-D pixel rect. Worker iterates the
    /// range, derives per-frame Zoom from the smoothstep schedule, and
    /// delivers all frames in one tile.deliver call with
    /// PayloadKind="frames" (see <see cref="TileDeliverDto"/>). For
    /// image-mode tiles this stays null.</summary>
    [JsonPropertyName("frameRange")]
    public FrameRangeDto? FrameRange { get; set; }
}
