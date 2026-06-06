// Batch/BatchRenderer.cs
// Headless image and video renderers driven by BatchOptions.
//
// Image path: builds a PosterRequest and calls PosterRenderer.RenderToFile.
// Video path: log-zoom interpolates from VideoStartZoom to target Zoom over
// (seconds * fps) frames; each frame runs the offscreen calculator at the
// requested resolution and is fed to Mp4Writer (Windows Media Foundation).
// PNG sequence fallback is used when MP4 init fails (or when --out points
// at a folder rather than an .mp4 file).

using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Batch
{
    public static class BatchRenderer
    {
        // ── Image ─────────────────────────────────────────────────────────────

        public static int RenderImage(BatchOptions opts)
        {
            var (cx, cy, zoom, iter, frType, quality, regionDispName) = ResolveRegion(opts);
            var theme = ResolveTheme(opts.ThemeName);
            string outPath = ResolveImageOutputPath(opts, regionDispName, frType);

            EnsureDirectoryForFile(outPath);

            ImageFormat format = GuessImageFormat(outPath);

            var req = new PosterRequest
            {
                FractalType = frType,
                Width = opts.Width,
                Height = opts.Height,
                CenterX = cx, CenterXLo = 0, CenterX2 = 0, CenterX3 = 0,
                CenterY = cy, CenterYLo = 0, CenterY2 = 0, CenterY3 = 0,
                Zoom = zoom,
                MaxIterations = iter,
                ColorMap = theme,
                Quality = quality,
                FractalParameters = new FractalParameters(),
                Rotate = false,
                Path = outPath,
                Format = format,
                Watermark = regionDispName ?? "",
                SubText = "Fracturing Fog batch render",
            };

            Console.WriteLine($"Batch image render");
            Console.WriteLine($"  fractal : {frType}");
            Console.WriteLine($"  region  : {regionDispName ?? "(manual)"}");
            Console.WriteLine($"  center  : x={cx:G14} y={cy:G14}");
            Console.WriteLine($"  zoom    : {zoom:G6}    iter: {iter}");
            Console.WriteLine($"  theme   : {opts.ThemeName}    quality: {quality.Name}");
            Console.WriteLine($"  size    : {opts.Width}x{opts.Height}");
            Console.WriteLine($"  out     : {outPath}");

            using var spinner = new ConsoleSpinner("Rendering");
            PosterResult result;
            try
            {
                result = PosterRenderer.RenderToFile(req, CancellationToken.None);
                spinner.Stop($"saved {Path.GetFileName(outPath)} ({result.SavedWidth}x{result.SavedHeight}, {result.ElapsedMs} ms)");
            }
            catch
            {
                spinner.Stop("FAILED");
                throw;
            }
            return 0;
        }

        // ── Video ─────────────────────────────────────────────────────────────

        public static int RenderVideo(BatchOptions opts)
        {
            var (cx, cy, targetZoom, iter, frType, quality, regionDispName) = ResolveRegion(opts);
            var theme = ResolveTheme(opts.ThemeName);

            int totalFrames = (int)Math.Round(opts.VideoSeconds * opts.VideoFps);
            if (totalFrames < 2) totalFrames = 2;

            double startZoom = Math.Max(opts.VideoStartZoom, 1e-12);
            double endZoom = Math.Max(targetZoom, 1e-12);
            if (opts.VideoReverse)
            {
                // Reverse: start at target, end at the start-zoom (full view).
                (startZoom, endZoom) = (endZoom, startZoom);
            }

            // Even-snap the output frame size; Mp4Writer requires even W/H.
            int outW = opts.Width & ~1;
            int outH = opts.Height & ~1;
            if (outW < 16) outW = 16;
            if (outH < 16) outH = 16;

            // Resolve output target.
            string outPath = opts.OutputPath;
            string baseName = !string.IsNullOrEmpty(opts.OutputName)
                ? Path.GetFileNameWithoutExtension(opts.OutputName!)
                : BuildBaseName(regionDispName, frType, cx, cy, targetZoom, opts.ThemeName);

            // ffmpeg image2 demuxer expects %06d starting at index 1. The
            // built-in WMF Mp4Writer path uses its own indexing and gets the
            // same naming so both modes produce a directly ffmpeg-encodable
            // PNG sequence.
            const string FrameNameFmt = "frame_{0:D6}.png";

            FfmpegEncoder.Preset? losslessPreset = opts.Lossless switch
            {
                BatchLossless.LosslessH264Mp4    => FfmpegEncoder.Preset.LosslessH264Mp4,
                BatchLossless.Ffv1Mkv            => FfmpegEncoder.Preset.Ffv1Mkv,
                BatchLossless.HighQualityH264Mp4 => FfmpegEncoder.Preset.HighQualityH264Mp4,
                _                                => null,
            };

            string finalVideoPath;
            string pngFolder;

            if (losslessPreset != null)
            {
                // Lossless encode through ffmpeg: --out must be a folder OR a
                // file path with the correct extension for the preset.
                string ext = FfmpegEncoder.DefaultExtensionFor(losslessPreset.Value);
                bool isFile = !string.IsNullOrEmpty(Path.GetExtension(outPath))
                              && !outPath.EndsWith('/') && !outPath.EndsWith('\\');
                if (isFile)
                {
                    EnsureDirectoryForFile(outPath);
                    finalVideoPath = outPath;
                }
                else
                {
                    Directory.CreateDirectory(outPath);
                    finalVideoPath = Path.Combine(outPath, baseName + "." + ext);
                }
                pngFolder = Path.Combine(
                    Path.GetDirectoryName(finalVideoPath) ?? ".",
                    baseName + "_frames");
            }
            else
            {
                bool isFile = outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                if (isFile)
                {
                    EnsureDirectoryForFile(outPath);
                    finalVideoPath = outPath;
                    pngFolder = Path.Combine(
                        Path.GetDirectoryName(outPath) ?? ".",
                        baseName + "_frames");
                }
                else
                {
                    Directory.CreateDirectory(outPath);
                    finalVideoPath = Path.Combine(outPath, baseName + ".mp4");
                    pngFolder = Path.Combine(outPath, baseName + "_frames");
                }
            }

            string losslessLabel = opts.Lossless switch
            {
                BatchLossless.LosslessH264Mp4    => "ffmpeg libx264 -qp 0 MP4",
                BatchLossless.Ffv1Mkv            => "ffmpeg FFV1 MKV",
                BatchLossless.HighQualityH264Mp4 => "ffmpeg libx264 CRF 18 MP4",
                _                                => "WMF H.264 MP4",
            };

            Console.WriteLine($"Batch video render");
            Console.WriteLine($"  fractal  : {frType}");
            Console.WriteLine($"  region   : {regionDispName ?? "(manual)"}");
            Console.WriteLine($"  center   : x={cx:G14} y={cy:G14}");
            Console.WriteLine($"  zoom     : {startZoom:G6} → {endZoom:G6}    iter: {iter}");
            Console.WriteLine($"  theme    : {opts.ThemeName}    quality: {quality.Name}");
            Console.WriteLine($"  size     : {outW}x{outH}    fps: {opts.VideoFps}    frames: {totalFrames}");
            Console.WriteLine($"  encoder  : {losslessLabel}");
            Console.WriteLine($"  out video: {finalVideoPath}");
            Console.WriteLine($"  frames   : {pngFolder}");

            if (losslessPreset != null && !FfmpegEncoder.IsAvailable())
            {
                Console.Error.WriteLine(
                    "ffmpeg.exe not found in app folder, Tools\\, Resources\\, or PATH.");
                Console.Error.WriteLine(
                    "Lossless encoding requires ffmpeg. Use --lossless none for built-in MP4.");
                return 3;
            }

            // WMF MP4 writer used only when no lossless preset selected.
            Mp4Writer? mp4 = null;
            if (losslessPreset == null)
            {
                try
                {
                    mp4 = new Mp4Writer(finalVideoPath, outW, outH, opts.VideoFps, 1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  MP4 writer unavailable ({ex.Message}); PNG sequence only.");
                }
            }

            Directory.CreateDirectory(pngFolder);

            double logZ0 = Math.Log(startZoom);
            double logZ1 = Math.Log(endZoom);

            // 100-ns ticks between frames so Mp4Writer gets a monotonic clock.
            long ticksPerFrame = (long)(10_000_000L / Math.Max(opts.VideoFps, 1));

            var progress = new ConsoleProgress("Frames");
            var sw = Stopwatch.StartNew();
            int framesWritten = 0;

            try
            {
                for (int f = 0; f < totalFrames; f++)
                {
                    double t = totalFrames == 1 ? 1.0 : (double)f / (totalFrames - 1);
                    double te = t * t * (3.0 - 2.0 * t);
                    double frameZoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);

                    uint[] buffer = RenderOneFrame(
                        frType, outW, outH, cx, cy, frameZoom, iter, theme, quality);

                    if (mp4 != null)
                    {
                        try { mp4.WriteFrame(buffer, (long)f * ticksPerFrame); }
                        catch (Exception ex)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  MP4 write failed at frame {f}: {ex.Message}. PNG sequence continues.");
                            try { mp4.Dispose(); } catch { }
                            mp4 = null;
                        }
                    }

                    // ffmpeg image2 demuxer wants frames numbered starting at 1.
                    string framePath = Path.Combine(pngFolder,
                        string.Format(FrameNameFmt, f + 1));
                    ImageExport.SavePixelsToFile(
                        buffer, outW, outH, framePath, ImageFormat.Png,
                        watermarkText: "", fontColor: System.Drawing.Color.White, subText: "");

                    framesWritten++;
                    progress.Report((double)(f + 1) / totalFrames,
                        $"frame {f + 1}/{totalFrames}  zoom {frameZoom:G4}");
                }

                progress.Finish($"frames={framesWritten}  elapsed={sw.Elapsed.TotalSeconds:F1}s");
            }
            finally
            {
                try { mp4?.Dispose(); } catch { }
            }

            // ── ffmpeg encode pass (lossless mode only) ──────────────────────
            if (losslessPreset != null)
            {
                Console.WriteLine($"Encoding {Path.GetFileName(finalVideoPath)} via ffmpeg ({losslessLabel})…");
                var encodeProgress = new ConsoleProgress("Encode");
                int lastFrame = 0;
                var encodeTask = FfmpegEncoder.EncodeAsync(
                    pngFolder, finalVideoPath, losslessPreset.Value,
                    fps: opts.VideoFps,
                    ct: CancellationToken.None,
                    onProgressLine: line =>
                    {
                        // ffmpeg stderr lines look like "frame=  123 fps= …".
                        int idx = line.IndexOf("frame=", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) return;
                        int start = idx + "frame=".Length;
                        int j = start;
                        while (j < line.Length && line[j] == ' ') j++;
                        int numStart = j;
                        while (j < line.Length && char.IsDigit(line[j])) j++;
                        if (j > numStart && int.TryParse(line.AsSpan(numStart, j - numStart), out int n))
                        {
                            lastFrame = n;
                            double frac = (double)Math.Min(n, totalFrames) / totalFrames;
                            encodeProgress.Report(frac, $"frame {n}/{totalFrames}");
                        }
                    });

                var (ok, log) = encodeTask.GetAwaiter().GetResult();
                if (ok)
                {
                    encodeProgress.Finish($"encoded {Path.GetFileName(finalVideoPath)} ({new FileInfo(finalVideoPath).Length / 1024:N0} KB)");
                }
                else
                {
                    encodeProgress.Finish("FAILED");
                    Console.Error.WriteLine("ffmpeg encode failed:");
                    Console.Error.WriteLine(TailLog(log, 2000));
                    return 4;
                }
            }

            // ── Cleanup ──────────────────────────────────────────────────────
            if (!opts.KeepFrames)
            {
                try { Directory.Delete(pngFolder, recursive: true); }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Could not remove frame folder: {ex.Message}");
                }
            }

            Console.WriteLine($"Video saved:");
            if (File.Exists(finalVideoPath))
                Console.WriteLine($"  video  : {finalVideoPath}");
            if (Directory.Exists(pngFolder))
                Console.WriteLine($"  frames : {pngFolder}");
            return 0;
        }

        private static string TailLog(string log, int maxChars)
        {
            if (string.IsNullOrEmpty(log)) return "(no output)";
            return log.Length <= maxChars ? log : "…" + log[^maxChars..];
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static uint[] RenderOneFrame(
            FractalType frType, int w, int h,
            double cx, double cy, double zoom, int iter,
            IColorMap theme, QualityPreset quality)
        {
            IFractalCalculator? alt = PosterRenderer.BuildCaptureCalculator(new PosterRequest
            {
                FractalType = frType,
                Width = w,
                Height = h,
                CenterX = cx,
                CenterY = cy,
                Zoom = zoom,
                MaxIterations = iter,
                Quality = quality,
                ColorMap = theme,
                FractalParameters = new FractalParameters(),
            });

            if (alt != null)
            {
                alt.Calculate(CancellationToken.None);
                return CopyBuffer(alt.ColorBuffer, w, h);
            }

            var calc = new MandelbrotCalculator(w, h)
            {
                CenterX = cx,
                CenterY = cy,
                Zoom = zoom,
                MaxIterations = iter,
                ColorMap = theme,
                Quality = quality,
            };
            calc.Calculate(CancellationToken.None);
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

        private static (double cx, double cy, double zoom, int iter,
                        FractalType frType, QualityPreset quality, string? regionName)
            ResolveRegion(BatchOptions opts)
        {
            FractalRegion? region = null;
            if (!string.IsNullOrWhiteSpace(opts.RegionName))
            {
                region = FractalRegionLibrary.Instance.FindByName(opts.RegionName!);
                if (region == null)
                    throw new InvalidOperationException(
                        $"Region '{opts.RegionName}' not found. Names are case-insensitive; check user/regions.json.");
            }

            double cx, cy, zoom;
            int iter;
            FractalType frType;
            QualityPreset quality;

            if (region != null)
            {
                cx = opts.CenterX ?? region.CenterX;
                cy = opts.CenterY ?? region.CenterY;
                zoom = opts.Zoom ?? region.Zoom;
                iter = opts.Iterations ?? (region.Iterations > 0 ? region.Iterations : 1000);
                frType = opts.FractalType != FractalType.Mandelbrot ? opts.FractalType : region.FractalType;
                quality = !string.Equals(opts.QualityName, "Standard", StringComparison.OrdinalIgnoreCase)
                    ? QualityPreset.FromName(opts.QualityName)
                    : (region.QualityPreset ?? QualityPreset.Standard);
            }
            else
            {
                cx = opts.CenterX!.Value;
                cy = opts.CenterY!.Value;
                zoom = opts.Zoom!.Value;
                iter = opts.Iterations ?? 1000;
                frType = opts.FractalType;
                quality = QualityPreset.FromName(opts.QualityName);
            }

            return (cx, cy, zoom, iter, frType, quality, region?.Name);
        }

        private static IColorMap ResolveTheme(string name)
        {
            var theme = FracturingFog.Models.ColorPalette.GetPaletteByName(name);
            return theme;
        }

        private static string ResolveImageOutputPath(BatchOptions opts, string? regionName, FractalType frType)
        {
            string outPath = opts.OutputPath;

            // If the user gave a directory or a path without an image extension,
            // synthesize a filename.
            bool isDirHint = outPath.EndsWith('/') || outPath.EndsWith('\\') || Directory.Exists(outPath);
            string ext = Path.GetExtension(outPath).ToLowerInvariant();
            bool hasImageExt = ext is ".png" or ".tif" or ".tiff" or ".bmp";

            if (isDirHint || !hasImageExt)
            {
                string baseName = !string.IsNullOrEmpty(opts.OutputName)
                    ? Path.GetFileNameWithoutExtension(opts.OutputName!)
                    : BuildBaseName(regionName, frType,
                        opts.CenterX ?? 0, opts.CenterY ?? 0,
                        opts.Zoom ?? 1, opts.ThemeName);
                Directory.CreateDirectory(outPath);
                outPath = Path.Combine(outPath, baseName + ".png");
            }

            return outPath;
        }

        private static string BuildBaseName(
            string? regionName, FractalType frType,
            double cx, double cy, double zoom, string theme)
        {
            string safeRegion = string.IsNullOrEmpty(regionName)
                ? "manual"
                : SanitizeFileName(regionName!);
            string safeTheme = SanitizeFileName(theme);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return $"FF_{frType}_{safeRegion}_{safeTheme}_z{zoom:G4}_{stamp}".Replace(' ', '_');
        }

        private static string SanitizeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        private static ImageFormat GuessImageFormat(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".bmp" => ImageFormat.Bmp,
                ".tif" or ".tiff" => ImageFormat.Tiff,
                _ => ImageFormat.Png,
            };

        private static void EnsureDirectoryForFile(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }
}
