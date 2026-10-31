// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MainViewModel.MathOverlay.cs
//
// #1554 (Math Plane S7, part 2) — the math overlay toggle for normal 2D views
// (View section): axes plus the orbit of one point (MathOverlay). While "Follow
// pointer" is on, the orbit follows the hovered pixel (live only, repainted
// without a recompute); Pin freezes the last hovered point, which poster export,
// the Command builder and --math-overlay then reproduce.

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
            _renderHost.RequestOverlayRepaint();
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
            if (!value) _renderHost.SetMathOverlayHover(null, null);
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
            if (_mathOverlayFollow && _lastOverlayHover is { } h) _renderHost.SetMathOverlayHover(h.Re, h.Im);
            else _renderHost.RequestOverlayRepaint();
        }
    }

    private string? _mathOverlayPinError;
    public string? MathOverlayPinError { get => _mathOverlayPinError; private set => this.RaiseAndSetIfChanged(ref _mathOverlayPinError, value); }

    /// <summary>The pinned point as "re im" (empty: none).</summary>
    public string MathOverlayPinText
    {
        get => _renderHost.MathOverlayPin is { } p
            ? p.Re.ToString("0.########", CultureInfo.InvariantCulture) + " " + p.Im.ToString("0.########", CultureInfo.InvariantCulture)
            : "";
        set
        {
            var parts = (value ?? "").Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { SetPin(null); return; }
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double re) || !double.IsFinite(re)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double im) || !double.IsFinite(im))
            { MathOverlayPinError = "The pin takes re im."; return; }
            SetPin((re, im));
            MathOverlayFollow = false;
        }
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
        if (_lastOverlayHover is not { } h) { MathOverlayPinError = "Hover over the view first."; return; }
        SetPin(h);
        MathOverlayFollow = false;
    }

    private void OnMathOverlayHover(PointerInput e)
    {
        if (!_renderHost.MathOverlayEnabled || ViewState.Is3D) return;
        var (re, im) = FracturingFog.ViewState.MathOverlay.ClientToPlane(e.X, e.Y, e.ClientWidth, e.ClientHeight,
                                                                         ViewState.CenterX, ViewState.CenterY, ViewState.Zoom);
        _lastOverlayHover = (re, im);
        if (_mathOverlayFollow) _renderHost.SetMathOverlayHover(re, im);
    }
}
