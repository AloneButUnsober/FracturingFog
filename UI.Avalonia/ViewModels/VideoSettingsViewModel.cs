using System;
using System.Collections.Generic;
using System.Reactive;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Views.VideoSettingsView"/> — the Avalonia
/// embedded-mode Video dialog. Opened from the unified Slideshow Settings
/// dialog when the user clicks "Video Settings…". The standalone single-shot
/// Video Zoom dialog still uses the WinForms Views/Dialogues.cs implementation
/// (legacy shell only) until that path also moves to Avalonia.
///
/// Embedded mode invariants:
///   • Slideshow + Start buttons hidden — caller owns playback dispatch.
///   • OK commits the working VideoSettingsConfig into <see cref="Result"/>.
///   • Cancel leaves <see cref="Result"/> null so the caller discards edits.
/// </summary>
public sealed class VideoSettingsViewModel : ViewModelBase
{
    private readonly VideoSettingsConfig _working;

    private string _speedPreset;
    private double _customSeconds;
    private bool _constantRate;
    private bool _reverse;
    private bool _saveVideo;
    private bool _saveLossless;
    private string _losslessEncode;
    private int _taaSmoothing;
    private bool _bandDither;
    private int _bandDitherStrength;
    private bool _themeFadeEnabled;
    private int _themesPerLeg;

    public VideoSettingsViewModel(VideoSettingsConfig? current)
    {
        _working = current?.Clone() ?? new VideoSettingsConfig();

        _speedPreset = string.IsNullOrWhiteSpace(_working.SpeedPreset) ? "Medium" : _working.SpeedPreset;
        _customSeconds = _working.CustomSeconds <= 0 ? 30.0 : _working.CustomSeconds;
        _constantRate = _working.ConstantRate;
        _reverse = _working.Reverse;
        _saveVideo = _working.SaveVideo;
        _saveLossless = _working.SaveLossless;
        _losslessEncode = string.IsNullOrWhiteSpace(_working.LosslessEncode) ? "None" : _working.LosslessEncode;
        _taaSmoothing = Math.Clamp(_working.TaaSmoothing, 0, 100);
        _bandDither = _working.BandDither;
        _bandDitherStrength = Math.Clamp(_working.BandDitherStrength, 0, 100);
        _themeFadeEnabled = _working.ThemeFadeEnabled;
        _themesPerLeg = Math.Clamp(_working.ThemesPerLeg, 1, 12);

        OkCommand = ReactiveCommand.Create(Commit);
        CancelCommand = ReactiveCommand.Create(() => { });
    }

    public VideoSettingsConfig? Result { get; private set; }

    public IReadOnlyList<string> SpeedPresets { get; } =
        new[] { "Slow", "Medium", "Fast", "Custom" };

    public IReadOnlyList<string> LosslessEncodeChoices { get; } =
        new[] { "None", "LosslessH264Mp4", "Ffv1Mkv", "HighQualityH264Mp4" };

    public string SpeedPreset
    {
        get => _speedPreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _speedPreset, value ?? "Medium");
            this.RaisePropertyChanged(nameof(IsCustomSpeed));
        }
    }

    public bool IsCustomSpeed => string.Equals(_speedPreset, "Custom", StringComparison.OrdinalIgnoreCase);

    public double CustomSeconds
    {
        get => _customSeconds;
        set => this.RaiseAndSetIfChanged(ref _customSeconds, Math.Clamp(value, 0.5, 600.0));
    }

    public bool ConstantRate
    {
        get => _constantRate;
        set => this.RaiseAndSetIfChanged(ref _constantRate, value);
    }

    public bool Reverse
    {
        get => _reverse;
        set => this.RaiseAndSetIfChanged(ref _reverse, value);
    }

    public bool SaveVideo
    {
        get => _saveVideo;
        set => this.RaiseAndSetIfChanged(ref _saveVideo, value);
    }

    public bool SaveLossless
    {
        get => _saveLossless;
        set => this.RaiseAndSetIfChanged(ref _saveLossless, value);
    }

    public string LosslessEncode
    {
        get => _losslessEncode;
        set => this.RaiseAndSetIfChanged(ref _losslessEncode, value ?? "None");
    }

    public int TaaSmoothing
    {
        get => _taaSmoothing;
        set => this.RaiseAndSetIfChanged(ref _taaSmoothing, Math.Clamp(value, 0, 100));
    }

    public bool BandDither
    {
        get => _bandDither;
        set => this.RaiseAndSetIfChanged(ref _bandDither, value);
    }

    public int BandDitherStrength
    {
        get => _bandDitherStrength;
        set => this.RaiseAndSetIfChanged(ref _bandDitherStrength, Math.Clamp(value, 0, 100));
    }

    public bool ThemeFadeEnabled
    {
        get => _themeFadeEnabled;
        set => this.RaiseAndSetIfChanged(ref _themeFadeEnabled, value);
    }

    public int ThemesPerLeg
    {
        get => _themesPerLeg;
        set => this.RaiseAndSetIfChanged(ref _themesPerLeg, Math.Clamp(value, 1, 12));
    }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private void Commit()
    {
        _working.SpeedPreset = _speedPreset;
        _working.CustomSeconds = _customSeconds;
        _working.SecondsPerLeg = ResolveSeconds(_speedPreset, _customSeconds);
        _working.ConstantRate = _constantRate;
        _working.Reverse = _reverse;
        _working.SaveVideo = _saveVideo;
        _working.SaveLossless = _saveLossless;
        _working.LosslessEncode = _losslessEncode;
        _working.TaaSmoothing = _taaSmoothing;
        _working.BandDither = _bandDither;
        _working.BandDitherStrength = _bandDitherStrength;
        _working.ThemeFadeEnabled = _themeFadeEnabled;
        _working.ThemesPerLeg = _themesPerLeg;
        Result = _working.Clone();
    }

    private static double ResolveSeconds(string preset, double customSecs)
    {
        return preset?.ToLowerInvariant() switch
        {
            "slow" => 60.0,
            "fast" => 12.0,
            "custom" => customSecs,
            _ => 30.0, // Medium
        };
    }
}

public static class VideoSettingsConfigExtensions
{
    /// <summary>Light deep-clone helper so the VM can edit a working copy
    /// without mutating the caller's instance until OK fires.</summary>
    public static VideoSettingsConfig Clone(this VideoSettingsConfig src)
    {
        return new VideoSettingsConfig
        {
            SpeedPreset = src.SpeedPreset,
            CustomSeconds = src.CustomSeconds,
            SecondsPerLeg = src.SecondsPerLeg,
            PauseBetweenMs = src.PauseBetweenMs,
            ConstantRate = src.ConstantRate,
            Reverse = src.Reverse,
            SaveVideo = src.SaveVideo,
            SaveLossless = src.SaveLossless,
            LosslessEncode = src.LosslessEncode,
            TaaSmoothing = src.TaaSmoothing,
            BandDither = src.BandDither,
            BandDitherStrength = src.BandDitherStrength,
            ThemeFadeEnabled = src.ThemeFadeEnabled,
            ThemesPerLeg = src.ThemesPerLeg,
            Extras = new System.Collections.Generic.Dictionary<string, string>(src.Extras ?? new()),
        };
    }
}
