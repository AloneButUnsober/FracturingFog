// ServerHost/ClusterParitySelfTest.cs
// D-2b acceptance: render a small image two ways, byte-compare the
// merged BGRA buffers.
//
//   1. Single-server path  — HostFractalRenderEngine.RenderAsync on the
//                            full image.
//   2. Cluster tile path   — TilePlanner.PlanImage → for each tile,
//                            HostFractalRenderEngine.RenderAsync on the
//                            per-tile DTO → ArtifactMerger.TryMergePngTile.
//
// In-process, no network. Skips FFServer + SslStream wiring because that
// path is already covered by ClusterEndToEndImageTests. What's new here
// is that the *real* engine, fed the *real* per-tile RenderRequestDto
// values produced by TilePlanner, renders pixels that paste back into a
// bit-identical full-image buffer.
//
// Output: "cluster-parity.out" next to the exe (matches the rest of the
// self-test family — --silk-smoke, --ilgpu-probe, --gentest etc.).

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;

namespace FracturingFog.ServerHost;

public static class ClusterParitySelfTest
{
    public static int Run(string[] args)
    {
        int width  = 256;
        int height = 128;
        int tilePx = 64;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--width":   if (int.TryParse(args[i + 1], out var w)) width = w; break;
                case "--height":  if (int.TryParse(args[i + 1], out var h)) height = h; break;
                case "--tile-px": if (int.TryParse(args[i + 1], out var t)) tilePx = t; break;
            }
        }

        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        string outPath = Path.Combine(AppContext.BaseDirectory, "cluster-parity.out");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"cluster-parity self-test (D-2b)");
        sb.AppendLine($"  dims={width}x{height}, tilePx={tilePx}");
        sb.AppendLine();

        var engine = new HostFractalRenderEngine();
        var codec  = new SkiaClusterImageCodec();
        var log    = new StdoutLog();
        var ct     = CancellationToken.None;

        var baseReq = new RenderRequestDto
        {
            Mode        = "image",
            FractalType = "Mandelbrot",
            Width       = width,
            Height      = height,
            CenterX     = -0.5, CenterY = 0.0, Zoom = 1.0,
            QualityName = "Standard",
            ThemeName   = "Hsv",
            ReturnMode  = "saved",
            // Both arms render decoration-free so the comparison sees the
            // calculator's raw output. TilePlanner sets the same flag on
            // each per-tile DTO; setting it here keeps the single-server
            // baseline apples-to-apples.
            SuppressDecorations = true,
        };

        int rc;
        try
        {
            byte[] fullBgra   = RenderFullBgra(engine, codec, baseReq, log, ct, sb);
            byte[] mergedBgra = RenderTiledBgra(engine, codec, baseReq, tilePx, log, ct, sb);
            int rcPng         = CompareBuffers(fullBgra, mergedBgra, width, height, sb, "png-path");
            // D-3 raw-RGBA wire path: worker decodes PNG→BGRA itself, ships
            // raw bytes via the binary envelope trailer; master pastes via
            // TryMergeRgbaTile (no master-side decode). Exercises the same
            // codec on the worker side and `TryMergeRgbaTile` on the
            // merger side.
            byte[] mergedRgbaBgra = RenderTiledBgraViaRgbaWire(engine, codec, baseReq, tilePx, log, ct, sb);
            int rcRgba            = CompareBuffers(fullBgra, mergedRgbaBgra, width, height, sb, "rgba-path");
            rc = (rcPng != 0 || rcRgba != 0) ? 1 : 0;
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

    private static byte[] RenderFullBgra(
        HostFractalRenderEngine engine, IClusterImageCodec codec,
        RenderRequestDto baseReq, ISessionLog log, CancellationToken ct,
        System.Text.StringBuilder sb)
    {
        string workDir = NewWorkDir("full");
        try
        {
            var req = Clone(baseReq);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var art = engine.RenderAsync(req, workDir, log, ct).GetAwaiter().GetResult();
            sw.Stop();
            byte[] png = File.ReadAllBytes(art.FilePath);
            byte[] bgra = codec.DecodePngToBgra(png, out int dw, out int dh);
            sb.AppendLine($"  single-server : {sw.ElapsedMilliseconds,6} ms, png={png.Length:N0} B, decoded={dw}x{dh}");
            if (dw != baseReq.Width || dh != baseReq.Height)
                throw new InvalidDataException(
                    $"single-server decoded dims {dw}x{dh} != requested {baseReq.Width}x{baseReq.Height}");
            return bgra;
        }
        finally { TryDelete(workDir); }
    }

    private static byte[] RenderTiledBgra(
        HostFractalRenderEngine engine, IClusterImageCodec codec,
        RenderRequestDto baseReq, int tilePx, ISessionLog log, CancellationToken ct,
        System.Text.StringBuilder sb)
    {
        var plan = TilePlanner.PlanImage(baseReq, tilePx, workerPrefHints: null);
        sb.AppendLine($"  tile plan     : {plan.Columns}x{plan.Rows} = {plan.TileCount} tiles, target={plan.TileTargetPixels} px");

        using var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, codec);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        foreach (var tile in plan.Tiles)
        {
            string workDir = NewWorkDir($"tile-{tile.TileId}");
            try
            {
                var art = engine.RenderAsync(tile.Render, workDir, log, ct).GetAwaiter().GetResult();
                byte[] png = File.ReadAllBytes(art.FilePath);
                if (!merger.TryMergePngTile(tile.TileId, tile.OffsetX, tile.OffsetY,
                                            tile.Render.Width, tile.Render.Height, png))
                    throw new InvalidOperationException($"merger refused tile {tile.TileId}");
                n++;
            }
            finally { TryDelete(workDir); }
        }
        sw.Stop();
        sb.AppendLine($"  cluster path  : {sw.ElapsedMilliseconds,6} ms, {n} tiles rendered + merged");

        if (!merger.IsComplete)
            throw new InvalidOperationException($"merger incomplete: {merger.TilesAccepted}/{merger.TilesTotal}");

        // Pull the merged BGRA via WritePng → decode round-trip so the
        // comparison uses the same codec on both sides.
        string mergedPath = Path.Combine(NewWorkDir("merged"), "merged.png");
        merger.WritePng(mergedPath);
        byte[] mergedPng = File.ReadAllBytes(mergedPath);
        byte[] mergedBgra = codec.DecodePngToBgra(mergedPng, out int dw, out int dh);
        TryDelete(Path.GetDirectoryName(mergedPath)!);
        if (dw != plan.ImageWidth || dh != plan.ImageHeight)
            throw new InvalidDataException(
                $"merged decoded dims {dw}x{dh} != plan {plan.ImageWidth}x{plan.ImageHeight}");
        return mergedBgra;
    }

    private static byte[] RenderTiledBgraViaRgbaWire(
        HostFractalRenderEngine engine, IClusterImageCodec codec,
        RenderRequestDto baseReq, int tilePx, ISessionLog log, CancellationToken ct,
        System.Text.StringBuilder sb)
    {
        var plan = TilePlanner.PlanImage(baseReq, tilePx, workerPrefHints: null);

        using var merger = new ArtifactMerger(plan.ImageWidth, plan.ImageHeight, plan.TileCount, codec);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        foreach (var tile in plan.Tiles)
        {
            string workDir = NewWorkDir($"rgba-tile-{tile.TileId}");
            try
            {
                var art = engine.RenderAsync(tile.Render, workDir, log, ct).GetAwaiter().GetResult();
                byte[] png = File.ReadAllBytes(art.FilePath);
                // Worker-side decode → ship raw BGRA. Mirrors D-3 worker
                // path; the master then takes the RGBA branch in the
                // coordinator and merges via TryMergeRgbaTile.
                byte[] bgra = codec.DecodePngToBgra(png, out int dw, out int dh);
                if (dw != tile.Render.Width || dh != tile.Render.Height)
                    throw new InvalidDataException(
                        $"rgba-path tile {tile.TileId}: codec produced {dw}x{dh}, expected {tile.Render.Width}x{tile.Render.Height}");
                if (!merger.TryMergeRgbaTile(tile.TileId, tile.OffsetX, tile.OffsetY,
                                             tile.Render.Width, tile.Render.Height, bgra))
                    throw new InvalidOperationException($"merger refused rgba tile {tile.TileId}");
                n++;
            }
            finally { TryDelete(workDir); }
        }
        sw.Stop();
        sb.AppendLine($"  rgba path     : {sw.ElapsedMilliseconds,6} ms, {n} tiles rendered + merged");

        if (!merger.IsComplete)
            throw new InvalidOperationException($"rgba merger incomplete: {merger.TilesAccepted}/{merger.TilesTotal}");

        string mergedPath = Path.Combine(NewWorkDir("rgba-merged"), "merged.png");
        merger.WritePng(mergedPath);
        byte[] mergedPng = File.ReadAllBytes(mergedPath);
        byte[] mergedBgra = codec.DecodePngToBgra(mergedPng, out int dw2, out int dh2);
        TryDelete(Path.GetDirectoryName(mergedPath)!);
        if (dw2 != plan.ImageWidth || dh2 != plan.ImageHeight)
            throw new InvalidDataException(
                $"rgba merged decoded dims {dw2}x{dh2} != plan {plan.ImageWidth}x{plan.ImageHeight}");
        return mergedBgra;
    }

    private static int CompareBuffers(byte[] full, byte[] tiled, int w, int h, System.Text.StringBuilder sb, string label)
    {
        if (full.LongLength != tiled.LongLength)
        {
            sb.AppendLine($"FAIL ({label}): buffer sizes differ ({full.LongLength} vs {tiled.LongLength})");
            return 1;
        }
        long diffBytes = 0;
        long diffPixels = 0;
        long maxAbsChannelDelta = 0;
        for (long i = 0; i < full.LongLength; i += 4)
        {
            bool pixelDiffers = false;
            for (int c = 0; c < 4; c++)
            {
                int d = Math.Abs(full[i + c] - tiled[i + c]);
                if (d != 0) { diffBytes++; pixelDiffers = true; if (d > maxAbsChannelDelta) maxAbsChannelDelta = d; }
            }
            if (pixelDiffers) diffPixels++;
        }
        long totalPixels = (long)w * h;
        sb.AppendLine();
        sb.AppendLine($"  [{label}] pixels        : {totalPixels:N0} total");
        sb.AppendLine($"  [{label}] diff pixels   : {diffPixels:N0}");
        sb.AppendLine($"  [{label}] diff bytes    : {diffBytes:N0}");
        sb.AppendLine($"  [{label}] max ΔchannelB : {maxAbsChannelDelta}");
        if (diffPixels == 0)
        {
            sb.AppendLine($"  [{label}] PARITY OK     : single-server and cluster paths produce byte-identical pixels.");
            return 0;
        }
        sb.AppendLine($"  [{label}] PARITY FAIL   : {diffPixels}/{totalPixels} pixels differ.");
        return 1;
    }

    private static string NewWorkDir(string label)
    {
        string name = $"ff-parity-{label}-{Guid.NewGuid():N}";
        if (name.Length > 56) name = name[..56];
        string p = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDelete(string dir)
    { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { } }

    private static RenderRequestDto Clone(RenderRequestDto r) => new()
    {
        Mode = r.Mode, FractalType = r.FractalType,
        Width = r.Width, Height = r.Height,
        CenterX = r.CenterX, CenterY = r.CenterY, Zoom = r.Zoom,
        CenterXLo = r.CenterXLo, CenterX2 = r.CenterX2, CenterX3 = r.CenterX3,
        CenterYLo = r.CenterYLo, CenterY2 = r.CenterY2, CenterY3 = r.CenterY3,
        Iterations = r.Iterations, RegionName = r.RegionName, RegionJson = r.RegionJson,
        ThemeName = r.ThemeName, ThemeJson = r.ThemeJson,
        QualityName = r.QualityName, OutputName = r.OutputName,
        ReturnMode = r.ReturnMode, Lossless = r.Lossless,
        SuppressDecorations = r.SuppressDecorations,
    };

    private sealed class StdoutLog : ISessionLog
    {
        public void Info(string line) { /* suppressed — parity test stays quiet */ }
        public void Warn(string line) => Console.Error.WriteLine("[parity] WARN " + line);
        public void Err (string line) => Console.Error.WriteLine("[parity] ERR  " + line);
    }
}
