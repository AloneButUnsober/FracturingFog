// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.Complex.cs
//
// #1553 (Math Plane S6) — the section's complex-map controls. The map text, the
// phase-portrait mode and the probe live in the scene (map / phase / probe
// statements); the mode and c are the numeric params MathPlaneMapMode and
// MathPlaneCRe / MathPlaneCIm. t, Play and Loop are shared with the matrix map.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Reactive;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel
{
    /// <summary>A named complex map for the preset buttons.</summary>
    public sealed record ComplexPreset(string Name, string Source, string Hint);

    /// <summary>The preset maps (#1553 scope).</summary>
    public static IReadOnlyList<ComplexPreset> ComplexPresetList { get; } = new[]
    {
        new ComplexPreset("z^2",     "z^2",     "squares lengths, doubles angles: the grid folds onto itself twice"),
        new ComplexPreset("z^2 + c", "z^2 + c", "the Mandelbrot / Julia step: square, then shift by c"),
        new ComplexPreset("1/z",     "1/z",     "inversion: the inside of the unit circle swaps with the outside, lines become circles"),
        new ComplexPreset("exp z",   "exp(z)",  "horizontal lines become rays, vertical lines circles"),
        new ComplexPreset("i z",     "i*z",     "a quarter turn: i-hat goes to j-hat"),
    };

    public IReadOnlyList<ComplexPreset> ComplexPresets => ComplexPresetList;

    public IReadOnlyList<MathPlanePhase> PhaseModes { get; } =
        (MathPlanePhase[])Enum.GetValues(typeof(MathPlanePhase));

    public ReactiveCommand<ComplexPreset, Unit> ApplyComplexPresetCommand { get; private set; } = null!;

    private void InitComplex()
    {
        ApplyComplexPresetCommand = ReactiveCommand.Create<ComplexPreset>(p => SetComplexSource(p.Source, enable: true));
    }

    private void RaiseComplexProperties()
    {
        this.RaisePropertyChanged(nameof(ComplexEnabled));
        this.RaisePropertyChanged(nameof(ComplexSource));
        this.RaisePropertyChanged(nameof(CRe));
        this.RaisePropertyChanged(nameof(CIm));
        this.RaisePropertyChanged(nameof(Phase));
        this.RaisePropertyChanged(nameof(ProbeEnabled));
        this.RaisePropertyChanged(nameof(ProbeAt));
        this.RaisePropertyChanged(nameof(ComplexReadout));
    }

    /// <summary>Apply the scene's complex map (MathPlaneMapMode Complex).</summary>
    public bool ComplexEnabled
    {
        get => P.MathPlaneMapMode == MathPlaneMapMode.Complex;
        set
        {
            if (value == ComplexEnabled) return;
            if (value && _sceneValid && _scene.MapSource == null) { SetComplexSource("z^2", enable: true); return; }
            SetMap(p => p.MathPlaneMapMode = value ? MathPlaneMapMode.Complex : MathPlaneMapMode.None);
            RaiseComplexProperties();
        }
    }

    private string? _complexError;
    /// <summary>Why the typed map was not taken, or null.</summary>
    public string? ComplexError { get => _complexError; private set => this.RaiseAndSetIfChanged(ref _complexError, value); }

    private string? _complexDraft;
    /// <summary>The map f(z), in the plot language (reads z and c).</summary>
    public string ComplexSource
    {
        get => _complexDraft ?? _scene.MapSource ?? "";
        set => SetComplexSource(value ?? "", enable: false);
    }

    private void SetComplexSource(string source, bool enable)
    {
        if (!_sceneValid) return;
        source = source.Trim();
        // Check it the way the scene parser will (one line, parses, reads only z and c).
        var trial = _scene with { MapSource = source.Length == 0 ? null : source };
        if (source.Length > 0 && !MathPlaneScene.TryParse(trial.Serialize(), out _, out var err))
        {
            _complexDraft = source;
            ComplexError = err!.Message;
            return;
        }
        _complexDraft = null;
        ComplexError = null;
        if (enable) P.MathPlaneMapMode = MathPlaneMapMode.Complex;
        Commit(trial);
        RaiseMapProperties();
        RaiseComplexProperties();
    }

    /// <summary>The c a map reads, real part.</summary>
    public double CRe
    {
        get => P.MathPlaneCRe;
        set { if (double.IsFinite(value) && value != P.MathPlaneCRe) { SetMap(p => p.MathPlaneCRe = value); RaiseComplexProperties(); } }
    }

    /// <summary>The c a map reads, imaginary part.</summary>
    public double CIm
    {
        get => P.MathPlaneCIm;
        set { if (double.IsFinite(value) && value != P.MathPlaneCIm) { SetMap(p => p.MathPlaneCIm = value); RaiseComplexProperties(); } }
    }

    /// <summary>The phase-portrait background (scene <c>phase</c>).</summary>
    public MathPlanePhase Phase
    {
        get => _scene.Phase;
        set
        {
            if (value == _scene.Phase || !_sceneValid) return;
            // A portrait needs an f: start from z^2 when the scene has none yet.
            var s = _scene with { Phase = value };
            if (value != MathPlanePhase.Off && s.MapSource == null) s = s with { MapSource = "z^2" };
            Commit(s);
            RaiseComplexProperties();
        }
    }

    /// <summary>Show the pinned f'(z) probe (scene <c>probe</c>); it starts at the view centre.</summary>
    public bool ProbeEnabled
    {
        get => _scene.Probe != null;
        set
        {
            if (value == ProbeEnabled || !_sceneValid) return;
            double x = _view.CenterX, y = -_view.CenterY;
            if (x == 0 && y == 0) x = 1;   // f'(0) of the presets is dull (0 or a pole)
            Commit(_scene with { Probe = value ? (x, y) : null });
            RaiseComplexProperties();
        }
    }

    /// <summary>The probe's z as "x y".</summary>
    public string ProbeAt
    {
        get => _scene.Probe is { } p ? $"{Num(p.X)} {Num(p.Y)}" : "";
        set
        {
            if (!_sceneValid) return;
            var parts = (value ?? "").Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, Inv, out double x) || !double.IsFinite(x)
                || !double.TryParse(parts[1], NumberStyles.Float, Inv, out double y) || !double.IsFinite(y))
            { ComplexError = "The probe takes x y."; return; }
            ComplexError = null;
            Commit(_scene with { Probe = (x, y) });
            RaiseComplexProperties();
        }
    }

    /// <summary>f'(probe) as a rotate-and-scale, or what is missing.</summary>
    public string ComplexReadout
    {
        get
        {
            if (!ComplexEnabled) return "";
            if (CurrentComplex is not { } m) return "no map yet: pick a preset or type f(z)";
            if (_scene.Probe is not { } z) return "tick Probe for f'(z) at a point";
            return DerivativeReadout(m, new Complex(z.X, z.Y));
        }
    }

    /// <summary>#1553 — f'(z) as "f'(z) = a + bi: scale |f'|, turn arg f'".</summary>
    public static string DerivativeReadout(MathPlaneComplexMap m, Complex z)
    {
        var d = m.Derivative(z);
        if (!double.IsFinite(d.Real) || !double.IsFinite(d.Imaginary)) return $"f'({Cx(z)}) is not finite";
        double deg = Math.Atan2(d.Imaginary, d.Real) * 180.0 / Math.PI;
        return $"f'({Cx(z)}) = {Cx(d)}: scale {Num(d.Magnitude)}, turn {Num(deg)}°";
    }

    private static string Cx(Complex z) => $"{Num(z.Real)} {(z.Imaginary < 0 ? "-" : "+")} {Num(Math.Abs(z.Imaginary))}i";

    /// <summary>#1553 — the complex map the view draws with, or null.</summary>
    private MathPlaneComplexMap? CurrentComplex => MathPlaneComplexMap.Current(P, _scene);
}
