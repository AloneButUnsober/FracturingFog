// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Controls/BilliardRayInsetControl.cs (#1447)
//
// Draws the chaotic-billiard ray-path inset in real space (y up): the gate
// sectors as a coloured ring outside the play circle (each gate's sector of exit
// directions, in the gate's render colour), the play circle, the mirror disks,
// then the paths. Pinned rays are solid with their letter at the launch point;
// the hovered ray is dashed. Each path is drawn in its exit gate's colour, so a
// path leaving through the vermillion sector is the pixel the render paints
// vermillion. The tracing is the host's (BilliardRayInsetViewModel).
//
// #1504 — layout editing input: left-drag a disk to move it, wheel over a disk to
// resize it, double-click empty space to add a disk, right-click a disk to remove
// it. This control only converts the pointer to world space; the rules and the
// parameter writes are the view model's.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class BilliardRayInsetControl : Control
{
    /// <summary>View half-extent as a multiple of the play radius: far-field launches
    /// start just outside the play circle, and the gate ring sits outside it.</summary>
    public const double ExtentFactor = 1.3;

    private BilliardRayInsetViewModel? _vm;

    public void Attach(BilliardRayInsetViewModel vm)
    {
        if (_vm != null) _vm.InsetChanged -= OnChanged;
        _vm = vm;
        _vm.InsetChanged += OnChanged;
        InvalidateVisual();
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    // World <-> control mapping shared by drawing and input: centred, y up, the view
    // half-extent ExtentFactor x the play radius. False when there is nothing to map.
    private bool Mapping(out double s, out double ox, out double oy)
    {
        var scene = _vm?.Scene;
        s = ox = oy = 0;
        if (scene == null || Bounds.Width < 4 || Bounds.Height < 4) return false;
        s = Math.Min(Bounds.Width, Bounds.Height) / (2.0 * scene.PlayRadius * ExtentFactor);
        ox = Bounds.Width * 0.5;
        oy = Bounds.Height * 0.5;
        return true;
    }

    private bool ToWorld(Point p, out double x, out double y)
    {
        x = y = 0;
        if (!Mapping(out double s, out double ox, out double oy)) return false;
        x = (p.X - ox) / s;
        y = (oy - p.Y) / s;
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm == null || !ToWorld(e.GetPosition(this), out double x, out double y)) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (_vm.RemoveAt(x, y)) e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && _vm.HitDisk(x, y) < 0)
        {
            if (_vm.AddAt(x, y)) e.Handled = true;
            return;
        }
        if (_vm.BeginDrag(x, y))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm == null || !ToWorld(e.GetPosition(this), out double x, out double y)) return;
        if (_vm.IsDragging)
        {
            _vm.DragTo(x, y);
            e.Handled = true;
        }
        else
        {
            Cursor = _vm.HitDisk(x, y) >= 0 ? s_handCursor : null;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_vm?.IsDragging != true) return;
        _vm.EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_vm?.IsDragging == true) _vm.EndDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm == null || !ToWorld(e.GetPosition(this), out double x, out double y)) return;
        if (_vm.ResizeAt(x, y, e.Delta.Y)) e.Handled = true;
    }

    private static readonly Cursor s_handCursor = new(StandardCursorType.Hand);

    private static readonly IBrush Background = new SolidColorBrush(Color.FromArgb(255, 14, 14, 14));
    private static readonly IBrush DiskFill = new SolidColorBrush(Color.FromArgb(255, 52, 52, 52));
    private static readonly IPen DiskPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)), 1);
    private static readonly IPen PlayPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 80, 80, 80)), 1,
                                                    new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(255, 220, 220, 220));

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var rect = new Rect(Bounds.Size);
        g.FillRectangle(Background, rect);
        var scene = _vm?.Scene;
        if (scene == null || !Mapping(out double s, out double ox, out double oy)) return;
        Point P(double x, double y) => new(ox + x * s, oy - y * s);

        using var clip = g.PushClip(rect);

        // Gate ring: each gate's sector of exit directions, in its colour.
        int n = scene.GateStartAngles.Count;
        double ringR = scene.PlayRadius * 1.1;
        for (int k = 0; k < n; k++)
        {
            double a0 = scene.GateStartAngles[k], a1 = a0 + scene.GateWidths[k];
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                const int steps = 24;
                c.BeginFigure(P(ringR * Math.Cos(a0), ringR * Math.Sin(a0)), false);
                for (int i = 1; i <= steps; i++)
                {
                    double a = a0 + (a1 - a0) * i / steps;
                    c.LineTo(P(ringR * Math.Cos(a), ringR * Math.Sin(a)));
                }
                c.EndFigure(false);
            }
            g.DrawGeometry(null, new Pen(Brush(scene.GateColors[k]), 5), geo);
        }

        g.DrawEllipse(null, PlayPen, P(0, 0), scene.PlayRadius * s, scene.PlayRadius * s);
        foreach (var d in scene.Disks)
            g.DrawEllipse(DiskFill, DiskPen, P(d.X, d.Y), d.R * s, d.R * s);

        if (_vm!.Hover is { Valid: true } hover)
            DrawPath(g, hover, P, dashed: true, label: null);
        for (int i = 0; i < _vm.Pins.Count; i++)
            if (_vm.Pins[i].Valid)
                DrawPath(g, _vm.Pins[i], P, dashed: false, label: BilliardRayInsetViewModel.PinLabel(i));
    }

    private static void DrawPath(DrawingContext g, BilliardRayPath path, Func<double, double, Point> P,
                                 bool dashed, string? label)
    {
        if (path.Points.Count == 0) return;
        var brush = Brush(path.Color);
        var start = P(path.Points[0].X, path.Points[0].Y);
        if (path.Points.Count > 1)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(start, false);
                for (int i = 1; i < path.Points.Count; i++) c.LineTo(P(path.Points[i].X, path.Points[i].Y));
                c.EndFigure(false);
            }
            var pen = dashed
                ? new Pen(brush, 1.5, new DashStyle(new double[] { 4, 2 }, 0))
                : new Pen(brush, 2);
            g.DrawGeometry(null, pen, geo);
        }
        g.DrawEllipse(brush, null, start, 3, 3);
        if (label != null)
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         Typeface.Default, 12, TextBrush);
            g.DrawText(text, new Point(start.X + 4, start.Y - 14));
        }
    }

    private static IBrush Brush(uint argb)
        => new SolidColorBrush(Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
