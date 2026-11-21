// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScatteringIntegrator.cs (#1923, shared core of the gravitational-scattering set #1922 / §2.6)
//
// One trajectory per pixel: integrate an ODE from a launch state until the system
// classifies it (captured by body i, escaped) or a step cap is reached (undecided).
// This is the shared engine the gravitational / relativistic scattering families
// plug into (Theoretical-Fractal-RnD.md §2.6): each family supplies only its equations
// of motion and exit tests through IScatteringSystem. Black hole shadows (#1922) is
// the first user, Hénon-Heiles (#1942) the second, the restricted three-body problem (#1943)
// the third, the magnetised black hole (#1944) the fourth, spinning binaries (#1945) the fifth, Mixmaster (#1946) the sixth and
// black hole + ring (#1947) the seventh.
//
// Integrator: the explicit Runge-Kutta 5(4) pair of Dormand & Prince (1980, J. Comput.
// Appl. Math. 6, 19-26) with the standard error-per-step controller described in
// Hairer, Nørsett & Wanner, Solving Ordinary Differential Equations I (2nd ed. 1993,
// ch. II): step factor 0.9·err^(-1/5), clamped. The tableau below is the published
// Dormand-Prince one. Generic over a struct system so
// the JIT inlines the derivative; all scratch lives on the stack (no per-pixel
// allocation on the render path).

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Scattering;

/// <summary>How a traced trajectory ended.</summary>
public enum ScatterFate : byte
{
    /// <summary>Still running (internal; never returned).</summary>
    Running,
    /// <summary>Captured by body <see cref="ScatterOutcome.Body"/>.</summary>
    Captured,
    /// <summary>Left the system (escaped to infinity / through an exit channel).</summary>
    Escaped,
    /// <summary>Hit the step cap (or the step size collapsed) before a decision.</summary>
    Undecided,
}

/// <summary>A system of first-order ODEs with exit rules, traced by
/// <see cref="ScatteringIntegrator"/>. Implemented as a struct so calls inline.</summary>
public interface IScatteringSystem
{
    /// <summary>State dimension (at most <see cref="ScatteringIntegrator.MaxDimension"/>).</summary>
    int Dimension { get; }

    /// <summary>dy/dλ at state <paramref name="y"/>.</summary>
    void Derivative(ReadOnlySpan<double> y, Span<double> dy);

    /// <summary>Exit test after an accepted step: <see cref="ScatterFate.Running"/> to continue,
    /// otherwise the fate and (for <see cref="ScatterFate.Captured"/>) the body index.</summary>
    ScatterFate Classify(ReadOnlySpan<double> y, out int body);

    /// <summary>Largest step allowed from <paramref name="y"/> (the local length scale), so the
    /// controller cannot step over a close approach. Must be &gt; 0.</summary>
    double MaxStep(ReadOnlySpan<double> y);

    /// <summary>Called after every accepted step with the new state, for per-ray channels
    /// (closest approach, winding, …). Cheap; may do nothing.</summary>
    void Observe(ReadOnlySpan<double> y, double h);
}

/// <summary>Result of one traced trajectory.</summary>
public readonly struct ScatterOutcome
{
    public ScatterFate Fate { get; init; }
    /// <summary>Capturing body for <see cref="ScatterFate.Captured"/>, else -1.</summary>
    public int Body { get; init; }
    /// <summary>Accepted steps taken.</summary>
    public int Steps { get; init; }
    /// <summary>Total integration parameter (λ) travelled.</summary>
    public double Lambda { get; init; }
}

/// <summary>Integrator settings shared by every family.</summary>
public readonly struct ScatterSettings
{
    /// <summary>Relative tolerance per step (default 1e-9).</summary>
    public double RelTol { get; init; }
    /// <summary>Absolute tolerance per step (default 1e-12).</summary>
    public double AbsTol { get; init; }
    /// <summary>Accepted-step cap; past it the trajectory is <see cref="ScatterFate.Undecided"/>.</summary>
    public int MaxSteps { get; init; }
    /// <summary>First trial step.</summary>
    public double InitialStep { get; init; }

    public static ScatterSettings Default => new() { RelTol = 1e-9, AbsTol = 1e-12, MaxSteps = 20000, InitialStep = 0.1 };
}

public static class ScatteringIntegrator
{
    /// <summary>Largest state dimension the stack scratch holds.</summary>
    public const int MaxDimension = 16;

    // Dormand-Prince 5(4) tableau (Dormand & Prince 1980), FSAL.
    private const double A21 = 1.0 / 5.0;
    private const double A31 = 3.0 / 40.0, A32 = 9.0 / 40.0;
    private const double A41 = 44.0 / 45.0, A42 = -56.0 / 15.0, A43 = 32.0 / 9.0;
    private const double A51 = 19372.0 / 6561.0, A52 = -25360.0 / 2187.0, A53 = 64448.0 / 6561.0, A54 = -212.0 / 729.0;
    private const double A61 = 9017.0 / 3168.0, A62 = -355.0 / 33.0, A63 = 46732.0 / 5247.0, A64 = 49.0 / 176.0, A65 = -5103.0 / 18656.0;
    private const double B1 = 35.0 / 384.0, B3 = 500.0 / 1113.0, B4 = 125.0 / 192.0, B5 = -2187.0 / 6784.0, B6 = 11.0 / 84.0;
    // Error coefficients: 5th-order weights minus the embedded 4th-order ones.
    // internal: the GPU twin (BlackHoleShadowGpuCalculator, #1926) reuses the exact constants.
    internal const double E1 = 71.0 / 57600.0, E3 = -71.0 / 16695.0, E4 = 71.0 / 1920.0,
                         E5 = -17253.0 / 339200.0, E6 = 22.0 / 525.0, E7 = -1.0 / 40.0;

