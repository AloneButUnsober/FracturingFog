// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Reactive;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Controls.SlideshowVcrControl"/>. Pure event
/// surface: host wires Play/Pause/Stop/Skip handlers, the VM owns only
/// button-enabled flags + Play/Pause label + the Hide-collapse state.
/// </summary>
public sealed class SlideshowVcrViewModel : ViewModelBase
{
    public SlideshowVcrViewModel()
    {
        PlayPauseCommand   = ReactiveCommand.Create(() => PlayPauseClicked?.Invoke(this, EventArgs.Empty));
        StopCommand        = ReactiveCommand.Create(() => StopClicked?.Invoke(this, EventArgs.Empty));
        SkipRegionCommand  = ReactiveCommand.Create(() => SkipRegionClicked?.Invoke(this, EventArgs.Empty));
        SkipThemeCommand   = ReactiveCommand.Create(() => SkipThemeClicked?.Invoke(this, EventArgs.Empty));
    }

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCollapsed, value);
            this.RaisePropertyChanged(nameof(IsExpanded));
            this.RaisePropertyChanged(nameof(HideLabel));
            CollapsedChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool IsExpanded => !_isCollapsed;
    public string HideLabel => _isCollapsed ? "Show" : "Hide";

    private string _playPauseLabel = "⏸ Pause";
    public string PlayPauseLabel { get => _playPauseLabel; private set => this.RaiseAndSetIfChanged(ref _playPauseLabel, value); }

    private bool _playPauseEnabled = true;
    public bool PlayPauseEnabled { get => _playPauseEnabled; set => this.RaiseAndSetIfChanged(ref _playPauseEnabled, value); }

    private bool _skipRegionEnabled = true;
    public bool SkipRegionEnabled { get => _skipRegionEnabled; set => this.RaiseAndSetIfChanged(ref _skipRegionEnabled, value); }

    private bool _skipThemeEnabled = true;
    public bool SkipThemeEnabled { get => _skipThemeEnabled; set => this.RaiseAndSetIfChanged(ref _skipThemeEnabled, value); }

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipRegionCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipThemeCommand { get; }

    public event EventHandler? PlayPauseClicked;
    public event EventHandler? StopClicked;
    public event EventHandler? SkipRegionClicked;
    public event EventHandler? SkipThemeClicked;
    public event EventHandler? CollapsedChanged;

    /// <summary>Toggles the Play/Pause label. Mirrors <c>SetPaused</c> in the WinForms panel.</summary>
    public void SetPaused(bool paused) => PlayPauseLabel = paused ? "▶ Play" : "⏸ Pause";
}
