// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/CameraPathDrawViewModel.cs
//
// #1056 — state for the "Draw camera path" dialog. The view turns mouse drags
// into plan-view points (object at the origin, camera-distance units); this VM
// fits them with the pure CameraPathFit core on every finished stroke / option
// change, exposes the result for the preview + validation line, and hands the
// track back on Apply. No Avalonia types, so it is unit-tested directly.

using System;
using System.Collections.Generic;
using System.Reactive;

using FracturingFog.Render;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class CameraPathDrawViewModel : ReactiveObject
{
    private readonly List<(double X, double Y)> _points = new();

    /// <param name="relief">The shot drives a Relief 3D oblique camera: distance
    /// is 1/zoom (0.2–5), elevation 5–89°, and there is no object to avoid.</param>
    /// <param name="durationSeconds">The shot length — the path's default timing.</param>
    public CameraPathDrawViewModel(bool relief, double durationSeconds)
    {
        IsRelief = relief;
        _durationSeconds = durationSeconds > 0 ? durationSeconds : 5.0;
        if (relief)
        {
            _objectRadius = 1.0 / CameraParamBinding.ReliefMaxZoom;   // closest = max zoom
            MaxDistance = 1.0 / CameraParamBinding.ReliefMinZoom;
            _viewRadius = MaxDistance;
            _startElevationDeg = _endElevationDeg = 45.0;
        }
        else
        {
            _objectRadius = 1.2;
            MaxDistance = 20.0;
            _viewRadius = 5.0;
            _startElevationDeg = _endElevationDeg = 20.0;
        }

        // Enabled state is bound to CanApply in the view (no WhenAnyValue — it
        // needs the ReactiveUI app builder, which unit tests don't run); Apply
        // itself also guards.
        ApplyCommand = ReactiveCommand.Create(Apply);
        ClearCommand = ReactiveCommand.Create(Clear);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, false));
        Refit();
    }

    public bool IsRelief { get; }
    public string Title => IsRelief ? "Draw camera path — Relief 3D" : "Draw camera path";

    /// <summary>Hint shown above the canvas.</summary>
    public string Hint => IsRelief
        ? "Drag around the centre to orbit the terrain (top-down). Further from the centre = zoomed out."
        : "Drag around the object to draw the orbit (top-down). Distance from the centre = camera distance.";

    /// <summary>Farthest allowed distance (validation).</summary>
    public double MaxDistance { get; }

    private double _viewRadius;
    /// <summary>Distance shown at the canvas edge (zoom of the plan view).</summary>
    public double ViewRadius
    {
        get => _viewRadius;
        set => this.RaiseAndSetIfChanged(ref _viewRadius, Math.Clamp(value, 1.0, MaxDistance));
    }

    private double _objectRadius;
    /// <summary>The centre shape's radius — the closest the camera may come.</summary>
    public double ObjectRadius
    {
        get => _objectRadius;
        set { this.RaiseAndSetIfChanged(ref _objectRadius, Math.Clamp(value, 0.05, MaxDistance * 0.9)); Refit(); }
    }

    private double _durationSeconds;
    public double DurationSeconds
    {
        get => _durationSeconds;
        set { this.RaiseAndSetIfChanged(ref _durationSeconds, value); Refit(); }
    }

    private double _startElevationDeg;
    public double StartElevationDeg
    {
        get => _startElevationDeg;
        set { this.RaiseAndSetIfChanged(ref _startElevationDeg, value); Refit(); }
    }

    private double _endElevationDeg;
    public double EndElevationDeg
    {
        get => _endElevationDeg;
        set { this.RaiseAndSetIfChanged(ref _endElevationDeg, value); Refit(); }
    }

    private int _maxKeys = 12;
    public int MaxKeys
    {
        get => _maxKeys;
        set { this.RaiseAndSetIfChanged(ref _maxKeys, Math.Clamp(value, 2, 64)); Refit(); }
    }

    /// <summary>The drawn points (plan view), for the view's polyline.</summary>
    public IReadOnlyList<(double X, double Y)> Points => _points;

    private CameraTrack? _fitted;
    /// <summary>The fitted track for the current drawing, or null.</summary>
    public CameraTrack? Fitted
    {
        get => _fitted;
        private set => this.RaiseAndSetIfChanged(ref _fitted, value);
    }

    private string _status = string.Empty;
    /// <summary>"N keys over T s" when valid, else why the path can't be used.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _hasError;
    /// <summary>The status is a validation error (shown in #FFCC00).</summary>
    public bool HasError
    {
        get => _hasError;
        private set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    private bool _canApply;
    public bool CanApply
    {
        get => _canApply;
        private set => this.RaiseAndSetIfChanged(ref _canApply, value);
    }

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised to close the dialog: true = applied (see <see cref="Result"/>).</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The accepted track after Apply.</summary>
    public CameraTrack? Result { get; private set; }

    /// <summary>Start a new stroke (replaces any previous drawing).</summary>
    public void BeginStroke((double X, double Y) p)
    {
        _points.Clear();
        _points.Add(p);
        this.RaisePropertyChanged(nameof(Points));
    }

    public void ExtendStroke((double X, double Y) p)
    {
        _points.Add(p);
        this.RaisePropertyChanged(nameof(Points));
    }

    /// <summary>Stroke finished: fit it.</summary>
    public void EndStroke() => Refit();

    /// <summary>Replace the drawing wholesale (tests / programmatic).</summary>
    public void SetPoints(IEnumerable<(double X, double Y)> points)
    {
        _points.Clear();
        _points.AddRange(points);
        this.RaisePropertyChanged(nameof(Points));
        Refit();
    }

    private void Clear()
    {
        _points.Clear();
        this.RaisePropertyChanged(nameof(Points));
        Refit();
    }

    private void Refit()
    {
        if (_points.Count == 0)
        {
            Fitted = null;
            Status = IsRelief ? "Draw a path around the centre." : "Draw a path around the object.";
            HasError = false;
            CanApply = false;
            return;
        }

        double lo = IsRelief ? CameraParamBinding.ReliefMinElevationDeg : -89.0;
        double hi = IsRelief ? CameraParamBinding.ReliefMaxElevationDeg : 89.0;
        var res = CameraPathFit.Fit(_points, new CameraPathFitOptions
        {
            DurationSeconds = _durationSeconds,
            StartElevation = Math.Clamp(_startElevationDeg, lo, hi) * Math.PI / 180.0,
            EndElevation = Math.Clamp(_endElevationDeg, lo, hi) * Math.PI / 180.0,
            MinDistance = _objectRadius,
            MaxDistance = MaxDistance,
            MaxKeys = _maxKeys,
        });
        Fitted = res.Track;
        HasError = !res.Ok;
        CanApply = res.Ok;
        Status = res.Ok
            ? $"{res.Track!.Keys.Count} keys over {_durationSeconds:0.##} s — Apply replaces this shot's camera keys."
            : res.Error!;
    }

    private void Apply()
    {
        if (!CanApply || Fitted == null) return;
        Result = Fitted;
        CloseRequested?.Invoke(this, true);
    }
}
