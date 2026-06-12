// UI.Avalonia/Slideshow/SlideshowEngine.cs
//
// Avalonia-side slideshow cycler with CPU cross-fade. Drives the shell-neutral
// IFractalRenderHost + IColorThemeService:
//
//   • Region transition — render the incoming region+theme to an offscreen BGRA
//     buffer (full calc, background thread), then blend the outgoing on-screen
//     frame into it over N steps before committing the live view.
//   • Theme transition (same region) — recolour the cached frame with the new
//     theme into an offscreen buffer (cheap, no recompute), then blend.
//
// The blend is a per-pixel CPU lerp pushed to the renderer via
// IFractalRenderHost.PresentBuffer — the same approach as the legacy WinForms
// slideshow, routed through the host instead of touching DirectXRenderer.
// Cross-fade is Mandelbrot-only (the offscreen helpers return null otherwise);
// non-Mandelbrot transitions fall back to a hard cut.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.Slideshow
{
    /// <summary>Timer-driven region + theme cycler with cross-fade.</summary>
    public sealed class SlideshowEngine
    {
        private readonly IFractalRenderHost _host;
        private readonly IColorThemeService _service;
        private SlideshowSettings _settings;
        private readonly Random _rng = new();

        private CancellationTokenSource? _cts;
        private volatile bool _paused;
        private volatile bool _skipRegion;
        private volatile bool _skipTheme;

        public SlideshowEngine(IFractalRenderHost host, IColorThemeService service, SlideshowSettings settings)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _settings = settings ?? new SlideshowSettings();
        }

        public bool IsRunning { get; private set; }
        public bool IsPaused => _paused;

        /// <summary>Replace the timing config used by the next iteration of
        /// the slideshow loop. Called before <see cref="Start"/> so user-saved
        /// SlideshowSettings (TotalDisplayMsPerRegion, FadeSteps, fade ms)
        /// take effect on each run without rebuilding the engine.</summary>
        public void ApplySettings(SlideshowSettings settings)
        {
            if (settings != null) _settings = settings;
        }

        /// <summary>Optional richer config — provides adaptive-sweep schedule,
        /// post-fx snapshot, and (eventually) include/filter sets. Null leaves
        /// the engine in pure-timing mode.</summary>
        public SlideshowConfig? Config { get; set; }

        /// <summary>Optional callback fired with the live Adaptive-slider value
        /// as the per-leg sweep ramp advances. Shell wires this to
        /// <c>FloatingMenu.Adaptive</c>.</summary>
        public Action<int>? AdaptiveValueSink { get; set; }

        /// <summary>Optional sink invoked with every BGRA frame the engine
        /// presents (one per cross-fade interpolation step + one per dwell
        /// commit). Shell sets this to feed a <c>PngSequenceWriter</c> when
        /// <c>SlideshowSettings.RecordSlideshow</c> is on. Buffer must be
        /// snapshotted by the sink — the engine reuses its blend array.</summary>
        public Action<uint[], int, int>? FrameSink { get; set; }

        public event EventHandler<string>? StatusChanged;
        public event EventHandler? Stopped;

        /// <summary>Fires after the engine applies a region (which may have
        /// overwritten ViewState.Quality with its own QualityPreset). The
        /// shell listens so it can mirror the new quality into the toolbar
        /// combo — otherwise the combo displays a stale value while the
        /// rendered view + saves use the region's quality.</summary>
        public event EventHandler<string>? RegionApplied;

        /// <summary>Fires after the engine applies a theme (region-jump leg
        /// or theme-only transition). Shell listens to mirror the new name
        /// into the toolbar + FloatingMenu Theme combos so the user sees
        /// which theme is currently rendering.</summary>
        public event EventHandler<string>? ThemeApplied;

        public void Start()
        {
            if (IsRunning) return;
            _paused = false;
            _skipRegion = false;
            _skipTheme = false;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }

        public void Stop() => _cts?.Cancel();
        public void TogglePause() => _paused = !_paused;
        public void SkipRegion() => _skipRegion = true;
        public void SkipTheme() => _skipTheme = true;

        /// <summary>When true, region-advance is suppressed: the cycler keeps
        /// the current region pinned and only rotates themes. Mirrors legacy
        /// MainForm._slideShowLockRegion (Shift+click on Slideshow button).</summary>
        public bool LockRegion { get; set; }

        /// <summary>Mirrors legacy MainForm._slideshowFocusRegion:
        /// true = "Region Focus" (3 themes/region, default);
        /// false = "Color Focus" (8 themes/region, shorter per-theme).
        /// Menu label shows the *next* action (what a click switches to).</summary>
        public bool FocusRegion { get; set; } = true;

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                var regions = ApplyRegionFilter(_service.EnumerateSlideshowRegionNames());
                if (regions == null || regions.Count == 0) return;

                int fadeSteps = Math.Clamp(_settings.FadeSteps, 2, 200);
                int regionStepMs = Math.Max(8, Math.Max(50, _settings.RegionFadeMs) / fadeSteps);
                int themeStepMs = Math.Max(8, Math.Max(50, _settings.ColorThemeFadeMs) / fadeSteps);

                int lastRegion = -1;
                string? heldRegion = null;

                while (!ct.IsCancellationRequested)
                {
                    string regionName;
                    if (LockRegion && heldRegion != null)
                    {
                        regionName = heldRegion;
                    }
                    else
                    {
                        int ri;
                        do { ri = _rng.Next(regions.Count); }
                        while (regions.Count > 1 && ri == lastRegion);
                        lastRegion = ri;
                        regionName = regions[ri];
                        heldRegion = regionName;
                    }

                    double zoom = _service.GetRegionZoom(regionName);
                    var themes = ApplyThemeFilter(_service.EnumerateThemeNamesForZoom(zoom));
                    int lastTheme = -1;

                    // Matches legacy Slideshow.cs cadence:
                    //   FocusRegion=true  (Region Focus) → 3 themes/region;
                    //   FocusRegion=false (Color Focus)  → 8 themes/region,
                    //                                      shorter per-theme duration.
                    // Re-read FocusRegion each iteration so a context-menu toggle
                    // mid-slideshow takes effect on the next theme step, not the
                    // next region (matches legacy focusChangedFunc).
                    int totalRegionMs = Math.Max(3_000, _settings.TotalDisplayMsPerRegion);

                    int t = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        int themesPerRegion = FocusRegion ? 3 : 8;
                        if (t >= themesPerRegion) break;

                        string? themeName = PickTheme(themes, ref lastTheme);

                        // All-black skip: peek-render the candidate region/theme
                        // at a tiny thumbnail. If every pixel is opaque black —
                        // theme paints in-set black + region is fully in-set, or
                        // iter depth too low at extreme zoom — retry up to one
                        // pass through the theme pool before giving up.
                        themeName = PickNonBlackTheme(regionName, themeName, themes, ref lastTheme, ct);

                        if (t == 0)
                            await RegionTransitionAsync(regionName, themeName, fadeSteps, regionStepMs, ct);
                        else
                            await ThemeTransitionAsync(themeName, fadeSteps, themeStepMs, ct);

                        StatusChanged?.Invoke(this,
                            $"Slideshow: {regionName}{(themeName != null ? " / " + themeName : "")}");

                        int themesPerRegionNow = FocusRegion ? 3 : 8;
                        int legMs = Math.Max(800, totalRegionMs / Math.Max(1, themesPerRegionNow));
                        using var legSweepCts = StartAdaptiveSweep(legMs, ct);

                        // themeMs is recomputed each WaitAsync tick so a
                        // FocusRegion toggle mid-theme shortens (or extends)
                        // the visible duration immediately.
                        if (await WaitAsync(
                            () => Math.Max(800, totalRegionMs / Math.Max(1, FocusRegion ? 3 : 8)),
                            ct)) break; // skip-region
                        if (ct.IsCancellationRequested) break;
                        t++;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal stop */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SlideshowEngine] loop error: {ex.Message}");
            }
            finally
            {
                await OnUiAsync(() =>
                {
                    IsRunning = false;
                    Stopped?.Invoke(this, EventArgs.Empty);
                    return 0;
                }, CancellationToken.None);
            }
        }

        // ── Filter helpers ────────────────────────────────────────────────
        // Intersect the eligibility set surfaced by the host with the include
        // list + metadata filters carried on Config. Null/empty filter = keep
        // everything. Returns the original list when every filter ends up
        // emptying the set so the engine's downstream "host produced zero
        // regions?" guard still fires (rather than silently skipping a leg).
        private IReadOnlyList<string> ApplyRegionFilter(IReadOnlyList<string> input)
        {
            var inc = Config?.IncludedRegions;
            var ft = Config?.FilterFractalTypes;
            var qp = Config?.FilterQualityPresets;
            bool hasInc = inc != null && inc.Count > 0;
            bool hasFt = ft != null && ft.Count > 0;
            bool hasQp = qp != null && qp.Count > 0;
            if (!hasInc && !hasFt && !hasQp) return input;

            var incSet = hasInc ? new HashSet<string>(inc!, StringComparer.OrdinalIgnoreCase) : null;
            var ftSet = hasFt ? new HashSet<string>(ft!, StringComparer.OrdinalIgnoreCase) : null;
            var qpSet = hasQp ? new HashSet<string>(qp!, StringComparer.OrdinalIgnoreCase) : null;

            var filtered = new List<string>(input.Count);
            foreach (var n in input)
            {
                if (incSet != null && !incSet.Contains(n)) continue;
                if (ftSet != null && !ftSet.Contains(_service.GetRegionFractalTypeName(n))) continue;
                if (qpSet != null && !qpSet.Contains(_service.GetRegionQualityPresetName(n))) continue;
                filtered.Add(n);
            }
            return filtered.Count > 0 ? filtered : input;
        }

        private IReadOnlyList<string>? ApplyThemeFilter(IReadOnlyList<string>? input)
        {
            if (input == null) return null;
            var inc = Config?.IncludedColorThemes;
            if (inc == null || inc.Count == 0) return input;
            var set = new HashSet<string>(inc, StringComparer.OrdinalIgnoreCase);
            var filtered = new List<string>(Math.Min(input.Count, inc.Count));
            foreach (var n in input)
                if (set.Contains(n)) filtered.Add(n);
            return filtered.Count > 0 ? filtered : input;
        }

        private string? PickTheme(IReadOnlyList<string>? themes, ref int lastTheme)
        {
            if (themes == null || themes.Count == 0) return null;
            int ti;
            do { ti = _rng.Next(themes.Count); }
            while (themes.Count > 1 && ti == lastTheme);
            lastTheme = ti;
            return themes[ti];
        }

        // Tiny offscreen probe — used by the all-black-leg skip path. Mandelbrot
        // regions get a 64×36 peek; anything else returns null (engine's
        // offscreen render is Mandelbrot-only) and the caller proceeds without
        // skipping.
        private const int PeekW = 64;
        private const int PeekH = 36;

        private static bool IsAllOpaqueBlack(uint[] buf)
        {
            const uint OpaqueBlack = 0xFF000000u;
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] != OpaqueBlack) return false;
            return true;
        }

        private string? PickNonBlackTheme(
            string regionName, string? themeName,
            IReadOnlyList<string>? themes, ref int lastTheme,
            CancellationToken ct)
        {
            if (themes == null || themes.Count == 0 || themeName == null) return themeName;
            int budget = Math.Max(1, themes.Count);
            for (int i = 0; i < budget; i++)
            {
                if (ct.IsCancellationRequested) return themeName;
                uint[]? probe;
                try { probe = _service.RenderRegionOffscreen(regionName, themeName, PeekW, PeekH); }
                catch { probe = null; }
                // Non-Mandelbrot region — no probe path, give up the skip.
                if (probe == null) return themeName;
                if (!IsAllOpaqueBlack(probe)) return themeName;

                StatusChanged?.Invoke(this,
                    $"Slideshow: skipping black {regionName} / {themeName}");
                themeName = PickTheme(themes, ref lastTheme);
                if (themeName == null) return null;
            }
            return themeName;
        }

        /// <summary>Region change: offscreen-render incoming, cross-fade, commit live.</summary>
        private async Task RegionTransitionAsync(string regionName, string? themeName, int steps, int stepMs, CancellationToken ct)
        {
            var (old, w, h) = await SnapshotAsync(ct);

            uint[]? incoming = (w > 0 && h > 0)
                ? await Task.Run(() => _service.RenderRegionOffscreen(regionName, themeName ?? string.Empty, w, h), ct)
                : null;

            if (old.Length > 0 && incoming != null && incoming.Length == old.Length)
            {
                await FadeAsync(old, incoming, w, h, steps, stepMs, ct);
            }
            else if (old.Length > 0 && w > 0 && h > 0)
            {
                // Non-Mandelbrot incoming region — RenderRegionOffscreen returns
                // null since the slideshow offscreen path is Mandelbrot-only.
                // Fall back to a fade-to-black on the outgoing buffer so the
                // transition isn't a hard cut (matches the cross-fade direction
                // the user already gets going non-Mandelbrot → Mandelbrot).
                var black = new uint[old.Length];
                for (int i = 0; i < black.Length; i++) black[i] = 0xFF000000u;
                await FadeAsync(old, black, w, h, steps, stepMs, ct);
            }
            else if (incoming != null && w > 0 && h > 0 && incoming.Length == w * h)
            {
                // Cold start — no frame uploaded yet (slideshow auto-launched
                // before the first interactive render landed). Fade in from
                // black to the offscreen-rendered incoming so the first leg
                // doesn't pop onto the screen. CommitRegionAsync's Trigger()
                // afterwards will re-present the production calc result; in
                // practice it matches the offscreen render closely enough
                // that the user sees a smooth fade-in.
                var black = new uint[incoming.Length];
                for (int i = 0; i < black.Length; i++) black[i] = 0xFF000000u;
                await FadeAsync(black, incoming, w, h, steps, stepMs, ct);
            }

            // Commit the live view to the new region+theme. Set the colour map
            // SILENTLY first (presenting here would recolour + flash the OUTGOING
            // region, whose smooth buffer is still live), then apply the region
            // and recompute. Wait for that recompute to actually land so a late
            // frame can't flash during the following theme transition.
            await CommitRegionAsync(regionName, themeName, ct);
        }

        /// <summary>Apply region + theme to the live view and wait for the
        /// recompute to present (or a timeout) before returning.</summary>
        private async Task CommitRegionAsync(string regionName, string? themeName, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource();
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                _host.AnimationFrameUploaded -= handler;
                tcs.TrySetResult();
            };

            await OnUiAsync(() =>
            {
                if (themeName != null) _service.ApplyThemeSilent(themeName);
                _service.ApplyRegion(regionName, _host.ViewState);
                // Push the new labels onto the render host BEFORE Trigger so
                // the very first composited frame already carries the right
                // watermark — otherwise the user sees one stale-watermark
                // frame before the shell's RegionApplied listener repaints.
                _host.RegionName = regionName;
                if (themeName != null) _host.ThemeName = themeName;
                _host.AnimationFrameUploaded += handler;
                _host.Trigger();
                return 0;
            }, ct);

            // Notify the shell so its combos can mirror whatever ApplyRegion
            // pushed into ViewState (quality + fractal type both can change).
            RegionApplied?.Invoke(this, regionName);
            if (themeName != null) ThemeApplied?.Invoke(this, themeName);

            // Recompute presents from a background continuation → AnimationFrameUploaded.
            var timeout = Task.Delay(8_000, ct);
            var done = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
            if (done == timeout) _host.AnimationFrameUploaded -= handler;
        }

        /// <summary>Theme change (same region): recolour offscreen, cross-fade.</summary>
        private async Task ThemeTransitionAsync(string? themeName, int steps, int stepMs, CancellationToken ct)
        {
            if (themeName == null) return;

            var (old, w, h) = await SnapshotAsync(ct);

            // Stamp the new theme label onto the render host BEFORE the recolour
            // so the next composited frame's watermark reflects the new theme.
            await OnUiAsync(() => { _host.ThemeName = themeName; return 0; }, ct);

            // Recolour returns the new buffer; null when the active fractal
            // has no cheap recolor → fall back to a plain apply. Runs on a
            // background thread (same pattern as RegionTransitionAsync's
            // RenderRegionOffscreen) so a slow non-Mandelbrot Calculate —
            // Sandbox / UserEquation / UserBulb can take seconds — does NOT
            // block the UI thread. When it did, the snapshot stayed frozen
            // on screen for the duration of the recalc and the fade
            // finished in the last 160 ms, which the user perceived as a
            // hard cut. Mandelbrot's Calculate is fast enough that the
            // pre-fix UI-thread path looked fine, masking the bug for
            // Mandel themes.
            uint[]? incoming = await Task.Run(
                () => _service.RenderThemeOffscreen(themeName!, w, h), ct);

            if (incoming == null)
            {
                await OnUiAsync(() => { _service.ApplyTheme(themeName!); return 0; }, ct);
                ThemeApplied?.Invoke(this, themeName);
                return;
            }

            if (old.Length > 0 && incoming.Length == old.Length)
                await FadeAsync(old, incoming, w, h, steps, stepMs, ct);
            else
            {
                await OnUiAsync(() => { _host.PresentBuffer(incoming, w, h); return 0; }, ct);
                EmitFrame(incoming, w, h);
            }

            // PresentBuffer / FadeAsync upload the recoloured buffer without
            // the watermark+grid overlay composite. Re-upload via
            // RepaintWithPostFx so the next visible frame carries the
            // updated theme name in the watermark.
            await OnUiAsync(() => { _host.RepaintWithPostFx(); return 0; }, ct);

            ThemeApplied?.Invoke(this, themeName);
        }

        /// <summary>Per-pixel CPU lerp from <paramref name="from"/> to
        /// <paramref name="to"/> over <paramref name="steps"/>, presenting each.</summary>
        private async Task FadeAsync(uint[] from, uint[] to, int w, int h, int steps, int stepMs, CancellationToken ct)
        {
            int n = w * h;
            if (from.Length < n || to.Length < n)
            {
                await OnUiAsync(() => { _host.PresentBuffer(to, w, h); return 0; }, ct);
                EmitFrame(to, w, h);
                return;
            }

            var blend = new uint[to.Length];
            for (int s = 1; s <= steps; s++)
            {
                if (ct.IsCancellationRequested) return;

                if (s == steps)
                {
                    // Final frame = exact target (stable reference for snapshots).
                    await OnUiAsync(() => { _host.PresentBuffer(to, w, h); return 0; }, ct);
                    EmitFrame(to, w, h);
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

                await OnUiAsync(() => { _host.PresentBuffer(blend, w, h); return 0; }, ct);
                EmitFrame(blend, w, h);
                try { await Task.Delay(stepMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Forward presented buffer to the optional recording sink. Sink owns
        // the snapshot — engine reuses the blend array on the next step, so
        // PngSequenceWriter.WriteFrame must copy before the next call.
        private void EmitFrame(uint[] bgra, int w, int h)
        {
            var sink = FrameSink;
            if (sink == null) return;
            try { sink(bgra, w, h); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SlideshowEngine] FrameSink failed: {ex.Message}");
            }
        }

        private Task<(uint[] Buffer, int W, int H)> SnapshotAsync(CancellationToken ct)
            => OnUiAsync(() =>
            {
                var b = _host.SnapshotFrame(out int sw, out int sh);
                return (b, sw, sh);
            }, ct);

        /// <summary>
        /// Wait <paramref name="ms"/> while honouring pause / skip-theme /
        /// skip-region / cancel. Returns true when skip-region fired.
        /// </summary>
        private Task<bool> WaitAsync(int ms, CancellationToken ct)
            => WaitAsync(() => ms, ct);

        /// <summary>
        /// Wait while honouring pause / skip-theme / skip-region / cancel.
        /// The target duration is re-evaluated each tick via
        /// <paramref name="msFunc"/> so a mid-wait FocusRegion toggle (which
        /// shortens / lengthens themeMs) is honoured immediately. Returns true
        /// when skip-region fired.
        /// </summary>
        private async Task<bool> WaitAsync(Func<int> msFunc, CancellationToken ct)
        {
            const int tick = 50;
            int elapsed = 0;
            while (elapsed < msFunc())
            {
                if (ct.IsCancellationRequested) return false;
                if (_skipRegion) { _skipRegion = false; return true; }
                if (_skipTheme) { _skipTheme = false; return false; }
                try { await Task.Delay(tick, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                if (!_paused) elapsed += tick;
            }
            return false;
        }

        // ── Adaptive sweep ────────────────────────────────────────────────
        //
        // Drives the FloatingMenu Adaptive slider over the lifetime of a leg
        // per <see cref="SlideshowConfig.AdaptiveSweep"/>. The shell wires
        // <see cref="AdaptiveValueSink"/> to <c>FloatingMenu.Adaptive</c>.
        // Returns a CTS that the caller should dispose to abort the sweep
        // when the leg ends early (skip / stop).
        private CancellationTokenSource StartAdaptiveSweep(int legMs, CancellationToken parentCt)
        {
            var legCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            var cfg = Config?.AdaptiveSweep;
            if (cfg == null || !cfg.Enabled || AdaptiveValueSink == null || legMs <= 0)
                return legCts;

            int start = Math.Clamp(cfg.Start, 0, 100);
            int end = Math.Clamp(cfg.End, 0, 100);
            var mode = cfg.Mode;
            bool loop = cfg.Loop;
            var sink = AdaptiveValueSink;
            var ct = legCts.Token;

            _ = Task.Run(async () =>
            {
                const int tickMs = 50;
                int elapsed = 0;
                while (!ct.IsCancellationRequested)
                {
                    double phase = legMs > 0 ? Math.Clamp(elapsed / (double)legMs, 0.0, 1.0) : 1.0;
                    int v = ComputeSweepValue(phase, start, end, mode);
                    try { await OnUiAsync(() => sink(v), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

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

        private static int ComputeSweepValue(double phase, int start, int end, AdaptiveSweepMode mode)
        {
            switch (mode)
            {
                case AdaptiveSweepMode.Reverse:
                    return Lerp(end, start, phase);
                case AdaptiveSweepMode.PingPong:
                    double pp = phase < 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0;
                    return Lerp(start, end, pp);
                case AdaptiveSweepMode.Forward:
                default:
                    return Lerp(start, end, phase);
            }
        }

        private static int Lerp(int a, int b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return (int)Math.Round(a + (b - a) * t);
        }

        private static Task OnUiAsync(Action action, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.CompletedTask;
            if (Dispatcher.UIThread.CheckAccess()) { action(); return Task.CompletedTask; }
            return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        }

        private static Task<T> OnUiAsync<T>(Func<T> func, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(default(T)!);
            if (Dispatcher.UIThread.CheckAccess()) return Task.FromResult(func());
            return Dispatcher.UIThread.InvokeAsync(func).GetTask();
        }
    }
}
