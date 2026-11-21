// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SpinningBinary.cs (#1945, gravitational-scattering set §2.6)
//
// Two spinning compact bodies under post-Newtonian equations of motion, the system in which
// Levin (Phys. Rev. Lett. 84, 3515, 2000) reported chaos and fractal basin boundaries between
// outcomes. Levin's eqs. (1)-(2) are x¨ = a_PN + a_SO + a_SS + a_RR and dS_A/dt = Ω_A × S_A, with the
// explicit forms taken from Kidder (Phys. Rev. D 52, 821, 1995). FF transcribes them from Kidder,
// numbered as there:
//
//   relative acceleration (Eq. 3) a = a_N + a_PN + a_SO + a_2PN + a_SS + a_RR, Eqs. (4a)-(4f)
//   spin precession Eqs. (6a)-(6b)
//   conserved energy (Eqs. 8, 9a-9e) and total angular momentum (Eqs. 10, 11a-11d), used by the
//   tests and the status note only.
//
// Harmonic coordinates, centre-of-mass frame, covariant spin supplementary condition, G = c = 1,
// total mass m = 1. x = x1 − x2, n = x/r, μ = m1 m2/m, η = μ/m, δm = m1 − m2, S = S1 + S2,
// Δ = m (S2/m2 − S1/m1), L_N = μ x × v. a_RR (2.5PN radiation reaction) is the dissipative term the
// published dispute is about (comments by Hughes and Cornish, PRL 85, 2000; Schnittman & Rasio
// 2001; Hartl & Buonanno 2005): FF makes it a switch.
//
// State: x (3), v (3), S1 (3), S2 (3), φ (accumulated orbital angle, dφ/dt = |x × v| / r²).

