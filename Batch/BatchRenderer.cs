// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using System.IO;
using System.Threading;

using FracturingFog.Hosting;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

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

            // AOV EXR export (roadmap S1, #389): force the .exr writer regardless
            // of the output extension so `--aov-exr --out foo.png` still produces a
            // multi-layer EXR (foo.exr) rather than a flat PNG.
            if (opts.AovExr)
            {
                outPath = Path.ChangeExtension(outPath, ".exr");
                format = FracturingFog.Imaging.ImageFileFormat.Exr;
            }

            var fp = new FractalParameters();
            if (opts.BulbPower.HasValue)          fp.BulbPower          = opts.BulbPower.Value;
            if (opts.MultibrotExponent.HasValue)  fp.MultibrotExponent  = opts.MultibrotExponent.Value;
            if (!string.IsNullOrWhiteSpace(opts.LSystemPresetName))
                                                  fp.LSystemPresetName  = opts.LSystemPresetName!;
            if (opts.LSystemDepth.HasValue)       fp.LSystemDepth       = opts.LSystemDepth.Value;
            if (opts.PlasmaRoughness.HasValue)    fp.PlasmaRoughness    = opts.PlasmaRoughness.Value;
            if (opts.PlasmaSeed.HasValue)         fp.PlasmaSeed         = opts.PlasmaSeed.Value;
            if (!string.IsNullOrWhiteSpace(opts.FlamePresetName))
                                                  fp.FlamePresetName    = opts.FlamePresetName!;
            if (opts.FlameIterations.HasValue)    fp.FlameIterations    = opts.FlameIterations.Value;
            if (opts.FlameGamma.HasValue)         fp.FlameGamma         = opts.FlameGamma.Value;
            if (opts.FlameVibrancy.HasValue)      fp.FlameVibrancy      = opts.FlameVibrancy.Value;
            if (opts.InteriorAlpha.HasValue)      fp.InteriorAlpha      = opts.InteriorAlpha.Value;
            if (opts.AcidPattern.HasValue)        fp.AcidWarpPattern      = opts.AcidPattern.Value;
            if (opts.AcidFrequency.HasValue)      fp.AcidWarpFrequency    = opts.AcidFrequency.Value;
            if (opts.AcidWarpStrength.HasValue)   fp.AcidWarpWarpStrength = opts.AcidWarpStrength.Value;
            if (opts.AcidSeed.HasValue)           fp.AcidWarpSeed         = opts.AcidSeed.Value;
            if (opts.DomainWarp)                  fp.DomainWarpEnabled    = true;
            if (opts.DomainWarpStrength.HasValue) fp.DomainWarpStrength   = opts.DomainWarpStrength.Value;
            if (opts.DomainWarpFrequency.HasValue) fp.DomainWarpFrequency = opts.DomainWarpFrequency.Value;
            if (opts.Relief)                      fp.Relief2DEnabled      = true;
            if (opts.ReliefRaymarch)              fp.Relief2DRaymarch     = true;
            if (opts.ReliefHeight.HasValue)       fp.Relief2DHeightScale  = opts.ReliefHeight.Value;
            if (opts.ReliefDetailGain.HasValue)   fp.Relief2DDetailGain   = opts.ReliefDetailGain.Value;   // #518
            if (opts.ReliefDetailRadius.HasValue) fp.Relief2DDetailRadius = opts.ReliefDetailRadius.Value;  // #518
            if (opts.ReliefHeightGamma.HasValue)  fp.Relief2DHeightGamma  = opts.ReliefHeightGamma.Value;   // #518
            if (opts.ReliefStrength.HasValue)     fp.Relief2DStrength     = opts.ReliefStrength.Value;
            if (opts.ReliefLightAzimuth.HasValue) fp.Relief2DLightAzimuthDeg   = opts.ReliefLightAzimuth.Value;
            if (opts.ReliefLightElevation.HasValue) fp.Relief2DLightElevationDeg = opts.ReliefLightElevation.Value;
            if (opts.ReliefShadow.HasValue)       fp.Relief2DShadowStrength = opts.ReliefShadow.Value;
            if (opts.ReliefAbsolute)              fp.Relief2DAbsolute      = true;
            if (opts.ReliefCameraAzimuth.HasValue)   fp.Relief2DCameraAzimuthDeg   = opts.ReliefCameraAzimuth.Value;
            if (opts.ReliefCameraElevation.HasValue) fp.Relief2DCameraElevationDeg = opts.ReliefCameraElevation.Value;
            if (opts.ReliefCameraFov.HasValue)    fp.Relief2DCameraFovDeg  = opts.ReliefCameraFov.Value;
            if (opts.ReliefCameraZoom.HasValue)   fp.Relief2DCameraZoom    = opts.ReliefCameraZoom.Value;
            if (opts.ReliefCameraOrtho)           fp.Relief2DCameraOrthographic = true;
            if (opts.ReliefDofAperture.HasValue)  fp.Relief2DDofApertureRadius = opts.ReliefDofAperture.Value;
            if (opts.ReliefDofFocus.HasValue)     fp.Relief2DDofFocusDistance  = opts.ReliefDofFocus.Value;
            if (opts.ReliefFroxel)                fp.Relief2DFroxelVolumetrics = true;   // S6 (#408)
            if (opts.ReliefFroxelQuality is { } fq) fp.Relief2DFroxelQuality = fq;        // S6 (#408)
            if (opts.ReliefDenoiseIterations.HasValue)  fp.Relief2DDenoiseIterations = opts.ReliefDenoiseIterations.Value;   // S4 (#389)
            if (opts.ReliefDenoiseColorSigma.HasValue)  fp.Relief2DDenoiseColorSigma = opts.ReliefDenoiseColorSigma.Value;
            if (opts.ReliefDenoiseNormalSigma.HasValue) fp.Relief2DDenoiseNormalSigma = opts.ReliefDenoiseNormalSigma.Value;
            if (opts.ReliefDenoiseDepthSigma.HasValue)  fp.Relief2DDenoiseDepthSigma = opts.ReliefDenoiseDepthSigma.Value;
            if (opts.ReliefIsolate)               fp.Relief2DIsolate       = true;
            if (opts.ReliefIsolateNoDetail)       fp.Relief2DIsolateByDetail = false;
            if (opts.ReliefIsolateThreshold.HasValue) fp.Relief2DDetailThreshold = opts.ReliefIsolateThreshold.Value;
            if (opts.ReliefIsolateByColor)        fp.Relief2DIsolateByColor = true;
            if (!string.IsNullOrEmpty(opts.ReliefIsolateColors)) fp.Relief2DDropColorsCsv = opts.ReliefIsolateColors!;
            if (opts.ReliefIsolateTolerance.HasValue) fp.Relief2DColorTolerance = opts.ReliefIsolateTolerance.Value;

            // Per-light point / spot overrides (roadmap S8, #404). Apply each
            // non-null field onto the matching LightingFxData light; unset fields
            // keep the default. fp.Lighting is a struct → copy, mutate, write back.
            if (opts.Lights[0].HasAny || opts.Lights[1].HasAny || opts.Lights[2].HasAny)
            {
                var fxl = fp.Lighting;
                ApplyLightOverride(ref fxl.Light1, opts.Lights[0]);
                ApplyLightOverride(ref fxl.Light2, opts.Lights[1]);
                ApplyLightOverride(ref fxl.Light3, opts.Lights[2]);
                fp.Lighting = fxl;
            }

            // S6 (#408) — per-light fog contribution mask.
            if (opts.FogLightMask.HasValue)
            {
                var fxm = fp.Lighting;
                fxm.VolumeLightMask = opts.FogLightMask.Value;
                fp.Lighting = fxm;
            }

            var (pfBrightness, pfContrast, pfAdaptive) = ResolvePostFx(opts, null);

            // Full-precision centre: when a Mandelbrot region is used without a
            // manual --x/--y override, carry its extended limbs (CenterXLo/2/3)
            // into the poster so deep regions (zoom past ~1e15) render at the
            // right coordinate instead of a collapsed double centre.
            FractalRegion? limbRegion =
                (frType == FractalType.Mandelbrot
                 && opts.CenterX == null && opts.CenterY == null
                 && !string.IsNullOrWhiteSpace(opts.RegionName))
                ? FractalRegionLibrary.Instance.FindByName(opts.RegionName!)
                : null;

            var req = new PosterRequest
            {
                FractalType = frType,
                Width = opts.Width,
                Height = opts.Height,
                CenterX = cx,
                CenterXLo = limbRegion?.CenterXLo ?? 0,
                CenterX2 = limbRegion?.CenterX2 ?? 0,
                CenterX3 = limbRegion?.CenterX3 ?? 0,
                CenterY = cy,
                CenterYLo = limbRegion?.CenterYLo ?? 0,
                CenterY2 = limbRegion?.CenterY2 ?? 0,
                CenterY3 = limbRegion?.CenterY3 ?? 0,
                Zoom = zoom,
                MaxIterations = iter,
                ColorMap = theme,
                Quality = quality,
                FractalParameters = fp,
                Rotate = false,
                Path = outPath,
                Format = format,
                // Pre-composed exactly like the interactive poster path
                // (FractalRenderHost.CreatePosterRequest): "Region - Theme"
                // over the mandatory program/version line. Batch printed its
                // own "batch render" label here before, which is the kind of
                // per-surface special-casing this consolidation removes.
                // #54: watermark is on by default; --watermark/--no-watermark
                // suppresses it. Empty strings paint nothing.
                Watermark = opts.Watermark
                    ? WatermarkResolver.ComposeDefaultTopText(regionDispName, opts.ThemeName) : "",
                SubText = opts.Watermark ? WatermarkResolver.BuildDefaultSubText() : "",
                Brightness = pfBrightness,
                Contrast = pfContrast,
                HistogramEq = pfAdaptive,
                // S2 (#389) — output-stage view transform / tonemap. Null leaves
                // the poster default (None = identity, byte-identical).
                ViewTransform = opts.ViewTransform ?? FracturingFog.Imaging.ViewTransform.None,
                ViewExposureEv = (float)(opts.ViewExposureEv ?? 0.0),
                // S7 (#394) — ZIP-compress the EXR when requested. Ignored for
                // non-EXR output; honoured by both the plain and AOV writers.
                ExrCompression = opts.ExrZip
                    ? FracturingFog.Imaging.ExrCompression.Zip
                    : FracturingFog.Imaging.ExrCompression.None,
            };

            Console.WriteLine($"Batch image render");
            Console.WriteLine($"  fractal : {frType}");
            Console.WriteLine($"  region  : {regionDispName ?? "(manual)"}");
            Console.WriteLine($"  center  : x={cx:G14} y={cy:G14}");
            Console.WriteLine($"  zoom    : {zoom:G6}    iter: {iter}");
            Console.WriteLine($"  theme   : {opts.ThemeName}    quality: {quality.Name}");
            Console.WriteLine($"  size    : {opts.Width}x{opts.Height}");
            if (pfBrightness != 0 || pfContrast != 0 || pfAdaptive != 0)
                Console.WriteLine($"  post-fx : brightness {pfBrightness}  contrast {pfContrast}  adaptive {pfAdaptive}");
            if (req.ViewTransform != FracturingFog.Imaging.ViewTransform.None || req.ViewExposureEv != 0f)
                Console.WriteLine($"  view    : {req.ViewTransform}  exposure {req.ViewExposureEv:+0.##;-0.##;0} EV");
            Console.WriteLine($"  out     : {outPath}");

            using var spinner = new ConsoleSpinner("Rendering");
            try
            {
                if (opts.AovExr)
                {
                    // Multi-pass AOV render (beauty + each pass) → one multi-layer EXR.
                    var (aw, ah) = FracturingFog.Imaging.AovExrRenderer.RenderToFile(req, outPath, CancellationToken.None);
                    spinner.Stop($"saved {Path.GetFileName(outPath)} ({aw}x{ah}, {FracturingFog.Imaging.AovExrRenderer.DefaultViews.Count + 1} layers)");
                }
                else
                {
                    var result = PosterRenderer.RenderToFile(req, CancellationToken.None);
                    spinner.Stop($"saved {Path.GetFileName(outPath)} ({result.SavedWidth}x{result.SavedHeight}, {result.ElapsedMs} ms)");
                }
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

            // Full-precision zoom path: when a Mandelbrot region is used without
            // a manual --x/--y override, render frames through the region's
            // extended centre limbs (CenterXLo/2/3) instead of the double cx/cy
            // ResolveRegion collapses to — otherwise deep regions (zoom past
            // ~1e15) render at wrong/imprecise coordinates.
            FractalRegion? limbRegion =
                (frType == FractalType.Mandelbrot
                 && opts.CenterX == null && opts.CenterY == null
                 && !string.IsNullOrWhiteSpace(opts.RegionName))
                ? FractalRegionLibrary.Instance.FindByName(opts.RegionName!)
                : null;

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

            // WMF MP4 writer used only when no lossless preset selected. The
            // concrete Media-Foundation Mp4Writer lives in the Windows-only
            // Rendering.D3D assembly, which the cross-platform exe cannot
            // reference; go through BatchVideoWriterFactoryHook (wired by
            // WindowsBootstrap on Windows, null elsewhere). Null → PNG
            // sequence + ffmpeg, which every platform can run.
            IVideoWriter? mp4 = null;
            if (losslessPreset == null)
            {
                try
                {
                    mp4 = BootstrapHooks.BatchVideoWriterFactoryHook?.Invoke(
                        finalVideoPath, outW, outH, opts.VideoFps);
                    if (mp4 == null)
                        Console.WriteLine("  MP4 writer unavailable; PNG sequence + ffmpeg.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  MP4 writer unavailable ({ex.Message}); PNG sequence only.");
                }
            }

            Directory.CreateDirectory(pngFolder);

            // Post-FX from CLI flags (video has no preset — cfg = null).
            var (pfBrightness, pfContrast, pfAdaptive) = ResolvePostFx(opts, null);
            if (pfBrightness != 0 || pfContrast != 0 || pfAdaptive != 0)
                Console.WriteLine($"  post-fx  : brightness {pfBrightness}  contrast {pfContrast}  adaptive {pfAdaptive}");

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

                    uint[] buffer = limbRegion != null
                        ? RenderRegionMandelFrame(limbRegion, outW, outH, frameZoom, iter, theme, pfAdaptive, quality)
                        : RenderOneFrame(frType, outW, outH, cx, cy, frameZoom, iter, theme, quality, pfAdaptive);

                    // Brightness/Contrast BGRA post-pass (parity with the
                    // interactive image); HE already baked in the frame render.
                    ApplyBrightnessContrast(buffer, outW * outH, pfBrightness, pfContrast);

                    // --watermark bakes the region/theme + program sub-line
                    // into every emitted frame so the WMF Mp4Writer path AND
                    // the PNG sequence both carry the watermark.
                    if (opts.Watermark)
                        ApplyWatermarkInPlace(buffer, outW, outH,
                            regionDispName ?? frType.ToString(),
                            opts.ThemeName);

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
                        buffer, outW, outH, framePath, ImageFileFormat.Png,
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
            IColorMap theme, QualityPreset quality, int adaptive = 0)
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
                // #145: escape-time alt calculators equalize through the shared
                // HistogramEqualizer core just like Mandelbrot; non-escape-time
                // families don't implement the capability and carry no HE.
                // Brightness/contrast still apply downstream on the buffer.
                alt.Calculate(CancellationToken.None);
                if (adaptive > 0 && alt is FracturingFog.Interefaces.ISupportsHistogramEq heAlt)
                    heAlt.ApplyHistogramEqualization(adaptive / 100.0);
                var altBuf = CopyBuffer(alt.ColorBuffer, w, h);
                CompositeInteriorAlpha(altBuf, w, h, theme);
                return altBuf;
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
            if (adaptive > 0)
                calc.ApplyHistogramEqualization(adaptive / 100.0);
            var buf = CopyBuffer(calc.ColorBuffer, w, h);
            CompositeInteriorAlpha(buf, w, h, theme);
            return buf;
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

        // Default interior-alpha params for the headless video / slideshow paths.
        // Neither leg threads the interactive Interior(2D) knobs, so the composite
        // runs against the canonical default state (Checkerboard backdrop) — the
        // same thing the on-screen present shows a user who never touched them.
        private static readonly FractalParameters s_defaultInteriorFp = new();

        // #96 / F10.5 parity for headless video + slideshow frames. The D3D
        // present ignores alpha (always opaque) and composites authored
        // translucency — translucent-interior themes (InSetColor.A < 255, e.g.
        // Cuba Vacation) and per-colour-stop exterior alpha — over the theme's
        // Interior2DBackground. The image path already gets this through
        // PosterRenderer; the raw-calculator video/slideshow frames did not, so
        // a translucent theme exported straight alpha (flattened to black by the
        // encoder) instead of matching the window. Opaque themes are a no-op
        // (the compositor's gate early-returns), so this is byte-identical for
        // the common case.
        private static void CompositeInteriorAlpha(
            uint[] buf, int w, int h, IColorMap? theme)
        {
            Interior2DBackgroundCompositor.Composite(
                buf, buf, w, h, s_defaultInteriorFp,
                theme?.InSetColor ?? 0xFF000000u,
                alphaPreview: false, srcAlreadyProcessed: false);
        }

        private static uint[] CopyBuffer(uint[] src, int w, int h)
        {
            int n = w * h;
            if (src.Length == n) return (uint[])src.Clone();
            var dst = new uint[n];
            Array.Copy(src, dst, Math.Min(src.Length, n));
            return dst;
        }

        // ── Post-FX (parity with interactive ViewState post-processing) ────────

        // Number of frames a cross-fade of <paramref name="fadeMs"/> spans at
        // <paramref name="fps"/>. Mirrors the interactive fade's wall-clock
        // duration (the fade ms floor of 50 matches SlideshowEngine); minimum
        // two frames so a fade is never a single hard-cut frame.
        private static int FadeFrames(int fadeMs, int fps)
            => Math.Max(2, (int)Math.Round(Math.Max(50, fadeMs) / 1000.0 * fps));

        // Resolve effective brightness / contrast / adaptive(HE) values.
        // CLI flags (when present) override the named preset's PostFx block;
        // a null flag falls back to the preset (Image/Video pass cfg = null,
        // so they read the flags only). Clamped to the interactive ranges.
        // Apply a per-light --lightN-* override (roadmap S8, #404) onto a light,
        // field by field. Unset (null) fields keep the light's current value.
        private static void ApplyLightOverride(ref DirectionalLight d, BatchLightOverride o)
        {
            if (!o.HasAny) return;
            if (o.Type.HasValue)         d.Type = o.Type.Value;
            if (o.Intensity.HasValue)    d.Intensity = o.Intensity.Value;
            if (o.Theta.HasValue)        d.Theta = o.Theta.Value;
            if (o.Phi.HasValue)          d.Phi = o.Phi.Value;
            if (o.PosX.HasValue)         d.PosX = o.PosX.Value;
            if (o.PosY.HasValue)         d.PosY = o.PosY.Value;
            if (o.PosZ.HasValue)         d.PosZ = o.PosZ.Value;
            if (o.Range.HasValue)        d.Range = o.Range.Value;
            if (o.SpotInnerDeg.HasValue) d.SpotInnerDeg = o.SpotInnerDeg.Value;
            if (o.SpotOuterDeg.HasValue) d.SpotOuterDeg = o.SpotOuterDeg.Value;
            if (o.Color.HasValue)        d.Color = o.Color.Value;
            if (o.AreaAngularRadius.HasValue) d.AreaAngularRadius = o.AreaAngularRadius.Value;
        }

        private static (int brightness, int contrast, int adaptive) ResolvePostFx(
            BatchOptions opts, FracturingFog.Models.SlideshowConfig? cfg)
        {
            int b = opts.Brightness ?? ReadPostFxValue(cfg, "Brightness", 0);
            int c = opts.Contrast   ?? ReadPostFxValue(cfg, "Contrast", 0);
            int a = opts.Adaptive   ?? ReadPostFxValue(cfg, "HistogramEq", 0);
            return (Math.Clamp(b, -100, 100), Math.Clamp(c, -100, 100), Math.Clamp(a, 0, 100));
        }

        private static int ReadPostFxValue(
            FracturingFog.Models.SlideshowConfig? cfg, string key, int fallback)
        {
            var pf = cfg?.PostFx;
            if (pf == null || !pf.Enabled || pf.Values == null) return fallback;
            return pf.Values.TryGetValue(key, out double v) ? (int)Math.Round(v) : fallback;
        }

        // In-place brightness/contrast BGRA post-pass. Same math as
        // FractalRenderHost.UploadProcessedBuffer so batch output matches the
        // interactive image: contrast pivots around mid-grey (127.5), then
        // brightness offsets in 0..255 space.
        private static void ApplyBrightnessContrast(uint[] buf, int n, int brightness, int contrast)
        {
            if (brightness == 0 && contrast == 0) return;
            float contrastFactor = 1f + contrast / 100f;
            float brightnessOffset255 = brightness / 100f * 255f;
            int len = Math.Min(n, buf.Length);
            for (int i = 0; i < len; i++)
            {
                uint p = buf[i];
                float r = (p >> 16) & 0xFF;
                float g = (p >> 8) & 0xFF;
                float b = p & 0xFF;
                r = (r - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                g = (g - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                b = (b - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                byte R = (byte)Math.Clamp(r, 0f, 255f);
                byte G = (byte)Math.Clamp(g, 0f, 255f);
                byte B = (byte)Math.Clamp(b, 0f, 255f);
                buf[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }

        // Render one Mandelbrot frame for a region at an arbitrary zoom, honouring
        // the region's full-precision centre limbs + quality. Adaptive HE (when
        // > 0) is applied on the calculator before the buffer is copied out.
        // Shared by the video-slideshow leg loop (many zoom frames per region).
        private static uint[] RenderRegionMandelFrame(
            FractalRegion region, int w, int h, double zoom, int iter, IColorMap theme, int adaptive,
            QualityPreset? quality = null)
        {
            var calc = new MandelbrotCalculator(w, h)
            {
                CenterX = region.CenterX, CenterXLo = region.CenterXLo,
                CenterX2 = region.CenterX2, CenterX3 = region.CenterX3,
                CenterY = region.CenterY, CenterYLo = region.CenterYLo,
                CenterY2 = region.CenterY2, CenterY3 = region.CenterY3,
                Zoom = zoom,
                MaxIterations = iter,
                ColorMap = theme,
                Quality = quality ?? region.QualityPreset ?? QualityPreset.Standard,
            };
            calc.Calculate(CancellationToken.None);
            if (adaptive > 0) calc.ApplyHistogramEqualization(adaptive / 100.0);
            var buf = CopyBuffer(calc.ColorBuffer, w, h);
            CompositeInteriorAlpha(buf, w, h, theme);
            return buf;
        }

        // Cross-fade helper: write `fadeFrames` blended frames from `from` to
        // `to` into the PNG sequence, honouring the total frame budget. Returns
        // the number of frames written. Shared by region + theme boundary fades.
        private static int WriteCrossFade(
            PngSequenceWriter pngWriter, uint[] from, uint[] to, int n,
            int fadeFrames, int budgetRemaining)
        {
            if (from.Length != to.Length) return 0;
            var blend = new uint[to.Length];
            int written = 0;
            for (int s = 1; s <= fadeFrames && written < budgetRemaining; s++)
            {
                float a = s / (float)fadeFrames;
                float ia = 1f - a;
                for (int i = 0; i < n; i++)
                {
                    uint o = from[i], nw = to[i];
                    byte rB = (byte)(((o >> 16) & 0xFF) * ia + ((nw >> 16) & 0xFF) * a);
                    byte gB = (byte)(((o >> 8) & 0xFF) * ia + ((nw >> 8) & 0xFF) * a);
                    byte bB = (byte)((o & 0xFF) * ia + (nw & 0xFF) * a);
                    blend[i] = 0xFF000000u | ((uint)rB << 16) | ((uint)gB << 8) | bB;
                }
                pngWriter.WriteFrame(blend);
                written++;
            }
            return written;
        }

        // In-memory per-pixel lerp `from`→`to` at weight `a` (0..1). Used by the
        // concurrent theme cross-fade so the blended frame can still be zoomed,
        // post-processed, and watermarked before it is written.
        private static uint[] BlendFrames(uint[] from, uint[] to, float a, int n)
        {
            var outb = new uint[to.Length];
            float ia = 1f - a;
            int len = Math.Min(n, Math.Min(from.Length, to.Length));
            for (int i = 0; i < len; i++)
            {
                uint o = from[i], nw = to[i];
                byte rB = (byte)(((o >> 16) & 0xFF) * ia + ((nw >> 16) & 0xFF) * a);
                byte gB = (byte)(((o >> 8) & 0xFF) * ia + ((nw >> 8) & 0xFF) * a);
                byte bB = (byte)((o & 0xFF) * ia + (nw & 0xFF) * a);
                outb[i] = 0xFF000000u | ((uint)rB << 16) | ((uint)gB << 8) | bB;
            }
            return outb;
        }

        // Video-slideshow render loop: one animated log-zoom leg per region
        // (vStartZoom → region target, smoothstep-eased), cross-fading between
        // regions over regionFadeFrames. Each leg is split into themesPerLeg
        // theme segments; at each segment boundary the theme cross-fades over
        // themeFadeFrames CONCURRENTLY with the zoom (the fade blends both
        // themes rendered at the live zoom, consuming normal zoom frames) so the
        // video never stalls — mirroring the interactive video-slideshow.
        private static int RenderVideoSlideshowLegs(
            PngSequenceWriter pngWriter,
            System.Collections.Generic.List<FractalRegion> regionPool,
            System.Collections.Generic.List<string> regionNames,
            ShuffleBag<string> regionBag,
            System.Collections.Generic.List<string> themePool,
            Random rng,
            int outW, int outH, int totalFrames, int legFrames,
            int regionFadeFrames, int themeFadeFrames, int themesPerLeg,
            double startZoom, bool reverse, bool watermark,
            int pfBrightness, int pfContrast, int pfAdaptive)
        {
            uint[]? prevFrame = null;
            int framesWritten = 0;
            int lastTheme = -1;
            int n = outW * outH;

            while (framesWritten < totalFrames)
            {
                string regionName = regionBag.Draw(regionNames);
                var region = regionPool.Find(r =>
                    string.Equals(r.Name, regionName, StringComparison.Ordinal));
                if (region == null) break;

                int iter = region.Iterations > 0 ? region.Iterations : 1000;
                double target = Math.Max(region.Zoom, 1e-12);
                double z0 = reverse ? target : startZoom;
                double z1 = reverse ? startZoom : target;
                double logZ0 = Math.Log(z0), logZ1 = Math.Log(z1);

                // Pick up to themesPerLeg distinct non-black themes for this leg,
                // probing each at the deepest zoom (target). Fewer is fine — a
                // region with only one non-black theme plays a single-theme leg.
                var legThemeNames = new System.Collections.Generic.List<string>(themesPerLeg);
                var legThemeMaps = new System.Collections.Generic.List<IColorMap>(themesPerLeg);
                int attempts = Math.Max(1, themePool.Count);
                for (int at = 0; at < attempts && legThemeMaps.Count < themesPerLeg; at++)
                {
                    int ti;
                    do { ti = rng.Next(themePool.Count); }
                    while (themePool.Count > 1 && ti == lastTheme);
                    lastTheme = ti;
                    var name = themePool[ti];
                    if (legThemeNames.Contains(name)) continue;
                    var map = ResolveTheme(name);
                    var probe = RenderRegionMandelFrame(region, outW, outH, target, iter, map, pfAdaptive);
                    if (IsAllBlack(probe, n)) continue;
                    legThemeNames.Add(name);
                    legThemeMaps.Add(map);
                }
                if (legThemeMaps.Count == 0) continue; // no non-black theme; next region

                int legThemes = legThemeMaps.Count;
                int segLen = Math.Max(1, legFrames / legThemes);
                // Theme cross-fade runs CONCURRENTLY with the zoom: the fade
                // consumes ordinary zoom frames (each a blend of the outgoing
                // and incoming theme rendered at that frame's live zoom) rather
                // than inserting frozen frames. This is why the video no longer
                // stalls at a theme change. Fade length is capped to the segment
                // so a fade always completes before the next boundary.
                int themeFade = Math.Min(themeFadeFrames, segLen);
                var sw = Stopwatch.StartNew();
                int legFramesWritten = 0;
                Console.Write($"  leg [{framesWritten}/{totalFrames}]: {region.Name} / " +
                              $"{string.Join(",", legThemeNames)} zoom {z0:G4}->{z1:G4} … ");

                for (int f = 0; f < legFrames && framesWritten + legFramesWritten < totalFrames; f++)
                {
                    double t = legFrames == 1 ? 1.0 : (double)f / (legFrames - 1);
                    double te = t * t * (3.0 - 2.0 * t);        // smoothstep ease
                    double frameZoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);
                    int seg = Math.Min(legThemes - 1, f / segLen);

                    // A theme boundary opens a fade window at the start of a new
                    // segment: blend the previous theme into the new one while
                    // the zoom keeps advancing. Outside the window a single
                    // theme renders.
                    int intoSeg = f - seg * segLen;         // frames into this segment
                    uint[] frame;
                    if (seg > 0 && intoSeg < themeFade)
                    {
                        float a = (intoSeg + 1) / (float)themeFade;
                        var fromFrame = RenderRegionMandelFrame(
                            region, outW, outH, frameZoom, iter, legThemeMaps[seg - 1], pfAdaptive);
                        var toFrame = RenderRegionMandelFrame(
                            region, outW, outH, frameZoom, iter, legThemeMaps[seg], pfAdaptive);
                        frame = BlendFrames(fromFrame, toFrame, a, n);
                    }
                    else
                    {
                        frame = RenderRegionMandelFrame(region, outW, outH, frameZoom, iter, legThemeMaps[seg], pfAdaptive);
                    }

                    ApplyBrightnessContrast(frame, n, pfBrightness, pfContrast);
                    if (watermark)
                        ApplyWatermarkInPlace(frame, outW, outH, region.Name, legThemeNames[seg]);

                    // Region boundary cross-fade (between different regions /
                    // zoom sequences) stays a frozen fade — the previous leg has
                    // ended, so there is no shared zoom to advance across it.
                    if (f == 0 && prevFrame != null)
                    {
                        legFramesWritten += WriteCrossFade(
                            pngWriter, prevFrame, frame, n, regionFadeFrames,
                            totalFrames - (framesWritten + legFramesWritten));
                    }

                    if (framesWritten + legFramesWritten >= totalFrames) break;
                    pngWriter.WriteFrame(frame);
                    legFramesWritten++;
                    prevFrame = frame;
                }

                framesWritten += legFramesWritten;
                sw.Stop();
                Console.WriteLine($"{legFramesWritten} frames in {sw.ElapsedMilliseconds} ms");
            }
            return framesWritten;
        }

        // In-place watermark composite for batch video + slideshow buffers.
        //
        // Batch used to hand-roll its own content ("Region" + "Theme: X",
        // hardcoded white) and its own layout via the legacy GDI+ overload,
        // which is how it drifted out of step with every other surface. It now
        // goes through WatermarkResolver for content and WatermarkPainterSkia
        // for pixels, exactly like the live overlay and the save paths — and
        // paints straight onto the BGRA buffer, so the System.Drawing detour
        // (and the Windows-only constraint it carried) is gone.
        private static void ApplyWatermarkInPlace(
            uint[] pixels, int w, int h, string? regionName, string? themeName)
        {
            var wm = BuildBatchWatermark(regionName, themeName, pixels, w, h);
            if (wm == null) return;
            WatermarkPainterSkia.PaintOntoBgra(pixels, w, h, wm);
        }

        // Default-path watermark for headless renders: "Region - Theme" over
        // the mandatory program/version line, auto-contrasted against the
        // pixels the block will sit on. Batch has no custom-watermark CLI
        // surface, so the resolver's custom branches are unreachable here —
        // it is still the resolver that decides, not this call site.
        private static WatermarkRender? BuildBatchWatermark(
            string? regionName, string? themeName, uint[] pixels, int w, int h)
        {
            if (string.IsNullOrEmpty(regionName) && string.IsNullOrEmpty(themeName)) return null;

            var auto = ImageExport.ComputeContrastColor(
                System.Drawing.Color.White, watermark: true, pixels: pixels, imgW: w, imgH: h);

            return WatermarkResolver.Resolve(
                activeCustom: null,
                regionEmbedded: null,
                overrideRegionWatermark: false,
                useCustomWatermark: false,
                regionName: regionName ?? string.Empty,
                themeName: themeName ?? string.Empty,
                programName: WatermarkResolver.DefaultProgramName,
                programVersion: WatermarkResolver.DetectProgramVersion(),
                defaultTextColor: new RgbDef(auto.R, auto.G, auto.B));
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
            //
            // Mirror the interactive SlideshowEngine's structure (Bug fix):
            //   • ONE region is held for a full "region leg" of N theme
            //     sub-legs — N = 3 (Region Focus) or 8 (Color Focus,
            //     --more-colors) — instead of drawing a fresh random region
            //     every leg. Regions are drawn without replacement via a
            //     shuffle-bag so every region shows once before any repeat,
            //     no back-to-back (parity with SlideshowEngine._regionBag).
            //   • The first theme sub-leg of a region is a REGION cross-fade
            //     (RegionFadeMs); the remaining sub-legs are THEME cross-fades
            //     (ColorThemeFadeMs). The fade DURATION is honoured in
            //     wall-clock: the fade spans (fadeMs/1000 × fps) frames rather
            //     than a fixed FadeSteps count — the old code wrote exactly
            //     FadeSteps frames (~0.7 s @ 22 steps / 30 fps) which read as a
            //     hard-cut compared with the interactive 2 s fade.
            // --more-colors flips the cadence to Color Focus (8 themes/region,
            // shorter per-theme dwell) — synonym of the "Slideshow: More
            // Colors" context-menu item in the interactive UI.
            int themesPerRegion = opts.MoreColors ? 8 : 3;
            int totalRegionMs = Math.Max(3_000, cfg.Timing.TotalDisplayMsPerRegion);
            int legMs = Math.Max(800, totalRegionMs / themesPerRegion); // full per-theme leg incl. fade
            int legFrames = Math.Max(1, (int)Math.Round(legMs / 1000.0 * fps));
            int regionFadeFrames = FadeFrames(cfg.Timing.RegionFadeMs, fps);
            int themeFadeFrames = FadeFrames(cfg.Timing.ColorThemeFadeMs, fps);

            // Video-slideshow cadence (Type == Video): one animated zoom leg per
            // region — zoom from vStartZoom into the region's target zoom over
            // legSeconds — with a cross-fade between regions (RegionFadeMs).
            bool videoType = cfg.Type == FracturingFog.Models.SlideshowType.Video;
            double legSeconds = cfg.Video?.SecondsPerLeg > 0 ? cfg.Video.SecondsPerLeg : 8.0;
            int videoLegFrames = Math.Max(2, (int)Math.Round(legSeconds * fps));
            int videoThemesPerLeg = Math.Clamp(cfg.Video?.ThemesPerLeg ?? 3, 1, 8);
            double vStartZoom = Math.Max(opts.VideoStartZoom, 1e-12);
            bool vReverse = opts.VideoReverse || (cfg.Video?.Reverse ?? false);

            // Post-FX: CLI flags override the preset's PostFx block.
            var (pfBrightness, pfContrast, pfAdaptive) = ResolvePostFx(opts, cfg);

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
            Console.WriteLine($"  type      : {cfg.Type}");
            if (videoType)
                Console.WriteLine($"  cadence   : {legSeconds:G4}s zoom legs ({videoLegFrames} f/leg), " +
                                  $"{videoThemesPerLeg} themes/leg, start-zoom {vStartZoom:G4}" +
                                  $"{(vReverse ? " reverse" : "")}, fade region {regionFadeFrames}f / theme {themeFadeFrames}f");
            else
            {
                Console.WriteLine($"  cadence   : {themesPerRegion} themes/region, {legFrames} frames/leg");
                Console.WriteLine($"  fade      : region {regionFadeFrames}f / theme {themeFadeFrames}f");
            }
            Console.WriteLine($"  post-fx   : brightness {pfBrightness}  contrast {pfContrast}  adaptive {pfAdaptive}");
            Console.WriteLine($"  temp      : {tempRoot}");
            Console.WriteLine($"  encode    : {encodePreset}");
            Console.WriteLine($"  out       : {opts.OutputPath}");

            // 6. Render loop — outer region (shuffle-bag). Image type cycles
            // themes per region with static cross-fades; Video type plays one
            // animated zoom leg per region with cross-fades between regions.
            using var pngWriter = new PngSequenceWriter(tempRoot, outW, outH);
            var regionNames = regionPool.ConvertAll(r => r.Name);
            var regionBag = new ShuffleBag<string>(n => rng.Next(n), StringComparer.Ordinal);
            uint[]? prevFrame = null;
            int framesWritten = 0;

            if (videoType)
            {
                framesWritten = RenderVideoSlideshowLegs(
                    pngWriter, regionPool, regionNames, regionBag, themePool, rng,
                    outW, outH, totalFrames, videoLegFrames,
                    regionFadeFrames, themeFadeFrames, videoThemesPerLeg,
                    vStartZoom, vReverse, opts.Watermark,
                    pfBrightness, pfContrast, pfAdaptive);
            }
            else
            while (framesWritten < totalFrames)
            {
                string regionName = regionBag.Draw(regionNames);
                var region = regionPool.Find(r =>
                    string.Equals(r.Name, regionName, StringComparison.Ordinal));
                if (region == null) break; // pool emptied unexpectedly
                int lastTheme = -1;

                for (int tIdx = 0; tIdx < themesPerRegion && framesWritten < totalFrames; tIdx++)
                {
                    // Pick a non-black theme (retry up to one pass over the
                    // pool). HE is applied on the calculator before the colour
                    // buffer is read; the all-black test runs on the final
                    // coloured buffer so an HE-flattened frame is judged fairly.
                    uint[]? currFrame = null;
                    string? themeChosen = null;
                    long calcMs = 0;
                    int attempts = Math.Max(1, themePool.Count);
                    for (int at = 0; at < attempts; at++)
                    {
                        int ti;
                        do { ti = rng.Next(themePool.Count); }
                        while (themePool.Count > 1 && ti == lastTheme);
                        lastTheme = ti;
                        var themeName = themePool[ti];
                        var theme = ResolveTheme(themeName);

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
                        if (pfAdaptive > 0)
                            calc.ApplyHistogramEqualization(pfAdaptive / 100.0);
                        sw.Stop();
                        calcMs = sw.ElapsedMilliseconds;

                        if (IsAllBlack(calc.ColorBuffer, outW * outH)) continue;

                        // Own the pixels — calc.ColorBuffer is reused across
                        // calculators / recolours, and we mutate it below.
                        currFrame = CopyBuffer(calc.ColorBuffer, outW, outH);
                        // #96/F10.5: composite authored translucency over the
                        // interior backdrop before the B/C pass, watermark and
                        // cross-fade so slideshow frames match the on-screen
                        // present (see CompositeInteriorAlpha).
                        CompositeInteriorAlpha(currFrame, outW, outH, theme);
                        themeChosen = themeName;
                        break;
                    }
                    if (currFrame == null) break; // no non-black theme this region

                    // Brightness/Contrast BGRA post-pass (parity with the
                    // interactive UploadProcessedBuffer), baked before the fade
                    // so cross-fades interpolate the processed image.
                    ApplyBrightnessContrast(currFrame, outW * outH, pfBrightness, pfContrast);

                    if (opts.Watermark)
                        ApplyWatermarkInPlace(currFrame, outW, outH,
                            region.Name, themeChosen);

                    Console.Write($"  leg [{framesWritten}/{totalFrames}]: {region.Name} / {themeChosen} … ");

                    int fadeFrames = tIdx == 0 ? regionFadeFrames : themeFadeFrames;
                    int legFramesWritten = 0;

                    // Cross-fade prev → curr. Skipped only for the very first
                    // frame of the whole show (no prior frame to fade from).
                    if (prevFrame != null && prevFrame.Length == currFrame.Length)
                    {
                        var blend = new uint[currFrame.Length];
                        int n = outW * outH;
                        for (int s = 1; s <= fadeFrames && framesWritten + legFramesWritten < totalFrames; s++)
                        {
                            float a = s / (float)fadeFrames;
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
                    int holdFrames = Math.Max(0, legFrames - legFramesWritten);
                    int remaining = totalFrames - (framesWritten + legFramesWritten);
                    if (holdFrames > remaining) holdFrames = remaining;
                    for (int s = 0; s < holdFrames; s++)
                    {
                        pngWriter.WriteFrame(currFrame);
                        legFramesWritten++;
                    }

                    framesWritten += legFramesWritten;
                    prevFrame = currFrame;
                    Console.WriteLine($"{legFramesWritten} frames in {calcMs} ms");
                }
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

        // ── Scene (Scene Engine Roadmap S7) ─────────────────────────────────
        //
        // Offline, frame-locked render of a saved SceneData to MP4 via the
        // cross-platform SceneVideoRenderer (PNG sequence → ffmpeg). Adds
        // accumulation motion blur (--motion-blur N) and frame-composited
        // cross-fades the realtime S6 path deferred here. The scene + animation
        // libraries are loaded by BatchEntry before dispatch.
        public static int RenderScene(BatchOptions opts)
        {
            var scene = FracturingFog.Models.SceneLibrary.Instance.GetByName(opts.SceneName);
            if (scene == null)
            {
                var names = FracturingFog.Models.SceneLibrary.Instance.Scenes
                    .ConvertAll(s => s.Name);
                Console.Error.WriteLine(
                    $"Scene '{opts.SceneName}' not found in scenes.json. " +
                    $"Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}");
                return 3;
            }

            var encodePreset = opts.SlideshowEncode switch
            {
                BatchLossless.LosslessH264Mp4 => FfmpegEncoder.Preset.LosslessH264Mp4,
                BatchLossless.Ffv1Mkv         => FfmpegEncoder.Preset.Ffv1Mkv,
                _                             => FfmpegEncoder.Preset.HighQualityH264Mp4,
            };

            var sceneOpts = new FracturingFog.Export.SceneVideoOptions
            {
                Width = opts.Width,
                Height = opts.Height,
                Encode = encodePreset,
                OutputPath = opts.OutputPath,
                KeepFrames = opts.KeepFrames,
                Settings = new FracturingFog.Abstractions.Animation.SceneRenderSettings
                {
                    Fps = opts.VideoFps,
                    MotionBlurSubframes = opts.MotionBlurSubframes,
                    ShutterFraction = opts.ShutterFraction,
                },
            };

            int outW = opts.Width & ~1;
            int outH = opts.Height & ~1;
            Console.WriteLine($"Batch scene render");
            Console.WriteLine($"  scene       : {scene.Name}  ({scene.Shots.Count} shots, {scene.TotalDurationSeconds:G4}s authored)");
            Console.WriteLine($"  size        : {outW}x{outH}  fps: {opts.VideoFps}");
            Console.WriteLine($"  motion blur : {opts.MotionBlurSubframes} subframe(s), shutter {opts.ShutterFraction:G3}");
            Console.WriteLine($"  encode      : {encodePreset}");
            Console.WriteLine($"  out         : {opts.OutputPath}");

            if (!FfmpegEncoder.IsAvailable())
                Console.WriteLine("  note        : ffmpeg not found — will keep the PNG sequence instead of encoding.");

            // Phase 7 (#266) — deterministic audio-reactive export: analyse the
            // scene's audio file into a seekable modulation source, sampled at each
            // frame's scene time, and mux it into the encoded video. Headless, so no
            // live capture; reproducible from the file alone.
            if (scene.AudioTracks is { Count: > 0 } && !string.IsNullOrWhiteSpace(scene.AudioFilePath))
            {
                string? ff = FfmpegEncoder.FindFfmpeg();
                if (ff != null)
                {
                    var baked = FracturingFog.Audio.OfflineAudioAnalysis.AnalyzeFile(scene.AudioFilePath, ff);
                    if (baked != null)
                    {
                        sceneOpts.AudioSource = baked;
                        sceneOpts.AudioMuxPath = scene.AudioFilePath;
                        Console.WriteLine($"  audio       : {scene.AudioFilePath}  (audio-reactive)");
                    }
                    else Console.WriteLine($"  audio       : could not analyse '{scene.AudioFilePath}' — rendering silent.");
                }
            }

            var progress = new ConsoleProgress("Frames");
            var result = FracturingFog.Export.SceneVideoRenderer.Render(
                scene, sceneOpts,
                (frac, line) => progress.Report(frac, line),
                CancellationToken.None);
            progress.Finish(result.Ok ? $"frames={result.FramesWritten}" : "incomplete");

            if (result.Ok)
            {
                if (!string.IsNullOrEmpty(result.VideoPath))
                    Console.WriteLine($"Scene saved: {result.VideoPath}");
                else if (!string.IsNullOrEmpty(result.FrameFolder))
                    Console.WriteLine($"PNG sequence kept at: {result.FrameFolder}");
                return 0;
            }

            Console.Error.WriteLine(result.Message ?? "Scene render failed.");
            // ffmpeg-missing left a recoverable PNG sequence — not a hard failure.
            return string.IsNullOrEmpty(result.FrameFolder) ? 4 : 1;
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
                // roadmap S7: a typed `.exr` --out routes through OpenExrWriter
                // (scene-linear HDR half RGBA) even without --aov-exr.
                ".exr" => FracturingFog.Imaging.ImageFileFormat.Exr,
                _ => FracturingFog.Imaging.ImageFileFormat.Png,
            };

        private static void EnsureDirectoryForFile(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }
}
