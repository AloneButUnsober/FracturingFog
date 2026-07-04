// ServerHost/ClusterVideoParitySelfTest.cs
// D-4 exit criterion: render a short zoom two ways, prove the cluster
// path's frames are bit-identical to the single-server path AND the
// encoded video files match under ffprobe + per-frame framemd5.
//
//   1. baseline     — single thread, walks frames sequentially. Smoothstep
//                     log-zoom schedule is the same one FFWorkerAgent uses,
//                     so per-frame inputs match the cluster path exactly.
//   2. cluster      — FramePlanner.PlanVideo → TileDispatcher → N worker
//                     tasks pull frame-range tiles and render in parallel.
//                     Frames land in frame_NNNNNN.png matching the
//                     JobStore convention so VideoFramePipeline can ingest
//                     them unchanged.
//
// Both arms feed VideoFramePipeline when ffmpeg is on disk; FFV1 is the
// default preset because it's deterministic across runs. ffprobe stream
// metadata + per-frame framemd5 establish encode parity. Per-frame PNG
// SHA-256 establishes render parity (the stronger check — if the PNGs
// match, the only way the encoded files can diverge is encoder
// non-determinism, which FFV1 explicitly avoids).
//
// Output: "cluster-video-parity.out" next to the exe. Exit 0 on full
// parity (frames AND encode), 1 on any mismatch.
//
// Invocation:
//   FracturingFog --cluster-video-parity
//                 [--seconds N] [--fps N] [--width N] [--height N]
//                 [--workers N] [--lossless ffv1|h264|h264hq|none]
//                 [--center X,Y] [--zoom-start Z] [--zoom-end Z]
//                 [--fractal Mandelbrot]
//
// Defaults render a tiny ~10-frame zoom that finishes in a few seconds.
// Pump --seconds 20 --fps 30 --width 1920 --height 1080 to hit the
// dev-plan §9 D-4 ffprobe parity scenario verbatim.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;

namespace FracturingFog.ServerHost;

