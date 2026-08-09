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

    public static IReadOnlyList<AudioSignalKind> Signals { get; } =
        (AudioSignalKind[])Enum.GetValues(typeof(AudioSignalKind));

    public static IReadOnlyList<AudioResponseCurve> Curves { get; } =
        (AudioResponseCurve[])Enum.GetValues(typeof(AudioResponseCurve));

    public AudioBindingRowViewModel(AnimatableParamDescriptor descriptor, AudioModulationManager mgr)
    {
        _mgr = mgr ?? throw new ArgumentNullException(nameof(mgr));
        ArgumentNullException.ThrowIfNull(descriptor);
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
}
