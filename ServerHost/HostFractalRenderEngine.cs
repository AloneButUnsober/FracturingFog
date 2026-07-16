// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ServerHost/HostFractalRenderEngine.cs
// WinExe-side IFractalRenderEngine implementation. Binds the protocol
// RenderRequestDto to the existing PosterRenderer / calculator zoo and to a
// log-zoom video loop modelled on Batch/BatchRenderer.cs:RenderVideo but
// re-implemented here so we can flow the per-job CancellationToken through.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Hosting;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Server;
using FracturingFog.Server.Cluster;
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

        // Fall back to a client-supplied region payload when the named lookup
        // misses but the client carried an inline FractalRegion JSON blob.
        // The blob has already passed RegionPayloadValidator in FFServer
        // (size, FractalType allowlist, forbidden-fields scrub) — this is
        // just the typed deserialize. Strip the user-authored-code fields
        // belt-and-braces in case the validator missed an alias.
        if (region == null && !string.IsNullOrEmpty(req.RegionJson))
        {
            try
            {
                region = JsonSerializer.Deserialize<FractalRegion>(req.RegionJson, RegionJsonOpts)
                    ?? throw new ServerProtocolException("bad-region-payload",
                            "regionJson deserialised to null");
            }
            catch (JsonException ex)
            {
                throw new ServerProtocolException("bad-region-payload",
                    $"regionJson deserialise failed: {ex.Message}");
            }
            region.UserBulbSource = null;
            region.UserBulbName = null;
            region.UserEquationName = null;
            region.SandboxName = null;
        }

        if (!string.IsNullOrWhiteSpace(req.RegionName) && region == null)
            throw new ServerProtocolException("unknown-region",
                $"unknown region '{req.RegionName}' and no inline regionJson supplied");

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

        IColorMap theme = ResolveTheme(req, log);

        bool isVideo = string.Equals(req.Mode, "video", StringComparison.OrdinalIgnoreCase);
        bool hasQuadLimbs = cxLo != 0 || cx2 != 0 || cx3 != 0 || cyLo != 0 || cy2 != 0 || cy3 != 0;
        log.Info($"resolved: fractal={ftype} cx={cx:G14} cy={cy:G14} zoom={zoom:G6} iter={iter} " +
                 $"size={req.Width}x{req.Height} quality={quality.Name} theme={req.ThemeName} " +
                 $"quadLimbs={(hasQuadLimbs ? "yes" : "no")}");

        return isVideo
            ? RenderVideoArtifactAsync(req, ftype, cx, cxLo, cx2, cx3, cy, cyLo, cy2, cy3, zoom, iter, theme, quality, region, workDir, log, ct)
            : RenderImageArtifactAsync(req, ftype, cx, cxLo, cx2, cx3, cy, cyLo, cy2, cy3, zoom, iter, theme, quality, region, workDir, log, ct);
    }

    /// <summary>JSON options for inline theme / region payloads. Mirrors the
    /// wire convention used by JsonRpcFraming (camelCase) so a client that
    /// serialised via System.Text.Json defaults round-trips into the typed
    /// Models DTOs. <see cref="JsonStringEnumConverter"/> wired up so
    /// FractalType / ColorThemeKind enum-name strings deserialise to the
    /// correct enum values without an explicit converter on every property.</summary>
    private static readonly JsonSerializerOptions RegionJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Resolve the theme name first against the host's combined
    /// built-in + user theme library; if that misses but the client carried
    /// inline ColorThemeData JSON, instantiate a transient data-driven theme
    /// for this render only. Built-in algorithmic themes always win when the
    /// name matches — the inline payload is the fallback, not an override.</summary>
    private static IColorMap ResolveTheme(RenderRequestDto req, ISessionLog log)
    {
        // GetPaletteByName silently returns HsvPalette as a fallback when the
        // name is unknown, so we cannot use it as a hit-test. Check the name
        // list explicitly first.
        var known = FracturingFog.Models.ColorPalette.GetPaletteNames();
        bool isKnown = false;
        foreach (var n in known)
        {
            if (string.Equals(n, req.ThemeName, StringComparison.OrdinalIgnoreCase))
            { isKnown = true; break; }
        }

        if (isKnown)
            return FracturingFog.Models.ColorPalette.GetPaletteByName(req.ThemeName);

        if (!string.IsNullOrEmpty(req.ThemeJson))
        {
            ColorThemeData? data;
            try { data = JsonSerializer.Deserialize<ColorThemeData>(req.ThemeJson, RegionJsonOpts); }
            catch (JsonException ex)
            {
                throw new ServerProtocolException("bad-theme-payload",
                    $"themeJson deserialise failed: {ex.Message}");
            }
            if (data == null)
                throw new ServerProtocolException("bad-theme-payload",
                    "themeJson deserialised to null");
            IColorMap? map = DataDrivenColorThemes.Create(data);
            if (map == null)
                throw new ServerProtocolException("bad-theme-payload",
                    "themeJson missing required fields (needs at least 2 stops)");
            log.Info($"using transient inline theme '{data.Name}' kind={data.Kind} stops={data.Stops.Count}");
            return map;
        }

        throw new ServerProtocolException("unknown-theme",
            $"unknown theme '{req.ThemeName}' and no inline themeJson supplied");
    }

    /// <summary>Resolve which watermark the server should composite. Honours
    /// the server's WatermarkMode + the region's EmbeddedWatermark + the
    /// client's request payload, in that order:
    ///   1. region.EmbeddedWatermark always wins (publisher's intent).
    ///   2. WatermarkMode=Client + valid clientWatermarkJson → client's def.
    ///   3. WatermarkMode=Custom + ServerCustomWatermarkName → server's def.
    ///   4. → null (= legacy default).</summary>
    private static WatermarkDef? ResolveServerWatermark(RenderRequestDto req, FractalRegion? region, ISessionLog log)
    {
        if (region?.EmbeddedWatermark != null)
            return region.EmbeddedWatermark.Clone();

        var cfg = ServerConfig.LoadOrDefault();
        switch (cfg.WatermarkMode)
        {
            case ServerWatermarkMode.Client:
                if (req.UseClientWatermark && !string.IsNullOrWhiteSpace(req.ClientWatermarkJson))
                {
                    try
                    {
                        WatermarkPayloadValidator.Validate(req.ClientWatermarkJson!);
                        return UserWatermarkStore.DeserializeOne(req.ClientWatermarkJson!);
                    }
                    catch (ServerProtocolException ex)
                    {
                        log.Warn($"client watermark refused: {ex.Message}");
                    }
                }
                return null;

            case ServerWatermarkMode.Custom:
                try { UserWatermarkStore.Instance.Load(); } catch { }
                return UserWatermarkStore.Instance.GetByName(cfg.ServerCustomWatermarkName)?.Clone();

            case ServerWatermarkMode.Default:
            default:
                return null;
        }
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

        // FFServer already rewrote req.Width/Height from inches×dpi when
        // poster fields were set, so the dims here are post-resolution.
        // Rotate + Dpi still need to flow into the PosterRequest so the
        // saved PNG carries the right orientation and DPI metadata.
        bool posterMode = req.PosterDpi is int pdpi0 && pdpi0 > 0
                          && req.PosterInchesW is double piw0 && piw0 > 0
                          && req.PosterInchesH is double pih0 && pih0 > 0;
        float dpiStamp = posterMode ? req.PosterDpi!.Value : 0f;

        // D-6b — when the master attached a precomputed reference orbit
        // blob to a tile render, decode it once here and forward to the
        // calculator via PosterRequest.SeededOrbit. Decode failures fall
        // back to per-tile compute (logged warning) — never abort the
        // render, because a partial degradation beats a hard failure for
        // an opt-in perf path.
        MandelbrotCalculator.OrbitDD? seededOrbit   = null;
        MandelbrotCalculator.OrbitQD? seededOrbitQD = null;
        MandelbrotCalculator.OrbitOD? seededOrbitOD = null;
        if (!string.IsNullOrEmpty(req.RefOrbitBlobBase64) && ftype == FractalType.Mandelbrot)
        {
            try
            {
                byte[] blob = Convert.FromBase64String(req.RefOrbitBlobBase64!);
                var decoded = ReferenceOrbitBlobCodec.Decode(blob);
                // Accept the seed only when the calculator's centre Hi/Lo
                // + cap match what the master used. Higher limbs (QD X2/X3,
                // OD X4..X7) are derived from the same request fields the
                // master used; the calculator's centerSame check still
                // guards against any silent stale-orbit reuse.
                bool centreOk = decoded.CentreX   == cx
                             && decoded.CentreXLo == cxLo
                             && decoded.CentreY   == cy
                             && decoded.CentreYLo == cyLo;
                bool capOk = iter <= decoded.MaxIter;
                if (centreOk && capOk)
                {
                    switch (decoded.Limbs)
                    {
                        case ReferenceOrbitBlobCodec.LimbsDD:
                            seededOrbit = new MandelbrotCalculator.OrbitDD
                            {
                                CentreX   = decoded.CentreX,
                                CentreXLo = decoded.CentreXLo,
                                CentreY   = decoded.CentreY,
                                CentreYLo = decoded.CentreYLo,
                                MaxIter   = decoded.MaxIter,
                                RefLen    = decoded.RefLen,
                                Escaped   = decoded.Escaped,
                                Zr        = decoded.RefZr,
                                Zi        = decoded.RefZi,
                                ZrLo      = decoded.RefZrLo,
                                ZiLo      = decoded.RefZiLo,
                            };
                            break;
                        case ReferenceOrbitBlobCodec.LimbsQD:
                            seededOrbitQD = new MandelbrotCalculator.OrbitQD
                            {
                                CentreX   = decoded.CentreX,  CentreXLo = decoded.CentreXLo,
                                CentreX2  = decoded.CentreX2, CentreX3  = decoded.CentreX3,
                                CentreY   = decoded.CentreY,  CentreYLo = decoded.CentreYLo,
                                CentreY2  = decoded.CentreY2, CentreY3  = decoded.CentreY3,
                                MaxIter   = decoded.MaxIter,
                                RefLen    = decoded.RefLen,
                                Escaped   = decoded.Escaped,
                                Zr   = decoded.RefZr,   Zi   = decoded.RefZi,
                                ZrLo = decoded.RefZrLo, ZiLo = decoded.RefZiLo,
                                ZrX2 = decoded.RefZrX2, ZiX2 = decoded.RefZiX2,
                                ZrX3 = decoded.RefZrX3, ZiX3 = decoded.RefZiX3,
                            };
                            break;
                        case ReferenceOrbitBlobCodec.LimbsOD:
                            seededOrbitOD = new MandelbrotCalculator.OrbitOD
                            {
                                CentreX   = decoded.CentreX,  CentreXLo = decoded.CentreXLo,
                                CentreX2  = decoded.CentreX2, CentreX3  = decoded.CentreX3,
                                CentreX4  = decoded.CentreX4, CentreX5  = decoded.CentreX5,
                                CentreX6  = decoded.CentreX6, CentreX7  = decoded.CentreX7,
                                CentreY   = decoded.CentreY,  CentreYLo = decoded.CentreYLo,
                                CentreY2  = decoded.CentreY2, CentreY3  = decoded.CentreY3,
                                CentreY4  = decoded.CentreY4, CentreY5  = decoded.CentreY5,
                                CentreY6  = decoded.CentreY6, CentreY7  = decoded.CentreY7,
                                MaxIter   = decoded.MaxIter,
                                RefLen    = decoded.RefLen,
                                Escaped   = decoded.Escaped,
                                Zr   = decoded.RefZr,   Zi   = decoded.RefZi,
                                ZrLo = decoded.RefZrLo, ZiLo = decoded.RefZiLo,
                                ZrX2 = decoded.RefZrX2, ZiX2 = decoded.RefZiX2,
                                ZrX3 = decoded.RefZrX3, ZiX3 = decoded.RefZiX3,
                                ZrX4 = decoded.RefZrX4, ZiX4 = decoded.RefZiX4,
                                ZrX5 = decoded.RefZrX5, ZiX5 = decoded.RefZiX5,
                                ZrX6 = decoded.RefZrX6, ZiX6 = decoded.RefZiX6,
                                ZrX7 = decoded.RefZrX7, ZiX7 = decoded.RefZiX7,
                            };
                            break;
                    }
                    log.Info($"seeded ref orbit: limbs={decoded.Limbs} refLen={decoded.RefLen} maxIter={decoded.MaxIter} escaped={decoded.Escaped}");
                }
                else
                {
                    log.Warn($"ref orbit blob rejected: centreOk={centreOk} capOk={capOk} (orbitMaxIter={decoded.MaxIter} vs reqIter={iter})");
                }
            }
            catch (Exception ex)
            {
                log.Warn($"ref orbit blob decode failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var poster = new PosterRequest
        {
            FractalType = ftype,
            Width = req.Width,
            Height = req.Height,
            CenterX = cx,
            CenterXLo = cxLo,
            CenterX2 = cx2,
            CenterX3 = cx3,
            // D-6b2 — OD limbs from the request (zero outside the OD
            // cluster path so legacy single-server renders are unchanged).
            CenterX4 = req.CenterX4,
            CenterX5 = req.CenterX5,
            CenterX6 = req.CenterX6,
            CenterX7 = req.CenterX7,
            CenterY = cy,
            CenterYLo = cyLo,
            CenterY2 = cy2,
            CenterY3 = cy3,
            CenterY4 = req.CenterY4,
            CenterY5 = req.CenterY5,
            CenterY6 = req.CenterY6,
            CenterY7 = req.CenterY7,
            Zoom = zoom,
            MaxIterations = iter,
            ColorMap = theme,
            Quality = quality,
            FractalParameters = new FractalParameters(),
            Rotate = posterMode && req.PosterPortrait,
            Path = outPath,
            Format = FracturingFog.Imaging.ImageFileFormat.Png,
            Watermark = req.SuppressDecorations ? "" : (region?.Name ?? ""),
            SubText = req.SuppressDecorations ? "" : "Fracturing Fog server render",
            Dpi = dpiStamp,
            CustomWatermark = req.SuppressDecorations ? null : ResolveServerWatermark(req, region, log),
            // D-6b — sub-rect geometry + seeded orbit forwarded to the
            // calculator. All zero / null for legacy single-server renders.
            ImageWidth     = req.ImageWidth,
            ImageHeight    = req.ImageHeight,
            SubRectOffsetX = req.SubRectOffsetX,
            SubRectOffsetY = req.SubRectOffsetY,
            SeededOrbit    = seededOrbit,
            SeededOrbitQD  = seededOrbitQD,
            SeededOrbitOD  = seededOrbitOD,
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

        // S-X7.1b (2026-06-23) — go through BootstrapHooks.NativeVideoWriterFactoryHook
        // instead of constructing Mp4Writer directly. On Windows the hook is
        // wired to Media Foundation via FracturingFog.Win.WindowsBootstrap;
        // on Linux/macOS it stays null and we fall through to the ffmpeg PNG-
        // sequence encode so the server still produces an .mp4 without a
        // Windows-only dep. The IVideoWriter abstraction means this file no
        // longer references Mp4Writer (or Rendering.D3D) at all, so it can
        // compile into the cross-plat FracturingFog.App.
        IVideoWriter? mp4 = null;
        if (losslessPreset == null)
        {
            mp4 = BootstrapHooks.NativeVideoWriterFactoryHook?.Invoke(finalVideoPath, outW, outH);
            if (mp4 == null)
            {
                if (!FfmpegEncoder.IsAvailable())
                    throw new ServerProtocolException("ffmpeg-missing",
                        "No native Mp4Writer (non-Windows host) and ffmpeg not on PATH; " +
                        "install ffmpeg or run --lossless h264.");
                losslessPreset = FfmpegEncoder.Preset.HighQualityH264Mp4;
                log.Info("native Mp4Writer unavailable → ffmpeg HighQualityH264Mp4");
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
        IVideoWriter? mp4Local = mp4;
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
                    // S-X7.1b (2026-06-23) — cross-plat Skia PNG via ImageExport.
                    // ImageExportGdi lives in FracturingFog.Win and is Win-only;
                    // ImageExport.SavePixelsToFile is the Skia path used everywhere
                    // else (FractalRenderHost.SaveLastFrameToPng etc.).
                    ImageExport.SavePixelsToFile(
                        buffer, outW, outH, framePath, ImageFileFormat.Png,
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
