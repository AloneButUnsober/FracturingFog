// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserPotential.cs (#1994, User Potential)
//
// A user-written 2D potential V(x, y) traced as a Hamiltonian scattering problem, the
// general form of the Hénon–Heiles family (#1942):
//
//     H = (vx² + vy²)/2 + V(x, y),   dx/dt = vx, dvx/dt = −∂V/∂x (+ 2 vy),
//                                    dy/dt = vy, dvy/dt = −∂V/∂y (− 2 vx),
//
// the bracketed Coriolis terms only in the rotating frame (unit angular velocity, the
// restricted three-body convention of #1943, where V = −Ω). Coriolis forces do no work, so
// E = v²/2 + V is conserved in both frames (in the rotating frame it is −C/2, C the Jacobi
// constant).
//
// The forces come from SYMBOLIC partial derivatives of the user's V (RealExpression, the
// equation language's syntax), compiled to delegates once per source text. Exits: past the
// exit radius, the particle leaves through angular sector k (N equal sectors, sector 0
// centred on the offset angle, counted anticlockwise; Hénon–Heiles is N = 3 at 90°); or an
// optional "exit if" expression in x, y, vx, vy, t returns a gate index (>= 0) to stop at,
// negative to keep going (the Converge-if precedent of the escape-time DSL).
//
// Traced on the shared Dormand–Prince core (ScatteringIntegrator, #1923); launch rules as
// #1942 (Plane: pixel = position, speed from E; Section: pixel = (y, vy) on x = 0, vx >= 0).

using System;
using System.Collections.Concurrent;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1994 — the compiled potential, forces and exit rule for one pair of source texts.</summary>
public sealed class UserPotentialModel
{
    public static readonly string[] PotentialVariables = { "x", "y" };
    public static readonly string[] ExitVariables = { "x", "y", "vx", "vy", "t" };

    public RealExpression Potential { get; }
    public RealExpression ForceX { get; }   // −∂V/∂x as dV/dx (sign applied in the system)
    public RealExpression ForceY { get; }
    public RealExpression? ExitIf { get; }
    public string? Error { get; }

    internal readonly Func<double[], double> V, Vx, Vy;
    internal readonly Func<double[], double>? Exit;

    private UserPotentialModel(RealExpression v, RealExpression? exit)
    {
        Potential = v;
        ForceX = v.Derivative(0);
        ForceY = v.Derivative(1);
        ExitIf = exit;
        V = v.Compile();
        Vx = ForceX.Compile();
        Vy = ForceY.Compile();
        Exit = exit?.Compile();
    }

    private UserPotentialModel(string error)
    {
        Error = error;
        Potential = ForceX = ForceY = null!;
        V = Vx = Vy = null!;
    }

    private static readonly ConcurrentDictionary<(string, string), UserPotentialModel> s_cache = new();

    /// <summary>The model for these sources (cached); <see cref="Error"/> is set when one does not parse.</summary>
    public static UserPotentialModel For(string? potential, string? exitIf)
    {
        var key = (potential ?? "", exitIf ?? "");
        if (s_cache.TryGetValue(key, out var m)) return m;
        if (s_cache.Count > 64) s_cache.Clear();
        return s_cache[key] = Build(potential, exitIf);
    }

    private static UserPotentialModel Build(string? potential, string? exitIf)
    {
        if (!RealExpression.TryParse(potential, PotentialVariables, out var v, out var err))
            return new UserPotentialModel("Potential V(x, y): " + err);
        RealExpression? exit = null;
        if (!string.IsNullOrWhiteSpace(exitIf) && !RealExpression.TryParse(exitIf, ExitVariables, out exit, out err))
            return new UserPotentialModel("Exit if: " + err);
        try { return new UserPotentialModel(v!, exit); }
        catch (Exception ex) { return new UserPotentialModel("Could not compile: " + ex.Message); }
    }

    /// <summary>V at (x, y) (thread-safe: allocates a small array; not for the step loop).</summary>
    public double PotentialAt(double x, double y) => V(new[] { x, y });
}

/// <summary>#1994 — the equations of motion and exit rule, for <see cref="ScatteringIntegrator"/>.
/// State = (x, y, vx, vy).</summary>
public struct UserPotentialSystem : IScatteringSystem
{
    public UserPotentialModel Model;
    public bool Rotating;
    public double ExitRadius;
    public int Sectors;
    public double SectorOffset;   // radians
    public double TimeCap;
    public double StepCap;
    public double[] Scratch;      // length >= 5, per thread

