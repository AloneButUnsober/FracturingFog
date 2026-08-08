// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/FractalRenderHost.cs
//
// Concrete IFractalRenderHost — owns the IFractalRenderer + every
// per-fractal-type calculator and orchestrates the trigger / calculate /
// upload pipeline that MainForm currently does inline.
//
// Step C of the Phase 2.3 cut plan. MainForm continues to operate on its
// own private renderer + calculator instances during the transition so
// the legacy WinForms shell keeps building green; the Avalonia shell
// constructs its own FractalRenderHost via this class.
//
// Cross-platform note: brightness/contrast post-FX is straight pixel-loop
// CPU work and stays here. Grid and watermark overlays use System.Drawing
// today and are NOT ported in this pass — the Avalonia shell will redraw
// them via Avalonia.Media when step F lands. Histogram-equalization stays
// here because it lives on MandelbrotCalculator itself.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.ViewState;

namespace FracturingFog.Rendering
{
    /// <inheritdoc/>
    /// <remarks>The Video Zoom engine (IVideoZoomController) lives in the
    /// FractalRenderHost.Video.cs partial — it needs direct access to the
    /// calculator fleet, the upload pipeline and the recolor internals.</remarks>
    public sealed partial class FractalRenderHost : IFractalRenderHost
    {
        private readonly IFractalRenderer _renderer;

        // Per-fractal-type calculators. MandelbrotCalculator is the canonical
        // primary; everything else is "alt" and selected by FractalType.
        private MandelbrotCalculator _calculator;

        // Wave 2.5 — progressive rendering ¼ → ½ → full chain. Dedicated
        // sidecar MandelbrotCalculator instances permanently sized to the
        // matching downsample of the active surface. Used only when a
        // Trigger(progressive: true) fires and only on the canonical
        // Mandelbrot path (useAlt always falls through to a single full
        // render). Resize() keeps them in step with the main calc; never
        // disposed because MandelbrotCalculator owns no native handles.
        // Memory cost at 1080p: ~5 MB (quarter) + ~20 MB (half) of pinned
        // LOH on top of the main calc's ~80 MB.
        private MandelbrotCalculator _previewCalcQuarter;
        private MandelbrotCalculator _previewCalcHalf;
        private EscapeTimeCalculator _escapeCalculator;
        private IFSCalculator _ifsCalculator;
        private LSystemCalculator _lsystemCalculator;
        private AttractorCalculator _attractorCalculator;
        private BuddhabrotCalculator _buddhabrotCalculator;
        private LogisticCalculator _logisticCalculator;
        private HalleyCalculator _halleyCalculator;
        private SecantCalculator _secantCalculator;
        private NebulabrotCalculator _nebulabrotCalculator;
        private AntiBuddhabrotCalculator _antiBuddhabrotCalculator;
        private AntiNebulabrotCalculator _antiNebulabrotCalculator;
        private NewtonCalculator _newtonCalculator;
        private UserEquationCalculator _userEquationCalculator;
        private MandelbulbCalculator _mandelbulbCalculator;
        private MandelboxCalculator _mandelboxCalculator;
        private KifsCalculator _kifsCalculator;
        private QuatJuliaCalculator _quatJuliaCalculator;
        private QuatMandelbrotCalculator _quatMandelbrotCalculator;
        private PlasmaCalculator _plasmaCalculator;
        private AcidWarpCalculator _acidWarpCalculator;
        private ApollonianCalculator _apollonianCalculator;
        private KleinianCalculator _kleinianCalculator;
        private BicomplexMandelbrotCalculator _bicomplexCalculator;
        private DlaCalculator _dlaCalculator;
        private FlameRenderer _flameCalculator;
        private SandboxCalculator _sandboxCalculator;
        private UserBulbCalculator _userBulbCalculator;
        private TearDropCalculator _tearDropCalculator;
        private FracturingFog.Calculators.Generated.MandelbrotZ2Calculator _generatedZ2Calculator;
        private FracturingFog.Calculators.Generated.MandelbrotZ3Calculator _generatedZ3Calculator;
        private FracturingFog.Calculators.Generated.MandelbrotZ4Calculator _generatedZ4Calculator;
        private FracturingFog.Calculators.Generated.MandelbrotZ5Calculator _generatedZ5Calculator;
        private FracturingFog.Calculators.Generated.TricornCalculator     _generatedTricornCalculator;
        private FracturingFog.Calculators.Generated.BurningShipCalculator _generatedBurningShipCalculator;
        // Dynamically loaded calculator from the UserEquation "Compile &
        // Load" path. Null when no hot-loaded calc is active; non-null
        // takes priority over the FractalType-dispatched alt calculators.
        private IFractalCalculator? _dynamicAltCalculator;

        private CancellationTokenSource? _calcCts;
        private readonly object _calcLock = new();

        // #85 — drain latch. Signaled (idle) whenever the calc thread is NOT
        // inside a job's write region; reset (busy) for the duration of
        // RunFrameJobCalc. Resize cancels the in-flight calc's token then
        // waits on this before swapping the calculator's buffer arrays, so an
        // in-flight Calculate can never index a freshly-swapped smaller array
        // out of range. Only the single dedicated calc thread touches it.
        private readonly ManualResetEventSlim _calcIdle = new(initialState: true);

        // Serialises every call into the D3D11 ImmediateContext. The
        // immediate context is NOT thread-safe; before this lock landed,
        // resize on the UI thread could overlap with UpdateTexture from a
        // calc-continuation thread and the upcoming auto-present, locking
        // the driver. Every _renderer.* call inside this class — and the
        // public Present() entry point — must take this lock.
        private readonly object _d3dGate = new();

        // CPU compositor for grid + watermark. Reused across frames. Only
        // touched from the calculator continuation, which serialises with
        // every other consumer behind _d3dGate.
        // S-X7.5 (2026-06-23) — overlay compositor is SkiaSharp cross-plat
        // (FractalOverlayCompositor.cs Phase X.A / Slice A.4 port). Stale
        // IsWindows guard from the GDI+ era dropped so Grid + Watermark +
        // Perf HUD render on Linux too.
        private readonly FractalOverlayCompositor _overlay = new FractalOverlayCompositor();

        // Cached previous frame — re-uploaded on the next trigger so the
        // user sees the stale (correct) image while the next one calculates,
        // instead of black flashes at High/Ultra quality.
        //
        // S-X9d note was wrong: progressive ¼ / ½ uploads go straight through
        // _renderer.UpdateTexture without touching _lastUploadedBuffer, so it
        // always holds the last FULL-RES finished frame (pre-pan content
        // while/after a pan). _lastUploadedBuffer keeps those semantics —
        // SnapshotFrame, SaveLastFrameToPng, RepaintWithSelectionBox all
        // depend on it being the last full-res content.
        private uint[]? _lastUploadedBuffer;
        private int _lastUploadedWidth;
        private int _lastUploadedHeight;
        // Same content as _lastUploadedBuffer for full-res frames, retained
        // here for RepaintWithSelectionBox (needs the most recent pre-overlay
        // full-res frame, not a panned-but-blurry preview).
        private uint[]? _lastFullResBuffer;
        private int _lastFullResWidth;
        private int _lastFullResHeight;
        // S-X9g (2026-06-27) — last buffer that was actually presented to the
        // screen, at WHATEVER res. Updated by both the full-res upload path
        // and the progressive ¼/½ preview upload path. The stale-frame
        // re-upload that paints "something" while the next calc runs picks
        // this so a pan-stop debounce doesn't paint pre-pan content over the
        // panned preview the user just saw (= snap-back). UpdateTexture
        // handles arbitrary dims; the fullscreen quad sampler stretches a
        // ¼-res preview to back-buffer size so the position is right even
        // if the resolution is blurry until the full-res calc lands.
        private uint[]? _lastPresentedBuffer;
        private int _lastPresentedWidth;
        private int _lastPresentedHeight;
        // #86 — newest-wins present ordering. Progressive stages (¼→½→full) and
        // TAA samples queue their uploads onto the threadpool, where _uploadGate
        // serialises them but does NOT preserve submission order. A fast frame
        // (deep zoom on the GPU finishes the final stage in a blink) can present
        // before a lagging preview upload from the same trigger, so the stale
        // preview then paints over the correct final — the deep-region "stale
        // image, but Save is correct" bug. Fix: every FrameJob gets a monotonic
        // Seq at construction (construction order == present priority); a present
        // is dropped when a newer Seq already reached the screen.
        private long _uploadSeq;
        private long _lastPresentedUploadSeq = -1;
        // Pinned scratch for the progressive ¼/½ preview snapshot above.
        // Grown lazily — typical sizes are 480x270 (¼) and 960x540 (½) at
        // 1080p, so ~0.5 MB / 2 MB respectively. Pinned (POH) so the GPU
        // upload path doesn't need a per-frame GCHandle.Alloc.
        private uint[]? _uploadPreviewPool;
        // Tracks the renderer's CURRENT back-buffer size (last value passed to
        // Resize). Survives _lastUploadedBuffer being nulled by Resize, so the
        // slideshow cold-start path can build a black source buffer at the right
        // dimensions before any frame has been uploaded.
        private int _currentTargetWidth;
        private int _currentTargetHeight;
        // Pre-overlay snapshot. Mirrors _lastUploadedBuffer but is captured
        // before grid+watermark composite, so file-save paths can paint a
        // fresh watermark (via ImageExport.AddWaterMark) without double-
        // compositing onto a buffer that already has one baked in.
        private uint[]? _lastPreOverlayBuffer;

        // True when the most recent buffer to reach the screen came from an
        // external PresentBuffer (slideshow cross-fade blend, video-slideshow
        // leg) rather than a real render through UploadProcessedBuffer. Those
        // presents update _lastUploadedBuffer but leave _lastPreOverlayBuffer
        // pointing at the previous real render — so the live ASCII source must
        // read _lastUploadedBuffer directly to mirror the blend, or Terminal
        // Mode would sample the stale committed frame and show no cross-fade.
        private volatile bool _lastUploadExternal;

        // Right-drag rubber-band rectangle (pixel space). Set by the shell via
        // SetSelectionBox while the user box-zooms; cleared on release. Drawn
        // on top of grid + watermark by FractalOverlayCompositor.
        private (int X, int Y, int W, int H)? _selectionBox;

        // Histogram CDF cache for the adaptive slider / sweep. Building the
        // CDF is a serial pass over the full SmoothBuffer + an allocation of
        // int[bins]/double[bins]; doing it on every slider tick is what made
        // the adaptive sweep stutter at deep zoom, where calc is still
        // hogging cores. The CDF only depends on the escape-time buffers, so
        // we build it once after each Calculate completes and reuse it for
        // every slider tick that follows. Invalidated by Trigger / Resize.
        private double[]? _cachedAdaptiveCdf;
        private int _cachedAdaptiveBins;
        private int _cachedAdaptiveSourceMaxIter;
        private bool _adaptiveCdfValid;
        private readonly object _adaptiveCdfLock = new();

        // Pooled BGRA scratch buffers for UploadProcessedBuffer. Re-allocated
        // only when the frame size changes. Without this, every adaptive-
        // slider / sweep tick burned two `new uint[w*h]` calls (~16 MB at
        // 1080p) plus a full Array.Copy for the pre-overlay snapshot —
        // 320 MB/s of GC pressure at 20 ticks/sec, which was the visible
        // sweep stutter at larger window sizes.
        //
        // _uploadGate serialises every writer of these pools (calc-completion
        // continuation, RepaintWithAdaptive, RepaintWithPostFx) so they can't
        // produce torn frames on the shared buffers, and so that the stale-
        // frame re-upload in Trigger sees a coherent _lastUploadedBuffer.
        private uint[]? _uploadDstPool;
        private uint[]? _uploadPrePool;
        private readonly object _uploadGate = new();

        // Set true by the video record path while an MP4 / PNG sequence is
        // being captured. During recording the pre-overlay snapshot
        // (consumed only by interactive SaveLastFrameToPng) is suppressed —
        // an 8 MB Array.Copy at 1080p per uploaded frame, ~33 MB at 4K.
        // SaveLastFrameToPng is a user-action path and does not race the
        // record loop.
        private volatile bool _recordingActive;

        private bool _disposed;

        // T2.4: dedicated calc thread + latest-only queue. Replaces the
        // per-Trigger Task.Run + ContinueWith pair (4+ allocations per frame)
        // with a single long-lived background Thread that owns the
        // stale-upload + Calculate pass. Bounded capacity 1 with a drain on
        // each enqueue gives "latest only" semantics: bursts of Triggers
        // collapse to the most recent job before the calc thread ever sees
        // them. The calc-completion (CDF build + UploadProcessedBuffer +
        // FrameCompleted) is dispatched onto the threadpool so the calc
        // thread turns around immediately for the next job.
        private readonly BlockingCollection<FrameJob> _calcQueue =
            new BlockingCollection<FrameJob>(boundedCapacity: 1);
        private Thread? _calcThread;

        // Phase 18b — host animation clock.
        // Wakes at ~30 FPS to advance LightingFxData.SceneTime and Trigger
        // when any of (LightOrbitSpeed, CausticsAnimSpeed, VolumeNoiseSpeed)
        // is non-zero. All three at defaults (== 0) → tick skips Trigger so
        // renders stay bit-identical.
        private System.Threading.Timer? _animTimer;
        private long _animStartTicks;

        // #96 follow-up — color-theme "settle" debounce. Live editor edits take
        // the cheap Mandelbrot recolor path in ApplyColorMap (fast, but skips
        // MSAA / TAA / SSAO / histogram-eq, so band edges show un-anti-aliased
        // speckle). Each cheap recolor (re)arms this timer; when edits stop for
        // ColorSettleDelayMs the callback fires a full Trigger() so the final
        // frame carries the same quality passes a pan/zoom would — without the
        // user having to navigate to "settle" the image.
        private System.Threading.Timer? _colorSettleTimer;
        private const int ColorSettleDelayMs = 300;
        // Re-entry guard: a frame can outrun the tick period at high res, so
        // skip enqueueing another Trigger while one is still in flight.
        private int _animTickBusy;
        // Phase 18b fix — frame-in-flight gate. Set when the tick fires
        // Trigger(), cleared when AnimationFrameUploaded marks completion.
        // Without this gate, a slow 3D scene whose Calculate() exceeds 33 ms
        // gets cancelled by every subsequent tick — calc never finishes,
        // status bar stays "Calculating…" forever.
        private int _animFrameInFlight;

        // Wave 2.7 — TAA accumulator state.
        //
        // _taaSumR/G/B/A hold per-pixel channel sums across all samples
        // (sample 0 = original ColorBuffer + any MSAA jitter, sample N>0 =
        // additional Halton-jittered Calculate runs). _taaSampleCount is the
        // number of samples folded in; the per-frame display value is
        // sum/count rounded to byte. _taaFp* captures the view fingerprint
        // (centre / zoom / iter cap / fractal type) we accumulated against —
        // any change invalidates the accumulator. Touched only from the
        // calc thread + the upload threadpool callback (which never overlap
        // on a single FrameJob, so no lock is needed beyond visibility).
        private long[]? _taaSumR;
        private long[]? _taaSumG;
        private long[]? _taaSumB;
        private long[]? _taaSumA;
        private int _taaSumPixels;
        private int _taaSampleCount;
        private double _taaFpCx, _taaFpCy, _taaFpZoom;
        private int _taaFpIter;
        private int _taaFpW, _taaFpH;
        private FractalType _taaFpType;
        private bool _taaValid;

        private readonly struct FrameJob
        {
            public readonly CancellationToken Token;
            public readonly MandelbrotCalculator Calc;
            public readonly IFractalCalculator? AltCalc;
            public readonly Stopwatch Sw;
            public readonly uint[]? StaleBuf;
            public readonly int StaleW;
            public readonly int StaleH;
            public readonly int CalcW;
            public readonly int CalcH;
            // Wave 2.7 — TAA continuation marker. 0 = normal first frame
            // (stale upload + Calculate + optional MSAA). >0 = jittered TAA
            // sample N (skip stale-upload + MSAA; blend into the TAA
            // accumulator and present the running average).
            public readonly int TaaSampleIndex;
            // Wave 2.5 — progressive downsample factor. 0 / 1 = final
            // full-resolution stage (existing path). 2 = half. 4 = quarter.
            // When non-final the calc runs on a sidecar preview calc; the
            // upload tail schedules the next stage (4 → 2 → 0).
            public readonly int ProgressiveStage;
            // #86 — monotonic present-priority id. Assigned at construction, so
            // a job built later (a newer trigger, or a later progressive/TAA
            // stage of the same trigger) always outranks an earlier one.
            public readonly long Seq;

            public FrameJob(CancellationToken token, MandelbrotCalculator calc,
                IFractalCalculator? altCalc, Stopwatch sw,
                uint[]? staleBuf, int staleW, int staleH, int calcW, int calcH,
                long seq,
                int taaSampleIndex = 0, int progressiveStage = 0)
            {
                Token = token; Calc = calc; AltCalc = altCalc; Sw = sw;
                StaleBuf = staleBuf; StaleW = staleW; StaleH = staleH;
                CalcW = calcW; CalcH = calcH;
                Seq = seq;
                TaaSampleIndex = taaSampleIndex;
                ProgressiveStage = progressiveStage;
            }
        }

        private readonly struct UploadCtx
        {
            public readonly FractalRenderHost Host;
            public readonly FrameJob Job;
            public readonly long Ms;
            public UploadCtx(FractalRenderHost host, FrameJob job, long ms)
            { Host = host; Job = job; Ms = ms; }
        }

        private static readonly Action<UploadCtx> s_uploadCallback =
            static ctx => ctx.Host.RunFrameJobUpload(ctx.Job, ctx.Ms);

        // #86 — newest-wins present gate. MUST be called while holding
        // _uploadGate. Returns false when a frame with a higher Seq has already
        // presented, meaning this upload is stale and must be dropped so it
        // cannot paint over the newer frame. Equal Seq passes (the same job's
        // stale-hold upload and its final upload share a Seq and should both
        // present, in execution order).
        private bool TryClaimPresent(long seq)
        {
            if (seq < _lastPresentedUploadSeq) return false;
            _lastPresentedUploadSeq = seq;
            return true;
        }

        // #86 diagnostic — opt-in via FF_GPU_PERTURB_DEBUG=1. Traces the present
        // path (trigger → stale-hold → progressive preview → final) so a single
        // A/B run on a live D3D box reveals whether the deep GPU final frame is
        // computed, queued, presented, or dropped by the newest-wins gate.
        //
        // Writes to a FILE, not Console.Error: the Windows shell (WinExe) has no
        // attached console, so stderr is swallowed there. Path is reported once
        // at startup (also to the debugger's Debug output, which IS visible).
        private static readonly bool s_dbg86 =
            Environment.GetEnvironmentVariable("FF_GPU_PERTURB_DEBUG") is "1" or "true" or "yes" or "on";
        private static readonly string s_dbg86Path =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff_gpu_perturb_86.log");
        private static readonly object s_dbg86Gate = new();
        private static bool s_dbg86Announced;
        private static void Dbg86(string msg)
        {
            if (!s_dbg86) return;
            try
            {
                lock (s_dbg86Gate)
                {
                    if (!s_dbg86Announced)
                    {
                        s_dbg86Announced = true;
                        string banner = $"[#86] log opened {DateTime.Now:HH:mm:ss} -> {s_dbg86Path}";
                        Console.Error.WriteLine(banner);
                        System.Diagnostics.Debug.WriteLine(banner);
                        System.IO.File.AppendAllText(s_dbg86Path,
                            $"==== #86 trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
                    }
                    System.IO.File.AppendAllText(s_dbg86Path,
                        $"{DateTime.Now:HH:mm:ss.fff} [#86] {msg}{Environment.NewLine}");
                }
            }
            catch { /* diagnostic must never break the render path */ }
        }

        public FractalRenderHost(IFractalRenderer renderer, FractalViewState state, int width, int height, IColorMap initialColorMap)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            ViewState = state ?? throw new ArgumentNullException(nameof(state));
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);

            // Wave 0.6: route per-stage post-FX timings into _perfStats. Idempotent
            // — multiple FractalRenderHost instances would overwrite the publisher
            // but the host is owned by a single shell, so a second host means the
            // first one has already been disposed.
            FracturingFog.Rendering.Lighting.StagePerf.Publisher = _perfStats.RecordStage;

            _calculator = new MandelbrotCalculator(w, h);
            // Wave 2.5 — progressive sidecars at ¼ and ½ resolution. Min
            // 64×64 to keep BLA / SA prelude math well-behaved at very small
            // window sizes.
            int qw = Math.Max(64, w / 4); int qh = Math.Max(64, h / 4);
            int hw = Math.Max(64, w / 2); int hh = Math.Max(64, h / 2);
            _previewCalcQuarter = new MandelbrotCalculator(qw, qh);
            _previewCalcHalf    = new MandelbrotCalculator(hw, hh);
            _escapeCalculator = new EscapeTimeCalculator(w, h);
            _ifsCalculator = new IFSCalculator(w, h);
            _lsystemCalculator = new LSystemCalculator(w, h);
            _attractorCalculator = new AttractorCalculator(w, h);
            _buddhabrotCalculator = new BuddhabrotCalculator(w, h);
            _logisticCalculator = new LogisticCalculator(w, h);
            _halleyCalculator = new HalleyCalculator(w, h);
            _secantCalculator = new SecantCalculator(w, h);
            _nebulabrotCalculator = new NebulabrotCalculator(w, h);
            _antiBuddhabrotCalculator = new AntiBuddhabrotCalculator(w, h);
            _antiNebulabrotCalculator = new AntiNebulabrotCalculator(w, h);
            _newtonCalculator = new NewtonCalculator(w, h);
            _userEquationCalculator = new UserEquationCalculator(w, h);
            _mandelbulbCalculator = new MandelbulbCalculator(w, h);
            _mandelboxCalculator = new MandelboxCalculator(w, h);
            _kifsCalculator = new KifsCalculator(w, h);
            _quatJuliaCalculator = new QuatJuliaCalculator(w, h);
            _quatMandelbrotCalculator = new QuatMandelbrotCalculator(w, h);
            _plasmaCalculator = new PlasmaCalculator(w, h);
            _acidWarpCalculator = new AcidWarpCalculator(w, h);
            _apollonianCalculator = new ApollonianCalculator(w, h);
            _kleinianCalculator = new KleinianCalculator(w, h);
            _bicomplexCalculator = new BicomplexMandelbrotCalculator(w, h);
            _dlaCalculator = new DlaCalculator(w, h);
            _flameCalculator = new FlameRenderer(w, h);
            _sandboxCalculator = new SandboxCalculator(w, h);
            _userBulbCalculator = new UserBulbCalculator(w, h);
            _tearDropCalculator = new TearDropCalculator(w, h);
            _generatedZ2Calculator = new FracturingFog.Calculators.Generated.MandelbrotZ2Calculator(w, h);
            _generatedZ3Calculator = new FracturingFog.Calculators.Generated.MandelbrotZ3Calculator(w, h);
            _generatedZ4Calculator = new FracturingFog.Calculators.Generated.MandelbrotZ4Calculator(w, h);
            _generatedZ5Calculator = new FracturingFog.Calculators.Generated.MandelbrotZ5Calculator(w, h);
            _generatedTricornCalculator     = new FracturingFog.Calculators.Generated.TricornCalculator(w, h);
            _generatedBurningShipCalculator = new FracturingFog.Calculators.Generated.BurningShipCalculator(w, h);

            if (initialColorMap != null)
            {
                _calculator.ColorMap = initialColorMap;
                _escapeCalculator.ColorMap = initialColorMap;
                _ifsCalculator.ColorMap = initialColorMap;
                _lsystemCalculator.ColorMap = initialColorMap;
                _attractorCalculator.ColorMap = initialColorMap;
                _buddhabrotCalculator.ColorMap = initialColorMap;
                _logisticCalculator.ColorMap = initialColorMap;
                _halleyCalculator.ColorMap = initialColorMap;
                _secantCalculator.ColorMap = initialColorMap;
                _nebulabrotCalculator.ColorMap = initialColorMap;
                _antiBuddhabrotCalculator.ColorMap = initialColorMap;
                _antiNebulabrotCalculator.ColorMap = initialColorMap;
                _newtonCalculator.ColorMap = initialColorMap;
                _userEquationCalculator.ColorMap = initialColorMap;
                _mandelbulbCalculator.ColorMap = initialColorMap;
                _mandelboxCalculator.ColorMap = initialColorMap;
                _kifsCalculator.ColorMap = initialColorMap;
                _quatJuliaCalculator.ColorMap = initialColorMap;
                _quatMandelbrotCalculator.ColorMap = initialColorMap;
                _plasmaCalculator.ColorMap = initialColorMap;
                _apollonianCalculator.ColorMap = initialColorMap;
                _kleinianCalculator.ColorMap = initialColorMap;
                _bicomplexCalculator.ColorMap = initialColorMap;
                _dlaCalculator.ColorMap = initialColorMap;
                _flameCalculator.ColorMap = initialColorMap;
                _sandboxCalculator.ColorMap = initialColorMap;
                _userBulbCalculator.ColorMap = initialColorMap;
                _tearDropCalculator.ColorMap = initialColorMap;
                _generatedZ2Calculator.ColorMap = initialColorMap;
                _generatedZ3Calculator.ColorMap = initialColorMap;
                _generatedZ4Calculator.ColorMap = initialColorMap;
                _generatedZ5Calculator.ColorMap = initialColorMap;
                _generatedTricornCalculator.ColorMap     = initialColorMap;
                _generatedBurningShipCalculator.ColorMap = initialColorMap;
            }

