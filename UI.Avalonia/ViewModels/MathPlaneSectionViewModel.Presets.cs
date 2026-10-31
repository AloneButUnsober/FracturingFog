// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Presets.cs
//
// #1555 (Math Plane S8) — the built-in demos (MathPlanePresets). Loading one
// copies its scene, params and view into the live view; the built-ins themselves
// never change. Save a region to keep an edited demo.

using System.Collections.Generic;
using System.Reactive;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel
{
    public IReadOnlyList<MathPlanePreset> Demos => MathPlanePresets.All;

    public ReactiveCommand<Unit, Unit> LoadDemoCommand { get; private set; } = null!;

    private void InitPresets()
    {
        LoadDemoCommand = ReactiveCommand.Create(() => { if (_selectedDemo is { } d) LoadDemo(d); });
    }

    private MathPlanePreset? _selectedDemo;
    public MathPlanePreset? SelectedDemo
    {
        get => _selectedDemo;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDemo, value);
            this.RaisePropertyChanged(nameof(DemoIdea));
        }
    }

    /// <summary>What the selected demo shows.</summary>
    public string DemoIdea => _selectedDemo?.Idea ?? "Pick a demo: each one shows one idea behind the fractals.";

    /// <summary>Load <paramref name="d"/> into the live view (scene, map, c, backdrop, view).</summary>
    public void LoadDemo(MathPlanePreset d)
    {
        StopPlay();
        d.Apply(_view.FractalParameters);
        _view.CenterX = d.ViewWorldX; _view.CenterXLo = 0; _view.CenterX2 = 0; _view.CenterX3 = 0;
        _view.CenterX4 = 0; _view.CenterX5 = 0; _view.CenterX6 = 0; _view.CenterX7 = 0;
        _view.CenterY = -d.ViewWorldY; _view.CenterYLo = 0; _view.CenterY2 = 0; _view.CenterY3 = 0;
        _view.CenterY4 = 0; _view.CenterY5 = 0; _view.CenterY6 = 0; _view.CenterY7 = 0;
        _view.Zoom = d.Zoom;
        _complexDraft = null; _cobwebDraft = null; ComplexError = null;
        Refresh();
        RaiseMapProperties();
        _trigger();
    }
}
