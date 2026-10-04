// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FracturingFog.Abstractions.Assets;
using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// The User Equation editor (#1089): ONE source box in the equation language
/// plus a <b>CalcGen</b> toggle. Replaces the two-tab notebook (C#-style "User
/// Equation" + "DSL"); #1088 already collapsed the data model to one source
/// (<see cref="FractalParameters.UserEquationSource"/>) and a flag
/// (<see cref="FractalParameters.UserEquationUseCalcGen"/>).
///
///   CalcGen off — the safe interpreter renders the source live on edit.
///   CalcGen on  — the interpreter still renders every edit (the flag never
///                 changes the image), and Compile &amp; Load / Compile + Save /
///                 Generate accelerate it. The Roslyn compile runs off the UI
///                 thread with a visible state (<see cref="CompileState"/>:
///                 idle → compiling → compiled / failed) so it never looks like
///                 a hang; <see cref="CalcGenReport"/> says what CalcGen will
///                 and won't accelerate.
///
/// C#-style text (<c>return Complex.Pow(z, 2) + c;</c>) is no longer a mode but
/// is still accepted, e.g. on paste: it is translated for rendering, and the
/// Ctrl+. quick fix (<see cref="SuggestedFix"/>) offers the converted form.
///
/// Edits are debounced (1800 ms) before they render / validate. An error span
/// is applied to the editor's selection only when it is not focused (see
/// <c>UserEquationView.ApplyErrorSpan</c>), so typing is never clobbered.
///
/// Host callbacks:
///   <see cref="CompileRequested"/>   — recompile current source (interpreter)
///   <see cref="RenderRequested"/>    — re-render only (rotation changed)
///   <see cref="PromotionChanged"/>   — refresh main fractal-type dropdown
///   <see cref="NamePromptRequested"/>— ask user for a name on Save…
///   <see cref="ConfirmDeleteRequested"/>— confirm before deleting
///   <see cref="HotLoadRequested"/>   — CalcGen → Roslyn → swap onto pipeline (async)
/// </summary>
public sealed class UserEquationViewModel : ViewModelBase
{
    /// <summary>#1089 — state of the CalcGen compile, shown next to its buttons.</summary>
    public enum CalcGenCompileState { Idle, Compiling, Compiled, Failed }

    private readonly FractalParameters _params;
    private readonly System.Reactive.Disposables.SerialDisposable _debounce = new();
    private bool _loadingNamedEquation;

    public UserEquationViewModel(FractalParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _params = parameters;

        _useCalcGen = parameters.UserEquationUseCalcGen;
        _source = string.IsNullOrWhiteSpace(parameters.UserEquationSource)
            ? "z*z + c"
            : parameters.UserEquationSource;
        _rotationDegrees = Math.Clamp(parameters.UserEquationRotationDegrees, -360, 360);

        SavedNames = new ObservableCollection<string>();

        UserEquationStore.Instance.Load();
        RefreshSavedList(parameters.UserEquationName);

        SaveCommand = ReactiveCommand.CreateFromTask(OnSaveAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(OnDeleteAsync,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ImportCommand = ReactiveCommand.CreateFromTask(OnImportAsync);
        RotPlus90Command = ReactiveCommand.Create(() => BumpRotation(90.0));
        RotMinus90Command = ReactiveCommand.Create(() => BumpRotation(-90.0));
        RotResetCommand = ReactiveCommand.Create(() => SetRotation(0.0));
        // #1089 — the CalcGen actions need the toggle on and no compile in flight.
        var canCalcGen = this.WhenAnyValue(x => x.UseCalcGen, x => x.IsCompiling, (on, busy) => on && !busy);
        GenerateViaCalcGenCommand = ReactiveCommand.Create(OnGenerateViaCalcGen, canCalcGen);
        HotLoadViaCalcGenCommand = ReactiveCommand.CreateFromTask(OnHotLoadViaCalcGenAsync, canCalcGen);
        // Wave 2.3 — Persist + Hot-Load: writes generated source under
        // %LOCALAPPDATA%/FracturingFog/UserCalculators/, then hot-loads it.
        // Host scans the dir at startup so persisted calculators survive
        // a restart with no rebuild.
        HotLoadAndPersistCommand = ReactiveCommand.CreateFromTask(OnHotLoadAndPersistAsync, canCalcGen);
        ApplyFixCommand = ReactiveCommand.Create(OnApplyFix,
            this.WhenAnyValue(x => x.SuggestedFix).Select(f => !string.IsNullOrEmpty(f)));
        // Docs were re-rooted under User/ + Technical/ — see Docs/Documentation-Plan.md.
        OpenEditorHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/CalcGen-UserGuide.md", "The editor at a glance",
                                  "CalcGen Help — User Equation editor"));
        OpenCalcGenHelpCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("User/CalcGen-UserGuide.md", null, "CalcGen — User Guide"));
        OpenEquationGuideCommand = ReactiveCommand.Create(() =>
            HelpRequested?.Invoke("Technical/FractalEquation-DesignGuide.md", null,
                                  "Fractal Equation Design Guide"));
        OpenCookbookCommand = ReactiveCommand.Create(OnOpenCookbook);
        OpenMorphCommand = ReactiveCommand.Create(OnOpenMorph);

        _params.UserEquationSource = _source;
        _params.UserEquationUseCalcGen = _useCalcGen;