    public double Time;
    public int Gate;
    public int Turns;
    private double _radial;

    public readonly int Dimension => 4;

    public void Begin(ReadOnlySpan<double> launch)
    {
        Time = 0; Gate = -1; Turns = 0;
        _radial = launch[0] * launch[2] + launch[1] * launch[3];
    }

    public readonly void Derivative(ReadOnlySpan<double> s, Span<double> ds)
    {
        var a = Scratch;
        a[0] = s[0]; a[1] = s[1];
        double fx = -Model.Vx(a), fy = -Model.Vy(a);
        ds[0] = s[2];
        ds[1] = s[3];
        if (Rotating) { fx += 2.0 * s[3]; fy -= 2.0 * s[2]; }
        ds[2] = fx;
        ds[3] = fy;
    }

    public ScatterFate Classify(ReadOnlySpan<double> s, out int body)
    {
        body = -1;
        if (Model.Exit is { } exit)
        {
            var a = Scratch;
            a[0] = s[0]; a[1] = s[1]; a[2] = s[2]; a[3] = s[3]; a[4] = Time;
            double g = exit(a);
            if (g >= 0 && double.IsFinite(g)) { Gate = (int)Math.Min(Math.Floor(g), 15); return ScatterFate.Escaped; }
        }
        if (s[0] * s[0] + s[1] * s[1] > ExitRadius * ExitRadius)
        {
            Gate = SectorOf(s[0], s[1], Sectors, SectorOffset);
            return ScatterFate.Escaped;
        }
        if (!double.IsFinite(s[0]) || !double.IsFinite(s[1])) { Gate = -1; return ScatterFate.Undecided; }
        return Time >= TimeCap ? ScatterFate.Undecided : ScatterFate.Running;
    }

    public readonly double MaxStep(ReadOnlySpan<double> s) => StepCap;

    public void Observe(ReadOnlySpan<double> s, double h)
    {
        Time += h;
        double radial = s[0] * s[2] + s[1] * s[3];
        if ((radial < 0) != (_radial < 0) && radial != 0 && _radial != 0) Turns++;
        if (radial != 0) _radial = radial;
    }

    /// <summary>The exit sector of (x, y): N equal sectors, sector 0 centred on
    /// <paramref name="offset"/> (radians), numbered anticlockwise. With N = 3 and 90° this is
    /// the Hénon–Heiles channel numbering (HenonHeilesSystem.ExitOf).</summary>
    public static int SectorOf(double x, double y, int n, double offset)
    {
        if (n <= 1) return 0;
        double t = (Math.Atan2(y, x) - offset) / (2.0 * Math.PI / n);
        int k = (int)Math.Round(t);
        return ((k % n) + n) % n;
    }
}

