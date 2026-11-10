// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using FracturingFog.Audio;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// #1434 — the audio recipe picker above the binding matrix (Fractured Waves
/// expander and Audio Settings). Apply lays a recipe's bindings onto the shared
/// <see cref="AudioModulationManager"/> (replace or merge) plus its side settings;
/// Undo puts back what was there; Save stores the current enabled bindings as a
/// user preset (built-in names are refused). The applied bindings are ordinary
/// matrix bindings, so they save with the region and export as <c>--audio-bind</c>.
/// </summary>
public sealed class AudioRecipePickerViewModel : ViewModelBase
{
    private readonly AudioModulationManager _mgr;
    private readonly FractalType _type;
    private readonly IReadOnlyList<AudioBindingRowViewModel> _rows;
    private readonly Func<AudioRecipePreset, Action?> _applySettings;
    private IReadOnlyList<AudioParamBinding>? _undoBindings;
    private Action? _undoSettings;

    /// <param name="applySettings">Applies a recipe's side settings to the live
    /// parameters and returns an action that undoes them (see <see cref="SettingsOn"/>).</param>
    public AudioRecipePickerViewModel(AudioModulationManager mgr, FractalType type,
        IReadOnlyList<AudioBindingRowViewModel> rows, Func<AudioRecipePreset, Action?> applySettings)
    {
        _mgr = mgr ?? throw new ArgumentNullException(nameof(mgr));
        _type = type;
        _rows = rows ?? Array.Empty<AudioBindingRowViewModel>();
        _applySettings = applySettings ?? (_ => null);

        var canApply = this.WhenAnyValue(x => x.SelectedRecipe).Select(r => r != null);
        ApplyCommand = ReactiveCommand.Create(Apply, canApply);
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        SaveCommand = ReactiveCommand.Create(Save, this.WhenAnyValue(x => x.SaveName).Select(n => !string.IsNullOrWhiteSpace(n)));
        DeleteCommand = ReactiveCommand.Create(Delete, this.WhenAnyValue(x => x.SelectedRecipe).Select(r => r is { IsBuiltIn: false }));
        Reload(null);
    }

    public ObservableCollection<AudioRecipePreset> Recipes { get; } = new();

    private AudioRecipePreset? _selected;
    public AudioRecipePreset? SelectedRecipe
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(SelectedDescription));
        }
    }

    public string SelectedDescription => _selected == null ? string.Empty
        : (_selected.IsBuiltIn ? "" : "(saved) ") + _selected.Description;

    public bool HasRecipes => Recipes.Count > 0;

    /// <summary>No recipe fits this fractal yet: the view shows a hint in place of
    /// the list, and Save stays available to make the first one.</summary>
    public bool HasNoRecipes => Recipes.Count == 0;

    public const string NoRecipesHint =
        "No recipes for this fractal yet: turn on bindings below and Save.";

    private bool _replace = true;
    /// <summary>True: the recipe replaces this type's bindings (others turned off).
    /// False: it merges over them.</summary>
    public bool ReplaceExisting { get => _replace; set => this.RaiseAndSetIfChanged(ref _replace, value); }

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }

    private string _saveName = string.Empty;
    public string SaveName { get => _saveName; set => this.RaiseAndSetIfChanged(ref _saveName, value); }

    private string _status = string.Empty;
    public string StatusText { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    private void Reload(string? select)
    {
        Recipes.Clear();
        foreach (var r in AudioRecipeLibrary.ForType(_type)) Recipes.Add(r);
        SelectedRecipe = Recipes.FirstOrDefault(r => string.Equals(r.Name, select, StringComparison.OrdinalIgnoreCase))
                         ?? Recipes.FirstOrDefault();
        this.RaisePropertyChanged(nameof(HasRecipes));
        this.RaisePropertyChanged(nameof(HasNoRecipes));
    }

    public void Apply()
    {
        var recipe = _selected;
        if (recipe == null) return;
        _undoBindings = _mgr.ExportBindings();
        var applied = _mgr.ApplyRecipe(recipe.BindingsFor(_type), _replace);
        _undoSettings = _applySettings(recipe);
        foreach (var r in _rows) r.Refresh();
        CanUndo = true;
        StatusText = applied.Count == 0
            ? $"'{recipe.Name}' has no binding for this fractal."
            : $"Applied '{recipe.Name}': {string.Join(", ", applied)}. Needs audio input to move.";
    }

    public void Undo()
    {
        if (_undoBindings == null) return;
        _mgr.RestoreBindings(_undoBindings);
        _undoSettings?.Invoke();
        foreach (var r in _rows) r.Refresh();
        _undoBindings = null; _undoSettings = null;
        CanUndo = false;
        StatusText = "Recipe undone.";
    }

    public void Save()
    {
        var enabled = _mgr.ExportBindings().Where(b => b.Enabled).ToList();
        var mine = new AudioRecipePreset { Name = _saveName.Trim(), Bindings = enabled };
        if (!mine.AppliesTo(_type)) { StatusText = "No audio binding is on for this fractal, so there is nothing to save."; return; }
        mine.Bindings = mine.BindingsFor(_type);
        mine.Settings = AudioRecipeLibrary.ImpliedSettings(mine.Bindings);
        mine.Description = "Drives " + string.Join(", ", mine.Bindings.Select(b => $"{b.ParamName} from {b.Binding.Source}")) + ".";
        if (!AudioRecipeLibrary.TrySaveUser(mine, out string? error)) { StatusText = error ?? "Could not save."; return; }
        Reload(mine.Name);
        StatusText = $"Saved '{mine.Name}'.";
        SaveName = string.Empty;
    }

    public void Delete()
    {
        var r = _selected;
        if (r == null || r.IsBuiltIn) return;
        StatusText = AudioRecipeLibrary.DeleteUser(r.Name) ? $"Deleted '{r.Name}'." : $"Could not delete '{r.Name}'.";
        Reload(null);
    }

    /// <summary>A side-settings applier for <paramref name="p"/>: records the old
    /// values, applies the recipe's settings, calls <paramref name="changed"/>, and
    /// returns the undo (old values back, then <paramref name="changed"/>).</summary>
    public static Func<AudioRecipePreset, Action?> SettingsOn(FractalParameters p, Action changed)
    {
        ArgumentNullException.ThrowIfNull(p);
        return recipe =>
        {
            var old = new List<(PropertyInfo prop, object? value)>();
            foreach (var s in recipe.Settings ?? new())
            {
                var prop = typeof(FractalParameters).GetProperty(s.ParamName ?? string.Empty, BindingFlags.Public | BindingFlags.Instance);
                if (prop is { CanRead: true, CanWrite: true }) old.Add((prop, prop.GetValue(p)));
            }
            if (AudioRecipeLibrary.ApplySettings(recipe, p).Count == 0) return null;
            changed?.Invoke();
            return () =>
            {
                foreach (var (prop, value) in old) prop.SetValue(p, value);
                changed?.Invoke();
            };
        };
    }
}
