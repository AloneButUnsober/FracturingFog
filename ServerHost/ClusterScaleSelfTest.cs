// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ServerHost/ClusterScaleSelfTest.cs
// D-3b / D-4 acceptance harness: render the same workload two ways and
// report wall-time + speedup. Both arms use the real
// HostFractalRenderEngine and the real planner / TileDispatcher — they
// differ only in how many concurrent in-process "workers" pull tiles.
//
//   1. baseline   — single thread, plan + render + (image: merge) in order.
//   2. distributed — N concurrent worker tasks pulling from a shared
//                    TileDispatcher; exercises the D-3b work-stealing
//                    and adaptive sizing code paths end-to-end without
//                    TCP/TLS framing.
//
// Modes (default image, override with --mode video):
//   image  → TilePlanner.PlanImage  + ArtifactMerger
//   video  → FramePlanner.PlanVideo + per-frame PNG to disk (no encode;
//            the encode pass is timed separately by
//            cluster-video-parity which exists for D-4 fidelity, not
//            speedup).
//
// The harness skips the FFServer SslStream layer because the wire
// path is already covered by ClusterEndToEnd*Tests; what's new here is
// the parallel-execution speedup measurement called for by the dev
// plan §9 D-3 / D-4 exit criteria. Output: "cluster-scale.out" next
// to the exe.
//
// Invocation:
//   FracturingFog --cluster-scale [--mode image|video]
//                                 [--width N] [--height N] [--tile-px N]
//                                 [--workers N] [--center X,Y --zoom Z]
//                                 [--seconds N] [--fps N]
//                                 [--frames-per-tile N]
//
// Default profile is a small Mandelbrot view that finishes in a few
// seconds even on a slow CPU. Override --center / --zoom / --width to
// run a Bird-of-Paradise 8K stress, or --mode video --seconds 20 --fps
// 30 --width 1920 --height 1080 to time the dev-plan video scenario.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Protocol;

namespace FracturingFog.ServerHost;

