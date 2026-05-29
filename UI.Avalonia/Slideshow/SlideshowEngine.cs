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
        private readonly SlideshowSettings _settings;
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

        public event EventHandler<string>? StatusChanged;
        public event EventHandler? Stopped;

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

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                var regions = _service.EnumerateSlideshowRegionNames();
                if (regions == null || regions.Count == 0) return;

                const int themesPerRegion = 3;
                int totalRegionMs = Math.Max(3_000, _settings.TotalDisplayMsPerRegion);
                int themeMs = Math.Max(800, totalRegionMs / themesPerRegion);
                int fadeSteps = Math.Clamp(_settings.FadeSteps, 2, 200);
                int regionStepMs = Math.Max(8, Math.Max(50, _settings.RegionFadeMs) / fadeSteps);
                int themeStepMs = Math.Max(8, Math.Max(50, _settings.ColorThemeFadeMs) / fadeSteps);

                int lastRegion = -1;

                while (!ct.IsCancellationRequested)
                {
                    int ri;
                    do { ri = _rng.Next(regions.Count); }
                    while (regions.Count > 1 && ri == lastRegion);
                    lastRegion = ri;
                    string regionName = regions[ri];

                    double zoom = _service.GetRegionZoom(regionName);
                    var themes = _service.EnumerateThemeNamesForZoom(zoom);
                    int lastTheme = -1;

                    for (int t = 0; t < themesPerRegion && !ct.IsCancellationRequested; t++)
                    {
                        string? themeName = PickTheme(themes, ref lastTheme);

                        if (t == 0)
                            await RegionTransitionAsync(regionName, themeName, fadeSteps, regionStepMs, ct);
                        else
                            await ThemeTransitionAsync(themeName, fadeSteps, themeStepMs, ct);

                        StatusChanged?.Invoke(this,
                            $"Slideshow: {regionName}{(themeName != null ? " / " + themeName : "")}");

                        if (await WaitAsync(themeMs, ct)) break; // skip-region
                        if (ct.IsCancellationRequested) break;
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

        private string? PickTheme(IReadOnlyList<string>? themes, ref int lastTheme)
        {
            if (themes == null || themes.Count == 0) return null;
            int ti;
            do { ti = _rng.Next(themes.Count); }
            while (themes.Count > 1 && ti == lastTheme);
            lastTheme = ti;
            return themes[ti];
        }

        /// <summary>Region change: offscreen-render incoming, cross-fade, commit live.</summary>
        private async Task RegionTransitionAsync(string regionName, string? themeName, int steps, int stepMs, CancellationToken ct)
        {
            var (old, w, h) = await SnapshotAsync(ct);

            uint[]? incoming = (w > 0 && h > 0)
                ? await Task.Run(() => _service.RenderRegionOffscreen(regionName, themeName ?? string.Empty, w, h), ct)
                : null;

            if (old.Length > 0 && incoming != null && incoming.Length == old.Length)
                await FadeAsync(old, incoming, w, h, steps, stepMs, ct);

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
                _host.AnimationFrameUploaded += handler;
                _host.Trigger();
                return 0;
            }, ct);

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

            // Recolour happens on the UI thread (mutates the live frame) and
            // returns the new buffer; null when the active fractal has no cheap
            // recolor → fall back to a plain apply.
            uint[]? incoming = await OnUiAsync(() => _service.RenderThemeOffscreen(themeName!, w, h), ct);

            if (incoming == null)
            {
                await OnUiAsync(() => { _service.ApplyTheme(themeName!); return 0; }, ct);
                return;
            }

            if (old.Length > 0 && incoming.Length == old.Length)
                await FadeAsync(old, incoming, w, h, steps, stepMs, ct);
            else
                await OnUiAsync(() => { _host.PresentBuffer(incoming, w, h); return 0; }, ct);
        }

        /// <summary>Per-pixel CPU lerp from <paramref name="from"/> to
        /// <paramref name="to"/> over <paramref name="steps"/>, presenting each.</summary>
        private async Task FadeAsync(uint[] from, uint[] to, int w, int h, int steps, int stepMs, CancellationToken ct)
        {
            int n = w * h;
            if (from.Length < n || to.Length < n)
            {
                await OnUiAsync(() => { _host.PresentBuffer(to, w, h); return 0; }, ct);
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
                try { await Task.Delay(stepMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
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
        private async Task<bool> WaitAsync(int ms, CancellationToken ct)
        {
            const int tick = 50;
            int elapsed = 0;
            while (elapsed < ms)
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
