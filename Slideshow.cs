using FracturingFog.Interefaces;
using FracturingFog.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static FracturingFog.Views.FormHelpers;

namespace FracturingFog
{
    public sealed partial class MainForm
    {
        // Timing
        //   • Each region is shown for 30 s total.
        //   • Within each region the colour theme changes every 10 s.
        //   • Theme and region transitions use a 2-second CPU cross-fade:
        //     both the outgoing and incoming colour buffers are rendered and
        //     blended pixel-by-pixel over 20 frames at ~100 ms each.
        //
        // Cross-fade implementation
        //   Since the frame is ultimately a uint[] BGRA buffer delivered to
        //   DirectXRenderer.UpdateTexture, a per-pixel lerp between old and new
        //   buffers is trivially achievable on the CPU without any GPU blend state
        //   changes.  The calculator always runs on a background thread; the fade
        //   itself is done on the background thread too and the blended frames are
        //   posted to the UI via Invoke.

        private void OnSlideshowClick(object? sender, EventArgs e)
        {
            if (_slideshowRunning)
                StopSlideshow();
            else
                StartSlideshow();
        }

        private void StartSlideshow()
        {
            if (_slideshowRunning) return;
            _slideshowRunning = true;
            _showSlideshowWatermark = true;
            //_chkSlideshowUseExtremeRegions.Enabled = false;
            RepaintWithBrightnessContrast();
            _slideshowButton.Text = "■ Stop";
            _slideshowButton.BackColor = Color.FromArgb(70, 30, 30);
            _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(120, 50, 50);
            SetStatus("Slideshow running…");
            ShowVcrForSlideshow();
            NotifySlideshowStarted();

            CancellationTokenSource cts;
            lock (_slideshowLock)
            {
                _slideshowCts?.Cancel();
                _slideshowCts = new CancellationTokenSource();
                cts = _slideshowCts;
            }

            Task.Run(() => SlideshowLoop(cts.Token,
                () => IsSlideshowRegionLocked(),
                () => IsSkipSlideshowRegion(),
                () => SlideshowFocusChanged()), cts.Token)
                .ContinueWith(t =>
                {
                    if (!IsHandleCreated || _disposed) return;
                    Invoke(() =>
                    {
                        _slideshowRunning = false;
                        _showSlideshowWatermark = false;
                        _slideshowPaused = false;
                        _slideshowRegionName = "";
                        //_chkSlideshowUseExtremeRegions.Enabled = true;
                        RepaintWithBrightnessContrast();
                        _slideshowButton.Text = "Slideshow";
                        _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
                        _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
                        HideVcrPanel();
                        NotifySlideshowStopped();
                        if (!t.IsCanceled && t.IsFaulted)
                            SetStatus($"Slideshow error: {t.Exception?.InnerException?.Message}");
                        else
                            SetStatus("Slideshow stopped.");
                    });
                }, TaskScheduler.Default);
        }

        private void StopSlideshow()
        {
            lock (_slideshowLock) _slideshowCts?.Cancel();
        }

        private void ToggleSlideshowRegionLock()
        {
            _slideShowLockRegion = !_slideShowLockRegion;
        }

        public bool IsSlideshowRegionLocked()
        {
            lock (_slideshowLock) return _slideShowLockRegion;
        }

        private void SkipSlideshowRegion()
        {
            lock (_slideshowLock)
            {
                _slideshowSkipRegion = true;
                SetStatus("Slideshow: skipping to next region…");
            }
        }

        private void SkipSlideshowTheme()
        {
            lock (_slideshowLock)
            {
                _slideshowSkipTheme = true;
                SetStatus("Slideshow: skipping to next color theme…");
            }
        }

        private void ToggleSlideshowPause()
        {
            lock (_slideshowLock)
            {
                _slideshowPaused = !_slideshowPaused;
                SetStatus(_slideshowPaused ? "Slideshow: paused" : "Slideshow: resumed");
            }
        }

        public bool IsSlideshowPaused()
        {
            lock (_slideshowLock) return _slideshowPaused;
        }

