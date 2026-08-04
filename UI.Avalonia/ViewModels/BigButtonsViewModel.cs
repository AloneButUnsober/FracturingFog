// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.ComponentModel;
using System.Reactive;

using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View-model for the standalone <c>BigButtonsView</c> — a child-friendly dialog
/// with three oversized buttons for young explorers. It owns no state of its own:
/// each button delegates to the shell, which drives the same machinery the
/// grown-up UI uses.
/// <list type="bullet">
///   <item><b>Color</b> — full-randomness recolour (theme + Kind), no save.</item>
///   <item><b>Place</b> — jump to a random slideshow region.</item>
///   <item><b>Show / Stop</b> — toggle the slideshow; the label follows the run
///   state so it reads "Stop" while running and "Show" when idle.</item>
/// </list>
/// The dialog is host-owned (opened from <c>AvaloniaShellBootstrap</c> on
/// <see cref="ShellViewModel.BigButtonsRequested"/>).
/// </summary>
public sealed class BigButtonsViewModel : ViewModelBase, IDisposable
{
    private readonly ShellViewModel _shell;

    public BigButtonsViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        ColorCommand    = ReactiveCommand.Create(_shell.RandomizeKidColors);
        PlaceCommand    = ReactiveCommand.Create(_shell.JumpToRandomRegion);
        ShowStopCommand = ReactiveCommand.Create(OnShowStop);

        // The slideshow can also stop itself (end of a non-looping run), so mirror
        // the shell's run-state onto the label rather than only toggling on click.
        _shell.PropertyChanged += OnShellPropertyChanged;
        UpdateShowStopLabel();
    }

    /// <summary>"Color" — randomise the whole colour theme, Kind included.</summary>
    public ReactiveCommand<Unit, Unit> ColorCommand { get; }

    /// <summary>"Place" — jump to a random curated slideshow region.</summary>
    public ReactiveCommand<Unit, Unit> PlaceCommand { get; }

    /// <summary>"Show / Stop" — toggle the slideshow.</summary>
    public ReactiveCommand<Unit, Unit> ShowStopCommand { get; }

    private string _showStopLabel = "Show";
    /// <summary>"Show" when the slideshow is idle, "Stop" while it runs.</summary>
    public string ShowStopLabel
    {
        get => _showStopLabel;
        private set => this.RaiseAndSetIfChanged(ref _showStopLabel, value);
    }

    private void OnShowStop()
    {
        _shell.ToggleKidSlideshow();
        // Reflect the new state immediately; the shell also raises
        // IsSlideshowRunning, which keeps the label honest if the run ends later.
        UpdateShowStopLabel();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSlideshowRunning))
            UpdateShowStopLabel();
    }

    private void UpdateShowStopLabel()
        => ShowStopLabel = _shell.IsSlideshowRunning ? "Stop" : "Show";

    public void Dispose() => _shell.PropertyChanged -= OnShellPropertyChanged;
}
