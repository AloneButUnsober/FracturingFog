// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/CommandBuilderViewModel.cs
// #994 (CB2 of #64) — the Control Center "Command" section: a configuration
// front-end on the --batch CLI. Rows are generated from BatchFlagCatalog and
// bound to a CommandComposer; every edit rebuilds the command and runs it
// through the real parser, so the panel can never offer a command the CLI
// would reject without saying so.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using Avalonia.Media;

using ReactiveUI;

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Result of reading the live view as batch arguments.</summary>
public sealed record LiveCommandSeed(IReadOnlyList<string> Args, IReadOnlyList<string> Gaps);

public sealed record CommandModeOption(BatchMode Mode, string Label);

public sealed class CommandBuilderViewModel : ViewModelBase
{
    private readonly Func<int, int, LiveCommandSeed?>? _liveSeed;
    private readonly Action<string>? _copy;

    public CommandBuilderViewModel(Func<int, int, LiveCommandSeed?>? liveSeed = null, Action<string>? copy = null)
    {
        _liveSeed = liveSeed;
        _copy = copy;
        Composer = new CommandComposer();

        Groups = new ObservableCollection<CommandFlagGroupViewModel>(
            BatchFlagCatalog.All
                .Where(s => !CommandComposer.IsModeFlag(s.Name))
                .GroupBy(s => s.Group)
                .Select(g => new CommandFlagGroupViewModel(g.Key, g.Select(s => new CommandFlagRowViewModel(s, this)))));

        SeedFromLiveCommand = ReactiveCommand.Create(SeedFromLive);
        CopyCommand = ReactiveCommand.Create(Copy);
        ResetCommand = ReactiveCommand.Create(() => { Composer.Reset(); GapWarning = ""; });
        BrowseModeValueCommand = ReactiveCommand.CreateFromTask(BrowseModeValueAsync);

        Composer.Changed += Refresh;
        Refresh();
    }

    public CommandComposer Composer { get; }
    public ObservableCollection<CommandFlagGroupViewModel> Groups { get; }

    public ReactiveCommand<Unit, Unit> SeedFromLiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseModeValueCommand { get; }

    /// <summary>Host save-file picker (suggested file name → chosen path).</summary>
    public Func<string, Task<string?>>? SavePathRequested { get; set; }
    /// <summary>Host open-file picker for input paths (.exr).</summary>
    public Func<Task<string?>>? OpenPathRequested { get; set; }

    // ── Library pickers (#995) ───────────────────────────────────────────────

    private Func<BatchFlagSource, FractalType?, IReadOnlyList<string>>? _namesProvider;
    private readonly Dictionary<(BatchFlagSource, FractalType?), IReadOnlyList<string>> _names = new();

    /// <summary>Host-supplied saved names for a library source (regions, themes
    /// compatible with the fractal, slideshow presets, scenes, remote
    /// connections / presets, L-System / flame presets). The fractal is null
    /// when the command renders a region (type unknown here).</summary>
    public Func<BatchFlagSource, FractalType?, IReadOnlyList<string>>? NamesProvider
    {
        get => _namesProvider;
        set { _namesProvider = value; _names.Clear(); Refresh(); }
    }

    /// <summary>The fractal a library list is filtered by. Regions filter only
    /// on an explicit --fractal (the implicit Mandelbrot default must not hide
    /// every other region); everything else follows the command's fractal.</summary>
    private FractalType? FilterFractal(BatchFlagSource source)
        => source == BatchFlagSource.Region && !Composer.IsSelected(BatchFlags.Fractal) ? null : Composer.Fractal;

