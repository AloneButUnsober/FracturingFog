// Rendering/FractalRenderHost.Video.cs
//
// Video Zoom engine — partial of FractalRenderHost implementing
// IVideoZoomController. Ported faithfully from the legacy MainForm partial
// (VideoZoom.cs) but driving the host's calculator + renderer internals on a
// dedicated background thread instead of marshalling each frame onto the UI
// thread.
//
// Why a partial (not a standalone UI.Avalonia class): the engine is intimately
// coupled to FractalRenderHost privates — _calculator + the alt-calculator
// fleet, SelectAltCalculator, UploadProcessedBuffer, _lastUploadedBuffer,
// _calcCts/_calcLock/_d3dGate — and to MandelbrotCalculator's recolor / CDF /
// dither internals. Living inside the class gives direct access without
// widening the public surface. The legacy VideoZoom.cs stays untouched.
//
// Threading: the whole loop runs on a Task.Run thread. The only thread-affine
// resource is the D3D11 immediate context, already serialised behind _d3dGate
// inside UploadProcessedBuffer / PresentBuffer. Calculator buffers are not
// thread-affine, so per-frame ApplyVideoFrameState → Calculate → recolor →
// upload all run sequentially on that one thread — no UI marshalling. The
// StatusChanged / RecordingFinished / Stopped events therefore fire on the
// background thread; the Avalonia subscriber marshals to the UI thread itself
// (this assembly has no Avalonia / Dispatcher reference).
//
// Save flow stays shell-side: the engine creates the Mp4Writer /
// PngSequenceWriter, feeds them the post-FX frames, finalises (Dispose) when
// the zoom ends, then raises RecordingFinished with the temp artefact paths +
// chosen encode. The shell does the SaveFileDialog / folder-pick / ffmpeg
// encode (it owns the Avalonia + ffmpeg plumbing).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.ViewState;

namespace FracturingFog.Rendering
{
    public sealed partial class FractalRenderHost : IVideoZoomController
    {
        // ── Single-shot video state ───────────────────────────────────────
        private bool _videoRunning;
        private CancellationTokenSource? _videoCts;
        private readonly object _videoLock = new();

        // ── Video slideshow state (independent so each is stoppable alone) ─
        private bool _videoSlideshowRunning;
        private CancellationTokenSource? _videoSlideshowCts;
        private CancellationTokenSource? _videoSlideshowLegCts;
        private readonly object _videoSlideshowLock = new();
        private readonly Random _videoRng = new();

        private const double VideoSlideshowSeconds = 30.0;
        private const int VideoSlideshowPauseMs = 7_000;

        // Fraction of total duration spent panning (rest is the zoom phase).
        private const double VideoPanFraction = 0.05;

        // Active quality preset for the running zoom — auto-promoted upward by
        // ApplyVideoFrameState as the zoom crosses each tier's ZoomMax. Capped
        // at Ultra (Extreme is excluded for video).
        private QualityPreset _videoQuality = QualityPreset.Standard;

        // Per-region iter floor carried across the current zoom / leg. Zero ⇒
        // use the preset's computed count only.
        private int _videoTargetIterations;

        // ── Recorders (single-shot only; slideshow never records) ──────────
        private Mp4Writer? _videoMp4Writer;
        private string? _videoMp4TempPath;
        private Stopwatch? _videoMp4Sw;
        private PngSequenceWriter? _videoPngWriter;
        private string? _videoPngFolder;
        private VideoLosslessEncode _videoLosslessEncode = VideoLosslessEncode.None;

        // ── Per-leg histogram-equalization CDF lock ────────────────────────
        private double[]? _videoLegCdf;
        private int _videoLegCdfBins;
        private int _videoLegCdfMaxIter;
        private bool _videoLegCdfStale;
        private const double VideoCdfSaturationRebuildFraction = 0.02;

        // ── Per-leg temporal reprojection (TAA-lite) state ─────────────────
        private uint[]? _videoPrevColorBuffer;
        private uint[]? _videoBlendScratch;
        private int _videoPrevWidth;
        private int _videoPrevHeight;
        private bool _videoPrevHasFrame;
        private double _videoPrevCenterX, _videoPrevCenterXLo, _videoPrevCenterX2, _videoPrevCenterX3;
        private double _videoPrevCenterY, _videoPrevCenterYLo, _videoPrevCenterY2, _videoPrevCenterY3;
        private double _videoPrevZoom;

        private const double VideoTaaAlphaCeiling = 0.85;
        private double _videoTaaAlpha = 0.55 * VideoTaaAlphaCeiling;
        private double _videoTaaDeepZoomFadeStart = 1e15;
        private double _videoTaaDeepZoomFadeEnd = 1e18;

        // ── Band-edge dither ───────────────────────────────────────────────
        private bool _videoBandDitherEnabled;
        private double _videoBandDitherStrength;

        // Theme registry — attached by the bootstrap after the theme service is
        // constructed (the slideshow needs it to enumerate + apply palettes).
        private IColorThemeService? _videoThemeService;

        // ── Per-leg colour-theme schedule ─────────────────────────────────
        // Populated by VideoSlideshowLoop before each VideoLoop call. Entries
        // are (legFractionAtFireTime, themeName); VideoLoop calls
        // TryRunScheduledThemeFade before each frame, walks past every entry
        // whose t-fraction has elapsed, and cross-fades the on-screen palette
        // from the outgoing buffer to the freshly-recoloured incoming buffer.
        // Same-leg zoom advancement freezes for the fade window (matches the
        // image slideshow's mid-region theme-fade UX).
        private List<(double T, string Theme)>? _videoLegThemeSchedule;
        private int _videoLegThemeIdx;
        private Stopwatch? _videoLegSw;
        // Cross-fade tuning for in-leg theme transitions. Time-based now —
        // the fade runs concurrently with zoom advancement by swapping in a
        // BlendedColorMap whose T ticks each frame. ~1 s by default.
        private const double VideoThemeFadeSeconds = 1.0;
        // Active fade state. Non-null between schedule fire and T>=1; the
        // _calculator.ColorMap is the BlendedColorMap during that window.
        private BlendedColorMap? _videoActiveFade;
        private IColorMap? _videoActiveFadeTo;
        private double _videoActiveFadeStartSecs;

        /// <summary>Adaptive-sweep schedule for the running video slideshow.
        /// Null = no sweep (slider stays at user value). Shell sets before
        /// StartVideoSlideshow; engine drives <see cref="VideoAdaptiveValueSink"/>.</summary>
        public AdaptiveSweepConfig? VideoSweepConfig { get; set; }

        /// <summary>Callback invoked with the current Adaptive slider value as
        /// the per-leg ramp advances. Shell wires this to
        /// <c>FloatingMenu.Adaptive</c>.</summary>
        public Action<int>? VideoAdaptiveValueSink { get; set; }

        private readonly record struct QDCoord(double Hi, double Lo, double X2, double X3);

        // ──────────────────────────────────────────────────────────────────
        // IVideoZoomController surface
        // ──────────────────────────────────────────────────────────────────

        public bool IsRunning => _videoRunning || _videoSlideshowRunning;
        public bool IsSlideshowRunning => _videoSlideshowRunning;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<VideoRecordingResult>? RecordingFinished;
        public event EventHandler? Stopped;

        /// <summary>Bootstrap hook: hand the engine the host theme service so
        /// the video slideshow can enumerate + silently apply palettes. Safe to
        /// call once after construction (HostColorThemeService is built with the
        /// render host, so it can't be passed via the ctor).</summary>
        public void AttachThemeService(IColorThemeService service) => _videoThemeService = service;

