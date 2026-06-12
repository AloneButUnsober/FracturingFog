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

            FracturingFog.Imaging.ImageFileFormat format = GuessImageFormat(outPath);

            var fp = new FractalParameters();
            if (opts.BulbPower.HasValue)          fp.BulbPower          = opts.BulbPower.Value;
            if (opts.MultibrotExponent.HasValue)  fp.MultibrotExponent  = opts.MultibrotExponent.Value;

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
                FractalParameters = fp,
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

                    // --watermark bakes the region/theme + program sub-line
                    // into every emitted frame so the WMF Mp4Writer path AND
                    // the PNG sequence both carry the watermark.
                    if (opts.Watermark)
                        ApplyWatermarkInPlace(buffer, outW, outH,
                            regionDispName ?? frType.ToString(),
                            "Fracturing Fog batch render");

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
                    ImageExportGdi.SavePixelsToFile(
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

        // True when every pixel in the buffer is opaque black (0xFF000000).
        // Used by the batch slideshow loop to drop region/theme combinations
        // that render as a hole (full in-set under a black-in-set palette,
        // or insufficient iter depth at extreme zoom).
        private static bool IsAllBlack(uint[] pixels, int n)
        {
            const uint OpaqueBlack = 0xFF000000u;
            int len = Math.Min(pixels.Length, n);
            for (int i = 0; i < len; i++)
                if (pixels[i] != OpaqueBlack) return false;
            return true;
        }

        private static uint[] CopyBuffer(uint[] src, int w, int h)
        {
            int n = w * h;
            if (src.Length == n) return (uint[])src.Clone();
            var dst = new uint[n];
            Array.Copy(src, dst, Math.Min(src.Length, n));
            return dst;
        }

        // In-place watermark composite for batch video + slideshow buffers.
        // Wraps the BGRA buffer in a System.Drawing.Bitmap, calls the shared
        // ImageExport.AddWaterMark, and copies the painted pixels back into
        // the buffer. Windows-only (System.Drawing); the batch host is
        // already Windows-only.
        private static unsafe void ApplyWatermarkInPlace(
            uint[] pixels, int w, int h, string text, string subText)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var bmp = new System.Drawing.Bitmap(w, h, PixelFormat.Format32bppArgb);
            var wd = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* src = pixels)
                {
                    if (wd.Stride == w * 4)
                        Buffer.MemoryCopy(src, (void*)wd.Scan0, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* dst = (byte*)wd.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                              dst + (long)row * wd.Stride,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(wd); }

            using (var g = System.Drawing.Graphics.FromImage(bmp))
                ImageExportGdi.AddWaterMark(
                    g, text, w, h, System.Drawing.Color.White, subText, poster: false);

            var rd = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* dst = pixels)
                {
                    if (rd.Stride == w * 4)
                        Buffer.MemoryCopy((void*)rd.Scan0, dst, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* src = (byte*)rd.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy(src + (long)row * rd.Stride,
                                              (byte*)dst + (long)row * w * 4,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(rd); }
        }

        // ── Slideshow ─────────────────────────────────────────────────────────
        //
        // Headless image-slideshow render. Loads a SlideshowConfig from the
        // library, builds a region/theme pool that honours the preset's filters,
        // and walks legs picking a random region+theme each time. Per leg a CPU
        // cross-fade interpolates from the previous frame to the new frame
        // (matching the interactive engine's FadeAsync); dwell extends the leg
        // by holding the final frame for the remaining theme budget. Frames are
        // pushed into a PngSequenceWriter and post-encoded with ffmpeg.
        //
        // First cut limits region rendering to Mandelbrot regions — other
        // fractal types would require per-type calculator dispatch + offscreen
        // colour map rebuild, which the BatchRenderer doesn't yet do. Mandelbrot
        // is the entire slideshow default in the legacy WinForms path, so this
        // matches the headless surface users actually reach for.
        public static int RenderSlideshow(BatchOptions opts)
        {
            var rng = new Random();

            // 1. Load + resolve preset.
            var configFile = FracturingFog.Models.SlideshowConfigLibrary.Load();
            FracturingFog.Models.SlideshowConfig cfg;
            if (!string.IsNullOrWhiteSpace(opts.SlideshowConfigName))
            {
                cfg = configFile.Configs.Find(c =>
                    string.Equals(c.Name, opts.SlideshowConfigName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Slideshow preset '{opts.SlideshowConfigName}' not found in slideshow-configs.json. " +
                        $"Available: {string.Join(", ", configFile.Configs.ConvertAll(c => c.Name))}");
            }
            else
            {
                cfg = FracturingFog.Models.SlideshowConfigLibrary.GetActive(configFile);
            }

            // 2. Build region pool (Mandelbrot-only for v1).
            var regionPool = new System.Collections.Generic.List<FractalRegion>();
            var incRegions = cfg.IncludedRegions != null && cfg.IncludedRegions.Count > 0
                ? new System.Collections.Generic.HashSet<string>(cfg.IncludedRegions, StringComparer.OrdinalIgnoreCase)
                : null;
            var incQuality = cfg.FilterQualityPresets != null && cfg.FilterQualityPresets.Count > 0
                ? new System.Collections.Generic.HashSet<string>(cfg.FilterQualityPresets, StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
            {
                if (r.FractalType != FractalType.Mandelbrot) continue;
                if (incRegions != null && !incRegions.Contains(r.Name)) continue;
                if (incQuality != null && !incQuality.Contains(r.QualityPreset?.Name ?? "Standard")) continue;
                regionPool.Add(r);
            }
            if (regionPool.Count == 0)
                throw new InvalidOperationException(
                    "No Mandelbrot regions available after applying preset filters. " +
                    "Headless slideshow rendering only supports Mandelbrot regions in v1.");

            // 3. Build theme pool.
            var themeAll = FracturingFog.Models.ColorPalette.GetPaletteNames();
            var themePool = new System.Collections.Generic.List<string>();
            var incThemes = cfg.IncludedColorThemes != null && cfg.IncludedColorThemes.Count > 0
                ? new System.Collections.Generic.HashSet<string>(cfg.IncludedColorThemes, StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var n in themeAll)
            {
                if (incThemes != null && !incThemes.Contains(n)) continue;
                themePool.Add(n);
            }
            if (themePool.Count == 0) themePool.AddRange(themeAll);
            if (themePool.Count == 0)
                throw new InvalidOperationException("No color themes available.");

            // 4. Frame budget + temp output folder.
            int outW = opts.Width & ~1;
            int outH = opts.Height & ~1;
            if (outW < 16) outW = 16;
            if (outH < 16) outH = 16;
            int fps = opts.VideoFps > 0 ? opts.VideoFps : 30;
            int totalFrames = (int)Math.Round(opts.SlideshowSeconds * fps);
            if (totalFrames < fps) totalFrames = fps; // 1s minimum

            string tempRoot = Path.Combine(
                Path.GetTempPath(),
                "FracturingFog",
                "batch-slideshow",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(tempRoot);

            // 5. Compute cadence from preset timing.
            // --more-colors flips the cadence to FocusRegion=false (Color Focus,
            // 8 themes/region, shorter per-theme dwell) — synonym of the
            // "Slideshow: More Colors" context-menu item in the interactive UI.
            int fadeSteps = Math.Clamp(cfg.Timing.FadeSteps, 2, 200);
            int themesPerRegion = opts.MoreColors ? 8 : 3;
            int totalRegionMs = Math.Max(3_000, cfg.Timing.TotalDisplayMsPerRegion);
            int themeDurationMs = Math.Max(800, totalRegionMs / themesPerRegion);
            int themeDurationFrames = Math.Max(fadeSteps, (int)Math.Round(themeDurationMs / 1000.0 * fps));

            FfmpegEncoder.Preset encodePreset = opts.SlideshowEncode switch
            {
                BatchLossless.LosslessH264Mp4 => FfmpegEncoder.Preset.LosslessH264Mp4,
                BatchLossless.Ffv1Mkv => FfmpegEncoder.Preset.Ffv1Mkv,
                _ => FfmpegEncoder.Preset.HighQualityH264Mp4,
            };

            Console.WriteLine($"Batch slideshow render");
            Console.WriteLine($"  preset    : {cfg.Name}");
            Console.WriteLine($"  regions   : {regionPool.Count}  themes: {themePool.Count}");
            Console.WriteLine($"  size      : {outW}x{outH}  fps: {fps}  duration: {opts.SlideshowSeconds:G4}s ({totalFrames} frames)");
            Console.WriteLine($"  fade      : {fadeSteps} steps, {themeDurationFrames} frames/theme");
            Console.WriteLine($"  temp      : {tempRoot}");
            Console.WriteLine($"  encode    : {encodePreset}");
            Console.WriteLine($"  out       : {opts.OutputPath}");

            // 6. Render loop.
            using var pngWriter = new PngSequenceWriter(tempRoot, outW, outH);
            uint[]? prevFrame = null;
            int framesWritten = 0;
            int lastRegion = -1, lastTheme = -1;

            while (framesWritten < totalFrames)
            {
                int ri;
                do { ri = rng.Next(regionPool.Count); }
                while (regionPool.Count > 1 && ri == lastRegion);
                lastRegion = ri;
                var region = regionPool[ri];

                int ti;
                do { ti = rng.Next(themePool.Count); }
                while (themePool.Count > 1 && ti == lastTheme);
                lastTheme = ti;
                var theme = ResolveTheme(themePool[ti]);

                Console.Write($"  leg [{framesWritten}/{totalFrames}]: {region.Name} / {themePool[ti]} … ");

                // Calculate the Mandelbrot frame.
                int iter = region.Iterations > 0 ? region.Iterations : 1000;
                var calc = new MandelbrotCalculator(outW, outH)
                {
                    CenterX = region.CenterX, CenterXLo = region.CenterXLo,
                    CenterX2 = region.CenterX2, CenterX3 = region.CenterX3,
                    CenterY = region.CenterY, CenterYLo = region.CenterYLo,
                    CenterY2 = region.CenterY2, CenterY3 = region.CenterY3,
                    Zoom = region.Zoom,
                    MaxIterations = iter,
                    ColorMap = theme,
                    Quality = region.QualityPreset ?? QualityPreset.Standard,
                };
                var sw = Stopwatch.StartNew();
                calc.Calculate(CancellationToken.None);
                sw.Stop();

                uint[] currFrame = calc.ColorBuffer;

                // Skip legs where the region+theme combination produced an
                // entirely black frame (theme renders to black at this iter
                // depth, or the region is fully in-set with a colour map that
                // paints in-set black). Picking again gives the user another
                // theme without spending budget on a hole.
                if (IsAllBlack(currFrame, outW * outH))
                {
                    Console.WriteLine($"all-black; skip ({sw.ElapsedMilliseconds} ms)");
                    continue;
                }

                if (opts.Watermark)
                    ApplyWatermarkInPlace(currFrame, outW, outH,
                        region.Name, $"Theme: {themePool[ti]}");

                int legFramesWritten = 0;

                // Cross-fade from prev → curr (skip on first leg).
                if (prevFrame != null && prevFrame.Length == currFrame.Length)
                {
                    var blend = new uint[currFrame.Length];
                    int n = outW * outH;
                    for (int s = 1; s <= fadeSteps && framesWritten + legFramesWritten < totalFrames; s++)
                    {
                        float a = s / (float)fadeSteps;
                        float ia = 1f - a;
                        for (int i = 0; i < n; i++)
                        {
                            uint o = prevFrame[i], nw = currFrame[i];
                            byte rB = (byte)(((o >> 16) & 0xFF) * ia + ((nw >> 16) & 0xFF) * a);
                            byte gB = (byte)(((o >> 8) & 0xFF) * ia + ((nw >> 8) & 0xFF) * a);
                            byte bB = (byte)((o & 0xFF) * ia + (nw & 0xFF) * a);
                            blend[i] = 0xFF000000u | ((uint)rB << 16) | ((uint)gB << 8) | bB;
                        }
                        pngWriter.WriteFrame(blend);
                        legFramesWritten++;
                    }
                }

                // Dwell — hold the final frame to fill the leg budget.
                int holdFrames = Math.Max(0, themeDurationFrames - legFramesWritten);
                int remaining = totalFrames - (framesWritten + legFramesWritten);
                if (holdFrames > remaining) holdFrames = remaining;
                for (int s = 0; s < holdFrames; s++)
                {
                    pngWriter.WriteFrame(currFrame);
                    legFramesWritten++;
                }

                framesWritten += legFramesWritten;
                prevFrame = currFrame;
                Console.WriteLine($"{legFramesWritten} frames in {sw.ElapsedMilliseconds} ms");
            }

            pngWriter.Dispose();
            Console.WriteLine($"Captured {framesWritten} frames.");

            // 7. Encode + emit.
            if (string.IsNullOrWhiteSpace(opts.OutputPath))
            {
                Console.WriteLine($"No --out given; PNG sequence kept at: {tempRoot}");
                return 0;
            }

            string outPath = opts.OutputPath;
            string outExt = Path.GetExtension(outPath).ToLowerInvariant();
            string presetExt = "." + FfmpegEncoder.DefaultExtensionFor(encodePreset);
            if (string.IsNullOrEmpty(outExt))
                outPath = Path.Combine(outPath, $"FracturingFog_Slideshow_{DateTime.Now:yyyyMMdd_HHmmss}{presetExt}");
            EnsureDirectoryForFile(outPath);

            if (!FfmpegEncoder.IsAvailable())
            {
                Console.Error.WriteLine($"ffmpeg.exe not found — keeping PNG sequence at {tempRoot}.");
                return 1;
            }

            Console.Write($"Encoding {Path.GetFileName(outPath)} with ffmpeg … ");
            var encSw = Stopwatch.StartNew();
            var (ok, log) = FfmpegEncoder
                .EncodeAsync(tempRoot, outPath, encodePreset, fps: fps)
                .GetAwaiter().GetResult();
            encSw.Stop();
            if (!ok)
            {
                Console.WriteLine($"FAILED ({encSw.ElapsedMilliseconds} ms)");
                Console.Error.WriteLine(log);
                Console.Error.WriteLine($"PNG sequence kept at: {tempRoot}");
                return 1;
            }
            Console.WriteLine($"done ({encSw.ElapsedMilliseconds} ms)");

            if (!opts.KeepFrames)
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
            else
            {
                Console.WriteLine($"PNG sequence kept at: {tempRoot}");
            }
            return 0;
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
            // Exact match first.
            var theme = FracturingFog.Models.ColorPalette.GetPaletteByName(name);
            if (!IsDefaultHsvFallback(theme, name)) return theme;

            // Case-insensitive retry against the full palette list. Shells often
            // mangle case (Tab-completion, lower-case habits), so a name like
            // "hsv" or "chromostereopsis ember/frost" should still hit.
            var names = FracturingFog.Models.ColorPalette.GetPaletteNames();
            foreach (var n in names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return FracturingFog.Models.ColorPalette.GetPaletteByName(n);
            }

            // Miss. Warn loudly so the user notices the silent HSV fallback,
            // and surface up to 5 nearest matches to catch typos like
            // "Chromosteropsis" vs "Chromostereopsis".
            Console.Error.WriteLine($"batch: theme '{name}' not found — falling back to HSV.");
            var suggestions = NearestThemeNames(name, names, 5);
            if (suggestions.Count > 0)
                Console.Error.WriteLine($"  did you mean: {string.Join(", ", suggestions)}");
            return theme;
        }

        private static bool IsDefaultHsvFallback(IColorMap theme, string requested)
        {
            // GetPaletteByName returns a fresh HsvPalette when the name misses.
            // It also legitimately returns the real HSV palette when the user
            // asked for "HSV". Distinguish by checking the requested name.
            if (theme is not FracturingFog.Models.HsvPalette) return false;
            return !string.Equals(requested, FracturingFog.Models.HsvPalette.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static System.Collections.Generic.List<string> NearestThemeNames(
            string query, System.Collections.Generic.List<string> names, int max)
        {
            string q = query.ToLowerInvariant();
            var scored = new System.Collections.Generic.List<(int dist, string name)>(names.Count);
            foreach (var n in names)
            {
                int d = LevenshteinDistance(q, n.ToLowerInvariant());
                scored.Add((d, n));
            }
            scored.Sort((a, b) => a.dist.CompareTo(b.dist));
            // Only keep suggestions that are reasonably close — beyond half the
            // query length the suggestion is noise.
            int cutoff = System.Math.Max(3, q.Length / 2);
            var result = new System.Collections.Generic.List<string>();
            foreach (var (dist, n) in scored)
            {
                if (dist > cutoff) break;
                result.Add(n);
                if (result.Count >= max) break;
            }
            return result;
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = System.Math.Min(
                        System.Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
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

        private static FracturingFog.Imaging.ImageFileFormat GuessImageFormat(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".bmp" => FracturingFog.Imaging.ImageFileFormat.Bmp,
                ".tif" or ".tiff" => FracturingFog.Imaging.ImageFileFormat.Tiff,
                ".jpg" or ".jpeg" => FracturingFog.Imaging.ImageFileFormat.Jpeg,
                ".gif" => FracturingFog.Imaging.ImageFileFormat.Gif,
                ".webp" => FracturingFog.Imaging.ImageFileFormat.Webp,
                _ => FracturingFog.Imaging.ImageFileFormat.Png,
            };

        private static void EnsureDirectoryForFile(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }
}
