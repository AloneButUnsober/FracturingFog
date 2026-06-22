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
        private readonly FractalOverlayCompositor _overlay = OperatingSystem.IsWindows()
            ? new FractalOverlayCompositor()
            : null!;

        // Cached previous frame — re-uploaded on the next trigger so the
        // user sees the stale (correct) image while the next one calculates,
        // instead of black flashes at High/Ultra quality.
        private uint[]? _lastUploadedBuffer;
        private int _lastUploadedWidth;
        private int _lastUploadedHeight;
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

            public FrameJob(CancellationToken token, MandelbrotCalculator calc,
                IFractalCalculator? altCalc, Stopwatch sw,
                uint[]? staleBuf, int staleW, int staleH, int calcW, int calcH,
                int taaSampleIndex = 0, int progressiveStage = 0)
            {
                Token = token; Calc = calc; AltCalc = altCalc; Sw = sw;
                StaleBuf = staleBuf; StaleW = staleW; StaleH = staleH;
                CalcW = calcW; CalcH = calcH;
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

            // Phase 18b fix — clear the frame-in-flight gate when each
            // upload completes (success or cancellation, both fire this
            // event). Frees the next animation tick to enqueue another
            // frame instead of being silently skipped.
            AnimationFrameUploaded += (_, _) =>
                System.Threading.Interlocked.Exchange(ref _animFrameInFlight, 0);
        }

        public FractalViewState ViewState { get; }

        public event EventHandler<RenderFrameInfo>? FrameCompleted;
        public event EventHandler? AnimationFrameUploaded;
        public event EventHandler<string>? StatusRequested;
        public event EventHandler? ColorMapChanged;

        // ── Overlay state (CPU-composited into the BGRA buffer) ──────────
        //
        // On Windows the GpuSurfaceControl is a NativeControlHost wrapping a
        // real HWND; the OS composites that HWND above every Avalonia control
        // regardless of XAML Z-order, so an Avalonia.Media overlay can't
        // render on top of it. Instead the host blends the grid + watermark
        // into the BGRA pixel buffer on the CPU before the swap-chain upload.

        public bool ShowGrid { get; set; }
        public bool ShowWatermark { get; set; }
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
        /// Used by the Avalonia shell's Poster command (the shared
        /// <see cref="PosterRenderer"/> does the actual calc + save). The full
        /// quad-precision centre is copied so a Mandelbrot deep zoom survives
        /// the re-render at poster resolution.
        /// </summary>
        public PosterRequest CreatePosterRequest(
            int width, int height, bool rotate,
            string path, FracturingFog.Imaging.ImageFileFormat format, string watermark, string subText,
            FracturingFog.Models.WatermarkDef? customWatermark = null)
        {
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
            _calculator.CenterY = ViewState.CenterY;
            _calculator.CenterYLo = ViewState.CenterYLo;
            _calculator.CenterY2 = ViewState.CenterY2;
            _calculator.CenterY3 = ViewState.CenterY3;
            _calculator.Zoom = ViewState.Zoom;
            _calculator.Quality = ViewState.Quality;

            if (ViewState.IterLocked)
                _calculator.MaxIterations = ViewState.LockedIterations;
            else if (maxIters > 0)
                _calculator.MaxIterations = maxIters;
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
            uint[]? staleBuf = _lastUploadedBuffer;
            int staleW = _lastUploadedWidth;
            int staleH = _lastUploadedHeight;
            int calcW = _calculator.Width;
            int calcH = _calculator.Height;

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
            var job = new FrameJob(token, calc, useAlt ? altCalc : null, sw,
                staleBuf, staleW, staleH, calcW, calcH,
                taaSampleIndex: 0, progressiveStage: progressiveStage);
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
                    try { RunFrameJobCalc(in job); }
                    catch (OperationCanceledException) { }
                    catch { /* swallow — token-driven cancellation is the only
                              expected failure mode; surface anything else via
                              the calc's own error path if it has one. */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private void RunFrameJobCalc(in FrameJob job)
        {
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
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
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
            if (job.StaleBuf != null && job.StaleW == job.CalcW && job.StaleH == job.CalcH)
            {
                lock (_uploadGate)
                {
                    lock (_d3dGate)
                    {
                        _renderer.UpdateTexture(job.StaleBuf, job.StaleW, job.StaleH);
                        _renderer.Render();
                    }
                }
            }

            long calcStart = Stopwatch.GetTimestamp();
            try
            {
                if (useAlt) altCalc!.Calculate(token);
                else calc.Calculate(token);

                // Wave 2.6 — sub-pixel MSAA via Calculate() re-runs at jittered
                // centre coords. Only on the canonical (non-alt) Mandelbrot
                // calc; alt calcs are the user-equation hot-load path which
                // already pays delegate-call overhead per pixel and shouldn't
                // pay AA on top. Pixel-size heuristic matches the calculator's
                // own (3.5 / max(W,H) / Zoom).
                int aaSamples = !useAlt ? (calc.Quality?.AaSamples ?? 1) : 1;
                if (aaSamples > 1 && !token.IsCancellationRequested)
                    RunMsaaAccumulateMandelbrot(calc, aaSamples, token);

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

            var nextJob = new FrameJob(
                job.Token, calc, altCalc: null, sw: Stopwatch.StartNew(),
                staleBuf: null, staleW: 0, staleH: 0,
                calcW: calc.Width, calcH: calc.Height,
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
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    return;
                }
                MandelbrotCalculator preview = job.ProgressiveStage >= 4
                    ? _previewCalcQuarter
                    : _previewCalcHalf;
                lock (_uploadGate)
                {
                    lock (_d3dGate)
                    {
                        _renderer.UpdateTexture(preview.ColorBuffer, preview.Width, preview.Height);
                        _renderer.Render();
                    }
                }
                AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);

                int nextStage = job.ProgressiveStage >= 4 ? 2 : 0;
                var nextJob = new FrameJob(
                    job.Token, calc, altCalc: null, sw: job.Sw,
                    staleBuf: null, staleW: 0, staleH: 0,
                    calcW: job.CalcW, calcH: job.CalcH,
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
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
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

                if (useAlt)
                    UploadProcessedBuffer(altCalc!.ColorBuffer, altCalc.Width, altCalc.Height);
                else
                    UploadProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height);

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

                FrameCompleted?.Invoke(this, new RenderFrameInfo(
                    curCx, curCy, curZoom, curIter, ms, curW, curH,
                    hp, ViewState.IterLocked, ViewState.FractalType, lbl));

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

        public void Resize(int width, int height)
        {
            if (_disposed) return;
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            _lastUploadedBuffer = null;
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
            if (alt != null) { RepaintWithPostFx(); return; }

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

            if (!needsFullRender && SelectAltCalculator(ViewState.FractalType) == null)
            {
                // Mandelbrot fast path — recolour from cached buffers.
                _calculator.ApplyBandDitherRecolor(0.0);
                UploadProcessedBuffer(_calculator.ColorBuffer, _calculator.Width, _calculator.Height);
            }
            else
            {
                // Alt calculator OR theme needs data not in the cached buffers.
                Trigger();
            }
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
                var altSrc = alt.ColorBuffer;
                int n = w * h;
                var altCopy = new uint[n];
                Array.Copy(altSrc, altCopy, Math.Min(altSrc.Length, n));
                return altCopy;
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
            var src = _calculator.ColorBuffer;
            int mn = w * h;
            var copy = new uint[mn];
            Array.Copy(src, copy, Math.Min(src.Length, mn));
            return copy;
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
        private void UploadProcessedBuffer(uint[] src, int w, int h)
        {
            int n = w * h;
            long uploadStart = ShowPerfHud ? Stopwatch.GetTimestamp() : 0;
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

            int brightness = ViewState.Brightness;
            int contrast = ViewState.Contrast;
            bool needsProcess = brightness != 0 || contrast != 0;

            if (needsProcess)
            {
                float contrastFactor = 1.0f + contrast / 100.0f;
                // Operate in 0..255 space so we can stay in integer-friendly
                // ranges and pack channels back without a final *255 multiply.
                float brightnessOffset255 = (brightness / 100.0f) * 255f;

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
                        if (Vector256.IsHardwareAccelerated)
                        {
                            i = ProcessRowSimd(src, dst, i, end,
                                               contrastFactor, brightnessOffset255);
                        }
                        // Scalar tail (and full fallback when SIMD unavailable).
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
                            dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                        }
                    }
                });
            }
            else
            {
                Array.Copy(src, dst, n);
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

            // Composite grid + watermark on top of the post-FX buffer so the
            // overlay survives every backend (Windows HWND swap-chain
            // included, where Avalonia.Media overlays are occluded). Only
            // runs when at least one toggle is on.
            if ((ShowGrid || ShowWatermark || _selectionBox.HasValue) && OperatingSystem.IsWindows())
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
            if (ShowPerfHud && OperatingSystem.IsWindows())
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
                    _overlay.CompositePerfHud(dst, w, h,
                        snap, HardwareProbe.Summary,
                        w, h, _calculator.MaxIterations, lbl);
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
            } // _uploadGate
        }

        /// <summary>
        /// Vectorized brightness/contrast inner loop. Processes 8 BGRA
        /// pixels per Vector256 step. Returns the next index to continue
        /// scalar processing from.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            for (; i <= simdEnd; i += vecLen)
            {
                var packed = Vector256.LoadUnsafe(ref src[i]);

                // Extract per-channel byte values into Vector256<int>.
                var bI = (packed & maskFF).AsInt32();
                var gI = ((packed >> 8)  & maskFF).AsInt32();
                var rI = ((packed >> 16) & maskFF).AsInt32();

                var b = Vector256.ConvertToSingle(bI);
                var g = Vector256.ConvertToSingle(gI);
                var r = Vector256.ConvertToSingle(rI);

                // (v - 127.5) * contrast + 127.5 + brightness255
                b = (b - halfV) * contrastV + halfV + brightnessV;
                g = (g - halfV) * contrastV + halfV + brightnessV;
                r = (r - halfV) * contrastV + halfV + brightnessV;

                // Clamp to [0, 255].
                b = Vector256.Max(zeroF, Vector256.Min(max255F, b));
                g = Vector256.Max(zeroF, Vector256.Min(max255F, g));
                r = Vector256.Max(zeroF, Vector256.Min(max255F, r));

                // Back to uint and pack into 0xFFRRGGBB layout.
                var bU = Vector256.ConvertToInt32(b).AsUInt32();
                var gU = Vector256.ConvertToInt32(g).AsUInt32() << 8;
                var rU = Vector256.ConvertToInt32(r).AsUInt32() << 16;
                var result = alpha | rU | gU | bU;

                result.StoreUnsafe(ref dst[i]);
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
        }

        /// <summary>
        /// Encode the most-recently-uploaded BGRA buffer to a PNG file at the
        /// given path. The saved image always carries the program watermark
        /// (region/theme + program-name sub-line) regardless of the on-screen
        /// <see cref="ShowWatermark"/> toggle — parity with the legacy
        /// WinForms screenshot flow in ImageCapture.cs. No-op when no frame
        /// has been rendered yet. Windows only — depends on System.Drawing.
        /// </summary>
        public void SaveLastFrameToPng(string path)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(path)) return;
            if (!OperatingSystem.IsWindows()) return;

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

            // T3.1: dispose the GPU compute kernel before the renderer so its
            // UAV / staging / cbuffer / CS releases hit the device first.
            try
            {
                _calculator.GpuKernel = null;
                _escapeCalculator.GpuKernel = null;
                _gpuKernel?.Dispose();
                _gpuKernel = null;
            }
            catch { }

            lock (_d3dGate) _renderer.Dispose();
        }
    }
}
