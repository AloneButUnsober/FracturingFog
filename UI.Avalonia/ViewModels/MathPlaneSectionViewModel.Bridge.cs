// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Bridge.cs
//
// #1554 (Math Plane S7) — the fractal bridge controls: the backdrop family (param
// MathPlaneBackdrop), the orbit and the cobweb (scene statements), and dragging c
// (params MathPlaneCRe / MathPlaneCIm) on the canvas while the orbit is shown.

using System;
using System.Collections.Generic;
using System.Globalization;
using FracturingFog.Input;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel
{
    public IReadOnlyList<MathPlaneBackdrop> Backdrops { get; } =
        (MathPlaneBackdrop[])Enum.GetValues(typeof(MathPlaneBackdrop));

    private void RaiseBridgeProperties()
    {
        this.RaisePropertyChanged(nameof(Backdrop));
        this.RaisePropertyChanged(nameof(OrbitEnabled));
        this.RaisePropertyChanged(nameof(OrbitSteps));
        this.RaisePropertyChanged(nameof(CobwebEnabled));
        this.RaisePropertyChanged(nameof(CobwebX0));
        this.RaisePropertyChanged(nameof(CobwebSteps));
        this.RaisePropertyChanged(nameof(CobwebSource));
    }

    /// <summary>The escape-time family drawn under the scene.</summary>
    public MathPlaneBackdrop Backdrop
    {
        get => P.MathPlaneBackdrop;
        set { if (value != P.MathPlaneBackdrop) SetMap(p => p.MathPlaneBackdrop = value); }
    }

    /// <summary>Draw the orbit of 0 (scene <c>orbit N</c>); c becomes draggable.</summary>
    public bool OrbitEnabled
    {
        get => _scene.OrbitSteps > 0;
        set
        {
            if (value == OrbitEnabled || !_sceneValid) return;
            Commit(_scene with { OrbitSteps = value ? DefaultOrbitSteps : 0 });
        }
    }

    public const int DefaultOrbitSteps = 50;

    public int OrbitSteps
    {
        get => _scene.OrbitSteps > 0 ? _scene.OrbitSteps : DefaultOrbitSteps;
        set
        {
            int n = Math.Clamp(value, 1, MathPlaneDynamics.MaxSteps);
            if (!_sceneValid || n == _scene.OrbitSteps) return;
            Commit(_scene with { OrbitSteps = n });
        }
    }

    /// <summary>Draw the cobweb (scene <c>cobweb</c>); its c is the real part of the plane's c.</summary>
    public bool CobwebEnabled
    {
        get => _scene.Cobweb != null;
        set
        {
            if (value == CobwebEnabled || !_sceneValid) return;
            Commit(_scene with { Cobweb = value ? new MathPlaneCobweb(0.1, MathPlaneCobweb.DefaultSteps, MathPlaneCobweb.DefaultSource) : null });
        }
    }

    public double CobwebX0
    {
        get => _scene.Cobweb?.X0 ?? 0.1;
        set { if (double.IsFinite(value) && _scene.Cobweb is { } cw && value != cw.X0) Commit(_scene with { Cobweb = cw with { X0 = value } }); }
    }

    public int CobwebSteps
    {
        get => _scene.Cobweb?.Steps ?? MathPlaneCobweb.DefaultSteps;
        set
        {
            int n = Math.Clamp(value, 1, MathPlaneDynamics.MaxSteps);
            if (_scene.Cobweb is { } cw && n != cw.Steps) Commit(_scene with { Cobweb = cw with { Steps = n } });
        }
    }

    private string? _cobwebDraft;
    /// <summary>The cobweb's real map f(x) (reads x and c).</summary>
    public string CobwebSource
    {
        get => _cobwebDraft ?? _scene.Cobweb?.Source ?? MathPlaneCobweb.DefaultSource;
        set
        {
            if (_scene.Cobweb is not { } cw || !_sceneValid) return;
            string src = (value ?? "").Trim();
            var trial = _scene with { Cobweb = cw with { Source = src } };
            if (!MathPlaneScene.TryParse(trial.Serialize(), out _, out var err))
            {
                _cobwebDraft = src;
                ComplexError = err!.Message;
                return;
            }
            _cobwebDraft = null;
            ComplexError = null;
            Commit(trial);
        }
    }

    // ── Dragging c ──

    private bool _dragC;

    /// <summary>True when the pointer is on the c handle (drawn while the orbit is on).</summary>
    private bool HitsC(PointerInput e)
    {
        if (!_sceneValid || _scene.OrbitSteps <= 0) return false;
        var (wx, wy) = World(e);
        double dx = wx - P.MathPlaneCRe, dy = wy - P.MathPlaneCIm;
        double r = MathPlaneSceneEdit.HitRadiusPixels * PixelPitch(e);
        return dx * dx + dy * dy <= r * r;
    }

    private void DragCTo(PointerInput e)
    {
        var (wx, wy) = World(e);
        if ((e.Modifiers & InputModifiers.Shift) != 0)
        {
            double step = MathPlaneSceneEdit.MinorStep(e.ClientWidth * PixelPitch(e));
            wx = MathPlaneSceneEdit.Snap(wx, step);
            wy = MathPlaneSceneEdit.Snap(wy, step);
        }
        SetMap(p => { p.MathPlaneCRe = wx; p.MathPlaneCIm = wy; });
        PointerReadout = $"c = {Num(wx)} {(wy < 0 ? "-" : "+")} {Num(Math.Abs(wy))}i";
    }
}
