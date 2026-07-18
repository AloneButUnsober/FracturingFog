// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/Protocol/FrameRangeDto.cs
// Master → Worker, optional payload attached to TileJobDto when the
// parent job is video mode. Carries a half-open contiguous frame index
// range [StartFrame, EndFrame) plus the log-zoom interpolation params
// the worker uses to produce each frame's per-frame zoom. The tile's
// Render dto carries the per-frame template (fractal type, region,
// centre, theme, dims) — only the per-frame zoom varies inside the
// range.
//
// Wire shape: identical math to BatchRenderer.RenderVideo —
//   t       = frame / (TotalFrames - 1)
//   smooth  = t * t * (3 - 2 * t)               // smoothstep
//   zoom    = exp(LogStartZoom + LogZoomDelta * smooth)
// LogStartZoom and LogZoomDelta are pre-computed on the master so the
// worker doesn't need the original start/end zoom and the planner can
// honour --video-reverse uniformly.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class FrameRangeDto
{
    /// <summary>0-based index of the first frame this tile owns.</summary>
    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    /// <summary>0-based exclusive index of the last frame this tile owns.
    /// Tile renders frames StartFrame .. EndFrame - 1. EndFrame > StartFrame
    /// is enforced at plan time.</summary>
    [JsonPropertyName("endFrame")]
    public int EndFrame { get; set; }

    /// <summary>Total frames in the parent video. Worker needs it to
    /// compute the smoothstep parameter (t depends on the global frame
    /// count, not the tile's local range).</summary>
    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; set; }

    /// <summary>Frames per second of the parent video. Carried for log /
    /// progress display on the worker; the encode pass on the master is
    /// the authoritative consumer.</summary>
    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    /// <summary>log(startZoom) of the parent video. Worker computes
    /// per-frame zoom as exp(LogStartZoom + LogZoomDelta * smoothstep(t)).</summary>
    [JsonPropertyName("logStartZoom")]
    public double LogStartZoom { get; set; }

    /// <summary>log(endZoom) - log(startZoom). Negative for zoom-out
    /// (--video-reverse) so the same code path covers both directions.</summary>
    [JsonPropertyName("logZoomDelta")]
    public double LogZoomDelta { get; set; }
}
