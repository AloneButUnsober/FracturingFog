// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalParamsViewModel.VolumetricPresets.cs
//
// #306 — the Volumetric FX preset droplist for the Lighting & FX dialog. Binds
// to the curated VolumetricFxPresets catalogue (Abstractions). Selecting a
// preset applies its fog subset over the current lighting and broadcasts an
// all-property change so every fog slider re-reads the new values, then Fire()s
// one re-render. The selection stays visible as a "which preset am I on" hint;
// the user then tunes the sliders freely (that does not clear the droplist), and
// a region save (#295) snapshots whatever they land on.

using FracturingFog.Rendering.Lighting;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class FractalParamsViewModel
{
    /// <summary>Droplist source: "—" (no-op) then every curated volumetric look.</summary>
    public System.Collections.Generic.IReadOnlyList<string> VolumetricPresetNames
        => VolumetricFxPresets.Names;

    private string _selectedVolumetricPreset = VolumetricFxPresets.NoneName;

    /// <summary>The chosen preset name. Setting it to a real preset applies that
    /// preset's fog subset onto the live lighting; "—" is a no-op. A Lighting
    /// "Defaults" reset silently drops this back to "—" (see Reset partial).</summary>
    public string SelectedVolumetricPreset
    {
        get => _selectedVolumetricPreset;
        set
        {
            if (_selectedVolumetricPreset == value) return;
            this.RaiseAndSetIfChanged(ref _selectedVolumetricPreset, value);
            if (string.IsNullOrEmpty(value) || value == VolumetricFxPresets.NoneName) return;
            _p.Lighting = VolumetricFxPresets.ApplyByName(value, _p.Lighting);
            this.RaisePropertyChanged(string.Empty);   // refresh every fog knob
            Fire();
        }
    }
}
