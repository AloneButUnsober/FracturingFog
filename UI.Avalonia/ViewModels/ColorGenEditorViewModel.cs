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
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

        SaveCommand = ReactiveCommand.CreateFromTask(OnSaveAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(OnDeleteAsync,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        HotLoadCommand = ReactiveCommand.Create(OnHotLoad);
        GenerateCommand = ReactiveCommand.Create(OnGenerate);
    }

    public ObservableCollection<string> SavedNames { get; }

    private const string DefaultSource =
        "// ColorGen DSL — author an algorithmic colour theme.\n" +
        "// Inputs: smooth, dist, iter, maxIter, t, nx, ny, zr, zi, dzr, dzi, arg, mag, isInSet, pxScale\n" +
        "// Orbit (CPU-only): trapMin, trapCross, trapRing, trapHyperbola, trapHexagon,\n" +
        "//                   stripeAvg, tiaAvg, curvature, lyapunov, gaussian, expSmooth\n" +
        "//         trap   — primary trap; its shape is picked from the Trap shape menu\n" +
        "//                  (same 19 shapes as the Color Theme Editor). Point == trapMin.\n" +
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

    // ── #611 — selectable trap shape (same 19-shape list as the Color Theme
    // Editor). Drives the DSL `trap` input's SDF. Point (default) ⇒ trap==trapMin.
    public OrbitTrapShapeDef[] TrapShapeOptions { get; } = Enum.GetValues<OrbitTrapShapeDef>();

    private OrbitTrapShapeDef _trapShape = OrbitTrapShapeDef.Point;
    /// <summary>Trap shape the DSL <c>trap</c> input is measured against.</summary>
    public OrbitTrapShapeDef TrapShape
    {
        get => _trapShape;
        set
        {
            this.RaiseAndSetIfChanged(ref _trapShape, value);
            if (!_loadingNamedEntry) _selectedSavedName = null;
        }
    }

    // ── #615 — out-of-bounds surround colour (beyond the escape radius). Off ⇒
    // the entry carries no colour ⇒ escape gradient paints the surround. ──
    private bool _useOutOfBounds;
    public bool UseOutOfBounds
    {
        get => _useOutOfBounds;
        set
        {
            this.RaiseAndSetIfChanged(ref _useOutOfBounds, value);
            this.RaisePropertyChanged(nameof(OutOfBoundsSwatchBrush));
            if (!_loadingNamedEntry) _selectedSavedName = null;
        }
    }

    private Color _outOfBoundsColor = Colors.Black;
    public Color OutOfBoundsColor
    {
        get => _outOfBoundsColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _outOfBoundsColor, value);
            this.RaisePropertyChanged(nameof(OutOfBoundsSwatchBrush));
            if (!_loadingNamedEntry) _selectedSavedName = null;
        }
    }

    public IBrush OutOfBoundsSwatchBrush =>
        new ImmutableSolidColorBrush(UseOutOfBounds ? _outOfBoundsColor : Colors.Black);

    /// <summary>Packed "AARRGGBB" hex for the store entry, or "" when disabled.</summary>
    private string OutOfBoundsArgb() => UseOutOfBounds
        ? (((uint)_outOfBoundsColor.A << 24) | ((uint)_outOfBoundsColor.R << 16)
           | ((uint)_outOfBoundsColor.G << 8) | _outOfBoundsColor.B)
          .ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

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
    public event Func<string, Task<string?>>? NamePromptRequested;

    /// <summary>Host shows a yes/no confirmation. Returns true to proceed.</summary>
    public event Func<string, Task<bool>>? ConfirmDeleteRequested;

    /// <summary>Host compiles + loads the theme via ColorGenHotLoad and swaps
    /// the result onto the active palette. Args: (source, className, themeName,
    /// description, trapShapeName, outOfBoundsArgb). <c>outOfBoundsArgb</c> is a
    /// packed "AARRGGBB" hex string or "" for none (#615). Return value: null on
    /// success, error message on failure.</summary>
    public event Func<string, string, string, string, string, string, string?>? HotLoadRequested;

    /// <summary>Host writes the rendered C# source to Models/ColorSchemes/Generated/
    /// (or wherever it prefers). Same args as HotLoad. Return: null on success.</summary>
    public event Func<string, string, string, string, string, string?>? GenerateRequested;

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
        string err = handler.Invoke(_source, MakeClassName(_themeName), _themeName, _description, _trapShape.ToString(), OutOfBoundsArgb()) ?? "";
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
        string err = handler.Invoke(_source, MakeClassName(_themeName), _themeName, _description, _trapShape.ToString()) ?? "";
        if (string.IsNullOrEmpty(err))
        {
            StatusText = $"✓ Generated {MakeClassName(_themeName)}.cs (rebuild to pick up)";
            StatusIsError = false;
        }
        else ShowError(err);
    }

    private async Task OnSaveAsync()
    {
        string defaultName = _selectedSavedName ?? _themeName;
        string? name = NamePromptRequested is { } prompt ? await prompt(defaultName) : null;
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserColorGenStore.Instance.SaveEntry(name.Trim(), _source, _description, _trapShape.ToString(), OutOfBoundsArgb());
        if (entry == null) return;
        _themeName = entry.Name;
        this.RaisePropertyChanged(nameof(ThemeName));
        RefreshSavedList(entry.Name);
        MessageRequested?.Invoke("ColorGen", $"Saved \"{entry.Name}\".", false);
    }

    private async Task OnDeleteAsync()
    {
        if (_selectedSavedName == null) return;
        if (ConfirmDeleteRequested is not { } confirm || !await confirm(_selectedSavedName)) return;
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
            TrapShape = Enum.TryParse<OrbitTrapShapeDef>(entry.TrapShape, ignoreCase: true, out var shp)
                ? shp : OrbitTrapShapeDef.Point;

            if (!string.IsNullOrWhiteSpace(entry.OutOfBoundsColorArgb) &&   // #615
                uint.TryParse(entry.OutOfBoundsColorArgb, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out uint packed))
            {
                UseOutOfBounds = true;
                OutOfBoundsColor = Color.FromArgb(
                    (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
            }
            else
            {
                UseOutOfBounds = false;
                OutOfBoundsColor = Colors.Black;
            }
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