        private bool SlideshowFocusChanged()
        {
            lock (_slideshowLock)
            {
                return _slideshowFocusRegion;
            }
        }

        public bool IsSkipSlideshowRegion()
        {
            lock (_slideshowLock)
            {
                if (_slideshowSkipRegion)
                {
                    _slideshowSkipRegion = false;
                    return true;
                }
                return false;
            }
        }

        public bool IsSkipSlideshowTheme()
        {
            lock (_slideshowLock)
            {
                if (_slideshowSkipTheme)
                {
                    _slideshowSkipTheme = false;
                    return true;
                }
                return false;
            }
        }

        private bool PeekSkipSlideshowRegion()
        {
            lock (_slideshowLock) return _slideshowSkipRegion;
        }

        private bool PeekSkipSlideshowTheme()
        {
            lock (_slideshowLock) return _slideshowSkipTheme;
        }

        // Returns all palettes whose Name does not start with "— " (i.e. header items excluded).
        private List<string> GetAllPaletteNames()
        {
            var names = new List<string>();
            foreach (var item in _colorThemeCombo.Items)
            {
                string s = item?.ToString() ?? "";
                if (!s.StartsWith("—")) names.Add(s);
            }
            return names;
        }

        private async Task SlideshowLoop(CancellationToken ct,
            Func<bool> regionLockFunc,
            Func<bool> skipRegionFunc,
            Func<bool> focusChangedFunc)
        {
            var builtIns = new List<FractalRegion>(FractalRegionLibrary.Instance.AllSlideshowRegions);
            if (builtIns.Count == 0) return;
            // paletteNames is rebuilt per region so themes whose
            // MaxRecommendedZoom is below the region's zoom are excluded.
            // Initialised here so loop fields referencing it bind cleanly; the
            // first region iteration always overwrites it.
            var paletteNames = Models.ColorPalette.GetPaletteNamesForZoom(0.0);
            if (paletteNames.Count == 0) return;

            // Timing design:
            // Region foucs:
            //   Each region shows exactly 3 colour themes.
            //   Each theme is visible for themeDurationMs, then a fadeDurationMs cross-fade
            //   transitions to the next theme (or the next region after the 3rd theme).
            //   The fade is counted as part of the *outgoing* theme's slot, so the
            //   incoming theme gets its full themeDurationMs of uninterrupted display.
            //
            // Color focus:
            //  Each region shows 8 color themes

            // Region Focus mode timings: fewer themes per region, longer durations, shorter fade for a calmer, more contemplative slideshow when the region is the main changing element.
            // When audio-reactive is active the theme duration is a soft upper bound:
            // the beat handler flips _slideshowSkipTheme to advance early. The cap stays
            // identical to the non-audio value so a silent / undetectable source still
            // advances at the normal cadence instead of stalling for a minute.
            int themesPerRegion = 3;
            int themeDurationMs = 12_000;
            int fadeDurationMs = 2_000;   // 2 s cross-fade (overlaps end of theme slot)
            int fadeSteps = 22;
            int fadeStepMs = fadeDurationMs / fadeSteps;

            // Color Focus mode timings: more themes per region, shorter durations, longer fade for more visual interest when the theme is the main changing element.
            int themesPerRegionCF = 8;
            int themeDurationMsCF = 3_000;
            int fadeDurationMsCF = 4_000;   // 4 s cross-fade (overlaps end of theme slot)
            int fadeStepsCF = 44;
            int fadeStepMsCF = fadeDurationMsCF / fadeStepsCF;

            // Beat-derived fade override: when audio-reactive + BPM known, use
            // half a beat for the region-focus fade and one full beat for the
            // color-focus fade. Step count fixed at ~22/44; step ms recomputed.
            if (ShouldUseBeatDrivenTiming())
            {
                double bpm = GetReactiveBpm();
                if (bpm > 30)
                {
                    double beatMs = 60_000.0 / bpm;
                    fadeDurationMs = System.Math.Max(200, (int)(beatMs * 0.5));
                    fadeStepMs = System.Math.Max(10, fadeDurationMs / fadeSteps);
                    fadeDurationMsCF = System.Math.Max(400, (int)beatMs);
                    fadeStepMsCF = System.Math.Max(10, fadeDurationMsCF / fadeStepsCF);
                }
            }
            int lastRegionIdx = -1;
            int lastThemeIdx = -1;
            int renderCounter = 0;
            int regionIdx = -1;
            bool[] regionsUsed = new bool[builtIns.Count];
            FractalRegion? lockedRegion = null;
            bool focusRegion = true; // starts in Region Focus mode

            while (!ct.IsCancellationRequested)
            {
                FractalRegion region;
                if (regionLockFunc() && lockedRegion != null)
                {
                    region = lockedRegion;
                }
                else
                {
                    // ── Pick a new region different from the last ─────────────────────
                    do { regionIdx = _slideshowRng.Next(builtIns.Count); }
                    while (builtIns.Count > 1 && regionIdx == lastRegionIdx);
                    lastRegionIdx = regionIdx;
                    if (regionsUsed[regionIdx]) continue;
                    region = builtIns[regionIdx];
                    renderCounter = 0;   // reset theme counter when moving to a new region
                }

                lockedRegion = region;

                // Refresh the palette pool for this region's zoom so themes
                // whose MaxRecommendedZoom is below region.Zoom are excluded.
                paletteNames = Models.ColorPalette.GetPaletteNamesForZoom(region.Zoom);
                if (paletteNames.Count == 0) return;
                lastThemeIdx = -1;   // pool changed — clear "different from last" anchor

                string lockStatus = regionLockFunc() ? "(L)" : "";
                // Mark the just-used region to avoid immediate repeats until all have been shown.
                if (!regionLockFunc())
                {
                    regionsUsed[regionIdx] = true;
                    if (regionsUsed.All(u => u)) Array.Clear(regionsUsed, 0, regionsUsed.Length);
                }

                // ── Pick an initial theme ─────────────────────────────────────────
                int themeIdx;
                do { themeIdx = _slideshowRng.Next(paletteNames.Count); }
                while (paletteNames.Count > 1 && themeIdx == lastThemeIdx);
                lastThemeIdx = themeIdx;
                string themeName = paletteNames[themeIdx];

                // ── Render the new region with the initial theme ───────────────────
                uint[]? previousBuffer = null;
                if (_calculator != null && _renderer != null &&
                    (!regionLockFunc() || renderCounter < 1))
                {
                    if (!regionLockFunc()) renderCounter = 0;   // reset counter when moving to a new region, if not locking
                                                                // ── FIX: snapshot the current on-screen buffer NOW, before any
                                                                //    region/theme state is changed.  _lastUploadedBuffer always
                                                                //    holds the most-recently-uploaded post-processed frame and is
                                                                //    updated on the UI thread, so reading it here (still on the
                                                                //    background slideshow task, but before any Invoke) is safe
                                                                //    because we copy it under no concurrent mutation.
                    uint[] oldBuf;
                    lock (_calcLock)   // brief lock to avoid racing with TriggerCalculation
                    {
                        if (_lastUploadedBuffer != null
                            && _calculator != null
                            && _lastUploadedWidth == _calculator.Width
                            && _lastUploadedHeight == _calculator.Height)
                        {
                            Debug.WriteLine($"SldShwLp: Capturing old buffer for cross-fade. " +
                            $"Last uploaded buffer: Length: {_lastUploadedBuffer.Length} pixels, size {_lastUploadedWidth}×{_lastUploadedHeight}");
                            oldBuf = new uint[_lastUploadedBuffer.Length];
                            _lastUploadedBuffer.CopyTo(oldBuf, 0);
                        }
                        else if (_calculator != null)
                        {
                            Debug.WriteLine($"SldShwLp: Falling back to direct ColorBuffer copy of {_calculator.ColorBuffer.Length} pixels");
                            oldBuf = new uint[_calculator.ColorBuffer.Length];
                            _calculator.ColorBuffer.CopyTo(oldBuf, 0);
                        }
                        else
                        {
                            oldBuf = Array.Empty<uint>();
                        }
                    }

                    // Apply region & theme on UI thread WITHOUT triggering a
                    // normal TriggerCalculation — we manage rendering ourselves.
                    if (ct.IsCancellationRequested) return;
                    await InvokeAsync(() =>
                    {
                        if (_disposed) return;
                        _slideshowRegionName = region.Name;
                        ApplyRegionSilent(region);
                        //var map = Models.ColorPalette.GetPaletteByName(themeName);
                        //if (_calculator != null) _calculator.ColorMap = map;
                        SuppressedSetRegionCombo(region.Name);
                        ApplyColorThemeSilent(themeName);
                        SetStatus($"Slideshow: {region.Name} {lockStatus}  •  {themeName}");
                    });

                    // Calculate on background thread.
                    if (ct.IsCancellationRequested) return;
                    uint[] newBuf = await Task.Run(() =>
                    {
                        if (_calculator == null) return Array.Empty<uint>();
                        Debug.WriteLine($"SldShwLp: Starting calculation for new region/theme. " +
                            $"Calculator state: {_calculator.Width}×{_calculator.Height}, MaxIterations: {_calculator.MaxIterations}, " +
                            $"Precision: {(_calculator.IsHighPrecisionActive ? "DD" : "SP")}");
                        return SlideshowCalcFrame(ct);
                    }, ct);

                    if (ct.IsCancellationRequested) return;

                    // Cross-fade between the captured on-screen frame and the new render.
                    if (oldBuf.Length == newBuf.Length && oldBuf.Length > 0)
                    {
                        await CrossFade(oldBuf, newBuf, focusRegion ? fadeSteps : fadeStepsCF, focusRegion ? fadeStepMs : fadeStepMsCF, ct);
                    }
                    else
                    {
                        await InvokeAsync(() =>
                        {
                            if (!_disposed && _renderer != null && _calculator != null)
                                _renderer.UpdateTexture(newBuf, _calculator.Width, _calculator.Height);
                        });
                    }

                    previousBuffer = newBuf;
                    renderCounter += regionLockFunc() ? 1 : 0;
                    focusRegion = focusChangedFunc();  // toggle focus mode if focus change detected
                }

                // ── Run exactly (themesPerRegion - 1) additional theme changes ────
                // The first theme was shown above; now show 2 more for a total of 3.
                int themesCount = regionLockFunc() ? paletteNames.Count : focusRegion ? themesPerRegion : themesPerRegionCF;
                for (int themeNum = 1; themeNum < themesCount && !ct.IsCancellationRequested; themeNum++)
                {
                    Debug.WriteLine($"SldShwLp: Theme {themeNum + 1} of {themesPerRegion} for region \"{region.Name}\" starting in {themeDurationMs} ms");
                    // Wait for the full theme display duration before starting the next fade.
                    // Polls skip-region / skip-theme flags so the VCR controls
                    // get an immediate response instead of waiting for the timer.
                    await SkippableDelay(focusRegion ? themeDurationMs : themeDurationMsCF, ct,
                        PeekSkipSlideshowRegion, PeekSkipSlideshowTheme);
                    if (ct.IsCancellationRequested) return;
                    // Skip-region wins over skip-theme: consume it now and bail
                    // out of the inner loop so the outer loop picks a new region.
                    if (PeekSkipSlideshowRegion()) { IsSkipSlideshowRegion(); break; }
                    // Consume any pending skip-theme so it doesn't fire again
                    // on the next iteration's delay.
                    IsSkipSlideshowTheme();
                    lockStatus = regionLockFunc() ? "(L)" : "";
                    // Pick next theme.

                    int newThemeIdx;
                    do { newThemeIdx = _slideshowRng.Next(paletteNames.Count); }
                    while (paletteNames.Count > 1 && newThemeIdx == lastThemeIdx);
                    lastThemeIdx = newThemeIdx;
                    string newThemeName = paletteNames[newThemeIdx];

                    if (_calculator == null || _renderer == null) break;

                    uint[] oldThemeBuf = previousBuffer ?? Array.Empty<uint>();

                    // Apply new theme silently — no TriggerCalculation.
                    Debug.WriteLine($"Pre await invoke: Applying new theme \"{newThemeName}\"");
                    await InvokeAsync(() =>
                    {
                        if (_disposed) return;
                        ApplyColorThemeSilent(newThemeName);
                        SetStatus($"Slideshow: {region.Name}{lockStatus}  •  {newThemeName}");
                    });

                    if (ct.IsCancellationRequested) return;

                    Debug.WriteLine($"Post await invoke: Starting calculation for new theme \"{newThemeName}\"");
                    uint[] newThemeBuf = await Task.Run(() =>
                    {
                        if (_calculator == null) return Array.Empty<uint>();
                        Debug.WriteLine($"SldShwLp: Calculating new theme \"{newThemeName}\" for region \"{region.Name}\"");
                        return SlideshowCalcFrame(ct);
                    }, ct);

                    if (ct.IsCancellationRequested) return;
                    if (skipRegionFunc()) break;  // move to next region if skip requested

                    if (oldThemeBuf.Length == newThemeBuf.Length && oldThemeBuf.Length > 0)
                    {
                        Debug.WriteLine($"SldShwLp: Starting theme cross-fade between buffers of {oldThemeBuf.Length} pixels");
                        await CrossFade(oldThemeBuf, newThemeBuf, focusRegion ? fadeSteps : fadeStepsCF, focusRegion ? fadeStepMs : fadeStepMsCF, ct);
                    }
                    else
                    {
                        Debug.WriteLine($"SldShwLp: Theme buffer size mismatch or empty (old: {oldThemeBuf.Length}, new: {newThemeBuf.Length}), skipping cross-fade");
                        await InvokeAsync(() =>
                        {
                            if (!_disposed && _renderer != null && _calculator != null)
                                _renderer.UpdateTexture(newThemeBuf, _calculator.Width, _calculator.Height);
                        });
                    }

                    focusRegion = focusChangedFunc();  // toggle focus mode if focus change detected
                    previousBuffer = newThemeBuf;
                    Debug.WriteLine($"Region lock: {regionLockFunc()}, theme {themeNum + 1} of {themesCount} for region \"{region.Name}\" displayed");
                    if (!regionLockFunc() && themeNum >= (focusRegion ? themesPerRegion : themesPerRegionCF)) break;  // move to next region if not locking; otherwise show all themes for this region before moving on
                    else if (regionLockFunc() && themeNum >= (focusRegion ? themesPerRegion : themesPerRegionCF)) themesCount = paletteNames.Count - themeNum;  // if locking and we've shown the preset number of themes, switch to showing all themes for the rest of the slideshow loop
                }

                // Wait for the final theme to display its full duration before
                // transitioning to the next region. Only a skip-region (VCR or
                // beat-driven) breaks out — a stray theme-skip beat must not
                // shortcut the region change.
                int finalWaitMs = focusRegion ? themeDurationMs : themeDurationMsCF;
                Debug.WriteLine($"SldShwLp: Final theme for region \"{region.Name}\" displayed, waiting {finalWaitMs} ms before next region");
                await SkippableDelay(finalWaitMs, ct, PeekSkipSlideshowRegion);
                // Consume any pending skip flags — we are advancing to the
                // next region either way.
                IsSkipSlideshowRegion();
                IsSkipSlideshowTheme();
                Debug.WriteLine($"SldShwLp: Theme duration complete for region \"{region.Name}\"");
                _lastUploadedBuffer = previousBuffer;
                Debug.WriteLine($"Region lock: {regionLockFunc()}, completed region \"{region.Name}\" with final theme displayed for full duration");
                lastRegionIdx = regionLockFunc() ? -1 : regionIdx;
                focusRegion = focusChangedFunc();  // toggle focus mode if focus change detected
            }
        }

