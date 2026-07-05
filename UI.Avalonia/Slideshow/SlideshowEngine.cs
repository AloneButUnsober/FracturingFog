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

using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.ViewModels.Animation;

namespace FracturingFog.UI.Avalonia.Slideshow
{
    /// <summary>Timer-driven region + theme cycler with cross-fade.</summary>
    public sealed class SlideshowEngine
    {
        private readonly IFractalRenderHost _host;
        private readonly IColorThemeService _service;
        private SlideshowSettings _settings;

        // RNG + region shuffle-bag. Both RNGs are reseeded on each Start from
        // SlideshowSettings.RandomSeed (0 = fresh entropy per run; non-zero =
        // reproducible). _regionRng is dedicated to the region bag so region
        // ordering is reproducible independent of how many draws theme picking
        // consumes (the solid-frame retry loop consumes a variable number).
        // _rng drives theme + animation picks. Both read through lambdas so a
        // Start-time reseed takes effect without rebuilding the bag delegate.
        // _regionBag draws every region once before repeating (no back-to-back).
        private Random _rng = new();
        private Random _regionRng = new();
        private readonly ShuffleBag<string> _regionBag;

        private CancellationTokenSource? _cts;
        private volatile bool _paused;
        private volatile bool _skipRegion;
        private volatile bool _skipTheme;

        // Audio-reactive beat counters — incremented by OnBeat, drained by
        // the slideshow loop via _skipRegion / _skipTheme. Lock guards both
        // counters so a region-skip atomically clears the theme counter too
        // (parity with WinForms MainForm.OnAudioBeat).
        private IBeatSource? _beatSource;
        private int _beatsSinceTheme;
        private int _beatsSinceRegion;
        private readonly object _beatLock = new();

        public SlideshowEngine(IFractalRenderHost host, IColorThemeService service, SlideshowSettings settings)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _settings = settings ?? new SlideshowSettings();
            _regionBag = new ShuffleBag<string>(n => _regionRng.Next(n), StringComparer.Ordinal);
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

        /// <summary>Optional live beat source. When set together with
        /// <see cref="SlideshowConfig.AudioReactive"/>=true on
        /// <see cref="Config"/>, each detected beat flips the engine's
        /// <c>_skipTheme</c> / <c>_skipRegion</c> flags once
        /// <see cref="BeatsPerTheme"/> / <see cref="BeatsPerRegion"/> have
        /// elapsed (parity with WinForms <c>MainForm.OnAudioBeat</c>). The
        /// adaptive-sweep tick rate also derives from this source's BPM when
        /// audio-reactive is on. Setting null detaches the handler.</summary>
        public IBeatSource? BeatSource
        {
            get => _beatSource;
            set
            {
                if (ReferenceEquals(_beatSource, value)) return;
                if (_beatSource != null) _beatSource.Beat -= OnBeat;
                _beatSource = value;
                lock (_beatLock) { _beatsSinceTheme = 0; _beatsSinceRegion = 0; }
                if (_beatSource != null) _beatSource.Beat += OnBeat;
            }
        }

        /// <summary>Number of beats between theme advances when
        /// audio-reactive is active. Default 8 (~2 bars at 4/4). Mirrors
        /// <c>AudioSettings.BeatsPerTheme</c>; the shell pushes it on Start.</summary>
        public int BeatsPerTheme { get; set; } = 8;

        /// <summary>Number of beats between region advances when
        /// audio-reactive is active. Default 32 (~8 bars). Mirrors
        /// <c>AudioSettings.BeatsPerRegion</c>; the shell pushes it on Start.</summary>
        public int BeatsPerRegion { get; set; } = 32;

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

            // Reseed per run so a fixed RandomSeed reproduces the same order
            // from the top, and 0 draws fresh entropy. The engine instance is
            // reused across Start/Stop toggles, so also reset the bag's carried
            // state — otherwise a second run draws from the previous run's
            // leftover shuffle instead of a fresh seeded one.
            int seed = _settings.RandomSeed;
            _rng = seed != 0 ? new Random(seed) : new Random();
            _regionRng = seed != 0 ? new Random(seed) : new Random();
            _regionBag.Reset();

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
                if (regions == null || regions.Count == 0)
                {
                    // Authoritative filter matched zero regions (or the host
                    // surfaced none). Surface it rather than silently showing
                    // excluded types; the finally block fires Stopped.
                    Console.Error.WriteLine(
                        "[SlideshowEngine] no regions match the active filter — slideshow not started.");
                    return;
                }

