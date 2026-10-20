// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Audio;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// #263 / Audio-Reactive Phase 4 — one row of the audio→param modulation matrix
/// (Audio Settings dialog). Edits the shared <see cref="AudioModulationBinding"/>
/// the manager holds, so gain / curve / range / signal changes take effect live
/// without a rebuild (the animator re-reads the binding each tick); only
/// <see cref="Enabled"/> toggles the bus registration.
/// </summary>
public sealed class AudioBindingRowViewModel : ViewModelBase
{
    private readonly AudioModulationManager _mgr;
    private readonly AudioModulationBinding _binding;
    private readonly AnimatableParamDescriptor _descriptor;

    public static IReadOnlyList<AudioSignalKind> Signals { get; } =
        (AudioSignalKind[])Enum.GetValues(typeof(AudioSignalKind));

    public static IReadOnlyList<AudioResponseCurve> Curves { get; } =
        (AudioResponseCurve[])Enum.GetValues(typeof(AudioResponseCurve));

    public static IReadOnlyList<AudioRateEdge> Edges { get; } =
        (AudioRateEdge[])Enum.GetValues(typeof(AudioRateEdge));

    public static IReadOnlyList<AudioBindingMode> Modes { get; } =
        (AudioBindingMode[])Enum.GetValues(typeof(AudioBindingMode));

    public AudioBindingRowViewModel(AnimatableParamDescriptor descriptor, AudioModulationManager mgr)
    {
        _mgr = mgr ?? throw new ArgumentNullException(nameof(mgr));
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptor = descriptor;
        ParamName = descriptor.ParamName;
        Notes = descriptor.Notes;
        _binding = mgr.GetOrCreateBinding(descriptor);
        _enabled = mgr.IsEnabled(descriptor.ParamName);
    }

    public string ParamName { get; }
    public string? Notes { get; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _enabled, value))
                _mgr.SetEnabled(ParamName, value);
        }
    }

    public AudioSignalKind Source
    {
        get => _binding.Source;
        set { _binding.Source = value; this.RaisePropertyChanged(); }
    }

    public AudioResponseCurve Curve
    {
        get => _binding.Curve;
        set { _binding.Curve = value; this.RaisePropertyChanged(); }
    }

    public bool Invert
    {
        get => _binding.Invert;
        set { _binding.Invert = value; this.RaisePropertyChanged(); }
    }

    public double Gain
    {
        get => _binding.Gain;
        set { _binding.Gain = value; this.RaisePropertyChanged(); }
    }

    public double OutMin
    {
        get => _binding.OutMin;
        set { _binding.OutMin = value; this.RaisePropertyChanged(); }
    }

    public double OutMax
    {
        get => _binding.OutMax;
        set { _binding.OutMax = value; this.RaisePropertyChanged(); }
    }

    /// <summary>#1431 — Position (the music picks the value) or Rate (the music
    /// picks a speed that is integrated onto the value). Switching re-seeds an
    /// untouched range to the new mode's default: the param's range for
    /// Position, 0 .. a tenth of the range per second for Rate.</summary>
    public AudioBindingMode Mode
    {
        get => _binding.Mode;
        set
        {
            if (_binding.Mode == value) return;
            var (oldMin, oldMax) = DefaultRange(_binding.Mode);
            if (_binding.OutMin == oldMin && _binding.OutMax == oldMax)
            {
                var (min, max) = DefaultRange(value);
                _binding.OutMin = min;
                _binding.OutMax = max;
                this.RaisePropertyChanged(nameof(OutMin));
                this.RaisePropertyChanged(nameof(OutMax));
            }
            _binding.Mode = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsRate));
            this.RaisePropertyChanged(nameof(MinLabel));
            this.RaisePropertyChanged(nameof(MaxLabel));
        }
    }

    private (double Min, double Max) DefaultRange(AudioBindingMode mode)
        => mode == AudioBindingMode.Rate
            ? (0.0, (_descriptor.Max - _descriptor.Min) / 10.0)
            : (_descriptor.Min, _descriptor.Max);

    public bool IsRate => _binding.Mode == AudioBindingMode.Rate;
    public string MinLabel => IsRate ? "Min/s:" : "Min:";
    public string MaxLabel => IsRate ? "Max/s:" : "Max:";

    /// <summary>#1436 — Rate mode: stop (Clamp), jump round (Wrap) or turn
    /// back (PingPong) at the ends of the param's range.</summary>
    public AudioRateEdge Edge
    {
        get => _binding.Edge;
        set { _binding.Edge = value; this.RaisePropertyChanged(); }
    }

    /// <summary>#1431 — seconds for the drive to rise (0 = instant).</summary>
    public double AttackSeconds
    {
        get => _binding.AttackSeconds;
        set { _binding.AttackSeconds = Math.Max(0.0, value); this.RaisePropertyChanged(); }
    }

    /// <summary>#1431 — seconds for the drive to fall (0 = instant).</summary>
    public double ReleaseSeconds
    {
        get => _binding.ReleaseSeconds;
        set { _binding.ReleaseSeconds = Math.Max(0.0, value); this.RaisePropertyChanged(); }
    }
}