        /// <summary>
        /// Applies a region to the calculator state without triggering a render
        /// (used by the slideshow, which manages rendering explicitly).
        /// </summary>
        private void ApplyRegionSilent(FractalRegion region)
        {
            // Switch fractal type + load type-specific params (UserEquation /
            // Sandbox / UserBulb source). Without this, _currentFractalType
            // stays on Mandelbrot and SlideshowCalcFrame never picks an alt
            // calculator — region UI/watermark show e.g. Julia but the
            // rendered pixels remain Mandelbrot.
            if (region.FractalType != _currentFractalType)
                SwitchFractalTypeForRegion(region.FractalType);
            LoadRegionFractalParams(region);

            _centerX = region.CenterX; _centerXLo = region.CenterXLo;
            _centerX2 = region.CenterX2; _centerX3 = region.CenterX3;
            _centerY = region.CenterY; _centerYLo = region.CenterYLo;
            _centerY2 = region.CenterY2; _centerY3 = region.CenterY3;
            _quality = region.QualityPreset;
            _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
            //_qualityCombo.Text = region.QualityPresetName;
            _zoom = System.Math.Clamp(region.Zoom, _quality.ZoomMin, _quality.ZoomMax);

            if (_calculator != null)
            {
                _calculator.CenterX = _centerX;
                _calculator.CenterXLo = _centerXLo;
                _calculator.CenterX2 = _centerX2;
                _calculator.CenterX3 = _centerX3;
                _calculator.CenterY = _centerY;
                _calculator.CenterYLo = _centerYLo;
                _calculator.CenterY2 = _centerY2;
                _calculator.CenterY3 = _centerY3;
                _calculator.Zoom = _zoom;
                _calculator.Quality = region.QualityPreset;
                if (!_iterLocked && region.Iterations > 0)
                    _calculator.MaxIterations = region.Iterations;
                else if (_iterLocked)
                    _calculator.MaxIterations = _lockedIterations;
            }
            UpdateCoordBoxes();
            _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
        }

