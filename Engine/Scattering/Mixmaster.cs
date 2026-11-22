// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Mixmaster.cs (#1946, gravitational-scattering set §2.6)
//
// The vacuum Bianchi IX ("Mixmaster") cosmology near its singularity, in the form Cornish & Levin
// used to show it is chaotic with coordinate-independent fractal methods (Phys. Rev. Lett. 78, 998,
// 1997; Phys. Rev. D 55, 7489, 1997, "A chaotic Farey tale"). Scale factors a, b, c; a prime is
// d/dτ with dt = abc dτ:
//
//   (ln a)″ = ½ [(b² − c²)² − a⁴]   and cyclically (a, b, c)                  (PRD eq. 2)
//   H = (ln a)′(ln b)′ + (ln a)′(ln c)′ + (ln b)′(ln c)′
//       − ¼ [a⁴ + b⁴ + c⁴ − 2(a²b² + b²c² + c²a²)] = 0                          (PRL eq. 2)
//
// The two papers print opposite signs before the ¼ (PRL eq. 2: minus; PRD eq. 1: plus). With the
// equations of motion above only the minus sign is conserved: the kinetic form Σ x_i′x_j′ is the
// Lagrangian's kinetic term, the potential U = −¼P gives x_a″ = −(1/2)·(−1)·[(b²−c²)² − a⁴]; the
// tests check both signs numerically.
//
// Initial conditions as in the papers:
//  • Kasner (u, v) view, PRD eq. 3: ln a = 0, ln b = 3Ω/(1 + v + uv), ln c = 3(v + uv)Ω/(1 + v + uv),
//    (ln a)′ = 3ω p_a(u), (ln b)′ = 3ω p_b(u), (ln c)′ = 3ω p_c(u), Kasner indices PRD eq. 5,
//    ω = 1/3 and Ω solved from the constraint (the Kasner kinetic term vanishes, so Ω is the root
//    of the potential P(Ω) = 0);
//  • anisotropy (β+, β−) view (their Fig. 19): Ω = −1, (ln a)′ = (ln b)′ = 10, (ln c)′ from the
//    constraint; with Ω = ⅓ ln(abc), β+ = ⅓ ln(bc/a²), β− = (1/√3) ln(b/c) (PRD eqs. 70-72).
//
// Direction: the Kasner epochs and bounces happen on the approach to the singularity, where the
// volume abc → 0. With the papers' ω = Ω′ > 0 that is the direction of decreasing τ, so FF integrates
// the time-reversed system (all first derivatives negated; the equations are second order with no
// first-derivative terms, so this is the same trajectory run backwards). The parts of the papers read
// for #1946 do not state their integration direction.
//
// Outcome (both papers): the evolution stops once the trajectory is in a Kasner epoch with
// u > u_exit (PRD: u_exit = 8 for the basins), and is coloured by which axis is collapsing most
// quickly then: towards the singularity a_i ∝ t^{p_i}, so that is the axis with the largest
// exponent p_i. FF decides "in a Kasner epoch" when every (ln x)″ is below a small fraction of the
// kinetic scale Σ(ln x)′² (the papers monitor the ratios of the (ln x)′ without a stated threshold),
// and reads u = p_max / p_mid from the exponents p_i = (ln x_i)′ / Σ(ln x)′ (PRD eq. 5 gives
// p_c / p_a = u).

using System;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1946 — vacuum Bianchi IX in Cornish &amp; Levin's variables, for
/// <see cref="ScatteringIntegrator"/>. State = (ln a, ln b, ln c, (ln a)′, (ln b)′, (ln c)′).</summary>
public struct MixmasterSystem : IScatteringSystem
{
    /// <summary>Outcome gates: the collapsing axis a, b or c.</summary>
    public const int GateCount = 3;

    /// <summary>"In a Kasner epoch": every |(ln x)″| below this fraction of Σ(ln x)′².</summary>
    public const double KasnerFraction = 1e-3;

