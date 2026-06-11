using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of <c>UserEquationDialog</c>. The editor surface is split into
/// two tabs:
///   Tab 0 "User Equation" — C#-style body (Roslyn live-compile via
///                           UserEquationCalculator; debounced 1200 ms).
///   Tab 1 "DSL"           — bare CalcGen DSL fed straight to CalculatorGen.
///                           Live-validated through EquationParser; no Roslyn,
///                           no auto-render. Compile/Generate must be clicked.
///
/// Debounce was 500 ms → 1200 ms → 1800 ms. The error span used to be
/// applied to the TextBox's <c>SelectionStart/End</c> as soon as it was
/// produced; if validation fired while the user was still typing, the next
/// keystroke replaced the selected text. Two fixes are now in place:
///   1) The view defers applying the selection until the editor loses focus
///      (see <c>UserEquationView.ApplyErrorSpan</c> / <c>FlushPending</c>).
///      Status-bar text still updates immediately for live feedback.
///   2) Debounce raised to 1800 ms so the validator does less work during
///      bursts of typing.
/// With (1) in place (2) is no longer strictly necessary, but the longer
/// window cuts CPU spent on partial-source parses.
///
/// Save/Delete/Promote/Compile/Generate sit ABOVE the TabControl and route to
/// the active tab. Saved entries carry a <see cref="UserEquationKind"/> so they
/// restore into the tab they were authored in.
///
/// Host wires the same five callbacks as before:
///   <see cref="CompileRequested"/>   — recompile current source (Roslyn path)
///   <see cref="RenderRequested"/>    — re-render only (rotation changed)
///   <see cref="PromotionChanged"/>   — refresh main fractal-type dropdown
///   <see cref="NamePromptRequested"/>— ask user for a name on Save…
///   <see cref="ConfirmDeleteRequested"/>— confirm before deleting
///   <see cref="HotLoadRequested"/>   — run CalcGen → Roslyn → swap onto pipeline
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
        _dslSource = string.IsNullOrWhiteSpace(parameters.UserEquationDslSource)
            ? "z*z + c"
            : parameters.UserEquationDslSource;
        _activeTabIndex = parameters.UserEquationActiveTab is 0 or 1 ? parameters.UserEquationActiveTab : 0;
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
        ApplyFixCommand = ReactiveCommand.Create(OnApplyFix,
            this.WhenAnyValue(x => x.SuggestedFix).Select(f => !string.IsNullOrEmpty(f)));
        // Docs were re-rooted under User/ + Technical/ — see Docs/Documentation-Plan.md.
        OpenUserEquationHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/CalcGen-UserGuide.md", "User Equation editor",
                                  "CalcGen Help — User Equation tab"));
        OpenDslHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/CalcGen-UserGuide.md", "Grammar at a glance",
                                  "CalcGen Help — DSL grammar"));
        OpenCalcGenHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/CalcGen-UserGuide.md", null, "CalcGen — User Guide"));
        OpenEquationGuideCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("Technical/FractalEquation-DesignGuide.md", null,
                                  "Fractal Equation Design Guide"));

        _params.UserEquationSource = _source;
        _params.UserEquationDslSource = _dslSource;
        _params.UserEquationActiveTab = _activeTabIndex;
    }

    // ── Validation (Tab 0 only) ──
    private bool _validateForCalcGen = true;
    public bool ValidateForCalcGen
    {
        get => _validateForCalcGen;
        set
        {
            this.RaiseAndSetIfChanged(ref _validateForCalcGen, value);
            // Re-run whatever is current — when turning OFF, clear any
            // stale error span; when turning ON, immediately validate.
            if (!value) ClearErrorSpan();
            if (_activeTabIndex == 0) ValidateCalcGenForCurrentSource();
        }
    }

    // ── Suggested fix (one-click / Ctrl+. replacement for the error span) ──
    private string? _suggestedFix;
    public string? SuggestedFix
    {
        get => _suggestedFix;
        private set => this.RaiseAndSetIfChanged(ref _suggestedFix, value);
    }
    public bool HasSuggestedFix => !string.IsNullOrEmpty(_suggestedFix);

    // ── Error span (consumed by code-behind to set TextBox.Selection) ──
    private int _errorSpanStart;
    private int _errorSpanLength;
    /// <summary>Start of the offending substring in the active tab's source.
    /// Combined with <see cref="ErrorSpanLength"/> for selection-based
    /// highlight. 0 / 0 means "no error span" — code-behind should clear
    /// any prior selection.</summary>
    public int ErrorSpanStart { get => _errorSpanStart; private set => this.RaiseAndSetIfChanged(ref _errorSpanStart, value); }
    public int ErrorSpanLength { get => _errorSpanLength; private set => this.RaiseAndSetIfChanged(ref _errorSpanLength, value); }

    /// <summary>Raised after error-span changes so the view can apply the
    /// span to the correct TextBox for the active tab. Argument is the
    /// tab the span applies to (0 = UserEquation, 1 = Dsl). Span values
    /// are read from <see cref="ErrorSpanStart"/> / <see cref="ErrorSpanLength"/>.</summary>
    public event Action<int>? ErrorSpanChanged;

    private void SetErrorSpan(int tab, int start, int length, string? fix = null)
    {
        ErrorSpanStart = Math.Max(0, start);
        ErrorSpanLength = Math.Max(0, length);
        SuggestedFix = fix;
        this.RaisePropertyChanged(nameof(HasSuggestedFix));
        ErrorSpanChanged?.Invoke(tab);
    }

    private void ClearErrorSpan()
    {
        bool hadSpan = _errorSpanStart != 0 || _errorSpanLength != 0;
        bool hadFix  = !string.IsNullOrEmpty(_suggestedFix);
        if (!hadSpan && !hadFix) return;
        ErrorSpanStart = 0;
        ErrorSpanLength = 0;
        SuggestedFix = null;
        this.RaisePropertyChanged(nameof(HasSuggestedFix));
        ErrorSpanChanged?.Invoke(_activeTabIndex);
    }

    // Splice the current SuggestedFix into the active tab's source at the
    // tracked ErrorSpan. Re-validates immediately so the status flips
    // green/red without waiting for the debounce. No-op when no fix or
    // when the span is zero-length (defensive — UI hides the button in
    // that case anyway).
    private void OnApplyFix()
    {
        if (string.IsNullOrEmpty(_suggestedFix)) return;
        if (_errorSpanLength <= 0) return;
        if (_activeTabIndex == 1)
        {
            string src = _dslSource ?? string.Empty;
            if (_errorSpanStart < 0 || _errorSpanStart + _errorSpanLength > src.Length) return;
            string next = src.Substring(0, _errorSpanStart) + _suggestedFix +
                          src.Substring(_errorSpanStart + _errorSpanLength);
            DslSource = next;
            _debounce.Disposable = null;
            ValidateDslNow();
        }
        else
        {
            string src = _source ?? string.Empty;
            if (_errorSpanStart < 0 || _errorSpanStart + _errorSpanLength > src.Length) return;
            string next = src.Substring(0, _errorSpanStart) + _suggestedFix +
                          src.Substring(_errorSpanStart + _errorSpanLength);
            Source = next;
            _debounce.Disposable = null;
            _params.UserEquationSource = next;
            CompileRequested?.Invoke();
            if (_validateForCalcGen) ValidateCalcGenForCurrentSource();
        }
    }

    public ObservableCollection<string> SavedNames { get; }

    // ── User Equation editor (Tab 0) ──
    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            this.RaiseAndSetIfChanged(ref _source, value);
            if (!_loadingNamedEquation) _params.UserEquationName = null;
            if (_activeTabIndex == 0) ScheduleCompile();
        }
    }

    // ── DSL editor (Tab 1) ──
    private string _dslSource;
    public string DslSource
    {
        get => _dslSource;
        set
        {
            this.RaiseAndSetIfChanged(ref _dslSource, value);
            if (!_loadingNamedEquation) _params.UserEquationName = null;
            _params.UserEquationDslSource = _dslSource;
            if (_activeTabIndex == 1) ScheduleDslValidate();
        }
    }

    // ── Active tab ──
    private int _activeTabIndex;
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            int clamped = value is 0 or 1 ? value : 0;
            this.RaiseAndSetIfChanged(ref _activeTabIndex, clamped);
            _params.UserEquationActiveTab = clamped;
            // Clear any tab-specific status when switching; trigger the new
            // tab's validation path so the user sees a fresh state.
            _debounce.Disposable = null;
            StatusText = string.Empty;
            StatusIsError = false;
            if (clamped == 0) ScheduleCompile();
            else ScheduleDslValidate();
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
    public ReactiveCommand<Unit, Unit> ApplyFixCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenUserEquationHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDslHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCalcGenHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenEquationGuideCommand { get; }

    /// <summary>Host opens an in-app help viewer. Args: (docId, anchor, title).
    /// docId is a filename inside the embedded Docs/ resource folder.
    /// anchor is a heading substring; null = show the whole document.</summary>
    public event Action<string, string?, string>? HelpRequested;

    public event Action? CompileRequested;
    public event Action? RenderRequested;
    public event Action? PromotionChanged;

    /// <summary>Host shows a name-entry dialog and returns the entered name (or null).</summary>
    public event Func<string, string?>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirm and returns true to proceed.</summary>
    public event Func<string, bool>? ConfirmDeleteRequested;

    /// <summary>Host shows a yes/no overwrite confirm and returns true to proceed.
    /// Fired only when Save would replace an existing equation with the same name.</summary>
    public event Func<string, bool>? ConfirmOverwriteRequested;

    /// <summary>Host compiles + loads the equation via CalcGen and swaps
    /// the result onto the render pipeline. Args: (equation, className).
    /// Return value: null on success, error message on failure.</summary>
    public event Func<string, string, string?>? HotLoadRequested;

    /// <summary>Force an immediate compile (cancel pending debounce).
    /// Only meaningful on the User Equation tab — DSL tab does not feed
    /// the Roslyn pipeline.</summary>
    public void TriggerCompile()
    {
        _debounce.Disposable = null;
        if (_activeTabIndex != 0) return;
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

    /// <summary>Select+load a saved equation by name. No-op if absent.
    /// Switches to the tab matching the entry's <see cref="UserEquationKind"/>.</summary>
    public void LoadEquationByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserEquationStore.Instance.GetByName(name);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try
        {
            if (entry.Kind == UserEquationKind.Dsl) DslSource = entry.Source;
            else Source = entry.Source;
        }
        finally { _loadingNamedEquation = false; }
        _params.UserEquationName = entry.Name;
        SelectedSavedName = entry.Name;
        ActiveTabIndex = entry.Kind == UserEquationKind.Dsl ? 1 : 0;
        _debounce.Disposable = null;
    }

    private void ScheduleCompile()
    {
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(1800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                _params.UserEquationSource = _source;
                CompileRequested?.Invoke();
                // After Roslyn compile, also surface CalcGen-compatibility
                // problems if the user opted in. Roslyn won't catch them
                // because the C# source compiles fine; CalcGen's stricter
                // DSL is what trips on Complex.ImaginaryOne / Abs / etc.
                if (_validateForCalcGen) ValidateCalcGenForCurrentSource();
            });
    }

    // Run the CalcGen preprocessor + parser over the UE-tab source. Surfaces
    // the FIRST blocker in the status bar with a span the view can highlight.
    // Called from Source setter (via ScheduleCompile when the checkbox is on)
    // and directly when toggling ValidateForCalcGen on. Roslyn compile errors
    // already shown by the host take precedence — only overrides status when
    // Roslyn was happy or this surfaced an error.
    private void ValidateCalcGenForCurrentSource()
    {
        if (_activeTabIndex != 0 || !_validateForCalcGen)
        {
            ClearErrorSpan();
            return;
        }
        string raw = _source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) { ClearErrorSpan(); return; }

        string equation = EquationPreprocessor.Preprocess(raw, out PreprocessDiagnostic? diag);
        if (diag != null)
        {
            StatusText = $"CalcGen: {diag.Message}";
            StatusIsError = true;
            // UE tab is Roslyn-compiled C#: pick the C# form. Applying the
            // DSL form here would either fail Roslyn compile (`abs(z)` is
            // not a C# function) or, worse, look like a typo to the user
            // who explicitly wrote `Complex.*`.
            SetErrorSpan(tab: 0, diag.Start, diag.Length, diag.SuggestionCSharp);
            return;
        }
        if (string.IsNullOrWhiteSpace(equation))
        {
            ClearErrorSpan();
            return;
        }
        try
        {
            EquationParser.Parse(equation);
            // CalcGen accepts it. Don't stomp on Roslyn's "✓ Compiled" — only
            // overwrite if the status is currently a CalcGen complaint we
            // raised on a previous tick.
            if (StatusIsError && StatusText.StartsWith("CalcGen:", StringComparison.Ordinal))
            {
                StatusText = "✓ Compiled (CalcGen OK)";
                StatusIsError = false;
            }
            ClearErrorSpan();
        }
        catch (Exception ex)
        {
            // Parser errors carry col (and sometimes line) in the message
            // but they're measured against the PREPROCESSED string, not the
            // user's typed source. Can't reliably map back — show the
            // message only and clear the span so we don't highlight wrong.
            StatusText = $"CalcGen: {ex.Message}";
            StatusIsError = true;
            ClearErrorSpan();
        }
    }

    // Live-validate the DSL tab's source by running the lexer + parser only.
    // No render, no Roslyn — just surface parse errors in the status bar so
    // the user sees red/green as they type. Mirrors the 500 ms debounce of
    // the User Equation tab so the feel is consistent.
    private void ScheduleDslValidate()
    {
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(1800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ValidateDslNow());
    }

    private void ValidateDslNow()
    {
        string raw = _dslSource ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusText = string.Empty;
            StatusIsError = false;
            ClearErrorSpan();
            return;
        }
        try
        {
            EquationParser.Parse(raw);
            StatusText = "✓ DSL parses";
            StatusIsError = false;
            ClearErrorSpan();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            // Lexer / parser format errors carry their position as
            //   "... at line L, col C." or "... at col C."
            // Map back to a char offset in the DSL source so the view can
            // select the bad token. Length = 1 (caret) — token boundary
            // recovery would require re-tokenising; out of scope here.
            var m = Regex.Match(ex.Message, @"\bcol\s+(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int col))
            {
                int line = 1;
                var lm = Regex.Match(ex.Message, @"\bline\s+(\d+)");
                if (lm.Success) int.TryParse(lm.Groups[1].Value, out line);
                int offset = ColToOffset(raw, line, col);
                // Lexer "Unknown identifier 'foo' at col N. Did you mean 'prev'?"
                // — extract the bad identifier's length so the span covers
                // the whole token, and pull the suggested replacement so
                // Apply Fix can splice it in.
                int spanLen = 1;
                string? fix = null;
                var idM = Regex.Match(ex.Message, @"Unknown identifier '([^']+)'");
                if (idM.Success) spanLen = idM.Groups[1].Value.Length;
                var hintM = Regex.Match(ex.Message, @"Did you mean '([^']+)'");
                if (hintM.Success) fix = hintM.Groups[1].Value;
                SetErrorSpan(tab: 1, offset, spanLen, fix);
            }
            else
            {
                ClearErrorSpan();
            }
        }
    }

    // Walk source line-by-line until the target line, then add (col-1) for
    // the character offset. Clamps to source length so a stale span past
    // the end of a freshly-trimmed buffer doesn't throw.
    private static int ColToOffset(string source, int line, int col)
    {
        if (line <= 1) return Math.Min(Math.Max(0, col - 1), source.Length);
        int offset = 0;
        int seen = 1;
        while (seen < line && offset < source.Length)
        {
            int nl = source.IndexOf('\n', offset);
            if (nl < 0) break;
            offset = nl + 1;
            seen++;
        }
        return Math.Min(offset + Math.Max(0, col - 1), source.Length);
    }

    private void OnSavedSelectionChanged()
    {
        if (_selectedSavedName is null) { _promote = false; this.RaisePropertyChanged(nameof(Promote)); return; }
        var entry = UserEquationStore.Instance.GetByName(_selectedSavedName);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try
        {
            if (entry.Kind == UserEquationKind.Dsl)
            {
                DslSource = entry.Source;
                _params.UserEquationDslSource = entry.Source;
            }
            else
            {
                Source = entry.Source;
                _params.UserEquationSource = entry.Source;
            }
        }
        finally { _loadingNamedEquation = false; }
        _params.UserEquationName = entry.Name;

        _promote = entry.Promoted;
        this.RaisePropertyChanged(nameof(Promote));

        ActiveTabIndex = entry.Kind == UserEquationKind.Dsl ? 1 : 0;
        _debounce.Disposable = null;
        if (entry.Kind == UserEquationKind.Dsl) ValidateDslNow();
        else CompileRequested?.Invoke();
    }

    private void OnSave()
    {
        string defaultName = _selectedSavedName ?? string.Empty;
        string? name = NamePromptRequested?.Invoke(defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        string trimmed = name.Trim();
        // Confirm before silently replacing an existing entry. Store match
        // is case-insensitive — mirror that here.
        if (UserEquationStore.Instance.GetByName(trimmed) is not null
            && ConfirmOverwriteRequested?.Invoke(trimmed) == false)
            return;

        var (kind, source) = ActiveSource();
        var entry = UserEquationStore.Instance.SaveEquation(trimmed, source, kind);
        if (entry is null) return;

        _params.UserEquationName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    // ── CalcGen pipeline ─────────────────────────────────────────────────
    //
    // Both Generate and HotLoad route by the currently active tab:
    //   Tab 0 (User Equation): run source through EquationPreprocessor to
    //                          rewrite C# Complex.* calls into DSL.
    //   Tab 1 (DSL):           feed source straight to CalculatorGen with
    //                          no preprocessing (lexer/parser handle errors).
    private void OnGenerateViaCalcGen()
    {
        if (!TryGetCalcGenSource(out string equation, out string baseName)) return;

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

    private void OnHotLoadViaCalcGen()
    {
        if (!TryGetCalcGenSource(out string equation, out string baseName)) return;

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

    // Produce the (DSL string, base class name) pair to hand to CalcGen for
    // the currently active tab. Writes any error to the status bar and
    // returns false. Tab 0 runs the C#→DSL preprocessor; Tab 1 trims the
    // raw source — the parser already gives crisp diagnostics.
    private bool TryGetCalcGenSource(out string equation, out string baseName)
    {
        equation = string.Empty;
        baseName = string.Empty;

        string raw;
        string fallbackBase;
        if (_activeTabIndex == 1)
        {
            raw = _dslSource ?? string.Empty;
            fallbackBase = "UserDslEquation";
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                ShowError("Equation is empty.");
                return false;
            }
            equation = trimmed;
        }
        else
        {
            raw = _source ?? string.Empty;
            fallbackBase = "UserHotLoaded";
            string preProcessed = EquationPreprocessor.Preprocess(raw, out string? preErr);
            if (preErr != null)
            {
                ShowError(preErr);
                return false;
            }
            if (string.IsNullOrWhiteSpace(preProcessed))
            {
                ShowError("Equation is empty.");
                return false;
            }
            equation = preProcessed;
        }

        baseName = string.IsNullOrWhiteSpace(_params.UserEquationName)
            ? fallbackBase
            : Regex.Replace(_params.UserEquationName, @"[^A-Za-z0-9_]", "");
        if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
        return true;
    }

    private (UserEquationKind Kind, string Source) ActiveSource() =>
        _activeTabIndex == 1
            ? (UserEquationKind.Dsl, _dslSource ?? string.Empty)
            : (UserEquationKind.UserEquation, _source ?? string.Empty);

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