using System;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1945 — which Kidder (1995) terms are on (tests switch pieces off to check each
/// transcribed term against its own conserved energy).</summary>
[Flags]
public enum PnTerms
{
    None = 0,
    PN1 = 1,        // a_PN (4b)
    SpinOrbit = 2,  // a_SO (4c) and the L_N × S precession terms of (6a)-(6b)
    PN2 = 4,        // a_2PN (4d)
    SpinSpin = 8,   // a_SS (4e) and the S × S precession terms of (6a)-(6b)
    RadiationReaction = 16,   // a_RR (4f)
    Conservative = PN1 | SpinOrbit | PN2 | SpinSpin,
    All = Conservative | RadiationReaction,
}

/// <summary>#1945 — post-Newtonian spinning binary (Kidder 1995 Eqs. 3-6), for
/// <see cref="ScatteringIntegrator"/>.</summary>
public struct SpinningBinarySystem : IScatteringSystem
{
    public const int StateSize = 13;

    public double M1, M2;
    public PnTerms Terms;
    public double MergeRadius, SeparateRadius, MaxOrbitAngle;
    /// <summary>Set by <see cref="Classify"/>: 0 merged, 1 separated, 2 completed the orbit cap.</summary>
    public int End;

    public readonly int Dimension => StateSize;

    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy)
    {
        double m1 = M1, m2 = M2, m = m1 + m2, mu = m1 * m2 / m, eta = mu / m, dmm = (m1 - m2) / m;
        double x = y[0], yy = y[1], z = y[2], vx = y[3], vy = y[4], vz = y[5];
        double r2 = x * x + yy * yy + z * z, r = Math.Sqrt(r2);
        double nx = x / r, ny = yy / r, nz = z / r;
        double v2 = vx * vx + vy * vy + vz * vz;
        double rd = nx * vx + ny * vy + nz * vz;   // ṙ
        double mr = m / r;

        // a_N (4a)
        double cn = -m / r2, cv = 0.0;
        var terms = Terms;
        if ((terms & PnTerms.PN1) != 0)   // (4b)
        {
            cn += -m / r2 * ((1 + 3 * eta) * v2 - 2 * (2 + eta) * mr - 1.5 * eta * rd * rd);
            cv += -m / r2 * (-2 * (2 - eta) * rd);
        }
        if ((terms & PnTerms.PN2) != 0)   // (4d)
        {
            cn += -m / r2 * (0.75 * (12 + 29 * eta) * mr * mr + eta * (3 - 4 * eta) * v2 * v2
                             + 15.0 / 8.0 * eta * (1 - 3 * eta) * rd * rd * rd * rd
                             - 1.5 * eta * (3 - 4 * eta) * v2 * rd * rd
                             - 0.5 * eta * (13 - 4 * eta) * mr * v2
                             - (2 + 25 * eta + 2 * eta * eta) * mr * rd * rd);
            cv += -m / r2 * (-0.5 * rd * (eta * (15 + 4 * eta) * v2 - (4 + 41 * eta + 8 * eta * eta) * mr
                                          - 3 * eta * (3 + 2 * eta) * rd * rd));
        }
        if ((terms & PnTerms.RadiationReaction) != 0)   // (4f)
        {
            double k = 1.6 * eta * m * m / (r2 * r);
            cn += k * rd * (18 * v2 + 2.0 / 3.0 * mr - 25 * rd * rd);
            cv += -k * (6 * v2 - 2 * mr - 15 * rd * rd);
        }
        double ax = cn * nx + cv * vx, ay = cn * ny + cv * vy, az = cn * nz + cv * vz;

        double s1x = y[6], s1y = y[7], s1z = y[8], s2x = y[9], s2y = y[10], s2z = y[11];
        double r3 = r2 * r;
        bool so = (terms & PnTerms.SpinOrbit) != 0, ss = (terms & PnTerms.SpinSpin) != 0;
        if (so)   // (4c)
        {
            double sx = s1x + s2x, sy = s1y + s2y, sz = s1z + s2z;
            double dx = m * (s2x / m2 - s1x / m1), dyv = m * (s2y / m2 - s1y / m1), dz = m * (s2z / m2 - s1z / m1);
            // n × v
            double nvx = ny * vz - nz * vy, nvy = nz * vx - nx * vz, nvz = nx * vy - ny * vx;
            double t1 = nvx * (2 * sx + dmm * dx) + nvy * (2 * sy + dmm * dyv) + nvz * (2 * sz + dmm * dz);
            double ux = 7 * sx + 3 * dmm * dx, uy = 7 * sy + 3 * dmm * dyv, uz = 7 * sz + 3 * dmm * dz;   // v × u
            double wx = 3 * sx + dmm * dx, wy = 3 * sy + dmm * dyv, wz = 3 * sz + dmm * dz;               // n × w
            ax += (6 * nx * t1 - (vy * uz - vz * uy) + 3 * rd * (ny * wz - nz * wy)) / r3;
            ay += (6 * ny * t1 - (vz * ux - vx * uz) + 3 * rd * (nz * wx - nx * wz)) / r3;
            az += (6 * nz * t1 - (vx * uy - vy * ux) + 3 * rd * (nx * wy - ny * wx)) / r3;
        }
        double ns1 = nx * s1x + ny * s1y + nz * s1z, ns2 = nx * s2x + ny * s2y + nz * s2z;
        if (ss)   // (4e)
        {
            double s12 = s1x * s2x + s1y * s2y + s1z * s2z;
            double k = -3.0 / (mu * r3 * r);
            ax += k * (nx * s12 + s1x * ns2 + s2x * ns1 - 5 * nx * ns1 * ns2);
            ay += k * (ny * s12 + s1y * ns2 + s2y * ns1 - 5 * ny * ns1 * ns2);
            az += k * (nz * s12 + s1z * ns2 + s2z * ns1 - 5 * nz * ns1 * ns2);
        }
        dy[0] = vx; dy[1] = vy; dy[2] = vz;
        dy[3] = ax; dy[4] = ay; dy[5] = az;

        // Spin precession (6a)-(6b): dS1 = Ω1 × S1 with
        // Ω1 = [ (2 + 3m2/(2m1)) L_N − S2 + 3 (n·S2) n ] / r³ (and 1 <-> 2).
        double lx = mu * (yy * vz - z * vy), ly = mu * (z * vx - x * vz), lz = mu * (x * vy - yy * vx);
        double c1 = so ? (2 + 1.5 * m2 / m1) : 0.0, c2 = so ? (2 + 1.5 * m1 / m2) : 0.0;
        double o1x = c1 * lx, o1y = c1 * ly, o1z = c1 * lz;
        double o2x = c2 * lx, o2y = c2 * ly, o2z = c2 * lz;
        if (ss)
        {
            o1x += -s2x + 3 * ns2 * nx; o1y += -s2y + 3 * ns2 * ny; o1z += -s2z + 3 * ns2 * nz;
            o2x += -s1x + 3 * ns1 * nx; o2y += -s1y + 3 * ns1 * ny; o2z += -s1z + 3 * ns1 * nz;
        }
        dy[6] = (o1y * s1z - o1z * s1y) / r3;
        dy[7] = (o1z * s1x - o1x * s1z) / r3;
        dy[8] = (o1x * s1y - o1y * s1x) / r3;
        dy[9] = (o2y * s2z - o2z * s2y) / r3;
        dy[10] = (o2z * s2x - o2x * s2z) / r3;
        dy[11] = (o2x * s2y - o2y * s2x) / r3;

        // Orbital angle: |x × v| / r².
        double cx = yy * vz - z * vy, cy = z * vx - x * vz, cz = x * vy - yy * vx;
        dy[12] = Math.Sqrt(cx * cx + cy * cy + cz * cz) / r2;
    }

    public ScatterFate Classify(ReadOnlySpan<double> y, out int body)
    {
        body = -1;
        double r = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
        if (r < MergeRadius) { End = 0; return ScatterFate.Captured; }
        if (r > SeparateRadius) { End = 1; return ScatterFate.Escaped; }
        if (y[12] >= MaxOrbitAngle) { End = 2; return ScatterFate.Undecided; }
        return ScatterFate.Running;
    }

    /// <summary>Step cap: a small fraction of the orbital time scale √(r³/m).</summary>
    public readonly double MaxStep(ReadOnlySpan<double> y)
    {
        double r = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
        return Math.Max(1e-6, 0.05 * Math.Sqrt(r * r * r / (M1 + M2)));
    }

    public readonly void Observe(ReadOnlySpan<double> y, double h) { }

    // ── Conserved quantities (Kidder Eqs. 8-11), for tests and the status note ──────────

    /// <summary>Kidder Eq. (8)/(9a)-(9e) with the given <paramref name="terms"/> (the RR flag ignored).</summary>
    public static double Energy(double m1, double m2, ReadOnlySpan<double> y, PnTerms terms)
    {
        double m = m1 + m2, mu = m1 * m2 / m, eta = mu / m, dmm = (m1 - m2) / m;
        double x = y[0], yy = y[1], z = y[2], vx = y[3], vy = y[4], vz = y[5];
        double r = Math.Sqrt(x * x + yy * yy + z * z), mr = m / r;
        double v2 = vx * vx + vy * vy + vz * vz, rd = (x * vx + yy * vy + z * vz) / r;
        double e = mu * (0.5 * v2 - mr);   // (9a)
        if ((terms & PnTerms.PN1) != 0)   // (9b)
            e += mu * (3.0 / 8.0 * (1 - 3 * eta) * v2 * v2 + 0.5 * (3 + eta) * v2 * mr + 0.5 * eta * mr * rd * rd + 0.5 * mr * mr);
        if ((terms & PnTerms.PN2) != 0)   // (9d)
            e += mu * (5.0 / 16.0 * (1 - 7 * eta + 13 * eta * eta) * v2 * v2 * v2
                       - 3.0 / 8.0 * eta * (1 - 3 * eta) * mr * rd * rd * rd * rd
                       + 1.0 / 8.0 * (21 - 23 * eta - 27 * eta * eta) * mr * v2 * v2
                       + 1.0 / 8.0 * (14 - 55 * eta + 4 * eta * eta) * mr * mr * v2
                       + 0.25 * eta * (1 - 15 * eta) * mr * v2 * rd * rd
                       - 0.25 * (2 + 15 * eta) * mr * mr * mr
                       + 1.0 / 8.0 * (4 + 69 * eta + 12 * eta * eta) * mr * mr * rd * rd);
        double r3 = r * r * r;
        if ((terms & PnTerms.SpinOrbit) != 0)   // (9c)
        {
            double lx = mu * (yy * vz - z * vy), ly = mu * (z * vx - x * vz), lz = mu * (x * vy - yy * vx);
            double qx = y[6] + y[9] + dmm * m * (y[9] / m2 - y[6] / m1);
            double qy = y[7] + y[10] + dmm * m * (y[10] / m2 - y[7] / m1);
            double qz = y[8] + y[11] + dmm * m * (y[11] / m2 - y[8] / m1);
            e += (lx * qx + ly * qy + lz * qz) / r3;
        }
        if ((terms & PnTerms.SpinSpin) != 0)   // (9e)
        {
            double nx = x / r, ny = yy / r, nz = z / r;
            double ns1 = nx * y[6] + ny * y[7] + nz * y[8], ns2 = nx * y[9] + ny * y[10] + nz * y[11];
            double s12 = y[6] * y[9] + y[7] * y[10] + y[8] * y[11];
            e += (3 * ns1 * ns2 - s12) / r3;
        }
        return e;
    }

    /// <summary>Kidder Eqs. (10)-(11d): J = L_N + L_PN + L_SO + L_2PN + S (terms switched as given).</summary>
    public static (double X, double Y, double Z) TotalAngularMomentum(double m1, double m2, ReadOnlySpan<double> y, PnTerms terms)
    {
        double m = m1 + m2, mu = m1 * m2 / m, eta = mu / m, dmm = (m1 - m2) / m;
        double x = y[0], yy = y[1], z = y[2], vx = y[3], vy = y[4], vz = y[5];
        double r = Math.Sqrt(x * x + yy * yy + z * z), mr = m / r;
        double v2 = vx * vx + vy * vy + vz * vz, rd = (x * vx + yy * vy + z * vz) / r;
        double nx = x / r, ny = yy / r, nz = z / r;
        double lx = mu * (yy * vz - z * vy), ly = mu * (z * vx - x * vz), lz = mu * (x * vy - yy * vx);
        double f = 1.0;
        if ((terms & PnTerms.PN1) != 0) f += 0.5 * v2 * (1 - 3 * eta) + (3 + eta) * mr;   // (11b)
        if ((terms & PnTerms.PN2) != 0)   // (11d)
            f += 3.0 / 8.0 * (1 - 7 * eta + 13 * eta * eta) * v2 * v2 - 0.5 * eta * (2 + 5 * eta) * mr * rd * rd
               + 0.5 * (7 - 10 * eta - 9 * eta * eta) * mr * v2 + 0.25 * (14 - 41 * eta + 4 * eta * eta) * mr * mr;
        double jx = lx * f, jy = ly * f, jz = lz * f;
        double sx = y[6] + y[9], sy = y[7] + y[10], sz = y[8] + y[11];
        if ((terms & PnTerms.SpinOrbit) != 0)   // (11c)
        {
            double dx = m * (y[9] / m2 - y[6] / m1), dyv = m * (y[10] / m2 - y[7] / m1), dz = m * (y[11] / m2 - y[8] / m1);
            double ax = 3 * sx + dmm * dx, ay = 3 * sy + dmm * dyv, az = 3 * sz + dmm * dz;
            double bx = sx + dmm * dx, by = sy + dmm * dyv, bz = sz + dmm * dz;
            // n × (n × a) = n (n·a) − a;  v × (v × b) = v (v·b) − v² b
            double na = nx * ax + ny * ay + nz * az, vb = vx * bx + vy * by + vz * bz;
            double k = mu / m;
            jx += k * (mr * (nx * na - ax) - 0.5 * (vx * vb - v2 * bx));
            jy += k * (mr * (ny * na - ay) - 0.5 * (vy * vb - v2 * by));
            jz += k * (mr * (nz * na - az) - 0.5 * (vz * vb - v2 * bz));
        }
        return (jx + sx, jy + sy, jz + sz);
    }
}