    public double UExit;
    /// <summary>Arc-length limit (λ); undecided past it.</summary>
    public double TimeCap;
    /// <summary>Arc length λ travelled so far.</summary>
    public double Time;
    /// <summary>Set by <see cref="Classify"/>: the axis collapsing most quickly at exit (largest
    /// Kasner exponent; 0 a, 1 b, 2 c), −1 none.</summary>
    public int Gate;
    /// <summary>u of the Kasner epoch at exit.</summary>
    public double ExitU;

    public readonly int Dimension => 6;

    /// <summary>P = a⁴ + b⁴ + c⁴ − 2(a²b² + b²c² + c²a²) from the logs.</summary>
    public static double Potential(double la, double lb, double lc)
    {
        double a2 = Math.Exp(2 * la), b2 = Math.Exp(2 * lb), c2 = Math.Exp(2 * lc);
        return a2 * a2 + b2 * b2 + c2 * c2 - 2 * (a2 * b2 + b2 * c2 + c2 * a2);
    }

    /// <summary>The constraint H with the given sign before ¼P (−1 is the conserved one).</summary>
    public static double Hamiltonian(ReadOnlySpan<double> y, double sign = -1.0)
        => y[3] * y[4] + y[3] * y[5] + y[4] * y[5] + sign * 0.25 * Potential(y[0], y[1], y[2]);

