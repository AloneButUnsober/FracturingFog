// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserHamiltonian.cs (#1997, General User Hamiltonian)
//
// A user-written Hamiltonian H(q, p) with N = 1-4 degrees of freedom: coordinates x, y, z, w
// and momenta px, py, pz, pw (the first N of each). FF derives Hamilton's equations
//
//     dq_i/dt = ∂H/∂p_i,   dp_i/dt = −∂H/∂q_i
//
// SYMBOLICALLY (RealExpression, #1994) and traces each pixel's launch on the shared
// Dormand–Prince core (#1923), the general form of #1994's User Potential.
//
// Launch spec (declarative, the issue's first design question):
//   • the two pixel axes name two of the variables (A across, B up);
//   • "fixed" sets others, `name=expression; ...`, evaluated in order, each expression reading
//     the pixel values and the variables set before it (e.g. `x=0; px=-y`); unset variables are 0;
//   • one variable may be SOLVED from the constraint H = h0: a root of H(v) − h0 is bracketed
//     outward from 0 in the chosen direction (sign +1 or −1) and refined by bisection; no root
//     means the pixel is not a launch. (For H = p²/2 + V this is v = ±√(2(h0 − ...)).)
// Exits: past the exit radius (on all N coordinates) the gate is the angular sector of (x, y)
// (N equal sectors from an offset, as #1994); an optional "exit if" expression in the variables
// and t returns a gate (>= 0) to stop with. Time limit: undecided.
// Constraint stabilisation (the issue's third question; FF's numerical method, as #1946's): with
// "project every K steps" on, the trace runs in segments of K steps and between segments the
// solved variable is moved (Newton on it, symbolic ∂H/∂v) so H returns to h0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1997 — the compiled H, its gradient and the launch / exit rules for one set of texts.</summary>
public sealed class UserHamiltonianModel
{
    public static readonly string[] Coordinates = { "x", "y", "z", "w" };
    public static readonly string[] Momenta = { "px", "py", "pz", "pw" };

    public int N { get; }
    public string? Error { get; }
    /// <summary>The variable names in state order: q1..qN, p1..pN.</summary>
    public IReadOnlyList<string> Variables { get; } = Array.Empty<string>();
    public RealExpression H { get; } = null!;
    internal readonly Func<double[], double> Hf = null!;
    internal readonly Func<double[], double>[] Grad = null!;     // ∂H/∂(state k)
    internal readonly Func<double[], double>? Exit;             // reads state then t
    internal readonly int AxisA = -1, AxisB = -1, Solve = -1;
    internal readonly (int Index, Func<double[], double> Value)[] Fixed = Array.Empty<(int, Func<double[], double>)>();

    private UserHamiltonianModel(string error) { Error = error; }

    private UserHamiltonianModel(int n, string[] vars, RealExpression h, RealExpression? exit, int a, int b, int solve,
                                 (int, Func<double[], double>)[] fixedVals)
    {
        N = n; Variables = vars; H = h;
        Hf = h.Compile();
        Grad = new Func<double[], double>[2 * n];
        for (int k = 0; k < 2 * n; k++) Grad[k] = h.Derivative(k).Compile();
        Exit = exit?.Compile();
        AxisA = a; AxisB = b; Solve = solve; Fixed = fixedVals;
    }

    private static readonly ConcurrentDictionary<string, UserHamiltonianModel> s_cache = new();

    public static UserHamiltonianModel For(FractalParameters p)
    {
        string key = string.Join("\u0001", p.UserHamDimension.ToString(CultureInfo.InvariantCulture), p.UserHamSource ?? "",
                                 p.UserHamExitIf ?? "", p.UserHamAxisA ?? "", p.UserHamAxisB ?? "", p.UserHamFixed ?? "", p.UserHamSolveFor ?? "");
        if (s_cache.TryGetValue(key, out var m)) return m;
        if (s_cache.Count > 64) s_cache.Clear();
        return s_cache[key] = Build(p);
    }

