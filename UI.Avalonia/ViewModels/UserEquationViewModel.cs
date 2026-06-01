using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FracturingFog.CalculatorGen;
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
        GenerateViaCalcGenCommand = ReactiveCommand.Create(OnGenerateViaCalcGen);
        HotLoadViaCalcGenCommand = ReactiveCommand.Create(OnHotLoadViaCalcGen);

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
    public ReactiveCommand<Unit, Unit> GenerateViaCalcGenCommand { get; }
    public ReactiveCommand<Unit, Unit> HotLoadViaCalcGenCommand { get; }

    public event Action? CompileRequested;
    public event Action? RenderRequested;
    public event Action? PromotionChanged;

    /// <summary>Host shows a name-entry dialog and returns the entered name (or null).</summary>
    public event Func<string, string?>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirm and returns true to proceed.</summary>
    public event Func<string, bool>? ConfirmDeleteRequested;

    /// <summary>Host compiles + loads the equation via CalcGen and swaps
    /// the result onto the render pipeline. Args: (equation, className).
    /// Return value: null on success, error message on failure.</summary>
    public event Func<string, string, string?>? HotLoadRequested;

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

    // ── CalcGen pipeline ─────────────────────────────────────────────────
    //
    // Run the user's equation through CalculatorGen and write the
    // generated calculator to Calculators/Generated/. CalcGen's grammar
    // is stricter than UserEquationCalculator's Roslyn-backed source: it
    // expects a bare polynomial RHS (e.g. `z*z + c`, no `return`, no
    // semicolons), so this method extracts the RHS from the user's
    // C# expression-body source. Anything CalcGen can't parse → status
    // shows the error and no file is written. On success, the user must
    // rebuild the app for the generated calculator to appear in the
    // FractalType dropdown — there's no in-process hot-load yet.
    private void OnGenerateViaCalcGen()
    {
        string source = _source ?? string.Empty;
        // Pre-translate C# `Complex.*` syntax → CalcGen DSL. Also strips
        // `return ` prefix and trailing `;`. Surfaces a crisp error on
        // unsupported constructs (Complex.ImaginaryOne, new Complex(...),
        // Complex.Abs) instead of letting them fall through to the
        // lexer with a vague "Unknown identifier" diagnostic.
        string equation = EquationPreprocessor.Preprocess(source, out string? preErr);
        if (preErr != null)
        {
            ShowError(preErr);
            return;
        }
        if (string.IsNullOrWhiteSpace(equation))
        {
            ShowError("Equation is empty.");
            return;
        }

        string baseName = string.IsNullOrWhiteSpace(_params.UserEquationName)
            ? "UserGenerated"
            : Regex.Replace(_params.UserEquationName, @"[^A-Za-z0-9_]", "");
        if (string.IsNullOrEmpty(baseName)) baseName = "UserGenerated";

        var result = CalculatorGenApi.Generate(equation, baseName, includeSelfTest: true);
        if (!result.Ok)
        {
            ShowError($"CalcGen: {result.Error}");
            return;
        }

        try
        {
            string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Calculators", "Generated");
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);
            string calcPath = Path.Combine(outDir, $"{result.ClassName}.cs");
            File.WriteAllText(calcPath, result.Source, new UTF8Encoding(false));
            if (result.SelfTest != null)
            {
                string stPath = Path.Combine(outDir, $"{result.ClassName}SelfTest.cs");
                File.WriteAllText(stPath, result.SelfTest, new UTF8Encoding(false));
            }
            StatusText = $"✓ CalcGen → {Path.GetFileName(calcPath)} (rebuild to pick up)";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            ShowError($"CalcGen write failed: {ex.Message}");
        }
    }

    // Compile + load via CalcGen WITHOUT touching disk. Calls the host's
    // HotLoadRequested callback, which runs Roslyn over the generated
    // source and swaps the resulting calculator onto the render
    // pipeline. The new calculator stays active until the host clears
    // it or the user closes/reopens the dialog.
    private void OnHotLoadViaCalcGen()
    {
        string source = _source ?? string.Empty;
        // Pre-translate C# `Complex.*` syntax → CalcGen DSL. See
        // OnGenerateViaCalcGen for the translation table + reject list.
        string equation = EquationPreprocessor.Preprocess(source, out string? preErr);
        if (preErr != null)
        {
            ShowError(preErr);
            return;
        }
        if (string.IsNullOrWhiteSpace(equation))
        {
            ShowError("Equation is empty.");
            return;
        }

        string baseName = string.IsNullOrWhiteSpace(_params.UserEquationName)
            ? "UserHotLoaded"
            : Regex.Replace(_params.UserEquationName, @"[^A-Za-z0-9_]", "");
        if (string.IsNullOrEmpty(baseName)) baseName = "UserHotLoaded";

        var handler = HotLoadRequested;
        if (handler == null)
        {
            ShowError("Hot-load not wired by host.");
            return;
        }

        string? err = handler.Invoke(equation, baseName);
        if (err == null)
        {
            StatusText = $"✓ Hot-loaded {baseName}Calculator";
            StatusIsError = false;
        }
        else
        {
            ShowError(err);
        }
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