    /// <summary>The equations in arc length λ, dλ = |x′| dτ with |x′| = √Σ(ln x)′²: the same
    /// trajectories (H is unchanged by a change of clock), but the velocities shrink at every bounce
    /// (ω falls) and the epochs lengthen, so in τ the approach to the singularity would need
    /// ever-larger times; in λ each epoch costs its length in the (ln a, ln b, ln c) space.</summary>
    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy)
    {
        double a2 = Math.Exp(2 * y[0]), b2 = Math.Exp(2 * y[1]), c2 = Math.Exp(2 * y[2]);
        double k = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
        double inv = k > 0 ? 1.0 / k : 0.0;
        dy[0] = y[3] * inv; dy[1] = y[4] * inv; dy[2] = y[5] * inv;
        dy[3] = 0.5 * ((b2 - c2) * (b2 - c2) - a2 * a2) * inv;
        dy[4] = 0.5 * ((c2 - a2) * (c2 - a2) - b2 * b2) * inv;
        dy[5] = 0.5 * ((a2 - b2) * (a2 - b2) - c2 * c2) * inv;
    }

    /// <summary>The τ-derivative (the papers' form, eq. 2), for tests.</summary>
    public static void DerivativeTau(ReadOnlySpan<double> y, Span<double> dy)
    {
        double a2 = Math.Exp(2 * y[0]), b2 = Math.Exp(2 * y[1]), c2 = Math.Exp(2 * y[2]);
        dy[0] = y[3]; dy[1] = y[4]; dy[2] = y[5];
        dy[3] = 0.5 * ((b2 - c2) * (b2 - c2) - a2 * a2);
        dy[4] = 0.5 * ((c2 - a2) * (c2 - a2) - b2 * b2);
        dy[5] = 0.5 * ((a2 - b2) * (a2 - b2) - c2 * c2);
    }

    public ScatterFate Classify(ReadOnlySpan<double> y, out int body)
    {
        body = -1;
        if (InKasnerEpoch(y, out double u, out int collapsing) && u > UExit)
        {
            Gate = collapsing; ExitU = u;
            return ScatterFate.Escaped;
        }
        if (Time >= TimeCap) { Gate = -1; return ScatterFate.Undecided; }
        return ScatterFate.Running;
    }

    /// <summary>True when the state is in a Kasner epoch; then u = p_max / p_mid and the axis collapsing
    /// most quickly towards the singularity (the largest exponent).</summary>
    public static bool InKasnerEpoch(ReadOnlySpan<double> y, out double u, out int collapsing)
    {
        u = double.NaN; collapsing = -1;
        double a2 = Math.Exp(2 * y[0]), b2 = Math.Exp(2 * y[1]), c2 = Math.Exp(2 * y[2]);
        double fa = 0.5 * ((b2 - c2) * (b2 - c2) - a2 * a2);
        double fb = 0.5 * ((c2 - a2) * (c2 - a2) - b2 * b2);
        double fc = 0.5 * ((a2 - b2) * (a2 - b2) - c2 * c2);
        double k = y[3] * y[3] + y[4] * y[4] + y[5] * y[5];
        if (!(k > 0) || !double.IsFinite(fa + fb + fc)) return false;
        double lim = KasnerFraction * k;
        if (Math.Abs(fa) > lim || Math.Abs(fb) > lim || Math.Abs(fc) > lim) return false;
        double sum = y[3] + y[4] + y[5];
        if (Math.Abs(sum) < 1e-300) return false;
        // Exponents p_i = (ln x_i)′ / Σ (the ratio does not depend on the direction of time).
        double pa = y[3] / sum, pb = y[4] / sum, pc = y[5] / sum;
        int imax = pa >= pb && pa >= pc ? 0 : pb >= pc ? 1 : 2;
        double pmax = Math.Max(pa, Math.Max(pb, pc));
        double pmid = pa + pb + pc - pmax - Math.Min(pa, Math.Min(pb, pc));
        collapsing = imax;
        u = pmid > 0 ? pmax / pmid : double.PositiveInfinity;
        return true;
    }

    /// <summary>Step cap (in λ): a tenth of the arc length over which the velocities change,
    /// |x′|² / |x″|. Inside a Kasner epoch the motion is straight and steps grow; at a wall the
    /// exponential force shrinks them (with the error control).</summary>
    public readonly double MaxStep(ReadOnlySpan<double> y)
    {
        double a2 = Math.Exp(2 * y[0]), b2 = Math.Exp(2 * y[1]), c2 = Math.Exp(2 * y[2]);
        double fa = 0.5 * ((b2 - c2) * (b2 - c2) - a2 * a2);
        double fb = 0.5 * ((c2 - a2) * (c2 - a2) - b2 * b2);
        double fc = 0.5 * ((a2 - b2) * (a2 - b2) - c2 * c2);
        double f = Math.Sqrt(fa * fa + fb * fb + fc * fc);
        double k = Math.Sqrt(y[3] * y[3] + y[4] * y[4] + y[5] * y[5]);
        if (!double.IsFinite(f)) return 1e-9;
        return Math.Clamp(0.1 * k * k / Math.Max(1e-300, f), 1e-9, MaxStepCeiling);
    }

    /// <summary>Largest step in λ.</summary>
    public const double MaxStepCeiling = 1e300;

    public void Observe(ReadOnlySpan<double> y, double h) => Time += h;

    /// <summary>Kasner indices p_a, p_b, p_c of u (PRD eq. 5).</summary>
    public static (double Pa, double Pb, double Pc) KasnerIndices(double u)
    {
        double d = 1 + u + u * u;
        return ((1 + u) / d, -u / d, (u + u * u) / d);
    }

    /// <summary>The continuous u-map (PRD eq. 10): u − 1 for u ≥ 2, 1/(u − 1) for 1 ≤ u &lt; 2.</summary>
    public static double UMap(double u) => u >= 2 ? u - 1 : 1.0 / (u - 1);
}