    private const double Safety = 0.9, MinFactor = 0.2, MaxFactor = 5.0;

    /// <summary>Trace <paramref name="y"/> (updated in place to the final state) under
    /// <paramref name="sys"/> until it is classified or the step cap is hit.</summary>
    [SkipLocalsInit]
    public static ScatterOutcome Trace<TSystem>(ref TSystem sys, Span<double> y, in ScatterSettings s)
        where TSystem : struct, IScatteringSystem
    {
        int n = sys.Dimension;
        if (n <= 0 || n > MaxDimension || y.Length < n)
            throw new ArgumentException($"state dimension {n} out of range", nameof(y));

        Span<double> buf = stackalloc double[MaxDimension * 9];
        Span<double> k1 = buf.Slice(0, n), k2 = buf.Slice(16, n), k3 = buf.Slice(32, n),
                     k4 = buf.Slice(48, n), k5 = buf.Slice(64, n), k6 = buf.Slice(80, n),
                     k7 = buf.Slice(96, n), tmp = buf.Slice(112, n), yNew = buf.Slice(128, n);
        Span<double> state = y.Slice(0, n);

        double rtol = s.RelTol > 0 ? s.RelTol : 1e-9;
        double atol = s.AbsTol > 0 ? s.AbsTol : 1e-12;
        int maxSteps = Math.Max(1, s.MaxSteps);
        double h = Math.Min(s.InitialStep > 0 ? s.InitialStep : 0.1, sys.MaxStep(state));
        double lambda = 0.0;
        int steps = 0, attempts = 0;
        int attemptCap = maxSteps * 8;   // rejected steps count too, so a stuck ray ends

        sys.Derivative(state, k1);
        while (steps < maxSteps && attempts < attemptCap)
        {
            attempts++;
            double hMax = sys.MaxStep(state);
            if (h > hMax) h = hMax;
            if (!(h > 0) || !double.IsFinite(h)) break;

            for (int i = 0; i < n; i++) tmp[i] = state[i] + h * (A21 * k1[i]);
            sys.Derivative(tmp, k2);
            for (int i = 0; i < n; i++) tmp[i] = state[i] + h * (A31 * k1[i] + A32 * k2[i]);
            sys.Derivative(tmp, k3);
            for (int i = 0; i < n; i++) tmp[i] = state[i] + h * (A41 * k1[i] + A42 * k2[i] + A43 * k3[i]);
            sys.Derivative(tmp, k4);
            for (int i = 0; i < n; i++) tmp[i] = state[i] + h * (A51 * k1[i] + A52 * k2[i] + A53 * k3[i] + A54 * k4[i]);
            sys.Derivative(tmp, k5);
            for (int i = 0; i < n; i++) tmp[i] = state[i] + h * (A61 * k1[i] + A62 * k2[i] + A63 * k3[i] + A64 * k4[i] + A65 * k5[i]);
            sys.Derivative(tmp, k6);
            for (int i = 0; i < n; i++) yNew[i] = state[i] + h * (B1 * k1[i] + B3 * k3[i] + B4 * k4[i] + B5 * k5[i] + B6 * k6[i]);
            sys.Derivative(yNew, k7);

            double err = 0.0;
            bool finite = true;
            for (int i = 0; i < n; i++)
            {
                double e = h * (E1 * k1[i] + E3 * k3[i] + E4 * k4[i] + E5 * k5[i] + E6 * k6[i] + E7 * k7[i]);
                double sc = atol + rtol * Math.Max(Math.Abs(state[i]), Math.Abs(yNew[i]));
                double r = Math.Abs(e) / sc;
                if (!double.IsFinite(r)) { finite = false; break; }
                if (r > err) err = r;
            }

            if (finite && err <= 1.0)
            {
                lambda += h;
                steps++;
                yNew.CopyTo(state);
                k7.CopyTo(k1);   // FSAL
                sys.Observe(state, h);
                var fate = sys.Classify(state, out int body);
                if (fate != ScatterFate.Running)
                    return new ScatterOutcome { Fate = fate, Body = fate == ScatterFate.Captured ? body : -1, Steps = steps, Lambda = lambda };
                double f = err == 0.0 ? MaxFactor : Math.Clamp(Safety * Math.Pow(err, -0.2), MinFactor, MaxFactor);
                h *= f;
            }
            else
            {
                double f = finite ? Math.Clamp(Safety * Math.Pow(err, -0.2), MinFactor, 1.0) : MinFactor;
                h *= f;
                if (h < 1e-14 * Math.Max(1.0, lambda)) break;   // step collapsed
            }
        }
        return new ScatterOutcome { Fate = ScatterFate.Undecided, Body = -1, Steps = steps, Lambda = lambda };
    }
}
