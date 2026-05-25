using System;
using System.Reactive;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Views.SlideshowSettingsView"/>. Wraps a
/// <see cref="SlideshowSettings"/> DTO plus the audio-reactive master toggle
/// so the view can bind to observable properties and produce a
/// <see cref="Result"/> + <see cref="AudioReactiveResult"/> on OK.
///
/// Mirrors the behaviour of the legacy WinForms SlideshowSettingsDialog at
/// Views/SlideshowSettingsDialog.cs but with no pixel layout, no DPI quirks,
/// and an Audio… command surfaced as an event so the host can decide how to
/// open the audio settings view (modal Avalonia dialog or legacy WinForms
/// fallback during the transition).
/// </summary>
public sealed class SlideshowSettingsViewModel : ViewModelBase
{
    private readonly SlideshowSettings _working;

    private bool _audioReactive;
    private bool _useExtremeRegions;
    private int _totalDisplaySec;
    private int _themeFadeMs;
    private int _regionFadeMs;
    private int _fadeSteps;

    public SlideshowSettingsViewModel(SlideshowSettings current, bool audioReactive)
    {
        ArgumentNullException.ThrowIfNull(current);

        _working = Clone(current);
        _audioReactive = audioReactive;
        _useExtremeRegions = _working.UseExtremeRegions;
        _totalDisplaySec = Math.Clamp(_working.TotalDisplayMsPerRegion / 1000, 3, 600);
        _themeFadeMs = _working.ColorThemeFadeMs;
        _regionFadeMs = _working.RegionFadeMs;
        _fadeSteps = _working.FadeSteps;

        OkCommand = ReactiveCommand.Create(Commit);
        CancelCommand = ReactiveCommand.Create(() => { });
        ShowAudioDialogCommand = ReactiveCommand.Create(() =>
            ShowAudioDialogRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Result DTO populated by <see cref="OkCommand"/>. Null until OK fires.</summary>
    public SlideshowSettings? Result { get; private set; }

    /// <summary>Audio-reactive master toggle as it was at OK time.</summary>
    public bool AudioReactiveResult { get; private set; }

    public bool AudioReactive
    {
        get => _audioReactive;
        set
        {
            this.RaiseAndSetIfChanged(ref _audioReactive, value);
            this.RaisePropertyChanged(nameof(TimingEnabled));
            this.RaisePropertyChanged(nameof(TimingNoteVisible));
        }
    }

    public bool UseExtremeRegions
    {
        get => _useExtremeRegions;
        set => this.RaiseAndSetIfChanged(ref _useExtremeRegions, value);
    }

    /// <summary>Total dwell per region, in seconds. Bound to a NumericUpDown 3..600.</summary>
    public int TotalDisplaySec
    {
        get => _totalDisplaySec;
        set => this.RaiseAndSetIfChanged(ref _totalDisplaySec, Math.Clamp(value, 3, 600));
    }

    public int ThemeFadeMs
    {
        get => _themeFadeMs;
        set => this.RaiseAndSetIfChanged(ref _themeFadeMs, Math.Clamp(value, 100, 20_000));
    }

    public int RegionFadeMs
    {
        get => _regionFadeMs;
        set => this.RaiseAndSetIfChanged(ref _regionFadeMs, Math.Clamp(value, 100, 20_000));
    }

    public int FadeSteps
    {
        get => _fadeSteps;
        set => this.RaiseAndSetIfChanged(ref _fadeSteps, Math.Clamp(value, 2, 200));
    }

    /// <summary>Convenience derived property — drives IsEnabled on timing controls.</summary>
    public bool TimingEnabled => !_audioReactive;

    /// <summary>Mirror of the WinForms "(Disabled while Audio-Reactive…)" note.</summary>
    public bool TimingNoteVisible => _audioReactive;

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAudioDialogCommand { get; }

    /// <summary>
    /// Raised when the user clicks the Audio… button. The host wires this to
    /// whatever audio settings UI it prefers (Avalonia view in the new shell
    /// or the legacy WinForms AudioSettingsDialog during the transition).
    /// </summary>
    public event EventHandler? ShowAudioDialogRequested;

    private void Commit()
    {
        AudioReactiveResult = _audioReactive;
        _working.UseExtremeRegions = _useExtremeRegions;
        _working.TotalDisplayMsPerRegion = _totalDisplaySec * 1000;
        _working.ColorThemeFadeMs = _themeFadeMs;
        _working.RegionFadeMs = _regionFadeMs;
        _working.FadeSteps = _fadeSteps;
        Result = _working;
    }

    private static SlideshowSettings Clone(SlideshowSettings s) => new()
    {
        UseExtremeRegions = s.UseExtremeRegions,
        TotalDisplayMsPerRegion = s.TotalDisplayMsPerRegion,
        ColorThemeFadeMs = s.ColorThemeFadeMs,
        RegionFadeMs = s.RegionFadeMs,
        FadeSteps = s.FadeSteps,
    };
}