/// <summary>#1945 — how one binary ended.</summary>
public readonly struct SpinningBinaryOutcome
{
    /// <summary>Categorical gate (see <see cref="SpinningBinaryScene"/>), −1 = none.</summary>
    public int Gate { get; init; }
    /// <summary>Orbits completed.</summary>
    public double Orbits { get; init; }
    public double Time { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1945 — one spinning-binary scene built from <see cref="FractalParameters"/>. The pixel is
/// the pair of initial spin polar angles (θ1, θ2), radians from the initial orbital angular momentum
/// (+z); the binary starts at x = r0 x̂ with velocity v0 ŷ (Levin 2000's set-up).</summary>
public sealed class SpinningBinaryScene
{
    public double M1 { get; }
    public double M2 { get; }
    public double Chi1 { get; }
    public double Chi2 { get; }
    public double Phi1 { get; }
    public double Phi2 { get; }
    public double R0 { get; }
    public double V0 { get; }
    public PnTerms Terms { get; }
    public SpinningBinaryOutcomeMode Mode { get; }
    public double MergeRadius { get; }
    public double SeparateRadius { get; }
    public double MaxOrbits { get; }
    public ScatterSettings Settings { get; }

    /// <summary>Gates. Fates: 0 merged, 1 separated, 2 still orbiting after the orbit cap.
    /// MergerSide: 0 merged with z &gt; 0, 1 merged with z &lt; 0, 2 separated, −1 still orbiting.</summary>
    public const int GateCount = 3;

    public SpinningBinaryScene(double massRatio, double chi1, double chi2, double phi1Deg, double phi2Deg,
                               double r0, double v0, bool radiationReaction, SpinningBinaryOutcomeMode mode,
                               double mergeRadius, double separateRadius, double maxOrbits, double tolerance)
    {
        double q = double.IsFinite(massRatio) ? Math.Clamp(massRatio, 0.01, 1.0) : 1.0 / 3.0;
        M1 = 1.0 / (1.0 + q); M2 = q / (1.0 + q);
        Chi1 = double.IsFinite(chi1) ? Math.Clamp(chi1, 0.0, 1.0) : 0.6;
        Chi2 = double.IsFinite(chi2) ? Math.Clamp(chi2, 0.0, 1.0) : 0.6;
        Phi1 = (double.IsFinite(phi1Deg) ? phi1Deg : 0.0) * Math.PI / 180.0;
        Phi2 = (double.IsFinite(phi2Deg) ? phi2Deg : 0.0) * Math.PI / 180.0;
        R0 = double.IsFinite(r0) ? Math.Clamp(r0, 2.0, 10000.0) : 5.0;
        V0 = double.IsFinite(v0) ? Math.Clamp(v0, 0.0, 0.99) : 0.45;
        Terms = radiationReaction ? PnTerms.All : PnTerms.Conservative;
        Mode = mode;
        MergeRadius = double.IsFinite(mergeRadius) ? Math.Clamp(mergeRadius, 0.1, R0 * 0.999) : 2.0;
        SeparateRadius = double.IsFinite(separateRadius) ? Math.Max(R0 * 1.001, separateRadius) : 1000.0;
        MaxOrbits = double.IsFinite(maxOrbits) ? Math.Clamp(maxOrbits, 1.0, 100000.0) : 50.0;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-9;
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = 50_000_000, InitialStep = 0.01 };
    }

    public static SpinningBinaryScene Build(FractalParameters p)
        => new(p.SpinBinMassRatio, p.SpinBinChi1, p.SpinBinChi2, p.SpinBinPhi1, p.SpinBinPhi2, p.SpinBinSeparation,
               p.SpinBinVelocity, p.SpinBinRadiationReaction, p.SpinBinOutcome, p.SpinBinMergeRadius,
               p.SpinBinSeparateRadius, p.SpinBinMaxOrbits, p.SpinBinTolerance);

    /// <summary>Initial state for spin polar angles (θ1, θ2) in radians.</summary>
    public void LaunchState(double theta1, double theta2, Span<double> s)
    {
        s[0] = R0; s[1] = 0; s[2] = 0;
        s[3] = 0; s[4] = V0; s[5] = 0;
        double a1 = Chi1 * M1 * M1, a2 = Chi2 * M2 * M2;
        s[6] = a1 * Math.Sin(theta1) * Math.Cos(Phi1); s[7] = a1 * Math.Sin(theta1) * Math.Sin(Phi1); s[8] = a1 * Math.Cos(theta1);
        s[9] = a2 * Math.Sin(theta2) * Math.Cos(Phi2); s[10] = a2 * Math.Sin(theta2) * Math.Sin(Phi2); s[11] = a2 * Math.Cos(theta2);
        s[12] = 0;
    }

    public SpinningBinarySystem System() => new()
    {
        M1 = M1, M2 = M2, Terms = Terms, MergeRadius = MergeRadius, SeparateRadius = SeparateRadius,
        MaxOrbitAngle = 2 * Math.PI * MaxOrbits, End = -1,
    };

    /// <summary>Trace the binary for spin angles (θ1, θ2). <paramref name="s"/> (length ≥ 13) ends as
    /// the final state.</summary>
    public SpinningBinaryOutcome Trace(double theta1, double theta2, Span<double> s)
    {
        LaunchState(theta1, theta2, s);
        var sys = System();
        var settings = Settings;
        var o = ScatteringIntegrator.Trace(ref sys, s, in settings);
        int gate = sys.End;
        if (Mode == SpinningBinaryOutcomeMode.MergerSide)
            gate = sys.End switch { 0 => s[2] >= 0 ? 0 : 1, 1 => 2, _ => -1 };
        return new SpinningBinaryOutcome { Gate = gate, Orbits = s[12] / (2 * Math.PI), Time = o.Lambda, Steps = o.Steps };
    }
}
