// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of <c>SandboxDialog</c>. Edits
/// <see cref="FractalParameters.SandboxSource"/> with the same 500 ms idle
/// debounce as <see cref="UserEquationViewModel"/>. Source uses the restricted
/// SandboxExpression DSL — host owns the compiler; the VM only schedules
/// the compile request and surfaces the result via <see cref="ShowError"/>.
///
/// Export/Import go through host callbacks that own the OpenFile/SaveFile
/// dialog so the VM stays UI-agnostic.
/// </summary>
public sealed class SandboxViewModel : ViewModelBase
{
    private readonly FractalParameters _params;
    private readonly System.Reactive.Disposables.SerialDisposable _debounce = new();
    private bool _loadingNamedEquation;

    public SandboxViewModel(FractalParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _params = parameters;

        _source = string.IsNullOrWhiteSpace(parameters.SandboxSource)
            ? "z*z + c"
            : parameters.SandboxSource;

        SavedNames = new ObservableCollection<string>();

        SandboxEquationStore.Instance.Load();
        RefreshSavedList(parameters.SandboxName);

        SaveCommand = ReactiveCommand.Create(OnSave);
        DeleteCommand = ReactiveCommand.Create(OnDelete,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ExportCommand = ReactiveCommand.Create(OnExport);
        ImportCommand = ReactiveCommand.Create(OnImport);
        OpenHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/Avalonia-UserGuide.md", "Sandbox", "Sandbox — Help"));

        _params.SandboxSource = _source;
    }

    public ObservableCollection<string> SavedNames { get; }

    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            this.RaiseAndSetIfChanged(ref _source, value);
            if (!_loadingNamedEquation) _params.SandboxName = null;
            ScheduleCompile();
        }
    }

    private string? _selectedSavedName;
    public string? SelectedSavedName
    {
        get => _selectedSavedName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSavedName, value);
            this.RaisePropertyChanged(nameof(PromoteEnabled));
            OnSavedSelectionChanged();
        }
    }

    private bool _promote;
    public bool Promote
    {
        get => _promote;
        set
        {
            this.RaiseAndSetIfChanged(ref _promote, value);
            if (_selectedSavedName is null) return;
            if (SandboxEquationStore.Instance.SetPromoted(_selectedSavedName, value))
                PromotionChanged?.Invoke();
        }
    }

    public bool PromoteEnabled => !string.IsNullOrEmpty(_selectedSavedName);

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => this.RaiseAndSetIfChanged(ref _statusIsError, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    /// <summary>Args: (docId, anchor, title). View opens HelpViewerView.</summary>
    public event Action<string, string?, string>? HelpRequested;

    public event Action? CompileRequested;
    public event Action? PromotionChanged;

    /// <summary>Host shows a name-entry dialog and returns the entered name (or null).</summary>
    public event Func<string, string?>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirm and returns true to proceed.</summary>
    public event Func<string, bool>? ConfirmDeleteRequested;

    /// <summary>Host shows a yes/no overwrite confirm and returns true to proceed.
    /// Fired only when Save would replace an existing equation with the same name.</summary>
    public event Func<string, bool>? ConfirmOverwriteRequested;

    /// <summary>Host shows SaveFile dialog; returns chosen path or null.</summary>
    public event Func<string, string?>? SaveFilePromptRequested;

    /// <summary>Host shows OpenFile dialog; returns chosen path or null.</summary>
    public event Func<string?>? OpenFilePromptRequested;

    /// <summary>Host shows a simple info/error message box.</summary>
    public event Action<string, string, bool>? MessageRequested;

    public void TriggerCompile()
    {
        _debounce.Disposable = null;
        _params.SandboxSource = _source;
        CompileRequested?.Invoke();
    }

    public void ShowError(string? error)
    {
        bool ok = string.IsNullOrEmpty(error);
        StatusText = ok ? "✓ Compiled" : error!;
        StatusIsError = !ok;
    }

    public void LoadEquationByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = SandboxEquationStore.Instance.GetByName(name);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try { Source = entry.Source; }
        finally { _loadingNamedEquation = false; }
        _params.SandboxName = entry.Name;
        SelectedSavedName = entry.Name;
        _debounce.Disposable = null;
    }

    private void ScheduleCompile()
    {
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                _params.SandboxSource = _source;
                CompileRequested?.Invoke();
            });
    }

    private void OnSavedSelectionChanged()
    {
        if (_selectedSavedName is null) { _promote = false; this.RaisePropertyChanged(nameof(Promote)); return; }
        var entry = SandboxEquationStore.Instance.GetByName(_selectedSavedName);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try { Source = entry.Source; }
        finally { _loadingNamedEquation = false; }
        _params.SandboxSource = entry.Source;
        _params.SandboxName = entry.Name;

        _promote = entry.Promoted;
        this.RaisePropertyChanged(nameof(Promote));

        _debounce.Disposable = null;
        CompileRequested?.Invoke();
    }

    private void OnSave()
    {
        string defaultName = _selectedSavedName ?? string.Empty;
        string? name = NamePromptRequested?.Invoke(defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        string trimmed = name.Trim();
        if (SandboxEquationStore.Instance.GetByName(trimmed) is not null
            && ConfirmOverwriteRequested?.Invoke(trimmed) == false)
            return;

        var entry = SandboxEquationStore.Instance.SaveEquation(trimmed, _source);
        if (entry is null) return;

        _params.SandboxName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    private void OnDelete()
    {
        if (_selectedSavedName is null) return;
        if (ConfirmDeleteRequested?.Invoke(_selectedSavedName) != true) return;

        SandboxEquationStore.Instance.Remove(_selectedSavedName);
        RefreshSavedList(null);
    }

    private void OnExport()
    {
        var equations = SandboxEquationStore.Instance.Equations;
        if (equations.Count == 0)
        {
            MessageRequested?.Invoke("Export Sandbox Equations",
                "There are no saved sandbox equations to export.", false);
            return;
        }

        string defaultName = (_selectedSavedName ?? "sandbox-equations") + ".json";
        string? path = SaveFilePromptRequested?.Invoke(defaultName);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var snapshot = new List<SandboxEquationEntry>(equations.Count);
            foreach (var eq in equations)
                snapshot.Add(new SandboxEquationEntry
                {
                    Name = eq.Name,
                    Source = eq.Source,
                    Promoted = eq.Promoted
                });
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, opts));
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke("Export Error", $"Export failed:\n\n{ex.Message}", true);
        }
    }

    private void OnImport()
    {
        string? path = OpenFilePromptRequested?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;

        List<SandboxEquationEntry>? imported;
        try
        {
            string text = File.ReadAllText(path);
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("["))
            {
                imported = JsonSerializer.Deserialize<List<SandboxEquationEntry>>(text);
            }
            else
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("SandboxEquations", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    imported = JsonSerializer.Deserialize<List<SandboxEquationEntry>>(arr.GetRawText());
                }
                else
                {
                    imported = null;
                }
            }
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke("Import Error",
                $"Could not read or parse the file:\n\n{ex.Message}", true);
            return;
        }

        if (imported == null || imported.Count == 0)
        {
            MessageRequested?.Invoke("Import Sandbox Equations",
                "The file contains no sandbox equations.", false);
            return;
        }

        int added = 0, skipped = 0;
        foreach (var eq in imported)
        {
            if (eq == null || string.IsNullOrWhiteSpace(eq.Name)) continue;
            if (SandboxEquationStore.Instance.GetByName(eq.Name) != null) { skipped++; continue; }
            SandboxEquationStore.Instance.Equations.Add(new SandboxEquationEntry
            {
                Name = eq.Name,
                Source = eq.Source ?? string.Empty,
                Promoted = eq.Promoted
            });
            added++;
        }

        if (added > 0) SandboxEquationStore.Instance.Save();

        RefreshSavedList(_params.SandboxName);
        if (added > 0) PromotionChanged?.Invoke();

        string summary = added == 1 ? "1 equation imported" : $"{added} equations imported";
        if (skipped > 0) summary += $" ({skipped} skipped — name exists)";
        MessageRequested?.Invoke("Import Sandbox Equations", summary, false);
    }

    private void RefreshSavedList(string? selectName)
    {
        SavedNames.Clear();
        foreach (var e in SandboxEquationStore.Instance.Equations) SavedNames.Add(e.Name);

        string? toSelect = !string.IsNullOrEmpty(selectName) && SavedNames.Contains(selectName)
            ? selectName
            : null;

        _selectedSavedName = toSelect;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
        this.RaisePropertyChanged(nameof(PromoteEnabled));

        if (toSelect is not null)
        {
            var entry = SandboxEquationStore.Instance.GetByName(toSelect);
            _promote = entry?.Promoted ?? false;
        }
        else _promote = false;
        this.RaisePropertyChanged(nameof(Promote));
    }
}