        /// <summary>
        /// Runs the calculator(s) for the current fractal type and returns a fresh
        /// copy of the resulting BGRA buffer. Mirrors the alt-calculator pattern in
        /// TriggerCalculation so non-Mandelbrot fractals render their own pixels
        /// during the slideshow (regions remain Mandelbrot-coordinate by design).
        /// </summary>
        private uint[] SlideshowCalcFrame(CancellationToken ct)
        {
            if (_calculator == null) return Array.Empty<uint>();
            IFractalCalculator? alt = SelectAltCalculator(_currentFractalType);
            if (alt == null)
            {
                _calculator.Calculate(ct);
                var copy = new uint[_calculator.ColorBuffer.Length];
                _calculator.ColorBuffer.CopyTo(copy, 0);
                return copy;
            }

            alt.CenterX = _calculator.CenterX;
            alt.CenterY = _calculator.CenterY;
            alt.Zoom = _calculator.Zoom;
            alt.MaxIterations = _calculator.MaxIterations;
            alt.Quality = _calculator.Quality;
            alt.ColorMap = _calculator.ColorMap;
            switch (alt)
            {
                case EscapeTimeCalculator e:
                    e.FractalType = _currentFractalType;
                    e.FractalParameters = _fractalParams;
                    break;
                case IFSCalculator ifs:        ifs.FractalParameters = _fractalParams; break;
                case LSystemCalculator ls:     ls.FractalParameters = _fractalParams; break;
                case AttractorCalculator a:    a.FractalParameters = _fractalParams; break;
                case BuddhabrotCalculator b:   b.FractalParameters = _fractalParams; break;
                case NewtonCalculator n:       n.FractalParameters = _fractalParams; break;
                case UserEquationCalculator u: u.FractalParameters = _fractalParams; break;
                case MandelbulbCalculator m:   m.FractalParameters = _fractalParams; break;
                case SandboxCalculator sb:     sb.FractalParameters = _fractalParams; break;
                case UserBulbCalculator ub:    ub.FractalParameters = _fractalParams; break;
            }
            alt.Calculate(ct);
            var altCopy = new uint[alt.ColorBuffer.Length];
            alt.ColorBuffer.CopyTo(altCopy, 0);
            return altCopy;
        }

