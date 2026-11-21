// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScatterPath.cs (#1993, Math Plane 2D trajectory statement)
//
// Path recording for the scattering families. A scene's Trace is generic over an
// IPathRecorder; the fractal renders with NoPathRecorder (an empty struct, so the
// JIT drops the calls and the render is unchanged), while the Math Plane
// `trajectory` statement passes a ScatterPathRecorder. Both run the same scene code
// (LaunchState, the system's forces and exit rules, ScatteringIntegrator.Trace), so
// the drawn path ends at the gate the fractal shows for that launch. Same idea as
// the billiard ray inspector (#1447), which records BilliardScene.Trace.
//
// RecordingSystem wraps a system and records the state after every accepted step
// (IScatteringSystem.Observe), after forwarding it to the wrapped system.

using System;
using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1993 — receives the state at the launch and after every accepted step.</summary>
public interface IPathRecorder
{
    void Record(ReadOnlySpan<double> state);
}

/// <summary>#1993 — records nothing: the render path.</summary>
public struct NoPathRecorder : IPathRecorder
{
    public readonly void Record(ReadOnlySpan<double> state) { }
}

/// <summary>#1993 — a system that forwards to <see cref="Inner"/> and records each accepted step.</summary>
public struct RecordingSystem<TSys, TRec> : IScatteringSystem
    where TSys : struct, IScatteringSystem
    where TRec : IPathRecorder
{
    public TSys Inner;
    public TRec Recorder;

    public RecordingSystem(TSys inner, TRec recorder) { Inner = inner; Recorder = recorder; }

    public readonly int Dimension => Inner.Dimension;
    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
    public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
    public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);

    public void Observe(ReadOnlySpan<double> y, double h)
    {
        Inner.Observe(y, h);
        Recorder.Record(y);
    }
}

/// <summary>#1993 — keeps the recorded states (first <see cref="Width"/> components each). Past
/// <see cref="MaxStates"/> it keeps every other state and doubles its stride, so a long trace
/// stays bounded while the first and latest states are kept.</summary>
public sealed class ScatterPathRecorder : IPathRecorder
{
    public const int DefaultMaxStates = 20000;

    public int Width { get; }
    public int MaxStates { get; }
    private readonly List<double> _data = new();
    private int _stride = 1, _skip;
    private readonly double[] _last;
    private bool _hasLast, _lastKept;

    public ScatterPathRecorder(int width, int maxStates = DefaultMaxStates)
    {
        Width = Math.Clamp(width, 1, ScatteringIntegrator.MaxDimension);
        MaxStates = Math.Max(16, maxStates);
        _last = new double[Width];
    }

    public int Count => _data.Count / Width;

    /// <summary>Component <paramref name="k"/> of state <paramref name="i"/>.</summary>
    public double this[int i, int k] => _data[i * Width + k];

    public void Record(ReadOnlySpan<double> state)
    {
        for (int k = 0; k < Width; k++) _last[k] = state[k];
        _hasLast = true;
        if (_skip > 0) { _skip--; _lastKept = false; return; }
        for (int k = 0; k < Width; k++) _data.Add(state[k]);
        _lastKept = true;
        _skip = _stride - 1;
        if (Count >= MaxStates)
        {
            // Thin: keep states 0, 2, 4 ...
            int n = Count, w = 0;
            for (int i = 0; i < n; i += 2, w++)
                for (int k = 0; k < Width; k++) _data[w * Width + k] = _data[i * Width + k];
            _data.RemoveRange(w * Width, _data.Count - w * Width);
            _stride *= 2;
            _skip = _stride - 1;
        }
    }

    /// <summary>Append the latest state if thinning skipped it, so the path ends where the trace ended.</summary>
    public void Finish()
    {
        if (_hasLast && !_lastKept)
        {
            for (int k = 0; k < Width; k++) _data.Add(_last[k]);
            _lastKept = true;
        }
    }
}

