// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Script.cs
//
// #1698 (scripted scenes SS4, #1557) — the Script group: play / pause, loop, a
// time slider with step markers, an s / D read-out and the list of script members
// with the ones running now highlighted (click one to jump to its start). All of
// it drives one param, MathPlaneScriptTime; the scene itself is drawn by the
// calculator as a pure function of that time (MathPlaneScript.Evaluate), so a
// scrubbed frame is the frame a batch still or video renders.
// Design: Docs/Technical/MathPlane-ScriptedScenes-Plan.md §4.

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using Avalonia.Collections;
using Avalonia.Threading;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>#1698 — one script member in the Script list.</summary>
public sealed class MathPlaneScriptRow : ReactiveObject
{
    public MathPlaneScriptRow(string text, double start, double end, bool with)
    {
        Text = text; Start = start; End = end; IsWith = with;
    }

    public string Text { get; }
    public double Start { get; }
    public double End { get; }
    public bool IsWith { get; }

    /// <summary>"0.00 - 2.00 s".</summary>
    public string Span => string.Create(CultureInfo.InvariantCulture, $"{Start:0.00} - {End:0.00} s");

    private bool _isCurrent;
    /// <summary>Running at the current script time.</summary>
    public bool IsCurrent { get => _isCurrent; set => this.RaiseAndSetIfChanged(ref _isCurrent, value); }
}

public sealed partial class MathPlaneSectionViewModel
{
    private DispatcherTimer? _scriptTimer;
    private readonly Stopwatch _scriptClock = new();
    private double _scriptPlayFrom;
    private bool _scriptPlaying;

    /// <summary>The script's members, in order.</summary>
    public ObservableCollection<MathPlaneScriptRow> ScriptRows { get; } = new();

    /// <summary>Step boundaries, drawn as ticks on the time slider.</summary>
    public AvaloniaList<double> ScriptMarkers { get; } = new();

    public ReactiveCommand<Unit, Unit> PlayScriptCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RestartScriptCommand { get; private set; } = null!;
    /// <summary>#1699 — load the Command panel with a headless video of this script.</summary>
    public ReactiveCommand<Unit, Unit> ScriptVideoCommand { get; private set; } = null!;

    /// <summary>#1699 — raised by <see cref="ScriptVideoCommand"/>; the Control Center seeds
    /// its Command builder with the live command plus <c>--mode video --video-motion script</c>.</summary>
    public event Action? ScriptVideoRequested;

    private void InitScript()
    {
        PlayScriptCommand = ReactiveCommand.Create(() => { if (_scriptPlaying) PauseScript(); else PlayScript(); });
        RestartScriptCommand = ReactiveCommand.Create(() => { ScriptTime = 0; });
        ScriptVideoCommand = ReactiveCommand.Create(() => { PauseScript(); ScriptVideoRequested?.Invoke(); });
    }

    private double _scriptDuration;
    /// <summary>The script's length in seconds (0 without steps).</summary>
    public double ScriptDuration
    {
        get => _scriptDuration;
        private set => this.RaiseAndSetIfChanged(ref _scriptDuration, value);
    }

    /// <summary>The scene has a script (valid steps): the Script group shows.</summary>
    public bool HasScript => ScriptRows.Count > 0;

    /// <summary>The moment drawn (MathPlaneScriptTime), clamped to 0..duration.</summary>
    public double ScriptTime
    {
        get => P.MathPlaneScriptTime;
        set
        {
            double s = double.IsFinite(value) ? Math.Clamp(value, 0, ScriptDuration) : 0;
            if (s == P.MathPlaneScriptTime) return;
            P.MathPlaneScriptTime = s;
            RaiseScriptTime();
            _trigger();
        }
    }

    /// <summary>"1.25 / 6.50 s".</summary>
    public string ScriptReadout => string.Create(CultureInfo.InvariantCulture, $"{ScriptTime:0.00} / {ScriptDuration:0.00} s");

    private bool _scriptLoop;
    /// <summary>Play starts over at the end instead of stopping.</summary>
    public bool ScriptLoop { get => _scriptLoop; set => this.RaiseAndSetIfChanged(ref _scriptLoop, value); }

    public bool IsScriptPlaying => _scriptPlaying;
    public string ScriptPlayLabel => _scriptPlaying ? "Pause" : "Play";

    /// <summary>Clicking a row jumps to its start.</summary>
    public MathPlaneScriptRow? SelectedScriptRow
    {
        get => null;
        set { if (value != null) { PauseScript(); ScriptTime = value.Start; } this.RaisePropertyChanged(); }
    }

    /// <summary>Start playing from the current time (from 0 when at the end).</summary>
    /// <param name="startTimer">False in tests: drive it with <see cref="AdvanceScript"/>.</param>
    public void PlayScript(bool startTimer = true)
    {
        if (!HasScript || ScriptDuration <= 0) return;
        if (ScriptTime >= ScriptDuration) ScriptTime = 0;
        _scriptPlayFrom = ScriptTime;
        _scriptPlaying = true;
        _scriptClock.Restart();
        if (startTimer)
        {
            _scriptTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
                                                 (_, _) => AdvanceScript(_scriptClock.Elapsed.TotalSeconds));
            _scriptTimer.Start();
        }
        RaisePlaying();
    }

    public void PauseScript()
    {
        _scriptTimer?.Stop();
        _scriptClock.Stop();
        if (!_scriptPlaying) return;
        _scriptPlaying = false;
        RaisePlaying();
    }

    /// <summary>One playback tick, <paramref name="elapsedSeconds"/> after Play was pressed.
    /// Stops at the end, or starts over when <see cref="ScriptLoop"/> is on.</summary>
    public void AdvanceScript(double elapsedSeconds)
    {
        if (!_scriptPlaying) return;
        double d = ScriptDuration;
        if (d <= 0) { PauseScript(); return; }
        double s = _scriptPlayFrom + Math.Max(0, elapsedSeconds);
        if (s < d) { ScriptTime = s; return; }
        if (!ScriptLoop) { ScriptTime = d; PauseScript(); return; }
        ScriptTime = s % d;
    }

    private void RaisePlaying()
    {
        this.RaisePropertyChanged(nameof(IsScriptPlaying));
        this.RaisePropertyChanged(nameof(ScriptPlayLabel));
    }

    private void RaiseScriptTime()
    {
        this.RaisePropertyChanged(nameof(ScriptTime));
        this.RaisePropertyChanged(nameof(ScriptReadout));
        double s = ScriptTime, d = ScriptDuration;
        foreach (var r in ScriptRows)
            r.IsCurrent = s >= r.Start && (s < r.End || (r.End >= d && s >= d) || (r.End == r.Start && s == r.Start));
    }

    /// <summary>Rebuild the rows and markers from the parsed scene (called whenever it reloads).</summary>
    private void RebuildScript(MathPlaneScene scene)
    {
        var timed = MathPlaneScript.Layout(scene.Steps, out double d);
        ScriptRows.Clear();
        foreach (var m in timed)
            ScriptRows.Add(new MathPlaneScriptRow(MathPlaneScene.StepText(m.Step), m.Start, m.End, m.Step.With));
        ScriptMarkers.Clear();
        ScriptMarkers.AddRange(timed.SelectMany(m => new[] { m.Start, m.End }).Distinct().OrderBy(x => x));
        ScriptDuration = d;
        if (ScriptRows.Count == 0) PauseScript();
        if (P.MathPlaneScriptTime > d) P.MathPlaneScriptTime = d;   // a shorter script: hold its end
        this.RaisePropertyChanged(nameof(HasScript));
        RaiseScriptTime();
    }
}