    /// <summary>Saved names for <paramref name="source"/>, cached per fractal
    /// until the view is re-seeded. Section-header rows ("— X —") and blanks
    /// are dropped; a throwing provider yields no names.</summary>
    internal IReadOnlyList<string> NamesFor(BatchFlagSource source)
    {
        if (_namesProvider == null || source == BatchFlagSource.None) return Array.Empty<string>();
        var fractal = FilterFractal(source);
        if (!_names.TryGetValue((source, fractal), out var list))
        {
            try
            {
                list = (_namesProvider(source, fractal) ?? Array.Empty<string>())
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('—'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { list = Array.Empty<string>(); }
            _names[(source, fractal)] = list;
        }
        return list;
    }

    /// <summary>Human name of a library, for "not found" notes.</summary>
    internal string LibraryLabel(BatchFlagSource source) => source switch
    {
        BatchFlagSource.Region           => FilterFractal(source) is FractalType rf ? $"saved {rf} regions" : "saved regions",
        BatchFlagSource.Theme            => FilterFractal(source) is FractalType f ? $"themes compatible with {f}" : "saved themes",
        BatchFlagSource.SlideshowConfig  => "slideshow presets",
        BatchFlagSource.Scene            => "saved scenes",
        BatchFlagSource.RemoteConnection => "saved connections",
        BatchFlagSource.RemotePreset     => "saved render presets",
        BatchFlagSource.LSystemPreset    => "L-System presets",
        BatchFlagSource.FlamePreset      => "flame presets",
        BatchFlagSource.ParamKey         => "--param keys",
        BatchFlagSource.LightingPreset   => "saved Lighting & FX presets",
        BatchFlagSource.Animation        => "saved animations",
        _                                => "saved names",
    };

    /// <summary>A note when <paramref name="value"/> is not one of the saved
    /// names (empty when it is, or when there is no list to check against).</summary>
    internal string NotFoundNote(BatchFlagSource source, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var names = NamesFor(source);
        if (names.Count == 0 || names.Contains(value, StringComparer.OrdinalIgnoreCase)) return "";
        return $"'{value}' is not in the {LibraryLabel(source)}";
    }

    // ── Mode ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<CommandModeOption> Modes { get; } = new[]
    {
        new CommandModeOption(BatchMode.Image,     "Image"),
        new CommandModeOption(BatchMode.Video,     "Video"),
        new CommandModeOption(BatchMode.Slideshow, "Slideshow"),
        new CommandModeOption(BatchMode.Scene,     "Scene"),
        new CommandModeOption(BatchMode.Regrade,   "Regrade EXR"),
        new CommandModeOption(BatchMode.Relight,   "Relight EXR"),
    };

    public CommandModeOption SelectedMode
    {
        get => Modes.First(m => m.Mode == Composer.Mode);
        set { if (value != null) Composer.Mode = value.Mode; }
    }

    public bool IsRemote
    {
        get => Composer.Remote;
        set => Composer.Remote = value;
    }

    /// <summary>The flag carrying the mode's own value (slideshow / scene name,
    /// input .exr), or null for image / video.</summary>
    private string? ModeValueFlag => Composer.Mode switch
    {
        BatchMode.Slideshow => BatchFlags.Slideshow,
        BatchMode.Scene     => BatchFlags.Scene,
        BatchMode.Regrade   => BatchFlags.RegradeExr,
        BatchMode.Relight   => BatchFlags.RelightFrom,
        _                   => null,
    };

    public bool HasModeValue => ModeValueFlag != null && !Composer.Remote;

    public string ModeValueLabel => Composer.Mode switch
    {
        BatchMode.Slideshow => "Slideshow preset (blank = active preset)",
        BatchMode.Scene     => "Scene name",
        BatchMode.Regrade   => "Input .exr to regrade",
        BatchMode.Relight   => "Input AOV .exr to relight",
        _                   => "",
    };

    public bool ModeValueIsPath => Composer.Mode is BatchMode.Regrade or BatchMode.Relight;

    private BatchFlagSource ModeValueSource => Composer.Mode switch
    {
        BatchMode.Slideshow => BatchFlagSource.SlideshowConfig,
        BatchMode.Scene     => BatchFlagSource.Scene,
        _                   => BatchFlagSource.None,
    };

    /// <summary>Saved slideshow presets / scenes for the mode's name box.</summary>
    public IReadOnlyList<string> ModeValueSuggestions => NamesFor(ModeValueSource);
    public bool ModeValueIsName => HasModeValue && !ModeValueIsPath;

    public string ModeValue
    {
        get => ModeValueFlag is string f ? Composer.ValueOf(f) ?? "" : "";
        set
        {
            if (ModeValueFlag is not string f) return;
            if (string.IsNullOrWhiteSpace(value)) Composer.Clear(f);
            else Composer.Set(f, value);   // untrimmed: a trim would eat spaces mid-typing
        }
    }

    private async Task BrowseModeValueAsync()
    {
        if (OpenPathRequested is not { } pick) return;
        var path = await pick();
        if (!string.IsNullOrWhiteSpace(path)) ModeValue = path!;
    }

    // ── Command + validation ─────────────────────────────────────────────────

    private bool _useFullExePath;
    /// <summary>Lead with the full path to the running executable instead of the
    /// bare "FracturingFog" name.</summary>
    public bool UseFullExePath
    {
        get => _useFullExePath;
        set { this.RaiseAndSetIfChanged(ref _useFullExePath, value); Refresh(); }
    }

    private string ExecutableName()
    {
        if (!_useFullExePath) return "FracturingFog";
        string? path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? "FracturingFog" : path;
    }

    private string _commandText = "";
    public string CommandText { get => _commandText; private set => this.RaiseAndSetIfChanged(ref _commandText, value); }

    private string _validationMessage = "";
    /// <summary>The parser's verdict on the current command.</summary>
    public string ValidationMessage { get => _validationMessage; private set => this.RaiseAndSetIfChanged(ref _validationMessage, value); }

    private bool _isValid;
    public bool IsValid
    {
        get => _isValid;
        private set { this.RaiseAndSetIfChanged(ref _isValid, value); this.RaisePropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !_isValid;

    private string _hint = "";
    /// <summary>Non-fatal notes: placeholder output path, parked flags.</summary>
    public string Hint { get => _hint; private set { this.RaiseAndSetIfChanged(ref _hint, value); this.RaisePropertyChanged(nameof(HasHint)); } }
    public bool HasHint => _hint.Length > 0;

    private string _gapWarning = "";
    /// <summary>Live fx the last seed could not express (#362).</summary>
    public string GapWarning
    {
        get => _gapWarning;
        private set { this.RaiseAndSetIfChanged(ref _gapWarning, value); this.RaisePropertyChanged(nameof(HasGaps)); }
    }
    public bool HasGaps => _gapWarning.Length > 0;

    private void Refresh()
    {
        CommandText = Composer.Command(ExecutableName());
        var error = Composer.Validate();
        IsValid = error == null;
        ValidationMessage = error == null ? "Valid command — the batch parser accepts it." : error;

        var notes = new List<string>();
        if (!Composer.Remote && Composer.HasPlaceholderOutput)
            notes.Add("Set an output path (Output → --out) before running.");
        if (HasModeValue && NotFoundNote(ModeValueSource, ModeValue) is { Length: > 0 } missing)
            notes.Add(char.ToUpperInvariant(missing[0]) + missing[1..] + " — the batch will stop with 'not found'.");
        var parked = Composer.Parked.Select(s => s.Name).ToList();
        if (parked.Count > 0)
            notes.Add("Kept but not used in this mode/fractal: " + string.Join(", ", parked));
        Hint = string.Join(" ", notes);

        this.RaisePropertyChanged(nameof(SelectedMode));
        this.RaisePropertyChanged(nameof(IsRemote));
        this.RaisePropertyChanged(nameof(HasModeValue));
        this.RaisePropertyChanged(nameof(ModeValueLabel));
        this.RaisePropertyChanged(nameof(ModeValueIsPath));
        this.RaisePropertyChanged(nameof(ModeValue));
        this.RaisePropertyChanged(nameof(ModeValueSuggestions));
        this.RaisePropertyChanged(nameof(ModeValueIsName));
        foreach (var g in Groups) g.Refresh();
    }

    private void SeedFromLive()
    {
        int w = int.TryParse(Composer.ValueOf(BatchFlags.Width), out var cw) ? cw : BatchDefaults.Width;
        int h = int.TryParse(Composer.ValueOf(BatchFlags.Height), out var ch) ? ch : BatchDefaults.Height;
        var seed = _liveSeed?.Invoke(w, h);
        if (seed == null) return;
        _names.Clear();   // the libraries may have changed since the last look
        Composer.SeedLook(seed.Args);
        GapWarning = seed.Gaps.Count > 0
            ? "Not represented (rendered output will differ): " + string.Join("; ", seed.Gaps)
            : "";
    }

    private void Copy()
    {
        if (!string.IsNullOrEmpty(CommandText)) _copy?.Invoke(CommandText);
    }

    internal async Task BrowseAsync(CommandFlagRowViewModel row)
    {
        string? path;
        if (row.Spec.PathIsInput)
        {
            if (OpenPathRequested is not { } open) return;
            path = await open();
        }
        else
        {
            if (SavePathRequested is not { } save) return;
            path = await save(SuggestedOutputName());
        }
        if (!string.IsNullOrWhiteSpace(path)) row.Value = path!;
    }

    private string SuggestedOutputName()
    {
        if (Composer.Mode is BatchMode.Video or BatchMode.Slideshow or BatchMode.Scene) return "render.mp4";
        return Composer.IsSelected(BatchFlags.AovExr) ? "render.exr" : "render.png";
    }
}

/// <summary>One catalog group (Source, Output, Relief, ...) in the panel.</summary>
public sealed class CommandFlagGroupViewModel : ViewModelBase
{
    public CommandFlagGroupViewModel(BatchFlagGroup group, IEnumerable<CommandFlagRowViewModel> rows)
    {
        Group = group;
        Title = BatchFlagCatalog.GroupTitle(group);
        Rows = rows.ToList();

        // Lights (#996): a sub-heading above each light's first row, so the 24
        // rows read as three lights rather than one flat list.
        foreach (var first in Rows.Where(r => r.Spec.LightNumber > 0).GroupBy(r => r.Spec.LightNumber).Select(g => g.First()))
            first.SectionTitle = first.Spec.LightNumber switch
            {
                1 => "Light 1 — key (on by default)",
                2 => "Light 2 — fill (off by default)",
                _ => "Light 3 — rim (off by default)",
            };
        _isExpanded = group is BatchFlagGroup.Source or BatchFlagGroup.Output;
    }

    public BatchFlagGroup Group { get; }
    public string Title { get; }
    public IReadOnlyList<CommandFlagRowViewModel> Rows { get; }

    public bool IsVisible => Rows.Any(r => r.IsVisible);

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    /// <summary>"3 set" style badge for a collapsed group.</summary>
    public string Summary
    {
        get
        {
            int n = Rows.Count(r => r.IsSelected && r.IsVisible);
            return n == 0 ? "" : n + " set";
        }
    }

    internal void Refresh()
    {
        foreach (var r in Rows) r.Refresh();
        this.RaisePropertyChanged(nameof(IsVisible));
        this.RaisePropertyChanged(nameof(Summary));
    }
}

/// <summary>One flag row: include checkbox + value editor + relation status.</summary>
public sealed class CommandFlagRowViewModel : ViewModelBase
{
    private readonly CommandBuilderViewModel _owner;
    private string _draft;

    public CommandFlagRowViewModel(BatchFlagSpec spec, CommandBuilderViewModel owner)
    {
        Spec = spec;
        _owner = owner;
        _draft = spec.Default ?? (spec.Kind == BatchFlagKind.Choice && spec.Choices.Length > 0 ? spec.Choices[0] : "");
        Description = BatchFlagCatalog.Describe(spec);
        BrowseCommand = ReactiveCommand.CreateFromTask(() => _owner.BrowseAsync(this));
    }

    public BatchFlagSpec Spec { get; }
    private CommandComposer Composer => _owner.Composer;

    public string Name => Spec.Name;
    public string Description { get; }
    /// <summary>Watermark for an empty text box: the default the batch uses
    /// when the flag is left out, else the value shape (N, F, PATH, ...).</summary>
    public string Placeholder => Spec.Default != null ? "default " + Spec.Default
        : Spec.Repeatable && Spec.Example != null ? Spec.Example + "   (one per line)"
        : Spec.ValueHint?.Trim('"') ?? "";

    public bool IsSwitch => Spec.Kind == BatchFlagKind.Switch;
    public bool IsChoice => Spec.Kind == BatchFlagKind.Choice;
    public bool IsTextEntry => !IsSwitch && !IsChoice;
    /// <summary>A text value drawn from a saved library: an editable combo.</summary>
    public bool HasSuggestions => IsTextEntry && Spec.Source != BatchFlagSource.None && !Spec.Repeatable;
    /// <summary>A repeatable flag (--param): one value per line.</summary>
    public bool IsMultiLine => Spec.Repeatable;
    public bool IsPlainText => IsTextEntry && !HasSuggestions && !IsMultiLine;
    public IReadOnlyList<string> Suggestions => HasSuggestions ? _owner.NamesFor(Spec.Source) : Array.Empty<string>();
    public bool HasBrowse => Spec.Kind == BatchFlagKind.Path;
    public IReadOnlyList<string> Choices => Spec.Choices;

    public ReactiveCommand<Unit, Unit> BrowseCommand { get; }

    /// <summary>Sub-heading shown above this row (the first row of each light).</summary>
    public string SectionTitle { get; internal set; } = "";
    public bool HasSectionTitle => SectionTitle.Length > 0;

    public bool IsColor => Spec.Kind == BatchFlagKind.Color;

    /// <summary>Preview of a colour value (the default when unset / unparseable).</summary>
    public Color SwatchColor
    {
        get
        {
            string v = Composer.ValueOf(Spec.Name) ?? Spec.Default ?? "#FFFFFF";
            if (!BatchOptions.TryParseHexColor(v, out uint c) && !BatchOptions.TryParseHexColor(Spec.Default ?? "#FFFFFF", out c))
                c = 0xFFFFFFFFu;
            return Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        }
    }

    /// <summary>A problem with the value itself, checked with the parser's own
    /// grammar (number, integer, "x,y,z", hex colour) and the catalog range —
    /// including ranges the parser does not enforce. Empty when fine.</summary>
    public string ValueNote
    {
        get
        {
            if (!IsSelected || IsSwitch || IsChoice) return "";
            string v = Composer.ValueOf(Spec.Name) ?? "";
            if (Spec.Repeatable) return RepeatableNote(v);
            switch (Spec.Kind)
            {
                case BatchFlagKind.Int:
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) return "expects a whole number";
                    return OutOfRange(iv) ? "outside " + BatchFlagCatalog.RangeText(Spec) : "";
                case BatchFlagKind.Double:
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) return "expects a number";
                    return OutOfRange(dv) ? "outside " + BatchFlagCatalog.RangeText(Spec) : "";
                case BatchFlagKind.Vector:
                    if (!BatchOptions.TryParseCsvDoubles(v, Spec.Arity, out var parts))
                        return $"expects {Spec.Arity} comma-separated numbers ({Placeholder})";
                    return parts.Any(OutOfRange) ? "each value must be within " + BatchFlagCatalog.RangeText(Spec) : "";
                case BatchFlagKind.Color:
                    return BatchOptions.TryParseHexColor(v, out _) ? "" : "expects a hex colour #RRGGBB or #AARRGGBB";
                default:
                    return string.IsNullOrWhiteSpace(v) ? "needs a value" : "";
            }
        }
    }

    /// <summary>--param lines: each KEY=VALUE, and a known key when the host
    /// supplied the key list.</summary>
    private string RepeatableNote(string stored)
    {
        var keys = _owner.NamesFor(Spec.Source);
        int n = 0;
        foreach (var line in CommandComposer.ValuesOf(Spec, stored))
        {
            n++;
            int eq = line.IndexOf('=');
            if (eq <= 0) return $"line {n}: expects KEY=VALUE (e.g. {Spec.Example})";
            string key = line[..eq].Trim();
            if (keys.Count > 0 && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return $"line {n}: unknown key '{key}'";
        }
        return "";
    }

    private bool OutOfRange(double v)
        => (Spec.Min is double lo && (v < lo || (Spec.MinExclusive && v == lo)))
        || (Spec.Max is double hi && v > hi);

    /// <summary>Include this flag in the command.</summary>
    public bool IsSelected
    {
        get => Composer.IsSelected(Spec.Name);
        set
        {
            if (value == IsSelected) return;
            if (value) Composer.Set(Spec.Name, IsSwitch ? null : _draft);
            else Composer.Clear(Spec.Name);
        }
    }

    /// <summary>The flag's value. Editing it selects the flag; clearing a free
    /// text value deselects it. An unselected text row shows empty (its default
    /// is the watermark) so it never looks set; a choice row shows its draft.</summary>
    public string Value
    {
        get => Composer.ValueOf(Spec.Name) ?? (IsChoice ? _draft : "");
        set
        {
            if (IsSwitch) return;
            // A header/blank pick from a combo is ignored.
            if (IsChoice && (value == null || !Spec.Choices.Contains(value))) return;
            _draft = value ?? "";
            if (IsTextEntry && string.IsNullOrWhiteSpace(_draft)) Composer.Clear(Spec.Name);
            else Composer.Set(Spec.Name, _draft);   // untrimmed: a trim would eat spaces mid-typing
        }
    }

    private CommandFlagState _state = new(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsVisible => _state.Applicable;
    /// <summary>Editable unless a flag in effect conflicts with it.</summary>
    public bool IsEditable => !_state.IsBlocked || IsSelected;

    /// <summary>Relation note: implied by / conflicts with / needs.</summary>
    public string Status
    {
        get
        {
            if (ValueNote is { Length: > 0 } vn) return vn;
            if (_state.IsBlocked) return "conflicts with " + string.Join(", ", _state.BlockedBy);
            if (_state.Missing.Count > 0) return "needs " + string.Join(", ", _state.Missing);
            if (HasSuggestions && IsSelected && _owner.NotFoundNote(Spec.Source, Composer.ValueOf(Spec.Name)) is { Length: > 0 } nf)
                return nf;
            if (_state.IsImplied && !IsSelected) return "implied by " + string.Join(", ", _state.ImpliedBy);
            return "";
        }
    }

    public bool HasStatus => Status.Length > 0;
    /// <summary>Conflicts / unmet needs are warnings (yellow); implications are info.</summary>
    public bool StatusIsWarning => ValueNote.Length > 0 || _state.IsBlocked || _state.Missing.Count > 0
        || (HasSuggestions && IsSelected && _owner.NotFoundNote(Spec.Source, Composer.ValueOf(Spec.Name)).Length > 0);

    internal void Refresh()
    {
        _state = Composer.StateOf(Spec);
        this.RaisePropertyChanged(nameof(IsSelected));
        this.RaisePropertyChanged(nameof(Value));
        this.RaisePropertyChanged(nameof(Suggestions));
        this.RaisePropertyChanged(nameof(SwatchColor));
        this.RaisePropertyChanged(nameof(IsVisible));
        this.RaisePropertyChanged(nameof(IsEditable));
        this.RaisePropertyChanged(nameof(Status));
        this.RaisePropertyChanged(nameof(HasStatus));
        this.RaisePropertyChanged(nameof(StatusIsWarning));
    }
}