                int fadeSteps = Math.Clamp(_settings.FadeSteps, 2, 200);
                int regionStepMs = Math.Max(8, Math.Max(50, _settings.RegionFadeMs) / fadeSteps);
                int themeStepMs = Math.Max(8, Math.Max(50, _settings.ColorThemeFadeMs) / fadeSteps);

                string? heldRegion = null;

                while (!ct.IsCancellationRequested)
                {
                    // Re-enumerate every region pick so a region saved (or
                    // deleted) mid-slideshow joins (or leaves) the pool without
                    // an app restart. Cheap in-memory library read. Keep the
                    // previous pool if the fresh read comes back empty (e.g. a
                    // transient filter mismatch) so the loop never starves.
                    var fresh = ApplyRegionFilter(_service.EnumerateSlideshowRegionNames());
                    if (fresh != null && fresh.Count > 0) regions = fresh;

                    string regionName;
                    if (LockRegion && heldRegion != null)
                    {
                        regionName = heldRegion;
                    }
                    else
                    {
                        // Draw-without-replacement: every region shows once per
                        // cycle before any repeat, no back-to-back repeats. The
                        // bag rebuilds itself when `regions` membership changes
                        // (live pool refresh above).
                        regionName = _regionBag.Draw(regions);
                        heldRegion = regionName;
                    }

                    double zoom = _service.GetRegionZoom(regionName);
                    var themes = ApplyThemeFilter(_service.EnumerateThemeNamesForZoom(zoom));
                    int lastTheme = -1;

                    // Animation Roadmap Phase 4 — pick the leg's animation once
                    // per region (reused across the region's theme sub-legs).
                    // Null when Type != Animation or no library animation is
                    // compatible → the leg plays static (unchanged behaviour).
                    var legAnimation = ResolveLegAnimation(regionName);

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

                        // Solid-frame skip: peek-render the candidate region/theme
                        // at a tiny thumbnail. If every pixel is the same color —
                        // in-set black, in-set flat color, or iter depth too low
                        // at extreme zoom — retry up to one pass through the
                        // theme pool before giving up.
                        themeName = PickNonSolidTheme(regionName, themeName, themes, ref lastTheme, ct);

                        if (t == 0)
                            await RegionTransitionAsync(regionName, themeName, fadeSteps, regionStepMs, ct);
                        else
                            await ThemeTransitionAsync(themeName, fadeSteps, themeStepMs, ct);

                        StatusChanged?.Invoke(this,
                            $"Slideshow: {regionName}{(themeName != null ? " / " + themeName : "")}");

                        int themesPerRegionNow = FocusRegion ? 3 : 8;
                        int legMs = Math.Max(800, totalRegionMs / Math.Max(1, themesPerRegionNow));
                        var sweep = StartAdaptiveSweep(legMs, ct);

                        // Start the leg's animation on the shared bus AFTER the
                        // cross-fade committed, so the bus's live renders don't
                        // race the fade's snapshot/present. Stopped in finally
                        // (below) before the next transition for the same reason.
                        await StartLegAnimationAsync(legAnimation, ct);

                        // themeMs is recomputed each WaitAsync tick so a
                        // FocusRegion toggle mid-theme shortens (or extends)
                        // the visible duration immediately.
                        bool skipRegion;
                        try
                        {
                            skipRegion = await WaitAsync(
                                () => Math.Max(800, totalRegionMs / Math.Max(1, FocusRegion ? 3 : 8)),
                                ct);
                        }
                        finally
                        {
                            // Stop the leg animation before tearing down the
                            // sweep so the next transition's snapshot is a
                            // static frame (no bus render mid-fade).
                            await StopLegAnimationAsync(legAnimation);
                            // CTS.Dispose alone does NOT cancel the token —
                            // must explicitly Cancel + await the sweep Task
                            // or each leg leaks a Task.Run that keeps writing
                            // to AdaptiveValueSink. N legs = N racing tasks =
                            // slider jitter + Stop() can't reach them
                            // (linked CTS already disposed, parent-cancel
                            // callback unregistered).
                            try { sweep.Cts.Cancel(); } catch { /* already disposed */ }
                            try { await sweep.Task.ConfigureAwait(false); }
                            catch (OperationCanceledException) { /* expected */ }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[SlideshowEngine] sweep task failed: {ex.Message}");
                            }
                            sweep.Cts.Dispose();
                        }
                        if (skipRegion) break;
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
                    // Animation Roadmap Phase 4 — drop any leg animators still
                    // registered so the bus doesn't keep ticking after Stop.
                    var bus = AnimationBusHost.Bus;
                    if (bus != null) { bus.ClearDynamic(); bus.Refresh(); }
                    IsRunning = false;
                    Stopped?.Invoke(this, EventArgs.Empty);
                    return 0;
                }, CancellationToken.None);
            }
        }

        // ── Filter helpers ────────────────────────────────────────────────
        // Intersect the eligibility set surfaced by the host with the include
        // list + metadata filters carried on Config. Null/empty filter = keep
        // everything. When a filter IS active but matches zero regions the
        // result is genuinely empty — the filter is authoritative, so we do
        // NOT fall back to the full unfiltered universe (that silently played
        // excluded fractal types). The caller's zero-count guard then holds
        // the current pool (live refresh) or stops the loop (initial pick)
        // instead of showing regions the user filtered out.
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
            // A filter is active (the no-filter case early-returned above).
            // Return the filtered set as-is, even when empty: an unsatisfiable
            // filter means "no matching regions", not "show everything".
            return filtered;
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

        // Tiny offscreen probe — used by the solid-frame-leg skip path. Mandelbrot
        // regions get a 64×36 peek; anything else returns null (engine's
        // offscreen render is Mandelbrot-only) and the caller proceeds without
        // skipping.
        private const int PeekW = 64;
        private const int PeekH = 36;

        // Solid-color frame: all pixels equal the first. Catches in-set black
        // (the original case) plus themes that paint the in-set a non-black
        // flat color on a fully in-set region.
        private static bool IsAllOneColor(uint[] buf)
        {
            if (buf.Length == 0) return false;
            uint first = buf[0];
            for (int i = 1; i < buf.Length; i++)
                if (buf[i] != first) return false;
            return true;
        }

        private string? PickNonSolidTheme(
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
                if (!IsAllOneColor(probe)) return themeName;

                StatusChanged?.Invoke(this,
                    $"Slideshow: skipping solid {regionName} / {themeName}");
                themeName = PickTheme(themes, ref lastTheme);
                if (themeName == null) return null;
            }
            return themeName;
        }

        // ── Animation leg (Animation Roadmap Phase 4) ─────────────────────
        //
        // Resolve the animation for a region leg via the pure AnimationLegPicker
        // (region's attached animation, or a random type-compatible library
        // animation), then drive it on the shared AnimationBusHost during the
        // leg's hold. Returns null when Type != Animation or no animation
        // qualifies — the caller then plays a static leg.

        private AnimationData? ResolveLegAnimation(string regionName)
        {
            // Animation type always animates. Image (this engine also drives
            // the Image slideshow) opts in via EnableAnimations (Phase 5).
            // Video is a separate engine (FractalRenderHost.Video).
            bool animate = Config?.Type == SlideshowType.Animation
                || (Config?.EnableAnimations ?? false);
            if (!animate) return null;

            var names = _service.EnumerateAnimationNames();
            if (names == null || names.Count == 0) return null;

            var candidates = new List<AnimationLegPicker.Candidate>(names.Count);
            foreach (var n in names)
            {
                var data = _service.GetAnimation(n);
                if (data == null) continue;
                candidates.Add(new AnimationLegPicker.Candidate(
                    data.Name, data.TargetFractalTypes, data.Tags));
            }
            if (candidates.Count == 0) return null;

            string? chosen = AnimationLegPicker.Pick(
                candidates,
                _service.GetRegionFractalTypeName(regionName),
                _service.GetRegionAnimationName(regionName),
                Config?.RandomizeAnimationsByFractalType ?? false,
                Config?.IncludedAnimations,
                Config?.FilterAnimations,
                _rng.Next);

            if (string.IsNullOrEmpty(chosen))
            {
                StatusChanged?.Invoke(this,
                    $"Slideshow: no animation compatible with {regionName} — static leg");
                return null;
            }
            return _service.GetAnimation(chosen);
        }

        private Task StartLegAnimationAsync(AnimationData? animation, CancellationToken ct)
        {
            if (animation == null) return Task.CompletedTask;
            return OnUiAsync(() =>
            {
                AnimationBusHost.LoadRegionAnimation(
                    animation, _host.ViewState.FractalParameters);
                return 0;
            }, ct);
        }

        private Task StopLegAnimationAsync(AnimationData? animation)
        {
            if (animation == null) return Task.CompletedTask;
            // Use CancellationToken.None so the stop always runs even when the
            // leg's token was cancelled (Stop pressed) — otherwise the bus
            // keeps ticking against stale params after the slideshow ends.
            return OnUiAsync(() =>
            {
                var bus = AnimationBusHost.Bus;
                if (bus != null) { bus.ClearDynamic(); bus.Refresh(); }
                return 0;
            }, CancellationToken.None);
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
        // Returns the linked CTS plus the running Task — caller MUST Cancel
        // the CTS and await the Task before disposing, otherwise the sweep
        // leaks (CTS.Dispose does not cancel) and successive legs accumulate
        // racing tasks all writing to the slider.
        //
        // Audio-reactive mode (Config.AudioReactive=true + BeatSource set):
        //   • Cycle duration = BeatFraction × beatPeriodMs (recomputed each
        //     tick so BPM drift updates live; falls back to legMs when BPM
        //     is still 0).
        //   • Loop is forced true for the slideshow's lifetime — user spec.
        private (CancellationTokenSource Cts, Task Task) StartAdaptiveSweep(int legMs, CancellationToken parentCt)
        {
            var legCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            var cfg = Config?.AdaptiveSweep;
            if (cfg == null || !cfg.Enabled || AdaptiveValueSink == null || legMs <= 0)
                return (legCts, Task.CompletedTask);

            int start = Math.Clamp(cfg.Start, 0, 100);
            int end = Math.Clamp(cfg.End, 0, 100);
            var mode = cfg.Mode;
            bool audioReactive = Config?.AudioReactive == true && _beatSource != null;
            bool loop = audioReactive || cfg.Loop;
            double beatFrac = Math.Clamp(cfg.BeatFraction, 0.0625, 32.0);
            var sink = AdaptiveValueSink;
            var ct = legCts.Token;

            var task = Task.Run(async () =>
            {
                const int tickMs = 50;
                int elapsed = 0;
                int currentCycleMs = ResolveSweepCycleMs(legMs, audioReactive, beatFrac);
                while (!ct.IsCancellationRequested)
                {
                    // Recompute the cycle duration each tick so audio-reactive
                    // mode tracks live BPM drift without waiting for the next
                    // leg boundary. Cheap (one BeatSource read + one divide).
                    currentCycleMs = ResolveSweepCycleMs(legMs, audioReactive, beatFrac);

                    double phase = currentCycleMs > 0
                        ? Math.Clamp(elapsed / (double)currentCycleMs, 0.0, 1.0)
                        : 1.0;
                    int v = ComputeSweepValue(phase, start, end, mode);
                    try { await OnUiAsync(() => sink(v), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    if (elapsed >= currentCycleMs)
                    {
                        if (!loop) return;
                        elapsed = 0;
                    }
                    try { await Task.Delay(tickMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    elapsed += tickMs;
                }
            }, ct);

            return (legCts, task);
        }

        // Resolve full-sweep cycle duration. Audio-reactive: beatFrac × beatPeriod
        // (clamped to a sane floor so wild BPM jumps don't cause runaway ticks).
        // Wall-clock fallback (or BPM not yet detected): the legMs envelope.
        private int ResolveSweepCycleMs(int legMs, bool audioReactive, double beatFrac)
        {
            if (audioReactive && _beatSource != null)
            {
                double bpm = _beatSource.EstimatedBpm;
                if (bpm > 0)
                {
                    double beatMs = 60_000.0 / bpm;
                    return Math.Max(50, (int)Math.Round(beatMs * beatFrac));
                }
            }
            return legMs;
        }

        // ── Beat → skip-flag bridge ───────────────────────────────────────
        //
        // Fires on a capture / analyzer thread. Increments per-leg counters
        // and trips _skipTheme / _skipRegion when the configured beat counts
        // elapse; the slideshow loop's WaitAsync consumes those flags on its
        // next 50 ms tick. Region-skip wins over theme-skip and clears both
        // counters (matches WinForms MainForm.OnAudioBeat semantics).
        private void OnBeat(object? sender, BeatEventArgs e)
        {
            if (Config?.AudioReactive != true) return;
            int bTheme, bRegion;
            lock (_beatLock)
            {
                _beatsSinceTheme++;
                _beatsSinceRegion++;
                bTheme = _beatsSinceTheme;
                bRegion = _beatsSinceRegion;
            }
            int perRegion = Math.Max(1, BeatsPerRegion);
            int perTheme = Math.Max(1, Math.Min(BeatsPerTheme, perRegion));
            if (bRegion >= perRegion)
            {
                lock (_beatLock) { _beatsSinceRegion = 0; _beatsSinceTheme = 0; }
                _skipRegion = true;
                return;
            }
            if (bTheme >= perTheme)
            {
                lock (_beatLock) _beatsSinceTheme = 0;
                _skipTheme = true;
            }
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
