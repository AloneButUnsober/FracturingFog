// ServerHost/HostFractalRenderEngine.cs
// WinExe-side IFractalRenderEngine implementation. Binds the protocol
// RenderRequestDto to the existing PosterRenderer / calculator zoo and to a
// log-zoom video loop modelled on Batch/BatchRenderer.cs:RenderVideo but
// re-implemented here so we can flow the per-job CancellationToken through.

using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Server;
using FracturingFog.Server.Guard;
using FracturingFog.Server.Protocol;

namespace FracturingFog.ServerHost;

public sealed class HostFractalRenderEngine : IFractalRenderEngine
{
    public Task<RenderArtifact> RenderAsync(
        RenderRequestDto req,
        string workDir,
        ISessionLog log,
        CancellationToken ct)
    {
        if (!Enum.TryParse<FractalType>(req.FractalType, ignoreCase: true, out var declaredType))
            throw new ServerProtocolException("bad-request", $"unknown fractal type '{req.FractalType}'");

        FractalRegion? region = !string.IsNullOrWhiteSpace(req.RegionName)
            ? FractalRegionLibrary.Instance.FindByName(req.RegionName!)
            : null;
        if (!string.IsNullOrWhiteSpace(req.RegionName) && region == null)
            throw new ServerProtocolException("unknown-region", $"unknown region '{req.RegionName}'");

        double cx, cy, zoom;
        // Quad-precision lower limbs (CenterX = Hi, plus 3 lo words). Only the
        // Mandelbrot path consumes them; alt calculators ignore them. Without
        // these, a deep-zoom saved region (e.g. "Deep Julias" at zoom 2.3e19)
        // renders at the Hi-word coordinate only — visibly different from the
        // UI render which uses the full quad. See PosterRenderer.cs:108-123.
        double cxLo = 0, cx2 = 0, cx3 = 0;
        double cyLo = 0, cy2 = 0, cy3 = 0;
        int iter;
        FractalType ftype;
        QualityPreset quality;

        if (region != null)
        {
            // The Avalonia NumericUpDown bound to a nullable double serializes
            // "untouched" as 0 instead of null, so a request that says
            // "use my region's defaults" arrives with zoom=0, cx=0, cy=0,
            // iter=0. Without this guard the zero zoom wins over region.Zoom
            // and the calculator emits an all-black frame. Treat 0 as "unset"
            // here when a region is supplied — the user picked a named region
            // precisely to inherit its coords.
            zoom = (req.Zoom is double rz && rz > 0) ? rz : region.Zoom;
            bool reqCxZero = req.CenterX is double rcx && rcx == 0.0;
            bool reqCyZero = req.CenterY is double rcy && rcy == 0.0;
            bool bothZero  = reqCxZero && reqCyZero;
            cx = (req.CenterX is double xv && !bothZero) ? xv : region.CenterX;
            cy = (req.CenterY is double yv && !bothZero) ? yv : region.CenterY;
            // Limb selection follows the Hi-word choice: when the client
            // overrode the Hi-word centre, use the client-supplied limbs (0
            // by default in the DTO); when we fell back to the region centre,
            // take the region's limbs so deep-zoom precision survives.
            bool useClientCentre = (req.CenterX is double && !bothZero);
            if (useClientCentre)
            {
                cxLo = req.CenterXLo; cx2 = req.CenterX2; cx3 = req.CenterX3;
                cyLo = req.CenterYLo; cy2 = req.CenterY2; cy3 = req.CenterY3;
            }
            else
            {
                cxLo = region.CenterXLo; cx2 = region.CenterX2; cx3 = region.CenterX3;
                cyLo = region.CenterYLo; cy2 = region.CenterY2; cy3 = region.CenterY3;
            }
            iter = (req.Iterations is int ri && ri > 0)
                ? ri
                : (region.Iterations > 0 ? region.Iterations : 1000);
            ftype = declaredType != FractalType.Mandelbrot ? declaredType : region.FractalType;
            quality = !string.Equals(req.QualityName, "Standard", StringComparison.OrdinalIgnoreCase)
                ? QualityPreset.FromName(req.QualityName)
                : (region.QualityPreset ?? QualityPreset.Standard);
        }
        else
        {
            if (req.CenterX is null || req.CenterY is null || req.Zoom is null)
                throw new ServerProtocolException("bad-request",
                    "manual coords require centerX, centerY, zoom");
            if (req.Zoom.Value <= 0)
                throw new ServerProtocolException("bad-request",
                    $"zoom must be > 0 (got {req.Zoom.Value})");
            cx = req.CenterX.Value;
            cy = req.CenterY.Value;
            zoom = req.Zoom.Value;
            cxLo = req.CenterXLo; cx2 = req.CenterX2; cx3 = req.CenterX3;
            cyLo = req.CenterYLo; cy2 = req.CenterY2; cy3 = req.CenterY3;
            iter = (req.Iterations is int ri && ri > 0) ? ri : 1000;
            ftype = declaredType;
            quality = QualityPreset.FromName(req.QualityName);
        }

        // CRITICAL: re-check allowlist after region resolve. The first
        // allowlist check in FFServer.HandleRenderAsync only validates the
        // declared fractal type. A saved region tagged with a blocked type
        // (UserEquation / Sandbox / UserBulb) would override ftype here and
        // silently run user-authored code. Refusing here closes that bypass.
        if (!FractalTypeAllowlist.IsAllowed(ftype))
            throw new ServerProtocolException("forbidden-fractal",
                $"fractal type '{ftype}' is not permitted for remote rendering" +
                (region != null ? $" (resolved via region '{region.Name}')" : ""));

        IColorMap theme = FracturingFog.Models.ColorPalette.GetPaletteByName(req.ThemeName)
            ?? throw new ServerProtocolException("unknown-theme", $"unknown theme '{req.ThemeName}'");

        bool isVideo = string.Equals(req.Mode, "video", StringComparison.OrdinalIgnoreCase);
        bool hasQuadLimbs = cxLo != 0 || cx2 != 0 || cx3 != 0 || cyLo != 0 || cy2 != 0 || cy3 != 0;
        log.Info($"resolved: fractal={ftype} cx={cx:G14} cy={cy:G14} zoom={zoom:G6} iter={iter} " +
                 $"size={req.Width}x{req.Height} quality={quality.Name} theme={req.ThemeName} " +
                 $"quadLimbs={(hasQuadLimbs ? "yes" : "no")}");

        return isVideo
            ? RenderVideoArtifactAsync(req, ftype, cx, cxLo, cx2, cx3, cy, cyLo, cy2, cy3, zoom, iter, theme, quality, region, workDir, log, ct)
            : RenderImageArtifactAsync(req, ftype, cx, cxLo, cx2, cx3, cy, cyLo, cy2, cy3, zoom, iter, theme, quality, region, workDir, log, ct);
    }

