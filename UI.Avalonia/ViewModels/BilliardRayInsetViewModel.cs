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
//
// #1504 — layout editing. Drag a disk to move it, wheel over a disk to resize it,
// double-click empty space to add one, right-click a disk to remove it. The first
// edit turns the layout into the Custom geometry seeded from the current disks.
// While dragging, ApplyLayout(commit: false) updates the parameters and the inset
// re-traces at once (cheap); the render follows on release (commit: true), since a
// full render cannot keep up with the pointer. Every edit obeys BilliardLayout.CanPlace.

using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Host (#1504): make <c>disks</c> the Custom layout. <c>commit</c> false while a
    /// drag is in progress (parameters only), true when the edit is done (re-render and
    /// refresh the params panel).</summary>
    public Action<IReadOnlyList<BilliardDisk>, bool>? ApplyLayout { get; set; }

    /// <summary>Host (#1504): radius for a newly added disk (the panel's disk radius).</summary>
    public Func<double>? NewDiskRadius { get; set; }

    /// <summary>Host (#1506): false when the render's pixels are not launches (Billiard
    /// Rays), so hover and pins do not apply; the inset still shows and edits the scene.</summary>
    public Func<bool>? CanTracePointer { get; set; }

    public const string LayoutOnlyText = "Billiard Rays draws every ray in the render; edit the disks here.";

    /// <summary>Radius change per wheel notch (#1504).</summary>
    public const double WheelRadiusStep = 1.05;

    /// <summary>Shown in the inset under the drawing.</summary>
    public const string EditHintText = "Drag a disk to move it, wheel to resize, double-click to add, right-click to remove.";

    private List<BilliardDisk>? _layout;   // working copy during a drag
    private int _dragIndex = -1;
    private double _dragDx, _dragDy;
    private bool _dragMoved;

    /// <summary>True while a disk is being dragged.</summary>
    public bool IsDragging => _dragIndex >= 0;

    /// <summary>Raised after anything the inset draws changed. (Not "Changed":
    /// ReactiveObject already has that, for property-change notifications.)</summary>
    public event EventHandler? InsetChanged;

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
    public const string WrongTypeText = "The ray-path inset works with Chaotic Billiard (Scatter) and Billiard Rays.";

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

    /// <summary>Index into <see cref="Scene"/>.Disks of the disk under world point (x, y), or -1.
    /// Overlapping hits go to the disk whose centre is nearest.</summary>
    public int HitDisk(double x, double y)
    {
        var disks = Scene?.Disks;
        if (disks == null) return -1;
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (int i = 0; i < disks.Count; i++)
        {
            double d = Math.Sqrt((x - disks[i].X) * (x - disks[i].X) + (y - disks[i].Y) * (y - disks[i].Y));
            if (d <= disks[i].R && d < bestD) { best = i; bestD = d; }
        }
        return best;
    }

    /// <summary>Press at world point (x, y): starts dragging the disk there. False when
    /// there is no disk under the point (or no billiard).</summary>
    public bool BeginDrag(double x, double y)
    {
        int i = HitDisk(x, y);
        if (i < 0) return false;
        _layout = Scene!.Disks.ToList();
        _dragIndex = i;
        _dragDx = _layout[i].X - x;
        _dragDy = _layout[i].Y - y;
        _dragMoved = false;
        return true;
    }

    /// <summary>Pointer moved to world point (x, y) while dragging. A position that would break
    /// the layout rules is skipped (the disk stays at its last valid position).</summary>
    public void DragTo(double x, double y)
    {
        if (_layout == null || _dragIndex < 0) return;
        var d = _layout[_dragIndex];
        var moved = new BilliardDisk(x + _dragDx, y + _dragDy, d.R);
        if (!BilliardLayout.CanPlace(_layout, _dragIndex, moved)) return;
        _layout[_dragIndex] = moved;
        _dragMoved = true;
        ApplyLayout?.Invoke(_layout, false);
        Refresh();
    }

    /// <summary>Pointer released: commits the drag (a click without a move changes nothing).</summary>
    public void EndDrag()
    {
        if (_layout != null && _dragMoved)
        {
            ApplyLayout?.Invoke(_layout, true);
            Refresh();
        }
        _layout = null;
        _dragIndex = -1;
        _dragMoved = false;
    }

    /// <summary>Wheel over world point (x, y): scale the disk there by
    /// <see cref="WheelRadiusStep"/> per notch. False when nothing changed.</summary>
    public bool ResizeAt(double x, double y, double notches)
    {
        int i = HitDisk(x, y);
        if (i < 0 || notches == 0) return false;
        var disks = Scene!.Disks.ToList();
        double r = Math.Clamp(disks[i].R * Math.Pow(WheelRadiusStep, notches), BilliardLayout.MinRadius, BilliardLayout.MaxRadius);
        var resized = disks[i] with { R = r };
        if (r == disks[i].R || !BilliardLayout.CanPlace(disks, i, resized)) return false;
        disks[i] = resized;
        return Commit(disks);
    }

    /// <summary>Double-click at world point (x, y) on empty space: add a disk there.</summary>
    public bool AddAt(double x, double y)
    {
        var scene = Scene;
        if (scene == null || HitDisk(x, y) >= 0 || scene.Disks.Count >= BilliardLayout.MaxDisks) return false;
        var disks = scene.Disks.ToList();
        double r = Math.Clamp(NewDiskRadius?.Invoke() ?? 0.5, BilliardLayout.MinRadius, BilliardLayout.MaxRadius);
        var added = new BilliardDisk(x, y, r);
        if (!BilliardLayout.CanPlace(disks, disks.Count, added)) return false;
        disks.Add(added);
        return Commit(disks);
    }

    /// <summary>Right-click at world point (x, y): remove the disk there (the last disk stays).</summary>
    public bool RemoveAt(double x, double y)
    {
        int i = HitDisk(x, y);
        if (i < 0 || Scene!.Disks.Count <= 1) return false;
        var disks = Scene.Disks.ToList();
        disks.RemoveAt(i);
        return Commit(disks);
    }

    private bool Commit(List<BilliardDisk> disks)
    {
        if (ApplyLayout == null) return false;
        ApplyLayout(disks, true);
        Refresh();
        return true;
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
            lines.Add(Hover != null ? "Hover  " + Hover.Summary
                      : CanTracePointer?.Invoke() == false ? LayoutOnlyText : HintText);
            Status = string.Join(Environment.NewLine, lines);
        }
        InsetChanged?.Invoke(this, EventArgs.Empty);
    }
}
