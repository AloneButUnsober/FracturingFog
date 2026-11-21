// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// HenonHeiles.cs (#1942, gravitational-scattering set §2.6)
//
// The Hénon-Heiles potential, the classic open Hamiltonian from galactic dynamics
// (Hénon & Heiles 1964, see Docs/Resources-Bibliography.md):
//
//     V(x, y) = (x² + y²)/2 + x²y − y³/3        H = (px² + py²)/2 + V
//
// In polar form V = r²/2 + (r³/3)·sin 3θ, so V has the symmetry of an equilateral
// triangle (C3v). Its three saddle points sit at distance 1 from the origin, 120°
// apart (θ = 90°, 210°, 330°), all at energy 1/6. Above the escape energy E = 1/6 a
// particle can leave the well through any of the three channels past the saddles.
// Colouring each launch condition by the exit it takes gives the exit basins, which
// Aguirre, Vallejo & Sanjuán (2001) showed numerically to be fractal and Wada: every
// boundary point borders all three basins.
//
// The equations of motion are traced with the shared Dormand-Prince core
// (ScatteringIntegrator, #1923); this file supplies only the forces, the exit rule
// and the per-trajectory channels.

using System;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1942 — Hénon-Heiles equations of motion and exit test, for
/// <see cref="ScatteringIntegrator"/>. State = (x, y, px, py), time t.</summary>
public struct HenonHeilesSystem : IScatteringSystem
{
    /// <summary>Exit channels (the three saddles).</summary>
    public const int ExitCount = 3;

    /// <summary>Escape energy: the potential at the saddles.</summary>
    public const double EscapeEnergy = 1.0 / 6.0;

    /// <summary>A trajectory has left through a channel once it is this far from the origin.
    /// The saddles are at r = 1; past r = 2 the open part of each channel is a narrow wedge
    /// around its axis (within about ±27° at E = 0.25) where the potential falls outward, so the
    /// channel is unambiguous and the particle does not come back.</summary>
    public const double ExitRadius = 2.0;

    /// <summary>Largest step (time units). The motion is smooth, so this only keeps the
    /// controller from stepping past the exit circle by much.</summary>
    public const double StepCap = 0.25;

    private static readonly double Sqrt3Half = Math.Sqrt(3.0) / 2.0;

    /// <summary>Time limit: a trajectory still inside at this time is undecided.</summary>
    public double TimeCap;
    /// <summary>Time travelled so far.</summary>
    public double Time;
    /// <summary>Turning points: sign changes of the radial velocity x·px + y·py.</summary>
    public int Turns;
    /// <summary>Smallest distance to any saddle point seen so far (launch included).</summary>
    public double MinSaddleDistance;
    private double _radial;

    public readonly int Dimension => 4;

    /// <summary>The potential V(x, y).</summary>
    public static double Potential(double x, double y) => 0.5 * (x * x + y * y) + x * x * y - y * y * y / 3.0;

    /// <summary>The energy H of state (x, y, px, py).</summary>
    public static double Energy(ReadOnlySpan<double> s) => 0.5 * (s[2] * s[2] + s[3] * s[3]) + Potential(s[0], s[1]);

    /// <summary>A system ready to trace from <paramref name="launch"/> (channels reset).</summary>
    public static HenonHeilesSystem Begin(ReadOnlySpan<double> launch, double timeCap)
    {
        var sys = new HenonHeilesSystem { TimeCap = timeCap, MinSaddleDistance = SaddleDistance(launch[0], launch[1]) };
        sys._radial = launch[0] * launch[2] + launch[1] * launch[3];
        return sys;
    }

    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy)
    {
        double x = y[0], v = y[1];
        dy[0] = y[2];
        dy[1] = y[3];
        dy[2] = -x - 2.0 * x * v;          // -dV/dx
        dy[3] = -v - x * x + v * v;        // -dV/dy
    }

    public readonly ScatterFate Classify(ReadOnlySpan<double> y, out int body)
    {
        body = -1;
        if (y[0] * y[0] + y[1] * y[1] > ExitRadius * ExitRadius) return ScatterFate.Escaped;
        return Time >= TimeCap ? ScatterFate.Undecided : ScatterFate.Running;
    }

    public readonly double MaxStep(ReadOnlySpan<double> y) => StepCap;

    public void Observe(ReadOnlySpan<double> y, double h)
    {
        Time += h;
        double radial = y[0] * y[2] + y[1] * y[3];
        if ((radial < 0) != (_radial < 0) && radial != 0 && _radial != 0) Turns++;
        if (radial != 0) _radial = radial;
        double d = SaddleDistance(y[0], y[1]);
        if (d < MinSaddleDistance) MinSaddleDistance = d;
    }

    /// <summary>Exit channel of a point past the exit circle: 0 = up (θ = 90°), 1 = lower left
    /// (210°), 2 = lower right (330°). A rotation by +120° maps channel k to k + 1 (mod 3).</summary>
    public static int ExitOf(double x, double y)
    {
        double t = (Math.Atan2(y, x) - Math.PI / 2.0) / (2.0 * Math.PI / 3.0);
        int k = (int)Math.Round(t);
        return ((k % 3) + 3) % 3;
    }

    /// <summary>Saddle point <paramref name="k"/> (same order as <see cref="ExitOf"/>).</summary>
    public static (double X, double Y) Saddle(int k) => k switch
    {
        0 => (0.0, 1.0),
        1 => (-Sqrt3Half, -0.5),
        _ => (Sqrt3Half, -0.5),
    };

    /// <summary>Distance from (x, y) to the nearest saddle point.</summary>
    public static double SaddleDistance(double x, double y)
    {
        double best = double.MaxValue;
        for (int k = 0; k < ExitCount; k++)
        {
            var (sx, sy) = Saddle(k);
            double d = (x - sx) * (x - sx) + (y - sy) * (y - sy);
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }
}

