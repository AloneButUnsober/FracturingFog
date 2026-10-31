// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Map.cs
//
// #1552 (Math Plane S5) — the section's map controls: the 2x2 matrix, presets,
// interpolation, the progress t, and Play / Loop. They edit the numeric params
// MathPlaneMapMode / MathPlaneM11..M22 / MathPlaneT / MathPlaneInterpolation
// (region snapshot, --param, keyframes) and trigger a render, like every other
// edit in this section.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reactive;
using Avalonia.Threading;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel
{
    /// <summary>A named matrix for the preset buttons.</summary>
    public sealed record MapPreset(string Name, double M11, double M12, double M21, double M22, string Hint);

    /// <summary>The preset matrices: one of each kind of linear map.</summary>
    public static IReadOnlyList<MapPreset> PresetList { get; } = new[]
    {
        new MapPreset("Shear",       1, 1, 0, 1, "i stays, j leans: det 1, one eigenvector"),
        new MapPreset("Rotate 90",   0, -1, 1, 0, "a quarter turn: det 1, no real eigenvectors"),
        new MapPreset("Rotate 45",   0.7071067811865476, -0.7071067811865476, 0.7071067811865476, 0.7071067811865476, "an eighth turn"),
        new MapPreset("Scale 2",     2, 0, 0, 2, "every vector doubles: every vector is an eigenvector"),
        new MapPreset("Stretch",     2, 0, 0, 0.5, "x doubles, y halves: det 1, eigenvectors on the axes"),
        new MapPreset("Reflect",     1, 0, 0, -1, "mirror in the x-axis: det -1"),
        new MapPreset("Project",     1, 0, 0, 0, "squash onto the x-axis: det 0"),
        new MapPreset("Spiral",      0.8, -0.6, 0.6, 0.8, "turn about 37 degrees, length kept"),
    };

    /// <summary>Instance view of <see cref="PresetList"/> for the XAML binding.</summary>
    public IReadOnlyList<MapPreset> MapPresets => PresetList;

    public IReadOnlyList<MathPlaneInterpolation> Interpolations { get; } =
        (MathPlaneInterpolation[])Enum.GetValues(typeof(MathPlaneInterpolation));

    public ReactiveCommand<MapPreset, Unit> ApplyPresetCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PlayCommand { get; private set; } = null!;

    private DispatcherTimer? _playTimer;
    private readonly Stopwatch _playClock = new();

    /// <summary>Seconds for one t = 0 -> 1 run of Play.</summary>
    public const double PlaySeconds = 2.0;

    private void InitMap()
    {
        ApplyPresetCommand = ReactiveCommand.Create<MapPreset>(ApplyPreset);
        PlayCommand = ReactiveCommand.Create(TogglePlay);
    }

    private FractalParameters P => _view.FractalParameters;

    private void SetMap(Action<FractalParameters> change)
    {
        change(P);
        RaiseMapProperties();
        _trigger();
    }

    public void RaiseMapProperties()
    {
        this.RaisePropertyChanged(nameof(MapEnabled));
        this.RaisePropertyChanged(nameof(M11));
        this.RaisePropertyChanged(nameof(M12));
        this.RaisePropertyChanged(nameof(M21));
        this.RaisePropertyChanged(nameof(M22));
        this.RaisePropertyChanged(nameof(T));
        this.RaisePropertyChanged(nameof(Interpolation));
        this.RaisePropertyChanged(nameof(MapReadout));
        RaiseComplexProperties();
    }

    /// <summary>Apply the matrix (MathPlaneMapMode Matrix) or show the plain plane.</summary>
    public bool MapEnabled
    {
        get => P.MathPlaneMapMode == MathPlaneMapMode.Matrix;
        set { if (value != MapEnabled) SetMap(p => p.MathPlaneMapMode = value ? MathPlaneMapMode.Matrix : MathPlaneMapMode.None); }
    }

    public double M11 { get => P.MathPlaneM11; set => SetEntry(value, v => P.MathPlaneM11 = v); }
    public double M12 { get => P.MathPlaneM12; set => SetEntry(value, v => P.MathPlaneM12 = v); }
    public double M21 { get => P.MathPlaneM21; set => SetEntry(value, v => P.MathPlaneM21 = v); }
    public double M22 { get => P.MathPlaneM22; set => SetEntry(value, v => P.MathPlaneM22 = v); }

    private void SetEntry(double value, Action<double> set)
    {
        if (!double.IsFinite(value)) return;
        SetMap(_ => set(value));
    }

    /// <summary>Map progress: 0 = identity, 1 = the full matrix.</summary>
    public double T
    {
        get => P.MathPlaneT;
        set
        {
            double t = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
            if (t != P.MathPlaneT) SetMap(p => p.MathPlaneT = t);
        }
    }

    public MathPlaneInterpolation Interpolation
    {
        get => P.MathPlaneInterpolation;
        set { if (value != P.MathPlaneInterpolation) SetMap(p => p.MathPlaneInterpolation = value); }
    }

    /// <summary>The current frame's matrix and det, e.g. "[[1, 0.5], [0, 1]]  det 1".</summary>
    public string MapReadout
    {
        get
        {
            var m = MathPlaneMap.Current(P);
            string F(double v) => Num(Math.Abs(v) < 5e-7 ? 0 : v);
            string s = $"now [[{F(m.A)}, {F(m.B)}], [{F(m.C)}, {F(m.D)}]]  det {F(m.Det)}";
            if (P.MathPlaneInterpolation == MathPlaneInterpolation.Polar && MathPlaneMap.Matrix(P).Det <= 0)
                s += "  (polar needs det > 0: using linear)";
            return s;
        }
    }

    private void ApplyPreset(MapPreset preset)
    {
        SetMap(p =>
        {
            p.MathPlaneMapMode = MathPlaneMapMode.Matrix;
            p.MathPlaneM11 = preset.M11; p.MathPlaneM12 = preset.M12;
            p.MathPlaneM21 = preset.M21; p.MathPlaneM22 = preset.M22;
        });
    }

    private bool _loop;
    /// <summary>Play repeats (ping-pong 0 -> 1 -> 0) until pressed again.</summary>
    public bool Loop { get => _loop; set => this.RaiseAndSetIfChanged(ref _loop, value); }

    public bool IsPlaying => _playTimer?.IsEnabled == true;
    public string PlayLabel => IsPlaying ? "Stop" : "Play";

    private void TogglePlay()
    {
        if (IsPlaying) { StopPlay(); return; }
        // #1553 — Play runs whichever map is on (matrix or complex); none: the matrix.
        if (P.MathPlaneMapMode == MathPlaneMapMode.None) MapEnabled = true;
        _playClock.Restart();
        _playTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => PlayTick());
        _playTimer.Start();
        this.RaisePropertyChanged(nameof(IsPlaying));
        this.RaisePropertyChanged(nameof(PlayLabel));
    }

    private void PlayTick()
    {
        double u = _playClock.Elapsed.TotalSeconds / PlaySeconds;
        if (!Loop && u >= 1) { T = 1; StopPlay(); return; }
        // Loop: ping-pong so the plane folds back as smoothly as it unfolds.
        double phase = u % 2;
        T = phase <= 1 ? phase : 2 - phase;
    }

    private void StopPlay()
    {
        _playTimer?.Stop();
        this.RaisePropertyChanged(nameof(IsPlaying));
        this.RaisePropertyChanged(nameof(PlayLabel));
    }
}