    private static Task<RenderArtifact> RenderImageArtifactAsync(
        RenderRequestDto req, FractalType ftype,
        double cx, double cxLo, double cx2, double cx3,
        double cy, double cyLo, double cy2, double cy3,
        double zoom, int iter,
        IColorMap theme, QualityPreset quality, FractalRegion? region,
        string workDir, ISessionLog log, CancellationToken ct)
    {
        string baseName = BuildBaseName(req.OutputName, region?.Name, ftype, cx, cy, zoom, req.ThemeName);
        string outPath = Path.Combine(workDir, baseName + ".png");

        var poster = new PosterRequest
        {
            FractalType = ftype,
            Width = req.Width,
            Height = req.Height,
            CenterX = cx,
            CenterXLo = cxLo,
            CenterX2 = cx2,
            CenterX3 = cx3,
            CenterY = cy,
            CenterYLo = cyLo,
            CenterY2 = cy2,
            CenterY3 = cy3,
            Zoom = zoom,
            MaxIterations = iter,
            ColorMap = theme,
            Quality = quality,
            FractalParameters = new FractalParameters(),
            Rotate = false,
            Path = outPath,
            Format = ImageFormat.Png,
            Watermark = region?.Name ?? "",
            SubText = "Fracturing Fog server render",
        };

        // CPU-bound rasterization runs on a thread-pool worker. The outer
        // caller (FFServer) awaits the returned Task so the dispatch loop
        // is not blocked even when a render takes minutes.
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var result = PosterRenderer.RenderToFile(poster, ct);
            sw.Stop();
            log.Info($"image saved: {Path.GetFileName(outPath)} {result.SavedWidth}x{result.SavedHeight} elapsedMs={result.ElapsedMs}");

            return new RenderArtifact
            {
                FilePath = outPath,
                Width = result.SavedWidth,
                Height = result.SavedHeight,
                ElapsedMs = result.ElapsedMs,
            };
        }, ct);
    }

    private static async Task<RenderArtifact> RenderVideoArtifactAsync(
        RenderRequestDto req, FractalType ftype,
        double cx, double cxLo, double cx2, double cx3,
        double cy, double cyLo, double cy2, double cy3,
        double targetZoom, int iter,
        IColorMap theme, QualityPreset quality, FractalRegion? region,
        string workDir, ISessionLog log, CancellationToken ct)
    {
        int totalFrames = (int)Math.Round(req.VideoSeconds * req.VideoFps);
        if (totalFrames < 2) totalFrames = 2;

        double startZoom = Math.Max(req.VideoStartZoom, 1e-12);
        double endZoom   = Math.Max(targetZoom, 1e-12);
        if (req.VideoReverse) (startZoom, endZoom) = (endZoom, startZoom);

        int outW = req.Width & ~1;
        int outH = req.Height & ~1;
        if (outW < 16) outW = 16;
        if (outH < 16) outH = 16;

        string baseName = BuildBaseName(req.OutputName, region?.Name, ftype, cx, cy, targetZoom, req.ThemeName);

        FfmpegEncoder.Preset? losslessPreset = req.Lossless.ToLowerInvariant() switch
        {
            "none"   => null,
            "h264"   => FfmpegEncoder.Preset.LosslessH264Mp4,
            "ffv1"   => FfmpegEncoder.Preset.Ffv1Mkv,
            "h264hq" => FfmpegEncoder.Preset.HighQualityH264Mp4,
            _        => throw new ServerProtocolException("bad-request",
                          $"unknown lossless preset '{req.Lossless}' (expected none|h264|ffv1|h264hq)"),
        };

        string finalVideoPath;
        string pngFolder = Path.Combine(workDir, baseName + "_frames");
        if (losslessPreset != null)
        {
            string ext = FfmpegEncoder.DefaultExtensionFor(losslessPreset.Value);
            finalVideoPath = Path.Combine(workDir, baseName + "." + ext);
            if (!FfmpegEncoder.IsAvailable())
                throw new ServerProtocolException("ffmpeg-missing",
                    "ffmpeg.exe not found on server; cannot satisfy lossless preset");
        }
        else
        {
            finalVideoPath = Path.Combine(workDir, baseName + ".mp4");
        }

        Directory.CreateDirectory(pngFolder);
        bool keepFrames = req.KeepFrames ?? (losslessPreset == null);

        Mp4Writer? mp4 = null;
        if (losslessPreset == null)
        {
            // Client asked for video mode. A silent fallback to "PNG
            // sequence only" leaves the documented .mp4 path empty —
            // client reads it and hits FileNotFound. Surface the real
            // reason now so the user sees something actionable.
            try { mp4 = new Mp4Writer(finalVideoPath, outW, outH, req.VideoFps, 1); }
            catch (Exception ex)
            {
                throw new ServerProtocolException("render-failed",
                    $"Mp4Writer init failed: {ex.Message}. Use --lossless h264 to force ffmpeg encode instead.");
            }
        }

        double logZ0 = Math.Log(startZoom);
        double logZ1 = Math.Log(endZoom);
        long ticksPerFrame = (long)(10_000_000L / Math.Max(req.VideoFps, 1));

        var sw = Stopwatch.StartNew();

        // Frame generation is CPU-bound; offload to a thread-pool worker
        // so the awaiting caller (FFServer dispatch) does not block its
        // own context. ffmpeg encode is launched as a child process and
        // is awaited natively below — no more GetAwaiter().GetResult().
        Mp4Writer? mp4Local = mp4;
        int framesWritten = await Task.Run(() =>
        {
            int written = 0;
            try
            {
                for (int f = 0; f < totalFrames; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    double t = totalFrames == 1 ? 1.0 : (double)f / (totalFrames - 1);
                    double te = t * t * (3.0 - 2.0 * t);
                    double frameZoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);

                    uint[] buffer = RenderOneFrame(
                        ftype, outW, outH,
                        cx, cxLo, cx2, cx3,
                        cy, cyLo, cy2, cy3,
                        frameZoom, iter, theme, quality, ct);

                    if (mp4Local != null)
                    {
                        try { mp4Local.WriteFrame(buffer, (long)f * ticksPerFrame); }
                        catch (Exception ex)
                        {
                            // Abort instead of half-encoded video + full PNG
                            // set. Previous behaviour produced an unplayable
                            // mp4 that the client could not detect.
                            try { mp4Local.Dispose(); } catch { }
                            mp4Local = null;
                            throw new ServerProtocolException("render-failed",
                                $"mp4 write failed at frame {f}: {ex.Message}");
                        }
                    }

                    string framePath = Path.Combine(pngFolder, $"frame_{f + 1:D6}.png");
                    ImageExport.SavePixelsToFile(
                        buffer, outW, outH, framePath, ImageFormat.Png,
                        watermarkText: "", fontColor: System.Drawing.Color.White, subText: "");

                    written++;
                    if ((f & 0x1F) == 0)
                        log.Info($"frame {f + 1}/{totalFrames} zoom={frameZoom:G4}");
                }
            }
            finally
            {
                try { mp4Local?.Dispose(); } catch { }
            }
            return written;
        }, ct).ConfigureAwait(false);

        if (losslessPreset != null)
        {
            log.Info($"encoding via ffmpeg ({losslessPreset.Value})");
            var (ok, ffLog) = await FfmpegEncoder.EncodeAsync(
                pngFolder, finalVideoPath, losslessPreset.Value,
                fps: req.VideoFps, ct: ct).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("ffmpeg encode failed: " + TailLog(ffLog, 800));
        }

        if (!keepFrames)
        {
            try { Directory.Delete(pngFolder, recursive: true); pngFolder = ""; }
            catch (Exception ex) { log.Warn($"could not clean frames: {ex.Message}"); }
        }

        sw.Stop();
        log.Info($"video saved: {Path.GetFileName(finalVideoPath)} frames={framesWritten} elapsedMs={sw.ElapsedMilliseconds}");

        return new RenderArtifact
        {
            FilePath = finalVideoPath,
            FrameFolderPath = keepFrames ? pngFolder : null,
            Width = outW,
            Height = outH,
            FramesWritten = framesWritten,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static uint[] RenderOneFrame(
        FractalType ftype, int w, int h,
        double cx, double cxLo, double cx2, double cx3,
        double cy, double cyLo, double cy2, double cy3,
        double zoom, int iter,
        IColorMap theme, QualityPreset quality, CancellationToken ct)
    {
        IFractalCalculator? alt = PosterRenderer.BuildCaptureCalculator(new PosterRequest
        {
            FractalType = ftype,
            Width = w, Height = h,
            CenterX = cx, CenterY = cy, Zoom = zoom,
            MaxIterations = iter,
            Quality = quality,
            ColorMap = theme,
            FractalParameters = new FractalParameters(),
        });

        if (alt != null)
        {
            alt.Calculate(ct);
            return CopyBuffer(alt.ColorBuffer, w, h);
        }

        // Mandelbrot path consumes the full quad-precision centre. Without the
        // Lo/2/3 limbs the calculator collapses to the Hi-word coordinate,
        // which for deep zooms (zoom > ~1e16) is visibly off the saved spot.
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = cx, CenterXLo = cxLo, CenterX2 = cx2, CenterX3 = cx3,
            CenterY = cy, CenterYLo = cyLo, CenterY2 = cy2, CenterY3 = cy3,
            Zoom = zoom,
            MaxIterations = iter,
            ColorMap = theme, Quality = quality,
        };
        calc.Calculate(ct);
        return CopyBuffer(calc.ColorBuffer, w, h);
    }

    private static uint[] CopyBuffer(uint[] src, int w, int h)
    {
        int n = w * h;
        if (src.Length == n) return (uint[])src.Clone();
        var dst = new uint[n];
        Array.Copy(src, dst, Math.Min(src.Length, n));
        return dst;
    }

    /// <summary>Strict filename token. Reject anything outside
    /// [A-Za-z0-9_-], leading dot, or longer than 64 chars. A client-
    /// supplied outputName that fails this is dropped in favour of the
    /// auto-derived base name — never reflected into a filesystem write
    /// target the attacker chose. Defends against path traversal
    /// (../, absolute paths, alt separators, NUL injection) regardless
    /// of Path.GetFileNameWithoutExtension behaviour on the host OS.</summary>
    private static readonly System.Text.RegularExpressions.Regex SafeNameRegex =
        new(@"^[A-Za-z0-9_\-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string BuildBaseName(
        string? outputName, string? regionName, FractalType ftype,
        double cx, double cy, double zoom, string theme)
    {
        if (!string.IsNullOrWhiteSpace(outputName))
        {
            // Strip any path component the client supplied, then validate
            // the remainder against the strict regex. On mismatch we fall
            // through to the auto-derived name — silently dropping the
            // attacker-influenced choice rather than echoing it.
            string stem = Path.GetFileNameWithoutExtension(outputName!);
            if (SafeNameRegex.IsMatch(stem))
                return stem;
        }
        string safeRegion = string.IsNullOrEmpty(regionName) ? "manual" : Sanitize(regionName);
        string safeTheme  = Sanitize(theme);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return $"FF_{ftype}_{safeRegion}_{safeTheme}_z{zoom:G4}_{stamp}".Replace(' ', '_');
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace(' ', '_');
        // Belt-and-braces: even with platform invalid-char strip, refuse
        // path-traversal sequences that survive on certain hosts.
        s = s.Replace("..", "_");
        return s;
    }

    private static string TailLog(string log, int maxChars)
    {
        if (string.IsNullOrEmpty(log)) return "(no output)";
        return log.Length <= maxChars ? log : "…" + log[^maxChars..];
    }
}
