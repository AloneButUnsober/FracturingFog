// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MainViewModel.MathOverlay.cs
//
// #1554 (Math Plane S7, part 2) — the math overlay toggle for normal 2D views
// (View section): axes plus the orbit of one point (MathOverlay). While "Follow
// pointer" is on, the orbit follows the hovered pixel (live only, repainted
// without a recompute); Pin freezes the last hovered point, which poster export,
// the Command builder and --math-overlay then reproduce.
//
// #1734 — on 3D raymarcher views the same controls drive the 3D overlay: the hovered point is
// the surface point under the pointer (the frame's own depth through its camera, object space),
// Pin pins it, and the pin text takes "x y z". Turning it on renders once so the frame has depth.

using System;
using System.Globalization;
using System.Reactive;
using FracturingFog.Input;
using FracturingFog.ViewState;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MainViewModel
{
    public ReactiveCommand<Unit, Unit> PinMathOverlayCommand { get; private set; } = null!;

    private (double Re, double Im)? _lastOverlayHover;
    private (double X, double Y, double Z)? _lastOverlayHover3D;   // #1734

    private void InitMathOverlay()
    {
        PinMathOverlayCommand = ReactiveCommand.Create(PinMathOverlay);
        _input.HoverObserver += OnMathOverlayHover;
    }

    /// <summary>Draw the axes and an orbit over 2D views.</summary>
    public bool MathOverlay
    {
        get => _renderHost.MathOverlayEnabled;
        set
        {
            if (value == _renderHost.MathOverlayEnabled) return;
            _renderHost.MathOverlayEnabled = value;
            this.RaisePropertyChanged();
            // #1734 — a 3D frame needs its depth, which only a render captures.
            if (ViewState.Is3D) _renderHost.Trigger();
            else _renderHost.RequestOverlayRepaint();
        }
    }

    private bool _mathOverlayFollow = true;
    /// <summary>The orbit follows the pointer (else it stays on the pinned point).</summary>
    public bool MathOverlayFollow
    {
        get => _mathOverlayFollow;
        set
        {
            if (!this.RaiseAndSetIfChangedReturnsChanged(ref _mathOverlayFollow, value)) return;
            if (!value) { _renderHost.SetMathOverlayHover(null, null); _renderHost.SetMathOverlayHover3D(null); }
            else if (ViewState.Is3D) _renderHost.SetMathOverlayHover3D(_lastOverlayHover3D);
            else if (_lastOverlayHover is { } h) _renderHost.SetMathOverlayHover(h.Re, h.Im);
        }
    }

    public int MathOverlaySteps
    {
        get => _renderHost.MathOverlaySteps;
        set
        {
            int n = Math.Clamp(value, 1, FracturingFog.Models.MathPlaneDynamics.MaxSteps);
            if (n == _renderHost.MathOverlaySteps) return;
            _renderHost.MathOverlaySteps = n;
            this.RaisePropertyChanged();
            if (_mathOverlayFollow && ViewState.Is3D && _lastOverlayHover3D is { } h3) _renderHost.SetMathOverlayHover3D(h3);
            else if (_mathOverlayFollow && _lastOverlayHover is { } h) _renderHost.SetMathOverlayHover(h.Re, h.Im);
            else _renderHost.RequestOverlayRepaint();
        }
    }

    private string? _mathOverlayPinError;
    public string? MathOverlayPinError { get => _mathOverlayPinError; private set => this.RaiseAndSetIfChanged(ref _mathOverlayPinError, value); }

    /// <summary>The pinned point as "re im" (empty: none).</summary>
    public string MathOverlayPinText
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            if (ViewState.Is3D)   // #1734
                return _renderHost.MathOverlay3DPin is { } q
                    ? q.X.ToString("0.########", inv) + " " + q.Y.ToString("0.########", inv) + " " + q.Z.ToString("0.########", inv)
                    : "";
            return _renderHost.MathOverlayPin is { } p
                ? p.Re.ToString("0.########", inv) + " " + p.Im.ToString("0.########", inv)
                : "";
        }
        set
        {
            var parts = (value ?? "").Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (ViewState.Is3D)   // #1734 — x y z in the fractal's own axes
            {
                if (parts.Length == 0) { SetPin3D(null); return; }
                var v = new double[3];
                if (parts.Length != 3 || !TryNums(parts, v)) { MathOverlayPinError = "The pin takes x y z on a 3D view."; return; }
                SetPin3D((v[0], v[1], v[2]));
                MathOverlayFollow = false;
                return;
            }
            if (parts.Length == 0) { SetPin(null); return; }
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double re) || !double.IsFinite(re)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double im) || !double.IsFinite(im))
            { MathOverlayPinError = "The pin takes re im."; return; }
            SetPin((re, im));
            MathOverlayFollow = false;
        }
    }

    private static bool TryNums(string[] parts, double[] into)
    {
        for (int k = 0; k < into.Length; k++)
            if (!double.TryParse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture, out into[k]) || !double.IsFinite(into[k]))
                return false;
        return true;
    }

    /// <summary>#1734 — pin a 3D point (object space), or clear the pin.</summary>
    private void SetPin3D((double X, double Y, double Z)? p)
    {
        MathOverlayPinError = null;
        _renderHost.MathOverlay3DPin = p is { } q ? new MathOverlay3DSpec(q.X, q.Y, q.Z, _renderHost.MathOverlaySteps) : null;
        this.RaisePropertyChanged(nameof(MathOverlayPinText));
        _renderHost.RequestOverlayRepaint();
    }

    private void SetPin((double Re, double Im)? p)
    {
        MathOverlayPinError = null;
        _renderHost.MathOverlayPin = p is { } q ? new MathOverlaySpec(q.Re, q.Im, _renderHost.MathOverlaySteps) : null;
        this.RaisePropertyChanged(nameof(MathOverlayPinText));
        _renderHost.RequestOverlayRepaint();
    }

    /// <summary>Pin the last hovered point and stop following the pointer.</summary>
    private void PinMathOverlay()
    {
        if (ViewState.Is3D)   // #1734
        {
            if (_lastOverlayHover3D is not { } h3) { MathOverlayPinError = "Point at the fractal first (the sky has no surface point)."; return; }
            SetPin3D(h3);
            MathOverlayFollow = false;
            return;
        }
        if (_lastOverlayHover is not { } h) { MathOverlayPinError = "Hover over the view first."; return; }
        SetPin(h);
        MathOverlayFollow = false;
    }

    private void OnMathOverlayHover(PointerInput e)
    {
        if (!_renderHost.MathOverlayEnabled) return;
        if (ViewState.Is3D)   // #1734 — the surface point under the pointer (none over the sky)
        {
            var p3 = _renderHost.PickSurface3D(e.X, e.Y, e.ClientWidth, e.ClientHeight);
            if (p3 != null) _lastOverlayHover3D = p3;
            if (_mathOverlayFollow) _renderHost.SetMathOverlayHover3D(p3);
            return;
        }
        var (re, im) = FracturingFog.ViewState.MathOverlay.ClientToPlane(e.X, e.Y, e.ClientWidth, e.ClientHeight,
                                                                         ViewState.CenterX, ViewState.CenterY, ViewState.Zoom);
        _lastOverlayHover = (re, im);
        if (_mathOverlayFollow) _renderHost.SetMathOverlayHover(re, im);
    }
}