/// <summary>#1942 — how one launch ended.</summary>
public readonly struct HenonHeilesOutcome
{
    /// <summary>False when the launch point is not allowed at this energy (V &gt; E, or the
    /// section's momentum would be imaginary).</summary>
    public bool Valid { get; init; }
    /// <summary>Exit channel 0..2, or -1 = undecided (still inside at the time limit).</summary>
    public int Exit { get; init; }
    public double Time { get; init; }
    public int Turns { get; init; }
    public double MinSaddleDistance { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1942 — one Hénon-Heiles exit-basin scene: energy, launch rule and integrator
/// settings, built from <see cref="FractalParameters"/>.</summary>
public sealed class HenonHeilesScene
{
    public double Energy { get; }
    public HenonHeilesView View { get; }
    public HenonHeilesLaunch Launch { get; }
    /// <summary>FixedAngle launch direction, radians.</summary>
    public double LaunchAngle { get; }
    public double TimeCap { get; }
    public ScatterSettings Settings { get; }

    public HenonHeilesScene(double energy, HenonHeilesView view, HenonHeilesLaunch launch, double launchAngleDeg,
                            double timeCap, double tolerance)
    {
        Energy = double.IsFinite(energy) ? Math.Clamp(energy, 0.0, 10.0) : 0.25;
        View = view;
        Launch = launch;
        LaunchAngle = (double.IsFinite(launchAngleDeg) ? launchAngleDeg : 0.0) * Math.PI / 180.0;
        TimeCap = double.IsFinite(timeCap) ? Math.Clamp(timeCap, 1.0, 100000.0) : 200.0;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-8;
        // The time limit ends a trajectory; the step cap is only a backstop (8x attempts).
        int steps = (int)Math.Min(int.MaxValue / 16, TimeCap / 1e-3);
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = steps, InitialStep = 0.05 };
    }

    public static HenonHeilesScene Build(FractalParameters p)
        => new(p.HenonHeilesEnergy, p.HenonHeilesView, p.HenonHeilesLaunch, p.HenonHeilesLaunchAngle,
               p.HenonHeilesMaxTime, p.HenonHeilesTolerance);

    /// <summary>The launch state for view point (a, b) (b up), or false where the point is not
    /// an allowed launch at this energy. Plane: (a, b) = (x, y), speed from the energy, direction
    /// from the launch rule. Section: (a, b) = (y, py) on the line x = 0, px ≥ 0 from the energy
    /// (the usual Poincaré-section launch).</summary>
    public bool LaunchState(double a, double b, Span<double> s)
    {
        if (View == HenonHeilesView.Section)
        {
            double k = Energy - HenonHeilesSystem.Potential(0.0, a) - 0.5 * b * b;
            if (!(k >= 0)) return false;
            s[0] = 0.0; s[1] = a; s[2] = Math.Sqrt(2.0 * k); s[3] = b;
            return true;
        }
        double kin = Energy - HenonHeilesSystem.Potential(a, b);
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
            default:   // Tangential, anticlockwise
                if (r > 0) { dx = -b / r; dy = a / r; } else { dx = 1.0; dy = 0.0; }
                break;
        }
        s[0] = a; s[1] = b; s[2] = speed * dx; s[3] = speed * dy;
        return true;
    }

    /// <summary>Trace view point (a, b). <paramref name="s"/> (length ≥ 4) ends as the final state.</summary>
    public HenonHeilesOutcome Trace(double a, double b, Span<double> s)
    {
        if (!LaunchState(a, b, s)) return new HenonHeilesOutcome { Valid = false, Exit = -1 };
        var sys = HenonHeilesSystem.Begin(s, TimeCap);
        int exit;
        int steps = 0;
        if (s[0] * s[0] + s[1] * s[1] > HenonHeilesSystem.ExitRadius * HenonHeilesSystem.ExitRadius)
        {
            exit = HenonHeilesSystem.ExitOf(s[0], s[1]);   // launched outside: already gone
        }
        else
        {
            var settings = Settings;
            var o = ScatteringIntegrator.Trace(ref sys, s, in settings);
            steps = o.Steps;
            exit = o.Fate == ScatterFate.Escaped ? HenonHeilesSystem.ExitOf(s[0], s[1]) : -1;
        }
        return new HenonHeilesOutcome
        {
            Valid = true, Exit = exit, Time = sys.Time, Turns = sys.Turns,
            MinSaddleDistance = sys.MinSaddleDistance, Steps = steps,
        };
    }
}
