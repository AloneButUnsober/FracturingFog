// Server/Cluster/TilePlanner.cs
// Splits an image RenderRequestDto into N tiles sized roughly around a
// target pixel edge. For each tile, computes the per-tile center+zoom
// so a normal worker render of the tile's (tW × tH) produces pixels
// identical to the same sub-rect of the (W × H) full render.
//
// Coord mapping (mirrors Engine/Calculators/MandelbrotCalculator.cs):
//   scale     = (3.5 / max(W,H))  / Zoom              ← world units / pixel
//   pixel→world for the full image:
//                cx = CenterX + (x - W/2) * scale
//                cy = CenterY + (y - H/2) * scale
//   For a tile at (offX, offY, tW, tH) to give identical pixels, the
//   per-tile render must use scale' == scale, which means:
//                zoom'    = Zoom * max(W,H) / max(tW,tH)
//                centerX' = CenterX + (offX + tW/2 - W/2) * scale
//                centerY' = CenterY + (offY + tH/2 - H/2) * scale
//
// The 3.5 / max(W,H) / Zoom formula is shared across the Mandelbrot
// family, BurningShip, Tricorn, Multibrot, Julia, Phoenix, Newton,
// Nova, Buddhabrot. Calculator paths that do NOT use this geometry
// (LSystem, IFS, StrangeAttractor, Mandelbulb*, TearDrop) are refused
// at planning time — distributed rendering for those types lands in
// a later phase.

using System;
using System.Collections.Generic;
using System.Globalization;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster;

public static class TilePlanner
{
    public const int DefaultTilePixels = 512;
    public const int MinTilePixels = 64;
    public const int MaxTilePixels = 8192;

    /// <summary>Default target wall-time per tile, in milliseconds.
    /// Adaptive sizing aims so the median worker finishes each tile in
    /// roughly this window — short enough to keep stragglers cheap, long
    /// enough that per-tile fixed costs (JSON-RPC frame, codec encode,
    /// per-tile workdir setup) stay under ~5 % of the tile budget.</summary>
    public const double DefaultTargetTileMs = 2000.0;