/// <summary>#1994 — the outcome of one launch.</summary>
public readonly struct UserPotentialOutcome
{
    public bool Valid { get; init; }
    public int Gate { get; init; }
    public double Time { get; init; }
    public int Turns { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1994 — one User Potential scene, built from <see cref="FractalParameters"/>.</summary>
public sealed class UserPotentialScene
{
    public UserPotentialModel Model { get; }
    public double Energy { get; }
    public UserPotentialView View { get; }
    public HenonHeilesLaunch Launch { get; }
    public double LaunchAngle { get; }
    public double ExitRadius { get; }
    public int Sectors { get; }
    public double SectorOffset { get; }
    public bool Rotating { get; }
    public double TimeCap { get; }
    public double StepCap { get; }
    public ScatterSettings Settings { get; }

    [ThreadStatic] private static double[]? t_scratch;

    public UserPotentialScene(UserPotentialModel model, double energy, UserPotentialView view, HenonHeilesLaunch launch,
                              double launchAngleDeg, double exitRadius, int sectors, double sectorOffsetDeg, bool rotating,
                              double timeCap, double tolerance, double stepCap)
    {
        Model = model;
        Energy = double.IsFinite(energy) ? Math.Clamp(energy, -1e6, 1e6) : 0.25;
        View = view;
        Launch = launch;
        LaunchAngle = (double.IsFinite(launchAngleDeg) ? launchAngleDeg : 0.0) * Math.PI / 180.0;
        ExitRadius = double.IsFinite(exitRadius) ? Math.Clamp(exitRadius, 1e-3, 1e6) : 2.0;
        Sectors = Math.Clamp(sectors, 1, 16);
        SectorOffset = (double.IsFinite(sectorOffsetDeg) ? sectorOffsetDeg : 90.0) * Math.PI / 180.0;
        Rotating = rotating;
        TimeCap = double.IsFinite(timeCap) ? Math.Clamp(timeCap, 1.0, 100000.0) : 200.0;
        StepCap = double.IsFinite(stepCap) ? Math.Clamp(stepCap, 1e-4, 10.0) : 0.25;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-8;
        int steps = (int)Math.Min(int.MaxValue / 16, TimeCap / 1e-3);
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = steps, InitialStep = 0.05 };
    }

    public static UserPotentialScene Build(FractalParameters p)
        => new(UserPotentialModel.For(p.UserPotentialSource, p.UserPotentialExitIf), p.UserPotentialEnergy, p.UserPotentialView,
               p.UserPotentialLaunch, p.UserPotentialLaunchAngle, p.UserPotentialExitRadius, p.UserPotentialSectors,
               p.UserPotentialSectorOffset, p.UserPotentialRotating, p.UserPotentialMaxTime, p.UserPotentialTolerance,
               p.UserPotentialMaxStep);

    private static double[] Scratch() => t_scratch ??= new double[8];

    /// <summary>The launch state at view point (a, b), or false where it is not allowed (V &gt; E).</summary>
    public bool LaunchState(double a, double b, Span<double> s)
    {
        if (Model.Error != null) return false;
        var sc = Scratch();
        if (View == UserPotentialView.Section)
        {
            sc[0] = 0; sc[1] = a;
            double k = Energy - Model.V(sc) - 0.5 * b * b;
            if (!(k >= 0)) return false;
            s[0] = 0.0; s[1] = a; s[2] = Math.Sqrt(2.0 * k); s[3] = b;
            return true;
        }
        sc[0] = a; sc[1] = b;
        double kin = Energy - Model.V(sc);
        if (!(kin >= 0)) return false;
        double speed = Math.Sqrt(2.0 * kin);
        double r = Math.Sqrt(a * a + b * b);
        double dx, dy;
        switch (Launch)
        {
            case HenonHeilesLaunch.Radial:
                if (r > 0) { dx = a / r; dy = b / r; } else { dx = 1.0; dy = 0.0; }
                break;
            case HenonHeilesLaunch.FixedAngle:
                dx = Math.Cos(LaunchAngle); dy = Math.Sin(LaunchAngle);
                break;
            default:
                if (r > 0) { dx = -b / r; dy = a / r; } else { dx = 1.0; dy = 0.0; }
                break;
        }
        s[0] = a; s[1] = b; s[2] = speed * dx; s[3] = speed * dy;
        return true;
    }

    public UserPotentialOutcome Trace(double a, double b, Span<double> s)
    {
        var none = default(NoPathRecorder);
        return Trace(a, b, s, ref none);
    }

    /// <summary>Trace view point (a, b), recording every state (see ScatterPath.cs).</summary>
    public UserPotentialOutcome Trace<TRec>(double a, double b, Span<double> s, ref TRec rec) where TRec : IPathRecorder
    {
        if (!LaunchState(a, b, s)) return new UserPotentialOutcome { Valid = false, Gate = -1 };
        rec.Record(s);
        var sys = new UserPotentialSystem
        {
            Model = Model, Rotating = Rotating, ExitRadius = ExitRadius, Sectors = Sectors, SectorOffset = SectorOffset,
            TimeCap = TimeCap, StepCap = StepCap, Scratch = Scratch(),
        };
        sys.Begin(s);
        // A launch already outside (or already meeting the exit rule) is decided at once.
        var first = sys.Classify(s, out _);
        if (first != ScatterFate.Running)
            return new UserPotentialOutcome { Valid = true, Gate = first == ScatterFate.Escaped ? sys.Gate : -1 };
        var settings = Settings;
        var wrapped = new RecordingSystem<UserPotentialSystem, TRec>(sys, rec);
        var o = ScatteringIntegrator.Trace(ref wrapped, s, in settings);
        sys = wrapped.Inner; rec = wrapped.Recorder;
        int gate = o.Fate == ScatterFate.Escaped ? sys.Gate : -1;
        return new UserPotentialOutcome { Valid = true, Gate = gate, Time = sys.Time, Turns = sys.Turns, Steps = o.Steps };
    }
}
