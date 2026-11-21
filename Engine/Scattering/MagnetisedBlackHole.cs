// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MagnetisedBlackHole.cs (#1944, gravitational-scattering set §2.6)
//
// A charged test particle near a Schwarzschild black hole in an asymptotically uniform
// magnetic field: Wald's test-field solution, A = (B/2) ξ_(φ) with ξ_(φ) the axial Killing
// vector (Wald 1974). The units and notation follow Bautista & Vega (Class. Quantum Grav.
// 38, 155016, 2021), who mapped the exit basins of this system: lengths and proper time in
// units of the horizon radius r_g = 2M (so the horizon is at ρ = 1, f = 1 − 1/ρ), specific
// energy ℰ, specific canonical axial angular momentum ℓ, and the single interaction strength
// b = qB r_g / (2m) (the charge and the field enter only as this product, so "q = 0" and
// "B = 0" are the same limit). Their effective potential (eq. 3.7) is
//
//     U(ρ, θ) = f · [1 + (ℓ / (ρ sin θ) − b ρ sin θ)²]
//
// and ℰ² = (dρ/dσ)² + ρ(ρ − 1)(dθ/dσ)² + U (their eq. 3.6). FF integrates the equivalent
// Hamiltonian in (ρ, θ, p_ρ, p_θ),
//
//     H = ½ [ f p_ρ² + p_θ²/ρ² − ℰ²/f + w² ],   w = ℓ/(ρ sin θ) − b ρ sin θ,   H = −½,
//
// with dρ/dσ = f p_ρ and dθ/dσ = p_θ/ρ² (so f p_ρ² ... reproduces eq. 3.6 after multiplying by
// f). Writing the field term through w keeps it finite on the axis when ℓ = 0.
//
// Fates (Bautista & Vega's three exits): captured (ρ falls to the capture radius just outside
// the horizon), escaped upstream (z = ρ cos θ → +∞) or downstream (z → −∞) along the field,
// judged when |z| passes the escape height; otherwise undecided at the time limit.

using System;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1944 — charged-particle motion in the Wald field round a Schwarzschild hole (units
/// r_g = 2M = 1), for <see cref="ScatteringIntegrator"/>. State = (ρ, θ, p_ρ, p_θ).</summary>
public struct MagnetisedBhSystem : IScatteringSystem
{
    /// <summary>Gates: captured, escaped upstream (z → +∞), escaped downstream (z → −∞).</summary>
    public const int CapturedGate = 0, UpstreamGate = 1, DownstreamGate = 2, GateCount = 3;

    /// <summary>Captured once ρ falls below this (the horizon is ρ = 1). Close to the horizon
    /// U ∝ f vanishes, so an infalling particle cannot turn round there unless the field term
    /// exceeds ℰ²/f − 1 (about 99 ℰ² at this radius).</summary>
    public const double CaptureRadius = 1.01;

    public double Energy, AngularMomentum, Field;
    public double EscapeHeight, TimeCap;
    /// <summary>Proper time σ travelled so far.</summary>
    public double Time;
    public int Gate;

    public readonly int Dimension => 4;

    /// <summary>w = ℓ/(ρ sin θ) − b ρ sin θ (the field term of U is w²).</summary>
    public static double W(double l, double b, double rho, double s) => l / (rho * s) - b * rho * s;

    /// <summary>Bautista &amp; Vega's effective potential U(ρ, θ).</summary>
    public static double EffectivePotential(double l, double b, double rho, double theta)
    {
        double w = W(l, b, rho, Math.Sin(theta));
        return (1.0 - 1.0 / rho) * (1.0 + w * w);
    }

    /// <summary>The Hamiltonian of state (ρ, θ, p_ρ, p_θ); −½ on a physical trajectory.</summary>
    public static double Hamiltonian(double e, double l, double b, ReadOnlySpan<double> s)
    {
        double rho = s[0], f = 1.0 - 1.0 / rho;
        double w = W(l, b, rho, Math.Sin(s[1]));
        return 0.5 * (f * s[2] * s[2] + s[3] * s[3] / (rho * rho) - e * e / f + w * w);
    }

    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy)
    {
        double rho = y[0], th = y[1], pr = y[2], pt = y[3];
        double s = Math.Sin(th), c = Math.Cos(th);
        double inv = 1.0 / rho, f = 1.0 - inv;
        double l = AngularMomentum, b = Field;
        double w = l / (rho * s) - b * rho * s;
        dy[0] = f * pr;
        dy[1] = pt * inv * inv;
        // -dH/dρ: f' = 1/ρ²; ∂(½w²)/∂ρ = w (−ℓ/(ρ² s) − b s).
        double dHdr = 0.5 * pr * pr * inv * inv - pt * pt * inv * inv * inv
                    + 0.5 * Energy * Energy * inv * inv / (f * f)
                    + w * (-l / (rho * rho * s) - b * s);
        // -dH/dθ: ∂(½w²)/∂θ = w (−ℓ c/(ρ s²) − b ρ c).
        double dHdt = w * (-l * c / (rho * s * s) - b * rho * c);
        dy[2] = -dHdr;
        dy[3] = -dHdt;
    }

    public ScatterFate Classify(ReadOnlySpan<double> y, out int body)
    {
        body = -1;
        double rho = y[0];
        if (rho < CaptureRadius) { Gate = CapturedGate; return ScatterFate.Captured; }
        double z = rho * Math.Cos(y[1]);
        if (z > EscapeHeight) { Gate = UpstreamGate; return ScatterFate.Escaped; }
        if (z < -EscapeHeight) { Gate = DownstreamGate; return ScatterFate.Escaped; }
        if (Time >= TimeCap) { Gate = -1; return ScatterFate.Undecided; }
        return ScatterFate.Running;
    }

    /// <summary>Step cap: a fraction of the distance to the horizon near it, growing with ρ far
    /// out (the motion there is a slow drift along the field plus gyration).</summary>
    public readonly double MaxStep(ReadOnlySpan<double> y) => Math.Clamp(0.25 * (y[0] - 1.0), 1e-6, 5.0);

    public void Observe(ReadOnlySpan<double> y, double h) => Time += h;

    /// <summary>Specific angular momentum ℓ± of a circular equatorial orbit at ρ0 (Bautista &amp; Vega
    /// eq. 4.2), or NaN where none exists. +1 / −1 picks the branch.</summary>
    public static double CircularAngularMomentum(double b, double rho0, int branch)
    {
        double d = 2.0 * rho0 - 3.0;
        if (Math.Abs(d) < 1e-12) return double.NaN;
        double disc = b * b * rho0 * rho0 - (3.0 - 2.0 * rho0) * (b * b * rho0 * rho0 * (2.0 * rho0 - 1.0) + 1.0);
        if (!(disc >= 0)) return double.NaN;
        return rho0 / d * (-b * rho0 + (branch >= 0 ? 1.0 : -1.0) * Math.Sqrt(disc));
    }
}

