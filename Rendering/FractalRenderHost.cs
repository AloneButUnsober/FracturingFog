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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Render;
using FracturingFog.ViewState;

namespace FracturingFog.Rendering
{
    /// <inheritdoc/>
    public sealed class FractalRenderHost : IFractalRenderHost
    {
        private readonly IFractalRenderer _renderer;

        // Per-fractal-type calculators. MandelbrotCalculator is the canonical
        // primary; everything else is "alt" and selected by FractalType.
        private MandelbrotCalculator _calculator;
        private EscapeTimeCalculator _escapeCalculator;
        private IFSCalculator _ifsCalculator;
        private LSystemCalculator _lsystemCalculator;
        private AttractorCalculator _attractorCalculator;
        private BuddhabrotCalculator _buddhabrotCalculator;
        private NewtonCalculator _newtonCalculator;
        private UserEquationCalculator _userEquationCalculator;
        private MandelbulbCalculator _mandelbulbCalculator;
        private SandboxCalculator _sandboxCalculator;
        private UserBulbCalculator _userBulbCalculator;
        private TearDropCalculator _tearDropCalculator;

        private CancellationTokenSource? _calcCts;
        private readonly object _calcLock = new();

        // Serialises every call into the D3D11 ImmediateContext. The
        // immediate context is NOT thread-safe; before this lock landed,
        // resize on the UI thread could overlap with UpdateTexture from a
        // calc-continuation thread and the upcoming auto-present, locking
        // the driver. Every _renderer.* call inside this class — and the
        // public Present() entry point — must take this lock.
        private readonly object _d3dGate = new();

        // Cached previous frame — re-uploaded on the next trigger so the
        // user sees the stale (correct) image while the next one calculates,
        // instead of black flashes at High/Ultra quality.
        private uint[]? _lastUploadedBuffer;
        private int _lastUploadedWidth;
        private int _lastUploadedHeight;

        private bool _disposed;

        public FractalRenderHost(IFractalRenderer renderer, FractalViewState state, int width, int height, IColorMap initialColorMap)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            ViewState = state ?? throw new ArgumentNullException(nameof(state));
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);

            _calculator = new MandelbrotCalculator(w, h);
            _escapeCalculator = new EscapeTimeCalculator(w, h);
            _ifsCalculator = new IFSCalculator(w, h);
            _lsystemCalculator = new LSystemCalculator(w, h);
            _attractorCalculator = new AttractorCalculator(w, h);
            _buddhabrotCalculator = new BuddhabrotCalculator(w, h);
            _newtonCalculator = new NewtonCalculator(w, h);
            _userEquationCalculator = new UserEquationCalculator(w, h);
            _mandelbulbCalculator = new MandelbulbCalculator(w, h);
            _sandboxCalculator = new SandboxCalculator(w, h);
            _userBulbCalculator = new UserBulbCalculator(w, h);
            _tearDropCalculator = new TearDropCalculator(w, h);