/// <summary>#1946 — how one Mixmaster trajectory ended.</summary>
public readonly struct MixmasterOutcome
{
    public bool Valid { get; init; }
    /// <summary>Collapsing axis at exit (0 a, 1 b, 2 c), −1 none by the time limit.</summary>
    public int Gate { get; init; }
    public double Time { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1946 — one Mixmaster scene built from <see cref="FractalParameters"/>.</summary>
public sealed class MixmasterScene
{
    public MixmasterView View { get; }
    public double UExit { get; }
    public double OmegaDot { get; }
    public double Omega { get; }
    public double AlphaDot { get; }
    public double BetaDot { get; }
    public double TimeCap { get; }
    public ScatterSettings Settings { get; }

    public MixmasterScene(MixmasterView view, double uExit, double omegaDot, double omega, double alphaDot, double betaDot,
                          double timeCap, double tolerance)
    {
        View = view;
        UExit = double.IsFinite(uExit) ? Math.Clamp(uExit, 2.0, 1000.0) : 8.0;
        OmegaDot = double.IsFinite(omegaDot) && omegaDot != 0 ? omegaDot : 1.0 / 3.0;
        Omega = double.IsFinite(omega) ? omega : -1.0;
        AlphaDot = double.IsFinite(alphaDot) ? alphaDot : 10.0;
        BetaDot = double.IsFinite(betaDot) ? betaDot : 10.0;
        TimeCap = double.IsFinite(timeCap) ? Math.Clamp(timeCap, 1.0, 1e300) : 1e30;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-9;
        // The velocities shrink by many orders of magnitude towards the singularity: the error control must
        // stay relative (an absolute floor would stop seeing them and the constraint would drift).
        Settings = new ScatterSettings { RelTol = tol, AbsTol = 1e-300, MaxSteps = 20_000_000, InitialStep = 1e-3 };
    }

    public static MixmasterScene Build(FractalParameters p)
        => new(p.MixView, p.MixUExit, p.MixOmegaDot, p.MixOmega, p.MixAlphaDot, p.MixBetaDot, p.MixMaxTime, p.MixTolerance);

    /// <summary>Initial state for view point (x, y); false where it does not exist.</summary>
    public bool LaunchState(double x, double y, Span<double> s)
    {
        if (View == MixmasterView.Anisotropy)
        {
            double bp = x, bm = y, omA = Omega;
            s[0] = omA - bp;
            s[1] = omA + 0.5 * bp + 0.5 * Math.Sqrt(3.0) * bm;
            s[2] = omA + 0.5 * bp - 0.5 * Math.Sqrt(3.0) * bm;
            s[3] = AlphaDot; s[4] = BetaDot;
            double den = AlphaDot + BetaDot;
            if (Math.Abs(den) < 1e-300) return false;
            // H = α̇β̇ + γ̇(α̇ + β̇) − ¼P = 0.
            s[5] = (0.25 * MixmasterSystem.Potential(s[0], s[1], s[2]) - AlphaDot * BetaDot) / den;
            if (!double.IsFinite(s[5])) return false;
            ReverseTime(s);
            return true;
        }
        double u = x, v = y;
        if (!(u >= 1.0) || !(v > 0) || !double.IsFinite(u * v)) return false;
        double d = 1 + v + u * v;
        double fb = 3.0 / d, fc = 3.0 * (v + u * v) / d;   // ln b = fb Ω, ln c = fc Ω, ln a = 0
        // P(Ω) = 0 with P → 1 as Ω → −∞ and P(0) = −3: bisect on Ω < 0.
        double lo = -1.0, hi = 0.0;
        while (MixmasterSystem.Potential(0, fb * lo, fc * lo) <= 0 && lo > -1e6) lo *= 2;
        if (MixmasterSystem.Potential(0, fb * lo, fc * lo) <= 0) return false;
        for (int i = 0; i < 200; i++)
        {
            double m = 0.5 * (lo + hi);
            if (MixmasterSystem.Potential(0, fb * m, fc * m) > 0) lo = m; else hi = m;
        }
        double om = 0.5 * (lo + hi);
        var (pa, pb, pc) = MixmasterSystem.KasnerIndices(u);
        s[0] = 0; s[1] = fb * om; s[2] = fc * om;
        s[3] = 3 * OmegaDot * pa; s[4] = 3 * OmegaDot * pb; s[5] = 3 * OmegaDot * pc;
        ReverseTime(s);
        return true;
    }

    /// <summary>Run towards the singularity: negate the first derivatives when the volume is growing
    /// (Σ(ln x)′ = 3Ω′ &gt; 0). H is quadratic in them, so the constraint is unchanged.</summary>
    private static void ReverseTime(Span<double> s)
    {
        if (s[3] + s[4] + s[5] > 0) { s[3] = -s[3]; s[4] = -s[4]; s[5] = -s[5]; }
    }

    public MixmasterSystem System() => new() { UExit = UExit, TimeCap = TimeCap, Gate = -1 };

    /// <summary>Accepted steps per segment between constraint corrections.</summary>
    public const int SegmentSteps = 32;

    /// <summary>Trace view point (x, y). <paramref name="s"/> (length ≥ 6) ends as the final state.
    /// The exit test runs after every accepted step, the first included, so a start already in a
    /// Kasner epoch with u0 &gt; u_exit exits at once with its own collapsing axis.
    ///
    /// Constraint stabilisation (FF's numerical method, not the papers'): the flow conserves H
    /// exactly, but the tiny error it picks up early stays fixed in absolute size while the
    /// velocities shrink at every bounce, so its share of the kinetic term doubles each bounce until
    /// it acts like spurious matter. Every <see cref="SegmentSteps"/> steps the volume velocity Ω′ is
    /// shifted by the smallest δ that restores H = 0 (all three (ln x)′ by δ:
    /// 3δ² + 2δΣ(ln x)′ + H = 0).</summary>
    public MixmasterOutcome Trace(double x, double y, Span<double> s)
    {
        var none = default(NoPathRecorder);
        return Trace(x, y, s, ref none);
    }

    /// <summary>#1996 — <see cref="Trace(double, double, Span{double})"/> with every state recorded
    /// (the launch, then each accepted step; the constraint projection between segments is applied
    /// to the state, not recorded as a step) for the Math Plane 3D orbit3 statement.</summary>
    public MixmasterOutcome Trace<TRec>(double x, double y, Span<double> s, ref TRec rec) where TRec : IPathRecorder
    {
        if (!LaunchState(x, y, s)) return new MixmasterOutcome { Valid = false, Gate = -1 };
        rec.Record(s);
        var wrapped = new RecordingSystem<MixmasterSystem, TRec>(System(), rec);
        var settings = Settings with { MaxSteps = SegmentSteps };
        int total = 0;
        ScatterFate fate;
        while (true)
        {
            var o = ScatteringIntegrator.Trace(ref wrapped, s, in settings);
            var sys = wrapped.Inner;
            total += o.Steps;
            fate = o.Fate;
            if (fate != ScatterFate.Undecided || sys.Time >= TimeCap || o.Steps == 0 || total >= Settings.MaxSteps)
                break;
            Project(s);
            settings = settings with { InitialStep = Math.Max(1e-12, o.Lambda / Math.Max(1, o.Steps)) };
        }
        rec = wrapped.Recorder;
        var end = wrapped.Inner;
        int gate = fate == ScatterFate.Escaped ? end.Gate : -1;
        return new MixmasterOutcome { Valid = true, Gate = gate, Time = end.Time, Steps = total };
    }

    /// <summary>Restore H = 0 by the smallest equal shift δ of the three (ln x)′.</summary>
    public static void Project(Span<double> s)
    {
        double h = MixmasterSystem.Hamiltonian(s);
        double sum = s[3] + s[4] + s[5];
        double disc = sum * sum - 3 * h;   // 3δ² + 2Σδ + H = 0  →  δ = (−Σ ± √(Σ² − 3H)) / 3
        if (!(disc >= 0)) return;
        double r = Math.Sqrt(disc);
        double d1 = (-sum + r) / 3, d2 = (-sum - r) / 3;
        double d = Math.Abs(d1) < Math.Abs(d2) ? d1 : d2;
        s[3] += d; s[4] += d; s[5] += d;
    }
}
