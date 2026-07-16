// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

// Wave 2.8 — Cookbook dialog VM. Lists curated CookbookEntry rows, lets the
// user pick one, fires Accepted with the entry on OK / double-click. View
// closes itself by listening for CloseRequested.
public sealed class CookbookViewModel : ViewModelBase
{
    public ObservableCollection<CookbookEntry> Entries { get; }

    private CookbookEntry? _selected;
    public CookbookEntry? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectedName));
            this.RaisePropertyChanged(nameof(SelectedDescription));
            this.RaisePropertyChanged(nameof(SelectedSourceDisplay));
            this.RaisePropertyChanged(nameof(SelectedCentreDisplay));
        }
    }

    public bool HasSelection => _selected != null;

    public string SelectedName => _selected?.Name ?? string.Empty;

    public string SelectedDescription => _selected?.Description ?? string.Empty;

    public string SelectedSourceDisplay =>
        _selected is { } e ? e.DslSource : string.Empty;

    public string SelectedCentreDisplay =>
        _selected is { } e
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "centre ({0:G6}, {1:G6})   zoom {2:G6}", e.CenterX, e.CenterY, e.Zoom)
            : string.Empty;

    public ReactiveCommand<Unit, Unit> AcceptCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public event Action<CookbookEntry>? Accepted;
    public event Action? CloseRequested;

    public CookbookViewModel()
    {
        Entries = new ObservableCollection<CookbookEntry>(EquationCookbook.Entries);
        Selected = Entries.Count > 0 ? Entries[0] : null;

        AcceptCommand = ReactiveCommand.Create(OnAccept,
            this.WhenAnyValue(x => x.Selected).Select(s => s != null));
        CancelCommand = ReactiveCommand.Create(OnCancel);
    }

    private void OnAccept()
    {
        if (_selected is { } e) Accepted?.Invoke(e);
        CloseRequested?.Invoke();
    }

    private void OnCancel() => CloseRequested?.Invoke();
}
