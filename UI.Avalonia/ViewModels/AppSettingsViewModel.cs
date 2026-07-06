using System;
using System.Reactive;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Views.AppSettingsView"/> — the general
/// application-settings dialog. Today it hosts the Animation Roadmap Phase 6
/// animated-param ceiling override (previously JSON-only in
/// animation-settings.json); structured with a section header so further
/// app-global settings can slot in without a new dialog.
///
/// OK commits the edited <see cref="AnimationSettings"/> into
/// <see cref="Result"/>; Cancel leaves it null so the host discards edits.
/// </summary>
public sealed class AppSettingsViewModel : ViewModelBase, IClosableDialog
{
    private int _ceilingOverride;

    public AppSettingsViewModel(AnimationSettings? current)
    {
        var s = current ?? new AnimationSettings();
        _ceilingOverride = Math.Clamp(s.AnimatedParamCeilingOverride, 0, CeilingMax);

        // Show the user what "Auto (0)" resolves to on this machine so the
        // override isn't a blind number. Both the 2D and 3D-raymarched
        // defaults, plus the hardware the policy saw.
        var hw = HardwareProfile.Detect();
        int def2d = AnimatedParamCeilingPolicy.DefaultCeiling(hw, includesRaymarched3D: false);
        int def3d = AnimatedParamCeilingPolicy.DefaultCeiling(hw, includesRaymarched3D: true);
        HardwareDefaultText =
            $"Auto (0) uses this machine's default: {def2d} tracks for 2D legs, " +
            $"{def3d} for 3D-raymarched legs ({hw.LogicalCores} logical cores, " +
            $"{(hw.DiscreteGpu ? "discrete GPU" : "integrated GPU")}).";

        OkCommand = ReactiveCommand.Create(Commit);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, false));
    }

    /// <summary>Upper clamp for the manual ceiling. Generous — the policy's
    /// own 2D default is 12; 64 leaves headroom for a strong workstation.</summary>
    public const int CeilingMax = 64;

    /// <summary>Populated by <see cref="Commit"/> on OK; null after Cancel.</summary>
    public AnimationSettings? Result { get; private set; }

    /// <summary>Raised with <c>true</c> on OK (after <see cref="Result"/> is
    /// set) and <c>false</c> on Cancel. The view closes on this signal.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string HardwareDefaultText { get; }

    /// <summary>Manual animated-param ceiling. <c>0</c> = auto (derive from
    /// hardware + whether the leg animates a 3D-raymarched param). A positive
    /// value pins the ceiling; the bus drops the most expensive tracks past
    /// it.</summary>
    public int AnimatedParamCeilingOverride
    {
        get => _ceilingOverride;
        set => this.RaiseAndSetIfChanged(ref _ceilingOverride, Math.Clamp(value, 0, CeilingMax));
    }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Snapshot the edited fields into <see cref="Result"/> and ask
    /// the view to close with success.</summary>
    public void Commit()
    {
        Result = new AnimationSettings { AnimatedParamCeilingOverride = _ceilingOverride };
        CloseRequested?.Invoke(this, true);
    }
}