/// <summary>#1944 — how one launch ended.</summary>
public readonly struct MagnetisedBhOutcome
{
    /// <summary>False where the launch does not exist (no circular orbit, or ℰ below the
    /// potential at the launch point).</summary>
    public bool Valid { get; init; }
    /// <summary>The view point is inside the capture radius (the hole itself): Kick ρ0, or
    /// Meridional ρ, below <see cref="MagnetisedBhSystem.CaptureRadius"/>. Not a launch.</summary>
    public bool InsideHole { get; init; }
    /// <summary><see cref="MagnetisedBhSystem.CapturedGate"/>, <see cref="MagnetisedBhSystem.UpstreamGate"/>,
    /// <see cref="MagnetisedBhSystem.DownstreamGate"/>, or −1 undecided.</summary>
    public int Gate { get; init; }
    public double Time { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1944 — one magnetised-black-hole scene built from <see cref="FractalParameters"/>.</summary>
public sealed class MagnetisedBhScene
{
    public MagnetisedBhView View { get; }
    public double Field { get; }
    public int Branch { get; }
    public double Energy { get; }
    public double AngularMomentum { get; }
    /// <summary>Meridional launch direction, radians from +R towards +z.</summary>
    public double LaunchAngle { get; }
    public double EscapeHeight { get; }
    public double TimeCap { get; }
    public ScatterSettings Settings { get; }

    public MagnetisedBhScene(MagnetisedBhView view, double field, MagnetisedBhBranch branch, double energy, double angularMomentum,
                             double launchAngleDeg, double escapeHeight, double timeCap, double tolerance)
    {
        View = view;
        Field = double.IsFinite(field) ? Math.Clamp(field, -10.0, 10.0) : 0.3;
        Branch = branch == MagnetisedBhBranch.Minus ? -1 : 1;
        Energy = double.IsFinite(energy) ? Math.Clamp(energy, 0.0, 100.0) : 1.5;
        AngularMomentum = double.IsFinite(angularMomentum) ? Math.Clamp(angularMomentum, -100.0, 100.0) : 0.0;
        LaunchAngle = (double.IsFinite(launchAngleDeg) ? launchAngleDeg : 0.0) * Math.PI / 180.0;
        EscapeHeight = double.IsFinite(escapeHeight) ? Math.Clamp(escapeHeight, 5.0, 100000.0) : 100.0;
        TimeCap = double.IsFinite(timeCap) ? Math.Clamp(timeCap, 1.0, 1e6) : 1000.0;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-9;
        int steps = (int)Math.Min(int.MaxValue / 16, TimeCap / 1e-4);
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = steps, InitialStep = 0.01 };
    }

    public static MagnetisedBhScene Build(FractalParameters p)
        => new(p.MagBhView, p.MagBhField, p.MagBhBranch, p.MagBhEnergy, p.MagBhAngularMomentum,
               p.MagBhLaunchAngle, p.MagBhEscapeHeight, p.MagBhMaxTime, p.MagBhTolerance);

    /// <summary>The launch for view point (a, c) (c up). Returns ℓ and ℰ through out parameters.
    /// Kick: (a, c) = (ρ0, ℰ); the particle sits on the circular equatorial orbit at ρ0 (branch
    /// ℓ±) and is kicked straight up (+z) to energy ℰ, Bautista &amp; Vega's launch.
    /// Meridional: (a, c) = (R, z) with R = |a| (the plane is drawn mirrored about the axis);
    /// ℰ and ℓ fixed; direction at <see cref="LaunchAngle"/> from +R towards +z.</summary>
    public bool LaunchState(double a, double c, Span<double> s, out double energy, out double l)
    {
        double b = Field;
        if (View == MagnetisedBhView.Kick)
        {
            double rho0 = a;
            energy = c; l = double.NaN;
            if (!(rho0 > MagnetisedBhSystem.CaptureRadius) || !(energy >= 0)) return false;
            l = MagnetisedBhSystem.CircularAngularMomentum(b, rho0, Branch);
            if (!double.IsFinite(l)) return false;
            double u = MagnetisedBhSystem.EffectivePotential(l, b, rho0, Math.PI / 2);
            double k = energy * energy - u;
            if (!(k >= 0)) return false;
            double thetaDot = Math.Sqrt(k / (rho0 * (rho0 - 1.0)));
            s[0] = rho0; s[1] = Math.PI / 2; s[2] = 0.0;
            s[3] = -rho0 * rho0 * thetaDot;   // upward: z = ρ cos θ grows as θ falls
            return true;
        }
        energy = Energy; l = AngularMomentum;
        double R = Math.Abs(a), z = c;
        double rho = Math.Sqrt(R * R + z * z);
        if (!(rho > MagnetisedBhSystem.CaptureRadius) || R < 1e-12) return false;
        double theta = Math.Atan2(R, z);
        double f = 1.0 - 1.0 / rho;
        double kin = energy * energy - MagnetisedBhSystem.EffectivePotential(l, b, rho, theta);
        if (!(kin >= 0)) return false;
        // Local orthonormal speeds: ℰ² − U = (dρ/dσ)² + ρ(ρ − 1)(dθ/dσ)² = v_ρ² + v_θ².
        double speed = Math.Sqrt(kin);
        double dR = Math.Cos(LaunchAngle), dZ = Math.Sin(LaunchAngle);
        // (R̂, ẑ) → (ρ̂, θ̂): ρ̂ = sinθ R̂ + cosθ ẑ, θ̂ = cosθ R̂ − sinθ ẑ.
        double st = Math.Sin(theta), ct = Math.Cos(theta);
        double vr = speed * (dR * st + dZ * ct), vt = speed * (dR * ct - dZ * st);
        double rhoDot = vr, thetaDot2 = vt / Math.Sqrt(rho * (rho - 1.0));
        s[0] = rho; s[1] = theta; s[2] = rhoDot / f; s[3] = rho * rho * thetaDot2;
        return true;
    }

    /// <summary>Trace view point (a, c). <paramref name="s"/> (length ≥ 4) ends as the final state.</summary>
    public MagnetisedBhOutcome Trace(double a, double c, Span<double> s)
    {
        if (!LaunchState(a, c, s, out double e, out double l))
        {
            double r = View == MagnetisedBhView.Kick ? a : Math.Sqrt(a * a + c * c);
            return new MagnetisedBhOutcome { Valid = false, Gate = -1, InsideHole = r < MagnetisedBhSystem.CaptureRadius };
        }
        var sys = new MagnetisedBhSystem
        {
            Energy = e, AngularMomentum = l, Field = Field, EscapeHeight = EscapeHeight, TimeCap = TimeCap, Gate = -1,
        };
        var settings = Settings;
        var o = ScatteringIntegrator.Trace(ref sys, s, in settings);
        int gate = o.Fate is ScatterFate.Captured or ScatterFate.Escaped ? sys.Gate : -1;
        return new MagnetisedBhOutcome { Valid = true, Gate = gate, Time = sys.Time, Steps = o.Steps };
    }
}
