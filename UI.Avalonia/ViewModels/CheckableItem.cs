// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Reactive Name + IsChecked pair used by multi-select ListBox UIs
/// (Slideshow filter panels: regions, themes, fractal types, quality presets).
/// Owner is notified when IsChecked flips so the parent VM can flag dirty.</summary>
public sealed class CheckableItem : ReactiveObject
{
    private bool _isChecked;

    public CheckableItem(string name, bool isChecked)
    {
        Name = name ?? string.Empty;
        _isChecked = isChecked;
    }

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            this.RaiseAndSetIfChanged(ref _isChecked, value);
            Owner?.OnFilterItemChanged();
        }
    }

    /// <summary>Parent VM notified when this item flips. Wired by the parent
    /// after construction (kept off the ctor so the type stays serialisation-friendly).</summary>
    public SlideshowSettingsViewModel? Owner { get; set; }
}
