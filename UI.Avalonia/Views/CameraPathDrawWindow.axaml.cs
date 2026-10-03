// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.ComponentModel;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>#1056 — modal "Draw camera path" dialog. A top-down plan of the
/// orbit: the centre shape is the object (or, for Relief 3D, the closest zoom),
/// rings mark camera distance, the 0° spoke marks azimuth zero. Dragging draws
/// the path; on release the VM fits it (CameraPathFit) and the key positions are
/// dotted along it. Apply returns the track through the VM.</summary>
public sealed partial class CameraPathDrawWindow : Window
{
    private const double Pad = 14;
    private Canvas? _canvas;
    private bool _drawing;

    private static readonly IBrush Ring = new SolidColorBrush(Color.Parse("#2A2A38"));
    private static readonly IBrush RingLabel = new SolidColorBrush(Color.Parse("#6A6A80"));
    private static readonly IBrush ObjectFill = new SolidColorBrush(Color.Parse("#3A3A4E"));
    private static readonly IBrush ObjectEdge = new SolidColorBrush(Color.Parse("#8FA8C8"));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush KeyDot = new SolidColorBrush(Color.Parse("#4FA3FF"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFCC00")); // colourblind-safe error
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#DCDCDC"));

    public CameraPathDrawWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        Services.WindowService.AttachUiScale(this);

        _canvas = this.FindControl<Canvas>("DrawCanvas");
        if (_canvas != null)
        {
            _canvas.PointerPressed += OnPressed;
            _canvas.PointerMoved += OnMoved;
            _canvas.PointerReleased += OnReleased;
        }
        DataContextChanged += (_, _) => Attach();
    }

    private CameraPathDrawViewModel? Vm => DataContext as CameraPathDrawViewModel;

    private void Attach()
    {
        if (Vm is not { } vm) return;
        vm.PropertyChanged += OnVmChanged;
        vm.CloseRequested += (_, _) => Close();
        Redraw();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CameraPathDrawViewModel.Points):
            case nameof(CameraPathDrawViewModel.Fitted):
            case nameof(CameraPathDrawViewModel.ViewRadius):
            case nameof(CameraPathDrawViewModel.ObjectRadius):
            case nameof(CameraPathDrawViewModel.HasError):
                Redraw();
                break;
        }
    }

    // ── Plan ↔ canvas ──────────────────────────────────────────────────────

    private double Scale => _canvas == null || Vm == null
        ? 1 : (Math.Min(_canvas.Width, _canvas.Height) / 2 - Pad) / Vm.ViewRadius;

    private Point ToCanvas((double X, double Y) p)
        => new(_canvas!.Width / 2 + p.X * Scale, _canvas.Height / 2 - p.Y * Scale);

    private (double X, double Y) ToPlan(Point c)
        => ((c.X - _canvas!.Width / 2) / Scale, (_canvas.Height / 2 - c.Y) / Scale);

    // ── Input ──────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_canvas == null || Vm == null) return;
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        _drawing = true;
        e.Pointer.Capture(_canvas);
        Vm.BeginStroke(ToPlan(e.GetPosition(_canvas)));
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_drawing || _canvas == null || Vm == null) return;
        Vm.ExtendStroke(ToPlan(e.GetPosition(_canvas)));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing || Vm == null) return;
        _drawing = false;
        e.Pointer.Capture(null);
        Vm.EndStroke();
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_canvas == null || Vm is not { } vm) return;
        var kids = _canvas.Children;
        kids.Clear();
        var c = ToCanvas((0, 0));

        // Distance rings (whole units) with labels.
        int rings = (int)Math.Floor(vm.ViewRadius);
        double step = rings > 10 ? 2 : 1;
        for (double r = step; r <= vm.ViewRadius + 1e-9; r += step)
        {
            double px = r * Scale;
            kids.Add(Circle(c, px, null, Ring, 1));
            kids.Add(Label(new Point(c.X + px + 2, c.Y - 14),
                vm.IsRelief ? $"zoom {1.0 / r:0.##}" : r.ToString("0.#", CultureInfo.InvariantCulture), RingLabel));
        }

        // Azimuth spokes: 0° (+X) and 90° (+Y).
        kids.Add(new Line { StartPoint = c, EndPoint = ToCanvas((vm.ViewRadius, 0)), Stroke = Ring, StrokeThickness = 1 });
        kids.Add(new Line { StartPoint = c, EndPoint = ToCanvas((0, vm.ViewRadius)), Stroke = Ring, StrokeThickness = 1 });
        kids.Add(Label(new Point(_canvas.Width - Pad - 18, c.Y + 2), "0°", RingLabel));
        kids.Add(Label(new Point(c.X + 4, Pad - 2), "90°", RingLabel));

        // The object / closest-approach shape.
        kids.Add(Circle(c, vm.ObjectRadius * Scale, ObjectFill, ObjectEdge, 1.5));

        // The drawn stroke.
        if (vm.Points.Count > 1)
        {
            var line = new Polyline { Stroke = Stroke, StrokeThickness = 2 };
            foreach (var p in vm.Points) line.Points.Add(ToCanvas(p));
            kids.Add(line);
        }

        // Fitted keys (azimuth may be unwrapped beyond ±π; cos/sin handle it).
        if (vm.Fitted is { } track)
            for (int i = 0; i < track.Keys.Count; i++)
            {
                var s = track.Keys[i].State;
                var at = ToCanvas((s.Distance * Math.Cos(s.Theta), s.Distance * Math.Sin(s.Theta)));
                kids.Add(Circle(at, i == 0 ? 6 : 4, KeyDot, null, 0));
                kids.Add(Label(new Point(at.X + 6, at.Y - 6), (i + 1).ToString(CultureInfo.InvariantCulture), KeyDot));
            }

        var status = this.FindControl<TextBlock>("StatusText");
        if (status != null) status.Foreground = vm.HasError ? Warn : Normal;
    }

    private static Ellipse Circle(Point centre, double radius, IBrush? fill, IBrush? stroke, double thickness)
    {
        var e = new Ellipse
        {
            Width = radius * 2, Height = radius * 2,
            Fill = fill, Stroke = stroke, StrokeThickness = thickness,
        };
        Canvas.SetLeft(e, centre.X - radius);
        Canvas.SetTop(e, centre.Y - radius);
        return e;
    }

    private static TextBlock Label(Point at, string text, IBrush brush)
    {
        var t = new TextBlock { Text = text, Foreground = brush, FontSize = 10 };
        Canvas.SetLeft(t, at.X);
        Canvas.SetTop(t, at.Y);
        return t;
    }
}
