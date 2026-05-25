using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of <c>UserEquationDialog</c>. Edits a
/// <see cref="FractalParameters.UserEquationSource"/> with a 500 ms idle
/// debounce that mirrors the WinForms dialog. Saved equations and the
/// promote flag are persisted through <see cref="UserEquationStore"/>
/// (lives in Abstractions, file-system stored under %APPDATA%).
///
/// Host wires three events:
///   <see cref="CompileRequested"/>   — recompile current source
///   <see cref="RenderRequested"/>    — re-render only (rotation changed)
///   <see cref="PromotionChanged"/>   — refresh main fractal-type dropdown
///   <see cref="NamePromptRequested"/>— ask user for a name on Save…
///   <see cref="ConfirmDeleteRequested"/>— confirm before deleting
/// </summary>
public sealed class UserEquationViewModel : ViewModelBase
{
    private readonly FractalParameters _params;
    private readonly System.Reactive.Disposables.SerialDisposable _debounce = new();
    private bool _loadingNamedEquation;

    public UserEquationViewModel(FractalParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _params = parameters;

        _source = string.IsNullOrWhiteSpace(parameters.UserEquationSource)
            ? "return z*z + c;"
            : parameters.UserEquationSource;
        _rotationDegrees = Math.Clamp(parameters.UserEquationRotationDegrees, -360, 360);

        SavedNames = new ObservableCollection<string>();

        UserEquationStore.Instance.Load();
        RefreshSavedList(parameters.UserEquationName);

        SaveCommand = ReactiveCommand.Create(OnSave);
        DeleteCommand = ReactiveCommand.Create(OnDelete,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        RotPlus90Command = ReactiveCommand.Create(() => BumpRotation(90.0));
        RotMinus90Command = ReactiveCommand.Create(() => BumpRotation(-90.0));
        RotResetCommand = ReactiveCommand.Create(() => SetRotation(0.0));

        _params.UserEquationSource = _source;
    }

    public ObservableCollection<string> SavedNames { get; }

    // ── Editor ──
    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            this.RaiseAndSetIfChanged(ref _source, value);
            if (!_loadingNamedEquation) _params.UserEquationName = null;
            ScheduleCompile();
        }
    }

    // ── Saved selection ──
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

    // ── Promote ──
    private bool _promote;
    public bool Promote
    {
        get => _promote;
        set
        {
            this.RaiseAndSetIfChanged(ref _promote, value);
            if (_selectedSavedName is null) return;
            if (UserEquationStore.Instance.SetPromoted(_selectedSavedName, value))
                PromotionChanged?.Invoke();
        }
    }

    public bool PromoteEnabled => !string.IsNullOrEmpty(_selectedSavedName);

    // ── Rotation ──
    private double _rotationDegrees;
    public double RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            double clamped = Math.Clamp(value, -360, 360);
            this.RaiseAndSetIfChanged(ref _rotationDegrees, clamped);
            _params.UserEquationRotationDegrees = clamped;
            RenderRequested?.Invoke();
        }
    }

    // ── Error / status ──
    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => this.RaiseAndSetIfChanged(ref _statusIsError, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> RotPlus90Command { get; }
    public ReactiveCommand<Unit, Unit> RotMinus90Command { get; }
    public ReactiveCommand<Unit, Unit> RotResetCommand { get; }

    public event Action? CompileRequested;
    public event Action? RenderRequested;
    public event Action? PromotionChanged;

    /// <summary>Host shows a name-entry dialog and returns the entered name (or null).</summary>
    public event Func<string, string?>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirm and returns true to proceed.</summary>
    public event Func<string, bool>? ConfirmDeleteRequested;

    /// <summary>Force an immediate compile (cancel pending debounce).</summary>
    public void TriggerCompile()
    {
        _debounce.Disposable = null;
        _params.UserEquationSource = _source;
        CompileRequested?.Invoke();
    }

    /// <summary>Host calls this with compile result. Empty error => success.</summary>
    public void ShowError(string? error)
    {
        bool ok = string.IsNullOrEmpty(error);
        StatusText = ok ? "✓ Compiled" : error!;
        StatusIsError = !ok;
    }

    /// <summary>Select+load a saved equation by name. No-op if absent.</summary>
    public void LoadEquationByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserEquationStore.Instance.GetByName(name);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try { Source = entry.Source; }
        finally { _loadingNamedEquation = false; }
        _params.UserEquationName = entry.Name;
        SelectedSavedName = entry.Name;
        _debounce.Disposable = null;
    }

    private void ScheduleCompile()
    {
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                _params.UserEquationSource = _source;
                CompileRequested?.Invoke();
            });
    }

    private void OnSavedSelectionChanged()
    {
        if (_selectedSavedName is null) { _promote = false; this.RaisePropertyChanged(nameof(Promote)); return; }
        var entry = UserEquationStore.Instance.GetByName(_selectedSavedName);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try { Source = entry.Source; }
        finally { _loadingNamedEquation = false; }
        _params.UserEquationSource = entry.Source;
        _params.UserEquationName = entry.Name;

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

        var entry = UserEquationStore.Instance.SaveEquation(name.Trim(), _source);
        if (entry is null) return;

        _params.UserEquationName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    private void OnDelete()
    {
        if (_selectedSavedName is null) return;
        if (ConfirmDeleteRequested?.Invoke(_selectedSavedName) != true) return;

        UserEquationStore.Instance.Remove(_selectedSavedName);
        RefreshSavedList(null);
    }

    private void BumpRotation(double delta)
    {
        double next = _params.UserEquationRotationDegrees + delta;
        while (next > 360.0) next -= 360.0;
        while (next < -360.0) next += 360.0;
        SetRotation(next);
    }

    private void SetRotation(double degrees) => RotationDegrees = degrees;

    private void RefreshSavedList(string? selectName)
    {
        SavedNames.Clear();
        foreach (var e in UserEquationStore.Instance.Equations) SavedNames.Add(e.Name);

        string? toSelect = !string.IsNullOrEmpty(selectName) && SavedNames.Contains(selectName)
            ? selectName
            : null;

        _selectedSavedName = toSelect;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
        this.RaisePropertyChanged(nameof(PromoteEnabled));

        if (toSelect is not null)
        {
            var entry = UserEquationStore.Instance.GetByName(toSelect);
            _promote = entry?.Promoted ?? false;
        }
        else _promote = false;
        this.RaisePropertyChanged(nameof(Promote));
    }
}
