// ViewModels/RegionEditorViewModel.cs
//
// Animation Roadmap Sub-goal B (Region Editor) — Phase R1. Modeless editor
// VM for a saved region's metadata. Edits Name / Description / attached
// Animation / CuratedThemes / keep-or-clear Lighting override + embedded
// Watermark, while the region's stored geometry (Center / Zoom / Iterations)
// is shown read-only and preserved on save. Persistence routes through
// IColorThemeService.UpdateRegionMetadata so the VM never touches the Engine
// project (where FractalRegionLibrary lives). Built-in regions open in
// clone-on-save mode.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class RegionEditorViewModel : ViewModelBase
{
    private const string NoneSentinel = "(none)";

    private readonly IColorThemeService _service;
    private readonly RegionEditModel _model;

    public RegionEditorViewModel(IColorThemeService service, RegionEditModel model)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _model   = model   ?? throw new ArgumentNullException(nameof(model));

        // Animation dropdown: "(none)" sentinel first, then the library.
        AnimationNames = new ObservableCollection<string> { NoneSentinel };
        foreach (var n in _service.EnumerateAnimationNames()) AnimationNames.Add(n);

        _description = model.Description ?? string.Empty;
        _keepLightingOverride  = model.KeepLightingOverride;
        _keepEmbeddedWatermark = model.KeepEmbeddedWatermark;

        // Curated-theme whitelist as a checkable list against the theme
        // library. Any curated name no longer present in the library is kept
        // (checked) so an edit never silently drops it.
        CuratedThemes = new ObservableCollection<CuratedThemeOption>();
        var selected = new HashSet<string>(
            model.CuratedThemes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _service.EnumerateThemeNames())
            if (seen.Add(t))
                CuratedThemes.Add(new CuratedThemeOption(t, selected.Contains(t)));
        foreach (var t in selected)
            if (seen.Add(t))
                CuratedThemes.Add(new CuratedThemeOption(t, true)); // orphaned curated name

        _selectedAnimation = !string.IsNullOrWhiteSpace(model.AnimationName)
            && AnimationNames.Contains(model.AnimationName)
                ? model.AnimationName!
                : NoneSentinel;

        // Built-in → clone: seed a distinct name so Save never collides with
        // the immutable original, and label the header accordingly.
        _name = model.IsBuiltIn ? $"{model.Name} (copy)" : model.Name;
        HeaderText = model.IsBuiltIn
            ? $"Clone built-in region — {model.OriginalName}"
            : $"Edit region — {model.OriginalName}";
        GeometrySummary =
            $"{model.FractalTypeName}   ·   center ({model.CenterX:0.############}, {model.CenterY:0.############})"
            + $"   ·   zoom {model.Zoom:0.###}   ·   {model.Iterations} iters";

        SaveCommand  = ReactiveCommand.CreateFromTask(SaveAsync);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    // ── Read-only display ─────────────────────────────────────────────────

    /// <summary>True when the edited region is a built-in — Save clones into a
    /// new user region rather than mutating the original.</summary>
    public bool IsBuiltIn => _model.IsBuiltIn;

    /// <summary>Header line — "Edit region — X" or "Clone built-in region — X".</summary>
    public string HeaderText { get; }

    /// <summary>One-line read-only echo of the region's stored geometry. This
    /// is never editable here (the Region Editor edits metadata only); use the
    /// Save Region flow to recapture geometry from the live view.</summary>
    public string GeometrySummary { get; }

    /// <summary>True when the region carries a lighting override the user can
    /// choose to keep or clear.</summary>
    public bool HasLightingOverride => _model.HasLightingOverride;

    /// <summary>True when the region carries an embedded watermark the user can
    /// choose to keep or clear.</summary>
    public bool HasEmbeddedWatermark => _model.HasEmbeddedWatermark;

    // ── Editable fields ───────────────────────────────────────────────────

    public ObservableCollection<string> AnimationNames { get; }

    private string _name;
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    private string _description;
    public string Description
    {
        get => _description;
        set => this.RaiseAndSetIfChanged(ref _description, value);
    }

    private string _selectedAnimation;
    /// <summary>Attached animation name, or the "(none)" sentinel.</summary>
    public string SelectedAnimation
    {
        get => _selectedAnimation;
        set => this.RaiseAndSetIfChanged(ref _selectedAnimation, value);
    }

    /// <summary>Checkable curated-theme whitelist. None checked = "no opinion"
    /// (region falls back to the compat-filtered theme pool).</summary>
    public ObservableCollection<CuratedThemeOption> CuratedThemes { get; }

    private string _curatedFilter = string.Empty;
    /// <summary>Live substring filter over the curated-theme checklist. Hides
    /// non-matching rows without disturbing their checked state.</summary>
    public string CuratedFilter
    {
        get => _curatedFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _curatedFilter, value);
            var f = value?.Trim() ?? string.Empty;
            foreach (var o in CuratedThemes)
                o.IsVisible = f.Length == 0
                    || o.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool _keepLightingOverride;
    public bool KeepLightingOverride
    {
        get => _keepLightingOverride;
        set => this.RaiseAndSetIfChanged(ref _keepLightingOverride, value);
    }

    private bool _keepEmbeddedWatermark;
    public bool KeepEmbeddedWatermark
    {
        get => _keepEmbeddedWatermark;
        set => this.RaiseAndSetIfChanged(ref _keepEmbeddedWatermark, value);
    }

    // ── Commands + events ─────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Fires after a successful save with the persisted region name
    /// (may be a rename or a fresh clone). The shell refreshes the region combo
    /// and reselects this name.</summary>
    public event EventHandler<string>? RegionSavedToLibrary;

    public event EventHandler? CloseRequested;
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    // ── Save ──────────────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Edit Region", "Region name cannot be empty.", MessageSeverity.Warning));
            return;
        }

        _model.Name = _name.Trim();
        _model.Description = _description ?? string.Empty;
        _model.AnimationName = string.Equals(_selectedAnimation, NoneSentinel, StringComparison.Ordinal)
            ? null
            : _selectedAnimation;
        _model.CuratedThemes = CollectCuratedThemes();
        _model.KeepLightingOverride  = _keepLightingOverride;
        _model.KeepEmbeddedWatermark = _keepEmbeddedWatermark;

        var result = _service.UpdateRegionMetadata(_model);
        if (!result.Success)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Edit Region", result.ErrorMessage ?? "Save failed.", MessageSeverity.Warning));
            return;
        }

        RegionSavedToLibrary?.Invoke(this, result.SavedName ?? _model.Name);

        string verb = result.Cloned ? "cloned to" : "saved as";
        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Edit Region", $"Region {verb} \"{result.SavedName}\".", MessageSeverity.Info));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private List<string>? CollectCuratedThemes()
    {
        var list = CuratedThemes.Where(o => o.IsSelected).Select(o => o.Name).ToList();
        return list.Count > 0 ? list : null;
    }

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }
}

/// <summary>One checkable row in the Region Editor's curated-theme whitelist.</summary>
public sealed class CuratedThemeOption : ReactiveObject
{
    public CuratedThemeOption(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    /// <summary>Theme display name.</summary>
    public string Name { get; }

    private bool _isSelected;
    /// <summary>True when this theme is in the region's curated whitelist.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    private bool _isVisible = true;
    /// <summary>False when hidden by the checklist filter (checked state is
    /// preserved regardless).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }
}
