// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalParamsViewModel.LightingFxPresets.cs — #580.
//
// User-named, recallable Volumetric Lighting & FX presets for the Lighting & FX
// dialog. Distinct from the curated VolumetricFxPresets droplist (that one
// applies a built-in fog subset on selection); these are the user's OWN saved
// looks — the full LightingFxData block — with the standard
// save / recall / delete / import / export lifecycle, mirroring the Workspace
// preset UX (#433). Persisted through LightingFxPresetLibrary and surfaced in
// the Asset Manager via LightingFxAssetSource.
//
// The dialog code-behind owns the name prompt + file pickers (it already does
// for the HDRI browse row) and calls the plain methods here; selection is inert
// (no apply-on-select) — Recall is an explicit action so merely opening the
// dialog can never clobber the live look.

using System.Collections.Generic;
using System.Collections.ObjectModel;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class FractalParamsViewModel
{
    /// <summary>Saved user preset names, in library order. Bound to the recall
    /// ComboBox.</summary>
    public ObservableCollection<string> UserFxPresets { get; } = new();

    private string? _selectedUserFxPreset;

    /// <summary>Currently highlighted preset. Inert on its own — applying is the
    /// explicit <see cref="RecallUserFxPreset"/> action so opening the dialog (or
    /// scrolling the list) never mutates the live look.</summary>
    public string? SelectedUserFxPreset
    {
        get => _selectedUserFxPreset;
        set => this.RaiseAndSetIfChanged(ref _selectedUserFxPreset, value);
    }

    /// <summary>(Re)load the recall list from the library, keeping the current
    /// selection when it still exists, else falling back to the active preset.</summary>
    public void RefreshUserFxPresets()
    {
        var file = LightingFxPresetLibrary.Load();
        string? keep = SelectedUserFxPreset;

        UserFxPresets.Clear();
        foreach (var p in file.Presets) UserFxPresets.Add(p.Name);

        SelectedUserFxPreset =
            keep != null && UserFxPresets.Contains(keep) ? keep
            : (file.ActiveName != null && UserFxPresets.Contains(file.ActiveName) ? file.ActiveName
            : (UserFxPresets.Count > 0 ? UserFxPresets[0] : null));
    }

    /// <summary>Apply the selected preset's full lighting/FX block over the live
    /// parameters and re-render. No-op when nothing is selected.</summary>
    public void RecallUserFxPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedUserFxPreset)) return;
        var file = LightingFxPresetLibrary.Load();
        var preset = LightingFxPresetLibrary.Get(file, SelectedUserFxPreset!);
        if (preset == null) return;

        preset.Data.ApplyTo(_p);   // overwrites _p.Lighting wholesale
        // The applied preset invalidates the "which built-in am I on" hint.
        _selectedVolumetricPreset = VolumetricFxPresets.NoneName;
        this.RaisePropertyChanged(nameof(SelectedVolumetricPreset));
        RaiseLightingKnobsChanged();
        Fire();
    }

    /// <summary>Snapshot the live lighting/FX block as a named preset (add or
    /// replace), persist, and select it. Blank names are ignored.</summary>
    public void SaveUserFxPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var preset = new LightingFxPreset
        {
            Name = name.Trim(),
            Data = LightingFxPresetData.FromFx(_p.Lighting),
        };
        var file = LightingFxPresetLibrary.Load();
        LightingFxPresetLibrary.Upsert(file, preset);   // persists; marks active
        RefreshUserFxPresets();
        SelectedUserFxPreset = preset.Name;
    }

    /// <summary>Delete the named preset. Returns true on a real delete.</summary>
    public bool DeleteUserFxPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var file = LightingFxPresetLibrary.Load();
        bool removed = LightingFxPresetLibrary.Delete(file, name);
        if (removed)
        {
            SelectedUserFxPreset = null;
            RefreshUserFxPresets();
        }
        return removed;
    }

    /// <summary>Import one or many presets from a JSON file, refresh the list, and
    /// select the last one imported. Returns the imported names (empty on
    /// error).</summary>
    public IReadOnlyList<string> ImportUserFxPresets(string path)
    {
        var file = LightingFxPresetLibrary.Load();
        var names = LightingFxPresetLibrary.Import(file, path);
        RefreshUserFxPresets();
        if (names.Count > 0) SelectedUserFxPreset = names[^1];
        return names;
    }

    /// <summary>Export the named preset to a JSON file. Returns true on success.</summary>
    public bool ExportUserFxPreset(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return false;
        var file = LightingFxPresetLibrary.Load();
        return LightingFxPresetLibrary.Export(file, name, path);
    }
}