        /// <inheritdoc/>
        public void StartVideo(VideoZoomRequest request)
        {
            if (_disposed || request == null || IsRunning) return;

            ApplySmoothing(request);

            double ultraMax = QualityPreset.Ultra.ZoomMax;
            double draftMin = QualityPreset.Draft.ZoomMin;
            double tz = request.TargetZoom;
            if (tz > ultraMax) tz = ultraMax;
            if (tz < draftMin) tz = draftMin;

            var tCX = new QDCoord(request.TargetCXHi, request.TargetCXLo, request.TargetCX2, request.TargetCX3);
            var tCY = new QDCoord(request.TargetCYHi, request.TargetCYLo, request.TargetCY2, request.TargetCY3);

            QDCoord startCX, startCY, targetCX, targetCY;
            double startZoom, targetZoom;

            if (request.IsReverse)
            {
                // Begin AT the target/zoom, animate back to the classic view.
                startCX = tCX; startCY = tCY; startZoom = tz;
                targetCX = new QDCoord(FractalViewState.DefaultCenterX, 0.0, 0.0, 0.0);
                targetCY = new QDCoord(FractalViewState.DefaultCenterY, 0.0, 0.0, 0.0);
                targetZoom = FractalViewState.DefaultZoom;

                _videoQuality = QualityPreset.Standard;
                foreach (var p in QualityPreset.All)
                {
                    if (p.Tier == QualityTier.Extreme) continue;
                    if (p.ZoomMax >= startZoom) { _videoQuality = p; break; }
                }
            }
            else
            {
                // Forward: classic → target. ApplyVideoFrameState promotes the
                // tier upward as the zoom phase crosses each ZoomMax.
                startCX = new QDCoord(FractalViewState.DefaultCenterX, 0.0, 0.0, 0.0);
                startCY = new QDCoord(FractalViewState.DefaultCenterY, 0.0, 0.0, 0.0);
                startZoom = FractalViewState.DefaultZoom;
                targetCX = tCX; targetCY = tCY; targetZoom = tz;
                _videoQuality = QualityPreset.Standard;
            }

            _videoTargetIterations = request.TargetIterations;

            // Capture recorder intent here (UI thread) but defer the actual
            // Media Foundation / PNG writer creation to the background loop
            // thread below. The MF sink writer is apartment-bound: created on
            // the Avalonia UI (STA) thread and then fed frames from the MTA
            // Task.Run loop, the cross-apartment QueryInterface on
            // IMFSinkWriter fails (InvalidCastException). Creating, writing and
            // disposing all on the same MTA loop thread avoids the marshal.
            bool wantMp4 = request.IsSaveVideo;
            bool wantPng = request.IsSaveLossless;
            var pngEncode = request.LosslessEncode;

            _videoRunning = true;
            RaiseStatus(request.IsReverse
                ? $"Video reverse zoom → classic from zoom={startZoom:G4} over {request.Seconds:F1}s"
                : $"Video zoom → zoom={targetZoom:G4} over {request.Seconds:F1}s");

            CancellationTokenSource cts;
            lock (_videoLock)
            {
                _videoCts?.Cancel();
                _videoCts = new CancellationTokenSource();
                cts = _videoCts;
            }

            double seconds = request.Seconds;
            bool reverse = request.IsReverse;

            // Build the in-zoom theme-fade schedule for single-shot when the
            // dialog asked for it. Slideshow has its own scheduler.
            _videoLegThemeSchedule = null;
            _videoLegThemeIdx = 0;
            if (request.ThemeFadeEnabled && _videoThemeService != null)
            {
                int n = Math.Clamp(request.ThemesPerLeg, 2, 12);
                var pool = _videoThemeService.EnumerateThemeNamesForZoom(reverse ? startZoom : targetZoom);
                if (pool != null && pool.Count >= 2)
                {
                    var sched = new List<(double T, string Theme)>(n - 1);
                    int last = -1;
                    for (int k = 1; k < n; k++)
                    {
                        int idx;
                        do { idx = _videoRng.Next(pool.Count); } while (pool.Count > 1 && idx == last);
                        sched.Add((k / (double)n, pool[idx]));
                        last = idx;
                    }
                    _videoLegThemeSchedule = sched;
                }
            }

            Task.Run(() =>
            {
                // Recorders before the loop so frame 0 is captured. Failure
                // disables that recorder but lets the zoom proceed.
                if (wantMp4) TryStartVideoRecording();
                if (wantPng)
                {
                    _videoLosslessEncode = pngEncode;
                    TryStartLosslessRecording();
                }
                VideoLoop(startCX, startCY, startZoom, targetCX, targetCY, targetZoom, seconds, cts.Token, reverse);
            }, cts.Token)
                .ContinueWith(t => FinishSingleShot(t), TaskScheduler.Default);
        }

        /// <inheritdoc/>
        public void StartSlideshow(VideoZoomRequest request)
        {
            if (_disposed || request == null || IsRunning) return;
            if (_videoThemeService == null)
            {
                RaiseStatus("Video slideshow unavailable — theme service not attached.");
                return;
            }

            ApplySmoothing(request);
            double seconds = request.SlideshowSecondsOverride ?? VideoSlideshowSeconds;
            bool constantRate = request.IsConstantRate;
            bool reverse = request.IsReverse;

            _videoSlideshowRunning = true;
            string mode = reverse ? "reverse " : "";
            RaiseStatus(constantRate
                ? $"Video {mode}slideshow running (constant rate, min {seconds:F1}s)…"
                : $"Video {mode}slideshow running ({seconds:F1}s per leg)…");

            CancellationTokenSource cts;
            lock (_videoSlideshowLock)
            {
                _videoSlideshowCts?.Cancel();
                _videoSlideshowCts = new CancellationTokenSource();
                cts = _videoSlideshowCts;
            }

            Task.Run(() => VideoSlideshowLoop(seconds, constantRate, reverse, request.UseRegionWatermark, cts.Token), cts.Token)
                .ContinueWith(t =>
                {
                    _videoSlideshowRunning = false;
                    _videoTargetIterations = 0;
                    if (t.IsFaulted)
                        RaiseStatus($"Video slideshow error: {t.Exception?.InnerException?.Message}");
                    else
                        RaiseStatus("Video slideshow stopped.");
                    Stopped?.Invoke(this, EventArgs.Empty);
                }, TaskScheduler.Default);
        }

        /// <inheritdoc/>
        public void Stop()
        {
            lock (_videoLock) _videoCts?.Cancel();
            lock (_videoSlideshowLock)
            {
                _videoSlideshowCts?.Cancel();
                _videoSlideshowLegCts?.Cancel();
            }
        }

        /// <inheritdoc/>
        public void SkipLeg()
        {
            lock (_videoSlideshowLock) _videoSlideshowLegCts?.Cancel();
            RaiseStatus("Video slideshow: skipping to next leg…");
        }

        // ──────────────────────────────────────────────────────────────────
        // Single-shot completion
        // ──────────────────────────────────────────────────────────────────

        private void FinishSingleShot(Task t)
        {
            _videoRunning = false;
            _videoTargetIterations = 0;

            // Finalise both encoders first so the temp artefacts are fully
            // written by the time the shell decides whether to keep them.
            var (writer, tempPath) = TakeVideoRecordingState();
            try { writer?.Dispose(); } catch { }
            var (pngWriter, pngFolder) = TakeLosslessRecordingState();
            try { pngWriter?.Dispose(); } catch { }
            var encode = _videoLosslessEncode;
            _videoLosslessEncode = VideoLosslessEncode.None;

            bool cancelled = t.IsCanceled || t.IsFaulted;
            if (t.IsCanceled) RaiseStatus("Video zoom cancelled.");
            else if (t.IsFaulted) RaiseStatus($"Video zoom error: {t.Exception?.InnerException?.Message}");
            else RaiseStatus($"Video zoom complete. zoom={_calculator.Zoom:G6}");

            // Raise the recording result only when a recorder was active so the
            // shell can prompt (or, on cancel/fault, discard the temp files).
            if (tempPath != null || pngFolder != null)
            {
                RecordingFinished?.Invoke(this, new VideoRecordingResult
                {
                    Mp4TempPath = tempPath,
                    PngFolder = pngFolder,
                    Encode = encode,
                    Cancelled = cancelled,
                });
            }

            Stopped?.Invoke(this, EventArgs.Empty);
        }

