// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/FramePlanner.cs
// Splits a video RenderRequestDto into frame-range tiles. Each tile owns
// a contiguous half-open [startFrame, endFrame) range; the worker walks
// the range, derives per-frame zoom via the smoothstep schedule shared
// with BatchRenderer.RenderVideo, and ships all frames back in one
// tile.deliver call (PayloadKind="frames").
//
// Sharding strategy: per the dev plan §4 video subsection, frame-level
// sharding is the unit. The planner picks tile granularity from the
// requested frame-bundle target (DefaultFramesPerTile) so dispatch
// granularity is independent of total frames — a short 60-frame zoom
// runs as a handful of tiles; a long 1800-frame render runs as ~75.
//
// Math mirrors BatchRenderer.RenderVideo exactly so a single-server
// video and the cluster-merged video are frame-for-frame identical:
//   logZ0 = log(startZoom)
//   logZ1 = log(endZoom)        (swapped when VideoReverse is true)
//   t     = totalFrames == 1 ? 1 : f / (totalFrames - 1)
//   te    = t * t * (3 - 2 * t)
//   zoomF = exp(logZ0 + (logZ1 - logZ0) * te)

using System;
using System.Collections.Generic;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster;

public static class FramePlanner
{
    /// <summary>Target frame count per tile. Lower = finer dispatch
    /// granularity but more JSON-RPC chatter; higher = less overhead but
    /// stragglers waste more wall-time. 30 ≈ one second of 30 fps video
    /// — empirically a good balance for the LAN case.</summary>
    public const int DefaultFramesPerTile = 30;

    public const int MinFramesPerTile = 1;
    public const int MaxFramesPerTile = 600;

    /// <summary>Lower bound on total frame count. A 1-frame "video" would
    /// degenerate the smoothstep maths; bounce the caller back to image
    /// mode in that case.</summary>
    public const int MinTotalFrames = 2;

    /// <summary>Upper bound on total frame count. 18000 = 10 minutes at
    /// 30 fps. Protects the master from a runaway request that would
    /// otherwise allocate 18k tile slots and equally many per-frame
    /// PNG files on disk.</summary>
    public const int MaxTotalFrames = 18000;

    /// <summary>Per-tile bytes-on-the-wire ceiling for the frames trailer.
    /// FramesPayloadCodec packs raw PNGs in one blob; cap conservatively
    /// at ~64 MB to stay well under the JsonRpcFraming 256 MB frame cap
    /// even with worst-case 1080p PNGs. Planner picks an effective tile
    /// frame count satisfying this implicitly via DefaultFramesPerTile;
    /// the field exists for documentation + future tightening.</summary>
    public const long FramesTrailerSoftCap = 64L * 1024 * 1024;

    public static bool ValidateForVideo(string fractalType, out string? reason)
    {
        // The per-frame render is a normal image render of one zoom
        // level, so the same per-pixel coord formula applies. Fall back
        // through TilePlanner's allowlist — anything tileable at image
        // mode is tileable at video mode too.
        return TilePlanner.ValidateForTiling(fractalType, out reason);
    }