        // Validate now (no render) so the status, quick fix, CalcGen report and
        // live preview show as soon as the dialog opens, without waiting for
        // the typing debounce.
        Validate();
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
    /// <summary>Start of the offending substring in <see cref="Source"/>.
    /// Combined with <see cref="ErrorSpanLength"/> for selection-based
    /// highlight. 0 / 0 means "no error span" — code-behind should clear
    /// any prior selection.</summary>
    public int ErrorSpanStart { get => _errorSpanStart; private set => this.RaiseAndSetIfChanged(ref _errorSpanStart, value); }
    public int ErrorSpanLength { get => _errorSpanLength; private set => this.RaiseAndSetIfChanged(ref _errorSpanLength, value); }

    /// <summary>Raised after the error span changes so the view can apply it
    /// to the editor. Span values are read from <see cref="ErrorSpanStart"/> /
    /// <see cref="ErrorSpanLength"/>.</summary>
    public event Action? ErrorSpanChanged;

    private void SetErrorSpan(int start, int length, string? fix = null)
    {
        ErrorSpanStart = Math.Max(0, start);
        ErrorSpanLength = Math.Max(0, length);
        SuggestedFix = fix;
        this.RaisePropertyChanged(nameof(HasSuggestedFix));
        ErrorSpanChanged?.Invoke();
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
        ErrorSpanChanged?.Invoke();
    }

    // Splice the current SuggestedFix into the source at the tracked
    // ErrorSpan, then commit immediately (render + validate) so the status
    // flips without waiting for the debounce. For C#-style text the span is
    // the whole source and the fix is its converted form.
    private void OnApplyFix()
    {
        if (string.IsNullOrEmpty(_suggestedFix)) return;
        if (_errorSpanLength <= 0) return;
        string src = _source ?? string.Empty;
        if (_errorSpanStart < 0 || _errorSpanStart + _errorSpanLength > src.Length) return;
        string next = src.Substring(0, _errorSpanStart) + _suggestedFix +
                      src.Substring(_errorSpanStart + _errorSpanLength);
        Source = next;
        CommitSourceNow();
    }

    public ObservableCollection<string> SavedNames { get; }