        private void ApplyColorThemeSilent(string themeName)
        {
            _colorThemeCombo.SelectedIndexChanged -= OnColorThemeChanged;
            try
            {
                var map = Models.ColorPalette.GetPaletteByName(themeName);
                _calculator?.ColorMap = map;
                _colorThemeCombo.Text = themeName;
            }
            finally
            {
                _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;
            }
        }

        /// <summary>
        /// Sets the region combo to the named entry without firing
        /// <see cref="OnRegionComboChanged"/> (which would call TriggerCalculation).
        /// </summary>
        private void SuppressedSetRegionCombo(string name)
        {
            _regionCombo.SelectedIndexChanged -= OnRegionComboChanged;
            try
            {
                for (int i = 0; i < _regionCombo.Items.Count; i++)
                    if (_regionCombo.Items[i]?.ToString() == name)
                    { _regionCombo.SelectedIndex = i; break; }
            }
            finally
            {
                UpdateDelRegionButton(_regionCombo, _delRegionButton);
                _regionCombo.SelectedIndexChanged += OnRegionComboChanged;
            }
        }

        /// <summary>
        /// Cross-fades two BGRA uint[] buffers by posting <paramref name="steps"/>
        /// blended frames to the renderer.  Each frame alpha-blends the buffers by
        /// an incrementing weight and posts the result to the UI thread.
        /// </summary>
        private async Task CrossFade(uint[] from, uint[] to, int steps, int stepMs, CancellationToken ct)
        {
            int len = System.Math.Min(from.Length, to.Length);
            var blended = new uint[len];
            int w = _calculator?.Width ?? 0;
            int h = _calculator?.Height ?? 0;
            if (w == 0 || h == 0 || w * h != len) return;

            for (int step = 1; step <= steps; step++)
            {
                if (ct.IsCancellationRequested) return;
                float alpha = step / (float)steps;

                // CPU pixel-blend — runs on the calling background thread.
                BlendBuffers(from, to, blended, len, alpha);
                if (_showSlideshowWatermark) BlendWatermarkOverlay(blended, w, h);

                //// Re-apply watermark on every fade frame so it never disappears
                //// during transitions (both region and theme cross-fades).
                //if (_showSlideshowWatermark)
                //        BlendWatermarkOverlay(blended, w, h);

                // Take a snapshot for the upload so we're not mutating blended
                // on the background thread while the UI thread may be reading it.
                var frame = new uint[len];
                Array.Copy(blended, frame, len);
                await InvokeAsync(() =>
                {
                    if (!_disposed && _renderer != null)
                        _renderer.UpdateTexture(frame, w, h);
                });

                await DelayWithCancel(stepMs, ct);
            }
        }