            _calcThread = new Thread(CalcThreadLoop)
            {
                IsBackground = true,
                Name = "FractalCalc",
            };
            _calcThread.Start();

            // Phase 18b — start the 30 FPS animation tick. Period and dueTime
            // both 33 ms. The tick is a no-op until the user dials up one of
            // the animation speeds, so the wake cost is a handful of CPU µs
            // per frame on an idle scene.
            _animTimer = new System.Threading.Timer(
                AnimationTick, state: null, dueTime: 33, period: 33);

            // #96 follow-up — color-settle debounce timer, created disabled
            // (Infinite due). ApplyColorMap's cheap recolor path arms it; the
            // callback fires one full-quality Trigger() once edits go quiet.
            _colorSettleTimer = new System.Threading.Timer(
                ColorSettleTick, state: null,
                dueTime: System.Threading.Timeout.Infinite,
                period: System.Threading.Timeout.Infinite);

            // Phase 18b fix — clear the frame-in-flight gate when each
            // upload completes (success or cancellation, both fire this
            // event). Frees the next animation tick to enqueue another
            // frame instead of being silently skipped.
            AnimationFrameUploaded += (_, _) =>
                System.Threading.Interlocked.Exchange(ref _animFrameInFlight, 0);
        }

        public FractalViewState ViewState { get; }

        public event EventHandler<RenderFrameInfo>? FrameCompleted;
        public event EventHandler? FrameBufferChanged;
        public event EventHandler? AnimationFrameUploaded;
        public event EventHandler<string>? StatusRequested;
        public event EventHandler? ColorMapChanged;
        public event EventHandler? RenderCancelled;

        // ── Overlay state (CPU-composited into the BGRA buffer) ──────────
        //
        // On Windows the GpuSurfaceControl is a NativeControlHost wrapping a
        // real HWND; the OS composites that HWND above every Avalonia control
        // regardless of XAML Z-order, so an Avalonia.Media overlay can't
        // render on top of it. Instead the host blends the grid + watermark
        // into the BGRA pixel buffer on the CPU before the swap-chain upload.

        public bool ShowGrid { get; set; }
        public bool ShowWatermark { get; set; }
        public FracturingFog.Imaging.AsciiWatermarkStyle AsciiWatermarkStyle { get; set; }
            = FracturingFog.Imaging.AsciiWatermarkStyle.Block;
        /// <summary>When true, the post-FX upload composites a perf HUD
        /// (phase timings + HW summary) into the top-left of the frame.
        /// Cheap (~0.1 ms/frame) — safe to leave on during video record.</summary>
        public bool ShowPerfHud { get; set; }

        // Rolling perf collector. Sampled by the calc thread + upload path.
        private readonly PerfStats _perfStats = new();

        /// <summary>Clear the perf HUD's rolling buffers + reset the
        /// GC-rate baseline. Used to start a clean capture window when
        /// switching regions / starting a video so the prior region's
        /// samples do not skew the averages.</summary>
        public void ResetPerfStats() => _perfStats.Reset();
        public string? RegionName { get; set; }
        public string? ThemeName { get; set; }
        public string? ProgramName { get; set; } = "Fracturing Fog";
        public string? ProgramVersion { get; set; }
        public FracturingFog.Models.WatermarkDef? ActiveWatermark { get; set; }

        /// <summary>The renderer this host drives. Exposed so the shell can
        /// call Render() in its idle loop.</summary>
        public IFractalRenderer Renderer => _renderer;

        /// <summary>The primary MandelbrotCalculator — exposed so the shell
        /// can plumb HP-diagnostic toggles (Ctrl+Shift+S / +A) and any other
        /// engine-specific knobs that have not yet been lifted into the
        /// view-state contract.</summary>
        public MandelbrotCalculator Mandelbrot => _calculator;

        public bool MandelbrotDisableAcceleration
        {
            get => _calculator.DisableAcceleration;
            set => _calculator.DisableAcceleration = value;
        }

        public bool MandelbrotDisableSeriesApproximation
        {
            get => _calculator.DisableSeriesApproximation;
            set => _calculator.DisableSeriesApproximation = value;
        }

        public bool MandelbrotDisableDdBla
        {
            get => _calculator.DisableDdBla;
            set => _calculator.DisableDdBla = value;
        }

        // SM-2 — deep-zoom rebasing A/B toggle. AllowPtRebasing is a static
        // switch on the calculator (it gates the glitch-fallback path for all
        // instances), so this passthrough drives the process-wide flag; that is
        // fine for a debug A/B control.
        public bool MandelbrotAllowPtRebasing
        {
            get => MandelbrotCalculator.AllowPtRebasing;
            set => MandelbrotCalculator.AllowPtRebasing = value;
        }

        /// <summary>SM-11b — reuse the reference orbit across progressive pan/zoom
        /// PREVIEW frames instead of recomputing it each move. Default OFF:
        /// `--panjitter` showed the FRESH per-frame render is already
        /// reference-consistent during a drag (inter-frame Δiter ≈ 0-6/px even at
        /// 40px steps), so recycling changes neither the pixels nor perceptibly
        /// the speed — the deep-zoom "jumping" is NOT reference recompute. Kept as
        /// clean plumbing (instance flag on the preview calc) for future recycle
        /// work; flip on only with a measured reason. See Docs/Deep-Zoom-Perturbation.md §6.</summary>
        public bool RecyclePreviewOrbit { get; set; } = false;

        // Render-context overlay lines + optional detail-limit warning, folded
        // into the perf HUD (ShowPerfHud). Kept out of the status bar so a long
        // warning can't wrap and resize the panel. Reads the live calculator
        // state; called on the render thread just before the HUD composites.
        private (System.Collections.Generic.List<string> lines, string? warning)
            BuildRenderContextOverlay()
        {
            var lines = new System.Collections.Generic.List<string>(9);
            double zoom = _calculator.Zoom;
            // Non-zero limb counts for X/Y centre. If these drop below what the
            // zoom needs (rough rule: ~1 limb per 16 zoom-decades, so ~5 limbs at
            // 1e64), the centre has been truncated somewhere and deep-zoom anchors
            // will drift — the single fastest tell for a navigation-precision bug.
            int lx = NonZeroLimbs(_calculator.CenterX, _calculator.CenterXLo, _calculator.CenterX2,
                _calculator.CenterX3, _calculator.CenterX4, _calculator.CenterX5,
                _calculator.CenterX6, _calculator.CenterX7);
            int ly = NonZeroLimbs(_calculator.CenterY, _calculator.CenterYLo, _calculator.CenterY2,
                _calculator.CenterY3, _calculator.CenterY4, _calculator.CenterY5,
                _calculator.CenterY6, _calculator.CenterY7);
            lines.Add($"type   {ViewState.FractalType}");
            lines.Add($"center {_calculator.CenterX:G10}");
            lines.Add($"       {_calculator.CenterY:G10}");
            lines.Add($"limbs  X:{lx}/8  Y:{ly}/8   px {_calculator.Width}x{_calculator.Height}");
            lines.Add($"zoom   {zoom:G4}   iter {_calculator.MaxIterations}");

            double maxUseful = _calculator.MaxUsefulZoomLog10;
            string orbit = _calculator.ReferenceOrbitEscaped
                ? $"ref-orbit escaped @ {_calculator.ReferenceOrbitLength}"
                : $"ref-orbit bounded ({_calculator.ReferenceOrbitLength})";
            lines.Add(orbit);
            lines.Add(double.IsPositiveInfinity(maxUseful)
                ? "max-detail zoom: unbounded (bounded orbit)"
                : $"max-detail zoom: ~1e{maxUseful:F0}");

            // Active diagnostic toggles (only when off/on-non-default).
            var flags = new System.Collections.Generic.List<string>(4);
            if (!MandelbrotCalculator.AllowPtRebasing) flags.Add("REBASE off");
            if (_calculator.DisableAcceleration) flags.Add("ACCEL off");
            if (_calculator.DisableSeriesApproximation) flags.Add("SA off");
            if (_calculator.DisableDdBla) flags.Add("DD-BLA off");
            if (flags.Count > 0) lines.Add("flags  " + string.Join(", ", flags));

            // Detail-limit warning when the live zoom has passed this centre's
            // δ-amplification floor (frame collapses to flat → navigation has no
            // structure to grab). Fires just as detail degrades (estimate − 1).
            string? warning = null;
            if (!double.IsPositiveInfinity(maxUseful) && zoom > 0 &&
                Math.Log10(zoom) > maxUseful - 1.0)
            {
                warning = $"detail limit ~1e{maxUseful:F0} - recenter on structure to zoom deeper";
            }
            return (lines, warning);
        }

        private static int NonZeroLimbs(double a, double b, double c, double d,
                                        double e, double f, double g, double h)
        {
            int n = 0;
            if (a != 0) n++; if (b != 0) n++; if (c != 0) n++; if (d != 0) n++;
            if (e != 0) n++; if (f != 0) n++; if (g != 0) n++; if (h != 0) n++;
            return n;
        }

        // T3.1: GPU compute kernel constructed lazily on the first Use
        // request when a factory is installed. Null on non-D3D11 backends or
        // when the user has never enabled the feature.
        private IGpuKernel? _gpuKernel;

        /// <summary>
        /// Backend-specific kernel factory installed by the host bootstrap
        /// (cross-platform App or legacy WinExe). Engine cannot construct an
        /// IGpuKernel itself because every implementation owns
        /// renderer-specific handles (ID3D11Device for the D3D11 path, an
        /// ILGPU accelerator for the future managed path, a Silk.NET context
        /// for the GL path, etc.). The factory closure receives the active
        /// renderer + the host's D3D-serialisation gate so it can downcast,
        /// pull the native handles, and construct the right backend.
        /// Phase X.0 / Slice 0.1c — broke the direct DirectXRenderer +
        /// MandelbrotGpuKernel dependency that previously pinned this class
        /// to the WinExe.
        /// </summary>
        public Func<IFractalRenderer, object, IGpuKernel?>? GpuKernelFactory { get; set; }

        // #162 (Slice 3d) — GPU relief raymarch kernel, constructed lazily the
        // first time a frame requests the GPU relief path (opt-in
        // FractalParameters.Relief2DGpuRaymarch). Separate from _gpuKernel: the
        // escape-time IGpuKernel and the relief sphere-trace kernel are distinct
        // compute programs. _reliefKernelTried latches a null/failed construct so
        // a missing factory or a non-D3D/Vulkan backend is not retried per frame.
        private FracturingFog.Rendering.Lighting.IReliefRaymarchKernel? _reliefKernel;
        private bool _reliefKernelTried;

        /// <summary>
        /// Backend-specific relief-raymarch kernel factory installed by the host
        /// bootstrap, mirroring <see cref="GpuKernelFactory"/> for the Relief 3D
        /// sphere trace (#162). Receives the active renderer + the host's
        /// D3D-serialisation gate so the D3D installer can downcast + pull native
        /// handles; the Vulkan installer ignores both and builds a self-owned
        /// context. Null on backends without a relief kernel — the CPU raymarch
        /// runs regardless.
        /// </summary>
        public Func<IFractalRenderer, object, FracturingFog.Rendering.Lighting.IReliefRaymarchKernel?>? ReliefKernelFactory { get; set; }

        /// <summary>Lazily construct (once) and return the GPU relief kernel, or
        /// null when no factory is installed or construction fails / returns null.
        /// Called on the calc/upload path only when the opt-in flag is set.</summary>
        private FracturingFog.Rendering.Lighting.IReliefRaymarchKernel? EnsureReliefKernel()
        {
            if (_reliefKernel != null) return _reliefKernel;
            if (_reliefKernelTried) return null;
            _reliefKernelTried = true;
            if (ReliefKernelFactory == null) return null;
            try
            {
                // Share _d3dGate so a D3D relief dispatch serialises with
                // renderer.Render on the non-thread-safe immediate context, exactly
                // as the escape-time kernel does.
                _reliefKernel = ReliefKernelFactory(_renderer, _d3dGate);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[FractalRenderHost] GPU relief kernel init failed: {ex.Message}");
                _reliefKernel = null;
            }
            return _reliefKernel;
        }

        /// <summary>
        /// Backend-specific video-encoder factory installed by the host
        /// bootstrap. Receives the temp file path + source width/height,
        /// returns a streaming IVideoWriter. Returning null disables video
        /// recording with a status banner. Engine cannot construct an
        /// IVideoWriter itself because every implementation owns
        /// platform-specific handles (Media Foundation COM on Windows via
        /// Mp4Writer in Rendering.D3D, an ffmpeg child process on Linux/macOS
        /// via the Phase X.2 FfmpegVideoWriter).
        /// </summary>
        public Func<string, int, int, FracturingFog.Imaging.IVideoWriter?>? VideoWriterFactory { get; set; }

        /// <summary>T3.1: toggle the SP-path GPU compute dispatch on the
        /// active MandelbrotCalculator. First true assignment lazily
        /// constructs the kernel via <see cref="GpuKernelFactory"/>;
        /// subsequent toggles just flip the calc's
        /// <see cref="MandelbrotCalculator.UseGpuCompute"/> flag. Silently
        /// stays false when no factory is installed or when the factory
        /// returns null (non-D3D11 renderer).</summary>
        public bool UseGpuCompute
        {
            get => _calculator.UseGpuCompute;
            set
            {
                if (value && _gpuKernel == null)
                {
                    if (GpuKernelFactory == null) return;
                    try
                    {
                        // Share _d3dGate so kernel.Run serialises with
                        // renderer.Render — the immediate context is not
                        // thread-safe across the calc thread + upload
                        // threadpool calls.
                        _gpuKernel = GpuKernelFactory(_renderer, _d3dGate);
                        if (_gpuKernel == null) return;
                        _calculator.GpuKernel = _gpuKernel;
                        _escapeCalculator.GpuKernel = _gpuKernel;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[FractalRenderHost] GPU compute kernel init failed: {ex.Message}");
                        _gpuKernel = null;
                        _calculator.GpuKernel = null;
                        _escapeCalculator.GpuKernel = null;
                        return;
                    }
                }
                _calculator.UseGpuCompute = value;
                _escapeCalculator.UseGpuCompute = value;
            }
        }

        // ── Source-compiled calculators (UserEquation / Sandbox / UserBulb) ──
        // The Avalonia shell's dedicated editors live in UI.Avalonia and can't
        // see these main-project calculators directly. These thin wrappers let
        // the bootstrap drive Compile() + read the result without exposing the
        // calculator types across the project boundary. Each returns
        // (ok, error): ok mirrors IsCompiled, error is null on success.

        /// <summary>Compile the Roslyn-backed UserEquation source.</summary>
        public (bool ok, string? error) CompileUserEquation(string source)
        {
            _userEquationCalculator.Compile(source);
            // Note: previously cleared _lastUploadedBuffer to "force a fresh
            // recompute on next Trigger" — but Trigger always recomputes; the
            // null only disabled the stale-frame re-upload. ApplyRegion calls
            // CompileX during the slideshow's PickNonBlackTheme probe, and
            // nulling here made SnapshotFrame return an empty buffer in the
            // following ThemeTransitionAsync, so the cross-fade gate failed
            // and every non-Mandelbrot theme change hard-cut.
            return (_userEquationCalculator.IsCompiled,
                string.IsNullOrEmpty(_userEquationCalculator.LastError) ? null : _userEquationCalculator.LastError);
        }

        /// <summary>Compile the restricted Sandbox-DSL source.</summary>
        public (bool ok, string? error) CompileSandbox(string source)
        {
            _sandboxCalculator.Compile(source);
            return (_sandboxCalculator.IsCompiled,
                string.IsNullOrEmpty(_sandboxCalculator.LastError) ? null : _sandboxCalculator.LastError);
        }

        /// <summary>Compile the 3D UserBulb source (per-component / quat step).</summary>
        public (bool ok, string? error) CompileUserBulb(string source)
        {
            _userBulbCalculator.Compile(source);
            return (_userBulbCalculator.IsCompiled,
                string.IsNullOrEmpty(_userBulbCalculator.LastError) ? null : _userBulbCalculator.LastError);
        }

        /// <summary>Closed-form DE pattern detected for the currently-compiled
        /// UserBulb source. Routed to the editor as a "Analytic engaged" badge.</summary>
        public global::FracturingFog.Calculators.AnalyticDEPattern UserBulbAnalyticPattern
            => _userBulbCalculator.AnalyticPattern;

        /// <summary>0-based character index of the most-recent Sandbox parse
        /// error. -1 when no error or error has no position.</summary>
        public int UserBulbLastErrorPosition => _userBulbCalculator.LastErrorPosition;

        /// <summary>Length of the offending substring at
        /// <see cref="UserBulbLastErrorPosition"/>.</summary>
        public int UserBulbLastErrorLength => _userBulbCalculator.LastErrorLength;

        /// <summary>Distance-estimator sampler for UserBulb mesh export.</summary>
        public double SampleUserBulbDE(double x, double y, double z) => _userBulbCalculator.SampleDE(x, y, z);

        /// <summary>#112 — snapshot mesh-export sampler with export-specific
        /// iteration count + Jacobian step (see
        /// <see cref="UserBulbCalculator.MakeExportSampler"/>). Independent of
        /// later param mutation, so the off-thread export is race-free.</summary>
        public Func<double, double, double, double>? MakeUserBulbExportSampler(int iterations, double jacobianH)
            => _userBulbCalculator.MakeExportSampler(iterations, jacobianH);

        /// <summary>UserBulb sampling-space centre (mesh export origin).</summary>
        public double UserBulbCenterX => _userBulbCalculator.CenterX;
        public double UserBulbCenterY => _userBulbCalculator.CenterY;

