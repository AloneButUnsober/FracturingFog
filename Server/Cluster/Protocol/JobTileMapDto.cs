// Server/Cluster/Protocol/JobTileMapDto.cs
// Admin → Master. cluster.jobTileMap returns per-tile rect + state +
// assigned/delivering workerId so the admin JobDetailView can paint a
// grid coloured by worker.
//
// Distinct from job.status (which is high-rate, client-facing, counters-
// only) because the tile-list payload scales with TileCount and would
// bloat every status poll. JobDetailView polls this at 2 s — high enough
// to track progress visibly, low enough that mTLS handshake CPU stays
// modest on a job with hundreds of tiles.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobTileMapRequestDto
{
    [JsonPropertyName("jobId")] public string JobId { get; set; } = "";
}

public sealed class JobTileMapDto
{
    [JsonPropertyName("jobId")]     public string JobId    { get; set; } = "";
    [JsonPropertyName("jobState")]  public string JobState { get; set; } = "";

    /// <summary>image | video | slideshow — the UI uses this to decide
    /// whether to render a spatial grid (image) or a flat tile/slide/frame
    /// list (video/slideshow).</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";

    /// <summary>Full image dimensions for image-mode jobs. 0/0 for video
    /// (every tile is a frame range over the whole image so the spatial
    /// grid degenerates to one cell) and for slideshow (one tile per
    /// slide, no shared canvas).</summary>
    [JsonPropertyName("imageWidth")]  public int ImageWidth  { get; set; }
    [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }

    [JsonPropertyName("tilesTotal")] public int TilesTotal { get; set; }
    [JsonPropertyName("tilesDone")]  public int TilesDone  { get; set; }
    [JsonPropertyName("tilesInFlight")] public int TilesInFlight { get; set; }

    [JsonPropertyName("tiles")] public List<TileMapEntryDto> Tiles { get; set; } = new();
}

public sealed class TileMapEntryDto
{
    [JsonPropertyName("tileId")] public int TileId { get; set; }

    /// <summary>Tile rect in image-mode coordinates. All zero for video /
    /// slideshow tiles (no spatial layout — the UI falls back to a flat
    /// list).</summary>
    [JsonPropertyName("offsetX")] public int OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public int OffsetY { get; set; }
    [JsonPropertyName("width")]   public int Width   { get; set; }
    [JsonPropertyName("height")]  public int Height  { get; set; }

    /// <summary>pending | inflight | completed. Failed tiles that the
    /// dispatcher requeued reappear as <c>pending</c>; the whole-job
    /// failure state is on <see cref="JobTileMapDto.JobState"/>.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "pending";

    /// <summary>Worker id that owns this tile right now. Null for pending;
    /// the assignee for <c>inflight</c>; the first deliverer for
    /// <c>completed</c>. Lets the UI assign a stable per-worker colour
    /// from the dashboard's workers list.</summary>
    [JsonPropertyName("workerId")] public string? WorkerId { get; set; }
}