public static class ClusterScaleSelfTest
{
    public static int Run(string[] args)
    {
        int width  = 512;
        int height = 512;
        int tilePx = 128;
        int workerCount = Math.Min(4, Environment.ProcessorCount);
        double centerX = -0.5;
        double centerY = 0.0;
        double zoom    = 1.0;
        string fractalType = "Mandelbrot";
        string region = "";
        string mode = "image";
        double videoSeconds = 1.0;
        int    videoFps     = 10;
        double zoomStart    = 0.5;
        int    framesPerTileHint = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? v = (i + 1 < args.Length) ? args[i + 1] : null;
            switch (a)
            {
                case "--width":   if (int.TryParse(v, out var w)) { width = w; i++; } break;
                case "--height":  if (int.TryParse(v, out var h)) { height = h; i++; } break;
                case "--tile-px": if (int.TryParse(v, out var t)) { tilePx = t; i++; } break;
                case "--workers": if (int.TryParse(v, out var n)) { workerCount = Math.Max(1, n); i++; } break;
                case "--zoom":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    { zoom = z; i++; }
                    break;
                case "--center":
                    if (v != null)
                    {
                        var parts = v.Split(',');
                        if (parts.Length == 2
                            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)
                            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
                        { centerX = cx; centerY = cy; i++; }
                    }
                    break;
                case "--fractal":
                    if (v != null) { fractalType = v; i++; }
                    break;
                case "--region":
                    if (v != null) { region = v; i++; }
                    break;
                case "--mode":
                    if (v != null) { mode = v.ToLowerInvariant(); i++; }
                    break;
                case "--seconds":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vs))
                    { videoSeconds = vs; i++; }
                    break;
                case "--fps":
                    if (int.TryParse(v, out var vf)) { videoFps = vf; i++; }
                    break;
                case "--zoom-start":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var zs))
                    { zoomStart = zs; i++; }
                    break;
                case "--frames-per-tile":
                    if (int.TryParse(v, out var fpt)) { framesPerTileHint = fpt; i++; }
                    break;
            }
        }

        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        string outPath = Path.Combine(AppContext.BaseDirectory, "cluster-scale.out");
        var sb = new StringBuilder();
        sb.AppendLine($"cluster-scale self-test (mode={mode})");
        sb.AppendLine($"  dims={width}x{height}, tilePx={tilePx}, workers={workerCount}");
        sb.AppendLine($"  fractal={fractalType}, center=({centerX},{centerY}), zoom={zoom}");
        if (mode == "video")
            sb.AppendLine($"  seconds={videoSeconds}, fps={videoFps}, zoomStart={zoomStart}, framesPerTile={(framesPerTileHint > 0 ? framesPerTileHint.ToString() : "default")}");
        if (!string.IsNullOrEmpty(region)) sb.AppendLine($"  region={region}");
        sb.AppendLine();

        var engine = new HostFractalRenderEngine();
        var codec  = new SkiaClusterImageCodec();
        var log    = new SilentLog();
        var ct     = CancellationToken.None;

        int rc;
        try
        {
            long baselineMs;
            long parallelMs;
            switch (mode)
            {
                case "image":
                {
                    var baseReq = new RenderRequestDto
                    {
                        Mode        = "image",
                        FractalType = fractalType,
                        RegionName  = string.IsNullOrEmpty(region) ? null : region,
                        Width       = width,
                        Height      = height,
                        CenterX     = centerX,
                        CenterY     = centerY,
                        Zoom        = zoom,
                        QualityName = "Standard",
                        ThemeName   = "Hsv",
                        ReturnMode  = "saved",
                        SuppressDecorations = true,
                    };
                    baselineMs = RunBaseline(engine, codec, baseReq, tilePx, log, ct, sb);
                    parallelMs = RunParallel(engine, codec, baseReq, tilePx, workerCount, log, ct, sb);
                    break;
                }
                case "video":
                {
                    var baseReq = new RenderRequestDto
                    {
                        Mode             = "video",
                        FractalType      = fractalType,
                        RegionName       = string.IsNullOrEmpty(region) ? null : region,
                        Width            = width,
                        Height           = height,
                        CenterX          = centerX,
                        CenterY          = centerY,
                        Zoom             = zoom,
                        VideoStartZoom   = zoomStart,
                        VideoSeconds     = videoSeconds,
                        VideoFps         = videoFps,
                        QualityName      = "Standard",
                        ThemeName        = "Hsv",
                        ReturnMode       = "saved",
                        SuppressDecorations = true,
                    };
                    baselineMs = RunBaselineVideo(engine, baseReq, log, ct, sb);
                    parallelMs = RunParallelVideo(engine, baseReq, framesPerTileHint, workerCount, log, ct, sb);
                    break;
                }
                default:
                    throw new ArgumentException($"unknown --mode '{mode}' (expected image|video)");
            }

            double speedup = baselineMs / (double)Math.Max(1, parallelMs);
            double efficiency = speedup / workerCount * 100.0;
            sb.AppendLine();
            sb.AppendLine($"  speedup        : {speedup:F2}x");
            sb.AppendLine($"  efficiency     : {efficiency:F1}% (target ≥ 80% per dev-plan §9 D-3)");
            // Non-strict exit — operators read the report; CI gates the
            // numeric criteria separately.
            rc = 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            rc = 1;
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Console.Write(sb.ToString());
        return rc;
    }

    private static long RunBaseline(
        HostFractalRenderEngine engine, IClusterImageCodec codec,
        RenderRequestDto baseReq, int tilePx, ISessionLog log,
        CancellationToken ct, StringBuilder sb)
    {
        var plan = TilePlanner.PlanImage(baseReq, tilePx, workerPrefHints: null);
        sb.AppendLine($"  baseline plan  : {plan.Columns}x{plan.Rows} = {plan.TileCount} tiles, target={plan.TileTargetPixels}px");

        using var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, codec);
        var sw = Stopwatch.StartNew();
        foreach (var tile in plan.Tiles)
        {
            string wd = NewWorkDir($"base-{tile.TileId}");
            try
            {
                var art = engine.RenderAsync(tile.Render, wd, log, ct).GetAwaiter().GetResult();
                byte[] png = File.ReadAllBytes(art.FilePath);
                byte[] bgra = codec.DecodePngToBgra(png, out int dw, out int dh);
                if (dw != tile.Render.Width || dh != tile.Render.Height)
                    throw new InvalidDataException(
                        $"baseline tile {tile.TileId}: codec produced {dw}x{dh}, expected {tile.Render.Width}x{tile.Render.Height}");
                merger.TryMergeRgbaTile(tile.TileId, tile.OffsetX, tile.OffsetY, tile.Render.Width, tile.Render.Height, bgra);
            }
            finally { TryDelete(wd); }
        }
        sw.Stop();
        if (!merger.IsComplete) throw new InvalidOperationException(
            $"baseline incomplete: {merger.TilesAccepted}/{merger.TilesTotal}");
        sb.AppendLine($"  baseline       : {sw.ElapsedMilliseconds,8:N0} ms (1 worker, sequential)");
        return sw.ElapsedMilliseconds;
    }

    private static long RunParallel(
        HostFractalRenderEngine engine, IClusterImageCodec codec,
        RenderRequestDto baseReq, int tilePx, int workerCount, ISessionLog log,
        CancellationToken ct, StringBuilder sb)
    {
        var plan = TilePlanner.PlanImage(baseReq, tilePx, workerPrefHints: null);
        const string jobId = "scale-job";
        foreach (var t in plan.Tiles) t.JobId = jobId;

        var disp = new TileDispatcher
        {
            // Shorten the steal warm-up so a small render still exercises
            // the path; production keeps the default 2 s.
            StealMinAge = TimeSpan.FromMilliseconds(250),
            StealMinTotalTiles = 4,
        };
        disp.EnqueueJob(jobId, plan.Tiles);
        using var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, codec);
        var perWorkerTiles = new int[workerCount];
        var perWorkerSteals = new int[workerCount];
        var sw = Stopwatch.StartNew();
        var workerTasks = new Task[workerCount];
        for (int wi = 0; wi < workerCount; wi++)
        {
            int idx = wi;
            string wid = $"sw{idx}";
            workerTasks[idx] = Task.Run(async () =>
            {
                while (!merger.IsComplete && !ct.IsCancellationRequested)
                {
                    var tile = await disp.ClaimNextAsync(wid, TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    if (tile is null)
                    {
                        if (merger.IsComplete) return;
                        continue;
                    }
                    string wd = NewWorkDir($"par-{wid}-{tile.TileId}");
                    try
                    {
                        var art = await engine.RenderAsync(tile.Render, wd, log, ct).ConfigureAwait(false);
                        byte[] png = await File.ReadAllBytesAsync(art.FilePath, ct).ConfigureAwait(false);
                        byte[] bgra = codec.DecodePngToBgra(png, out int dw, out int dh);
                        bool merged = merger.TryMergeRgbaTile(
                            tile.TileId, tile.OffsetX, tile.OffsetY,
                            tile.Render.Width, tile.Render.Height, bgra);
                        if (merged) Interlocked.Increment(ref perWorkerTiles[idx]);
                        else        Interlocked.Increment(ref perWorkerSteals[idx]);
                        disp.AcceptDelivery(jobId, tile.TileId, $"sw{idx}");
                    }
                    finally { TryDelete(wd); }
                }
            });
        }
        Task.WaitAll(workerTasks);
        sw.Stop();
        if (!merger.IsComplete) throw new InvalidOperationException(
            $"parallel incomplete: {merger.TilesAccepted}/{merger.TilesTotal}");

        sb.AppendLine($"  parallel       : {sw.ElapsedMilliseconds,8:N0} ms ({workerCount} workers, work-stealing on)");
        for (int i = 0; i < workerCount; i++)
        {
            sb.AppendLine($"    sw{i,-2}        : {perWorkerTiles[i],4} merged, {perWorkerSteals[i],3} stolen-duplicate (lost the race)");
        }
        return sw.ElapsedMilliseconds;
    }

    private static long RunBaselineVideo(
        HostFractalRenderEngine engine, RenderRequestDto baseReq,
        ISessionLog log, CancellationToken ct, StringBuilder sb)
    {
        int totalFrames = (int)Math.Round(baseReq.VideoSeconds * baseReq.VideoFps);
        double startZoom = Math.Max(baseReq.VideoStartZoom, 1e-12);
        double endZoom   = Math.Max(baseReq.Zoom!.Value,    1e-12);
        if (baseReq.VideoReverse) (startZoom, endZoom) = (endZoom, startZoom);
        double logStart = Math.Log(startZoom);
        double logDelta = Math.Log(endZoom) - logStart;
        int outW = baseReq.Width  & ~1; if (outW < 16) outW = 16;
        int outH = baseReq.Height & ~1; if (outH < 16) outH = 16;

        sb.AppendLine($"  baseline plan  : 1 thread, {totalFrames} frames @ {outW}x{outH}");
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < totalFrames; f++)
        {
            double t = totalFrames == 1 ? 1.0 : (double)f / (totalFrames - 1);
            double te = t * t * (3.0 - 2.0 * t);
            double zoomF = Math.Exp(logStart + logDelta * te);

            var perFrame = CloneVideoFrame(baseReq, outW, outH);
            perFrame.Zoom = zoomF;

            string wd = NewWorkDir($"base-vid-{f}");
            try
            {
                var art = engine.RenderAsync(perFrame, wd, log, ct).GetAwaiter().GetResult();
                _ = File.ReadAllBytes(art.FilePath);   // touch result so disk I/O time stays comparable
            }
            finally { TryDelete(wd); }
        }
        sw.Stop();
        sb.AppendLine($"  baseline       : {sw.ElapsedMilliseconds,8:N0} ms (1 worker, sequential frames)");
        return sw.ElapsedMilliseconds;
    }

    private static long RunParallelVideo(
        HostFractalRenderEngine engine, RenderRequestDto baseReq,
        int framesPerTileHint, int workerCount,
        ISessionLog log, CancellationToken ct, StringBuilder sb)
    {
        var plan = FramePlanner.PlanVideo(baseReq, framesPerTileHint);
        const string jobId = "scale-vid-job";
        foreach (var t in plan.Tiles) t.JobId = jobId;

        sb.AppendLine($"  cluster plan   : {plan.Tiles.Count} tiles, totalFrames={plan.TotalFrames}, frame-dims={plan.ImageWidth}x{plan.ImageHeight}");

        var disp = new TileDispatcher
        {
            StealMinAge        = TimeSpan.FromMilliseconds(250),
            StealMinTotalTiles = 4,
        };
        disp.EnqueueJob(jobId, plan.Tiles);

        var perWorkerTiles  = new int[workerCount];
        var perWorkerFrames = new int[workerCount];
        var sw = Stopwatch.StartNew();
        var workerTasks = new Task[workerCount];
        for (int wi = 0; wi < workerCount; wi++)
        {
            int idx = wi;
            string wid = $"sw{idx}";
            workerTasks[idx] = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var tile = await disp.ClaimNextAsync(wid, TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    if (tile is null) return;
                    var fr = tile.FrameRange ?? throw new InvalidOperationException(
                        "FramePlanner produced a tile without FrameRange");
                    for (int f = fr.StartFrame; f < fr.EndFrame; f++)
                    {
                        double t = fr.TotalFrames == 1 ? 1.0 : (double)f / (fr.TotalFrames - 1);
                        double te = t * t * (3.0 - 2.0 * t);
                        double zoomF = Math.Exp(fr.LogStartZoom + fr.LogZoomDelta * te);

                        var perFrame = CloneVideoFrame(tile.Render, tile.Render.Width, tile.Render.Height);
                        perFrame.Zoom = zoomF;

                        string wd = NewWorkDir($"par-vid-{wid}-{f}");
                        try
                        {
                            var art = await engine.RenderAsync(perFrame, wd, log, ct).ConfigureAwait(false);
                            _ = await File.ReadAllBytesAsync(art.FilePath, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref perWorkerFrames[idx]);
                        }
                        finally { TryDelete(wd); }
                    }
                    disp.AcceptDelivery(jobId, tile.TileId, $"vw{idx}");
                    Interlocked.Increment(ref perWorkerTiles[idx]);
                }
            });
        }
        Task.WaitAll(workerTasks);
        sw.Stop();
        sb.AppendLine($"  parallel       : {sw.ElapsedMilliseconds,8:N0} ms ({workerCount} workers, work-stealing on)");
        for (int i = 0; i < workerCount; i++)
            sb.AppendLine($"    sw{i,-2}        : {perWorkerTiles[i],4} tiles, {perWorkerFrames[i],5} frames");
        return sw.ElapsedMilliseconds;
    }

    private static RenderRequestDto CloneVideoFrame(RenderRequestDto src, int outW, int outH) => new()
    {
        Mode                 = "image",
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
        Width                = outW,
        Height               = outH,
        OutputName           = null,
        ReturnMode           = "inline",
        SuppressDecorations  = true,
    };

    private static string NewWorkDir(string label)
    {
        string name = $"ff-scale-{label}-{Guid.NewGuid():N}";
        if (name.Length > 56) name = name[..56];
        string p = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDelete(string dir)
    { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { } }

    private sealed class SilentLog : ISessionLog
    {
        public void Info(string line) { }
        public void Warn(string line) => Console.Error.WriteLine("[scale] WARN " + line);
        public void Err (string line) => Console.Error.WriteLine("[scale] ERR  " + line);
    }
}