    // ── The equation ──
    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            this.RaiseAndSetIfChanged(ref _source, value);
            if (!_loadingNamedEquation) _params.UserEquationName = null;
            // A loaded CalcGen calculator no longer matches the text.
            if (_compileState == CalcGenCompileState.Compiled) CompileState = CalcGenCompileState.Idle;
            ScheduleCommit();
        }
    }

    // ── CalcGen toggle (#1089) ──
    private bool _useCalcGen;
    /// <summary>The equation is meant for CalcGen: shows Compile &amp; Load /
    /// Compile + Save / Generate and the eligibility report. Persisted as
    /// <see cref="FractalParameters.UserEquationUseCalcGen"/> and with a saved
    /// entry. Turning it off returns the view to the live interpreter.</summary>
    public bool UseCalcGen
    {
        get => _useCalcGen;
        set
        {
            if (_useCalcGen == value) return;
            this.RaiseAndSetIfChanged(ref _useCalcGen, value);
            _params.UserEquationUseCalcGen = value;
            CompileState = CalcGenCompileState.Idle;
            if (!value) CommitSourceNow();   // drop any loaded CalcGen calc: back to the interpreter
            else Validate();
        }
    }

    // ── Compile state (#1089) ──
    private CalcGenCompileState _compileState = CalcGenCompileState.Idle;
    public CalcGenCompileState CompileState
    {
        get => _compileState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _compileState, value);
            this.RaisePropertyChanged(nameof(IsCompiling));
            this.RaisePropertyChanged(nameof(CompileStateText));
            this.RaisePropertyChanged(nameof(CompileFailed));
            this.RaisePropertyChanged(nameof(CompileSucceeded));
        }
    }
    public bool IsCompiling => _compileState == CalcGenCompileState.Compiling;
    public bool CompileFailed => _compileState == CalcGenCompileState.Failed;
    public bool CompileSucceeded => _compileState == CalcGenCompileState.Compiled;
    public string CompileStateText => _compileState switch
    {
        CalcGenCompileState.Compiling => "Compiling… (Roslyn, a few seconds)",
        CalcGenCompileState.Compiled  => "✓ Compiled — the CalcGen calculator is rendering",
        CalcGenCompileState.Failed    => "Compile failed — see the status line",
        _                             => "Not compiled — the interpreter is rendering",
    };

    // ── What CalcGen will / won't accelerate (#1087 lowering + preview flags) ──
    private PreviewResult? _lastPreview;
    private string _calcGenReport = string.Empty;
    public string CalcGenReport { get => _calcGenReport; private set => this.RaiseAndSetIfChanged(ref _calcGenReport, value); }

    private void RefreshCalcGenReport()
    {
        if (_lastPreview is not { } p) { CalcGenReport = string.Empty; return; }
        if (!p.Ok)
        {
            string why = (p.Error ?? string.Empty).Replace("Parse error: ", string.Empty);
            CalcGenReport = $"CalcGen can't compile this: {why} The interpreter still renders it.";
            return;
        }
        string sa = p.SaFastDegree >= 2 || p.SaGenericDegree >= 2 ? "on" : "off";
        var sb = new StringBuilder("CalcGen compiles this to a native calculator. ");
        sb.Append($"Perturbation (deep zoom): {(p.SupportsPerturbation ? "on" : "off")} · ");
        sb.Append($"series approximation: {sa} · DE / normals: {(p.SupportsDe ? "on" : "off")}.");
        if (!string.IsNullOrWhiteSpace(_params.UserEquationSeed))
            sb.Append(" The z₀ seed is interpreter-only.");
        if (!string.IsNullOrWhiteSpace(_params.UserEquationBailoutCondition))
            sb.Append(" 'Bail if' is interpreter-only (#860).");
        CalcGenReport = sb.ToString();
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

    // ── Escape radius (#541) ──
    // 0 = auto (interpreter |z|²=1024 → r=32; CalcGen r=512). >0 overrides both.
    // Re-renders on change so the effect is immediate.
    public double EscapeRadius
    {
        get => _params.EscapeRadius;
        set
        {
            double clamped = value < 0.0 ? 0.0 : value;
            if (Math.Abs(_params.EscapeRadius - clamped) < 1e-12) return;
            _params.EscapeRadius = clamped;
            this.RaisePropertyChanged();
            RenderRequested?.Invoke();
        }
    }

    // ── z0 seed (#542) ──
    // Blank = z0=0 (Mandelbrot). `c` = z0=pixel (Julia). Bare DSL over c.
    // Re-renders on change; the calculator recompiles the seed lazily.
    public string? SeedExpression
    {
        get => _params.UserEquationSeed;
        set
        {
            string? next = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_params.UserEquationSeed == next) return;
            _params.UserEquationSeed = next;
            this.RaisePropertyChanged();
            RefreshCalcGenReport();
            RenderRequested?.Invoke();
        }
    }

    // ── Convergence bailout condition (#544) ──
    // Blank = escape-radius test only. Boolean DSL over z/prev/c/n/iter.
    public string? BailoutCondition
    {
        get => _params.UserEquationBailoutCondition;
        set
        {
            string? next = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_params.UserEquationBailoutCondition == next) return;
            _params.UserEquationBailoutCondition = next;
            this.RaisePropertyChanged();
            RefreshCalcGenReport();
            RenderRequested?.Invoke();
        }
    }

    // ── Bailout replaces modulus (#859) ──
    // When on (and a bailout condition is set), the condition is the SOLE escape
    // test — the modulus |z|² bailout is disabled. Enables non-modulus /
    // transcendental maps (e.g. sin(z)+c with condition abs(im(z)) > 50).
    public bool BailoutReplacesModulus
    {
        get => _params.UserEquationBailoutReplacesModulus;
        set
        {
            if (_params.UserEquationBailoutReplacesModulus == value) return;
            _params.UserEquationBailoutReplacesModulus = value;
            this.RaisePropertyChanged();
            RenderRequested?.Invoke();
        }
    }

    // ── Skip Jacobian (Phase 11b) ──
    public bool SkipJacobian
    {
        get => _params.UserEquationSkipJacobian;
        set
        {
            if (_params.UserEquationSkipJacobian == value) return;
            _params.UserEquationSkipJacobian = value;
            this.RaisePropertyChanged();
            RenderRequested?.Invoke();
        }
    }

    // ── Interior orbit colouring (#583) ──
    // Colour in-set (non-escaping) pixels by their accumulated orbit rather than
    // a flat fill. Only affects orbit-aware themes (Orbit Trap / Stripe / TIA).
    // Re-renders on change.
    public bool ColorInterior
    {
        get => _params.UserEquationColorInterior;
        set
        {
            if (_params.UserEquationColorInterior == value) return;
            _params.UserEquationColorInterior = value;
            this.RaisePropertyChanged();
            RenderRequested?.Invoke();
        }
    }

    // ── Live preview (Wave 2.4 / D-6.24) ───────────────────────────────
    //
    // After every successful parse we re-run CalcGen's
    // Preview pass — same AST + feature-flag logic the generator uses —
    // and project the result into observable properties bound by the
    // Expander in UserEquationView.axaml. When parsing fails the panel
    // freezes on the last good result so the user can still see what
    // the previous valid equation produced; PreviewError surfaces the
    // current state in red separately if desired.
    private string _previewAstText = string.Empty;
    private string _previewLatexText = string.Empty;
    private string _previewMathmlText = string.Empty;
    private string _previewDpDzText = string.Empty;
    private string _previewDpDcText = string.Empty;
    private string _previewSaText = "off";
    private string _previewPerturbText = "off";
    private string _previewDeText = "off";
    private string _previewFlagsText = string.Empty;
    private bool _hasPreview;

    public string PreviewAstText { get => _previewAstText; private set => this.RaiseAndSetIfChanged(ref _previewAstText, value); }
    /// <summary>The equation as interpreted by the DSL engine, rendered to a
    /// standard, portable LaTeX math string (#754). Copyable for paste/import
    /// into Overleaf / KaTeX / MathJax / Word. <see cref="HasLatex"/> gates the
    /// row + copy button.</summary>
    public string PreviewLatexText { get => _previewLatexText; private set { this.RaiseAndSetIfChanged(ref _previewLatexText, value); this.RaisePropertyChanged(nameof(HasLatex)); } }
    public bool HasLatex => !string.IsNullOrEmpty(_previewLatexText);

    /// <summary>The equation as interpreted by the DSL engine, as presentation
    /// MathML (#756). Word / LibreOffice import math natively as MathML, so this
    /// pastes/imports there as an editable equation. Not shown as text (verbose
    /// XML) — surfaced via the "Copy MathML" button. Shares <see cref="HasLatex"/>
    /// for visibility (both are produced from the same successful parse).</summary>
    public string PreviewMathmlText { get => _previewMathmlText; private set => this.RaiseAndSetIfChanged(ref _previewMathmlText, value); }

    // ── Typeset math image (#755) ──
    // Self-rendered from the SAME parsed AST via MathImageRenderer (SkiaSharp).
    // Null when the equation didn't parse/lay out; HasMathImage gates the row.
    private global::Avalonia.Media.Imaging.Bitmap? _mathImage;
    public global::Avalonia.Media.Imaging.Bitmap? MathImage
    {
        get => _mathImage;
        private set
        {
            var old = _mathImage;
            this.RaiseAndSetIfChanged(ref _mathImage, value);
            this.RaisePropertyChanged(nameof(HasMathImage));
            if (!ReferenceEquals(old, value)) old?.Dispose();
        }
    }
    public bool HasMathImage => _mathImage != null;
    public string PreviewDpDzText { get => _previewDpDzText; private set => this.RaiseAndSetIfChanged(ref _previewDpDzText, value); }
    public string PreviewDpDcText { get => _previewDpDcText; private set => this.RaiseAndSetIfChanged(ref _previewDpDcText, value); }
    public string PreviewSaText { get => _previewSaText; private set => this.RaiseAndSetIfChanged(ref _previewSaText, value); }
    public string PreviewPerturbText { get => _previewPerturbText; private set => this.RaiseAndSetIfChanged(ref _previewPerturbText, value); }
    public string PreviewDeText { get => _previewDeText; private set => this.RaiseAndSetIfChanged(ref _previewDeText, value); }
    public string PreviewFlagsText { get => _previewFlagsText; private set => this.RaiseAndSetIfChanged(ref _previewFlagsText, value); }
    public bool HasPreview { get => _hasPreview; private set => this.RaiseAndSetIfChanged(ref _hasPreview, value); }

    // Run CalcGen's analysis pass and project flags into the preview pane.
    // Caller passes the equation-language text (C#-style input is translated
    // through EquationPreprocessor before getting here). Silent on
    // parse failure — leaves the last valid preview frozen so transient
    // typing errors don't blank the panel.
    private void UpdatePreview(string equation)
    {
        if (string.IsNullOrWhiteSpace(equation)) return;
        var p = CalculatorGenApi.Preview(equation);
        _lastPreview = p;            // #1089 — the CalcGen report reads it
        RefreshCalcGenReport();
        if (!p.Ok) return;
        PreviewAstText = p.AstText;
        PreviewLatexText = p.LatexText;
        PreviewMathmlText = p.MathmlText;
        // #755 — typeset the interpreted equation. 0xFFDCDCDC matches the
        // preview panel's foreground; renderer returns null on any failure.
        // Best-effort: with no Avalonia platform (headless tests) the bitmap
        // can't be created; the text preview still updates.
        try { MathImage = Latex.MathImageRenderer.TryRender(equation, 0xFFDCDCDCu); }
        catch (InvalidOperationException) { MathImage = null; }
        PreviewDpDzText = p.DpDzText;
        PreviewDpDcText = p.DpDcText;
        PreviewSaText = p.SaFastDegree >= 2
            ? $"on (fast, z^{p.SaFastDegree}+c)"
            : p.SaGenericDegree >= 2
                ? $"on (generic, degree {p.SaGenericDegree})"
                : "off";
        PreviewPerturbText = p.SupportsPerturbation ? "on" : "off";
        PreviewDeText = p.SupportsDe ? "on" : "off";

        var flags = new System.Collections.Generic.List<string>();
        if (p.HasPrev)   flags.Add("prev");
        if (p.HasIter)   flags.Add("iter");
        if (p.HasConj)   flags.Add("conj");
        if (p.HasFolded) flags.Add("fold");
        if (p.HasDiv)    flags.Add("div");
        if (p.HasTrans)  flags.Add("trans");
        if (p.HasCond)   flags.Add("if");
        PreviewFlagsText = flags.Count == 0 ? "(plain polynomial)" : string.Join(", ", flags);
        HasPreview = true;
    }

    // ── Error / status ──
    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => this.RaiseAndSetIfChanged(ref _statusIsError, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> RotPlus90Command { get; }
    public ReactiveCommand<Unit, Unit> RotMinus90Command { get; }
    public ReactiveCommand<Unit, Unit> RotResetCommand { get; }
    public ReactiveCommand<Unit, Unit> GenerateViaCalcGenCommand { get; }
    public ReactiveCommand<Unit, Unit> HotLoadViaCalcGenCommand { get; }
    public ReactiveCommand<Unit, Unit> HotLoadAndPersistCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyFixCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenEditorHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCalcGenHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenEquationGuideCommand { get; }
    /// <summary>Wave 2.8 — open the equation cookbook dialog.</summary>
    public ReactiveCommand<Unit, Unit> OpenCookbookCommand { get; private set; } = null!;
    /// <summary>Wave 2.9 — open the equation morph dialog.</summary>
    public ReactiveCommand<Unit, Unit> OpenMorphCommand { get; private set; } = null!;

    /// <summary>Host opens an in-app help viewer. Args: (docId, anchor, title).
    /// docId is a filename inside the embedded Docs/ resource folder.
    /// anchor is a heading substring; null = show the whole document.</summary>
    public event Action<string, string?, string>? HelpRequested;

    public event Action? CompileRequested;
    public event Action? RenderRequested;
    public event Action? PromotionChanged;

    /// <summary>Host shows a name-entry dialog and returns the entered name (or null).</summary>
    public event Func<string, Task<string?>>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirm and returns true to proceed.</summary>
    public event Func<string, Task<bool>>? ConfirmDeleteRequested;

    /// <summary>Host shows a yes/no overwrite confirm and returns true to proceed.
    /// Fired only when Save would replace an existing equation with the same name.</summary>
    public event Func<string, Task<bool>>? ConfirmOverwriteRequested;

    /// <summary>Host shows OpenFile dialog; returns chosen path or null.</summary>
    public event Func<Task<string?>>? OpenFilePromptRequested;

    /// <summary>Host shows a simple info/error message box.</summary>
    public event Action<string, string, bool>? MessageRequested;

    /// <summary>Host compiles + loads the equation via CalcGen and swaps
    /// the result onto the render pipeline. Args: (equation, className).
    /// Completes with null on success, an error message on failure. #1089 —
    /// asynchronous: the host compiles off the UI thread so the editor can show
    /// <see cref="CalcGenCompileState.Compiling"/>.</summary>
    public event Func<string, string, Task<string?>>? HotLoadRequested;

    /// <summary>Host persists + compiles + loads the equation. Args: (equation, className).
    /// Return value: (error, savedPath). error == null → success; savedPath
    /// is the on-disk source path even on compile failure so the editor can
    /// surface where the .cs landed.</summary>
    public event Func<string, string, Task<(string? error, string? savedPath)>>? HotLoadAndPersistRequested;

    /// <summary>Wave 2.8 — host opens the cookbook picker dialog (modeless).
    /// The dialog calls back into <see cref="ApplyCookbookEntry"/> on accept;
    /// cancel is a no-op.</summary>
    public event Action? CookbookRequested;

    /// <summary>Wave 2.9 — host opens the equation-morph dialog (modeless).
    /// Dialog owns its own render loop via its own RenderAndSaveRequested
    /// delegate; this VM just kicks the window open.</summary>
    public event Action? MorphRequested;

    /// <summary>Wave 2.8 — host applies (centre X, centre Y, zoom) from the
    /// accepted cookbook entry to the active view. The editor source is set
    /// via <see cref="ApplyCookbookEntry"/>.</summary>
    public event Action<double, double, double>? CookbookCentreRequested;

    /// <summary>Wave 2.8 — host calls this on accept (from the cookbook
    /// dialog). Replaces the equation, sets the CalcGen toggle, and asks the
    /// host to re-centre.</summary>
    public void ApplyCookbookEntry(CookbookEntry entry)
    {
        // #859 — entries carrying a bailout condition (non-modulus / transcendental
        // maps) need the interpreter, which honours the condition; CalcGen codegen
        // does not yet (#860). Those load with CalcGen off and the bailout
        // settings applied; the rest load with CalcGen on.
        if (!string.IsNullOrWhiteSpace(entry.BailoutCondition))
        {
            EscapeRadius = entry.EscapeRadius;
            BailoutCondition = entry.BailoutCondition;
            BailoutReplacesModulus = entry.BailoutReplacesModulus;
        }
        _useCalcGen = string.IsNullOrWhiteSpace(entry.BailoutCondition);
        _params.UserEquationUseCalcGen = _useCalcGen;
        this.RaisePropertyChanged(nameof(UseCalcGen));
        CompileState = CalcGenCompileState.Idle;
        Source = entry.DslSource;
        CommitSourceNow();
        CookbookCentreRequested?.Invoke(entry.CenterX, entry.CenterY, entry.Zoom);
        StatusText = $"Loaded \"{entry.Name}\" from cookbook.";
        StatusIsError = false;
    }

    /// <summary>#764 — import pasted presentation MathML into the editor.
    /// On success replaces the source (which drives validate + the #754-#756
    /// preview so the user immediately sees the interpreted result) and
    /// confirms. On failure the reason goes to the status bar,
    /// same channel as parse errors. The view supplies the clipboard text.</summary>
    public void ImportMathmlFromText(string? mathml)
    {
        var r = MathmlImporter.Import(mathml);
        if (!r.Ok)
        {
            ShowStatus($"MathML import: {r.Error}", isError: true);
            return;
        }
        Source = r.Dsl;
        CommitSourceNow();              // render + preview now, don't wait for the debounce
        ShowStatus("✓ Imported MathML");
    }

    /// <summary>#765 — import pasted LaTeX (constrained subset) into the
    /// editor. Same flow as <see cref="ImportMathmlFromText"/>; best-effort — the
    /// importer reports a specific reason for anything outside the subset.</summary>
    public void ImportLatexFromText(string? latex)
    {
        var r = LatexImporter.Import(latex);
        if (!r.Ok)
        {
            ShowStatus($"LaTeX import: {r.Error}", isError: true);
            return;
        }
        Source = r.Dsl;
        CommitSourceNow();              // render + preview now, don't wait for the debounce
        ShowStatus("✓ Imported LaTeX");
    }

    /// <summary>Force an immediate render of the current source (cancel the
    /// pending debounce).</summary>
    public void TriggerCompile() => CommitSourceNow();

    /// <summary>Set a transient status-bar message (e.g. copy confirmation from
    /// the view code-behind, which owns the clipboard call).</summary>
    public void ShowStatus(string text, bool isError = false)
    {
        StatusText = text;
        StatusIsError = isError;
    }

    /// <summary>Host calls this with compile result. Empty error => success.</summary>
    public void ShowError(string? error)
    {
        bool ok = string.IsNullOrEmpty(error);
        StatusText = ok ? "✓ Compiled" : error!;
        StatusIsError = !ok;
    }

    /// <summary>Select+load a saved equation by name. No-op if absent.
    /// Restores its <see cref="UserEquationEntry.UseCalcGen"/> toggle.</summary>
    public void LoadEquationByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserEquationStore.Instance.GetByName(name);
        if (entry is null) return;

        LoadEntry(entry);
        SelectedSavedNameSilently(entry.Name);
        _debounce.Disposable = null;   // the caller renders (as before)
        Validate();
    }

    // Put a saved entry's equation + CalcGen flag into the editor without the
    // edit side effects (name reset), then render + validate once.
    private void LoadEntry(UserEquationEntry entry)
    {
        _loadingNamedEquation = true;
        try
        {
            _useCalcGen = entry.UseCalcGen;
            _params.UserEquationUseCalcGen = entry.UseCalcGen;
            this.RaisePropertyChanged(nameof(UseCalcGen));
            CompileState = CalcGenCompileState.Idle;
            Source = entry.Source;
            _params.UserEquationSource = entry.Source;
        }
        finally { _loadingNamedEquation = false; }
        _params.UserEquationName = entry.Name;
    }

    private void SelectedSavedNameSilently(string name)
    {
        _selectedSavedName = name;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
        this.RaisePropertyChanged(nameof(PromoteEnabled));
        _promote = UserEquationStore.Instance.GetByName(name)?.Promoted ?? false;
        this.RaisePropertyChanged(nameof(Promote));
    }

    private void ScheduleCommit()
    {
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(1800))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => CommitSourceNow());
    }

    /// <summary>Publish the source to the parameters, render it on the
    /// interpreter (the host drops a loaded CalcGen calculator), and validate.
    /// Cancels a pending debounce.</summary>
    private void CommitSourceNow()
    {
        _debounce.Disposable = null;
        _params.UserEquationSource = _source;
        _params.UserEquationUseCalcGen = _useCalcGen;
        // #27 Phase 0 — editor content is interactive/trusted; clear any
        // ExternalFile stamp left by a previously-viewed imported region.
        _params.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
        CompileRequested?.Invoke();
        Validate();
    }

    /// <summary>The C#-style test: text the equation language doesn't parse but
    /// <see cref="EquationPreprocessor"/> translates into text it does.</summary>
    private static bool TryConvertCSharp(string raw, out string converted, out PreprocessDiagnostic? diag)
    {
        converted = EquationPreprocessor.Preprocess(raw, out diag);
        return diag == null && !string.IsNullOrWhiteSpace(converted)
               && EquationLanguage.TryParse(converted, out _, out _);
    }

    /// <summary>#1089 — validate the one source: status line, error span with a
    /// quick fix, live preview and the CalcGen report. No render.
    ///   - Equation-language text: a parse error is positioned ("at line L, col
    ///     C") with a Did-you-mean fix; with CalcGen on, a lowering refusal is
    ///     an error too (Compile &amp; Load would fail).
    ///   - C#-style text: translated for rendering; the quick fix (Ctrl+.)
    ///     offers the converted form for the whole source. A construct with no
    ///     equation-language form is flagged at its span.</summary>
    private void Validate()
    {
        string raw = _source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusText = string.Empty;
            StatusIsError = false;
            ClearErrorSpan();
            _lastPreview = null;
            RefreshCalcGenReport();
            return;
        }

        if (EquationLanguage.TryParse(raw, out _, out string? parseError))
        {
            ClearErrorSpan();
            UpdatePreview(raw);
            if (_useCalcGen && _lastPreview is { Ok: false } refused)
            {
                StatusText = $"CalcGen: {(refused.Error ?? string.Empty).Replace("Parse error: ", string.Empty)}";
                StatusIsError = true;
            }
            else
            {
                StatusText = "✓ Equation parses";
                StatusIsError = false;
            }
            return;
        }

        if (TryConvertCSharp(raw, out string converted, out _))
        {
            UpdatePreview(converted);
            StatusText = $"C#-style equation, translated for rendering. Ctrl+. converts it to: {converted}";
            StatusIsError = false;
            SetErrorSpan(0, raw.Length, converted);
            return;
        }

        // Neither form. Prefer a C#-specific diagnostic when the text is C#-ish
        // (its span points into the original text), else the language's own.
        EquationPreprocessor.Preprocess(raw, out PreprocessDiagnostic? diag);
        if (diag != null && LooksLikeCSharp(raw))
        {
            StatusText = diag.Message;
            StatusIsError = true;
            SetErrorSpan(diag.Start, diag.Length, diag.SuggestionDsl);
            _lastPreview = null;
            RefreshCalcGenReport();
            return;
        }

        string message = parseError ?? "The equation doesn't parse.";
        StatusText = message;
        StatusIsError = true;
        _lastPreview = null;
        RefreshCalcGenReport();
        // Parse errors carry their position as "... at line L, col C." Map
        // back to a char offset so the view can select the bad token, covering
        // a whole unknown name, with its Did-you-mean as the quick fix.
        var m = Regex.Match(message, @"\bcol\s+(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int col))
        {
            int line = 1;
            var lm = Regex.Match(message, @"\bline\s+(\d+)");
            if (lm.Success) int.TryParse(lm.Groups[1].Value, out line);
            int offset = ColToOffset(raw, line, col);
            int spanLen = 1;
            string? fix = null;
            var idM = Regex.Match(message, @"Unknown (?:identifier|function) '([^']+)'");
            if (idM.Success) spanLen = idM.Groups[1].Value.Length;
            var hintM = Regex.Match(message, @"Did you mean '([^']+)'");
            if (hintM.Success) fix = hintM.Groups[1].Value;
            SetErrorSpan(offset, spanLen, fix);
        }
        else
        {
            ClearErrorSpan();
        }
    }

    private static bool LooksLikeCSharp(string s)
        => s.Contains("Complex.") || s.Contains("Math.") || s.Contains("new Complex")
           || Regex.IsMatch(s, @"\.(Real|Imaginary|Magnitude|Phase)\b");

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

        LoadEntry(entry);

        // Restore (and reset) the per-equation render settings from the entry.
        ApplyEntryRenderSettings(entry);

        _promote = entry.Promoted;
        this.RaisePropertyChanged(nameof(Promote));

        CommitSourceNow();
    }

    /// <summary>Apply a saved entry's per-equation render settings (Escape r, z0
    /// seed, convergence bailout, interior-orbit colouring) to the live params and
    /// refresh the bound controls. Setting <c>_params</c> directly + raising the
    /// property notifications (rather than going through the setters) both restores
    /// the values and RESETS them when switching to an entry that lacks them
    /// (legacy JSON ⇒ defaults), without firing a render per field — the caller's
    /// compile/validate does the single re-render.</summary>
    private void ApplyEntryRenderSettings(UserEquationEntry entry)
    {
        _params.EscapeRadius = entry.EscapeRadius;
        _params.UserEquationSeed = string.IsNullOrWhiteSpace(entry.Seed) ? null : entry.Seed;
        _params.UserEquationBailoutCondition =
            string.IsNullOrWhiteSpace(entry.BailoutCondition) ? null : entry.BailoutCondition;
        _params.UserEquationColorInterior = entry.ColorInterior;
        _params.UserEquationBailoutReplacesModulus = entry.BailoutReplacesModulus;

        this.RaisePropertyChanged(nameof(EscapeRadius));
        this.RaisePropertyChanged(nameof(SeedExpression));
        this.RaisePropertyChanged(nameof(BailoutCondition));
        this.RaisePropertyChanged(nameof(BailoutReplacesModulus));
        this.RaisePropertyChanged(nameof(ColorInterior));
    }

    private async Task OnSaveAsync()
    {
        string defaultName = _selectedSavedName ?? string.Empty;
        string? name = NamePromptRequested is { } prompt ? await prompt(defaultName) : null;
        if (string.IsNullOrWhiteSpace(name)) return;

        string trimmed = name.Trim();
        // Confirm before silently replacing an existing entry. Store match
        // is case-insensitive — mirror that here.
        if (UserEquationStore.Instance.GetByName(trimmed) is not null
            && ConfirmOverwriteRequested is { } confirm
            && !await confirm(trimmed))
            return;

        string source = _source ?? string.Empty;
        // Persist the per-equation render settings alongside the source so a Save
        // captures them and a later selection restores them.
        var entry = UserEquationStore.Instance.SaveEquation(
            trimmed, source, _useCalcGen,
            escapeRadius: _params.EscapeRadius,
            seed: _params.UserEquationSeed,
            bailoutCondition: _params.UserEquationBailoutCondition,
            colorInterior: _params.UserEquationColorInterior,
            bailoutReplacesModulus: _params.UserEquationBailoutReplacesModulus);
        if (entry is null) return;

        _params.UserEquationName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    /// <summary>Import saved equations from a JSON file — one entry object, or
    /// an array of them (what the Asset Manager's bundle holds per entry, and
    /// what a hand-assembled share file looks like). Same-name entries are
    /// skipped rather than replaced, mirroring the Sandbox importer. Entries are
    /// added whole so UseCalcGen / Promoted round-trip.</summary>
    private async Task OnImportAsync()
    {
        string? path = OpenFilePromptRequested is { } pick ? await pick() : null;
        if (string.IsNullOrWhiteSpace(path)) return;

        IReadOnlyList<string> entries;
        try
        {
            entries = AssetJsonFile.SplitEntries(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            MessageRequested?.Invoke("Import Error",
                $"Could not read the file:\n\n{ex.Message}", true);
            return;
        }

        int added = 0, skipped = 0, failed = 0;
        foreach (var entry in entries)
        {
            UserEquationEntry? parsed;
            try { parsed = JsonSerializer.Deserialize<UserEquationEntry>(entry); }
            catch { failed++; continue; }

            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Name)) { failed++; continue; }
            if (UserEquationStore.Instance.GetByName(parsed.Name) != null) { skipped++; continue; }

            UserEquationStore.Instance.Equations.Add(parsed);
            added++;
        }

        if (added > 0)
        {
            // #1088 — an export made before #1088 has no version: upgrade its text.
            UserEquationStore.Instance.UpgradeLegacyEntries(persist: false);
            UserEquationStore.Instance.Save();
            RefreshSavedList(_selectedSavedName);
            PromotionChanged?.Invoke();
        }

        if (added == 0 && skipped == 0 && failed == 0)
        {
            MessageRequested?.Invoke("Import User Equations",
                "The file contains no equations.", false);
            return;
        }

        string summary = added == 1 ? "1 equation imported" : $"{added} equations imported";
        if (skipped > 0) summary += $" ({skipped} skipped — name exists)";
        if (failed > 0)  summary += $" ({failed} unreadable)";
        MessageRequested?.Invoke("Import User Equations", summary, false);
    }

    // ── Cookbook (Wave 2.8 / D-6.23) ─────────────────────────────────────
    // Opens the picker dialog modeless. The dialog calls
    // <see cref="ApplyCookbookEntry"/> on accept; cancel is a no-op.
    private void OnOpenCookbook()
    {
        CookbookRequested?.Invoke();
    }

    // ── Morph (Wave 2.9 / D-6.25) ────────────────────────────────────────
    // Opens the morph dialog modeless. Dialog handles its own loop —
    // VM just routes the open request.
    private void OnOpenMorph()
    {
        MorphRequested?.Invoke();
    }

    // ── CalcGen pipeline ─────────────────────────────────────────────────
    //
    // Generate and Compile & Load take the one source; C#-style text is
    // translated first (TryGetCalcGenSource).
    private void OnGenerateViaCalcGen()
    {
        if (!TryGetCalcGenSource(out string equation, out string baseName)) return;

        // #541 — honour the configurable escape radius (0 = CalcGen default 512).
        double bailoutRadius = _params.EscapeRadius > 0.0 ? _params.EscapeRadius : 512.0;
        var result = CalculatorGenApi.Generate(equation, baseName, includeSelfTest: true, bailoutRadius);
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

    private async Task OnHotLoadViaCalcGenAsync()
    {
        if (!TryGetCalcGenSource(out string equation, out string baseName)) { CompileState = CalcGenCompileState.Failed; return; }

        var handler = HotLoadRequested;
        if (handler == null)
        {
            ShowError("Hot-load not wired by host.");
            CompileState = CalcGenCompileState.Failed;
            return;
        }

        _debounce.Disposable = null;
        CompileState = CalcGenCompileState.Compiling;
        ShowStatus($"Compiling {baseName}Calculator…");
        string? err;
        try { err = await handler.Invoke(equation, baseName); }
        catch (Exception ex) { err = $"Hot-load failed: {ex.GetType().Name}: {ex.Message}"; }

        if (err == null)
        {
            CompileState = CalcGenCompileState.Compiled;
            ShowStatus($"✓ Hot-loaded {baseName}Calculator");
        }
        else
        {
            CompileState = CalcGenCompileState.Failed;
            ShowError(err);
        }
    }

    private async Task OnHotLoadAndPersistAsync()
    {
        if (!TryGetCalcGenSource(out string equation, out string baseName)) { CompileState = CalcGenCompileState.Failed; return; }

        var handler = HotLoadAndPersistRequested;
        if (handler == null)
        {
            ShowError("Persist + Hot-load not wired by host.");
            CompileState = CalcGenCompileState.Failed;
            return;
        }

        _debounce.Disposable = null;
        CompileState = CalcGenCompileState.Compiling;
        ShowStatus($"Compiling {baseName}Calculator…");
        string? err, savedPath;
        try { (err, savedPath) = await handler.Invoke(equation, baseName); }
        catch (Exception ex) { (err, savedPath) = ($"Persist + Hot-load failed: {ex.GetType().Name}: {ex.Message}", null); }

        if (err == null)
        {
            CompileState = CalcGenCompileState.Compiled;
            ShowStatus(savedPath == null
                ? $"✓ Hot-loaded {baseName}Calculator (no path)"
                : $"✓ Hot-loaded + saved → {savedPath}");
        }
        else
        {
            CompileState = CalcGenCompileState.Failed;
            ShowError(savedPath == null ? err : $"{err}\n(source saved to {savedPath})");
        }
    }

    // Produce the (equation-language text, base class name) pair to hand to
    // CalcGen. C#-style text is translated first. Writes any error to the
    // status bar and returns false.
    private bool TryGetCalcGenSource(out string equation, out string baseName)
    {
        equation = string.Empty;
        baseName = string.Empty;

        string raw = (_source ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            ShowError("Equation is empty.");
            return false;
        }
        if (EquationLanguage.TryParse(raw, out _, out _)) equation = raw;
        else if (TryConvertCSharp(raw, out string converted, out _)) equation = converted;
        else
        {
            EquationPreprocessor.Preprocess(raw, out PreprocessDiagnostic? diag);
            EquationLanguage.TryParse(raw, out _, out string? parseError);
            ShowError(diag != null && LooksLikeCSharp(raw) ? diag.Message : parseError ?? "The equation doesn't parse.");
            return false;
        }

        const string fallbackBase = "UserEquation";
        baseName = string.IsNullOrWhiteSpace(_params.UserEquationName)
            ? fallbackBase
            : Regex.Replace(_params.UserEquationName, @"[^A-Za-z0-9_]", "");
        if (string.IsNullOrEmpty(baseName)) baseName = fallbackBase;
        return true;
    }

    private async Task OnDeleteAsync()
    {
        if (_selectedSavedName is null) return;
        if (ConfirmDeleteRequested is not { } confirm || !await confirm(_selectedSavedName)) return;

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
