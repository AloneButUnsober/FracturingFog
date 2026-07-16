// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ColorGenEditorViewModel.cs
//
// Avalonia view-model for the ColorGenEditor dialog. Mirrors
// UserEquationViewModel: a TextBox-bound DSL source + saved-name combo +
// "Compile & Load" / "Generate via ColorGen" buttons. The host wires the
// HotLoadRequested / GenerateRequested callbacks (no FracturingFog.Models
// dependency leaks into UI.Avalonia, just like the rest of the editor VMs).

using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using FracturingFog.ColorGen;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ColorGenEditorViewModel : ViewModelBase
{
    private readonly System.Reactive.Disposables.SerialDisposable _debounce = new();
    private bool _loadingNamedEntry;

    public ColorGenEditorViewModel()
    {
        _source = DefaultSource;

        UserColorGenStore.Instance.Load();
        SavedNames = new ObservableCollection<string>();
        RefreshSavedList(null);

        SaveCommand = ReactiveCommand.Create(OnSave);
        DeleteCommand = ReactiveCommand.Create(OnDelete,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        HotLoadCommand = ReactiveCommand.Create(OnHotLoad);
        GenerateCommand = ReactiveCommand.Create(OnGenerate);
    }

    public ObservableCollection<string> SavedNames { get; }

    private const string DefaultSource =
        "// ColorGen DSL — author an algorithmic colour theme.\n" +
        "// Inputs: smooth, dist, iter, maxIter, t, nx, ny, zr, zi, dzr, dzi, arg, mag, isInSet, pxScale\n" +
        "// Funcs:  rgb(r,g,b), hsv(h,s,v), hsl(h,s,l), palette(t, c0, c1, …)\n" +
        "//         mix(a,b,t), brightness(c,s), contrast(c,s), gamma(c,g)\n" +
        "//         sin/cos/exp/log/pow/abs/clamp/smoothstep/hash/hash2 …\n" +
        "let h = smooth * 0.03;\n" +
        "let s = 0.85;\n" +
        "let v = isInSet > 0.5 ? 0.3 : 1.0;\n" +
        "return hsv(h, s, v);\n";

    // ── Editor ──
    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            this.RaiseAndSetIfChanged(ref _source, value);
            if (!_loadingNamedEntry) _selectedSavedName = null;
        }
    }

    // ── Identity ──
    private string _themeName = "My Theme";
    public string ThemeName { get => _themeName; set => this.RaiseAndSetIfChanged(ref _themeName, value); }

    private string _category = "User";
    public string Category { get => _category; set => this.RaiseAndSetIfChanged(ref _category, value); }

    private string _description = "";
    public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }

    // ── Saved selection ──
    private string? _selectedSavedName;
    public string? SelectedSavedName
    {
        get => _selectedSavedName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSavedName, value);
            OnSavedSelectionChanged();
        }
    }

    // ── Status ──
    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => this.RaiseAndSetIfChanged(ref _statusIsError, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> HotLoadCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateCommand { get; }

    /// <summary>Host shows a name-entry dialog. Returns null on cancel.</summary>
    public event Func<string, string?>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirmation. Returns true to proceed.</summary>
    public event Func<string, bool>? ConfirmDeleteRequested;

    /// <summary>Host compiles + loads the theme via ColorGenHotLoad and swaps
    /// the result onto the active palette. Args: (source, className, themeName,
    /// description). Return value: null on success, error message on failure.</summary>
    public event Func<string, string, string, string, string?>? HotLoadRequested;

    /// <summary>Host writes the rendered C# source to Models/ColorSchemes/Generated/
    /// (or wherever it prefers). Same args as HotLoad. Return: null on success.</summary>
    public event Func<string, string, string, string, string?>? GenerateRequested;

    /// <summary>Host shows an informational message box (eg "Saved").</summary>
    public event Action<string, string, bool>? MessageRequested;

    /// <summary>Host calls this to surface a compile/parse error.</summary>
    public void ShowError(string? error)
    {
        bool ok = string.IsNullOrEmpty(error);
        StatusText = ok ? "✓ Compiled" : error!;
        StatusIsError = !ok;
    }

    private void OnHotLoad()
    {
        if (string.IsNullOrWhiteSpace(_source)) { ShowError("Source is empty."); return; }
        var handler = HotLoadRequested;
        if (handler == null) { ShowError("Hot-load not wired by host."); return; }
        string err = handler.Invoke(_source, MakeClassName(_themeName), _themeName, _description) ?? "";
        if (string.IsNullOrEmpty(err))
        {
            StatusText = $"✓ Hot-loaded \"{_themeName}\"";
            StatusIsError = false;
        }
        else ShowError(err);
    }

    private void OnGenerate()
    {
        if (string.IsNullOrWhiteSpace(_source)) { ShowError("Source is empty."); return; }
        var handler = GenerateRequested;
        if (handler == null) { ShowError("Generate not wired by host."); return; }
        string err = handler.Invoke(_source, MakeClassName(_themeName), _themeName, _description) ?? "";
        if (string.IsNullOrEmpty(err))
        {
            StatusText = $"✓ Generated {MakeClassName(_themeName)}.cs (rebuild to pick up)";
            StatusIsError = false;
        }
        else ShowError(err);
    }

    private void OnSave()
    {
        string defaultName = _selectedSavedName ?? _themeName;
        string? name = NamePromptRequested?.Invoke(defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserColorGenStore.Instance.SaveEntry(name.Trim(), _source, _description);
        if (entry == null) return;
        _themeName = entry.Name;
        this.RaisePropertyChanged(nameof(ThemeName));
        RefreshSavedList(entry.Name);
        MessageRequested?.Invoke("ColorGen", $"Saved \"{entry.Name}\".", false);
    }

    private void OnDelete()
    {
        if (_selectedSavedName == null) return;
        if (ConfirmDeleteRequested?.Invoke(_selectedSavedName) != true) return;
        UserColorGenStore.Instance.Remove(_selectedSavedName);
        RefreshSavedList(null);
    }

    private void OnSavedSelectionChanged()
    {
        if (_selectedSavedName == null) return;
        var entry = UserColorGenStore.Instance.GetByName(_selectedSavedName);
        if (entry == null) return;
        _loadingNamedEntry = true;
        try
        {
            Source = entry.Source;
            ThemeName = entry.Name;
            Description = entry.Description ?? "";
        }
        finally { _loadingNamedEntry = false; }
    }

    private void RefreshSavedList(string? selectName)
    {
        SavedNames.Clear();
        foreach (var e in UserColorGenStore.Instance.Entries) SavedNames.Add(e.Name);

        string? toSelect = !string.IsNullOrEmpty(selectName) && SavedNames.Contains(selectName)
            ? selectName : null;
        _selectedSavedName = toSelect;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
    }

    private static string MakeClassName(string themeName)
    {
        var sb = new System.Text.StringBuilder();
        bool upper = true;
        foreach (char c in themeName ?? "")
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(upper ? char.ToUpperInvariant(c) : c); upper = false; }
            else upper = true;
        }
        if (sb.Length == 0) sb.Append("MyColorGen");
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
