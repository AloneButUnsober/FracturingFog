// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/WatermarkEditorViewModel.cs
//
// Avalonia VM for the Watermark Editor dialog. Same shape as
// ColorThemeEditorViewModel — host-callback events for library save and host
// message-box; live-preview push of the edited def to MainViewModel so the
// overlay updates as the user types.
//
// Store contract: UserWatermarkStore singleton (Abstractions). The host loads
// it on startup, the editor reads/writes through it directly because, unlike
// the colour-theme library which has algorithmic themes the user can't author,
// every watermark in the store is a plain JSON DTO.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class WatermarkEditorViewModel : ViewModelBase
{
    private bool _suppressChange;
    private string? _loadedSourceName;

    public WatermarkEditorViewModel(string? initialWatermarkName)
    {
        UserWatermarkStore.Instance.Load();
        WatermarkNames = new ObservableCollection<string>(UserWatermarkStore.Instance.EnumerateNames());

        Placements = new ObservableCollection<WatermarkPlacement>
        {
            WatermarkPlacement.Left, WatermarkPlacement.Top, WatermarkPlacement.Right, WatermarkPlacement.Bottom
        };
        Justifications = new ObservableCollection<WatermarkJustify>
        {
            WatermarkJustify.Left, WatermarkJustify.Center, WatermarkJustify.Right
        };

        NewBlankCommand = ReactiveCommand.Create(NewBlank);
        SaveCommand     = ReactiveCommand.CreateFromTask(SaveToLibraryAsync);
        DeleteCommand   = ReactiveCommand.CreateFromTask(DeleteFromLibraryAsync);
        RevertCommand   = ReactiveCommand.Create(Revert);
        HelpCommand     = ReactiveCommand.Create(() => HelpRequested?.Invoke(this, EventArgs.Empty));
        CloseCommand    = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        if (!string.IsNullOrEmpty(initialWatermarkName) && WatermarkNames.Contains(initialWatermarkName))
        {
            _suppressChange = true;
            SelectedWatermark = initialWatermarkName;
            _suppressChange = false;
            LoadFromLibrary(initialWatermarkName);
        }
        else
        {
            NewBlank();
        }
    }

    // ── Combo + radio-source collections ─────────────────────────────────────

    public ObservableCollection<string> WatermarkNames { get; }
    public ObservableCollection<WatermarkPlacement> Placements { get; }
    public ObservableCollection<WatermarkJustify> Justifications { get; }

    private string? _selectedWatermark;
    public string? SelectedWatermark
    {
        get => _selectedWatermark;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedWatermark, value);
            if (_suppressChange || string.IsNullOrEmpty(value)) return;
            LoadFromLibrary(value);
            PushPreview();
        }
    }

    // ── Editable fields ─────────────────────────────────────────────────────

    private string _name = "My Watermark";
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); FieldChanged(); }
    }

    private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set { this.RaiseAndSetIfChanged(ref _text, value); FieldChanged(); }
    }

    private byte _textR = 255, _textG = 255, _textB = 255;
    public Color TextColor
    {
        get => Color.FromRgb(_textR, _textG, _textB);
        set
        {
            _textR = value.R; _textG = value.G; _textB = value.B;
            this.RaisePropertyChanged(nameof(TextColor));
            this.RaisePropertyChanged(nameof(TextSwatchBrush));
            FieldChanged();
        }
    }
    public IBrush TextSwatchBrush => new SolidColorBrush(TextColor);

    private bool _useHighlight;
    public bool UseHighlight
    {
        get => _useHighlight;
        set { this.RaiseAndSetIfChanged(ref _useHighlight, value); FieldChanged(); }
    }

    private byte _highlightR = 0, _highlightG = 0, _highlightB = 0, _highlightA = 190;
    public Color HighlightColor
    {
        get => Color.FromArgb(_highlightA, _highlightR, _highlightG, _highlightB);
        set
        {
            _highlightA = value.A; _highlightR = value.R; _highlightG = value.G; _highlightB = value.B;
            this.RaisePropertyChanged(nameof(HighlightColor));
            this.RaisePropertyChanged(nameof(HighlightSwatchBrush));
            FieldChanged();
        }
    }
    public IBrush HighlightSwatchBrush => new SolidColorBrush(HighlightColor);

    private bool _useBackground;
    public bool UseBackground
    {
        get => _useBackground;
        set { this.RaiseAndSetIfChanged(ref _useBackground, value); FieldChanged(); }
    }

    private byte _bgR = 0, _bgG = 0, _bgB = 0, _bgA = 140;
    public Color BackgroundColor
    {
        get => Color.FromArgb(_bgA, _bgR, _bgG, _bgB);
        set
        {
            _bgA = value.A; _bgR = value.R; _bgG = value.G; _bgB = value.B;
            this.RaisePropertyChanged(nameof(BackgroundColor));
            this.RaisePropertyChanged(nameof(BackgroundSwatchBrush));
            FieldChanged();
        }
    }
    public IBrush BackgroundSwatchBrush => new SolidColorBrush(BackgroundColor);

    private WatermarkPlacement _placement = WatermarkPlacement.Bottom;
    public WatermarkPlacement Placement
    {
        get => _placement;
        set
        {
            this.RaiseAndSetIfChanged(ref _placement, value);
            this.RaisePropertyChanged(nameof(IsPlacementLeft));
            this.RaisePropertyChanged(nameof(IsPlacementTop));
            this.RaisePropertyChanged(nameof(IsPlacementRight));
            this.RaisePropertyChanged(nameof(IsPlacementBottom));
            FieldChanged();
        }
    }
    public bool IsPlacementLeft   { get => Placement == WatermarkPlacement.Left;   set { if (value) Placement = WatermarkPlacement.Left;   } }
    public bool IsPlacementTop    { get => Placement == WatermarkPlacement.Top;    set { if (value) Placement = WatermarkPlacement.Top;    } }
    public bool IsPlacementRight  { get => Placement == WatermarkPlacement.Right;  set { if (value) Placement = WatermarkPlacement.Right;  } }
    public bool IsPlacementBottom { get => Placement == WatermarkPlacement.Bottom; set { if (value) Placement = WatermarkPlacement.Bottom; } }

    private WatermarkJustify _justify = WatermarkJustify.Right;
    public WatermarkJustify Justify
    {
        get => _justify;
        set
        {
            this.RaiseAndSetIfChanged(ref _justify, value);
            this.RaisePropertyChanged(nameof(IsJustifyLeft));
            this.RaisePropertyChanged(nameof(IsJustifyCenter));
            this.RaisePropertyChanged(nameof(IsJustifyRight));
            FieldChanged();
        }
    }
    public bool IsJustifyLeft   { get => Justify == WatermarkJustify.Left;   set { if (value) Justify = WatermarkJustify.Left;   } }
    public bool IsJustifyCenter { get => Justify == WatermarkJustify.Center; set { if (value) Justify = WatermarkJustify.Center; } }
    public bool IsJustifyRight  { get => Justify == WatermarkJustify.Right;  set { if (value) Justify = WatermarkJustify.Right;  } }

    // ── Header / status ─────────────────────────────────────────────────────

    private bool _livePreview = true;
    public bool LivePreview
    {
        get => _livePreview;
        set => this.RaiseAndSetIfChanged(ref _livePreview, value);
    }

    private string _titleText = "Watermark Editor";
    public string TitleText { get => _titleText; private set => this.RaiseAndSetIfChanged(ref _titleText, value); }

    // ── Commands ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> NewBlankCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand     { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand   { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand   { get; }
    public ReactiveCommand<Unit, Unit> HelpCommand     { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand    { get; }

    // ── Events ──────────────────────────────────────────────────────────────

    /// <summary>Fires whenever the edited def changes (debounced via the
    /// host's existing 150 ms debounce idiom — for now we push immediately;
    /// MainViewModel calls _renderHost.RepaintWithPostFx which is itself
    /// throttled by the renderer's debounce).</summary>
    public event EventHandler<WatermarkDef>? PreviewRequested;

    /// <summary>Fires after a successful Save — host refreshes any combos
    /// (FloatingMenu watermark dropdown, Poster dialog dropdown, ServerAdmin).</summary>
    public event EventHandler<string>? WatermarkSavedToLibrary;

    /// <summary>Fires after a successful Delete with the deleted name.</summary>
    public event EventHandler<string>? WatermarkDeletedFromLibrary;

    public event EventHandler? HelpRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    // ── Build / load / save ────────────────────────────────────────────────

    public WatermarkDef BuildDef() => new WatermarkDef
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name.Trim(),
        Text = Text ?? string.Empty,
        TextColor = new RgbDef(_textR, _textG, _textB),
        HighlightColor = UseHighlight
            ? new RgbaDef(_highlightR, _highlightG, _highlightB, _highlightA)
            : null,
        BackgroundColor = UseBackground
            ? new RgbaDef(_bgR, _bgG, _bgB, _bgA)
            : null,
        Placement = Placement,
        Justify   = Justify,
    };

    private void LoadFromLibrary(string name)
    {
        var def = UserWatermarkStore.Instance.GetByName(name);
        if (def == null) return;
        _loadedSourceName = name;
        LoadDef(def);
    }

    private void LoadDef(WatermarkDef def)
    {
        _suppressChange = true;
        try
        {
            Name = def.Name ?? string.Empty;
            Text = def.Text ?? string.Empty;
            var tc = def.TextColor ?? new RgbDef(255, 255, 255);
            TextColor = Color.FromRgb(tc.R, tc.G, tc.B);

            if (def.HighlightColor != null)
            {
                UseHighlight = true;
                HighlightColor = Color.FromArgb(def.HighlightColor.A,
                    def.HighlightColor.R, def.HighlightColor.G, def.HighlightColor.B);
            }
            else
            {
                UseHighlight = false;
            }

            if (def.BackgroundColor != null)
            {
                UseBackground = true;
                BackgroundColor = Color.FromArgb(def.BackgroundColor.A,
                    def.BackgroundColor.R, def.BackgroundColor.G, def.BackgroundColor.B);
            }
            else
            {
                UseBackground = false;
            }

            Placement = def.Placement;
            Justify   = def.Justify;
        }
        finally { _suppressChange = false; }
    }

    private void FieldChanged()
    {
        if (_suppressChange) return;
        if (!LivePreview) return;
        PushPreview();
    }

    private void PushPreview() => PreviewRequested?.Invoke(this, BuildDef());

    private void NewBlank()
    {
        _loadedSourceName = null;
        _suppressChange = true;
        try
        {
            Name = "My Watermark";
            Text = string.Empty;
            TextColor = Color.FromRgb(255, 255, 255);
            UseHighlight = false;
            UseBackground = false;
            Placement = WatermarkPlacement.Bottom;
            Justify = WatermarkJustify.Right;
            TitleText = "Watermark Editor — new";
        }
        finally { _suppressChange = false; }
        PushPreview();
    }

    private void Revert()
    {
        if (string.IsNullOrEmpty(_loadedSourceName)) { NewBlank(); return; }
        LoadFromLibrary(_loadedSourceName);
        PushPreview();
    }

    private async Task SaveToLibraryAsync()
    {
        var def = BuildDef();
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs("Save Watermark", "Name cannot be empty.", MessageSeverity.Warning));
            return;
        }
        if (UserWatermarkStore.Instance.Exists(def.Name)
            && !string.Equals(_loadedSourceName, def.Name, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = new ThemeMessageEventArgs("Replace Watermark",
                $"A watermark named \"{def.Name}\" already exists.\n\nReplace it?",
                MessageSeverity.Question) { ExpectsConfirmation = true };
            await RaiseMessageAsync(confirm);
            if (!confirm.Confirmed) return;
        }

        UserWatermarkStore.Instance.SaveWatermark(def);
        WatermarkSavedToLibrary?.Invoke(this, def.Name);

        // Refresh the names list so the new entry appears, and select it.
        _suppressChange = true;
        WatermarkNames.Clear();
        foreach (var n in UserWatermarkStore.Instance.EnumerateNames()) WatermarkNames.Add(n);
        SelectedWatermark = def.Name;
        _suppressChange = false;
        _loadedSourceName = def.Name;

        await RaiseMessageAsync(new ThemeMessageEventArgs("Save Watermark", $"\"{def.Name}\" saved.", MessageSeverity.Info));
    }

    private async Task DeleteFromLibraryAsync()
    {
        if (string.IsNullOrEmpty(_loadedSourceName))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs("Delete Watermark", "No saved watermark loaded.", MessageSeverity.Warning));
            return;
        }
        var confirm = new ThemeMessageEventArgs("Delete Watermark",
            $"Delete \"{_loadedSourceName}\" from the library?",
            MessageSeverity.Question) { ExpectsConfirmation = true };
        await RaiseMessageAsync(confirm);
        if (!confirm.Confirmed) return;

        string deleted = _loadedSourceName;
        UserWatermarkStore.Instance.Remove(deleted);
        WatermarkDeletedFromLibrary?.Invoke(this, deleted);

        _suppressChange = true;
        WatermarkNames.Clear();
        foreach (var n in UserWatermarkStore.Instance.EnumerateNames()) WatermarkNames.Add(n);
        _suppressChange = false;

        NewBlank();
    }

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }
}