            if (initialColorMap != null)
            {
                _calculator.ColorMap = initialColorMap;
                _escapeCalculator.ColorMap = initialColorMap;
                _ifsCalculator.ColorMap = initialColorMap;
                _lsystemCalculator.ColorMap = initialColorMap;
                _attractorCalculator.ColorMap = initialColorMap;
                _buddhabrotCalculator.ColorMap = initialColorMap;
                _newtonCalculator.ColorMap = initialColorMap;
                _userEquationCalculator.ColorMap = initialColorMap;
                _mandelbulbCalculator.ColorMap = initialColorMap;
                _sandboxCalculator.ColorMap = initialColorMap;
                _userBulbCalculator.ColorMap = initialColorMap;
                _tearDropCalculator.ColorMap = initialColorMap;
            }
        }

        public FractalViewState ViewState { get; }

        public event EventHandler<RenderFrameInfo>? FrameCompleted;
        public event EventHandler? AnimationFrameUploaded;
        public event EventHandler<string>? StatusRequested;

        /// <summary>The renderer this host drives. Exposed so the shell can
        /// call Render() in its idle loop.</summary>
        public IFractalRenderer Renderer => _renderer;

        /// <summary>The primary MandelbrotCalculator — exposed so the shell
        /// can plumb HP-diagnostic toggles (Ctrl+Shift+S / +A) and any other
        /// engine-specific knobs that have not yet been lifted into the
        /// view-state contract.</summary>
        public MandelbrotCalculator Mandelbrot => _calculator;

        /// <summary>Mutable colour map applied across all calculators. Setting
        /// this updates every alt calculator so a theme switch is a single
        /// assignment from the caller's perspective.</summary>
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
                _newtonCalculator.ColorMap = value;
                _userEquationCalculator.ColorMap = value;
                _mandelbulbCalculator.ColorMap = value;
                _sandboxCalculator.ColorMap = value;
                _userBulbCalculator.ColorMap = value;
                _tearDropCalculator.ColorMap = value;
            }
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

        public void Trigger(bool progressive = false)
        {
            if (_disposed) return;

            CancellationTokenSource cts;
            lock (_calcLock)
            {
                _calcCts?.Cancel();
                _calcCts = new CancellationTokenSource();
                cts = _calcCts;
            }

            // Stale-frame re-upload so the screen shows a correct (if old)
            // image while the next frame computes. Locked + presented so the
            // user sees the prev frame immediately even if the next calc
            // takes seconds.
            if (_lastUploadedBuffer != null
                && _lastUploadedWidth == _calculator.Width
                && _lastUploadedHeight == _calculator.Height)
            {
                lock (_d3dGate)
                {
                    _renderer.UpdateTexture(_lastUploadedBuffer, _lastUploadedWidth, _lastUploadedHeight);
                    _renderer.Render();
                }
            }

            ApplyView();

            var token = cts.Token;
            var calc = _calculator;
            IFractalCalculator? altCalc = SelectAltCalculator(ViewState.FractalType);
            bool useAlt = altCalc != null;

            if (useAlt)
            {
                altCalc!.CenterX = calc.CenterX;
                altCalc.CenterY = calc.CenterY;
                altCalc.Zoom = calc.Zoom;
                altCalc.MaxIterations = calc.MaxIterations;
                altCalc.Quality = calc.Quality;
                altCalc.ColorMap = calc.ColorMap;
                switch (altCalc)
                {
                    case EscapeTimeCalculator e:
                        e.FractalType = ViewState.FractalType;
                        e.FractalParameters = ViewState.FractalParameters;
                        break;
                    case IFSCalculator ifs: ifs.FractalParameters = ViewState.FractalParameters; break;
                    case LSystemCalculator ls: ls.FractalParameters = ViewState.FractalParameters; break;
                    case AttractorCalculator a: a.FractalParameters = ViewState.FractalParameters; break;
                    case BuddhabrotCalculator b: b.FractalParameters = ViewState.FractalParameters; break;
                    case NewtonCalculator n: n.FractalParameters = ViewState.FractalParameters; break;
                    case UserEquationCalculator u: u.FractalParameters = ViewState.FractalParameters; break;
                    case MandelbulbCalculator m: m.FractalParameters = ViewState.FractalParameters; break;
                    case SandboxCalculator sb: sb.FractalParameters = ViewState.FractalParameters; break;
                    case UserBulbCalculator ub: ub.FractalParameters = ViewState.FractalParameters; break;
                }
            }

            StatusRequested?.Invoke(this, "Calculating…");
            var sw = Stopwatch.StartNew();

            Task.Run(() =>
            {
                if (useAlt) altCalc!.Calculate(token);
                else calc.Calculate(token);
                return sw.ElapsedMilliseconds;
            }, token)
            .ContinueWith(t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested)
                {
                    // Cancelled render still counts as "done" for animation
                    // gating — otherwise a mid-animation cancel would leave
                    // the gate stuck.
                    AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (_disposed) return;

                long ms = t.IsCompletedSuccessfully ? t.Result : -1;

                // Adaptive contrast — Mandelbrot only.
                if (!useAlt && ViewState.HistogramEq > 0)
                    calc.ApplyHistogramEqualization(ViewState.HistogramEq / 100.0);

                if (useAlt)
                    UploadProcessedBuffer(altCalc!.ColorBuffer, altCalc.Width, altCalc.Height);
                else
                    UploadProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height);

                bool hp = !useAlt && calc.IsHighPrecisionActive;
                int curW = useAlt ? altCalc!.Width : calc.Width;
                int curH = useAlt ? altCalc!.Height : calc.Height;
                int curIter = useAlt ? altCalc!.MaxIterations : calc.MaxIterations;
                double curCx = useAlt ? altCalc!.CenterX : calc.CenterX;
                double curCy = useAlt ? altCalc!.CenterY : calc.CenterY;
                double curZoom = useAlt ? altCalc!.Zoom : calc.Zoom;

                FrameCompleted?.Invoke(this, new RenderFrameInfo(
                    curCx, curCy, curZoom, curIter, ms, curW, curH,
                    hp, ViewState.IterLocked, ViewState.FractalType));

                AnimationFrameUploaded?.Invoke(this, EventArgs.Empty);
            }, TaskScheduler.Default);
        }

        // ── Resize ────────────────────────────────────────────────────────────

        public void Resize(int width, int height)
        {
            if (_disposed) return;
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            _lastUploadedBuffer = null;

            lock (_d3dGate)
            {
                _renderer.Resize(w, h);
                // Present once so the new back-buffer dimensions become
                // visible immediately even before the next calc finishes.
                _renderer.Render();
            }
            _calculator.Resize(w, h);
            _escapeCalculator.Resize(w, h);
            _ifsCalculator.Resize(w, h);
            _lsystemCalculator.Resize(w, h);
            _attractorCalculator.Resize(w, h);
            _buddhabrotCalculator.Resize(w, h);
            _newtonCalculator.Resize(w, h);
            _userEquationCalculator.Resize(w, h);
            _mandelbulbCalculator.Resize(w, h);
            _sandboxCalculator.Resize(w, h);
            _userBulbCalculator.Resize(w, h);
            _tearDropCalculator.Resize(w, h);

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

        // ── Internals ─────────────────────────────────────────────────────────

        private IFractalCalculator? SelectAltCalculator(FractalType type) => type switch
        {
            FractalType.Mandelbrot => null,
            FractalType.Julia => _escapeCalculator,
            FractalType.BurningShip => _escapeCalculator,
            FractalType.Tricorn => _escapeCalculator,
            FractalType.Multibrot => _escapeCalculator,
            FractalType.Phoenix => _escapeCalculator,
            FractalType.IFS => _ifsCalculator,
            FractalType.LSystem => _lsystemCalculator,
            FractalType.StrangeAttractor => _attractorCalculator,
            FractalType.BuddhaBrot => _buddhabrotCalculator,
            FractalType.Newton => _newtonCalculator,
            FractalType.Nova => _newtonCalculator,
            FractalType.UserEquation => _userEquationCalculator,
            FractalType.Mandelbulb => _mandelbulbCalculator,
            FractalType.Sandbox => _sandboxCalculator,
            FractalType.UserBulb => _userBulbCalculator,
            FractalType.TearDrop => _tearDropCalculator,
            _ => null
        };

        /// <summary>Pure CPU brightness + contrast pass over a BGRA uint[]
        /// followed by an upload to the renderer. Grid + watermark overlays
        /// are intentionally omitted in this host — they will be drawn by
        /// the Avalonia shell with Avalonia.Media in step F.</summary>
        private void UploadProcessedBuffer(uint[] src, int w, int h)
        {
            int n = w * h;
            var dst = new uint[n];

            int brightness = ViewState.Brightness;
            int contrast = ViewState.Contrast;
            bool needsProcess = brightness != 0 || contrast != 0;

            if (needsProcess)
            {
                float contrastFactor = 1.0f + contrast / 100.0f;
                float brightnessOffset = brightness / 100.0f;

                for (int i = 0; i < n; i++)
                {
                    uint p = src[i];
                    float r = ((p >> 16) & 0xFF) / 255f;
                    float g = ((p >> 8) & 0xFF) / 255f;
                    float b = (p & 0xFF) / 255f;

                    r = (r - 0.5f) * contrastFactor + 0.5f;
                    g = (g - 0.5f) * contrastFactor + 0.5f;
                    b = (b - 0.5f) * contrastFactor + 0.5f;

                    r += brightnessOffset;
                    g += brightnessOffset;
                    b += brightnessOffset;

                    byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                    byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
                    byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
                    dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                }
            }
            else
            {
                Array.Copy(src, dst, n);
            }

            lock (_d3dGate)
            {
                _renderer.UpdateTexture(dst, w, h);
                _renderer.Render();
            }
            _lastUploadedBuffer = dst;
            _lastUploadedWidth = w;
            _lastUploadedHeight = h;
        }

        /// <inheritdoc/>
        public void Present()
        {
            if (_disposed) return;
            lock (_d3dGate) _renderer.Render();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_calcLock) _calcCts?.Cancel();
            lock (_d3dGate) _renderer.Dispose();
        }
    }
}