    /// <summary>Fractal types this planner can shard. All use the shared
    /// `(3.5 / max(W,H)) / Zoom` per-pixel world-unit formula. Other
    /// fractal types render the whole image as a single tile (or are
    /// refused — see <see cref="ValidateForTiling"/>).</summary>
    private static readonly HashSet<string> TileableTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Mandelbrot", "BurningShip", "Tricorn", "Multibrot",
            "Julia", "Phoenix", "Newton", "Nova", "BuddhaBrot",
        };

    public sealed class Plan
    {
        public required int ImageWidth  { get; init; }
        public required int ImageHeight { get; init; }
        public required int TileTargetPixels { get; init; }
        public required int Columns { get; init; }
        public required int Rows    { get; init; }
        public required IReadOnlyList<TileJobDto> Tiles { get; init; }
        public int TileCount => Tiles.Count;
    }

    /// <summary>Refuses a fractal type that the planner cannot tile (uses
    /// a different per-pixel formula). Returns true with a single-tile
    /// plan when the type IS tileable but the image fits in one tile.</summary>
    public static bool ValidateForTiling(string fractalType, out string? reason)
    {
        if (TileableTypes.Contains(fractalType))
        {
            reason = null;
            return true;
        }
        reason = $"fractal type '{fractalType}' uses a non-cartesian or non-zoom-scaled coord " +
                 "system; tiling support pending (planned for D-4+)";
        return false;
    }

    public static Plan PlanImage(
        RenderRequestDto request, int tilePixelsHint,
        IReadOnlyList<int>? workerPrefHints = null,
        double medianMsPerKilopixel = 0,
        double targetTileMs = DefaultTargetTileMs)
    {
        if (request.Width <= 0 || request.Height <= 0)
            throw new ArgumentException(
                $"invalid image dims {request.Width}×{request.Height}");
        if (request.Zoom is null or 0)
            throw new ArgumentException("Zoom is required for tiled planning");
        if (request.CenterX is null || request.CenterY is null)
            throw new ArgumentException("CenterX/CenterY required for tiled planning");

        int target = PickTargetPixels(tilePixelsHint, workerPrefHints, medianMsPerKilopixel, targetTileMs);

        int W = request.Width;
        int H = request.Height;
        int cols = Math.Max(1, (W + target - 1) / target);
        int rows = Math.Max(1, (H + target - 1) / target);

        double maxWH = Math.Max(W, H);
        double scale = (3.5 / maxWH) / request.Zoom!.Value;

        double cx0 = request.CenterX!.Value;
        double cy0 = request.CenterY!.Value;

        var tiles = new List<TileJobDto>(cols * rows);
        int tileId = 0;
        for (int row = 0; row < rows; row++)
        {
            int offY = row * target;
            int tH = Math.Min(target, H - offY);

            for (int col = 0; col < cols; col++)
            {
                int offX = col * target;
                int tW = Math.Min(target, W - offX);

                // Same per-pixel scale: zoom' = Zoom * max(W,H) / max(tW,tH)
                double tileMaxWH = Math.Max(tW, tH);
                double tileZoom = request.Zoom.Value * (maxWH / tileMaxWH);

                // Center of the tile, translated from the full image's center.
                double tileCx = cx0 + (offX + tW * 0.5 - W * 0.5) * scale;
                double tileCy = cy0 + (offY + tH * 0.5 - H * 0.5) * scale;

                var tileReq = CloneRequest(request);
                tileReq.Width   = tW;
                tileReq.Height  = tH;
                tileReq.CenterX = tileCx;
                tileReq.CenterY = tileCy;
                tileReq.Zoom    = tileZoom;
                // ReturnMode irrelevant for tile rendering — the worker
                // never persists per-tile artifacts on disk; pin to
                // "inline" so a stale "saved-path" doesn't make the
                // worker side-effect the host filesystem.
                tileReq.ReturnMode = "inline";
                // Mode is image — video tiling lands in D-4.
                tileReq.Mode = "image";
                // OutputName must not contain the original image's name —
                // workers may concurrently run multiple tiles of the same
                // job and writing to a shared file would race. Pinning
                // null lets the engine auto-derive a per-tile workdir name.
                tileReq.OutputName = null;
                // D-2b — tiles produce raw fractal pixels; watermark /
                // sub-text / region-branding land once at merge time on
                // the master (D-3+). Per-tile decoration would yield a
                // grid of mini-watermarks in the merged artifact.
                tileReq.SuppressDecorations = true;

                tiles.Add(new TileJobDto
                {
                    TileId      = tileId++,
                    OffsetX     = offX,
                    OffsetY     = offY,
                    ImageWidth  = W,
                    ImageHeight = H,
                    Render      = tileReq,
                });
            }
        }

        return new Plan
        {
            ImageWidth       = W,
            ImageHeight      = H,
            TileTargetPixels = target,
            Columns          = cols,
            Rows             = rows,
            Tiles            = tiles,
        };
    }

    private static int PickTargetPixels(
        int hint, IReadOnlyList<int>? workerHints,
        double medianMsPerKilopixel, double targetTileMs)
    {
        if (hint > 0) return Math.Clamp(hint, MinTilePixels, MaxTilePixels);

        // D-3b adaptive sizing: with a learned per-tile cost, pick a tile
        // side that lands the median worker inside the target window.
        // pixels = tileKpx * 1000; side ≈ sqrt(pixels) for square tiles.
        if (medianMsPerKilopixel > 0 && targetTileMs > 0)
        {
            double tileKpx = targetTileMs / medianMsPerKilopixel;
            int side = (int)Math.Round(Math.Sqrt(tileKpx * 1000.0));
            if (side >= MinTilePixels) return Math.Clamp(side, MinTilePixels, MaxTilePixels);
        }

        if (workerHints != null && workerHints.Count > 0)
        {
            // Use the median worker hint — defends against one outlier
            // worker advertising a giant or microscopic preferred size.
            var copy = new List<int>(workerHints);
            copy.Sort();
            int median = copy[copy.Count / 2];
            if (median > 0) return Math.Clamp(median, MinTilePixels, MaxTilePixels);
        }
        return DefaultTilePixels;
    }

    /// <summary>Exposes the same adaptive math used inside <see cref="PlanImage"/>
    /// so the coordinator (and tests) can preview the tile side a plan
    /// will land on. Returns 0 when no data is available.</summary>
    public static int ComputeAdaptiveTilePixels(double medianMsPerKilopixel, double targetTileMs = DefaultTargetTileMs)
    {
        if (medianMsPerKilopixel <= 0 || targetTileMs <= 0) return 0;
        double tileKpx = targetTileMs / medianMsPerKilopixel;
        int side = (int)Math.Round(Math.Sqrt(tileKpx * 1000.0));
        return Math.Clamp(side, MinTilePixels, MaxTilePixels);
    }

    private static RenderRequestDto CloneRequest(RenderRequestDto src)
    {
        // Field-by-field clone is the right shape here — RenderRequestDto
        // is a flat record-like DTO with no shared mutable substructures
        // we'd need to deep-copy. Serializer round-trip would also work
        // but is wasteful per tile.
        return new RenderRequestDto
        {
            Mode                 = src.Mode,
            RegionName           = src.RegionName,
            FractalType          = src.FractalType,
            CenterX              = src.CenterX,
            CenterY              = src.CenterY,
            Zoom                 = src.Zoom,
            Iterations           = src.Iterations,
            CenterXLo            = src.CenterXLo,
            CenterX2             = src.CenterX2,
            CenterX3             = src.CenterX3,
            CenterYLo            = src.CenterYLo,
            CenterY2             = src.CenterY2,
            CenterY3             = src.CenterY3,
            ThemeName            = src.ThemeName,
            QualityName          = src.QualityName,
            ThemeJson            = src.ThemeJson,
            RegionJson           = src.RegionJson,
            Width                = src.Width,
            Height               = src.Height,
            OutputName           = src.OutputName,
            VideoSeconds         = src.VideoSeconds,
            VideoFps             = src.VideoFps,
            VideoStartZoom       = src.VideoStartZoom,
            VideoReverse         = src.VideoReverse,
            Lossless             = src.Lossless,
            KeepFrames           = src.KeepFrames,
            ReturnMode           = src.ReturnMode,
            RequestedMaxMinutes  = src.RequestedMaxMinutes,
            PosterInchesW        = src.PosterInchesW,
            PosterInchesH        = src.PosterInchesH,
            PosterDpi            = src.PosterDpi,
            PosterPortrait       = src.PosterPortrait,
            UseClientWatermark   = src.UseClientWatermark,
            ClientWatermarkJson  = src.ClientWatermarkJson,
            SuppressDecorations  = src.SuppressDecorations,
        };
    }

    /// <summary>Diagnostic-only string form of the planning math —
    /// useful in test failures.</summary>
    public static string DescribeMath(RenderRequestDto request, int tile)
    {
        if (request.Zoom is null) return "(no zoom)";
        double maxWH = Math.Max(request.Width, request.Height);
        double scale = (3.5 / maxWH) / request.Zoom.Value;
        return string.Format(CultureInfo.InvariantCulture,
            "W={0} H={1} Zoom={2:G6} scale={3:G6} target={4}",
            request.Width, request.Height, request.Zoom, scale, tile);
    }
}