public static class ClusterVideoParitySelfTest
{
    public static int Run(string[] args)
    {
        double seconds   = 1.0;
        int    fps       = 10;
        int    width     = 160;
        int    height    = 120;
        int    workers   = Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1));
        string lossless  = "ffv1";
        double centerX   = -0.5;
        double centerY   = 0.0;
        double zoomStart = 0.5;
        double zoomEnd   = 4.0;
        string fractalType = "Mandelbrot";
        int    framesPerTileHint = 0;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? v = (i + 1 < args.Length) ? args[i + 1] : null;
            switch (a)
            {
                case "--seconds":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                    { seconds = s; i++; }
                    break;
                case "--fps":     if (int.TryParse(v, out var f))  { fps = f; i++; } break;
                case "--width":   if (int.TryParse(v, out var w))  { width = w; i++; } break;
                case "--height":  if (int.TryParse(v, out var h))  { height = h; i++; } break;
                case "--workers": if (int.TryParse(v, out var n))  { workers = Math.Max(1, n); i++; } break;
                case "--frames-per-tile":
                    if (int.TryParse(v, out var fpt)) { framesPerTileHint = fpt; i++; }
                    break;
                case "--lossless":
                    if (v != null) { lossless = v; i++; }
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
                case "--zoom-start":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var zs))
                    { zoomStart = zs; i++; }
                    break;
                case "--zoom-end":
                    if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var ze))
                    { zoomEnd = ze; i++; }
                    break;
                case "--fractal":
                    if (v != null) { fractalType = v; i++; }
                    break;
            }
        }

        try { FracturingFog.Models.ColorPalette.LoadUserThemes(); } catch { }
        try { FracturingFog.Models.FractalRegionLibrary.Instance.Load(); } catch { }

        string outPath = Path.Combine(AppContext.BaseDirectory, "cluster-video-parity.out");
        var sb = new StringBuilder();
        sb.AppendLine("cluster-video-parity self-test (D-4)");
        sb.AppendLine($"  dims={width}x{height}, seconds={seconds}, fps={fps}, workers={workers}");
        sb.AppendLine($"  fractal={fractalType}, center=({centerX},{centerY}), zoom={zoomStart}->{zoomEnd}");
        sb.AppendLine($"  preset={lossless}, framesPerTile={(framesPerTileHint > 0 ? framesPerTileHint.ToString() : "default")}");
        sb.AppendLine();

        int totalFrames = (int)Math.Round(seconds * fps);
        if (totalFrames < FramePlanner.MinTotalFrames)
        {
            sb.AppendLine($"FAIL: totalFrames={totalFrames} below MinTotalFrames={FramePlanner.MinTotalFrames}");
            Console.Write(sb.ToString());
            try { File.WriteAllText(outPath, sb.ToString()); } catch { }
            return 1;
        }

        var engine = new HostFractalRenderEngine();
        var log    = new SilentLog();
        var ct     = CancellationToken.None;

        var baseReq = new RenderRequestDto
        {
            Mode             = "video",
            FractalType      = fractalType,
            Width            = width,
            Height           = height,
            CenterX          = centerX,
            CenterY          = centerY,
            Zoom             = zoomEnd,
            VideoStartZoom   = zoomStart,
            VideoSeconds     = seconds,
            VideoFps         = fps,
            VideoReverse     = false,
            QualityName      = "Standard",
            ThemeName        = "Hsv",
            ReturnMode       = "saved",
            SuppressDecorations = true,
            Lossless         = lossless,
        };

        string rootDir = Path.Combine(Path.GetTempPath(), $"ff-vparity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        string framesA = Path.Combine(rootDir, "baseline-frames");
        string framesB = Path.Combine(rootDir, "cluster-frames");
        Directory.CreateDirectory(framesA);
        Directory.CreateDirectory(framesB);

        int rc;
        try
        {
            long baseRenderMs = RenderBaselineFrames(engine, baseReq, totalFrames, framesA, log, ct, sb);
            long clusRenderMs = RenderClusterFrames(engine, baseReq, totalFrames, framesPerTileHint, workers, framesB, log, ct, sb);

            int frameMismatches = CompareFrameSet(framesA, framesB, totalFrames, sb);

            int encodeRc = 0;
            var preset = VideoFramePipeline.PresetFromLossless(lossless);
            if (preset is null)
            {
                sb.AppendLine();
                sb.AppendLine($"  encode-arm     : SKIPPED (--lossless={lossless} → no preset; only frame parity asserted)");
            }
            else if (!VideoFramePipeline.IsAvailable())
            {
                sb.AppendLine();
                sb.AppendLine($"  encode-arm     : SKIPPED (ffmpeg not on disk; only frame parity asserted)");
            }
            else
            {
                encodeRc = EncodeAndCompare(framesA, framesB, totalFrames, fps, preset.Value, rootDir, sb, ct);
            }

            sb.AppendLine();
            sb.AppendLine($"  timings        : baseline={baseRenderMs:N0} ms, cluster={clusRenderMs:N0} ms");
            rc = (frameMismatches != 0 || encodeRc != 0) ? 1 : 0;
            sb.AppendLine();
            sb.AppendLine(rc == 0
                ? "  PARITY OK      : frames bit-identical and encode parity passed."
                : "  PARITY FAIL    : see diagnostics above.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
            rc = 1;
        }
        finally
        {
            TryDelete(rootDir);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Console.Write(sb.ToString());
        return rc;
    }

    private static long RenderBaselineFrames(
        HostFractalRenderEngine engine, RenderRequestDto baseReq,
        int totalFrames, string outDir, ISessionLog log,
        CancellationToken ct, StringBuilder sb)
    {
        double startZoom = Math.Max(baseReq.VideoStartZoom, 1e-12);
        double endZoom   = Math.Max(baseReq.Zoom!.Value,    1e-12);
        if (baseReq.VideoReverse) (startZoom, endZoom) = (endZoom, startZoom);
        double logStart = Math.Log(startZoom);
        double logDelta = Math.Log(endZoom) - logStart;

        // Even-snap mirrors FramePlanner so the per-frame engine inputs
        // match the cluster path exactly.
        int outW = baseReq.Width  & ~1; if (outW < 16) outW = 16;
        int outH = baseReq.Height & ~1; if (outH < 16) outH = 16;

        var sw = Stopwatch.StartNew();
        for (int f = 0; f < totalFrames; f++)
        {
            double t = totalFrames == 1 ? 1.0 : (double)f / (totalFrames - 1);
            double te = t * t * (3.0 - 2.0 * t);
            double zoomF = Math.Exp(logStart + logDelta * te);

            var perFrame = CloneFrame(baseReq, outW, outH);
            perFrame.Zoom = zoomF;

            string wd = NewWorkDir($"base-{f}");
            try
            {
                var art = engine.RenderAsync(perFrame, wd, log, ct).GetAwaiter().GetResult();
                byte[] png = File.ReadAllBytes(art.FilePath);
                string outFile = Path.Combine(outDir, $"frame_{f + 1:D6}.png");
                File.WriteAllBytes(outFile, png);
            }
            finally { TryDelete(wd); }
        }
        sw.Stop();
        sb.AppendLine($"  baseline       : {sw.ElapsedMilliseconds,8:N0} ms (single thread, {totalFrames} frames)");
        return sw.ElapsedMilliseconds;
    }

    private static long RenderClusterFrames(
        HostFractalRenderEngine engine, RenderRequestDto baseReq,
        int totalFrames, int framesPerTileHint, int workerCount,
        string outDir, ISessionLog log, CancellationToken ct, StringBuilder sb)
    {
        var plan = FramePlanner.PlanVideo(baseReq, framesPerTileHint);
        const string jobId = "vparity-job";
        foreach (var t in plan.Tiles) t.JobId = jobId;

        sb.AppendLine($"  cluster plan   : {plan.Tiles.Count} tiles, totalFrames={plan.TotalFrames}, frame-dims={plan.ImageWidth}x{plan.ImageHeight}");

        var disp = new TileDispatcher
        {
            StealMinAge        = TimeSpan.FromMilliseconds(250),
            StealMinTotalTiles = 4,
        };
        disp.EnqueueJob(jobId, plan.Tiles);

        var perWorkerTiles = new int[workerCount];
        var sw = Stopwatch.StartNew();
        var workerTasks = new Task[workerCount];
        for (int wi = 0; wi < workerCount; wi++)
        {
            int idx = wi;
            string wid = $"w{idx}";
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

                        var perFrame = CloneFrame(tile.Render, tile.Render.Width, tile.Render.Height);
                        perFrame.Zoom = zoomF;

                        string wd = NewWorkDir($"par-{wid}-{f}");
                        try
                        {
                            var art = await engine.RenderAsync(perFrame, wd, log, ct).ConfigureAwait(false);
                            byte[] png = await File.ReadAllBytesAsync(art.FilePath, ct).ConfigureAwait(false);
                            string outFile = Path.Combine(outDir, $"frame_{f + 1:D6}.png");
                            await File.WriteAllBytesAsync(outFile, png, ct).ConfigureAwait(false);
                        }
                        finally { TryDelete(wd); }
                    }
                    disp.AcceptDelivery(jobId, tile.TileId, $"w{idx}");
                    Interlocked.Increment(ref perWorkerTiles[idx]);
                }
            });
        }
        Task.WaitAll(workerTasks);
        sw.Stop();
        sb.AppendLine($"  cluster        : {sw.ElapsedMilliseconds,8:N0} ms ({workerCount} workers)");
        for (int i = 0; i < workerCount; i++)
            sb.AppendLine($"    w{i,-2}         : {perWorkerTiles[i],4} tiles");
        return sw.ElapsedMilliseconds;
    }

    private static int CompareFrameSet(string dirA, string dirB, int totalFrames, StringBuilder sb)
    {
        int mismatches = 0;
        int missing    = 0;
        var diffs = new List<string>();
        for (int f = 0; f < totalFrames; f++)
        {
            string name = $"frame_{f + 1:D6}.png";
            string pa = Path.Combine(dirA, name);
            string pb = Path.Combine(dirB, name);
            if (!File.Exists(pa) || !File.Exists(pb)) { missing++; continue; }
            string ha = Sha256Hex(pa);
            string hb = Sha256Hex(pb);
            if (!string.Equals(ha, hb, StringComparison.Ordinal))
            {
                mismatches++;
                if (diffs.Count < 5) diffs.Add($"    frame {f+1}: baseline={ha.Substring(0, 12)} cluster={hb.Substring(0, 12)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"  frame-parity   : {totalFrames - mismatches - missing}/{totalFrames} SHA-256 match, {missing} missing, {mismatches} differ");
        foreach (var d in diffs) sb.AppendLine(d);
        return mismatches + missing;
    }

    private static int EncodeAndCompare(
        string framesA, string framesB, int totalFrames, int fps,
        ClusterVideoPreset preset, string rootDir, StringBuilder sb,
        CancellationToken ct)
    {
        string artA = Path.Combine(rootDir, "baseline-artifact");
        string artB = Path.Combine(rootDir, "cluster-artifact");

        (bool okA, string logA, string pathA) = EncodeOne(framesA, totalFrames, fps, preset, artA, ct);
        (bool okB, string logB, string pathB) = EncodeOne(framesB, totalFrames, fps, preset, artB, ct);

        if (!okA) { sb.AppendLine($"  encode baseline: FAIL\n{Tail(logA)}"); return 1; }
        if (!okB) { sb.AppendLine($"  encode cluster : FAIL\n{Tail(logB)}"); return 1; }

        long lenA = new FileInfo(pathA).Length;
        long lenB = new FileInfo(pathB).Length;
        sb.AppendLine($"  encode bytes   : baseline={lenA:N0} B, cluster={lenB:N0} B");

        string? ffprobe = FindFfprobe();
        if (ffprobe == null)
        {
            sb.AppendLine("  ffprobe        : SKIPPED (ffprobe not on disk; bytes-equal check only)");
            return lenA == lenB && FilesEqual(pathA, pathB) ? 0 : 1;
        }

        string streamsA = RunCapture(ffprobe, $"-v quiet -of json -show_streams -show_entries stream=codec_name,codec_type,width,height,pix_fmt,r_frame_rate,nb_frames \"{pathA}\"");
        string streamsB = RunCapture(ffprobe, $"-v quiet -of json -show_streams -show_entries stream=codec_name,codec_type,width,height,pix_fmt,r_frame_rate,nb_frames \"{pathB}\"");
        bool streamsMatch = string.Equals(streamsA, streamsB, StringComparison.Ordinal);
        sb.AppendLine($"  ffprobe streams: {(streamsMatch ? "MATCH" : "DIFFER")}");
        if (!streamsMatch)
        {
            sb.AppendLine("    baseline: " + Compact(streamsA));
            sb.AppendLine("    cluster : " + Compact(streamsB));
        }

        string? ffmpeg = VideoFramePipeline.FindFfmpeg();
        if (ffmpeg == null)
        {
            sb.AppendLine("  framemd5       : SKIPPED (ffmpeg not on disk after ffprobe found — unusual)");
            return streamsMatch ? 0 : 1;
        }

        string md5A = StripFramemd5Header(RunCapture(ffmpeg, $"-v quiet -i \"{pathA}\" -f framemd5 -"));
        string md5B = StripFramemd5Header(RunCapture(ffmpeg, $"-v quiet -i \"{pathB}\" -f framemd5 -"));
        bool md5Match = string.Equals(md5A, md5B, StringComparison.Ordinal);
        sb.AppendLine($"  framemd5       : {(md5Match ? "MATCH" : "DIFFER")} ({CountLines(md5A)} baseline frames vs {CountLines(md5B)} cluster frames)");
        if (!md5Match)
        {
            int diffLine = FirstDiffLine(md5A, md5B);
            sb.AppendLine($"    first diff at frame line {diffLine}");
        }

        return (streamsMatch && md5Match) ? 0 : 1;
    }

    private static (bool ok, string log, string artifactPath) EncodeOne(
        string framesDir, int totalFrames, int fps,
        ClusterVideoPreset preset, string artifactBaseNoExt, CancellationToken ct)
    {
        var pipe = VideoFramePipeline.TryStart(framesDir, totalFrames, fps, preset, artifactBaseNoExt, ct);
        if (pipe == null) return (false, "VideoFramePipeline.TryStart returned null", "");
        try
        {
            pipe.NotifyFramesDelivered(totalFrames);
            var (ok, log) = pipe.Completion.GetAwaiter().GetResult();
            return (ok, log, pipe.ArtifactPath);
        }
        finally
        {
            pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool FilesEqual(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;
        Span<byte> bufA = stackalloc byte[8192];
        Span<byte> bufB = stackalloc byte[8192];
        int ra, rb;
        while ((ra = fa.Read(bufA)) > 0)
        {
            rb = fb.Read(bufB);
            if (ra != rb) return false;
            if (!bufA.Slice(0, ra).SequenceEqual(bufB.Slice(0, rb))) return false;
        }
        return true;
    }

    private static string? FindFfprobe()
    {
        // Lives next to ffmpeg in every distribution we ship.
        string? exe = VideoFramePipeline.FindFfmpeg();
        if (exe == null) return null;
        string dir = Path.GetDirectoryName(exe)!;
        string name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        string p = Path.Combine(dir, name);
        if (File.Exists(p)) return p;
        // Fall back to PATH lookup.
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var d in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try
            {
                string cand = Path.Combine(d.Trim(), name);
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }

    private static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi)!;
        string outStr = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return outStr;
    }

    private static string StripFramemd5Header(string raw)
    {
        // ffmpeg's framemd5 output starts with a few comment lines
        // (# format, # version, # codec_id) that include the running
        // ffmpeg version + container metadata — strip so two builds of
        // ffmpeg can still produce equal per-frame digests.
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            string trim = line.TrimEnd('\r');
            if (trim.StartsWith('#')) continue;
            if (string.IsNullOrWhiteSpace(trim)) continue;
            sb.Append(trim).Append('\n');
        }
        return sb.ToString();
    }

    private static int FirstDiffLine(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        int n = Math.Min(la.Length, lb.Length);
        for (int i = 0; i < n; i++)
            if (!string.Equals(la[i], lb[i], StringComparison.Ordinal)) return i + 1;
        return n + 1;
    }

    private static int CountLines(string s) => s.Split('\n').Length;

    private static string Compact(string s)
    {
        // Squeeze whitespace so a long JSON blob doesn't blow up the log.
        var sb = new StringBuilder();
        bool inSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { if (!inSpace) sb.Append(' '); inSpace = true; }
            else { sb.Append(c); inSpace = false; }
        }
        string compact = sb.ToString();
        return compact.Length > 240 ? compact.Substring(0, 240) + " …" : compact;
    }

    private static string Tail(string log)
    {
        if (log.Length <= 2000) return log;
        return "…\n" + log.Substring(log.Length - 2000);
    }

    private static string Sha256Hex(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(f, hash);
        var sb = new StringBuilder(64);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static RenderRequestDto CloneFrame(RenderRequestDto src, int outW, int outH) => new()
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
        string name = $"ff-vparity-{label}-{Guid.NewGuid():N}";
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
        public void Warn(string line) => Console.Error.WriteLine("[vparity] WARN " + line);
        public void Err (string line) => Console.Error.WriteLine("[vparity] ERR  " + line);
    }
}