        /// <summary>Per-pixel linear blend between two BGRA uint[] buffers.</summary>
        private static void BlendBuffers(uint[] from, uint[] to, uint[] result, int len, float alpha)
        {
            float beta = 1f - alpha;
            for (int i = 0; i < len; i++)
            {
                uint pF = from[i], pT = to[i];
                byte bF = (byte)(pF & 0xFF);
                byte gF = (byte)(pF >> 8 & 0xFF);
                byte rF = (byte)(pF >> 16 & 0xFF);
                byte bT = (byte)(pT & 0xFF);
                byte gT = (byte)(pT >> 8 & 0xFF);
                byte rT = (byte)(pT >> 16 & 0xFF);

                byte bR = (byte)(bF * beta + bT * alpha);
                byte gR = (byte)(gF * beta + gT * alpha);
                byte rR = (byte)(rF * beta + rT * alpha);
                result[i] = 0xFF000000u | ((uint)rR << 16) | ((uint)gR << 8) | bR;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Floating VCR control panel — shown while a Slideshow or Video
        // Slideshow is running. Buttons forward to the corresponding skip /
        // pause / stop methods. Panel is parented under _renderPanel so it
        // floats over the fractal.
        // ─────────────────────────────────────────────────────────────────

        private bool _vcrSizeHandlerAttached;

        private void EnsureVcrPanel()
        {
            if (_vcrPanel != null && !_vcrPanel.IsDisposed) return;
            _vcrPanel = new Views.SlideshowVcrPanel();
            _vcrPanel.PlayPauseClicked   += OnVcrPlayPause;
            _vcrPanel.StopClicked        += OnVcrStop;
            _vcrPanel.SkipRegionClicked  += OnVcrSkipRegion;
            _vcrPanel.SkipThemeClicked   += OnVcrSkipTheme;
            _vcrPanel.CollapsedChanged   += (s, e) => LayoutVcrPanel();
            LayoutVcrPanel();
            _renderPanel.Controls.Add(_vcrPanel);
            _vcrPanel.BringToFront();

            // Re-center horizontally on render panel resize. Anchor=Bottom
            // keeps the vertical position correct but not the horizontal.
            if (!_vcrSizeHandlerAttached)
            {
                _renderPanel.SizeChanged += (s, e) => LayoutVcrPanel();
                _vcrSizeHandlerAttached = true;
            }
        }

        private void LayoutVcrPanel()
        {
            if (_vcrPanel == null) return;
            _vcrPanel.Left = System.Math.Max(0, (_renderPanel.ClientSize.Width - _vcrPanel.Width) / 2);
            _vcrPanel.Top  = System.Math.Max(0, _renderPanel.ClientSize.Height - _vcrPanel.Height - 12);
            _vcrPanel.Anchor = AnchorStyles.Bottom;
        }

        private void ShowVcrForSlideshow()
        {
            EnsureVcrPanel();
            if (_vcrPanel == null) return;
            _vcrPanel.SetPauseEnabled(true);
            _vcrPanel.SetSkipThemeEnabled(true);
            _vcrPanel.SetSkipRegionEnabled(true);
            _vcrPanel.SetPaused(false);
            _vcrPanel.Visible = true;
            _vcrPanel.BringToFront();
        }

        private void ShowVcrForVideoSlideshow()
        {
            EnsureVcrPanel();
            if (_vcrPanel == null) return;
            // Pausing mid-zoom is not supported — buttons disabled. Skip-Theme
            // is meaningless during a leg (one theme per leg) so disabled too.
            _vcrPanel.SetPauseEnabled(false);
            _vcrPanel.SetSkipThemeEnabled(false);
            _vcrPanel.SetSkipRegionEnabled(true);
            _vcrPanel.SetPaused(false);
            _vcrPanel.Visible = true;
            _vcrPanel.BringToFront();
        }

        private void HideVcrPanel()
        {
            if (_vcrPanel == null) return;
            _vcrPanel.Visible = false;
        }

        private void OnVcrPlayPause(object? sender, EventArgs e)
        {
            if (!_slideshowRunning) return;
            ToggleSlideshowPause();
            _vcrPanel?.SetPaused(IsSlideshowPaused());
        }

        private void OnVcrStop(object? sender, EventArgs e)
        {
            if (_slideshowRunning) StopSlideshow();
            else if (_videoSlideshowRunning) StopVideoSlideshow();
        }

        private void OnVcrSkipRegion(object? sender, EventArgs e)
        {
            if (_slideshowRunning) SkipSlideshowRegion();
            else if (_videoSlideshowRunning) SkipVideoSlideshowLeg();
        }

        private void OnVcrSkipTheme(object? sender, EventArgs e)
        {
            if (_slideshowRunning) SkipSlideshowTheme();
        }

        /// <summary>Awaitable Task.Delay that tolerates cancellation silently.</summary>
        private static async Task DelayWithCancel(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { /* expected */ }
        }

        /// <summary>
        /// Awaitable delay that polls the supplied skip predicates every ~50 ms
        /// and returns early when any of them is true. Also holds (does not
        /// accumulate elapsed time) while the slideshow is paused so the VCR
        /// Pause button can suspend region/theme progress.
        /// </summary>
        private async Task SkippableDelay(int ms, CancellationToken ct, params Func<bool>[] skipChecks)
        {
            int elapsed = 0;
            while (elapsed < ms && !ct.IsCancellationRequested)
            {
                if (IsSlideshowPaused())
                {
                    try { await Task.Delay(80, ct); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                if (skipChecks != null)
                {
                    for (int i = 0; i < skipChecks.Length; i++)
                        if (skipChecks[i]()) return;
                }
                int chunk = System.Math.Min(50, ms - elapsed);
                try { await Task.Delay(chunk, ct); }
                catch (OperationCanceledException) { return; }
                elapsed += chunk;
            }
        }
    }
}