    public static TilePlanner.Plan PlanVideo(
        RenderRequestDto request,
        int framesPerTileHint = 0)
    {
        if (!string.Equals(request.Mode, "video", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"FramePlanner.PlanVideo called with non-video mode '{request.Mode}'");

        if (request.Width <= 0 || request.Height <= 0)
            throw new ArgumentException(
                $"invalid frame dims {request.Width}×{request.Height}");
        if (request.Zoom is null or 0)
            throw new ArgumentException("Zoom is required for video planning (target zoom)");
        if (request.CenterX is null || request.CenterY is null)
            throw new ArgumentException("CenterX/CenterY required for video planning");
        if (request.VideoFps <= 0)
            throw new ArgumentException($"invalid VideoFps {request.VideoFps}");
        if (request.VideoSeconds <= 0)
            throw new ArgumentException($"invalid VideoSeconds {request.VideoSeconds}");

        int totalFrames = (int)Math.Round(request.VideoSeconds * request.VideoFps);
        if (totalFrames < MinTotalFrames)
            throw new ArgumentException(
                $"video too short: {totalFrames} frames < min {MinTotalFrames}");
        if (totalFrames > MaxTotalFrames)
            throw new ArgumentException(
                $"video too long: {totalFrames} frames > max {MaxTotalFrames}");

        double startZoom = Math.Max(request.VideoStartZoom, 1e-12);
        double endZoom   = Math.Max(request.Zoom!.Value, 1e-12);
        if (request.VideoReverse) (startZoom, endZoom) = (endZoom, startZoom);

        double logStart = Math.Log(startZoom);
        double logDelta = Math.Log(endZoom) - logStart;

        int framesPerTile = framesPerTileHint > 0
            ? Math.Clamp(framesPerTileHint, MinFramesPerTile, MaxFramesPerTile)
            : DefaultFramesPerTile;

        // Even-snap output frame size to match the encoder requirement.
        // Mirrors BatchRenderer's snap so single-server and cluster
        // outputs share dimensions for the ffprobe parity test.
        int outW = request.Width & ~1; if (outW < 16) outW = 16;
        int outH = request.Height & ~1; if (outH < 16) outH = 16;

        var tiles = new List<TileJobDto>((totalFrames + framesPerTile - 1) / framesPerTile);
        int tileId = 0;
        for (int start = 0; start < totalFrames; start += framesPerTile)
        {
            int end = Math.Min(totalFrames, start + framesPerTile);

            // Per-frame render template. Worker overrides Zoom each
            // iteration; everything else (theme, region, fractal type,
            // dims) stays identical across the range.
            var tileReq = CloneFrameTemplate(request, outW, outH);

            tiles.Add(new TileJobDto
            {
                TileId      = tileId++,
                OffsetX     = start,           // legacy field carries frame start for log clarity
                OffsetY     = 0,
                ImageWidth  = outW,
                ImageHeight = outH,
                Render      = tileReq,
                FrameRange  = new FrameRangeDto
                {
                    StartFrame   = start,
                    EndFrame     = end,
                    TotalFrames  = totalFrames,
                    Fps          = request.VideoFps,
                    LogStartZoom = logStart,
                    LogZoomDelta = logDelta,
                },
            });
        }

        return new TilePlanner.Plan
        {
            ImageWidth       = outW,
            ImageHeight      = outH,
            TileTargetPixels = 0,        // not meaningful for video
            Columns          = 1,
            Rows             = tiles.Count,
            Tiles            = tiles,
            Mode             = "video",
            TotalFrames      = totalFrames,
        };
    }

    /// <summary>How many frames per tile a hint of <paramref name="hint"/>
    /// would resolve to. Exposed for tests + the coordinator's plan
    /// audit log.</summary>
    public static int ResolveFramesPerTile(int hint)
        => hint > 0 ? Math.Clamp(hint, MinFramesPerTile, MaxFramesPerTile) : DefaultFramesPerTile;

    private static RenderRequestDto CloneFrameTemplate(RenderRequestDto src, int outW, int outH)
    {
        // The per-frame request renders ONE image at one zoom level.
        // Mode is "image" so the worker's engine picks the image path;
        // FrameRange on the parent TileJobDto signals to the worker code
        // to iterate the range and substitute Zoom per frame.
        return new RenderRequestDto
        {
            Mode                 = "image",
            RegionName           = src.RegionName,
            FractalType          = src.FractalType,
            CenterX              = src.CenterX,
            CenterY              = src.CenterY,
            Zoom                 = src.Zoom,        // overwritten per frame
            Iterations           = src.Iterations,
            CenterXLo            = src.CenterXLo,
            CenterX2             = src.CenterX2,
            CenterX3             = src.CenterX3,
            CenterYLo            = src.CenterYLo,
            CenterY2             = src.CenterY2,
            CenterY3             = src.CenterY3,
            // D-6g — OD-limbs propagation (mirrors TilePlanner's image-tile
            // copy). Zero at DD/QD zoom; non-zero only when the submission
            // carried an OD-precision centre. Without these, a cluster video
            // at zoom > 1e50 silently degrades to DD precision per frame.
            CenterX4             = src.CenterX4,
            CenterX5             = src.CenterX5,
            CenterX6             = src.CenterX6,
            CenterX7             = src.CenterX7,
            CenterY4             = src.CenterY4,
            CenterY5             = src.CenterY5,
            CenterY6             = src.CenterY6,
            CenterY7             = src.CenterY7,
            ThemeName            = src.ThemeName,
            QualityName          = src.QualityName,
            ThemeJson            = src.ThemeJson,
            RegionJson           = src.RegionJson,
            Width                = outW,
            Height               = outH,
            OutputName           = null,
            ReturnMode           = "inline",
            // Per-tile renders skip watermarking — the master applies any
            // overlay once at encode time (D-4b). Mirrors the image-tile
            // SuppressDecorations=true convention from TilePlanner.
            SuppressDecorations  = true,
        };
    }
}
