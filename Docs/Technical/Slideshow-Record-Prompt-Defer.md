# Slideshow Record-Prompt: Unresolved (deferred)

**Status:** Deferred 2026-06-30. Three fix attempts landed; user smoke test
still shows: Convert/Save/Cancel prompt appears only when slideshow stopped
via right-click context menu. VCR Stop, FloatingMenu Slideshow button,
mainmenu Stop, and Esc all fail to surface the prompt. User confirmed the
active saved preset has `RecordSlideshow=true` (saved, not just OK'd).

## What works
- Right-click context menu → Slideshow item → prompt appears.

## What does not work
- FloatingMenu Slideshow button (start *and* stop).
- VCR Stop button on `SlideshowVcrControl`.
- Esc key handler ([ShellViewModel.cs:613](../../UI.Avalonia/ViewModels/ShellViewModel.cs:613)).

## Fixes already attempted (all landed, none resolved)

1. **`a14067f` — `ToggleSlideshow` loads active preset.** Was constructing
   `new SlideshowConfig()` (default `RecordSlideshow=false`). Replaced with
   `SlideshowConfigLibrary.GetActive(SlideshowConfigLibrary.Load())`.
   Hypothesis: non-context-menu paths weren't carrying the user's recording
   flag. Did not fix.

2. **`a14067f` — Slideshow button text toggle.** Added
   `FloatingMenuViewModel.SlideshowButtonText`; flips "Slideshow" ↔ "Stop"
   on Start/Stopped. Cosmetic, unrelated to prompt — still useful.

3. **`7eb31c2` — Prompt window `Topmost=true` + `Activate()` on
   `Opened`.** Hypothesis: prompt firing but obscured behind FloatingMenu
   (sibling non-modal child of MainWindow). Did not fix.

## What was ruled out
- Code paths converge: all three stop entry points call
  `_slideshow.Stop()` ([ShellViewModel.cs:638](../../UI.Avalonia/ViewModels/ShellViewModel.cs:638),
  [ShellViewModel.cs:401](../../UI.Avalonia/ViewModels/ShellViewModel.cs:401),
  [ShellViewModel.cs:613](../../UI.Avalonia/ViewModels/ShellViewModel.cs:613)).
- `Stop()` cancels CTS; `LoopAsync` `finally` posts `Stopped` to UI thread
  via `OnUiAsync` ([SlideshowEngine.cs:253](../../UI.Avalonia/Slideshow/SlideshowEngine.cs:253))
  regardless of how cancellation propagated.
- `Stopped` handler ([ShellViewModel.cs:675](../../UI.Avalonia/ViewModels/ShellViewModel.cs:675))
  calls `FinalizeSlideshowRecording` unconditionally.
- `FinalizeSlideshowRecording` ([ShellViewModel.cs:856](../../UI.Avalonia/ViewModels/ShellViewModel.cs:856))
  fires `SlideshowRecordingReady` when `_slideshowRecorder != null` AND
  `frames > 0`.
- `AvaloniaShellBootstrap.HandleSlideshowRecordingReadyAsync` ([AvaloniaShellBootstrap.cs:2180](../../Hosting/AvaloniaShellBootstrap.cs:2180))
  is wired once; calls `ShowSlideshowRecordingPromptAsync`.

## Hypotheses to investigate next

1. **`_slideshowRecorder` null on non-context-menu stops.** Maybe the
   recorder lazy-build path in `FrameSink` ([ShellViewModel.cs:821](../../UI.Avalonia/ViewModels/ShellViewModel.cs:821))
   never runs because `FrameSink` is null at the moment frames flow.
   Re-entry into `StartSlideshowRecordingIfRequested` calls
   `DisposeSlideshowRecorder()` first — if `ToggleSlideshow` is racing
   itself (double-fire from XAML Command + event), the second call could
   null out the recorder mid-run.
   - **Action:** add `Console.Error.WriteLine` at every branch of
     `FinalizeSlideshowRecording` and `StartSlideshowRecordingIfRequested`
     plus the FrameSink lazy-build. Have user run and share log.

2. **`Stopped` handler fires twice / on wrong instance.** The handler is
   wired only when `_slideshow == null` (first construction); engine is
   reused across runs. If subsequent runs construct a *second* engine
   (because something nulled `_slideshow`), only the original handler is
   wired — second engine's Stopped goes nowhere.
   - **Action:** confirm `_slideshow` field is never reassigned after first
     run.

3. **`frames` is 0 when stop hits.** Engine's `FrameSink` only fires inside
   `FadeAsync` ([SlideshowEngine.cs:529](../../UI.Avalonia/Slideshow/SlideshowEngine.cs:529)).
   If user stops within the first `RegionTransitionAsync` cold-start where
   no fade runs (snapshot empty, incoming null, host not ready), no frame
   flows → recorder builds zero frames → `frames <= 0` bail at
   [ShellViewModel.cs:872](../../UI.Avalonia/ViewModels/ShellViewModel.cs:872)
   → prompt skipped. Context menu user may naturally wait longer before
   stopping than VCR/button users.
   - **Action:** drop the `frames <= 0` bail or change it to always
     prompt (let user discard via Cancel). Or instrument the count.

4. **`Stopped` event fires *before* `FrameSink` finishes its last write.**
   Race between `Stop()` returning and the cross-fade's
   `await OnUiAsync(...)` resolving — `_slideshowRecorder` could be
   disposed-and-nulled in the handler while FrameSink is mid-call,
   throwing inside the sink. Unlikely to mask the prompt though.

## Recommended starting point
Hypothesis #3 is the most consistent with user's observation
("only context menu works"): right-click flow naturally takes ~500 ms+
between click → menu open → item click → close → engine sees stop, while
button/key paths are instantaneous. If user is hitting Stop within the
first ~half-second of slideshow start, the first leg's `FrameSink` may
not have flushed any frames yet.

Easiest diagnostic: add a status-bar log line in `FinalizeSlideshowRecording`
reporting `frames`, `_slideshowRecorder != null`, and whether
`SlideshowRecordingReady` fired. Have user reproduce on each path and
read off the values.

## Cross-references
- Adaptive sweep leak fix: `1591ecb`.
- Active-preset load + button text: `a14067f`.
- Topmost prompt: `7eb31c2`.
- Linux toy-drag (unrelated, fixed): `2cd0d11`.