        /// <summary>Mutable colour map applied across all calculators. Setting
        /// this updates every alt calculator so a theme switch is a single
        /// assignment from the caller's perspective. Raises
        /// <see cref="ColorMapChanged"/> after the propagation so overlay
        /// controls can refresh their contrast colour.</summary>
        public IColorMap ColorMap
        {
            get => _calculator.ColorMap;
            set
            {
                _calculator.ColorMap = value;
                _escapeCalculator.ColorMap = value;
                _ifsCalculator.ColorMap = value;
                _lsystemCalculator.ColorMap = value;
                _attractorCalculator.ColorMap = value;
                _buddhabrotCalculator.ColorMap = value;
                _logisticCalculator.ColorMap = value;
                _halleyCalculator.ColorMap = value;
                _secantCalculator.ColorMap = value;
                _nebulabrotCalculator.ColorMap = value;
                _antiBuddhabrotCalculator.ColorMap = value;
                _antiNebulabrotCalculator.ColorMap = value;
                _newtonCalculator.ColorMap = value;
                _userEquationCalculator.ColorMap = value;
                _mandelbulbCalculator.ColorMap = value;
                _mandelboxCalculator.ColorMap = value;
                _kifsCalculator.ColorMap = value;
                _quatJuliaCalculator.ColorMap = value;
                _quatMandelbrotCalculator.ColorMap = value;
                _plasmaCalculator.ColorMap = value;
                _apollonianCalculator.ColorMap = value;
                _kleinianCalculator.ColorMap = value;
                _bicomplexCalculator.ColorMap = value;
                _dlaCalculator.ColorMap = value;
                _flameCalculator.ColorMap = value;
                _sandboxCalculator.ColorMap = value;
                _userBulbCalculator.ColorMap = value;
                _tearDropCalculator.ColorMap = value;
                _generatedZ2Calculator.ColorMap = value;
                _generatedZ3Calculator.ColorMap = value;
                _generatedZ4Calculator.ColorMap = value;
                _generatedZ5Calculator.ColorMap = value;
                _generatedTricornCalculator.ColorMap     = value;
                _generatedBurningShipCalculator.ColorMap = value;
                ColorMapChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Samples the active colour map at five iteration depths spread
        /// across the iteration range and Rec.709-weights the resulting RGB
        /// triplets into a single luminance byte. Cheap (five Map() calls,
        /// recomputed only on theme change via <see cref="ColorMapChanged"/>)
        /// and lets the Abstractions interface stay free of the main-project
        /// <c>IColorMap</c> reference.
        /// </remarks>
        public byte OverlayContrastLuma
        {
            get
            {
                var map = _calculator.ColorMap;
                if (map == null) return 255;
                try
                {
                    int maxIter = Math.Max(1, map.MaxIterations);
                    int[] samples =
                    {
                        map.Map(0.10f * maxIter, 0.10f, maxIter, 0.0f, 0.0f),
                        map.Map(0.30f * maxIter, 0.05f, maxIter, 0.0f, 0.0f),
                        map.Map(0.50f * maxIter, 0.02f, maxIter, 0.0f, 0.0f),
                        map.Map(0.80f * maxIter, 0.005f, maxIter, 0.0f, 0.0f),
                        map.SwatchSample,
                    };
                    double sum = 0;
                    foreach (int packed in samples)
                    {
                        byte r = (byte)((packed >> 16) & 0xFF);
                        byte g = (byte)((packed >> 8) & 0xFF);
                        byte b = (byte)(packed & 0xFF);
                        sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    }
                    double avg = sum / samples.Length;
                    return (byte)Math.Clamp((int)avg, 0, 255);
                }
                catch
                {
                    return 255; // fall back to "dark image" → overlay picks white
                }
            }
        }

        /// <summary>
        /// Snapshot the current view + colour state into a
        /// <see cref="PosterRequest"/> for an offscreen high-resolution render.
        /// Used by the Avalonia shell's Poster + Wallpaper commands (the shared
        /// <see cref="PosterRenderer"/> does the actual calc + save). The full
        /// quad-precision centre is copied so a Mandelbrot deep zoom survives
        /// the re-render at poster resolution.
        ///
        /// The watermark top-line and program/version sub-line are composed
        /// here, from the same RegionName / ThemeName / ProgramName /
        /// ProgramVersion the live overlay reads. Callers used to pass those
        /// strings in, and each one spelled them out slightly differently —
        /// that is how Wallpaper ended up with a "Fracturing Fog" region
        /// fallback and a version-less sub-line that no other surface had.
        /// </summary>
        public PosterRequest CreatePosterRequest(
            int width, int height, bool rotate,
            string path, FracturingFog.Imaging.ImageFileFormat format,
            FracturingFog.Models.WatermarkDef? customWatermark = null)
        {
            string watermark = FracturingFog.Imaging.WatermarkResolver.ComposeDefaultTopText(
                RegionName, ThemeName);
            string subText = FracturingFog.Imaging.WatermarkResolver.BuildSubText(
                ProgramName ?? "Fracturing Fog", ProgramVersion ?? string.Empty);
            var s = ViewState;
            int effIters = _calculator.MaxIterations > 0
                ? _calculator.MaxIterations
                : s.Quality.ComputeIterations(s.Zoom);

            return new PosterRequest
            {
                FractalType = s.FractalType,
                Width = width,
                Height = height,
                CenterX = s.CenterX, CenterXLo = s.CenterXLo, CenterX2 = s.CenterX2, CenterX3 = s.CenterX3,
                CenterY = s.CenterY, CenterYLo = s.CenterYLo, CenterY2 = s.CenterY2, CenterY3 = s.CenterY3,
                Zoom = s.Zoom,
                MaxIterations = effIters,
                ColorMap = _calculator.ColorMap,
                Quality = s.Quality,
                FractalParameters = s.FractalParameters,
                // F11 deband parity — carry the interactive toggle into the
                // offscreen render so an exported still matches what the deband
                // switch shows on screen (WYSIWYG). Default-off requests stay
                // byte-identical to the pre-F11 poster output.
                BandDither = s.BandDither,
                BandDitherStrength = s.BandDitherStrength,
                Rotate = rotate,
                Path = path,
                Format = format,
                Watermark = watermark,
                SubText = subText,
                CustomWatermark = customWatermark,
            };
        }

        // ── ApplyView ─────────────────────────────────────────────────────────

        public void ApplyView(int maxIters = 0)
        {
            _calculator.CenterX = ViewState.CenterX;
            _calculator.CenterXLo = ViewState.CenterXLo;
            _calculator.CenterX2 = ViewState.CenterX2;
            _calculator.CenterX3 = ViewState.CenterX3;
            // OD limbs (X4..X7) — required past the OD threshold (1e50). Dropping
            // them truncated the render centre to QD while the view state held
            // the full OD value, so deep frames rendered at a wrong centre and
            // navigation compounded against the mis-placed image.
            _calculator.CenterX4 = ViewState.CenterX4;
            _calculator.CenterX5 = ViewState.CenterX5;
            _calculator.CenterX6 = ViewState.CenterX6;
            _calculator.CenterX7 = ViewState.CenterX7;
            _calculator.CenterY = ViewState.CenterY;
            _calculator.CenterYLo = ViewState.CenterYLo;
            _calculator.CenterY2 = ViewState.CenterY2;
            _calculator.CenterY3 = ViewState.CenterY3;
            _calculator.CenterY4 = ViewState.CenterY4;
            _calculator.CenterY5 = ViewState.CenterY5;
            _calculator.CenterY6 = ViewState.CenterY6;
            _calculator.CenterY7 = ViewState.CenterY7;
            _calculator.Zoom = ViewState.Zoom;
            _calculator.Quality = ViewState.Quality;
            // Issue #96 — global interior alpha (Mandelbrot canonical path).
            _calculator.InteriorAlpha = ViewState.FractalParameters?.InteriorAlpha ?? 255;

            if (ViewState.IterLocked)
                _calculator.MaxIterations = ViewState.LockedIterations;
            else if (maxIters > 0)
                _calculator.MaxIterations = maxIters;
            else if (ViewState.PreferredIterations > 0)
                // Region-supplied iter override (cleared on first user pan/zoom).
                // Without this the live render falls back to Quality.ComputeIterations
                // and drops to a lower iter count than the cross-fade source used,
                // causing visible detail loss the moment the post-commit Trigger
                // present overwrites the faded-in offscreen buffer.
                _calculator.MaxIterations = ViewState.PreferredIterations;
            else
                _calculator.MaxIterations = ViewState.Quality.ComputeIterations(ViewState.Zoom);

            // EscapeTime + TearDrop also need the full state; the rest pick
            // up CX/CY/Zoom/MaxIter via the explicit copy block in Trigger().
            _escapeCalculator.CenterX = ViewState.CenterX;
            _escapeCalculator.CenterY = ViewState.CenterY;
            _escapeCalculator.Zoom = ViewState.Zoom;
            _escapeCalculator.Quality = ViewState.Quality;
            _escapeCalculator.MaxIterations = _calculator.MaxIterations;
            _escapeCalculator.FractalType = ViewState.FractalType;
            _escapeCalculator.FractalParameters = ViewState.FractalParameters;
            _escapeCalculator.ColorMap = _calculator.ColorMap;

            _tearDropCalculator.CenterX = ViewState.CenterX;
            _tearDropCalculator.CenterXLo = ViewState.CenterXLo;
            _tearDropCalculator.CenterX2 = ViewState.CenterX2;
            _tearDropCalculator.CenterX3 = ViewState.CenterX3;
            _tearDropCalculator.CenterY = ViewState.CenterY;
            _tearDropCalculator.CenterYLo = ViewState.CenterYLo;
            _tearDropCalculator.CenterY2 = ViewState.CenterY2;
            _tearDropCalculator.CenterY3 = ViewState.CenterY3;
            _tearDropCalculator.Zoom = ViewState.Zoom;
            _tearDropCalculator.Quality = ViewState.Quality;
            _tearDropCalculator.MaxIterations = _calculator.MaxIterations;
            _tearDropCalculator.FractalParameters = ViewState.FractalParameters;
            _tearDropCalculator.ColorMap = _calculator.ColorMap;
        }

        // ── Trigger ───────────────────────────────────────────────────────────

        public void TriggerFast()
        {
            int saved = _calculator.MaxIterations;
            _calculator.MaxIterations = Math.Min(128, saved);
            _userBulbCalculator.LowResPreview = true;
            Trigger(progressive: false);
            _calculator.MaxIterations = saved;
            _userBulbCalculator.LowResPreview = false;
        }

        private void InvalidateAdaptiveCdf()
        {
            lock (_adaptiveCdfLock)
            {
                _adaptiveCdfValid = false;
                _cachedAdaptiveCdf = null;
            }
        }

        /// <summary>#249 / IDEA-1 — set the global live palette-rotation phase
        /// and re-render so the palette appears to rotate over the field. The
        /// rotation lives on <see cref="GradientColorMap.LivePaletteRotation"/>
        /// (read inside the per-pixel LUT sample); a fast re-render re-maps the
        /// field through the rotated LUT. Cheap for procedural / Acid Warp.</summary>
        public void SetLivePaletteRotation(float turns)
        {
            if (_disposed) return;
            GradientColorMap.LivePaletteRotation = turns;
            // Re-run the map over the field. TriggerFast keeps live cycling
            // smooth (capped-iter for heavy types); procedural fills ignore the
            // cap and just re-evaluate their closed form.
            TriggerFast();
        }

        public void Trigger(bool progressive = false)
        {
            if (_disposed) return;

            // Phase 18b fix — mark a frame as in flight so the animation tick
            // doesn't cancel this Trigger 33 ms later. AnimationFrameUploaded
            // clears the flag once the upload completes (cancelled frames
            // count too — see RunFrameJobUpload).
            System.Threading.Interlocked.Exchange(ref _animFrameInFlight, 1);

            // Finding A fix: fire status BEFORE any blocking work so the user
            // sees "Calculating…" immediately on click. Previously this fired
            // after Cancel + stale-upload + ApplyView + alt-switch, so under
            // burst input the status string lagged by tens of ms at 4K, and
            // on fast frames the calc finished before status even flipped.
            StatusRequested?.Invoke(this, "Calculating…");

            // Buffers are about to be overwritten by Calculate — any cached
            // CDF is stale until the next completion repopulates it.
            InvalidateAdaptiveCdf();

            CancellationTokenSource cts;
            lock (_calcLock)
            {
                _calcCts?.Cancel();
                _calcCts = new CancellationTokenSource();
                cts = _calcCts;
            }

            ApplyView();

            var token = cts.Token;
            var calc = _calculator;
            IFractalCalculator? altCalc = SelectAltCalculator(ViewState.FractalType);
            bool useAlt = altCalc != null;

            if (useAlt)
            {
                SyncAltStateFromMandel(altCalc!);
                switch (altCalc)
                {
                    case FracturingFog.Calculators.Generated.MandelbrotZ2Calculator gz2:
                        // Big+ deep zoom: plumb the full DD/QD centre limbs +
                        // opt into the perturbation + BLA paths so the
                        // generated calc tracks the legacy MandelbrotCalculator
                        // through QD-precision zooms (~1e50 ceiling).
                        gz2.CenterXLo = ViewState.CenterXLo;
                        gz2.CenterX2  = ViewState.CenterX2;
                        gz2.CenterX3  = ViewState.CenterX3;
                        gz2.CenterYLo = ViewState.CenterYLo;
                        gz2.CenterY2  = ViewState.CenterY2;
                        gz2.CenterY3  = ViewState.CenterY3;
                        // Task #14 fixed: AVX-512 perturbation lane now
                        // promotes |z|² to DD (using refZrLo/refZiLo) and
                        // calls ColorForDd — matches scalar tail precision.
                        // Smooth count survives the log-log cast past
                        // zoom 1e12; per-decade unique-value count tracks
                        // legacy MandelbrotCalculator.
                        gz2.UsePerturbation = true;
                        gz2.UseBla          = true;
                        gz2.UseSa           = true;
                        break;
                    case FracturingFog.Calculators.Generated.MandelbrotZ3Calculator gz3:
                        gz3.CenterXLo = ViewState.CenterXLo;
                        gz3.CenterX2  = ViewState.CenterX2;
                        gz3.CenterX3  = ViewState.CenterX3;
                        gz3.CenterYLo = ViewState.CenterYLo;
                        gz3.CenterY2  = ViewState.CenterY2;
                        gz3.CenterY3  = ViewState.CenterY3;
                        gz3.UsePerturbation = true;
                        gz3.UseBla          = true;
                        break;
                    case FracturingFog.Calculators.Generated.MandelbrotZ4Calculator gz4:
                        gz4.CenterXLo = ViewState.CenterXLo;
                        gz4.CenterX2  = ViewState.CenterX2;
                        gz4.CenterX3  = ViewState.CenterX3;
                        gz4.CenterYLo = ViewState.CenterYLo;
                        gz4.CenterY2  = ViewState.CenterY2;
                        gz4.CenterY3  = ViewState.CenterY3;
                        gz4.UsePerturbation = true;
                        gz4.UseBla          = true;
                        break;
                    case FracturingFog.Calculators.Generated.MandelbrotZ5Calculator gz5:
                        gz5.CenterXLo = ViewState.CenterXLo;
                        gz5.CenterX2  = ViewState.CenterX2;
                        gz5.CenterX3  = ViewState.CenterX3;
                        gz5.CenterYLo = ViewState.CenterYLo;
                        gz5.CenterY2  = ViewState.CenterY2;
                        gz5.CenterY3  = ViewState.CenterY3;
                        gz5.UsePerturbation = true;
                        gz5.UseBla          = true;
                        break;
                }
            }

            var sw = Stopwatch.StartNew();

            // Snapshot state for the stale-frame upload (runs on the
            // threadpool so the UI thread doesn't block on a 5-15 ms GPU
            // upload before Calculate even starts — Finding A render-start
            // lag fix).
            //
            // S-X9g (2026-06-27) — pick _lastPresentedBuffer (whatever res it
            // is) so a pan-stop debounce Trigger paints the most recent thing
            // the user saw (= the panned ¼/½ progressive preview) rather than
            // the pre-pan full-res frame. The previous dim-equality gate
            // forced a fall back to pre-pan content and produced a visible
            // snap-back. UpdateTexture handles arbitrary dims (the fullscreen
            // quad sampler stretches).
            int calcW = _calculator.Width;
            int calcH = _calculator.Height;
            uint[]? staleBuf;
            int staleW, staleH;
            if (_lastPresentedBuffer != null)
            {
                staleBuf = _lastPresentedBuffer;
                staleW = _lastPresentedWidth;
                staleH = _lastPresentedHeight;
            }
            else
            {
                staleBuf = null;
                staleW = staleH = 0;
            }

            // Wave 2.5 — progressive only on the canonical Mandelbrot path
            // and only when the dynamic alt slot is empty. Alt calcs run a
            // single full render as before. Tiny windows (W*H < 256 px) skip
            // progressive too — overhead exceeds the win.
            int progressiveStage = (progressive && !useAlt && _dynamicAltCalculator == null
                                     && calcW * calcH >= 256 * 256)
                ? 4
                : 0;

            // T2.4: enqueue onto the dedicated calc thread (latest-only).
            // Drain any queued-but-unstarted job first so a burst of Triggers
            // (wheel zoom, key-repeat) collapses to the freshest job before
            // the calc thread can pick a stale one up.
            long seq0 = System.Threading.Interlocked.Increment(ref _uploadSeq);
            var job = new FrameJob(token, calc, useAlt ? altCalc : null, sw,
                staleBuf, staleW, staleH, calcW, calcH,
                seq: seq0,
                taaSampleIndex: 0, progressiveStage: progressiveStage);
            Dbg86($"TRIGGER seq={seq0} progressive={progressive} stage={progressiveStage} " +
                  $"zoom={ViewState.Zoom:0e+0} staleBuf={(staleBuf != null ? $"{staleW}x{staleH}" : "none")}");
            while (_calcQueue.TryTake(out _)) { }
            try { _calcQueue.Add(job); }
            catch (InvalidOperationException) { /* CompleteAdding during Dispose */ }
        }

        // Long-lived background thread. Owns the stale-frame re-upload + the
        // Calculate() call. After Calculate the upload step is handed off to
        // the threadpool so this thread immediately becomes available for the
        // next queued frame, preserving the calc↔upload overlap the old
        // Task.Run+ContinueWith path got for free.
        private void CalcThreadLoop()
        {
            try
            {
                foreach (var job in _calcQueue.GetConsumingEnumerable())
                {
                    // #85 — mark busy for the whole write region so a
                    // concurrent Resize drains (waits) before swapping arrays.
                    _calcIdle.Reset();
                    try { RunFrameJobCalc(in job); }
                    catch (OperationCanceledException) { }
                    catch { /* swallow — token-driven cancellation is the only
                              expected failure mode; surface anything else via
                              the calc's own error path if it has one. */ }
                    finally { _calcIdle.Set(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        // F11: push the ViewState deband toggle into the global GradientColorMap
        // statics that the CPU (F11a) and GPU (F11b) quantise points read. One
        // knob, applied at every render entry so interactive / video / export
        // stay consistent. Default-off ⇒ statics stay false ⇒ plain quantise.
        private void ApplyBandDitherState()
        {
            FracturingFog.Models.GradientColorMap.DitherEnabled = ViewState.BandDither;
            FracturingFog.Models.GradientColorMap.DitherStrength =
                System.Math.Clamp(ViewState.BandDitherStrength, 0, 100) / 100f;
        }

        private void RunFrameJobCalc(in FrameJob job)
        {
            ApplyBandDitherState();
            var token = job.Token;
            var calc = job.Calc;
            var altCalc = job.AltCalc;
            bool useAlt = altCalc != null;

            // Wave 2.5 — progressive preview stage. Run Calculate on a
            // sidecar quarter / half calc, upload its smaller buffer (the D3D
            // renderer's full-screen triangle stretches via the texture
            // sampler), then hand off to RunFrameJobUpload tail which will
            // schedule the next stage. Skips TAA / MSAA / SSAO / CDF rebuild
            // / FrameCompleted — those apply only to the final full-res
            // frame.
            if (job.ProgressiveStage >= 2 && !useAlt)
            {
                long pCalcStart = Stopwatch.GetTimestamp();
                MandelbrotCalculator preview = job.ProgressiveStage >= 4
                    ? _previewCalcQuarter
                    : _previewCalcHalf;
                MirrorMandelbrotState(calc, preview);
                // SM-11b — let the preview reuse its cached reference orbit across
                // a drag so consecutive preview frames share one reference (no
                // per-move recompute → no deep-zoom "jumping"). Only the transient
                // preview; the committed full-res calc below stays fresh/exact.
                preview.AllowRecycleThisRender = RecyclePreviewOrbit;
                try { preview.Calculate(token); }
                catch (OperationCanceledException)
                {
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    return;
                }
                long pCalcEnd = Stopwatch.GetTimestamp();
                if (ShowPerfHud)
                    _perfStats.RecordCalc((pCalcEnd - pCalcStart) * 1000.0 / Stopwatch.Frequency);

                if (token.IsCancellationRequested)
                {
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    return;
                }

                long pMs = job.Sw.ElapsedMilliseconds;
                ThreadPool.UnsafeQueueUserWorkItem(s_uploadCallback,
                    new UploadCtx(this, job, pMs), preferLocal: false);
                return;
            }

            // Wave 2.7 — TAA continuation frame. Skips stale-upload, MSAA,
            // SSAO recompute, CDF rebuild. Runs one jittered Calculate, blends
            // the result into the running TAA sum, writes the averaged colour
            // back into calc.ColorBuffer, and hands off to the standard upload
            // path so brightness/contrast + grid/watermark composite still
            // applies. The accumulator was already seeded by the initial
            // (TaaSampleIndex == 0) frame.
            if (job.TaaSampleIndex > 0 && !useAlt)
            {
                long taaCalcStart = Stopwatch.GetTimestamp();
                bool ok = false;
                try
                {
                    ok = RunOneTaaSample(calc, job.TaaSampleIndex, token);
                }
                catch (OperationCanceledException) { ok = false; }
                long taaCalcEnd = Stopwatch.GetTimestamp();

                if (ShowPerfHud)
                    _perfStats.RecordCalc((taaCalcEnd - taaCalcStart) * 1000.0 / Stopwatch.Frequency);

                if (!ok || token.IsCancellationRequested)
                {
                    // Cancelled or fingerprint changed mid-step — drop this
                    // frame quietly (the next user-initiated Trigger will
                    // present a fresh image). Still fire AnimationFrameUploaded
                    // so any in-flight gate doesn't get stuck.
                    // S-X8 (2026-06-27) — also raise RenderCancelled so the
                    // status bar's "Calculating…" set by Trigger gets cleared
                    // instead of lingering until the next user action.
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    RenderCancelled?.Invoke(this, EventArgs.Empty);
                    return;
                }

                long taaMs = job.Sw.ElapsedMilliseconds;
                ThreadPool.UnsafeQueueUserWorkItem(s_uploadCallback,
                    new UploadCtx(this, job, taaMs), preferLocal: false);
                return;
            }

            // Stale-frame re-upload so the screen shows a correct (if
            // old) image while the next frame computes. Serialised
            // against the calc-completion upload via _d3dGate, so the
            // new frame in the continuation always paints over the
            // stale here (never the reverse).
            //
            // S-X9g (2026-06-27) — dim-equality gate dropped. UpdateTexture
            // accepts any (w,h) and the fullscreen quad sampler stretches.
            // Letting a ¼-res progressive preview upload here (when that's
            // the most recent thing the user saw) keeps the displayed
            // POSITION correct through a pan-stop debounce; pre-fix the gate
            // skipped the preview because StaleW != CalcW, and the fallback
            // to the pre-pan full-res frame produced the visible snap-back.
            if (job.StaleBuf != null)
            {
                lock (_uploadGate)
                {
                    // #86 — drop the stale-hold present if a newer frame already
                    // reached the screen, so this old buffer can't clobber it.
                    bool claimed = TryClaimPresent(job.Seq);
                    Dbg86($"STALE-HOLD seq={job.Seq} claimed={claimed} lastSeq={_lastPresentedUploadSeq} {job.StaleW}x{job.StaleH}");
                    if (claimed)
                    lock (_d3dGate)
                    {
                        _renderer.UpdateTexture(job.StaleBuf, job.StaleW, job.StaleH);
                        _renderer.Render();
                    }
                }
            }

            long calcStart = Stopwatch.GetTimestamp();

            // #107 — True per-eye side-by-side stereo. Only the 3D raymarcher
            // family honours StereoEyeOffset (ViewState.Is3D). The two eye
            // renders must run here on the calc thread: RenderTrueStereo drives
            // calc.Calculate twice, and letting the upload threadpool re-enter
            // the shared calc while this thread loops to the next job would
            // corrupt both. Opt-in (StereoMode.True + eye-sep > 0); the default
            // Off path below is byte-identical to pre-#107. The composited
            // 2·W × H buffer is display-ready (each eye ran the full per-calc
            // tonemap/bloom), so it uploads with srcAlreadyProcessed:true and
            // presents straight to screen + the screenshot/export snapshot.
            {
                var stFx = ViewState?.FractalParameters?.Lighting;
                bool trueStereo = !useAlt
                    && (ViewState?.Is3D ?? false)
                    && stFx.HasValue
                    && stFx.Value.StereoMode == FracturingFog.Rendering.Lighting.StereoMode.True
                    && stFx.Value.StereoEyeSeparation > 0.0;
                if (trueStereo)
                {
                    uint[]? sbs = null;
                    try
                    {
                        sbs = FracturingFog.Rendering.Lighting.StereoRender.RenderTrueStereo(
                            ViewState!.FractalParameters,
                            t => calc.Calculate(t),
                            () => calc.ColorBuffer,
                            calc.Width, calc.Height, token);
                    }
                    catch (OperationCanceledException) { }
                    long stEnd = Stopwatch.GetTimestamp();
                    if (ShowPerfHud)
                        _perfStats.RecordCalc((stEnd - calcStart) * 1000.0 / Stopwatch.Frequency);

                    // Each eye is a single sample — no TAA/MSAA accumulation.
                    InvalidateTaa();

                    if (sbs != null && !token.IsCancellationRequested)
                    {
                        var (outW, outH) = FracturingFog.Rendering.Lighting.StereoRender
                            .OutputDims(calc.Width, calc.Height, stFx.Value.StereoLayout);
                        lock (_uploadGate)
                        {
                            if (TryClaimPresent(job.Seq))
                                UploadProcessedBuffer(sbs, outW, outH,
                                                      srcAlreadyProcessed: true);
                        }
                        FrameCompleted?.Invoke(this, new RenderFrameInfo(
                            calc.CenterX, calc.CenterY, calc.Zoom, calc.MaxIterations,
                            job.Sw.ElapsedMilliseconds, calc.Width, calc.Height,
                            false, ViewState.IterLocked, ViewState.FractalType,
                            "3D-SBS", double.PositiveInfinity));
                    }
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            try
            {
                if (useAlt) altCalc!.Calculate(token);
                else calc.Calculate(token);

                // Wave 2.6 — sub-pixel MSAA via Calculate() re-runs at jittered
                // centre coords. Canonical Mandelbrot path goes through the
                // typed helper (carries the QD/DD/OD limb state). Alt calcs
                // route through the IFractalCalculator-shaped helper when the
                // family honours centre+zoom — gates out IFS/LSystem/Plasma/
                // Flame/DLA/Apollonian/StrangeAttractor whose Calculate()
                // ignores those fields and would just re-roll noise.
                // Pixel scale: 3.5/max(W,H)/Zoom mirrors generator template.
                if (!useAlt)
                {
                    int aaSamples = calc.Quality?.AaSamples ?? 1;
                    if (aaSamples > 1 && !token.IsCancellationRequested)
                        RunMsaaAccumulateMandelbrot(calc, aaSamples, token);
                }
                else if (altCalc!.SupportsZoomPan)
                {
                    int aaSamples = altCalc.Quality?.AaSamples ?? 1;
                    if (aaSamples > 1 && !token.IsCancellationRequested)
                        RunMsaaAccumulateAlt(altCalc, aaSamples, token);
                }

                // Wave 2.7 — seed the TAA accumulator from this frame's
                // finished (possibly MSAA-averaged) ColorBuffer. Captures the
                // view fingerprint so the upload tail can decide whether to
                // queue continuation samples.
                if (!useAlt && !token.IsCancellationRequested)
                    SeedTaaAccumulator(calc);
                else if (useAlt)
                    InvalidateTaa();
            }
            catch (OperationCanceledException) { }
            long calcEnd = Stopwatch.GetTimestamp();

            // Phase 8b — 2D SSAO on the canonical Mandelbrot path. Synthesises
            // depth from the smooth iteration count. Default SsaoSamples=0 keeps
            // pre-Phase-8b output bit-identical. 3D raymarchers run their own
            // SSAO inside Calculate() and aren't touched here.
            if (!useAlt && !token.IsCancellationRequested)
            {
                var fxParams = ViewState?.FractalParameters;
                if (fxParams != null && fxParams.Lighting.SsaoSamples > 0)
                {
                    try
                    {
                        var fxLocal = fxParams.Lighting;
                        FracturingFog.Rendering.Lighting.ScreenSpacePost.ApplySsao2D(
                            calc.ColorBuffer,
                            calc.SmoothBuffer,
                            calc.IterationBuffer,
                            calc.MaxIterations,
                            calc.Width, calc.Height,
                            in fxLocal);
                    }
                    catch { /* SSAO best-effort — never fail the frame. */ }
                }
            }
            if (ShowPerfHud)
            {
                _perfStats.RecordCalc((calcEnd - calcStart) * 1000.0 / Stopwatch.Frequency);
                // Phase 1.b: if the SP path ran on the GPU kernel this
                // frame, sample its split timings into the same window.
                // Skipped when CPU path ran (LastDispatchMs == 0 or NaN).
                if (_gpuKernel != null
                    && calc.UseGpuCompute
                    && !calc.IsHighPrecisionActive)
                {
                    _perfStats.RecordGpuDispatch(_gpuKernel.LastDispatchMs);
                    _perfStats.RecordGpuReadback(_gpuKernel.LastReadbackMs);
                }
            }

            long ms = job.Sw.ElapsedMilliseconds;

            // Hand the post-calc upload to the threadpool so this calc
            // thread loops back for the next queued frame without blocking
            // on UploadProcessedBuffer (post-FX + GPU upload).
            ThreadPool.UnsafeQueueUserWorkItem(s_uploadCallback,
                new UploadCtx(this, job, ms), preferLocal: false);
        }

        // Wave 2.6 — MSAA helper. Runs N total samples on a √N×√N jitter grid
        // around the original centre coords; first sample (already in
        // calc.ColorBuffer when this is called) is reused as the (0,0)-jitter
        // entry. Each subsequent Calculate() shifts CenterX/CenterY by a
        // sub-pixel offset, then we accumulate channel sums and write the
        // averaged colour back. Restores Center on exit.
        //
        // Format: ColorBuffer is uint with packed BGRA (matches IColorMap.Map's
        // output everywhere; the renderer's UpdateTexture also expects BGRA).
        // The byte extraction below is endian-safe because we touch the same
        // uint on both sides — we never reinterpret the byte order.
        private static void RunMsaaAccumulateMandelbrot(
            MandelbrotCalculator calc, int aaSamples, CancellationToken token)
        {
            int side = (int)Math.Round(Math.Sqrt(aaSamples));
            if (side * side != aaSamples) return; // only square grids
            int pixels = calc.Width * calc.Height;
            uint[] color = calc.ColorBuffer;
            if (color.Length < pixels) return;

            var sumR = new int[pixels];
            var sumG = new int[pixels];
            var sumB = new int[pixels];
            var sumA = new int[pixels];

            // Accumulate the already-computed sample 0 (centre).
            for (int i = 0; i < pixels; i++)
            {
                uint c = color[i];
                sumB[i] +=  (int)(c        & 0xFF);
                sumG[i] +=  (int)((c >> 8)  & 0xFF);
                sumR[i] +=  (int)((c >> 16) & 0xFF);
                sumA[i] +=  (int)((c >> 24) & 0xFF);
            }

            // Pixel scale matches generated calculators: 3.5/max(W,H)/Zoom.
            double scale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;
            double origCx = calc.CenterX;
            double origCy = calc.CenterY;

            int count = 1; // sample 0 already counted
            for (int sy = 0; sy < side; sy++)
            {
                double jy = (sy + 0.5) / side - 0.5; // ∈ [-0.5, 0.5)
                for (int sx = 0; sx < side; sx++)
                {
                    if (sx == side / 2 && sy == side / 2) continue; // centre already done
                    if (token.IsCancellationRequested) goto WriteBack;

                    double jx = (sx + 0.5) / side - 0.5;
                    calc.CenterX = origCx + jx * scale;
                    calc.CenterY = origCy + jy * scale;
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { goto WriteBack; }

                    var buf = calc.ColorBuffer;
                    for (int i = 0; i < pixels; i++)
                    {
                        uint c = buf[i];
                        sumB[i] += (int)(c        & 0xFF);
                        sumG[i] += (int)((c >> 8)  & 0xFF);
                        sumR[i] += (int)((c >> 16) & 0xFF);
                        sumA[i] += (int)((c >> 24) & 0xFF);
                    }
                    count++;
                }
            }
        WriteBack:
            calc.CenterX = origCx;
            calc.CenterY = origCy;
            if (count <= 0) return;
            int half = count / 2;
            var outBuf = calc.ColorBuffer;
            for (int i = 0; i < pixels; i++)
            {
                uint b = (uint)((sumB[i] + half) / count) & 0xFF;
                uint g = (uint)((sumG[i] + half) / count) & 0xFF;
                uint r = (uint)((sumR[i] + half) / count) & 0xFF;
                uint a = (uint)((sumA[i] + half) / count) & 0xFF;
                outBuf[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        // Wave 2.6 alt-calc broadening — IFractalCalculator-shaped twin of
        // RunMsaaAccumulateMandelbrot for the user-equation hot-load path
        // and other escape-time alt calcs (Newton/Nova/Halley/Secant/Magnet/
        // Glynn/Spider/Phoenix) and 3D raymarchers (Mandelbulb/UserBulb/
        // Mandelbox/KIFS/Quaternion*/Bicomplex/Kleinian). Identical maths;
        // only the calc shape differs because MandelbrotCalculator is the
        // concrete legacy path (still carries QD/DD/OD limb fields), not an
        // IFractalCalculator. Sub-pixel jitter on (CenterX, CenterY) at the
        // standard pixel-size heuristic; weighted-mean BGRA channels written
        // back into ColorBuffer. Caller gates SupportsZoomPan so families
        // whose Calculate() ignores centre+zoom (IFS/LSystem/Plasma/Flame/
        // DLA/Apollonian/StrangeAttractor) never reach here.
        private static void RunMsaaAccumulateAlt(
            IFractalCalculator calc, int aaSamples, CancellationToken token)
        {
            int side = (int)Math.Round(Math.Sqrt(aaSamples));
            if (side * side != aaSamples) return;
            int pixels = calc.Width * calc.Height;
            uint[] color = calc.ColorBuffer;
            if (color.Length < pixels) return;

            var sumR = new int[pixels];
            var sumG = new int[pixels];
            var sumB = new int[pixels];
            var sumA = new int[pixels];

            for (int i = 0; i < pixels; i++)
            {
                uint c = color[i];
                sumB[i] +=  (int)(c        & 0xFF);
                sumG[i] +=  (int)((c >> 8)  & 0xFF);
                sumR[i] +=  (int)((c >> 16) & 0xFF);
                sumA[i] +=  (int)((c >> 24) & 0xFF);
            }

            double scale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;
            double origCx = calc.CenterX;
            double origCy = calc.CenterY;

            int count = 1;
            for (int sy = 0; sy < side; sy++)
            {
                double jy = (sy + 0.5) / side - 0.5;
                for (int sx = 0; sx < side; sx++)
                {
                    if (sx == side / 2 && sy == side / 2) continue;
                    if (token.IsCancellationRequested) goto WriteBack;

                    double jx = (sx + 0.5) / side - 0.5;
                    calc.CenterX = origCx + jx * scale;
                    calc.CenterY = origCy + jy * scale;
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { goto WriteBack; }

                    var buf = calc.ColorBuffer;
                    for (int i = 0; i < pixels; i++)
                    {
                        uint c = buf[i];
                        sumB[i] += (int)(c        & 0xFF);
                        sumG[i] += (int)((c >> 8)  & 0xFF);
                        sumR[i] += (int)((c >> 16) & 0xFF);
                        sumA[i] += (int)((c >> 24) & 0xFF);
                    }
                    count++;
                }
            }
        WriteBack:
            calc.CenterX = origCx;
            calc.CenterY = origCy;
            if (count <= 0) return;
            int half = count / 2;
            var outBuf = calc.ColorBuffer;
            for (int i = 0; i < pixels; i++)
            {
                uint b = (uint)((sumB[i] + half) / count) & 0xFF;
                uint g = (uint)((sumG[i] + half) / count) & 0xFF;
                uint r = (uint)((sumR[i] + half) / count) & 0xFF;
                uint a = (uint)((sumA[i] + half) / count) & 0xFF;
                outBuf[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        // Wave 2.7 — TAA helpers.
        //
        // Seed: reset the sum arrays to the current ColorBuffer contents
        // (which may already be MSAA-averaged) and stamp the fingerprint.
        // Blend: jitter Calculate, add into sums, write average back into
        // ColorBuffer. Invalidate: drop the accumulator (called on Resize,
        // useAlt, or fingerprint mismatch).
        private void SeedTaaAccumulator(MandelbrotCalculator calc)
        {
            int taaMax = calc.Quality?.TaaMaxSamples ?? 1;
            if (taaMax <= 1) { InvalidateTaa(); return; }

            int pixels = calc.Width * calc.Height;
            var color = calc.ColorBuffer;
            if (color.Length < pixels) { InvalidateTaa(); return; }

            if (_taaSumR == null || _taaSumPixels != pixels)
            {
                _taaSumR = new long[pixels];
                _taaSumG = new long[pixels];
                _taaSumB = new long[pixels];
                _taaSumA = new long[pixels];
                _taaSumPixels = pixels;
            }

            for (int i = 0; i < pixels; i++)
            {
                uint c = color[i];
                _taaSumB![i] =  (long)(c        & 0xFF);
                _taaSumG![i] =  (long)((c >> 8)  & 0xFF);
                _taaSumR![i] =  (long)((c >> 16) & 0xFF);
                _taaSumA![i] =  (long)((c >> 24) & 0xFF);
            }
            _taaSampleCount = 1;
            _taaFpCx = calc.CenterX;
            _taaFpCy = calc.CenterY;
            _taaFpZoom = calc.Zoom;
            _taaFpIter = calc.MaxIterations;
            _taaFpW = calc.Width;
            _taaFpH = calc.Height;
            _taaFpType = ViewState.FractalType;
            _taaValid = true;
        }

        private void InvalidateTaa()
        {
            _taaValid = false;
            _taaSampleCount = 0;
            // Keep sum arrays around for reuse — they'll be re-zeroed on the
            // next SeedTaaAccumulator.
        }

        private bool TaaFingerprintMatches(MandelbrotCalculator calc)
        {
            return _taaValid
                && _taaFpCx == calc.CenterX
                && _taaFpCy == calc.CenterY
                && _taaFpZoom == calc.Zoom
                && _taaFpIter == calc.MaxIterations
                && _taaFpW == calc.Width
                && _taaFpH == calc.Height
                && _taaFpType == ViewState.FractalType;
        }

        // Halton(idx, base) radical-inverse — quasi-random in [0, 1).
        // Used for sub-pixel jitter that distributes far better than a
        // grid would across an arbitrary sample count.
        private static double Halton(int index, int b)
        {
            double f = 1.0, r = 0.0;
            int i = index;
            while (i > 0)
            {
                f /= b;
                r += f * (i % b);
                i /= b;
            }
            return r;
        }

        private bool RunOneTaaSample(MandelbrotCalculator calc, int sampleIndex, CancellationToken token)
        {
            if (!TaaFingerprintMatches(calc)) return false;
            int pixels = calc.Width * calc.Height;
            if (_taaSumR == null || _taaSumPixels != pixels) return false;

            double jx = Halton(sampleIndex + 2, 2) - 0.5;
            double jy = Halton(sampleIndex + 2, 3) - 0.5;
            double scale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;

            double origCx = calc.CenterX;
            double origCy = calc.CenterY;
            calc.CenterX = origCx + jx * scale;
            calc.CenterY = origCy + jy * scale;
            try { calc.Calculate(token); }
            catch (OperationCanceledException)
            {
                calc.CenterX = origCx; calc.CenterY = origCy;
                return false;
            }
            calc.CenterX = origCx;
            calc.CenterY = origCy;
            if (token.IsCancellationRequested) return false;

            var buf = calc.ColorBuffer;
            for (int i = 0; i < pixels; i++)
            {
                uint c = buf[i];
                _taaSumB![i] += (long)(c        & 0xFF);
                _taaSumG![i] += (long)((c >> 8)  & 0xFF);
                _taaSumR![i] += (long)((c >> 16) & 0xFF);
                _taaSumA![i] += (long)((c >> 24) & 0xFF);
            }
            _taaSampleCount++;

            int count = _taaSampleCount;
            long half = count / 2;
            for (int i = 0; i < pixels; i++)
            {
                uint b = (uint)((_taaSumB![i] + half) / count) & 0xFF;
                uint g = (uint)((_taaSumG![i] + half) / count) & 0xFF;
                uint r = (uint)((_taaSumR![i] + half) / count) & 0xFF;
                uint a = (uint)((_taaSumA![i] + half) / count) & 0xFF;
                buf[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
            return true;
        }

        // Wave 2.7 — called at the tail of the upload step. If TAA is enabled,
        // view is still stable, and we haven't hit the cap, enqueue the next
        // jittered sample so the image keeps refining while the camera idles.
        // Any user Trigger cancels the token (which the calc thread checks) and
        // mutates the calc state (which invalidates the fingerprint), so we
        // don't need explicit cooperation to stop.
        private void TryScheduleNextTaaSample(in FrameJob job)
        {
            if (job.AltCalc != null) return;
            if (job.Token.IsCancellationRequested) return;
            var calc = job.Calc;
            int taaMax = calc.Quality?.TaaMaxSamples ?? 1;
            if (taaMax <= 1) return;
            if (!TaaFingerprintMatches(calc)) return;
            if (_taaSampleCount >= taaMax) return;

            // Deep-zoom TAA no-op guard. RunOneTaaSample jitters the centre by
            // ~scale on the DOUBLE CenterX/CenterY only; once that sub-pixel
            // offset falls below the centre's double ULP (roughly past zoom
            // ~1e10) it rounds away, so every continuation renders a byte-
            // identical frame. Those are pure waste and re-run the upload/HUD/
            // status path each pass — the "status flushing after the render
            // finished" the user sees at deep zoom. Skip them: the base sample
            // already IS the converged image. (Real deep-zoom TAA needs the
            // jitter fed through the OD centre / per-pixel offset — deferred.)
            double taaScale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;
            double ulpCentre = Math.Max(Math.Abs(calc.CenterX), Math.Abs(calc.CenterY))
                               * 2.220446049250313e-16;
            if (taaScale <= ulpCentre) return;

            var nextJob = new FrameJob(
                job.Token, calc, altCalc: null, sw: Stopwatch.StartNew(),
                staleBuf: null, staleW: 0, staleH: 0,
                calcW: calc.Width, calcH: calc.Height,
                seq: System.Threading.Interlocked.Increment(ref _uploadSeq),
                taaSampleIndex: _taaSampleCount);
            try { _calcQueue.Add(nextJob); }
            catch (InvalidOperationException) { /* CompleteAdding during Dispose */ }
            catch (ArgumentException)
            {
                // Bounded(1) full — drain stale entry then re-enqueue. A user
                // Trigger between the drain and add would just supersede us
                // with a fresh frame, which is desired.
                while (_calcQueue.TryTake(out _)) { }
                try { _calcQueue.Add(nextJob); }
                catch { /* shutdown race */ }
            }
        }

        // Wave 2.5 — copy view-relevant Mandelbrot state from the main calc
        // onto a sidecar preview calc. Buffer dims stay at the preview calc's
        // (smaller) size — the calc thread temporarily inherits the centre /
        // zoom / iter / quality / colour map / acceleration flags so the
        // pixel scale and ref orbit reproduce the main view at downsample.
        private static void MirrorMandelbrotState(MandelbrotCalculator src, MandelbrotCalculator dst)
        {
            dst.CenterX   = src.CenterX;   dst.CenterXLo = src.CenterXLo;
            dst.CenterX2  = src.CenterX2;  dst.CenterX3  = src.CenterX3;
            dst.CenterX4  = src.CenterX4;  dst.CenterX5  = src.CenterX5;
            dst.CenterX6  = src.CenterX6;  dst.CenterX7  = src.CenterX7;
            dst.CenterY   = src.CenterY;   dst.CenterYLo = src.CenterYLo;
            dst.CenterY2  = src.CenterY2;  dst.CenterY3  = src.CenterY3;
            dst.CenterY4  = src.CenterY4;  dst.CenterY5  = src.CenterY5;
            dst.CenterY6  = src.CenterY6;  dst.CenterY7  = src.CenterY7;
            dst.Zoom = src.Zoom;
            dst.MaxIterations = src.MaxIterations;
            dst.Quality = src.Quality;
            dst.ColorMap = src.ColorMap;
            dst.DisableAcceleration = src.DisableAcceleration;
            dst.DisableSeriesApproximation = src.DisableSeriesApproximation;
            dst.DisableDdBla = src.DisableDdBla;
        }

        /// <summary>#143 — render the smooth-count height field at a resolution
        /// floor (short axis <see cref="FractalParameters.Relief2DFieldFloor"/>,
        /// aspect preserved) into <see cref="_reliefHeight"/>, decoupling relief
        /// quality from the display size. Returns false — leaving the relief state
        /// untouched so the caller falls back to the display-res field — when the
        /// window is already at/above the floor or the field render is cancelled.
        /// Runs on the calc thread after the main Calculate; the dedicated
        /// <see cref="_reliefFieldCalc"/> mirrors the main calc's view + precision
        /// so deep-zoom perturbation is reproduced at the higher resolution.</summary>
        private bool TryCaptureHiResReliefField(MandelbrotCalculator calc,
            FractalParameters p, int dispW, int dispH, CancellationToken token)
        {
            if (dispW <= 2 || dispH <= 2) return false;
            int floor = Math.Clamp(p.Relief2DFieldFloor, 480, 2160);
            int shortAxis = Math.Min(dispW, dispH);
            if (shortAxis >= floor) return false;   // display already ≥ floor — no gain

            // Scale so the short axis hits the floor; cap the long axis so a very
            // wide Span doesn't blow the field render up.
            double s = floor / (double)shortAxis;
            int fw = (int)Math.Round(dispW * s);
            int fh = (int)Math.Round(dispH * s);
            const int MaxLong = 3840;
            if (Math.Max(fw, fh) > MaxLong)
            {
                double s2 = MaxLong / (double)Math.Max(fw, fh);
                fw = (int)Math.Round(fw * s2);
                fh = (int)Math.Round(fh * s2);
            }
            fw = Math.Max(4, fw); fh = Math.Max(4, fh);

            try
            {
                var rc = _reliefFieldCalc ??= new MandelbrotCalculator(fw, fh);
                if (rc.Width != fw || rc.Height != fh) rc.Resize(fw, fh);
                MirrorMandelbrotState(calc, rc);
                // #156 — run the hi-res relief field on the GPU whenever the main
                // calc does. MirrorMandelbrotState copies view + precision but not
                // the GPU config, so without this the dedicated field calc always
                // fell back to CPU — the single biggest relief cost at depth. The
                // kernel is thread-affine to the calc thread; this capture runs on
                // that thread AFTER the main Calculate, so the two calcs share the
                // one kernel sequentially (no concurrent dispatch). Shallow uses the
                // FP32 escape-time kernel; deep uses the perturbation kernel
                // (static UseGpuPerturbation + IGpuKernel.SupportsPerturbation),
                // exactly as the main calc chooses.
                rc.UseGpuCompute = calc.UseGpuCompute;
                rc.GpuKernel = calc.GpuKernel;
                rc.Calculate(token);
                if (token.IsCancellationRequested) return false;

                var field = (rc as Interefaces.IHeightFieldSource)?.SmoothBuffer;
                int hn = fw * fh;
                if (field == null || field.Length < hn) return false;
                if (_reliefHeight == null || _reliefHeight.Length < hn)
                    _reliefHeight = new float[hn];
                Array.Copy(field, _reliefHeight, hn);
                _reliefW = fw; _reliefH = fh; _reliefValid = true;
                return true;
            }
            catch (OperationCanceledException) { return false; }
        }

        private void RunFrameJobUpload(FrameJob job, long ms)
        {
            var token = job.Token;
            var calc = job.Calc;
            var altCalc = job.AltCalc;
            bool useAlt = altCalc != null;

            // Wave 2.5 — progressive preview upload. The sidecar's
            // ColorBuffer is at preview-calc dims; the renderer's
            // EnsureTexture recreates the texture at those dims, and the
            // full-screen quad sampler scales it to the back buffer. No
            // overlay composite, no TAA, no FrameCompleted, no perf-HUD
            // frame timing (still records calc ms above). After upload,
            // enqueue the next stage (4 → 2 → 0 final).
            if (job.ProgressiveStage >= 2 && !useAlt)
            {
                if (token.IsCancellationRequested || _disposed)
                {
                    // S-X8 (2026-06-27) — progressive intermediate cancelled
                    // before the final stage queued. Status bar would stay
                    // "Calculating…" until next user input; raise
                    // RenderCancelled so the consumer clears it now.
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    RenderCancelled?.Invoke(this, EventArgs.Empty);
                    return;
                }
                MandelbrotCalculator preview = job.ProgressiveStage >= 4
                    ? _previewCalcQuarter
                    : _previewCalcHalf;
                lock (_uploadGate)
                {
                    // #86 — a later stage / newer trigger that already presented
                    // outranks this preview; drop it so it can't paint a stale,
                    // lower-res image over the newer frame.
                    bool claimedP = TryClaimPresent(job.Seq);
                    Dbg86($"PREVIEW  seq={job.Seq} stage={job.ProgressiveStage} claimed={claimedP} lastSeq={_lastPresentedUploadSeq} {preview.Width}x{preview.Height}");
                    if (claimedP)
                    {
                    // #131 — apply the heightfield relief to the PREVIEW buffer at
                    // preview dims too. Without this a pan flashes between the flat
                    // low-res 2D preview (uploaded here) and the 3D relief final
                    // frame. The preview is a MandelbrotCalculator, so it exposes
                    // its own SmoothBuffer at preview resolution; the raymarch is
                    // cheap at quarter/half and frames identically (aspect-based),
                    // so the 3D preview lines up with the final — no flash, no jump.
                    uint[] previewSrc = preview.ColorBuffer;
                    int pw = preview.Width, ph = preview.Height, pn = pw * ph;
                    {
                        var rp = ViewState.FractalParameters;
                        var phs = (preview as Interefaces.IHeightFieldSource)?.SmoothBuffer;
                        if (rp.Relief2DEnabled && phs != null && pn > 0
                            && phs.Length >= pn && previewSrc != null && previewSrc.Length >= pn)
                        {
                            // #155 — preview budget: supersample off + heavy
                            // per-hit FX (AO/SSAO/reflections/volumetric) dropped,
                            // cheap depth cues kept identical to the final frame.
                            var prp = FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.MakePreviewParams(rp);
                            if (_reliefPreviewScratch == null || _reliefPreviewScratch.Length < pn)
                                _reliefPreviewScratch = new uint[pn];
                            if (prp.Relief2DRaymarch)
                                FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.Render(
                                    previewSrc, phs, pw, ph, prp, _reliefPreviewScratch);
                            else
                                FracturingFog.Rendering.Lighting.HeightfieldRelief2D.Apply(
                                    previewSrc, _reliefPreviewScratch, phs, pw, ph, prp);
                            previewSrc = _reliefPreviewScratch;
                        }
                    }
                    lock (_d3dGate)
                    {
                        _renderer.UpdateTexture(previewSrc, pw, ph);
                        _renderer.Render();
                    }

                    // S-X9g (2026-06-27) — snapshot the preview buffer into
                    // _lastPresentedBuffer so the next stale-upload (e.g.
                    // pan-stop debounce Trigger) re-paints the panned preview
                    // instead of the pre-pan full-res frame held in
                    // _lastUploadedBuffer. Snapshot the RELIEF-applied buffer so
                    // the debounce repaint stays 3D too. Pinned pool grows lazily.
                    if (pn > 0 && previewSrc != null && previewSrc.Length >= pn)
                    {
                        if (_uploadPreviewPool == null || _uploadPreviewPool.Length < pn)
                            _uploadPreviewPool = GC.AllocateUninitializedArray<uint>(pn, pinned: true);
                        Array.Copy(previewSrc, _uploadPreviewPool, pn);
                        _lastPresentedBuffer = _uploadPreviewPool;
                        _lastPresentedWidth = pw;
                        _lastPresentedHeight = ph;
                    }
                    }
                }
                AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);

                int nextStage = job.ProgressiveStage >= 4 ? 2 : 0;
                var nextJob = new FrameJob(
                    job.Token, calc, altCalc: null, sw: job.Sw,
                    staleBuf: null, staleW: 0, staleH: 0,
                    calcW: job.CalcW, calcH: job.CalcH,
                    seq: System.Threading.Interlocked.Increment(ref _uploadSeq),
                    taaSampleIndex: 0, progressiveStage: nextStage);
                try { _calcQueue.Add(nextJob); }
                catch (InvalidOperationException) { /* shutdown */ }
                catch (ArgumentException)
                {
                    while (_calcQueue.TryTake(out _)) { }
                    try { _calcQueue.Add(nextJob); } catch { }
                }
                return;
            }

            {
                if (token.IsCancellationRequested)
                {
                    // Cancelled render still counts as "done" for animation
                    // gating — otherwise a mid-animation cancel would leave
                    // the gate stuck.
                    // S-X8 (2026-06-27) — raise RenderCancelled so the status
                    // bar consumer drops the "Calculating…" set at Trigger
                    // entry. Without this, a cancelled deep-Extreme frame
                    // leaves the status string stuck indefinitely.
                    Dbg86($"FINAL-CANCELLED seq={job.Seq} (token cancelled before present)");
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    RenderCancelled?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (_disposed) return;

                // Adaptive contrast — Mandelbrot only. Build the CDF and
                // cache it so subsequent live-slider / sweep ticks can skip
                // the (serial) histogram build and just re-apply with the new
                // strength. Even at HistogramEq == 0 we build the CDF here so
                // the first slider movement off zero is instant.
                if (!useAlt)
                {
                    if (calc.BuildHistogramCdf(out double[]? cdf, out int bins, out int srcMaxIter))
                    {
                        lock (_adaptiveCdfLock)
                        {
                            _cachedAdaptiveCdf = cdf;
                            _cachedAdaptiveBins = bins;
                            _cachedAdaptiveSourceMaxIter = srcMaxIter;
                            _adaptiveCdfValid = true;
                        }
                        if (ViewState.HistogramEq > 0)
                            calc.ApplyHistogramEqualizationWithCdf(cdf!, bins, srcMaxIter, ViewState.HistogramEq / 100.0);
                    }
                    else if (ViewState.HistogramEq > 0)
                    {
                        // No escaped pixels (e.g. fully in-set) — leave the
                        // ColorBuffer alone; Calculate already coloured it.
                    }
                }
                // #145 — escape-time alt calculators (Julia, BurningShip,
                // Tricorn, Multibrot, Phoenix, Magnet1/2, Glynn, Spider) equalize
                // through the same shared core. Builds + applies inline (no
                // per-calc CDF cache on the alt path; the live slider rebuilds in
                // RepaintWithAdaptive). Non-escape-time alts skip HE entirely.
                else if (ViewState.HistogramEq > 0
                    && altCalc is FracturingFog.Interefaces.ISupportsHistogramEq heAltCalc
                    && heAltCalc.BuildHistogramCdf(out double[]? altCdf, out int altBins, out int altSrc))
                {
                    heAltCalc.ApplyHistogramEqualizationWithCdf(
                        altCdf!, altBins, altSrc, ViewState.HistogramEq / 100.0);
                }

                // #102 — stash the active 2D height field (smooth iteration
                // count) so UploadProcessedBuffer can add heightfield relief on
                // top of the themed colour. Only escape-time 2D calculators
                // expose one; raymarchers / IFS / Apollonian / etc. leave it
                // invalid so relief is skipped for them.
                //
                // Capture a STABLE COPY on the base frame only (TaaSampleIndex 0,
                // final full-res progressive stage). TAA continuation samples
                // jitter the camera sub-pixel and overwrite the calc's
                // SmoothBuffer each pass; without a locked base height the relief
                // gradient + specular recompute on every jittered height and the
                // detail areas visibly sparkle for several seconds. Locking the
                // height keeps the relief pattern fixed while the colour
                // accumulates underneath — no jitter, same settled result.
                if (job.ProgressiveStage <= 1)
                {
                    // Any escape-time 2D calculator exposes its height field via
                    // IHeightFieldSource — Mandelbrot, the EscapeTimeCalculator
                    // family, AND the CalcGen-generated calculators (generated
                    // Tricorn / Burning Ship / MandelbrotZ*). Raymarchers, IFS,
                    // Apollonian etc. don't implement it → relief skipped.
                    float[]? srcH; int hw, hh;
                    if (useAlt)
                    {
                        srcH = (altCalc as Interefaces.IHeightFieldSource)?.SmoothBuffer;
                        hw = altCalc!.Width; hh = altCalc.Height;
                    }
                    else
                    {
                        srcH = (calc as Interefaces.IHeightFieldSource)?.SmoothBuffer;
                        hw = calc.Width; hh = calc.Height;
                    }

                    if (srcH == null)
                    {
                        _reliefValid = false;
                    }
                    else if (job.TaaSampleIndex == 0)
                    {
                        // #143 — for the canonical Mandelbrot path, compute the
                        // height field at a resolution floor (independent of the
                        // window size) so a small window renders the same smooth
                        // terrain as a maximized one. Falls back to the display-res
                        // SmoothBuffer when the hi-res knob is off, the window is
                        // already at/above the floor, or the field render is
                        // cancelled. Alt calcs keep the display-res field.
                        var rp = ViewState.FractalParameters;
                        if (!useAlt && rp.Relief2DEnabled && rp.Relief2DRaymarch
                            && rp.Relief2DHiResField
                            && TryCaptureHiResReliefField(calc, rp, hw, hh, token))
                        {
                            // _reliefHeight / _reliefW / _reliefH / _reliefValid
                            // set inside on success.
                        }
                        else
                        {
                            int hn = hw * hh;
                            if (_reliefHeight == null || _reliefHeight.Length < hn)
                                _reliefHeight = new float[hn];
                            Array.Copy(srcH, _reliefHeight, Math.Min(srcH.Length, hn));
                            _reliefW = hw; _reliefH = hh; _reliefValid = true;
                        }
                    }
                    // TaaSampleIndex > 0: keep the locked base height.
                }

                // #86 — newest-wins: only present this final frame if no newer
                // frame's upload has already reached the screen. A superseded
                // final still runs its bookkeeping below (status label, events,
                // TAA seed) so gates never stick — it just doesn't paint.
                lock (_uploadGate)
                {
                    bool claimedF = TryClaimPresent(job.Seq);
                    Dbg86($"FINAL    seq={job.Seq} claimed={claimedF} lastSeq={_lastPresentedUploadSeq} " +
                          $"hp={calc.IsHighPrecisionActive} gpu={calc.LastFrameUsedGpuPerturbation} {calc.Width}x{calc.Height}");
                    if (claimedF)
                    {
                        if (useAlt)
                            UploadProcessedBuffer(altCalc!.ColorBuffer, altCalc.Width, altCalc.Height);
                        else
                            UploadProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height);
                    }
                }

                // Pull the richer LastPrecisionLabel from the generated calcs
                // (PT, QD-PT, DD-HP4, etc.) so the status bar can show what
                // path actually ran — essential for diagnosing perf at
                // deep zoom. Legacy MandelbrotCalculator exposes only a
                // boolean (IsHighPrecisionActive); collapse to "DD"/"SP"
                // for the status string in that case.
                string? lbl = useAlt
                    ? altCalc switch
                    {
                        FracturingFog.Calculators.Generated.MandelbrotZ2Calculator g2 => g2.LastPrecisionLabel,
                        FracturingFog.Calculators.Generated.MandelbrotZ3Calculator g3 => g3.LastPrecisionLabel,
                        FracturingFog.Calculators.Generated.MandelbrotZ4Calculator g4 => g4.LastPrecisionLabel,
                        FracturingFog.Calculators.Generated.MandelbrotZ5Calculator g5 => g5.LastPrecisionLabel,
                        FracturingFog.Calculators.Generated.TricornCalculator     tc => tc.LastPrecisionLabel,
                        FracturingFog.Calculators.Generated.BurningShipCalculator bs => bs.LastPrecisionLabel,
                        _ => null
                    }
                    : (calc.IsHighPrecisionActive ? "DD" : "SP");
                bool hp = useAlt
                    ? (lbl != null && (lbl.StartsWith("DD") || lbl.StartsWith("QD")))
                    : calc.IsHighPrecisionActive;
                int curW = useAlt ? altCalc!.Width : calc.Width;
                int curH = useAlt ? altCalc!.Height : calc.Height;
                int curIter = useAlt ? altCalc!.MaxIterations : calc.MaxIterations;
                double curCx = useAlt ? altCalc!.CenterX : calc.CenterX;
                double curCy = useAlt ? altCalc!.CenterY : calc.CenterY;
                double curZoom = useAlt ? altCalc!.Zoom : calc.Zoom;

                // S-X8 (2026-06-27) — only the initial sample (TaaSampleIndex
                // == 0) updates the status bar via FrameCompleted. TAA
                // continuation samples carry a cumulative job.Sw elapsed
                // that climbs with every refinement pass, so firing per
                // sample makes the status-bar ms oscillate up and down as
                // overlapping continuations cancel and restart. PerfHud +
                // AnimationFrameUploaded still fire each sample so HUD +
                // animation gating stay accurate.
                if (job.TaaSampleIndex == 0)
                {
                    // Detail-depth estimate is meaningful only for the canonical
                    // Mandelbrot calculator's reference orbit; alt calcs leave +∞.
                    double maxUseful = useAlt ? double.PositiveInfinity
                                              : _calculator.MaxUsefulZoomLog10;
                    FrameCompleted?.Invoke(this, new RenderFrameInfo(
                        curCx, curCy, curZoom, curIter, ms, curW, curH,
                        hp, ViewState.IterLocked, ViewState.FractalType, lbl, maxUseful));
                }

                if (ShowPerfHud) _perfStats.RecordFrame(ms);

                AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);

                // Wave 2.7 — once the user-visible frame is up, queue the next
                // TAA continuation if the view is still settled. Bounded(1)
                // queue means a fresh Trigger from input simply replaces our
                // continuation before it runs. Skipped for alt calcs (TAA
                // currently restricted to the canonical Mandelbrot path).
                if (!useAlt) TryScheduleNextTaaSample(in job);
            }
        }

        // ── Resize ────────────────────────────────────────────────────────────

        // #85 — wait for the calc thread to leave its current job's write
        // region so a caller can safely swap the calculator's buffer arrays.
        // The caller must cancel the in-flight token FIRST; the calc then exits
        // at its next row/stage boundary and sets _calcIdle. Bounded wait keeps
        // a wedged calc from hanging the UI thread — on timeout we fall back to
        // the old cancel-only (racy) behaviour, no worse than before. Runs on
        // the UI thread holding no locks, so it cannot deadlock the calc.
        private void DrainCalc()
        {
            try { _calcIdle.Wait(2000); } catch { }
        }

        public void Resize(int width, int height)
        {
            if (_disposed) return;
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            _lastUploadedBuffer = null;
            // S-X9d — kill stale full-res snapshot on resize too; old buffer
            // is sized for old dims and would fail the stale-upload size gate.
            _lastFullResBuffer = null;
            _lastFullResWidth = 0;
            _lastFullResHeight = 0;
            // S-X9g — the presented-buffer tracker and its scratch pool are
            // both sized for old dims too. Drop them so the next progressive
            // upload alloc-grows fresh.
            _lastPresentedBuffer = null;
            _lastPresentedWidth = 0;
            _lastPresentedHeight = 0;
            _uploadPreviewPool = null;
            _currentTargetWidth = w;
            _currentTargetHeight = h;
            // Buffer dimensions changing → old CDF is sized for old buffers.
            InvalidateAdaptiveCdf();
            // Wave 2.7 — sum arrays sized for old buffers too.
            InvalidateTaa();
            _taaSumR = null; _taaSumG = null; _taaSumB = null; _taaSumA = null;
            _taaSumPixels = 0;

            lock (_d3dGate)
            {
                _renderer.Resize(w, h);
                // Present once so the new back-buffer dimensions become
                // visible immediately even before the next calc finishes.
                _renderer.Render();
            }
            // Cancel any in-flight calc BEFORE reallocating its buffers below —
            // _calculator.Resize swaps IterationBuffer/aux arrays, and a calc
            // thread mid-write (CPU rows OR the GPU-perturbation readback +
            // FillAuxAndColorHP pass) would tear against the new arrays. The old
            // frame is for the old dims and about to be superseded anyway, so
            // cancelling it is strictly correct. Cooperative cancellation
            // narrows the window; DrainCalc then closes it by waiting for the
            // calc thread to actually leave its write region before the swap.
            lock (_calcLock) _calcCts?.Cancel();
            DrainCalc();
            _calculator.Resize(w, h);
            // Wave 2.5 — keep progressive sidecars in sync with main surface.
            int qw = Math.Max(64, w / 4); int qh = Math.Max(64, h / 4);
            int hw = Math.Max(64, w / 2); int hh = Math.Max(64, h / 2);
            _previewCalcQuarter.Resize(qw, qh);
            _previewCalcHalf.Resize(hw, hh);
            _escapeCalculator.Resize(w, h);
            _ifsCalculator.Resize(w, h);
            _lsystemCalculator.Resize(w, h);
            _attractorCalculator.Resize(w, h);
            _buddhabrotCalculator.Resize(w, h);
            _logisticCalculator.Resize(w, h);
            _halleyCalculator.Resize(w, h);
            _secantCalculator.Resize(w, h);
            _nebulabrotCalculator.Resize(w, h);
            _antiBuddhabrotCalculator.Resize(w, h);
            _antiNebulabrotCalculator.Resize(w, h);
            _newtonCalculator.Resize(w, h);
            _userEquationCalculator.Resize(w, h);
            _mandelbulbCalculator.Resize(w, h);
            _mandelboxCalculator.Resize(w, h);
            _kifsCalculator.Resize(w, h);
            _quatJuliaCalculator.Resize(w, h);
            _quatMandelbrotCalculator.Resize(w, h);
            _plasmaCalculator.Resize(w, h);
            _apollonianCalculator.Resize(w, h);
            _kleinianCalculator.Resize(w, h);
            _bicomplexCalculator.Resize(w, h);
            _dlaCalculator.Resize(w, h);
            _flameCalculator.Resize(w, h);
            _sandboxCalculator.Resize(w, h);
            _userBulbCalculator.Resize(w, h);
            _tearDropCalculator.Resize(w, h);
            _generatedZ2Calculator.Resize(w, h);
            _generatedZ3Calculator.Resize(w, h);
            _generatedZ4Calculator.Resize(w, h);
            _generatedZ5Calculator.Resize(w, h);
            _generatedTricornCalculator.Resize(w, h);
            _generatedBurningShipCalculator.Resize(w, h);

            ApplyView();
            Trigger();
        }

        // ── Repaint ───────────────────────────────────────────────────────────

        public void RepaintWithPostFx()
        {
            if (_disposed) return;
            IFractalCalculator? alt = SelectAltCalculator(ViewState.FractalType);
            if (alt != null)
                UploadProcessedBuffer(alt.ColorBuffer, alt.Width, alt.Height);
            else
                UploadProcessedBuffer(_calculator.ColorBuffer, _calculator.Width, _calculator.Height);
        }

        /// <summary>Set (or clear with null) the rubber-band rectangle drawn
        /// on top of the current frame while the user is right-drag-selecting
        /// a zoom region in 2D. Re-uploads the most recently completed frame
        /// with the rect composited on top — no recompute, so the preview
        /// stays smooth during the drag.</summary>
        public void SetSelectionBox(int? x, int? y, int? w, int? h)
        {
            if (_disposed) return;
            if (x is null || y is null || w is null || h is null)
                _selectionBox = null;
            else
                _selectionBox = (x.Value, y.Value, w.Value, h.Value);
            // S-X9e (2026-06-27) — composite over the last-known-good frame,
            // not over calc.ColorBuffer. At deep zoom the active calc holds
            // a partial buffer (mid-render rows / cancelled state); reading
            // it for the selection-box repaint stamped partial-render
            // artifacts that compounded as the user dragged. Prefer the
            // cached full-res snapshot; fall back to RepaintWithPostFx when
            // we don't have one yet (first-frame edge case).
            RepaintWithSelectionBox();
        }

        private void RepaintWithSelectionBox()
        {
            uint[]? srcBuf;
            int srcW, srcH;
            lock (_uploadGate)
            {
                if (_lastFullResBuffer != null
                    && _lastFullResWidth == _currentTargetWidth
                    && _lastFullResHeight == _currentTargetHeight)
                {
                    srcBuf = _lastFullResBuffer;
                    srcW = _lastFullResWidth;
                    srcH = _lastFullResHeight;
                }
                else
                {
                    srcBuf = null;
                    srcW = srcH = 0;
                }
            }
            if (srcBuf != null)
                // srcBuf is _lastFullResBuffer — a snapshot with post-FX
                // already baked in. Flag it so UploadProcessedBuffer does NOT
                // re-apply brightness/contrast/gamma (which would compound on
                // every selection-box repaint, e.g. repeated right-clicks).
                UploadProcessedBuffer(srcBuf, srcW, srcH, srcAlreadyProcessed: true);
            else
                RepaintWithPostFx();
        }

        /// <summary>
        /// Re-apply Adaptive (histogram equalization) at the current
        /// <see cref="FractalViewState.HistogramEq"/> strength using the
        /// cached SmoothBuffer/IterationBuffer — no recompute — then upload.
        /// Mandelbrot only; alt calculators fall through to
        /// <see cref="RepaintWithPostFx"/>. Used by the live Adaptive slider
        /// so it updates the image with the same latency as Brightness /
        /// Contrast instead of triggering a full Calculate().
        /// </summary>
        public void RepaintWithAdaptive()
        {
            if (_disposed) return;
            IFractalCalculator? alt = SelectAltCalculator(ViewState.FractalType);
            if (alt != null)
            {
                // #145 — escape-time alt calculators recolor live from their
                // cached smooth buffers at the new strength (strength 0 recolors
                // to the plain linear mapping). No recompute; builds the CDF each
                // tick (no per-calc CDF cache on the alt path). Non-escape-time
                // alts have no HE — just re-upload.
                if (alt is FracturingFog.Interefaces.ISupportsHistogramEq heAlt)
                {
                    heAlt.ApplyHistogramEqualization(
                        Math.Clamp(ViewState.HistogramEq / 100.0, 0.0, 1.0));
                    UploadProcessedBuffer(alt.ColorBuffer, alt.Width, alt.Height);
                }
                else
                {
                    RepaintWithPostFx();
                }
                return;
            }

            double strength = Math.Clamp(ViewState.HistogramEq / 100.0, 0.0, 1.0);
            if (strength > 0.0)
            {
                // Fast path: a CDF was cached at the end of the last
                // Calculate. Sweep / live-drag only changes `strength`, not
                // the pixel data, so we skip the serial histogram build and
                // the per-tick array allocations and just re-apply with the
                // current strength. This is what makes the adaptive sweep
                // smooth at deep zoom — previously every tick paid for a
                // fresh build that competed with the calc threadpool.
                double[]? cdfSnap;
                int binsSnap, srcMaxIterSnap;
                bool valid;
                lock (_adaptiveCdfLock)
                {
                    valid = _adaptiveCdfValid;
                    cdfSnap = _cachedAdaptiveCdf;
                    binsSnap = _cachedAdaptiveBins;
                    srcMaxIterSnap = _cachedAdaptiveSourceMaxIter;
                }
                if (valid && cdfSnap != null)
                {
                    _calculator.ApplyHistogramEqualizationWithCdf(cdfSnap, binsSnap, srcMaxIterSnap, strength);
                }
                else
                {
                    // First tick after Calculate (or buffers still fresh from
                    // pre-cache codepaths): pay for one build, then cache it.
                    if (_calculator.BuildHistogramCdf(out double[]? cdf, out int bins, out int srcMaxIter))
                    {
                        lock (_adaptiveCdfLock)
                        {
                            _cachedAdaptiveCdf = cdf;
                            _cachedAdaptiveBins = bins;
                            _cachedAdaptiveSourceMaxIter = srcMaxIter;
                            _adaptiveCdfValid = true;
                        }
                        _calculator.ApplyHistogramEqualizationWithCdf(cdf!, bins, srcMaxIter, strength);
                    }
                    else
                    {
                        _calculator.ApplyBandDitherRecolor(0.0);
                    }
                }
            }
            else
            {
                _calculator.ApplyBandDitherRecolor(0.0);
            }
            UploadProcessedBuffer(_calculator.ColorBuffer, _calculator.Width, _calculator.Height);
        }

        /// <summary>
        /// Replace the active colour map and re-colourise the CURRENT frame so the
        /// change is visible immediately. Mandelbrot takes the cheap path
        /// (RecolorFromBuffers via <c>ApplyBandDitherRecolor(0)</c> — no recompute);
        /// alt calculators have no cheap recolor, so they fall back to a full
        /// <see cref="Trigger"/>. Replaces the old "set ColorMap + RepaintWithPostFx"
        /// path that re-uploaded the stale (old-map) buffer, so themes only took
        /// effect on the next pan/zoom.
        /// </summary>
        public void ApplyColorMap(IColorMap map)
        {
            if (_disposed || map == null) return;
            ColorMap = map; // propagate to every calculator + raise ColorMapChanged

            // The cheap Mandelbrot recolor path reads only the cached
            // Smooth/Distance/Iter/Normal/FinalZ/Final-dz buffers. Themes that
            // pull data the cached buffers don't carry can't be drawn from
            // them — they need a fresh Calculate:
            //   • IOrbitAwareColorMap     — needs per-iteration z samples
            //     (orbit traps, stripe / TIA, Lyapunov, Gaussian-integer,
            //     curvature, exponential smoothing).
            //   • IInteriorAwareColorMap  — needs Brent cycle-detection pass
            //     (Cycle Period, Multiplier |λ|, Atom Domains, Interior
            //     Argument, Fake DE).
            //   • IPostProcessColorMap    — needs the post-pass over the full
            //     framebuffer (Emboss Pump, Ambient Occlusion, Soft Shadow,
            //     Entropy themes); a recolor would skip it.
            // Without this, picking one of those themes only takes effect on
            // the next pan/zoom (visible bug: image stays the previous theme
            // until the user nudges the view).
            bool needsFullRender =
                map is IOrbitAwareColorMap     ||
                map is IInteriorAwareColorMap  ||
                map is IPostProcessColorMap;

            IFractalCalculator? altCalc = SelectAltCalculator(ViewState.FractalType);

            if (!needsFullRender && altCalc == null)
            {
                // Mandelbrot fast path — recolour from cached buffers. This is
                // cheap (keeps a slider drag responsive) but skips the MSAA /
                // TAA / SSAO / histogram-eq passes a full render runs, so band
                // edges show un-anti-aliased speckle. Arm the settle debounce:
                // when edits stop, ColorSettleTick fires a full Trigger() so the
                // final frame matches post-navigate quality (#96 follow-up).
                _calculator.ApplyBandDitherRecolor(0.0);
                UploadProcessedBuffer(_calculator.ColorBuffer, _calculator.Width, _calculator.Height);
                ArmColorSettle();
            }
            else if (!needsFullRender && altCalc is ISupportsCheapRecolor recolorAlt)
            {
                // #194 — alt calculator with a cheap recolor path (Buddhabrot
                // family): recomposite from cached intermediates instead of
                // re-running Calculate(). For a Monte Carlo density plot the
                // sample pass dominates, so this turns a theme change from a
                // full re-sample (seconds) into a composite (milliseconds). The
                // recolour IS the same composite a full render would produce, so
                // no settle debounce is needed. ColorMap was already propagated
                // to the alt calculator above.
                DisarmColorSettle();
                recolorAlt.Recolor();
                UploadProcessedBuffer(altCalc.ColorBuffer, altCalc.Width, altCalc.Height);
            }
            else
            {
                // Alt calculator with no cheap path OR theme needs data not in
                // the cached buffers: this already IS the full render, so cancel
                // any pending settle.
                DisarmColorSettle();
                Trigger();
            }
        }

        /// <summary>
        /// (Re)start the color-settle debounce. Each cheap recolor pushes the
        /// full-render fire-time out by <see cref="ColorSettleDelayMs"/>, so a
        /// burst of live editor edits collapses into one full Trigger() after
        /// the user goes quiet.
        /// </summary>
        private void ArmColorSettle()
        {
            if (_disposed) return;
            try
            {
                _colorSettleTimer?.Change(
                    ColorSettleDelayMs, System.Threading.Timeout.Infinite);
            }
            catch (ObjectDisposedException) { /* racing Dispose — ignore. */ }
        }

        /// <summary>Cancel a pending color-settle full render (edits took the
        /// full-render branch, or the host is tearing down).</summary>
        private void DisarmColorSettle()
        {
            try
            {
                _colorSettleTimer?.Change(
                    System.Threading.Timeout.Infinite,
                    System.Threading.Timeout.Infinite);
            }
            catch (ObjectDisposedException) { /* racing Dispose — ignore. */ }
        }

        private void ColorSettleTick(object? _)
        {
            if (_disposed) return;
            // Edits have gone quiet — promote the cheap recolor to a full,
            // quality-complete render (MSAA / TAA / SSAO / histogram-eq).
            try { Trigger(); }
            catch (ObjectDisposedException) { /* racing Dispose — ignore. */ }
        }

        /// <summary>
        /// Set the active colour map and recolour the current frame into a
        /// returned BGRA copy WITHOUT presenting. Mandelbrot fast path only —
        /// returns null for alt calculators (no cheap recolor) so the caller can
        /// fall back to a hard cut. The live colour map is updated so the
        /// post-fade state is consistent. Used by the slideshow theme cross-fade.
        /// </summary>
        public uint[]? RecolorActiveToBuffer(IColorMap map)
            => RecolorActiveToBuffer(map, _currentTargetWidth, _currentTargetHeight);

        /// <summary>
        /// Overload that pins the recolor to caller-supplied dimensions.
        /// Used by the slideshow theme cross-fade: the engine snapshots the
        /// live frame at <c>(w, h)</c>, and the returned buffer must match
        /// those dims for <c>FadeAsync</c> to interpolate against the
        /// snapshot. Mismatched lengths skipped the fade entirely (hard
        /// cut) — passing the snapshot dims here forces consistency.
        /// </summary>
        public uint[]? RecolorActiveToBuffer(IColorMap map, int w, int h)
        {
            if (_disposed || map == null) return null;
            if (w <= 0 || h <= 0) return null;
            ColorMap = map;
            IFractalCalculator? alt = SelectAltCalculator(ViewState.FractalType);
            // Same gate as ApplyColorMap: themes that need data the cached
            // buffers don't carry (orbit/interior/post-process) can't be drawn
            // by the cheap Mandelbrot recolor — they need a fresh Calculate.
            bool needsFullRender =
                map is IOrbitAwareColorMap     ||
                map is IInteriorAwareColorMap  ||
                map is IPostProcessColorMap;
            if (alt != null)
            {
                // Alt calculators have no cheap recolor — recompute into the
                // alt's ColorBuffer with the new map. Live state already
                // reflects the new ColorMap; this fills the post-fade target
                // so the caller can cross-fade against the snapshot of the
                // old frame.
                //
                // Cancel any queued or in-flight ordinary calc first so it
                // can't race our synchronous Calculate on the same alt
                // instance (mirrors RenderRegionToBuffer). And force alt
                // back to the current surface dims — without this, a race
                // with a still-in-flight calc that resized the buffer mid-
                // recolor produces a tiny altCopy. The slideshow then runs
                // RepaintWithPostFx at the alt's (now-stale) Width/Height,
                // uploads a sub-surface buffer, the swap chain stretches
                // it 2-8× to fill, and the watermark — drawn at fixed
                // 14pt into that small buffer — appears huge on screen.
                lock (_calcLock) _calcCts?.Cancel();
                while (_calcQueue.TryTake(out _)) { }

                if (alt.Width != w || alt.Height != h) alt.Resize(w, h);

                SyncAltStateFromMandel(alt);
                alt.Calculate(System.Threading.CancellationToken.None);
                // #145 — bake Adaptive HE into the alt cross-fade target too, so
                // an escape-time fractal's slideshow theme fade interpolates
                // between two HE-applied buffers (mirrors the Mandelbrot bake
                // below). Non-escape-time alts skip it.
                if (ViewState.HistogramEq > 0
                    && alt is FracturingFog.Interefaces.ISupportsHistogramEq heAlt)
                    heAlt.ApplyHistogramEqualization(ViewState.HistogramEq / 100.0);
                var altSrc = alt.ColorBuffer;
                int n = w * h;
                var altCopy = new uint[n];
                Array.Copy(altSrc, altCopy, Math.Min(altSrc.Length, n));
                return ApplyCachedRelief(altCopy, w, h);
            }
            if (needsFullRender)
            {
                if (_calculator.Width != w || _calculator.Height != h) _calculator.Resize(w, h);
                _calculator.Calculate(System.Threading.CancellationToken.None);
            }
            else
            {
                _calculator.ApplyBandDitherRecolor(0.0);
            }

            // Wave 3.7 — bake Adaptive HE into the recolor target so the
            // slideshow cross-fade interpolates between two HE-applied
            // buffers (snapshot source has HE; without this the target was
            // pre-HE and the fade ended in a pre-HE state that the post-
            // fade `RepaintWithPostFx` then snapped onto, producing the
            // visible "HE pops on" jump at fade end). Mirrors the calc-
            // completion HE step at the top of UploadCompletedFrame.
            if (ViewState.HistogramEq > 0
                && _calculator.BuildHistogramCdf(out double[]? cdf, out int bins, out int srcMaxIter))
            {
                _calculator.ApplyHistogramEqualizationWithCdf(
                    cdf!, bins, srcMaxIter, ViewState.HistogramEq / 100.0);
            }

            var src = _calculator.ColorBuffer;
            int mn = w * h;
            var copy = new uint[mn];
            Array.Copy(src, copy, Math.Min(src.Length, mn));
            return ApplyCachedRelief(copy, w, h);
        }

        // Reapply the cached heightfield relief to an off-screen recolor buffer
        // (slideshow theme cross-fade target). The live upload path adds relief
        // in UploadProcessedBuffer; standalone buffers returned to the slideshow
        // skipped it, so a theme fade WITHIN a relief region blended a FLAT
        // recolor and popped the 3D relief back on commit. Uses the stable height
        // field captured on the region's base frame (_reliefHeight) — the view is
        // unchanged across a theme-only fade, so the cached field still matches.
        // CPU raymarch oracle (the GPU relief kernel is thread-affine to the calc
        // thread; this runs on the slideshow's background thread → pass null).
        // Returns the input unchanged when relief is off or the field mis-fits.
        private uint[] ApplyCachedRelief(uint[] buf, int w, int h)
        {
            var rp = ViewState.FractalParameters;
            if (!rp.Relief2DEnabled || !_reliefValid || _reliefHeight == null) return buf;
            int n = w * h;
            if (buf.Length < n) return buf;
            bool raymarch = rp.Relief2DRaymarch;
            // Mirror UploadProcessedBuffer's field-fit gate: the raymarch samples
            // the field by normalised coords (field may be hi-res, dims _reliefW×
            // _reliefH), the Phase 1 hillshade is screen-space (field must match w×h).
            bool fieldOk = raymarch
                ? (_reliefW > 0 && _reliefH > 0 && _reliefHeight.Length >= _reliefW * _reliefH)
                : (_reliefW == w && _reliefH == h && _reliefHeight.Length >= n);
            if (!fieldOk) return buf;
            var dst = new uint[n];
            if (raymarch)
                FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.Render(
                    buf, _reliefHeight, w, h, _reliefW, _reliefH, rp, dst, out _, null);
            else
                FracturingFog.Rendering.Lighting.HeightfieldRelief2D.Apply(
                    buf, dst, _reliefHeight, w, h, rp);
            return dst;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        /// <summary>Install a hot-loaded calculator. While set it overrides
        /// the static UserEquation slot only — switching the active
        /// FractalType to anything else still routes through
        /// SelectAltCalculatorByType, so the user can leave the hot-loaded
        /// equation and come back to it. Pass null to clear and revert to
        /// the static alt-calc table. Caller takes the lifetime — the host
        /// doesn't dispose what it doesn't own.</summary>
        public void SetDynamicAltCalculator(IFractalCalculator? alt)
        {
            _dynamicAltCalculator = alt;
            if (alt != null)
            {
                alt.Resize(_calculator.Width, _calculator.Height);
                alt.ColorMap = _calculator.ColorMap;
            }
            Trigger();
        }

        // Common alt-calc state sync: pull centre/zoom/iter/quality/colormap
        // from the live Mandelbrot calculator and the per-engine FractalParameters
        // off ViewState onto the given alt. Used by the main render path and by
        // RecolorActiveToBuffer (slideshow theme cross-fade). Generated calc
        // tail (CenterXLo, UsePerturbation, UseBla, etc.) is set by the caller.
        private void SyncAltStateFromMandel(IFractalCalculator alt)
        {
            var calc = _calculator;
            alt.CenterX       = calc.CenterX;
            alt.CenterY       = calc.CenterY;
            alt.Zoom          = calc.Zoom;
            alt.MaxIterations = calc.MaxIterations;
            alt.Quality       = calc.Quality;
            alt.ColorMap      = calc.ColorMap;
            switch (alt)
            {
                case EscapeTimeCalculator e:
                    e.FractalType = ViewState.FractalType;
                    e.FractalParameters = ViewState.FractalParameters;
                    break;
                case IFSCalculator ifs: ifs.FractalParameters = ViewState.FractalParameters; break;
                case LSystemCalculator ls: ls.FractalParameters = ViewState.FractalParameters; break;
                case AttractorCalculator a: a.FractalParameters = ViewState.FractalParameters; break;
                case BuddhaFamilyCalculator b: b.FractalParameters = ViewState.FractalParameters; break;
                case NewtonCalculator n: n.FractalParameters = ViewState.FractalParameters; break;
                case UserEquationCalculator u:
                    u.FractalParameters = ViewState.FractalParameters;
                    // Plumb the centre's low limbs so deep-zoom pan / box /
                    // double-click recenter anchors to the right pixel. The
                    // base sync above only copies the Hi limb; at zoom >
                    // ~1e15 one Hi ULP is ~100 pixels so the rendered view
                    // disagrees with where the input controller anchored.
                    u.CenterXLo = calc.CenterXLo;
                    u.CenterX2  = calc.CenterX2;
                    u.CenterX3  = calc.CenterX3;
                    u.CenterYLo = calc.CenterYLo;
                    u.CenterY2  = calc.CenterY2;
                    u.CenterY3  = calc.CenterY3;
                    break;
                case MandelbulbCalculator m: m.FractalParameters = ViewState.FractalParameters; break;
                case MandelboxCalculator mb: mb.FractalParameters = ViewState.FractalParameters; break;
                case KifsCalculator kf: kf.FractalParameters = ViewState.FractalParameters; break;
                case QuatJuliaCalculator qj: qj.FractalParameters = ViewState.FractalParameters; break;
                case QuatMandelbrotCalculator qm: qm.FractalParameters = ViewState.FractalParameters; break;
                case PlasmaCalculator pl: pl.FractalParameters = ViewState.FractalParameters; break;
                case AcidWarpCalculator aw: aw.FractalParameters = ViewState.FractalParameters; break;
                case ApollonianCalculator ap: ap.FractalParameters = ViewState.FractalParameters; break;
                case KleinianCalculator kl: kl.FractalParameters = ViewState.FractalParameters; break;
                case BicomplexMandelbrotCalculator bc: bc.FractalParameters = ViewState.FractalParameters; break;
                case DlaCalculator dl: dl.FractalParameters = ViewState.FractalParameters; break;
                case FlameRenderer fr: fr.FractalParameters = ViewState.FractalParameters; break;
                case SandboxCalculator sb: sb.FractalParameters = ViewState.FractalParameters; break;
                case UserBulbCalculator ub: ub.FractalParameters = ViewState.FractalParameters; break;
                case LogisticCalculator lg: lg.FractalParameters = ViewState.FractalParameters; break;
                case HalleyCalculator hc: hc.FractalParameters = ViewState.FractalParameters; break;
                case SecantCalculator sc: sc.FractalParameters = ViewState.FractalParameters; break;
            }
        }

        /// <summary>Render <paramref name="region"/> into an off-screen colour
        /// buffer at the given size using the live alt-calculator fleet. Used
        /// by the slideshow cross-fade so non-Mandelbrot region transitions
        /// get a real fade instead of a hard cut.
        ///
        /// Caller must have already applied the region into <see cref="ViewState"/>
        /// (via <c>IColorThemeService.ApplyRegion</c>) so source-compiled
        /// types (UserEquation / Sandbox / UserBulb) are compiled and the
        /// per-engine <c>FractalParameters</c> are populated.
        ///
        /// Cancels any in-flight calc, configures the appropriate alt calc
        /// (Resize + SyncAlt + theme), runs Calculate synchronously on the
        /// caller's thread, and returns a copy of <c>ColorBuffer</c>. Returns
        /// null for <see cref="FractalType.Mandelbrot"/> (caller renders that
        /// path with the standalone MandelbrotCalculator) or when no alt calc
        /// is registered for the type.</summary>
        public uint[]? RenderRegionToBuffer(FractalRegion region, IColorMap? map, int w, int h)
        {
            if (_disposed || region == null || w <= 0 || h <= 0) return null;
            if (region.FractalType == FractalType.Mandelbrot) return null;

            var alt = SelectAltCalculator(region.FractalType);
            if (alt == null) return null;

            // Push region centre/zoom/iter/quality + theme into the primary
            // Mandelbrot calc so SyncAltStateFromMandel picks the right values
            // (it copies from _calculator + ViewState).
            _calculator.CenterX  = region.CenterX;  _calculator.CenterXLo = region.CenterXLo;
            _calculator.CenterX2 = region.CenterX2; _calculator.CenterX3  = region.CenterX3;
            _calculator.CenterY  = region.CenterY;  _calculator.CenterYLo = region.CenterYLo;
            _calculator.CenterY2 = region.CenterY2; _calculator.CenterY3  = region.CenterY3;
            if (region.Zoom > 0) _calculator.Zoom = region.Zoom;
            var quality = region.QualityPreset ?? QualityPreset.Standard;
            _calculator.Quality = quality;
            _calculator.MaxIterations = region.Iterations > 0
                ? region.Iterations
                : quality.ComputeIterations(_calculator.Zoom);
            if (map != null) _calculator.ColorMap = map;

            // Cancel any queued or in-flight ordinary calc so it can't race
            // our synchronous Calculate on the same alt instance. Mirrors the
            // pattern VideoLoop uses for its per-frame Calculate.
            lock (_calcLock) _calcCts?.Cancel();
            while (_calcQueue.TryTake(out _)) { }

            try
            {
                if (alt.Width != w || alt.Height != h) alt.Resize(w, h);
                SyncAltStateFromMandel(alt);
                alt.Calculate(CancellationToken.None);

                var src = alt.ColorBuffer;
                if (src == null || src.Length == 0) return null;
                int n = w * h;
                var copy = new uint[n];
                Array.Copy(src, copy, Math.Min(src.Length, n));
                return copy;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>#138 — expose the active 2D calculator's height field + flat
        /// albedo for heightfield mesh export. Returns false when the active
        /// fractal doesn't provide an <see cref="Interefaces.IHeightFieldSource"/>
        /// (3D raymarchers, IFS, etc.). Buffers are the calculator's live arrays;
        /// the caller must copy/consume on the same frame.</summary>
        public bool TryGetHeightFieldExport(out uint[] albedo, out float[] height,
                                            out int w, out int h)
        {
            albedo = Array.Empty<uint>(); height = Array.Empty<float>(); w = 0; h = 0;
            Interefaces.IHeightFieldSource? hs;
            uint[] colBuf; int aw, ah;
            var alt = SelectAltCalculator(ViewState.FractalType);
            if (alt != null)
            {
                hs = alt as Interefaces.IHeightFieldSource;
                colBuf = alt.ColorBuffer; aw = alt.Width; ah = alt.Height;
            }
            else
            {
                hs = _calculator as Interefaces.IHeightFieldSource;
                colBuf = _calculator.ColorBuffer; aw = _calculator.Width; ah = _calculator.Height;
            }
            if (hs == null) return false;
            var sm = hs.SmoothBuffer;
            int n = aw * ah;
            if (n <= 0 || sm.Length < n || colBuf.Length < n) return false;
            albedo = colBuf; height = sm; w = aw; h = ah;
            return true;
        }

        private IFractalCalculator? SelectAltCalculator(FractalType type)
        {
            // Dynamic hot-load slot is bound to UserEquation semantically — the
            // Compile & Load button on the UserEquation dialog produces this
            // calculator. Honouring it for every FractalType would lock the
            // dropdown on the user equation until app close.
            if (_dynamicAltCalculator != null && type == FractalType.UserEquation)
                return _dynamicAltCalculator;
            return SelectAltCalculatorByType(type);
        }

        private IFractalCalculator? SelectAltCalculatorByType(FractalType type) => type switch
        {
            FractalType.Mandelbrot => null,
            FractalType.Julia => _escapeCalculator,
            FractalType.BurningShip => _escapeCalculator,
            FractalType.Tricorn => _escapeCalculator,
            FractalType.Multibrot => _escapeCalculator,
            FractalType.Phoenix => _escapeCalculator,
            FractalType.Magnet1 => _escapeCalculator,
            FractalType.Magnet2 => _escapeCalculator,
            FractalType.Glynn => _escapeCalculator,
            FractalType.Spider => _escapeCalculator,
            FractalType.Logistic => _logisticCalculator,
            FractalType.Halley => _halleyCalculator,
            FractalType.Secant => _secantCalculator,
            FractalType.IFS => _ifsCalculator,
            FractalType.LSystem => _lsystemCalculator,
            FractalType.StrangeAttractor => _attractorCalculator,
            FractalType.BuddhaBrot => _buddhabrotCalculator,
            FractalType.Nebulabrot => _nebulabrotCalculator,
            FractalType.AntiBuddhabrot => _antiBuddhabrotCalculator,
            FractalType.AntiNebulabrot => _antiNebulabrotCalculator,
            FractalType.Newton => _newtonCalculator,
            FractalType.Nova => _newtonCalculator,
            FractalType.UserEquation => _userEquationCalculator,
            FractalType.Mandelbulb => _mandelbulbCalculator,
            FractalType.Mandelbox => _mandelboxCalculator,
            FractalType.Kifs => _kifsCalculator,
            FractalType.QuaternionJulia => _quatJuliaCalculator,
            FractalType.QuaternionMandelbrot => _quatMandelbrotCalculator,
            FractalType.Plasma => _plasmaCalculator,
            FractalType.AcidWarp => _acidWarpCalculator,
            FractalType.Apollonian => _apollonianCalculator,
            FractalType.Kleinian => _kleinianCalculator,
            FractalType.BicomplexMandelbrot => _bicomplexCalculator,
            FractalType.Dla => _dlaCalculator,
            FractalType.Flame => _flameCalculator,
            FractalType.Sandbox => _sandboxCalculator,
            FractalType.UserBulb => _userBulbCalculator,
            FractalType.TearDrop => _tearDropCalculator,
            FractalType.GeneratedMandelbrotZ2 => _generatedZ2Calculator,
            FractalType.GeneratedMandelbrotZ3 => _generatedZ3Calculator,
            FractalType.GeneratedMandelbrotZ4 => _generatedZ4Calculator,
            FractalType.GeneratedMandelbrotZ5 => _generatedZ5Calculator,
            FractalType.GeneratedTricorn      => _generatedTricornCalculator,
            FractalType.GeneratedBurningShip  => _generatedBurningShipCalculator,
            _ => null
        };

        /// <summary>Pure CPU brightness + contrast pass over a BGRA uint[]
        /// followed by an upload to the renderer. Grid + watermark overlays
        /// are intentionally omitted in this host — they will be drawn by
        /// the Avalonia shell with Avalonia.Media in step F.</summary>
        // S-X9 (2026-06-27) — leak diagnostic. Set FF_LEAK_DEBUG=1 to log
        // managed-heap + working-set deltas per upload frame so we can tell
        // whether the user-reported climb is .NET-side (managed bytes climb,
        // chase allocations) or native-side (WS climbs but managed stays
        // flat, chase Mesa / Avalonia / Skia). Logs every N frames to keep
        // output legible; N defaults to 30 (≈1 s at 30 FPS) but overridable
        // via FF_LEAK_DEBUG_EVERY=<n>.
        //
        // S-X9b (2026-06-27) — first probe revealed two issues:
        //   1. Baseline at f=0 fired during the ctor's 1×1 dummy Render()
        //      before the 20-calc pool warms, making the +812 MB Linux delta
        //      mostly warm-up artifact (20 calcs × 1280×733 × ~40 B/px ≈
        //      750 MB). Skip baseline until the first frame at real surface
        //      dims (W > 64 && H > 64).
        //   2. GC.GetTotalMemory(forceFullCollection: false) reports the
        //      live heap including not-yet-collected gen-0 garbage, so what
        //      looks like a leak may be transient churn the GC just hasn't
        //      reaped yet. FF_LEAK_DEBUG_FORCEGC=1 adds a forced full-
        //      collection sample so retained-vs-transient is separable. Off
        //      by default — forcing gen-2 blocks every worker thread for tens
        //      of ms and skews the workload.
        private static readonly bool s_leakDiag =
            string.Equals(Environment.GetEnvironmentVariable("FF_LEAK_DEBUG"), "1", StringComparison.Ordinal);
        private static readonly int s_leakDiagEvery =
            int.TryParse(Environment.GetEnvironmentVariable("FF_LEAK_DEBUG_EVERY"), out var __n) && __n > 0 ? __n : 30;
        private static readonly bool s_leakDiagForceGc =
            string.Equals(Environment.GetEnvironmentVariable("FF_LEAK_DEBUG_FORCEGC"), "1", StringComparison.Ordinal);
        private long _leakDiagFrame;
        private long _leakDiagBaselineManagedBytes;
        private long _leakDiagBaselineRetainedBytes;
        private long _leakDiagBaselineWorkingSet;
        private int _leakDiagBaselineGen0, _leakDiagBaselineGen1, _leakDiagBaselineGen2;
        private bool _leakDiagBaselineTaken;

        // srcAlreadyProcessed: true when the caller hands us a buffer that has
        // ALREADY had brightness/contrast/gamma baked in (the _lastFullResBuffer
        // snapshot). Re-applying the post-FX pass to it would compound the
        // adjustment on every call — the exact bug behind "right-click darkens
        // the image, progressively darker each click": a plain right-click
        // fires two selection-box repaints (set + clear), each re-uploading the
        // already-processed snapshot, and each re-darkening it because we then
        // write the result back into that same snapshot below.
        // #102 — locked 2D height field (smooth iteration count) for the
        // heightfield-relief post-pass. A STABLE COPY captured on the base frame
        // (see the stash at calc completion), reused across TAA continuation
        // uploads so relief doesn't sparkle. _reliefValid is false when the
        // active fractal isn't an escape-time 2D type.
        private float[]? _reliefHeight;
        private bool _reliefValid;
        private int _reliefW, _reliefH;
        private uint[]? _reliefColorScratch;
        private uint[]? _reliefPreviewScratch;   // #131 — relief applied to the pan preview
        // #143 — dedicated hi-res relief FIELD calculator. When the oblique
        // raymarch is active and the display is smaller than the field floor, the
        // smooth-count height field is computed here at a resolution floor
        // (short axis Relief2DFieldFloor, aspect preserved) instead of reusing the
        // display-resolution SmoothBuffer, so relief quality is decoupled from
        // window size (small windows no longer collapse the boundary into spiky
        // needles). Mandelbrot path only; lazily sized on the calc thread.
        private MandelbrotCalculator? _reliefFieldCalc;

        private void UploadProcessedBuffer(uint[] src, int w, int h, bool srcAlreadyProcessed = false)
        {
            int n = w * h;
            long uploadStart = ShowPerfHud ? Stopwatch.GetTimestamp() : 0;
            if (s_leakDiag) LeakDiagSample(w, h);
            lock (_uploadGate)
            {

            // Reuse the pooled scratch buffer instead of allocating a fresh
            // uint[n] every call. At 1080p this is an 8 MB allocation that
            // used to happen on every adaptive-slider tick. Pinned LOH so
            // the buffer is removed from GC scan and the GPU upload path
            // does not need a per-frame GCHandle.Alloc.
            if (_uploadDstPool == null || _uploadDstPool.Length < n)
                _uploadDstPool = GC.AllocateUninitializedArray<uint>(n, pinned: true);
            var dst = _uploadDstPool;

            // #102 — heightfield relief: modulate the themed colour with real
            // raised relief + cast shadows before the brightness/contrast pass.
            // Gated to fresh (unprocessed) escape-time 2D frames whose height
            // buffer matches these dims; snapshots (srcAlreadyProcessed) already
            // carry relief. Writes to a scratch so the calculator's ColorBuffer
            // stays flat (idempotent across re-uploads).
            bool reliefRaymarchApplied = false;
            {
                var reliefParams = ViewState.FractalParameters;
                bool raymarch = reliefParams.Relief2DRaymarch;
                // #143 — the Phase 2 raymarch samples the field by normalised
                // coords, so the height field may be a HIGHER resolution than the
                // output/albedo grid (hi-res field decoupled from window size).
                // The Phase 1 hillshade is screen-space and needs the field at the
                // exact output dims. The hi-res field is only ever captured while
                // raymarch is on, so Phase 1 always sees a display-res field here.
                bool fieldOk = reliefParams.Relief2DEnabled && !srcAlreadyProcessed
                    && _reliefValid && _reliefHeight != null && src.Length >= n
                    && (raymarch
                        ? (_reliefW > 0 && _reliefH > 0 && _reliefHeight.Length >= _reliefW * _reliefH)
                        : (_reliefW == w && _reliefH == h && _reliefHeight.Length >= n));
                if (fieldOk)
                {
                    if (_reliefColorScratch == null || _reliefColorScratch.Length < n)
                        _reliefColorScratch = new uint[n];
                    if (raymarch)
                    {
                        // Phase 2 — oblique 3D raymarch of the (possibly hi-res)
                        // height field (perspective relief, silhouette, fog).
                        // #162 — dispatch on the GPU when the opt-in flag is set and
                        // a relief kernel is attached; Render falls back to the CPU
                        // sphere trace (full-FX oracle) otherwise. The kernel is
                        // built lazily only when the flag is on, so a non-relief GPU
                        // session never constructs it.
                        FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.Render(
                            src, _reliefHeight!, w, h, _reliefW, _reliefH,
                            reliefParams, _reliefColorScratch, out _,
                            reliefParams.Relief2DGpuRaymarch ? EnsureReliefKernel() : null);
                        reliefRaymarchApplied = true;
                    }
                    else
                        // Phase 1 — screen-space hillshade + cast-shadow post-pass.
                        FracturingFog.Rendering.Lighting.HeightfieldRelief2D.Apply(
                            src, _reliefColorScratch, _reliefHeight!, w, h, reliefParams);
                    src = _reliefColorScratch;
                }
            }

            int brightness = ViewState.Brightness;
            int contrast = ViewState.Contrast;
            int gamma = ViewState.Gamma;
            bool needsProcess = !srcAlreadyProcessed
                                && (brightness != 0 || contrast != 0 || gamma != 0);

            if (needsProcess)
            {
                float contrastFactor = 1.0f + contrast / 100.0f;
                // Operate in 0..255 space so we can stay in integer-friendly
                // ranges and pack channels back without a final *255 multiply.
                float brightnessOffset255 = (brightness / 100.0f) * 255f;

                // F6 part 2 — live image gamma. Precompute a 256-entry byte LUT
                // once (pow is too costly per pixel and has no Vector256
                // intrinsic). When gamma is active we take the scalar path so
                // the LUT applies cleanly; the SIMD fast path stays intact for
                // the common brightness/contrast-only case.
                byte[]? gammaLut = gamma != 0 ? BuildGammaLut(gamma) : null;
                bool gammaActive = gammaLut != null;

                // Parallelise the brightness/contrast pass. At 2M pixels the
                // serial loop was the dominant cost of an adaptive repaint —
                // it scaled with window area, which is why the sweep
                // visibly stuttered at larger window sizes. SIMD inner loop
                // (Vector256, 8 BGRA pixels per step) gives another 4-8×.
                // T2.5: chunked Partitioner — one dispatch per worker chunk
                // (procCount * 4 total) instead of one per row.
                int chunk = h / (Environment.ProcessorCount * 4);
                if (chunk < 1) chunk = 1;
                Parallel.ForEach(Partitioner.Create(0, h, chunk), range =>
                {
                    for (int y = range.Item1; y < range.Item2; y++)
                    {
                        int rowBase = y * w;
                        int end = rowBase + w;
                        int i = rowBase;
                        // SIMD fast path only when gamma is inactive (the LUT
                        // lookup below is not vectorised).
                        if (!gammaActive && Vector256.IsHardwareAccelerated)
                        {
                            i = ProcessRowSimd(src, dst, i, end,
                                               contrastFactor, brightnessOffset255);
                        }
                        // Scalar tail (and full fallback when SIMD unavailable
                        // or gamma is active).
                        for (; i < end; i++)
                        {
                            uint p = src[i];
                            float r = ((p >> 16) & 0xFF);
                            float g = ((p >> 8) & 0xFF);
                            float b = (p & 0xFF);

                            r = (r - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                            g = (g - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;
                            b = (b - 127.5f) * contrastFactor + 127.5f + brightnessOffset255;

                            byte R = (byte)Math.Clamp(r, 0f, 255f);
                            byte G = (byte)Math.Clamp(g, 0f, 255f);
                            byte B = (byte)Math.Clamp(b, 0f, 255f);
                            if (gammaActive)
                            {
                                R = gammaLut![R];
                                G = gammaLut![G];
                                B = gammaLut![B];
                            }
                            dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                        }
                    }
                });
            }
            else
            {
                Array.Copy(src, dst, n);
            }

            // Lighting-FX debug HUD (Phase 19) for Relief 3D. The 3D raymarcher
            // calculators bake the HUD into their ColorBuffer as the last step;
            // the oblique-relief path renders through HeightfieldRaymarch2D (not a
            // calculator), so draw it here on the final buffer using the SAME
            // LightingFxData that lit the relief. Only for the Phase 2 raymarch
            // (Phase 1 hillshade doesn't use the FX light rig). Drawn before the
            // pre-overlay snapshot so screenshots include it, matching the 3D
            // calculators. Self-skips when no flags set or the frame is < 128px.
            if (reliefRaymarchApplied)
            {
                var hudFx = ViewState.FractalParameters.Lighting;
                if (hudFx.DebugHudFlags != 0)
                    FracturingFog.Rendering.Lighting.ScreenSpacePost.ApplyDebugHud(
                        dst, w, h, in hudFx);
            }

            // Snapshot pre-overlay buffer so SaveLastFrameToPng can render a
            // fresh watermark via ImageExport (instead of relying on whatever
            // the on-screen ShowWatermark toggle was at upload time). Pooled
            // so we pay one copy per upload instead of one alloc + one copy.
            // Skipped during active video recording — SaveLastFrameToPng is
            // a user-action path that does not fire mid-record.
            if (!_recordingActive)
            {
                if (_uploadPrePool == null || _uploadPrePool.Length < n)
                    _uploadPrePool = GC.AllocateUninitializedArray<uint>(n, pinned: true);
                var pre = _uploadPrePool;
                Array.Copy(dst, pre, n);
                _lastPreOverlayBuffer = pre;
            }
            else
            {
                _lastPreOverlayBuffer = null;
            }
            // Real render: the pre-overlay snapshot (or ColorBuffer) is now the
            // authoritative ASCII source again.
            _lastUploadExternal = false;

            // F10.5 / issue #96 — composite translucent 2D pixels over a
            // background. The on-screen present is opaque (the swap-chain ignores
            // the alpha channel and the post-FX pass above forces 0xFF), so any
            // authored interior/exterior alpha only shows if we composite it here
            // using the ORIGINAL coverage byte from `src` (which still carries
            // the authored alpha even after the post-FX force-opaque). Runs AFTER
            // the pre-overlay snapshot above, so SaveLastFrameToPng and the
            // export path keep straight alpha. srcAlreadyProcessed frames (video
            // record) are left untouched.
            //
            // Triggers:
            //   • AlphaPreview toggle — the theme-editor see-through aid; always
            //     forces the Checkerboard backdrop regardless of the saved mode
            //     so editing alpha reads as see-through.
            //   • interior translucency (global knob < 255 OR theme in-set alpha
            //     < 255) — the #96 interior-alpha feature.
            //   • an explicit backdrop (Solid / Gradient / Image) — composite
            //     unconditionally so translucent EXTERIOR colour stops show over
            //     it too (opaque pixels skip via the a>=255 continue).
            // Transparent mode is a no-op here (straight alpha kept for export).
            var ip96 = ViewState.FractalParameters;
            var bgMode96 = ip96?.Interior2DBackground ?? Interior2DBackgroundMode.Checkerboard;
            uint inSetArgb = _calculator?.ColorMap?.InSetColor ?? 0xFF000000u;
            bool themeInteriorTranslucent = ((inSetArgb >> 24) & 0xFF) < 255;
            bool interiorTranslucent =
                (ip96 != null && ip96.InteriorAlpha < 255) || themeInteriorTranslucent;
            bool explicitBackdrop =
                bgMode96 == Interior2DBackgroundMode.SolidColor
                || bgMode96 == Interior2DBackgroundMode.Gradient
                || bgMode96 == Interior2DBackgroundMode.Image;
            bool wantAlphaComposite =
                !srcAlreadyProcessed
                && (ViewState.AlphaPreview
                    || (bgMode96 != Interior2DBackgroundMode.Transparent
                        && (interiorTranslucent || explicitBackdrop)));
            if (wantAlphaComposite)
            {
                // AlphaPreview always wins with the checkerboard aid.
                var mode = ViewState.AlphaPreview
                    ? Interior2DBackgroundMode.Checkerboard
                    : bgMode96;
                uint bgTop = ip96?.Interior2DBgTop ?? 0xFF202040u;
                uint bgBot = ip96?.Interior2DBgBottom ?? 0xFF101020u;
                int topR = (int)((bgTop >> 16) & 0xFF), topG = (int)((bgTop >> 8) & 0xFF), topB = (int)(bgTop & 0xFF);
                int botR = (int)((bgBot >> 16) & 0xFF), botG = (int)((bgBot >> 8) & 0xFF), botB = (int)(bgBot & 0xFF);
                int denom = h > 1 ? h - 1 : 1;

                // Image backdrop: decode (cached) up front. On any failure fall
                // back to a flat fill (bgTop) so a bad path never blanks the frame.
                uint[]? imgPx = null;
                int imgW = 0, imgH = 0;
                if (mode == Interior2DBackgroundMode.Image)
                {
                    if (BackgroundImageCache.TryGet(ip96?.Interior2DBgImagePath, out var px, out imgW, out imgH))
                        imgPx = px;
                    else
                        mode = Interior2DBackgroundMode.SolidColor;
                }

                int aChunk = h / (Environment.ProcessorCount * 4);
                if (aChunk < 1) aChunk = 1;
                Parallel.ForEach(Partitioner.Create(0, h, aChunk), range =>
                {
                    for (int y = range.Item1; y < range.Item2; y++)
                    {
                        int rowBase = y * w;
                        // Per-row background base for Solid / Gradient / Image
                        // (checker varies per pixel, computed inline below).
                        int rowBgR = 0, rowBgG = 0, rowBgB = 0;
                        int imgRowBase = 0;
                        if (mode == Interior2DBackgroundMode.SolidColor)
                        {
                            rowBgR = topR; rowBgG = topG; rowBgB = topB;
                        }
                        else if (mode == Interior2DBackgroundMode.Gradient)
                        {
                            // t = 0 at top row (bgTop), 1 at bottom row (bgBot).
                            int t = (y * 256) / denom;   // 0..256 fixed-point
                            rowBgR = (topR * (256 - t) + botR * t) >> 8;
                            rowBgG = (topG * (256 - t) + botG * t) >> 8;
                            rowBgB = (topB * (256 - t) + botB * t) >> 8;
                        }
                        else if (mode == Interior2DBackgroundMode.Image)
                        {
                            // Nearest-neighbour stretch to fill the viewport.
                            int iy = imgH > 0 ? (int)((long)y * imgH / h) : 0;
                            if (iy >= imgH) iy = imgH - 1;
                            imgRowBase = iy * imgW;
                        }
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowBase + x;
                            int a = (int)((src[i] >> 24) & 0xFF);
                            if (a >= 255) continue;   // opaque — dst already right
                            uint pc = dst[i];
                            int R = (int)((pc >> 16) & 0xFF);
                            int G = (int)((pc >> 8) & 0xFF);
                            int B = (int)(pc & 0xFF);
                            int bgR, bgG, bgB;
                            if (mode == Interior2DBackgroundMode.Checkerboard)
                            {
                                int bg = ((((x >> 3) + (y >> 3)) & 1) == 0) ? 200 : 120;
                                bgR = bgG = bgB = bg;
                            }
                            else if (mode == Interior2DBackgroundMode.Image)
                            {
                                int ix = imgW > 0 ? (int)((long)x * imgW / w) : 0;
                                if (ix >= imgW) ix = imgW - 1;
                                uint ipx = imgPx![imgRowBase + ix];
                                bgR = (int)((ipx >> 16) & 0xFF);
                                bgG = (int)((ipx >> 8) & 0xFF);
                                bgB = (int)(ipx & 0xFF);
                            }
                            else
                            {
                                bgR = rowBgR; bgG = rowBgG; bgB = rowBgB;
                            }
                            int inv = 255 - a;
                            R = (R * a + bgR * inv) / 255;
                            G = (G * a + bgG * inv) / 255;
                            B = (B * a + bgB * inv) / 255;
                            dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                        }
                    }
                });
            }

            // Composite grid + watermark on top of the post-FX buffer so the
            // overlay survives every backend (Windows HWND swap-chain
            // included, where Avalonia.Media overlays are occluded). Only
            // runs when at least one toggle is on.
            // S-X7.5 (2026-06-23) — IsWindows gate dropped; compositor is Skia.
            if (ShowGrid || ShowWatermark || _selectionBox.HasValue)
            {
                try
                {
                    _overlay.Composite(dst, w, h, ViewState,
                        ShowGrid, ShowWatermark, OverlayContrastLuma,
                        RegionName, ThemeName, ProgramName, ProgramVersion,
                        ActiveWatermark,
                        _selectionBox);
                }
                catch (Exception ex)
                {
                    // Overlay must never block the render pipeline.
                    Console.Error.WriteLine($"[FractalRenderHost] Overlay composite failed: {ex.Message}");
                }
            }

            // Perf HUD: composited last so it sits above grid + watermark.
            // Standalone of those toggles — user wants timings even on a
            // bare frame. Sampled phase data from _perfStats.
            // S-X7.5 (2026-06-23) — IsWindows gate dropped; HUD is Skia too.
            if (ShowPerfHud)
            {
                try
                {
                    var snap = _perfStats.Snapshot();
                    string lbl = _calculator.IsHighPrecisionActive ? "DD" : "SP";
                    // T3.1: append GPU marker when the SP path actually ran
                    // on the GPU compute kernel this frame. Mirrors the
                    // exact same predicate the calculator uses (toggle on,
                    // kernel present, not HP, zoom within FP32 band) so the
                    // label reads "(GPU)" iff the kernel really did fire.
                    if (_calculator.UseGpuCompute
                        && _calculator.GpuKernel != null
                        && !_calculator.IsHighPrecisionActive
                        && _calculator.Zoom <= MandelbrotCalculator.MaxGpuZoom)
                    {
                        lbl += " (GPU)";
                    }
                    // V6 (#82): deep-zoom GPU perturbation marker. Uses the
                    // calculator's own per-frame latch (set only when the kernel
                    // dispatch actually succeeded, cleared on CPU fallback) so
                    // "DD (GPU)" appears iff the deep frame really ran on the GPU.
                    else if (_calculator.LastFrameUsedGpuPerturbation)
                    {
                        lbl += " (GPU)";
                    }
                    var (ctxLines, warnLine) = BuildRenderContextOverlay();
                    _overlay.CompositePerfHud(dst, w, h,
                        snap, HardwareProbe.Summary,
                        w, h, _calculator.MaxIterations, lbl,
                        ctxLines, warnLine);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FractalRenderHost] Perf HUD composite failed: {ex.Message}");
                }
            }

            // Stop the upload-ms clock here so its value reflects only
            // post-FX + overlay + HUD CPU work, separate from the GPU
            // upload + present cost which is measured below.
            if (ShowPerfHud)
            {
                long uploadEnd = Stopwatch.GetTimestamp();
                _perfStats.RecordUpload((uploadEnd - uploadStart) * 1000.0 / Stopwatch.Frequency);
            }
            long presentStart = ShowPerfHud ? Stopwatch.GetTimestamp() : 0;
            lock (_d3dGate)
            {
                _renderer.UpdateTexture(dst, w, h);
                _renderer.Render();
            }
            if (ShowPerfHud)
            {
                long presentEnd = Stopwatch.GetTimestamp();
                _perfStats.RecordPresent((presentEnd - presentStart) * 1000.0 / Stopwatch.Frequency);
            }
            _lastUploadedBuffer = dst;
            _lastUploadedWidth = w;
            _lastUploadedHeight = h;
            // S-X9g (2026-06-27) — full-res upload is also "what's on screen
            // right now"; mirror into the presented-buffer tracker so the
            // next stale-upload pulls from the freshest content. Cheap —
            // same pointer, no copy.
            _lastPresentedBuffer = dst;
            _lastPresentedWidth = w;
            _lastPresentedHeight = h;

            // S-X9d (2026-06-27) — keep a separate full-res snapshot for the
            // stale-upload fallback. Updated only when this frame matches the
            // current target dims so progressive ¼/½ previews don't clobber
            // it. Source is _lastPreOverlayBuffer (= dst before grid/water/
            // selection-box composite) so SetSelectionBox can repaint over
            // it without double-stamping the overlay. Allocated lazily and
            // grown like the other pinned pools. Skip if no pre-overlay
            // snapshot was taken (recording mode suppresses it).
            if (w == _currentTargetWidth && h == _currentTargetHeight
                && _lastPreOverlayBuffer != null)
            {
                if (_lastFullResBuffer == null || _lastFullResBuffer.Length < n)
                    _lastFullResBuffer = GC.AllocateUninitializedArray<uint>(n, pinned: true);
                Array.Copy(_lastPreOverlayBuffer, _lastFullResBuffer, n);
                _lastFullResWidth = w;
                _lastFullResHeight = h;
            }
            } // _uploadGate

            // Every upload path funnels through here — full calculations, the
            // post-FX / adaptive repaints, alpha/relief recomposites. Signal
            // buffer consumers (the live ASCII view) AFTER the gate releases so
            // their pull re-locks cleanly and sees the fresh _lastPreOverlayBuffer.
            FrameBufferChanged?.Invoke(this, EventArgs.Empty);
        }

        // S-X9 (2026-06-27) — see UploadProcessedBuffer for activation gate.
        // Reports managed-heap bytes, working set, and per-generation GC
        // collection counts. First sample at a real surface (W,H > 64) is
        // the baseline; subsequent logs print delta-from-baseline.
        //
        // S-X9b (2026-06-27) — baseline gating + forced-GC option.
        //   * Skip the ctor's 1×1 dummy frame so warm-up isn't counted as
        //     leak.
        //   * FF_LEAK_DEBUG_FORCEGC=1 runs a blocking Gen-2 collect so
        //     "retained" reflects only objects the GC can't reclaim.
        //     Compare retained-Δ vs managed-Δ to separate transient churn
        //     from real leaks. Off by default — forcing gen-2 blocks every
        //     thread for tens of ms.
        private void LeakDiagSample(int w, int h)
        {
            if (!_leakDiagBaselineTaken && (w < 65 || h < 65)) return;

            long frame = System.Threading.Interlocked.Increment(ref _leakDiagFrame) - 1;
            // S-X9f (2026-06-27) — also log the first 5 frames after baseline
            // unconditionally. One-shot user actions (region jump from combo,
            // theme pick) produce just 1-4 uploads; if those land between
            // modulo hits at the default EVERY=30 the diag drops them silently
            // and the user reports "no log lines fired" for what looked like a
            // bypass bug. Burst window guarantees those single triggers show
            // up in the log.
            if (_leakDiagBaselineTaken && frame >= 6 && (frame % s_leakDiagEvery) != 0) return;

            long managed = GC.GetTotalMemory(forceFullCollection: false);
            long retained = s_leakDiagForceGc
                ? GC.GetTotalMemory(forceFullCollection: true)
                : managed;
            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);
            long ws;
            try { ws = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
            catch { ws = -1; }

            if (!_leakDiagBaselineTaken)
            {
                _leakDiagBaselineTaken = true;
                _leakDiagBaselineManagedBytes = managed;
                _leakDiagBaselineRetainedBytes = retained;
                _leakDiagBaselineWorkingSet = ws;
                _leakDiagBaselineGen0 = g0;
                _leakDiagBaselineGen1 = g1;
                _leakDiagBaselineGen2 = g2;
                Console.Error.WriteLine(
                    s_leakDiagForceGc
                        ? $"[FF_LEAK] baseline f={frame} {w}x{h} managed={managed / (1024 * 1024)}MB retained={retained / (1024 * 1024)}MB ws={(ws < 0 ? -1 : ws / (1024 * 1024))}MB g0={g0} g1={g1} g2={g2}"
                        : $"[FF_LEAK] baseline f={frame} {w}x{h} managed={managed / (1024 * 1024)}MB ws={(ws < 0 ? -1 : ws / (1024 * 1024))}MB g0={g0} g1={g1} g2={g2}");
                return;
            }

            long dManaged = managed - _leakDiagBaselineManagedBytes;
            long dRetained = retained - _leakDiagBaselineRetainedBytes;
            long dWs = ws < 0 ? 0 : ws - _leakDiagBaselineWorkingSet;
            Console.Error.WriteLine(
                s_leakDiagForceGc
                    ? $"[FF_LEAK] f={frame} {w}x{h} managed={managed / (1024 * 1024)}MB (Δ{dManaged / (1024 * 1024):+#;-#;0}) retained={retained / (1024 * 1024)}MB (Δ{dRetained / (1024 * 1024):+#;-#;0}) ws={(ws < 0 ? -1 : ws / (1024 * 1024))}MB (Δ{dWs / (1024 * 1024):+#;-#;0}) g0={g0 - _leakDiagBaselineGen0} g1={g1 - _leakDiagBaselineGen1} g2={g2 - _leakDiagBaselineGen2}"
                    : $"[FF_LEAK] f={frame} {w}x{h} managed={managed / (1024 * 1024)}MB (Δ{dManaged / (1024 * 1024):+#;-#;0}) ws={(ws < 0 ? -1 : ws / (1024 * 1024))}MB (Δ{dWs / (1024 * 1024):+#;-#;0}) g0={g0 - _leakDiagBaselineGen0} g1={g1 - _leakDiagBaselineGen1} g2={g2 - _leakDiagBaselineGen2}");
        }

        /// <summary>
        /// Vectorized brightness/contrast inner loop. Processes 8 BGRA
        /// pixels per Vector256 step. Returns the next index to continue
        /// scalar processing from.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>Builds the 256-entry byte gamma LUT for the live image
        /// gamma slider (F6 part 2). Slider maps to gamma = 2^(slider/100)
        /// (+100→2 brightens, −100→0.5 darkens); the LUT stores
        /// <c>round(pow(v/255, 1/gamma) * 255)</c>.</summary>
        private static byte[] BuildGammaLut(int gammaSlider)
        {
            double gammaValue = Math.Pow(2.0, gammaSlider / 100.0);
            double exp = 1.0 / gammaValue;
            var lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                double outN = Math.Pow(v / 255.0, exp);
                int o = (int)(outN * 255.0 + 0.5);
                lut[v] = (byte)Math.Clamp(o, 0, 255);
            }
            return lut;
        }

        private static int ProcessRowSimd(
            uint[] src, uint[] dst, int start, int end,
            float contrastFactor, float brightnessOffset255)
        {
            int vecLen = Vector256<uint>.Count;
            if (vecLen != 8 || end - start < vecLen) return start;

            var maskFF       = Vector256.Create((uint)0xFF);
            var alpha        = Vector256.Create((uint)0xFF000000);
            var contrastV    = Vector256.Create(contrastFactor);
            var halfV        = Vector256.Create(127.5f);
            var brightnessV  = Vector256.Create(brightnessOffset255);
            var zeroF        = Vector256<float>.Zero;
            var max255F      = Vector256.Create(255f);

            int i = start;
            int simdEnd = end - vecLen;

            // T3.3 — non-temporal stores when dst[start] is 32-byte aligned.
            // Each step writes 32 bytes (8 uints) so alignment is preserved
            // across the loop. The post-FX buffer is consumed by GPU upload
            // immediately — no CPU re-read — so bypassing the cache saves
            // L2 eviction pressure on 4K renders. One-time check up front
            // keeps the branch out of the hot loop.
            bool useNonTemp;
            unsafe
            {
                useNonTemp = (((nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref dst[start])) & 31) == 0;
            }

            if (useNonTemp)
            {
                unsafe
                {
                    fixed (uint* pDst = &dst[0])
                    {
                        for (; i <= simdEnd; i += vecLen)
                        {
                            var packed = Vector256.LoadUnsafe(ref src[i]);
                            var bI = (packed & maskFF).AsInt32();
                            var gI = ((packed >> 8)  & maskFF).AsInt32();
                            var rI = ((packed >> 16) & maskFF).AsInt32();
                            var b = Vector256.ConvertToSingle(bI);
                            var g = Vector256.ConvertToSingle(gI);
                            var r = Vector256.ConvertToSingle(rI);
                            b = (b - halfV) * contrastV + halfV + brightnessV;
                            g = (g - halfV) * contrastV + halfV + brightnessV;
                            r = (r - halfV) * contrastV + halfV + brightnessV;
                            b = Vector256.Max(zeroF, Vector256.Min(max255F, b));
                            g = Vector256.Max(zeroF, Vector256.Min(max255F, g));
                            r = Vector256.Max(zeroF, Vector256.Min(max255F, r));
                            var bU = Vector256.ConvertToInt32(b).AsUInt32();
                            var gU = Vector256.ConvertToInt32(g).AsUInt32() << 8;
                            var rU = Vector256.ConvertToInt32(r).AsUInt32() << 16;
                            var result = alpha | rU | gU | bU;
                            result.StoreAlignedNonTemporal(pDst + i);
                        }
                    }
                }
            }
            else
            {
                for (; i <= simdEnd; i += vecLen)
                {
                    var packed = Vector256.LoadUnsafe(ref src[i]);
                    var bI = (packed & maskFF).AsInt32();
                    var gI = ((packed >> 8)  & maskFF).AsInt32();
                    var rI = ((packed >> 16) & maskFF).AsInt32();
                    var b = Vector256.ConvertToSingle(bI);
                    var g = Vector256.ConvertToSingle(gI);
                    var r = Vector256.ConvertToSingle(rI);
                    b = (b - halfV) * contrastV + halfV + brightnessV;
                    g = (g - halfV) * contrastV + halfV + brightnessV;
                    r = (r - halfV) * contrastV + halfV + brightnessV;
                    b = Vector256.Max(zeroF, Vector256.Min(max255F, b));
                    g = Vector256.Max(zeroF, Vector256.Min(max255F, g));
                    r = Vector256.Max(zeroF, Vector256.Min(max255F, r));
                    var bU = Vector256.ConvertToInt32(b).AsUInt32();
                    var gU = Vector256.ConvertToInt32(g).AsUInt32() << 8;
                    var rU = Vector256.ConvertToInt32(r).AsUInt32() << 16;
                    var result = alpha | rU | gU | bU;
                    result.StoreUnsafe(ref dst[i]);
                }
            }
            return i;
        }

        /// <inheritdoc/>
        public void Present()
        {
            if (_disposed) return;
            lock (_d3dGate) _renderer.Render();
        }

        /// <inheritdoc/>
        public uint[] SnapshotFrame(out int width, out int height)
        {
            // _lastUploadedBuffer is now a pooled scratch that gets overwritten
            // by the next UploadProcessedBuffer — take _uploadGate so the copy
            // is coherent.
            lock (_uploadGate)
            {
                var buf = _lastUploadedBuffer;
                if (buf == null)
                {
                    // No frame uploaded yet — still surface the renderer's
                    // current target size so callers (slideshow cold start)
                    // can size a fade-in source. Buffer stays empty so
                    // length-guarded cross-fade paths fall through.
                    width = _currentTargetWidth;
                    height = _currentTargetHeight;
                    return Array.Empty<uint>();
                }
                width = _lastUploadedWidth;
                height = _lastUploadedHeight;
                int n = width * height;
                var copy = new uint[n];
                Array.Copy(buf, copy, n);
                return copy;
            }
        }

        /// <inheritdoc/>
        public void PresentBuffer(uint[] bgra, int width, int height)
        {
            if (_disposed || bgra == null || width <= 0 || height <= 0) return;
            if (bgra.Length < (long)width * height) return;
            lock (_d3dGate)
            {
                _renderer.UpdateTexture(bgra, width, height);
                _renderer.Render();
            }
            _lastUploadedBuffer = bgra;
            _lastUploadedWidth = width;
            _lastUploadedHeight = height;
            // S-X9g — external present is also "current screen content".
            _lastPresentedBuffer = bgra;
            _lastPresentedWidth = width;
            _lastPresentedHeight = height;
            // Route the ASCII source to this presented buffer (see field note) —
            // _lastPreOverlayBuffer still points at the previous real render.
            _lastUploadExternal = true;
            // A live ASCII view mirrors the buffer via FrameBufferChanged. An
            // external present (slideshow cross-fade steps, etc.) mutates
            // _lastUploadedBuffer — the ASCII source — without going through
            // UploadProcessedBuffer, so fire the event here too or Terminal Mode
            // would snap between committed frames instead of cross-fading.
            FrameBufferChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Encode the most-recently-uploaded BGRA buffer to a PNG file at the
        /// given path. The saved image always carries the program watermark
        /// (region/theme + program-name sub-line) regardless of the on-screen
        /// <see cref="ShowWatermark"/> toggle — parity with the legacy
        /// WinForms screenshot flow in ImageCapture.cs. No-op when no frame
        /// has been rendered yet. Cross-platform via SkiaSharp in
        /// ImageExport.SavePixelsToFile (Phase X.A / Slice A.7).
        /// </summary>
        public void SaveLastFrameToPng(string path)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(path)) return;
            // S-X7.4 (2026-06-23) — IsWindows gate dropped. The underlying
            // ImageExport.SavePixelsToFile is SkiaSharp end-to-end; the old
            // gate dates from before that migration when this method called
            // System.Drawing-bound encoder paths.

            // Prefer the pre-overlay snapshot so a fresh watermark renders at
            // file resolution instead of double-stamping the buffer's already-
            // composited one. Fall back to the post-upload buffer if no
            // pre-snapshot was captured (e.g. an externally-presented frame).
            // Copy out under _uploadGate because the source is now pooled
            // and will be overwritten by the next upload.
            uint[]? buf;
            int w, h;
            lock (_uploadGate)
            {
                var src = _lastPreOverlayBuffer ?? _lastUploadedBuffer;
                w = _lastUploadedWidth; h = _lastUploadedHeight;
                if (src == null || w <= 0 || h <= 0) return;
                int n = w * h;
                buf = new uint[n];
                Array.Copy(src, buf, n);
            }

            // Resolve through the shared chain. ActiveWatermark = non-null when
            // the shell has pushed a custom watermark in via the precedence
            // resolver; null = legacy default path (region/theme + auto contrast).
            var auto = FracturingFog.Imaging.ImageExport.ComputeContrastColor(
                System.Drawing.Color.White, watermark: true, pixels: buf, imgW: w, imgH: h);
            var defaultText = new FracturingFog.Models.RgbDef(auto.R, auto.G, auto.B);
            var wm = FracturingFog.Imaging.WatermarkResolver.Resolve(
                activeCustom: ActiveWatermark,
                regionEmbedded: null,
                overrideRegionWatermark: ActiveWatermark != null,
                useCustomWatermark: ActiveWatermark != null,
                regionName: RegionName ?? string.Empty,
                themeName: ThemeName ?? string.Empty,
                programName: ProgramName ?? "Fracturing Fog",
                programVersion: ProgramVersion ?? string.Empty,
                defaultTextColor: defaultText);

            FracturingFog.Imaging.ImageExport.SavePixelsToFile(
                buf, w, h, path, FracturingFog.Imaging.ImageFileFormat.Png,
                wm);
        }

        /// <summary>
        /// Render the most-recently-uploaded frame as character/text art (#226)
        /// and write it to <paramref name="path"/>. Consumes the real
        /// <see cref="FracturingFog.Interefaces.IColorMap"/> output — the same
        /// pre-overlay BGRA buffer the screenshot path uses — so the colored
        /// formats (ANSI / half-block / HTML / SVG) tint from the active theme.
        /// When the active calculator exposes an <see cref="Interefaces.IHeightFieldSource"/>
        /// smooth field at matching dimensions it drives the glyph ramp
        /// (banding-free); otherwise the exporter falls back to pixel luminance.
        /// The resolved watermark is stamped into every format as character-art ink
        /// (#241), matching the always-on watermark on the PNG/video paths. No-op
        /// before the first frame.
        /// </summary>
        public void SaveLastFrameAsAsciiArt(
            string path, FracturingFog.Imaging.AsciiArtOptions options)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(path) || options == null) return;
            if (!TryGetAsciiSource(out var buf, out var smooth, out int w, out int h)) return;
            // Stamp the same resolved watermark every other export surface uses.
            // Explicit export always carries it (parity with SaveLastFrameToPng /
            // video), independent of the live-terminal ShowWatermark toggle.
            options.Watermark = BuildAsciiWatermark();
            options.WatermarkStyle = AsciiWatermarkStyle;
            FracturingFog.Imaging.AsciiArtRenderer.WriteToFile(buf, smooth, w, h, options, path);
        }

        /// <inheritdoc/>
        // Persistent cross-frame state for the stateful ASCII FX (rain / particles
        // / trails). The host owns it because the UI shell can't reference the
        // Engine-side AsciiFxState; the live pump just passes settings each frame.
        private FracturingFog.Imaging.AsciiFxState? _asciiFxState;

        // Live ASCII recording (#230): while active, every RenderLastFrameAscii
        // captures its FX'd grid at wall-clock cadence, so whatever is animating
        // (zoom video / Scene / slideshow / interactive) is recorded frame-by-frame.
        private readonly object _liveRecLock = new();
        private FracturingFog.Imaging.AsciiAnimationRecorder? _liveRec;      // capturing now
        private FracturingFog.Imaging.AsciiAnimationRecorder? _liveRecPending; // stopped, awaiting save
        private readonly System.Diagnostics.Stopwatch _liveRecClock = new();
        private double _liveRecLast;
        private const int MaxLiveRecFrames = 3600; // ~2 min at 30fps — bounds memory

        /// <summary>True while a live ASCII recording is actively capturing.</summary>
        public bool IsLiveAsciiRecording { get { lock (_liveRecLock) return _liveRec != null; } }

        /// <summary>Start capturing the live ASCII frames. Discards any prior
        /// (active or pending) capture.</summary>
        public void BeginLiveAsciiRecording()
        {
            lock (_liveRecLock)
            {
                _liveRec = new FracturingFog.Imaging.AsciiAnimationRecorder();
                _liveRecPending = null;
                _liveRecClock.Restart();
                _liveRecLast = 0.0;
            }
        }

        /// <summary>Freeze the capture (no more frames appended) and hold it as
        /// pending for a save. Returns the captured frame count.</summary>
        public int StopLiveAsciiRecording()
        {
            lock (_liveRecLock)
            {
                _liveRecClock.Stop();
                _liveRecPending = _liveRec;
                _liveRec = null;
                return _liveRecPending?.FrameCount ?? 0;
            }
        }

        /// <summary>Serialise the pending recording to a text container
        /// ("cast" / "svg" / "ans"). Null if none pending / empty.</summary>
        public string? SerializePendingRecording(string format)
        {
            FracturingFog.Imaging.AsciiAnimationRecorder? rec;
            lock (_liveRecLock) rec = _liveRecPending;
            if (rec == null || rec.FrameCount == 0) return null;
            var fmt = format?.ToLowerInvariant() switch
            {
                "svg" => FracturingFog.Imaging.AsciiAnimationFormat.AnimatedSvg,
                "ans" => FracturingFog.Imaging.AsciiAnimationFormat.AnsiSequence,
                _      => FracturingFog.Imaging.AsciiAnimationFormat.AsciinemaCast,
            };
            return rec.Serialize(fmt, new FracturingFog.Imaging.AsciiArtOptions());
        }

        /// <summary>The pending recording's grids for the MP4 exporter. Null if
        /// none pending / empty.</summary>
        public System.Collections.Generic.IReadOnlyList<FracturingFog.Render.AsciiFrame>? PendingRecordingFrames()
        {
            FracturingFog.Imaging.AsciiAnimationRecorder? rec;
            lock (_liveRecLock) rec = _liveRecPending;
            if (rec == null || rec.FrameCount == 0) return null;
            return rec.ExportFrames();
        }

        /// <summary>Drop the pending recording (save cancelled).</summary>
        public void ClearPendingRecording() { lock (_liveRecLock) _liveRecPending = null; }

        // Resolve the same watermark payload every other surface uses, for the
        // ASCII painter. The default (non-custom) text colour is a bright neutral
        // rather than the per-pixel auto-contrast the raster path samples — a
        // Terminal watermark is monochrome ink over the character art, and the
        // grid has no stable "lower-right patch" to sample at cell resolution.
        private FracturingFog.Imaging.WatermarkRender BuildAsciiWatermark()
            => FracturingFog.Imaging.WatermarkResolver.Resolve(
                activeCustom: ActiveWatermark,
                regionEmbedded: null,
                overrideRegionWatermark: ActiveWatermark != null,
                useCustomWatermark: ActiveWatermark != null,
                regionName: RegionName ?? string.Empty,
                themeName: ThemeName ?? string.Empty,
                programName: ProgramName ?? "Fracturing Fog",
                programVersion: ProgramVersion ?? string.Empty,
                defaultTextColor: new FracturingFog.Models.RgbDef(230, 230, 230));

        public FracturingFog.Render.AsciiFrame? RenderLastFrameAscii(
            int columns, double cellAspect, bool color, bool invert, bool fineRamp,
            bool rampFromColor = false, FracturingFog.Imaging.AsciiFxSettings? fx = null)
        {
            if (_disposed) return null;
            if (!TryGetAsciiSource(out var buf, out var smooth, out int w, out int h)) return null;

            var opt = new FracturingFog.Imaging.AsciiArtOptions
            {
                Format = FracturingFog.Imaging.AsciiArtFormat.PlainText, // grid producer ignores format
                Columns = Math.Max(1, columns),
                CellAspect = cellAspect > 0.1 ? cellAspect : 2.0,
                Invert = invert,
                UseSmoothField = true,
                RampFromColorLuma = rampFromColor,
                Ramp = fineRamp
                    ? FracturingFog.Imaging.AsciiArtOptions.FineRamp
                    : new FracturingFog.Imaging.AsciiArtOptions().Ramp,
            };

            var cells = FracturingFog.Imaging.AsciiArtRenderer.RenderCells(
                buf, smooth, w, h, opt, out int cols, out int rows);

            // ASCII-native FX (#229): transform the cell grid in place. The full
            // effect set arrives in `fx`; the host supplies the persistent state
            // for the stateful effects.
            if (fx != null && fx.AnyEnabled)
            {
                var state = fx.NeedsState ? (_asciiFxState ??= new FracturingFog.Imaging.AsciiFxState()) : null;
                FracturingFog.Imaging.AsciiFxChain.Apply(cells, cols, rows, opt.Ramp, fx, state);
            }

            // ASCII watermark (#241): stamp last, over the FX, so it stays legible.
            // Live Terminal honours the same ShowWatermark toggle as the render
            // window; stamped before the recording capture so REC is WYSIWYG.
            if (ShowWatermark)
                FracturingFog.Imaging.AsciiWatermark.Stamp(
                    cells, cols, rows, BuildAsciiWatermark(), AsciiWatermarkStyle);

            // Live recording (#230): append this exact grid at wall-clock cadence.
            lock (_liveRecLock)
            {
                if (_liveRec != null && _liveRec.FrameCount < MaxLiveRecFrames)
                {
                    double now = _liveRecClock.Elapsed.TotalSeconds;
                    double hold = now - _liveRecLast;
                    _liveRecLast = now;
                    try { _liveRec.AddFrame(cells, cols, rows, hold); }
                    catch { /* grid-size change mid-record: skip the odd frame */ }
                }
            }

            int n = cols * rows;
            var glyphs = new char[n];
            uint[] colors = color ? new uint[n] : System.Array.Empty<uint>();
            for (int i = 0; i < n; i++)
            {
                var c = cells[i];
                glyphs[i] = c.Glyph;
                if (color) colors[i] = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
            return new FracturingFog.Render.AsciiFrame(cols, rows, glyphs, colors, color);
        }

        /// <inheritdoc/>
        public string? RecordAsciiAnimation(
            int columns, double cellAspect, bool invert, bool fineRamp, bool rampFromColor,
            FracturingFog.Imaging.AsciiFxSettings fx, int frames, double fps, string format)
        {
            if (_disposed || fx is null) return null;
            if (frames <= 0) frames = 1;
            if (fps <= 0) fps = 12.0;
            if (!TryGetAsciiSource(out var buf, out var smooth, out int w, out int h)) return null;

            var opt = new FracturingFog.Imaging.AsciiArtOptions
            {
                Format = FracturingFog.Imaging.AsciiArtFormat.PlainText,
                Columns = Math.Max(1, columns),
                CellAspect = cellAspect > 0.1 ? cellAspect : 2.0,
                Invert = invert,
                UseSmoothField = true,
                RampFromColorLuma = rampFromColor,
                Ramp = fineRamp
                    ? FracturingFog.Imaging.AsciiArtOptions.FineRamp
                    : new FracturingFog.Imaging.AsciiArtOptions().Ramp,
            };

            // Re-render the same last frame each step, advancing the FX clock so
            // the animated effects play out, and accumulate into the recorder.
            var rec = new FracturingFog.Imaging.AsciiAnimationRecorder();
            RecordAsciiInto(buf, smooth, w, h, opt, fx, frames, fps,
                (cells, cols, rows, dt) => rec.AddFrame(cells, cols, rows, dt),
                BuildAsciiWatermark(), AsciiWatermarkStyle);

            var fmt = format?.ToLowerInvariant() switch
            {
                "svg" => FracturingFog.Imaging.AsciiAnimationFormat.AnimatedSvg,
                "ans" => FracturingFog.Imaging.AsciiAnimationFormat.AnsiSequence,
                _      => FracturingFog.Imaging.AsciiAnimationFormat.AsciinemaCast,
            };
            return rec.Serialize(fmt, opt);
        }

        /// <inheritdoc/>
        public System.Collections.Generic.IReadOnlyList<FracturingFog.Render.AsciiFrame>? RecordAsciiFrames(
            int columns, double cellAspect, bool invert, bool fineRamp, bool rampFromColor,
            FracturingFog.Imaging.AsciiFxSettings fx, int frames, double fps)
        {
            if (_disposed || fx is null) return null;
            if (frames <= 0) frames = 1;
            if (fps <= 0) fps = 12.0;
            if (!TryGetAsciiSource(out var buf, out var smooth, out int w, out int h)) return null;

            var opt = BuildAsciiOptions(columns, cellAspect, invert, fineRamp, rampFromColor);
            var list = new System.Collections.Generic.List<FracturingFog.Render.AsciiFrame>(frames);
            RecordAsciiInto(buf, smooth, w, h, opt, fx, frames, fps, (cells, cols, rows, dt) =>
            {
                int n = cols * rows;
                var glyphs = new char[n];
                var colors = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    var c = cells[i];
                    glyphs[i] = c.Glyph;
                    colors[i] = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                }
                list.Add(new FracturingFog.Render.AsciiFrame(cols, rows, glyphs, colors, true));
            },
            BuildAsciiWatermark(), AsciiWatermarkStyle);
            return list;
        }

        private static FracturingFog.Imaging.AsciiArtOptions BuildAsciiOptions(
            int columns, double cellAspect, bool invert, bool fineRamp, bool rampFromColor) =>
            new()
            {
                Format = FracturingFog.Imaging.AsciiArtFormat.PlainText,
                Columns = Math.Max(1, columns),
                CellAspect = cellAspect > 0.1 ? cellAspect : 2.0,
                Invert = invert,
                UseSmoothField = true,
                RampFromColorLuma = rampFromColor,
                Ramp = fineRamp
                    ? FracturingFog.Imaging.AsciiArtOptions.FineRamp
                    : new FracturingFog.Imaging.AsciiArtOptions().Ramp,
            };

        // Shared record loop: re-render the same last frame `frames` times,
        // advancing the FX + transition clocks by 1/fps each step, and hand each
        // FX'd cell grid to `sink`. A fresh AsciiFxState keeps it deterministic.
        private static void RecordAsciiInto(
            uint[] buf, float[]? smooth, int w, int h,
            FracturingFog.Imaging.AsciiArtOptions opt, FracturingFog.Imaging.AsciiFxSettings fx,
            int frames, double fps,
            System.Action<FracturingFog.Imaging.AsciiCell[], int, int, double> sink,
            FracturingFog.Imaging.WatermarkRender? watermark = null,
            FracturingFog.Imaging.AsciiWatermarkStyle watermarkStyle =
                FracturingFog.Imaging.AsciiWatermarkStyle.Block)
        {
            var state = fx.NeedsState ? new FracturingFog.Imaging.AsciiFxState() : null;
            double dt = 1.0 / fps;
            for (int f = 0; f < frames; f++)
            {
                var cells = FracturingFog.Imaging.AsciiArtRenderer.RenderCells(
                    buf, smooth, w, h, opt, out int cols, out int rows);
                fx.TimeSeconds = f * dt;
                fx.TransitionTimeSeconds = f * dt;
                if (fx.AnyEnabled)
                    FracturingFog.Imaging.AsciiFxChain.Apply(cells, cols, rows, opt.Ramp, fx, state);
                // Export always carries the watermark, matching image / video save.
                if (watermark != null)
                    FracturingFog.Imaging.AsciiWatermark.Stamp(
                        cells, cols, rows, watermark, watermarkStyle);
                sink(cells, cols, rows, dt);
            }
        }

        // Shared source selection for both the ASCII file export (#226) and the
        // live ASCII display (#227): the real IColorMap pre-overlay BGRA buffer
        // plus the smooth iteration field read off whichever calculator produced
        // the last frame (alt for non-Mandelbrot types, else the concrete
        // primary), only when its dimensions match the uploaded buffer (deep-zoom
        // / MSAA / preview paths can mismatch — the exporter falls back to luma).
        private bool TryGetAsciiSource(
            out uint[] buf, out float[]? smooth, out int w, out int h)
        {
            buf = System.Array.Empty<uint>(); smooth = null; w = 0; h = 0;
            lock (_uploadGate)
            {
                // Prefer the clean pre-overlay snapshot so ASCII doesn't sample
                // baked-in grid/watermark pixels — EXCEPT after an external
                // present (slideshow blend), where that snapshot is a stale
                // committed frame and the presented buffer is the live content.
                var src = _lastUploadExternal
                    ? _lastUploadedBuffer
                    : (_lastPreOverlayBuffer ?? _lastUploadedBuffer);
                w = _lastUploadedWidth; h = _lastUploadedHeight;
                if (src == null || w <= 0 || h <= 0) { w = h = 0; return false; }
                int n = w * h;
                buf = new uint[n];
                Array.Copy(src, buf, n);
            }

            IFractalCalculator? alt = SelectAltCalculator(ViewState.FractalType);
            Interefaces.IHeightFieldSource? hfSrc;
            int cw, ch;
            if (alt != null) { hfSrc = alt as Interefaces.IHeightFieldSource; cw = alt.Width; ch = alt.Height; }
            else             { hfSrc = _calculator; cw = _calculator.Width; ch = _calculator.Height; }
            if (hfSrc != null && cw == w && ch == h)
            {
                var sb = hfSrc.SmoothBuffer;
                if (sb != null && sb.Length == (long)w * h) smooth = sb;
            }
            return true;
        }

        // Phase 18b — animation tick callback. Polls the active Lighting
        // struct for any non-zero animation speed; if none, returns without
        // touching ViewState (idle scene stays bit-identical to a stopped
        // clock). Otherwise advances SceneTime to "seconds since the first
        // tick that found a non-zero speed" and kicks a Trigger.
        //
        // SceneTime injection happens *on the timer thread* via a copy-out
        // / mutate / copy-in dance because Lighting is exposed as an
        // auto-property of a value type — a direct field write would be
        // discarded by the compiler. The mutation is a single struct
        // assignment; one tick can race a calculator's own snapshot read
        // and produce 1/30 s of phase mismatch, but the value is a smooth
        // double so the artifact is below perception threshold.
        private void AnimationTick(object? state)
        {
            if (_disposed) return;
            if (System.Threading.Interlocked.Exchange(ref _animTickBusy, 1) != 0) return;
            try
            {
                var p = ViewState.FractalParameters;
                if (p == null) return;
                var l = p.Lighting;
                bool anySpeed =
                    Math.Abs(l.LightOrbitSpeed)   > 1e-9 ||
                    Math.Abs(l.CausticsAnimSpeed) > 1e-9 ||
                    Math.Abs(l.VolumeNoiseSpeed)  > 1e-9;
                if (!anySpeed)
                {
                    // Reset clock so the next time speed becomes non-zero we
                    // re-anchor at "now" rather than carrying an ancient base.
                    _animStartTicks = 0;
                    return;
                }

                // Phase 18b fix — anchor the scene clock the moment speed becomes
                // non-zero, regardless of whether this tick will proceed to
                // Trigger. Without this, _animStartTicks was set only when the
                // gate cleared, so the first animated render snapshotted
                // SceneTime=0 → identical pixels to the user's initial frame →
                // looked like "no animation". By the time the gate clears the
                // clock has already advanced through the in-flight render.
                long now = Stopwatch.GetTimestamp();
                if (_animStartTicks == 0) _animStartTicks = now;

                // Phase 18b fix — gate on the previous frame having completed.
                // Trigger() sets _animFrameInFlight; AnimationFrameUploaded
                // clears it. Skip if a frame (user-initiated OR animation-tick
                // initiated) is still running. Without this gate, a slow 3D
                // scene whose Calculate exceeds the 33 ms tick period gets
                // cancelled by every subsequent tick — the user sees the
                // status bar stuck on "Calculating…".
                if (System.Threading.Volatile.Read(ref _animFrameInFlight) != 0)
                    return;

                double sceneTime = (now - _animStartTicks) / (double)Stopwatch.Frequency;
                l.SceneTime = sceneTime;
                p.Lighting = l;

                Trigger();
            }
            catch
            {
                // Animation tick must never tear down the host. Swallow and
                // wait for the next period.
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _animTickBusy, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Phase 18b — stop the animation timer first so no more Triggers
            // fire while the rest of the pipeline is being torn down.
            try { _animTimer?.Dispose(); } catch { }
            _animTimer = null;
            try { _colorSettleTimer?.Dispose(); } catch { }
            _colorSettleTimer = null;
            // Tear down any running video / slideshow first so its background
            // loop stops touching the calculator + renderer before disposal.
            lock (_videoLock) _videoCts?.Cancel();
            lock (_videoSlideshowLock)
            {
                _videoSlideshowCts?.Cancel();
                _videoSlideshowLegCts?.Cancel();
            }
            lock (_calcLock) _calcCts?.Cancel();

            // T2.4: stop the dedicated calc thread before tearing down the
            // renderer so a running Calculate doesn't try to use a disposed
            // device on its way out.
            try { _calcQueue.CompleteAdding(); } catch { }
            try { _calcThread?.Join(2000); } catch { }
            try { _calcQueue.Dispose(); } catch { }
            // #85 — safe now that the calc thread has joined (no more Reset/Set).
            try { _calcIdle.Dispose(); } catch { }

            // T3.1: dispose the GPU compute kernel before the renderer so its
            // UAV / staging / cbuffer / CS releases hit the device first.
            try
            {
                _calculator.GpuKernel = null;
                _escapeCalculator.GpuKernel = null;
                _gpuKernel?.Dispose();
                _gpuKernel = null;
                // #162 — release the relief kernel's device objects before the
                // renderer too (D3D buffers/UAV/CS, or the Vulkan device it owns).
                _reliefKernel?.Dispose();
                _reliefKernel = null;
            }
            catch { }

            lock (_d3dGate) _renderer.Dispose();
        }
    }
}