        // ──────────────────────────────────────────────────────────────────
        // Smoothing config
        // ──────────────────────────────────────────────────────────────────

        private void ApplySmoothing(VideoZoomRequest r)
        {
            int taaPct = Math.Clamp(r.TaaSmoothing, 0, 100);
            _videoTaaAlpha = (taaPct / 100.0) * VideoTaaAlphaCeiling;

            _videoBandDitherEnabled = r.BandDither;
            int ditherPct = Math.Clamp(r.BandDitherStrength, 0, 100);
            // 0..100 % → 0..1 iteration units (100 % blurs ~one iteration band).
            _videoBandDitherStrength = ditherPct / 100.0;
        }

        // ──────────────────────────────────────────────────────────────────
        // Recorder lifecycle
        // ──────────────────────────────────────────────────────────────────

        private void TryStartVideoRecording()
        {
            int w = _calculator.Width, h = _calculator.Height;
            if (w < 16 || h < 16) return;
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"fracturingfog_{Guid.NewGuid():N}.mp4");
                _videoMp4Writer = new Mp4Writer(tempPath, w, h);
                _videoMp4TempPath = tempPath;
                _videoMp4Sw = Stopwatch.StartNew();
            }
            catch (Exception ex)
            {
                RaiseStatus($"Video recording disabled — encoder init failed: {ex.Message}");
                ClearVideoRecordingState(deleteTempFile: true);
            }
        }

        private void TryStartLosslessRecording()
        {
            int w = _calculator.Width, h = _calculator.Height;
            if (w < 16 || h < 16) return;
            try
            {
                string folder = Path.Combine(Path.GetTempPath(), $"fracturingfog_pngseq_{Guid.NewGuid():N}");
                _videoPngWriter = new PngSequenceWriter(folder, w, h);
                _videoPngFolder = folder;
            }
            catch (Exception ex)
            {
                RaiseStatus($"Lossless recording disabled — init failed: {ex.Message}");
                ClearLosslessRecordingState(deleteFolder: true);
            }
        }

        private (Mp4Writer? Writer, string? TempPath) TakeVideoRecordingState()
        {
            var w = _videoMp4Writer;
            var p = _videoMp4TempPath;
            _videoMp4Writer = null;
            _videoMp4TempPath = null;
            _videoMp4Sw = null;
            return (w, p);
        }

        private void ClearVideoRecordingState(bool deleteTempFile)
        {
            var (w, p) = TakeVideoRecordingState();
            try { w?.Dispose(); } catch { }
            if (deleteTempFile && p != null && File.Exists(p))
                try { File.Delete(p); } catch { }
        }

        private (PngSequenceWriter? Writer, string? Folder) TakeLosslessRecordingState()
        {
            var w = _videoPngWriter;
            var f = _videoPngFolder;
            _videoPngWriter = null;
            _videoPngFolder = null;
            return (w, f);
        }

        private void ClearLosslessRecordingState(bool deleteFolder)
        {
            var (w, f) = TakeLosslessRecordingState();
            try { w?.Dispose(); } catch { }
            if (deleteFolder && f != null && Directory.Exists(f))
                try { Directory.Delete(f, recursive: true); } catch { }
        }

        // Feeds the post-FX buffer (what was just uploaded) to any active
        // recorders. A write failure disables that recorder but does not
        // interrupt the zoom or affect the other recorder.
        private void CaptureVideoFrame()
        {
            var buf = _lastUploadedBuffer;
            if (buf == null) return;

            var mp4 = _videoMp4Writer;
            var sw = _videoMp4Sw;
            if (mp4 != null && sw != null)
            {
                try { mp4.WriteFrame(buf, sw.Elapsed.Ticks); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[FractalRenderHost] Mp4 frame write failed: buf={buf.Length} " +
                        $"src={_calculator.Width}x{_calculator.Height} " +
                        $"upload={_lastUploadedWidth}x{_lastUploadedHeight} :: {ex}");
                    ClearVideoRecordingState(deleteTempFile: true);
                    RaiseStatus("MP4 recording disabled (frame encode error).");
                }
            }

            var png = _videoPngWriter;
            if (png != null)
            {
                try { png.WriteFrame(buf); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PNG frame write failed: {ex.Message}");
                    ClearLosslessRecordingState(deleteFolder: true);
                    RaiseStatus("Lossless recording disabled (PNG write error).");
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Animation loop (background thread, synchronous)
        // ──────────────────────────────────────────────────────────────────

        private void VideoLoop(
            QDCoord cx0, QDCoord cy0, double z0,
            QDCoord cx1, QDCoord cy1, double z1,
            double seconds, CancellationToken ct, bool reverse)
        {
            // Cancel any in-flight ordinary calculation so it can't race our
            // per-frame Calculate calls.
            lock (_calcLock) _calcCts?.Cancel();

            BeginVideoLeg();

            // Reset the per-leg theme-fade tracker (caller seeds
            // _videoLegThemeSchedule before invoking VideoLoop).
            _videoLegThemeIdx = 0;
            _videoLegSw = Stopwatch.StartNew();

            // If a fade survived the previous leg (shouldn't, but guard) commit
            // it to its target so the new leg starts on a plain palette.
            if (_videoActiveFade != null && _videoActiveFadeTo != null)
                ColorMap = _videoActiveFadeTo;
            _videoActiveFade = null;
            _videoActiveFadeTo = null;

            double logZ0 = Math.Log(Math.Max(z0, 1e-12));
            double logZ1 = Math.Log(Math.Max(z1, 1e-12));

            bool centerMoves = !QDEqual(cx0, cx1) || !QDEqual(cy0, cy1);
            bool zoomChanges = z0 != z1;

            double panSecs = centerMoves && zoomChanges ? seconds * VideoPanFraction
                            : centerMoves ? seconds
                            : 0.0;
            double zoomSecs = seconds - panSecs;

            if (reverse)
            {
                // Zoom out first (at cx0/cy0), then pan at the shallow end.
                if (zoomSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swZoom = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swZoom.Elapsed.TotalSeconds / zoomSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);
                        double zoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);
                        TryRunScheduledThemeFade(seconds, ct);
                        if (ct.IsCancellationRequested) break;
                        RenderVideoFrame(cx0, cy0, zoom, ct);
                        if (last) break;
                    }
                }
                if (panSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swPan = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swPan.Elapsed.TotalSeconds / panSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);
                        QDCoord cx = QDLerp(cx0, cx1, te);
                        QDCoord cy = QDLerp(cy0, cy1, te);
                        TryRunScheduledThemeFade(seconds, ct);
                        if (ct.IsCancellationRequested) break;
                        RenderVideoFrame(cx, cy, z1, ct);
                        if (last) break;
                    }
                }
            }
            else
            {
                var sw = Stopwatch.StartNew();
                // Phase 1: pan to target CX/CY at the current zoom.
                if (panSecs > 0.0)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        double t = sw.Elapsed.TotalSeconds / panSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);
                        QDCoord cx = QDLerp(cx0, cx1, te);
                        QDCoord cy = QDLerp(cy0, cy1, te);
                        TryRunScheduledThemeFade(seconds, ct);
                        if (ct.IsCancellationRequested) break;
                        RenderVideoFrame(cx, cy, z0, ct);
                        if (last) break;
                    }
                }
                // Phase 2: zoom in at target CX/CY (full QD precision).
                if (zoomSecs > 0.0 && !ct.IsCancellationRequested)
                {
                    var swZoom = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested)
                    {
                        double t = swZoom.Elapsed.TotalSeconds / zoomSecs;
                        bool last = t >= 1.0;
                        if (last) t = 1.0;
                        double te = t * t * (3.0 - 2.0 * t);
                        double zoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);
                        TryRunScheduledThemeFade(seconds, ct);
                        if (ct.IsCancellationRequested) break;
                        RenderVideoFrame(cx1, cy1, zoom, ct);
                        if (last) break;
                    }
                }
            }
        }

        // Per-frame hook called before each RenderVideoFrame inside VideoLoop.
        // Walks past every elapsed schedule entry and starts a new fade for
        // the latest, then advances any in-flight fade's blend factor. Zoom
        // continues to advance normally — the fade rides along on the per-
        // pixel palette lookup instead of pausing the view.
        private void TryRunScheduledThemeFade(double legSeconds, CancellationToken ct)
        {
            AdvanceActiveFade();

            if (_videoLegThemeSchedule == null || _videoThemeService == null) return;
            if (_videoLegSw == null || legSeconds <= 0.0) return;

            double t = _videoLegSw.Elapsed.TotalSeconds / legSeconds;
            while (_videoLegThemeIdx < _videoLegThemeSchedule.Count
                && _videoLegThemeSchedule[_videoLegThemeIdx].T <= t)
            {
                var entry = _videoLegThemeSchedule[_videoLegThemeIdx++];
                if (ct.IsCancellationRequested) return;
                BeginVideoThemeFade(entry.Theme);
            }
        }

        // Capture the current host.ColorMap as "from", swap to the new theme's
        // map (which the host setter assigns), capture as "to", then install
        // a BlendedColorMap so subsequent Calculate() calls produce a per-pixel
        // lerp. T starts at 0; AdvanceActiveFade ticks it toward 1.
        private void BeginVideoThemeFade(string newTheme)
        {
            if (_videoThemeService == null) return;

            // Mid-fade arrival: commit prior fade to its target so we never
            // stack three palettes (the BlendedColorMap holds two).
            if (_videoActiveFade != null && _videoActiveFadeTo != null)
            {
                ColorMap = _videoActiveFadeTo;
                _videoActiveFade = null;
                _videoActiveFadeTo = null;
            }

            var fromMap = ColorMap;
            if (!_videoThemeService.ApplyThemeSilent(newTheme)) return;
            var toMap = ColorMap;
            if (fromMap == null || toMap == null || ReferenceEquals(fromMap, toMap)) return;

            var blended = new BlendedColorMap(fromMap, toMap, 0f);
            ColorMap = blended;
            _videoActiveFade = blended;
            _videoActiveFadeTo = toMap;
            _videoActiveFadeStartSecs = _videoLegSw?.Elapsed.TotalSeconds ?? 0.0;

            // The leg-locked CDF was built against the from-palette's recolour
            // output. Invalidate so the next eq pass rebuilds against the
            // blended palette — keeps histogram mapping stable through the
            // transition.
            _videoLegCdfStale = true;
        }

        private void AdvanceActiveFade()
        {
            if (_videoActiveFade == null || _videoLegSw == null) return;
            double elapsed = _videoLegSw.Elapsed.TotalSeconds - _videoActiveFadeStartSecs;
            float t = (float)(elapsed / VideoThemeFadeSeconds);
            if (t >= 1f)
            {
                if (_videoActiveFadeTo != null) ColorMap = _videoActiveFadeTo;
                _videoActiveFade = null;
                _videoActiveFadeTo = null;
                return;
            }
            _videoActiveFade.T = t;
        }

        private void RenderVideoFrame(QDCoord cx, QDCoord cy, double zoom, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            ApplyVideoFrameState(cx, cy, zoom);
            if (ct.IsCancellationRequested) return;

            // Non-Mandelbrot fractals: dispatch to the alt calculator and upload
            // its buffer directly. Histogram-eq / TAA / dither read from the
            // Mandelbrot ColorBuffer and have no equivalent here, so they skip.
            IFractalCalculator? alt = SelectAltCalculator(ViewState.FractalType);
            if (alt != null)
            {
                SyncAltCalculatorForVideoFrame(alt);
                alt.Calculate(ct);
                if (ct.IsCancellationRequested) return;
                UploadProcessedBuffer(alt.ColorBuffer, alt.Width, alt.Height);
                CaptureVideoFrame();
                return;
            }

            _calculator.Calculate(ct);
            if (ct.IsCancellationRequested) return;

            // Adaptive contrast via the leg-locked CDF so the histogram mapping
            // is identical across all frames of the leg (else the palette
            // assignment for the same complex point flickers as the view zooms).
            // Band-edge dither folds into the same recolor sweep.
            double ditherIter = _videoBandDitherEnabled ? _videoBandDitherStrength : 0.0;
            int eq = ViewState.HistogramEq;
            if (eq > 0)
            {
                double strength = eq / 100.0;
                EnsureVideoLegCdf();
                if (_videoLegCdf != null)
                {
                    _calculator.ApplyHistogramEqualizationWithCdf(
                        _videoLegCdf, _videoLegCdfBins, _videoLegCdfMaxIter,
                        strength, ditherIter,
                        out long escapedCount, out long saturatedCount);
                    if (escapedCount > 0
                        && saturatedCount > escapedCount * VideoCdfSaturationRebuildFraction)
                    {
                        _videoLegCdfStale = true;
                    }
                }
                else
                {
                    _calculator.ApplyHistogramEqualization(strength);
                    if (ditherIter > 0.0) _calculator.ApplyBandDitherRecolor(ditherIter);
                }
            }
            else if (ditherIter > 0.0)
            {
                _calculator.ApplyBandDitherRecolor(ditherIter);
            }

            // Temporal reprojection blend (skipped on frame 0 of a leg).
            BlendWithPrevFrameInPlace();

            UploadProcessedBuffer(_calculator.ColorBuffer, _calculator.Width, _calculator.Height);
            CaptureVideoFrame();

            // Capture this frame for the next iteration's reprojection.
            StashCurrentFrameAsPrev();
        }

        // Builds the per-leg CDF on the first frame; refreshes after a >5%
        // MaxIterations shift (tier auto-promote) or a saturation stale flag.
        private void EnsureVideoLegCdf()
        {
            int curMax = _calculator.MaxIterations;
            bool needRebuild = _videoLegCdf == null
                || _videoLegCdfMaxIter <= 0
                || _videoLegCdfStale
                || Math.Abs(curMax - _videoLegCdfMaxIter) > _videoLegCdfMaxIter * 0.05;
            if (!needRebuild) return;

            if (_calculator.BuildHistogramCdf(out double[]? cdf, out int bins, out int srcMax))
            {
                _videoLegCdf = cdf;
                _videoLegCdfBins = bins;
                _videoLegCdfMaxIter = srcMax;
                _videoLegCdfStale = false;
            }
            else
            {
                _videoLegCdf = null;
                _videoLegCdfBins = 0;
                _videoLegCdfMaxIter = 0;
                _videoLegCdfStale = false;
            }
        }

        // Smoothstep falloff from 1.0 at zoom ≤ fadeStart to 0.0 at zoom ≥
        // fadeEnd, interpolated in log10(zoom). Attenuates the TAA alpha so
        // deep-zoom frames don't accumulate bilinear low-pass blur.
        private double ComputeTaaZoomFalloff(double zoom)
        {
            double fadeStart = _videoTaaDeepZoomFadeStart;
            double fadeEnd = _videoTaaDeepZoomFadeEnd;
            if (fadeEnd <= fadeStart) return zoom < fadeStart ? 1.0 : 0.0;
            if (zoom <= fadeStart) return 1.0;
            if (zoom >= fadeEnd) return 0.0;
            double logStart = Math.Log10(fadeStart);
            double logEnd = Math.Log10(fadeEnd);
            double t = (Math.Log10(zoom) - logStart) / (logEnd - logStart);
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double s = 1.0 - t;
            return s * s * (3.0 - 2.0 * s);
        }

        private void BeginVideoLeg()
        {
            _videoLegCdf = null;
            _videoLegCdfBins = 0;
            _videoLegCdfMaxIter = 0;
            _videoLegCdfStale = false;
            _videoPrevColorBuffer = null;
            _videoPrevHasFrame = false;
            _videoPrevWidth = 0;
            _videoPrevHeight = 0;
        }

        // For each pixel of the current ColorBuffer, derive its complex-plane
        // coordinate from the current view, transform it into the previous
        // frame's pixel space, bilinearly sample the prev frame, clamp to the
        // local 3×3 neighborhood of the fresh frame, and alpha-blend back in.
        private void BlendWithPrevFrameInPlace()
        {
            if (!_videoPrevHasFrame) return;
            uint[]? prev = _videoPrevColorBuffer;
            if (prev == null) return;

            int w = _calculator.Width;
            int h = _calculator.Height;
            if (w <= 0 || h <= 0) return;
            if (w != _videoPrevWidth || h != _videoPrevHeight) return;
            var cur = _calculator.ColorBuffer;
            int total = w * h;
            if (cur.Length != total || prev.Length != total) return;

            if (_videoBlendScratch == null || _videoBlendScratch.Length != total)
                _videoBlendScratch = new uint[total];
            Array.Copy(cur, _videoBlendScratch, total);
            var src = _videoBlendScratch;

            double curScale = (3.5 / Math.Max(w, h)) / _calculator.Zoom;
            double curCx = _calculator.CenterX + _calculator.CenterXLo
                         + _calculator.CenterX2 + _calculator.CenterX3;
            double curCy = _calculator.CenterY + _calculator.CenterYLo
                         + _calculator.CenterY2 + _calculator.CenterY3;
            double halfW = (w - 1) * 0.5;
            double halfH = (h - 1) * 0.5;

            double prevScale = (3.5 / Math.Max(_videoPrevWidth, _videoPrevHeight)) / _videoPrevZoom;
            if (prevScale <= 0.0) return;
            double invPrevScale = 1.0 / prevScale;
            double prevCx = _videoPrevCenterX + _videoPrevCenterXLo
                          + _videoPrevCenterX2 + _videoPrevCenterX3;
            double prevCy = _videoPrevCenterY + _videoPrevCenterYLo
                          + _videoPrevCenterY2 + _videoPrevCenterY3;
            double prevHalfW = (_videoPrevWidth - 1) * 0.5;
            double prevHalfH = (_videoPrevHeight - 1) * 0.5;

            double alpha = _videoTaaAlpha * ComputeTaaZoomFalloff(_calculator.Zoom);
            if (alpha <= 0.0) return;
            double oneMinus = 1.0 - alpha;
            int prevW = _videoPrevWidth;
            int prevLastX = prevW - 1;
            int prevLastY = _videoPrevHeight - 1;

            Parallel.For(0, h, y =>
            {
                double cIm = curCy + (y - halfH) * curScale;
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    double cRe = curCx + (x - halfW) * curScale;
                    double px = (cRe - prevCx) * invPrevScale + prevHalfW;
                    double py = (cIm - prevCy) * invPrevScale + prevHalfH;
                    if (px < 0.0 || py < 0.0 || px > prevLastX || py > prevLastY) continue;

                    int x0 = (int)px;
                    int y0 = (int)py;
                    int x1 = x0 + 1; if (x1 > prevLastX) x1 = prevLastX;
                    int y1 = y0 + 1; if (y1 > prevLastY) y1 = prevLastY;
                    double fx = px - x0;
                    double fy = py - y0;

                    uint c00 = prev[y0 * prevW + x0];
                    uint c10 = prev[y0 * prevW + x1];
                    uint c01 = prev[y1 * prevW + x0];
                    uint c11 = prev[y1 * prevW + x1];

                    double w00 = (1.0 - fx) * (1.0 - fy);
                    double w10 = fx * (1.0 - fy);
                    double w01 = (1.0 - fx) * fy;
                    double w11 = fx * fy;

                    double pb = (c00 & 0xFFu) * w00 + (c10 & 0xFFu) * w10
                              + (c01 & 0xFFu) * w01 + (c11 & 0xFFu) * w11;
                    double pg = ((c00 >> 8) & 0xFFu) * w00 + ((c10 >> 8) & 0xFFu) * w10
                              + ((c01 >> 8) & 0xFFu) * w01 + ((c11 >> 8) & 0xFFu) * w11;
                    double pr = ((c00 >> 16) & 0xFFu) * w00 + ((c10 >> 16) & 0xFFu) * w10
                              + ((c01 >> 16) & 0xFFu) * w01 + ((c11 >> 16) & 0xFFu) * w11;
                    double pa = ((c00 >> 24) & 0xFFu) * w00 + ((c10 >> 24) & 0xFFu) * w10
                              + ((c01 >> 24) & 0xFFu) * w01 + ((c11 >> 24) & 0xFFu) * w11;

                    int idx = rowBase + x;
                    uint c = src[idx];
                    double cb = c & 0xFFu;
                    double cg = (c >> 8) & 0xFFu;
                    double cr = (c >> 16) & 0xFFu;
                    double ca = (c >> 24) & 0xFFu;

                    // 3×3 neighborhood clamp of the fresh frame.
                    int xm = x > 0 ? x - 1 : 0;
                    int xp = x < w - 1 ? x + 1 : w - 1;
                    int ym = y > 0 ? y - 1 : 0;
                    int yp = y < h - 1 ? y + 1 : h - 1;
                    int rowM = ym * w;
                    int rowP = yp * w;
                    uint s00 = src[rowM + xm], s10 = src[rowM + x], s20 = src[rowM + xp];
                    uint s01 = src[rowBase + xm], s21 = src[rowBase + xp];
                    uint s02 = src[rowP + xm], s12 = src[rowP + x], s22 = src[rowP + xp];

                    uint nbMinB = c & 0xFFu, nbMaxB = nbMinB;
                    uint nbMinG = (c >> 8) & 0xFFu, nbMaxG = nbMinG;
                    uint nbMinR = (c >> 16) & 0xFFu, nbMaxR = nbMinR;
                    uint nbMinA = (c >> 24) & 0xFFu, nbMaxA = nbMinA;

                    uint nbV, nbB, nbG, nbR, nbA;
                    nbV = s00;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s10;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s20;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s01;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s21;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s02;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s12;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;
                    nbV = s22;
                    nbB = nbV & 0xFFu; nbG = (nbV >> 8) & 0xFFu; nbR = (nbV >> 16) & 0xFFu; nbA = (nbV >> 24) & 0xFFu;
                    if (nbB < nbMinB) nbMinB = nbB; else if (nbB > nbMaxB) nbMaxB = nbB;
                    if (nbG < nbMinG) nbMinG = nbG; else if (nbG > nbMaxG) nbMaxG = nbG;
                    if (nbR < nbMinR) nbMinR = nbR; else if (nbR > nbMaxR) nbMaxR = nbR;
                    if (nbA < nbMinA) nbMinA = nbA; else if (nbA > nbMaxA) nbMaxA = nbA;

                    if (pb < nbMinB) pb = nbMinB; else if (pb > nbMaxB) pb = nbMaxB;
                    if (pg < nbMinG) pg = nbMinG; else if (pg > nbMaxG) pg = nbMaxG;
                    if (pr < nbMinR) pr = nbMinR; else if (pr > nbMaxR) pr = nbMaxR;
                    if (pa < nbMinA) pa = nbMinA; else if (pa > nbMaxA) pa = nbMaxA;

                    uint ob = (uint)(cb * oneMinus + pb * alpha + 0.5);
                    uint og = (uint)(cg * oneMinus + pg * alpha + 0.5);
                    uint or = (uint)(cr * oneMinus + pr * alpha + 0.5);
                    uint oa = (uint)(ca * oneMinus + pa * alpha + 0.5);
                    if (ob > 255) ob = 255;
                    if (og > 255) og = 255;
                    if (or > 255) or = 255;
                    if (oa > 255) oa = 255;
                    cur[idx] = (oa << 24) | (or << 16) | (og << 8) | ob;
                }
            });
        }

        private void StashCurrentFrameAsPrev()
        {
            int w = _calculator.Width;
            int h = _calculator.Height;
            int n = w * h;
            if (n <= 0) return;

            if (_videoPrevColorBuffer == null || _videoPrevColorBuffer.Length != n)
                _videoPrevColorBuffer = new uint[n];
            Array.Copy(_calculator.ColorBuffer, _videoPrevColorBuffer, n);

            _videoPrevWidth = w;
            _videoPrevHeight = h;
            _videoPrevCenterX = _calculator.CenterX;
            _videoPrevCenterXLo = _calculator.CenterXLo;
            _videoPrevCenterX2 = _calculator.CenterX2;
            _videoPrevCenterX3 = _calculator.CenterX3;
            _videoPrevCenterY = _calculator.CenterY;
            _videoPrevCenterYLo = _calculator.CenterYLo;
            _videoPrevCenterY2 = _calculator.CenterY2;
            _videoPrevCenterY3 = _calculator.CenterY3;
            _videoPrevZoom = _calculator.Zoom;
            _videoPrevHasFrame = true;
        }

        // Per-limb lerp — sum(lerp(a_i,b_i,t)) == lerp(sum a, sum b, t).
        private static QDCoord QDLerp(QDCoord a, QDCoord b, double t) => new(
            a.Hi + (b.Hi - a.Hi) * t,
            a.Lo + (b.Lo - a.Lo) * t,
            a.X2 + (b.X2 - a.X2) * t,
            a.X3 + (b.X3 - a.X3) * t);

        private static bool QDEqual(QDCoord a, QDCoord b)
            => a.Hi == b.Hi && a.Lo == b.Lo && a.X2 == b.X2 && a.X3 == b.X3;

        // Pushes view state for one video frame into ViewState + the calculator
        // and auto-promotes the quality preset (upward only) as the zoom crosses
        // a tier boundary. All four limbs flow through so deep targets land on
        // the correct pixel.
        private void ApplyVideoFrameState(QDCoord cx, QDCoord cy, double zoom)
        {
            QualityPreset target = _videoQuality;
            double cap = QualityPreset.Ultra.ZoomMax;
            if (zoom > cap) zoom = cap;

            if (zoom > _videoQuality.ZoomMax)
            {
                foreach (var p in QualityPreset.All)
                {
                    if (p.Tier == QualityTier.Extreme) continue;
                    if (p.ZoomMax >= zoom) { target = p; break; }
                }
                if (target.Tier != _videoQuality.Tier) _videoQuality = target;
            }

            double clampedZoom = Math.Clamp(zoom, _videoQuality.ZoomMin, _videoQuality.ZoomMax);

            var s = ViewState;
            s.CenterX = cx.Hi; s.CenterXLo = cx.Lo; s.CenterX2 = cx.X2; s.CenterX3 = cx.X3;
            s.CenterY = cy.Hi; s.CenterYLo = cy.Lo; s.CenterY2 = cy.X2; s.CenterY3 = cy.X3;
            s.Zoom = clampedZoom;
            s.Quality = _videoQuality;

            _calculator.CenterX = cx.Hi; _calculator.CenterXLo = cx.Lo; _calculator.CenterX2 = cx.X2; _calculator.CenterX3 = cx.X3;
            _calculator.CenterY = cy.Hi; _calculator.CenterYLo = cy.Lo; _calculator.CenterY2 = cy.X2; _calculator.CenterY3 = cy.X3;
            _calculator.Zoom = clampedZoom;
            _calculator.Quality = _videoQuality;

            if (s.IterLocked)
            {
                _calculator.MaxIterations = s.LockedIterations;
            }
            else
            {
                int it = _videoQuality.ComputeIterations(clampedZoom);
                if (_videoTargetIterations > it) it = _videoTargetIterations;
                _calculator.MaxIterations = it;
            }
        }

        // Pushes alt-calculator view state from _calculator + applies the
        // type-specific parameters. Mirrors the alt block in Trigger().
        private void SyncAltCalculatorForVideoFrame(IFractalCalculator alt)
        {
            alt.CenterX = _calculator.CenterX;
            alt.CenterY = _calculator.CenterY;
            alt.Zoom = _calculator.Zoom;
            alt.MaxIterations = _calculator.MaxIterations;
            alt.Quality = _calculator.Quality;
            alt.ColorMap = _calculator.ColorMap;
            switch (alt)
            {
                case EscapeTimeCalculator e: e.FractalType = ViewState.FractalType; e.FractalParameters = ViewState.FractalParameters; break;
                case IFSCalculator ifs: ifs.FractalParameters = ViewState.FractalParameters; break;
                case LSystemCalculator ls: ls.FractalParameters = ViewState.FractalParameters; break;
                case AttractorCalculator a: a.FractalParameters = ViewState.FractalParameters; break;
                case BuddhaFamilyCalculator b: b.FractalParameters = ViewState.FractalParameters; break;
                case NewtonCalculator n: n.FractalParameters = ViewState.FractalParameters; break;
                case UserEquationCalculator u: u.FractalParameters = ViewState.FractalParameters; break;
                case MandelbulbCalculator m: m.FractalParameters = ViewState.FractalParameters; break;
                case SandboxCalculator sb: sb.FractalParameters = ViewState.FractalParameters; break;
                case UserBulbCalculator ub: ub.FractalParameters = ViewState.FractalParameters; break;
                case FracturingFog.Calculators.Generated.MandelbrotZ2Calculator gz2:
                    gz2.CenterXLo = _calculator.CenterXLo;
                    gz2.CenterX2  = _calculator.CenterX2;
                    gz2.CenterX3  = _calculator.CenterX3;
                    gz2.CenterYLo = _calculator.CenterYLo;
                    gz2.CenterY2  = _calculator.CenterY2;
                    gz2.CenterY3  = _calculator.CenterY3;
                    gz2.UsePerturbation = true;
                    gz2.UseBla          = true;
                    break;
                case FracturingFog.Calculators.Generated.MandelbrotZ3Calculator gz3:
                    gz3.CenterXLo = _calculator.CenterXLo;
                    gz3.CenterX2  = _calculator.CenterX2;
                    gz3.CenterX3  = _calculator.CenterX3;
                    gz3.CenterYLo = _calculator.CenterYLo;
                    gz3.CenterY2  = _calculator.CenterY2;
                    gz3.CenterY3  = _calculator.CenterY3;
                    gz3.UsePerturbation = true;
                    gz3.UseBla          = true;
                    break;
                case FracturingFog.Calculators.Generated.MandelbrotZ4Calculator gz4:
                    gz4.CenterXLo = _calculator.CenterXLo;
                    gz4.CenterX2  = _calculator.CenterX2;
                    gz4.CenterX3  = _calculator.CenterX3;
                    gz4.CenterYLo = _calculator.CenterYLo;
                    gz4.CenterY2  = _calculator.CenterY2;
                    gz4.CenterY3  = _calculator.CenterY3;
                    gz4.UsePerturbation = true;
                    gz4.UseBla          = true;
                    break;
                case FracturingFog.Calculators.Generated.MandelbrotZ5Calculator gz5:
                    gz5.CenterXLo = _calculator.CenterXLo;
                    gz5.CenterX2  = _calculator.CenterX2;
                    gz5.CenterX3  = _calculator.CenterX3;
                    gz5.CenterYLo = _calculator.CenterYLo;
                    gz5.CenterY2  = _calculator.CenterY2;
                    gz5.CenterY3  = _calculator.CenterY3;
                    gz5.UsePerturbation = true;
                    gz5.UseBla          = true;
                    break;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Video slideshow (Mandelbrot-only legs, cross-faded)
        // ──────────────────────────────────────────────────────────────────

        private void VideoSlideshowLoop(double seconds, bool constantRate, bool reverse, bool useRegionWatermark, CancellationToken ct)
        {
            var svc = _videoThemeService;
            if (svc == null) return;

            // Mandelbrot-only pool: FractalRegion carries no per-engine
            // parameters, so non-Mandelbrot regions (Julia constant, Newton
            // root, equation source) can't be faithfully reconstructed for an
            // unattended zoom. Excluding Extreme tier + near-classic zoom too.
            const double SlideshowMinRegionZoom = 5.0;
            var regions = new List<FractalRegion>();
            foreach (var r in FractalRegionLibrary.Instance.AllSlideshowRegions)
                if (r.FractalType == FractalType.Mandelbrot
                    && r.QualityPreset.Tier != QualityTier.Extreme
                    && r.Zoom > SlideshowMinRegionZoom)
                    regions.Add(r);

            var themes = svc.EnumerateThemeNames();
            if (regions.Count == 0 || themes == null || themes.Count == 0) return;

            int lastRegion = -1, lastTheme = -1;
            double ultraMax = QualityPreset.Ultra.ZoomMax;
            double draftMin = QualityPreset.Draft.ZoomMin;
            double defZoom = FractalViewState.DefaultZoom;
            double logStart = Math.Log(defZoom);

            double minLogRange = double.MaxValue;
            if (constantRate)
            {
                foreach (var r in regions)
                {
                    double rtz = Math.Clamp(r.Zoom, draftMin, ultraMax);
                    double range = Math.Log(rtz) - logStart;
                    if (range > 0 && range < minLogRange) minLogRange = range;
                }
                double minFloor = Math.Log(SlideshowMinRegionZoom / defZoom);
                if (minLogRange < minFloor) minLogRange = minFloor;
                if (minLogRange == double.MaxValue || minLogRange <= 0)
                    constantRate = false;
            }

            while (!ct.IsCancellationRequested)
            {
                CancellationTokenSource legCts;
                lock (_videoSlideshowLock)
                {
                    _videoSlideshowLegCts?.Dispose();
                    _videoSlideshowLegCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    legCts = _videoSlideshowLegCts;
                }
                var legCt = legCts.Token;

                int ri;
                do { ri = _videoRng.Next(regions.Count); }
                while (regions.Count > 1 && ri == lastRegion);
                lastRegion = ri;
                var region = regions[ri];

                // When the user asked the video slideshow to honour each
                // region's embedded watermark, swap it in before the leg's
                // pre-render so the very first composited frame already
                // carries the right branding.
                if (useRegionWatermark)
                    ActiveWatermark = region.EmbeddedWatermark;

                double tz = Math.Clamp(region.Zoom, draftMin, ultraMax);

                // Per-leg palette pool capped at the leg's deep endpoint.
                var legThemes = svc.EnumerateThemeNamesForZoom(tz);
                if (legThemes == null || legThemes.Count == 0) legThemes = themes;
                if (legThemes.Count != themes.Count) lastTheme = -1;
                int ti;
                do { ti = _videoRng.Next(legThemes.Count); }
                while (legThemes.Count > 1 && ti == lastTheme);
                lastTheme = ti;
                string theme = legThemes[ti];

                double legSeconds = seconds;
                if (constantRate)
                {
                    double logRange = Math.Log(tz) - logStart;
                    if (logRange > 0) legSeconds = seconds * (logRange / minLogRange);
                    if (legSeconds < seconds) legSeconds = seconds;
                }

                // Snapshot the on-screen frame to cross-fade into the new leg.
                var oldLegBuf = SnapshotFrame(out int snapW, out int snapH);

                // Set up the leg's starting view, force Mandelbrot, apply theme
                // silently (no present — we cross-fade explicitly).
                ViewState.FractalType = FractalType.Mandelbrot;
                _videoTargetIterations = region.Iterations;

                if (reverse)
                {
                    ViewState.CenterX = region.CenterX; ViewState.CenterXLo = region.CenterXLo;
                    ViewState.CenterX2 = region.CenterX2; ViewState.CenterX3 = region.CenterX3;
                    ViewState.CenterY = region.CenterY; ViewState.CenterYLo = region.CenterYLo;
                    ViewState.CenterY2 = region.CenterY2; ViewState.CenterY3 = region.CenterY3;
                    ViewState.Zoom = tz;

                    _videoQuality = QualityPreset.Standard;
                    foreach (var p in QualityPreset.All)
                    {
                        if (p.Tier == QualityTier.Extreme) continue;
                        if (p.ZoomMax >= tz) { _videoQuality = p; break; }
                    }
                }
                else
                {
                    ViewState.CenterX = FractalViewState.DefaultCenterX; ViewState.CenterXLo = 0.0; ViewState.CenterX2 = 0.0; ViewState.CenterX3 = 0.0;
                    ViewState.CenterY = FractalViewState.DefaultCenterY; ViewState.CenterYLo = 0.0; ViewState.CenterY2 = 0.0; ViewState.CenterY3 = 0.0;
                    ViewState.Zoom = defZoom;
                    _videoQuality = QualityPreset.Standard;
                }
                ViewState.Quality = _videoQuality;

                // Push the start state into the calculator for the pre-render.
                _calculator.CenterX = ViewState.CenterX;
                _calculator.CenterXLo = ViewState.CenterXLo;
                _calculator.CenterX2 = ViewState.CenterX2;
                _calculator.CenterX3 = ViewState.CenterX3;
                _calculator.CenterY = ViewState.CenterY;
                _calculator.CenterYLo = ViewState.CenterYLo;
                _calculator.CenterY2 = ViewState.CenterY2;
                _calculator.CenterY3 = ViewState.CenterY3;
                _calculator.Zoom = ViewState.Zoom;
                _calculator.Quality = _videoQuality;
                if (ViewState.IterLocked)
                    _calculator.MaxIterations = ViewState.LockedIterations;
                else
                {
                    int it = _videoQuality.ComputeIterations(ViewState.Zoom);
                    if (reverse && region.Iterations > it) it = region.Iterations;
                    _calculator.MaxIterations = it;
                }

                svc.ApplyThemeSilent(theme);

                // Build the in-leg theme-fade schedule. Default 3 themes per
                // leg (matches the image slideshow's Region-Focus cadence);
                // schedule swaps at t = 1/3 and 2/3 of the leg so each theme
                // gets roughly equal screen time. Skip when the leg pool is
                // too small to pick distinct themes.
                _videoLegThemeSchedule = null;
                _videoLegThemeIdx = 0;
                const int themesPerLeg = 3;
                if (legThemes.Count >= 2)
                {
                    var schedule = new List<(double T, string Theme)>(themesPerLeg - 1);
                    int prev = ti;
                    for (int k = 1; k < themesPerLeg; k++)
                    {
                        int next;
                        do { next = _videoRng.Next(legThemes.Count); }
                        while (legThemes.Count > 1 && next == prev);
                        schedule.Add((k / (double)themesPerLeg, legThemes[next]));
                        prev = next;
                    }
                    _videoLegThemeSchedule = schedule;
                }

                RaiseStatus($"Video {(reverse ? "reverse " : "")}slideshow: {region.Name}  •  {theme}  ({legSeconds:F1}s)");

                if (ct.IsCancellationRequested) break;
                if (legCt.IsCancellationRequested) continue;

                // Pre-render the leg's starting frame (Mandelbrot path: eq +
                // dither applied so the fade target matches the live look).
                int eqSnapshot = ViewState.HistogramEq;
                double ditherStr = _videoBandDitherEnabled ? _videoBandDitherStrength : 0.0;
                uint[] newLegBuf;
                try
                {
                    _calculator.Calculate(legCt);
                    if (eqSnapshot > 0) _calculator.ApplyHistogramEqualization(eqSnapshot / 100.0);
                    if (ditherStr > 0.0) _calculator.ApplyBandDitherRecolor(ditherStr);
                    var cb = _calculator.ColorBuffer;
                    newLegBuf = new uint[cb.Length];
                    Array.Copy(cb, newLegBuf, cb.Length);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) break;
                    continue;
                }

                if (ct.IsCancellationRequested) break;
                if (legCt.IsCancellationRequested) continue;

                // Cross-fade prev-leg final frame → new-leg start frame.
                if (oldLegBuf.Length == newLegBuf.Length && oldLegBuf.Length > 0
                    && snapW == _calculator.Width && snapH == _calculator.Height)
                {
                    const int legFadeSteps = 24;
                    const int legFadeStepMs = 80; // ~1.9 s
                    VideoCrossFade(oldLegBuf, newLegBuf, legFadeSteps, legFadeStepMs, legCt);
                }
                else
                {
                    PresentBuffer(newLegBuf, _calculator.Width, _calculator.Height);
                }

                if (ct.IsCancellationRequested) break;
                if (legCt.IsCancellationRequested) continue;

                QDCoord legStartCX, legStartCY, legTargetCX, legTargetCY;
                double legStartZoom, legTargetZoom;
                if (reverse)
                {
                    legStartCX = new QDCoord(region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    legStartCY = new QDCoord(region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);
                    legStartZoom = tz;
                    legTargetCX = new QDCoord(FractalViewState.DefaultCenterX, 0.0, 0.0, 0.0);
                    legTargetCY = new QDCoord(FractalViewState.DefaultCenterY, 0.0, 0.0, 0.0);
                    legTargetZoom = defZoom;
                }
                else
                {
                    legStartCX = new QDCoord(FractalViewState.DefaultCenterX, 0.0, 0.0, 0.0);
                    legStartCY = new QDCoord(FractalViewState.DefaultCenterY, 0.0, 0.0, 0.0);
                    legStartZoom = defZoom;
                    legTargetCX = new QDCoord(region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
                    legTargetCY = new QDCoord(region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);
                    legTargetZoom = tz;
                }

                using var sweepCts = StartVideoLegSweep(legSeconds, legCt);
                VideoLoop(legStartCX, legStartCY, legStartZoom,
                          legTargetCX, legTargetCY, legTargetZoom,
                          legSeconds, legCt, reverse);
                sweepCts.Cancel();

                if (ct.IsCancellationRequested) break;

                // Pause between legs — interruptible by leg skip or stop.
                if (legCt.WaitHandle.WaitOne(VideoSlideshowPauseMs))
                {
                    if (ct.IsCancellationRequested) break;
                    // leg cancel — fall through to next iteration
                }
            }
        }

        // Per-pixel CPU dissolve from from→to over steps, presenting each.
        private void VideoCrossFade(uint[] from, uint[] to, int steps, int stepMs, CancellationToken ct)
        {
            int w = _calculator.Width, h = _calculator.Height;
            int n = w * h;
            if (n <= 0 || from.Length < n || to.Length < n)
            {
                PresentBuffer(to, w, h);
                return;
            }

            var blend = new uint[n];
            for (int s = 1; s <= steps; s++)
            {
                if (ct.IsCancellationRequested) return;
                if (s == steps)
                {
                    PresentBuffer(to, w, h);
                    return;
                }

                float a = s / (float)steps;
                float ia = 1f - a;
                for (int i = 0; i < n; i++)
                {
                    uint o = from[i], nw = to[i];
                    byte r = (byte)(((o >> 16) & 0xFF) * ia + ((nw >> 16) & 0xFF) * a);
                    byte g = (byte)(((o >> 8) & 0xFF) * ia + ((nw >> 8) & 0xFF) * a);
                    byte b = (byte)((o & 0xFF) * ia + (nw & 0xFF) * a);
                    blend[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                }

                PresentBuffer(blend, w, h);
                if (ct.WaitHandle.WaitOne(stepMs)) return;
            }
        }

        private void RaiseStatus(string message) => StatusChanged?.Invoke(this, message);

        // ── Adaptive sweep (video) ────────────────────────────────────────
        // Returns a CTS scoped to the current leg; caller cancels it when the
        // leg ends so the ramp task exits promptly even when the leg was cut
        // short by a skip. Sink is invoked from a background thread; consumers
        // marshal to the UI thread themselves.
        private CancellationTokenSource StartVideoLegSweep(double legSeconds, CancellationToken parentCt)
        {
            var legCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            var cfg = VideoSweepConfig;
            var sink = VideoAdaptiveValueSink;
            if (cfg == null || !cfg.Enabled || sink == null || legSeconds <= 0.0)
                return legCts;

            int start = Math.Clamp(cfg.Start, 0, 100);
            int end = Math.Clamp(cfg.End, 0, 100);
            var mode = cfg.Mode;
            bool loop = cfg.Loop;
            int legMs = (int)Math.Max(50.0, legSeconds * 1000.0);
            var ct = legCts.Token;

            Task.Run(async () =>
            {
                const int tickMs = 50;
                int elapsed = 0;
                while (!ct.IsCancellationRequested)
                {
                    double phase = legMs > 0 ? Math.Clamp(elapsed / (double)legMs, 0.0, 1.0) : 1.0;
                    int v = ComputeVideoSweepValue(phase, start, end, mode);
                    try { sink(v); } catch { }

                    if (elapsed >= legMs)
                    {
                        if (!loop) return;
                        elapsed = 0;
                    }
                    try { await Task.Delay(tickMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    elapsed += tickMs;
                }
            }, ct);

            return legCts;
        }

        private static int ComputeVideoSweepValue(double phase, int start, int end, AdaptiveSweepMode mode)
        {
            switch (mode)
            {
                case AdaptiveSweepMode.Reverse: return LerpAdaptive(end, start, phase);
                case AdaptiveSweepMode.PingPong:
                    double pp = phase < 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0;
                    return LerpAdaptive(start, end, pp);
                case AdaptiveSweepMode.Forward:
                default: return LerpAdaptive(start, end, phase);
            }
        }

        private static int LerpAdaptive(int a, int b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return (int)Math.Round(a + (b - a) * t);
        }
    }
}