    private static UserHamiltonianModel Build(FractalParameters p)
    {
        int n = Math.Clamp(p.UserHamDimension, 1, 4);
        var vars = new string[2 * n];
        for (int i = 0; i < n; i++) { vars[i] = Coordinates[i]; vars[n + i] = Momenta[i]; }
        int Index(string? name) => Array.IndexOf(vars, (name ?? "").Trim());
        if (!RealExpression.TryParse(p.UserHamSource, vars, out var h, out var err)) return new("H: " + err);
        RealExpression? exit = null;
        if (!string.IsNullOrWhiteSpace(p.UserHamExitIf))
        {
            var ev = new List<string>(vars) { "t" };
            if (!RealExpression.TryParse(p.UserHamExitIf, ev, out exit, out err)) return new("Exit if: " + err);
        }
        int a = Index(p.UserHamAxisA), b = Index(p.UserHamAxisB);
        if (a < 0) return new($"Axis A: '{p.UserHamAxisA}' is not one of {string.Join(", ", vars)}");
        if (b < 0) return new($"Axis B: '{p.UserHamAxisB}' is not one of {string.Join(", ", vars)}");
        if (a == b) return new("Axis A and axis B must be different variables.");
        int solve = string.IsNullOrWhiteSpace(p.UserHamSolveFor) ? -1 : Index(p.UserHamSolveFor);
        if (!string.IsNullOrWhiteSpace(p.UserHamSolveFor) && solve < 0)
            return new($"Solve for: '{p.UserHamSolveFor}' is not one of {string.Join(", ", vars)}");
        if (solve == a || solve == b) return new("The solved variable cannot be a pixel axis.");
        var fixedVals = new List<(int, Func<double[], double>)>();
        var seen = new HashSet<int> { a, b };
        foreach (var part in (p.UserHamFixed ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return new($"Fixed: '{part}' is not name=expression");
            int k = Index(part[..eq]);
            if (k < 0) return new($"Fixed: '{part[..eq].Trim()}' is not one of {string.Join(", ", vars)}");
            if (k == solve) return new($"Fixed: '{vars[k]}' is the solved variable");
            if (!seen.Add(k)) return new($"Fixed: '{vars[k]}' is set twice (or is a pixel axis)");
            if (!RealExpression.TryParse(part[(eq + 1)..], vars, out var fe, out err)) return new($"Fixed {vars[k]}: " + err);
            fixedVals.Add((k, fe!.Compile()));
        }
        try { return new(n, vars, h!, exit, a, b, solve, fixedVals.ToArray()); }
        catch (Exception ex) { return new("Could not compile: " + ex.Message); }
    }
}

/// <summary>#1997 — Hamilton's equations and the exit rule, for <see cref="ScatteringIntegrator"/>.</summary>
public struct UserHamiltonianSystem : IScatteringSystem
{
    public UserHamiltonianModel Model;
    public double ExitRadius;
    public int Sectors;
    public double SectorOffset;
    public double TimeCap, StepCap;
    public double[] Scratch;   // length >= 2N + 1, per thread
    public double Time;
    public int Gate;

    public readonly int Dimension => 2 * Model.N;

    public readonly void Derivative(ReadOnlySpan<double> s, Span<double> ds)
    {
        int n = Model.N;
        var a = Scratch;
        for (int k = 0; k < 2 * n; k++) a[k] = s[k];
        for (int i = 0; i < n; i++)
        {
            ds[i] = Model.Grad[n + i](a);        // dq/dt =  ∂H/∂p
            ds[n + i] = -Model.Grad[i](a);       // dp/dt = −∂H/∂q
        }
    }

    public ScatterFate Classify(ReadOnlySpan<double> s, out int body)
    {
        body = -1;
        int n = Model.N;
        if (Model.Exit is { } exit)
        {
            var a = Scratch;
            for (int k = 0; k < 2 * n; k++) a[k] = s[k];
            a[2 * n] = Time;
            double g = exit(a);
            if (g >= 0 && double.IsFinite(g)) { Gate = (int)Math.Min(Math.Floor(g), 15); return ScatterFate.Escaped; }
        }
        double r2 = 0;
        for (int i = 0; i < n; i++) r2 += s[i] * s[i];
        if (r2 > ExitRadius * ExitRadius)
        {
            Gate = UserPotentialSystem.SectorOf(s[0], n > 1 ? s[1] : 0.0, Sectors, SectorOffset);
            return ScatterFate.Escaped;
        }
        for (int k = 0; k < 2 * n; k++) if (!double.IsFinite(s[k])) { Gate = -1; return ScatterFate.Undecided; }
        return Time >= TimeCap ? ScatterFate.Undecided : ScatterFate.Running;
    }

