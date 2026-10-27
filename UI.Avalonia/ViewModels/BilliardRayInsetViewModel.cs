// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/BilliardRayInsetViewModel.cs (#1447)
//
// State of the chaotic-billiard ray-path inset: the real-space scene, the ray
// under the pointer (hover) and up to MaxPins pinned rays (Ctrl+click). The host
// supplies the tracing (UI.Avalonia does not reference the Engine): TraceAtPointer
// maps a pointer to the render's plane point and traces it, TraceAtPlane re-traces
// a pinned plane point after the parameters or the theme change, GetScene builds
// the disks and gates. Pins are kept as plane points, so they stay on the same
// launch through pans and zooms.

using System;
using System.Collections.Generic;
using FracturingFog.Input;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class BilliardRayInsetViewModel : ViewModelBase
{
    /// <summary>Most pinned rays kept; pinning another drops the oldest.</summary>
    public const int MaxPins = 4;

    /// <summary>Host: trace the launch under pointer (x, y) of a client (w, h); null when
    /// the active type is not the chaotic billiard.</summary>
    public Func<double, double, double, double, BilliardRayPath?>? TraceAtPointer { get; set; }

    /// <summary>Host: trace the launch at a plane point; null when not the billiard.</summary>
    public Func<double, double, BilliardRayPath?>? TraceAtPlane { get; set; }

    /// <summary>Host: the current real-space scene; null when not the billiard.</summary>
    public Func<BilliardInsetScene?>? GetScene { get; set; }

    /// <summary>Raised after anything the inset draws changed.</summary>
    public event EventHandler? Changed;

    private readonly List<(double X, double Y)> _pinPlanes = new();
    private readonly List<BilliardRayPath> _pins = new();
    private PointerInput? _lastHover;

    public BilliardInsetScene? Scene { get; private set; }
    public BilliardRayPath? Hover { get; private set; }
    public IReadOnlyList<BilliardRayPath> Pins => _pins;

    private string _status = HintText;
    /// <summary>Text under the drawing: one line per pinned ray, then the hovered ray.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public const string HintText = "Hover the render to trace a ray. Ctrl+click to pin it.";
    public const string WrongTypeText = "The ray-path inset works with Chaotic Billiard (Scatter).";

    /// <summary>Pin letters, in pin order.</summary>
    public static string PinLabel(int i) => ((char)('A' + i)).ToString();

    /// <summary>Pointer moved over the render with no button held.</summary>
    public void OnHover(PointerInput e)
    {
        _lastHover = e;
        Hover = TraceAtPointer?.Invoke(e.X, e.Y, e.ClientWidth, e.ClientHeight);
        Publish();
    }

    /// <summary>Ctrl+click: pin the ray under the pointer. Returns true when a ray was
    /// pinned (the press is consumed, so it does not start a pan).</summary>
    public bool Pin(PointerInput e)
    {
        var path = TraceAtPointer?.Invoke(e.X, e.Y, e.ClientWidth, e.ClientHeight);
        if (path == null) return false;
        if (_pins.Count >= MaxPins)
        {
            _pins.RemoveAt(0);
            _pinPlanes.RemoveAt(0);
        }
        _pins.Add(path);
        _pinPlanes.Add((path.PlaneX, path.PlaneY));
        Publish();
        return true;
    }

    public void ClearPins()
    {
        _pins.Clear();
        _pinPlanes.Clear();
        Publish();
    }

    /// <summary>Re-trace after a frame: the parameters, theme or view may have changed.
    /// Pins re-trace at their plane points; the hover re-traces under the last pointer
    /// position (the view under it may have moved).</summary>
    public void Refresh()
    {
        Scene = GetScene?.Invoke();
        if (Scene == null)
        {
            Hover = null;
        }
        else
        {
            for (int i = 0; i < _pins.Count; i++)
            {
                var p = TraceAtPlane?.Invoke(_pinPlanes[i].X, _pinPlanes[i].Y);
                if (p != null) _pins[i] = p;
            }
            if (_lastHover is { } h)
                Hover = TraceAtPointer?.Invoke(h.X, h.Y, h.ClientWidth, h.ClientHeight);
        }
        Publish();
    }

    private void Publish()
    {
        if (Scene == null)
        {
            Status = WrongTypeText;
        }
        else
        {
            var lines = new List<string>(_pins.Count + 1);
            for (int i = 0; i < _pins.Count; i++) lines.Add(PinLabel(i) + "  " + _pins[i].Summary);
            lines.Add(Hover != null ? "Hover  " + Hover.Summary : HintText);
            Status = string.Join(Environment.NewLine, lines);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