/// <summary>#1993 — one traced trajectory for the Math Plane: the path in the family's physical
/// plane, the fate (gate) and the recorded states.</summary>
public sealed class ScatterPath
{
    /// <summary>Path vertices in the drawn plane.</summary>
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = Array.Empty<(double, double)>();
    /// <summary>False where the point is not a launch (forbidden region, inside a body).</summary>
    public bool Launched { get; init; }
    /// <summary>The family's gate: the same number its fractal colours by; -1 = bound / undecided.</summary>
    public int Gate { get; init; } = -1;
    /// <summary>A short fate read-out, e.g. "exit up", "escapes", "no exit by t = 200".</summary>
    public string Fate { get; init; } = "";
    /// <summary>Family time when the trace ended.</summary>
    public double Time { get; init; }
    /// <summary>The recorded raw states (family state vectors), launch first.</summary>
    public ScatterPathRecorder? States { get; init; }
}

/// <summary>#1993 — traces a <see cref="MathPlaneObjectKind.Trajectory"/> object with its family's
/// scene (built from <see cref="FractalParameters"/> through <see cref="MathPlaneTrajectory.Apply"/>).</summary>
public static class ScatterPathTracer
{
    public static ScatterPath Trace(MathPlaneObject o, int maxStates = ScatterPathRecorder.DefaultMaxStates)
    {
        var p = new FractalParameters();
        MathPlaneTrajectory.Apply(o, p);
        double a = o.Coords.Count > 0 ? o.Coords[0] : 0, b = o.Coords.Count > 1 ? o.Coords[1] : 0;
        var rec = new ScatterPathRecorder(4, maxStates);
        Span<double> s = stackalloc double[ScatteringIntegrator.MaxDimension];
        switch (MathPlaneTrajectory.FamilyOf(o))
        {
            case MathPlaneTrajectoryFamily.HenonHeiles:
            {
                var scene = HenonHeilesScene.Build(p);
                var r = scene.Trace(a, b, s, ref rec);
                rec.Finish();
                string fate = !r.Valid ? "no launch: V > E here"
                    : r.Exit >= 0 ? "exit " + (r.Exit switch { 0 => "up", 1 => "lower left", _ => "lower right" })
                    : "no exit by t = " + Short(r.Time);
                return Make(rec, (x0, x1, _, _) => (x0, x1), r.Valid, r.Valid ? r.Exit : -1, fate, r.Time);
            }
            case MathPlaneTrajectoryFamily.ThreeBody:
            {
                var scene = ThreeBodyScene.Build(p);
                var r = scene.Trace(a, b, s, ref rec);
                rec.Finish();
                string fate = !r.Valid ? "no launch: 2 Omega < C here"
                    : r.Gate switch
                    {
                        ThreeBodySystem.Primary1Gate => "crashes into m1",
                        ThreeBodySystem.Primary2Gate => "crashes into m2",
                        ThreeBodySystem.EscapeGate => "escapes",
                        _ => "no fate by t = " + Short(r.Time),
                    };
                return Make(rec, (x0, x1, _, _) => (x0, x1), r.Valid, r.Valid ? r.Gate : -1, fate, r.Time);
            }
            default:
            {
                var scene = MagnetisedBhScene.Build(p);
                var r = scene.Trace(a, b, s, ref rec);
                rec.Finish();
                string fate = !r.Valid ? (r.InsideHole ? "no launch: inside the hole" : "no launch: U > E^2 here")
                    : r.Gate switch
                    {
                        MagnetisedBhSystem.CapturedGate => "captured",
                        MagnetisedBhSystem.UpstreamGate => "escapes up",
                        MagnetisedBhSystem.DownstreamGate => "escapes down",
                        _ => "no fate by s = " + Short(r.Time),
                    };
                // (rho, theta) -> meridional (R, z); the mirrored half when the launch R < 0.
                double side = scene.View == MagnetisedBhView.Meridional && a < 0 ? -1.0 : 1.0;
                return Make(rec, (rho, th, _, _) => (side * rho * Math.Sin(th), rho * Math.Cos(th)),
                            r.Valid, r.Valid ? r.Gate : -1, fate, r.Time);
            }
        }
    }

    private static ScatterPath Make(ScatterPathRecorder rec, Func<double, double, double, double, (double, double)> project,
                                    bool launched, int gate, string fate, double time)
    {
        var pts = new List<(double X, double Y)>(rec.Count);
        for (int i = 0; i < rec.Count; i++) pts.Add(project(rec[i, 0], rec[i, 1], rec[i, 2], rec[i, 3]));
        return new ScatterPath { Points = pts, Launched = launched, Gate = gate, Fate = fate, Time = time, States = rec };
    }

    private static string Short(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