    public readonly double MaxStep(ReadOnlySpan<double> s) => StepCap;
    public void Observe(ReadOnlySpan<double> s, double h) => Time += h;
}

/// <summary>#1997 — one launch's outcome.</summary>
public readonly struct UserHamiltonianOutcome
{
    public bool Valid { get; init; }
    public int Gate { get; init; }
    public double Time { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1997 — one User Hamiltonian scene, built from <see cref="FractalParameters"/>.</summary>
public sealed class UserHamiltonianScene
{
    public UserHamiltonianModel Model { get; }
    public double Energy { get; }
    public int SolveSign { get; }
    public double ExitRadius { get; }
    public int Sectors { get; }
    public double SectorOffset { get; }
    public double TimeCap { get; }
    public double StepCap { get; }
    public int ProjectEvery { get; }
    public ScatterSettings Settings { get; }

    [ThreadStatic] private static double[]? t_scratch;
    private static double[] Scratch() => t_scratch ??= new double[16];

    public UserHamiltonianScene(FractalParameters p)
    {
        Model = UserHamiltonianModel.For(p);
        Energy = double.IsFinite(p.UserHamEnergy) ? p.UserHamEnergy : 0.0;
        SolveSign = p.UserHamSolveSign < 0 ? -1 : 1;
        ExitRadius = double.IsFinite(p.UserHamExitRadius) ? Math.Clamp(p.UserHamExitRadius, 1e-3, 1e6) : 2.0;
        Sectors = Math.Clamp(p.UserHamSectors, 1, 16);
        SectorOffset = (double.IsFinite(p.UserHamSectorOffset) ? p.UserHamSectorOffset : 90.0) * Math.PI / 180.0;
        TimeCap = double.IsFinite(p.UserHamMaxTime) ? Math.Clamp(p.UserHamMaxTime, 1.0, 100000.0) : 200.0;
        StepCap = double.IsFinite(p.UserHamMaxStep) ? Math.Clamp(p.UserHamMaxStep, 1e-4, 10.0) : 0.25;
        ProjectEvery = Math.Clamp(p.UserHamProjectEvery, 0, 100000);
        double tol = double.IsFinite(p.UserHamTolerance) ? Math.Clamp(p.UserHamTolerance, 1e-13, 1e-3) : 1e-8;
        int steps = (int)Math.Min(int.MaxValue / 16, TimeCap / 1e-3);
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = steps, InitialStep = 0.05 };
    }

    public static UserHamiltonianScene Build(FractalParameters p) => new(p);

    /// <summary>The launch state at view point (a, b), or false where none exists.</summary>
    public bool LaunchState(double a, double b, Span<double> s)
    {
        var m = Model;
        if (m.Error != null) return false;
        int d = 2 * m.N;
        var v = Scratch();
        Array.Clear(v, 0, v.Length);
        v[m.AxisA] = a; v[m.AxisB] = b;
        foreach (var (k, f) in m.Fixed)
        {
            double x = f(v);
            if (!double.IsFinite(x)) return false;
            v[k] = x;
        }
        if (m.Solve >= 0 && !SolveFor(v, m.Solve)) return false;
        for (int k = 0; k < d; k++) s[k] = v[k];
        return true;
    }

    /// <summary>A root of H − h0 in variable <paramref name="k"/>: bracketed outward from 0 in the
    /// chosen direction, then bisected.</summary>
    private bool SolveFor(double[] v, int k)
    {
        var hf = Model.Hf;
        double G(double x) { v[k] = x; return hf(v) - Energy; }
        double lo = 0, glo = G(0);
        if (!double.IsFinite(glo)) return false;
        if (glo == 0) { v[k] = 0; return true; }
        double hi = double.NaN, ghi = double.NaN;
        for (int i = 0; i < 80; i++)
        {
            double x = SolveSign * 1e-4 * Math.Pow(2, i);
            double g = G(x);
            if (!double.IsFinite(g)) break;
            if (Math.Sign(g) != Math.Sign(glo)) { hi = x; ghi = g; break; }
            lo = x; glo = g;
        }
        if (double.IsNaN(hi)) return false;
        for (int i = 0; i < 200 && lo != hi; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid == lo || mid == hi) break;
            double g = G(mid);
            if (g == 0) { lo = hi = mid; break; }
            if (Math.Sign(g) == Math.Sign(glo)) { lo = mid; glo = g; } else { hi = mid; ghi = g; }
        }
        v[k] = Math.Abs(glo) <= Math.Abs(ghi) ? lo : hi;
        return true;
    }

    /// <summary>Move the solved variable so H returns to h0 (Newton, symbolic ∂H/∂v).</summary>
    public void Project(Span<double> s)
    {
        var m = Model;
        if (m.Solve < 0) return;
        var v = Scratch();
        for (int k = 0; k < 2 * m.N; k++) v[k] = s[k];
        for (int it = 0; it < 4; it++)
        {
            double g = m.Hf(v) - Energy, dg = m.Grad[m.Solve](v);
            if (!(Math.Abs(dg) > 1e-300) || !double.IsFinite(g)) return;
            v[m.Solve] -= g / dg;
        }
        if (double.IsFinite(v[m.Solve])) s[m.Solve] = v[m.Solve];
    }

    /// <summary>H at state <paramref name="s"/>.</summary>
    public double HamiltonianAt(ReadOnlySpan<double> s)
    {
        var v = new double[2 * Model.N];
        for (int k = 0; k < v.Length; k++) v[k] = s[k];
        return Model.Hf(v);
    }

    public UserHamiltonianOutcome Trace(double a, double b, Span<double> s)
    {
        var none = default(NoPathRecorder);
        return Trace(a, b, s, ref none);
    }

    public UserHamiltonianOutcome Trace<TRec>(double a, double b, Span<double> s, ref TRec rec) where TRec : IPathRecorder
    {
        if (!LaunchState(a, b, s)) return new UserHamiltonianOutcome { Valid = false, Gate = -1 };
        rec.Record(s);
        var sys = new UserHamiltonianSystem
        {
            Model = Model, ExitRadius = ExitRadius, Sectors = Sectors, SectorOffset = SectorOffset,
            TimeCap = TimeCap, StepCap = StepCap, Scratch = Scratch(), Gate = -1,
        };
        var first = sys.Classify(s, out _);
        if (first != ScatterFate.Running)
            return new UserHamiltonianOutcome { Valid = true, Gate = first == ScatterFate.Escaped ? sys.Gate : -1 };
        var wrapped = new RecordingSystem<UserHamiltonianSystem, TRec>(sys, rec);
        var settings = Settings;
        ScatterFate fate;
        int total = 0;
        if (ProjectEvery > 0 && Model.Solve >= 0)
        {
            var seg = settings with { MaxSteps = ProjectEvery };
            while (true)
            {
                var o = ScatteringIntegrator.Trace(ref wrapped, s, in seg);
                total += o.Steps;
                fate = o.Fate;
                if (fate != ScatterFate.Undecided || wrapped.Inner.Time >= TimeCap || o.Steps == 0 || total >= settings.MaxSteps) break;
                Project(s);
                seg = seg with { InitialStep = Math.Max(1e-12, o.Lambda / Math.Max(1, o.Steps)) };
            }
        }
        else
        {
            var o = ScatteringIntegrator.Trace(ref wrapped, s, in settings);
            total = o.Steps; fate = o.Fate;
        }
        rec = wrapped.Recorder;
        var end = wrapped.Inner;
        return new UserHamiltonianOutcome { Valid = true, Gate = fate == ScatterFate.Escaped ? end.Gate : -1, Time = end.Time, Steps = total };
    }
}
